using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    internal static class LocalAssemblyCheck
    {
        private sealed class LocalReader : IReferenceReader
        {
            private readonly string root; private readonly IReferenceReader reader;
            internal LocalReader(string modelRoot, IReferenceReader inner) { root = modelRoot; reader = inner; }
            public List<RefEdge> Read(string path)
            {
                if (!Paths.IsWithin(root, path)) throw new IOException("引用位于本地模型根目录外，本检查不访问：" + path);
                return reader.Read(path);
            }
        }
        internal static List<UpdateReportRow> Check(string root, IReferenceReader reader, CancellationToken ct, Action<string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new IOException("请选择存在的本地模型根目录。");
            var session = new WorkflowSession { LocalModelsRoot = root };
            var localReader = new CachedReader(new LocalReader(root, reader), DwgReader.IsOpen);
            foreach (var path in AssemblyCatalog.Discover(root).Where(a => a.Matches.Count == 1).Select(a => a.Path))
            {
                ct.ThrowIfCancellationRequested(); progress?.Invoke("检查本地 DWG：" + Path.GetFileName(path));
                session.Assemblies.Add(Workflow.ScanAssembly(path, localReader));
            }
            AssemblyCatalog.Evaluate(session);
            return session.AssemblyAudit.Select(a =>
            {
                var assembly = session.Assemblies.FirstOrDefault(x => x.Path == a.Path);
                return new UpdateReportRow { Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), Operation = "检查本地 DWG", File = Path.GetFileName(a.Path), Local = a.Path, Assembly = a.Path, State = a.State == "检查通过，无本次更新" ? "本地引用检查通过" : a.State, Details = "仅检查本地总装与本地 Xref，不比较 Connector 内容，不判断 NWC 是否需要上传。\r\n" + a.Diagnostics + "\r\n引用：\r\n" + string.Join("\r\n", assembly?.Scan.Rows.Select(r => r.LocalPath + "；" + r.State) ?? Enumerable.Empty<string>()) };
            }).ToList();
        }
    }
}
