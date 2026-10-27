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
        internal async Task VerifyCompactUi(string evidence)
        {
            WorkflowTestProgram.Check(Text == "Auto sync" && Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<Label>()).Any(l => l.Visible && l.Text == "Auto sync"), "窗口与顶部标题统一为Auto sync");
            WorkflowTestProgram.Check(!Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<Label>()).Any(l => l.Visible && (l.Text.Contains("一键更新合并") || l.Text.Contains("云端更新 → 本地总装"))), "顶部不再显示旧流程标题和说明文字");
            var beforePaths = new[] { connector.Text, localRoot.Text, scope.Text, output.Text };
            var beforeChecks = Session.Assemblies.Where(a => a.Selected).Select(a => a.Path).ToArray();
            WorkflowTestProgram.Check(!pathFields.Visible && pathSummary.Visible, "已配置项目默认折叠四行目录并保留目录摘要");
            TogglePaths(); WorkflowTestProgram.Check(pathFields.Visible, "目录按钮可展开原始路径编辑与浏览控件");
            TogglePaths(); WorkflowTestProgram.Check(!pathFields.Visible && beforePaths.SequenceEqual(new[] { connector.Text, localRoot.Text, scope.Text, output.Text }), "折叠目录不修改同步和输出路径");
            WorkflowTestProgram.Check(beforeChecks.SequenceEqual(Session.Assemblies.Where(a => a.Selected).Select(a => a.Path)), "目录折叠不改变导出勾选");
            var toolbar = Controls.OfType<FlowLayoutPanel>().Single();
            var visibleButtons = toolbar.Controls.OfType<Button>().Where(b => b.Visible).ToList();
            WorkflowTestProgram.Check(visibleButtons.Count == 8 && new[] { "检查更新", "一键更新", "导出 NWC", "本地 DWG", "NWC 日期", "日志", "目录", "更多" }.All(name => visibleButtons.Any(b => b.Text == name)), "主工具栏仅保留8个短名称常用入口");
            var menu = visibleButtons.Single(b => b.Text == "更多").ContextMenuStrip;
            WorkflowTestProgram.Check(new[] { "从文件夹导出 NWC…", "强制刷新", "查看问题", "详细信息", "恢复已保存目录" }.All(name => menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == name)), "更多菜单保留强制刷新和查看问题的新名称");
            var issueDialogOpened = false;
            using (var dismiss = new Timer { Interval = 50 })
            {
                dismiss.Tick += (s, e) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f != this && f.Modal);
                    if (dialog == null) return;
                    issueDialogOpened = dialog is ScanIssuesForm; dismiss.Stop(); dialog.Close();
                };
                dismiss.Start(); menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "查看问题").PerformClick();
            }
            WorkflowTestProgram.Check(issueDialogOpened, "更多中的查看问题实际打开问题表而非文字弹窗");
            var detailed = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "详细信息"); detailed.Checked = true;
            WorkflowTestProgram.Check(sourcesGrid.Columns["DisplayPath"].Visible && sourcesGrid.Columns["MatchReason"].Visible && sourcesGrid.Columns["LocalSummary"].Visible && sourcesGrid.Columns["MissingSummary"].Visible && modelsGrid.Columns["XrefSummary"].Visible && sourceDetail.Height == 220, "详细信息开关仍可查看完整路径、匹配和Xref列");
            detailed.Checked = false;
            WorkflowTestProgram.Check(sourcesGrid.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible) == 5 && modelsGrid.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible) == 3 && sourceDetail.Height == 120, "默认双栏仅显示核心列并压缩详情区");
            WorkflowTestProgram.Check(!sourceTable.Filter.Visible && stateFilter.Visible && modelTable.Filter.Visible, "云端移除重复分类下拉但保留状态筛选和总装分类");
            WorkflowTestProgram.Check(beforeChecks.SequenceEqual(Session.Assemblies.Where(a => a.Selected).Select(a => a.Path)), "精简和详细视图切换不改变勾选");
            await Busy(ct =>
            {
                WorkflowTestProgram.Check(cancel.Visible && cancel.Enabled && visibleButtons.All(b => !b.Enabled) && !sourcesGrid.Enabled && !modelsGrid.Enabled, "任务期间仍禁用操作入口与表格并显示取消");
                return Task.CompletedTask;
            });
            WorkflowTestProgram.Check(!cancel.Visible && visibleButtons.All(b => b.Enabled), "任务结束隐藏取消并恢复操作入口");
            Size = MinimumSize; PerformLayout(); Refresh();
            WorkflowTestProgram.Check(visibleButtons.All(b => b.Bounds.Right <= toolbar.ClientSize.Width) && toolbar.VerticalScroll.Visible == false, "最小窗口下常用按钮全部可见且工具栏不产生纵向滚动条");
            WorkflowTestProgram.Check(sourcesGrid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Sum(c => c.Width) <= sourcesGrid.ClientSize.Width && modelsGrid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Sum(c => c.Width) <= modelsGrid.ClientSize.Width, "最小窗口默认核心列无需横向滚动即可查看");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "compact-sync-minimum.png"), ImageFormat.Png); }
            TogglePaths(); PerformLayout(); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "compact-sync-paths.png"), ImageFormat.Png); }
            TogglePaths();
        }
    }
}
