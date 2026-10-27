using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class ManualNwcExportForm : Form
    {
        private sealed class Row
        {
            public string File { get; set; }
            public string Path { get; set; }
            public string Target { get; set; }
        }
        internal ManualNwcExportForm(List<BatchExportJob> jobs)
        {
            Text = "手动选择总装 · NWC 导出确认"; Width = 1150; Height = 640; MinimumSize = new Size(950, 530); StartPosition = FormStartPosition.CenterParent;
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (var spec in new[] { new[] { "File", "总装 DWG" }, new[] { "Path", "选择的完整路径" }, new[] { "Target", "NWC 输出完整路径" } }) grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = spec[0], HeaderText = spec[1], Width = spec[0] == "File" ? 230 : 380 });
            var table = new ReviewTable<Row>(grid, r => r.File + " " + r.Path + " " + r.Target, r => "手动选择");
            table.SetRows(jobs.Select(j => new Row { File = Path.GetFileName(j.Assembly.Path), Path = j.Assembly.Path, Target = j.Target }));
            var details = new TextBox { Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            Action show = () => { if (grid.CurrentRow?.DataBoundItem is Row row) details.Text = "总装：" + row.Path + "\r\n输出：" + row.Target; };
            grid.SelectionChanged += (s, e) => show(); Shown += (s, e) => show();
            var heading = new Label { Dock = DockStyle.Top, Height = 78, Padding = new Padding(14), Text = "导出 " + jobs.Count + " 份当前本地模型到日期子目录，不检查同步、不执行 Pull。\r\n使用原生 NWCOUT；已打开模型按当前状态导出，不重载引用。引用范围遵循 NWOPT（Convert XRefs）。\r\n不保存 DWG；缺失或卸载引用不保证出现在 NWC 中。已有同名 NWC 保留备份。" };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(10), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel }; var export = new RoundedButton { Text = "确认导出这些 DWG", Primary = true, Width = 180, DialogResult = DialogResult.OK };
            footer.Controls.Add(cancel); footer.Controls.Add(export); Controls.Add(table); Controls.Add(details); Controls.Add(heading); Controls.Add(footer); CancelButton = cancel; ModernUi.Apply(this);
        }
    }
}
