using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class ScanIssuesForm
    {
        internal void VerifyIssuesUi(string evidence)
        {
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 6 && table.Grid.Columns.Count == 3 && table.Grid.ReadOnly, "问题表只读展示类型、文件和问题三列");
            table.Search.Text = "EL+16.40";
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 4, "问题表可按云端文件名搜索");
            table.Search.Clear(); table.Filter.SelectedItem = "同步阻止";
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 1, "问题表可按类型筛选");
            var selected = table.Grid.Rows[0]; table.Grid.CurrentCell = selected.Cells["Message"]; selected.Selected = true;
            WorkflowTestProgram.Check(details.Text.Contains(@"C:\fixtures\local\9424\EL+16.40.dwg") && details.Text.Contains("本地与云端均有修改"), "选中问题后详情保留完整路径与原因");
            table.Search.Text = "no-match";
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 0 && empty.Text == "未找到匹配问题" && details.Text == "", "筛选无匹配不冒充未发现问题且清空旧详情");
            var reset = table.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Single(b => b.Text == "重置"); reset.PerformClick();
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 6 && table.Search.Text == "" && table.Filter.SelectedIndex == 0, "问题表重置恢复全部问题");
            table.Grid.Sort(table.Grid.Columns["Category"], ListSortDirection.Ascending);
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 6, "问题表支持列排序且不丢失记录");
            Size = MinimumSize; PerformLayout(); Refresh();
            WorkflowTestProgram.Check(table.Grid.Columns.Cast<DataGridViewColumn>().Sum(c => c.Width) <= table.Grid.ClientSize.Width, "问题表最小窗口核心列无需横向滚动");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "scan-issues-table.png"), ImageFormat.Png); }
        }
        internal void VerifyEmptyIssues(string evidence, bool inspected)
        {
            WorkflowTestProgram.Check(table.Grid.Rows.Count == 0 && empty.Visible && empty.Text == (inspected ? "未发现问题" : "尚无检查结果"), inspected ? "已检查无问题显示未发现问题且不伪造表格记录" : "尚未检查不显示未发现问题");
            WorkflowTestProgram.Check(summary.Text == (inspected ? "未发现问题" : "尚未检查，请先检查更新"), "无问题与未检查状态明确区分");
            Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, inspected ? "scan-issues-empty.png" : "scan-issues-unchecked.png"), ImageFormat.Png); }
        }
        internal void VerifyIncomplete() => WorkflowTestProgram.Check(summary.Text.StartsWith("检查尚未完成") && empty.Text != "未发现问题", "未完成检查不能报告为通过");
    }

    internal static partial class WorkflowTestProgram
    {
        private static void VerifyScanIssues(string evidence)
        {
            // 问题表只读取会话快照，不访问真实项目文件。
            var session = new WorkflowSession { Checked = true };
            var assembly = new AssemblyUpdate { Name = "9424总装", Path = @"C:\fixtures\单系统总装文件\114101-SIF-M3-MLP-9424_GYPSUM DEWATERING SYSTEM.dwg", Scan = new ScanResult(), Selected = true };
            assembly.Scan.Warnings.Add("不能展开缺失引用：missing.dwg"); session.Assemblies.Add(assembly);
            session.Warnings.Add(assembly.Name + "：" + assembly.Scan.Warnings[0]); session.Warnings.Add(assembly.Scan.Warnings[0]);
            session.Warnings.Add("目录不可读取：C:\\fixtures\\cloud\\受限目录");
            var source = new CloudUpdate { Name = "EL+16.40.dwg", Path = @"C:\fixtures\cloud\9424\EL+16.40.dwg", Selected = true };
            source.Diagnostics.Add("文件日期不可读取"); source.Diagnostics.Add("文件日期不可读取");
            source.MissingReferenceIssues.Add("Xref 指向旧位置，需核对路径");
            source.AmbiguousLocals.Add(@"C:\fixtures\local\9424\EL+16.40.dwg");
            source.Plans.Add(new TransferPlan { Local = source.AmbiguousLocals[0], Blocked = "本地与云端均有修改，禁止自动覆盖" }); session.Sources.Add(source);
            var rows = ScanIssues.Collect(session);
            Check(rows.Count == 6 && rows.Count(r => r.Category == "总装引用") == 1, "问题汇总去除同一总装诊断在会话汇总中的重复项");
            Check(rows.Count(r => r.Category == "源文件") == 1 && rows.Any(r => r.Category == "引用路径") && rows.Any(r => r.Category == "对应歧义") && rows.Any(r => r.Category == "同步阻止") && rows.Any(r => r.Category == "扫描"), "问题表汇总源文件、引用路径、歧义、阻止项与独立扫描警告");
            using (var form = new ScanIssuesForm(session) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) }) { form.Show(); form.VerifyIssuesUi(evidence); form.Close(); }
            Check(session.Assemblies[0].Selected && source.Selected && source.Diagnostics.Count == 2 && session.Warnings.Count == 3 && source.Plans[0].Blocked != null, "查看筛选排序问题不修改原同步勾选、诊断与阻止原因");
            using (var form = new ScanIssuesForm(new WorkflowSession { Checked = true }) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) }) { form.Show(); form.VerifyEmptyIssues(evidence, true); form.Close(); }
            using (var form = new ScanIssuesForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) }) { form.Show(); form.VerifyEmptyIssues(evidence, false); form.Close(); }
            using (var form = new ScanIssuesForm(new WorkflowSession()) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) }) { form.Show(); form.VerifyIncomplete(); form.Close(); }
        }
    }
}
