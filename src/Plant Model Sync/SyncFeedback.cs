using System;
using System.IO;

namespace IWMF.PlantModelSync
{
    // 成功状态只代表工作文件与 Connector 本地文件已校验一致。
    internal static class SyncFeedback
    {
        public const string Synced = "已同步";
        public const string DateMatched = "日期相同";
        public const string Pending = "待同步";
        public const string Processing = "同步中";
        public const string Failed = "失败";
        public const string Review = "需处理";

        private static bool Matches(string path, long length, long ticks)
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == length && file.LastWriteTimeUtc.Ticks == ticks;
        }

        public static void Refresh(Settings settings, FolderLink folder, XrefRow row, string target)
        {
            if (row.SyncState == Failed || row.SyncState == Processing) return;
            row.SyncedUtc = null;
            try
            {
                if (target != null && settings.SyncReceipts.TryGetValue(Settings.MapKey(folder, row.LocalPath), out var receipt)
                    && string.Equals(receipt.ConnectorPath, Paths.Canonical(target), StringComparison.OrdinalIgnoreCase)
                    && Matches(row.LocalPath, receipt.LocalLength, receipt.LocalModifiedTicks)
                    && Matches(target, receipt.ConnectorLength, receipt.ConnectorModifiedTicks))
                {
                    row.SyncState = Synced; row.SyncedUtc = receipt.SyncedUtc; return;
                }
                row.SyncState = target == null || !File.Exists(row.LocalPath) ? Review : Pending;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { row.SyncState = Review; }
        }

        public static bool Record(Settings settings, FolderLink folder, XrefRow row, string target, string expectedHash)
        {
            var local = new FileInfo(row.LocalPath); var remote = new FileInfo(target);
            local.Refresh(); remote.Refresh();
            if (!local.Exists || !remote.Exists) { row.SyncState = Pending; return false; }
            var receipt = new SyncReceipt { ConnectorPath = Paths.Canonical(target), Hash = expectedHash,
                LocalLength = local.Length, LocalModifiedTicks = local.LastWriteTimeUtc.Ticks,
                ConnectorLength = remote.Length, ConnectorModifiedTicks = remote.LastWriteTimeUtc.Ticks, SyncedUtc = DateTime.UtcNow };
            if (string.IsNullOrEmpty(expectedHash) || Files.Hash(row.LocalPath) != expectedHash || Files.Hash(target) != expectedHash
                || !Matches(row.LocalPath, receipt.LocalLength, receipt.LocalModifiedTicks)
                || !Matches(target, receipt.ConnectorLength, receipt.ConnectorModifiedTicks))
            { row.SyncState = Pending; row.SyncedUtc = null; return false; }
            settings.SyncReceipts[Settings.MapKey(folder, row.LocalPath)] = receipt;
            row.SyncState = Synced; row.SyncedUtc = receipt.SyncedUtc;
            return true;
        }
    }
}
