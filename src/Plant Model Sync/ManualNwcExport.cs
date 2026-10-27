using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    // 手动导出只选择本地总装，不读取 Connector 或依赖图，不制造同步成功记录。
    internal static class ManualNwcExport
    {
        internal static string DateFolder(string output, DateTimeOffset? now = null)
        {
            var root = Path.GetFullPath(output);
            var date = (now ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            // 已选择当天日期目录时复用，避免重复生成日期层级；配置仍保存用户选择的根目录。
            return string.Equals(new DirectoryInfo(root).Name, date, StringComparison.Ordinal) ? root : Path.Combine(root, date);
        }
        public static List<BatchExportJob> Plan(IEnumerable<string> files, string output, IReferenceReader reader, WorkflowSession known = null, CancellationToken ct = default, Action<string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new IOException("请选择本地 NWC 输出目录。");
            var datedOutput = DateFolder(output);
            var paths = files.Select(Paths.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) throw new IOException("请选择至少一份总装 DWG。");
            var assemblies = new List<AssemblyUpdate>();
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested(); progress?.Invoke("准备导出：" + Path.GetFileName(path));
                if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new IOException("请选择存在的 DWG：" + path);
                Files.CheckDwg(path);
                // 参数 reader / known 为旧调用保留；当前导出不使用旧扫描、待 Pull 或云端状态。
                assemblies.Add(new AssemblyUpdate { Path = path, Name = Path.GetFileName(path), Selected = true, Scan = new ScanResult(), RefreshState = "当前本地模型" });
            }
            var duplicateNames = new HashSet<string>(assemblies.GroupBy(a => Path.GetFileNameWithoutExtension(a.Path), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            return assemblies.Select(a =>
            {
                var name = Path.GetFileNameWithoutExtension(a.Path);
                if (duplicateNames.Contains(name)) using (var sha = System.Security.Cryptography.SHA256.Create()) name += "__" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(a.Path.ToUpperInvariant()))).Replace("-", "").Substring(0, 8);
                return new BatchExportJob { Assembly = a, Target = Path.Combine(datedOutput, name + ".nwc"), ManualSelection = true, CurrentLocal = true };
            }).ToList();
        }
    }
}
