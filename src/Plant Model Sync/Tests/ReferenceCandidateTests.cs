using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyReferenceCandidates(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "reference-candidates"); var local = Path.Combine(root, "Local"); var cloud = Path.Combine(root, "Connector"); var scope = Path.Combine(local, "Assemblies");
            var a = Path.Combine(scope, "114101-SIF-M3-M-9424.dwg"); var b = Path.Combine(scope, "114101-SIF-M3-M-9426.dwg");
            var name = "EL+5.30_1st Storey.dwg";
            var ca = Path.Combine(cloud, "9424", name); var cb = Path.Combine(cloud, "9426", name);
            var la = Paths.MirrorTarget(local, cloud, ca); var lb = Paths.MirrorTarget(local, cloud, cb);
            foreach (var p in new[] { a, b, ca, cb, la, lb }) Dwg(p, p);
            RefEdge Edge(string file, string alias = null, bool unloaded = false) => new RefEdge { Name = alias ?? Path.GetFileNameWithoutExtension(file), Saved = file, Resolved = file, Unloaded = unloaded };
            DwgReader.Graph[a] = new List<RefEdge> { Edge(la) }; DwgReader.Graph[b] = new List<RefEdge> { Edge(lb) };
            var config = new Settings { LocalModelsRoot = local, ConnectorRoot = cloud, ProjectScanFolder = scope };
            var sync = new SyncService(config, () => { }, Path.Combine(root, "State"));
            async Task<WorkflowSession> Inspect()
            {
                var s = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true };
                s.Assemblies.Add(Workflow.ScanAssembly(a, new DwgReader(), true)); s.Assemblies.Add(Workflow.ScanAssembly(b, new DwgReader(), true));
                s.Sources = await Workflow.Check(s, config, CancellationToken.None, null, sync); return s;
            }
            var session = await Inspect(); var sa = session.Sources.Single(s => s.Path == ca); var sb = session.Sources.Single(s => s.Path == cb);
            sb.AmbiguousLocals.Add(la);
            var rows = ReferenceComparisonBuilder.Build(session, session.Assemblies[0]);
            Check(rows.Count(r => r.Local == la) == 1 && rows.Single(r => r.Local == la).Cloud == ca && rows.Single(r => r.Local == la).CandidateCloudPaths.Count == 0, "明确本地云端对不被其他系统的弱候选展开成重复行");

            DwgReader.Graph[a].Add(Edge(lb)); session = await Inspect();
            session.Sources.Single(s => s.Path == ca).AmbiguousLocals.Add(lb); session.Sources.Single(s => s.Path == cb).AmbiguousLocals.Add(la);
            rows = ReferenceComparisonBuilder.Build(session, session.Assemblies[0]);
            Check(rows.Count(r => r.Referenced && r.Name == name) == 2 && rows.Single(r => r.Local == la).Cloud == ca && rows.Single(r => r.Local == lb).Cloud == cb, "真实跨系统同名直接引用按完整路径保留两份，不按裸文件名去重");
            DwgReader.Graph[a] = new List<RefEdge> { Edge(la, "Loaded"), Edge(la, "Unloaded", true) }; session = await Inspect();
            rows = ReferenceComparisonBuilder.Build(session, session.Assemblies[0]);
            Check(rows.Count(r => r.Local == la) == 1 && rows.Single(r => r.Local == la).ReferenceNames.Count == 2 && !rows.Single(r => r.Local == la).Unloaded, "同源多个原生引用名合并一行，保留名称及实际加载状态");

            // 明确人工对应优先于默认镜像，但不修改该映射或总装。
            config.Mappings[Settings.MapKey(FolderLink.Parse(cloud), la)] = cb; session = await Inspect();
            rows = ReferenceComparisonBuilder.Build(session, session.Assemblies[0]);
            Check(rows.Count(r => r.Local == la) == 1 && rows.Single(r => r.Local == la).Cloud == cb, "人工跨系统对应不会与默认镜像生成第二条相同本地路径");
            config.Mappings.Clear();

            // 重现云端 9424 被移动到 9425 下，失效人工对应污染所有同名系统的情况。
            var nested = Path.Combine(cloud, "9425", "9424", name); Directory.CreateDirectory(Path.GetDirectoryName(nested)); File.Move(ca, nested);
            config.Mappings[Settings.MapKey(FolderLink.Parse(cloud), la)] = ca;
            var configBefore = JsonConvert.SerializeObject(config); var before = new[] { a, b, la, lb }.ToDictionary(p => p, Files.Hash);
            session = await Inspect(); var moved = session.Sources.Single(s => s.Path == nested); var unrelated = session.Sources.Single(s => s.Path == cb);
            Check(moved.Plans.Count == 0 && moved.AmbiguousLocals.Contains(la) && !unrelated.AmbiguousLocals.Contains(la) && !unrelated.Diagnostics.Any(d => d.Contains(la)), "旧云端人工路径失效时只提示本系统完整尾部候选，不污染其他系统同名源");
            rows = ReferenceComparisonBuilder.Build(session, session.Assemblies[0]);
            var row = rows.Single(r => r.Local == la);
            Check(row.Cloud == nested && row.State == "对应待确认" && row.CandidateCloudPaths.SequenceEqual(new[] { nested }), "移动后的唯一候选显示一行并明确待确认，不冒充已完成映射");
            Check(JsonConvert.SerializeObject(config) == configBefore && before.All(p => Files.Hash(p.Key) == p.Value), "候选归并不修改人工映射、不重写总装或源DWG");
            Check(new SystemMatcher(session.Sources.Select(s => s.Path)).Systems(nested).SetEquals(new[] { "9424" }), "嵌套系统文件夹按最近系统编号识别，不串入父级9425");

            var orphan = Path.Combine(local, "local_9431", name); var orphanAssembly = Path.Combine(scope, "114101-SIF-M3-M-9431.dwg"); Dwg(orphan, "orphan"); Dwg(orphanAssembly, "assembly");
            DwgReader.Graph[orphanAssembly] = new List<RefEdge> { Edge(orphan) };
            config.Mappings[Settings.MapKey(FolderLink.Parse(cloud), orphan)] = Path.Combine(cloud, "9431", name);
            var orphanSession = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true };
            orphanSession.Assemblies.Add(Workflow.ScanAssembly(orphanAssembly, new DwgReader(), true)); orphanSession.Sources = await Workflow.Check(orphanSession, config, CancellationToken.None, null, sync);
            Check(orphanSession.Sources.All(s => !s.AmbiguousLocals.Contains(orphan)) && orphanSession.Warnings.Any(w => w.Contains(orphan)), "系统筛选无候选时只报告本地问题，不回退到全项目同名文件");

            var copy = Path.Combine(cloud, "9425", "9424", "copies", name); Dwg(copy, "another-candidate");
            var shared = Path.Combine(local, "Shared", name); Dwg(shared, "unknown-owner");
            var scan = new ScanResult { DirectReferencesScanned = true }; scan.DirectRows.Add(new XrefRow { LocalPath = shared, Name = name, ReferenceNames = { "Shared" } });
            var ambiguous = new WorkflowSession { LocalModelsRoot = local, ConnectorRoot = cloud, ScopeFolder = scope, Checked = true };
            var model = new AssemblyUpdate { Path = a, Scan = scan }; ambiguous.Assemblies.Add(model);
            ambiguous.Sources = new[] { nested, copy }.Select(p => new CloudUpdate { Name = name, Path = p, ExpectedLocalPath = Paths.MirrorTarget(local, cloud, p), AmbiguousLocals = new List<string> { shared } }).ToList();
            rows = ReferenceComparisonBuilder.Build(ambiguous, model); row = rows.Single(r => r.Local == shared);
            Check(rows.Count(r => r.Referenced) == 1 && row.CandidateCloudPaths.Count == 2 && row.CloudState == "待确认" && row.CloudDate == "—" && row.DateDifference == "无法比较日期", "真正多候选只保留一条真实引用，不伪造具体云端路径或日期");
            foreach (var source in ambiguous.Sources) source.Plans.Add(new TransferPlan { Local = shared, ConnectorPath = source.Path, Row = new XrefRow { LocalPath = shared, CloudPath = source.Path } });
            rows = ReferenceComparisonBuilder.Build(ambiguous, model); row = rows.Single(r => r.Local == shared);
            Check(rows.Count == 1 && row.CandidateCloudPaths.Count == 2 && row.CloudState == "待确认", "多个强对应互相冲突时仍只显示一条本地文件并阻止任意选源");
            using (var form = new WorkflowForm(ambiguous, config, () => { }, () => sync))
            {
                bool rejected = false;
                try { await form.PlanReferenceDownload(row, shared, CancellationToken.None); } catch (IOException) { rejected = true; }
                Check(rejected, "多候选汇总行不能直接下载或任意覆盖本地文件");
            }
            var again = ReferenceComparisonBuilder.Build(ambiguous, model);
            Check(again.Count == rows.Count && again.Select(r => r.Local).Distinct(StringComparer.OrdinalIgnoreCase).Count() == again.Count, "反复检查及未引用目录枚举不会重新制造重复本地路径");
            using (var form = new WorkflowForm(ambiguous, config, () => { }, () => sync))
            {
                var original = Files.Hash(shared); form.SetReferenceSource(row, copy);
                var key = Settings.MapKey(FolderLink.Parse(cloud), shared);
                Check(config.Mappings[key] == copy && Files.Hash(shared) == original, "用户显式指定云端文件只保存对应，不下载或改写本地DWG");
                var outside = Path.Combine(root, "Outside.dwg"); Dwg(outside, "outside");
                Reject(() => form.SetReferenceSource(row, outside), "人工指定来源仍禁止选择Connector根目录以外的文件");
                Check(config.Mappings[key] == copy, "指定非法来源失败不覆盖已有人工对应");
            }
        }
    }
}
