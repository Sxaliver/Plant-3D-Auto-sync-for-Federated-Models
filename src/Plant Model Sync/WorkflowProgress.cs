using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    // 自绘避免原生进度条动画在宿主同步读取 DWG 时滞后，已完成部分立即绘制。
    internal sealed class ScanProgressIndicator : Control
    {
        private readonly Timer animation = new Timer();
        private int maximum = 1, value, offset, speed;
        private ProgressBarStyle style;
        internal ScanProgressIndicator()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(223, 232, 244); ForeColor = Color.FromArgb(46, 101, 225);
            animation.Tick += (s, e) => { offset = (offset + 5) % Math.Max(1, Width + Width / 4); Invalidate(); };
        }
        public int Maximum { get => maximum; set { maximum = Math.Max(1, value); this.value = Math.Min(this.value, maximum); Invalidate(); } }
        public int Value { get => value; set { this.value = Math.Max(0, Math.Min(value, maximum)); Invalidate(); } }
        public ProgressBarStyle Style { get => style; set { if (style != value && value == ProgressBarStyle.Marquee) offset = Width / 4; style = value; Animate(); Invalidate(); } }
        public int MarqueeAnimationSpeed { get => speed; set { speed = Math.Max(0, value); Animate(); } }
        private void Animate()
        {
            animation.Stop();
            if (style == ProgressBarStyle.Marquee && speed > 0) { animation.Interval = Math.Max(15, speed); animation.Start(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            using (var brush = new SolidBrush(ForeColor))
            {
                if (style == ProgressBarStyle.Marquee) e.Graphics.FillRectangle(brush, offset - Width / 4, 0, Math.Max(16, Width / 4), Height);
                else e.Graphics.FillRectangle(brush, 0, 0, (int)((long)Width * value / maximum), Height);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
    internal sealed partial class WorkflowForm
    {
        private readonly Panel scanProgressPanel = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(16, 0, 16, 4), Visible = false };
        private readonly Label scanProgressCaption = new Label { Dock = DockStyle.Top, Height = 20, AutoEllipsis = true };
        private readonly ScanProgressIndicator scanProgress = new ScanProgressIndicator { Dock = DockStyle.Bottom, Height = 8, AccessibleName = "总装及 Xref 检查进度", AccessibleRole = AccessibleRole.ProgressBar };
        private int scanProgressRun;
        private bool scanProgressActive;

        private void InitializeScanProgress()
        {
            scanProgressPanel.BackColor = BackColor;
            scanProgressCaption.Font = Font;
            scanProgressCaption.ForeColor = Color.FromArgb(28, 64, 110);
            scanProgressPanel.Controls.Add(scanProgressCaption);
            scanProgressPanel.Controls.Add(scanProgress);
            Controls.Add(scanProgressPanel);
        }
        private int BeginScanProgress(string phase)
        {
            var run = ++scanProgressRun;
            scanProgressActive = true;
            scanProgressPanel.Visible = true;
            ReportScanProgress(run, phase);
            PaintScanProgress();
            return run;
        }
        private void ReportScanProgress(int run, string phase, int completed = 0, int total = 0, string file = null)
        {
            // 忽略已结束或上一轮后台任务排队中的回调，不能覆盖新任务或最终结果。
            if (IsDisposed || !scanProgressActive || run != scanProgressRun) return;
            if (total > 0)
            {
                scanProgress.Style = ProgressBarStyle.Blocks;
                scanProgress.Maximum = total;
                scanProgress.Value = Math.Max(0, Math.Min(completed, total));
                scanProgressCaption.Text = phase + " · " + scanProgress.Value + " / " + total;
            }
            else
            {
                // 枚举目录或单份 DWG 的内部读取没有可靠总量，不显示虚构百分比。
                scanProgress.Style = ProgressBarStyle.Marquee;
                scanProgress.MarqueeAnimationSpeed = 30;
                scanProgressCaption.Text = phase + "…";
            }
            if (!string.IsNullOrEmpty(file))
            {
                status.Text = "当前文件：" + Path.GetFileName(file);
                uiTips.SetToolTip(status, file);
            }
        }
        private void PaintScanProgress() { scanProgressPanel.Update(); scanProgressCaption.Update(); scanProgress.Update(); status.Update(); }
        private void ReportScanMessage(int run, string message)
        {
            if (IsDisposed || !scanProgressActive || run != scanProgressRun) return;
            status.Text = message;
            uiTips.SetToolTip(status, message);
        }
        private void EndScanProgress(int run)
        {
            if (run != scanProgressRun) return;
            scanProgressActive = false;
            scanProgress.MarqueeAnimationSpeed = 0;
            scanProgressPanel.Visible = false;
        }
    }
}
