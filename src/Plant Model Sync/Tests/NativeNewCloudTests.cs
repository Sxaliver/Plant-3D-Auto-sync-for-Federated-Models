using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(IWMF.PlantModelSync.Commands))]
namespace IWMF.PlantModelSync
{
    // Core Console 只验证真实旁路数据库；GUI 文档打开状态由完整 Plant 宿主检查。
    internal static partial class Host { internal static object Find(string path) => null; }
    internal sealed class DwgReader : IReferenceReader
    {
        public List<RefEdge> Read(string path)
        {
            var result = new List<RefEdge>();
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null); db.CloseInput(true);
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (block.IsFromExternalReference) result.Add(new RefEdge { Name = block.Name, Saved = block.PathName, Resolved = Path.IsPathRooted(block.PathName) ? block.PathName : Path.Combine(Path.GetDirectoryName(path), block.PathName) });
                    }
                    tr.Commit();
                }
            }
            return result;
        }
    }
}
