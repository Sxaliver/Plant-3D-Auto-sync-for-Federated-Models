using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace IWMF.PlantModelSync
{
    // 表头只操作当前可见行；搜索、分类和排序都不清除被隐藏行的勾选。
    internal sealed class VisibleSelectionHeader<T> : DataGridViewColumnHeaderCell where T : class
    {
        private readonly Func<T, bool> eligible, selected;
        private readonly Action<T, bool> set;
        private readonly Action changed;
        private DataGridView observed;
        internal VisibleSelectionHeader(Func<T, bool> canSelect, Func<T, bool> isSelected, Action<T, bool> setSelected, Action onChanged = null)
        {
            eligible = canSelect; selected = isSelected; set = setSelected; changed = onChanged;
            ToolTipText = "勾选 / 清除当前可见的可操作行；不会更改筛选隐藏项。部分选中时再次点击全选。";
        }
        internal CheckState VisibleState
        {
            get
            {
                var all = VisibleRows(); var rows = all.Where(eligible).ToList();
                var count = rows.Count(selected);
                return !all.Any(selected) ? CheckState.Unchecked : count == rows.Count ? CheckState.Checked : CheckState.Indeterminate;
            }
        }
        private T[] VisibleRows() => DataGridView == null ? new T[0] : DataGridView.Rows.Cast<DataGridViewRow>().Where(r => r.Visible).Select(r => r.DataBoundItem).OfType<T>().ToArray();
        protected override void OnDataGridViewChanged()
        {
            if (observed != null) { observed.CellValueChanged -= RefreshHeader; observed.DataBindingComplete -= RefreshHeader; observed.RowsAdded -= RefreshHeader; observed.RowsRemoved -= RefreshHeader; }
            base.OnDataGridViewChanged(); observed = DataGridView;
            if (observed != null) { observed.CellValueChanged += RefreshHeader; observed.DataBindingComplete += RefreshHeader; observed.RowsAdded += RefreshHeader; observed.RowsRemoved += RefreshHeader; }
        }
        private void RefreshHeader(object sender, EventArgs e) { if (ColumnIndex >= 0) DataGridView?.InvalidateCell(ColumnIndex, -1); }
        internal void ToggleVisible()
        {
            if (DataGridView == null || !DataGridView.Enabled) return;
            DataGridView.EndEdit();
            var check = VisibleState != CheckState.Checked;
            foreach (var row in VisibleRows()) if (!check || eligible(row)) set(row, check);
            DataGridView.Refresh(); changed?.Invoke();
        }
        protected override void OnMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnMouseClick(e); if (e.Button == MouseButtons.Left) ToggleVisible();
        }
        protected override void OnKeyDown(KeyEventArgs e, int rowIndex)
        {
            if (e.KeyCode == Keys.Space) { ToggleVisible(); e.Handled = true; } else base.OnKeyDown(e, rowIndex);
        }
        protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex, DataGridViewElementStates dataGridViewElementState, object value, object formattedValue, string errorText, DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, dataGridViewElementState, value, "", errorText, cellStyle, advancedBorderStyle, paintParts);
            var state = VisibleState;
            var check = state == CheckState.Checked ? CheckBoxState.CheckedNormal : state == CheckState.Indeterminate ? CheckBoxState.MixedNormal : CheckBoxState.UncheckedNormal;
            if (DataGridView?.Enabled != true || (!VisibleRows().Any(eligible) && !VisibleRows().Any(selected))) check = CheckBoxState.UncheckedDisabled;
            var size = CheckBoxRenderer.GetGlyphSize(graphics, check);
            CheckBoxRenderer.DrawCheckBox(graphics, new Point(cellBounds.Left + (cellBounds.Width - size.Width) / 2, cellBounds.Top + (cellBounds.Height - size.Height) / 2), check);
        }
    }
}
