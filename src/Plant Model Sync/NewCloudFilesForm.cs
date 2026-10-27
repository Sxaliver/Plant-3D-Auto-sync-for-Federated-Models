using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed partial class NewCloudFilesForm : Form
    {
        private readonly NewFileBatch batch;
        private readonly WorkflowSession session;
        private readonly DataGridView grid;
        private readonly List<ExistingUpdateRow> existing = new List<ExistingUpdateRow>();
        private Action updateDetails;
        internal List<TransferPlan> ApprovedExisting => existing.Where(r => r.Selected && r.Plan.Blocked == null && (!r.Plan.RequireExistingLocal || System.IO.File.Exists(r.Plan.Local))).Select(r => r.Plan).ToList();
        internal NewCloudFilesForm(WorkflowSession current, NewFileBatch plan, List<TransferPlan> updates = null, List<string> diagnostics = null)
        {
            session = current; batch = plan;
            Text = updates == null ? "新增云端 DWG → 本地镜像 → 加入总装" : "一键更新 · 已有文件 + 新增 Xref · 执行前确认"; Width = 1240; Height = 780; MinimumSize = new Size(1000, 650); StartPosition = FormStartPosition.CenterParent;
            grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", DataPropertyName = "Selected", HeaderText = "新增", Width = 50 });
            Add("Name", "源 DWG", 175); Add("LocalState", "本地计划", 125); Add("TargetSummary", "加入的总装（双击选择，可多选）", 280); Add("Local", "保存到本地完整路径", 420);
            foreach (var property in new[] { "X", "Y", "Z", "Scale", "Rotation" })
            {
                Add(property, property == "Scale" ? "统一比例" : property == "Rotation" ? "旋转（度）" : "WCS " + property, property == "X" || property == "Y" ? 115 : 90);
                grid.Columns[property].ReadOnly = false;
                grid.Columns[property].DefaultCellStyle.Format = "0.########";
                grid.Columns[property].DisplayIndex = property == "X" ? 3 : property == "Y" ? 4 : property == "Z" ? 5 : property == "Scale" ? 6 : 7;
            }
            grid.DataSource = batch.Files;
            var table = new ReviewTable<NewCloudFile>(grid, f => f.Name + " " + f.Local + " " + f.Cloud + " " + f.TargetSummary, f => f.LocalState);
            table.SetRows(batch.Files);
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty && grid.CurrentCell?.OwningColumn.Name == "Selected") grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellDoubleClick += (s, e) => { if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "TargetSummary") ChooseTargets(); };
            grid.DataError += (s, e) => { e.ThrowException = false; e.Cancel = true; ModernUi.Message(this, "数值无效", "坐标、比例和旋转请输入有效数字。"); };
            var details = new TextBox { Dock = DockStyle.Bottom, Height = 115, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            Action showDetails = () => { if (Current != null) details.Text = "云端源：" + Current.Cloud + "\r\n本地：" + Current.Local + "\r\n本地 SHA-256：" + Current.Transfer.LocalHash + "\r\nConnector SHA-256：" + Current.Transfer.ConnectorHash + "\r\n计划：" + Current.LocalState + "\r\n总装：" + string.Join(" | ", Current.Targets); };
            updateDetails = showDetails;
            grid.SelectionChanged += (s, e) => showDetails(); Shown += (s, e) => updateDetails();
            var heading = new Label { Dock = DockStyle.Top, Height = 146, Padding = new Padding(14), Text = (updates == null ? "" : "一次确认两个页签；已有文件默认更新，新文件无明确总装时不默认勾选。全部未勾选则仅检查总装。\r\n") + "新源保存到 local_ 对应位置；加入总装会修改 DWG，修改前备份。\r\n同系统仅有一份候选时预填，其他情况必须选择总装，可一对多。\r\n默认 WCS 点 (6653112.1, 29134629, 0)，比例 1，旋转 -4.42°；逐文件可编辑 X/Y/Z、比例和角度。\r\nAttachment，相对路径；同源在所选总装用相同定位，不自动换算单位，INSUNITS 须一致。\r\n仅新增 Xref 的目标总装须保存关闭；普通 Pull 可更新已打开总装，不移动已有引用。" };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(10), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel };
            var execute = new RoundedButton { Text = updates == null ? "确认下载并新增 Xref" : "确认执行一键更新", Primary = true, Width = 190 };
            var choose = new RoundedButton { Text = "选择该文件的总装…", Width = 185 };
            choose.Click += (s, e) => ChooseTargets();
            execute.Click += (s, e) => { try { if (!grid.EndEdit()) return; foreach (var view in Controls.OfType<TabControl>().SelectMany(t => t.TabPages.Cast<TabPage>()).SelectMany(p => p.Controls.OfType<ReviewTable<ExistingUpdateRow>>())) if (!view.Grid.EndEdit()) return; if (updates == null || batch.Files.Any(f => f.Selected)) NewCloudFiles.ValidateSelection(session, batch); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { ModernUi.Message(this, "请检查更新计划", ex.Message); } };
            footer.Controls.Add(cancel); footer.Controls.Add(execute); footer.Controls.Add(choose);
            if (updates == null) Controls.Add(table);
            else
            {
                existing.AddRange(updates.Select(p => new ExistingUpdateRow { Plan = p, Selected = p.Blocked == null && !string.IsNullOrEmpty(p.LocalHash) }));
                var updateGrid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
                updateGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Selected", HeaderText = "更新", Width = 55 });
                foreach (var spec in new[] { new[] { "Name", "DWG" }, new[] { "State", "操作 / 阻止原因" }, new[] { "Local", "本地完整路径" }, new[] { "Connector", "Connector 完整路径" }, new[] { "Assemblies", "受影响总装" } }) updateGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = spec[0], HeaderText = spec[1], Width = spec[0] == "Name" ? 180 : 330, ReadOnly = true });
                var updateTable = new ReviewTable<ExistingUpdateRow>(updateGrid, r => r.Name + " " + r.Local + " " + r.Connector + " " + r.State, r => r.Plan.Blocked == null ? "可执行" : "已阻止"); updateTable.SetRows(existing);
                updateGrid.CurrentCellDirtyStateChanged += (s, e) => { if (updateGrid.IsCurrentCellDirty && updateGrid.CurrentCell.ColumnIndex == 0) updateGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
                updateGrid.SelectionChanged += (s, e) => { if (updateGrid.CurrentRow?.DataBoundItem is ExistingUpdateRow row) details.Text = "本地：" + row.Local + "\r\n云端：" + row.Connector + "\r\n受影响总装：" + row.Assemblies + "\r\n" + row.State; };
                var tabs = new TabControl { Dock = DockStyle.Fill }; var oldPage = new TabPage("已有文件更新（" + updates.Count + "）"); var newPage = new TabPage("新增 Xref（" + batch.Files.Count + "）");
                oldPage.Controls.Add(updateTable); newPage.Controls.Add(table); tabs.TabPages.Add(oldPage); tabs.TabPages.Add(newPage);
                if (diagnostics?.Count > 0)
                {
                    var issuePage = new TabPage("待核对未执行（" + diagnostics.Count + "）"); issuePage.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Text = string.Join("\r\n\r\n", diagnostics) }); tabs.TabPages.Add(issuePage);
                }
                updateDetails = () => { if (tabs.SelectedIndex == 1) showDetails(); else if (tabs.SelectedIndex == 0 && updateGrid.CurrentRow?.DataBoundItem is ExistingUpdateRow row) details.Text = "本地：" + row.Local + "\r\n云端：" + row.Connector + "\r\n本地 SHA-256：" + row.Plan.LocalHash + "\r\nConnector SHA-256：" + row.Plan.ConnectorHash + "\r\n受影响总装：" + row.Assemblies + "\r\n" + row.State; else details.Clear(); };
                tabs.SelectedIndexChanged += (s, e) => { choose.Enabled = tabs.SelectedIndex == 1; updateDetails(); }; choose.Enabled = false;
                if (updates.Count == 0 && batch.Files.Count > 0) tabs.SelectedIndex = 1;
                Controls.Add(tabs);
            }
            Controls.Add(details); Controls.Add(heading); Controls.Add(footer); CancelButton = cancel; ModernUi.Apply(this);
        }
        private NewCloudFile Current => grid.CurrentRow?.DataBoundItem as NewCloudFile;
        private void Add(string property, string text, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn { Name = property, DataPropertyName = property, HeaderText = text, Width = width, ReadOnly = true });
        private void ChooseTargets()
        {
            var file = Current; if (file == null) return;
            using (var picker = new Form { Text = "为 " + file.Name + " 选择总装（可多选）", Width = 950, Height = 540, StartPosition = FormStartPosition.CenterParent })
            {
                var assemblies = session.Assemblies.OrderBy(a => a.DisplayName).ToList();
                var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
                foreach (var a in assemblies) list.Items.Add(a.DisplayName + "  |  " + a.Path, file.Targets.Contains(a.Path, StringComparer.OrdinalIgnoreCase));
                var done = new RoundedButton { Text = "确认选择", Dock = DockStyle.Bottom, Height = 42, DialogResult = DialogResult.OK };
                picker.Controls.Add(list); picker.Controls.Add(done); ModernUi.Apply(picker);
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                file.Targets = list.CheckedIndices.Cast<int>().Select(i => assemblies[i].Path).ToList(); grid.Refresh(); updateDetails?.Invoke();
            }
        }
    }
}
