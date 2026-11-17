using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal static partial class WorkflowTestProgram
    {
        private static void VerifyModelReports(string evidence)
        {
            var root = Path.Combine(evidence, "reports"); Directory.CreateDirectory(root);
            using (var blank = new Bitmap(1600, 900))
            using (var graphics = Graphics.FromImage(blank))
            {
                graphics.Clear(Color.White); Check(!ReportImageQuality.IsUseful(blank), "纯白截图不能视为模型截图");
                using (var faint = new Pen(Color.FromArgb(248, 248, 248))) graphics.DrawLine(faint, 3, 5, 200, 5);
                Check(!ReportImageQuality.IsUseful(blank), "极浅文字或几乎无对比的画面不冒充有效模型");
                graphics.DrawLine(Pens.Black, 3, 7, 200, 7);
                Check(ReportImageQuality.IsUseful(blank), "全像素检查能够识别旧13像素采样步长漏掉的细线");
            }
            var longModel = Path.Combine(root, "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM_LONG ASSEMBLY NAME.dwg");
            var shortFolder = Path.GetPathRoot(root);
            var longName = ReportImageFiles.Name(shortFolder, longModel, "test", "before");
            Check(longName.StartsWith(Path.GetFileNameWithoutExtension(longModel) + "__") && longName.EndsWith("_before.png"), "路径空间足够时截图保留完整总装名，不再固定截为48字符");
            Check(ReportImageFiles.Name(shortFolder, longModel, "test", "after").EndsWith("_after.png") && ReportImageFiles.Name(shortFolder, longModel, "next", "before") != longName, "前后阶段及不同更新有独立标识，不相互覆盖");
            Check(ReportImageFiles.Name(shortFolder, Path.Combine(root, "other", Path.GetFileName(longModel)), "test", "before") != longName, "不同目录同名总装的截图不会冲突");
            var deepFolder = Path.Combine(shortFolder, new string('a', 210));
            Check(Path.Combine(deepFolder, ReportImageFiles.Name(deepFolder, longModel, "test", "after")).Length <= 250, "深层输出目录按路径预算缩短名字并保留阶段与唯一标识");
            var specialName = ReportImageFiles.Name(shortFolder, Path.Combine(root, "9424_总装 A&B#50%{test}.dwg"), "test", "before");
            Check(specialName.StartsWith("9424_总装 A_B_50__test_") && specialName.IndexOfAny(new[] { '%', '#', '{', '}', '&' }) < 0, "中文空格模型名可读且图片文件名不包含TeX特殊符号");
            var a = Path.Combine(root, "9424_总装 A&B#50%.dwg"); var x = Path.Combine(root, "楼层_5.30%_A&B.dwg"); var y = Path.Combine(root, "新增_Xref.dwg");
            var b = Path.Combine(root, "9422_总装.dwg"); var other = Path.Combine(root, "另一系统.dwg");
            Dwg(a, "assembly"); Dwg(x, "old"); Dwg(b, "other assembly"); Dwg(other, "other source");
            var model = new AssemblyUpdate { Path = a, Scan = new ScanResult() }; model.Scan.Rows.Add(new XrefRow { LocalPath = x });
            var modelB = new AssemblyUpdate { Path = b, Scan = new ScanResult() }; modelB.Scan.Rows.Add(new XrefRow { LocalPath = other });
            var stage = "before"; var shots = new List<string>(); var fixedCamera = new ReportCamera { Target = new[] { 0d, 0d, 0d }, Width = 100, Height = 56.25 };
            ReportImage Capture(string file, string png, ReportCamera camera)
            {
                shots.Add(stage); Directory.CreateDirectory(Path.GetDirectoryName(png));
                using (var image = new Bitmap(1600, 900)) using (var g = Graphics.FromImage(image)) using (var font = new Font("Arial", 28))
                {
                    g.Clear(Color.White); g.DrawString("REGRESSION FIXTURE - " + stage.ToUpperInvariant(), font, Brushes.Navy, 40, 40);
                    g.FillRectangle(Brushes.SteelBlue, 260, 320, 700, 120); g.FillEllipse(Brushes.Gray, 900, 280, 240, 220);
                    if (stage != "before") g.FillRectangle(Brushes.OrangeRed, 500, 200, 100, 360);
                    image.Save(png, ImageFormat.Png);
                }
                return new ReportImage { Path = png, Camera = camera ?? fixedCamera, CapturedUtc = DateTime.UtcNow };
            }
            var outputRoot = Path.Combine(root, "NWC"); var dated = ManualNwcExport.DateFolder(outputRoot);
            var store = new ModelEvidenceStore(root, Capture, file => file == a ? new[] { x, y }.Where(File.Exists) : new[] { other }, outputRoot);
            using (store.Begin(new[] { model }, new[] { x }))
            {
                Check(shots.Single() == "before", "前截图在覆盖源文件前完成");
                Check(Directory.GetFiles(dated, "*_before.png").Length == 1, "更新开始前，第一张PNG已直接保存在NWC当日文件夹");
                stage = "after"; Dwg(x, "new");
            }
            using (store.Begin(new[] { model }, new[] { x })) { stage = "after second update"; Dwg(x, "new twice"); }
            Check(shots.Count == 1, "导出前连续更新保留第一张前截图而非中间版本");
            var nwc = Path.Combine(dated, "9424.nwc"); File.WriteAllText(nwc, "NWC regression fixture");
            var page = store.AfterExport(a, nwc);
            Check(shots.Last() == "after second update" && page.Before.CapturedUtc <= page.After.CapturedUtc, "后截图在更新完成并导出后获取，时间顺序正确");
            Check(Path.GetDirectoryName(page.Before.Path) == dated && Path.GetDirectoryName(page.After.Path) == dated && File.Exists(page.Before.Path) && File.Exists(page.After.Path), "导出前后两张PNG均在NWC同一个当日目录，非仅留在日志缓存");
            Check(Directory.GetFiles(dated, "*_before.png").Length == 1, "连续更新及导出不会用中间状态重复覆盖同一次前图");
            Check(Path.GetFileName(page.Before.Path).StartsWith("9424_总装 A_B_50_") && Path.GetFileName(page.After.Path).StartsWith("9424_总装 A_B_50_"), "NWC当日目录前后截图均带当前总装模型名");
            Check(Directory.GetFiles(Path.Combine(root, "Logs", "ModelEvidence"), "*.png", SearchOption.AllDirectories).All(p => Path.GetFileName(p).StartsWith("9424_总装")), "证据缓存截图也含模型名，不使用通用before.png");
            Check(JsonConvert.SerializeObject(page.Before.Camera) == JsonConvert.SerializeObject(page.After.Camera), "前后截图使用完全相同的相机参数");
            Check(!ReportCamera.Same(fixedCamera, new ReportCamera { Target = new[] { 1d, 0d, 0d }, Width = 100, Height = 56.25 }) && !ReportCamera.Same(null, null), "不同相机或缺少相机不能标注同视角对比");
            Check(ReportCamera.CanFrameCenter(fixedCamera, fixedCamera) && !ReportCamera.CanFrameCenter(new ReportCamera { Target = new[] { 6653185.51, 29134624.23, 0d }, Width = 382, Height = 215 }, new ReportCamera { Target = new[] { 7043360.23, 29210061.99, 9649d }, Width = 50175, Height = 28223 }), "旧标题范围不能覆盖真实Xref时识别失效取景，不沿用成空白后图");
            Check(page.Changes.Single().File == x && page.Changes[0].AfterHash == Files.Hash(x), "变更表只列实际文件变化，不列未执行计划");
            stage = "before";
            using (store.Begin(new[] { model, modelB }, new[] { a, b, y, other })) { stage = "after"; Dwg(a, "new assembly"); Dwg(b, "new other"); Dwg(y, "new child"); Dwg(other, "changed other"); }
            var newPage = store.AfterExport(a, nwc);
            Check(newPage.Changes.Any(c => c.File == y && c.BeforeHash == null) && newPage.Changes.All(c => c.File != b && c.File != other), "新增源按实际依赖归属，其他系统不会混入本模型清单");
            Dwg(x, "changed outside plugin");
            var stale = store.AfterExport(a, nwc); Check(stale.Before == null && stale.Changes.Count == 0 && stale.Notes.Any(n => n.Contains("不伪造")), "证据版本不匹配时不沿用旧前图或伪造变更");
            var noBefore = new ModelEvidenceStore(Path.Combine(root, "fresh"), Capture).AfterExport(a, nwc);
            Check(noBefore.Before == null && noBefore.After != null, "仅导出当前模型时明确缺少前图，不把当前图重复当作前图");
            var rows = Enumerable.Range(0, 34).Select(i => new ReportFileChange { File = Path.Combine(root, "第" + i + "层_管道#50%&设备.dwg"), BeforeHash = new string('A', 64), AfterHash = new string('B', 64) }).ToList();
            var splits = ModelUpdateReport.SplitChanges(rows); Check(splits.Count == 4 && splits.Sum(p => p.Count) == 34 && splits.All(p => p.Count <= 10), "长变更清单按模型续页，完整保留每条变更");
            var escaped = ModelUpdateReport.Escape("a_%&#${}\\^~"); Check(escaped.Contains(@"\_") && escaped.Contains(@"\textbackslash{}") && escaped.Contains(@"\%"), "文件名和文字转义TeX特殊字符");
            Check(ModelReportEnglish.Action(new ReportFileChange { AfterHash = "new" }) == "Added" && ModelReportEnglish.Action(new ReportFileChange { BeforeHash = "old" }) == "Missing" && ModelReportEnglish.Action(new ReportFileChange { BeforeHash = "old", AfterHash = "new" }) == "Updated", "报告文件级动作使用英文且不修改原日志动作");
            var englishError = ModelReportEnglish.Text("导出时截图失败：截图为空白或对比度不足，未作为有效模型截图使用。");
            Check(englishError.Contains("Export-time image capture failed") && englishError.Contains("insufficient contrast") && !System.Text.RegularExpressions.Regex.IsMatch(englishError, @"[\u3400-\u9FFF]"), "旧中文截图错误在英文报告中完整转为英文提示");
            Check(ModelReportEnglish.Text("未映射的中文诊断") == "Additional diagnostic details are recorded in report.json." && ModelReportEnglish.Text("Native test evidence.") == "Native test evidence.", "未知本地化诊断引导原始日志，英文原文保持不变");
            var settings = new ModelReportSettings { TemplateDirectory = Environment.GetEnvironmentVariable("IWMF_PMS_TEST_REPORT_TEMPLATE"), CompilerPath = Environment.GetEnvironmentVariable("IWMF_PMS_TEST_REPORT_COMPILER"), TimeoutSeconds = 300 };
            var output = Path.Combine(root, "NWC", "2026-10-29"); Directory.CreateDirectory(output);
            newPage.Changes = rows; newPage.Notes.Add("隔离回归示意几何，不是用户项目截图。"); noBefore.Assembly = b; noBefore.Nwc = Path.Combine(root, "9422.nwc"); noBefore.Notes.Add("隔离回归：仅导出、未留存更新前图的示例。");
            if (!string.IsNullOrWhiteSpace(settings.TemplateDirectory) && !string.IsNullOrWhiteSpace(settings.CompilerPath))
            {
                var template = Path.Combine(settings.TemplateDirectory, "P3D_COBie_Guide_Template.tex"); var templateHash = Files.Hash(template);
                var report = ModelUpdateReport.Generate(new[] { newPage, noBefore }, output, settings);
                if (report.Error != null) Console.WriteLine(report.Error);
                Check(report.Error == null && File.Exists(report.Pdf), "从用户TeX模板真实编译PDF，不使用替代PDF生成器");
                Check(report.Pages == 5 && report.Models == 2 && File.Exists(report.Manifest) && Directory.GetFiles(Path.Combine(Path.GetDirectoryName(report.Tex), "images")).Length == 3, "PDF及截图保存到NWC目录，两个模型加续页计五页");
                var reportImages = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(report.Tex), "images")).Select(Path.GetFileName).ToArray();
                Check(reportImages.Count(n => n.StartsWith("9424_总装 A_B_50_") && (n.EndsWith("_before.png") || n.EndsWith("_after.png"))) == 2 && reportImages.Single(n => n.StartsWith("9422_总装")).EndsWith("_after.png"), "报告images目录按各自总装模型名保存前后图，不使用0-before等通用名称");
                Check(Files.Hash(template) == templateHash, "报告生成不修改用户原始TeX模板");
                var tex = File.ReadAllText(report.Tex); Check(!tex.Contains("Replace this paragraph") && tex.Contains("projectheader") && tex.Contains("No verified before-update image was recorded."), "保留页眉、移除示例正文，缺前图时用英文明确提示");
                Check(tex.Contains("Auto sync Update Report") && tex.Contains("Before update") && tex.Contains("At NWC export / After update") && tex.Contains("Completed file-level changes") && !tex.Contains("已执行的文件级变更") && !tex.Contains("更新报告"), "报告标题截图说明表头及状态统一英文");
                Check(tex.Contains("管道") && File.ReadAllText(report.Manifest).Contains("隔离回归"), "中文原文件名与原始诊断保留，不为英文报告改写源数据");
                File.WriteAllText(Path.Combine(evidence, "model-report-result.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
                var diagnostic = new ModelReportPage { Assembly = Path.Combine(root, "DIAGNOSTIC_TEST.dwg"), Nwc = "NOT_EXPORTED.nwc", Before = page.Before, After = new ReportImage { Error = "导出时截图失败：截图为空白或对比度不足，未作为有效模型截图使用。" } };
                diagnostic.Notes.Add("Injected diagnostic-layout regression sample only; no model update or NWC export was performed.");
                diagnostic.Notes.Add("未知中文技术诊断，完整原文需要保留。");
                var diagnostics = ModelUpdateReport.Generate(new[] { diagnostic }, Path.Combine(root, "english-diagnostic"), settings);
                Check(diagnostics.Error == null && diagnostics.Pages == 1 && File.ReadAllText(diagnostics.Tex).Contains("Export-time image capture failed") && File.ReadAllText(diagnostics.Manifest).Contains("未知中文技术诊断"), "截图失败场景英文PDF真实编译，完整原文保留在JSON");
                File.WriteAllText(Path.Combine(evidence, "english-diagnostic-result.json"), JsonConvert.SerializeObject(diagnostics, Formatting.Indented));
                var nativeResult = Environment.GetEnvironmentVariable("IWMF_PMS_TEST_REPORT_NATIVE_RESULT");
                if (!string.IsNullOrWhiteSpace(nativeResult))
                {
                    var nativePage = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(nativeResult))["Page"].ToObject<ModelReportPage>();
                    nativePage.Notes.Add("English rendering of previously captured native test evidence; no new model update, capture or NWC export was performed for this language conversion.");
                    var nativeReport = ModelUpdateReport.Generate(new[] { nativePage }, Path.Combine(evidence, "English_Report"), settings);
                    Check(nativeReport.Error == null && nativeReport.Pages == 1, "真实原生截图证据可使用同一生产生成器编译英文报告");
                    File.WriteAllText(Path.Combine(evidence, "english-native-result.json"), JsonConvert.SerializeObject(nativeReport, Formatting.Indented));
                }
            }
            var failed = ModelUpdateReport.Generate(new[] { page }, Path.Combine(root, "no-compiler"), new ModelReportSettings { TemplateDirectory = settings.TemplateDirectory ?? root, CompilerPath = Path.Combine(root, "missing.exe") });
            Check(failed.Pdf == null && failed.Error != null && File.Exists(failed.Manifest) && File.Exists(nwc), "PDF失败保留NWC和诊断，不冒充报告成功");
        }
    }
}
