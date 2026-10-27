using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace IWMF.PlantModelSync
{
    internal static partial class Host
    {
        internal static IWeeklyDwg LocalRepairDwg() => new WeeklyNativeDwg();
        public static void CheckAssemblyClosedForAttach(string path)
        {
            if (Find(path) != null) throw new IOException("新增 Xref 需要保存并关闭目标总装（无需退出 Plant 3D）：" + path);
        }

        // 只编辑旁路数据库并保存到同目录暂存图，原总装由上层校验、备份后原子替换。
        public static AttachmentResult StageNewReferences(string assembly, List<ReferencePlacement> placements)
            => StageReferencesCore(assembly, placements, false);

        // 只有交互式本地引用在明确确认后允许保留卸载分支；周更/新增云端源仍使用严格入口。
        public static AttachmentResult StageConfirmedLocalReferences(string assembly, List<ReferencePlacement> placements, bool unloadedWarningsConfirmed)
            => StageReferencesCore(assembly, placements, unloadedWarningsConfirmed);

        private static AttachmentResult StageReferencesCore(string assembly, List<ReferencePlacement> placements, bool deferUnloaded)
        {
            var sources = placements.Select(p => p.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CheckAssemblyClosedForAttach(assembly); Files.AssertClosed(assembly);
            foreach (var source in sources)
            {
                var scan = deferUnloaded
                    ? Scanner.ScanForAttachment(source, Path.GetDirectoryName(source), new DwgReader())
                    : Scanner.Scan(source, Path.GetDirectoryName(source), new DwgReader());
                if (scan.Warnings.Count > 0) throw new IOException("新源图纸的嵌套引用不完整，请先补齐：" + source + "；" + string.Join(" | ", scan.Warnings));
                if (string.Equals(source, assembly, StringComparison.OrdinalIgnoreCase) || scan.Rows.Any(r => string.Equals(r.LocalPath, assembly, StringComparison.OrdinalIgnoreCase))) throw new IOException("新增引用将形成循环：" + source);
            }
            var result = new AttachmentResult { Assembly = assembly, Sources = sources.ToList() };
            var staged = Path.Combine(Path.GetDirectoryName(assembly), ".pms-attach-" + Guid.NewGuid().ToString("N") + ".dwg");
            var previous = HostApplicationServices.WorkingDatabase;
            try
            {
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(assembly, FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true);
                    HostApplicationServices.WorkingDatabase = db;
                    var originals = ExistingReferenceState(db);
                    foreach (var placement in placements)
                    {
                        var source = placement.Source;
                        // 比例按用户确认值应用；单位不一致时不静默换算或放大模型。
                        using (var sourceDb = new Database(false, true))
                        {
                            sourceDb.ReadDwgFile(source, FileOpenMode.OpenForReadAndAllShare, false, null); sourceDb.CloseInput(true);
                            if (db.Insunits != sourceDb.Insunits) throw new IOException("源图与总装 INSUNITS 不一致，请先统一单位：" + source);
                        }
                        bool alreadyPresent = false;
                        string name;
                        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                        {
                            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            var direct = DwgReader.DirectIds(db);
                            var existing = table.Cast<ObjectId>().Where(direct.Contains).Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).FirstOrDefault(b => b.IsFromExternalReference && SameReference(assembly, b.PathName, source));
                            if (existing != null)
                            {
                                var refs = existing.GetBlockReferenceIds(true, false).Cast<ObjectId>().Select(id => (BlockReference)tr.GetObject(id, OpenMode.ForRead)).Where(r => r.OwnerId == SymbolUtilityServices.GetBlockModelSpaceId(db)).ToList();
                                if (existing.IsUnloaded || !refs.Any(r => SamePlacement(r, placement))) throw new IOException("已有同路径引用未加载，或插入点/比例/旋转不同；请核对，未自动移动或重复插入：" + source);
                                alreadyPresent = true;
                            }
                            // 原生 Xref 面板直接显示块名，优先保留文件名，仅重名时增加稳定后缀。
                            var stem = SymbolUtilityServices.RepairSymbolName(Path.GetFileNameWithoutExtension(source), false);
                            name = ReferenceNamePolicy.Choose(source, stem, table.Cast<ObjectId>().Select(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name));
                            tr.Commit();
                        }
                        if (alreadyPresent) continue;
                        // AttachXref 会写块表，不能在仍持有块表只读打开时调用。
                        var id = db.AttachXref(source, name);
                        if (id.IsNull) throw new IOException("AttachXref 未创建引用：" + source);
                        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                        {
                            var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                            block.PathName = RelativeReference(assembly, source);
                            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                            HostApplicationServices.WorkingDatabase = db;
                            try
                            {
                                using (var reference = new BlockReference(new Point3d(placement.X, placement.Y, placement.Z), id))
                                {
                                    reference.SetDatabaseDefaults(db); reference.Layer = "0"; reference.ScaleFactors = new Scale3d(placement.Scale); reference.Rotation = placement.Rotation * Math.PI / 180;
                                    ms.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true);
                                }
                            }
                            finally { HostApplicationServices.WorkingDatabase = db; }
                            tr.Commit(); result.Added++;
                        }
                    }
                    if (result.Added == 0) return result;
                    PreserveExistingReferences(db, originals);
                    db.SaveAs(staged, db.OriginalFileVersion);
                    using (var saved = new Database(false, true))
                    {
                        saved.ReadDwgFile(staged, FileOpenMode.OpenForReadAndAllShare, false, null); saved.CloseInput(true);
                        var after = ExistingReferenceState(saved);
                        if (originals.Any(p => !after.TryGetValue(p.Key, out var state) || state.Item1 != p.Value.Item1 || state.Item2 != p.Value.Item2)) throw new IOException("新增引用改变了原有 Xref 的路径、类型、加载状态或位置，已停止替换总装。");
                    }
                    HostApplicationServices.WorkingDatabase = previous;
                }
                // 暂存文件位于同目录，重新打开后相对路径仍须解析到同一组源文件。
                using (var verify = new Database(false, true))
                {
                    verify.ReadDwgFile(staged, FileOpenMode.OpenForReadAndAllShare, false, null); verify.CloseInput(true);
                    using (var tr = verify.TransactionManager.StartOpenCloseTransaction())
                    {
                        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(verify), OpenMode.ForRead);
                        foreach (var placement in placements)
                        {
                            var source = placement.Source;
                            bool found = false;
                            foreach (ObjectId id in ms)
                            {
                                if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference reference)) continue;
                                var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                                if (definition.IsFromExternalReference && !definition.IsUnloaded && SameReference(assembly, definition.PathName, source) && SamePlacement(reference, placement)) { found = true; break; }
                            }
                            if (!found) throw new IOException("暂存总装未通过新增引用校验：" + source);
                        }
                        tr.Commit();
                    }
                }
                result.Staged = staged; return result;
            }
            catch { if (File.Exists(staged)) File.Delete(staged); throw; }
            finally { HostApplicationServices.WorkingDatabase = previous; }
        }
        private static Dictionary<string, Tuple<string, string>> ExistingReferenceState(Database db)
        {
            var result = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in DwgReader.DirectIds(db))
                {
                    var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    var instances = block.GetBlockReferenceIds(true, false).Cast<ObjectId>().Where(rid => rid.Database == db).Distinct().Select(rid => (BlockReference)tr.GetObject(rid, OpenMode.ForRead)).Where(r => { var owner = (BlockTableRecord)tr.GetObject(r.OwnerId, OpenMode.ForRead); return !owner.IsFromExternalReference && !owner.IsDependent; }).Select(r => r.Handle + "|" + r.BlockTransform + "|" + r.Normal + "|" + r.Layer).OrderBy(x => x, StringComparer.Ordinal);
                    result[block.Name] = Tuple.Create(block.PathName, block.IsUnloaded + "|" + block.IsFromOverlayReference + "|" + string.Join("\n", instances));
                }
                tr.Commit();
            }
            return result;
        }
        private static void PreserveExistingReferences(Database db, Dictionary<string, Tuple<string, string>> originals)
        {
            var current = ExistingReferenceState(db);
            if (originals.Any(p => !current.TryGetValue(p.Key, out var state) || state.Item2 != p.Value.Item2)) throw new IOException("挂接过程改变了原有引用的位置或加载状态，已停止保存。");
            // AttachXref 可能重用相同源的嵌套定义并改写路径；仅还原原有直接定义的保存路径。
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (var pair in originals)
                    if (current[pair.Key].Item1 != pair.Value.Item1) ((BlockTableRecord)tr.GetObject(table[pair.Key], OpenMode.ForWrite)).PathName = pair.Value.Item1;
                tr.Commit();
            }
        }
        private static bool SameReference(string assembly, string saved, string source)
        {
            if (string.IsNullOrWhiteSpace(saved)) return false;
            return string.Equals(Paths.Canonical(Path.IsPathRooted(saved) ? saved : Path.Combine(Path.GetDirectoryName(assembly), saved)), Paths.Canonical(source), StringComparison.OrdinalIgnoreCase);
        }
        private static bool SamePlacement(BlockReference reference, ReferencePlacement placement)
        {
            var angle = reference.Rotation - placement.Rotation * Math.PI / 180;
            return reference.Position.DistanceTo(new Point3d(placement.X, placement.Y, placement.Z)) < 1e-6 &&
                Math.Abs(reference.ScaleFactors.X - placement.Scale) < 1e-9 && Math.Abs(reference.ScaleFactors.Y - placement.Scale) < 1e-9 && Math.Abs(reference.ScaleFactors.Z - placement.Scale) < 1e-9 && Math.Abs(Math.IEEERemainder(angle, 2 * Math.PI)) < 1e-9 && reference.Normal.IsEqualTo(Vector3d.ZAxis);
        }
        private static string RelativeReference(string assembly, string source)
        {
            var folder = new Uri(Path.GetDirectoryName(assembly) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(folder.MakeRelativeUri(new Uri(source)).ToString()).Replace('/', '\\');
        }
    }
}
