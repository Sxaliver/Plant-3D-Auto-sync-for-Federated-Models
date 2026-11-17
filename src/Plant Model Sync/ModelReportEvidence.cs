using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed class ReportCamera
    {
        public double[] Target { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int PixelWidth { get; set; } = 1600;
        public int PixelHeight { get; set; } = 900;
        internal static bool Same(ReportCamera a, ReportCamera b) => a?.Target?.Length == 3 && b?.Target?.Length == 3 && a.Target.SequenceEqual(b.Target) && a.Width == b.Width && a.Height == b.Height && a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight;
        internal static bool CanFrameCenter(ReportCamera requested, ReportCamera fitted)
        {
            if (requested?.Target?.Length != 3 || fitted?.Target?.Length != 3 || requested.Width <= 0 || requested.Height <= 0) return false;
            var x = fitted.Target[0] - requested.Target[0]; var y = fitted.Target[1] - requested.Target[1]; var z = fitted.Target[2] - requested.Target[2];
            var horizontal = Math.Abs((x + y) / Math.Sqrt(2)); var vertical = Math.Abs((-x + y + 2 * z) / Math.Sqrt(6));
            return horizontal <= requested.Width * 0.45 && vertical <= requested.Height * 0.45 && fitted.Width <= requested.Width * 3;
        }
    }
    internal sealed class ReportImage
    {
        public string Path { get; set; }
        public DateTime? CapturedUtc { get; set; }
        public string Error { get; set; }
        public ReportCamera Camera { get; set; }
        public bool UnsavedMemory { get; set; }
        public string PublishedPath { get; set; }
        public string Method { get; set; }
        public string Diagnostic { get; set; }
    }
    internal sealed class ReportFileChange
    {
        public string File { get; set; }
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public string Action => string.IsNullOrEmpty(BeforeHash) ? "新增文件" : string.IsNullOrEmpty(AfterHash) ? "文件缺失" : "内容 / 引用变更";
    }
    internal sealed class ModelReportEvidence
    {
        public string Assembly { get; set; }
        public string Id { get; set; }
        public ReportImage Before { get; set; }
        public DateTime FinishedUtc { get; set; }
        public int Exports { get; set; }
        public Dictionary<string, string> BeforeHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> AfterHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<ReportFileChange> Changes { get; set; } = new List<ReportFileChange>();
        public string Note { get; set; }
    }
    internal sealed class ModelReportPage
    {
        public string Assembly { get; set; }
        public string Nwc { get; set; }
        public string NwcHash { get; set; }
        public ReportImage Before { get; set; }
        public ReportImage After { get; set; }
        public List<ReportFileChange> Changes { get; set; } = new List<ReportFileChange>();
        public List<string> Notes { get; set; } = new List<string>();
    }
    internal static class ReportImageFiles
    {
        internal static string Name(string folder, string model, string id, string phase)
        {
            if (phase != "before" && phase != "after") throw new ArgumentException("截图阶段必须为 before 或 after。", nameof(phase));
            // 保留可识别的总装名；替换 TeX 特殊符号，避免图片路径被当作排版指令。
            var stem = new string(Path.GetFileNameWithoutExtension(model).Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.' ? c : '_').ToArray()).Trim(' ', '.');
            if (string.IsNullOrEmpty(stem)) stem = "Assembly";
            var suffix = "__" + Digest(Paths.Canonical(model).ToUpperInvariant()) + "_" + Digest(id) + "_" + phase + ".png";
            // 同时约束组件长度和完整路径，报告子目录较深时才缩短模型名，标识与阶段不截断。
            var budget = Math.Min(240, 250 - Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length - 1) - suffix.Length;
            if (budget < 1) throw new IOException("截图输出目录过长，请选择更短的 NWC 输出目录。");
            if (stem.Length > budget)
            {
                if (char.IsHighSurrogate(stem[budget - 1])) budget--;
                stem = stem.Substring(0, budget).TrimEnd(' ', '.');
            }
            return stem + suffix;
        }
        private static string Digest(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").Substring(0, 8);
        }
    }
    internal sealed class ModelEvidenceStore
    {
        private readonly string root;
        private readonly Func<string, string, ReportCamera, ReportImage> capture;
        private readonly Func<string, IEnumerable<string>> dependencies;
        private readonly string outputRoot;
        internal ModelEvidenceStore(string storage, Func<string, string, ReportCamera, ReportImage> renderer, Func<string, IEnumerable<string>> readDependencies = null, string outputFolder = null)
        { root = Path.Combine(storage, "Logs", "ModelEvidence"); capture = renderer; dependencies = readDependencies; outputRoot = outputFolder; }
        private static ReportImage Publish(ReportImage source, string folder, string model, string id, string phase)
        {
            if (source?.Path == null || !source.CapturedUtc.HasValue || !File.Exists(source.Path)) return source;
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, ReportImageFiles.Name(folder, model, id, phase));
            if (!string.Equals(Paths.Canonical(target), Paths.Canonical(source.Path), StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(target)) File.Copy(source.Path, target);
                if (Hash(target) != Hash(source.Path)) throw new IOException("截图保存到 NWC 目录后校验失败：" + target);
            }
            return new ReportImage { Path = target, PublishedPath = target, CapturedUtc = source.CapturedUtc, Camera = source.Camera, UnsavedMemory = source.UnsavedMemory, Method = source.Method, Diagnostic = source.Diagnostic, Error = source.Error };
        }
        private static string Key(string path)
        {
            return Digest(Paths.Canonical(path).ToUpperInvariant());
        }
        private static string Digest(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").Substring(0, 20); }
        private string Latest(string model) => Path.Combine(root, Key(model), "latest.json");
        private static string Hash(string file) { var value = Files.Hash(file); return string.IsNullOrEmpty(value) ? null : value; }
        private static Dictionary<string, string> Hashes(IEnumerable<string> paths) => paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Paths.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, Hash, StringComparer.OrdinalIgnoreCase);
        private static bool Matches(IDictionary<string, string> expected) => expected.Count > 0 && expected.All(p => Hash(p.Key) == p.Value);
        private ModelReportEvidence Load(string model)
        {
            var path = Latest(model); if (!File.Exists(path)) return null;
            var record = JsonConvert.DeserializeObject<ModelReportEvidence>(File.ReadAllText(path, Encoding.UTF8));
            if (record == null || !string.Equals(record.Assembly, Paths.Canonical(model), StringComparison.OrdinalIgnoreCase)) return null;
            // 证据图必须来自本机证据目录，不接受日志中指向任意文件的路径。
            if (record.Before?.Path != null && !Paths.IsWithin(root, record.Before.Path)) throw new IOException("截图证据路径不在报告目录内。");
            return record;
        }
        internal IDisposable Begin(IEnumerable<AssemblyUpdate> models, IEnumerable<string> changingFiles)
        {
            var changes = new HashSet<string>(changingFiles.Select(Paths.Canonical), StringComparer.OrdinalIgnoreCase);
            var proposedBefore = Hashes(changes);
            var pending = new List<ModelReportEvidence>();
            foreach (var a in models.Where(a => changes.Contains(Paths.Canonical(a.Path)) || a.Scan.Rows.Any(r => changes.Contains(Paths.Canonical(r.LocalPath)))))
            {
                var tracked = a.Scan.Rows.Select(r => r.LocalPath).Append(a.Path);
                var before = Hashes(tracked); ModelReportEvidence record = null;
                try { record = Load(a.Path); } catch { }
                if (record != null && record.Exports == 0 && Matches(record.AfterHashes))
                {
                    // 同一次导出前的连续更新沿用第一张真实更新前图，而不是用中途状态覆盖它。
                    foreach (var p in before) if (!record.BeforeHashes.ContainsKey(p.Key)) record.BeforeHashes[p.Key] = p.Value;
                }
                else
                {
                    var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var folder = Path.Combine(root, Key(a.Path), id);
                    var file = Path.Combine(folder, ReportImageFiles.Name(folder, a.Path, id, "before")); Directory.CreateDirectory(folder);
                    record = new ModelReportEvidence { Assembly = Paths.Canonical(a.Path), Id = id, BeforeHashes = before };
                    try { record.Before = capture(a.Path, file, null); }
                    catch (Exception ex) { record.Before = new ReportImage { Error = "更新前截图失败：" + ex.Message }; }
                    if (!Matches(before)) record.Before = new ReportImage { Error = "截图期间源文件发生变化，未使用该更新前截图。" };
                    if (record.Before == null) record.Before = new ReportImage { Error = "没有获得更新前截图。" };
                }
                if (record.Before != null && !string.IsNullOrWhiteSpace(outputRoot))
                    try { record.Before.PublishedPath = Publish(record.Before, ManualNwcExport.DateFolder(outputRoot), a.Path, record.Id, "before")?.PublishedPath; }
                    catch (Exception ex) { record.Note = "更新前截图暂存成功，但保存到 NWC 目录失败：" + ex.Message; }
                pending.Add(record);
            }
            return new FinishScope(() =>
            {
                foreach (var record in pending)
                {
                    // 写入后按真实依赖归属补入新源，不能把同批其他系统的文件混进此模型清单。
                    if (dependencies != null)
                        try
                        {
                            foreach (var file in dependencies(record.Assembly).Select(Paths.Canonical))
                                if (!record.BeforeHashes.ContainsKey(file) && proposedBefore.TryGetValue(file, out var beforeHash)) record.BeforeHashes[file] = beforeHash;
                        }
                        catch (Exception ex) { record.Note = "更新后依赖读取未完整通过：" + ex.Message; }
                    record.AfterHashes = Hashes(record.BeforeHashes.Keys); record.FinishedUtc = DateTime.UtcNow;
                    record.Changes = record.BeforeHashes.Where(p => p.Value != record.AfterHashes[p.Key]).Select(p => new ReportFileChange { File = p.Key, BeforeHash = p.Value, AfterHash = record.AfterHashes[p.Key] }).ToList();
                    if (record.Changes.Count == 0) continue;
                    WeeklyUpdate.AtomicJson(Latest(record.Assembly), record);
                }
            });
        }
        internal ModelReportPage AfterExport(string model, string nwc)
        {
            var page = new ModelReportPage { Assembly = Paths.Canonical(model), Nwc = nwc, NwcHash = Hash(nwc) };
            ModelReportEvidence evidence = null;
            try { evidence = Load(model); } catch (Exception ex) { page.Notes.Add(ex.Message); }
            if (evidence != null && Matches(evidence.AfterHashes))
            {
                page.Before = Publish(evidence.Before, Path.GetDirectoryName(nwc), model, evidence.Id, "before"); page.Changes = evidence.Changes.ToList();
                if (evidence.Note != null) page.Notes.Add(evidence.Note);
                if (evidence.Exports > 0) page.Notes.Add("复用此前同一更新的证据，非本次新发生的变更。");
                evidence.Exports++; WeeklyUpdate.AtomicJson(Latest(model), evidence);
            }
            else page.Notes.Add("未留存与当前文件版本匹配的更新前证据；不伪造前后对比，也不把计划当作已执行变更。");
            var afterFolder = Path.GetDirectoryName(nwc);
            var after = Path.Combine(afterFolder, ReportImageFiles.Name(afterFolder, model, Guid.NewGuid().ToString("N"), "after")); Directory.CreateDirectory(afterFolder);
            try { page.After = capture(model, after, page.Before?.Camera); }
            catch (Exception ex) { page.After = new ReportImage { Error = "导出时截图失败：" + ex.Message }; }
            if (page.After == null) page.After = new ReportImage { Error = "未获得导出时截图。" };
            if (page.After.Path != null) page.After.PublishedPath = page.After.Path;
            if (!string.IsNullOrWhiteSpace(page.After.Diagnostic)) page.Notes.Add(page.After.Diagnostic);
            if (page.Before?.Camera != null && !ReportCamera.Same(page.Before.Camera, page.After.Camera)) page.Notes.Add("前后图取景参数未一致，不能作为同视角对比。");
            if (evidence != null && !Matches(evidence.AfterHashes)) { page.Before = null; page.Changes.Clear(); page.Notes.Add("截图期间源文件变化，已撤销不再匹配的前图与变更对比。"); }
            if (page.After.UnsavedMemory || page.Before?.UnsavedMemory == true) page.Notes.Add("截图含宿主当前内存状态；未保存编辑不在文件级变更清单内。");
            if (page.NwcHash == null || Hash(nwc) != page.NwcHash) throw new IOException("报告取证期间 NWC 发生变化或文件不存在。");
            return page;
        }
        private sealed class FinishScope : IDisposable
        {
            private Action finish; internal FinishScope(Action action) { finish = action; }
            public void Dispose() { var action = finish; finish = null; action?.Invoke(); }
        }
    }
}
