using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;

namespace IWMF.PlantModelSync
{
    internal sealed class ReferenceNameChange
    {
        public string OldName { get; set; }
        public string NewName { get; set; }
        public string Source { get; set; }
    }
    internal static partial class Host
    {
        // 仅规范可校验为本插件旧命名的直接引用；用原定义改名，保留实例和原路径。
        internal static UpdateReportRow NormalizeReferenceNames(string assembly, string localRoot, CancellationToken ct)
        {
            CheckAssemblyClosedForAttach(assembly); Files.AssertClosed(assembly);
            if (!Paths.IsWithin(localRoot, assembly) || Paths.IsHistory(assembly)) throw new IOException("只能规范正式本地总装的引用名称。");
            var beforeHash = Files.Hash(assembly); string temporary = null;
            var changes = new List<ReferenceNameChange>(); var previous = HostApplicationServices.WorkingDatabase;
            var report = new UpdateReportRow { Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), Operation = "规范 Xref 名称", File = Path.GetFileName(assembly), Local = assembly, Assembly = assembly, BeforeHash = beforeHash, State = "无须改名" };
            try
            {
                ct.ThrowIfCancellationRequested();
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(assembly, FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true); HostApplicationServices.WorkingDatabase = db;
                    var originals = ExistingReferenceState(db);
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var occupied = new HashSet<string>(table.Cast<ObjectId>().Select(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name), StringComparer.OrdinalIgnoreCase);
                        foreach (var id in DwgReader.DirectIds(db))
                        {
                            var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            var source = Paths.Canonical(Paths.Resolve(assembly, block.PathName));
                            if (!Paths.IsWithin(localRoot, source) || Paths.IsHistory(source) || !File.Exists(source) || !ReferenceNamePolicy.IsOwned(block.Name, source)) continue;
                            var oldName = block.Name; occupied.Remove(oldName);
                            var stem = SymbolUtilityServices.RepairSymbolName(Path.GetFileNameWithoutExtension(source), false);
                            var name = ReferenceNamePolicy.Choose(source, stem, occupied); occupied.Add(name);
                            block.UpgradeOpen(); block.Name = name;
                            changes.Add(new ReferenceNameChange { OldName = oldName, NewName = name, Source = source });
                        }
                        tr.Commit();
                    }
                    if (changes.Count == 0) return report;
                    temporary = Path.Combine(Path.GetDirectoryName(assembly), ".pms-names-" + Guid.NewGuid().ToString("N") + ".dwg");
                    db.SaveAs(temporary, db.OriginalFileVersion);
                    using (var saved = new Database(false, true))
                    {
                        saved.ReadDwgFile(temporary, FileOpenMode.OpenForReadAndAllShare, false, null); saved.CloseInput(true);
                        var after = ExistingReferenceState(saved);
                        if (after.Count != originals.Count) throw new IOException("改名后直接引用数量变化，停止替换。");
                        foreach (var pair in originals)
                        {
                            var name = changes.SingleOrDefault(c => c.OldName == pair.Key)?.NewName ?? pair.Key;
                            if (!after.TryGetValue(name, out var state) || state.Item1 != pair.Value.Item1 || state.Item2 != pair.Value.Item2) throw new IOException("改名后引用路径、状态或插入变换变化，停止替换：" + pair.Key);
                        }
                    }
                    HostApplicationServices.WorkingDatabase = previous;
                }
                ct.ThrowIfCancellationRequested(); CheckAssemblyClosedForAttach(assembly);
                report.Backup = Files.InstallDownload(temporary, assembly, beforeHash); temporary = null;
                report.AfterHash = Files.Hash(assembly); report.State = "已备份并规范引用名称";
                report.Details = string.Join("\r\n", changes.Select(c => c.OldName + " → " + c.NewName + "；文件=" + c.Source));
                return report;
            }
            finally { HostApplicationServices.WorkingDatabase = previous; if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
