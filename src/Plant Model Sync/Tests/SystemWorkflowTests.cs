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
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal async Task VerifySystemFilters(string evidence)
        {
            SelectSources(true);
            var before = Session.Sources.Count(s => s.Selected);
            systemFilter.SelectedItem = "9421";
            WorkflowTestProgram.Check(sourcesGrid.Rows.Count > 0 && sourcesGrid.Rows.Cast<DataGridViewRow>().All(r => ((CloudUpdate)r.DataBoundItem).System == "9421"), "系统下拉只显示所选系统，不混淆其他同名图纸");
            stateFilter.SelectedIndex = 2;
            stateFilter.SelectedIndex = 0; systemFilter.SelectedIndex = 0;
            WorkflowTestProgram.Check(Session.Sources.Count(s => s.Selected) == before, "系统及状态筛选不改变原有 Pull 勾选");
            systemFilter.SelectedItem = "9421"; SelectSources(false);
            WorkflowTestProgram.Check(Session.Sources.Where(s => s.System == "9424").All(s => s.Selected == (s.State == "可更新")), "清除可见勾选不误改其他系统");
            systemFilter.SelectedIndex = 0;
            ShowSource(); ShowModel(); await Task.Delay(40); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "system-matching-ui.png"), ImageFormat.Png); }
        }
    }

    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifySystemMatchingCacheAndLogs(string evidence)
        {
            var root = Path.Combine(evidence, "system-fixtures");
            var local = Path.Combine(root, "本地总装根目录"); var cloud = Path.Combine(root, "Plant 3D Models");
            var c21 = Path.Combine(cloud, "114101-SIF-M3-M-9421_OCF"); var c24 = Path.Combine(cloud, "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM");
            var a21 = Path.Combine(local, "单系统总装文件", "114101-SIF-M3-M-9421_OCF.dwg");
            var a24 = Path.Combine(local, "单系统总装文件", "114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM.dwg");
            var x21 = Path.Combine(local, "OCF", "EL+5.30.dwg"); var x24 = Path.Combine(local, "GYPSUM DEWATERING SYSTEM", "EL+5.30.dwg");
            var owner = Path.Combine(local, "无编号目录", "ByOwner.dwg"); var shared = Path.Combine(local, "公共目录", "SharedGrid.dwg");
            var suffix = Path.Combine(local, "OCF", "Level A", "Duplicate.dwg");
            foreach (var p in new[] { a21, a24, x21, x24, owner, shared, suffix }) Dwg(p, "local-" + p);
            foreach (var dir in new[] { c21, c24 }) foreach (var name in new[] { "EL+5.30.dwg", "ByOwner.dwg", "SharedGrid.dwg", "Level A/Duplicate.dwg", "Level B/Duplicate.dwg" }) Dwg(Path.Combine(dir, name), "cloud-" + dir + name);
            List<RefEdge> Edges(params string[] paths) => paths.Select(p => new RefEdge { Name = Path.GetFileNameWithoutExtension(p), Saved = p, Resolved = p }).ToList();
            DwgReader.Graph[a21] = Edges(x21, owner, shared, suffix); DwgReader.Graph[a24] = Edges(x24, shared);
            var reader = new CachedReader(new DwgReader());
            var session = new WorkflowSession { ConnectorRoot = cloud, ScopeFolder = local, Checked = true, ReferenceCache = reader };
            session.Assemblies.Add(Workflow.ScanAssembly(a21, reader)); session.Assemblies.Add(Workflow.ScanAssembly(a24, reader));
            var config = new Settings(); var service = new SyncService(config, () => { }, Path.Combine(root, "state")); var folder = FolderLink.Parse(cloud);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            var floor = session.Sources.Where(s => s.Name == "EL+5.30.dwg").ToList();
            Check(floor.All(s => s.Plans.Count == 1 && s.State == "可更新" && s.MatchReason.Contains(s.System)), "9421/9424 同名楼层 DWG 按系统准确分开匹配");
            Check(floor.Single(s => s.System == "9421").Plans[0].Local == x21 && floor.Single(s => s.System == "9424").Plans[0].Local == x24, "本地 OCF 与 GYPSUM 文件夹别名对应正确系统");
            Check(session.Sources.Single(s => s.Path == Path.Combine(c21, "ByOwner.dwg")).Plans.Single().Local == owner, "本地路径无编号时使用引用它的总装系统编号");
            Check(session.Sources.Where(s => s.Name == "SharedGrid.dwg").All(s => s.State == "对应有歧义" && s.Plans.Count == 0), "跨系统共用同名引用不猜测或自动覆盖");
            Check(session.Sources.Single(s => s.Path == Path.Combine(c21, "Level A", "Duplicate.dwg")).Plans.Single().Local == suffix, "系统内重名进一步按一致子目录唯一匹配");
            var reads = reader.Reads; var hashReads = session.HashCache.Reads;
            Workflow.ScanAssembly(a21, reader); Workflow.ScanAssembly(a24, reader);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(reader.Reads == reads && session.HashCache.Reads == hashReads && session.HashCache.Hits > 0, "重复检测未变化 DWG 不重新打开或重复读取完整哈希");
            File.AppendAllText(x21, "-changed", Encoding.UTF8);
            Workflow.ScanAssembly(a21, reader);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(reader.Reads == reads + 1 && session.HashCache.Reads == hashReads + 1, "单个 DWG 改变仅使相应引用缓存和哈希缓存失效");
            var liveReader = new CachedReader(new DwgReader(), p => true); liveReader.Read(a21); liveReader.Read(a21);
            Check(liveReader.Reads == 2 && liveReader.Hits == 0, "已打开可能未保存的图纸绕过依赖缓存");
            session.HashCache.Clear(); reader.Clear(); Workflow.ScanAssembly(a21, reader);
            await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(reader.Reads > reads + 1 && session.HashCache.Reads > hashReads + 1, "完整重检明确清空两类缓存");

            // 已确认文件夹规则覆盖自动归属，并适用于后续新增的同名文件。
            config.SystemFolders[Settings.MapKey(folder, Path.GetDirectoryName(shared))] = Path.GetFileName(c24);
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            var mappedShared = session.Sources.Single(s => s.Path == Path.Combine(c24, "SharedGrid.dwg"));
            Check(mappedShared.Plans.Single().Local == shared && mappedShared.MatchReason.Contains("已确认"), "一个文件夹规则即可解除跨系统共享文件的对应歧义");
            config.Mappings[Settings.MapKey(folder, shared)] = Path.Combine(c21, "SharedGrid.dwg");
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(session.Sources.Single(s => s.Path == Path.Combine(c21, "SharedGrid.dwg")).Plans.Single().Local == shared, "逐文件人工对应优先于系统文件夹规则");
            config.Mappings[Settings.MapKey(folder, shared)] = Path.Combine(c21, "不存在.dwg");
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(session.Sources.Where(s => s.Name == "SharedGrid.dwg").All(s => s.Plans.Count == 0 && s.AmbiguousLocals.Contains(shared)), "原人工对应失效时阻止静默回退到其他源");
            config.Mappings.Remove(Settings.MapKey(folder, shared));
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache); Workflow.UpdateAffected(session);
            File.WriteAllText(Path.Combine(evidence, "system-matching.json"), JsonConvert.SerializeObject(session.Sources.Select(s => new { s.Name, s.System, s.RelativePath, s.State, s.MatchReason, localPaths = s.Plans.Select(p => p.Local), s.Diagnostics }), Formatting.Indented), new UTF8Encoding(false));
            using (var form = new WorkflowForm(session, config, () => { }, () => service))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show(); await form.VerifySystemFilters(evidence); form.Close();
            }
            using (var dialog = new SystemFolderForm(folder, new List<string> { Path.GetDirectoryName(x21), Path.GetDirectoryName(x24) }, config))
            {
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-5000, -5000); dialog.Show(); dialog.Refresh();
                var grid = dialog.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>()).OfType<DataGridView>().Single();
                grid.Rows[0].Cells[2].Value = Path.GetFileName(c21); grid.EndEdit(); dialog.Refresh();
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height)); bitmap.Save(Path.Combine(evidence, "system-folder-rules.png"), ImageFormat.Png); }
                dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "保存并重新检测").PerformClick();
                Check(config.SystemFolders[Settings.MapKey(folder, Path.GetDirectoryName(x21))] == Path.GetFileName(c21), "系统规则表格下拉选择实际保存本地文件夹对应");
            }
            config.Mappings[Settings.MapKey(folder, x21)] = "";
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service, session.HashCache);
            Check(session.Sources.Single(s => s.Path == Path.Combine(c21, "EL+5.30.dwg")).Plans.Single().Local == x21, "兼容旧配置空映射，不把未指定误报为失效人工对应");
            var plan = session.Sources.Single(s => s.Path == Path.Combine(c24, "SharedGrid.dwg")).Plans;
            var log = await service.Execute(folder, plan, false, CancellationToken.None, null);
            var report = JObject.Parse(File.ReadAllText(Path.ChangeExtension(log, ".json"), Encoding.UTF8));
            Check(File.Exists(log) && report["files"][0]["verified"].Value<bool>() && plan[0].BackupPath == null && report["files"][0]["backupPolicy"].Value<string>() == "Xref 覆盖不备份", "每次Pull仍生成独立日志及JSON，明确记录免备份规则和校验结果");
            Check(report["files"][0]["affectedAssemblies"].Count() == 2 && report["files"][0]["beforeLocalHash"].Value<string>() != report["files"][0]["beforeConnectorHash"].Value<string>(), "更新日志含两份受影响总装和更新前两侧哈希");
            var canceled = new CancellationTokenSource(); canceled.Cancel();
            // 复用文件路径但重新计划，避免前一次结果混入取消日志。
            var cancelPlans = await service.Plan(folder, new[] { floor[0].Plans[0].Row }, false, CancellationToken.None, null);
            var journal = service.BeginUpdateLog(folder, cancelPlans, false, local);
            try { await service.Execute(folder, cancelPlans, false, canceled.Token, null, journal); throw new Exception("预期取消"); } catch (OperationCanceledException) { }
            Check(File.ReadAllText(journal.Path, Encoding.UTF8).Contains("已取消") && !cancelPlans[0].Written, "取消也留下日志，且不把未写入文件报告成成功");

            // 即使长度和修改时间被恢复，执行阶段仍完整重算哈希。
            var safetyRow = new XrefRow { Name = "EL+5.30.dwg", LocalPath = x24, CloudPath = Path.Combine(c24, "EL+5.30.dwg") };
            var safetyPlans = await service.Plan(folder, new[] { safetyRow }, false, CancellationToken.None, null, session.HashCache);
            var time = File.GetLastWriteTimeUtc(x24); var bytes = File.ReadAllBytes(x24); bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(x24, bytes); File.SetLastWriteTimeUtc(x24, time);
            var safetyLog = await service.Execute(folder, safetyPlans, false, CancellationToken.None, null);
            Check(!safetyPlans[0].Written && safetyRow.SyncState == SyncFeedback.Failed && File.ReadAllText(safetyLog, Encoding.UTF8).Contains("预览后发生改变"), "缓存不削弱执行前完整哈希校验或失败日志");
            var newSource = Path.Combine(c21, "NewFile.dwg"); var newLocal = Path.Combine(local, "OCF", "NewFile.dwg"); Dwg(newSource, "new-publish");
            var create = await service.Plan(folder, new[] { new XrefRow { Name = "NewFile.dwg", LocalPath = newLocal, CloudPath = newSource } }, false, CancellationToken.None, null);
            var createLog = await service.Execute(folder, create, false, CancellationToken.None, null);
            Check(createLog != log && create[0].Written && create[0].BackupPath == null, "不同更新使用不同日志，新建文件明确没有原文件备份");

            var batchRows = new List<XrefRow>();
            foreach (var name in new[] { "First.dwg", "Second.dwg", "Third.dwg" })
            {
                var from = Path.Combine(c21, name); var to = Path.Combine(local, "OCF", name); Dwg(from, "published"); Dwg(to, "previous");
                batchRows.Add(new XrefRow { Name = name, LocalPath = to, CloudPath = from });
            }
            var partial = await service.Plan(folder, batchRows, false, CancellationToken.None, null); var midCancel = new CancellationTokenSource();
            var midLog = service.BeginUpdateLog(folder, partial, false, local);
            try { await service.Execute(folder, partial, false, midCancel.Token, m => { if (m.StartsWith("已同步")) midCancel.Cancel(); }, midLog); throw new Exception("预期中途取消"); } catch (OperationCanceledException) { }
            var canceledReport = JObject.Parse(File.ReadAllText(midLog.DetailPath, Encoding.UTF8));
            Check(partial[0].Written && partial[0].Verified && !partial[1].Written && !partial[2].StartedUtc.HasValue && canceledReport["files"][2]["result"].Value<string>() == "未执行", "中途取消日志区分已完成、已取消及尚未执行文件");
            var partialFailure = await service.Plan(folder, batchRows.Skip(1), false, CancellationToken.None, null);
            File.AppendAllText(batchRows[1].LocalPath, "-edit-after-preview", Encoding.UTF8);
            var mixedLog = await service.Execute(folder, partialFailure, false, CancellationToken.None, null);
            Check(!partialFailure[0].Written && partialFailure[1].Verified && File.ReadAllText(mixedLog, Encoding.UTF8).Contains("含失败"), "部分文件失败仍处理其他文件，日志明确报告部分失败");

            // 使用隔离文件记录重复检查的实际读文件次数，不把此计时当作真实项目性能保证。
            var bulkRows = new List<XrefRow>();
            for (var i = 0; i < 160; i++)
            {
                var from = Path.Combine(root, "benchmark", "cloud", i + ".dwg"); var to = Path.Combine(root, "benchmark", "local", i + ".dwg");
                Dwg(from, new string('C', 65536)); Dwg(to, new string('L', 65536));
                bulkRows.Add(new XrefRow { Name = i + ".dwg", LocalPath = to, CloudPath = from });
            }
            var bulkFolder = FolderLink.Parse(Path.Combine(root, "benchmark", "cloud")); var bulkCache = new DetectionCache(); var watch = Stopwatch.StartNew();
            var first = await service.Plan(bulkFolder, bulkRows, false, CancellationToken.None, null, bulkCache); var firstMs = watch.ElapsedMilliseconds;
            var firstReads = bulkCache.Reads; watch.Restart();
            var repeat = await service.Plan(bulkFolder, bulkRows, false, CancellationToken.None, null, bulkCache); var repeatMs = watch.ElapsedMilliseconds;
            Check(firstReads == 320 && bulkCache.Reads == firstReads && bulkCache.Hits == 320 && first.All(p => p.Blocked == null) && repeat.All(p => p.Blocked == null), "160 对隔离文件重复检查完整哈希读取从 320 次降为 0 次");
            File.WriteAllText(Path.Combine(evidence, "cache-benchmark.json"), JsonConvert.SerializeObject(new { fixtureFiles = 320, bytesPerFile = 65542, firstMs, repeatMs, firstFullHashReads = firstReads, repeatedFullHashReads = bulkCache.Reads - firstReads, repeatCacheHits = bulkCache.Hits, realProjectBenchmark = false }, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
