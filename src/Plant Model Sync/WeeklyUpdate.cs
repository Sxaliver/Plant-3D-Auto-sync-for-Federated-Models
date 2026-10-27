using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed class WeeklyRequest
    {
        public string SettingsPath { get; set; }
        public string Storage { get; set; }
        public string OutputRoot { get; set; }
        public string RunDirectory { get; set; }
        public int ProcessId { get; set; }
        public bool DryRun { get; set; }
        public string[] LegacyConnectorRoots { get; set; } = new string[0];
        public string[] TrustedRepairReceipts { get; set; } = new string[0];
        public string[] AssemblyFiles { get; set; } = new string[0];
        public string[] SystemCodes { get; set; } = new string[0];
        public bool ForceExport { get; set; }
    }
    internal sealed class WeeklySourceReceipt
    {
        public string LocalHash { get; set; }
        public string CloudHash { get; set; }
        public string CloudPath { get; set; }
    }
    internal sealed class WeeklyExportReceipt
    {
        public string Signature { get; set; }
        public string Output { get; set; }
        public string OutputHash { get; set; }
    }
    internal sealed class WeeklyLedger
    {
        public Dictionary<string, WeeklySourceReceipt> Sources { get; set; } = new Dictionary<string, WeeklySourceReceipt>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, WeeklyExportReceipt> Exports { get; set; } = new Dictionary<string, WeeklyExportReceipt>(StringComparer.OrdinalIgnoreCase);
    }
    internal sealed class WeeklyRewrite
    {
        public string Name { get; set; }
        public string Target { get; set; }
    }
    internal interface IWeeklyDwg : IReferenceReader
    {
        string Stage(string source, string owner, List<WeeklyRewrite> rewrites);
        string StageAttachments(string owner, List<ReferencePlacement> placements);
    }
    internal sealed class WeeklyNode
    {
        public string Local, Cloud, CloudHash, BeforeHash, Snapshot, Error;
        public DateTime? LocalDate, CloudDate;
        public List<RefEdge> Edges = new List<RefEdge>();
        public List<WeeklyRewrite> Rewrites = new List<WeeklyRewrite>();
    }
    // 周更账本与交互同步收据分离：路径本地化不冒充与云端原文件逐字节一致。
    internal sealed class WeeklyUpdate
    {
        internal readonly WeeklyRequest Request;
        internal readonly Settings Config;
        internal readonly IWeeklyDwg Reader;
        internal readonly WeeklyLedger Ledger;
        internal readonly Dictionary<string, WeeklyNode> Nodes = new Dictionary<string, WeeklyNode>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<UpdateReportRow> Rows = new List<UpdateReportRow>();
        internal readonly WorkflowSession Session;
        internal readonly string DateFolder, LedgerPath, ReportPath;
        internal List<BatchExportJob> Jobs = new List<BatchExportJob>();
        private readonly Dictionary<string, string> initialAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,List<string>> additions = new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> blockedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> now;
        internal string State = "准备执行";
        internal string Error = null;
        internal bool Completed;
        internal WeeklyUpdate(WeeklyRequest request, Settings config, IWeeklyDwg reader, Func<DateTimeOffset> clock = null)
        {
            Request = request; Config = config; Reader = reader; now = clock ?? (() => DateTimeOffset.UtcNow);
            config.PreparePaths();
            foreach (var p in new[] {config.LocalModelsRoot, config.ConnectorRoot, config.ProjectScanFolder, request.OutputRoot, request.RunDirectory, request.Storage})
                if (string.IsNullOrWhiteSpace(p) || !Path.IsPathRooted(p)) throw new IOException("周更目录未完整配置。");
            if (!Directory.Exists(config.LocalModelsRoot) || !Directory.Exists(config.ProjectScanFolder)) throw new IOException("本地模型或总装目录不存在。");
            FolderLink.Parse(config.ConnectorRoot);
            if (Paths.IsWithin(config.LocalModelsRoot, config.ConnectorRoot) || Paths.IsWithin(config.ConnectorRoot, config.LocalModelsRoot) || string.Equals(Paths.Canonical(config.LocalModelsRoot), Paths.Canonical(config.ConnectorRoot), StringComparison.OrdinalIgnoreCase)) throw new IOException("本地与 Connector 目录不能交叉。");
            DateFolder = Path.Combine(request.OutputRoot, now().ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"));
            LedgerPath = Path.Combine(request.Storage, "weekly-ledger.json");
            ReportPath = Path.Combine(request.Storage, "Logs", "Weekly", Path.GetFileName(request.RunDirectory) + ".json");
            Ledger = File.Exists(LedgerPath) ? JsonConvert.DeserializeObject<WeeklyLedger>(File.ReadAllText(LedgerPath, Encoding.UTF8)) : new WeeklyLedger();
            Session = new WorkflowSession { LocalModelsRoot = config.LocalModelsRoot, ScopeFolder = config.ProjectScanFolder, ConnectorRoot = config.ConnectorRoot, OutputFolder = DateFolder, Checked = true };
        }
        internal static void AtomicJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        internal void Log(string operation, string local, string state, string details = null, string cloud = null, string backup = null, string before = null, string after = null, string assembly = null, DateTime? sourceModified = null, DateTime? localModified = null)
        {
            Rows.Add(new UpdateReportRow { Time = now().ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), SourceModifiedTime = UpdateReports.FileTime(sourceModified), LocalModifiedTime = UpdateReports.FileTime(localModified), Operation = operation, Local = local, File = Path.GetFileName(local), Connector = cloud, Assembly = assembly, State = state, Details = details, Backup = backup, BeforeHash = before, AfterHash = after });
            Save();
        }
        internal void Save()
        {
            AtomicJson(ReportPath, new { Format = "IWMF.OneClickUpdate.v1", Path = ReportPath, State, Error, StartedUtc = now().UtcDateTime, ChildLogs = new[] { Path.Combine(Request.RunDirectory,"result.json") }, Rows });
            AtomicJson(Path.Combine(Request.RunDirectory, "result.json"), new { State, Error, Completed, ProcessId = Request.ProcessId, DryRun = Request.DryRun, OutputFolder = DateFolder, ReportPath, ChangedFiles = changed.Count, PlannedExports = Jobs.Count, SuccessfulExports = Rows.Count(r=>r.Operation=="周更 NWC"&&r.State=="已导出"), HasFailures = Rows.Any(r=>(r.State??"").Contains("失败")||(r.State??"").Contains("待处理")||(r.State??"").Contains("待确认")), UpdatedUtc = now().UtcDateTime });
        }
        private string CloudFor(string local)
        {
            if (!Paths.IsWithin(Config.LocalModelsRoot, local) || Paths.IsHistory(local)) return null;
            var rel = Paths.Relative(Config.LocalModelsRoot, local);
            if (rel.StartsWith("local_", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(6);
            var cloud = Paths.UnderRoot(Config.ConnectorRoot, rel);
            return File.Exists(cloud) && InScope(cloud) ? cloud : null;
        }
        private bool InScope(string cloud)
        {
            if(Request.SystemCodes==null||Request.SystemCodes.Length==0)return true;
            var first=Paths.Relative(Config.ConnectorRoot,cloud).Split('/')[0];
            var codes=Regex.Matches(first,@"(?<!\d)94\d{2}(?!\d)").Cast<Match>().Select(m=>m.Value).ToList();
            return codes.Count==0 || codes.All(c=>Request.SystemCodes.Contains(c,StringComparer.OrdinalIgnoreCase));
        }
        private string OwnersOf(string local)
        {
            bool Contains(string root,HashSet<string> visited)
            {
                if(string.Equals(root,local,StringComparison.OrdinalIgnoreCase))return true;
                if(!visited.Add(root)||!Nodes.TryGetValue(root,out var node))return false;
                return node.Edges.Any(e=>Contains(Resolve(root,e),visited));
            }
            return string.Join(" | ",initialAssemblies.Keys.Where(a=>Contains(a,new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
        }
        internal string Resolve(string owner, RefEdge edge)
        {
            var path = Paths.Canonical(Paths.Resolve(owner, edge.Saved));
            foreach (var root in new[] { Config.ConnectorRoot }.Concat(Request.LegacyConnectorRoots ?? new string[0]))
            {
                var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = path.Substring(prefix.Length);
                var cloud = Paths.UnderRoot(Config.ConnectorRoot, rel);
                return File.Exists(cloud) ? Paths.MirrorTarget(Config.LocalModelsRoot, Config.ConnectorRoot, cloud) : path;
            }
            if (Paths.IsWithin(Config.LocalModelsRoot, path))
            {
                var rel = Paths.Relative(Config.LocalModelsRoot, path);
                if (!rel.StartsWith("local_", StringComparison.OrdinalIgnoreCase))
                {
                    var cloud = Paths.UnderRoot(Config.ConnectorRoot, rel);
                    if (File.Exists(cloud)) return Paths.MirrorTarget(Config.LocalModelsRoot, Config.ConnectorRoot, cloud);
                }
            }
            return path;
        }
        private void Visit(string local, bool assembly = false)
        {
            local = Paths.Canonical(local);
            if (Nodes.ContainsKey(local)) return;
            var n = new WeeklyNode { Local = local }; Nodes.Add(local, n);
            try
            {
                n.BeforeHash = Files.Hash(local);
                n.LocalDate = SyncService.ModifiedUtc(local);
                n.Cloud = assembly ? null : CloudFor(local);
                var read = local;
                if (n.Cloud != null)
                {
                    n.CloudHash = Files.Hash(n.Cloud); Files.CheckDwg(n.Cloud);
                    n.CloudDate = SyncService.ModifiedUtc(n.Cloud);
                    var reusable = n.LocalDate.HasValue && n.LocalDate == n.CloudDate;
                    if(!reusable)
                    {
                        n.Snapshot = Path.Combine(Request.RunDirectory, "cloud-snapshots", Paths.Relative(Config.ConnectorRoot, n.Cloud).Replace('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(n.Snapshot)); File.Copy(n.Cloud, n.Snapshot, false);
                        if (Files.Hash(n.Snapshot) != n.CloudHash || Files.Hash(n.Cloud) != n.CloudHash) throw new IOException("Connector 文件读取期间改变。");
                        read = n.Snapshot;
                    }
                }
                n.Edges = Reader.Read(read);
                foreach (var edge in n.Edges)
                {
                    var target = Resolve(local, edge);
                    if (Paths.IsHistory(target)) throw new IOException("引用指向历史目录，需确认：" + target);
                    var actual = Path.GetFullPath(Paths.Resolve(local, edge.Saved));
                    if (!string.Equals(actual, target, StringComparison.OrdinalIgnoreCase) && CloudFor(target) != null) n.Rewrites.Add(new WeeklyRewrite { Name = edge.Name, Target = target });
                    // 不按同名猜测；未知位置只读扫描，不自动复制、删除或重挂。
                    if (File.Exists(target) || CloudFor(target) != null) Visit(target, initialAssemblies.ContainsKey(target));
                    else Log("引用诊断", target, "待确认，未替换", "父图=" + local + "；卸载=" + edge.Unloaded, assembly: local);
                }
            }
            catch (Exception ex) { n.Error = ex.Message; Log("扫描", local, "失败，待处理", ex.Message, n.Cloud); }
        }
        private void CheckWriteLocation(string local)
        {
            if(!Paths.IsWithin(Config.LocalModelsRoot,local)||Paths.IsHistory(local))throw new IOException("不允许写入模型根目录以外或历史目录。");
            if(File.Exists(local)&&(File.GetAttributes(local)&FileAttributes.ReparsePoint)!=0)throw new IOException("不自动替换文件链接："+local);
            for(var parent=Path.GetDirectoryName(local);!string.IsNullOrEmpty(parent)&&Paths.IsWithin(Config.LocalModelsRoot,parent);parent=Path.GetDirectoryName(parent))
                if(Directory.Exists(parent)&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("写入路径含未解析目录链接："+parent);
        }
        internal void Prepare()
        {
            State = "扫描及准备"; Save();
            var assemblies = Directory.GetFiles(Config.ProjectScanFolder, "*.dwg", SearchOption.TopDirectoryOnly).Where(p => !Path.GetFileName(p).StartsWith(".")).Select(Paths.Canonical).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            if(Request.AssemblyFiles?.Length>0)
            {
                var selected=new HashSet<string>(Request.AssemblyFiles.Select(Paths.Canonical),StringComparer.OrdinalIgnoreCase);
                if(selected.Any(p=>!assemblies.Contains(p,StringComparer.OrdinalIgnoreCase)))throw new IOException("指定总装不在已确认目录或文件已消失，停止执行。");
                assemblies=assemblies.Where(selected.Contains).ToList();
            }
            if (assemblies.Count == 0) throw new IOException("总装目录没有 DWG，停止更新。");
            foreach (var a in assemblies) initialAssemblies[a] = Files.Hash(a);
            foreach (var a in assemblies) Visit(a, true);
            var index = TargetFinder.Index(Config.ConnectorRoot, CancellationToken.None);
            var cloudFiles = index.Values.SelectMany(x=>x).Where(InScope).ToList();
            var matcher = new SystemMatcher(cloudFiles);
            foreach(var cloud in cloudFiles)
            {
                var local = Paths.MirrorTarget(Config.LocalModelsRoot,Config.ConnectorRoot,cloud);
                if(local==null || Nodes.ContainsKey(local))continue;
                var codes=matcher.Systems(cloud);
                // 单系统执行不把未引用的公共文件列成该系统的待确认项。
                if(Request.SystemCodes?.Length>0 && codes.Count==0)continue;
                var candidates=assemblies.Where(a=>codes.Count==1 && matcher.Systems(a).SetEquals(codes)).ToList();
                if(candidates.Count!=1) { Log("云端新增检查",local,"待确认，未下载 / 未挂接","未建立引用；唯一目标数="+candidates.Count,cloud); continue; }
                var owner=candidates[0];
                if(!additions.TryGetValue(owner,out var sources))additions[owner]=sources=new List<string>();
                sources.Add(local); Visit(local);
            }
            // 提交前对全部原文件做预检；遇到并行修改，不以新哈希悄悄接受。
            foreach (var n in Nodes.Values)
                if (Files.Hash(n.Local) != n.BeforeHash) throw new IOException("扫描期间本地文件发生变化：" + n.Local);
            foreach (var n in Nodes.Values.OrderBy(n => initialAssemblies.ContainsKey(n.Local)))
            {
                if (n.Error != null || n.Cloud == null && !initialAssemblies.ContainsKey(n.Local)) continue;
                try
                {
                    bool needs = n.Rewrites.Count > 0 || n.Cloud != null && (!n.LocalDate.HasValue || n.LocalDate != n.CloudDate);
                    if (!needs)
                    {
                        if (n.Cloud != null && !Request.DryRun) { Ledger.Sources[n.Local] = new WeeklySourceReceipt { LocalHash = n.BeforeHash, CloudHash = n.CloudHash, CloudPath = n.Cloud }; AtomicJson(LedgerPath, Ledger); }
                        if(n.Cloud!=null)Log("Xref 比较",n.Local,"日期相同，未 Pull","仅比较日期，不代表内容相同",n.Cloud,before:n.BeforeHash,after:n.BeforeHash,assembly:OwnersOf(n.Local),sourceModified:n.CloudDate,localModified:n.LocalDate);
                        continue;
                    }
                    if (Request.DryRun) { Log("只读预检", n.Local, "待更新，尚未执行", "引用路径调整=" + n.Rewrites.Count, n.Cloud); continue; }
                    CheckWriteLocation(n.Local);Files.AssertClosed(n.Local);
                    Directory.CreateDirectory(Path.GetDirectoryName(n.Local));
                    string staged = null;
                    try
                    {
                        staged = Reader.Stage(n.Snapshot ?? n.Local, n.Local, n.Rewrites);
                        var hash = Files.Hash(staged);
                        if (n.Cloud != null && Files.Hash(n.Cloud) != n.CloudHash) throw new IOException("提交前 Connector 文件已改变。");
                        if (SyncService.ModifiedUtc(n.Local) != n.LocalDate || n.Cloud != null && SyncService.ModifiedUtc(n.Cloud) != n.CloudDate) throw new IOException("提交前文件日期已改变。");
                        if (n.CloudDate.HasValue) File.SetLastWriteTimeUtc(staged, n.CloudDate.Value);
                        // 从 Connector 下载的 Xref 覆盖免备份；仅修复本地原图及总装仍保留备份。
                        var backup = Files.InstallDownload(staged, n.Local, n.BeforeHash, backupExisting: n.Snapshot == null); staged = null;
                        if (n.CloudDate.HasValue) Files.PreserveModifiedUtc(n.Local, n.CloudDate.Value);
                        if (Files.Hash(n.Local) != hash) throw new IOException("提交后文件哈希校验失败。");
                        changed.Add(n.Local);
                        if (n.Cloud != null) { Ledger.Sources[n.Local] = new WeeklySourceReceipt { LocalHash = hash, CloudHash = n.CloudHash, CloudPath = n.Cloud }; AtomicJson(LedgerPath, Ledger); }
                        Log("周更 Pull / 路径本地化", n.Local, "完成，已写入校验", "云端为准；更新前本地日期 UTC=" + n.LocalDate?.ToString("o") + "；Connector 源修改日期 UTC=" + n.CloudDate?.ToString("o") + "；更新后本地日期 UTC=" + SyncService.ModifiedUtc(n.Local)?.ToString("o") + "；下载覆盖不备份，仅本地修复保留备份；路径调整=" + n.Rewrites.Count + "；原始云端哈希=" + n.CloudHash + "；保留插入变换及加载状态", n.Cloud, backup == "新建" ? null : backup, n.BeforeHash, hash, OwnersOf(n.Local), sourceModified:n.CloudDate, localModified:SyncService.ModifiedUtc(n.Local));
                    }
                    finally { if (staged != null && File.Exists(staged)) File.Delete(staged); }
                }
                catch (Exception ex) { n.Error = ex.Message; Log("周更更新", n.Local, "失败，待处理", ex.Message, n.Cloud); }
            }
            if (Request.DryRun) { Completed = true; State = "只读预检完成，未修改 DWG / 未导出"; Save(); return; }
            foreach(var addition in additions)
            {
                string staged=null;
                try
                {
                    if(addition.Value.Any(p=>!Nodes.TryGetValue(p,out var n)||n.Error!=null))throw new IOException("新增源未全部就绪，不修改总装。");
                    CheckWriteLocation(addition.Key);Files.AssertClosed(addition.Key); var before=Files.Hash(addition.Key);
                    var placements=addition.Value.Select(p=>new ReferencePlacement{Source=p,X=6653112.1,Y=29134629,Z=0,Scale=1,Rotation=-4.42}).ToList();
                    staged=Reader.StageAttachments(addition.Key,placements);
                    if(staged==null)continue;
                    var hash=Files.Hash(staged);var backup=Files.InstallDownload(staged,addition.Key,before);staged=null;
                    if(Files.Hash(addition.Key)!=hash)throw new IOException("新增总装写后校验失败。");
                    changed.Add(addition.Key);
                    Log("周更新增 Xref",addition.Key,"完成，已写入校验","WCS=6653112.1,29134629,0；比例=1；角度=-4.42°；"+string.Join(" | ",addition.Value),backup:backup,before:before,after:hash,assembly:addition.Key);
                }
                catch(Exception ex){blockedAssemblies.Add(addition.Key);Log("周更新增 Xref",addition.Key,"失败，待处理",ex.Message,assembly:addition.Key);}
                finally{if(staged!=null&&File.Exists(staged))File.Delete(staged);}
            }
            State = "检查待导出总装"; Save();
            var duplicateNames = new HashSet<string>(assemblies.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var file in assemblies)
            {
                try
                {
                    if(blockedAssemblies.Contains(file))throw new IOException("新增引用未完成，保留重试，不导出旧总装冒充更新。");
                    Files.AssertClosed(file);
                    var a = Workflow.ScanAssembly(file, Reader); Session.Assemblies.Add(a);
                    if (a.Scan.Warnings.Count > 0) throw new IOException(string.Join(" | ", a.Scan.Warnings));
                    var dependencies = new HashSet<string>(a.Scan.Rows.Select(r => r.LocalPath), StringComparer.OrdinalIgnoreCase); dependencies.Add(file);
                    var failed = Nodes.Values.Where(n => n.Error != null && dependencies.Contains(n.Local)).ToList();
                    if (failed.Count > 0) throw new IOException("关联源文件未全部更新通过：" + string.Join(" | ", failed.Select(n => n.Local + "：" + n.Error)));
                    foreach (var p in dependencies) a.ExpectedHashes[p] = Files.Hash(p);
                    if (a.ExpectedHashes.Any(p => string.IsNullOrEmpty(p.Value))) throw new IOException("引用文件不存在。");
                    foreach (var n in Nodes.Values.Where(n => n.Cloud != null && dependencies.Contains(n.Local))) a.ExpectedCloudHashes[n.Cloud] = n.CloudHash;
                    var signature = Signature(a.ExpectedHashes);
                    Signatures[file] = signature;
                    if (!Request.ForceExport && Ledger.Exports.TryGetValue(file, out var exported) && exported.Signature == signature && Files.Hash(exported.Output) == exported.OutputHash && !string.IsNullOrEmpty(exported.OutputHash))
                    { Log("周更 NWC", file, "无变化，跳过", "上次成功导出=" + exported.Output, assembly: file); continue; }
                    a.UpdatedPaths = changed.Where(p=>dependencies.Contains(p)&&!string.Equals(p,file,StringComparison.OrdinalIgnoreCase)).ToList(); a.Selected = true;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (duplicateNames.Contains(name)) name += "__" + Signature(new Dictionary<string,string> { [file] = "" }).Substring(0, 8);
                    Jobs.Add(new BatchExportJob { Assembly = a, Target = Path.Combine(DateFolder, name + ".nwc") });
                }
                catch (Exception ex) { Log("周更 NWC", file, "跳过，待处理", ex.Message, assembly: file); }
            }
            Completed = Jobs.Count == 0;
            State = Jobs.Count == 0 ? "完成，无可导出项（详见逐项日志）" : "等待原生 NWC 导出"; Save();
        }
        internal static string Signature(IDictionary<string,string> hashes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", hashes.OrderBy(p => p.Key,StringComparer.OrdinalIgnoreCase).Select(p => p.Key.ToUpperInvariant()+"|"+p.Value))))).Replace("-", "");
        }
        internal void Finish(List<BatchExportJob> jobs)
        {
            foreach (var job in jobs)
            {
                if (job.Assembly.ExportState == "已导出" && Files.Hash(job.Target) == job.Assembly.ExportHash && !string.IsNullOrEmpty(job.Assembly.ExportHash))
                    Ledger.Exports[job.Assembly.Path] = new WeeklyExportReceipt { Signature = Signatures[job.Assembly.Path], Output = job.Target, OutputHash = job.Assembly.ExportHash };
                Log("周更 NWC", job.Assembly.Path, job.Assembly.ExportState, "输出=" + job.Target, after: job.Assembly.ExportHash, assembly: job.Assembly.Path);
            }
            AtomicJson(LedgerPath, Ledger);
            Completed = true; State = "周更结束，详见逐项日志"; Save();
        }
    }
}
