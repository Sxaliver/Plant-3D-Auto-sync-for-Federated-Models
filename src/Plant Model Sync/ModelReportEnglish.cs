using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace IWMF.PlantModelSync
{
    internal static class ModelReportEnglish
    {
        // 报告展示单独本地化；不改写原日志、原始诊断或用户的 DWG/Xref 文件名。
        private static readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["复用此前同一更新的证据，非本次新发生的变更。"] = "Evidence from the same previous update is reused; these are not new changes.",
            ["未留存与当前文件版本匹配的更新前证据；不伪造前后对比，也不把计划当作已执行变更。"] = "No before-update evidence matches the current file versions. No before image or completed change is inferred.",
            ["前后图取景参数未一致，不能作为同视角对比。"] = "The images use different framing and must not be treated as a same-view comparison.",
            ["原取景未覆盖当前模型，已按真实 Xref 几何重新取景；前后范围不同，不冒充同范围对比。"] = "The previous framing did not cover the current model. The image was reframed using Xref geometry; the comparison uses different extents.",
            ["截图期间源文件变化，已撤销不再匹配的前图与变更对比。"] = "Source files changed during capture. The outdated before image and change comparison have been excluded.",
            ["截图含宿主当前内存状态；未保存编辑不在文件级变更清单内。"] = "Images reflect the model currently loaded in Plant 3D. Unsaved edits are not included in the file-level change list.",
            ["截图期间源文件发生变化，未使用该更新前截图。"] = "Source files changed during capture; the before image was discarded.",
            ["没有获得更新前截图。"] = "No before-update image was captured.",
            ["未获得导出时截图。"] = "No export-time image was captured.",
            ["未捕获模型截图，不伪造更新前状态。"] = "Model images were not captured. No before-update state is inferred.",
            ["截图证据路径不在报告目录内。"] = "The image evidence path is outside the trusted evidence folder.",
            ["报告取证期间 NWC 发生变化或文件不存在。"] = "The NWC file changed or was missing while report evidence was collected.",
            ["截图为空白或对比度不足，未作为有效模型截图使用。"] = "The captured image is blank or has insufficient contrast and was rejected.",
            ["截图为空白，未作为有效模型截图使用。"] = "The captured image is blank and was rejected.",
            ["宿主不支持原生离屏截图。"] = "The host does not support native off-screen capture.",
            ["当前宿主不提供图形设备，未生成假截图。"] = "The host does not provide a graphics device; no image was generated.",
            ["模型没有可用于取景的可见几何。"] = "The model has no visible geometry available for framing.",
            ["模型几何范围无效。"] = "The model extents are invalid.",
            ["原生截图返回空图像。"] = "Native capture returned no image.",
            ["隔离回归示意几何，不是用户项目截图。"] = "Isolated regression illustration; these are not screenshots of the user project.",
            ["隔离回归：仅导出、未留存更新前图的示例。"] = "Isolated regression example: export only, with no recorded before image."
        };
        private static readonly KeyValuePair<string, string>[] Prefixes =
        {
            new KeyValuePair<string, string>("更新前截图失败：", "Before-update image capture failed: "),
            new KeyValuePair<string, string>("导出时截图失败：", "Export-time image capture failed: "),
            new KeyValuePair<string, string>("更新后依赖读取未完整通过：", "Dependency scanning after the update was incomplete: "),
            new KeyValuePair<string, string>("更新前截图暂存成功，但保存到 NWC 目录失败：", "The before image was captured, but publishing it to the NWC folder failed: "),
            new KeyValuePair<string, string>("导出报告取证失败：", "Export report evidence could not be collected: "),
            new KeyValuePair<string, string>("PDF 报告失败：", "PDF report generation failed: ")
        };
        internal static string Text(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (Messages.TryGetValue(value, out var english)) return english;
            foreach (var prefix in Prefixes)
                if (value.StartsWith(prefix.Key, StringComparison.Ordinal)) return prefix.Value + Text(value.Substring(prefix.Key.Length));
            // 未知本地化异常不逐字猜译，完整原文仍保存在 report.json。
            if (Regex.IsMatch(value, @"[\u3400-\u9FFF\uF900-\uFAFF]")) return "Additional diagnostic details are recorded in report.json.";
            return value.Length <= 300 ? value : value.Substring(0, 260) + " ... See report.json for full details.";
        }
        internal static string Action(ReportFileChange change) => string.IsNullOrEmpty(change.BeforeHash) ? "Added" : string.IsNullOrEmpty(change.AfterHash) ? "Missing" : "Updated";
    }
}
