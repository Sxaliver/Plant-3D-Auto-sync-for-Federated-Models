using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed partial class NwcTimestampCompareForm : Form
    {
        private readonly TextBox local = new TextBox(), connector = new TextBox();
        private readonly ReviewTable<NwcTimestampRow> table;
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(10, 6, 0, 0) };
        private readonly Label selected = new Label { AutoSize = true, Margin = new Padding(12, 11, 0, 0) };
        private readonly List<Control> inputs = new List<Control>();
        private readonly VisibleSelectionHeader<NwcTimestampRow> selectionHeader;
        private List<NwcTimestampRow> rows = new List<NwcTimestampRow>();
        private CancellationTokenSource work;
        internal string LocalRoot => local.Text;
        internal string ConnectorRoot => connector.Text;
        internal NwcTimestampCompareForm(string localRoot, string connectorRoot)
        {
            Text = "NWC 日期对比"; Width = 1400; Height = 820; MinimumSize = new Size(1050, 650); StartPosition = FormStartPosition.CenterParent;
            local.Text = localRoot ?? ""; connector.Text = connectorRoot ?? "";
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 95, ColumnCount = 3, RowCount = 2, Padding = new Padding(12, 5, 12, 5) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            AddPath(fields, 0, "本地 NWC", local); AddPath(fields, 1, "云端 NWC", connector);
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", DataPropertyName = "Selected", HeaderText = "上传清单", Width = 48 });
            foreach (var spec in new[] { new[] { "Name", "NWC 文件", "350" }, new[] { "LocalDate", "本地修改时间", "185" }, new[] { "ConnectorDate", "云端修改时间", "185" }, new[] { "State", "状态", "190" }, new[] { "Local", "本地路径", "460" }, new[] { "Connector", "云端路径", "460" } })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = spec[0], DataPropertyName = spec[0], HeaderText = spec[1], Width = int.Parse(spec[2]), ReadOnly = true });
            grid.Columns["Name"].DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            grid.Columns["Name"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; grid.Columns["Name"].MinimumWidth = 260;
            grid.Columns["Local"].Visible = false; grid.Columns["Connector"].Visible = false;
            selectionHeader = new VisibleSelectionHeader<NwcTimestampRow>(r => r.RecommendUpload, r => r.Selected, (r, value) => r.Selected = value, ShowCount);
            grid.Columns["Selected"].HeaderCell = selectionHeader;
            table = new ReviewTable<NwcTimestampRow>(grid, r => r.Name + " " + r.Local + " " + r.Connector + " " + r.State, r => r.State);
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => ShowCount(); grid.DataError += (s, e) => e.ThrowException = false;
            void ShowRow() { if (grid.CurrentRow?.DataBoundItem is NwcTimestampRow row) details.Text = row.State + "\r\n本地：" + row.Local + "\r\n云端：" + row.Connector; else details.Clear(); }
            grid.SelectionChanged += (s, e) => ShowRow(); grid.CurrentCellChanged += (s, e) => ShowRow();
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is NwcTimestampRow r) e.ToolTipText = r.Local + "\r\n" + r.Connector + "\r\n" + r.Details; };
            grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || !(grid.Rows[e.RowIndex].DataBoundItem is NwcTimestampRow r)) return;
                var property = grid.Columns[e.ColumnIndex].Name;
                if (property == "LocalDate" || property == "ConnectorDate")
                {
                    var value = property == "LocalDate" ? r.LocalUtc : r.ConnectorUtc;
                    e.Value = value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss.fff") : "—"; e.FormattingApplied = true;
                }
                else if (property == "State")
                {
                    e.Value = r.State.StartsWith("日期相同", StringComparison.Ordinal) ? "日期相同" : r.State.StartsWith("本地缺失", StringComparison.Ordinal) ? "本地缺失" : r.State.Contains("歧义") ? "对应待确认" : r.State.Replace("，建议上传", " · 待上传"); e.FormattingApplied = true;
                }
                e.CellStyle.BackColor = r.RecommendUpload ? Color.FromArgb(232, 240, 255) : r.State.StartsWith("日期相同", StringComparison.Ordinal) ? Color.Honeydew : Color.Cornsilk;
            };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 57, Padding = new Padding(9), WrapContents = false };
            var check = new RoundedButton { Text = "比较日期", Primary = true }; check.Click += async (s, e) => await RefreshDates(); footer.Controls.Add(check); inputs.Add(check);
            var copy = new RoundedButton { Text = "复制路径" }; copy.Click += (s, e) => { grid.EndEdit(); var paths = rows.Where(r => r.Selected && !string.IsNullOrEmpty(r.Local)).Select(r => r.Local).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); if (paths.Count == 0) { status.Text = "请勾选本地 NWC。"; return; } try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); status.Text = "已复制 " + paths.Count + " 个路径"; } catch (Exception ex) { status.Text = "复制失败：" + ex.Message; } }; footer.Controls.Add(copy); inputs.Add(copy);
            var more = new RoundedButton { Text = "详情" }; more.Click += (s, e) => { if (grid.CurrentRow?.DataBoundItem is NwcTimestampRow r) ModernUi.Message(this, "NWC 详情", r.State + "\r\n" + r.Details); }; footer.Controls.Add(more); inputs.Add(more);
            footer.Controls.Add(selected); var close = new RoundedButton { Text = "关闭", DialogResult = DialogResult.Cancel }; footer.Controls.Add(close); CancelButton = close;
            var note = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(12, 4, 0, 0), Text = "仅比较修改时间（新加坡） · 不比较内容 · 上传由你手动完成" };
            Controls.Add(table); Controls.Add(details); Controls.Add(status); Controls.Add(footer); Controls.Add(note); Controls.Add(fields); ModernUi.Apply(this);
            foreach (var box in new[] { local, connector }) box.TextChanged += (s, e) => { rows.Clear(); table.SetRows(rows); ShowCount(); status.Text = "目录已设置"; };
            FormClosing += (s, e) => { if (work != null) { work.Cancel(); e.Cancel = true; } };
            Shown += async (s, e) => { if (Directory.Exists(local.Text) && Directory.Exists(connector.Text)) await RefreshDates(); else status.Text = "请选择两侧 NWC 目录"; };
            ShowCount();
        }
        private void AddPath(TableLayoutPanel fields, int row, string title, TextBox box)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); fields.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, row);
            box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 7, 8, 0); fields.Controls.Add(box, 1, row); inputs.Add(box);
            var browse = new RoundedButton { Text = "浏览" }; browse.Click += (s, e) => { using (var picker = new FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : "", Description = title }) if (picker.ShowDialog(this) == DialogResult.OK) box.Text = picker.SelectedPath; }; fields.Controls.Add(browse, 2, row); inputs.Add(browse);
        }
        private void ShowCount() => selected.Text = "待上传 " + rows.Count(r => r.RecommendUpload) + " · 已勾选 " + rows.Count(r => r.Selected);
        private async Task RefreshDates()
        {
            if (work != null) return;
            work = new CancellationTokenSource(); inputs.ForEach(c => c.Enabled = false); table.Enabled = false; status.Text = "读取 NWC 文件修改日期……";
            try
            {
                var l = local.Text; var c = connector.Text; rows = await Task.Run(() => NwcTimestampCompare.Check(l, c, work.Token)); table.SetRows(rows); ShowCount(); status.Text = "比较完成 · " + rows.Count + " 项";
            }
            catch (OperationCanceledException) { status.Text = "日期比较已取消。"; }
            catch (Exception ex) { rows.Clear(); table.SetRows(rows); ShowCount(); status.Text = ex.Message; }
            finally { work.Dispose(); work = null; inputs.ForEach(c => c.Enabled = true); table.Enabled = true; }
        }
    }
}
