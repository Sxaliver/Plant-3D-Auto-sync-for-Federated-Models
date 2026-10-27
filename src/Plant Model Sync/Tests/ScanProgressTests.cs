using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        private void CaptureScanProgress(string file)
        {
            using (var bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        internal async Task VerifyScanProgressUi(string evidence)
        {
            await LoadAssemblyDirectory();
            var first = browserRows[0]; var second = browserRows[1]; first.Selected = true;
            bool singleVisible = false; var batchValues = new List<int>();
            var phases = new List<string>(); scanProgressCaption.TextChanged += (s, e) => phases.Add(scanProgressCaption.Text);
            try
            {
                DwgReader.BeforeRead = path =>
                {
                    if (path != first.Path) return;
                    singleVisible = scanProgressActive && scanProgressPanel.Visible && scanProgress.Maximum == 1 && scanProgress.Value == 0 && cancel.Enabled && !assemblyBrowser.Enabled;
                    CaptureScanProgress(Path.Combine(evidence, "scan-single-progress.png"));
                };
                await InspectModel(first);
                WorkflowTestProgram.Check(singleVisible && Session.Checked && !scanProgressPanel.Visible && work == null, "单总装读取前进度条已显示，完成后收起并恢复操作");
                var reads = DwgReader.Reads.Values.Sum();
                var cached = InspectModel(first);
                WorkflowTestProgram.Check(scanProgressPanel.Visible && scanProgress.Style == ProgressBarStyle.Marquee, "再次选择已缓存总装时也显示正在整理Xref的进度");
                await cached;
                WorkflowTestProgram.Check(DwgReader.Reads.Values.Sum() == reads && !scanProgressPanel.Visible, "缓存命中不重复扫描DWG，列表显示完成后收起进度");

                second.Selected = true;
                DwgReader.BeforeRead = path =>
                {
                    if (path != first.Path && path != second.Path) return;
                    if (scanProgressActive && scanProgressPanel.Visible && scanProgress.Maximum == 2) batchValues.Add(scanProgress.Value);
                    if (path == second.Path) CaptureScanProgress(Path.Combine(evidence, "scan-batch-progress.png"));
                };
                await InspectCheckedAssemblies(true);
                WorkflowTestProgram.Check(batchValues.SequenceEqual(new[] { 0, 1 }) && phases.Any(p => p.Contains("2 / 2")), "批量检查按真实总装数量递增，显示0/2、1/2到2/2");
                WorkflowTestProgram.Check(phases.Any(p => p.StartsWith("扫描 Connector")) && phases.Any(p => p.StartsWith("对比 Xref")), "扫描匹配阶段使用滚动进度，Xref对比阶段显示文件完成数量");
                WorkflowTestProgram.Check(browserRows.All(r => r.Selected) && !scanProgressActive && assemblyBrowser.Enabled && referenceBrowser.Enabled, "进度反馈不改变勾选，结束后恢复两栏");

                DwgReader.BeforeRead = path => { if (path == first.Path) work.Cancel(); };
                await InspectCheckedAssemblies(true); await Task.Yield();
                WorkflowTestProgram.Check(!scanProgressActive && !scanProgressPanel.Visible && work == null && !Session.Checked && status.Text.Contains("取消"), "取消检查不会显示假完成，迟到回调不使进度重新出现");
                WorkflowTestProgram.Check(browserRows.All(r => r.Selected) && assemblyBrowser.Enabled && referenceBrowser.Enabled && !cancel.Enabled, "取消后保留原勾选并恢复检查入口");

                DwgReader.BeforeRead = path => { if (path == first.Path) throw new IOException("隔离读取失败"); };
                await InspectCheckedAssemblies(true);
                WorkflowTestProgram.Check(!scanProgressPanel.Visible && browserRows.Single(r => r.Path == first.Path).State == "需处理" && Session.Assemblies.Single(a => a.Path == first.Path).Scan.Warnings.Count > 0, "单份读取失败保留问题状态，不残留进度条或伪报成功");
            }
            finally { DwgReader.BeforeRead = null; }
            var old = BeginScanProgress("旧检查"); EndScanProgress(old);
            var current = BeginScanProgress("新检查");
            ReportScanProgress(old, "旧回调", 10, 10, "不应显示");
            WorkflowTestProgram.Check(scanProgressCaption.Text == "新检查…" && scanProgress.Style == ProgressBarStyle.Marquee, "旧轮次回调不能覆盖新检查的进度");
            ReportScanProgress(current, "读取总装", 1, 2);
            using (var image = new Bitmap(scanProgress.Width, scanProgress.Height))
            {
                scanProgress.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                WorkflowTestProgram.Check(image.GetPixel(image.Width / 4, image.Height / 2).ToArgb() == scanProgress.ForeColor.ToArgb() && image.GetPixel(image.Width * 3 / 4, image.Height / 2).ToArgb() == scanProgress.BackColor.ToArgb(), "批量已完成部分立即绘制蓝色，不依赖原生动画延迟");
            }
            EndScanProgress(current); ReportScanProgress(current, "迟到回调", 1, 1);
            WorkflowTestProgram.Check(!scanProgressActive && !scanProgressPanel.Visible, "已结束任务的后台进度不会重新显示");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyScanProgress(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "scan-progress"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector");
            var scope = Path.Combine(local, "Assemblies"); var state = Path.Combine(root, "State");
            foreach (var code in new[] { "9422", "9424" })
            {
                var assembly = Path.Combine(scope, "114101-SIF-M3-M-" + code + "_System.dwg");
                var source = Path.Combine(cloud, code, "Source" + code + ".dwg"); var target = Paths.MirrorTarget(local, cloud, source);
                Dwg(assembly, "assembly-" + code); Dwg(source, "source-" + code); Dwg(target, "source-" + code);
                DwgReader.Graph[assembly] = new List<RefEdge> { new RefEdge { Name = code, Saved = target, Resolved = target } };
            }
            var config = new Settings { LocalModelsRoot = local, ConnectorRoot = cloud, ProjectScanFolder = scope, OutputFolder = Path.Combine(root, "NWC") };
            using (var form = new WorkflowForm(null, config, () => { }, () => new SyncService(config, () => { }, state), assemblyFirst: true))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show(); await Task.Delay(100);
                await form.VerifyScanProgressUi(evidence); form.Close();
            }
            var items = new List<Tuple<int, int>>();
            var files = Directory.GetFiles(cloud, "*.dwg", SearchOption.AllDirectories).Select(p => new XrefRow { Name = Path.GetFileName(p), LocalPath = Paths.MirrorTarget(local, cloud, p), CloudPath = p }).ToList();
            var service = new SyncService(config, () => { }, state);
            await service.Plan(FolderLink.Parse(cloud), files, false, CancellationToken.None, null, itemProgress: (done, total, file) => items.Add(Tuple.Create(done, total)));
            Check(items.Select(p => p.Item1).SequenceEqual(new[] { 0, 1, 1, 2 }) && items.All(p => p.Item2 == 2), "Xref对比回调使用真实完成计数，不按耗时估计百分比");
        }
    }
}
