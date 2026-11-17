using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        internal async Task VerifyAssemblySummaryColors(string evidence)
        {
            await LoadAssemblyDirectory();
            WorkflowTestProgram.Check(browserRows.All(r => r.ReferencePriority == 2), "未检查总装默认中性，不显示绿色");
            await InspectModel(browserRows.Single(r => r.Name.Contains("9424")));
            WorkflowTestProgram.Check(browserRows.Single(r => r.Name.Contains("9424")).ReferencePriority == 0 && browserRows.Where(r => !r.Name.Contains("9424")).All(r => r.ReferencePriority == 2), "单选总装检查后仅更新该行颜色，不为未检查总装染色");
            foreach (var row in browserRows) row.Selected = true;
            await InspectCheckedAssemblies();
            var green = browserRows.Single(r => r.Name.Contains("9424"));
            var yellow = browserRows.Single(r => r.Name.Contains("9422"));
            var empty = browserRows.Single(r => r.Name.Contains("9431"));
            WorkflowTestProgram.Check(green.ReferencePriority == 0 && yellow.ReferencePriority == 1 && empty.ReferencePriority == 2, "批量检查后全绿总装标绿、含黄总装标黄、无Xref总装中性");
            ShowReferenceComparison(yellow.Path);
            referenceBrowser.Search.Text = "current";
            WorkflowTestProgram.Check(referenceBrowser.Grid.Rows.Cast<DataGridViewRow>().All(r => ((ReferenceComparisonRow)r.DataBoundItem).DisplayPriority == 0) && yellow.ReferencePriority == 1, "右侧黄色被搜索隐藏时左侧仍按完整清单标黄");
            referenceBrowser.Search.Clear();
            assemblyBrowser.Grid.Sort(assemblyBrowser.Grid.Columns["Name"], ListSortDirection.Descending);
            foreach (var gridRow in assemblyBrowser.Grid.Rows.Cast<DataGridViewRow>())
            {
                var row = (AssemblyBrowserRow)gridRow.DataBoundItem;
                foreach (DataGridViewCell cell in gridRow.Cells)
                {
                    var e = new DataGridViewCellFormattingEventArgs(cell.ColumnIndex, gridRow.Index, cell.Value, cell.ValueType ?? typeof(string), new DataGridViewCellStyle());
                    typeof(DataGridView).GetMethod("OnCellFormatting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(DataGridViewCellFormattingEventArgs) }, null).Invoke(assemblyBrowser.Grid, new object[] { e });
                    WorkflowTestProgram.Check(row.ReferencePriority == 2 ? e.CellStyle.BackColor == Color.Empty : e.CellStyle.BackColor == (row.ReferencePriority == 0 ? Color.FromArgb(220, 244, 228) : Color.FromArgb(255, 237, 170)) && e.CellStyle.SelectionBackColor != Color.Empty, "总装整行及选中颜色与Xref汇总一致：" + row.Name + "/" + cell.OwningColumn.Name);
                }
            }
            WorkflowTestProgram.Check(browserRows.All(r => r.Selected), "颜色汇总和排序不改变总装勾选");
            Size = new Size(1650, 820); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "assembly-xref-colors.png"), ImageFormat.Png); }
            // 下载新源并引用后使用同一显示刷新路径，确认颜色不滞留在黄色。
            var remote = Session.Sources.Single(s => s.Name == "new.dwg"); var target = remote.ExpectedLocalPath;
            WorkflowTestProgram.WriteSummaryFixture(target);
            DwgReader.Graph[yellow.Path].Add(new RefEdge { Name = "new", Saved = target, Resolved = target });
            File.SetLastWriteTimeUtc(yellow.Path, DateTime.UtcNow);
            await InspectCheckedAssemblies();
            WorkflowTestProgram.Check(yellow.ReferencePriority == 0, "补齐本地并挂接后重新检查使总装从黄色变绿色");
            var scan = Session.Assemblies.Single(a => a.Path == green.Path).Scan; scan.Warnings.Add("测试读取异常");
            ShowReferenceComparison(green.Path);
            WorkflowTestProgram.Check(green.ReferencePriority == 2, "即使已有行全绿，扫描存在异常也不能将总装标绿");
            scan.Warnings.Clear(); ShowReferenceComparison(green.Path);
            WorkflowTestProgram.Check(green.ReferencePriority == 0, "再次显示完整有效结果会恢复绿色汇总");
            InvalidateAutomaticMappings();
            WorkflowTestProgram.Check(browserRows.All(r => r.ReferencePriority == 2 && r.Selected), "强制刷新清除过期总装颜色并保留勾选");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        internal static void WriteSummaryFixture(string path) => Dwg(path, "summary");
        private static async Task VerifyAssemblySummaryColors(string evidence)
        {
            ReferenceComparisonRow Green() => new ReferenceComparisonRow { CloudState = "存在", LocalState = "存在", PairConfirmed = true, Referenced = true };
            var yellow = new ReferenceComparisonRow { CloudState = "存在", LocalState = "缺失" };
            var unknown = new ReferenceComparisonRow { CloudState = "待确认", LocalState = "存在", Referenced = true };
            var summary = new AssemblyBrowserRow();
            summary.UpdateReferenceSummary(new[] { Green(), Green() }, true); Check(summary.ReferencePriority == 0, "仅有绿色Xref时总装为绿色");
            summary.UpdateReferenceSummary(new[] { Green(), yellow, unknown }, true); Check(summary.ReferencePriority == 1, "绿色黄色及待确认混合时黄色优先");
            summary.UpdateReferenceSummary(new[] { Green(), unknown }, true); Check(summary.ReferencePriority == 2, "绿色加待确认不等于只有绿色");
            summary.UpdateReferenceSummary(new ReferenceComparisonRow[0], true); Check(summary.ReferencePriority == 2, "空Xref清单不能利用全称判断误报绿色");
            summary.UpdateReferenceSummary(new[] { Green() }, false); Check(summary.ReferencePriority == 2, "扫描不完整时绿色行不能使总装误标绿色");
            var root = Path.Combine(evidence, "summary-colors"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector"); var scope = Path.Combine(local, "总装");
            var a = Path.Combine(scope, "114101-SIF-M3-M-9424.dwg"); var b = Path.Combine(scope, "114101-SIF-M3-M-9422.dwg"); var c = Path.Combine(scope, "114101-SIF-M3-M-9431.dwg");
            string L(string system) => Path.Combine(local, "local_" + system, "current.dwg");
            foreach (var p in new[] { a, b, c, L("9424"), L("9422"), Path.Combine(cloud, "9424", "current.dwg"), Path.Combine(cloud, "9422", "current.dwg"), Path.Combine(cloud, "9422", "new.dwg") }) WriteSummaryFixture(p);
            RefEdge Edge(string path) => new RefEdge { Name = "current", Saved = path, Resolved = path };
            DwgReader.Graph[a] = new List<RefEdge> { Edge(L("9424")) }; DwgReader.Graph[b] = new List<RefEdge> { Edge(L("9422")) }; DwgReader.Graph[c] = new List<RefEdge>();
            var config = new Settings { ConnectorRoot = cloud, LocalModelsRoot = local, ProjectScanFolder = scope };
            using (var form = new WorkflowForm(null, config, () => { }, () => new SyncService(config, () => { }, Path.Combine(root, "State")), assemblyFirst: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Show(); await Task.Delay(50); await form.VerifyAssemblySummaryColors(evidence); form.Close();
            }
        }
    }
}
