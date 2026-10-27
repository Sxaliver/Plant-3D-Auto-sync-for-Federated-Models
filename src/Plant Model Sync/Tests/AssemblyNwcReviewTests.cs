using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class AssemblyNwcReviewForm
    {
        internal void ConfirmReviewForTest()
        {
            rows.First(r => r.CanExport).Selected = true; table.Grid.Refresh();
            Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Single(b => b.Text == "导出勾选 NWC").PerformClick();
        }
        internal void VerifyReviewActions(string evidence)
        {
            var grid = table.Grid;
            var ready = rows.Single(r => r.CanExport);
            grid.CurrentCell = grid.Rows.Cast<DataGridViewRow>().Single(r => r.DataBoundItem == ready).Cells["Name"];
            WorkflowTestProgram.Check(references.Rows.Count == 1 && details.Text.Contains(ready.Path), "选中总装后下方真实表格显示对应云端路径与逐Xref明细");
            var buttons = Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).ToList();
            ((VisibleSelectionHeader<AssemblyNwcReviewRow>)grid.Columns["Selected"].HeaderCell).ToggleVisible();
            WorkflowTestProgram.Check(SelectedRows.Count == 1 && SelectedRows.Single() == ready, "表头勾选存在的本地总装，不纳入缺失项");
            table.Search.Text = "does-not-exist"; table.Search.Clear();
            WorkflowTestProgram.Check(SelectedRows.Count == 1 && grid.Rows.Count == 17, "比较表筛选与重置显示不清除导出勾选");
            var blocked = grid.Rows.Cast<DataGridViewRow>().First(r => !((AssemblyNwcReviewRow)r.DataBoundItem).CanExport);
            blocked.Cells["Selected"].Value = true;
            WorkflowTestProgram.Check(!((AssemblyNwcReviewRow)blocked.DataBoundItem).Selected, "即使程序设置勾选也不能选择不存在的本地总装");
            grid.CurrentCell = grid.Rows.Cast<DataGridViewRow>().Single(r => r.DataBoundItem == ready).Cells["Name"]; Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "assembly-cloud-nwc-review.png"), ImageFormat.Png); }
            buttons.Single(b => b.Text == "导出勾选 NWC").PerformClick();
            WorkflowTestProgram.Check(DialogResult == DialogResult.OK && SelectedRows.Count == 1, "比较表导出按钮只提交已确认可导出项至原生队列确认，不执行上传");
        }
    }
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifyAuditExportRoute()
        {
            var audits = 0;
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) =>
                {
                    var review = Application.OpenForms.OfType<UpdateLogForm>().FirstOrDefault();
                    if (review != null) { audits++; WorkflowTestProgram.Check(review.RowCount == 17 && review.Text.StartsWith("1 检查本地 DWG"), "独立本地DWG检查仅展示17项本地引用结果"); review.Close(); }
                };
                timer.Start(); await AuditRequiredAssemblies(); timer.Stop();
            }
            WorkflowTestProgram.Check(audits == 1 && ExportRequests == null, "本地DWG检查不再触发云端对比或NWC导出确认");
            WorkflowTestProgram.Check(Session.UpdatedHashes.Count == 0, "本地检查不伪造本次Pull或已更新Xref记录");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyAssemblyNwcReview(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "nwc-review-fixtures"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector"); var storage = Path.Combine(root, "state");
            var assembly = Path.Combine(local, "单系统总装文件", "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM.dwg");
            var source = Path.Combine(cloud, "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM", "Floor.dwg");
            var target = Paths.MirrorTarget(local, cloud, source);
            Dwg(assembly, "assembly"); Dwg(source, "cloud"); Dwg(target, "cloud");
            var scan = new ScanResult(); scan.Rows.Add(new XrefRow { Name = "Floor.dwg", LocalPath = target });
            var a = new AssemblyUpdate { Name = Path.GetFileName(assembly), Path = assembly, Scan = scan };
            var plan = new TransferPlan { Local = target, ConnectorPath = source, Action = "内容相同，跳过", Row = scan.Rows[0] };
            var s = new CloudUpdate { Path = source, Name = "Floor.dwg", Plans = new List<TransferPlan> { plan } };
            var session = new WorkflowSession { LocalModelsRoot = local, ScopeFolder = Path.GetDirectoryName(assembly), ConnectorRoot = cloud, Assemblies = new List<AssemblyUpdate> { a }, Sources = new List<CloudUpdate> { s } };
            List<AssemblyNwcReviewRow> Review() { session.HashCache.Clear(); Workflow.UpdateAffected(session); AssemblyCatalog.Evaluate(session); return AssemblyNwcReview.Check(session, storage, CancellationToken.None); }
            AssemblyNwcReviewRow Current() => Review().Single(r => r.Path == assembly);
            var first = Current();
            Check(first.CanExport && first.NeedsExport && first.Advice == "需要导出 NWC", "云端本地一致但无可靠NWC记录仍建议导出，不把无本次Pull当作无需导出");
            Check(Review().Count == 17 && Review().Count(r => r.LocalState == "文件不存在") == 16, "NWC检查保留17项固定清单及缺失项");
            Check(first.References.Single().Connector == source && first.References.Single().CloudHash == Files.Hash(source) && first.References.Single().LocalDate.EndsWith("+08:00"), "逐Xref记录真实Connector路径、两侧完整哈希和新加坡日期");
            var output = Path.Combine(root, "NWC"); var job = AssemblyNwcReview.Jobs(new[] { first }, output).Single();
            Check(job.CurrentLocal && job.Assembly.ExpectedHashes.Count == 0 && job.Assembly.ExpectedCloudHashes.Count == 0 && job.ManualSelection, "比较页手动导出同样不依赖两侧同步快照");
            Check(!Directory.Exists(output), "只读比较和导出规划不创建假NWC");
            Dwg(job.Target, "fixture-nwc-output");
            // 独立模拟既有严格导出收据；当前模型导出不生成这种基准。
            var verified = new BatchExportJob { Target = job.Target, Assembly = new AssemblyUpdate { Path = assembly, ExpectedHashes = first.Snapshot, ExportHash = Files.Hash(job.Target), ExportState = "已导出" } };
            NwcExportHistory.Record(storage, verified);
            Check(!Current().NeedsExport && Current().NwcPath == job.Target, "成功NWC的源快照与输出哈希一致时跨会话判定无需重导");
            Dwg(target, "new"); Dwg(source, "new");
            Check(Current().NeedsExport && Current().CanExport && Current().Reason.Contains("发生变化"), "源文件两侧一起更新后即使本轮无Pull仍识别旧NWC需要重导");
            Dwg(assembly, "assembly-revision");
            Check(Current().NeedsExport, "总装自身变化也使成功NWC快照失效");
            Dwg(assembly, "assembly"); Dwg(target, "cloud"); Dwg(source, "cloud");
            File.Delete(job.Target);
            Check(Current().NeedsExport && Current().Reason.Contains("输出缺失"), "已有成功记录但NWC文件丢失时建议重导");
            Dwg(job.Target, "different-nwc");
            Check(Current().NeedsExport, "NWC内容被替换不能仅凭存在和日期判定最新");
            Dwg(source, "new-cloud");
            Check(!Current().CanExport && Current().Advice.Contains("先 Pull") && Current().References.Single().State.Contains("日期不同"), "云端本地日期不同时先Pull，禁止把旧本地直接当最新NWC");
            Check(AssemblyNwcReview.Jobs(new[] { Current() }, output).Single().CurrentLocal, "比较页的待Pull诊断不阻止直接导出本地模型");
            Dwg(source, "cloud"); plan.Blocked = "双方修改冲突";
            Check(!Current().CanExport && Current().Reason.Contains("双方修改冲突"), "比较保留同步冲突防线"); plan.Blocked = null;
            s.AmbiguousLocals.Add(target);
            Check(!Current().CanExport && Current().Reason.Contains("歧义"), "同名多处云端源有歧义时不猜配或导出"); s.AmbiguousLocals.Clear();
            session.Sources.Clear();
            Check(Current().CanExport && Current().References.Single().Connector == source, "没有现成Pull计划时仍按精确local镜像找到云端源");
            session.Sources.Add(s); File.Delete(target);
            Check(!Current().CanExport && Current().Advice.Contains("先 Pull"), "本地缺失但云端可用时明确需Pull且不能导出"); Dwg(target, "cloud");
            a.Scan.Warnings.Add("嵌套引用不能展开");
            Check(!Current().CanExport && Current().Reason.Contains("嵌套引用不能展开"), "两侧内容一致仍不能绕过完整引用诊断"); a.Scan.Warnings.Clear();
            Dwg(target, "localized");
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            var ledger = new WeeklyLedger(); ledger.Sources[target] = new WeeklySourceReceipt { LocalHash = Files.Hash(target), CloudHash = Files.Hash(source), CloudPath = source };
            WeeklyUpdate.AtomicJson(Path.Combine(storage, "weekly-ledger.json"), ledger);
            Check(Current().CanExport && Current().References.Single().State.Contains("日期相同"), "路径本地化保持源日期后无需反复Pull，允许不同哈希");
            plan.Blocked = "Pull 目标不在本地模型根目录内";
            Check(!Current().CanExport, "本地化收据不能绕过非冲突类阻止原因"); plan.Blocked = null;
            s.Diagnostics.Add("已保存文件对应失效");
            Check(!Current().CanExport, "实际内容一致或有本地化基准仍不能绕过云端映射诊断"); s.Diagnostics.Clear();
            Dwg(source, "cloud-newer");
            Check(!Current().CanExport, "云端变化使旧路径本地化收据失效"); Dwg(source, "cloud");
            var publicFile = Path.Combine(cloud, "Unrelated.dwg"); Dwg(publicFile, "public");
            session.Sources.Add(new CloudUpdate { Name = "Unrelated.dwg", Path = publicFile });
            Check(Current().CanExport, "不把无系统归属公共新文件误认为指定系统必须加入的引用");
            var added = Path.Combine(Path.GetDirectoryName(source), "NewFloor.dwg"); Dwg(added, "new-floor");
            session.Sources.Add(new CloudUpdate { Name = "NewFloor.dwg", Path = added, ExpectedLocalPath = Paths.MirrorTarget(local, cloud, added) });
            var pending = Current();
            Check(!pending.CanExport && pending.Reason.Contains("未引用候选") && pending.References.Any(r => r.Connector == added), "云端同系统新增未引用DWG单列候选需确认，不能被本地依赖图漏掉");
            session.Sources.RemoveAll(x => x.Path == added); File.Delete(added);
            var ready = Current(); var before = new[] { Files.Hash(assembly), Files.Hash(target), Files.Hash(source) };
            var log = AssemblyNwcReview.SaveReport(storage, session, Review());
            Check(UpdateReports.Read(log).Count == 17 && UpdateReports.Read(log).Single(r => r.Local == assembly).Connector == source, "检查日志保留17份结论且Connector列包含实际源路径");
            Check(before.SequenceEqual(new[] { Files.Hash(assembly), Files.Hash(target), Files.Hash(source) }), "比较和记录不Pull、不改写总装或Xref");
            var stale = AssemblyNwcReview.Jobs(new[] { ready }, output).Single(); Dwg(source, "changed-after-preview");
            Check(AssemblyNwcReview.Jobs(new[] { ready }, output).Single().CurrentLocal, "对比后云端变化不影响用户手动导出当前本地模型");
            Check(stale.CurrentLocal && stale.Assembly.ExpectedCloudHashes.Count == 0, "对比页导出同样不锁定云端快照"); Dwg(source, "cloud");
            using (var form = new AssemblyNwcReviewForm(Review(), cloud, log))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show(); form.Refresh();
                Check(form.RowCount == 17 && form.SelectedRows.Count == 0, "真实NWC检查表保留17行且不自动执行导出");
                form.VerifyReviewActions(evidence); form.Close();
            }
            var weekly = new WeeklyLedger();
            var weeklyCurrent = Current(); var weeklyNwc = Path.Combine(output, "weekly.nwc"); Dwg(weeklyNwc, "weekly-output");
            weekly.Sources[target] = ledger.Sources[target]; weekly.Exports[assembly] = new WeeklyExportReceipt { Signature = WeeklyUpdate.Signature(weeklyCurrent.Snapshot), Output = weeklyNwc, OutputHash = Files.Hash(weeklyNwc) };
            WeeklyUpdate.AtomicJson(Path.Combine(storage, "weekly-ledger.json"), weekly);
            Check(!Current().NeedsExport && Current().NwcPath == weeklyNwc, "交互检查可读取既有周更成功导出基准，不要求重新导出建立第二套记录");
            DwgReader.Graph[assembly] = new List<RefEdge> { new RefEdge { Name = "Floor", Saved = target, Resolved = target } };
            var config = new Settings { ConnectorRoot = cloud, LocalModelsRoot = local, ProjectScanFolder = Path.GetDirectoryName(assembly), OutputFolder = output };
            using (var form = new WorkflowForm(session, config, () => { }, () => new SyncService(config, () => { }, storage), storage))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show();
                await form.VerifyAuditExportRoute(); form.Close();
            }
        }
    }
}
