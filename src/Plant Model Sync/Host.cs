using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.ProcessPower.ProjectManager;
using Autodesk.ProcessPower.PlantInstance;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(IWMF.PlantModelSync.Commands))]
namespace IWMF.PlantModelSync
{
    public sealed class DwgReader : IReferenceReader
    {
        public static bool IsOpen(string path) => Host.Find(path) != null;
        public List<RefEdge> Read(string path)
        {
            var opened = Host.Find(path);
            if (opened != null) using (opened.LockDocument()) return ReadDatabase(opened.Database);
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                db.CloseInput(true);
                return ReadDatabase(db);
            }
        }
        private static List<RefEdge> ReadDatabase(Database db)
        {
            var rows = new List<RefEdge>();
            var direct = DirectIds(db);
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!block.IsFromExternalReference || !direct.Contains(id)) continue;
                    var xdb = block.GetXrefDatabase(false);
                    rows.Add(new RefEdge { Name = block.Name, Saved = block.PathName, Resolved = xdb?.Filename, Overlay = block.IsFromOverlayReference, Unloaded = block.IsUnloaded });
                }
                tr.Commit();
            }
            return rows;
        }
        internal static HashSet<ObjectId> DirectIds(Database db)
        {
            var direct = new HashSet<ObjectId>();
            using (var graph = db.GetHostDwgXrefGraph(true))
            {
                for (int i = 1; i < graph.NumNodes; i++)
                {
                    var node = graph.GetXrefNode(i);
                    if (!node.IsNested && !node.BlockTableRecordId.IsNull) direct.Add(node.BlockTableRecordId);
                }
            }
            return direct;
        }
    }
    internal static partial class Host
    {
        public static Document Find(string file) => AcApp.DocumentManager.Cast<Document>().FirstOrDefault(d => string.Equals(Paths.Canonical(d.Name), Paths.Canonical(file), StringComparison.OrdinalIgnoreCase));
        public static List<ProjectDrawing> CurrentProjectDrawings()
        {
            var result = new List<ProjectDrawing>();
            var current = PlantApplication.CurrentProject;
            if (current != null)
            {
                foreach (string partName in new[] { "Piping", "PnId" })
                {
                    try
                    {
                        var part = current.ProjectParts[partName];
                        if (part == null) continue;
                        foreach (PnPProjectDrawing drawing in part.GetPnPDrawingFiles())
                        {
                            var path = !string.IsNullOrWhiteSpace(drawing.ResolvedFilePath) ? drawing.ResolvedFilePath : drawing.AbsoluteFileName;
                            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                                result.Add(new ProjectDrawing { Part = partName, Name = Path.GetFileName(path), Path = Path.GetFullPath(path) });
                        }
                    }
                    catch { }
                }
            }
            foreach (Document document in AcApp.DocumentManager)
                if (!string.IsNullOrWhiteSpace(document.Name) && File.Exists(document.Name))
                    result.Add(new ProjectDrawing { Part = "当前已打开", Name = Path.GetFileName(document.Name), Path = Path.GetFullPath(document.Name) });
            return result.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.Part).ThenBy(x => x.Name).ToList();
        }
        public static void CheckSourcesClosed(IEnumerable<XrefRow> rows)
        {
            foreach (var row in rows) if (Find(row.LocalPath) != null) throw new IOException("请先保存并关闭源 DWG：" + row.LocalPath);
        }
        public static IDisposable ReleaseReferences(string assembly)
        {
            var doc = Find(assembly);
            return doc == null ? null : new UnloadScope(doc);
        }
        private sealed class UnloadScope : IDisposable
        {
            private readonly Document doc;
            private readonly ObjectIdCollection ids = new ObjectIdCollection();
            public UnloadScope(Document document)
            {
                doc = document;
                using (doc.LockDocument())
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in DwgReader.DirectIds(doc.Database))
                        {
                            var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (!block.IsUnloaded) ids.Add(id);
                        }
                        tr.Commit();
                    }
                    if (ids.Count > 0) doc.Database.UnloadXrefs(ids);
                }
            }
            public void Dispose()
            {
                using (doc.LockDocument()) if (ids.Count > 0) doc.Database.ReloadXrefs(ids);
                doc.Editor.Regen();
            }
        }
        public static void QueueNativeExport(string assembly)
        {
            var doc = Find(assembly) ?? AcApp.DocumentManager.Open(assembly, true);
            AcApp.DocumentManager.MdiActiveDocument = doc;
            // 在插件窗口关闭后执行原生导出；原生命令负责文件选择和覆盖确认。
            doc.SendStringToExecute("_.NWCOUT ", true, false, false);
        }
        public static void QueueExport(string assembly, string destination)
        {
            var doc = Find(assembly) ?? AcApp.DocumentManager.Open(assembly, true);
            AcApp.DocumentManager.MdiActiveDocument = doc;
            if (Commands.Exports.ContainsKey(doc.Name)) throw new InvalidOperationException("此总装已有导出任务。");
            Commands.Exports.Add(doc.Name, destination);
            doc.SendStringToExecute("PMSNWCEXEC ", true, false, false);
        }
        public static void ExportCurrent(string target, bool requireResolvedReferences = true)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("没有活动总装。");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            // 周更先在独立临时目录导出，再校验复制到日期目录，避开导出器对目标路径的限制。
            string temp = Commands.BatchCompletion != null ? Path.Combine(Path.GetTempPath(), "IWMF-PMS-" + Guid.NewGuid().ToString("N") + ".nwc") : Path.Combine(Path.GetDirectoryName(target), ".pms-" + Guid.NewGuid().ToString("N") + ".nwc");
            string install = null;
            object filedia = AcApp.GetSystemVariable("FILEDIA");
            try
            {
                var missing = new List<string>();
                if (requireResolvedReferences)
                using (var graph = doc.Database.GetHostDwgXrefGraph(true))
                using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var visited = new HashSet<ObjectId>();
                    void Visit(GraphNode parent, int depth)
                    {
                        for (int i = 0; i < parent.NumOut; i++)
                        {
                            var node = (XrefGraphNode)parent.Out(i);
                            if (node.BlockTableRecordId.IsNull) continue;
                            var block = (BlockTableRecord)tr.GetObject(node.BlockTableRecordId, OpenMode.ForRead);
                            if (block.IsUnloaded || (depth > 0 && block.IsFromOverlayReference)) continue;
                            if (!visited.Add(node.BlockTableRecordId)) continue;
                            if (block.XrefStatus != XrefStatus.Resolved) missing.Add(block.Name);
                            else Visit(node, depth + 1);
                        }
                    }
                    Visit(graph.RootNode, 0);
                    tr.Commit();
                }
                if (missing.Count > 0) throw new IOException("加载的引用未解析：" + string.Join(", ", missing));
                AcApp.SetSystemVariable("FILEDIA", 0);
                doc.Editor.Command("_.NWCOUT", temp);
                if (!File.Exists(temp) || new FileInfo(temp).Length < 32) throw new IOException("导出器未生成有效文件。请确认已安装并加载 Navisworks Exporter 2024。");
                install = Path.Combine(Path.GetDirectoryName(target), ".pms-install-" + Guid.NewGuid().ToString("N") + ".nwc");
                File.Copy(temp, install, false);
                if (Files.Hash(install) != Files.Hash(temp)) throw new IOException("NWC 复制到输出目录后的哈希校验失败。");
                if (File.Exists(target)) File.Replace(install, target, target + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak");
                else File.Move(install, target);
                install = null;
                doc.Editor.WriteMessage("\nNWC 文件已生成：" + target + "\n");
            }
            finally
            {
                AcApp.SetSystemVariable("FILEDIA", filedia);
                if (File.Exists(temp)) File.Delete(temp);
                if (install != null && File.Exists(install)) File.Delete(install);
            }
        }
    }
    internal sealed class ProjectDrawing
    {
        public string Part { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }
    public sealed partial class Commands
    {
        internal static readonly Dictionary<string, string> Exports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        [CommandMethod("SYNC", CommandFlags.Session)]
        public void Show()
        {
            try
            {
                if (BatchJobs != null) { AcApp.ShowAlertDialog("总装 NWC 批次仍在执行，请等待批次结束；取消原生导出时按 Esc。"); return; }
                var resume = ResumeWorkflow; ResumeWorkflow = null;
                using (var form = new WorkflowForm(resume, assemblyFirst: true))
                {
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
                    ResumeWorkflow = form.InputMatchesSession ? form.Session : null;
                    if (form.ExportRequests?.Count > 0) Host.QueueWorkflowExports(form.Session, form.ExportRequests);
                }
            }
            catch (Exception ex) { AcApp.ShowAlertDialog("Plant Model Sync\n" + ex.Message); }
        }
        [CommandMethod("PMSNWCEXEC", CommandFlags.Modal)]
        public void Export()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (!Exports.TryGetValue(doc.Name, out var target)) return;
            Exports.Remove(doc.Name);
            try { Host.ExportCurrent(target); }
            catch (Exception ex) { AcApp.ShowAlertDialog("NWC 导出失败\n" + ex.Message); }
        }
    }
}
