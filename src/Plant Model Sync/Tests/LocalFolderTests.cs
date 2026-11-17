using System;
using System.IO;
using System.Text;
using System.Threading;
using IWMF.PlantModelSync;

internal static class LocalFolderTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "pms-local-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "A"));
            var one = Path.Combine(root, "A", "One.dwg");
            File.WriteAllBytes(one, Encoding.ASCII.GetBytes("AC1032fixture"));
            var folder = FolderLink.Parse(root);
            var index = TargetFinder.Index(root, CancellationToken.None);
            var row = new XrefRow { Name = "One.dwg", LocalPath = Path.Combine(root, "local.dwg"), CloudPath = "missing/One.dwg" };
            Check(TargetFinder.Match(folder, row, index, out var message) == one && message.Contains("唯一"), "唯一同名自动对应");
            Check(Files.DwgFormat(one).StartsWith("AC1032"), "预览读取 DWG 格式");
            var two = Path.Combine(root, "One.dwg");
            File.WriteAllBytes(two, Encoding.ASCII.GetBytes("AC1032fixture2"));
            index = TargetFinder.Index(root, CancellationToken.None);
            Check(TargetFinder.Match(folder, row, index, out message) == null && message.Contains("逐行选择"), "多处同名拒绝自动对应");
            row.CloudPath = two;
            Check(TargetFinder.Match(folder, row, index, out message) == two && message == "目标已存在", "明确对应路径优先");
            row.Name = "New.dwg"; row.CloudPath = "";
            Check(TargetFinder.Match(folder, row, index, out message) == Path.Combine(root, "New.dwg") && message.Contains("Push 可新建"), "无同名时只规划新路径");
            Check(!File.Exists(Path.Combine(root, "New.dwg")), "查找不会复制文件");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
