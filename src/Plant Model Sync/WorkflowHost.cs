using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

namespace IWMF.PlantModelSync
{
    internal static partial class Host
    {
        public static IDisposable ReleaseWorkflowReferences(List<AssemblyUpdate> assemblies, IEnumerable<XrefRow> targets)
        {
            CheckSourcesClosed(targets);
            var open = assemblies.Select(a => (Assembly: a, Document: Find(a.Path))).Where(x => x.Document != null).ToList();
            foreach (var item in open)
            {
                var command = item.Document.CommandInProgress;
                if (!string.IsNullOrWhiteSpace(command) && !string.Equals(command, "SYNC", StringComparison.OrdinalIgnoreCase)) throw new IOException("总装仍有命令运行，请先结束：" + item.Assembly.Name + " · " + command);
            }
            var release = new WorkflowRelease();
            try
            {
                foreach (var item in open) release.Scopes.Add((item.Assembly, new UnloadScope(item.Document)));
                return release;
            }
            catch { release.Dispose(); throw; }
        }

        private sealed class WorkflowRelease : IDisposable
        {
            internal readonly List<(AssemblyUpdate Assembly, IDisposable Scope)> Scopes = new List<(AssemblyUpdate, IDisposable)>();
            public void Dispose()
            {
                foreach (var item in Scopes.AsEnumerable().Reverse())
                {
                    try { item.Scope.Dispose(); item.Assembly.RefreshState = "引用已重载"; }
                    catch (Exception ex) { item.Assembly.RefreshState = "引用重载失败：" + ex.Message; }
                }
                Scopes.Clear();
            }
        }

