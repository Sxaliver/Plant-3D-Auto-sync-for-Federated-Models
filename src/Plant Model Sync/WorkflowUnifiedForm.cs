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
        private async Task RefreshAfterUpdate(SyncService service)
        {
            Session.Checked = false;
            var previous = Session.Assemblies.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);
            var paths = (assemblyBrowser != null ? previous.Keys.AsEnumerable() : previous.Keys.Concat(AssemblyCatalog.Discover(Session.LocalModelsRoot).SelectMany(a => a.Matches))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var reader = new CachedReader(new DwgReader(), DwgReader.IsOpen); var assemblies = new List<AssemblyUpdate>();
            Session.Warnings.Clear();
            foreach (var path in paths)
            {
                status.Text = "更新后检查总装：" + Path.GetFileName(path);
                var fresh = Workflow.ScanAssembly(path, reader, assemblyBrowser != null);
                if (previous.TryGetValue(path, out var old))
                {
                    fresh.Selected = old.Selected; fresh.RefreshState = old.RefreshState;
                    if (fresh.DrawingStamp == old.DrawingStamp) { fresh.ExportState = old.ExportState; fresh.ExportHash = old.ExportHash; fresh.ExportPath = old.ExportPath; fresh.ExpectedHashes = old.ExpectedHashes; }
                }
                if (Session.AddedReferences.ContainsKey(path) && !fresh.RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal)) fresh.RefreshState = "源文件已更新；导出时加载";
                assemblies.Add(fresh); Session.Warnings.AddRange(fresh.Scan.Warnings.Select(w => fresh.Name + "：" + w));
                await Task.Yield();
            }
            Session.Assemblies = assemblies; Session.ReferenceCache = reader; Session.HashCache.Clear();
            Session.Sources = await Task.Run(() => Workflow.Check(Session, settings, CancellationToken.None, null, service, Session.HashCache));
            LimitBrowserSources();
            Workflow.UpdateAffected(Session); if (assemblyBrowser == null) AssemblyCatalog.Evaluate(Session);
            foreach (var a in Session.Assemblies.Where(a => a.UpdatedPaths.Count > 0))
            {
                a.Selected = true;
                if (a.RefreshState == "尚未 Pull") a.RefreshState = "源文件已更新；导出时加载";
            }
            Session.Checked = !Session.Assemblies.Any(a => a.RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal));
        }

        private async Task AuditRequiredAssemblies()
        {
            await Busy(async ct =>
            {
                var rows = LocalAssemblyCheck.Check(localRoot.Text, new DwgReader(), ct, message => status.Text = message);
                var log = Path.Combine(reportStorage, "Logs", "LocalDwgChecks", "local-dwg-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                WeeklyUpdate.AtomicJson(log, new { Format = "IWMF.OneClickUpdate.v1", State = "本地 DWG 检查完成；未比较云端、未导出", Rows = rows });
                if (Session != null) Session.LastLog = log;
                status.Text = "本地 DWG 检查完成 · " + rows.Count + " 项"; uiTips.SetToolTip(status, log);
                await Task.Yield();
                using (var dialog = new UpdateLogForm(rows, "1 检查本地 DWG", true)) dialog.ShowDialog(this);
            });
        }

        private void CheckNwcDates()
        {
            using (var dialog = new NwcTimestampCompareForm(string.IsNullOrWhiteSpace(settings.NwcCompareLocalRoot) ? output.Text : settings.NwcCompareLocalRoot, settings.ConnectorNwcRoot))
            {
                dialog.ShowDialog(this);
                settings.NwcCompareLocalRoot = dialog.LocalRoot; settings.ConnectorNwcRoot = dialog.ConnectorRoot; persist();
            }
        }

        private async Task UpdateAll()
        {
            if (Session == null || !Session.Checked || !InputMatchesSession) await Check();
            if (Session == null || !Session.Checked || !InputMatchesSession) return;
            await Busy(async ct =>
            {
                ValidateSession(); sourcesGrid.EndEdit(); var sync = createService();
                // 按钮每次都重新检测，避免上次“相同”的云端文件随后改变却未进入计划。
                var selectedPaths = new HashSet<string>(Session.Sources.Where(s => s.Selected).Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
                await RefreshAfterUpdate(sync);
                OneClickUpdate.RestoreSelection(Session, selectedPaths);
                BindSources(); ValidateSession(); ct.ThrowIfCancellationRequested();
                var plan = await Task.Run(() => OneClickUpdate.Plan(Session, settings, sync, onlyExisting.Checked, ct), ct);
                List<TransferPlan> approved;
                using (var preview = new NewCloudFilesForm(Session, plan.NewFiles, plan.Existing, plan.Diagnostics))
                {
                    if (preview.ShowDialog(this) != DialogResult.OK) return;
                    approved = preview.ApprovedExisting;
                }
                if (plan.NewFiles.Files.Any(f => f.Selected)) NewCloudFiles.ValidateSelection(Session, plan.NewFiles);
                var report = OneClickReport.Create(reportStorage);
                Session.LastUpdateLog = Session.LastLog = report.Path;
                report.State = "执行中"; OneClickUpdate.Capture(report, plan, Session);
                try
                {
                    if (approved.Count > 0)
                    {
                        var folder = FolderLink.Parse(Session.ConnectorRoot);
                        var journal = sync.BeginUpdateLog(folder, approved, false, Session.ScopeFolder); report.ChildLogs.Add(journal.DetailPath); report.Save();
                        var changing = approved.Where(p => !p.IsUnchanged).ToList();
                        var affected = Session.Assemblies.Where(a => a.Scan.Rows.Any(r => changing.Any(p => string.Equals(p.Local, r.LocalPath, StringComparison.OrdinalIgnoreCase)))).ToList();
                        IDisposable release = null;
                        try
                        {
                            if (changing.Count > 0) release = Host.ReleaseWorkflowReferences(affected, changing.Select(p => p.Row));
                            await Task.Run(() => sync.Execute(folder, approved, false, ct, null, journal));
                        }
                        finally
                        {
                            try { release?.Dispose(); }
                            catch (Exception ex) { foreach (var a in affected) a.RefreshState = "引用重载失败：" + ex.Message; }
                            var error = Workflow.RecordReferenceRecovery(Session, affected);
                            if (error != null) { report.Error = error; journal.Write("引用恢复失败 / 需重检", error); }
                            Workflow.RecordPull(Session, approved); OneClickUpdate.Capture(report, plan, Session);
                        }
                    }
                    ct.ThrowIfCancellationRequested();
                    if (plan.NewFiles.Files.Any(f => f.Selected))
                    {
                        try { await NewCloudFiles.Run(Session, plan.NewFiles, sync, ct, Host.CheckAssemblyClosedForAttach, Host.StageNewReferences, reportStorage); }
                        finally { if (plan.NewFiles.LogPath != null) report.ChildLogs.Add(Path.ChangeExtension(plan.NewFiles.LogPath, ".json")); }
                    }
                }
                catch (Exception ex) { report.Error = ex is OperationCanceledException ? "用户取消；此前完成项保留" : ex.Message; throw; }
                finally
                {
                    try
                    {
                        await RefreshAfterUpdate(sync);
                        stateFilter.SelectedIndex = 0;
                        SetLinkedSources(Session.Sources.Where(s => plan.Sources.Any(p => string.Equals(p.Path, s.Path, StringComparison.OrdinalIgnoreCase))));
                        BindSources(); ShowAffected(); ShowSource(); ShowModel();
                    }
                    catch (Exception ex) { Session.Checked = false; report.Error = (report.Error == null ? "" : report.Error + "；") + "更新后检查失败：" + ex.Message; }
                    Session.LastUpdateLog = Session.LastLog = report.Path;
                    report.State = report.Error != null ? "中止 / 需核对" : approved.Any(p => !p.Verified || p.Row.SyncState == SyncFeedback.Failed) || plan.NewFiles.Results.Any(r => r.AfterHash == null) || plan.NewFiles.Files.Any(f => !f.Selected) || plan.Diagnostics.Count > 0 || Session.AssemblyAudit.Any(a => !a.State.Contains("检查通过")) ? "完成，含待处理 / 未执行项" : "完成";
                    OneClickUpdate.Capture(report, plan, Session); Save();
                }
                status.Text = "一键更新完成 · 更新 " + approved.Count(p => p.Written && p.Verified) + " · 新增 " + plan.NewFiles.Results.Where(r => r.AfterHash != null).Sum(r => r.Added) + " · 失败 " + (approved.Count(p => p.Row.SyncState == SyncFeedback.Failed) + plan.NewFiles.Results.Count(r => r.AfterHash == null)) + " · 缺失总装 " + Session.AssemblyAudit.Count(a => a.State == "文件不存在"); uiTips.SetToolTip(status, Session.LastLog ?? "");
            });
        }
    }
}
