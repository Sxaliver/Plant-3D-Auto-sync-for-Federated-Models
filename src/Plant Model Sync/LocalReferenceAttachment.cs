using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class LocalAttachmentPlan
    {
        internal string Assembly;
        internal ReferencePlacement Placement;
        internal Dictionary<string, string> Hashes;
        internal List<string> Warnings = new List<string>();
        internal bool UnloadedWarningsConfirmed;
        internal LocalReferenceRepair Repair;
    }
    internal static class LocalReferenceAttachment
    {
        internal static LocalAttachmentPlan Plan(string root, string assembly, string source, IReferenceReader reader)
        {
            assembly = Paths.Canonical(assembly); source = Paths.Canonical(source);
            foreach (var path in new[] { assembly, source })
            {
                if (!Paths.IsWithin(root, path) || Paths.IsHistory(path)) throw new IOException("只能引用正式本地模型目录内的 DWG。");
                Files.CheckDwg(path);
            }
            if (string.Equals(assembly, source, StringComparison.OrdinalIgnoreCase)) throw new IOException("总装不能引用自身。");
            var before = Files.Hash(assembly);
            var sourceBefore = Files.Hash(source);
            var scan = Scanner.ScanForAttachment(source, Path.GetDirectoryName(source), reader);
            if (scan.Warnings.Count > 0) throw new IOException("本地源的嵌套引用未通过检查：" + string.Join(" | ", scan.Warnings));
            if (scan.Rows.Any(r => string.Equals(r.LocalPath, assembly, StringComparison.OrdinalIgnoreCase))) throw new IOException("新增引用将形成循环。");
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [assembly] = before, [source] = sourceBefore };
            foreach (var row in scan.Rows)
            {
                if (!Paths.IsWithin(root, row.LocalPath) || Paths.IsHistory(row.LocalPath)) throw new IOException("嵌套引用不在正式本地模型目录内：" + row.LocalPath);
                hashes[row.LocalPath] = Files.Hash(row.LocalPath);
            }
            var plan = new LocalAttachmentPlan { Assembly = assembly, Hashes = hashes, Placement = new ReferencePlacement { Source = source, X = 6653112.1, Y = 29134629, Z = 0, Scale = 1, Rotation = -4.42 } };
            if (scan.DeferredReferences.Count > 0)
            {
                plan.Warnings.AddRange(scan.DeferredReferences);
                // 完整诊断仅用于提示，不将卸载分支的问题冒充有效依赖或已修复。
                if (!(reader is LocalReferenceRepair)) plan.Warnings.AddRange(Scanner.Scan(source, Path.GetDirectoryName(source), reader).Warnings);
            }
            plan.Repair = reader as LocalReferenceRepair;
            plan.Repair?.Verify();
            Verify(plan); return plan;
        }
        private static void Verify(LocalAttachmentPlan plan)
        {
            if (plan.Hashes.Any(p => string.IsNullOrEmpty(p.Value) || Files.Hash(p.Key) != p.Value)) throw new IOException("总装或源文件在确认后发生变化，请重新检查。");
            var p = plan.Placement;
            if (new[] { p.X, p.Y, p.Z, p.Scale, p.Rotation }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || p.Scale <= 0) throw new IOException("插入位置、比例或角度无效。");
            if (!plan.Hashes.ContainsKey(p.Source)) throw new IOException("引用源已改变，请重新确认。");
        }
        internal static AttachmentResult Run(LocalAttachmentPlan plan, string log, Action<string> closed, Func<string, List<ReferencePlacement>, AttachmentResult> stage, CancellationToken ct)
        {
            var result = new AttachmentResult { Assembly = plan.Assembly, BeforeHash = plan.Hashes[plan.Assembly], Sources = new List<string> { plan.Placement.Source } };
            string temporary = null;
            try
            {
                ct.ThrowIfCancellationRequested(); Verify(plan);
                if (plan.Warnings.Count > 0 && !plan.UnloadedWarningsConfirmed) throw new IOException("请先确认已卸载引用警告；未修改总装。");
                closed(plan.Assembly); Files.AssertClosed(plan.Assembly);
                plan.Repair?.Apply(plan, closed, ct);
                Verify(plan);
                var made = stage(plan.Assembly, new List<ReferencePlacement> { plan.Placement }); temporary = made.Staged; result.Added = made.Added;
                ct.ThrowIfCancellationRequested(); Verify(plan); closed(plan.Assembly);
                if (temporary != null) { result.Backup = Files.InstallDownload(temporary, plan.Assembly, result.BeforeHash); temporary = null; }
                result.AfterHash = Files.Hash(plan.Assembly); result.State = made.Added > 0 ? "已新增本地 Xref" : "已存在，未重复引用";
                return result;
            }
            catch (Exception ex) { result.State = "引用失败：" + ex.Message; throw; }
            finally
            {
                if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
                var p = plan.Placement;
                var rows = plan.Repair?.Reports.ToList() ?? new List<UpdateReportRow>();
                rows.Add(new UpdateReportRow { Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), Operation = "引用本地 DWG", File = Path.GetFileName(p.Source), Local = p.Source, Assembly = plan.Assembly, State = result.State, Backup = result.Backup, BeforeHash = result.BeforeHash, AfterHash = result.AfterHash, Details = "WCS=" + p.X + "," + p.Y + "," + p.Z + "；比例=" + p.Scale + "；角度=" + p.Rotation + (plan.Warnings.Count == 0 ? "" : "\r\n卸载分支警告已确认=" + plan.UnloadedWarningsConfirmed + "；未修复，NWC 仍需完整检查\r\n" + string.Join("\r\n", plan.Warnings)) });
                WeeklyUpdate.AtomicJson(log, new { Format = "IWMF.OneClickUpdate.v1", State = result.State, Rows = rows });
            }
        }
    }
    internal sealed partial class WorkflowForm
    {
        private async Task AttachCurrentLocalReference()
        {
            if (browserBusy || work != null) return;
            var row = referenceBrowser.Grid.CurrentRow?.DataBoundItem as ReferenceComparisonRow;
            if (row == null || row.Referenced) { status.Text = "请选择右侧未引用的本地 DWG"; return; }
            if (!File.Exists(row.Local)) throw new IOException("本地文件缺失，请先下载。");
            var target = displayedAssemblyPath;
            if (Session == null || !Session.Assemblies.Any(a => string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase))) throw new IOException("请先检查目标总装。");
            if (ReferenceComparisonBuilder.HasMissingSameName(Session.Assemblies.Single(a => string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase)), row.Local)) throw new IOException("总装已有同名的失效引用，请先使用“更新所选”修复或确认已有路径，避免重复挂接。");
            var paths = Session.Assemblies.Select(a => a.Path).ToArray();
            browserBusy = true; assemblyBrowser.Enabled = referenceBrowser.Enabled = false;
            try
            {
                await Busy(async ct =>
                {
                    var repair = new LocalReferenceRepair(Session.LocalModelsRoot, Session.ConnectorRoot, Host.LocalRepairDwg());
                    var plan = LocalReferenceAttachment.Plan(Session.LocalModelsRoot, target, row.Local, repair);
                    if (!ConfirmLocalPlacement(plan)) return;
                    var log = Path.Combine(reportStorage, "Logs", "LocalReferences", "local-xref-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
                    Session.LastLog = Session.LastUpdateLog = log;
                    AttachmentResult result;
                    try { result = LocalReferenceAttachment.Run(plan, log, Host.CheckAssemblyClosedForAttach, (path, placements) => Host.StageConfirmedLocalReferences(path, placements, plan.UnloadedWarningsConfirmed), ct); }
                    finally
                    {
                        // 修复保留修改日期，不能依赖日期/长度缓存发现路径变化；失败也可能已有部分修复。
                        Session.Checked = false; Session.ReferenceCache?.Clear(); Session.HashCache.Clear();
                    }
                    if (!Session.AddedReferences.TryGetValue(target, out var added)) Session.AddedReferences[target] = added = new List<string>();
                    if (!added.Contains(row.Local, StringComparer.OrdinalIgnoreCase)) added.Add(row.Local);
                    Session.Checked = false; status.Text = result.State; await Task.Yield();
                });
            }
            finally { browserBusy = false; assemblyBrowser.Enabled = referenceBrowser.Enabled = true; }
            await InspectPaths(paths); ShowReferenceComparison(target);
        }
        private bool ConfirmLocalPlacement(LocalAttachmentPlan plan)
        {
            using (var dialog = new Form { Text = "引用到当前总装", Width = 850, Height = 365, MinimumSize = new Size(750, 365), StartPosition = FormStartPosition.CenterParent })
            {
                var names = new[] { "WCS X", "WCS Y", "WCS Z", "比例", "角度（°）" };
                var values = new[] { plan.Placement.X, plan.Placement.Y, plan.Placement.Z, plan.Placement.Scale, plan.Placement.Rotation };
                var numbers = new List<NumericUpDown>();
                var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(15, 0, 15, 0) };
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int i = 0; i < 5; i++) { var number = new NumericUpDown { DecimalPlaces = 6, Minimum = i == 3 ? 0.000001m : -1000000000000m, Maximum = 1000000000000m, Value = (decimal)values[i], Dock = DockStyle.Fill }; numbers.Add(number); fields.Controls.Add(new Label { Text = names[i], AutoSize = true }, 0, i); fields.Controls.Add(number, 1, i); }
                var title = new TextBox { Dock = DockStyle.Top, Height = 80, Multiline = true, ReadOnly = true, Text = "总装：" + plan.Assembly + "\r\n本地 DWG：" + plan.Placement.Source + "\r\nAttachment · 相对路径 · 修改前备份" };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                var cancel = new RoundedButton { Text = "取消", DialogResult = DialogResult.Cancel }; var confirm = new RoundedButton { Text = "确认引用", Primary = true, DialogResult = DialogResult.OK }; footer.Controls.Add(cancel); footer.Controls.Add(confirm);
                CheckBox acknowledge = null;
                dialog.Controls.Add(fields);
                if (plan.Repair != null && plan.Repair.Changes.Count > 0)
                {
                    dialog.Height += 120;
                    dialog.Controls.Add(new TextBox { Dock = DockStyle.Bottom, Height = 120, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = "确认后先备份并修复以下嵌套路径（保留日期、位置及加载状态）：\r\n" + string.Join("\r\n", plan.Repair.Changes.SelectMany(c => c.Value.Select(v => Path.GetFileName(c.Key) + " / " + v.Name + " → " + v.Target))) });
                }
                if (plan.Warnings.Count > 0)
                {
                    dialog.Height += 230;
                    var warnings = new TextBox { Dock = DockStyle.Bottom, Height = 190, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = string.Join("\r\n", plan.Warnings) };
                    acknowledge = new CheckBox { Dock = DockStyle.Bottom, Height = 35, Text = "保留卸载状态并继续引用；上述问题未修复，NWC 仍需完整检查" };
                    confirm.Enabled = false;
                    acknowledge.CheckedChanged += (s, e) => confirm.Enabled = acknowledge.Checked;
                    dialog.Controls.Add(warnings); dialog.Controls.Add(acknowledge);
                }
                dialog.Controls.Add(title); dialog.Controls.Add(footer); dialog.CancelButton = cancel; ModernUi.Apply(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                if (acknowledge != null && !acknowledge.Checked) return false;
                plan.UnloadedWarningsConfirmed = acknowledge?.Checked == true;
                plan.Placement.X = (double)numbers[0].Value; plan.Placement.Y = (double)numbers[1].Value; plan.Placement.Z = (double)numbers[2].Value; plan.Placement.Scale = (double)numbers[3].Value; plan.Placement.Rotation = (double)numbers[4].Value; return true;
            }
        }
    }
}
