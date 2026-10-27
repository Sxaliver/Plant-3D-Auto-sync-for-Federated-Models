using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal void VerifyAttachmentWarningDialog(LocalAttachmentPlan plan, string evidence, bool accept)
        {
            using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
            {
                timer.Tick += (s, e) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Modal && f.Text == "引用到当前总装");
                    if (dialog == null) return;
                    timer.Stop();
                    var acknowledge = dialog.Controls.OfType<CheckBox>().Single();
                    var button = dialog.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Single(b => b.Text == "确认引用");
                    WorkflowTestProgram.Check(!acknowledge.Checked && !button.Enabled, "卸载分支警告须主动确认，默认不能继续");
                    using (var image = new Bitmap(dialog.Width, dialog.Height))
                    {
                        dialog.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        image.Save(Path.Combine(evidence, "attachment-unloaded-warning.png"), ImageFormat.Png);
                    }
                    acknowledge.Checked = accept;
                    WorkflowTestProgram.Check(button.Enabled == accept, "确认按钮状态与警告勾选一致");
                    dialog.DialogResult = accept ? DialogResult.OK : DialogResult.Cancel; dialog.Close();
                };
                timer.Start();
                WorkflowTestProgram.Check(ConfirmLocalPlacement(plan) == accept && plan.UnloadedWarningsConfirmed == accept, "取消不确认警告，接受则保存单次确认状态");
            }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static void VerifyAttachmentPolicy(string evidence)
        {
            var root = Path.Combine(evidence, "attachment-policy");
            string Make(string name) { var path = Path.Combine(root, name + ".dwg"); Dwg(path, name); DwgReader.Graph[path] = new List<RefEdge>(); return path; }
            var assembly = Make("target"); var source = Make("source"); var child = Make("child"); var shared = Make("shared");
            var missing = Path.Combine(root, "missing.dwg");
            RefEdge Edge(string path, bool unloaded = false) => new RefEdge { Name = Path.GetFileNameWithoutExtension(path), Saved = path, Resolved = path, Unloaded = unloaded };
            LocalAttachmentPlan Plan() => LocalReferenceAttachment.Plan(root, assembly, source, new DwgReader());
            DwgReader.Graph[source] = new List<RefEdge> { Edge(missing, true) };
            var plan = Plan();
            Check(plan.Warnings.Any(w => w.Contains("missing")) && plan.Hashes.Count == 2, "卸载缺失引用列为警告且不要求缺失文件哈希");
            Check(Scanner.Scan(source, root, new DwgReader()).Warnings.Count > 0, "完整扫描仍报告卸载分支缺失，不改变导出规则");
            bool staged = false;
            Reject(() => LocalReferenceAttachment.Run(plan, Path.Combine(root, "unconfirmed.json"), p => { }, (p, list) => { staged = true; return new AttachmentResult(); }, CancellationToken.None), "未确认警告禁止进入原生暂存");
            Check(!staged, "未确认时总装保持未操作");
            using (var form = new WorkflowForm())
            {
                form.VerifyAttachmentWarningDialog(plan, evidence, false);
                form.VerifyAttachmentWarningDialog(plan, evidence, true);
            }
            var sourceHash = Files.Hash(source); var targetHash = Files.Hash(assembly);
            LocalReferenceAttachment.Run(plan, Path.Combine(root, "confirmed.json"), p => { }, (p, list) => { staged = true; return new AttachmentResult(); }, CancellationToken.None);
            Check(staged && Files.Hash(source) == sourceHash && Files.Hash(assembly) == targetHash, "确认卸载警告后允许暂存回调且不改源文件");
            Check(UpdateReports.Read(Path.Combine(root, "confirmed.json")).Any(r => r.Details.Contains("未修复") && r.Details.Contains("missing")), "日志记录明确确认及未修复的问题");
            DwgReader.Graph[source] = new List<RefEdge> { Edge(missing) };
            Reject(() => Plan(), "同一缺失引用处于已加载状态时必须阻止");
            DwgReader.Graph[source] = new List<RefEdge> { Edge(child) }; DwgReader.Graph[child] = new List<RefEdge> { Edge(missing, true) };
            Check(Plan().Warnings.Count > 0 && Plan().Hashes.ContainsKey(child), "多层嵌套中的卸载分支通用处理，已加载父节点仍受哈希保护");
            DwgReader.Graph[child] = new List<RefEdge> { Edge(source) };
            Reject(() => Plan(), "已加载间接循环仍阻止");
            DwgReader.Graph[child] = new List<RefEdge> { Edge(source, true) };
            Check(Plan().Warnings.Any(w => w.Contains("循环引用")), "卸载边打断的循环仅作为未修复警告");
            DwgReader.Graph[child] = new List<RefEdge> { Edge(assembly) };
            Reject(() => Plan(), "已加载依赖回到目标总装时阻止新循环");
            DwgReader.Graph[source] = new List<RefEdge> { Edge(shared, true), Edge(child) };
            DwgReader.Graph[child] = new List<RefEdge> { Edge(shared) }; DwgReader.Graph[shared] = new List<RefEdge> { Edge(missing) };
            Reject(() => Plan(), "同一文件先从卸载边后从加载边到达时不能掩盖有效分支缺失");
            DwgReader.Graph[source] = new List<RefEdge> { Edge(source) };
            Reject(() => Plan(), "源图已加载自引用仍阻止");
            Reject(() => LocalReferenceAttachment.Plan(root, source, source, new DwgReader()), "总装引用自身始终阻止");
        }
    }
}
