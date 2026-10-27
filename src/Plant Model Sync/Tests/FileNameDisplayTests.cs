using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal void VerifyFileNameDisplay(string evidence)
        {
            onlyLinked.Checked = false;
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == Session.Assemblies.Count, "名称验证显示全部样例总装");
            WorkflowTestProgram.Check(modelsGrid.Rows.Cast<DataGridViewRow>().All(r => (string)r.Cells["DisplayName"].Value == Path.GetFileName(((AssemblyUpdate)r.DataBoundItem).Path)), "实际表格逐行显示真实路径的完整文件名");
            WorkflowTestProgram.Check(modelsGrid.Columns["DisplayName"].DefaultCellStyle.WrapMode == DataGridViewTriState.True && modelsGrid.AutoSizeRowsMode == DataGridViewAutoSizeRowsMode.DisplayedCells, "长总装文件名换行并自动调整可见行高");
            var selectedPaths = Session.Assemblies.Where(a => a.Selected).Select(a => a.Path).ToArray();
            modelTable.Search.Text = "-MLP-";
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == 2 && modelsGrid.Rows.Cast<DataGridViewRow>().All(r => ((AssemblyUpdate)r.DataBoundItem).DisplayName.Contains("-MLP-")), "M 与 MLP 文件名可准确区分并按前缀搜索");
            modelTable.Search.Clear();
            modelsGrid.Sort(modelsGrid.Columns["DisplayName"], System.ComponentModel.ListSortDirection.Ascending);
            WorkflowTestProgram.Check(selectedPaths.SequenceEqual(Session.Assemblies.Where(a => a.Selected).Select(a => a.Path)), "完整文件名筛选排序不改变导出勾选");
            var first = modelsGrid.Rows.Cast<DataGridViewRow>().First(r => ((AssemblyUpdate)r.DataBoundItem).DisplayName.Contains("-MLP-9424"));
            modelsGrid.CurrentCell = first.Cells["DisplayName"]; first.Selected = true; ShowModel();
            var assembly = (AssemblyUpdate)first.DataBoundItem;
            WorkflowTestProgram.Check(modelDetail.Text.Contains("文件名：" + Path.GetFileName(assembly.Path)) && modelDetail.Text.Contains("总装路径：" + assembly.Path), "下方详情同时显示完整文件名及完整路径");
            Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "full-file-names.png"), ImageFormat.Png); }
            Size = MinimumSize; PerformLayout(); Refresh();
            WorkflowTestProgram.Check(modelsGrid.Columns["DisplayName"].Width >= 260 && modelsGrid.Rows.Cast<DataGridViewRow>().All(r => (string)r.Cells["DisplayName"].Value == Path.GetFileName(((AssemblyUpdate)r.DataBoundItem).Path)), "最小窗口仍保留完整名称数据及名称列最小宽度");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "full-file-names-minimum.png"), ImageFormat.Png); }
        }
    }

    internal static partial class WorkflowTestProgram
    {
        private static async System.Threading.Tasks.Task VerifyFileNames(string evidence)
        {
            // 仅使用真实命名规则的隔离样例，不读取或修改用户总装和 Xref。
            var root = Path.Combine(evidence, "file-name-fixtures");
            var names = new[] { "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM.dwg", "114101-SIF-M3-MLP-9424_GYPSUM DEWATERING SYSTEM.dwg", "114101-FWTF-M3-M-9460_Piping Model.dwg", "114101-FWTF-M3-MLP-9460_Piping Model.dwg", "Drains Pipe.dwg", "中文总装.dwg" };
            var session = new WorkflowSession { ConnectorRoot = Path.Combine(root, "Connector"), LocalModelsRoot = root, ScopeFolder = Path.Combine(root, "单系统总装文件"), OutputFolder = Path.Combine(root, "NWC") };
            foreach (var name in names) session.Assemblies.Add(new AssemblyUpdate { Name = name, Path = Path.Combine(session.ScopeFolder, name), Scan = new ScanResult(), Selected = name.Contains("-MLP-") });
            Check(session.Assemblies.All(a => a.DisplayName == a.Name), "完整命名保留编号前缀、空格、中文及扩展名");
            Check(session.Assemblies.Select(a => a.DisplayName).Distinct().Count() == names.Length, "同系统 M 和 MLP 总装不再显示成同名");
            Check(new AssemblyUpdate { Name = "旧名称.dwg", Path = Path.Combine(root, names[0]) }.DisplayName == names[0], "路径为显示文件名的唯一依据而非旧缓存名称");
            Check(new AssemblyUpdate { Name = "fallback.dwg" }.DisplayName == "fallback.dwg" && new AssemblyUpdate().DisplayName == "", "缺少路径时安全回退名称");
            var relative = "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM/EL+16.40_2nd Storey.dwg";
            Check(new CloudUpdate { RelativePath = relative }.DisplayPath == relative && new CloudUpdate { RelativePath = relative.Replace('/', '\\') }.DisplayPath == relative.Replace('/', '\\'), "云端子目录保留完整实际名称及原路径分隔符");
            var config = new Settings { ConnectorRoot = session.ConnectorRoot, LocalModelsRoot = root, ProjectScanFolder = session.ScopeFolder, OutputFolder = session.OutputFolder };
            using (var form = new WorkflowForm(session, config, () => { }, () => new SyncService(config, () => { }, Path.Combine(root, "state"))))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show();
                form.VerifyFileNameDisplay(evidence); await form.VerifyCompactUi(evidence); form.Close();
            }
        }
    }
}
