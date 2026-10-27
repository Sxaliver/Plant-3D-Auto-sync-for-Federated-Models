using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IWMF.PlantModelSync
{
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyPullDates(string evidence)
        {
            var root = Path.Combine(evidence, "pull-date-policy"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector");
            var target = Path.Combine(local, "Source.dwg"); var source = Path.Combine(cloud, "Source.dwg"); var assembly = Path.Combine(local, "Assembly.dwg");
            Dwg(source, "cloud-content"); Dwg(target, "local-edit"); Dwg(assembly, "assembly");
            var timestamp = new DateTime(2026, 10, 21, 9, 40, 45, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, timestamp); File.SetLastWriteTimeUtc(target, timestamp);
            var config = new Settings { LocalModelsRoot = local, ConnectorRoot = cloud }; var folder = FolderLink.Parse(cloud);
            var key = Settings.SyncKey(folder, target, source); config.Baselines[key] = new Baseline { Hash = "old-local", Version = "old-cloud" };
            var service = new SyncService(config, () => { }, Path.Combine(root, "state"));
            var row = new XrefRow { Name = "Source.dwg", LocalPath = target, CloudPath = source };
            async Task<TransferPlan> Plan(bool push = false) => (await service.Plan(folder, new[] { row }, push, CancellationToken.None, null)).Single();
            var equal = await Plan();
            Check(equal.IsUnchanged && equal.Blocked == null && equal.LocalHash != equal.ConnectorHash, "相同修改日期即跳过，内容不同及旧冲突基准不影响Pull判断");
            var before = Files.Hash(target);
            await service.Execute(folder, new List<TransferPlan> { equal }, false, CancellationToken.None, null);
            Check(!equal.Written && equal.Verified && Files.Hash(target) == before && row.SyncState == SyncFeedback.DateMatched, "日期相同执行不复制、不冒充字节已同步");
            Check(config.Baselines[key].Hash == "old-local" && config.SyncReceipts.Count == 0, "日期相同不伪造内容基准或收据");
            var session = new WorkflowSession { Checked = true, ConnectorRoot = cloud, LocalModelsRoot = local };
            DwgReader.Graph[assembly] = new List<RefEdge> { new RefEdge { Name = "Source", Saved = target, Resolved = target } };
            session.Assemblies.Add(Workflow.ScanAssembly(assembly, new DwgReader()));
            session.Sources.Add(new CloudUpdate { Path = source, Plans = new List<TransferPlan> { equal } });
            Workflow.UpdateAffected(session); session.Assemblies[0].Selected = true;
            var checkedAssembly = session.Assemblies[0];
            Check(checkedAssembly.ExpectedHashes[target] == before && checkedAssembly.ExpectedCloudHashes[source] == Files.Hash(source), "日期相同但内容不同导出分别保护两侧快照，不错误套用云端哈希到本地");
            File.SetLastWriteTimeUtc(target, timestamp.AddDays(2));
            var newerLocal = await Plan();
            Check(!newerLocal.IsUnchanged && newerLocal.Blocked == null, "本地日期更晚仍按云端为准列为可更新");
            using (var preview = new PreviewForm(folder, new List<TransferPlan> { newerLocal }, false))
            {
                preview.StartPosition = System.Windows.Forms.FormStartPosition.Manual; preview.Location = new System.Drawing.Point(-5000, -5000); preview.Show();
                var grid = preview.Controls.OfType<System.Windows.Forms.DataGridView>().Single();
                Check(!grid.Columns["LocalHash"].Visible && grid.Columns["LocalTime"].DisplayIndex == 3 && grid.Columns["TargetTime"].DisplayIndex == 4, "Pull预览优先展示修改时间且隐藏哈希列");
                using (var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, preview.Width, preview.Height)); bitmap.Save(Path.Combine(evidence, "pull-date-preview.png"), System.Drawing.Imaging.ImageFormat.Png); }
                preview.Close();
            }
            Check((await Plan(true)).Blocked != null, "本轮只改Pull，Push仍保留原冲突保护");
            await service.Execute(folder, new List<TransferPlan> { newerLocal }, false, CancellationToken.None, null);
            Check(newerLocal.Written && newerLocal.Verified && newerLocal.BackupPath == null && Directory.GetFiles(local, "*.pms-*.bak").Length == 0 && Files.Hash(target) == Files.Hash(source), "覆盖本地较新Xref不生成备份，复制后仍校验内容");
            Check(File.GetLastWriteTimeUtc(target) == timestamp && (await Plan()).IsUnchanged, "Pull保留云端时间，下一轮日期检查不重复下载");
            Check(newerLocal.AfterLocalModifiedUtc == timestamp && newerLocal.FinishedUtc > timestamp, "下载操作时间与源修改时间分别记录，不用下载时刻覆盖文件日期");
            var transferRow = UpdateReports.Transfer(newerLocal, "Pull");
            Check(transferRow.SourceModifiedTime == UpdateReports.FileTime(timestamp) && transferRow.LocalModifiedTime == transferRow.SourceModifiedTime, "一键更新日志分别展示源修改时间和本地文件修改时间");
            File.SetLastWriteTimeUtc(source, timestamp.AddHours(1)); var dateOnly = await Plan();
            Check(!dateOnly.IsUnchanged && dateOnly.LocalHash == dateOnly.ConnectorHash, "内容相同但云端日期变化仍按日期更新");
            File.SetLastWriteTimeUtc(source, timestamp.AddHours(2));
            await service.Execute(folder, new List<TransferPlan> { dateOnly }, false, CancellationToken.None, null);
            Check(!dateOnly.Written && row.SyncState == SyncFeedback.Failed && File.GetLastWriteTimeUtc(target) == timestamp, "预览后仅时间变化也停止旧计划，不覆盖目标");
            var missing = Path.Combine(local, "Missing.dwg");
            var create = (await service.Plan(folder, new[] { new XrefRow { Name = "Source.dwg", LocalPath = missing, CloudPath = source } }, false, CancellationToken.None, null)).Single();
            var createdLog = await service.Execute(folder, new List<TransferPlan> { create }, false, CancellationToken.None, null);
            Check(create.Written && create.Verified && File.GetLastWriteTimeUtc(missing) == File.GetLastWriteTimeUtc(source), "本地缺失时下载并保留云端日期");
            Check(File.GetCreationTimeUtc(missing) != File.GetLastWriteTimeUtc(missing), "新下载的创建时间与源修改时间不同，不把创建时间用于更新比较");
            var createdRow = UpdateReports.Read(createdLog).Single();
            Check(createdRow.SourceModifiedTime == createdRow.LocalModifiedTime && createdRow.SourceModifiedTime == UpdateReports.FileTime(create.ConnectorModifiedUtc), "普通Pull日志读回也保留源日期和落盘日期");
            using (var viewer = new UpdateLogForm(Path.GetDirectoryName(createdLog), createdLog))
            {
                var logTable = viewer.Controls.OfType<IWMF.ColumnAutoModel.ReviewTable<UpdateReportRow>>().Single();
                Check(logTable.Grid.Columns["Time"].HeaderText == "操作时间" && logTable.Grid.Columns["SourceModifiedTime"].HeaderText == "Connector 源修改时间" && logTable.Grid.Columns["LocalModifiedTime"].HeaderText == "本地文件修改时间", "日志表头明确区分操作时间与两侧文件日期");
            }
            // 仅修改文件日期也执行免备份更新；旧历史备份不删除。
            var historical = target + ".pms-history.bak"; Dwg(historical, "historical-content"); var historicalHash = Files.Hash(historical);
            var dateUpdate = await Plan();
            await service.Execute(folder, new List<TransferPlan> { dateUpdate }, false, CancellationToken.None, null);
            Check(dateUpdate.Written && dateUpdate.Verified && dateUpdate.AfterLocalModifiedUtc == dateUpdate.ConnectorModifiedUtc && Directory.GetFiles(local, "*.pms-*.bak").Single() == historical && Files.Hash(historical) == historicalHash, "日期更新不产生新备份、不删除历史备份，下一轮日期一致");
            // 共享原子安装器的默认分支仍保护总装及仅本地路径修复。
            var stagedAssembly = Path.Combine(local, "assembly.stage"); Dwg(stagedAssembly, "assembly-updated");
            var assemblyBefore = Files.Hash(assembly); var backup = Files.InstallDownload(stagedAssembly, assembly, assemblyBefore);
            Check(File.Exists(backup) && Files.Hash(backup) == assemblyBefore, "免备份只限Pull，修改总装仍保存原图备份");
        }
    }
}
