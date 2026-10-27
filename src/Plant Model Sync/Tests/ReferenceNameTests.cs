using System;
using System.Collections.Generic;
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
        internal void VerifyReferenceNames(WorkflowSession session, string generated, string evidence)
        {
            Session = session; browserRows = session.Assemblies.Select(a => new AssemblyBrowserRow { Path = a.Path, State = "已检查" }).ToList(); assemblyBrowser.SetRows(browserRows); ShowReferenceComparison(session.Assemblies.Single().Path);
            WorkflowTestProgram.Check(referenceBrowser.Grid.Columns.Contains("ReferenceName"), "右侧表格具有总装引用名列，可与原生Xref面板直接核对");
            referenceBrowser.Search.Text = generated;
            var row = referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().Select(r => (ReferenceComparisonRow)r.DataBoundItem).Single();
            WorkflowTestProgram.Check(row.Name == "VACUUM_PIPING.dwg" && row.ReferenceNames.Contains(generated), "搜索原生PMS引用名可找到真实DWG文件名且不混淆名称与路径");
            referenceBrowser.Search.Clear(); Size = new Size(1780, 820); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "xref-reference-names.png"), ImageFormat.Png); }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyReferenceNames(string evidence)
        {
            var root = Path.Combine(evidence, "reference-names"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Cloud"); var scope = Path.Combine(local, "Assemblies");
            var assembly = Path.Combine(scope, "114101-SIF-M3-M-9423.dwg"); var source = Path.Combine(local, "local_9423", "VACUUM_PIPING.dwg"); var remote = Path.Combine(cloud, "9423", "VACUUM_PIPING.dwg");
            foreach (var p in new[] { assembly, source, remote, Path.Combine(local, "local_9423", "未引用本地文件.dwg") }) Dwg(p, p);
            var generated = "PMS_" + ReferenceNamePolicy.Digest(source);
            Check(ReferenceNamePolicy.Choose(source, "VACUUM_PIPING", new string[0]) == "VACUUM_PIPING", "新引用无名称冲突时直接保留文件名");
            var colliding = ReferenceNamePolicy.Choose(source, "VACUUM_PIPING", new[] { "vacuum_piping" });
            Check(colliding.StartsWith("VACUUM_PIPING__") && !colliding.StartsWith("PMS_") && colliding != "VACUUM_PIPING", "同名块冲突时使用可读文件名与稳定后缀，不覆盖已有块");
            Check(ReferenceNamePolicy.Choose(source, "VACUUM_PIPING", new[] { "VACUUM_PIPING", colliding }) == colliding + "_1", "文件名及路径后缀均被占用时继续唯一命名");
            Check(ReferenceNamePolicy.IsOwned(generated, source) && ReferenceNamePolicy.IsOwned(generated + "_2", source) && !ReferenceNamePolicy.IsOwned(generated, source + ".different.dwg") && !ReferenceNamePolicy.IsOwned("PMS_manual", source), "仅识别可通过实际文件路径校验的旧PMS名称，不误改自定义或其他路径名称");
            var longName = ReferenceNamePolicy.Choose(source, new string('管', 234) + char.ConvertFromUtf32(0x1F600) + "file", new string[0]);
            Check(longName.Length <= 235 && !char.IsHighSurrogate(longName.Last()), "长Unicode引用名称截断时保留合法字符");
            DwgReader.Graph[assembly] = new List<RefEdge> { new RefEdge { Name = generated, Saved = source, Resolved = source }, new RefEdge { Name = "第二个引用名", Saved = source, Resolved = source } };
            var scan = Workflow.ScanAssembly(assembly, new DwgReader(), true);
            Check(scan.Scan.DirectRows.Count == 1 && scan.Scan.DirectRows[0].ReferenceNames.SequenceEqual(new[] { generated, "第二个引用名" }), "同一DWG的多个原生引用名完整保留，不因按路径汇总丢失名称");
            var session = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true, Assemblies = new List<AssemblyUpdate> { scan }, Sources = new List<CloudUpdate> { new CloudUpdate { Path = remote, Name = "VACUUM_PIPING.dwg", ExpectedLocalPath = source } } };
            var rows = ReferenceComparisonBuilder.Build(session, scan);
            Check(rows.Single(r => r.Referenced).ReferenceName.Contains(generated) && rows.Single(r => !r.Referenced).ReferenceName == "—", "真实引用展示块名，未引用本地候选不伪造引用名");
            using (var form = new WorkflowForm(session, new Settings { LocalModelsRoot = local, ConnectorRoot = cloud, ProjectScanFolder = scope }, () => { }, () => new SyncService(new Settings(), () => { }, Path.Combine(root, "State")), assemblyFirst: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Show(); await Task.Delay(100); form.VerifyReferenceNames(session, generated, evidence); form.Close();
            }
        }
    }
}
