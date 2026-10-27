using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    internal static class ReferenceComparisonBuilder
    {
        private static string Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path ?? "";
            return Paths.Canonical(path);
        }
        private static bool Same(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        private static string Date(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss.fffffff") : "—"; }
            catch { return "不可读取"; }
        }
        internal static bool HasMissingSameName(AssemblyUpdate assembly, string file)
        {
            var direct = assembly.Scan.DirectReferencesScanned ? assembly.Scan.DirectRows : assembly.Scan.Rows;
            return direct.Any(r => string.Equals(r.Name, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase) && (r.SavedPaths.Count > 0 ? r.SavedPaths.Any(p => !File.Exists(p)) : !File.Exists(r.LocalPath)));
        }
        internal static List<ReferenceComparisonRow> Build(WorkflowSession session, AssemblyUpdate assembly)
        {
            var rows = new Dictionary<string, ReferenceComparisonRow>(StringComparer.OrdinalIgnoreCase);
            var sources = session.Sources.GroupBy(s => Key(s.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var cloudPaths = sources.Select(s => s.Path).ToList(); var matcher = new SystemMatcher(cloudPaths); var systems = matcher.Systems(assembly.Path);
            var direct = assembly.Scan.DirectReferencesScanned ? assembly.Scan.DirectRows : assembly.Scan.Rows;
            ReferenceComparisonRow Add(string local, CloudUpdate source, bool referenced, bool unloaded = false, string original = null, bool ambiguous = false, IEnumerable<string> referenceNames = null)
            {
                local = Key(local); var cloud = source?.Path ?? "未匹配";
                // 文件身份按规范化本地路径归并，不能将同名云端候选展开为多个真实引用。
                var key = Path.IsPathRooted(local) ? local : local + "\n" + Key(cloud);
                if (!rows.TryGetValue(key, out var row))
                {
                    var exists = File.Exists(local); var plan = source?.Plans.FirstOrDefault(p => Same(p.Local, local));
                    var state = source == null ? referenced ? "对应待确认" : "仅本地 · 未引用" : plan?.Blocked ?? (ambiguous ? "对应待确认" : plan != null ? plan.IsUnchanged ? "日期相同" : "可更新" : !referenced ? "新增候选 · 待确认" : "对应待确认");
                    row = new ReferenceComparisonRow { Name = Path.GetFileName(local), Local = local, Cloud = cloud, LocalState = exists ? "存在" : "缺失", CloudState = source == null ? "未匹配" : File.Exists(cloud) ? "存在" : "缺失 / 不可读取", State = state, LocalDate = Date(local), CloudDate = Date(cloud), Unloaded = unloaded, PairConfirmed = source != null && !ambiguous };
                    rows.Add(key, row);
                }
                if (!referenced) return row;
                foreach (var name in referenceNames ?? Enumerable.Empty<string>()) if (!row.ReferenceNames.Contains(name, StringComparer.OrdinalIgnoreCase)) row.ReferenceNames.Add(name);
                row.Unloaded = row.Referenced ? row.Unloaded && unloaded : unloaded; row.Referenced = true;
                if (!string.IsNullOrEmpty(original) && !row.ReferencePaths.Contains(original, StringComparer.OrdinalIgnoreCase)) row.ReferencePaths.Add(original);
                if (!string.IsNullOrEmpty(original) && !File.Exists(original) && File.Exists(local))
                {
                    row.RequiresPathRepair = true; row.State = "引用路径待修复"; row.LocalState = "存在 · 旧引用失效";
                }
                return row;
            }
            List<CloudUpdate> Confirmed(string local)
            {
                var planned = sources.Where(s => s.Plans.Any(p => Same(p.Local, local))).ToList();
                // 明确计划（含人工对应）优先于默认镜像；待确认候选绝不与已确认对等。
                return planned.Count > 0 ? planned : sources.Where(s => Same(s.ExpectedLocalPath, local)).ToList();
            }
            void Choices(ReferenceComparisonRow row, List<CloudUpdate> matches)
            {
                foreach (var source in matches) if (!row.CandidateCloudPaths.Contains(source.Path, StringComparer.OrdinalIgnoreCase)) row.CandidateCloudPaths.Add(source.Path);
                row.State = "对应待确认";
                row.PairConfirmed = false;
                if (matches.Count > 1)
                {
                    row.Cloud = "待确认（" + matches.Count + " 个候选）"; row.CloudState = "待确认"; row.CloudDate = "—";
                }
            }
            foreach (var reference in direct)
            {
                var local = reference.LocalPath; var resolutionAmbiguous = false;
                if (!File.Exists(local))
                {
                    try
                    {
                        var candidates = AssemblyReferenceRepair.FindTargets(session.LocalModelsRoot, session.ConnectorRoot, assembly.Path, local, cloudPaths);
                        if (candidates.Count == 1) local = candidates[0];
                    }
                    catch (IOException) { resolutionAmbiguous = true; }
                }
                var matches = Confirmed(local); var confirmed = matches.Count > 0;
                if (!confirmed)
                {
                    matches = sources.Where(s => s.AmbiguousLocals.Any(p => Same(p, reference.LocalPath))).ToList();
                    var referenceSystems = matcher.Systems(Path.GetDirectoryName(local));
                    if (referenceSystems.Count == 0 && !File.Exists(local)) referenceSystems = systems;
                    if (referenceSystems.Count == 1) matches = matches.Where(s => matcher.Systems(Path.GetDirectoryName(s.Path)).SetEquals(referenceSystems) || matcher.Systems(Path.GetDirectoryName(s.Path)).Count == 0).ToList();
                }
                var originals = reference.SavedPaths.Count > 0 ? reference.SavedPaths : new List<string> { reference.LocalPath };
                foreach (var original in originals)
                {
                    var row = Add(local, matches.Count == 1 ? matches[0] : null, true, reference.Unloaded, original, resolutionAmbiguous || !confirmed || matches.Count > 1, reference.ReferenceNames);
                    if (matches.Count > 1 || !confirmed && matches.Count > 0) Choices(row, matches);
                }
            }
            foreach (var source in sources.Where(s => matcher.Systems(s.Path).Overlaps(systems)))
            {
                var target = source.ExpectedLocalPath;
                if (!File.Exists(target) && source.Plans.Count == 1) target = source.Plans[0].Local;
                // 已关联的失效引用与正式文件共用同一行；真实存在的另一份副本仍保留。
                if (rows.Values.Any(r => Same(r.Cloud, source.Path) && (Same(r.Local, target) || r.Referenced && !File.Exists(target)))) continue;
                if (Path.IsPathRooted(target ?? "") && rows.Values.Any(r => Same(r.Local, target))) continue;
                Add(target ?? "待指定", source, false);
            }
            var folders = rows.Values.Select(r => Path.IsPathRooted(r.Local ?? "") ? Path.GetDirectoryName(r.Local) : null).Where(p => p != null && Directory.Exists(p) && Paths.IsWithin(session.LocalModelsRoot, p) && !Paths.IsHistory(p) && !Same(p, session.ScopeFolder) && (systems.Count == 0 || matcher.Systems(p).Overlaps(systems))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var enumerated = new HashSet<string>(rows.Values.Select(r => Key(r.Local)), StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
                foreach (var file in TargetFinder.Index(folder, CancellationToken.None, message => { if (!session.Warnings.Contains(message)) session.Warnings.Add(message); }).Values.SelectMany(v => v))
                {
                    if (Paths.IsHistory(file) || !enumerated.Add(Key(file))) continue;
                    var matches = Confirmed(file);
                    var row = Add(file, matches.Count == 1 ? matches[0] : null, false, ambiguous: matches.Count > 1);
                    if (matches.Count > 1) Choices(row, matches);
                }
            foreach (var row in rows.Values)
            {
                if (!row.Referenced) row.LocalState += " / 未引用";
                try { row.DateDifference = ReferenceComparisonRow.FormatDateDifference(SyncService.ModifiedUtc(row.Local), SyncService.ModifiedUtc(row.Cloud)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { row.DateDifference = "日期不可读取"; }
            }
            return rows.Values.OrderBy(r => r.DisplayPriority).ThenBy(r => r.Referenced ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Local, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
