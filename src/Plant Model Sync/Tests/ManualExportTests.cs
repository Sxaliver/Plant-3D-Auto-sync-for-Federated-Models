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
    internal sealed class NoExportScanReader : IReferenceReader
    {
        public List<RefEdge> Read(string path) => throw new Exception("当前本地导出不应读取引用图：" + path);
    }
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifyUnscannedExport()
        {
            await LoadAssemblyDirectory();
            var model = browserRows.First(); model.Selected = true;
            var reads = DwgReader.Reads.Values.Sum(); var opened = false;
            using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
            {
                timer.Tick += (s, e) =>
                {
                    var dialog = Application.OpenForms.OfType<ManualNwcExportForm>().FirstOrDefault();
                    if (dialog == null) return;
                    opened = true; timer.Stop(); dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                };
                timer.Start(); await ExportBrowserSelection(); timer.Stop();
            }
            WorkflowTestProgram.Check(opened && Session == null && DwgReader.Reads.Values.Sum() == reads && model.Selected, "主导出按钮无需先检查或联网，取消确认不触发扫描、不改变勾选");
        }
        internal async Task VerifyCheckDefault()
        {
            stateFilter.SelectedIndex = 0; await Check();
            WorkflowTestProgram.Check(stateFilter.SelectedIndex == 1 && sourcesGrid.Rows.Count > 0 && sourcesGrid.Rows.Cast<DataGridViewRow>().All(r => ((CloudUpdate)r.DataBoundItem).DisplayState == "可更新"), "检查可更新DWG完成后默认只看可更新，不影响用户随后切换全部");
            stateFilter.SelectedIndex = 0;
            WorkflowTestProgram.Check(sourcesGrid.Rows.Count > 1, "用户仍能手动切回全部状态查看相同文件");
        }
        internal async Task VerifyManualExportUi(string evidence, string[] files)
        {
            int confirmations = 0;
            using (var confirm = new System.Windows.Forms.Timer { Interval = 25 })
            {
                confirm.Tick += (s, e) =>
                {
                    var preview = Application.OpenForms.OfType<ManualNwcExportForm>().FirstOrDefault(); if (preview == null) return;
                    confirmations++;
                    WorkflowTestProgram.Check(preview.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>()).OfType<DataGridView>().Single().Rows.Count == 2, "手动导出确认表显示两份从文件夹选择的DWG");
                    using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(0, 0, preview.Width, preview.Height)); bitmap.Save(Path.Combine(evidence, "manual-nwc-confirmation.png"), ImageFormat.Png); }
                    preview.DialogResult = DialogResult.OK; preview.Close();
                };
                confirm.Start(); await ExportManualFiles(files); confirm.Stop();
            }
            WorkflowTestProgram.Check(confirmations == 1 && ExportRequests.Count == 2 && ExportRequests.All(j => j.ManualSelection && j.Assembly.UpdatedPaths.Count == 0), "手动导出入口不要求本轮更新且不伪造更新记录，确认后生成原生队列任务");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyManualExport(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "manual-export-fixtures"); var local = Path.Combine(root, "Models"); var cloud = Path.Combine(root, "Cloud"); var output = Path.Combine(root, "NWC");
            var assembly = Path.Combine(local, "A", "Assembly.dwg"); var sameName = Path.Combine(local, "B", "Assembly.dwg"); var empty = Path.Combine(local, "Empty.dwg"); var dependency = Path.Combine(local, "Source.dwg");
            var cloudFile = Path.Combine(cloud, "Source.dwg"); var sameLocal = Path.Combine(local, "Same.dwg"); var sameCloud = Path.Combine(cloud, "Same.dwg");
            Dwg(assembly, "assembly-aa"); Dwg(sameName, "assembly-bb"); Dwg(empty, "empty"); Dwg(dependency, "old"); Dwg(cloudFile, "new"); Dwg(sameLocal, "same"); Dwg(sameCloud, "same");
            DwgReader.Graph[assembly] = new List<RefEdge> { new RefEdge { Name = "Source", Saved = dependency, Resolved = dependency }, new RefEdge { Name = "Same", Saved = sameLocal, Resolved = sameLocal } };
            var config = new Settings { ConnectorRoot = cloud, LocalModelsRoot = local, ProjectScanFolder = Path.Combine(local, "A"), OutputFolder = output };
            var sync = new SyncService(config, () => { }, Path.Combine(root, "state"));
            using (var ui = new WorkflowForm(null, config, () => { }, () => sync, assemblyFirst: true))
            {
                ui.StartPosition = FormStartPosition.Manual; ui.Location = new Point(-5000, -5000); ui.Show(); await Task.Delay(100); await ui.VerifyUnscannedExport(); ui.Close();
            }
            using (var ui = new WorkflowForm(null, config, () => { }, () => sync))
            {
                ui.StartPosition = FormStartPosition.Manual; ui.Location = new Point(-5000, -5000); ui.Show(); await ui.VerifyCheckDefault(); ui.Close();
            }
            var before = Files.Hash(assembly);
            var jobs = ManualNwcExport.Plan(new[] { assembly, sameName, assembly }, output, new DwgReader());
            var dated = ManualNwcExport.DateFolder(output);
            Check(jobs.All(j => Path.GetDirectoryName(j.Target) == dated) && !Directory.Exists(dated), "手动导出自动规划当天日期子目录，确认前不创建目录或文件");
            var singaporeMidnight = new DateTimeOffset(2026, 10, 26, 16, 1, 0, TimeSpan.Zero);
            Check(ManualNwcExport.DateFolder(output, singaporeMidnight) == Path.Combine(output, "2026-10-27"), "日期目录使用新加坡日期而非UTC日期");
            Check(ManualNwcExport.DateFolder(Path.Combine(output, "2026-10-27"), singaporeMidnight) == Path.Combine(output, "2026-10-27"), "已选当天日期目录时复用，不重复嵌套日期");
            Check(ManualNwcExport.Plan(new[] { assembly }, dated, null).All(j => Path.GetDirectoryName(j.Target) == dated), "从文件夹导出和重复导出共用同一天目录");
            Check(jobs.Count == 2 && jobs.Select(j => j.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "手动导出去重重复选择，并为跨目录同名总装生成不同输出名");
            Check(jobs.All(j => j.ManualSelection && j.Assembly.UpdatedPaths.Count == 0) && !Directory.Exists(output) && Files.Hash(assembly) == before, "手动规划允许未Pull模型，不创建NWC或改写DWG，不冒充更新成功");
            Check(jobs.All(j => j.CurrentLocal && j.Assembly.ExpectedHashes.Count == 0 && j.Assembly.ExpectedCloudHashes.Count == 0), "手动导出不获取云端或本地依赖快照，交由原生导出当前状态");
            Check(ManualNwcExport.Plan(new[] { empty }, output, new DwgReader()).Single().Assembly.Scan.Rows.Count == 0, "手动导出允许没有Xref的独立DWG");
            var known = new WorkflowSession { ConnectorRoot = cloud, LocalModelsRoot = local, Checked = true, Assemblies = new List<AssemblyUpdate> { Workflow.ScanAssembly(assembly, new DwgReader()) } };
            known.Sources = await Workflow.Check(known, config, CancellationToken.None, null, sync);
            Check(ManualNwcExport.Plan(new[] { assembly }, output, new NoExportScanReader(), known).Single().CurrentLocal, "已知待Pull且两侧内容不同仍直接导出，不读取引用图");
            known.Checked = false; known.Assemblies[0].Selected = true; known.Assemblies[0].PendingXrefs = "待同步";
            known.Sources[0].Plans[0].Blocked = "Connector 不可用";
            Check(Workflow.ExportJobs(known, output).Single().CurrentLocal, "旧检查失效、待同步及Connector不可用均不阻止手动导出");
            known.Sources[0].Plans[0].Blocked = null;
            File.Copy(cloudFile, dependency, true);
            var good = ManualNwcExport.Plan(new[] { assembly }, output, new DwgReader(), known).Single();
            Check(good.Assembly.ExpectedCloudHashes.Count == 0, "两侧一致也不为当前导出读取或记录Connector快照");
            good.Assembly.ExportState = "已导出"; good.Assembly.ExportPath = good.Target; good.Assembly.ExportHash = "isolated-fixture";
            Workflow.RecordExportResult(known, good);
            Check(known.Assemblies.Single().ExportState == "已导出" && known.Assemblies.Single().UpdatedPaths.Count == 0 && known.UpdatedHashes.Count == 0, "手动导出结果能返回已检测模型列表而不伪造同步历史");
            var fixtureLog = Path.Combine(root, "fixture-manual-export.json");
            File.WriteAllText(fixtureLog, Newtonsoft.Json.JsonConvert.SerializeObject(new[] { new { assembly, manualSelection = true, target = good.Target, result = "已导出", outputHash = "isolated-fixture", updatedXrefs = new string[0] } }), new System.Text.UTF8Encoding(false));
            Check(UpdateReports.Read(fixtureLog).Single().Operation == "手动 NWC 导出" && UpdateReports.Read(fixtureLog).Single().Details.Contains("本地快照"), "表格日志明确区分手动导出与本轮同步更新");
            var currentLog = Path.Combine(root, "fixture-current-export.json");
            File.WriteAllText(currentLog, Newtonsoft.Json.JsonConvert.SerializeObject(new[] { new { assembly, manualSelection = true, currentLocal = true, target = good.Target, result = "已导出", outputHash = "fixture" } }), new System.Text.UTF8Encoding(false));
            Check(UpdateReports.Read(currentLog).Single().Details.Contains("不检查同步"), "当前导出日志不将生成NWC解释为云端同步或完整性通过");
            known.PendingReferenceAdds[assembly] = new List<string> { Path.Combine(local, "New.dwg") };
            Check(ManualNwcExport.Plan(new[] { assembly }, output, new NoExportScanReader(), known).Single().CurrentLocal, "新增引用尚未完成也允许导出当前已拥有的模型"); known.PendingReferenceAdds.Clear();
            known.Sources[0].AmbiguousLocals.Add(dependency);
            Check(ManualNwcExport.Plan(new[] { assembly }, output, new NoExportScanReader(), known).Single().CurrentLocal, "旧云端映射歧义不作为当前本地导出前提"); known.Sources[0].AmbiguousLocals.Clear();
            File.AppendAllText(dependency, "-after-preview");
            Check(good.CurrentLocal && good.Assembly.ExpectedHashes.Count == 0, "手动导出不使用旧引用快照阻止当前版本");
            File.Copy(cloudFile, dependency, true);
            var snapshot = ManualNwcExport.Plan(new[] { assembly }, output, new DwgReader()).Single();
            var stamp = File.GetLastWriteTimeUtc(assembly); Dwg(assembly, "assembly-zz"); File.SetLastWriteTimeUtc(assembly, stamp);
            Check(snapshot.CurrentLocal && snapshot.Assembly.DrawingStamp == null, "当前导出不锁定旧检查时刻的总装版本");
            Dwg(assembly, "assembly-aa");
            var broken = Path.Combine(local, "Broken.dwg"); Dwg(broken, "broken"); DwgReader.Graph[broken] = new List<RefEdge> { new RefEdge { Name = "Missing", Saved = Path.Combine(local, "Missing.dwg"), Resolved = Path.Combine(local, "Missing.dwg") } };
            Check(ManualNwcExport.Plan(new[] { broken }, output, new NoExportScanReader()).Single().CurrentLocal, "缺失引用不触发插件完整性阻止，交由原生导出当前可用内容");
            var receipts = Path.Combine(root, "unverified-history");
            Dwg(good.Target, "export-output-fixture"); good.Assembly.ExportHash = Files.Hash(good.Target);
            NwcExportHistory.Record(receipts, good);
            Check(!Directory.Exists(Path.Combine(receipts, "NwcReceipts")), "当前导出不生成完整源快照或云端同步收据");
            Reject(() => ManualNwcExport.Plan(new[] { Path.Combine(local, "NotThere.dwg") }, output, null), "仍拒绝不存在的总装文件");
            using (var ui = new WorkflowForm(null, config, () => { }, () => sync))
            {
                ui.StartPosition = FormStartPosition.Manual; ui.Location = new Point(-5000, -5000); ui.Show(); await ui.VerifyManualExportUi(evidence, new[] { assembly, sameName });
            }
        }
    }
}
