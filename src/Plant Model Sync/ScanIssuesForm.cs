using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class ScanIssue
    {
        public bool Informational { get; set; }
        public string Category { get; set; }
        public string File { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
    }

    internal static class ScanIssues
    {
        internal static List<ScanIssue> Collect(WorkflowSession session)
        {
            var rows = new List<ScanIssue>();
            if (session == null) return rows;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string category, string file, string path, string message, bool informational = false)
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                var key = category + "\n" + path + "\n" + file + "\n" + message;
                if (seen.Add(key)) rows.Add(new ScanIssue { Category = category, File = file ?? "", Path = path ?? "", Message = message, Informational = informational });
            }
            foreach (var assembly in session.Assemblies)
            {
                foreach (var deferred in assembly.Scan?.DeferredReferences ?? new List<string>()) Add("引用信息（未参与同步）", assembly.DisplayName, assembly.Path, deferred, true);
                foreach (var warning in assembly.Scan?.Warnings ?? new List<string>())
                {
                    Add("总装引用", assembly.DisplayName, assembly.Path, warning);
                    // 汇总警告可能是同一条总装诊断的副本，不重复展示。
                    coveredWarnings.Add(warning);
                    coveredWarnings.Add(assembly.Name + "：" + warning);
                    coveredWarnings.Add(assembly.DisplayName + "：" + warning);
                }
            }
            foreach (var source in session.Sources)
            {
                var name = source.Name ?? System.IO.Path.GetFileName(source.Path ?? "");
                foreach (var issue in source.Diagnostics) Add("源文件", name, source.Path, issue);
                foreach (var issue in source.MissingReferenceIssues) Add("引用路径", name, source.Path, issue);
                foreach (var local in source.AmbiguousLocals) Add("对应歧义", name, local, "对应目标待确认；云端：" + source.Path);
                foreach (var plan in source.Plans) Add("同步阻止", name, plan.Local, plan.Blocked);
            }
            foreach (var warning in session.Warnings.Where(w => !coveredWarnings.Contains(w))) Add("扫描", "", "", warning);
            return rows;
        }
    }

    internal sealed partial class ScanIssuesForm : Form
    {
        private readonly ReviewTable<ScanIssue> table;
        private readonly TextBox details = new TextBox { Dock = DockStyle.Bottom, Height = 130, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label summary = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 8, 0, 0), AutoEllipsis = true };
        private readonly Label empty;
        private readonly List<ScanIssue> rows;
        private readonly bool checkedSession;
        internal ScanIssuesForm(WorkflowSession session)
        {
            Text = "查看问题"; Width = 1100; Height = 700; MinimumSize = new Size(940, 580); StartPosition = FormStartPosition.CenterParent;
            checkedSession = session?.Checked == true;
            rows = ScanIssues.Collect(session);
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", DataPropertyName = "Category", HeaderText = "类型", Width = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", DataPropertyName = "File", HeaderText = "文件", Width = 290 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", DataPropertyName = "Message", HeaderText = "问题", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 260 });
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            table = new ReviewTable<ScanIssue>(grid, r => r.Category + " " + r.File + " " + r.Path + " " + r.Message, r => r.Category);
            empty = grid.Controls.OfType<Label>().Single();
            grid.SelectionChanged += (s, e) => ShowDetails();
            table.Search.TextChanged += (s, e) => UpdateEmpty();
            table.Filter.SelectedIndexChanged += (s, e) => UpdateEmpty();
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var close = new RoundedButton { Text = "关闭", DialogResult = DialogResult.Cancel }; footer.Controls.Add(close);
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) }; body.Controls.Add(table); body.Controls.Add(details); body.Controls.Add(summary);
            Controls.Add(body); Controls.Add(footer); CancelButton = close; ModernUi.Apply(this);
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (DataGridViewColumn column in grid.Columns) column.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is ScanIssue issue) e.ToolTipText = issue.File + "\r\n" + issue.Path + "\r\n" + issue.Message; };
            table.SetRows(rows);
            var problems = rows.Count(r => !r.Informational); var information = rows.Count - problems;
            summary.Text = checkedSession ? (problems == 0 ? "未发现问题" : "上次检查 · " + problems + " 个问题") : session == null ? "尚未检查，请先检查更新" : "检查尚未完成 · " + problems + " 个已记录问题";
            if (information > 0) summary.Text += " · " + information + " 条引用信息";
            UpdateEmpty(); ShowDetails();
        }
        private void UpdateEmpty()
        {
            // 筛选为空和真正没有问题必须区分，未检查不能报告为通过。
            empty.Text = rows.Count > 0 ? "未找到匹配问题" : checkedSession ? "未发现问题" : "尚无检查结果";
            ShowDetails();
        }
        private void ShowDetails()
        {
            var issue = table.Grid.CurrentRow?.DataBoundItem as ScanIssue;
            details.Text = table.Grid.SelectedRows.Count == 0 || issue == null ? "" : issue.Category + " · " + issue.File + "\r\n路径：" + issue.Path + "\r\n" + issue.Message;
        }
    }
}
