using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IWMF.PlantModelSync
{
    internal sealed class ReferenceComparison
    {
        public string Name { get; set; }
        public string Local { get; set; }
        public string Connector { get; set; }
        public string State { get; set; }
        public string LocalHash { get; set; }
        public string CloudHash { get; set; }
        public string LocalDate { get; set; }
        public string CloudDate { get; set; }
        public bool Equal { get; set; }
    }
    internal sealed class AssemblyNwcReviewRow
    {
        public bool Selected { get; set; }
        public string Key { get; set; }
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string LocalState { get; set; }
        public string CloudSummary { get; set; }
        public string Advice { get; set; }
        public string Reason { get; set; }
        public string NwcPath { get; set; }
        public string NwcDate { get; set; }
        public bool CanExport { get; set; }
        public bool NeedsExport { get; set; }
        public List<ReferenceComparison> References { get; set; } = new List<ReferenceComparison>();
        internal Dictionary<string, string> Snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> CloudHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal AssemblyUpdate Assembly;
    }
    internal sealed class NwcExportReceipt
    {
        public string Assembly { get; set; }
        public string Signature { get; set; }
        public string Output { get; set; }
        public string OutputHash { get; set; }
        public string ExportedUtc { get; set; }
    }
    // 只有完整源快照和实际 NWC 哈希同时匹配才承认已有对应导出，日期不用于猜测版本。
    internal static class NwcExportHistory
    {
        internal static void Record(string storage, BatchExportJob job)
        {
            // 当前模型导出未核验完整依赖，不生成已同步或完整快照收据。
            if (job.CurrentLocal) return;
            if (job.Assembly.ExportState != "已导出" || string.IsNullOrEmpty(job.Assembly.ExportHash) || Files.Hash(job.Target) != job.Assembly.ExportHash) return;
            if (!job.Assembly.ExpectedHashes.ContainsKey(job.Assembly.Path) || job.Assembly.ExpectedHashes.Any(p => string.IsNullOrEmpty(p.Value))) throw new IOException("缺少完整导出源快照，未保存 NWC 基准。");
            var folder = System.IO.Path.Combine(storage, "NwcReceipts"); Directory.CreateDirectory(folder);
            WeeklyUpdate.AtomicJson(System.IO.Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json"), new NwcExportReceipt { Assembly = job.Assembly.Path, Signature = WeeklyUpdate.Signature(job.Assembly.ExpectedHashes), Output = job.Target, OutputHash = job.Assembly.ExportHash, ExportedUtc = DateTime.UtcNow.ToString("o") });
        }
        internal static List<NwcExportReceipt> Read(string storage, List<string> warnings)
        {
            var result = new List<NwcExportReceipt>();
            void ReadFile(string path, Action<JToken> read)
            {
                try { read(JToken.Parse(File.ReadAllText(path, Encoding.UTF8))); }
                catch (Exception ex) { warnings.Add("导出基准无法读取：" + path + "：" + ex.Message); }
            }
            var folder = System.IO.Path.Combine(storage, "NwcReceipts");
            foreach (var file in Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : new string[0]) ReadFile(file, t => result.Add(t.ToObject<NwcExportReceipt>()));
            var ledger = System.IO.Path.Combine(storage, "weekly-ledger.json");
            if (File.Exists(ledger)) ReadFile(ledger, t =>
            {
                foreach (var item in (t["Exports"] as JObject ?? new JObject()).Properties()) result.Add(new NwcExportReceipt { Assembly = item.Name, Signature = (string)item.Value["Signature"], Output = (string)item.Value["Output"], OutputHash = (string)item.Value["OutputHash"] });
            });
            // 旧日志仅在包含总装本体和完整依赖快照时复用；不从文件日期推断导出版本。
            var logs = System.IO.Path.Combine(storage, "Logs");
            foreach (var file in Directory.Exists(logs) ? Directory.GetFiles(logs, "batch-nwc-*.json", SearchOption.TopDirectoryOnly) : new string[0]) ReadFile(file, t =>
            {
                foreach (var item in t as JArray ?? new JArray())
                {
                    var assembly = (string)item["assembly"]; var hashes = item["expectedHashes"]?.ToObject<Dictionary<string, string>>();
                    if ((string)item["result"] != "已导出" || assembly == null || hashes == null || !hashes.Keys.Contains(assembly, StringComparer.OrdinalIgnoreCase) || hashes.Any(p => string.IsNullOrEmpty(p.Value))) continue;
                    result.Add(new NwcExportReceipt { Assembly = assembly, Signature = WeeklyUpdate.Signature(hashes), Output = (string)item["target"], OutputHash = (string)item["outputHash"] });
                }
            });
            return result.Where(r => r != null && !string.IsNullOrEmpty(r.Assembly)).ToList();
        }
    }
    internal static class AssemblyNwcReview
    {
        private static string Date(string path) => File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz") : "不存在";
        internal static List<AssemblyNwcReviewRow> Check(WorkflowSession session, string storage, CancellationToken ct)
        {
            var warnings = new List<string>(); var receipts = NwcExportHistory.Read(storage, warnings);
            var localized = new Dictionary<string, WeeklySourceReceipt>(StringComparer.OrdinalIgnoreCase);
            var ledgerPath = System.IO.Path.Combine(storage, "weekly-ledger.json");
            if (File.Exists(ledgerPath))
                try { foreach (var p in JsonConvert.DeserializeObject<WeeklyLedger>(File.ReadAllText(ledgerPath, Encoding.UTF8)).Sources) localized[p.Key] = p.Value; }
                catch (Exception ex) { warnings.Add("本地化源基准无法读取：" + ex.Message); }
            var cache = session.HashCache; var rows = new List<AssemblyNwcReviewRow>();
            var plans = session.Sources.SelectMany(s => s.Plans.Select(p => new { Source = s, Plan = p })).GroupBy(x => x.Plan.Local, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var matcher = new SystemMatcher(session.Sources.Select(s => s.Path));
            var allReferences = new HashSet<string>(session.Assemblies.SelectMany(a => a.Scan.Rows).Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase);
            foreach (var audit in session.AssemblyAudit)
            {
                ct.ThrowIfCancellationRequested();
                var row = new AssemblyNwcReviewRow { Key = audit.Key, Path = audit.Path, LocalState = audit.State, CloudSummary = "尚未比较", Advice = "需处理，暂不能导出" }; rows.Add(row);
                row.Assembly = session.Assemblies.FirstOrDefault(a => string.Equals(a.Path, audit.Path, StringComparison.OrdinalIgnoreCase));
                if (audit.Matches.Count != 1 || row.Assembly == null) { row.Reason = audit.Diagnostics + " " + audit.State; continue; }
                try
                {
                    var assembly = row.Assembly;
                    foreach (var local in assembly.Scan.Rows.Select(r => r.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        var reference = new ReferenceComparison { Name = System.IO.Path.GetFileName(local), Local = local, LocalDate = Date(local), State = "未找到云端对应" }; row.References.Add(reference);
                        try
                        {
                            reference.LocalHash = cache.Hash(local);
                            plans.TryGetValue(local, out var matches);
                            var cloudPaths = matches == null ? new List<string>() : matches.Select(x => x.Plan.ConnectorPath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            var ambiguous = session.Sources.Where(s => s.AmbiguousLocals.Contains(local, StringComparer.OrdinalIgnoreCase)).ToList();
                            if (ambiguous.Count > 0 || cloudPaths.Count > 1) { reference.Connector = string.Join(" | ", cloudPaths.Concat(ambiguous.Select(s => s.Path))); reference.State = "云端对应有歧义，需确认"; continue; }
                            // 仅补充精确镜像路径，不按全项目同名文件猜配。
                            if (cloudPaths.Count == 0 && Paths.IsWithin(session.LocalModelsRoot, local) && !Paths.IsHistory(local))
                            {
                                var relative = Paths.Relative(session.LocalModelsRoot, local);
                                if (relative.StartsWith("local_", StringComparison.OrdinalIgnoreCase)) relative = relative.Substring(6);
                                var exact = Paths.UnderRoot(session.ConnectorRoot, relative);
                                cloudPaths.Add(exact);
                            }
                            if (cloudPaths.Count == 0) continue;
                            reference.Connector = Paths.SyncTarget(session.ConnectorRoot, cloudPaths[0]);
                            reference.CloudDate = Date(reference.Connector); reference.CloudHash = cache.Hash(reference.Connector);
                            if (string.IsNullOrEmpty(reference.CloudHash)) { reference.State = "Connector 文件不存在或尚未可用"; continue; }
                            Files.CheckDwg(reference.Connector);
                            var blocked = matches?.Select(x => x.Plan.Blocked).FirstOrDefault(b => b != null);
                            var diagnostics = matches?.SelectMany(x => x.Source.Diagnostics).Distinct().ToList();
                            if (diagnostics != null && diagnostics.Count > 0) { reference.State = "云端对应需处理：" + string.Join(" | ", diagnostics); continue; }
                            if (blocked != null) { reference.State = "需处理：" + blocked; continue; }
                            reference.Equal = !string.IsNullOrEmpty(reference.LocalHash) && SyncService.ModifiedUtc(local) == SyncService.ModifiedUtc(reference.Connector);
                            reference.State = reference.Equal ? "日期相同（不代表内容相同）" : string.IsNullOrEmpty(reference.LocalHash) ? "本地缺失，需 Pull" : "日期不同，需 Pull（云端为准）";
                            row.CloudHashes[reference.Connector] = reference.CloudHash;
                        }
                        catch (Exception ex) { reference.State = "对比失败：" + ex.Message; }
                    }
                    var different = row.References.Count(r => !r.Equal);
                    row.CloudSummary = "引用 " + row.References.Count + "；一致 " + row.References.Count(r => r.Equal) + "；待处理 " + different;
                    var issues = new List<string>(assembly.Scan.Warnings);
                    if (assembly.RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal)) issues.Add(assembly.RefreshState);
                    if (session.PendingReferenceAdds.TryGetValue(assembly.Path, out var waiting) && waiting.Count > 0) issues.Add("新增引用未完成：" + string.Join(" | ", waiting));
                    issues.AddRange(row.References.Where(r => !r.Equal).Select(r => r.Name + "：" + r.State));
                    // 未引用文件不能因未进入本地依赖图而被报告为“云端全部相同”。不自动猜测挂接。
                    var codes = matcher.Systems(assembly.Path);
                    var candidates = session.Sources.Where(s => !s.Plans.Any(p => allReferences.Contains(p.Local)) && s.AmbiguousLocals.Count == 0 && matcher.Systems(s.Path).Overlaps(codes)).ToList();
                    if (candidates.Count > 0)
                    {
                        issues.Add("同系统云端未引用候选需确认是否加入总装：" + string.Join(" | ", candidates.Select(s => s.Path)));
                        foreach (var candidate in candidates) row.References.Add(new ReferenceComparison { Name = candidate.Name, Connector = candidate.Path, Local = candidate.ExpectedLocalPath, CloudDate = Date(candidate.Path), State = "云端新增候选，未确认加入此总装" });
                        row.CloudSummary += "；新增候选 " + candidates.Count;
                    }
                    foreach (var path in assembly.Scan.Rows.Select(r => r.LocalPath).Append(assembly.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested(); var hash = cache.Hash(path);
                        if (string.IsNullOrEmpty(hash)) issues.Add("本地文件不存在：" + path); else row.Snapshot[path] = hash;
                    }
                    if (issues.Count > 0)
                    {
                        row.NeedsExport = different > 0 || candidates.Count > 0;
                        row.Advice = different > 0 && row.References.Any(r => (r.State ?? "").Contains("Pull")) ? "先 Pull / 处理后再导出" : "需处理，暂不能导出";
                        row.Reason = string.Join("\r\n", issues); continue;
                    }
                    var signature = WeeklyUpdate.Signature(row.Snapshot);
                    var history = receipts.Where(r => string.Equals(r.Assembly, assembly.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                    var matching = history.Where(r => r.Signature == signature).ToList();
                    NwcExportReceipt valid = null;
                    foreach (var receipt in matching)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(receipt.Output) || string.IsNullOrEmpty(receipt.OutputHash)) continue;
                        try { if (cache.Hash(receipt.Output) == receipt.OutputHash) { valid = receipt; break; } } catch { }
                    }
                    row.CanExport = true; row.NeedsExport = valid == null;
                    row.Advice = valid != null ? "已有匹配 NWC，无需重导" : "需要导出 NWC";
                    row.NwcPath = valid?.Output ?? history.FirstOrDefault()?.Output;
                    row.NwcDate = string.IsNullOrEmpty(row.NwcPath) ? "无可靠成功记录" : Date(row.NwcPath);
                    row.Reason = valid != null ? "总装及全部依赖快照、NWC 文件哈希均匹配。是否上传由你手动确认，插件不宣称云端 NWC 已更新。" : matching.Count > 0 ? "已有源快照相符的导出记录，但输出缺失、不可读或内容已改变。" : history.Count > 0 ? "本地总装或 Xref 相比成功导出快照发生变化。" : "没有包含总装本体和全部引用的可靠成功导出记录；即使云端与本地一致也建议导出一次建立基准。";
                    if (warnings.Count > 0) row.Reason += "\r\n" + string.Join("\r\n", warnings);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { row.CanExport = false; row.Advice = "对比失败，暂不能导出"; row.Reason = ex.Message; }
            }
            return rows;
        }
        internal static List<BatchExportJob> Jobs(IEnumerable<AssemblyNwcReviewRow> selected, string output)
        {
            // 对比结论仅供参考；手动导出不再锁定检查时的云端或本地快照。
            return ManualNwcExport.Plan(selected.Select(r => r.Path), output, null);
        }
        internal static string SaveReport(string storage, WorkflowSession session, List<AssemblyNwcReviewRow> rows)
        {
            var path = System.IO.Path.Combine(storage, "Logs", "AssemblyComparisons", "comparison-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            var time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz");
            var reportRows = rows.Select(r => new UpdateReportRow { Time = time, Operation = "云端本地 / NWC 检查", File = r.Name, Local = r.Path, Connector = string.Join(" | ", r.References.Select(x => x.Connector).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)), Assembly = r.Path, State = r.Advice, Details = "Connector 根目录：" + session.ConnectorRoot + "\r\n" + r.CloudSummary + "\r\n" + r.Reason + "\r\n已有 NWC：" + r.NwcPath + "\r\n" + string.Join("\r\n", r.References.Select(x => x.Local + " ⇐ " + x.Connector + "；" + x.State + "；本地SHA=" + x.LocalHash + "；云端SHA=" + x.CloudHash)) }).ToList();
            WeeklyUpdate.AtomicJson(path, new { Format = "IWMF.OneClickUpdate.v1", State = "只读比较完成；未 Pull、未导出、未上传", ConnectorRoot = session.ConnectorRoot, Rows = reportRows }); return path;
        }
    }
}
