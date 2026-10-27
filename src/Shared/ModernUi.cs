using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using IWMF.ColumnAutoModel.Core;

namespace IWMF.ColumnAutoModel
{
    internal sealed class RoundedButton : Button
    {
        public bool Primary { get; set; }
        private bool hover, pressed;
        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            AutoSize = true; MinimumSize = new Size(82, 36); Padding = new Padding(14, 7, 14, 7);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? ModernUi.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = !Enabled ? Color.FromArgb(231, 235, 240) : Primary ? (pressed ? Color.FromArgb(28, 66, 157) : hover ? Color.FromArgb(39, 87, 190) : ModernUi.Accent) : hover ? Color.FromArgb(225, 235, 250) : Color.White;
            using (var path = ModernUi.Round(new RectangleF(1, 1, Width - 3, Height - 3), 9))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(Focused ? ModernUi.Accent : Primary ? fill : ModernUi.Border, Focused ? 2 : 1))
            { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, !Enabled ? Color.Gray : Primary ? Color.White : ModernUi.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    internal static class ModernUi
    {
        public static readonly Color Background = Color.FromArgb(244, 247, 251), Ink = Color.FromArgb(32, 47, 68), Accent = Color.FromArgb(49, 105, 218), Border = Color.FromArgb(217, 226, 238);
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2; var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public static void Apply(Form form)
        {
            form.BackColor = Background; form.ForeColor = Ink;
            form.Font = new Font("Microsoft YaHei UI", 9F); form.AutoScaleMode = AutoScaleMode.Dpi;
            void Visit(Control parent)
            {
                foreach (Control c in parent.Controls)
                {
                    if (c is DataGridView grid) StyleGrid(grid);
                    else if (c is ComboBox combo) { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Color.White; }
                    else if (c is TextBox text) { text.BorderStyle = BorderStyle.FixedSingle; text.BackColor = Color.White; }
                    else if (c is NumericUpDown number) number.BorderStyle = BorderStyle.FixedSingle;
                    else if (c is CheckedListBox list) { list.BorderStyle = BorderStyle.None; list.BackColor = Color.White; }
                    Visit(c);
                }
            }
            Visit(form);
        }
        public static void StyleGrid(DataGridView grid)
        {
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(237, 241, 247); grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; grid.ColumnHeadersHeight = 38;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(233, 239, 248), ForeColor = Ink, Padding = new Padding(8, 0, 8, 0), SelectionBackColor = Color.FromArgb(233, 239, 248), SelectionForeColor = Ink };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Color.FromArgb(220, 234, 255), SelectionForeColor = Color.FromArgb(22, 65, 139), Padding = new Padding(7, 3, 7, 3) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 254);
            grid.RowTemplate.Height = 34; grid.RowHeadersVisible = false; grid.AllowUserToOrderColumns = true;
        }
        public static Control Header(string title, string subtitle)
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 85, Padding = new Padding(22, 12, 22, 8), BackColor = Color.White };
            panel.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.FromArgb(94, 111, 135) });
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 32, Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), ForeColor = Ink }); return panel;
        }
        public static void Message(IWin32Window owner, string title, string text)
        {
            using (var form = new Form { Text = title, Width = 720, Height = 420, MinimumSize = new Size(540, 320), StartPosition = FormStartPosition.CenterParent })
            {
                var body = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), Margin = new Padding(20) };
                var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) }; content.Controls.Add(body); form.Controls.Add(content);
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(14, 8, 14, 8), FlowDirection = FlowDirection.RightToLeft };
                var close = new RoundedButton { Text = "知道了", Primary = true, DialogResult = DialogResult.OK }; footer.Controls.Add(close); form.Controls.Add(footer);
                form.Controls.Add(Header(title, "操作反馈 · 可选择并复制详细信息")); form.AcceptButton = close; form.CancelButton = close; Apply(form); form.ShowDialog(owner);
            }
        }
    }

    internal sealed class ReviewTable<T> : UserControl
    {
        public DataGridView Grid { get; }
        public Func<T, int> SortGroup { get; set; }
        internal TextBox Search { get; } = new TextBox { Width = 245, AccessibleName = "搜索名称、ID 或关联依据" };
        internal ComboBox Filter { get; } = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "筛选分类" };
        private readonly Label count = new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        private readonly Label empty = new Label { Text = "暂无记录。请调整筛选条件或选择标高后查看关联元素。", AutoSize = true, BackColor = Color.White, ForeColor = Color.SlateGray };
        private readonly Func<T, string> text, category;
        private List<T> all = new List<T>();
        private bool updating;
        public ReviewTable(DataGridView grid, Func<T, string> searchable, Func<T, string> grouping)
        {
            Dock = DockStyle.Fill; Grid = grid; text = searchable; category = grouping;
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(3, 8, 0, 0), AutoScroll = true, WrapContents = false };
            tools.Controls.Add(new Label { Text = "搜索", AutoSize = true, Margin = new Padding(0, 6, 5, 0) }); tools.Controls.Add(Search); tools.Controls.Add(Filter);
            var clear = Ui.Button("重置"); clear.Click += (s, e) => { updating = true; Search.Clear(); Filter.SelectedIndex = 0; updating = false; ApplyFilter(); }; tools.Controls.Add(clear); tools.Controls.Add(count);
            Controls.Add(grid); Controls.Add(tools); grid.Controls.Add(empty); empty.Location = new Point(18, 62);
            Search.TextChanged += (s, e) => ApplyFilter(); Filter.SelectedIndexChanged += (s, e) => ApplyFilter();
            grid.SelectionChanged += (s, e) => UpdateCount();
            grid.DataBindingComplete += (s, e) => { foreach (DataGridViewColumn c in grid.Columns) if (!(c is DataGridViewCheckBoxColumn)) c.SortMode = DataGridViewColumnSortMode.Automatic; UpdateCount(); };
            grid.RowsAdded += (s, e) => UpdateCount(); grid.RowsRemoved += (s, e) => UpdateCount();
            Filter.Items.Add("全部分类"); Filter.SelectedIndex = 0;
        }
        public void SetRows(IEnumerable<T> rows)
        {
            updating = true; all = rows.ToList(); var selected = Filter.SelectedItem as string;
            Filter.Items.Clear(); Filter.Items.Add("全部分类");
            Filter.Items.AddRange(all.Select(category).Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderBy(v => v).Cast<object>().ToArray());
            Filter.SelectedIndex = selected != null && Filter.Items.Contains(selected) ? Filter.Items.IndexOf(selected) : 0;
            updating = false; ApplyFilter();
        }
        public void ApplyFilter()
        {
            if (updating) return;
            Grid.EndEdit();
            var selected = new HashSet<T>(Grid.SelectedRows.Cast<DataGridViewRow>().Where(r => r.DataBoundItem is T).Select(r => (T)r.DataBoundItem));
            var sort = Grid.SortedColumn?.DataPropertyName; var order = Grid.SortOrder;
            var rows = ReviewList<T>.Filter(all, Search.Text, text, r => Filter.SelectedIndex <= 0 || category(r) == (string)Filter.SelectedItem);
            Grid.DataSource = new ReviewList<T>(rows, SortGroup);
            if (sort != null) { var column = Grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == sort); if (column != null) Grid.Sort(column, order == SortOrder.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending); }
            Grid.ClearSelection(); foreach (DataGridViewRow row in Grid.Rows) row.Selected = selected.Contains((T)row.DataBoundItem);
            empty.Visible = rows.Count == 0; empty.BringToFront(); UpdateCount();
        }
        private void UpdateCount() { count.Text = $"显示 {Grid.Rows.Count} / {all.Count} · 已选 {Grid.SelectedRows.Count}"; }
    }
}
