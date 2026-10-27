using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifyAssemblyBrowser(string evidence)
        {
            await LoadAssemblyDirectory();
            WorkflowTestProgram.Check(browserRows.Count == 2 && browserRows.All(r => r.State == "未检查"), "打开新界面只列出指定目录顶层总装，不递归混入源图纸");
            WorkflowTestProgram.Check(assemblyBrowser.Search.Text == "-M-" && assemblyBrowser.Grid.Rows.Count == 1 && ((AssemblyBrowserRow)assemblyBrowser.Grid.Rows[0].DataBoundItem).Name.Contains("-M-"), "默认搜索-M-，列表保留MLP数据但默认只显示M总装");
            await InspectCheckedAssemblies();
            WorkflowTestProgram.Check(Session == null && status.Text.Contains("请先勾选"), "无勾选的检查所选不回退到当前行或全目录扫描");
            await InspectCheckedAssemblies(true);
            WorkflowTestProgram.Check(Session == null, "无勾选的强制刷新同样不扩大检查范围");
            WorkflowTestProgram.Check(!pathFields.Visible && !sourceDetail.Visible && !modelDetailsPanel.Visible && Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Count(b => b.Visible) == 5, "总装界面只有五个主要入口且不显示目录行及旧详情区");
            WorkflowTestProgram.Check(Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Any(b => b.Text == "检查所选"), "批量检查按钮准确命名为检查所选");
            var aboutOpened = false;
            using (var timer = new Timer { Interval = 50 })
            {
                timer.Tick += (s, e) =>
                {
                    var dialog = Application.OpenForms.OfType<AboutForm>().FirstOrDefault(); if (dialog == null) return;
                    timer.Stop(); aboutOpened = true;
                    var content = dialog.Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TextBox>()).Single();
                    WorkflowTestProgram.Check(content.ReadOnly && content.Text.Contains("SO") && content.Text.Contains("分楼层") && content.Text.Contains("每个系统的总装") && content.Text.Contains("手动上传 ACC"), "关于弹窗只读展示SO分楼层及系统总装需求和插件工作流程");
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height)); bitmap.Save(Path.Combine(evidence, "about-auto-sync.png"), ImageFormat.Png); }
                    dialog.Close();
                };
                timer.Start();
                Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "更多").ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "关于").PerformClick();
            }
            WorkflowTestProgram.Check(aboutOpened && Session == null && browserRows.All(r => !r.Selected), "更多关于入口可打开关闭，不触发扫描或改变勾选");
            var selected = browserRows.Single(r => r.Name.Contains("9424")); selected.Selected = true;
            await InspectCheckedAssemblies();
            WorkflowTestProgram.Check(Session.Assemblies.Count == 1 && Session.Assemblies[0].Path == selected.Path && Session.Sources.All(s => !s.Path.Contains("9422") && !s.Path.Contains("9460")), "单总装检查限定模型与关联源，不纳入其他系统源");
            WorkflowTestProgram.Check(selected.Selected && browserRows.Single(r => r.Name.Contains("9422")).State == "未检查", "隐藏勾选仍参与检查，可见但未勾选的总装不参与");
            await InspectCheckedAssemblies(true);
            WorkflowTestProgram.Check(Session.Assemblies.Count == 1 && Session.Assemblies[0].Path == selected.Path, "强制刷新也仅重新检查已勾选总装");
            assemblyBrowser.Search.Clear();
            WorkflowTestProgram.Check(Session.Assemblies.Count == 1 && Session.Assemblies[0].Path == selected.Path, "清空搜索不因高亮行变化自动扫描未勾选模型");
            var refs = BuildReferenceComparison(Session.Assemblies[0]);
            WorkflowTestProgram.Check(refs.Any(r => r.LocalState == "存在" && r.CloudState == "存在" && r.State == "可更新") && refs.Any(r => r.LocalState == "缺失" && r.CloudState == "未匹配"), "逐引用并排显示本地与云端状态且保留缺失或未匹配项");
            WorkflowTestProgram.Check(refs.Any(r => r.State == "新增候选 · 待确认"), "同系统未引用新源可见且标记需要确认");
            var second = browserRows.Single(r => r.Name.Contains("9422")); second.Selected = true;
            await InspectCheckedAssemblies();
            WorkflowTestProgram.Check(Session.Assemblies.Count == 2 && selected.Selected && second.Selected, "多选批量检查只处理勾选集合且不改变勾选");
            second.Selected = false;
            var other = browserRows.Single(r => r.Name.Contains("9422")); ShowReferenceComparison(other.Path);
            WorkflowTestProgram.Check(referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().All(r => ((ReferenceComparisonRow)r.DataBoundItem).Name == "air.dwg"), "切换总装右侧只显示该模型引用");
            assemblyBrowser.Search.Text = "9422"; WorkflowTestProgram.Check(selected.Selected && BrowserSelection().Single() == selected.Path, "总装筛选保留隐藏勾选和更新目标"); assemblyBrowser.Search.Clear();
            var original = connector.Text;
            using (var timer = new Timer { Interval = 50 })
            {
                timer.Tick += (s, e) => { var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "目录" && f.Modal); if (dialog == null) return; timer.Stop(); var boxes = dialog.Controls.OfType<TableLayoutPanel>().SelectMany(p => p.Controls.OfType<TextBox>()).ToList(); WorkflowTestProgram.Check(boxes.Count == 4, "目录按钮弹窗集中编辑四个路径"); boxes[0].Text = "cancelled-path"; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); };
                timer.Start(); EditBrowserDirectories();
            }
            WorkflowTestProgram.Check(connector.Text == original, "取消目录弹窗不更改现有路径");
            ShowReferenceComparison(selected.Path);
            var comparisonRows = BuildReferenceComparison(Session.Assemblies.Single(a => a.Path == selected.Path));
            var utc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            WorkflowTestProgram.Check(ReferenceComparisonRow.FormatDateDifference(utc, utc.AddDays(6)) == "云端较新 6 天" && ReferenceComparisonRow.FormatDateDifference(utc.AddDays(2).AddHours(3), utc) == "本地较新 2 天 3 小时", "日期差显示方向以及完整天数和余下小时");
            WorkflowTestProgram.Check(ReferenceComparisonRow.FormatDateDifference(utc, utc.AddHours(4)) == "云端较新 不足 1 天" && ReferenceComparisonRow.FormatDateDifference(utc, utc) == "相差 0 天" && ReferenceComparisonRow.FormatDateDifference(null, utc) == "无法比较日期", "不足一天和相同时间明确显示，缺失一侧不伪造零天");
            WorkflowTestProgram.Check(comparisonRows.Single(r => r.Name == "gds.dwg").Comparison == "可更新 · 云端较新 6 天" && referenceBrowser.Grid.Columns["State"].DataPropertyName == "Comparison", "右侧对比结果绑定日期差而不改变可更新筛选状态");
            var launches = new System.Collections.Generic.List<System.Diagnostics.ProcessStartInfo>();
            var launch = StartReferenceFolder; StartReferenceFolder = info => launches.Add(info);
            try
            {
                var rightClicked = referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Single(r => ((ReferenceComparisonRow)r.DataBoundItem).Name == "gds.dwg");
                var mouse = new DataGridViewCellMouseEventArgs(0, rightClicked.Index, 0, 0, new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0));
                typeof(DataGridView).GetMethod("OnCellMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(DataGridViewCellMouseEventArgs) }, null).Invoke(referenceBrowser.Grid, new object[] { mouse });
                var menu = referenceBrowser.Grid.ContextMenuStrip;
                menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "打开本地所在文件夹").PerformClick();
                menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "打开云端所在文件夹").PerformClick();
                var row = (ReferenceComparisonRow)rightClicked.DataBoundItem;
                WorkflowTestProgram.Check(launches.Count == 2 && launches[0].FileName == "explorer.exe" && launches[0].Arguments == "\"" + Path.GetDirectoryName(row.Local) + "\"" && launches[1].Arguments == "\"" + Path.GetDirectoryName(row.Cloud) + "\"", "右键先选中点击行，两个菜单分别打开完整本地及Connector父目录");
                WorkflowTestProgram.Check(ExistingReferenceFolder(Path.Combine(Path.GetDirectoryName(row.Local), "absent.dwg")) == Path.GetDirectoryName(row.Local) && ExistingReferenceFolder("未匹配") == null && ExistingReferenceFolder("relative.dwg") == null && ExistingReferenceFolder(Path.Combine(evidence, "nonexistent", "file.dwg")) == null, "文件缺失但父目录存在仍可打开，未对应相对路径及缺失目录不启动外部程序");
                var localRow = referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Single(r => ((ReferenceComparisonRow)r.DataBoundItem).Name == "local-only.dwg");
                referenceBrowser.Grid.CurrentCell = localRow.Cells["Name"];
                typeof(ContextMenuStrip).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(System.ComponentModel.CancelEventArgs) }, null).Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                WorkflowTestProgram.Check(menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "打开本地所在文件夹").Enabled && !menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "打开云端所在文件夹").Enabled, "无云端对应时禁用打开云端目录，本地目录仍可用");
            }
            finally { StartReferenceFolder = launch; }
            WorkflowTestProgram.Check(comparisonRows.TakeWhile(r => r.DisplayPriority == 0).Count() == 1 && comparisonRows.Select(r => r.DisplayPriority).SequenceEqual(comparisonRows.Select(r => r.DisplayPriority).OrderBy(p => p)) && comparisonRows.Any(r => !r.Referenced && r.CloudState == "未匹配" && r.LocalState.StartsWith("存在")), "右侧绿色已匹配引用置顶、黄色待处理其次，并保留仅本地未引用文件");
            WorkflowTestProgram.Check(ReferenceComparisonRow.ShortPath(@"C:\project\models\local_9424\level\part.dwg") == @"…\local_9424\level\part.dwg", "路径显示末两级目录与文件名");
            var candidate = comparisonRows.Single(r => r.Name == "new.dwg");
            WorkflowTestProgram.Check(candidate.MissingLocalAvailableCloud && !candidate.Referenced, "本地缺失云端存在的未引用源进入黄色提示状态");
            var targetGridRow = referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Single(r => ((ReferenceComparisonRow)r.DataBoundItem).Name == "new.dwg");
            referenceBrowser.Grid.CurrentCell = targetGridRow.Cells["Name"]; targetGridRow.Selected = true;
            var style = targetGridRow.Cells["LocalState"].GetInheritedStyle(null, targetGridRow.Index, true);
            var colorEvent = new DataGridViewCellFormattingEventArgs(targetGridRow.Cells["LocalState"].ColumnIndex, targetGridRow.Index, candidate.LocalState, typeof(string), style);
            typeof(DataGridView).GetMethod("OnCellFormatting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(DataGridViewCellFormattingEventArgs) }, null).Invoke(referenceBrowser.Grid, new object[] { colorEvent });
            WorkflowTestProgram.Check(colorEvent.CellStyle.BackColor == Color.FromArgb(255, 237, 170) && colorEvent.CellStyle.SelectionBackColor == colorEvent.CellStyle.BackColor, "本地缺失状态格标黄且选中后仍保持黄色");
            Refresh(); using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "xref-missing-highlight.png"), ImageFormat.Png); }
            var rejected = false;
            try { await PlanReferenceDownload(candidate, Path.Combine(evidence, "outside.dwg"), System.Threading.CancellationToken.None); } catch (IOException) { rejected = true; }
            WorkflowTestProgram.Check(rejected, "单文件下载拒绝本地模型目录以外的目标");
            var beforeAssembly = Files.Hash(selected.Path); var otherSource = comparisonRows.Single(r => r.Name == "gds.dwg"); var beforeOther = Files.Hash(otherSource.Local);
            var previewCorrect = false; string unexpectedDialog = null;
            using (var timer = new Timer { Interval = 50 })
            {
                timer.Tick += (s, e) => { var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Modal); if (dialog == null) return; timer.Stop(); if (dialog is PreviewForm preview) { var previewGrid = preview.Controls.OfType<DataGridView>().Single(); previewGrid.Rows[0].Cells["Update"].Value = true; previewGrid.EndEdit(); previewCorrect = preview.ApprovedPlans.Count == 1 && preview.ApprovedPlans[0].Local == candidate.Local; preview.DialogResult = previewCorrect ? DialogResult.OK : DialogResult.Cancel; } else { unexpectedDialog = dialog.Text + " " + string.Join(" | ", dialog.Controls.Cast<Control>().Select(c => c.Text)); } dialog.Close(); };
                timer.Start(); await DownloadCurrentReference();
            }
            WorkflowTestProgram.Check(previewCorrect, "右侧下载预览仅包含所选 Xref 的一个明确本地目标：" + unexpectedDialog);
            WorkflowTestProgram.Check(File.Exists(candidate.Local) && Files.Hash(candidate.Local) == Files.Hash(candidate.Cloud), "右侧单文件下载可创建缺失本地文件并与Connector内容一致");
            WorkflowTestProgram.Check(Files.Hash(selected.Path) == beforeAssembly && Files.Hash(otherSource.Local) == beforeOther, "单文件下载不修改总装或其他Xref，也不自动挂接");
            WorkflowTestProgram.Check(!string.IsNullOrEmpty(Session.LastUpdateLog) && File.Exists(Session.LastUpdateLog), "单文件下载写入可查看更新日志");
            WorkflowTestProgram.Check(BuildReferenceComparison(Session.Assemblies.Single(a => a.Path == selected.Path)).Any(r => r.Name == "new.dwg" && r.LocalState.StartsWith("存在") && !r.MissingLocalAvailableCloud), "下载后刷新本地存在状态并清除黄色缺失提示");
            ShowReferenceComparison(selected.Path);
            var localOnlyRow = referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Single(r => ((ReferenceComparisonRow)r.DataBoundItem).Name == "local-only.dwg");
            referenceBrowser.Grid.CurrentCell = localOnlyRow.Cells["Name"]; localOnlyRow.Selected = true;
            var localOnly = (ReferenceComparisonRow)localOnlyRow.DataBoundItem;
            var sourceHash = Files.Hash(localOnly.Local); var assemblyBeforeAttach = Files.Hash(selected.Path); var placementsCorrect = false; var calls = 0; var confirmationOpened = false;
            var oldStage = Host.NewStage;
            Host.NewStage = (assemblyPath, placements) =>
            {
                calls++; var p = placements.Single(); placementsCorrect = assemblyPath == selected.Path && p.Source == localOnly.Local && p.X == 6653112.1 && p.Y == 29134629 && p.Z == 0 && p.Scale == 1 && p.Rotation == -4.42;
                var staged = Path.Combine(Path.GetDirectoryName(assemblyPath), ".test-attach-" + Guid.NewGuid().ToString("N") + ".dwg");
                File.WriteAllBytes(staged, System.Text.Encoding.UTF8.GetBytes("AC1032isolated-attachment"));
                DwgReader.Graph[assemblyPath].Add(new RefEdge { Name = "local-only", Saved = p.Source, Resolved = p.Source });
                return new AttachmentResult { Assembly = assemblyPath, Staged = staged, Added = 1 };
            };
            try
            {
                using (var timer = new Timer { Interval = 50 })
                {
                    timer.Tick += (s, e) => { var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Modal); if (dialog == null) return; timer.Stop(); confirmationOpened = dialog.Text == "引用到当前总装"; dialog.DialogResult = confirmationOpened ? DialogResult.OK : DialogResult.Cancel; dialog.Close(); };
                    timer.Start(); await AttachCurrentLocalReference();
                }
                WorkflowTestProgram.Check(confirmationOpened && placementsCorrect && calls == 1, "本地未引用DWG可确认后挂接当前总装并沿用默认坐标比例角度");
                WorkflowTestProgram.Check(Files.Hash(localOnly.Local) == sourceHash && Files.Hash(selected.Path) != assemblyBeforeAttach && Directory.GetFiles(Path.GetDirectoryName(selected.Path), Path.GetFileName(selected.Path) + ".pms-*.bak").Any(p => Files.Hash(p) == assemblyBeforeAttach), "挂接保留源文件且原子保存总装前保留原图备份");
                WorkflowTestProgram.Check(UpdateReports.Read(Session.LastUpdateLog).Any(r => r.Operation == "引用本地 DWG" && r.State == "已新增本地 Xref"), "本地挂接日志记录源文件总装定位与备份");
                ShowReferenceComparison(selected.Path);
                var attachedRow = referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Single(r => ((ReferenceComparisonRow)r.DataBoundItem).Name == "local-only.dwg");
                WorkflowTestProgram.Check(((ReferenceComparisonRow)attachedRow.DataBoundItem).Referenced, "挂接后刷新为已引用并进入列表上部");
                referenceBrowser.Grid.CurrentCell = attachedRow.Cells["Name"]; attachedRow.Selected = true; await AttachCurrentLocalReference();
                WorkflowTestProgram.Check(calls == 1, "已引用项不再次执行挂接");
            }
            finally { Host.NewStage = oldStage; }
            var safePlan = LocalReferenceAttachment.Plan(Session.LocalModelsRoot, selected.Path, localOnly.Local, new DwgReader());
            var beforeFailure = Files.Hash(selected.Path); var stagedOnFailure = false;
            WorkflowTestProgram.Reject(() => LocalReferenceAttachment.Run(safePlan, Path.Combine(evidence, "attach-locked.json"), p => { throw new IOException("测试占用"); }, (p, placements) => { stagedOnFailure = true; return new AttachmentResult(); }, System.Threading.CancellationToken.None), "目标总装占用时阻止挂接");
            WorkflowTestProgram.Check(!stagedOnFailure && Files.Hash(selected.Path) == beforeFailure, "占用失败不进入暂存也不修改原总装");
            File.AppendAllText(localOnly.Local, "changed-after-confirmation", System.Text.Encoding.UTF8);
            WorkflowTestProgram.Reject(() => LocalReferenceAttachment.Run(safePlan, Path.Combine(evidence, "attach-changed.json"), p => { }, (p, placements) => { stagedOnFailure = true; return new AttachmentResult(); }, System.Threading.CancellationToken.None), "确认后源文件变化时阻止挂接");
            ShowReferenceComparison(selected.Path); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "assembly-first.png"), ImageFormat.Png); }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyAssemblyFirst(string evidence)
        {
            var root = Path.Combine(evidence, "assembly-browser"); var local = Path.Combine(root, "local"); var cloud = Path.Combine(root, "Connector"); var scope = Path.Combine(local, "单系统总装文件");
            var a = Path.Combine(scope, "114101-SIF-M3-MLP-9424_GYPSUM DEWATERING SYSTEM.dwg"); var b = Path.Combine(scope, "114101-SIF-M3-M-9422_COMPRESSED AIR SYSTEM.dwg");
            var x = Path.Combine(local, "local_9424", "gds.dwg"); var y = Path.Combine(local, "local_9422", "air.dwg");
            foreach (var path in new[] { a, b, x, y, Path.Combine(local, "local_9424", "local-only.dwg"), Path.Combine(scope, "subfolder", "not-an-assembly.dwg") }) Dwg(path, "local");
            foreach (var path in new[] { "9424/gds.dwg", "9424/new.dwg", "9422/air.dwg", "9460/unrelated.dwg" }) Dwg(Path.Combine(cloud, path), "cloud");
            File.SetLastWriteTimeUtc(x, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(Path.Combine(cloud, "9424", "gds.dwg"), new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            DwgReader.Graph[a] = new System.Collections.Generic.List<RefEdge> { new RefEdge { Name = "gds", Saved = x, Resolved = x }, new RefEdge { Name = "missing", Saved = Path.Combine(local, "missing.dwg"), Resolved = Path.Combine(local, "missing.dwg") } };
            DwgReader.Graph[b] = new System.Collections.Generic.List<RefEdge> { new RefEdge { Name = "air", Saved = y, Resolved = y } };
            var config = new Settings { ConnectorRoot = cloud, LocalModelsRoot = local, ProjectScanFolder = scope, OutputFolder = Path.Combine(root, "NWC") };
            using (var form = new WorkflowForm(null, config, () => { }, () => new SyncService(config, () => { }, Path.Combine(root, "state")), assemblyFirst: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Show(); await Task.Delay(100); await form.VerifyAssemblyBrowser(evidence); form.Close();
            }
        }
    }
}
