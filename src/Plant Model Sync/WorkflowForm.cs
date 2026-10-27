using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm : Form
    {
        private readonly Settings settings;
        private readonly Action persist;
        private readonly Func<SyncService> createService;
        private readonly string reportStorage;
        private readonly TextBox connector = new TextBox(), scope = new TextBox { ReadOnly = true }, output = new TextBox();
        private readonly TextBox localRoot = new TextBox();
        private readonly CheckBox onlyExisting = new CheckBox { Text = "仅本地已有", Checked = true, AutoSize = true, Margin = new Padding(6, 10, 0, 0) };
        private readonly CheckBox onlyLinked = new CheckBox { Text = "仅关联总装", Checked = true, AutoSize = true, Margin = new Padding(5, 10, 0, 0) };
        private readonly HashSet<string> linkedCloudPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DataGridView sourcesGrid = MakeGrid(), modelsGrid = MakeGrid();
        private readonly ReviewTable<CloudUpdate> sourceTable;
        private readonly ReviewTable<AssemblyUpdate> modelTable;
        private readonly FileDetailsPanel sourceDetail = new FileDetailsPanel("文件详情") { ContextFirst = true };
        private readonly TextBox modelDetail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(16, 7, 16, 0), AutoEllipsis = true, Text = "就绪" };
        private readonly List<Control> actions = new List<Control>();
        private readonly Button cancel = new RoundedButton { Text = "取消", Enabled = false, Visible = false };
        private CancellationTokenSource work;
        private string scopeFolder;
        private bool binding;
        private bool followChecks;
        private readonly ComboBox systemFilter = new ComboBox { Width = 98, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "云端系统筛选" };
        private readonly ComboBox stateFilter = new ComboBox { Width = 112, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "更新状态筛选" };
        private readonly Label pullCount = new Label { AutoSize = true, Margin = new Padding(8, 6, 0, 0), Text = "待 Pull 勾选 0" };
        private readonly Label exportCount = new Label { AutoSize = true, Margin = new Padding(8, 10, 0, 0), Text = "导出勾选 0" };
        internal WorkflowSession Session { get; private set; }
        internal List<BatchExportJob> ExportRequests { get; private set; }
        internal bool InputMatchesSession
        {
            get
            {
                try { return Session != null && string.Equals(Paths.Canonical(connector.Text), Session.ConnectorRoot, StringComparison.OrdinalIgnoreCase) && string.Equals(scopeFolder ?? "", Session.ScopeFolder, StringComparison.OrdinalIgnoreCase) && string.Equals(localRoot.Text, Session.LocalModelsRoot ?? "", StringComparison.OrdinalIgnoreCase); }
                catch (ArgumentException) { return false; }
            }
        }

        public WorkflowForm(WorkflowSession resume = null, Settings config = null, Action saveConfig = null, Func<SyncService> serviceFactory = null, string logStorage = null, bool assemblyFirst = false)
        {
            settings = config ?? Settings.Load(); persist = saveConfig ?? settings.Save; createService = serviceFactory ?? (() => new SyncService(settings)); Session = resume;
            reportStorage = logStorage ?? createService().StorageDirectory;
            settings.PreparePaths();
            Text = "Auto sync"; Width = 1440; Height = 920; MinimumSize = new Size(1140, 850); StartPosition = FormStartPosition.CenterScreen;
            connector.Text = resume?.ConnectorRoot ?? settings.ConnectorRoot;
            scopeFolder = resume?.ScopeFolder ?? settings.ProjectScanFolder;
            scope.Text = string.IsNullOrEmpty(scopeFolder) ? "当前 Plant 项目" : scopeFolder;
            output.Text = resume?.OutputFolder ?? settings.OutputFolder;
            localRoot.Text = resume?.LocalModelsRoot ?? settings.LocalModelsRoot;
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 174, ColumnCount = 3, RowCount = 4, Padding = new Padding(16, 5, 16, 5) };
            pathFields = fields;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            AddField(fields, 0, "云端 DWG", connector, "浏览", () => Browse(connector));
            AddField(fields, 1, "本地模型", localRoot, "浏览", () => Browse(localRoot));
            AddField(fields, 2, "本地总装", scope, "浏览", () =>
            {
                using (var picker = new FolderBrowserDialog { Description = "选择本地总装/图纸目录，将递归检测 DWG 的 Xref", SelectedPath = Directory.Exists(scopeFolder) ? scopeFolder : "" })
                    if (picker.ShowDialog(this) == DialogResult.OK) { scopeFolder = picker.SelectedPath; scope.Text = scopeFolder; }
            }, "当前项目", () => { scopeFolder = ""; scope.Text = "当前 Plant 项目"; UpdatePathSummary(); });
            AddField(fields, 3, "NWC 输出", output, "浏览", () => Browse(output));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 4, 16, 3), WrapContents = false, AutoScroll = true };
            AsyncButton(tools, "检查更新", Check, true);
            AsyncButton(tools, "一键更新", UpdateAll, true);
            Button(tools, "导出 NWC", Export);
            AsyncButton(tools, "本地 DWG", AuditRequiredAssemblies);
            Button(tools, "NWC 日期", CheckNwcDates);
            Button(tools, "日志", OpenLog);
            Button(tools, "目录", TogglePaths);
            MenuButton(tools, "更多", menu =>
            {
                MenuAsync(menu, "从文件夹导出 NWC…", BrowseNwcExport);
                MenuAsync(menu, "强制刷新", async () => { Session?.InvalidateAutomaticMappings(); await Check(); });
                MenuAction(menu, "查看问题", () => { using (var form = new ScanIssuesForm(Session)) form.ShowDialog(this); });
                var detailed = new ToolStripMenuItem("详细信息") { CheckOnClick = true };
                detailed.CheckedChanged += (s, e) => SetDetailedView(detailed.Checked); menu.Items.Add(detailed);
                menu.Items.Add(new ToolStripSeparator()); MenuAction(menu, "恢复已保存目录", UseSavedPaths);
            });
            cancel.Click += (s, e) => work?.Cancel(); tools.Controls.Add(cancel);
            AddColumn(sourcesGrid, "Selected", "更新", 48, true);
            AddColumn(sourcesGrid, "Name", "文件", 220, false, 100);
            sourcesGrid.Columns["Name"].MinimumWidth = 180;
            sourcesGrid.Columns["Name"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            sourcesGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            AddColumn(sourcesGrid, "System", "系统", 60);
            AddColumn(sourcesGrid, "State", "状态", 90);
            sourcesGrid.Columns["State"].DataPropertyName = "DisplayState";
            AddColumn(sourcesGrid, "DisplayPath", "云端子目录 / DWG", 250);
            AddColumn(sourcesGrid, "MatchReason", "匹配依据", 210);
            AddColumn(sourcesGrid, "LocalSummary", "本地对应文件", 220);
            sourcesGrid.Columns["LocalSummary"].DataPropertyName = "DisplayLocalSummary";
            AddColumn(sourcesGrid, "MissingSummary", "缺失目标（需核对）", 125);
            AddColumn(sourcesGrid, "Modified", "修改时间", 120);
            AddColumn(modelsGrid, "Selected", "导出", 58, true);
            AddColumn(modelsGrid, "DisplayName", "总装 DWG", 290, false, 100);
            modelsGrid.Columns["DisplayName"].MinimumWidth = 260;
            modelsGrid.Columns["DisplayName"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            modelsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            AddColumn(modelsGrid, "XrefSummary", "涉及的 Xref", 250, false, 60);
            AddColumn(modelsGrid, "Status", "状态", 140);
            sourcesGrid.Columns["Selected"].HeaderCell = new VisibleSelectionHeader<CloudUpdate>(
                r => Workflow.SourceState(r, onlyExisting.Checked) == "可更新" || (!onlyExisting.Checked && r.Plans.Count == 0 && r.AmbiguousLocals.Count == 0 && r.Diagnostics.Count == 0),
                r => r.Selected, (r, value) => r.Selected = value,
                () => { followChecks = true; SetLinkedSources(Session?.Sources.Where(r => r.Selected)); ShowAffected(); });
            modelsGrid.Columns["Selected"].HeaderCell = new VisibleSelectionHeader<AssemblyUpdate>(
                a => File.Exists(a.Path),
                a => a.Selected, (a, value) => a.Selected = value, ShowCounts);
            sourceTable = new ReviewTable<CloudUpdate>(sourcesGrid, r => r.Name + " " + r.RelativePath + " " + r.System + " " + r.MatchReason + " " + r.DisplayState + " " + r.DisplayLocalSummary + " " + r.AffectedSummary, r => r.DisplayState);
            modelTable = new ReviewTable<AssemblyUpdate>(modelsGrid, r => r.Name + " " + r.Path + " " + r.PendingXrefs + " " + r.UpdatedXrefs + " " + r.Status, r => r.Status);
            modelsGrid.MultiSelect = true;
            sourcesGrid.MultiSelect = true;
            Compact(sourceTable); Compact(modelTable);
            sourceTable.Filter.Visible = false;
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1380, 560), Panel1MinSize = 420, Panel2MinSize = 420, SplitterWidth = 8, SplitterDistance = 690 };
            split.SizeChanged += (s, e) => { if (split.Width > 848) split.SplitterDistance = split.Width / 2; };
            var leftTools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false, AutoScroll = true };
            MenuButton(leftTools, "对应", menu => { MenuAsync(menu, "指定本地文件…", MapLocal); MenuAsync(menu, "系统目录对应…", MapFolders); });
            leftTools.Controls.Add(systemFilter);
            systemFilter.Margin = new Padding(4, 9, 4, 0); stateFilter.Margin = new Padding(4, 9, 4, 0);
            stateFilter.Items.AddRange(new object[] { "全部状态", "只看可更新", "只看待确认", "只看云端新增" }); stateFilter.SelectedIndex = 0;
            leftTools.Controls.Add(stateFilter); systemFilter.Items.Add("全部系统"); systemFilter.SelectedIndex = 0;
            leftTools.Controls.Add(onlyExisting); leftTools.Controls.Add(pullCount);
            onlyExisting.CheckedChanged += (s, e) => FilterSources(); actions.Add(onlyExisting);
            foreach (var reset in sourceTable.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Where(b => b.Text == "重置"))
                reset.Click += (s, e) => { systemFilter.SelectedIndex = 0; stateFilter.SelectedIndex = 0; onlyExisting.Checked = true; FilterSources(); };
            actions.Add(systemFilter); actions.Add(stateFilter);
            systemFilter.SelectedIndexChanged += (s, e) => FilterSources(); stateFilter.SelectedIndexChanged += (s, e) => { if (stateFilter.SelectedIndex == 3) onlyExisting.Checked = false; FilterSources(); };
            var rightTools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, WrapContents = false, AutoScroll = true };
            rightTools.Controls.Add(onlyLinked); rightTools.Controls.Add(exportCount);
            onlyLinked.CheckedChanged += (s, e) => ShowAffected(); actions.Add(onlyLinked);
            split.Panel1.Controls.Add(Pane("云端 DWG", sourceTable, leftTools, sourceDetail));
            sourceDetail.Height = 120;
            var modelDetails = new Panel { Dock = DockStyle.Bottom, Height = 120, Padding = new Padding(0, 8, 0, 0) };
            modelDetailsPanel = modelDetails;
            modelDetails.Controls.Add(modelDetail); modelDetails.Controls.Add(new Label { Text = "总装详情", Dock = DockStyle.Top, Height = 25, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) });
            split.Panel2.Controls.Add(Pane("本地总装", modelTable, rightTools, modelDetails));
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 0) }; body.Controls.Add(split);
            Controls.Add(body); Controls.Add(status); Controls.Add(tools); Controls.Add(pathSummary); Controls.Add(fields);
            var heading = ModernUi.Header("Auto sync", ""); heading.Height = 54;
            foreach (var label in heading.Controls.OfType<Label>().Where(l => l.Text.Length == 0)) label.Visible = false;
            Controls.Add(heading);
            fields.Visible = string.IsNullOrWhiteSpace(connector.Text) || string.IsNullOrWhiteSpace(localRoot.Text);
            foreach (var box in new[] { connector, localRoot, scope, output }) box.TextChanged += (s, e) => UpdatePathSummary();
            pathSummary.Click += (s, e) => TogglePaths(); UpdatePathSummary(); SetDetailedView(false);
            Disposed += (s, e) => uiTips.Dispose();
            ModernUi.Apply(this);
            foreach (var grid in new[] { sourcesGrid, modelsGrid })
            {
                grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
                grid.DataError += (s, e) => { e.ThrowException = false; status.Text = "请检查表格选择。"; };
            }
            sourcesGrid.CellValueChanged += (s, e) => { if (!binding && e.RowIndex >= 0 && e.ColumnIndex == 0) { followChecks = true; SetLinkedSources(Session?.Sources.Where(r => r.Selected)); ShowAffected(); } };
            modelsGrid.CellValueChanged += (s, e) => ShowCounts();
            sourcesGrid.SelectionChanged += (s, e) =>
            {
                if (binding) return;
                if (!followChecks && sourcesGrid.CurrentCell?.OwningColumn.Name != "Selected") SetLinkedSources(sourcesGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<CloudUpdate>());
                ShowSource();
            }; modelsGrid.SelectionChanged += (s, e) => ShowModel();
            sourcesGrid.CellMouseDown += (s, e) => { if (e.ColumnIndex > 0) followChecks = false; };
            sourcesGrid.CellClick += (s, e) => { if (e.ColumnIndex > 0) FollowSourceRows(); };
            sourcesGrid.KeyUp += (s, e) => { if (e.KeyCode != Keys.Space && sourcesGrid.CurrentCell?.OwningColumn.Name != "Selected") FollowSourceRows(); };
            sourcesGrid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && sourcesGrid.Rows[e.RowIndex].DataBoundItem is CloudUpdate row) e.ToolTipText = row.Path + "\r\n" + row.MatchReason + "\r\n" + string.Join("\r\n", row.Diagnostics); };
            modelsGrid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && modelsGrid.Rows[e.RowIndex].DataBoundItem is AssemblyUpdate a) e.ToolTipText = a.Path + "\r\n已更新：" + a.UpdatedXrefs + "\r\n待 Pull：" + a.PendingXrefs + "\r\n" + a.RefreshState + "\r\n" + a.ExportState; };
            sourcesGrid.CellFormatting += (s, e) => { if (e.RowIndex >= 0 && sourcesGrid.Rows[e.RowIndex].DataBoundItem is CloudUpdate row) ColorStatus(e.CellStyle, row.DisplayState); };
            modelsGrid.CellFormatting += (s, e) => { if (e.RowIndex >= 0 && modelsGrid.Rows[e.RowIndex].DataBoundItem is AssemblyUpdate row) ColorStatus(e.CellStyle, row.Status); };
            foreach (Label label in sourcesGrid.Controls.OfType<Label>()) label.Text = "暂无文件";
            foreach (Label label in modelsGrid.Controls.OfType<Label>()) label.Text = "暂无关联总装";
            sourceDetail.Clear("选择文件查看详情");
            FormClosing += (s, e) => { if (work != null) { work.Cancel(); e.Cancel = true; } else { sourceDetail.CancelRead(); Save(); } };
            if (resume != null) { SetLinkedSources(resume.Sources.Where(s => s.Selected || s.Plans.Any(p => resume.UpdatedHashes.ContainsKey(p.Local)))); BindSources(); ShowAffected(); status.Text = "已恢复上次结果"; uiTips.SetToolTip(status, resume.LastLog ?? ""); }
            if (assemblyFirst) InitializeAssemblyBrowser();
            InitializeScanProgress();
        }

        private static DataGridView MakeGrid() => new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        private static void AddColumn(DataGridView grid, string property, string title, int width, bool editable = false, int fill = 0)
        {
            DataGridViewColumn c = editable ? (DataGridViewColumn)new DataGridViewCheckBoxColumn() : new DataGridViewTextBoxColumn();
            c.Name = property; c.DataPropertyName = property; c.HeaderText = title; c.Width = width; c.ReadOnly = !editable;
            if (fill > 0) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.FillWeight = fill; }
            grid.Columns.Add(c);
        }
        private void AddField(TableLayoutPanel fields, int row, string name, TextBox text, string title, Action click, string second = null, Action next = null)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            fields.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 12, 0, 0) }, 0, row);
            text.Dock = DockStyle.Fill; text.Margin = new Padding(0, 9, 8, 0); fields.Controls.Add(text, 1, row); actions.Add(text);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            Button(panel, title, click); if (second != null) Button(panel, second, next); fields.Controls.Add(panel, 2, row);
        }
        private void Button(FlowLayoutPanel parent, string title, Action click, bool primary = false)
        {
            var button = new RoundedButton { Text = title, Primary = primary };
            button.Click += (s, e) => { try { click(); } catch (Exception ex) { Error(ex); } }; actions.Add(button); parent.Controls.Add(button);
        }
        private void AsyncButton(FlowLayoutPanel parent, string title, Func<Task> click, bool primary = false)
        {
            var button = new RoundedButton { Text = title, Primary = primary };
            button.Click += async (s, e) => { try { await click(); } catch (Exception ex) { Error(ex); } }; actions.Add(button); parent.Controls.Add(button);
        }
        private static Control Pane(string title, Control list, Control tools, Control details)
        {
            var pane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
            pane.Controls.Add(list); pane.Controls.Add(details); pane.Controls.Add(tools);
            pane.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 32, Padding = new Padding(3, 5, 0, 0), Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) }); return pane;
        }
        private static void Compact<T>(ReviewTable<T> table)
        {
            table.Search.Width = 150; table.Filter.Width = 115;
            foreach (var tools in table.Controls.OfType<FlowLayoutPanel>()) tools.Height = 51;
        }
        private void Browse(TextBox box) { using (var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : "" }) if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; }
        private void UseSavedPaths()
        {
            // 公开源码不包含机器专用预设，只恢复用户已保存的路径。
            connector.Text = settings.ConnectorRoot ?? "";
            localRoot.Text = settings.LocalModelsRoot ?? "";
            scopeFolder = settings.ProjectScanFolder ?? ""; scope.Text = scopeFolder;
            status.Text = "已恢复已保存目录";
        }
        private void OpenLog()
        {
            using (var dialog = new UpdateLogForm(Path.Combine(reportStorage, "Logs"), Session?.LastUpdateLog ?? Session?.LastLog)) dialog.ShowDialog(this);
        }
        private void Save() { settings.ConnectorRoot = connector.Text; settings.ProjectScanFolder = scopeFolder ?? ""; settings.LocalModelsRoot = localRoot.Text; settings.OutputFolder = output.Text; if (Session != null) Session.OutputFolder = output.Text; persist(); }
        private void Error(Exception ex) { status.Text = ex.Message; ModernUi.Message(this, "请检查", ex.Message); }
        private async Task Busy(Func<CancellationToken, Task> action)
        {
            if (work != null) return;
            work = new CancellationTokenSource(); actions.ForEach(c => c.Enabled = false); sourceTable.Enabled = modelTable.Enabled = false; cancel.Enabled = true; cancel.Visible = true;
            try { await action(work.Token); }
            catch (OperationCanceledException) { status.Text = "已取消，已完成项保留"; }
            catch (Exception ex) { Error(ex); }
            finally { work.Dispose(); work = null; actions.ForEach(c => c.Enabled = true); sourceTable.Enabled = modelTable.Enabled = true; cancel.Enabled = false; cancel.Visible = false; }
        }
        private async Task Check()
        {
            await Busy(async ct =>
            {
                var ownProgress = !scanProgressActive;
                var run = ownProgress ? BeginScanProgress("准备检查") : scanProgressRun;
                try
                {
                var folder = FolderLink.Parse(connector.Text);
                settings.LocalModelsRoot = localRoot.Text.Trim();
                if (settings.LocalModelsRoot.Length > 0 && !Directory.Exists(settings.LocalModelsRoot)) throw new IOException("本地模型根目录不存在。");
                var watch = Stopwatch.StartNew();
                var candidates = assemblyBrowser != null ? (inspectionPaths?.ToList() ?? new List<string>()) : string.IsNullOrEmpty(scopeFolder) ? Host.CurrentProjectDrawings().Where(d => d.Part != "PnId").Select(d => d.Path).ToList()
                    : await Task.Run(() => TargetFinder.Index(scopeFolder, ct).Values.SelectMany(x => x).ToList(), ct);
                if (assemblyBrowser != null) candidates = inspectionPaths?.ToList() ?? new List<string>();
                else candidates.AddRange(AssemblyCatalog.Discover(settings.LocalModelsRoot).SelectMany(a => a.Matches));
                if (candidates.Count == 0) throw new IOException("没有可访问的本地图纸。请打开 Plant 项目，或选择本地图纸文件夹。");
                var previous = Session;
                var reuse = previous != null && string.Equals(previous.ConnectorRoot, folder.Project, StringComparison.OrdinalIgnoreCase) && string.Equals(previous.ScopeFolder, scopeFolder ?? "", StringComparison.OrdinalIgnoreCase) && string.Equals(previous.LocalModelsRoot ?? "", settings.LocalModelsRoot, StringComparison.OrdinalIgnoreCase);
                Session = new WorkflowSession { ConnectorRoot = folder.Project, ScopeFolder = scopeFolder ?? "", LocalModelsRoot = settings.LocalModelsRoot, OutputFolder = output.Text };
                linkedCloudPaths.Clear();
                if (reuse) { Session.UpdatedHashes = new Dictionary<string, string>(previous.UpdatedHashes, StringComparer.OrdinalIgnoreCase); Session.AddedReferences = previous.AddedReferences.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase); Session.PendingReferenceAdds = previous.PendingReferenceAdds.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase); }
                Session.ReferenceCache = reuse ? previous.ReferenceCache : null;
                Session.LocalMappingFiles = reuse ? previous.LocalMappingFiles : null;
                if (reuse) { Session.HashCache = previous.HashCache; Session.LastLog = previous.LastLog; Session.LastUpdateLog = previous.LastUpdateLog; }
                var reader = Session.ReferenceCache ?? (Session.ReferenceCache = new CachedReader(new DwgReader(), DwgReader.IsOpen));
                int oldReads = reader.Reads, oldHits = reader.Hits, oldHashReads = Session.HashCache.Reads, oldHashHits = Session.HashCache.Hits;
                var drawings = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int completedDrawings = 0;
                foreach (var path in drawings)
                {
                    ct.ThrowIfCancellationRequested();
                    ReportScanProgress(run, "读取总装", completedDrawings, drawings.Count, path);
                    PaintScanProgress();
                    // 原生 DWG 读取必须留在宿主线程；读取前让界面完成绘制并接收取消。
                    await Task.Yield(); ct.ThrowIfCancellationRequested();
                    var assembly = Workflow.ScanAssembly(path, reader, assemblyBrowser != null);
                    // 空总装也必须可选，才能把首次发现的云端源加入其中。
                    {
                        var prior = reuse ? previous.Assemblies.FirstOrDefault(a => string.Equals(a.Path, assembly.Path, StringComparison.OrdinalIgnoreCase)) : null;
                        if (prior != null)
                        {
                            assembly.Selected = prior.Selected; assembly.ExportState = prior.ExportState; assembly.ExportPath = prior.ExportPath; assembly.ExportHash = prior.ExportHash; assembly.RefreshState = prior.RefreshState; assembly.ExpectedHashes = prior.ExpectedHashes;
                            if (assembly.DrawingStamp != prior.DrawingStamp) { assembly.ExportState = "未导出"; assembly.ExportHash = null; }
                            if (assembly.Scan.Warnings.Count == 0 && assembly.RefreshState.StartsWith("引用重载失败", StringComparison.Ordinal)) assembly.RefreshState = "源文件已更新；导出时加载";
                        }
                        Session.Assemblies.Add(assembly);
                    }
                    Session.Warnings.AddRange(assembly.Scan.Warnings.Select(w => assembly.Name + "：" + w));
                    ReportScanProgress(run, "读取总装", ++completedDrawings, drawings.Count, path);
                    await Task.Yield();
                }
                ReportScanProgress(run, "扫描 Connector / 匹配路径");
                var progress = new Progress<string>(message => ReportScanMessage(run, message));
                var counts = new Progress<Tuple<int, int, string>>(p => ReportScanProgress(run, "对比 Xref", p.Item1, p.Item2, p.Item3));
                Session.Sources = await Task.Run(() => Workflow.Check(Session, settings, ct, m => ((IProgress<string>)progress).Report(m), createService(), Session.HashCache,
                    (done, total, file) => ((IProgress<Tuple<int, int, string>>)counts).Report(Tuple.Create(done, total, file))), ct);
                ct.ThrowIfCancellationRequested();
                ReportScanProgress(run, "整理检查结果");
                LimitBrowserSources();
                Session.Checked = true;
                Workflow.UpdateAffected(Session); if (assemblyBrowser == null) AssemblyCatalog.Evaluate(Session);
                binding = true; stateFilter.SelectedIndex = 1; binding = false;
                BindSources(); ShowAffected(); Save();
                status.Text = "检查完成 · 可更新 " + Session.Sources.Count(s => (!onlyExisting.Checked || s.LocalExists) && s.DisplayState == "可更新") + " · 总装 " + Session.Assemblies.Count + " · " + watch.Elapsed.TotalSeconds.ToString("F1") + " 秒";
                uiTips.SetToolTip(status, "云端文件 " + Session.Sources.Count + "；缺失目标 " + Session.Sources.Sum(s => s.Plans.Count(p => !File.Exists(p.Local)) + s.AmbiguousLocals.Count(p => !File.Exists(p))) + "；DWG 读取/复用 " + (reader.Reads - oldReads) + "/" + (reader.Hits - oldHits) + "；哈希读取/复用 " + (Session.HashCache.Reads - oldHashReads) + "/" + (Session.HashCache.Hits - oldHashHits));
                }
                finally { if (ownProgress) EndScanProgress(run); }
            });
        }
        private void BindSources()
        {
            binding = true;
            try
            {
                var selected = systemFilter.SelectedItem as string;
                systemFilter.Items.Clear(); systemFilter.Items.Add("全部系统"); systemFilter.Items.AddRange(Session.Sources.Select(s => s.System ?? "公共 / 未分类").Distinct().OrderBy(x => x).Cast<object>().ToArray());
                systemFilter.SelectedItem = selected != null && systemFilter.Items.Contains(selected) ? selected : "全部系统";
            }
            finally { binding = false; }
            FilterSources();
        }
        private void FilterSources()
        {
            if (binding || Session == null) return;
            binding = true;
            try
            {
                foreach (var source in Session.Sources)
                {
                    source.LocalExists = source.Plans.Any(p => File.Exists(p.Local)) || source.AmbiguousLocals.Any(File.Exists);
                    source.DisplayState = Workflow.SourceState(source, onlyExisting.Checked);
                    source.DisplayLocalSummary = string.Join(" | ", Workflow.VisiblePlans(source, onlyExisting.Checked).Select(p => Path.GetFileName(Path.GetDirectoryName(p.Local)) + "/" + Path.GetFileName(p.Local)));
                }
                sourceTable.SetRows(Session.Sources.Where(s => (!onlyExisting.Checked || s.LocalExists) && (systemFilter.SelectedIndex <= 0 || (s.System ?? "公共 / 未分类") == (string)systemFilter.SelectedItem) && (stateFilter.SelectedIndex <= 0 || (stateFilter.SelectedIndex == 1 ? s.DisplayState == "可更新" : stateFilter.SelectedIndex == 3 ? s.Plans.Count == 0 && s.AmbiguousLocals.Count == 0 : s.DisplayState == "对应有歧义" || s.DisplayState == "需处理"))));
            }
            finally { binding = false; }
            ShowAffected();
        }
        private void ShowAffected()
        {
            if (Session == null || binding) return;
            Workflow.UpdateAffected(Session);
            var related = onlyLinked.Checked ? Workflow.RelatedAssemblies(Session, Session.Sources.Where(s => linkedCloudPaths.Contains(s.Path)), onlyExisting.Checked) : Session.Assemblies;
            modelTable.SetRows(related);
            if (modelsGrid.Rows.Count == 0) modelDetail.Text = linkedCloudPaths.Count == 0 ? "请在左侧点击、Ctrl 多选或勾选需要 Pull 的文件。" : "所选文件没有被当前总装目录内的 DWG 引用。";
            ShowCounts();
        }
        private void ShowCounts()
        {
            pullCount.Text = "已勾选 " + (Session?.Sources.Count(s => s.Selected) ?? 0);
            exportCount.Text = "已勾选 " + (Session?.Assemblies.Count(a => a.Selected) ?? 0);
        }
        private void SetLinkedSources(IEnumerable<CloudUpdate> sources) { linkedCloudPaths.Clear(); foreach (var source in sources ?? Enumerable.Empty<CloudUpdate>()) linkedCloudPaths.Add(source.Path); }
        private void FollowSourceRows() { followChecks = false; SetLinkedSources(sourcesGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<CloudUpdate>()); ShowSource(); }
        private void SelectSources(bool selected) { if (Session == null) return; sourcesGrid.EndEdit(); foreach (var row in sourcesGrid.Rows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<CloudUpdate>()) row.Selected = selected && Workflow.SourceState(row, onlyExisting.Checked) == "可更新"; followChecks = true; SetLinkedSources(Session.Sources.Where(r => r.Selected)); sourcesGrid.Refresh(); ShowAffected(); }
        private CloudUpdate CurrentSource => sourcesGrid.CurrentRow?.DataBoundItem as CloudUpdate;
        private void ShowSource()
        {
            if (assemblyBrowser != null) return;
            if (binding) return;
            ShowAffected();
            var source = CurrentSource; if (source == null) { sourceDetail.Clear("选择文件查看详情"); return; }
            var lines = source.Plans.Select(p => "本地：" + p.Local + "\r\n" + (onlyExisting.Checked && !File.Exists(p.Local) ? "本目标被存在筛选排除，不执行 Pull。" : "本地修改日期：" + (File.Exists(p.Local) ? File.GetLastWriteTime(p.Local).ToString("yyyy-MM-dd HH:mm:ss") : "不存在，需逐项确认新建")) + "\r\n" + (p.Row.Result ?? p.Blocked ?? p.Action));
            _ = sourceDetail.ShowFileAsync(source.Path, source.DisplayState + " · " + source.System + " · " + source.MissingSummary + "\r\n" + string.Join("\r\n", lines) + (source.Plans.Count == 0 ? "\r\n新增文件，待确认目标与定位" : "") + "\r\n对应：" + source.MatchReason + "\r\n镜像：" + (source.ExpectedLocalPath ?? "未设置") + "\r\n总装：" + source.AffectedSummary + "\r\n" + string.Join("\r\n", source.Diagnostics.Concat(source.MissingReferenceIssues)), Session.HashCache);
            ShowModel();
        }
        private void ShowModel()
        {
            if (!(modelsGrid.CurrentRow?.DataBoundItem is AssemblyUpdate a)) return;
            modelDetail.Text = a.Status + " · " + a.ExportState + "\r\n已更新 Xref：" + (string.IsNullOrEmpty(a.UpdatedXrefs) ? "无" : a.UpdatedXrefs) + (string.IsNullOrEmpty(a.PendingXrefs) ? "" : "\r\n待 Pull：" + a.PendingXrefs) + "\r\n文件名：" + a.DisplayName + "\r\n总装路径：" + a.Path + (string.IsNullOrEmpty(a.ExportPath) ? "" : "\r\nNWC：" + a.ExportPath) + (a.Scan.Warnings.Count == 0 ? "" : "\r\n诊断：\r\n" + string.Join("\r\n", a.Scan.Warnings));
        }
        private async Task MapLocal()
        {
            var source = CurrentSource; if (source == null || Session == null) throw new IOException("请先检查，并选择左侧云端 DWG。");
            using (var picker = new OpenFileDialog { Title = "为 " + source.Name + " 指定本地工作 DWG", Filter = "DWG (*.dwg)|*.dwg", FileName = source.Name, CheckFileExists = true })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                var local = Paths.Canonical(picker.FileName);
                if (!Session.Assemblies.Any(a => a.Scan.Rows.Any(r => string.Equals(Paths.Canonical(r.LocalPath), local, StringComparison.OrdinalIgnoreCase)))) throw new IOException("该文件不是当前检测范围内的 Xref。请扩大本地图纸检测范围后重试。");
                settings.Mappings[Settings.MapKey(FolderLink.Parse(Session.ConnectorRoot), local)] = source.Path;
            }
            Session.Checked = false;
            await RecheckMappings();
        }
        private async Task RecheckMappings()
        {
            Session.Checked = false;
            await Busy(async ct => { Session.Sources = await Task.Run(() => Workflow.Check(Session, settings, ct, null, createService(), Session.HashCache), ct); Session.Checked = true; BindSources(); ShowAffected(); Save(); status.Text = "对应规则已保存并重新比较；未复制文件。请检查可更新清单。"; });
        }
        private async Task MapFolders()
        {
            ValidateSession();
            var folder = FolderLink.Parse(Session.ConnectorRoot);
            var parents = Session.Assemblies.SelectMany(a => a.Scan.Rows).Select(r => Path.GetDirectoryName(r.LocalPath)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            using (var dialog = new SystemFolderForm(folder, parents, settings))
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await RecheckMappings();
        }
        private void ValidateSession()
        {
            if (Session == null || !Session.Checked || !InputMatchesSession) throw new IOException("目录、检测范围或嵌套引用已改变，请先重新检查可更新 DWG。");
        }
        private async Task ImportNewFiles()
        {
            await Busy(async ct =>
            {
                ValidateSession(); sourcesGrid.EndEdit();
                var service = createService();
                var chosen = Session.Sources.Where(s => s.Selected).ToList();
                var batch = await Task.Run(() => NewCloudFiles.Plan(Session, settings, chosen, service, ct), ct);
                using (var dialog = new NewCloudFilesForm(Session, batch))
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var selected = NewCloudFiles.ValidateSelection(Session, batch);
                try { await NewCloudFiles.Run(Session, batch, service, ct, Host.CheckAssemblyClosedForAttach, Host.StageNewReferences, reportStorage); }
                finally
                {
                    if (batch.Results.Count > 0)
                    {
                        // 无论部分成功还是取消，重新读取实际磁盘关系，不把计划当作成功。
                        Session.Checked = false; var reader = new CachedReader(new DwgReader());
                        Session.Warnings.Clear();
                        for (int i = 0; i < Session.Assemblies.Count; i++)
                        {
                            var before = Session.Assemblies[i]; var fresh = Workflow.ScanAssembly(before.Path, reader);
                            var result = batch.Results.FirstOrDefault(r => string.Equals(r.Assembly, before.Path, StringComparison.OrdinalIgnoreCase));
                            fresh.Selected = result?.AfterHash != null || before.Selected;
                            fresh.RefreshState = result == null ? before.RefreshState : result.AfterHash == null ? result.State : "源文件已更新；导出时加载";
                            if (fresh.DrawingStamp == before.DrawingStamp && result?.AfterHash == null) { fresh.ExportState = before.ExportState; fresh.ExportHash = before.ExportHash; fresh.ExportPath = before.ExportPath; }
                            Session.Assemblies[i] = fresh; Session.Warnings.AddRange(fresh.Scan.Warnings);
                        }
                        Session.ReferenceCache = reader; Session.HashCache.Clear();
                        Session.Sources = await Task.Run(() => Workflow.Check(Session, settings, CancellationToken.None, null, service, Session.HashCache));
                        Session.Checked = true;
                        stateFilter.SelectedIndex = 0;
                        SetLinkedSources(Session.Sources.Where(s => selected.Any(f => string.Equals(f.Cloud, s.Path, StringComparison.OrdinalIgnoreCase))));
                        BindSources(); ShowAffected(); ShowSource(); Save();
                    }
                }
                status.Text = "新增完成：总装成功 " + batch.Results.Count(r => r.AfterHash != null) + "，未完成 " + batch.Results.Count(r => r.AfterHash == null) + "；右侧显示本次所选文件关联总装，可核对后导出 NWC。日志：" + batch.LogPath;
            });
        }

        private async Task Pull()
        {
            await Busy(async ct =>
            {
                ValidateSession(); sourcesGrid.EndEdit();
                if (Session.Sources.Any(s => s.Selected && s.Plans.Count == 0)) throw new IOException("选中项包含云端新增文件，请使用“新增文件 → 总装”指定落盘位置及挂接总装；普通 Pull 仅更新已有对应。");
                var selected = Workflow.PullRows(Session, onlyExisting.Checked);
                if (selected.Count == 0) throw new IOException("当前筛选下没有可执行本地目标；缺失目标已排除。请核对对应关系或明确关闭存在筛选后恢复缺失文件。");
                var folder = FolderLink.Parse(Session.ConnectorRoot); var service = createService();
                var progress = new Progress<string>(message => { status.Text = message; sourcesGrid.Refresh(); });
                var plans = await Task.Run(() => service.Plan(folder, selected, false, ct, m => ((IProgress<string>)progress).Report(m)), ct);
                foreach (var plan in plans) plan.RequireExistingLocal = onlyExisting.Checked;
                foreach (var source in Session.Sources)
                {
                    var freshPlans = plans.Where(p => string.Equals(p.ConnectorPath, source.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var plan in freshPlans)
                    {
                        plan.AffectedAssemblies = Session.Assemblies.Where(a => a.Scan.Rows.Any(r => string.Equals(r.LocalPath, plan.Local, StringComparison.OrdinalIgnoreCase))).Select(a => a.Path).ToList();
                        plan.Row.SyncState = plan.Blocked != null ? SyncFeedback.Review : plan.IsUnchanged ? SyncFeedback.DateMatched : SyncFeedback.Pending;
                        source.Plans.RemoveAll(p => string.Equals(p.Local, plan.Local, StringComparison.OrdinalIgnoreCase)); source.Plans.Add(plan);
                    }
                    if (freshPlans.Count > 0) source.State = Workflow.SourceState(source, false);
                }
                FilterSources(); ShowAffected();
                if (plans.All(p => p.Blocked == null && p.IsUnchanged))
                {
                    var equalLog = service.BeginUpdateLog(folder, plans, false, Session.ScopeFolder);
                    Session.LastLog = Session.LastUpdateLog = equalLog.Path;
                    await Task.Run(() => service.Execute(folder, plans, false, ct, null, equalLog));
                    Workflow.RecordPull(Session, plans); BindSources(); ShowAffected();
                    status.Text = (plans.Any(p => !p.Verified || p.Row.SyncState == SyncFeedback.Failed) ? "未复制；预览后文件发生变化，请核对日志。" : "所选文件修改日期相同，未复制；不代表内容相同。") + "日志：" + equalLog.Path; return;
                }
                List<TransferPlan> approved;
                using (var preview = new PreviewForm(folder, plans, false, onlyExisting.Checked)) { if (preview.ShowDialog(this) != DialogResult.OK) return; approved = preview.ApprovedPlans; }
                var affected = Session.Assemblies.Where(a => a.Scan.Rows.Any(r => approved.Any(p => string.Equals(p.Local, r.LocalPath, StringComparison.OrdinalIgnoreCase)))).ToList();
                if (approved.Count == 0) return;
                approved.AddRange(plans.Where(p => p.Blocked == null && p.IsUnchanged));
                var journal = service.BeginUpdateLog(folder, approved, false, Session.ScopeFolder.Length == 0 ? "当前 Plant 项目" : Session.ScopeFolder);
                Session.LastLog = journal.Path;
                Session.LastUpdateLog = journal.Path;
                IDisposable references = null;
                string failure = null;
                try
                {
                    references = Host.ReleaseWorkflowReferences(affected, approved.Where(p => !p.IsUnchanged).Select(p => p.Row));
                    await Task.Run(() => service.Execute(folder, approved, false, ct, m => ((IProgress<string>)progress).Report(m), journal));
                }
                catch (Exception ex) { failure = ex is OperationCanceledException ? "用户取消" : ex.Message; throw; }
                finally
                {
                    try { references?.Dispose(); }
                    catch (Exception ex) { failure = (failure == null ? "" : failure + "；") + "引用恢复失败：" + ex.Message; Session.Checked = false; Session.Warnings.Add(failure); }
                    var reloadFailure = Workflow.RecordReferenceRecovery(Session, affected);
                    if (reloadFailure != null) failure = (failure == null ? "" : failure + "；") + reloadFailure;
                    Workflow.RecordPull(Session, approved);
                    var fresh = new CachedReader(new DwgReader());
                    foreach (var a in affected.Where(a => a.UpdatedPaths.Count > 0))
                    {
                        var known = new HashSet<string>(a.Scan.Rows.Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase);
                        a.Selected = true;
                        if (a.RefreshState == "尚未 Pull") a.RefreshState = "源文件已更新；导出时加载";
                        a.Scan = Workflow.ScanAssembly(a.Path, fresh).Scan;
                        if (!known.SetEquals(a.Scan.Rows.Select(r => r.LocalPath))) Session.Checked = false;
                        Session.Warnings.AddRange(a.Scan.Warnings.Select(w => a.Name + "：" + w));
                        if (a.Scan.Rows.Any(r => approved.Any(p => p.Row.SyncState == SyncFeedback.Failed && string.Equals(p.Local, r.LocalPath, StringComparison.OrdinalIgnoreCase)))) a.RefreshState = "部分 Pull 失败";
                    }
                    BindSources(); ShowAffected(); ShowSource(); ShowModel();
                    journal.Write(failure != null ? "中止 / 需核对" : approved.Any(p => !p.Verified || p.Row.SyncState == SyncFeedback.Failed) ? "完成，含失败 / 未执行项" : "完成", failure);
                }
                status.Text = "Pull 完成 · 成功 " + approved.Count(p => p.Written && p.Verified) + " · 失败 " + approved.Count(p => p.Row.SyncState == SyncFeedback.Failed) + (Session.Checked ? "" : " · 需重检"); uiTips.SetToolTip(status, Session.LastLog ?? "");
            });
        }
        private void Export()
        {
            modelsGrid.EndEdit();
            if (Session == null) throw new IOException("请选择要导出的本地总装。");
            Session.OutputFolder = output.Text;
            if (string.IsNullOrWhiteSpace(output.Text)) { Browse(output); Session.OutputFolder = output.Text; }
            ExportRequests = Workflow.ExportJobs(Session, output.Text, modelsGrid.Rows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<AssemblyUpdate>());
            if (ExportRequests.Count == 0) throw new IOException("请勾选要导出的本地总装。");
            Save(); DialogResult = DialogResult.OK; Close();
        }
        private static void ColorStatus(DataGridViewCellStyle style, string value)
        {
            if (value == null) return;
            var failed = value.Contains("失败"); var done = value == "已 Pull" || value == "日期相同" || value == "已导出" || value == "引用已重载";
            var pending = value == "可更新" || value.Contains("待") || value.Contains("歧义") || value.Contains("需处理") || value.Contains("导出时");
            if (!failed && !done && !pending) return;
            style.BackColor = failed ? Color.FromArgb(255, 231, 231) : done ? Color.FromArgb(224, 245, 229) : Color.FromArgb(255, 247, 227);
            style.SelectionBackColor = failed ? Color.FromArgb(246, 193, 193) : done ? Color.FromArgb(179, 224, 192) : Color.FromArgb(255, 229, 176);
            style.ForeColor = style.SelectionForeColor = failed ? Color.Firebrick : done ? Color.DarkGreen : Color.FromArgb(137, 91, 14);
        }
    }

    // 文件夹规则优先于自动系统推断，但不覆盖已逐文件确认的对应关系。
    internal sealed class SystemFolderForm : Form
    {
        private sealed class FolderRule
        {
            public string LocalFolder { get; set; }
            public string FolderName => Path.GetFileName(LocalFolder);
            public string CloudFolder { get; set; }
        }
        public SystemFolderForm(FolderLink folder, List<string> localFolders, Settings settings)
        {
            Text = "系统文件夹对应 · 只保存规则，不复制文件"; Width = 1120; Height = 680; MinimumSize = new Size(900, 550); StartPosition = FormStartPosition.CenterParent;
            var choices = new List<string> { "（自动判断）", "." };
            choices.AddRange(Directory.GetDirectories(folder.Project).Select(Path.GetFileName).OrderBy(x => x));
            var rows = localFolders.Select(p => new FolderRule { LocalFolder = p, CloudFolder = settings.SystemFolders.TryGetValue(Settings.MapKey(folder, p), out var saved) ? saved : "（自动判断）" }).ToList();
            foreach (var missing in rows.Select(r => r.CloudFolder).Where(v => !choices.Contains(v)).Distinct()) choices.Add(missing);
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "FolderName", HeaderText = "本地文件夹", Width = 200, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LocalFolder", DataPropertyName = "LocalFolder", HeaderText = "本地 Xref 所在文件夹", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55 });
            grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "CloudFolder", HeaderText = "对应的云端系统文件夹（下拉选择）", DataSource = choices, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, FlatStyle = FlatStyle.Flat });
            var table = new ReviewTable<FolderRule>(grid, r => r.LocalFolder + " " + r.CloudFolder, r => r.CloudFolder == "（自动判断）" ? "自动判断" : "已指定规则"); table.SetRows(rows);
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is FolderRule row) e.ToolTipText = row.LocalFolder; };
            var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var apply = new RoundedButton { Text = "保存并重新检测", Primary = true }; var cancel = new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel };
            apply.Click += (s, e) =>
            {
                grid.EndEdit();
                foreach (var row in rows)
                {
                    var key = Settings.MapKey(folder, row.LocalFolder);
                    if (row.CloudFolder == "（自动判断）") settings.SystemFolders.Remove(key); else settings.SystemFolders[key] = row.CloudFolder;
                }
                DialogResult = DialogResult.OK; Close();
            };
            foot.Controls.Add(cancel); foot.Controls.Add(apply); Controls.Add(table); Controls.Add(foot);
            Controls.Add(ModernUi.Header("按系统目录批量确认", "本地 OCF → 云端 9421_OCF；同名文件仅在选定系统中匹配。“.”表示云端根目录；逐文件人工对应优先。"));
            ModernUi.Apply(this); CancelButton = cancel;
        }
    }
}
