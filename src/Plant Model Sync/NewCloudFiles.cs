using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed class NewCloudFile
    {
        public bool Selected { get; set; } = true;
        public string Name => Path.GetFileName(Transfer.Local);
        public string Cloud => Transfer.ConnectorPath;
        public string Local => Transfer.Local;
        public string LocalState => string.IsNullOrEmpty(Transfer.LocalHash) ? "新建" : Transfer.IsUnchanged ? "日期相同，复用" : "日期不同，以云端覆盖（不备份）";
        public string TargetSummary => Targets.Count == 0 ? "请选择总装" : string.Join(" | ", Targets.Select(Path.GetFileName));
        public List<string> Targets { get; set; } = new List<string>();
        public TransferPlan Transfer { get; set; }
        public string State { get; set; } = "待确认";
        public double X { get; set; } = 6653112.1;
        public double Y { get; set; } = 29134629;
        public double Z { get; set; }
        public double Scale { get; set; } = 1;
        public double Rotation { get; set; } = -4.42;
        public ReferencePlacement Placement() => new ReferencePlacement { Source = Local, X = X, Y = Y, Z = Z, Scale = Scale, Rotation = Rotation };
    }

    internal sealed class NewFileBatch
    {
        public List<NewCloudFile> Files { get; set; } = new List<NewCloudFile>();
        public Dictionary<string, string> AssemblyHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<AttachmentResult> Results { get; set; } = new List<AttachmentResult>();
        public string LogPath { get; set; }
        public string DownloadLog { get; set; }
        public string Error { get; set; }
    }

    // 下载与新增引用分开记账；总装逐份原子安装，失败保留下载及原图备份以便重试。
    internal static class NewCloudFiles
    {
        public static async Task<NewFileBatch> Plan(WorkflowSession session, Settings settings, IEnumerable<CloudUpdate> sources, SyncService sync, CancellationToken ct, bool allowExistingUpdate = false, bool captureAssemblyHashes = true)
        {
            if (!session.Checked) throw new IOException("请先完成更新检查。");
            if (string.IsNullOrWhiteSpace(session.LocalModelsRoot)) throw new IOException("请指定本地模型根目录。");
            var batch = new NewFileBatch(); var folder = FolderLink.Parse(session.ConnectorRoot);
            var selected = sources.GroupBy(s => Paths.Canonical(s.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (selected.Count == 0) throw new IOException("请先勾选云端文件；新增文件可取消“仅显示本地已存在”，或选择“只看云端新增”。");
            var matcher = new SystemMatcher(selected.Select(s => s.Path));
            foreach (var source in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (!session.Sources.Contains(source)) throw new IOException("所选源文件不属于本次检查。");
                if (source.AmbiguousLocals.Count > 0 || source.Diagnostics.Count > 0 || source.MissingReferenceIssues.Count > 0) throw new IOException(source.Name + " 有待处理的对应或缺失引用，请先核对，不通过新增入口绕过。");
                var local = Paths.MirrorTarget(session.LocalModelsRoot, folder.Project, source.Path);
                ValidatePath(session, local, source.Path);
                var plans = await sync.Plan(folder, new[] { new XrefRow { Name = source.Name, LocalPath = local, CloudPath = source.Path, MatchStatus = "用户确认新增云端文件至 local_ 镜像并挂接总装" } }, false, ct, null);
                var plan = plans.Single();
                if (plan.Blocked != null) throw new IOException(source.Name + "：" + plan.Blocked);
                if (!allowExistingUpdate && !string.IsNullOrEmpty(plan.LocalHash) && !plan.IsUnchanged) throw new IOException(source.Name + " 的镜像文件日期不同；请先通过普通 Pull 核对并更新，再新增引用。");
                var item = new NewCloudFile { Transfer = plan };
                var codes = matcher.Systems(source.Path);
                var candidates = session.Assemblies.Where(a => codes.Count == 1 && matcher.Systems(a.Path).SetEquals(codes)).ToList();
                if (candidates.Count == 1) item.Targets.Add(candidates[0].Path);
                batch.Files.Add(item);
            }
            if (batch.Files.GroupBy(f => f.Local, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new IOException("多个云端源指向同一本地目标，请修正目录。");
            if (captureAssemblyHashes) foreach (var a in session.Assemblies) batch.AssemblyHashes[a.Path] = Files.Hash(a.Path);
            return batch;
        }

        private static void ValidatePath(WorkflowSession session, string local, string cloud)
        {
            if (local == null || !Paths.IsWithin(session.LocalModelsRoot, local) || Paths.IsHistory(local)) throw new IOException("新增文件落盘位置不在正式本地模型目录内。");
            if (!Paths.IsWithin(session.ConnectorRoot, cloud) || Paths.IsWithin(session.ConnectorRoot, local) || Paths.IsWithin(session.LocalModelsRoot, cloud)) throw new IOException("本地与 Connector 目录不可交叉，源文件必须在本次云端根目录内。");
            if (!local.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || session.Assemblies.Any(a => string.Equals(a.Path, local, StringComparison.OrdinalIgnoreCase))) throw new IOException("新增源文件不能覆盖总装。");
            if (File.Exists(local) && (File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0) throw new IOException("新增目标是文件链接，不允许替换：" + local);
            // 未声明的目录链接不能把新文件写出本地模型目录。
            for (var p = Path.GetDirectoryName(local); !string.IsNullOrEmpty(p) && Paths.IsWithin(session.LocalModelsRoot, p); p = Path.GetDirectoryName(p))
                if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("新增目标经过目录链接，请改用实际 local_ 工作目录：" + p);
        }

        public static List<NewCloudFile> ValidateSelection(WorkflowSession session, NewFileBatch batch)
        {
            var files = batch.Files.Where(f => f.Selected).ToList();
            if (files.Count == 0) throw new IOException("没有勾选新增文件。");
            foreach (var f in files)
            {
                if (f.Transfer.Blocked != null) throw new IOException(f.Name + "：" + f.Transfer.Blocked);
                if (new[] { f.X, f.Y, f.Z, f.Scale, f.Rotation }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || f.Scale <= 0) throw new IOException(f.Name + " 的坐标、比例或旋转无效；比例须大于 0。");
                ValidatePath(session, f.Local, f.Cloud);
                if (f.Targets.Count == 0) throw new IOException(f.Name + " 尚未指定要加入的总装。");
                f.Targets = f.Targets.Select(Paths.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var a in f.Targets)
                {
                    if (!session.Assemblies.Any(x => string.Equals(x.Path, a, StringComparison.OrdinalIgnoreCase)) || !batch.AssemblyHashes.ContainsKey(a) || Paths.IsHistory(a) || !Paths.IsWithin(session.LocalModelsRoot, a)) throw new IOException("目标必须为本次已检查的正式本地总装：" + a);
                    if (files.Any(x => string.Equals(x.Local, a, StringComparison.OrdinalIgnoreCase))) throw new IOException("源文件与总装相同，禁止自引用。");
                }
                f.Transfer.AffectedAssemblies = f.Targets.ToList();
            }
            return files;
        }

        public static async Task Run(WorkflowSession session, NewFileBatch batch, SyncService sync, CancellationToken ct, Action<string> checkClosed, Func<string, List<ReferencePlacement>, AttachmentResult> stage, string storage = null)
        {
            var files = ValidateSelection(session, batch);
            var groups = files.SelectMany(f => f.Targets.Select(a => new { File = f, Assembly = a })).GroupBy(p => p.Assembly, StringComparer.OrdinalIgnoreCase).ToList();
            batch.Results = groups.Select(g => new AttachmentResult { Assembly = g.Key, Sources = g.Select(x => x.File.Local).ToList(), BeforeHash = batch.AssemblyHashes[g.Key] }).ToList();
            foreach (var result in batch.Results)
            {
                if (!session.PendingReferenceAdds.TryGetValue(result.Assembly, out var waiting)) session.PendingReferenceAdds[result.Assembly] = waiting = new List<string>();
                foreach (var source in result.Sources) if (!waiting.Contains(source, StringComparer.OrdinalIgnoreCase)) waiting.Add(source);
            }
            var directory = Path.Combine(storage ?? Settings.DataDirectory, "Logs", "NewReferences"); Directory.CreateDirectory(directory);
            batch.LogPath = Path.Combine(directory, "new-xrefs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".md");
            session.LastUpdateLog = session.LastLog = batch.LogPath; Write(batch, "预检");
            try
            {
                // 先检查全部总装，避免已知锁定或变化后才开始下载。
                foreach (var result in batch.Results)
                {
                    ct.ThrowIfCancellationRequested(); checkClosed(result.Assembly); Files.AssertClosed(result.Assembly);
                    if (Files.Hash(result.Assembly) != result.BeforeHash) throw new IOException("总装在确认后变化，请重新检查：" + result.Assembly);
                }
                var plans = files.Select(f => f.Transfer).ToList(); var folder = FolderLink.Parse(session.ConnectorRoot);
                var journal = sync.BeginUpdateLog(folder, plans, false, session.ScopeFolder);
                batch.DownloadLog = journal.Path;
                await sync.Execute(folder, plans, false, ct, null, journal);
                foreach (var f in files) f.State = f.Transfer.Verified && f.Transfer.Row.SyncState != SyncFeedback.Failed ? "源文件已就绪" : "下载失败，不挂接";
                Write(batch, "下载完成；逐份挂接总装");
                foreach (var result in batch.Results)
                {
                    ct.ThrowIfCancellationRequested();
                    var members = files.Where(f => f.Targets.Contains(result.Assembly, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (members.Any(f => !f.Transfer.Verified || f.Transfer.Row.SyncState == SyncFeedback.Failed)) { result.State = "下载未全部通过，该总装未修改"; Write(batch, "部分失败"); continue; }
                    string staged = null;
                    try
                    {
                        checkClosed(result.Assembly); Files.AssertClosed(result.Assembly);
                        if (Files.Hash(result.Assembly) != result.BeforeHash) throw new IOException("总装已变化，请重新检查。");
                        foreach (var f in members) VerifySource(f);
                        result.State = "准备暂存新总装"; Write(batch, "执行中");
                        var made = stage(result.Assembly, members.Select(f => f.Placement()).ToList()); staged = made.Staged;
                        result.Added = made.Added;
                        if (staged != null)
                        {
                            ct.ThrowIfCancellationRequested(); checkClosed(result.Assembly);
                            foreach (var f in members) VerifySource(f);
                            result.Backup = Files.InstallDownload(staged, result.Assembly, result.BeforeHash); staged = null;
                        }
                        result.AfterHash = Files.Hash(result.Assembly);
                        result.State = result.Added > 0 ? "已新增 Xref 并保存总装" : "引用已存在，未重复插入";
                        session.PendingReferenceAdds[result.Assembly].RemoveAll(p => result.Sources.Contains(p, StringComparer.OrdinalIgnoreCase));
                        if (session.PendingReferenceAdds[result.Assembly].Count == 0) session.PendingReferenceAdds.Remove(result.Assembly);
                        foreach (var f in members) if (f.Transfer.Written && f.Transfer.Verified) session.UpdatedHashes[f.Local] = f.Transfer.ConnectorHash;
                        var original = session.Assemblies.Single(a => string.Equals(a.Path, result.Assembly, StringComparison.OrdinalIgnoreCase));
                        if (!session.AddedReferences.TryGetValue(result.Assembly, out var additions)) session.AddedReferences[result.Assembly] = additions = new List<string>();
                        foreach (var p in result.Sources.Where(p => !original.Scan.Rows.Any(r => string.Equals(r.LocalPath, p, StringComparison.OrdinalIgnoreCase))))
                            if (!additions.Contains(p, StringComparer.OrdinalIgnoreCase)) additions.Add(p);
                        // 新图纸关系必须重扫，不能继续使用旧的导出期望集合。
                        session.Checked = false;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { result.State = "挂接失败：" + ex.Message; }
                    finally { if (staged != null && File.Exists(staged)) File.Delete(staged); Write(batch, "执行中"); }
                }
            }
            catch (Exception ex) { batch.Error = ex is OperationCanceledException ? "用户取消；已完成项保留" : ex.Message; throw; }
            finally { Write(batch, batch.Error != null ? "中止 / 需核对" : batch.Results.All(r => r.AfterHash != null) ? "完成" : "部分失败"); }
        }

        private static void VerifySource(NewCloudFile file)
        {
            var expected = file.Transfer.Written ? file.Transfer.ConnectorHash : file.Transfer.LocalHash;
            if (Files.Hash(file.Local) != expected || Files.Hash(file.Cloud) != file.Transfer.ConnectorHash) throw new IOException("本地或 Connector 源内容变化，请重检：" + file.Name);
        }

        private static void Write(NewFileBatch batch, string state)
        {
            var text = new StringBuilder("# 新增云端文件与总装 Xref 记录\r\n\r\n");
            text.AppendLine("状态：" + state).AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).AppendLine("异常：" + (batch.Error ?? "无")).AppendLine("下载逐项日志：" + batch.DownloadLog);
            text.AppendLine("挂接规则：Attachment；WCS 坐标、统一比例及角度按逐文件确认值；相对路径；不自动换算单位。总装逐份保存，不是整个批次全部回滚。");
            foreach (var f in batch.Files.Where(f => f.Selected)) text.AppendLine("\r\n云端：" + f.Cloud + "\r\n本地：" + f.Local + "\r\n源状态：" + f.State + "\r\nSHA-256：" + f.Transfer.ConnectorHash + "\r\nXref 覆盖：不备份；源修改时间 UTC：" + f.Transfer.ConnectorModifiedUtc?.ToString("o") + "；本地修改时间 UTC：" + (f.Transfer.AfterLocalModifiedUtc ?? f.Transfer.LocalModifiedUtc)?.ToString("o") + "\r\nWCS 插入点：" + f.X + ", " + f.Y + ", " + f.Z + "；统一比例：" + f.Scale + "；旋转（度）：" + f.Rotation);
            foreach (var r in batch.Results) text.AppendLine("\r\n总装：" + r.Assembly + "\r\n结果：" + r.State + "\r\n新增数量：" + r.Added + "\r\n原图备份：" + r.Backup + "\r\n修改前：" + r.BeforeHash + "\r\n修改后：" + r.AfterHash + "\r\n引用：" + string.Join(" | ", r.Sources));
            AtomicText(Path.ChangeExtension(batch.LogPath, ".json"), JsonConvert.SerializeObject(new { recordedUtc = DateTime.UtcNow, state, batch.Error, batch.DownloadLog, files = batch.Files.Where(f => f.Selected).Select(f => new { f.Cloud, f.Local, f.State, f.Targets, connectorModifiedUtc = f.Transfer.ConnectorModifiedUtc, afterLocalModifiedUtc = f.Transfer.AfterLocalModifiedUtc ?? f.Transfer.LocalModifiedUtc, placement = f.Placement(), hash = f.Transfer.ConnectorHash, downloaded = f.Transfer.Written, verified = f.Transfer.Verified }), assemblies = batch.Results }, Formatting.Indented));
            AtomicText(batch.LogPath, text.ToString());
        }
        private static void AtomicText(string path, string content)
        {
            var temporary = path + ".tmp"; File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
}
