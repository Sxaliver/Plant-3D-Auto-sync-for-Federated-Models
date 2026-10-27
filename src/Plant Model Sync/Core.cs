using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    public sealed class FolderLink
    {
        public string Project { get; set; }
        public string Folder { get; set; }
        public static FolderLink Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !Path.IsPathRooted(text.Trim()) || !Directory.Exists(text.Trim()))
                throw new ArgumentException("请选择已存在的 Desktop Connector 本地同步文件夹。");
            var path = Paths.Canonical(text.Trim());
            return new FolderLink { Project = path, Folder = Path.GetFileName(path) };
        }
        public string Key => Project;
    }

    public static class Paths
    {
        private static KeyValuePair<string, string>[] aliases = new KeyValuePair<string, string>[0];
        // 目录迁移保留旧名称链接；统一逻辑路径，避免旧、新名称被识别成两份工作文件。
        public static void ConfigureAliases(IDictionary<string, string> mappings)
        {
            aliases = (mappings ?? new Dictionary<string, string>()).Select(p => new KeyValuePair<string, string>(Path.GetFullPath(p.Key).TrimEnd('\\'), Path.GetFullPath(p.Value).TrimEnd('\\'))).OrderByDescending(p => p.Key.Length).ToArray();
        }
        public static string Canonical(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var pair in aliases)
                if (string.Equals(full, pair.Key, StringComparison.OrdinalIgnoreCase) || full.StartsWith(pair.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return pair.Value + full.Substring(pair.Key.Length);
            return full;
        }
        public static bool IsWithin(string root, string file) => Canonical(file).StartsWith(Canonical(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        public static bool IsHistory(string path) => Canonical(path).Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith("local_history_", StringComparison.OrdinalIgnoreCase));
        public static string MirrorTarget(string localRoot, string cloudRoot, string cloudFile)
        {
            var relative = Relative(cloudRoot, cloudFile);
            if (string.IsNullOrEmpty(localRoot) || string.IsNullOrEmpty(relative)) return null;
            var parts = relative.Split('/');
            var target = parts.Length == 1 ? Path.Combine(localRoot, parts[0]) : Path.Combine(localRoot, "local_" + parts[0], string.Join("\\", parts.Skip(1)));
            return IsWithin(localRoot, target) ? Canonical(target) : null;
        }
        public static string Relative(string root, string file)
        {
            var prefix = Canonical(root) + Path.DirectorySeparatorChar;
            var full = Canonical(file);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length).Replace('\\', '/') : "";
        }
        public static string CloudRelative(string input)
        {
            var value = (input ?? "").Trim().Replace('\\', '/');
            var parts = value.Split('/');
            if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || !value.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("同步相对路径必须是文件夹内的 DWG 路径，不能包含 ..、空段或绝对路径。");
            return value;
        }
        public static string UnderRoot(string root, string relative)
        {
            var safe = CloudRelative(relative).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(Canonical(root), safe));
            var prefix = Canonical(root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("同步目标超出 Desktop Connector 根目录。");
            return full;
        }
        public static string SyncTarget(string root, string mapping)
        {
            if (string.IsNullOrWhiteSpace(mapping))
                throw new ArgumentException("此 Xref 尚未对应同步 DWG。请选中表格中的这一行，点击“选择对应 DWG”。");
            var value = mapping.Trim();
            if (!Path.IsPathRooted(value)) return UnderRoot(root, value);
            var full = Canonical(value);
            if (!IsWithin(root, full)) throw new ArgumentException("对应文件必须位于当前选择的 Connector 根目录内。");
            if (!full.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("对应的同步文件必须是 DWG。");
            return full;
        }
        public static string Resolve(string owner, string saved)
        {
            if (string.IsNullOrWhiteSpace(saved)) throw new ArgumentException("Xref 路径为空。");
            return Path.GetFullPath(Path.IsPathRooted(saved) ? saved : Path.Combine(Path.GetDirectoryName(owner), saved));
        }
    }

    public sealed class RefEdge
    {
        public string Name { get; set; }
        public string Saved { get; set; }
        public string Resolved { get; set; }
        public bool Overlay { get; set; }
        public bool Unloaded { get; set; }
    }
    public interface IReferenceReader { List<RefEdge> Read(string path); }
    public sealed class XrefRow
    {
        public bool Unloaded { get; set; }
        public List<string> SavedPaths { get; } = new List<string>();
        public List<string> ReferenceNames { get; } = new List<string>();
        public bool Selected { get; set; }
        public string Name { get; set; }
        public string LocalPath { get; set; }
        public string CloudPath { get; set; }
        public string Parents { get; set; }
        public string References { get; set; }
        public string State { get; set; }
        public string Result { get; set; }
        public string MatchStatus { get; set; }
        public string SyncState { get; set; }
        public DateTime? SyncedUtc { get; set; }
        public int Count { get; set; }
    }
    public sealed class ScanResult
    {
        public List<XrefRow> Rows { get; } = new List<XrefRow>();
        public List<XrefRow> DirectRows { get; } = new List<XrefRow>();
        public bool DirectReferencesScanned { get; internal set; }
        public List<string> Warnings { get; } = new List<string>();
        public List<string> DeferredReferences { get; } = new List<string>();
    }
    public static class Scanner
    {
        public static ScanResult Scan(string assembly, string root, IReferenceReader reader)
            => ScanCore(assembly, root, reader, false);

        // 仅新增引用预检使用：卸载边不参与有效依赖图；完整扫描及导出保持原规则。
        public static ScanResult ScanForAttachment(string assembly, string root, IReferenceReader reader)
            => ScanCore(assembly, root, reader, true);

        // 日常同步只追踪会参与当前总装的依赖；总装自身的直接引用单独记录供界面展示。
        public static ScanResult ScanForSynchronization(string assembly, string root, IReferenceReader reader)
            => ScanCore(assembly, root, reader, true, true);

        private static ScanResult ScanCore(string assembly, string root, IReferenceReader reader, bool deferUnloaded, bool honorOverlay = false)
        {
            var result = new ScanResult();
            var rows = new Dictionary<string, XrefRow>(StringComparer.OrdinalIgnoreCase);
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string file, HashSet<string> stack, int depth)
            {
                if (depth > 64) { result.Warnings.Add("嵌套超过 64 层：" + file); return; }
                if (stack.Contains(file)) { result.Warnings.Add("循环引用：" + file); return; }
                if (!expanded.Add(file)) return;
                stack.Add(file);
                try
                {
                    var edges = reader.Read(file);
                    if (depth == 0)
                    {
                        result.DirectReferencesScanned = true;
                        foreach (var edge in edges)
                        {
                            try
                            {
                                var directPath = Paths.Canonical(string.IsNullOrWhiteSpace(edge.Resolved) ? Paths.Resolve(file, edge.Saved) : edge.Resolved);
                                var direct = result.DirectRows.FirstOrDefault(r => string.Equals(r.LocalPath, directPath, StringComparison.OrdinalIgnoreCase));
                                if (direct == null) result.DirectRows.Add(direct = new XrefRow { Name = Path.GetFileName(directPath), LocalPath = directPath, Unloaded = edge.Unloaded, Parents = Path.GetFileName(file), References = edge.Name, State = File.Exists(directPath) ? "本地存在" : "文件缺失" });
                                else direct.Unloaded &= edge.Unloaded;
                                direct.Count++;
                                if (!direct.ReferenceNames.Contains(edge.Name, StringComparer.OrdinalIgnoreCase)) direct.ReferenceNames.Add(edge.Name);
                                var savedPath = Paths.Resolve(file, edge.Saved);
                                if (!direct.SavedPaths.Contains(savedPath, StringComparer.OrdinalIgnoreCase)) direct.SavedPaths.Add(savedPath);
                            }
                            catch { /* 无效路径由下方有效依赖检查或卸载提示统一报告。 */ }
                        }
                    }
                    foreach (var edge in edges)
                    {
                        if (deferUnloaded && edge.Unloaded)
                        {
                            result.DeferredReferences.Add(file + " → " + edge.Name + "：" + edge.Saved + "（已卸载，保持原状态）");
                            continue;
                        }
                        if (honorOverlay && depth > 0 && edge.Overlay)
                        {
                            result.DeferredReferences.Add(file + " → " + edge.Name + "：" + edge.Saved + "（嵌套 Overlay，不随上级传播）");
                            continue;
                        }
                        string child;
                        try { child = Paths.Canonical(string.IsNullOrWhiteSpace(edge.Resolved) ? Paths.Resolve(file, edge.Saved) : edge.Resolved); }
                        catch (Exception ex) { result.Warnings.Add(edge.Name + "：" + ex.Message); continue; }
                        if (!rows.TryGetValue(child, out var row))
                        {
                            row = new XrefRow { Name = Path.GetFileName(child), LocalPath = child, CloudPath = Paths.Relative(root, child), State = File.Exists(child) ? "本地存在" : "文件缺失", Parents = "", References = "", Result = "" };
                            rows.Add(child, row); result.Rows.Add(row);
                        }
                        row.Count++;
                        row.Parents += (row.Parents.Length == 0 ? "" : " | ") + Path.GetFileName(file);
                        row.References += (row.References.Length == 0 ? "" : " | ") + edge.Name + ":" + (edge.Overlay ? "Overlay" : "Attachment") + (edge.Unloaded ? "(卸载)" : "") + "@" + depth;
                        Visit(child, stack, depth + 1);
                    }
                }
                catch (Exception ex) { result.Warnings.Add("不能展开 " + file + "：" + ex.Message); }
                finally { stack.Remove(file); }
            }
            Visit(Paths.Canonical(assembly), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            // 总装自身不因循环引用而成为可传输源文件。
            result.Rows.RemoveAll(r => string.Equals(r.LocalPath, Paths.Canonical(assembly), StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }

    public sealed class Baseline { public string Hash { get; set; } public string Version { get; set; } }
    public sealed class SyncReceipt
    {
        public string ConnectorPath { get; set; }
        public string Hash { get; set; }
        public long LocalLength { get; set; }
        public long LocalModifiedTicks { get; set; }
        public long ConnectorLength { get; set; }
        public long ConnectorModifiedTicks { get; set; }
        public DateTime SyncedUtc { get; set; }
    }
    public sealed class Settings
    {
        public string Assembly { get; set; } = "";
        public string LocalRoot { get; set; } = "";
        public string ConnectorRoot { get; set; } = "";
        public string OutputFolder { get; set; } = "";
        public string NwcCompareLocalRoot { get; set; } = "";
        public string ConnectorNwcRoot { get; set; } = "";
        public string ProjectScanFolder { get; set; } = "";
        public string LocalModelsRoot { get; set; } = "";
        public Dictionary<string, string> LocalAliases { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Mappings { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> SystemFolders { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Baseline> Baselines { get; set; } = new Dictionary<string, Baseline>();
        public Dictionary<string, SyncReceipt> SyncReceipts { get; set; } = new Dictionary<string, SyncReceipt>();
        public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IWMF", "PlantModelSync");
        public static Settings Load()
        {
            var path = Path.Combine(DataDirectory, "settings.json");
            var result = File.Exists(path) ? JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path, Encoding.UTF8)) ?? new Settings() : new Settings();
            result.PreparePaths(); return result;
        }
        public void PreparePaths()
        {
            Paths.ConfigureAliases(LocalAliases);
            // 保留旧键供回退，同时复制迁移后的键，不丢失同步基准和人工确认记录。
            RemapKeys(Mappings); RemapKeys(Baselines); RemapKeys(SyncReceipts); RemapKeys(SystemFolders);
        }
        private static void RemapKeys<T>(Dictionary<string, T> values)
        {
            foreach (var pair in values.ToList())
            {
                var parts = pair.Key.Split('|'); if (parts.Length < 2) continue;
                parts[1] = Paths.Canonical(parts[1]).ToUpperInvariant(); var key = string.Join("|", parts);
                if (!values.ContainsKey(key)) values[key] = pair.Value;
            }
        }
        public void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "settings.json");
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        public static string MapKey(FolderLink folder, string local) => folder.Key + "|" + Paths.Canonical(local).ToUpperInvariant();
        public static string SyncKey(FolderLink folder, string local, string remote) => MapKey(folder, local) + "|" + remote;
    }
    public static class Files
    {
        public static string DwgFormat(string file)
        {
            if (!File.Exists(file)) return "—";
            using (var stream = File.OpenRead(file))
            {
                var head = new byte[6];
                if (stream.Read(head, 0, head.Length) != head.Length) return "未知";
                var code = Encoding.ASCII.GetString(head);
                switch (code)
                {
                    case "AC1032": return "AC1032 / 2018 格式";
                    case "AC1027": return "DWG 2013–2017";
                    case "AC1024": return "DWG 2010–2012";
                    case "AC1021": return "DWG 2007–2009";
                    default: return Regex.IsMatch(code, @"^AC10\d\d$") ? code : "非 DWG";
                }
            }
        }
        public static string Hash(string file)
        {
            if (!File.Exists(file)) return "";
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        public static void CheckDwg(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                byte[] head = new byte[6];
                if (stream.Read(head, 0, 6) != 6 || !Regex.IsMatch(Encoding.ASCII.GetString(head), @"^AC10\d\d$"))
                    throw new IOException("文件内容不是可识别的 DWG，未替换本地文件。");
            }
        }
        public static void AssertClosed(string file)
        {
            if (File.Exists(Path.ChangeExtension(file, ".dwl")) || File.Exists(Path.ChangeExtension(file, ".dwl2")))
                throw new IOException("发现 CAD 锁文件，请关闭源 DWG 并确认锁已释放：" + file);
            if (File.Exists(file)) using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        public static string InstallDownload(string staged, string target, string expectedHash, bool backupExisting = true)
        {
            CheckDwg(staged); AssertClosed(target);
            if (Hash(target) != expectedHash) throw new IOException("本地文件在预览后发生改变，请重新预览。");
            if (!File.Exists(target)) { File.Move(staged, target); return "新建"; }
            // 只有云端源覆盖本地镜像免备份；总装及本地路径修复继续使用默认备份。
            if (!backupExisting) { File.Replace(staged, target, null); return null; }
            var backup = target + ".pms-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
            File.Replace(staged, target, backup);
            return backup;
        }
        public static void PreserveModifiedUtc(string target, DateTime sourceModifiedUtc)
        {
            // 原子替换后再设置并核验，不能以下载时间或创建时间代替源修改时间。
            File.SetLastWriteTimeUtc(target, sourceModifiedUtc);
            if (File.GetLastWriteTimeUtc(target) != sourceModifiedUtc)
                throw new IOException("文件修改时间未能与 Connector 源保持一致，请重新检查：" + target);
        }
        public static string Conflict(Baseline baseline, string localHash, string remoteVersion)
        {
            return baseline != null && localHash != baseline.Hash && remoteVersion != baseline.Version
                ? "双方均有变更：请先手动合并或明确处理版本后再同步。" : "";
        }
    }
}
