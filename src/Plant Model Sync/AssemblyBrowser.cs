using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class AssemblyBrowserRow
    {
        public bool Selected { get; set; }
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string State { get; set; } = "未检查";
    }
    internal sealed class ReferenceComparisonRow
    {
        public bool Referenced { get; set; }
        public bool Unloaded { get; set; }
        public bool RequiresPathRepair { get; set; }
        public bool PairConfirmed { get; set; }
        public bool LocalExists => LocalState?.StartsWith("存在", StringComparison.Ordinal) == true;
        public bool CloudExists => CloudState == "存在";
        // 绿色表示存在且已引用，不代表日期相同或已完成 Pull；歧义与失效引用不能冒充绿色。
        public int DisplayPriority => CloudExists && LocalExists && Referenced && PairConfirmed && !RequiresPathRepair && CandidateCloudPaths.Count == 0 ? 0
            : CloudExists && (!LocalExists || !Referenced || RequiresPathRepair) ? 1 : 2;
        public List<string> ReferencePaths { get; } = new List<string>();
        public List<string> ReferenceNames { get; } = new List<string>();
        public List<string> CandidateCloudPaths { get; } = new List<string>();
        public string ReferenceName => ReferenceNames.Count == 0 ? "—" : string.Join(" | ", ReferenceNames);
        public string Usage => Referenced ? Unloaded ? "已引用（卸载）" : "已引用" : "未引用";
        public string ShortLocal => ShortPath(Local);
        public string ShortCloud => ShortPath(Cloud);
        public bool MissingLocalAvailableCloud => (RequiresPathRepair || LocalState?.StartsWith("缺失", StringComparison.Ordinal) == true) && CloudState == "存在";
        internal static string ShortPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) return value;
            var parts = value.Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 3 ? value : "…\\" + string.Join("\\", parts.Skip(parts.Length - 3));
        }
        public string Name { get; set; }
        public string LocalState { get; set; }
        public string CloudState { get; set; }
        public string State { get; set; }
        public string Local { get; set; }
        public string Cloud { get; set; }
        public string LocalDate { get; set; }
        public string CloudDate { get; set; }
        public string DateDifference { get; set; }
        public string Comparison => string.IsNullOrEmpty(DateDifference) ? State : State + " · " + DateDifference;
        internal static string FormatDateDifference(DateTime? localUtc, DateTime? cloudUtc)
        {
            if (!localUtc.HasValue || !cloudUtc.HasValue) return "无法比较日期";
            var delta = cloudUtc.Value - localUtc.Value;
            if (delta == TimeSpan.Zero) return "相差 0 天";
            var duration = delta.Duration();
            var direction = delta > TimeSpan.Zero ? "云端较新 " : "本地较新 ";
            if (duration.TotalDays < 1) return direction + "不足 1 天";
            return direction + duration.Days + " 天" + (duration.Hours == 0 ? "" : " " + duration.Hours + " 小时");
        }
    }
    internal sealed partial class WorkflowForm
    {
        private ReviewTable<AssemblyBrowserRow> assemblyBrowser;
        private ReviewTable<ReferenceComparisonRow> referenceBrowser;
        private List<AssemblyBrowserRow> browserRows = new List<AssemblyBrowserRow>();
        private string[] inspectionPaths;
        private bool browserBusy;
        private Label referenceTitle;
        private string displayedAssemblyPath;
        internal Action<System.Diagnostics.ProcessStartInfo> StartReferenceFolder = info => System.Diagnostics.Process.Start(info);

        private void InitializeAssemblyBrowser()
        {
            // 原操作控件保留给已验证的工作流；新界面独立管理总装选择。
            var legacy = new Panel { Visible = false };
            foreach (var control in Controls.Cast<Control>().ToArray()) legacy.Controls.Add(control);
            Controls.Add(legacy); Controls.Add(status); sourceDetail.CancelRead();
            var grid = MakeGrid(); var refs = MakeGrid(); refs.ReadOnly = true;
            AddColumn(grid, "Selected", "选择", 46, true);
            AddColumn(grid, "Name", "本地总装 DWG", 280, false, 100);
            AddColumn(grid, "State", "检查结果", 105);
            grid.Columns["Name"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
            foreach (var spec in new[] { new[] { "Name", "Xref DWG" }, new[] { "LocalState", "本地状态" }, new[] { "CloudState", "云端状态" }, new[] { "State", "对比结果" }, new[] { "Local", "本地路径" }, new[] { "Cloud", "Connector 路径" }, new[] { "LocalDate", "本地修改时间" }, new[] { "CloudDate", "云端修改时间" } })
                AddColumn(refs, spec[0], spec[1], spec[0] == "Name" ? 220 : spec[0] == "Local" || spec[0] == "Cloud" ? 300 : 135);
            assemblyBrowser = new ReviewTable<AssemblyBrowserRow>(grid, r => r.Name + " " + r.Path + " " + r.State, r => r.State);
            referenceBrowser = new ReviewTable<ReferenceComparisonRow>(refs, r => r.Name + " " + r.ReferenceName + " " + r.Local + " " + r.Cloud + " " + r.Comparison, r => r.State);
            referenceBrowser.SortGroup = r => r.DisplayPriority;
            Compact(assemblyBrowser); Compact(referenceBrowser);
            assemblyBrowser.Search.Text = "-M-";
            assemblyBrowser.Search.Width = 110; assemblyBrowser.Filter.Width = 100;
            foreach (var bar in assemblyBrowser.Controls.OfType<FlowLayoutPanel>()) { bar.WrapContents = true; bar.AutoScroll = false; bar.Height = 78; }
            refs.Columns["Name"].Width = 160;
            refs.Columns["LocalState"].Width = refs.Columns["CloudState"].Width = 90;
            refs.Columns["State"].Width = 260;
            refs.Columns["State"].DataPropertyName = "Comparison";
            foreach (var name in new[] { "Local", "Cloud" }) { refs.Columns[name].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; refs.Columns[name].MinimumWidth = 100; }
            refs.Columns["LocalDate"].Visible = refs.Columns["CloudDate"].Visible = false;
            refs.Columns["Local"].DataPropertyName = "ShortLocal"; refs.Columns["Cloud"].DataPropertyName = "ShortCloud";
            AddColumn(refs, "Usage", "引用", 110); refs.Columns["Usage"].DisplayIndex = 1;
            AddColumn(refs, "ReferenceName", "总装引用名", 170); refs.Columns["ReferenceName"].DisplayIndex = 1;
            refs.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || !(refs.Rows[e.RowIndex].DataBoundItem is ReferenceComparisonRow row)) return;
                ApplyReferenceColor(e.CellStyle, row);
            };
            var referenceTools = referenceBrowser.Controls.OfType<FlowLayoutPanel>().Single();
            AsyncButton(referenceTools, "下载所选 Xref", DownloadCurrentReference);
            AsyncButton(referenceTools, "引用到当前总装", AttachCurrentLocalReference);
            referenceTools.WrapContents = true; referenceTools.Height = 86; referenceTools.AutoScroll = false;
            grid.Columns["Selected"].HeaderCell = new VisibleSelectionHeader<AssemblyBrowserRow>(r => true, r => r.Selected, (r, v) => r.Selected = v, () => { });
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            // 筛选和勾选可能改变高亮行，但不应因此读取未勾选的图纸。
            grid.CellClick += async (s, e) => { if (!browserBusy && e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name != "Selected" && grid.Rows[e.RowIndex].DataBoundItem is AssemblyBrowserRow row) await InspectModel(row); };
            grid.KeyUp += async (s, e) => { if (!browserBusy && e.KeyCode == Keys.Enter && grid.CurrentCell?.OwningColumn.Name != "Selected" && grid.CurrentRow?.DataBoundItem is AssemblyBrowserRow row) { e.Handled = true; await InspectModel(row); } };
            grid.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0) e.ToolTipText = (grid.Rows[e.RowIndex].DataBoundItem as AssemblyBrowserRow)?.Path; };
            refs.CellToolTipTextNeeded += (s, e) => { if (e.RowIndex >= 0 && refs.Rows[e.RowIndex].DataBoundItem is ReferenceComparisonRow r) e.ToolTipText = "总装引用名：" + r.ReferenceName + "\r\nDWG 文件：" + r.Name + "\r\n本地：" + r.Local + "\r\n" + r.LocalDate + "\r\n云端：" + r.Cloud + "\r\n" + r.CloudDate + "\r\n" + r.Comparison + (r.CandidateCloudPaths.Count > 0 ? "\r\n尚未确认的云端候选：\r\n" + string.Join("\r\n", r.CandidateCloudPaths) : "") + (r.RequiresPathRepair ? "\r\n总装原引用路径：\r\n" + string.Join("\r\n", r.ReferencePaths) + "\r\n使用“更新所选”修复已有引用，不新增实例。" : ""); };
            var copyMenu = new ContextMenuStrip();
            MenuAsync(copyMenu, "指定云端文件…", ConfirmReferenceSource);
            MenuAsync(copyMenu, "下载到本地", DownloadCurrentReference);
            MenuAsync(copyMenu, "引用到当前总装", AttachCurrentLocalReference);
            copyMenu.Items.Add(new ToolStripSeparator());
            var openLocal = copyMenu.Items.Add("打开本地所在文件夹", null, (s, e) => OpenReferenceFolder(false));
            var openCloud = copyMenu.Items.Add("打开云端所在文件夹", null, (s, e) => OpenReferenceFolder(true));
            copyMenu.Opening += (s, e) =>
            {
                var row = refs.CurrentRow?.DataBoundItem as ReferenceComparisonRow;
                openLocal.Enabled = ExistingReferenceFolder(row?.Local) != null;
                openCloud.Enabled = ExistingReferenceFolder(row?.Cloud) != null;
            };
            copyMenu.Items.Add(new ToolStripSeparator());
            MenuAction(copyMenu, "复制本地路径", () => { if (refs.CurrentRow?.DataBoundItem is ReferenceComparisonRow r && !string.IsNullOrEmpty(r.Local)) Clipboard.SetText(r.Local); });
            MenuAction(copyMenu, "复制云端路径", () => { if (refs.CurrentRow?.DataBoundItem is ReferenceComparisonRow r && !string.IsNullOrEmpty(r.Cloud)) Clipboard.SetText(r.Cloud); });
            refs.ContextMenuStrip = copyMenu; Disposed += (s, e) => copyMenu.Dispose();
            refs.CellMouseDown += (s, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0) refs.CurrentCell = refs.Rows[e.RowIndex].Cells[e.ColumnIndex]; };
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1380, 700), SplitterDistance = 470, Panel1MinSize = 380, Panel2MinSize = 480, SplitterWidth = 8 };
            var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
            left.Controls.Add(assemblyBrowser); left.Controls.Add(new Label { Text = "本地总装", Dock = DockStyle.Top, Height = 32 });
            referenceTitle = new Label { Text = "选择总装查看 Xref", Dock = DockStyle.Top, Height = 32, AutoEllipsis = true };
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White }; right.Controls.Add(referenceBrowser); right.Controls.Add(referenceTitle);
            split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 0) }; body.Controls.Add(split); Controls.Add(body);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(16, 5, 0, 0), WrapContents = false };
            Button(toolbar, "目录", EditBrowserDirectories);
            AsyncButton(toolbar, "检查所选", () => InspectCheckedAssemblies());
            AsyncButton(toolbar, "更新所选", UpdateBrowserSelection, true);
            AsyncButton(toolbar, "导出 NWC", ExportBrowserSelection);
            MenuButton(toolbar, "更多", menu =>
            {
                MenuAsync(menu, "强制刷新", () => InspectCheckedAssemblies(true));
                MenuAction(menu, "查看问题", () => { using (var dialog = new ScanIssuesForm(Session)) dialog.ShowDialog(this); });
                MenuAction(menu, "更新日志", OpenLog); MenuAction(menu, "NWC 日期", CheckNwcDates);
                MenuAsync(menu, "从文件夹导出 NWC…", BrowseNwcExport);
                MenuAsync(menu, "系统目录对应…", MapFolders);
                menu.Items.Add(new ToolStripSeparator());
                MenuAction(menu, "关于", () => { using (var dialog = new AboutForm()) dialog.ShowDialog(this); });
            });
            toolbar.Controls.Add(cancel); Controls.Add(toolbar);
            var heading = ModernUi.Header("Auto sync", ""); heading.Height = 54; foreach (var label in heading.Controls.OfType<Label>().Where(l => l.Text == "")) label.Visible = false; Controls.Add(heading);
            MinimumSize = new Size(1140, 680); Height = 850; ModernUi.Apply(this);
            Controls.Remove(status); Controls.Add(status);
            foreach (var label in refs.Controls.OfType<Label>()) label.Text = "选择左侧总装后显示本地与云端 Xref";
            foreach (var label in grid.Controls.OfType<Label>()) label.Text = "此目录没有总装 DWG，请修改目录";
            onlyExisting.Checked = false;
            Shown += async (s, e) => await LoadAssemblyDirectory();
        }
        private async Task LoadAssemblyDirectory()
        {
            browserBusy = true;
            var run = BeginScanProgress("读取总装目录");
            try
            {
                var paths = await Task.Run(() => Directory.Exists(scopeFolder) ? Directory.GetFiles(scopeFolder, "*.dwg", SearchOption.TopDirectoryOnly) : new string[0]);
                if (IsDisposed || Disposing) return;
                browserRows = paths.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).Select(p => new AssemblyBrowserRow { Path = p }).ToList();
                Session = null; inspectionPaths = null; assemblyBrowser.SetRows(browserRows); referenceBrowser.SetRows(new ReferenceComparisonRow[0]);
                referenceTitle.Text = "选择总装查看 Xref"; status.Text = "总装 " + browserRows.Count + " 个";
            }
            catch (Exception ex) { Error(ex); }
            finally { EndScanProgress(run); browserBusy = false; }
        }
        internal static string ExistingReferenceFolder(string file)
        {
            try
            {
                // 仅打开 DWG 的实际父目录，不将占位文字或网页链接传给资源管理器。
                if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file) || file.Contains("\"")) return null;
                var folder = Path.GetDirectoryName(Path.GetFullPath(file));
                return Directory.Exists(folder) ? folder : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is UnauthorizedAccessException) { return null; }
        }
        private void OpenReferenceFolder(bool cloud)
        {
            var row = referenceBrowser.Grid.CurrentRow?.DataBoundItem as ReferenceComparisonRow;
            var folder = ExistingReferenceFolder(cloud ? row?.Cloud : row?.Local);
            if (folder == null) { status.Text = "所在文件夹不存在或尚未对应"; return; }
            try { StartReferenceFolder(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Error(ex); }
        }
        private string[] BrowserSelection()
        {
            assemblyBrowser.Grid.EndEdit();
            var selected = browserRows.Where(r => r.Selected).Select(r => r.Path).ToArray();
            if (selected.Length == 0 && assemblyBrowser.Grid.CurrentRow?.DataBoundItem is AssemblyBrowserRow row) selected = new[] { row.Path };
            if (selected.Length == 0) status.Text = "请先选择总装";
            return selected;
        }
        private async Task InspectCheckedAssemblies(bool force = false)
        {
            if (browserBusy || work != null) return;
            assemblyBrowser.Grid.EndEdit();
            // 批量检查只采用复选框，不回退到高亮行或全部模型；筛选隐藏的勾选仍保留。
            var paths = browserRows.Where(r => r.Selected).Select(r => r.Path).ToArray();
            if (paths.Length == 0) { status.Text = "请先勾选需要检查的总装"; return; }
            if (force) InvalidateAutomaticMappings();
            await InspectPaths(paths);
        }
        private void InvalidateAutomaticMappings()
        {
            // 仅丢弃可再生成的检测结果；人工文件/文件夹规则、同步记录和导出历史不是缓存。
            Session?.InvalidateAutomaticMappings(); linkedCloudPaths.Clear(); displayedAssemblyPath = null;
            foreach (var row in browserRows) row.State = "未检查";
            referenceBrowser.SetRows(new ReferenceComparisonRow[0]); referenceTitle.Text = "Xref";
            assemblyBrowser.Grid.Refresh();
        }
        internal static void ApplyReferenceColor(DataGridViewCellStyle style, ReferenceComparisonRow row)
        {
            if (row.DisplayPriority == 0)
            {
                style.BackColor = Color.FromArgb(220, 244, 228); style.SelectionBackColor = Color.FromArgb(190, 230, 206);
                style.ForeColor = style.SelectionForeColor = Color.FromArgb(26, 98, 54);
            }
            else if (row.DisplayPriority == 1 || row.MissingLocalAvailableCloud)
            {
                style.BackColor = style.SelectionBackColor = Color.FromArgb(255, 237, 170);
                style.ForeColor = style.SelectionForeColor = Color.FromArgb(120, 78, 0);
            }
        }
        private async Task InspectModel(AssemblyBrowserRow row)
        {
            if (browserBusy || work != null) return;
            if (Session?.Checked != true || !Session.Assemblies.Any(a => a.Path == row.Path)) { await InspectPaths(new[] { row.Path }); return; }
            var run = BeginScanProgress("显示 Xref");
            browserBusy = true; assemblyBrowser.Enabled = referenceBrowser.Enabled = false;
            try
            {
                await Busy(async ct =>
                {
                    ReportScanProgress(run, "显示 Xref", file: row.Path);
                    PaintScanProgress();
                    await Task.Yield(); ct.ThrowIfCancellationRequested();
                    ShowReferenceComparison(row.Path);
                });
            }
            finally { EndScanProgress(run); browserBusy = false; assemblyBrowser.Enabled = referenceBrowser.Enabled = true; }
        }
        private async Task InspectPaths(string[] paths)
        {
            if (browserBusy || paths.Length == 0) return;
            browserBusy = true; assemblyBrowser.Enabled = referenceBrowser.Enabled = false;
            var run = BeginScanProgress("准备检查");
            try
            {
                inspectionPaths = paths; if (Session != null) Session.Checked = false;
                await Check();
                ReportScanProgress(run, "整理总装 / Xref 列表");
                foreach (var row in browserRows.Where(r => paths.Contains(r.Path, StringComparer.OrdinalIgnoreCase)))
                {
                    var a = Session?.Assemblies.FirstOrDefault(x => x.Path == row.Path);
                    var comparisons = Session?.Checked != true || a == null ? new List<ReferenceComparisonRow>() : BuildReferenceComparison(a);
                    row.State = Session?.Checked != true ? "检查未完成" : a == null || a.Scan.Warnings.Count > 0 ? "需处理" : comparisons.Any(r => r.State != "日期相同" && r.State != "已 Pull") ? "有差异" : "已检查";
                }
                assemblyBrowser.Grid.Refresh();
                if (Session?.Checked != true)
                {
                    referenceBrowser.SetRows(new ReferenceComparisonRow[0]);
                    referenceTitle.Text = "检查未完成，可重新检查";
                    return;
                }
                var current = assemblyBrowser.Grid.CurrentRow?.DataBoundItem as AssemblyBrowserRow;
                ShowReferenceComparison(current != null && paths.Contains(current.Path) ? current.Path : paths[0]);
            }
            finally { EndScanProgress(run); browserBusy = false; assemblyBrowser.Enabled = referenceBrowser.Enabled = true; }
        }
        private void LimitBrowserSources()
        {
            if (assemblyBrowser == null || Session == null) return;
            var matcher = new SystemMatcher(Session.Sources.Select(s => s.Path));
            var systems = new HashSet<string>(Session.Assemblies.SelectMany(a => matcher.Systems(a.Path)));
            Session.Sources = Session.Sources.Where(s => s.Plans.Any(p => p.AffectedAssemblies.Count > 0) || s.AmbiguousLocals.Any(p => Session.Assemblies.Any(a => a.Scan.Rows.Any(r => string.Equals(r.LocalPath, p, StringComparison.OrdinalIgnoreCase)))) || matcher.Systems(s.Path).Overlaps(systems)).ToList();
        }
        private async Task UpdateBrowserSelection()
        {
            var paths = BrowserSelection(); if (paths.Length == 0) return;
            await InspectPaths(paths); if (Session?.Checked != true) return;
            if (!await RepairMissingAssemblyPaths()) return;
            browserBusy = true; assemblyBrowser.Enabled = referenceBrowser.Enabled = false;
            try { await UpdateAll(); }
            finally { browserBusy = false; assemblyBrowser.Enabled = referenceBrowser.Enabled = true; }
            await InspectPaths(paths);
        }
        private async Task<bool> RepairMissingAssemblyPaths()
        {
            var completed = false; var changed = false;
            await Busy(async ct =>
            {
                var native = Host.LocalRepairDwg();
                var plans = Session.Assemblies.Select(a => AssemblyReferenceRepair.Plan(Session.LocalModelsRoot, Session.ConnectorRoot, a.Path, Session.Sources.Select(s => s.Path), native, Host.CheckAssemblyClosedForAttach)).Where(p => p.Changes.Count > 0).ToList();
                if (plans.Count == 0) { completed = true; return; }
                using (var dialog = new Form { Text = "修复已有 Xref 路径", Width = 1000, Height = 420, StartPosition = FormStartPosition.CenterParent })
                {
                    var grid = MakeGrid(); grid.ReadOnly = true;
                    AddColumn(grid, "File", "总装", 300); AddColumn(grid, "Details", "修复路径", 600, false, 100);
                    var table = new ReviewTable<UpdateReportRow>(grid, r => r.File + " " + r.Details, r => "路径修复");
                    table.SetRows(plans.Select(p => new UpdateReportRow { File = Path.GetFileName(p.Assembly), Details = string.Join(" | ", p.Changes.Select(c => c.Name + " → " + ReferenceComparisonRow.ShortPath(c.Target))) }));
                    var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                    footer.Controls.Add(new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel }); footer.Controls.Add(new RoundedButton { Text = "备份并修复", Primary = true, DialogResult = DialogResult.OK, Width = 130 });
                    dialog.Controls.Add(table); dialog.Controls.Add(footer); ModernUi.Apply(dialog);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                }
                var rows = new List<UpdateReportRow>(); var log = Path.Combine(reportStorage, "Logs", "PathRepairs", "assembly-paths-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
                Session.LastLog = Session.LastUpdateLog = log;
                try
                {
                    // 批次开始前统一检查，避免只修复前一份后才发现后一份仍在打开。
                    foreach (var plan in plans) { Host.CheckAssemblyClosedForAttach(plan.Assembly); Files.AssertClosed(plan.Assembly); }
                    foreach (var plan in plans)
                    {
                        try { rows.Add(AssemblyReferenceRepair.Apply(plan, native, Host.CheckAssemblyClosedForAttach, ct)); changed = true; }
                        catch { if (plan.Report != null) rows.Add(plan.Report); throw; }
                    }
                    completed = true;
                }
                finally
                {
                    WeeklyUpdate.AtomicJson(log, new { Format = "IWMF.OneClickUpdate.v1", State = completed ? "引用路径修复完成" : "引用路径修复未完成", Rows = rows });
                    Session.Checked = false; Session.ReferenceCache?.Clear(); Session.HashCache.Clear();
                }
                await Task.Yield();
            });
            if (changed) await InspectPaths(inspectionPaths);
            return completed;
        }
        private List<ReferenceComparisonRow> BuildReferenceComparison(AssemblyUpdate assembly)
        {
            return ReferenceComparisonBuilder.Build(Session, assembly);
        }
        private void ShowReferenceComparison(string path)
        {
            displayedAssemblyPath = path;
            var assembly = Session?.Assemblies.FirstOrDefault(a => a.Path == path);
            referenceTitle.Text = Path.GetFileName(path) + " · Xref";
            referenceBrowser.SetRows(assembly == null ? new List<ReferenceComparisonRow>() : BuildReferenceComparison(assembly));
            foreach (var label in referenceBrowser.Grid.Controls.OfType<Label>()) label.Text = assembly == null ? "尚未检查" : assembly.Scan.Warnings.Count > 0 ? "引用读取异常，请查看问题" : "此总装没有 Xref";
        }
        private void EditBrowserDirectories()
        {
            using (var dialog = new Form { Text = "目录", Width = 920, Height = 320, MinimumSize = new Size(750, 320), StartPosition = FormStartPosition.CenterParent })
            {
                var values = new[] { connector.Text, localRoot.Text, scopeFolder ?? "", output.Text };
                var labels = new[] { "Connector DWG", "本地模型", "总装 DWG", "NWC 输出" };
                var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Padding = new Padding(15) };
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
                var boxes = new List<TextBox>();
                for (int i = 0; i < 4; i++)
                {
                    fields.RowStyles.Add(new RowStyle(SizeType.Percent, 25)); var box = new TextBox { Text = values[i], Dock = DockStyle.Fill, Margin = new Padding(0, 8, 8, 0) }; boxes.Add(box);
                    fields.Controls.Add(new Label { Text = labels[i], AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, i); fields.Controls.Add(box, 1, i);
                    var browse = new RoundedButton { Text = "浏览" }; browse.Click += (s, e) => { using (var picker = new FolderBrowserDialog { SelectedPath = box.Text }) if (picker.ShowDialog(dialog) == DialogResult.OK) box.Text = picker.SelectedPath; }; fields.Controls.Add(browse, 2, i);
                }
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                var cancelButton = new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel }; var saveButton = new RoundedButton { Text = "保存", Primary = true };
                footer.Controls.Add(cancelButton); footer.Controls.Add(saveButton); dialog.CancelButton = cancelButton;
                saveButton.Click += (s, e) => { if (boxes.Any(b => !Directory.Exists(b.Text.Trim()))) { ModernUi.Message(dialog, "目录", "请选择四个有效文件夹"); return; } dialog.DialogResult = DialogResult.OK; };
                dialog.Controls.Add(fields); dialog.Controls.Add(footer); ModernUi.Apply(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                connector.Text = boxes[0].Text.Trim(); localRoot.Text = boxes[1].Text.Trim(); scopeFolder = boxes[2].Text.Trim(); scope.Text = scopeFolder; output.Text = boxes[3].Text.Trim(); Save();
            }
            _ = LoadAssemblyDirectory();
        }
    }
}
