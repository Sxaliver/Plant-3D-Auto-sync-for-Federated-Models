using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;

namespace IWMF.PlantModelSync
{
    // 只读取 Connector 本地目录；发现同名文件不会触发复制或云端操作。
    internal static class TargetFinder
    {
        public static Dictionary<string, List<string>> Index(string directory, CancellationToken ct, Action<string> warning = null)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    foreach (var path in Directory.EnumerateFiles(current, "*.dwg", SearchOption.TopDirectoryOnly))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(path);
                        if (!result.TryGetValue(name, out var matches)) result[name] = matches = new List<string>();
                        matches.Add(Paths.Canonical(path));
                    }
                    foreach (var sub in Directory.EnumerateDirectories(current))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (CanTraverse(sub)) pending.Push(sub);
                        else warning?.Invoke("未展开目录链接，或无法读取目录标记：" + sub);
                    }
                }
                catch (UnauthorizedAccessException ex) { warning?.Invoke(current + "：" + ex.Message); }
                catch (DirectoryNotFoundException ex) { warning?.Invoke(current + "：" + ex.Message); }
                catch (IOException ex) { warning?.Invoke(current + "：" + ex.Message); }
            }
            foreach (var matches in result.Values) matches.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // 云端占位目录也是重解析点，不能与跳转目录一起跳过；仅拒绝名称重定向，避免链接循环。
        internal static bool CanTraverse(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return true;
            var handle = FindFirstFile(path, out var data);
            if (handle == new IntPtr(-1)) return false;
            try { return (data.Tag & 0x20000000u) == 0; }
            finally { FindClose(handle); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
            public uint SizeHigh, SizeLow, Tag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string fileName, out FindData data);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);

        public static string Match(FolderLink folder, XrefRow row, Dictionary<string, List<string>> index, out string message)
        {
            string existing = null;
            try { existing = Paths.SyncTarget(folder.Project, row.CloudPath); }
            catch (ArgumentException) { }
            if (existing != null && File.Exists(existing))
            {
                message = "目标已存在";
                return existing;
            }
            if (index.TryGetValue(row.Name, out var matches) && matches.Count == 1)
            {
                message = "找到唯一同名文件";
                return matches[0];
            }
            if (matches != null && matches.Count > 1)
            {
                message = "发现 " + matches.Count + " 个同名文件，请逐行选择对应 DWG";
                return null;
            }
            message = "目标目录无同名文件；Push 可新建，Pull 无来源";
            return existing ?? Path.Combine(folder.Project, row.Name);
        }
    }
}
