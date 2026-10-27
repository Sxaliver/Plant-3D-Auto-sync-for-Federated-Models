using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class UpdateLogForm : Form
    {
        private readonly ComboBox reports = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Height = 30 };
        private readonly ReviewTable<UpdateReportRow> table;
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Height = 140, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        internal UpdateLogForm(string folder, string preferred = null)
        {
            Text = "更新日志 · 文件 / 新增 Xref / 总装检查"; Width = 1320; Height = 780; MinimumSize = new Size(1000, 650); StartPosition = FormStartPosition.CenterParent;
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (var spec in new[] { new[] { "Time", "操作时间" }, new[] { "Operation", "操作" }, new[] { "File", "文件 / 总装编号" }, new[] { "State", "结果" }, new[] { "SourceModifiedTime", "Connector 源修改时间" }, new[] { "LocalModifiedTime", "本地文件修改时间" }, new[] { "Assembly", "涉及总装" }, new[] { "Local", "本地完整路径" }, new[] { "Connector", "Connector 完整路径" }, new[] { "Backup", "备份" }, new[] { "BeforeHash", "更新前 SHA-256" }, new[] { "AfterHash", "更新后 SHA-256" } })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = spec[0], DataPropertyName = spec[0], HeaderText = spec[1], Width = spec[0] == "Time" ? 185 : spec[0] == "Operation" ? 105 : spec[0] == "File" ? 230 : 260 });
            table = new ReviewTable<UpdateReportRow>(grid, r => r.Time + " " + r.Operation + " " + r.File + " " + r.State + " " + r.Local + " " + r.Connector + " " + r.Assembly + " " + r.Details, r => r.Operation);
            grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || !(grid.Rows[e.RowIndex].DataBoundItem is UpdateReportRow row)) return;
                var value = row.State ?? ""; var failed = value.Contains("失败") || value.Contains("不存在") || value.Contains("重复");
                var pending = value.Contains("待") || value.Contains("未") || value.Contains("中止");
                var done = value.Contains("通过") || value.Contains("完成") || value.Contains("已新增") || value.Contains("已存在");
                if (!failed && !pending && !done) return;
                e.CellStyle.BackColor = failed ? Color.MistyRose : pending ? Color.Cornsilk : Color.Honeydew;
                e.CellStyle.ForeColor = failed ? Color.Firebrick : pending ? Color.DarkGoldenrod : Color.DarkGreen;
            };
            grid.SelectionChanged += (s, e) => { if (grid.CurrentRow?.DataBoundItem is UpdateReportRow row) details.Text = "操作时间：" + row.Time + "\r\nConnector 源修改时间：" + row.SourceModifiedTime + "\r\n本地文件修改时间：" + row.LocalModifiedTime + "\r\n本地：" + row.Local + "\r\n云端：" + row.Connector + "\r\n总装：" + row.Assembly + "\r\n备份：" + row.Backup + "\r\n" + row.State + "\r\n" + row.Details; };
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).ToList() : new List<string>();
            if (!string.IsNullOrEmpty(preferred)) { preferred = Path.ChangeExtension(preferred, ".json"); if (File.Exists(preferred) && !files.Contains(preferred, StringComparer.OrdinalIgnoreCase)) files.Insert(0, preferred); }
            foreach (var file in files) reports.Items.Add(file);
            reports.SelectedIndexChanged += (s, e) => LoadReport(reports.SelectedItem as string);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(9), FlowDirection = FlowDirection.RightToLeft };
            var close = new RoundedButton { Text = "关闭", DialogResult = DialogResult.Cancel }; footer.Controls.Add(close);
            var browse = new RoundedButton { Text = "浏览日志…" }; browse.Click += (s, e) => { using (var picker = new OpenFileDialog { Filter = "更新日志 (*.json)|*.json", InitialDirectory = folder }) if (picker.ShowDialog(this) == DialogResult.OK) { if (!reports.Items.Contains(picker.FileName)) reports.Items.Insert(0, picker.FileName); reports.SelectedItem = picker.FileName; } }; footer.Controls.Add(browse);
            Controls.Add(table); Controls.Add(details); Controls.Add(reports); Controls.Add(footer); CancelButton = close; ModernUi.Apply(this);
            if (files.Count > 0) reports.SelectedItem = preferred != null && files.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? files.First(f => string.Equals(f, preferred, StringComparison.OrdinalIgnoreCase)) : files[0];
            else table.SetRows(new[] { new UpdateReportRow { Operation = "提示", State = "尚无更新日志；执行一键更新后在此查看。" } });
        }
        private void LoadReport(string path)
        {
            if (path == null) return;
            try { table.SetRows(UpdateReports.Read(path)); }
            catch (Exception ex) { table.SetRows(new[] { new UpdateReportRow { Operation = "日志读取", State = "读取失败", Local = path, Details = ex.Message } }); }
        }
        internal int RowCount => table.Grid.Rows.Count;
        internal UpdateLogForm(IEnumerable<UpdateReportRow> rows, string title, bool compact = false) : this("", null)
        {
            Text = title; reports.Visible = false;
            var snapshot = rows.ToList(); foreach (var row in snapshot.Where(r => string.IsNullOrEmpty(r.Time))) row.Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz");
            table.SetRows(snapshot);
            if (compact)
            {
                foreach (DataGridViewColumn column in table.Grid.Columns) column.Visible = column.Name == "File" || column.Name == "State" || column.Name == "Local";
                table.Grid.Columns["File"].HeaderText = "总装 DWG"; table.Grid.Columns["File"].Width = 420;
                table.Grid.Columns["State"].HeaderText = "状态"; table.Grid.Columns["State"].Width = 190;
                table.Grid.Columns["Local"].HeaderText = "路径"; table.Grid.Columns["Local"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                table.Filter.Visible = false;
            }
        }
    }
}
