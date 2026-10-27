using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IWMF.PlantModelSync
{
    internal sealed class AssemblyAuditRow
    {
        public string Key { get; set; }
        public string Path { get; set; }
        public string State { get; set; }
        public string Updated { get; set; }
        public string Pending { get; set; }
        public string Diagnostics { get; set; }
        public List<string> Matches { get; set; } = new List<string>();
    }
    // 按编号而非截断显示名核对固定清单；缺失和重复均必须保留在报告中。
    internal static class AssemblyCatalog
    {
        internal static List<string> Keys => Enumerable.Range(9422, 8).Select(n => "114101-SIF-M3-M-" + n)
            .Concat(new[] { "114101-SIF-M3-M-9431" }).Concat(Enumerable.Range(9422, 8).Select(n => "114101-SIF-M3-MLP-" + n)).ToList();
        public static List<AssemblyAuditRow> Discover(string modelRoot)
        {
            if (string.IsNullOrWhiteSpace(modelRoot)) return new List<AssemblyAuditRow>();
            var directory = System.IO.Path.Combine(modelRoot, "单系统总装文件");
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.dwg", SearchOption.TopDirectoryOnly).Select(Paths.Canonical).ToList() : new List<string>();
            return Keys.Select(key =>
            {
                var matches = files.Where(p => Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(p), "^" + Regex.Escape(key) + "(?:_|$)", RegexOptions.IgnoreCase)).ToList();
                return new AssemblyAuditRow { Key = key, Matches = matches, Path = matches.Count == 1 ? matches[0] : System.IO.Path.Combine(directory, key + ".dwg"), State = matches.Count == 0 ? "文件不存在" : matches.Count > 1 ? "编号重复，需确认" : "待检查", Diagnostics = matches.Count > 1 ? string.Join(" | ", matches) : "" };
            }).ToList();
        }
        public static void Evaluate(WorkflowSession session)
        {
            session.AssemblyAudit = Discover(session.LocalModelsRoot);
            foreach (var row in session.AssemblyAudit.Where(r => r.Matches.Count == 1))
            {
                var assembly = session.Assemblies.FirstOrDefault(a => string.Equals(a.Path, row.Path, StringComparison.OrdinalIgnoreCase));
                if (assembly == null) { row.State = "未检查"; continue; }
                row.Updated = assembly.UpdatedXrefs; row.Pending = assembly.PendingXrefs;
                row.Diagnostics = string.Join(" | ", assembly.Scan.Warnings);
                row.State = !File.Exists(row.Path) ? "文件不存在" : assembly.Scan.Warnings.Count > 0 ? "引用诊断待处理" : assembly.RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal) ? "引用重载失败" : !string.IsNullOrEmpty(assembly.PendingXrefs) ? "仍有待更新引用" : assembly.UpdatedPaths.Count > 0 ? "已更新，检查通过" : "检查通过，无本次更新";
            }
        }
    }
}
