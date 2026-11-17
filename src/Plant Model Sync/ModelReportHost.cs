using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsSystem;
using Autodesk.AutoCAD.GraphicsInterface;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

namespace IWMF.PlantModelSync
{
    internal static partial class Host
    {
        private static ModelEvidenceStore Evidence(string storage, string output = null) => new ModelEvidenceStore(storage, CaptureReportImage,
            model => Scanner.ScanForSynchronization(model, Path.GetDirectoryName(model), new DwgReader()).Rows.Select(r => r.LocalPath), output);
        internal static IDisposable BeginModelReportUpdate(WorkflowSession session, IEnumerable<string> changing, string storage)
        {
            try { return new SafeReportScope(Evidence(storage, session.OutputFolder).Begin(session.Assemblies, changing), session); }
            catch (Exception ex) { session.Warnings.Add("更新前截图 / 证据未完成：" + ex.Message); return new SafeReportScope(null, session); }
        }
        private sealed class SafeReportScope : IDisposable
        {
            private readonly IDisposable inner; private readonly WorkflowSession session;
            internal SafeReportScope(IDisposable scope, WorkflowSession value) { inner = scope; session = value; }
            public void Dispose() { try { inner?.Dispose(); } catch (Exception ex) { session.Warnings.Add("更新后证据记录失败：" + ex.Message); } }
        }
        private static ReportCamera FitReportCamera(BlockTableRecord space, Transaction tr)
        {
            Extents3d? geometry = null, annotations = null;
            var stack = new HashSet<ObjectId>();
            void Add(Extents3d ext, bool text)
            {
                if (text) { if (annotations.HasValue) { var value = annotations.Value; value.AddExtents(ext); annotations = value; } else annotations = ext; }
                else { if (geometry.HasValue) { var value = geometry.Value; value.AddExtents(ext); geometry = value; } else geometry = ext; }
            }
            void Visit(BlockTableRecord block, Matrix3d transform, int depth)
            {
                if (depth > 32 || !stack.Add(block.ObjectId)) return;
                try
                {
                    foreach (ObjectId id in block)
                    {
                        try
                        {
                            var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (entity == null || !entity.Visible) continue;
                            var layer = tr.GetObject(entity.LayerId, OpenMode.ForRead) as LayerTableRecord;
                            if (layer != null && (layer.IsOff || layer.IsFrozen)) continue;
                            if (entity is BlockReference reference)
                            {
                                var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                                if (definition.IsUnloaded || (depth > 0 && definition.IsFromOverlayReference)) continue;
                                Visit(definition, transform * reference.BlockTransform, depth + 1);
                            }
                            else
                            {
                                var ext = entity.GeometricExtents; ext.TransformBy(transform);
                                Add(ext, entity is DBText || entity is MText || entity is Dimension);
                            }
                        }
                        catch { /* 部分代理不提供包围盒，继续读取其他真实图元。 */ }
                    }
                }
                finally { stack.Remove(block.ObjectId); }
            }
            // 展开块及 Xref 后取景，避免总装中的标题文字成为唯一可用的包围盒。
            Visit(space, Matrix3d.Identity, 0);
            var bounds = geometry ?? annotations;
            if (!bounds.HasValue) throw new IOException("模型没有可用于取景的可见几何。");
            var min = bounds.Value.MinPoint; var max = bounds.Value.MaxPoint;
            var span = Math.Max(1, Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)));
            if (double.IsNaN(span) || double.IsInfinity(span)) throw new IOException("模型几何范围无效。");
            return new ReportCamera { Target = new[] { (min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2 }, Width = span * 2.6, Height = span * 2.6 * 900 / 1600 };
        }
        // 使用原生离屏设备绘制真实模型，不截取桌面，也不改变用户窗口、视角或加载状态。
        internal static ReportImage CaptureReportImage(string model, string target, ReportCamera requested)
        {
            var origin = HostApplicationServices.WorkingDatabase; Database side = null; DocumentLock docLock = null;
            try
            {
                var doc = Find(model); var db = doc?.Database;
                if (db == null)
                {
                    side = new Database(false, true); side.ReadDwgFile(model, FileOpenMode.OpenForReadAndAllShare, true, null); side.CloseInput(true);
                    side.ResolveXrefs(true, false); db = side;
                }
                else docLock = doc.LockDocument();
                HostApplicationServices.WorkingDatabase = db;
                var manager = AcApp.DocumentManager.MdiActiveDocument?.GraphicsManager;
                if (manager == null) throw new IOException("当前宿主不提供图形设备，未生成假截图。");
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var fitted = FitReportCamera(space, tr);
                    var reuse = requested != null && ReportCamera.CanFrameCenter(requested, fitted);
                    var camera = reuse ? requested : fitted;
                    var diagnostic = requested != null && !reuse ? "原取景未覆盖当前模型，已按真实 Xref 几何重新取景；前后范围不同，不冒充同范围对比。" : null;
                    var descriptor = new KernelDescriptor(); descriptor.addRequirement(Autodesk.AutoCAD.UniqueString.Intern("3D Drawing"));
                    var kernel = Manager.AcquireGraphicsKernel(descriptor);
                    try
                    {
                    using (var view = new Autodesk.AutoCAD.GraphicsSystem.View())
                    using (var device = manager.CreateAutoCADOffScreenDevice(kernel))
                    using (var gsModel = manager.CreateAutoCADModel(kernel))
                    using (var style = new VisualStyle(VisualStyleType.FlatWithEdges))
                    {
                        if (device == null || gsModel == null) throw new IOException("宿主不支持原生离屏截图。");
                        device.OnSize(new Size(camera.PixelWidth, camera.PixelHeight)); device.DeviceRenderType = RendererType.Default; device.BackgroundColor = Color.White;
                        // 离屏设备不会自动随白底切换 ACI 7；否则白色管道会绘成白底上的白色。
                        var palette = Enumerable.Range(0, 256).Select(i => i == 0 ? Color.White : Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)i).ColorValue).ToArray();
                        palette[7] = Color.Black; device.SetLogicalPalette(palette);
                        view.VisualStyle = style;
                        var center = new Point3d(camera.Target[0], camera.Target[1], camera.Target[2]);
                        view.SetView(center + new Vector3d(1, -1, 1).GetNormal() * camera.Width * 3, center, Vector3d.ZAxis, camera.Width, camera.Height);
                        try
                        {
                        device.Add(view); view.Add(space, gsModel); device.Update(); view.Update();
                        using (var bitmap = device.GetSnapshot(new Rectangle(0, 0, camera.PixelWidth, camera.PixelHeight)))
                        {
                            if (bitmap == null) throw new IOException("原生截图返回空图像。");
                            if (!ReportImageQuality.IsUseful(bitmap)) throw new IOException("截图为空白或对比度不足，未作为有效模型截图使用。");
                            Directory.CreateDirectory(Path.GetDirectoryName(target)); bitmap.Save(target, ImageFormat.Png);
                        }
                        }
                        finally { view.EraseAll(); device.EraseAll(); }
                    }
                    }
                    finally { if (kernel != null) Manager.ReleaseGraphicsKernel(kernel); }
                    tr.Commit(); return new ReportImage { Path = target, CapturedUtc = DateTime.UtcNow, Camera = camera, UnsavedMemory = doc != null, Method = "原生离屏 / 白底 ACI 调色板", Diagnostic = diagnostic };
                }
            }
            finally { HostApplicationServices.WorkingDatabase = origin; docLock?.Dispose(); side?.Dispose(); }
        }
        internal static void CaptureExportReport(BatchExportJob job)
        {
            try { job.ReportPage = Evidence(job.ReportStorage ?? Settings.DataDirectory).AfterExport(job.Assembly.Path, job.Target); }
            catch (Exception ex) { job.ReportError = "导出报告取证失败：" + ex.Message; }
        }
        internal static List<ModelReportResult> FinishModelReports(IEnumerable<BatchExportJob> jobs)
        {
            var reports = new List<ModelReportResult>();
            foreach (var group in jobs.Where(j => j.Assembly.ExportHash != null && Files.Hash(j.Target) == j.Assembly.ExportHash).GroupBy(j => Path.GetDirectoryName(j.Target), StringComparer.OrdinalIgnoreCase))
            {
                var pages = group.Select(j => j.ReportPage ?? new ModelReportPage { Assembly = j.Assembly.Path, Nwc = j.Target, NwcHash = j.Assembly.ExportHash, Notes = { j.ReportError ?? "未捕获模型截图，不伪造更新前状态。" } }).ToList();
                var report = ModelUpdateReport.Generate(pages, group.Key, ModelReportSettings.Load(group.First().ReportStorage ?? Settings.DataDirectory)); reports.Add(report);
                foreach (var job in group)
                {
                    job.ReportPdf = report.Pdf;
                    job.ReportError = report.Error ?? (job.ReportPage?.Before?.Error != null || job.ReportPage?.After?.Error != null ? "PDF 已生成，但截图取证未完整：" + (job.ReportPage?.Before?.Error ?? job.ReportPage?.After?.Error) : null);
                }
            }
            return reports;
        }
    }
}
