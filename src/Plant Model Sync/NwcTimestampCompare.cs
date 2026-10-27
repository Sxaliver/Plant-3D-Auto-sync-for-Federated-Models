using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    internal sealed class NwcTimestampRow
    {
        public bool Selected { get; set; }
        public string Name { get; set; }
        public string Local { get; set; }
        public string Connector { get; set; }
        public DateTime? LocalUtc { get; set; }
        public DateTime? ConnectorUtc { get; set; }
        public string LocalDate => Format(LocalUtc);
        public string ConnectorDate => Format(ConnectorUtc);
        public string State { get; set; }
        public string Details { get; set; }
        public bool RecommendUpload { get; set; }
        private static string Format(DateTime? utc) => utc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss.fff zzz") : "—";
    }
    // 日期检查只读取目录项和修改时间，不打开 NWC 内容、不加载 DWG、不依赖导出收据。
    internal static class NwcTimestampCompare
    {
        private sealed class Entry
        {
            internal string Path, Key;
            internal DateTime Modified;
        }
        private static List<Entry> Index(string root, CancellationToken ct)
        {
            var files = new List<Entry>(); var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested(); var directory = pending.Pop();
                foreach (var child in Directory.GetDirectories(directory))
                {
                    if (TargetFinder.CanTraverse(child)) pending.Push(child);
                }
                foreach (var path in Directory.GetFiles(directory, "*.nwc"))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Paths.Relative(root, path); var parts = relative.Split('/');
                    // 根目录下 YYYY-MM-DD 历史批次同名输出只取修改时间最新的一份，并明确记录历史候选。
                    if (parts.Length > 1 && DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) relative = string.Join("/", parts.Skip(1));
                    var info = new FileInfo(path);
                    if (!info.Exists) throw new IOException("NWC 在扫描期间已消失，请重新比较：" + path);
                    files.Add(new Entry { Path = path, Key = relative, Modified = info.LastWriteTimeUtc });
                }
            }
            return files;
        }
        internal static List<NwcTimestampRow> Check(string local, string connector, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(local) || !Directory.Exists(local)) throw new IOException("请选择存在的本地 NWC 目录。");
            if (string.IsNullOrWhiteSpace(connector) || !Directory.Exists(connector)) throw new IOException("请选择存在的 Connector NWC 目录；此目录独立于 DWG 云端源目录。");
            local = System.IO.Path.GetFullPath(local).TrimEnd('\\'); connector = System.IO.Path.GetFullPath(connector).TrimEnd('\\');
            if (string.Equals(local, connector, StringComparison.OrdinalIgnoreCase) || Paths.IsWithin(local, connector) || Paths.IsWithin(connector, local)) throw new IOException("本地与 Connector NWC 比较目录不能相同或互相包含。");
            var left = Index(local, ct).GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Modified).ThenByDescending(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var right = Index(connector, ct).GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Modified).ThenByDescending(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var localNames = left.Keys.GroupBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var cloudNames = right.Keys.GroupBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<NwcTimestampRow>();
            foreach (var pair in left.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested(); var name = System.IO.Path.GetFileName(pair.Key); string cloudKey = null;
                if (right.ContainsKey(pair.Key)) cloudKey = pair.Key;
                else if (cloudNames.TryGetValue(name, out var candidates) && candidates.Count == 1 && localNames[name].Count == 1 && !used.Contains(candidates[0])) cloudKey = candidates[0];
                var ambiguous = cloudKey == null && cloudNames.ContainsKey(name);
                var row = Make(pair.Value, cloudKey == null ? null : right[cloudKey]);
                if (ambiguous) { row.State = "同名对应有歧义，需确认"; row.RecommendUpload = false; row.Details += "\r\n云端候选：" + string.Join(" | ", cloudNames[name].SelectMany(k => right[k]).Select(e => e.Path)); }
                result.Add(row); if (cloudKey != null) used.Add(cloudKey);
            }
            foreach (var pair in right.Where(p => !used.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var row = Make(null, pair.Value); var name = System.IO.Path.GetFileName(pair.Key);
                if (localNames.TryGetValue(name, out var sameName)) { row.State = "同名对应有歧义，需确认"; row.Details += "\r\n本地候选：" + string.Join(" | ", sameName.SelectMany(k => left[k]).Select(e => e.Path)); }
                result.Add(row);
            }
            return result;
        }
        private static NwcTimestampRow Make(List<Entry> local, List<Entry> connector)
        {
            var l = local?.FirstOrDefault(); var c = connector?.FirstOrDefault();
            var row = new NwcTimestampRow { Name = System.IO.Path.GetFileName(l?.Path ?? c.Path), Local = l?.Path, Connector = c?.Path, LocalUtc = l?.Modified, ConnectorUtc = c?.Modified };
            row.State = l == null ? "本地缺失，需导出或获取" : c == null ? "云端缺失，建议上传" : l.Modified > c.Modified ? "本地较新，建议上传" : l.Modified < c.Modified ? "云端较新" : "日期相同（未比较内容）";
            row.RecommendUpload = l != null && (c == null || l.Modified > c.Modified);
            row.Details = "仅按文件修改日期比较；日期相同不代表内容相同，日期较新也不代表模型内容完整。不会自动 Pull、导出或上传。\r\n本地候选（按修改日期降序，取第一份）：\r\n" + string.Join("\r\n", local?.Select(e => e.Path) ?? Enumerable.Empty<string>()) + "\r\nConnector 候选（按修改日期降序，取第一份）：\r\n" + string.Join("\r\n", connector?.Select(e => e.Path) ?? Enumerable.Empty<string>());
            return row;
        }
    }
}
