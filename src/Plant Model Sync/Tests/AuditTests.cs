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
    internal sealed partial class PreviewForm
    {
        internal void SelectTestUpdates(bool missing)
        {
            foreach (var row in previewRows) row.Update = row.Plan.Blocked == null && !row.Plan.IsUnchanged && (missing || !string.IsNullOrEmpty(row.Plan.LocalHash));
        }
        internal bool MissingDefaultUnchecked => previewRows.Where(r => string.IsNullOrEmpty(r.Plan.LocalHash)).All(r => !r.Update);
        internal bool ShowsLocalPath => Controls.OfType<DataGridView>().Single().Columns.Contains("LocalPath");
    }
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifyExistingTargetScope(string evidence, string missing, Settings config, FolderLink folder)
        {
            var source = Session.Sources.Single();
            WorkflowTestProgram.Check(source.DisplayState == "日期相同" && Workflow.PullRows(Session, true).Count == 1 && modelsGrid.Rows.Count == 1, "混合存在/缺失目标时按现有目标显示相同、选择范围和关联总装");
            SelectSources(true);
            WorkflowTestProgram.Check(!source.Selected, "仅勾选可见更新不会把只缺少其他副本的相同文件勾上");
            source.Selected = true; SetLinkedSources(new[] { source });
            await Pull();
            var baselineKey = Settings.SyncKey(folder, source.Plans.Single(p => File.Exists(p.Local)).Local, source.Path);
            WorkflowTestProgram.Check(!File.Exists(missing) && config.Baselines[baselineKey].Hash == "old" && status.Text.Contains("未复制"), "日期相同不伪造内容基准，也不新建被过滤的缺失目标");
            WorkflowTestProgram.Check(source.State == "可更新" && source.DisplayState == "日期相同", "局部重新计划不把未处理缺失目标冒充全部已同步");
            onlyExisting.Checked = false; SelectSources(true);
            WorkflowTestProgram.Check(Workflow.PullRows(Session, false).Count == 2 && modelsGrid.Rows.Count == 2, "明确取消存在筛选才包含缺失目标及其引用总装");
            followChecks = true; SetLinkedSources(new[] { source }); sourceTable.Search.Text = "No match";
            WorkflowTestProgram.Check(source.Selected && modelsGrid.Rows.Count == 2, "搜索隐藏已勾选源文件不丢失实际 Pull 关联总装范围");
            sourceTable.Search.Clear(); onlyExisting.Checked = true;
            await Task.Delay(30); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "existing-target-scope.png"), ImageFormat.Png); }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyAuditFixes(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "audit-fixtures"); var models = Path.Combine(root, "Models"); var cloud = Path.Combine(root, "Cloud");
            const string system = "114101-SIF-M3-M-9421_OCF";
            var sourceFile = Path.Combine(cloud, system, "OCF_PIPING.dwg"); var existing = Path.Combine(models, "local_" + system, "OCF_PIPING.dwg");
            var missing = Path.Combine(models, "单系统总装文件", "OCF_PIPING.dwg");
            var good = Path.Combine(models, "单系统总装文件", "9421_Good.dwg"); var broken = Path.Combine(models, "单系统总装文件", "9421_Broken.dwg");
            Dwg(sourceFile, "same-publication"); Dwg(existing, "same-publication"); Dwg(good, "assembly"); Dwg(broken, "assembly");
            DwgReader.Graph[good] = new List<RefEdge> { new RefEdge { Name = "Piping", Saved = existing, Resolved = existing } };
            DwgReader.Graph[broken] = new List<RefEdge> { new RefEdge { Name = "Piping", Saved = missing, Resolved = missing } };
            var config = new Settings { LocalModelsRoot = models, ConnectorRoot = cloud }; var folder = FolderLink.Parse(cloud);
            var service = new SyncService(config, () => { }, Path.Combine(root, "state"));
            var session = new WorkflowSession { ConnectorRoot = cloud, LocalModelsRoot = models, ScopeFolder = Path.GetDirectoryName(good), Checked = true };
            session.Assemblies = new[] { good, broken }.Select(p => Workflow.ScanAssembly(p, new CachedReader(new DwgReader()))).ToList();
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            var group = session.Sources.Single();
            Check(group.Plans.Count == 1 && group.Plans[0].Local == existing && group.MissingReferenceIssues.Count == 1 && group.AmbiguousLocals.Contains(missing), "正式镜像已存在时不按同名猜配缺失 Xref 或创建错误副本");
            Workflow.UpdateAffected(session);
            Check(session.Assemblies.Single(a => a.Path == broken).PendingXrefs.Contains("待确认"), "缺失引用仍记为待处理，不冒充引用已修复");
            Check(Workflow.SourceState(group, true) == "日期相同" && Workflow.SourceState(group, false) == "对应有歧义", "缺失引用诊断与现有文件状态分开呈现");

            // 显式逐文件映射代表用户确实需要第二份副本，用来复现一云端对两本地的过滤缺陷。
            config.Mappings[Settings.MapKey(folder, missing)] = sourceFile;
            config.Baselines[Settings.SyncKey(folder, existing, sourceFile)] = new Baseline { Hash = "old", Version = "old" };
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service); group = session.Sources.Single(); group.Selected = true;
            Check(group.Plans.Count == 2 && Workflow.PullRows(session, true).Single().LocalPath == existing && Workflow.PullRows(session, false).Count == 2, "存在筛选按每个本地目标生效，而不是只按云端组任一目标存在");
            using (var form = new WorkflowForm(session, config, () => { }, () => service))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show();
                await form.VerifyExistingTargetScope(evidence, missing, config, folder); form.Close();
            }
            File.AppendAllText(sourceFile, "-new-publication");
            var fresh = await service.Plan(folder, group.Plans.Select(p => p.Row), false, CancellationToken.None, null);
            Check(fresh.Single(p => p.Local == existing).Blocked == null, "确认相同内容会推进同步基准，后续单侧发布不误报双方变更");
            using (var preview = new PreviewForm(folder, fresh, false))
            {
                Check(preview.MissingDefaultUnchecked && preview.ApprovedPlans.Count == 1 && preview.ApprovedPlans[0].Local == existing && preview.ShowsLocalPath, "预览显示完整本地路径，缺失目标默认不勾选");
                preview.StartPosition = FormStartPosition.Manual; preview.Location = new Point(-5000, -5000); preview.Show(); preview.Refresh();
                using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(0, 0, preview.Width, preview.Height)); bitmap.Save(Path.Combine(evidence, "pull-preview-target-paths.png"), ImageFormat.Png); }
                preview.SelectTestUpdates(true);
                Check(preview.ApprovedPlans.Count == 2, "关闭存在筛选后只有用户逐项确认才批准新建缺失目标"); preview.Close();
            }
            using (var preview = new PreviewForm(folder, fresh, false, true))
            {
                preview.SelectTestUpdates(true);
                Check(preview.ApprovedPlans.Count == 1 && preview.ApprovedPlans[0].Local == existing, "存在筛选开启时即使强制勾选也不批准缺失目标");
                Check(fresh.All(p => p.RequireExistingLocal), "预览将仅现有目标限制传递到实际执行防线");
            }
            var aliases = new Dictionary<string, string> { [Path.Combine(models, "OCF")] = Path.GetDirectoryName(existing) }; Paths.ConfigureAliases(aliases);
            var duplicate = await service.Plan(folder, new[] { new XrefRow { Name = "OCF_PIPING.dwg", LocalPath = Path.Combine(models, "OCF", "OCF_PIPING.dwg"), CloudPath = sourceFile }, new XrefRow { Name = "OCF_PIPING.dwg", LocalPath = existing, CloudPath = sourceFile } }, false, CancellationToken.None, null);
            Check(duplicate.All(p => p.Blocked != null), "旧兼容路径和新目录归一后阻止覆盖同一文件两次");
            Check(Paths.Relative(Path.Combine(models, "OCF"), Path.Combine(models, "OCF", "OCF_PIPING.dwg")) == "OCF_PIPING.dwg", "兼容别名计算相对路径时统一两端身份");
            Paths.ConfigureAliases(null);
            var outside = Path.Combine(root, "outside.dwg"); Dwg(outside, "outside");
            var escaped = await service.Plan(folder, new[] { new XrefRow { Name = "outside.dwg", LocalPath = existing, CloudPath = outside } }, false, CancellationToken.None, null);
            Check(escaped.Single().Blocked != null, "Connector 绝对映射不能越出已选根目录");

            var a = session.Assemblies.Single(m => m.Path == good);
            a.PendingXrefs = ""; a.UpdatedPaths = new List<string> { existing }; a.Selected = true;
            a.ExpectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [existing] = Files.Hash(existing) };
            a.ExpectedCloudHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourceFile] = Files.Hash(sourceFile) };
            Workflow.VerifyExportFiles(a);
            File.AppendAllText(sourceFile, "-during-export");
            Reject(() => Workflow.VerifyExportFiles(a), "NWC 导出后仍检查 Connector 内容是否在过程中变化");
            a.ExpectedCloudHashes[sourceFile] = Files.Hash(sourceFile);
            var changedGraph = new ScanResult();
            Reject(() => Workflow.VerifyExportGraph(a, changedGraph), "导出前依赖移除不能仅因更新文件还在就被忽略");
            changedGraph.Rows.AddRange(a.Scan.Rows); changedGraph.Rows.Add(new XrefRow { LocalPath = outside });
            Reject(() => Workflow.VerifyExportGraph(a, changedGraph), "导出前出现新增依赖要求重新检查");
            a.Scan.Warnings.Add("未能读取子引用");
            a.Selected = true;
            Check(Workflow.ExportJobs(session, Path.Combine(root, "nwc"), new[] { a }).Single().CurrentLocal, "旧扫描诊断不阻止当前本地导出，也不声明完整同步"); a.Scan.Warnings.Clear();
            a.RefreshState = "引用重载失败：fixture";
            Check(Workflow.ExportJobs(session, Path.Combine(root, "nwc"), new[] { a }).Single().CurrentLocal, "旧引用重载结果不强制当前导出再次重载");
            Check(Workflow.RecordReferenceRecovery(session, new[] { a }).Contains("fixture") && !session.Checked && session.Warnings.Any(w => w.Contains("引用重载失败")), "宿主内部记录的重载失败会传递到会话与日志状态");
            session.Checked = true; a.RefreshState = "尚未 Pull";
            File.AppendAllText(good, "-saved-change");
            Reject(() => Workflow.VerifyExportFiles(a), "已检查总装的保存内容改变时要求重检");

            var lostPlan = fresh.Single(p => p.Local == existing); lostPlan.RequireExistingLocal = true;
            File.Move(existing, existing + ".test-removed.bak");
            await service.Execute(folder, new List<TransferPlan> { lostPlan }, false, CancellationToken.None, null);
            Check(!File.Exists(existing) && !lostPlan.Written && lostPlan.Row.SyncState == SyncFeedback.Failed, "目标在预览后消失时不退化为新建文件");
            File.Move(existing + ".test-removed.bak", existing);
            a.ExportState = "已导出"; a.ExpectedHashes[outside] = "obsolete";
            Workflow.UpdateAffected(session);
            Check(a.ExportState == "未导出" && a.ExportHash == null, "期望引用集合缩小时清除过期的已导出状态");
        }
    }
}
