using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class ConnectorFile
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string RelativePath { get; set; }
        public string Folder { get; set; }
        public string Modified { get; set; }
        public string SyncState { get; set; } = "未关联";
    }

    internal sealed partial class MainForm
    {
        private readonly DataGridView connectorGrid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        private ReviewTable<ConnectorFile> connectorTable;
        private readonly FileDetailsPanel localDetails = new FileDetailsPanel("本地 Xref 详情");
        private readonly FileDetailsPanel connectorDetails = new FileDetailsPanel("Connector 文件详情");
        private Dictionary<string, List<string>> connectorIndex;
        private List<ConnectorFile> connectorFiles = new List<ConnectorFile>();
        private string indexedFolder;
        private bool selectingPair;

        private Control CreateSplitView()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 8, Size = new Size(1330, 570), Panel1MinSize = 390, Panel2MinSize = 390, SplitterDistance = 660 };
            split.SizeChanged += (s, e) => { if (split.Width >= 788) split.SplitterDistance = Math.Max(390, Math.Min(split.Width - 398, split.Width / 2)); };
            var leftActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, AutoScroll = true };
            AddButton(leftActions, "勾选可见行", () => SelectVisible(true));
            AddButton(leftActions, "清除勾选", () => SelectVisible(false));
            AddButton(leftActions, "浏览对应 DWG", PickMappedDwg);
            var left = CreateFilePane("本地 · 总装引用的 Xref", table, leftActions, localDetails);
            split.Panel1.Controls.Add(left);
            ConnectorColumn("Name", "Connector DWG", 190, 45);
            ConnectorColumn("RelativePath", "目录内路径", 225, 55);
            ConnectorColumn("SyncState", "同步状态", 90);
            ConnectorColumn("Modified", "修改日期", 145);
            connectorTable = new ReviewTable<ConnectorFile>(connectorGrid, r => r.Name + " " + r.RelativePath + " " + r.Modified + " " + r.SyncState, r => r.Folder);
            var rightActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, AutoScroll = true };
            AddButton(rightActions, "对应到左侧 Xref", MapSelectedConnector, true);
            rightActions.Controls.Add(new Label { Text = "先选左侧 Xref，再选此侧文件", AutoSize = true, Margin = new Padding(8, 10, 0, 0) });
            var right = CreateFilePane("Connector · 同步目录中的源 DWG", connectorTable, rightActions, connectorDetails);
            split.Panel2.Controls.Add(right);
            CompactTable(table); CompactTable(connectorTable);
            foreach (Label label in grid.Controls.OfType<Label>()) label.Text = "暂无 Xref。请选择总装 DWG，或调整筛选条件。";
            foreach (Label label in connectorGrid.Controls.OfType<Label>()) label.Text = "暂无 DWG。请选择或刷新 Connector 目录。";
            grid.SelectionChanged += (s, e) => ShowLocalSelection();
            grid.CellFormatting += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is XrefRow row) ApplySyncColor(e.CellStyle, row.SyncState); };
            connectorGrid.CellFormatting += (s, e) => { if (e.RowIndex >= 0 && connectorGrid.Rows[e.RowIndex].DataBoundItem is ConnectorFile file) ApplySyncColor(e.CellStyle, file.SyncState); };
            connectorGrid.SelectionChanged += (s, e) => { if (!selectingPair) ShowConnectorSelection(); };
            connectorGrid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && connectorGrid.Rows[e.RowIndex].DataBoundItem is ConnectorFile file) e.ToolTipText = file.Path; };
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is XrefRow row) e.ToolTipText = row.LocalPath + Environment.NewLine + row.MatchStatus + Environment.NewLine + row.Result; };
            return split;
        }

        private static Control CreateFilePane(string title, Control list, Control buttons, FileDetailsPanel details)
        {
            var pane = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8) };
            var heading = new Label { Dock = DockStyle.Top, Height = 32, Text = title, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Padding = new Padding(3, 5, 0, 0) };
            pane.Controls.Add(list); pane.Controls.Add(details); pane.Controls.Add(buttons); pane.Controls.Add(heading);
            return pane;
        }

        private static void CompactTable<T>(ReviewTable<T> review)
        {
            review.Search.Width = 160; review.Filter.Width = 110;
            // 复用共用表格，调整窄栏搜索工具的宽度；不改变筛选和勾选逻辑。
            foreach (Control control in review.Controls)
                if (control is FlowLayoutPanel tools)
                    foreach (Control item in tools.Controls)
                        if (item is Button button) { button.AutoSize = false; button.MinimumSize = new Size(58, 36); button.Width = 58; }
        }

        private void ConnectorColumn(string property, string title, int width, float fill = 0)
        {
            connectorGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = property, DataPropertyName = property, HeaderText = title, Width = width, ReadOnly = true, AutoSizeMode = fill > 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, FillWeight = fill > 0 ? fill : 100 });
        }

        private static List<ConnectorFile> BuildConnectorRows(FolderLink folder, Dictionary<string, List<string>> index, CancellationToken ct)
        {
            var files = new List<ConnectorFile>();
            foreach (var path in index.Values.SelectMany(matches => matches).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Paths.Relative(folder.Project, path);
                var parent = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar));
                var item = new ConnectorFile { Name = Path.GetFileName(path), Path = path, RelativePath = relative, Folder = string.IsNullOrEmpty(parent) ? "根目录" : parent };
                try { item.Modified = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { item.Modified = "不可读取"; }
                files.Add(item);
            }
            return files;
        }

        private void ClearConnectorView()
        {
            connectorIndex = null; indexedFolder = null;
            connectorFiles.Clear(); connectorTable.SetRows(connectorFiles);
            connectorDetails.Clear("请选择 Desktop Connector 同步目录。");
            foreach (var row in rows) { row.SyncState = SyncFeedback.Review; row.SyncedUtc = null; }
            grid.Refresh(); Count();
        }

        private static void ApplyTargetMatches(FolderLink folder, IEnumerable<XrefRow> selected, Dictionary<string, List<string>> index)
        {
            foreach (var row in selected)
            {
                var match = TargetFinder.Match(folder, row, index, out var message);
                // 同名候选不唯一时清空未确认映射，执行预览会阻止此项。
                row.CloudPath = match ?? "";
                row.MatchStatus = message;
            }
        }

        private XrefRow FocusedLocal => grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<XrefRow>().FirstOrDefault();
        private ConnectorFile FocusedConnector => connectorGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<ConnectorFile>().FirstOrDefault();

        private void ShowLocalSelection()
        {
            if (selectingPair || connectorTable == null) return;
            var row = FocusedLocal;
            if (row == null) { localDetails.Clear("点击左侧文件查看版本与修改日期。"); return; }
            string target = null;
            if (Directory.Exists(connector.Text))
            {
                var folder = FolderLink.Parse(connector.Text);
                if (connectorIndex != null && string.Equals(indexedFolder, folder.Project, StringComparison.OrdinalIgnoreCase)) target = TargetFinder.Match(folder, row, connectorIndex, out _);
                else { try { target = Paths.SyncTarget(folder.Project, row.CloudPath); } catch (ArgumentException) { } }
                SyncFeedback.Refresh(settings, folder, row, target); grid.Refresh(); UpdateConnectorStates();
            }
            _ = localDetails.ShowFileAsync(row.LocalPath, "同步状态：" + row.SyncState + (row.SyncedUtc.HasValue ? " · 校验时间：" + row.SyncedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "") + Environment.NewLine + "对应同步文件：" + (string.IsNullOrEmpty(row.CloudPath) ? "未指定" : row.CloudPath) + Environment.NewLine + "对应状态：" + row.MatchStatus + Environment.NewLine + "最近结果：" + row.Result + Environment.NewLine + "引用次数：" + row.Count + " · " + row.References + Environment.NewLine + "父文件：" + row.Parents);
            selectingPair = true;
            try
            {
                connectorGrid.ClearSelection();
                if (target != null)
                {
                    foreach (DataGridViewRow candidate in connectorGrid.Rows)
                        if (candidate.DataBoundItem is ConnectorFile file && string.Equals(file.Path, target, StringComparison.OrdinalIgnoreCase))
                        {
                            connectorGrid.CurrentCell = candidate.Cells["Name"]; candidate.Selected = true;
                            connectorGrid.FirstDisplayedScrollingRowIndex = candidate.Index;
                            break;
                        }
                    _ = connectorDetails.ShowFileAsync(target, "左侧 Xref：" + row.Name);
                }
                else connectorDetails.Clear("未找到唯一对应文件。可在右侧选取文件并点击“对应到左侧 Xref”。");
            }
            finally { selectingPair = false; }
        }

        private void ShowConnectorSelection()
        {
            var file = FocusedConnector;
            if (file == null) { connectorDetails.Clear("点击右侧文件查看版本与修改日期。"); return; }
            if (Directory.Exists(connector.Text))
            {
                var folder = FolderLink.Parse(connector.Text);
                foreach (var row in rows)
                {
                    string target = null; try { target = Paths.SyncTarget(folder.Project, row.CloudPath); } catch (ArgumentException) { }
                    if (string.Equals(target, file.Path, StringComparison.OrdinalIgnoreCase)) SyncFeedback.Refresh(settings, folder, row, target);
                }
                grid.Refresh(); UpdateConnectorStates();
            }
            _ = connectorDetails.ShowFileAsync(file.Path, "同步状态：" + file.SyncState + Environment.NewLine + "目录内路径：" + file.RelativePath);
        }

        private void MapSelectedConnector()
        {
            grid.EndEdit();
            var row = FocusedLocal; var file = FocusedConnector;
            if (row == null || file == null) throw new IOException("请先在左侧选中一个 Xref，再在右侧选中对应的 DWG。");
            if (!File.Exists(file.Path)) throw new IOException("所选 Connector 文件已不存在，请刷新列表。");
            row.CloudPath = file.Path; row.Selected = true; row.MatchStatus = "已手动对应"; row.Result = ""; row.SyncState = SyncFeedback.Pending; row.SyncedUtc = null;
            grid.Refresh(); Count(); Save(); ShowLocalSelection();
            status.Text = row.Name + " 已对应 " + file.RelativePath + "。请点击 Pull/Push 预览更新。";
        }

        private Control CreateSyncLegend()
        {
            var legend = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 27, Padding = new Padding(20, 0, 0, 0), WrapContents = false };
            foreach (var state in new[] { SyncFeedback.Synced, SyncFeedback.Pending, SyncFeedback.Processing, SyncFeedback.Failed })
            {
                var style = new DataGridViewCellStyle(); ApplySyncColor(style, state);
                legend.Controls.Add(new Label { Text = "● " + state, AutoSize = true, ForeColor = style.ForeColor, Margin = new Padding(0, 2, 18, 0) });
            }
            legend.Controls.Add(new Label { Text = "绿色：本地与 Connector 文件已校验一致", AutoSize = true, ForeColor = Color.SlateGray, Margin = new Padding(6, 2, 0, 0) });
            return legend;
        }

        private static void ApplySyncColor(DataGridViewCellStyle style, string state)
        {
            Color back, selected, ink;
            switch (state)
            {
                case SyncFeedback.Synced: back = Color.FromArgb(224, 245, 229); selected = Color.FromArgb(179, 224, 192); ink = Color.FromArgb(25, 102, 51); break;
                case SyncFeedback.Failed: back = Color.FromArgb(255, 231, 231); selected = Color.FromArgb(246, 193, 193); ink = Color.FromArgb(163, 38, 38); break;
                case SyncFeedback.Pending:
                case SyncFeedback.Review: back = Color.FromArgb(255, 247, 227); selected = Color.FromArgb(255, 229, 176); ink = Color.FromArgb(137, 91, 14); break;
                case SyncFeedback.Processing: back = Color.FromArgb(228, 239, 255); selected = Color.FromArgb(183, 213, 250); ink = Color.FromArgb(33, 90, 173); break;
                default: return;
            }
            style.BackColor = back; style.ForeColor = ink; style.SelectionBackColor = selected; style.SelectionForeColor = ink;
        }

        private void RefreshSyncFeedback(FolderLink folder, Dictionary<string, List<string>> index)
        {
            foreach (var row in rows)
            {
                var target = TargetFinder.Match(folder, row, index, out _);
                SyncFeedback.Refresh(settings, folder, row, target);
            }
            UpdateConnectorStates();
        }

        private void UpdateConnectorStates()
        {
            var mappings = new Dictionary<string, List<XrefRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                try
                {
                    var target = Paths.SyncTarget(connector.Text, row.CloudPath);
                    if (!mappings.TryGetValue(target, out var linked)) mappings[target] = linked = new List<XrefRow>();
                    linked.Add(row);
                }
                catch (ArgumentException) { }
            }
            foreach (var file in connectorFiles)
            {
                mappings.TryGetValue(file.Path, out var linked);
                file.SyncState = linked == null ? "未关联" : linked.Any(r => r.SyncState == SyncFeedback.Failed) ? SyncFeedback.Failed
                    : linked.Any(r => r.SyncState == SyncFeedback.Processing) ? SyncFeedback.Processing
                    : linked.All(r => r.SyncState == SyncFeedback.Synced) ? SyncFeedback.Synced : SyncFeedback.Pending;
            }
            connectorGrid.Refresh();
            Count();
        }
    }

    internal sealed class FileDetailsPanel : Panel
    {
        private readonly TextBox body = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
        private CancellationTokenSource reading;
        internal string DetailText => body.Text;
        internal bool ContextFirst { get; set; }
        public FileDetailsPanel(string title)
        {
            Dock = DockStyle.Bottom; Height = 194; Padding = new Padding(0, 8, 0, 0);
            Controls.Add(body);
            Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 25, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) });
            Clear("点击上方文件查看版本与修改日期。");
        }
        public void CancelRead() { reading?.Cancel(); }
        public void Clear(string text) { CancelRead(); body.Text = text; }
        public async Task ShowFileAsync(string path, string context, DetectionCache cache = null)
        {
            CancelRead();
            var request = new CancellationTokenSource(); reading = request;
            body.Text = "路径：" + path + Environment.NewLine + "正在读取文件版本与内容哈希……";
            try
            {
                var detail = await Task.Run(() => ReadFile(path, request.Token, cache), request.Token);
                if (!request.IsCancellationRequested && !IsDisposed) body.Text = ContextFirst ? context + Environment.NewLine + detail : detail + Environment.NewLine + context;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!request.IsCancellationRequested && !IsDisposed) body.Text = "路径：" + path + Environment.NewLine + "无法读取：" + ex.Message + Environment.NewLine + context; }
            finally { if (reading == request) reading = null; request.Dispose(); }
        }
        private static string ReadFile(string path, CancellationToken ct, DetectionCache cache)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return "路径：" + path + Environment.NewLine + "文件状态：不存在";
            var before = new FileInfo(path);
            var length = before.Length; var time = before.LastWriteTimeUtc;
            var format = Files.DwgFormat(path);
            string hash = null;
            var reused = cache != null && cache.TryGetHash(path, out hash);
            if (!reused)
            {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1024 * 1024]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                ct.ThrowIfCancellationRequested(); sha.TransformFinalBlock(buffer, 0, 0);
                hash = BitConverter.ToString(sha.Hash).Replace("-", "");
            }
            }
            var after = new FileInfo(path);
            if (after.Length != length || after.LastWriteTimeUtc != time) return "路径：" + path + Environment.NewLine + "文件在读取时发生变化，请重新点击查看。";
            return "文件：" + Path.GetFileName(path) + Environment.NewLine + "DWG 格式：" + format + "    大小：" + (length / 1024d).ToString("N1") + " KB" + Environment.NewLine + "修改日期：" + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + "内容版本（SHA-256" + (reused ? "，复用本次检测" : "") + "）：" + hash + Environment.NewLine + "路径：" + path;
        }
        protected override void Dispose(bool disposing) { if (disposing) CancelRead(); base.Dispose(disposing); }
    }
}
