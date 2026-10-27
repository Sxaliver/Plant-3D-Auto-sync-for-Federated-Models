using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed partial class AssemblyNwcReviewForm : Form
    {
        private readonly List<AssemblyNwcReviewRow> rows;
        private readonly ReviewTable<AssemblyNwcReviewRow> table;
        private readonly DataGridView references = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 80 };
        private readonly Label selection = new Label { AutoSize = true, Margin = new Padding(10, 10, 0, 0) };
        internal int RowCount => table.Grid.Rows.Count;
        internal List<AssemblyNwcReviewRow> SelectedRows => rows.Where(r => r.Selected && File.Exists(r.Path)).ToList();
        internal AssemblyNwcReviewForm(List<AssemblyNwcReviewRow> data, string connector, string log)
        {
            rows = data;
            Text = "指定 17 项总装 · Connector / 本地对比与 NWC 导出建议"; Width = 1500; Height = 950; MinimumSize = new Size(1100, 780); StartPosition = FormStartPosition.CenterParent;
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", DataPropertyName = "Selected", HeaderText = "导出", Width = 50 });
            foreach (var spec in new[] { new[] { "Name", "总装完整文件名", "340" }, new[] { "CloudSummary", "Connector / 本地对比", "230" }, new[] { "Advice", "NWC 建议", "210" }, new[] { "NwcDate", "已记录 NWC 修改时间", "210" }, new[] { "LocalState", "本地引用检查", "200" }, new[] { "NwcPath", "已有 NWC 路径", "380" } })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = spec[0], DataPropertyName = spec[0], HeaderText = spec[1], Width = int.Parse(spec[2]), ReadOnly = true });
            grid.Columns["Name"].DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            table = new ReviewTable<AssemblyNwcReviewRow>(grid, r => r.Name + " " + r.Path + " " + r.Key + " " + r.Advice + " " + r.CloudSummary + " " + r.Reason, r => r.Advice);
            grid.Columns["Selected"].HeaderCell = new VisibleSelectionHeader<AssemblyNwcReviewRow>(r => File.Exists(r.Path), r => r.Selected, (r, value) => r.Selected = value, ShowCount);
            foreach (var spec in new[] { new[] { "Name", "Xref / 云端新增候选", "220" }, new[] { "State", "比较结果", "230" }, new[] { "Local", "本地完整路径", "440" }, new[] { "Connector", "Connector 完整路径", "460" }, new[] { "LocalDate", "本地修改日期", "200" }, new[] { "CloudDate", "Connector 修改日期", "200" }, new[] { "LocalHash", "本地 SHA-256", "440" }, new[] { "CloudHash", "Connector SHA-256", "440" } })
                references.Columns.Add(new DataGridViewTextBoxColumn { Name = spec[0], DataPropertyName = spec[0], HeaderText = spec[1], Width = int.Parse(spec[2]) });
            references.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && references.Rows[e.RowIndex].DataBoundItem is ReferenceComparison r) e.ToolTipText = r.Local + "\r\n⇐ " + r.Connector + "\r\n" + r.State + "\r\n本地：" + r.LocalHash + "\r\nConnector：" + r.CloudHash; };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is AssemblyNwcReviewRow r && r.Selected && !File.Exists(r.Path)) { r.Selected = false; grid.Refresh(); }
                ShowCount();
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || !(grid.Rows[e.RowIndex].DataBoundItem is AssemblyNwcReviewRow r)) return;
                e.CellStyle.BackColor = !r.CanExport ? Color.Cornsilk : r.NeedsExport ? Color.FromArgb(232, 240, 255) : Color.Honeydew;
                if (e.ColumnIndex == 0) grid.Rows[e.RowIndex].Cells[0].ReadOnly = !File.Exists(r.Path);
            };
            void ShowReferenceData()
            {
                if (!(grid.CurrentRow?.DataBoundItem is AssemblyNwcReviewRow r)) { references.DataSource = null; details.Clear(); return; }
                references.DataSource = r.References;
                details.Text = "总装：" + r.Path + "\r\n" + r.Advice + "：" + r.Reason + "\r\n已有 NWC：" + r.NwcPath + "\r\n检查记录：" + log;
            }
            grid.SelectionChanged += (s, e) => ShowReferenceData();
            grid.CurrentCellChanged += (s, e) => ShowReferenceData();
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is AssemblyNwcReviewRow r) e.ToolTipText = r.Path + "\r\n" + r.Advice + "\r\n" + r.Reason; };
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1450, 630), SplitterDistance = 365, Panel1MinSize = 160, Panel2MinSize = 130 };
            split.Panel1.Controls.Add(table); split.Panel2.Controls.Add(references); split.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 28, Text = "选中总装的逐 Xref 比较 · 滚动查看路径、日期与哈希", Padding = new Padding(8, 6, 0, 0) });
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(9), WrapContents = false };
            var export = new RoundedButton { Text = "导出勾选 NWC", Primary = true }; export.Click += (s, e) => { grid.EndEdit(); if (SelectedRows.Count == 0) { ModernUi.Message(this, "请选择", "请勾选存在的本地总装，无需先 Pull。" ); return; } DialogResult = DialogResult.OK; Close(); }; footer.Controls.Add(export); footer.Controls.Add(selection);
            var close = new RoundedButton { Text = "关闭", DialogResult = DialogResult.Cancel }; footer.Controls.Add(close); CancelButton = close;
            var note = new TextBox { Dock = DockStyle.Top, Height = 68, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Text = "Connector 根目录：" + connector + "\r\n比较仅供参考，检查不 Pull、不改写 DWG；手动导出只使用当前本地模型，不要求先同步。导出后由你手动上传 NWC。\r\n蓝色：建议导出；绿色：已有匹配 NWC（不表示已上传）；黄色：对比诊断，不阻止存在的本地总装导出。" };
            Controls.Add(split); Controls.Add(details); Controls.Add(footer); Controls.Add(note); ModernUi.Apply(this); table.SetRows(rows); ShowCount();
        }
        private void ShowCount() => selection.Text = "本地总装 " + rows.Count(r => File.Exists(r.Path)) + "；建议导出 " + rows.Count(r => r.CanExport && r.NeedsExport) + "；已勾选 " + SelectedRows.Count;
    }
}
