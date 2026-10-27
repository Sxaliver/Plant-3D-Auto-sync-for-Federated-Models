using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace IWMF.PlantModelSync
{
    internal sealed class AssemblyUpdate
    {
        public bool Selected { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        // 按实际目标路径显示完整文件名，不能省略用于区分 M / MLP 的前缀。
        public string DisplayName => string.IsNullOrEmpty(Path) ? Name ?? "" : System.IO.Path.GetFileName(Path);
        public string PendingXrefs { get; set; }
        public string UpdatedXrefs { get; set; }
        public string RefreshState { get; set; } = "尚未 Pull";
        public string ExportState { get; set; } = "未导出";
        public string ExportPath { get; set; }
        public string ExportHash { get; set; }
        public string DrawingStamp { get; set; }
        public string XrefSummary => (string.IsNullOrEmpty(UpdatedXrefs) ? "" : "已更新：" + UpdatedXrefs) + (string.IsNullOrEmpty(PendingXrefs) ? "" : " 待 Pull：" + PendingXrefs);
        public string Status => ExportState.StartsWith("导出失败", StringComparison.Ordinal) ? "导出失败" : ExportState.StartsWith("已导出", StringComparison.Ordinal) ? "已导出" : ExportState != "未导出" ? ExportState : RefreshState.StartsWith("源文件已更新", StringComparison.Ordinal) ? "待加载 / 待导出" : RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal) ? "引用重载失败" : RefreshState;
        public ScanResult Scan { get; set; }
        public List<string> UpdatedPaths { get; set; } = new List<string>();
        public Dictionary<string, string> ExpectedHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ExpectedCloudHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class CloudUpdate
    {
        public bool Selected { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string RelativePath { get; set; }
        public string DisplayPath => RelativePath ?? "";
        public string Modified { get; set; }
        public string State { get; set; }
        public string DisplayState { get; set; }
        public string DisplayLocalSummary { get; set; }
        public string MissingSummary => "缺失 " + Plans.Select(p => p.Local).Concat(AmbiguousLocals).Where(p => !File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        public string LocalSummary { get; set; }
        public string AffectedSummary { get; set; }
        public string System { get; set; }
        public string MatchReason { get; set; }
        public bool LocalExists { get; set; }
        public string ExpectedLocalPath { get; set; }
        public List<TransferPlan> Plans { get; set; } = new List<TransferPlan>();
        public List<string> Diagnostics { get; set; } = new List<string>();
        public List<string> AmbiguousLocals { get; set; } = new List<string>();
        public List<string> MissingReferenceIssues { get; set; } = new List<string>();
    }

    internal sealed class WorkflowSession
    {
        public string ConnectorRoot { get; set; }
        public string ScopeFolder { get; set; }
        public string LocalModelsRoot { get; set; }
        public string OutputFolder { get; set; }
        public List<AssemblyUpdate> Assemblies { get; set; } = new List<AssemblyUpdate>();
        public List<CloudUpdate> Sources { get; set; } = new List<CloudUpdate>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, string> UpdatedHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> AddedReferences { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> PendingReferenceAdds { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public string LastLog { get; set; }
        public string LastUpdateLog { get; set; }
        public bool Checked { get; set; }
        public CachedReader ReferenceCache { get; set; }
        public List<string> LocalMappingFiles { get; set; }
        public DetectionCache HashCache { get; set; } = new DetectionCache();
        public List<AssemblyAuditRow> AssemblyAudit { get; set; } = new List<AssemblyAuditRow>();
        public void InvalidateAutomaticMappings()
        {
            Checked = false; Sources.Clear(); LocalMappingFiles = null; ReferenceCache?.Clear(); HashCache.Clear();
        }
    }

    // 在宿主线程读取 DWG；缓存直接依赖，避免每一份总装重复打开共享源文件。
    internal sealed class CachedReader : IReferenceReader
    {
        private readonly IReferenceReader inner;
        private readonly Dictionary<string, Tuple<string, List<RefEdge>>> cache = new Dictionary<string, Tuple<string, List<RefEdge>>>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, bool> bypass;
        public int Hits { get; private set; }
        public int Reads { get; private set; }
        public CachedReader(IReferenceReader reader, Func<string, bool> bypassCache = null) { inner = reader; bypass = bypassCache; }
        public void Clear() { cache.Clear(); }
        public List<RefEdge> Read(string path)
        {
            var key = Paths.Canonical(path);
            // 已打开的图纸可能有未保存的引用变化，始终交给宿主读取。
            var stamp = DetectionCache.Stamp(key);
            if (bypass?.Invoke(key) != true && stamp != null && cache.TryGetValue(key, out var cached) && cached.Item1 == stamp) { Hits++; return cached.Item2; }
            var result = inner.Read(path); Reads++;
            var after = DetectionCache.Stamp(key);
            if (stamp != null && stamp == after && bypass?.Invoke(key) != true) cache[key] = Tuple.Create(stamp, result);
            else cache.Remove(key);
            return result;
        }
    }

    // 按实际系统目录归属缩小同名候选，不以日期或内容相似度猜测对应关系。
    internal sealed class SystemMatcher
    {
        private static readonly Regex Code = new Regex(@"(?<!\d)(94\d{2})(?!\d)", RegexOptions.Compiled);
        private readonly Dictionary<string, HashSet<string>> aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public SystemMatcher(IEnumerable<string> cloudPaths)
        {
            foreach (var segment in cloudPaths.SelectMany(Segments).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var matches = Code.Matches(segment);
                if (matches.Count != 1) continue;
                var match = matches[0]; var alias = Normalize(segment.Substring(match.Index + match.Length));
                if (alias.Length < 3) continue;
                if (!aliases.TryGetValue(alias, out var systems)) aliases[alias] = systems = new HashSet<string>();
                systems.Add(match.Value);
            }
        }
        private static string[] Segments(string path) => (path ?? "").Replace('\\', '/').Split('/');
        private static string Normalize(string text) => Regex.Replace(Path.GetFileNameWithoutExtension(text).ToUpperInvariant(), @"[^\p{L}\p{N}]", "");
        public HashSet<string> Systems(string path)
        {
            foreach (var segment in Segments(path).Reverse())
            {
                var codes = Code.Matches(segment).Cast<Match>().Select(m => m.Value).ToList();
                if (codes.Count > 0) return new HashSet<string>(codes);
                if (aliases.TryGetValue(Normalize(segment), out var systems) && systems.Count == 1) return new HashSet<string>(systems);
            }
            return new HashSet<string>();
        }
        public static int SuffixScore(string local, string cloud)
        {
            var a = Segments(local).Reverse().ToArray(); var b = Segments(cloud).Reverse().ToArray(); int n = 0;
            while (n < a.Length && n < b.Length && string.Equals(a[n], b[n], StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
    }

    internal static class Workflow
    {
        public static AssemblyUpdate ScanAssembly(string path, IReferenceReader reader, bool forSynchronization = false)
        {
            var full = Paths.Canonical(path);
            return new AssemblyUpdate { Name = System.IO.Path.GetFileName(full), Path = full, DrawingStamp = DetectionCache.Stamp(full), Scan = forSynchronization ? Scanner.ScanForSynchronization(full, System.IO.Path.GetDirectoryName(full), reader) : Scanner.Scan(full, System.IO.Path.GetDirectoryName(full), reader) };
        }

        public static async Task<List<CloudUpdate>> Check(WorkflowSession session, Settings settings, CancellationToken ct, Action<string> progress, SyncService service = null, DetectionCache hashCache = null, Action<int, int, string> itemProgress = null)
        {
            var folder = FolderLink.Parse(session.ConnectorRoot);
            var index = TargetFinder.Index(folder.Project, ct, message => session.Warnings.Add(message));
            var locals = session.Assemblies.SelectMany(a => a.Scan.Rows).GroupBy(r => r.LocalPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var mirrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var knownLocals = new HashSet<string>(locals.Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase);
            // 目录索引只枚举文件，不打开未勾选总装；强制刷新后重新建立整个本地源目录的对应候选。
            if (session.LocalMappingFiles == null && !string.IsNullOrWhiteSpace(settings.LocalModelsRoot) && Directory.Exists(settings.LocalModelsRoot))
            {
                progress?.Invoke("扫描本地 DWG / 重建目录映射");
                session.LocalMappingFiles = TargetFinder.Index(settings.LocalModelsRoot, ct, message => session.Warnings.Add(message)).Values.SelectMany(v => v)
                    .Where(p => !Paths.IsHistory(p) && !session.Assemblies.Any(a => string.Equals(Paths.Canonical(a.Path), p, StringComparison.OrdinalIgnoreCase))
                        && (string.IsNullOrWhiteSpace(session.ScopeFolder) || string.Equals(Paths.Canonical(session.ScopeFolder), Paths.Canonical(settings.LocalModelsRoot), StringComparison.OrdinalIgnoreCase) || !Paths.IsWithin(session.ScopeFolder, p)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            foreach (var path in session.LocalMappingFiles ?? Enumerable.Empty<string>())
                if (File.Exists(path) && knownLocals.Add(path)) locals.Add(new XrefRow { Name = Path.GetFileName(path), LocalPath = path, State = "本地存在" });
            foreach (var source in index.Values.SelectMany(x => x))
            {
                var target = Paths.MirrorTarget(settings.LocalModelsRoot, folder.Project, source);
                if (target == null || !File.Exists(target)) continue;
                mirrors[target] = source;
                if (knownLocals.Add(target)) locals.Add(new XrefRow { Name = Path.GetFileName(target), LocalPath = target, State = "本地存在" });
            }
            var localNames = locals.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var matcher = new SystemMatcher(index.Values.SelectMany(x => x));
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var ownerModels = new Dictionary<string, List<AssemblyUpdate>>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in session.Assemblies)
            {
                var assemblySystems = matcher.Systems(assembly.Path);
                foreach (var row in assembly.Scan.Rows)
                {
                    if (!owners.TryGetValue(row.LocalPath, out var systems)) owners[row.LocalPath] = systems = new HashSet<string>();
                    systems.UnionWith(assemblySystems);
                    if (!ownerModels.TryGetValue(row.LocalPath, out var models)) ownerModels[row.LocalPath] = models = new List<AssemblyUpdate>();
                    models.Add(assembly);
                }
            }
            var result = index.Values.SelectMany(x => x).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => new CloudUpdate
            {
                Name = Path.GetFileName(p), Path = p, RelativePath = Paths.Relative(folder.Project, p), Modified = "读取时核对", State = "无本地对应", System = string.Join(" / ", matcher.Systems(p).OrderBy(x => x)), ExpectedLocalPath = Paths.MirrorTarget(settings.LocalModelsRoot, folder.Project, p)
            }).ToList();
            var byPath = result.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
            var mapping = new List<XrefRow>();
            var reasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var local in locals)
            {
                ct.ThrowIfCancellationRequested();
                if (Paths.IsHistory(local.LocalPath) || (!string.IsNullOrWhiteSpace(settings.LocalModelsRoot) && !Paths.IsWithin(settings.LocalModelsRoot, local.LocalPath)))
                { session.Warnings.Add("不参与 Pull（历史目录或模型根目录以外）：" + local.LocalPath); continue; }
                string target = null, reason = null;
                var candidates = index.TryGetValue(local.Name, out var named) ? named.ToList() : new List<string>();
                if (!File.Exists(local.LocalPath))
                {
                    var missingSystems = matcher.Systems(local.LocalPath);
                    if (missingSystems.Count == 0 && owners.TryGetValue(local.LocalPath, out var ownedSystems)) missingSystems.UnionWith(ownedSystems);
                    if (missingSystems.Count == 1)
                    {
                        var sameSystem = candidates.Where(p => matcher.Systems(Path.GetDirectoryName(p)).SetEquals(missingSystems)).ToList();
                        if (sameSystem.Count > 0) candidates = sameSystem;
                    }
                }
                if (settings.Mappings.TryGetValue(Settings.MapKey(folder, local.LocalPath), out var saved) && !string.IsNullOrWhiteSpace(saved))
                {
                    string mapped = null;
                    try { mapped = Paths.SyncTarget(folder.Project, saved); if (byPath.ContainsKey(mapped)) { target = mapped; reason = "人工确认的文件对应"; } }
                    catch (ArgumentException) { }
                    if (target == null)
                    {
                        // 失效人工对应只列本系统及完整尾部路径候选，不能污染所有同名系统。
                        var context = matcher.Systems(local.LocalPath);
                        if (context.Count == 0 && owners.TryGetValue(local.LocalPath, out var owned)) context.UnionWith(owned);
                        if (context.Count == 1) candidates = candidates.Where(p => matcher.Systems(Path.GetDirectoryName(p)).SetEquals(context)).ToList();
                        if (mapped != null && candidates.Count > 0)
                        {
                            var score = candidates.Max(p => SystemMatcher.SuffixScore(mapped, p));
                            if (score >= 2) candidates = candidates.Where(p => SystemMatcher.SuffixScore(mapped, p) == score).ToList();
                        }
                        var diagnostic = "已保存的对应文件不在本次目录中，请重新指定：" + local.LocalPath + "；旧云端：" + saved;
                        foreach (var candidate in candidates) byPath[candidate].Diagnostics.Add(diagnostic);
                        if (candidates.Count == 0) session.Warnings.Add(diagnostic);
                    }
                }
                else if (!File.Exists(local.LocalPath) && candidates.Any(p => { var expected = Paths.MirrorTarget(settings.LocalModelsRoot, folder.Project, p); return expected != null && File.Exists(expected) && !string.Equals(expected, local.LocalPath, StringComparison.OrdinalIgnoreCase); }))
                {
                    // 正式目录已有文件时，缺失引用不能按同名猜成另一个需要创建的副本。
                    foreach (var candidate in candidates)
                    {
                        byPath[candidate].MissingReferenceIssues.Add("Xref 指向不存在位置，正式镜像已有同名文件；需核对引用路径，不自动新建：" + local.LocalPath);
                        byPath[candidate].AmbiguousLocals.Add(local.LocalPath);
                    }
                    continue;
                }
                else if (mirrors.TryGetValue(local.LocalPath, out var mirror)) { target = mirror; reason = "local_ 目录镜像，相对子路径一致"; }
                else if (candidates.Count > 0)
                {
                    var systems = matcher.Systems(local.LocalPath);
                    if (systems.Count == 0 && owners.TryGetValue(local.LocalPath, out var ownerSystems)) systems.UnionWith(ownerSystems);
                    string rule = null;
                    for (var parent = Path.GetDirectoryName(local.LocalPath); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                        if (settings.SystemFolders.TryGetValue(Settings.MapKey(folder, parent), out rule)) break;
                    if (!string.IsNullOrEmpty(rule))
                    {
                        var prefix = Paths.Canonical(Path.Combine(folder.Project, rule)) + Path.DirectorySeparatorChar;
                        candidates = candidates.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                        reason = "已确认的系统文件夹：" + rule;
                    }
                    else if (systems.Count == 1)
                    {
                        var sameSystem = candidates.Where(p => matcher.Systems(p).SetEquals(systems)).ToList();
                        if (sameSystem.Count == 0 && candidates.Count == 1 && matcher.Systems(candidates[0]).Count == 0 && localNames[local.Name].Count == 1)
                            reason = "公共目录唯一同名 DWG";
                        else { candidates = sameSystem; reason = "系统 " + systems.Single() + " + 同名 DWG"; }
                    }
                    // 多系统共用的本地源，不因某个系统优先而错误覆盖。
                    else if (systems.Count > 1 && candidates.Count > 1) reason = "跨系统共享 Xref，需人工确认";
                    if ((!string.IsNullOrEmpty(rule) || systems.Count <= 1) && candidates.Count > 1)
                    {
                        var score = candidates.Max(p => SystemMatcher.SuffixScore(local.LocalPath, p));
                        if (score >= 2) { candidates = candidates.Where(p => SystemMatcher.SuffixScore(local.LocalPath, p) == score).ToList(); reason = (reason == null ? "" : reason + "；") + "子目录及文件名一致"; }
                    }
                    if (candidates.Count == 1 && (!string.IsNullOrEmpty(rule) || systems.Count == 1 || localNames[local.Name].Count == 1)) { target = candidates[0]; reason = reason ?? "全目录唯一同名 DWG"; }
                }
                if (target == null)
                {
                    // 系统内无同名时不跨系统回退；仍显示真实候选及需要确认的本地路径。
                    var uncertain = candidates;
                    foreach (var candidate in uncertain) { byPath[candidate].Diagnostics.Add((reason ?? "同名对应未确认") + "：" + local.LocalPath); byPath[candidate].AmbiguousLocals.Add(local.LocalPath); }
                }
                else
                {
                    mapping.Add(new XrefRow { Name = local.Name, LocalPath = local.LocalPath, CloudPath = target, State = local.State, MatchStatus = reason });
                    if (!reasons.TryGetValue(target, out var list)) reasons[target] = list = new List<string>(); list.Add(reason);
                }
            }
            var plans = await (service ?? new SyncService(settings)).Plan(folder, mapping, false, ct, progress, hashCache, itemProgress).ConfigureAwait(false);
            foreach (var plan in plans)
            {
                plan.AffectedAssemblies = ownerModels.TryGetValue(plan.Local, out var planOwners) ? planOwners.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
                byPath[plan.ConnectorPath].Plans.Add(plan);
            }
            foreach (var source in result)
            {
                try { source.Modified = File.GetLastWriteTime(source.Path).ToString("yyyy-MM-dd HH:mm"); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { source.Diagnostics.Add("文件日期不可读取：" + ex.Message); }
                source.LocalSummary = string.Join(" | ", source.Plans.Select(p => Path.GetFileName(Path.GetDirectoryName(p.Local)) + "/" + Path.GetFileName(p.Local)));
                source.MatchReason = reasons.TryGetValue(source.Path, out var matched) ? string.Join("；", matched.Distinct()) : source.AmbiguousLocals.Count > 0 ? "待确认：选择此行后点击“确认对应”" : "未被本地总装引用";
                if (string.IsNullOrEmpty(source.System)) source.System = "公共 / 未分类";
                if (source.AmbiguousLocals.Count > 0) source.State = "对应有歧义";
                else if (source.Diagnostics.Count > 0) source.State = "需处理";
                else if (source.Plans.Count == 0) source.State = "无本地对应";
                else if (source.Plans.Any(p => p.Blocked != null)) source.State = "需处理";
                else if (source.Plans.Any(p => !p.IsUnchanged)) source.State = "可更新";
                else source.State = "日期相同";
                source.LocalExists = source.Plans.Any(p => File.Exists(p.Local)) || source.AmbiguousLocals.Any(File.Exists);
                source.DisplayState = source.State; source.DisplayLocalSummary = source.LocalSummary; source.Selected = false;
                source.AffectedSummary = string.Join(" | ", source.Plans.SelectMany(p => ownerModels.TryGetValue(p.Local, out var models) ? models : Enumerable.Empty<AssemblyUpdate>()).Distinct().Select(a => a.DisplayName));
            }
            return result;
        }

        public static List<TransferPlan> VisiblePlans(CloudUpdate source, bool existingOnly) => source.Plans.Where(p => !existingOnly || File.Exists(p.Local)).ToList();

        public static string SourceState(CloudUpdate source, bool existingOnly)
        {
            var plans = VisiblePlans(source, existingOnly);
            if (source.AmbiguousLocals.Any(p => !existingOnly || File.Exists(p))) return "对应有歧义";
            if (source.Diagnostics.Count > 0 || plans.Any(p => p.Blocked != null)) return "需处理";
            if (plans.Count == 0) return "无本地对应";
            if (plans.Any(p => p.Row.SyncState == SyncFeedback.Failed)) return "Pull 失败";
            if (plans.All(p => p.Row.SyncState == SyncFeedback.Synced)) return plans.Any(p => p.Written && p.Verified) ? "已 Pull" : "日期相同";
            return plans.Any(p => !p.IsUnchanged && p.Row.SyncState != SyncFeedback.Synced) ? "可更新" : "日期相同";
        }

        public static List<XrefRow> PullRows(WorkflowSession session, bool existingOnly) => session.Sources.Where(s => s.Selected).SelectMany(s => VisiblePlans(s, existingOnly)).Select(p => p.Row).GroupBy(r => Paths.Canonical(r.LocalPath), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        public static List<AssemblyUpdate> RelatedAssemblies(WorkflowSession session, IEnumerable<CloudUpdate> sources, bool existingOnly = false)
        {
            var allPending = new HashSet<string>(session.PendingReferenceAdds.Values.SelectMany(p => p), StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(sources.SelectMany(s => VisiblePlans(s, existingOnly).Select(p => p.Local).Concat(s.AmbiguousLocals.Where(p => !existingOnly || File.Exists(p))).Concat(s.ExpectedLocalPath != null && allPending.Contains(s.ExpectedLocalPath) ? new[] { s.ExpectedLocalPath } : new string[0])), StringComparer.OrdinalIgnoreCase);
            return session.Assemblies.Where(a => a.Scan.Rows.Any(r => paths.Contains(r.LocalPath)) || session.PendingReferenceAdds.TryGetValue(a.Path, out var waiting) && waiting.Any(paths.Contains)).ToList();
        }

        public static void UpdateAffected(WorkflowSession session)
        {
            var pending = session.Sources.SelectMany(s => s.Plans).Where(p => !p.IsUnchanged && p.Row.SyncState != SyncFeedback.Synced).ToList();
            foreach (var assembly in session.Assemblies)
            {
                var dependencies = new HashSet<string>(assembly.Scan.Rows.Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase);
                assembly.PendingXrefs = string.Join(" | ", pending.Where(p => dependencies.Contains(p.Local)).Select(p => p.Row.Name)
                    .Concat(session.Sources.Where(s => s.AmbiguousLocals.Any(dependencies.Contains)).Select(s => s.Name + "（对应待确认）"))
                    .Concat(session.PendingReferenceAdds.TryGetValue(assembly.Path, out var waiting) ? waiting.Select(p => Path.GetFileName(p) + "（新增引用未完成）") : Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase));
                var added = session.AddedReferences.TryGetValue(assembly.Path, out var additions) ? additions : new List<string>();
                assembly.UpdatedPaths = session.UpdatedHashes.Keys.Concat(added).Where(dependencies.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var repeated = new HashSet<string>(assembly.UpdatedPaths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
                assembly.UpdatedXrefs = string.Join(" | ", assembly.UpdatedPaths.Select(p => (added.Contains(p, StringComparer.OrdinalIgnoreCase) ? "新增引用：" : "") + (repeated.Contains(Path.GetFileName(p)) ? Path.GetFileName(Path.GetDirectoryName(p)) + "/" + Path.GetFileName(p) : Path.GetFileName(p))));
                var expected = assembly.UpdatedPaths.Where(session.UpdatedHashes.ContainsKey).ToDictionary(p => p, p => session.UpdatedHashes[p], StringComparer.OrdinalIgnoreCase);
                var verified = session.Sources.SelectMany(s => s.Plans).Where(p => dependencies.Contains(p.Local) && p.Blocked == null && (p.IsUnchanged || p.Row.SyncState == SyncFeedback.Synced)).ToList();
                foreach (var plan in verified) expected[plan.Local] = plan.Written && plan.Verified ? plan.ConnectorHash : plan.LocalHash;
                if (expected.Count != assembly.ExpectedHashes.Count || expected.Any(pair => !assembly.ExpectedHashes.TryGetValue(pair.Key, out var old) || old != pair.Value)) { assembly.ExportState = "未导出"; assembly.ExportHash = null; }
                assembly.ExpectedHashes = expected;
                assembly.ExpectedCloudHashes = verified.GroupBy(p => p.ConnectorPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ConnectorHash, StringComparer.OrdinalIgnoreCase);
                if (assembly.UpdatedPaths.Count == 0) assembly.Selected = false;
            }
        }

        public static void RecordPull(WorkflowSession session, IEnumerable<TransferPlan> executed)
        {
            foreach (var plan in executed)
            {
                if (plan.Written && plan.Verified && !plan.IsUnchanged) session.UpdatedHashes[plan.Local] = plan.ConnectorHash;
            }
            foreach (var source in session.Sources)
            {
                source.LocalExists = source.Plans.Any(p => File.Exists(p.Local)) || source.AmbiguousLocals.Any(File.Exists);
                source.State = SourceState(source, false);
                if (source.State == "已 Pull" || source.State == "日期相同") source.Selected = false;
            }
            UpdateAffected(session);
        }

        public static string RecordReferenceRecovery(WorkflowSession session, IEnumerable<AssemblyUpdate> affected)
        {
            var errors = affected.Where(a => a.RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal)).Select(a => a.Name + "：" + a.RefreshState).ToList();
            if (errors.Count == 0) return null;
            session.Checked = false; session.Warnings.AddRange(errors);
            return string.Join("；", errors);
        }

        public static void RecordExportResult(WorkflowSession session, BatchExportJob job)
        {
            var original = session.Assemblies.FirstOrDefault(a => string.Equals(a.Path, job.Assembly.Path, StringComparison.OrdinalIgnoreCase));
            if (original == null || ReferenceEquals(original, job.Assembly)) return;
            original.ExportState = job.Assembly.ExportState; original.ExportHash = job.Assembly.ExportHash; original.ExportPath = job.Assembly.ExportPath;
        }

        public static List<BatchExportJob> ExportJobs(WorkflowSession session, string folder, IEnumerable<AssemblyUpdate> visibleScope = null)
        {
            if (session == null) throw new IOException("请选择要导出的本地总装。");
            var scopePaths = visibleScope == null ? null : new HashSet<string>(visibleScope.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
            var selected = session.Assemblies.Where(a => a.Selected && (scopePaths == null || scopePaths.Contains(a.Path))).Select(a => a.Path).ToList();
            return ManualNwcExport.Plan(selected, folder, null);
        }

        public static void VerifyExportFiles(AssemblyUpdate assembly)
        {
            if (assembly.DrawingStamp != null && DetectionCache.Stamp(assembly.Path) != assembly.DrawingStamp) throw new IOException("总装 DWG 在检查后发生变化，请重新检查：" + assembly.Path);
            foreach (var pair in assembly.ExpectedHashes)
                if (Files.Hash(pair.Key) != pair.Value) throw new IOException("本地 Xref 又有变化，请重新检查并 Pull：" + pair.Key);
            foreach (var pair in assembly.ExpectedCloudHashes)
                if (Files.Hash(pair.Key) != pair.Value) throw new IOException("Connector 文件又有变化，请重新检查并 Pull：" + pair.Key);
        }

        public static void VerifyExportGraph(AssemblyUpdate assembly, ScanResult fresh)
        {
            if (fresh.Warnings.Count > 0) throw new IOException("总装引用扫描存在未解决诊断，不能确认导出完整：" + string.Join(" | ", fresh.Warnings));
            var before = new HashSet<string>(assembly.Scan.Rows.Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase);
            if (!before.SetEquals(fresh.Rows.Select(r => r.LocalPath))) throw new IOException("总装 Xref 依赖有新增或移除，请重新检查后导出。");
        }
    }

    internal sealed class BatchExportJob
    {
        public AssemblyUpdate Assembly { get; set; }
        public string Target { get; set; }
        public bool OpenedByPlugin { get; set; }
        public bool ManualSelection { get; set; }
        // 交互导出按当前宿主状态执行；周更默认 false，继续使用原校验流程。
        public bool CurrentLocal { get; set; }
    }
}