        private static void ReloadLoadedReferences(Document doc)
        {
            var ids = new ObjectIdCollection();
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in DwgReader.DirectIds(doc.Database))
                {
                    var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!block.IsUnloaded) ids.Add(id);
                }
                tr.Commit();
            }
            if (ids.Count > 0) doc.Database.ReloadXrefs(ids);
            doc.Editor.Regen();
        }

        public static void QueueWorkflowExports(WorkflowSession session, List<BatchExportJob> jobs)
        {
            if (Commands.BatchJobs != null) throw new IOException("已有总装 NWC 批次正在执行。");
            Directory.CreateDirectory(session.OutputFolder);
            Commands.BatchSession = session; Commands.ResumeWorkflow = session;
            Commands.BatchOrigin = AcApp.DocumentManager.MdiActiveDocument;
            Commands.BatchJobs = new Queue<BatchExportJob>(jobs); Commands.BatchStop = false;
            Commands.DispatchBatch();
        }

        internal static void ExportWorkflowJob(BatchExportJob job)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !string.Equals(Paths.Canonical(doc.Name), job.Assembly.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("活动图纸不是待导出的总装，已停止此项。");
            if (job.CurrentLocal)
            {
                // 不重载、修复或比较云端；已打开的总装按当前内存状态交给原生导出器。
                ExportCurrent(job.Target, requireResolvedReferences: false);
                job.Assembly.ExportHash = Files.Hash(job.Target);
                job.Assembly.ExportPath = job.Target; job.Assembly.ExportState = "已导出";
                job.Assembly.RefreshState = "当前本地模型；未核对云端";
                return;
            }
            Workflow.VerifyExportFiles(job.Assembly);
            if (Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0) throw new IOException("总装有未保存修改，请先保存并重新检查后导出，避免 NWC 与磁盘快照不一致。");
            ReloadLoadedReferences(doc);
            if (Commands.BatchCompletion != null) doc.Editor.Command("_.ZOOM", "_E");
            job.Assembly.RefreshState = "引用已重载";
            var reader = new DwgReader();
            var scan = Scanner.Scan(job.Assembly.Path, Path.GetDirectoryName(job.Assembly.Path), reader);
            Workflow.VerifyExportGraph(job.Assembly, scan);
            job.Assembly.Scan = scan;
            // 所有入口都保留总装本体和完整依赖快照，供跨会话判断是否需要重导。
            foreach (var path in scan.Rows.Select(r => r.LocalPath).Append(job.Assembly.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!job.Assembly.ExpectedHashes.ContainsKey(path))
                {
                    var hash = Files.Hash(path);
                    if (string.IsNullOrEmpty(hash)) throw new IOException("导出源文件不存在：" + path);
                    job.Assembly.ExpectedHashes[path] = hash;
                }
            Workflow.VerifyExportFiles(job.Assembly);
            foreach (var path in job.Assembly.UpdatedPaths)
                if (!scan.Rows.Any(r => string.Equals(r.LocalPath, path, StringComparison.OrdinalIgnoreCase))) throw new IOException("总装 Xref 关系已变化，请重新检测后导出：" + path);
            ExportCurrent(job.Target);
            Workflow.VerifyExportFiles(job.Assembly);
            job.Assembly.ExportHash = Files.Hash(job.Target);
            job.Assembly.ExportPath = job.Target; job.Assembly.ExportState = "已导出";
            // 周更使用其独立账本；交互导出才写入本机交互收据，避免隔离周更污染用户记录。
            if (Commands.BatchCompletion == null) NwcExportHistory.Record(Settings.DataDirectory, job);
        }
    }

    public sealed partial class Commands
    {
        internal static WorkflowSession ResumeWorkflow;
        internal static WorkflowSession BatchSession;
        internal static Queue<BatchExportJob> BatchJobs;
        internal static BatchExportJob BatchCurrent;
        internal static Document BatchOrigin;
        internal static bool BatchStop;
        internal static Action<List<BatchExportJob>> BatchCompletion;
        private static readonly List<BatchExportJob> BatchFinished = new List<BatchExportJob>();

        [CommandMethod("PMSBATCHNEXT", CommandFlags.Session)]
        public void ContinueBatch() { DispatchBatch(); }

        internal static void DispatchBatch()
        {
            if (BatchJobs == null) return;
            // 只关闭本批次自己只读打开的文档，不关闭用户原有文档。
            if (BatchCurrent != null)
            {
                if (BatchCurrent.OpenedByPlugin)
                {
                    try { Host.Find(BatchCurrent.Assembly.Path)?.CloseAndDiscard(); }
                    catch (Exception ex) { BatchCurrent.Assembly.ExportState += "（临时图纸未关闭：" + ex.Message + "）"; }
                }
                BatchFinished.Add(BatchCurrent); BatchCurrent = null;
            }
            while (BatchJobs.Count > 0)
            {
                var job = BatchJobs.Dequeue();
                if (BatchStop) { job.Assembly.ExportState = "已取消"; BatchFinished.Add(job); continue; }
                try
                {
                    var doc = Host.Find(job.Assembly.Path);
                    job.Assembly.ExportHash = null; job.Assembly.ExportPath = job.Target;
                    job.OpenedByPlugin = doc == null;
                    if (doc == null) doc = AcApp.DocumentManager.Open(job.Assembly.Path, true);
                    var command = doc.CommandInProgress;
                    if (!string.IsNullOrWhiteSpace(command) && command != "SYNC" && command != "PMSBATCHNEXT") throw new IOException("总装有命令未结束：" + command);
                    AcApp.DocumentManager.MdiActiveDocument = doc;
                    BatchCurrent = job; job.Assembly.ExportState = "正在导出";
                    doc.SendStringToExecute("PMSBATCHNWC ", true, false, false);
                    return;
                }
                catch (Exception ex)
                {
                    job.Assembly.ExportState = "导出失败：" + ex.Message;
                    if (job.OpenedByPlugin) { try { Host.Find(job.Assembly.Path)?.CloseAndDiscard(); } catch { } }
                    BatchFinished.Add(job);
                }
            }
            var session = BatchSession;
            foreach (var finished in BatchFinished.Where(j => j.ManualSelection)) Workflow.RecordExportResult(session, finished);
            try
            {
                var dir = Path.Combine(Settings.DataDirectory, "Logs"); Directory.CreateDirectory(dir);
                session.LastLog = Path.Combine(dir, "batch-nwc-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                File.WriteAllText(session.LastLog, JsonConvert.SerializeObject(BatchFinished.Select(j => new { assembly = j.Assembly.Path, manualSelection = j.ManualSelection, currentLocal = j.CurrentLocal, mode = j.CurrentLocal ? "当前本地模型；不检查同步，不承诺引用完整性" : "已校验更新快照", updatedXrefs = j.Assembly.UpdatedPaths, expectedHashes = j.Assembly.ExpectedHashes, target = j.Target, result = j.Assembly.ExportState, outputHash = j.Assembly.ExportHash }), Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception ex) { AcApp.ShowAlertDialog("批次已结束，但日志写入失败：" + ex.Message); }
            finally { BatchJobs = null; BatchSession = null; ResumeWorkflow = session; }
            var completion = BatchCompletion; BatchCompletion = null;
            var finishedJobs = BatchFinished.ToList(); BatchFinished.Clear();
            if (completion != null) { completion(finishedJobs); return; }
            try { if (BatchOrigin != null && AcApp.DocumentManager.Cast<Document>().Contains(BatchOrigin)) AcApp.DocumentManager.MdiActiveDocument = BatchOrigin; } catch { }
            var active = AcApp.DocumentManager.MdiActiveDocument;
            if (active != null) active.SendStringToExecute("SYNC ", true, false, false);
        }

        [CommandMethod("PMSBATCHNWC", CommandFlags.Modal)]
        public void ExportBatchItem()
        {
            if (BatchCurrent == null) return;
            try { Host.ExportWorkflowJob(BatchCurrent); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak) { BatchCurrent.Assembly.ExportState = "已取消"; BatchStop = true; }
            catch (Exception ex) { BatchCurrent.Assembly.ExportState = "导出失败：" + ex.Message; }
            finally { AcApp.DocumentManager.MdiActiveDocument.SendStringToExecute("PMSBATCHNEXT ", true, false, false); }
        }
    }
}
