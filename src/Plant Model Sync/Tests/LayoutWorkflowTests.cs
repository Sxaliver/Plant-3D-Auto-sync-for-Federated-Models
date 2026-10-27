using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifyLinkedFileUi(string evidence)
        {
            WorkflowTestProgram.Check(onlyExisting.Checked && sourcesGrid.Rows.Cast<DataGridViewRow>().All(r => ((CloudUpdate)r.DataBoundItem).LocalExists) && sourcesGrid.Rows.Count < Session.Sources.Count, "默认隐藏本地对应文件不存在的云端项");
            WorkflowTestProgram.Check(!Session.Sources.Any(s => s.Selected) && modelsGrid.Rows.Count == 0, "默认不自动勾选 Pull，未选文件时不混入无关总装");
            var first = Session.Sources.Single(s => s.System == "9421" && s.Name == "Floor.dwg");
            var second = Session.Sources.Single(s => s.System == "9424" && s.Name == "Floor.dwg");
            DataGridViewRow Row(CloudUpdate source) => sourcesGrid.Rows.Cast<DataGridViewRow>().Single(r => r.DataBoundItem == source);
            sourcesGrid.CurrentCell = Row(first).Cells["Name"]; sourcesGrid.ClearSelection(); Row(first).Selected = true;
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == 1 && ((AssemblyUpdate)modelsGrid.Rows[0].DataBoundItem).Name.StartsWith("9421"), "点击一份源文件右侧只列出对应总装，不是只高亮");
            Row(second).Selected = true;
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == 2 && modelsGrid.Rows.Cast<DataGridViewRow>().All(r => !((AssemblyUpdate)r.DataBoundItem).Name.StartsWith("Other")), "Ctrl 多选等效行选择显示关联总装并集且排除无关总装");
            sourcesGrid.CurrentCell = Row(first).Cells["Selected"]; Row(first).Cells["Selected"].Value = true; sourcesGrid.EndEdit();
            WorkflowTestProgram.Check(first.Selected && modelsGrid.Rows.Count == 1, "勾选 Pull 使用已勾选文件集合筛选右侧总装");
            sourcesGrid.CurrentCell = Row(second).Cells["Selected"]; Row(second).Cells["Selected"].Value = true; sourcesGrid.EndEdit();
            WorkflowTestProgram.Check(first.Selected && second.Selected && modelsGrid.Rows.Count == 2, "多份 Pull 勾选累计关联总装且不重复");
            onlyExisting.Checked = false;
            WorkflowTestProgram.Check(sourcesGrid.Rows.Count == Session.Sources.Count && first.Selected && second.Selected, "关闭本地存在筛选恢复全部项且保留勾选");
            onlyExisting.Checked = true;
            onlyLinked.Checked = false;
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == 3, "可取消关联筛选查看总装目录内全部总装");
            onlyLinked.Checked = true;
            var missing = Session.Sources.Single(s => s.Name == "Missing.dwg");
            WorkflowTestProgram.Check(!missing.LocalExists && missing.Plans.Count == 1, "本地缺失但已被总装引用的文件保持恢复计划，默认仅隐藏");
            var orphan = Session.Sources.Single(s => s.Name == "Orphan.dwg");
            onlyExisting.Checked = false; sourcesGrid.CurrentCell = Row(orphan).Cells["Name"]; sourcesGrid.ClearSelection(); Row(orphan).Selected = true; FollowSourceRows();
            WorkflowTestProgram.Check(modelsGrid.Rows.Count == 0, "无关联总装的本地镜像文件被选中时右侧清空，不残留上次总装");
            systemFilter.SelectedItem = "9421"; stateFilter.SelectedIndex = 1;
            sourceTable.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Single(b => b.Text == "重置").PerformClick();
            WorkflowTestProgram.Check(systemFilter.SelectedIndex == 0 && stateFilter.SelectedIndex == 0 && onlyExisting.Checked && first.Selected && second.Selected, "重置恢复全部系统和默认本地存在筛选，不清除 Pull 勾选");
            sourcesGrid.CurrentCell = Row(first).Cells["Name"]; sourcesGrid.ClearSelection(); Row(first).Selected = true; Row(second).Selected = true; FollowSourceRows(); onlyExisting.Checked = true;
            await Task.Delay(50); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "local-layout-linked-ui.png"), ImageFormat.Png); }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyLayoutAndLinkedFilters(string evidence)
        {
            var root = Path.Combine(evidence, "layout-fixtures"); var modelRoot = Path.Combine(root, "Plant 3D 模型"); var cloud = Path.Combine(root, "Plant 3D Models");
            var c21 = "114101-SIF-M3-M-9421_OCF"; var c24 = "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM";
            var old21 = Path.Combine(modelRoot, "OCF"); var new21 = Path.Combine(modelRoot, "local_" + c21);
            var old24 = Path.Combine(modelRoot, "GYPSUM DEWATERING SYSTEM"); var new24 = Path.Combine(modelRoot, "local_" + c24);
            var floor21 = Path.Combine(new21, "Floor.dwg"); var floor24 = Path.Combine(new24, "Floor.dwg"); var missing = Path.Combine(new21, "Missing.dwg");
            var orphan = Path.Combine(new21, "Orphan.dwg"); var deep = Path.Combine(new21, "Sub", "Deep.dwg"); var history = Path.Combine(modelRoot, "local_history_9428_old", "Floor.dwg");
            foreach (var path in new[] { floor21, floor24, orphan, deep, history }) Dwg(path, "local-" + path);
            foreach (var relative in new[] { c21 + "/Floor.dwg", c24 + "/Floor.dwg", c21 + "/Orphan.dwg", c21 + "/Missing.dwg", c21 + "/Sub/Deep.dwg", c21 + "/CloudOnly.dwg" }) Dwg(Path.Combine(cloud, relative), "published-" + relative);
            var assemblyDir = Path.Combine(modelRoot, "单系统总装文件");
            var a = Path.Combine(assemblyDir, "9421_OCF.dwg"); var b = Path.Combine(assemblyDir, "9424_GYPSUM.dwg"); var unrelated = Path.Combine(assemblyDir, "Other.dwg");
            foreach (var path in new[] { a, b, unrelated }) Dwg(path, "assembly");
            List<RefEdge> Edges(params string[] paths) => paths.Select(p => new RefEdge { Name = Path.GetFileNameWithoutExtension(p), Saved = p, Resolved = p }).ToList();
            DwgReader.Graph[a] = Edges(Path.Combine(old21, "Floor.dwg"), Path.Combine(old21, "Missing.dwg"), Path.Combine(old21, "Sub", "Deep.dwg"), history);
            DwgReader.Graph[b] = Edges(Path.Combine(old24, "Floor.dwg")); DwgReader.Graph[unrelated] = new List<RefEdge>();
            var config = new Settings { LocalModelsRoot = modelRoot, ProjectScanFolder = assemblyDir, ConnectorRoot = cloud };
            config.LocalAliases[old21] = new21; config.LocalAliases[old24] = new24;
            var oldKey = cloud + "|" + Path.Combine(old21, "Floor.dwg").ToUpperInvariant(); config.Baselines[oldKey + "|" + Path.Combine(cloud, c21, "Floor.dwg")] = new Baseline { Hash = "previous", Version = "previous" };
            config.Mappings[oldKey] = Path.Combine(cloud, c21, "Floor.dwg");
            config.PreparePaths();
            Check(Paths.Canonical(Path.Combine(old21, "Sub", "Deep.dwg")) == deep && Paths.Canonical(old21 + "_other\\Floor.dwg") != floor21, "旧目录别名规范化包含嵌套路径但不误匹配相似前缀");
            var folder = FolderLink.Parse(cloud); var newKey = Settings.MapKey(folder, floor21);
            Check(config.Mappings.ContainsKey(newKey) && config.Baselines.ContainsKey(newKey + "|" + Path.Combine(cloud, c21, "Floor.dwg")) && config.Mappings.ContainsKey(oldKey), "改名后继承同步基准和人工对应，保留旧配置键用于回退");
            config.Baselines.Clear();
            var reader = new CachedReader(new DwgReader());
            var session = new WorkflowSession { LocalModelsRoot = modelRoot, ScopeFolder = assemblyDir, ConnectorRoot = cloud, Checked = true };
            session.Assemblies = new[] { a, b, unrelated }.Select(p => Workflow.ScanAssembly(p, reader)).ToList();
            Check(session.Assemblies[0].Scan.Rows.Any(r => r.LocalPath == deep) && session.Assemblies[0].Scan.Rows.Any(r => r.LocalPath == floor21), "总装保留旧 Xref 路径时仍追踪到改名后的实际工作文件");
            var service = new SyncService(config, () => { }, Path.Combine(root, "state"));
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(session.Sources.Where(s => s.Name == "Floor.dwg").All(s => s.Plans.Count == 1 && s.LocalExists), "两个 local_ 系统镜像内同名文件准确分开");
            Check(session.Sources.Single(s => s.Name == "Orphan.dwg").Plans.Single().Local == orphan && session.Sources.Single(s => s.Name == "Orphan.dwg").Plans[0].AffectedAssemblies.Count == 0, "本地镜像已有但未被总装引用的文件也可检查差异");
            Check(session.Sources.Single(s => s.Name == "Deep.dwg").Plans.Single().Local == deep, "Pull 目标保留云端系统内的嵌套相对路径");
            Check(session.Sources.SelectMany(s => s.Plans).All(p => p.Local != history), "历史目录不参与自动 Pull");
            using (var form = new WorkflowForm(session, config, () => { }, () => service))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show(); await form.VerifyLinkedFileUi(evidence); form.Close();
            }
            var source = session.Sources.Single(s => s.Name == "Deep.dwg"); var beforeAssembly = Files.Hash(a); var beforeHistory = Files.Hash(history);
            await service.Execute(folder, source.Plans, false, CancellationToken.None, null); Workflow.RecordPull(session, source.Plans);
            Check(source.Plans.Single().Written && Files.Hash(deep) == Files.Hash(source.Path) && session.Assemblies[0].UpdatedPaths.Contains(deep), "Pull 写入 local_ 对应嵌套位置，并追踪使用旧路径的总装");
            Check(Files.Hash(a) == beforeAssembly && Files.Hash(history) == beforeHistory, "Pull 不改写总装及被保留的历史文件");
            Dwg(missing, "test-explicitly-restored");
            foreach (var model in session.Assemblies) { model.Scan = Workflow.ScanAssembly(model.Path, new CachedReader(new DwgReader())).Scan; model.Selected = true; model.PendingXrefs = ""; model.UpdatedPaths = new List<string> { deep }; }
            Check(Workflow.ExportJobs(session, Path.Combine(root, "nwc"), session.Assemblies.Take(1)).Count == 1 && session.Assemblies.All(m => m.Selected), "NWC 导出只使用右侧可见勾选总装，隐藏勾选仍保留");
            var outside = Path.Combine(root, "outside.dwg"); Dwg(outside, "outside");
            var blocked = await service.Plan(folder, new[] { new XrefRow { Name = "outside.dwg", LocalPath = outside, CloudPath = source.Path } }, false, CancellationToken.None, null);
            Check(blocked.Single().Blocked != null, "本地模型根目录以外的 Pull 被安全阻止");
            Paths.ConfigureAliases(null);
        }
    }
}
