using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace IWMF.PlantModelSync
{
    internal sealed partial class NewCloudFilesForm
    {
        internal void ConfirmUnifiedForTest(WorkflowSession current, string evidence)
        {
            WorkflowTestProgram.Check(Controls.OfType<TabControl>().Single().TabPages.Count == 2 && existing.Count == 2 && batch.Files.Count == 3, "统一确认同时列出现有更新和三类新增候选，只有一个执行确认");
            foreach (var f in batch.Files)
            {
                f.Targets = current.Assemblies.Where(a => f.Name == "New.dwg" ? a.Name.Contains("9423") : f.Name == "Mirror.dwg" && a.Name.StartsWith("114101-SIF-M3-M-9424_")).Select(a => a.Path).ToList();
                f.Selected = f.Targets.Count > 0;
                WorkflowTestProgram.Check(f.Rotation == -4.42, "统一入口新增默认角度为负4.42度：" + f.Name);
            }
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "one-click-confirmation.png"), ImageFormat.Png); }
            DialogResult = DialogResult.OK; Close();
        }
    }
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifyUnifiedUpdate(string evidence)
        {
            int confirmations = 0;
            var before = Session.Assemblies.ToDictionary(a => a.Path, a => DwgReader.Reads.TryGetValue(a.Path, out var n) ? n : 0);
            using (var confirm = new System.Windows.Forms.Timer { Interval = 25 })
            {
                confirm.Tick += (s, e) => { var preview = Application.OpenForms.OfType<NewCloudFilesForm>().FirstOrDefault(); if (preview != null) { confirmations++; preview.ConfirmUnifiedForTest(Session, evidence); } };
                confirm.Start(); await UpdateAll(); confirm.Stop();
            }
            WorkflowTestProgram.Check(confirmations == 1 && status.Text.StartsWith("一键更新完成") && Session.Checked, "一键更新一次确认完成 Pull、新增挂接及全量复查");
            WorkflowTestProgram.Check(Session.AssemblyAudit.Count == 17 && Session.AssemblyAudit.Count(a => a.State == "文件不存在") == 2, "更新后检查表保留17项并明确列出两份缺失总装");
            WorkflowTestProgram.Check(before.All(p => DwgReader.Reads[p.Key] > p.Value), "更新后重新读取全部15份现有指定总装，包括无关联未变化项");
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == 6 && Session.Assemblies.Count(a => a.UpdatedPaths.Count > 0) == 6, "右侧自动显示既有更新及新增共同影响的六份总装");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "one-click-results.png"), ImageFormat.Png); }
            using (var close = new System.Windows.Forms.Timer { Interval = 25 })
            {
                int count = 0;
                close.Tick += (s, e) => { var viewer = Application.OpenForms.OfType<UpdateLogForm>().FirstOrDefault(); if (viewer != null) { WorkflowTestProgram.Check(viewer.RowCount > 17, "更新日志按钮直接打开文件及总装检查表格，不调用外部程序"); using (var bitmap = new Bitmap(viewer.Width, viewer.Height)) { viewer.DrawToBitmap(bitmap, new Rectangle(0, 0, viewer.Width, viewer.Height)); bitmap.Save(Path.Combine(evidence, "one-click-log-table.png"), ImageFormat.Png); } count++; viewer.Close(); } };
                close.Start(); OpenLog(); close.Stop(); WorkflowTestProgram.Check(count == 1, "更新日志按钮仅打开一个内置日志查看器");
            }
            using (var closeAudit = new System.Windows.Forms.Timer { Interval = 25 })
            {
                int count = 0;
                closeAudit.Tick += (s, e) => { var viewer = Application.OpenForms.OfType<UpdateLogForm>().FirstOrDefault(); if (viewer != null) { WorkflowTestProgram.Check(viewer.RowCount == 17, "本地DWG检查按钮独立显示完整17行"); count++; viewer.Close(); } };
                closeAudit.Start(); await AuditRequiredAssemblies(); closeAudit.Stop(); WorkflowTestProgram.Check(count == 1, "总装检查按钮无需退出插件或手动打开图纸");
            }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyOneClick(string evidence)
        {
            Paths.ConfigureAliases(null);
            var fixture = Path.Combine(evidence, "one-click-fixtures"); var models = Path.Combine(fixture, "Models"); var cloud = Path.Combine(fixture, "Cloud"); var state = Path.Combine(fixture, "state");
            var totals = Path.Combine(models, "单系统总装文件");
            foreach (var key in AssemblyCatalog.Keys.Where(k => k != "114101-SIF-M3-MLP-9425" && k != "114101-SIF-M3-MLP-9428")) Dwg(Path.Combine(totals, key + "_System.dwg"), "assembly-" + key);
            var discovery = AssemblyCatalog.Discover(models);
            Check(discovery.Count == 17 && discovery.Count(a => a.Matches.Count == 1) == 15 && discovery.Count(a => a.State == "文件不存在") == 2, "固定清单准确包含M九项及MLP八项，缺失不自动创建");
            Dwg(Path.Combine(totals, "114101-SIF-M3-M-94220_Wrong.dwg"), "wrong-prefix");
            Check(AssemblyCatalog.Discover(models).SelectMany(a => a.Matches).Count() == 15, "编号匹配不误包含94220等相似前缀");
            var duplicate = Path.Combine(totals, "114101-SIF-M3-M-9422_Duplicate.dwg"); Dwg(duplicate, "duplicate");
            Check(AssemblyCatalog.Discover(models).Single(a => a.Key == "114101-SIF-M3-M-9422").State.Contains("重复"), "同编号多份总装提示歧义而非任取一份"); File.Delete(duplicate);
            var cloudOld = Path.Combine(cloud, "9422_System", "Existing.dwg"); var localOld = Paths.MirrorTarget(models, cloud, cloudOld);
            var cloudNew = Path.Combine(cloud, "9423_System", "New.dwg"); var cloudMirror = Path.Combine(cloud, "9424_System", "Mirror.dwg"); var localMirror = Paths.MirrorTarget(models, cloud, cloudMirror);
            var cloudPublic = Path.Combine(cloud, "Public.dwg");
            Dwg(cloudOld, "publication-new"); Dwg(localOld, "publication-old"); Dwg(cloudNew, "new-file"); Dwg(cloudMirror, "mirror-new"); Dwg(localMirror, "mirror-old"); Dwg(cloudPublic, "unconfirmed");
            var cloudLate = Path.Combine(cloud, "9431_System", "Late.dwg"); var localLate = Paths.MirrorTarget(models, cloud, cloudLate);
            Dwg(cloudLate, "was-same"); Dwg(localLate, "was-same");
            foreach (var a in discovery.Where(a => a.Matches.Count == 1 && a.Key.EndsWith("9422"))) DwgReader.Graph[a.Path] = new List<RefEdge> { new RefEdge { Name = "Existing", Saved = localOld, Resolved = localOld } };
            DwgReader.Graph[discovery.Single(a => a.Key.EndsWith("M-9431")).Path] = new List<RefEdge> { new RefEdge { Name = "Late", Saved = localLate, Resolved = localLate } };
            var originalHashes = discovery.Where(a => a.Matches.Count == 1).ToDictionary(a => a.Path, a => Files.Hash(a.Path));
            var session = new WorkflowSession { ConnectorRoot = cloud, LocalModelsRoot = models, ScopeFolder = totals, Checked = true };
            session.Assemblies = discovery.Where(a => a.Matches.Count == 1).Select(a => Workflow.ScanAssembly(a.Path, new DwgReader())).ToList();
            var config = new Settings { ConnectorRoot = cloud, LocalModelsRoot = models, ProjectScanFolder = totals }; var sync = new SyncService(config, () => { }, state);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, sync);
            var plan = await OneClickUpdate.Plan(session, config, sync, true, CancellationToken.None);
            Check(plan.Existing.Count == 1 && plan.NewFiles.Files.Count == 3 && !plan.NewFiles.Files.Single(f => f.Name == "Mirror.dwg").Transfer.IsUnchanged, "统一规划同时处理已有Pull、云端新增和未引用但日期不同的镜像，不要求分两次操作");
            Check(plan.NewFiles.Files.All(f => !f.Selected), "多个同系统总装或无明确归属时新增默认不勾选，需用户确认");
            var conflictKey = Settings.SyncKey(FolderLink.Parse(cloud), localMirror, cloudMirror);
            config.Baselines[conflictKey] = new Baseline { Hash = "old-other", Version = "old-other" };
            var conflicted = await OneClickUpdate.Plan(session, config, sync, true, CancellationToken.None);
            Check(conflicted.Existing.Count == 1 && conflicted.NewFiles.Files.Count == 3 && conflicted.Diagnostics.Count == 0, "一键更新不再因旧内容基准冲突排除明确云端镜像");
            using (var preview = new NewCloudFilesForm(session, conflicted.NewFiles, conflicted.Existing, conflicted.Diagnostics)) Check(preview.Controls.OfType<TabControl>().Single().TabPages.Count == 2, "无阻止项时不显示空问题页签");
            config.Baselines.Remove(conflictKey);
            session.Sources.Single(s => s.Path == cloudOld).Selected = true;
            var limited = await OneClickUpdate.Plan(session, config, sync, true, CancellationToken.None);
            Check(limited.Existing.Count == 1 && limited.NewFiles.Files.Count == 0, "用户已有明确勾选时一键更新不擅自扩大到其他云端文件");
            Reject(() => OneClickUpdate.RestoreSelection(session, new HashSet<string>(new[] { Path.Combine(cloud, "vanished.dwg") }, StringComparer.OrdinalIgnoreCase)), "勾选文件在重新检测时消失，不回退为更新全部其他文件");
            foreach (var source in session.Sources) source.Selected = false;
            File.AppendAllText(cloudLate, "-published-after-check");
            Host.NewStage = (assembly, placements) =>
            {
                Check(placements.All(p => p.Rotation == -4.42 && p.X == 6653112.1 && p.Y == 29134629), "统一执行向挂接器传递负4.42度及指定大坐标");
                var staged = assembly + ".tmp.dwg"; Dwg(staged, "one-click-attached");
                var graph = DwgReader.Graph.TryGetValue(assembly, out var edges) ? edges : new List<RefEdge>();
                DwgReader.Graph[assembly] = graph.Concat(placements.Select(p => new RefEdge { Name = Path.GetFileName(p.Source), Saved = p.Source, Resolved = p.Source })).ToList();
                return new AttachmentResult { Added = placements.Count, Staged = staged };
            };
            try
            {
                using (var form = new WorkflowForm(session, config, () => { }, () => sync, state))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show(); await form.VerifyUnifiedUpdate(evidence); session = form.Session; form.Close();
                }
            }
            finally { Host.NewStage = null; }
            Check(Files.Hash(localOld) == Files.Hash(cloudOld) && File.Exists(Paths.MirrorTarget(models, cloud, cloudNew)) && Files.Hash(localMirror) == Files.Hash(cloudMirror), "同一批次实际更新旧文件并下载新增和镜像更新");
            Check(Files.Hash(localLate) == Files.Hash(cloudLate), "一键更新重新检测上次相同但随后发布的源文件，不依赖旧状态漏更");
            Check(!File.Exists(Paths.MirrorTarget(models, cloud, cloudPublic)), "未确认归属的公共新文件不静默下载或加入模型");
            Check(originalHashes.Count(p => Files.Hash(p.Key) != p.Value) == 3, "只改写明确新增引用的三份总装，普通Pull不重写两份总装");
            Check(Directory.GetFiles(totals, "*.bak").Length == 3 && Directory.GetFiles(Path.GetDirectoryName(localOld), "*.bak").Length == 0, "统一更新仅备份新增引用的总装，不备份覆盖的Xref源");
            var report = JObject.Parse(File.ReadAllText(session.LastUpdateLog)); var logRows = UpdateReports.Read(session.LastUpdateLog);
            Check(logRows.Count(r => r.Operation == "指定总装检查") == 17 && logRows.Count(r => r.Operation == "新增 Xref") == 3 && report["ChildLogs"].Count() == 2, "统一报告包含两阶段子日志、三份挂接和全部17项检查");
            Check(logRows.Any(r => r.Operation == "新增定位 / 选择" && r.Details.Contains("-4.42")) && logRows.Any(r => r.File == "Public.dwg" && r.State.Contains("未勾选")), "日志记录负角度、未执行原因，避免误报全部已同步");
            Check(logRows.All(r => !string.IsNullOrEmpty(r.Time)) && logRows.First().Time.EndsWith("+08:00"), "统一日志全部行具有新加坡时区日期时间");
            var children = report["ChildLogs"].Values<string>().ToList();
            Check(UpdateReports.Read(children[0]).Any(r => r.Operation == "Pull") && UpdateReports.Read(children[1]).Any(r => r.Operation == "新增 Xref"), "表格读取器兼容已有Pull及新增日志格式");
            using (var viewer = new UpdateLogForm(session.AssemblyAudit.Select(a => new UpdateReportRow { Operation = "指定总装检查", File = a.Key, State = a.State, Local = a.Path }), "17项总装检查"))
            {
                viewer.StartPosition = FormStartPosition.Manual; viewer.Location = new Point(-5000, -5000); viewer.Show(); viewer.Refresh();
                Check(viewer.RowCount == 17, "总装检查表显示完整17行而非只显示关联总装");
                using (var bitmap = new Bitmap(viewer.Width, viewer.Height)) { viewer.DrawToBitmap(bitmap, new Rectangle(0, 0, viewer.Width, viewer.Height)); bitmap.Save(Path.Combine(evidence, "required-assemblies-table.png"), ImageFormat.Png); } viewer.Close();
            }
            var failedCloud = Path.Combine(cloud, "9422_System", "FailedNew.dwg"); Dwg(failedCloud, "failed-new");
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, sync);
            var failedBatch = await NewCloudFiles.Plan(session, config, session.Sources.Where(s => s.Path == failedCloud), sync, CancellationToken.None);
            var target = session.Assemblies.Single(a => a.Name.StartsWith("114101-SIF-M3-M-9422_")); failedBatch.Files[0].Targets = new List<string> { target.Path };
            await NewCloudFiles.Run(session, failedBatch, sync, CancellationToken.None, _ => { }, (_, __) => throw new IOException("模拟统一更新新增失败"), state);
            Workflow.UpdateAffected(session); AssemblyCatalog.Evaluate(session); target.Selected = true;
            Check(target.PendingXrefs.Contains("新增引用未完成") && session.AssemblyAudit.Single(a => a.Path == target.Path).State.Contains("待更新"), "已有Pull成功但新增失败时，总装检查不会误报全部完成");
            target.Selected = true;
            Check(Workflow.ExportJobs(session, Path.Combine(fixture, "nwc"), new[] { target }).Single().CurrentLocal, "新增失败不阻止手动导出当前总装，不冒充新增或同步完成");
        }
    }
}
