using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    public sealed class TransferPlan
    {
        public XrefRow Row { get; set; }
        public string Local { get; set; }
        public string Relative { get; set; }
        public string ConnectorPath { get; set; }
        public string LocalHash { get; set; }
        public string ConnectorHash { get; set; }
        public DateTime? LocalModifiedUtc { get; set; }
        public DateTime? ConnectorModifiedUtc { get; set; }
        public DateTime? AfterLocalModifiedUtc { get; set; }
        public bool DateComparison { get; set; }
        public bool IsUnchanged => Action == "内容相同，跳过" || Action == "日期相同，跳过";
        public string Blocked { get; set; }
        public string Action { get; set; }
        public string Key { get; set; }
        public bool Written { get; set; }
        public bool Verified { get; set; }
        public bool RequireExistingLocal { get; set; }
        public string BackupPath { get; set; }
        public List<string> AffectedAssemblies { get; set; } = new List<string>();
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
    }

    // 缓存只用于检测提速，真正复制前及复制后仍采用完整哈希校验。
    public sealed class DetectionCache
    {
        private readonly Dictionary<string, Tuple<string, string>> hashes = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
        public int Hits { get; private set; }
        public int Reads { get; private set; }
        public void Clear() { lock (hashes) hashes.Clear(); }
        internal static string Stamp(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.CreationTimeUtc.Ticks : null;
        }
        public string Hash(string path)
        {
            var key = Paths.Canonical(path); var stamp = Stamp(key);
            lock (hashes) if (stamp != null && hashes.TryGetValue(key, out var entry) && entry.Item1 == stamp) { Hits++; return entry.Item2; }
            var hash = Files.Hash(key); Reads++;
            var after = Stamp(key);
            if (stamp != after) throw new IOException("文件在检查期间发生变化，请稍后重新检查：" + key);
            lock (hashes) { if (stamp != null) hashes[key] = Tuple.Create(stamp, hash); else hashes.Remove(key); }
            return hash;
        }
        public bool TryGetHash(string path, out string hash)
        {
            var key = Paths.Canonical(path); var stamp = Stamp(key); hash = null;
            lock (hashes)
                if (stamp != null && hashes.TryGetValue(key, out var entry) && entry.Item1 == stamp) { hash = entry.Item2; return true; }
            return false;
        }
    }

    public sealed class UpdateJournal
    {
        private readonly List<TransferPlan> plans;
        private readonly string connector, direction, scope;
        private readonly DateTime started = DateTime.UtcNow;
        public string Path { get; }
        public string DetailPath => System.IO.Path.ChangeExtension(Path, ".json");
        public string EventPath => System.IO.Path.ChangeExtension(Path, ".jsonl");
        internal UpdateJournal(string directory, FolderLink folder, List<TransferPlan> items, bool push, string localScope)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".md");
            plans = items; connector = folder.Project; direction = push ? "Push" : "Pull"; scope = localScope;
            foreach (var p in plans) { p.Written = p.Verified = false; p.StartedUtc = p.FinishedUtc = p.AfterLocalModifiedUtc = null; p.BackupPath = null; }
            Write("准备执行", null);
        }
        public void Write(string state, string error)
        {
            var snapshot = new { startedUtc = started, recordedUtc = DateTime.UtcNow, direction, state, error, connectorRoot = connector, localScope = scope, events = EventPath,
                files = plans.Select(p => new { local = p.Local, connector = p.ConnectorPath, match = p.Row.MatchStatus, comparison = p.DateComparison ? "修改日期；云端为准" : "内容", localModifiedUtc = p.LocalModifiedUtc, connectorModifiedUtc = p.ConnectorModifiedUtc, beforeLocalHash = p.LocalHash, beforeConnectorHash = p.ConnectorHash,
                    afterLocalModifiedUtc = p.AfterLocalModifiedUtc, timestampSource = "Desktop Connector 文件 LastWriteTimeUtc；非下载时间，非 APS 云端元数据", backupPolicy = direction == "Pull" ? "Xref 覆盖不备份" : "原 Push 规则",
                    expectedAfterHash = direction == "Pull" && !p.IsUnchanged ? p.ConnectorHash : p.LocalHash, backupPath = p.BackupPath, written = p.Written, verified = p.Verified, startedUtc = p.StartedUtc, finishedUtc = p.FinishedUtc, affectedAssemblies = p.AffectedAssemblies,
                    result = !p.StartedUtc.HasValue ? "未执行" : p.Row.Result ?? p.Blocked ?? "执行中" }) };
            var text = new StringBuilder();
            text.AppendLine("# Plant Model Sync 更新记录").AppendLine().AppendLine("方向：" + direction).AppendLine("开始时间：" + started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .AppendLine("记录时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).AppendLine("批次状态：" + state).AppendLine("云端同步根目录：" + connector)
                .AppendLine("本地总装范围：" + scope).AppendLine("确认写入并校验：" + plans.Count(p => p.Written && p.Verified) + " / " + plans.Count).AppendLine("批次异常：" + (error ?? "无"))
                .AppendLine("逐项事件日志：" + EventPath).AppendLine("若程序意外退出未生成最终报告，请核对上述逐项事件；未结束批次不能视为全部完成。").AppendLine();
            foreach (var p in plans)
            {
                text.AppendLine("## " + System.IO.Path.GetFileName(p.Local)).AppendLine().AppendLine("云端源：" + p.ConnectorPath).AppendLine("本地目标：" + p.Local)
                    .AppendLine("匹配依据：" + p.Row.MatchStatus).AppendLine("比较规则：" + (p.DateComparison ? "修改日期；云端为准" : "内容")).AppendLine("本地修改时间 UTC：" + p.LocalModifiedUtc?.ToString("o")).AppendLine("Connector 修改时间 UTC：" + p.ConnectorModifiedUtc?.ToString("o")).AppendLine("更新前本地 SHA-256：" + p.LocalHash).AppendLine("更新前 Connector SHA-256：" + p.ConnectorHash)
                    .AppendLine("更新后本地修改时间 UTC：" + p.AfterLocalModifiedUtc?.ToString("o")).AppendLine("时间来源：Connector 文件修改时间，不是下载时间；不读取 APS 云端元数据。")
                    .AppendLine("已写入：" + p.Written + "；校验通过：" + p.Verified).AppendLine("备份路径：" + (p.BackupPath ?? (direction == "Pull" ? "不备份 Xref 覆盖" : "无 / 尚未替换")))
                    .AppendLine("结果：" + (!p.StartedUtc.HasValue ? "未执行" : p.Row.Result ?? p.Blocked ?? "执行中")).AppendLine("受影响总装：");
                foreach (var a in p.AffectedAssemblies) text.AppendLine("- " + a);
                text.AppendLine();
            }
            AtomicWrite(DetailPath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            AtomicWrite(Path, text.ToString());
        }
        private static void AtomicWrite(string target, string text)
        {
            var temp = target + ".tmp"; File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
        }
    }

    public sealed class SyncService
    {
        private readonly Settings settings;
        private readonly Action save;
        private readonly string storage;
        internal string StorageDirectory => storage;
        public SyncService(Settings config, Action saveConfig = null, string storageDirectory = null)
        { settings = config; save = saveConfig ?? config.Save; storage = storageDirectory ?? Settings.DataDirectory; }

        public Task<List<TransferPlan>> Plan(FolderLink folder, IEnumerable<XrefRow> selection, bool push, CancellationToken ct, Action<string> progress, DetectionCache cache = null, Action<int, int, string> itemProgress = null)
        {
            progress?.Invoke("检查 Desktop Connector 本地同步目录……");
            var result = new List<TransferPlan>();
            var selectedRows = selection.ToList(); int completed = 0;
            foreach (var row in selectedRows)
            {
                ct.ThrowIfCancellationRequested();
                itemProgress?.Invoke(completed, selectedRows.Count, row.LocalPath);
                row.LocalPath = Paths.Canonical(row.LocalPath);
                var plan = new TransferPlan { Row = row, Local = row.LocalPath, Relative = row.CloudPath };
                result.Add(plan);
                try
                {
                    if (!push && !string.IsNullOrWhiteSpace(settings.LocalModelsRoot) && (!Paths.IsWithin(settings.LocalModelsRoot, plan.Local) || Paths.IsHistory(plan.Local)))
                        throw new IOException("Pull 目标不在本地模型根目录内，或属于只读历史目录，已阻止写入。");
                    plan.ConnectorPath = Paths.SyncTarget(folder.Project, row.CloudPath);
                    plan.Relative = Path.IsPathRooted(row.CloudPath.Trim()) ? plan.ConnectorPath : Paths.CloudRelative(row.CloudPath);
                    progress?.Invoke((push ? "比较内容：" : "比较日期：") + row.Name);
                    plan.DateComparison = !push;
                    plan.LocalModifiedUtc = ModifiedUtc(plan.Local);
                    plan.ConnectorModifiedUtc = ModifiedUtc(plan.ConnectorPath);
                    plan.LocalHash = cache == null ? Files.Hash(plan.Local) : cache.Hash(plan.Local);
                    plan.ConnectorHash = cache == null ? Files.Hash(plan.ConnectorPath) : cache.Hash(plan.ConnectorPath);
                    if (string.Equals(Paths.Canonical(plan.Local), plan.ConnectorPath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("本地源文件与 Desktop Connector 文件是同一路径，无需复制。");
                    if (push && plan.LocalHash == "") throw new IOException("本地源文件缺失。");
                    if (!push && plan.ConnectorHash == "") throw new IOException("同步目录中没有此文件；请先确认 Desktop Connector 已下载该文件。");
                    if (push) Files.CheckDwg(plan.Local); else Files.CheckDwg(plan.ConnectorPath);
                    plan.Key = Settings.SyncKey(folder, plan.Local, plan.ConnectorPath);
                    settings.Baselines.TryGetValue(plan.Key, out var baseline);
                    // 兼容旧版相对路径基准，切换工作流不能丢失双方变更检测。
                    if (baseline == null)
                    {
                        var legacy = Paths.Relative(folder.Project, plan.ConnectorPath);
                        if (!string.IsNullOrEmpty(legacy)) settings.Baselines.TryGetValue(Settings.SyncKey(folder, plan.Local, legacy), out baseline);
                    }
                    var conflict = Files.Conflict(baseline, plan.LocalHash, plan.ConnectorHash);
                    if (push && conflict.Length != 0 && plan.LocalHash != plan.ConnectorHash) throw new IOException(conflict);
                    if (ModifiedUtc(plan.Local) != plan.LocalModifiedUtc || ModifiedUtc(plan.ConnectorPath) != plan.ConnectorModifiedUtc) throw new IOException("文件日期在检查期间改变，请重新检查。");
                    plan.Action = push ? (plan.LocalHash == plan.ConnectorHash ? "内容相同，跳过" : plan.ConnectorHash == "" ? "复制到同步目录" : "更新同步目录文件")
                        : !plan.LocalModifiedUtc.HasValue ? "复制到本地" : plan.LocalModifiedUtc == plan.ConnectorModifiedUtc ? "日期相同，跳过" : "日期不同，以云端覆盖（不备份）";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { plan.Blocked = ex.Message; plan.Action = "阻止"; }
                itemProgress?.Invoke(++completed, selectedRows.Count, row.LocalPath);
            }
            foreach (var duplicate in result.Where(p => p.Blocked == null).GroupBy(p => push ? p.ConnectorPath : p.Local, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var plan in duplicate) { plan.Blocked = "多个来源将覆盖同一目标路径，请修正此行映射。"; plan.Action = "阻止"; }
            foreach (var plan in result) plan.Row.SyncState = plan.Blocked != null ? SyncFeedback.Review : plan.IsUnchanged ? (push ? SyncFeedback.Synced : SyncFeedback.DateMatched) : SyncFeedback.Pending;
            return Task.FromResult(result);
        }

        public UpdateJournal BeginUpdateLog(FolderLink folder, List<TransferPlan> plans, bool push, string scope = null) => new UpdateJournal(Path.Combine(storage, "Logs", "Updates"), folder, plans, push, scope);

        public async Task<string> Execute(FolderLink folder, List<TransferPlan> plans, bool push, CancellationToken ct, Action<string> progress, UpdateJournal journal = null)
        {
            journal = journal ?? BeginUpdateLog(folder, plans, push);
            string log = journal.EventPath;
            string outcome = "完成", batchError = null;
            try
            {
            journal.Write("执行中", null);
            foreach (var plan in plans)
            {
                string staged = null;
                try
                {
                    plan.StartedUtc = DateTime.UtcNow;
                    ct.ThrowIfCancellationRequested();
                    if (plan.Blocked != null) { plan.Row.Result = "跳过：" + plan.Blocked; continue; }
                    if (!push && plan.RequireExistingLocal && !File.Exists(plan.Local)) throw new IOException("已启用仅更新现有文件，目标已不存在；未创建文件，请重新检查。");
                    if (!push && plan.DateComparison && (ModifiedUtc(plan.Local) != plan.LocalModifiedUtc || ModifiedUtc(plan.ConnectorPath) != plan.ConnectorModifiedUtc)) throw new IOException("文件日期在预览后改变，请重新预览。");
                    if (plan.Action == "日期相同，跳过")
                    {
                        // 日期相同不证明字节一致，不写入内容相同的收据或更改基准。
                        if (Files.Hash(plan.Local) != plan.LocalHash || Files.Hash(plan.ConnectorPath) != plan.ConnectorHash) throw new IOException("文件在预览后改变，请重新预览。");
                        plan.Verified = true; plan.Row.SyncState = SyncFeedback.DateMatched; plan.Row.Result = "日期相同，未复制（不代表内容相同）"; continue;
                    }
                    if (plan.Action == "内容相同，跳过")
                    {
                        if (!SyncFeedback.Record(settings, folder, plan.Row, plan.ConnectorPath, plan.LocalHash)) throw new IOException("文件在校验过程中发生改变，请重新预览。");
                        plan.Verified = true;
                        settings.Baselines[plan.Key] = new Baseline { Hash = plan.LocalHash, Version = plan.ConnectorHash };
                        save(); plan.Row.Result = plan.Action; continue;
                    }
                    plan.Row.SyncState = SyncFeedback.Processing;
                    progress?.Invoke((push ? "Push " : "Pull ") + plan.Relative);
                    if (Files.Hash(plan.Local) != plan.LocalHash || Files.Hash(plan.ConnectorPath) != plan.ConnectorHash)
                        throw new IOException("源文件或同步文件在预览后发生改变，请重新预览。");
                    if (push)
                    {
                        Files.AssertClosed(plan.ConnectorPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(plan.ConnectorPath));
                        staged = Path.Combine(Path.GetDirectoryName(plan.ConnectorPath), ".pms-" + Guid.NewGuid().ToString("N") + ".tmp");
                        await CopyAndCheck(plan.Local, staged, plan.LocalHash, ct).ConfigureAwait(false);
                        if (Files.Hash(plan.ConnectorPath) != plan.ConnectorHash) throw new IOException("同步目录文件在写入期间发生改变，请重新预览。");
                        if (File.Exists(plan.ConnectorPath)) File.Replace(staged, plan.ConnectorPath, null); else File.Move(staged, plan.ConnectorPath);
                        staged = null;
                        plan.Written = true;
                        var hash = Files.Hash(plan.ConnectorPath);
                        if (hash != plan.LocalHash) throw new IOException("写入同步目录后的文件校验失败。");
                        settings.Baselines[plan.Key] = new Baseline { Hash = hash, Version = hash };
                        plan.Row.Result = "已写入 Desktop Connector；云端同步由 Connector 处理。";
                    }
                    else
                    {
                        Files.AssertClosed(plan.Local);
                        Directory.CreateDirectory(Path.GetDirectoryName(plan.Local));
                        staged = Path.Combine(Path.GetDirectoryName(plan.Local), ".pms-" + Guid.NewGuid().ToString("N") + ".tmp");
                        await CopyAndCheck(plan.ConnectorPath, staged, plan.ConnectorHash, ct).ConfigureAwait(false);
                        if (Files.Hash(plan.ConnectorPath) != plan.ConnectorHash) throw new IOException("Desktop Connector 同步文件在读取期间发生改变，请重新预览。");
                        if (plan.DateComparison)
                        {
                            if (ModifiedUtc(plan.ConnectorPath) != plan.ConnectorModifiedUtc || ModifiedUtc(plan.Local) != plan.LocalModifiedUtc) throw new IOException("文件日期在复制期间改变，请重新预览。");
                            // 保留云端源时间，避免下一次检查将复制时间误判为更新。
                            File.SetLastWriteTimeUtc(staged, plan.ConnectorModifiedUtc.Value);
                        }
                        ct.ThrowIfCancellationRequested();
                        Files.InstallDownload(staged, plan.Local, plan.LocalHash, backupExisting: false);
                        staged = null;
                        plan.Written = true;
                        if (plan.ConnectorModifiedUtc.HasValue) Files.PreserveModifiedUtc(plan.Local, plan.ConnectorModifiedUtc.Value);
                        plan.AfterLocalModifiedUtc = ModifiedUtc(plan.Local);
                        settings.Baselines[plan.Key] = new Baseline { Hash = plan.ConnectorHash, Version = plan.ConnectorHash };
                        plan.Row.Result = "Pull 完成（Xref 不备份；保留源修改时间）";
                    }
                    if (!SyncFeedback.Record(settings, folder, plan.Row, plan.ConnectorPath, push ? plan.LocalHash : plan.ConnectorHash))
                        throw new IOException("复制后文件又发生变化，请重新核对；未标记为已同步。");
                    plan.Verified = true;
                    save();
                    progress?.Invoke("已同步：" + plan.Row.Name);
                }
                catch (OperationCanceledException) { plan.Row.SyncState = SyncFeedback.Pending; plan.Row.Result = "已取消；已完成的文件保留。请检查结果列和日志。"; throw; }
                catch (Exception ex) { plan.Row.SyncState = SyncFeedback.Failed; plan.Row.Result = "失败 / 待核对：" + ex.Message; }
                finally
                {
                    if (staged != null && File.Exists(staged)) File.Delete(staged);
                    plan.FinishedUtc = DateTime.UtcNow;
                    if (!push && plan.Written) plan.AfterLocalModifiedUtc = ModifiedUtc(plan.Local);
                    File.AppendAllText(log, JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, direction = push ? "Push" : "Pull", local = plan.Local, connector = plan.ConnectorPath, connectorModifiedUtc = plan.ConnectorModifiedUtc, afterLocalModifiedUtc = plan.AfterLocalModifiedUtc, downloadFinishedUtc = !push && plan.Written ? plan.FinishedUtc : null, beforeLocalHash = plan.LocalHash, beforeConnectorHash = plan.ConnectorHash, backup = plan.BackupPath, assemblies = plan.AffectedAssemblies, written = plan.Written, verified = plan.Verified, result = plan.Row.Result }) + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            if (plans.Any(p => p.Row.SyncState == SyncFeedback.Failed || p.Blocked != null)) outcome = "完成，含失败 / 阻止项";
            }
            catch (OperationCanceledException) { outcome = "已取消；已完成文件保留"; throw; }
            catch (Exception ex) { outcome = "异常中止"; batchError = ex.Message; throw; }
            finally { journal.Write(outcome, batchError); }
            return journal.Path;
        }

        private static async Task CopyAndCheck(string source, string target, string expectedHash, CancellationToken ct)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await input.CopyToAsync(output, 1024 * 1024, ct).ConfigureAwait(false);
            if (Files.Hash(target) != expectedHash) throw new IOException("文件复制后校验失败。");
        }

        internal static DateTime? ModifiedUtc(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
    }
}
