using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal void VerifyReferencePriorityUi(WorkflowSession session, string evidence)
        {
            RenderScope(session, evidence);
            List<ReferenceComparisonRow> Rows() => referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Select(r => (ReferenceComparisonRow)r.DataBoundItem).ToList();
            void Ordered(string operation) => WorkflowTestProgram.Check(Rows().Select(r => r.DisplayPriority).SequenceEqual(Rows().Select(r => r.DisplayPriority).OrderBy(p => p)), operation);
            Ordered("绿色置顶、黄色其次、未确认或仅本地置后");
            foreach (var direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
            {
                referenceBrowser.Grid.Sort(referenceBrowser.Grid.Columns["Name"], direction);
                Ordered("点击列标头排序仍保留绿色和黄色的固定优先级：" + direction);
            }
            var checkedBefore = browserRows.Select(r => r.Selected).ToArray();
            referenceBrowser.Search.Text = "green"; Ordered("搜索后保留分组优先级"); referenceBrowser.Search.Clear();
            WorkflowTestProgram.Check(browserRows.Select(r => r.Selected).SequenceEqual(checkedBefore), "右侧筛选排序不改变总装勾选");
            foreach (var gridRow in referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Where(r => ((ReferenceComparisonRow)r.DataBoundItem).DisplayPriority < 2))
            {
                var row = (ReferenceComparisonRow)gridRow.DataBoundItem;
                foreach (DataGridViewCell cell in gridRow.Cells)
                {
                    var e = new DataGridViewCellFormattingEventArgs(cell.ColumnIndex, gridRow.Index, cell.Value, typeof(string), new DataGridViewCellStyle());
                    typeof(DataGridView).GetMethod("OnCellFormatting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(DataGridViewCellFormattingEventArgs) }, null).Invoke(referenceBrowser.Grid, new object[] { e });
                    WorkflowTestProgram.Check(e.CellStyle.BackColor == (row.DisplayPriority == 0 ? Color.FromArgb(220, 244, 228) : Color.FromArgb(255, 237, 170)) && e.CellStyle.SelectionBackColor != Color.Empty, "整行及选中状态保持分组颜色：" + row.Name + "/" + cell.OwningColumn.Name);
                }
            }
            referenceBrowser.Grid.Sort(referenceBrowser.Grid.Columns["Name"], ListSortDirection.Ascending);
            Refresh(); using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "xref-cloud-priority.png"), ImageFormat.Png); }
            var history = Session.UpdatedHashes; var mappings = JsonConvert.SerializeObject(settings.Mappings); var folders = JsonConvert.SerializeObject(settings.SystemFolders);
            InvalidateAutomaticMappings();
            WorkflowTestProgram.Check(!Session.Checked && Session.Sources.Count == 0 && Session.LocalMappingFiles == null && referenceBrowser.Grid.Rows.Count == 0 && browserRows.All(r => r.State == "未检查"), "强制刷新清除自动映射、旧检查状态与右侧旧结果");
            WorkflowTestProgram.Check(ReferenceEquals(history, Session.UpdatedHashes) && JsonConvert.SerializeObject(settings.Mappings) == mappings && JsonConvert.SerializeObject(settings.SystemFolders) == folders && browserRows.Select(r => r.Selected).SequenceEqual(checkedBefore), "强制刷新保留人工对应、文件夹规则、更新记录及勾选");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyReferencePriorityAndRefresh(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "priority-refresh"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector"); var scope = Path.Combine(local, "单系统总装文件");
            var assembly = Path.Combine(scope, "114101-SIF-M3-M-9424_GYPSUM.dwg");
            string L(string n) => Path.Combine(local, "local_9424", n + ".dwg");
            string C(string n) => Path.Combine(cloud, "9424", n + ".dwg");
            foreach (var p in new[] { assembly, L("green-current"), L("green-unloaded"), L("yellow-unreferenced"), L("local-only"), C("green-current"), C("green-unloaded"), C("yellow-unreferenced"), C("yellow-missing") }) Dwg(p, p);
            var fixtureDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            foreach (var p in Directory.EnumerateFiles(root, "*.dwg", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(p, fixtureDate);
            File.SetLastWriteTimeUtc(C("green-current"), fixtureDate.AddDays(6));
            RefEdge Edge(string n, bool unloaded = false) => new RefEdge { Name = n, Saved = L(n), Resolved = L(n), Unloaded = unloaded };
            DwgReader.Graph[assembly] = new List<RefEdge> { Edge("green-current"), Edge("green-unloaded", true), Edge("yellow-missing"), Edge("local-only") };
            var config = new Settings { LocalModelsRoot = local, ConnectorRoot = cloud, ProjectScanFolder = scope };
            var service = new SyncService(config, () => { }, Path.Combine(root, "State"));
            var session = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true, ReferenceCache = new CachedReader(new DwgReader()) };
            session.Assemblies.Add(Workflow.ScanAssembly(assembly, session.ReferenceCache, true));
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            var rows = ReferenceComparisonBuilder.Build(session, session.Assemblies.Single());
            Check(rows.Where(r => r.DisplayPriority == 0).Select(r => r.Name).OrderBy(n => n).SequenceEqual(new[] { "green-current.dwg", "green-unloaded.dwg" }), "两端文件匹配且已引用为绿色，卸载仍明确显示其引用状态");
            Check(rows.Where(r => r.DisplayPriority == 1).Select(r => r.Name).OrderBy(n => n).SequenceEqual(new[] { "yellow-missing.dwg", "yellow-unreferenced.dwg" }), "云端存在而本地缺失，或两端存在但未引用，均为黄色");
            Check(rows.Single(r => r.Name == "local-only.dwg").DisplayPriority == 2, "仅本地存在不得误标为绿色");
            var green = rows.Single(r => r.Name == "green-current.dwg"); green.DateDifference = "云端较新 8 天"; green.State = "可更新";
            Check(green.DisplayPriority == 0, "绿色只代表匹配及引用，不吞掉云端更新日期提示");
            green.CandidateCloudPaths.Add(C("other")); Check(green.DisplayPriority == 2, "未确认云端候选不得冒充绿色"); green.CandidateCloudPaths.Clear();
            green.RequiresPathRepair = true; Check(green.DisplayPriority == 1, "旧引用路径待修复标黄，不能误报有效引用"); green.RequiresPathRepair = false;
            green.CloudState = "缺失 / 不可读取"; Check(green.DisplayPriority == 2, "云端缺失不得标绿");
            var beforeHashes = new[] { assembly, L("green-current"), C("green-current") }.ToDictionary(p => p, Files.Hash);
            // 模拟上轮自动结果及目录内容改变；强制刷新不改人工选择和文件本身。
            config.Mappings[Settings.MapKey(FolderLink.Parse(cloud), L("green-current"))] = C("green-current");
            config.SystemFolders[Settings.MapKey(FolderLink.Parse(cloud), Path.Combine(local, "Manual"))] = "9424";
            session.UpdatedHashes[L("green-current")] = Files.Hash(L("green-current"));
            using (var form = new WorkflowForm(session, config, () => { }, () => service, assemblyFirst: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Show(); form.VerifyReferencePriorityUi(session, evidence); form.Close();
            }
            Dwg(L("new-after-refresh"), "new local"); Dwg(C("new-after-refresh"), "new cloud");
            var oldCloud = C("yellow-unreferenced"); var movedCloud = Path.Combine(cloud, "9424", "Moved", Path.GetFileName(oldCloud)); Directory.CreateDirectory(Path.GetDirectoryName(movedCloud)); File.Move(oldCloud, movedCloud);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Check(session.LocalMappingFiles.Contains(L("new-after-refresh")) && session.Sources.Single(s => s.Path == C("new-after-refresh")).Plans.Any(p => p.Local == L("new-after-refresh")), "刷新后重新扫描两端新增文件并创建自动对应");
            Check(!session.Sources.Any(s => s.Path == oldCloud) && session.Sources.Single(s => s.Path == movedCloud).Plans.Any(p => p.Local == L("yellow-unreferenced")), "云端文件夹移动后不复用旧路径，按系统和唯一文件重建对应");
            Check(session.Sources.Single(s => s.Path == C("green-current")).MatchReason.Contains("人工") && session.UpdatedHashes.ContainsKey(L("green-current")), "重建自动对应后人工规则和本轮更新记录仍有效");
            Check(!session.LocalMappingFiles.Contains(assembly) && beforeHashes.All(p => Files.Hash(p.Key) == p.Value), "仅枚举本地源文件不把总装当源，不修改或下载实际DWG");
            var newCloud = Path.Combine(cloud, "9424", "Other", "new-after-refresh.dwg"); Dwg(newCloud, "ambiguous");
            File.Move(C("new-after-refresh"), Path.Combine(cloud, "9424", "Moved", "new-after-refresh.dwg"));
            session.InvalidateAutomaticMappings(); session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Check(session.Sources.Where(s => s.Name == "new-after-refresh.dwg").All(s => s.Plans.Count == 0 && s.AmbiguousLocals.Contains(L("new-after-refresh"))), "重新映射遇到同系统多个候选仍保持待确认，不任意选择下载源");
        }
    }
}
