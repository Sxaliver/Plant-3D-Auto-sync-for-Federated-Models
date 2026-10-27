using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal List<ReferenceComparisonRow> ScopeRows(WorkflowSession session, string path = null)
        {
            Session = session; LimitBrowserSources(); return BuildReferenceComparison(path == null ? session.Assemblies.Single() : session.Assemblies.Single(a => a.Path == path));
        }
        internal void RenderScope(WorkflowSession session, string evidence)
        {
            ScopeRows(session); browserRows = session.Assemblies.Select(a => new AssemblyBrowserRow { Path = a.Path, Selected = true, State = "已检查" }).ToList();
            assemblyBrowser.SetRows(browserRows); ShowReferenceComparison(session.Assemblies.Single().Path);
            Size = new Size(1650, 820); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "xref-direct-scope.png"), ImageFormat.Png); }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyReferenceScope(string evidence)
        {
            var root = Path.Combine(evidence, "reference-scope"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector");
            var scope = Path.Combine(local, "单系统总装文件"); var system = "114101-SIF-M3-M-9422_COMPRESSED AIR SYSTEM";
            var model = Path.Combine(scope, system + ".dwg");
            string Local(string name) => Path.Combine(local, "local_" + system, name + ".dwg");
            string Remote(string name) => Path.Combine(cloud, system, name + ".dwg");
            var air = Local("SERVICE AIR_PIPING"); var compressed = Local("Comepressed Air Piping"); var floor = Local("EL+20.00_3rd Storey");
            var other = Path.Combine(local, "local_114101-FWTF-M3-MLP-9460_Piping Model", "FWTF_PIPING.dwg");
            var otherCloud = Path.Combine(cloud, "114101-FWTF-M3-MLP-9460_Piping Model", "FWTF_PIPING.dwg");
            var history = Path.Combine(local, "local_history_9422", "old.dwg");
            foreach (var path in new[] { model, air, compressed, floor, other, otherCloud, history, Path.Combine(Path.GetDirectoryName(other), "other-system-only.dwg"), Remote("SERVICE AIR_PIPING"), Remote("Comepressed Air Piping"), Remote("EL+20.00_3rd Storey") }) Dwg(path, "scope fixture " + path);
            RefEdge Edge(string path, bool unloaded = false, bool overlay = false) => new RefEdge { Name = Path.GetFileNameWithoutExtension(path), Saved = path, Resolved = path, Unloaded = unloaded, Overlay = overlay };
            DwgReader.Graph[model] = new List<RefEdge> { Edge(floor), Edge(air), Edge(compressed) };
            DwgReader.Graph[air] = new List<RefEdge> { Edge(other, true), Edge(floor, true), Edge(compressed, true), Edge(Path.Combine(root, "Downloads", "unknown.dwg"), true) };
            DwgReader.Graph[compressed] = new List<RefEdge> { Edge(other, true), Edge(air, true) };
            DwgReader.Graph[other] = new List<RefEdge> { Edge(air), Edge(Path.Combine(root, "Missing", "floor.dwg")) };
            var reader = new DwgReader(); var config = new Settings { LocalModelsRoot = local, ConnectorRoot = cloud, ProjectScanFolder = scope, OutputFolder = Path.Combine(root, "NWC") };
            var session = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true };
            var assembly = Workflow.ScanAssembly(model, reader, true); session.Assemblies.Add(assembly);
            Check(assembly.Scan.Rows.Count == 3 && assembly.Scan.DirectRows.Count == 3 && assembly.Scan.Warnings.Count == 0, "两份新Xref带有卸载嵌套时，同步有效依赖及总装直接引用仍只有本系统三份DWG");
            Check(assembly.Scan.DeferredReferences.Count == 6 && !DwgReader.Reads.ContainsKey(other), "不进入卸载分支，避免无关目录与循环产生虚假诊断");
            var full = Workflow.ScanAssembly(model, reader);
            Check(full.Scan.Rows.Any(r => r.LocalPath == other) && full.Scan.Warnings.Count > 0, "完整扫描仍保留卸载分支和缺失诊断，未放宽NWC导出检查");
            var hashes = new[] { model, air, compressed, floor, other }.ToDictionary(p => p, Files.Hash);
            var sync = new SyncService(config, () => { }, Path.Combine(root, "State"));
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, sync);
            using (var form = new WorkflowForm(session, config, () => { }, () => sync, assemblyFirst: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Show(); await Task.Delay(100);
                var rows = form.ScopeRows(session);
                Check(rows.Count == 3 && rows.All(r => r.Referenced) && rows.Select(r => r.Local).ToHashSet().SetEquals(new[] { floor, air, compressed }), "右侧准确显示图三的三份本系统DWG，不将嵌套记录标成直接引用");
                Check(session.Sources.Count == 3 && session.Sources.All(s => !s.Path.Contains("9460")), "其他系统仅由卸载嵌套关联时不进入当前更新源集合");
                foreach (var source in session.Sources) source.Selected = true;
                var plan = await OneClickUpdate.Plan(session, config, sync, false, CancellationToken.None);
                Check(plan.Existing.Count == 3 && plan.NewFiles.Files.Count == 0 && plan.Existing.All(p => !p.Local.Contains("9460")), "更新所选只计划三份直接DWG，不准备拉取或挂接无关系统");
                form.RenderScope(session, evidence);
                var ownOnly = Local("本系统新文件"); Dwg(ownOnly, "local only");
                rows = form.ScopeRows(session);
                Check(rows.Count == 4 && rows.Single(r => r.Local == ownOnly).Usage == "未引用" && !rows.Any(r => r.Name == "other-system-only.dwg"), "未引用本地候选限定本系统，保留手动引用入口");
                DwgReader.Graph[model].Add(Edge(other));
                session.Assemblies[0] = Workflow.ScanAssembly(model, reader, true);
                rows = form.ScopeRows(session);
                Check(rows.Any(r => r.Local == other && r.Referenced) && !rows.Any(r => r.Name == "other-system-only.dwg"), "真实跨系统直接引用可见，但不会枚举该系统其他文件");
                DwgReader.Graph[model].RemoveAt(3);
                DwgReader.Graph[air] = new List<RefEdge> { Edge(other, false, true) };
                var overlay = Workflow.ScanAssembly(model, reader, true);
                Check(overlay.Scan.Rows.Count == 3 && overlay.Scan.DeferredReferences.Any(r => r.Contains("Overlay")), "源中的Overlay不随上级总装传播，也不扩大同步范围");
                DwgReader.Graph[air] = new List<RefEdge> { Edge(other) }; DwgReader.Graph[other] = new List<RefEdge>();
                var active = Workflow.ScanAssembly(model, reader, true);
                Check(active.Scan.Rows.Any(r => r.LocalPath == other) && active.Scan.DirectRows.Count == 3, "确实参与模型的加载Attachment依赖仍受同步追踪，与直接列表分开");
                DwgReader.Graph[model][0].Unloaded = true;
                var unloaded = Workflow.ScanAssembly(model, reader, true);
                Check(unloaded.Scan.DirectRows.Count == 3 && unloaded.Scan.DirectRows.Single(r => r.LocalPath == floor).Unloaded && !unloaded.Scan.Rows.Any(r => r.LocalPath == floor), "总装直接卸载引用保持记录和明确状态，不假装当前已加载");
                Check(hashes.All(p => Files.Hash(p.Key) == p.Value), "检查和显示修复不删除或修改任何总装及源DWG");
                form.Close();
            }
            var repairArea = Path.Combine(root, "repair"); var repairLocal = Path.Combine(repairArea, "Local"); var repairCloud = Path.Combine(repairArea, "Cloud");
            var repairAssembly = Path.Combine(repairLocal, "Assemblies", "114101-SIF-M3-M-9422.dwg"); var repairTarget = Path.Combine(repairLocal, "local_9422", "Floor.dwg"); var repairRemote = Path.Combine(repairCloud, "9422", "Floor.dwg");
            var repairNative = new WeeklyFakeReader();
            WeeklyFakeReader.Write(repairTarget, "floor"); WeeklyFakeReader.Write(repairRemote, "cloud");
            WeeklyFakeReader.Write(repairAssembly, "original assembly", new RefEdge { Name = "Floor", Saved = "Floor.dwg" });
            var repairPlan = AssemblyReferenceRepair.Plan(repairLocal, repairCloud, repairAssembly, new[] { repairRemote }, repairNative);
            var assemblyBefore = Files.Hash(repairAssembly);
            Check(repairPlan.Changes.Count == 1 && repairPlan.Changes[0].Target == repairTarget, "缺失的同目录楼层路径仅按总装系统唯一匹配正式本地镜像");
            var repaired = AssemblyReferenceRepair.Apply(repairPlan, repairNative, p => { }, CancellationToken.None);
            Check(repaired.BeforeHash == assemblyBefore && Files.Hash(repaired.Backup) == assemblyBefore && WeeklyFakeReader.Load(repairAssembly).Geometry == "original assembly", "修复已有总装路径前保留原图备份及几何内容");
            Check(repairNative.Read(repairAssembly).Single().Saved == @"..\local_9422\Floor.dwg" && Scanner.ScanForSynchronization(repairAssembly, repairLocal, repairNative).Warnings.Count == 0, "已有总装修复保存相对路径且实际复查通过");
            Check(AssemblyReferenceRepair.Plan(repairLocal, repairCloud, repairAssembly, new[] { repairRemote }, repairNative).Changes.Count == 0, "正确的已有引用不重复改写");
            WeeklyFakeReader.Write(repairAssembly, "race assembly", new RefEdge { Name = "Floor", Saved = "Floor.dwg" });
            repairPlan = AssemblyReferenceRepair.Plan(repairLocal, repairCloud, repairAssembly, new[] { repairRemote }, repairNative);
            WeeklyFakeReader.Write(repairTarget, "changed after preview");
            Reject(() => AssemblyReferenceRepair.Apply(repairPlan, repairNative, p => { }, CancellationToken.None), "总装路径修复预览后源内容变化时阻止覆盖");
            var secondRemote = Path.Combine(repairCloud, "9422", "duplicate", "Floor.dwg"); var secondLocal = Paths.MirrorTarget(repairLocal, repairCloud, secondRemote);
            WeeklyFakeReader.Write(secondLocal, "second floor"); WeeklyFakeReader.Write(secondRemote, "second cloud floor");
            Reject(() => AssemblyReferenceRepair.Plan(repairLocal, repairCloud, repairAssembly, new[] { repairRemote, secondRemote }, repairNative), "同系统有两个正式楼层候选时不自动修复");
        }
    }
}
