using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IWMF.PlantModelSync
{
    internal sealed class ExistingUpdateRow
    {
        public bool Selected { get; set; }
        public TransferPlan Plan { get; set; }
        public string Name => Plan.Row.Name;
        public string State => Plan.Blocked == null ? Plan.Action : "阻止：" + Plan.Blocked;
        public string Local => Plan.Local;
        public string Connector => Plan.ConnectorPath;
        public string Assemblies => string.Join(" | ", Plan.AffectedAssemblies);
    }
    internal sealed class OneClickPlan
    {
        public List<TransferPlan> Existing { get; set; } = new List<TransferPlan>();
        public NewFileBatch NewFiles { get; set; } = new NewFileBatch();
        public List<CloudUpdate> Sources { get; set; } = new List<CloudUpdate>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }
    internal static class OneClickUpdate
    {
        public static void RestoreSelection(WorkflowSession session, HashSet<string> selectedPaths)
        {
            var missing = selectedPaths.Except(session.Sources.Select(s => s.Path), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0) throw new IOException("已勾选云端文件在重新检查后消失，请重新确认；未扩大到其他文件：" + string.Join(" | ", missing));
            foreach (var source in session.Sources) source.Selected = selectedPaths.Contains(source.Path);
        }
        public static async Task<OneClickPlan> Plan(WorkflowSession session, Settings settings, SyncService sync, bool existingOnly, CancellationToken ct)
        {
            if (!session.Checked) throw new IOException("引用检查尚未通过，请先处理重载或扫描异常。");
            var result = new OneClickPlan();
            var checkedSources = session.Sources.Where(s => s.Selected).ToList();
            bool NeedsAttachment(CloudUpdate source) => source.Plans.Count == 0 || source.Plans.All(p => p.AffectedAssemblies.Count == 0);
            result.Sources = checkedSources.Count > 0 ? checkedSources : session.Sources.Where(s => s.Plans.Any(p => !p.IsUnchanged) || NeedsAttachment(s)).ToList();
            var newSources = result.Sources.Where(NeedsAttachment).ToList();
            var rows = result.Sources.Except(newSources).SelectMany(s => Workflow.VisiblePlans(s, existingOnly)).Select(p => p.Row).GroupBy(r => Paths.Canonical(r.LocalPath), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            result.Existing = await sync.Plan(FolderLink.Parse(session.ConnectorRoot), rows, false, ct, null);
            foreach (var p in result.Existing)
            {
                p.RequireExistingLocal = existingOnly;
                p.AffectedAssemblies = session.Assemblies.Where(a => a.Scan.Rows.Any(r => string.Equals(r.LocalPath, p.Local, StringComparison.OrdinalIgnoreCase))).Select(a => a.Path).ToList();
            }
            var eligible = newSources.Where(s => s.AmbiguousLocals.Count == 0 && s.Diagnostics.Count == 0 && s.MissingReferenceIssues.Count == 0 && s.ExpectedLocalPath != null).ToList();
            result.Diagnostics.AddRange(newSources.Except(eligible).Select(s => s.Path + "：对应 / 目录有待核对，未纳入新增。"));
            foreach (var source in eligible)
            {
                try
                {
                    var single = await NewCloudFiles.Plan(session, settings, new[] { source }, sync, ct, true, false);
                    foreach (var file in single.Files) { file.Selected = file.Targets.Count > 0; result.NewFiles.Files.Add(file); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { result.Diagnostics.Add(source.Path + "：" + ex.Message); }
            }
            foreach (var group in result.NewFiles.Files.GroupBy(f => f.Local, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var file in group) { file.Selected = false; file.Transfer.Blocked = "多个云端源指向同一本地目标，需确认。"; }
            if (result.NewFiles.Files.Count > 0) foreach (var a in session.Assemblies) result.NewFiles.AssemblyHashes[a.Path] = Files.Hash(a.Path);
            return result;
        }
        public static void Capture(OneClickReport report, OneClickPlan plan, WorkflowSession session)
        {
            report.Rows = plan.Existing.Select(p => UpdateReports.Transfer(p, "已有文件 Pull")).Concat(plan.NewFiles.Files.Select(f => UpdateReports.Transfer(f.Transfer, "新增源下载"))).ToList();
            foreach (var result in plan.NewFiles.Results) report.Rows.Add(new UpdateReportRow { Operation = "新增 Xref", File = Path.GetFileName(result.Assembly), Local = result.Assembly, Assembly = result.Assembly, State = result.State, Backup = result.Backup, BeforeHash = result.BeforeHash, AfterHash = result.AfterHash, Details = "新增数量=" + result.Added + "；" + string.Join(" | ", result.Sources) });
            foreach (var file in plan.NewFiles.Files) report.Rows.Add(new UpdateReportRow { Operation = "新增定位 / 选择", File = file.Name, Local = file.Local, Connector = file.Cloud, Assembly = string.Join(" | ", file.Targets), State = file.Selected ? file.State : "未勾选；未新增", Details = "WCS=" + file.X + "," + file.Y + "," + file.Z + "；比例=" + file.Scale + "；旋转(度)=" + file.Rotation });
            report.Rows.AddRange(plan.Diagnostics.Select(d => new UpdateReportRow { Operation = "未执行诊断", State = "需核对", Details = d }));
            report.Rows.AddRange(session.AssemblyAudit.Select(a => new UpdateReportRow { Operation = "指定总装检查", File = a.Key, Local = a.Path, Assembly = a.Path, State = a.State, Details = "本次更新：" + a.Updated + "；待更新：" + a.Pending + "；" + a.Diagnostics }));
            foreach (var row in report.Rows.Where(r => string.IsNullOrEmpty(r.Time))) row.Time = new DateTimeOffset(report.StartedUtc).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz");
            report.Save();
        }
    }
}
