using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

// 共用控件所需的轻量适配器，不引入 Revit 依赖。
namespace IWMF.ColumnAutoModel
{
    internal static class Ui { public static Button Button(string text) => new RoundedButton { Text = text }; }
}
namespace IWMF.PlantModelSync
{
    internal sealed partial class MainForm : Form
    {
        private readonly Settings settings;
        private readonly TextBox assembly = new TextBox(), connector = new TextBox();
        // 本地源路径由总装和 Xref 自动确定，不再要求用户选择根目录。
        private string LocalRoot => File.Exists(assembly.Text) ? Path.GetDirectoryName(Path.GetFullPath(assembly.Text)) : "";
        private readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        private readonly ReviewTable<XrefRow> table;
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 5, 12, 5), Text = "选择总装模型后，点击“扫描 Xref”。" };
        private readonly Label selectedCount = new Label { AutoSize = true, Margin = new Padding(14, 9, 0, 0) };
        private readonly List<Control> actions = new List<Control>();
        private readonly Button cancel = new RoundedButton { Text = "取消任务", Enabled = false };
        private List<XrefRow> rows = new List<XrefRow>();
        private CancellationTokenSource operation;
        private CancellationTokenSource targetCheck;
        private string scannedAssembly, scannedRoot;
        private readonly bool testing;
        public string ExportAssembly { get; private set; }
        public MainForm(Settings initial = null, bool testMode = false)
        {
            testing = testMode; settings = initial ?? Settings.Load();
            Text = "IWMF · Plant Model Sync"; Width = 1380; Height = 880; MinimumSize = new Size(1100, 820); StartPosition = FormStartPosition.CenterScreen;
            assembly.Text = settings.Assembly; connector.Text = settings.ConnectorRoot;
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 98, Padding = new Padding(16, 8, 16, 8), ColumnCount = 3, RowCount = 2 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
            AddField(fields, 0, "总装 DWG", assembly, "浏览文件", BrowseAssembly, "当前项目", PickProjectAssembly);
            AddField(fields, 1, "Desktop Connector 同步目录", connector, "选择目录", SelectConnectorFolder);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 6, 16, 6), WrapContents = false, AutoScroll = true };
            AddButton(toolbar, "扫描 Xref", () => Scan(), true);
            AddButton(toolbar, "刷新 Connector", () => { _ = RefreshTargets(true); });
            AddButton(toolbar, "← Pull 到本地", async () => await Transfer(false));
            AddButton(toolbar, "Push 到 Connector →", async () => await Transfer(true));
            AddButton(toolbar, "Export as NWC", Export);
            cancel.Click += (s, e) => operation?.Cancel(); toolbar.Controls.Add(cancel); toolbar.Controls.Add(selectedCount);
            Column("Selected", "同步", 54, true); Column("Name", "本地 Xref", 230);
            Column("SyncState", "同步状态", 94); Column("MatchStatus", "Connector 对应", 210);
            grid.Columns["Name"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["Name"].FillWeight = 48;
            grid.Columns["MatchStatus"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["MatchStatus"].FillWeight = 52;
            table = new ReviewTable<XrefRow>(grid, r => r.Name + " " + r.LocalPath + " " + r.CloudPath + " " + r.Parents + " " + r.MatchStatus + " " + r.SyncState + " " + r.Result, r => r.State);
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => { Count(); if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["Selected"].Index) _ = RefreshTargets(); };
            grid.DataError += (s, e) => { status.Text = "表格值无效，请检查路径。"; e.ThrowException = false; };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 0) }; body.Controls.Add(CreateSplitView());
            Controls.Add(body); Controls.Add(status); Controls.Add(CreateSyncLegend()); Controls.Add(toolbar); Controls.Add(fields);
            Controls.Add(ModernUi.Header("Plant Model Sync", "左侧勾选本地 Xref · 右侧浏览 Connector 文件 · 点击查看版本与日期 · Pull / Push 前逐项确认"));
            ModernUi.Apply(this);
            FormClosing += (s, e) => { if (operation != null) { operation.Cancel(); e.Cancel = true; status.Text = "正在取消，请等待当前请求结束后关闭。"; } else { targetCheck?.Cancel(); localDetails.CancelRead(); connectorDetails.CancelRead(); Save(); } };
            Shown += (s, e) => { if (!testing) { try { if (File.Exists(assembly.Text)) Scan(); else _ = RefreshTargets(true); } catch (Exception ex) { Error(ex); } } };
            Count();
        }
        private void AddField(TableLayoutPanel fields, int row, string title, TextBox text, string button, Action action, string secondButton = null, Action secondAction = null)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fields.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, row);
            text.Dock = DockStyle.Fill; text.Margin = new Padding(0, 7, 10, 0); fields.Controls.Add(text, 1, row); actions.Add(text);
            if (secondButton == null)
            {
                var b = new RoundedButton { Text = button, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 3) };
                b.Click += (s, e) => { try { action(); } catch (Exception ex) { Error(ex); } }; fields.Controls.Add(b, 2, row); actions.Add(b);
            }
            else
            {
                var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
                foreach (var option in new[] { (Title: button, Run: action), (Title: secondButton, Run: secondAction) })
                {
                    var b = new RoundedButton { Text = option.Title, AutoSize = false, Width = 110, Height = 36, Margin = new Padding(0, 0, 5, 0) };
                    b.Click += (s, e) => { try { option.Run(); } catch (Exception ex) { Error(ex); } }; panel.Controls.Add(b); actions.Add(b);
                }
                fields.Controls.Add(panel, 2, row);
            }
        }
        private void PickProjectAssembly()
        {
            var drawings = Host.CurrentProjectDrawings();
            using (var picker = new ProjectDrawingPicker(drawings))
                if (picker.ShowDialog(this) == DialogResult.OK) SelectAssembly(picker.SelectedPath);
        }
        private void BrowseAssembly()
        {
            using (var file = new OpenFileDialog { Title = "选择一个总装 DWG", InitialDirectory = LocalRoot, Filter = "AutoCAD 图形 (*.dwg)|*.dwg", CheckFileExists = true })
                if (file.ShowDialog(this) == DialogResult.OK) SelectAssembly(file.FileName);
        }
        private void SelectAssembly(string path)
        {
            assembly.Text = Path.GetFullPath(path);
            Scan();
        }
        private void AddButton(FlowLayoutPanel panel, string title, Action action, bool primary = false)
        {
            var b = new RoundedButton { Text = title, Primary = primary };
            b.Click += (s, e) => { try { action(); } catch (Exception ex) { Error(ex); } }; panel.Controls.Add(b); actions.Add(b);
        }
        private void Column(string property, string title, int width, bool editable = false)
        {
            DataGridViewColumn col = property == "Selected" ? (DataGridViewColumn)new DataGridViewCheckBoxColumn() : new DataGridViewTextBoxColumn();
            col.DataPropertyName = property; col.Name = property; col.HeaderText = title; col.Width = width; col.ReadOnly = !editable; grid.Columns.Add(col);
        }
        private void Browse(TextBox box)
        {
            using (var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : "" })
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
        }
        private void SelectConnectorFolder()
        {
            var previous = connector.Text;
            Browse(connector);
            if (string.Equals(previous, connector.Text, StringComparison.OrdinalIgnoreCase)) return;
            var folder = FolderLink.Parse(connector.Text);
            foreach (var row in rows)
            {
                row.CloudPath = settings.Mappings.TryGetValue(Settings.MapKey(folder, row.LocalPath), out var mapped)
                    ? mapped : (string.IsNullOrEmpty(LocalRoot) ? row.Name : Paths.Relative(LocalRoot, row.LocalPath));
                row.MatchStatus = "待检查新同步目录";
                row.SyncState = SyncFeedback.Pending; row.SyncedUtc = null;
            }
            grid.Refresh(); Save();
            _ = RefreshTargets(true);
        }
        private void PickMappedDwg()
        {
            grid.EndEdit();
            if (!(grid.CurrentRow?.DataBoundItem is XrefRow row)) throw new IOException("请先扫描 Xref，并点击需要设置对应文件的表格行。");
            using (var dialog = new OpenFileDialog
            {
                Title = "为 " + row.Name + " 选择 Desktop Connector 中对应的 DWG",
                Filter = "AutoCAD 图形 (*.dwg)|*.dwg",
                InitialDirectory = Directory.Exists(connector.Text) ? connector.Text : Path.GetDirectoryName(row.LocalPath),
                FileName = Path.GetFileName(row.LocalPath),
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                row.CloudPath = Path.GetFullPath(dialog.FileName);
                row.Selected = true;
                row.MatchStatus = "已手动对应"; row.Result = ""; row.SyncState = SyncFeedback.Pending; row.SyncedUtc = null;
                Save(); grid.Refresh(); Count();
                status.Text = row.Name + " 已对应 " + row.CloudPath;
                _ = RefreshTargets();
            }
        }
        private void Count() { selectedCount.Text = "勾选 " + rows.Count(r => r.Selected) + " / " + rows.Count + " · 已同步 " + rows.Count(r => r.SyncState == SyncFeedback.Synced) + " · 失败 " + rows.Count(r => r.SyncState == SyncFeedback.Failed); }
        private void SelectVisible(bool value) { grid.EndEdit(); foreach (DataGridViewRow r in grid.Rows) ((XrefRow)r.DataBoundItem).Selected = value; grid.Refresh(); Count(); _ = RefreshTargets(); }
        private void Save()
        {
            settings.Assembly = assembly.Text; settings.LocalRoot = LocalRoot; settings.ConnectorRoot = connector.Text;
            if (rows.Count > 0)
            {
                try { var folder = FolderLink.Parse(connector.Text); foreach (var row in rows) settings.Mappings[Settings.MapKey(folder, row.LocalPath)] = row.CloudPath; }
                catch (ArgumentException) { }
            }
            if (!testing) settings.Save();
        }
        private void Scan()
        {
            if (!File.Exists(assembly.Text)) throw new IOException("请选择已保存的总装 DWG。");
            grid.EndEdit(); var previous = rows.ToDictionary(r => r.LocalPath, StringComparer.OrdinalIgnoreCase);
            var scan = Scanner.Scan(assembly.Text, LocalRoot, new DwgReader()); rows = scan.Rows;
            FolderLink folder = null; try { folder = FolderLink.Parse(connector.Text); } catch (ArgumentException) { }
            foreach (var row in rows)
            {
                if (previous.TryGetValue(row.LocalPath, out var old)) { row.Selected = old.Selected; row.Result = old.Result; row.CloudPath = old.CloudPath; row.MatchStatus = old.MatchStatus; row.SyncState = old.SyncState; row.SyncedUtc = old.SyncedUtc; }
                else if (folder != null && settings.Mappings.TryGetValue(Settings.MapKey(folder, row.LocalPath), out var mapped)) row.CloudPath = mapped;
            }
            scannedAssembly = Paths.Canonical(assembly.Text); scannedRoot = Paths.Canonical(LocalRoot);
            table.SetRows(rows); Count(); Save();
            var unmapped = rows.Count(r => string.IsNullOrWhiteSpace(r.CloudPath));
            status.Text = "识别 " + rows.Count + " 个独立 DWG 源文件。" + (unmapped == 0 ? "请勾选需要同步的文件。" : "有 " + unmapped + " 个文件需逐行“选择对应 DWG”。") + (scan.Warnings.Count == 0 ? "" : "另有 " + scan.Warnings.Count + " 条依赖诊断。");
            if (scan.Warnings.Count > 0) ModernUi.Message(this, "依赖扫描诊断", string.Join(Environment.NewLine, scan.Warnings));
            _ = RefreshTargets();
        }
        private async Task RefreshTargets(bool rescan = false, bool keepStatus = false)
        {
            targetCheck?.Cancel();
            if (operation != null) return;
            if (!Directory.Exists(connector.Text)) { ClearConnectorView(); return; }
            var selected = rows.Where(r => r.Selected).ToList();
            var folder = FolderLink.Parse(connector.Text);
            var request = new CancellationTokenSource(); targetCheck = request;
            if (!keepStatus) status.Text = "正在读取 Connector 文件列表与对应关系……";
            try
            {
                var reload = rescan || connectorIndex == null || !string.Equals(indexedFolder, folder.Project, StringComparison.OrdinalIgnoreCase);
                var index = reload ? await Task.Run(() => TargetFinder.Index(folder.Project, request.Token), request.Token) : connectorIndex;
                var catalog = reload ? await Task.Run(() => BuildConnectorRows(folder, index, request.Token), request.Token) : null;
                if (request.IsCancellationRequested || IsDisposed) return;
                connectorIndex = index; indexedFolder = folder.Project;
                if (catalog != null) { connectorFiles = catalog; connectorTable.SetRows(connectorFiles); }
                ApplyTargetMatches(folder, selected, index);
                foreach (var row in rows.Where(r => !r.Selected)) { TargetFinder.Match(folder, row, index, out var message); row.MatchStatus = message; }
                RefreshSyncFeedback(folder, index);
                grid.Refresh(); Save();
                ShowLocalSelection();
                if (!keepStatus) status.Text = "本地 Xref " + rows.Count + " 个 · Connector DWG " + index.Values.Sum(list => list.Count) + " 个。点击文件查看详情；左侧勾选后预览 Pull/Push。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) status.Text = "目标文件检查未完成：" + ex.Message; }
            finally { if (targetCheck == request) targetCheck = null; request.Dispose(); }
        }
        private async Task Run(Func<CancellationToken, Task> work)
        {
            if (operation != null) return;
            operation = new CancellationTokenSource(); foreach (var control in actions) control.Enabled = false; table.Enabled = false; connectorTable.Enabled = false; cancel.Enabled = true;
            try { await work(operation.Token); }
            catch (OperationCanceledException) { status.Text = "任务已取消；已完成的文件保留。请检查结果列和日志。"; }
            catch (Exception ex) { Error(ex); }
            finally { operation.Dispose(); operation = null; foreach (var control in actions) control.Enabled = true; table.Enabled = true; connectorTable.Enabled = true; cancel.Enabled = false; grid.Refresh(); Count(); _ = RefreshTargets(true, true); }
        }
        private void Error(Exception ex) { status.Text = ex.Message; ModernUi.Message(this, "请检查", ex.Message); }
        private async Task Transfer(bool push)
        {
            await Run(async ct =>
            {
                targetCheck?.Cancel();
                grid.EndEdit();
                if (!File.Exists(assembly.Text) || scannedAssembly != Paths.Canonical(assembly.Text) || scannedRoot != Paths.Canonical(LocalRoot)) throw new IOException("总装已改变，请重新扫描 Xref。");
                var selected = rows.Where(r => r.Selected).ToList();
                if (selected.Count == 0) throw new IOException("请先勾选 Xref。");
                var folder = FolderLink.Parse(connector.Text);
                // 执行前重新确认对应关系，避免点击同步时后台目录扫描尚未结束。
                var index = await Task.Run(() => TargetFinder.Index(folder.Project, ct), ct);
                ApplyTargetMatches(folder, selected, index);
                Host.CheckSourcesClosed(selected); Save();
                var service = new SyncService(settings);
                var progress = new Progress<string>(message => { status.Text = message; grid.Refresh(); UpdateConnectorStates(); Count(); });
                Action<string> report = message => ((IProgress<string>)progress).Report(message);
                var plans = await Task.Run(() => service.Plan(folder, selected, push, ct, report), ct);
                foreach (var plan in plans) { plan.Row.Result = plan.Blocked ?? plan.Action; plan.Row.SyncState = plan.Blocked != null ? SyncFeedback.Review : SyncFeedback.Pending; }
                await Task.Run(() =>
                {
                    foreach (var plan in plans.Where(p => p.Blocked == null && p.IsUnchanged))
                        if (push) SyncFeedback.Record(settings, folder, plan.Row, plan.ConnectorPath, plan.LocalHash);
                        else plan.Row.SyncState = SyncFeedback.DateMatched;
                }, ct);
                Save(); UpdateConnectorStates();
                grid.Refresh();
                List<TransferPlan> approved;
                using (var preview = new PreviewForm(folder, plans, push))
                {
                    if (preview.ShowDialog(this) != DialogResult.OK) { status.Text = "已取消执行；预览未修改文件。"; return; }
                    approved = preview.ApprovedPlans;
                }
                foreach (var skipped in plans.Except(approved).Where(p => p.Blocked == null && !p.IsUnchanged)) skipped.Row.Result = "用户选择不更新";
                if (approved.Count == 0) { status.Text = "没有选择需要更新的文件；未修改任何文件。"; return; }
                IDisposable references = null;
                try
                {
                    if (!push) references = Host.ReleaseReferences(assembly.Text);
                    var log = await Task.Run(() => service.Execute(folder, approved, push, ct, report), ct);
                    UpdateConnectorStates(); grid.Refresh();
                    status.Text = "处理结束：已同步 " + approved.Count(p => p.Row.SyncState == SyncFeedback.Synced) + " 个，失败 " + approved.Count(p => p.Row.SyncState == SyncFeedback.Failed) + " 个。日志：" + log;
                }
                finally { references?.Dispose(); }
                if (!push) { var message = status.Text; Scan(); status.Text = message + "；已重新扫描依赖。"; }
            });
        }
        private void Export()
        {
            if (!File.Exists(assembly.Text)) throw new IOException("请选择总装 DWG。");
            ExportAssembly = Path.GetFullPath(assembly.Text);
            Save(); DialogResult = DialogResult.OK; Close();
        }
        internal void RenderTest(string path)
        {
            Show(); Scan();
            rows[0].Selected = true;
            table.Search.Text = rows[0].Name;
            table.Search.Text = "";
            grid.Sort(grid.Columns["Name"], System.ComponentModel.ListSortDirection.Descending);
            if (!rows[0].Selected || grid.Rows.Count != rows.Count) throw new InvalidOperationException("表格筛选/排序改变了勾选状态。");
            Count(); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            Close();
        }
    }
    internal sealed partial class PreviewForm : Form
    {
        private sealed class PreviewRow
        {
            public TransferPlan Plan { get; set; }
            public bool Update { get; set; }
            public string Name { get; set; }
            public string Status { get; set; }
            public string LocalExists { get; set; }
            public string LocalPath { get; set; }
            public string LocalFormat { get; set; }
            public string LocalHash { get; set; }
            public string LocalTime { get; set; }
            public string TargetExists { get; set; }
            public string TargetFormat { get; set; }
            public string TargetHash { get; set; }
            public string TargetTime { get; set; }
            public string TargetPath { get; set; }
        }
        private readonly List<PreviewRow> previewRows;
        private readonly bool existingOnly;
        public List<TransferPlan> ApprovedPlans => previewRows.Where(r => r.Update && r.Plan.Blocked == null && !r.Plan.IsUnchanged && (!existingOnly || (!string.IsNullOrEmpty(r.Plan.LocalHash) && File.Exists(r.Plan.Local)))).Select(r => r.Plan).ToList();
        public PreviewForm(FolderLink folder, List<TransferPlan> plans, bool push, bool requireExistingLocal = false)
        {
            existingOnly = !push && requireExistingLocal;
            if (existingOnly) foreach (var plan in plans) plan.RequireExistingLocal = true;
            Text = (push ? "Push" : "Pull") + " · 日期对比与更新确认"; Width = 1320; Height = 780; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(1000, 650);
            previewRows = plans.Select(p => new PreviewRow
            {
                Plan = p, Update = p.Blocked == null && !p.IsUnchanged && (push || !string.IsNullOrEmpty(p.LocalHash)), Name = p.Row.Name,
                Status = p.Blocked == null ? p.Action : "阻止：" + p.Blocked,
                LocalExists = File.Exists(p.Local) ? "已存在" : "不存在",
                LocalPath = p.Local,
                LocalFormat = SafeFormat(p.Local), LocalHash = DisplayHash(p.LocalHash), LocalTime = p.DateComparison ? (p.LocalModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff") ?? "—") : FileTime(p.Local),
                TargetExists = !string.IsNullOrWhiteSpace(p.ConnectorPath) && File.Exists(p.ConnectorPath) ? "已存在" : "不存在",
                TargetFormat = SafeFormat(p.ConnectorPath), TargetHash = DisplayHash(p.ConnectorHash), TargetTime = p.DateComparison ? (p.ConnectorModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff") ?? "—") : FileTime(p.ConnectorPath),
                TargetPath = p.ConnectorPath ?? "（未映射）"
            }).ToList();
            var heading = new Label { Dock = DockStyle.Top, Height = 94, Padding = new Padding(16, 12, 16, 0), Text = "方向：" + (push ? "本地 → Desktop Connector（上传由 Connector 处理）" : "Desktop Connector → 本地（覆盖 Xref 不备份）") + Environment.NewLine + "同步目录：" + folder.Project + Environment.NewLine + "Pull 按源修改日期覆盖并保留日期，不使用下载时间；SHA-256 仅用于复制校验。" + Environment.NewLine + (push ? "请核对上传目标；仅复制到 Connector 本机目录，不代表服务器已完成同步。" : existingOnly ? "仅更新现有目标；目标消失时阻止写入，不创建副本。" : "缺失本地目标默认不勾选；逐项勾选才会新建，不会自动修复总装的 Xref 路径。") };
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, DataSource = previewRows };
            AddColumn(grid, "Update", "更新", 54, true);
            AddColumn(grid, "Name", "Xref", 190);
            AddColumn(grid, "Status", "计划 / 原因", 180);
            AddColumn(grid, "LocalExists", "本地文件", 85);
            AddColumn(grid, "LocalPath", "本地目标完整路径", 420);
            AddColumn(grid, "LocalFormat", "本地格式", 110);
            AddColumn(grid, "LocalHash", "本地 SHA-256", 240);
            AddColumn(grid, "LocalTime", "本地修改时间", 135);
            AddColumn(grid, "TargetExists", "同步文件", 85);
            AddColumn(grid, "TargetFormat", "同步格式", 110);
            AddColumn(grid, "TargetHash", "同步 SHA-256", 240);
            AddColumn(grid, "TargetTime", "同步修改时间", 135);
            AddColumn(grid, "TargetPath", "同步 DWG 路径", 380);
            if (!push)
            {
                grid.Columns["LocalTime"].DisplayIndex = 3; grid.Columns["TargetTime"].DisplayIndex = 4;
                grid.Columns["LocalTime"].Width = grid.Columns["TargetTime"].Width = 240;
                grid.Columns["LocalHash"].Visible = grid.Columns["TargetHash"].Visible = false;
            }
            grid.Columns["Update"].ReadOnly = false;
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["Update"].Index) { var item = (PreviewRow)grid.Rows[e.RowIndex].DataBoundItem; if (item.Plan.Blocked != null || item.Plan.IsUnchanged || (existingOnly && (string.IsNullOrEmpty(item.Plan.LocalHash) || !File.Exists(item.Plan.Local)))) { item.Update = false; grid.Refresh(); } } };
            var details = new TextBox { Dock = DockStyle.Bottom, Height = 112, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            Action showDetails = () =>
            {
                if (!(grid.CurrentRow?.DataBoundItem is PreviewRow row)) return;
                details.Text = "实际本地目标：" + row.Plan.Local + "\r\nConnector 来源：" + row.Plan.ConnectorPath + "\r\n匹配依据：" + row.Plan.Row.MatchStatus + "\r\n受影响总装：" + string.Join(" | ", row.Plan.AffectedAssemblies) + "\r\n" + row.Status;
            };
            grid.SelectionChanged += (s, e) => showDetails();
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is PreviewRow row) e.ToolTipText = row.Plan.Local + "\r\n" + row.Plan.ConnectorPath; };
            Controls.Add(grid); Controls.Add(details); Controls.Add(heading); Shown += (s, e) => showDetails();
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
            var close = new RoundedButton { Text = "返回", DialogResult = DialogResult.Cancel };
            var execute = new RoundedButton { Text = "执行勾选的更新", Primary = true, Enabled = plans.Any(p => p.Blocked == null && !p.IsUnchanged) };
            execute.Click += (s, e) => { grid.EndEdit(); if (ApprovedPlans.Count == 0) { MessageBox.Show(this, "请先勾选需要更新的行。", "没有更新项", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } DialogResult = DialogResult.OK; Close(); };
            footer.Controls.Add(close); footer.Controls.Add(execute); Controls.Add(footer); CancelButton = close; ModernUi.Apply(this);
        }
        private static void AddColumn(DataGridView grid, string property, string title, int width, bool check = false)
        {
            DataGridViewColumn column = check ? (DataGridViewColumn)new DataGridViewCheckBoxColumn() : new DataGridViewTextBoxColumn();
            column.Name = property; column.DataPropertyName = property; column.HeaderText = title; column.Width = width; column.ReadOnly = !check;
            grid.Columns.Add(column);
        }
        private static string SafeFormat(string path) { try { return string.IsNullOrWhiteSpace(path) ? "—" : Files.DwgFormat(path); } catch (IOException) { return "不可读取"; } }
        private static string DisplayHash(string hash) => string.IsNullOrEmpty(hash) ? "—" : hash;
        private static string FileTime(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss.fffffff") : "—";
    }
    internal sealed class ProjectDrawingPicker : Form
    {
        private readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        public string SelectedPath { get; private set; }
        public ProjectDrawingPicker(List<ProjectDrawing> drawings)
        {
            Text = "从当前 Plant 项目选择总装 DWG"; Width = 900; Height = 600; StartPosition = FormStartPosition.CenterParent;
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Part", HeaderText = "项目分区", FillWeight = 18 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "DWG", FillWeight = 22 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Path", HeaderText = "完整路径", FillWeight = 60 });
            grid.DataSource = drawings;
            grid.CellDoubleClick += (s, e) => Choose();
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
            var choose = new RoundedButton { Text = "选择此 DWG", Primary = true };
            var browse = new RoundedButton { Text = "浏览文件…" };
            var cancel = new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel };
            choose.Click += (s, e) => Choose();
            browse.Click += (s, e) =>
            {
                var selected = grid.CurrentRow?.DataBoundItem as ProjectDrawing;
                using (var dialog = new OpenFileDialog { Title = "浏览总装 DWG", Filter = "AutoCAD 图形 (*.dwg)|*.dwg", CheckFileExists = true, InitialDirectory = selected != null ? Path.GetDirectoryName(selected.Path) : "" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { SelectedPath = dialog.FileName; DialogResult = DialogResult.OK; Close(); }
            };
            footer.Controls.Add(cancel); footer.Controls.Add(choose); footer.Controls.Add(browse);
            Controls.Add(grid); Controls.Add(footer); CancelButton = cancel;
            ModernUi.Apply(this);
        }
        private void Choose()
        {
            if (grid.CurrentRow?.DataBoundItem is ProjectDrawing drawing)
            {
                SelectedPath = drawing.Path; DialogResult = DialogResult.OK; Close();
            }
        }
    }
}
