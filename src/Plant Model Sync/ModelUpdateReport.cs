using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed class ModelReportSettings
    {
        public string TemplateDirectory { get; set; }
        public string CompilerPath { get; set; }
        public int TimeoutSeconds { get; set; } = 180;
        internal static ModelReportSettings Load(string storage)
        {
            var file = Path.Combine(storage, "report-settings.json");
            var result = File.Exists(file) ? JsonConvert.DeserializeObject<ModelReportSettings>(File.ReadAllText(file, Encoding.UTF8)) : new ModelReportSettings();
            if (result == null) result = new ModelReportSettings();
            if (string.IsNullOrWhiteSpace(result.TemplateDirectory)) result.TemplateDirectory = Path.Combine(storage, "Logs", "Template");
            return result;
        }
    }
    internal sealed class ModelReportResult
    {
        public string Pdf { get; set; }
        public string Tex { get; set; }
        public string Manifest { get; set; }
        public string Error { get; set; }
        public int Models { get; set; }
        public int Pages { get; set; }
    }
    internal static class ModelUpdateReport
    {
        internal static string Escape(string value)
        {
            var text = new StringBuilder();
            foreach (var c in value ?? "")
                switch (c)
                {
                    case '\\': text.Append(@"\textbackslash{}"); break;
                    case '{': case '}': case '%': case '$': case '&': case '#': case '_': text.Append('\\').Append(c); break;
                    case '~': text.Append(@"\textasciitilde{}"); break;
                    case '^': text.Append(@"\textasciicircum{}"); break;
                    default: text.Append(char.IsControl(c) ? ' ' : c); break;
                }
            return text.ToString();
        }
        internal static List<List<ReportFileChange>> SplitChanges(IEnumerable<ReportFileChange> changes)
        {
            var pages = new List<List<ReportFileChange>>(); var page = new List<ReportFileChange>(); var cost = 0;
            foreach (var item in changes)
            {
                var rows = 1 + Path.GetFileName(item.File).Length / 42;
                if (page.Count > 0 && (page.Count >= 10 || cost + rows > 16)) { pages.Add(page); page = new List<ReportFileChange>(); cost = 0; }
                page.Add(item); cost += rows;
            }
            if (page.Count > 0 || pages.Count == 0) pages.Add(page); return pages;
        }
        private static string FileText(string value)
        {
            var result = new StringBuilder(); int length = 0;
            foreach (var c in value ?? "") { result.Append(Escape(c.ToString())); if (++length % 14 == 0 || c == '_' || c == '.' || c == '-') result.Append(@"\allowbreak{}"); }
            return result.ToString();
        }
        private static string Image(ReportImage image, string dir, string model, int index, bool before)
        {
            if (image?.Path != null && image.CapturedUtc.HasValue && File.Exists(image.Path))
            {
                var folder = Path.Combine(dir, "images");
                var name = ReportImageFiles.Name(folder, model, index.ToString(System.Globalization.CultureInfo.InvariantCulture), before ? "before" : "after");
                var target = Path.Combine(folder, name); File.Copy(image.Path, target);
                return @"\includegraphics[width=\linewidth,height=53mm,keepaspectratio]{\detokenize{images/" + name + "}}";
            }
            return @"\fbox{\parbox[c][45mm][c]{0.92\linewidth}{\centering\small " + Escape(image?.Error != null ? ModelReportEnglish.Text(image.Error) : before ? "No verified before-update image was recorded." : "No verified export-time image was recorded.") + "}}";
        }
        private static string At(DateTime? value) => value.HasValue ? value.Value.ToUniversalTime().AddHours(8).ToString("yyyy-MM-dd HH:mm:ss") + " +08:00" : "Not captured";
        internal static ModelReportResult Generate(IReadOnlyList<ModelReportPage> models, string output, ModelReportSettings settings)
        {
            var result = new ModelReportResult { Models = models.Count };
            var id = "AutoSync_Report_" + DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var work = Path.Combine(output, id); Directory.CreateDirectory(Path.Combine(work, "images"));
            result.Tex = Path.Combine(work, "report.tex"); result.Manifest = Path.Combine(work, "report.json");
            try
            {
                if (models.Count == 0) throw new IOException("没有成功导出的模型，未生成空报告。");
                var templateFile = Path.Combine(settings.TemplateDirectory, "P3D_COBie_Guide_Template.tex");
                var template = File.ReadAllText(templateFile, Encoding.UTF8);
                var begin = template.IndexOf(@"\begin{document}", StringComparison.Ordinal); if (begin < 0) throw new IOException("模板缺少 document 正文起点。");
                var preamble = template.Substring(0, begin).Replace("| Data Entry Guide", "| Model Update Report");
                preamble = preamble.Replace(@"\newcommand{\GuideTitle}{P3D Data Entry}", @"\newcommand{\GuideTitle}{Auto sync Model Update Report}");
                preamble = preamble.Replace(@"\newcommand{\GuideSubtitle}{Simple Work Instruction}", @"\newcommand{\GuideSubtitle}{NWC Export Summary}");
                preamble += "\n" + @"\usepackage{xeCJK}" + "\n" + @"\IfFontExistsTF{Microsoft YaHei}{\setCJKmainfont{Microsoft YaHei}}{\setCJKmainfont{SimSun}}" + "\n";
                var logo = Regex.Match(preamble, @"\\newcommand\{\\HeaderImage\}\{([^{}]+)\}");
                if (logo.Success)
                {
                    var leaf = logo.Groups[1].Value; if (Path.GetFileName(leaf) != leaf || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("模板页眉图片必须为模板目录内的单个文件名。");
                    File.Copy(Path.Combine(settings.TemplateDirectory, leaf), Path.Combine(work, leaf));
                }
                var body = new StringBuilder(preamble).AppendLine(@"\begin{document}");
                int modelIndex = 0;
                foreach (var model in models)
                {
                    var before = Image(model.Before, work, model.Assembly, modelIndex, true); var after = Image(model.After, work, model.Assembly, modelIndex, false);
                    var sections = SplitChanges(model.Changes); int part = 0;
                    foreach (var list in sections)
                    {
                        if (result.Pages++ > 0) body.AppendLine(@"\clearpage");
                        body.AppendLine(@"{\Large\bfseries Auto sync Update Report}\par");
                        body.AppendLine(@"{\small\bfseries " + FileText(Path.GetFileName(model.Assembly)) + @"}\par");
                        body.AppendLine(@"{\footnotesize NWC: " + FileText(Path.GetFileName(model.Nwc)) + " / Model " + (modelIndex + 1) + " / Part " + (++part) + "/" + sections.Count + @"}\par");
                        body.AppendLine(@"\begin{minipage}[t]{0.48\textwidth}\centering\textbf{Before update}\par " + before + @"\par{\scriptsize " + Escape(At(model.Before?.CapturedUtc)) + @"}\end{minipage}\hfill\begin{minipage}[t]{0.48\textwidth}\centering\textbf{At NWC export / After update}\par " + after + @"\par{\scriptsize " + Escape(At(model.After?.CapturedUtc)) + @"}\end{minipage}\par");
                        body.AppendLine(@"{\scriptsize " + (ReportCamera.Same(model.Before?.Camera, model.After?.Camera) ? "Same framing. " : "Matching before/after framing is unavailable; this is not a same-view comparison. ") + @"Images are not an element-by-element geometry comparison.}\par");
                        body.AppendLine(@"{\small\bfseries Completed file-level changes}\par");
                        if (list.Count == 0) body.AppendLine(@"{\small No verified plugin update record; this run exports the current model only.}\par");
                        else
                        {
                            body.AppendLine(@"\begingroup\footnotesize\setlength{\tabcolsep}{3pt}\begin{tabularx}{\textwidth}{@{}L{24mm}Y L{31mm}@{}}\toprule\rowcolor{KeyYellow}Action & File & SHA-256 (before / after)\\\midrule");
                            foreach (var change in list) body.AppendLine(Escape(ModelReportEnglish.Action(change)) + " & " + FileText(Path.GetFileName(change.File)) + " & " + Escape(ShortHash(change.BeforeHash) + " / " + ShortHash(change.AfterHash)) + @"\\\midrule");
                            body.AppendLine(@"\bottomrule\end{tabularx}\par\endgroup");
                        }
                        body.AppendLine(@"{\scriptsize NWC SHA-256: " + Escape(model.NwcHash) + @"}\par");
                        foreach (var note in model.Notes) body.AppendLine(@"{\footnotesize " + Escape(ModelReportEnglish.Text(note)) + @"}\par");
                        if (model.Before?.Error != null) body.AppendLine(@"{\footnotesize " + Escape(ModelReportEnglish.Text(model.Before.Error)) + @"}\par");
                        if (model.After?.Error != null) body.AppendLine(@"{\footnotesize " + Escape(ModelReportEnglish.Text(model.After.Error)) + @"}\par");
                    }
                    modelIndex++;
                }
                body.AppendLine(@"\end{document}"); File.WriteAllText(result.Tex, body.ToString(), new UTF8Encoding(false));
                WeeklyUpdate.AtomicJson(Path.Combine(work, "template-origin.json"), new { Template = templateFile, Sha256 = Files.Hash(templateFile) });
                Compile(settings, work);
                var pdf = Path.Combine(work, "report.pdf");
                using (var stream = File.OpenRead(pdf)) { var head = new byte[5]; if (stream.Read(head, 0, 5) != 5 || Encoding.ASCII.GetString(head) != "%PDF-") throw new IOException("编译器未生成有效 PDF。"); }
                result.Pdf = Path.Combine(output, id + ".pdf"); File.Copy(pdf, result.Pdf);
            }
            catch (Exception ex) { result.Error = "NWC 保留；PDF 报告未完成：" + ex.Message; }
            WeeklyUpdate.AtomicJson(result.Manifest, new { Format = "IWMF.ModelUpdateReport.v1", Language = "en", Result = result, Models = models }); return result;
        }
        private static string ShortHash(string hash) => string.IsNullOrEmpty(hash) ? "not present" : hash.Substring(0, Math.Min(10, hash.Length));
        private static void Compile(ModelReportSettings settings, string work)
        {
            var exe = settings.CompilerPath;
            if (string.IsNullOrWhiteSpace(exe)) exe = "tectonic.exe";
            bool tectonic = Path.GetFileName(exe).StartsWith("tectonic", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(exe, tectonic ? "--keep-logs --keep-intermediates --outdir . report.tex" : "-interaction=nonstopmode -halt-on-error -no-shell-escape report.tex") { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            for (int pass = 0; pass < (tectonic ? 1 : 2); pass++)
                using (var process = new Process { StartInfo = start })
                {
                    var log = new StringBuilder();
                    process.OutputDataReceived += (s, e) => { lock (log) log.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { lock (log) log.AppendLine(e.Data); };
                    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (!process.WaitForExit(Math.Max(10, Math.Min(600, settings.TimeoutSeconds)) * 1000)) { process.Kill(); throw new IOException("TeX 编译超时；已停止本次编译器。"); }
                    process.WaitForExit(); File.WriteAllText(Path.Combine(work, "compiler-" + pass + ".log"), log.ToString(), new UTF8Encoding(false));
                    if (process.ExitCode != 0) throw new IOException("TeX 编译失败，请查看 " + Path.Combine(work, "compiler-" + pass + ".log"));
                }
        }
    }
}
