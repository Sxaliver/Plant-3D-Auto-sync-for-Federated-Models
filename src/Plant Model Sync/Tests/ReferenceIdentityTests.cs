using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IWMF.PlantModelSync
{
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyReferenceIdentity(string evidence)
        {
            var root = Path.Combine(evidence, "reference-identity"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector"); var scope = Path.Combine(local, "Assemblies");
            string Model(string code) => Path.Combine(scope, "114101-SIF-M3-M-" + code + ".dwg");
            string Remote(string code, string name) => Path.Combine(cloud, code, name + ".dwg");
            string Mirror(string code, string name) => Paths.MirrorTarget(local, cloud, Remote(code, name));
            var a = Model("9422"); var b = Model("9424"); var floor = Mirror("9422", "EL+20.00_3rd Storey"); var otherFloor = Mirror("9424", "EL+20.00_3rd Storey");
            var air = Mirror("9422", "SERVICE AIR_PIPING"); var compressed = Mirror("9422", "Comepressed Air Piping"); var bad = Path.Combine(scope, "EL+20.00_3rd Storey.dwg");
            foreach (var p in new[] { a, b, floor, otherFloor, air, compressed, Remote("9422", "EL+20.00_3rd Storey"), Remote("9424", "EL+20.00_3rd Storey"), Remote("9422", "SERVICE AIR_PIPING"), Remote("9422", "Comepressed Air Piping") }) Dwg(p, p);
            RefEdge Edge(string path) => new RefEdge { Name = Path.GetFileNameWithoutExtension(path), Saved = path, Resolved = path };
            DwgReader.Graph[a] = new List<RefEdge> { Edge(bad), Edge(air), Edge(compressed) }; DwgReader.Graph[b] = new List<RefEdge> { Edge(otherFloor) };
            var config = new Settings { LocalModelsRoot = local, ConnectorRoot = cloud, ProjectScanFolder = scope, OutputFolder = Path.Combine(root, "NWC") };
            var sync = new SyncService(config, () => { }, Path.Combine(root, "State")); var reader = new DwgReader();
            async Task<WorkflowSession> Inspect()
            {
                var s = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true };
                s.Assemblies.Add(Workflow.ScanAssembly(a, reader, true)); s.Assemblies.Add(Workflow.ScanAssembly(b, reader, true)); s.Sources = await Workflow.Check(s, config, CancellationToken.None, null, sync); return s;
            }
            var session = await Inspect();
            var before = new[] { a, b, floor, otherFloor, air, compressed }.ToDictionary(p => p, Files.Hash);
            using (var form = new WorkflowForm(session, config, () => { }, () => sync, assemblyFirst: true) { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Show(); await Task.Delay(100);
                var rows = form.ScopeRows(session, a); var row = rows.Single(r => r.Name == Path.GetFileName(floor));
                Check(rows.Count == 3 && row.Local == floor && row.Cloud == Remote("9422", "EL+20.00_3rd Storey"), "多总装检查将缺失旧引用与唯一正式文件归为一行，不再显示第四个楼层文件");
                Check(row.Referenced && row.RequiresPathRepair && row.State == "引用路径待修复" && row.ReferencePaths.Single() == bad, "合并行保留旧路径并明确待修复，不冒充已正确引用");
                Check(row.MissingLocalAvailableCloud && row.LocalState.Contains("旧引用失效") && row.DateDifference != "无法比较日期", "合并行标黄并使用实际本地文件日期，不再伪报本地文件缺失");
                Check(ReferenceComparisonBuilder.HasMissingSameName(session.Assemblies[0], floor), "正式文件已存在失效同名引用时阻止把它重复挂接");
                var downloads = await form.PlanReferenceDownload(row, row.Local, CancellationToken.None);
                Check(downloads.Count == 1 && downloads[0].Local == floor && downloads[0].ConnectorPath == row.Cloud, "合并行下载仅更新正式目录目标，不在旧失效路径创建副本");
                var repair = AssemblyReferenceRepair.FindTargets(local, cloud, a, bad, session.Sources.Select(s => s.Path));
                Check(repair.Single() == row.Local, "列表归并和实际路径修复选择完全相同的目标");
                Check(form.ScopeRows(session, b).Single(r => r.Name == Path.GetFileName(otherFloor)).Local == otherFloor, "其他系统同名楼层保持独立，不合并到9422");
                DwgReader.Graph[b] = new List<RefEdge> { Edge(bad) }; session = await Inspect();
                Check(form.ScopeRows(session, a).Single(r => r.Name == Path.GetFileName(floor)).Local == floor && form.ScopeRows(session, b).Single(r => r.Name == Path.GetFileName(otherFloor)).Local == otherFloor, "多份总装保存相同错误短路径时仍按各自系统解析，不串用文件");
                var secondBad = Path.Combine(scope, "old", Path.GetFileName(floor)); DwgReader.Graph[a].Add(Edge(secondBad)); session = await Inspect();
                row = form.ScopeRows(session, a).Single(r => r.Name == Path.GetFileName(floor));
                Check(row.ReferencePaths.Count == 2 && row.RequiresPathRepair, "同一目标的多个失效引用合并显示并保留每个原路径供实际修复");
                var one = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true, Assemblies = new List<AssemblyUpdate> { session.Assemblies[0] }, Sources = session.Sources.Where(s => s.Path.Contains("9422")).ToList() };
                form.RenderScope(one, evidence);
                DwgReader.Graph[a] = new List<RefEdge> { Edge(floor), Edge(air), Edge(compressed) }; session = await Inspect();
                row = form.ScopeRows(session, a).Single(r => r.Name == Path.GetFileName(floor));
                Check(!row.RequiresPathRepair && row.ReferencePaths.Single() == floor && !ReferenceComparisonBuilder.HasMissingSameName(session.Assemblies[0], floor), "引用修复后恢复普通状态并清除重复挂接保护提示");
                var copyRemote = Remote("9422", "copies/EL+20.00_3rd Storey"); var copyLocal = Paths.MirrorTarget(local, cloud, copyRemote); Dwg(copyLocal, "real different copy"); Dwg(copyRemote, "different cloud copy");
                DwgReader.Graph[a].Add(Edge(copyLocal)); session = await Inspect(); rows = form.ScopeRows(session, a);
                Check(rows.Count(r => r.Name == Path.GetFileName(floor)) == 2 && rows.Any(r => r.Local == floor && r.Referenced) && rows.Any(r => r.Local == copyLocal && r.Referenced), "真实存在的同名不同路径文件保持两行，不靠文件名误去重");
                DwgReader.Graph[a] = new List<RefEdge> { Edge(bad), Edge(air), Edge(compressed) }; session = await Inspect(); rows = form.ScopeRows(session, a);
                Check(rows.Any(r => r.Local == bad && !r.RequiresPathRepair) && rows.Any(r => r.Local == floor) && rows.Any(r => r.Local == copyLocal), "同系统多个候选时不把缺失引用错误合并到其中一份");
                Reject(() => AssemblyReferenceRepair.FindTargets(local, cloud, a, bad, session.Sources.Select(s => s.Path)), "同系统多个同名云端候选时实际修复同样阻止");
                Check(rows.Where(r => !r.Referenced && File.Exists(r.Local) && r.Name == Path.GetFileName(floor)).All(r => r.Cloud != "未匹配"), "本地实际副本保留可确认的云端对应，不伪报云端未匹配");
                Check(rows.Select(r => r.Local.ToUpperInvariant() + "\n" + r.Cloud.ToUpperInvariant()).Distinct().Count() == rows.Count, "重叠目录枚举与多个来源合并后每个本地/云端对只显示一次");
                Check(before.All(p => Files.Hash(p.Key) == p.Value) && !File.Exists(bad), "归并显示和下载预览不修改实际DWG或创建失效路径副本");
                form.Close();
            }
        }
    }
}
