using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    internal sealed class AssemblyPathRepairPlan
    {
        internal string Assembly, BeforeHash;
        internal List<RefEdge> Before;
        internal List<WeeklyRewrite> Changes = new List<WeeklyRewrite>();
        internal Dictionary<string, string> Targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal UpdateReportRow Report;
    }
    internal static class AssemblyReferenceRepair
    {
        // 界面归并与实际修复共用同一规则，避免显示已对应但执行时指向另一份文件。
        internal static List<string> FindTargets(string root, string connector, string assembly, string raw, IEnumerable<string> cloudFiles)
        {
            bool Valid(string path)
            {
                if (!File.Exists(path) || !Paths.IsWithin(root, path) || Paths.IsHistory(path)) return false;
                for (var parent = Path.GetDirectoryName(path); Paths.IsWithin(root, parent); parent = Path.GetDirectoryName(parent))
                    if (!TargetFinder.CanTraverse(parent)) throw new IOException("修复目标含目录链接：" + parent);
                Files.CheckDwg(path); return true;
            }
            var canonical = Paths.Canonical(raw);
            if (Valid(canonical)) return new List<string> { canonical };
            var cloud = cloudFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var matcher = new SystemMatcher(cloud);
            var systems = matcher.Systems(Path.GetDirectoryName(raw));
            if (systems.Count == 0) systems = matcher.Systems(assembly);
            if (systems.Count != 1) return new List<string>();
            var named = cloud.Where(p => string.Equals(Path.GetFileName(p), Path.GetFileName(raw), StringComparison.OrdinalIgnoreCase) && matcher.Systems(Path.GetDirectoryName(p)).SetEquals(systems)).ToList();
            if (named.Count > 1) throw new IOException("同系统有多个同名云端文件，需人工确认：" + Path.GetFileName(raw));
            if (named.Count == 0) return new List<string>();
            var target = Paths.MirrorTarget(root, connector, named[0]);
            return Valid(target) ? new List<string> { target } : new List<string>();
        }
        internal static AssemblyPathRepairPlan Plan(string root, string connector, string assembly, IEnumerable<string> cloudFiles, IReferenceReader reader, Action<string> closed = null)
        {
            if (!Paths.IsWithin(root, assembly) || Paths.IsHistory(assembly)) throw new IOException("总装不在正式本地模型目录内。");
            var before = reader.Read(assembly); var cloud = cloudFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var result = new AssemblyPathRepairPlan { Assembly = assembly, Before = before };
            foreach (var edge in before)
            {
                var raw = Paths.Resolve(assembly, edge.Saved);
                if (File.Exists(raw)) continue;
                var candidates = FindTargets(root, connector, assembly, raw, cloud);
                if (candidates.Count > 1) throw new IOException("总装引用存在多个候选，需人工确认：" + edge.Name);
                if (candidates.Count == 1)
                {
                    var target = candidates.Single(); result.Changes.Add(new WeeklyRewrite { Name = edge.Name, Target = target }); result.Targets[target] = Files.Hash(target);
                }
            }
            if (result.Changes.Count > 0) { closed?.Invoke(assembly); Files.AssertClosed(assembly); result.BeforeHash = Files.Hash(assembly); }
            return result;
        }
        internal static UpdateReportRow Apply(AssemblyPathRepairPlan plan, IWeeklyDwg native, Action<string> closed, CancellationToken ct)
        {
            string temporary = null;
            var report = new UpdateReportRow { Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), Operation = "修复总装引用路径", File = Path.GetFileName(plan.Assembly), Local = plan.Assembly, Assembly = plan.Assembly, BeforeHash = plan.BeforeHash, State = "未完成", Details = string.Join("\r\n", plan.Changes.Select(c => c.Name + ": " + plan.Before.Single(e => e.Name == c.Name).Saved + " → " + c.Target)) };
            plan.Report = report;
            void Verify()
            {
                if (Files.Hash(plan.Assembly) != plan.BeforeHash || plan.Targets.Any(p => Files.Hash(p.Key) != p.Value)) throw new IOException("修复预览后文件发生变化，请重新检查。");
            }
            try
            {
                ct.ThrowIfCancellationRequested(); closed(plan.Assembly); Files.AssertClosed(plan.Assembly); Verify();
                temporary = native.Stage(plan.Assembly, plan.Assembly, plan.Changes);
                var actual = native.Read(temporary);
                if (actual.Count != plan.Before.Count) throw new IOException("暂存总装的直接引用数量改变，停止替换。");
                foreach (var edge in plan.Before)
                {
                    var after = actual.SingleOrDefault(e => e.Name == edge.Name); var change = plan.Changes.SingleOrDefault(c => c.Name == edge.Name);
                    if (after == null || after.Unloaded != edge.Unloaded || after.Overlay != edge.Overlay || (change == null ? after.Saved != edge.Saved : Path.IsPathRooted(after.Saved) || !string.Equals(Paths.Resolve(plan.Assembly, after.Saved), change.Target, StringComparison.OrdinalIgnoreCase))) throw new IOException("暂存总装路径或引用状态未通过校验：" + edge.Name);
                }
                var scan = Scanner.ScanForSynchronization(temporary, Path.GetDirectoryName(plan.Assembly), native);
                if (scan.Warnings.Count > 0) throw new IOException("修复后仍有有效引用问题：" + string.Join(" | ", scan.Warnings));
                ct.ThrowIfCancellationRequested(); Verify(); closed(plan.Assembly);
                report.Backup = Files.InstallDownload(temporary, plan.Assembly, plan.BeforeHash); temporary = null;
                report.AfterHash = Files.Hash(plan.Assembly); report.State = "已备份并修复"; return report;
            }
            catch (Exception ex) { report.State = "修复失败：" + ex.Message; throw; }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
