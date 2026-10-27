using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    // 预检只投影路径；用户确认后才暂存、备份并替换本地文件。
    internal sealed class LocalReferenceRepair : IReferenceReader
    {
        private readonly string root, connector;
        internal readonly IWeeklyDwg Native;
        internal readonly Dictionary<string, List<WeeklyRewrite>> Changes = new Dictionary<string, List<WeeklyRewrite>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<RefEdge>> projected = new Dictionary<string, List<RefEdge>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> dates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<UpdateReportRow> Reports = new List<UpdateReportRow>();

        internal LocalReferenceRepair(string localRoot, string connectorRoot, IWeeklyDwg native)
        {
            root = Path.GetFullPath(localRoot).TrimEnd('\\');
            connector = string.IsNullOrWhiteSpace(connectorRoot) ? null : Path.GetFullPath(connectorRoot).TrimEnd('\\'); Native = native;
        }
        private static bool Below(string folder, string file) => file.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        private bool SafeLocal(string file)
        {
            if (!Below(root, file) || Paths.IsHistory(file) || !File.Exists(file)) return false;
            // 防止目录联接把本地写入引向云端或项目之外。
            for (var current = file; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        private string Target(string saved)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path) { path = Path.GetFullPath(path); if (SafeLocal(path)) candidates.Add(path); }
            Add(saved); Add(Paths.Canonical(saved));
            if (Below(root, saved))
            {
                var relative = saved.Substring(root.Length + 1);
                var slash = relative.IndexOf('\\');
                if (slash > 0 && !relative.StartsWith("local_", StringComparison.OrdinalIgnoreCase)) Add(Path.Combine(root, "local_" + relative));
            }
            if (connector != null)
            {
                string relative = null;
                if (Below(connector, saved)) relative = saved.Substring(connector.Length + 1);
                else
                {
                    // 只允许工作空间盘符/用户前缀变化，租户、项目及完整云端目录必须相同。
                    var marker = connector.IndexOf("\\ACCDocs\\", StringComparison.OrdinalIgnoreCase);
                    var oldMarker = saved.IndexOf("\\ACCDocs\\", StringComparison.OrdinalIgnoreCase);
                    if (marker >= 0 && oldMarker >= 0)
                    {
                        var tail = connector.Substring(marker) + "\\";
                        if (saved.Substring(oldMarker).StartsWith(tail, StringComparison.OrdinalIgnoreCase)) relative = saved.Substring(oldMarker + tail.Length);
                    }
                }
                if (relative != null) Add(Paths.MirrorTarget(root, connector, Path.Combine(connector, relative)));
            }
            if (candidates.Count > 1) throw new IOException("嵌套路径有多个本地候选，需人工确认：" + saved + " → " + string.Join(" | ", candidates));
            return candidates.SingleOrDefault();
        }
        public List<RefEdge> Read(string owner)
        {
            if (projected.TryGetValue(owner, out var cached)) return cached;
            if (!SafeLocal(owner)) throw new IOException("嵌套源不在可安全修改的正式本地目录：" + owner);
            hashes[owner] = Files.Hash(owner); dates[owner] = File.GetLastWriteTimeUtc(owner);
            var edges = Native.Read(owner); var result = new List<RefEdge>(); var changes = new List<WeeklyRewrite>();
            foreach (var edge in edges)
            {
                var saved = Paths.Resolve(owner, edge.Saved); var target = Target(saved);
                if (target == null)
                {
                    // 未唯一定位的卸载分支仍由原有确认流程提示，已加载分支仍阻止。
                    result.Add(new RefEdge { Name = edge.Name, Saved = edge.Saved, Resolved = saved, Unloaded = edge.Unloaded, Overlay = edge.Overlay });
                    continue;
                }
                Files.CheckDwg(target);
                if (Path.IsPathRooted(edge.Saved) || !string.Equals(saved, target, StringComparison.OrdinalIgnoreCase)) changes.Add(new WeeklyRewrite { Name = edge.Name, Target = target });
                result.Add(new RefEdge { Name = edge.Name, Saved = edge.Saved, Resolved = target, Unloaded = edge.Unloaded, Overlay = edge.Overlay });
            }
            if (changes.Count > 0) Changes[owner] = changes;
            if (Files.Hash(owner) != hashes[owner] || File.GetLastWriteTimeUtc(owner) != dates[owner]) throw new IOException("读取期间嵌套源发生变化：" + owner);
            projected[owner] = result; return result;
        }
        internal void Verify()
        {
            foreach (var pair in hashes)
                if (!SafeLocal(pair.Key) || Files.Hash(pair.Key) != pair.Value || File.GetLastWriteTimeUtc(pair.Key) != dates[pair.Key]) throw new IOException("嵌套源在预检后发生变化，请重新检查：" + pair.Key);
            foreach (var target in Changes.Values.SelectMany(x => x).Select(x => x.Target))
                if (!SafeLocal(target)) throw new IOException("嵌套目标已不存在或不再安全：" + target);
        }
        internal void Apply(LocalAttachmentPlan plan, Action<string> closed, CancellationToken ct)
        {
            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Verify();
                foreach (var owner in Changes.Keys) { closed(owner); Files.AssertClosed(owner); }
                // 所有暂存图验证通过后才开始替换，避免暂存失败留下半批修改。
                foreach (var pair in Changes)
                {
                    ct.ThrowIfCancellationRequested(); staged[pair.Key] = Native.Stage(pair.Key, pair.Key, pair.Value);
                    Files.CheckDwg(staged[pair.Key]);
                    var actual = Native.Read(staged[pair.Key]);
                    foreach (var change in pair.Value)
                    {
                        var edge = actual.SingleOrDefault(e => e.Name == change.Name);
                        if (edge == null || Path.IsPathRooted(edge.Saved) || !string.Equals(Paths.Resolve(pair.Key, edge.Saved), change.Target, StringComparison.OrdinalIgnoreCase)) throw new IOException("暂存嵌套相对路径验证失败：" + change.Name);
                    }
                    File.SetLastWriteTimeUtc(staged[pair.Key], dates[pair.Key]);
                }
                Verify();
                foreach (var pair in Changes)
                {
                    ct.ThrowIfCancellationRequested(); closed(pair.Key);
                    if (!SafeLocal(pair.Key) || File.GetLastWriteTimeUtc(pair.Key) != dates[pair.Key]) throw new IOException("替换前嵌套源路径或日期发生变化：" + pair.Key);
                    var report = new UpdateReportRow { Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), Operation = "嵌套路径本地化", File = Path.GetFileName(pair.Key), Local = pair.Key, Assembly = plan.Assembly, BeforeHash = hashes[pair.Key], State = "未完成", Details = string.Join("\r\n", pair.Value.Select(c => c.Name + ": " + projected[pair.Key].Single(e => e.Name == c.Name).Saved + " → " + c.Target)) };
                    Reports.Add(report);
                    report.Backup = Files.InstallDownload(staged[pair.Key], pair.Key, hashes[pair.Key]);
                    report.AfterHash = Files.Hash(pair.Key); report.State = "已备份并本地化（保留修改日期）";
                    hashes[pair.Key] = report.AfterHash; plan.Hashes[pair.Key] = report.AfterHash;
                }
                Verify();
                // 从实际保存的 DWG 重新读取，不以预检投影代替最终检查。
                var checkedPlan = LocalReferenceAttachment.Plan(root, plan.Assembly, plan.Placement.Source, Native);
                if (checkedPlan.Hashes.Any(p => !plan.Hashes.TryGetValue(p.Key, out var h) || h != p.Value)) throw new IOException("修复后依赖发生变化，请重新检查。");
            }
            finally { foreach (var file in staged.Values) if (File.Exists(file)) File.Delete(file); }
        }
    }
}
