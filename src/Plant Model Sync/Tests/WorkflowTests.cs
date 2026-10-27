using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    // 本程序集只使用假 DWG 读取器，文件复制、哈希及备份采用生产实现。
    internal static class Host
    {
        internal static List<ProjectDrawing> Drawings = new List<ProjectDrawing>();
        public static List<ProjectDrawing> CurrentProjectDrawings() => Drawings;
        public static void CheckSourcesClosed(IEnumerable<XrefRow> rows) { }
        public static IDisposable ReleaseReferences(string assembly) => null;
        public static IDisposable ReleaseWorkflowReferences(List<AssemblyUpdate> assemblies, IEnumerable<XrefRow> targets) => null;
        public static void CheckAssemblyClosedForAttach(string path) { }
        internal static Func<string, List<ReferencePlacement>, AttachmentResult> NewStage;
        internal static IWeeklyDwg LocalRepairDwg() => new TestLocalRepairDwg();
        public static AttachmentResult StageNewReferences(string path, List<ReferencePlacement> sources) => NewStage == null ? throw new IOException("隔离界面测试不调用真实 DWG 挂接器") : NewStage(path, sources);
        public static AttachmentResult StageConfirmedLocalReferences(string path, List<ReferencePlacement> sources, bool confirmed) => StageNewReferences(path, sources);
    }
    internal sealed class TestLocalRepairDwg : IWeeklyDwg
    {
        public List<RefEdge> Read(string path) => new DwgReader().Read(path);
        public string Stage(string source, string owner, List<WeeklyRewrite> changes) => throw new IOException("界面隔离测试不执行原生路径改写");
        public string StageAttachments(string owner, List<ReferencePlacement> placements) => throw new IOException("界面隔离测试不执行原生挂接");
    }
    internal sealed class ProjectDrawing { public string Part { get; set; } public string Name { get; set; } public string Path { get; set; } }
    internal sealed class DwgReader : IReferenceReader
    {
        public static bool IsOpen(string path) => false;
        internal static Dictionary<string, List<RefEdge>> Graph = new Dictionary<string, List<RefEdge>>(StringComparer.OrdinalIgnoreCase);
        internal static Dictionary<string, int> Reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal static Action<string> BeforeRead;
        internal static string Nested, NewChild;
        public List<RefEdge> Read(string path)
        {
            BeforeRead?.Invoke(path);
            if (!File.Exists(path)) throw new FileNotFoundException("隔离样例引用不存在", path);
            Reads[path] = Reads.TryGetValue(path, out var n) ? n + 1 : 1;
            var result = Graph.TryGetValue(path, out var rows) ? rows : new List<RefEdge>();
            if (path == Nested && File.ReadAllText(path, Encoding.UTF8).Contains("nested-new-edges")) return result.Concat(new[] { new RefEdge { Name = "NewChild", Saved = NewChild, Resolved = NewChild } }).ToList();
            return result;
        }
    }
    internal sealed partial class WorkflowForm
    {
        internal async Task RenderWorkflow(string evidence)
        {
            stateFilter.SelectedIndex = 0;
            WorkflowTestProgram.Check(sourcesGrid.Columns["Selected"].HeaderCell is VisibleSelectionHeader<CloudUpdate> && modelsGrid.Columns["Selected"].HeaderCell is VisibleSelectionHeader<AssemblyUpdate>, "主界面两栏均在第一列表头安装勾选框");
            WorkflowTestProgram.Check(sourcesGrid.Rows.Count == Session.Sources.Count(s => !onlyExisting.Checked || s.LocalExists) && modelsGrid.Rows.Count == 2, "双栏按本地存在筛选及所选文件显示受影响总装");
            var checkedCount = Session.Assemblies.Count(a => a.Selected);
            modelTable.Search.Text = "Train A"; modelTable.Search.Clear();
            modelsGrid.Sort(modelsGrid.Columns["DisplayName"], System.ComponentModel.ListSortDirection.Descending);
            WorkflowTestProgram.Check(Session.Assemblies.Count(a => a.Selected) == checkedCount, "筛选排序保留总装导出勾选");
            var source = Session.Sources.First(s => s.Name == "Shared.dwg");
            foreach (DataGridViewRow row in sourcesGrid.Rows) if (row.DataBoundItem == source) { sourcesGrid.CurrentCell = row.Cells["Name"]; row.Selected = true; break; }
            await sourceDetail.ShowFileAsync(source.Path, "实际已 Pull 到：" + source.LocalSummary + "\r\n影响总装：" + source.AffectedSummary);
            ShowModel(); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "cloud-to-assembly-nwc.png"), ImageFormat.Png); }
            Size = MinimumSize; PerformLayout(); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "cloud-to-assembly-nwc-minimum.png"), ImageFormat.Png); }
        }
        private async Task ConfirmedPull()
        {
            onlyExisting.Checked = false; SelectSources(true);
            using (var confirm = new System.Windows.Forms.Timer { Interval = 25 })
            {
                confirm.Tick += (s, e) => { var preview = Application.OpenForms.OfType<PreviewForm>().FirstOrDefault(); if (preview != null) { preview.SelectTestUpdates(true); preview.DialogResult = DialogResult.OK; preview.Close(); } };
                confirm.Start(); await Pull(); confirm.Stop();
            }
        }
        internal async Task VerifyNestedWorkflow(string nested, string newChild)
        {
            await Check();
            WorkflowTestProgram.Check(Session.Sources.Single(s => s.Name == "Nested.dwg").State == "可更新", "完整界面流程先识别父引用更新");
            await ConfirmedPull();
            WorkflowTestProgram.Check(!Session.Checked && Session.UpdatedHashes.ContainsKey(nested), "Pull 后发现新增嵌套引用时要求重新检查");
            Session.Assemblies.ForEach(m => m.Selected = true);
            WorkflowTestProgram.Check(Workflow.ExportJobs(Session, output.Text).All(j => j.CurrentLocal), "嵌套引用待复查不阻止用户明确选择导出当前模型");
            await Check();
            WorkflowTestProgram.Check(Session.UpdatedHashes.ContainsKey(nested) && Session.UpdatedHashes.Keys.Any(p => Path.GetFileName(p) == "Shared.dwg"), "重新检查保留本轮实际 Pull 历史");
            WorkflowTestProgram.Check(Session.Sources.Single(s => s.Name == "NewChild.dwg").State == "可更新", "重新检查发现可恢复的新嵌套 DWG");
            await ConfirmedPull();
            WorkflowTestProgram.Check(Session.Checked && File.Exists(newChild) && Session.Assemblies.All(m => m.UpdatedPaths.Contains(newChild)), "新嵌套引用 Pull 后所有总装记录实际更新");
            WorkflowTestProgram.Check(Workflow.ExportJobs(Session, output.Text).Count == 2, "全部引用完成后界面工作流可以规划两份总装 NWC");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        internal static int Count;
        private static readonly List<string> Passed = new List<string>();
        internal static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Count++; Passed.Add(name); Console.WriteLine("PASS " + name); }
        internal static void Reject(Action action, string name) { var failed = false; try { action(); } catch (IOException) { failed = true; } Check(failed, name); }
        // 旧回归样例用同一发布内容生成相同时间；日期策略专项测试再显式覆盖时间。
        internal static DateTime FixtureDate(string content) { using (var sha = System.Security.Cryptography.SHA256.Create()) return new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.ToUInt32(sha.ComputeHash(Encoding.UTF8.GetBytes(content)), 0)); }
        private static void Dwg(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, Encoding.UTF8.GetBytes("AC1032" + content)); File.SetLastWriteTimeUtc(path, FixtureDate(content)); }
        [STAThread]
        public static int Main(string[] args)
        {
            var evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(evidence);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var bootstrap = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), Size = new Size(1, 1) };
            var exit = 0;
            bootstrap.Shown += async (s, e) =>
            {
                try { await Run(evidence); Console.WriteLine("TOTAL PASS " + Count); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
                finally { bootstrap.Close(); }
            };
            Application.Run(bootstrap); bootstrap.Dispose(); return exit;
        }
        private static async Task Run(string evidence)
        {
            var liveConfig = Path.Combine(Settings.DataDirectory, "settings.json"); var liveBefore = Files.Hash(liveConfig);
            var local = Path.Combine(evidence, "fixtures", "Local"); var cloud = Path.Combine(evidence, "fixtures", "Connector");
            var a = Path.Combine(local, "Train A", "Assembly.dwg"); var b = Path.Combine(local, "Train B", "Assembly.dwg");
            var shared = Path.Combine(local, "Parts", "Shared.dwg"); var nested = Path.Combine(local, "Parts", "Nested.dwg");
            var grid = Path.Combine(local, "Parts", "Grid.dwg"); var same = Path.Combine(local, "Parts", "Unchanged.dwg");
            var conflict = Path.Combine(local, "Parts", "Conflicted.dwg"); var legacy = Path.Combine(local, "Parts", "Legacy.dwg");
            var twin1 = Path.Combine(local, "Zone 1", "Twin.dwg"); var twin2 = Path.Combine(local, "Zone 2", "Twin.dwg");
            foreach (var path in new[] { a, b, shared, nested, grid, same, conflict, legacy, twin1, twin2 }) Dwg(path, "local-" + path);
            var files = new[] { "Shared.dwg", "Unchanged.dwg", "Conflicted.dwg", "Legacy.dwg", "Twin.dwg", "Unused.dwg", "Zone 1/Grid.dwg", "Zone 2/Grid.dwg" };
            foreach (var file in files) Dwg(Path.Combine(cloud, file), "cloud-" + file);
            File.Copy(same, Path.Combine(cloud, "Unchanged.dwg"), true);
            List<RefEdge> Edges(params string[] paths) => paths.Select(p => new RefEdge { Name = Path.GetFileNameWithoutExtension(p), Saved = p, Resolved = p }).ToList();
            DwgReader.Graph[a] = Edges(shared, nested, grid, same, conflict, legacy); DwgReader.Graph[b] = Edges(nested, twin1, twin2); DwgReader.Graph[nested] = Edges(shared);
            Host.Drawings = new[] { a, b }.Select(path => new ProjectDrawing { Part = "Piping", Name = Path.GetFileName(path), Path = path }).ToList();
            var cache = new CachedReader(new DwgReader());
            var session = new WorkflowSession { ConnectorRoot = cloud, ScopeFolder = local, OutputFolder = Path.Combine(evidence, "NWC"), Checked = true };
            session.Assemblies.Add(Workflow.ScanAssembly(a, cache)); session.Assemblies.Add(Workflow.ScanAssembly(b, cache));
            Check(DwgReader.Reads[shared] == 1, "扫描多总装复用共享 DWG 依赖读取缓存");
            var config = new Settings(); var folder = FolderLink.Parse(cloud);
            config.Baselines[Settings.SyncKey(folder, conflict, Path.Combine(cloud, "Conflicted.dwg"))] = new Baseline { Hash = "old", Version = "old" };
            config.Baselines[Settings.SyncKey(folder, legacy, "Legacy.dwg")] = new Baseline { Hash = "old", Version = "old" };
            var state = Path.Combine(evidence, "state"); Directory.CreateDirectory(state);
            Action save = () => File.WriteAllText(Path.Combine(state, "settings.json"), JsonConvert.SerializeObject(config, Formatting.Indented), new UTF8Encoding(false));
            var service = new SyncService(config, save, state);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            CloudUpdate Source(string name) => session.Sources.Single(row => row.Name == name);
            Check(session.Sources.Count == files.Length, "云端目录全部 DWG 均进入检查清单");
            Check(Source("Unused.dwg").State == "无本地对应", "未被总装引用的云端文件不会静默选取本地目标");
            Check(Source("Unchanged.dwg").State == "日期相同" && !Source("Unchanged.dwg").Selected, "相同内容不重复拉取");
            Check(Source("Shared.dwg").AffectedSummary.Split('|').Length == 2, "共享 Xref 影响直接及嵌套引用的两个总装");
            Check(session.Sources.Where(row => row.Name == "Grid.dwg").All(row => row.State == "对应有歧义" && row.Plans.Count == 0), "云端多处同名必须人工对应");
            Check(Source("Twin.dwg").State == "对应有歧义", "本地多处同名必须人工对应");
            Check(Source("Conflicted.dwg").Plans[0].Blocked == null, "双方内容改变不阻止日期策略 Pull，云端为准");
            Check(Source("Legacy.dwg").Plans[0].Blocked == null, "旧版基准不再触发 Pull 双方变更冲突");
            config.Mappings[Settings.MapKey(folder, grid)] = Path.Combine(cloud, "Zone 1", "Grid.dwg");
            config.Mappings[Settings.MapKey(folder, twin1)] = Path.Combine(cloud, "Twin.dwg"); config.Mappings[Settings.MapKey(folder, twin2)] = Path.Combine(cloud, "Twin.dwg");
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Check(Source("Twin.dwg").Plans.Count == 2 && Source("Twin.dwg").Plans.All(p => p.Blocked == null), "一个云端 DWG 可 Pull 到多个已确认本地副本");
            Workflow.UpdateAffected(session);
            Check(session.Assemblies.All(m => string.IsNullOrEmpty(m.UpdatedXrefs) && !string.IsNullOrEmpty(m.PendingXrefs)), "检查阶段只列待更新引用，不冒充实际更新");
            foreach (var row in session.Sources) row.Selected = row.State == "可更新";
            var plans = session.Sources.Where(row => row.Selected).SelectMany(row => row.Plans).Where(p => p.Blocked == null && !p.IsUnchanged).ToList();
            var before = plans.ToDictionary(p => p.Local, p => Files.Hash(p.Local));
            session.LastLog = await service.Execute(folder, plans, false, CancellationToken.None, null);
            Workflow.RecordPull(session, plans);
            Check(plans.All(p => p.Written && p.Verified && Files.Hash(p.Local) == p.ConnectorHash), "Pull 实际复制并校验所有已选本地副本");
            Check(before.All(pair => Directory.GetFiles(Path.GetDirectoryName(pair.Key), Path.GetFileName(pair.Key) + ".pms-*.bak").Length == 0), "所有已选Xref覆盖均不生成备份");
            Check(session.Assemblies.All(m => m.UpdatedPaths.Contains(shared) && m.UpdatedXrefs.Contains("Shared.dwg")), "实际更新共享引用后两个总装同时列出更新明细");
            Check(session.Assemblies.Any(m => m.UpdatedPaths.Contains(conflict)), "原双方变更文件完成 Pull 后记录为已更新");
            foreach (var m in session.Assemblies) m.Selected = true;
            Check(Workflow.ExportJobs(session, session.OutputFolder).Count == 2, "日期策略已完成全部明确目标更新后可以规划导出");
            File.Copy(Path.Combine(cloud, "Conflicted.dwg"), conflict, true); File.Copy(Path.Combine(cloud, "Legacy.dwg"), legacy, true);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Check(Source("Conflicted.dwg").State == "日期相同", "双方内容已一致时不被旧基准误报冲突");
            Workflow.UpdateAffected(session); foreach (var m in session.Assemblies) m.Selected = true;
            var jobs = Workflow.ExportJobs(session, session.OutputFolder);
            Check(jobs.Count == 2 && jobs.Select(j => j.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "不同目录同名总装生成不同 NWC 输出文件名");
            Check(!Directory.Exists(session.OutputFolder), "导出规划阶段不创建伪 NWC 文件");
            Check(jobs.All(j => j.CurrentLocal && j.Assembly.ExpectedHashes.Count == 0 && j.Assembly.ExpectedCloudHashes.Count == 0), "交互导出独立于旧本地及Connector检查快照");
            session.Assemblies[0].ExportState = "已导出";
            File.AppendAllText(Path.Combine(cloud, "Shared.dwg"), "-new-publish", Encoding.UTF8);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Check(Source("Shared.dwg").State == "可更新", "云端出现新发布时重新列为可更新");
            Workflow.UpdateAffected(session); session.Assemblies.ForEach(m => m.Selected = true);
            Check(Workflow.ExportJobs(session, session.OutputFolder).Count == 2, "云端又有发布时仍可手动导出当前两份本地模型");
            plans = Source("Shared.dwg").Plans;
            await service.Execute(folder, plans, false, CancellationToken.None, null); Workflow.RecordPull(session, plans);
            Check(session.Assemblies[0].ExportState == "未导出", "再次 Pull 新内容后清除旧的导出成功状态");
            session.Assemblies.ForEach(m => { m.Selected = true; m.RefreshState = "源文件已更新；导出时加载"; });
            session.ScopeFolder = "";
            var newChild = Path.Combine(local, "Parts", "NewChild.dwg"); DwgReader.Nested = nested; DwgReader.NewChild = newChild;
            Dwg(Path.Combine(cloud, "Nested.dwg"), "nested-new-edges"); Dwg(Path.Combine(cloud, "NewChild.dwg"), "new-child");
            using (var form = new WorkflowForm(session, config, save, () => new SyncService(config, save, state)))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show();
                await form.VerifyNestedWorkflow(nested, newChild);
                await form.RenderWorkflow(evidence); session = form.Session; form.Close();
            }
            await VerifySystemMatchingCacheAndLogs(evidence);
            await VerifyLayoutAndLinkedFilters(evidence);
            await VerifyAuditFixes(evidence);
            await VerifyNewCloudFiles(evidence);
            await VerifyOneClick(evidence);
            await VerifyManualExport(evidence);
            VerifyWeekly(evidence);
            await VerifyFileNames(evidence);
            await VerifyAssemblyNwcReview(evidence);
            await VerifyNwcTimestamps(evidence);
            VerifyScanIssues(evidence);
            await VerifyAssemblyFirst(evidence);
            VerifyAttachmentPolicy(evidence);
            VerifyLocalRepair(evidence);
            await VerifyReferenceScope(evidence);
            await VerifyReferenceIdentity(evidence);
            await VerifyReferenceCandidates(evidence);
            await VerifyReferencePriorityAndRefresh(evidence);
            await VerifyReferenceNames(evidence);
            await VerifyPullDates(evidence);
            await VerifyScanProgress(evidence);
            Check(Files.Hash(liveConfig) == liveBefore, "隔离验证未改写用户插件配置");
            File.WriteAllText(Path.Combine(evidence, "verification.json"), JsonConvert.SerializeObject(new { version = "0.4.15", checks = Count, passedChecks = Passed, fakeDwgReader = true, nativePlantHostTested = false, nativeNwcExporterTested = false, sources = session.Sources.Select(s => new { s.Name, s.State, s.LocalSummary }), assemblies = session.Assemblies.Select(m => new { m.Path, m.UpdatedXrefs, m.RefreshState }), jobs = jobs.Select(j => new { j.Target, j.CurrentLocal }) }, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
