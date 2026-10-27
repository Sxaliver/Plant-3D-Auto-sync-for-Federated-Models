using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal void SetReferenceSource(ReferenceComparisonRow row, string file)
        {
            ValidateSession();
            if (row == null || !Path.IsPathRooted(row.Local ?? "") || !Paths.IsWithin(Session.LocalModelsRoot, row.Local) || Paths.IsHistory(row.Local)) throw new IOException("请先选择正式本地模型目录内的文件。");
            string target;
            try { target = Paths.SyncTarget(Session.ConnectorRoot, file); }
            catch (ArgumentException ex) { throw new IOException(ex.Message, ex); }
            Files.CheckDwg(target);
            // 只有用户在文件选择器确认后才保存对应，不下载、不改写总装或源文件。
            settings.Mappings[Settings.MapKey(FolderLink.Parse(Session.ConnectorRoot), row.Local)] = target;
            persist();
        }
        private async Task ConfirmReferenceSource()
        {
            if (browserBusy || work != null) return;
            var row = referenceBrowser.Grid.CurrentRow?.DataBoundItem as ReferenceComparisonRow;
            if (row == null) { status.Text = "请选择右侧 Xref"; return; }
            var view = displayedAssemblyPath;
            using (var picker = new OpenFileDialog { Title = "指定对应的 Connector DWG（仅保存对应，不下载）", Filter = "AutoCAD 图形 (*.dwg)|*.dwg", CheckFileExists = true, InitialDirectory = ExistingReferenceFolder(row.Cloud) ?? Session.ConnectorRoot })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                SetReferenceSource(row, picker.FileName);
            }
            await InspectPaths(new[] { view });
            status.Text = "云端对应已保存；未下载、未改写 DWG";
        }
        // 一个明确的云端/本地对只产生一项下载计划，不能扩展到该源的其他副本。
        internal async Task<List<TransferPlan>> PlanReferenceDownload(ReferenceComparisonRow row, string target, CancellationToken ct)
        {
            ValidateSession();
            if (row?.CandidateCloudPaths.Count > 1) throw new IOException("存在多个云端候选，请右键“指定云端文件…”确认唯一对应后再下载。");
            if (row == null || !Session.Sources.Any(s => string.Equals(s.Path, row.Cloud, StringComparison.OrdinalIgnoreCase))) throw new IOException("此文件没有已识别的云端来源，请先确认对应关系。");
            if (!Path.IsPathRooted(target ?? "") || !Paths.IsWithin(Session.LocalModelsRoot, target) || Paths.IsHistory(target)) throw new IOException("下载位置必须位于本地模型工作目录内。");
            if (!target.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 DWG 文件路径。");
            for (var parent = Path.GetDirectoryName(Paths.Canonical(target)); Paths.IsWithin(Session.LocalModelsRoot, parent); parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && !TargetFinder.CanTraverse(parent)) throw new IOException("下载目录包含未确认的目录链接：" + parent);
            var plan = await createService().Plan(FolderLink.Parse(Session.ConnectorRoot), new[] { new XrefRow { Name = row.Name, LocalPath = target, CloudPath = row.Cloud } }, false, ct, null);
            foreach (var item in plan) item.AffectedAssemblies = Session.Assemblies.Where(a => a.Scan.Rows.Any(r => string.Equals(r.LocalPath, item.Local, StringComparison.OrdinalIgnoreCase))).Select(a => a.Path).ToList();
            return plan;
        }

        private async Task DownloadCurrentReference()
        {
            if (browserBusy || work != null) return;
            var row = referenceBrowser.Grid.CurrentRow?.DataBoundItem as ReferenceComparisonRow;
            if (row == null) { status.Text = "请选择右侧的 Xref"; return; }
            if (row.CandidateCloudPaths.Count > 1) throw new IOException("存在多个云端候选，请右键“指定云端文件…”确认唯一对应后再下载。");
            var source = Session?.Sources.FirstOrDefault(s => string.Equals(s.Path, row.Cloud, StringComparison.OrdinalIgnoreCase));
            if (source == null) throw new IOException("没有可下载的云端对应文件。");
            var known = source.Plans.FirstOrDefault(p => string.Equals(p.Local, row.Local, StringComparison.OrdinalIgnoreCase));
            var target = row.Local;
            var manualTarget = known == null && row.Referenced;
            if (manualTarget)
            {
                // 缺失的旧引用路径与正式镜像可能不同，用户确认目标后才下载。
                var suggested = File.Exists(row.Local) ? row.Local : source.ExpectedLocalPath ?? row.Local;
                using (var picker = new SaveFileDialog { Title = "确认下载位置（仅下载文件，不修改总装引用）", Filter = "DWG (*.dwg)|*.dwg", FileName = Path.GetFileName(suggested), InitialDirectory = Directory.Exists(Path.GetDirectoryName(suggested)) ? Path.GetDirectoryName(suggested) : Session.LocalModelsRoot, OverwritePrompt = false })
                { if (picker.ShowDialog(this) != DialogResult.OK) return; target = picker.FileName; }
            }
            var view = displayedAssemblyPath; var paths = Session.Assemblies.Select(a => a.Path).ToArray();
            browserBusy = true; assemblyBrowser.Enabled = referenceBrowser.Enabled = false;
            try
            {
                await Busy(async ct =>
                {
                    var plans = await PlanReferenceDownload(row, target, ct);
                    using (var preview = new PreviewForm(FolderLink.Parse(Session.ConnectorRoot), plans, false, false))
                    {
                        if (preview.ShowDialog(this) != DialogResult.OK) return;
                        plans = preview.ApprovedPlans;
                    }
                    if (plans.Count == 0) return;
                    var sync = createService(); var journal = sync.BeginUpdateLog(FolderLink.Parse(Session.ConnectorRoot), plans, false, Session.ScopeFolder);
                    Session.LastLog = Session.LastUpdateLog = journal.Path;
                    journal.Write("执行中", null);
                    var affected = Session.Assemblies.Where(a => plans.Any(p => p.AffectedAssemblies.Contains(a.Path))).ToList();
                    IDisposable released = null;
                    try
                    {
                        released = Host.ReleaseWorkflowReferences(affected, plans.Where(p => !p.IsUnchanged).Select(p => p.Row));
                        await Task.Run(() => sync.Execute(FolderLink.Parse(Session.ConnectorRoot), plans, false, ct, null, journal), ct);
                        if (manualTarget && plans.All(p => p.Verified)) { settings.Mappings[Settings.MapKey(FolderLink.Parse(Session.ConnectorRoot), target)] = row.Cloud; persist(); }
                    }
                    finally
                    {
                        try { released?.Dispose(); }
                        catch (Exception ex) { foreach (var a in affected) a.RefreshState = "引用重载失败：" + ex.Message; Session.Warnings.Add(ex.Message); }
                        Workflow.RecordPull(Session, plans); Workflow.RecordReferenceRecovery(Session, affected); Session.Checked = false;
                        journal.Write(plans.All(p => p.Verified) ? "完成" : "未完成 / 需核对", string.Join(" | ", plans.Where(p => !p.Verified).Select(p => p.Row.Result ?? "下载未完成")));
                    }
                    status.Text = "下载完成 · 成功 " + plans.Count(p => p.Verified) + " · 失败 " + plans.Count(p => !p.Verified);
                });
            }
            finally { browserBusy = false; assemblyBrowser.Enabled = referenceBrowser.Enabled = true; }
            await InspectPaths(paths); ShowReferenceComparison(view);
        }
    }
}
