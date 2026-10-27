using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    internal sealed class WeeklyFakeDrawing
    {
        public string Geometry { get; set; }
        public List<RefEdge> Edges { get; set; } = new List<RefEdge>();
    }
    internal sealed class WeeklyFakeReader : IWeeklyDwg
    {
        internal Action OnStage = null;
        internal List<ReferencePlacement> Added = new List<ReferencePlacement>();
        internal static void Write(string path,string geometry,params RefEdge[] edges)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,"AC1032"+JsonConvert.SerializeObject(new WeeklyFakeDrawing{Geometry=geometry,Edges=edges.ToList()}),new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, WorkflowTestProgram.FixtureDate(File.ReadAllText(path, Encoding.UTF8)));
        }
        internal static WeeklyFakeDrawing Load(string path) => JsonConvert.DeserializeObject<WeeklyFakeDrawing>(File.ReadAllText(path,Encoding.UTF8).Substring(6));
        public List<RefEdge> Read(string path) => Load(path).Edges;
        public string Stage(string source,string owner,List<WeeklyRewrite> changes)
        {
            OnStage?.Invoke();var drawing=Load(source);
            foreach(var change in changes) drawing.Edges.Single(e=>e.Name==change.Name).Saved=Uri.UnescapeDataString(new Uri(Path.GetDirectoryName(owner)+"\\").MakeRelativeUri(new Uri(change.Target)).ToString()).Replace('/','\\');
            var staged=owner+".stage-"+Guid.NewGuid().ToString("N")+".dwg";
            Write(staged,drawing.Geometry,drawing.Edges.ToArray());return staged;
        }
        public string StageAttachments(string owner,List<ReferencePlacement> placements)
        {
            Added.AddRange(placements);var drawing=Load(owner);
            drawing.Edges.AddRange(placements.Select(p=>new RefEdge{Name=Path.GetFileNameWithoutExtension(p.Source),Saved=p.Source}));
            var staged=owner+".attach-"+Guid.NewGuid().ToString("N")+".dwg";Write(staged,drawing.Geometry,drawing.Edges.ToArray());return staged;
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static void VerifyWeekly(string evidence)
        {
            var root=Path.Combine(evidence,"weekly");var local=Path.Combine(root,"Local");var cloud=Path.Combine(root,"Connector");var assemblies=Path.Combine(local,"Assemblies");
            var storage=Path.Combine(root,"State");var output=Path.Combine(root,"NWC");Directory.CreateDirectory(assemblies);Directory.CreateDirectory(cloud);
            var cloudSource=Path.Combine(cloud,"114101-SIF-M3-M-9422_A","Source.dwg");var cloudGrid=Path.Combine(cloud,"114101-SIF-M3-M-9422_A","Grid.dwg");
            var localSource=Paths.MirrorTarget(local,cloud,cloudSource);var localGrid=Paths.MirrorTarget(local,cloud,cloudGrid);
            var assembly=Path.Combine(assemblies,"114101-SIF-M3-M-9422_A.dwg");
            var sourceEdge=new RefEdge{Name="Grid",Saved=cloudGrid,Overlay=true,Unloaded=false};
            WeeklyFakeReader.Write(cloudGrid,"grid");WeeklyFakeReader.Write(cloudSource,"source-v1",sourceEdge);
            WeeklyFakeReader.Write(localSource,"source-v1",sourceEdge);
            WeeklyFakeReader.Write(assembly,"assembly",new RefEdge{Name="Source",Saved=cloudSource});
            var config=new Settings{LocalModelsRoot=local,ConnectorRoot=cloud,ProjectScanFolder=assemblies};
            int runId=0;WeeklyUpdate Make(bool dry=false)=>new WeeklyUpdate(new WeeklyRequest{Storage=storage,OutputRoot=output,RunDirectory=Path.Combine(root,"Runs","run-"+(++runId)),DryRun=dry},config,new WeeklyFakeReader(),()=>new DateTimeOffset(2026,10,22,16,1,0,TimeSpan.Zero));
            var cloudBefore=Files.Hash(cloudSource);var original=Files.Hash(assembly);
            var first=Make();first.Prepare();
            Check(first.DateFolder.EndsWith("2026-10-23"),"周更以新加坡日期而非UTC日期建立文件夹");
            Check(File.Exists(localGrid)&&Files.Hash(localGrid)==Files.Hash(cloudGrid),"周更自动补齐云端可精确对应的缺失嵌套文件");
            Check(first.Jobs.Count==1&&first.Jobs[0].Target.StartsWith(first.DateFolder),"首次无成功导出基准时规划有效总装日期目录NWC");
            Check(first.Jobs.All(j=>!j.CurrentLocal),"手动导出简化不改变每周自动更新任务的严格快照校验");
            Check(!first.Jobs[0].Assembly.UpdatedPaths.Contains(assembly),"总装自身的路径修复不误列为Xref更新并触发导出关系拦截");
            Check(Files.Hash(cloudSource)==cloudBefore&&Files.Hash(localSource)!=cloudBefore,"路径本地化只修改本地，原始云端文件不变");
            Check(first.Rows.Any(r=>r.Local==localSource&&r.Backup!=null&&File.Exists(r.Backup)),"周更仅本地路径修复仍备份，不受云端覆盖免备份规则影响");
            Check(Directory.GetFiles(assemblies,"*.bak").Length==1&&Files.Hash(Directory.GetFiles(assemblies,"*.bak").Single())==original,"周更保存总装原图可恢复备份");
            var localized=WeeklyFakeReader.Load(localSource).Edges.Single();
            Check(localized.Overlay&&!localized.Unloaded&&Paths.Canonical(Paths.Resolve(localSource,localized.Saved))==localGrid,"周更本地化保持Overlay与加载状态并保存真实本地路径");
            var saved=Files.Hash(localSource);var second=Make();second.Prepare();
            Check(Files.Hash(localSource)==saved,"云端不变时复用已本地化收据，不反复下载重存");
            Check(second.Jobs.Count==1,"未成功导出的总装在下一轮仍会重试，不依赖再次Pull");
            foreach(var job in second.Jobs){Directory.CreateDirectory(Path.GetDirectoryName(job.Target));File.WriteAllText(job.Target,new string('N',80),Encoding.UTF8);job.Assembly.ExportState="已导出";job.Assembly.ExportHash=Files.Hash(job.Target);}
            second.Finish(second.Jobs);var third=Make();third.Prepare();
            Check(third.Jobs.Count==0,"已成功导出且依赖无变化的总装不会重复导出");
            File.Move(second.Jobs[0].Target,second.Jobs[0].Target+".kept");var missing=Make();missing.Prepare();
            Check(missing.Jobs.Count==1,"旧NWC丢失时即使源无变化也重新规划导出");
            WeeklyFakeReader.Write(cloudSource,"source-v2",sourceEdge);var updated=Make();updated.Prepare();
            Check(WeeklyFakeReader.Load(localSource).Geometry=="source-v2"&&updated.Jobs.Count==1,"周更收据允许云端新版本替换已知本地化版本并重导总装");
            Check(updated.Rows.Any(r=>r.Local==localSource&&r.State.Contains("完成")&&r.Backup==null),"周更云端版本覆盖本地源不产生备份");
            Check(File.GetLastWriteTimeUtc(localSource)==File.GetLastWriteTimeUtc(cloudSource),"周更路径本地化后仍以Connector源修改时间落盘");
            Check(updated.Rows.Any(r=>r.Local==localSource&&r.SourceModifiedTime==UpdateReports.FileTime(File.GetLastWriteTimeUtc(cloudSource))&&r.LocalModifiedTime==r.SourceModifiedTime),"周更表格分别记录操作时间与两侧文件修改时间");
            WeeklyFakeReader.Write(localSource,"user-local-edit",WeeklyFakeReader.Load(localSource).Edges.ToArray());var edited=Files.Hash(localSource);
            WeeklyFakeReader.Write(cloudSource,"source-v3",sourceEdge);var conflict=Make();conflict.Prepare();
            Check(Files.Hash(localSource)!=edited&&conflict.Jobs.Count==1&&WeeklyFakeReader.Load(localSource).Geometry=="source-v3","周更日期不同以云端为准覆盖本地源，不备份");
            Check(conflict.Rows.Any(r=>r.Local==localSource&&r.State.Contains("完成")&&r.Backup==null&&r.Details.Contains("Connector 源修改日期")),"免备份覆盖仍记录来源日期和实际执行日志");
            edited=Files.Hash(localSource);
            var ledgerBefore=Files.Hash(Path.Combine(storage,"weekly-ledger.json"));var dry=Make(true);dry.Prepare();
            Check(Files.Hash(localSource)==edited&&Files.Hash(Path.Combine(storage,"weekly-ledger.json"))==ledgerBefore,"只读预检不修改用户DWG和周更账本");
            Check(dry.Completed&&dry.Jobs.Count==0,"只读预检不启动原生导出");
            var unknown=Path.Combine(cloud,"114101-SIF-M3-M-9422_A","Unattached.dwg");WeeklyFakeReader.Write(unknown,"new");
            var newRun=Make();newRun.Prepare();
            Check(((WeeklyFakeReader)newRun.Reader).Added.Any(p=>p.Source.EndsWith("Unattached.dwg")&&p.X==6653112.1&&p.Y==29134629&&p.Scale==1&&p.Rotation==-4.42),"新增文件唯一总装时按已确认点比例角度挂接");
            var b1=Path.Combine(assemblies,"114101-SIF-M3-M-9426_A.dwg");var b2=Path.Combine(assemblies,"114101-SIF-M3-MLP-9426_A.dwg");
            WeeklyFakeReader.Write(b1,"a");WeeklyFakeReader.Write(b2,"b");var uncertain=Path.Combine(cloud,"114101-SIF-M3-M-9426_A","New.dwg");WeeklyFakeReader.Write(uncertain,"new");
            var ambiguity=Make();ambiguity.Prepare();
            Check(!File.Exists(Paths.MirrorTarget(local,cloud,uncertain))&&ambiguity.Rows.Any(r=>(r.Details??"").Contains("唯一目标数=2")),"新增文件有M与MLP两个目标时不猜测挂接或静默下载");
            var broken=Path.Combine(assemblies,"Broken.dwg");WeeklyFakeReader.Write(broken,"bad",new RefEdge{Name="Gone",Saved=Path.Combine(local,"Missing.dwg")});
            var brokenRun=Make();brokenRun.Prepare();
            Check(!brokenRun.Jobs.Any(j=>j.Assembly.Path==broken)&&brokenRun.Rows.Any(r=>r.Local==broken&&(r.State??"").Contains("跳过")),"引用不完整的总装逐项跳过，不阻止其他安全总装处理");
            Check(Directory.GetFiles(Path.Combine(storage,"Logs","Weekly"),"*.json").Length>=runId,"每次周更都有独立可表格查看的日志");
            Check(WeeklyUpdate.Signature(new Dictionary<string,string>{{"b","2"},{"a","1"}})==WeeklyUpdate.Signature(new Dictionary<string,string>{{"A","1"},{"B","2"}}),"导出依赖签名不因顺序和Windows路径大小写改变");
            var narrow=Path.Combine(root,"Scoped");var narrowLocal=Path.Combine(narrow,"Local");var narrowCloud=Path.Combine(narrow,"Connector");var narrowAsm=Path.Combine(narrowLocal,"Assemblies");Directory.CreateDirectory(narrowAsm);
            var src24=Path.Combine(narrowCloud,"114101-SIF-M3-M-9424_A","Pipe.dwg");var src25=Path.Combine(narrowCloud,"114101-SIF-M3-M-9425_A","Other.dwg");
            WeeklyFakeReader.Write(src24,"9424");WeeklyFakeReader.Write(src25,"9425-new");var mirror24=Paths.MirrorTarget(narrowLocal,narrowCloud,src24);var mirror25=Paths.MirrorTarget(narrowLocal,narrowCloud,src25);
            WeeklyFakeReader.Write(mirror24,"9424");WeeklyFakeReader.Write(mirror25,"9425-local");
            var model24=Path.Combine(narrowAsm,"114101-SIF-M3-M-9424_A.dwg");var model24b=Path.Combine(narrowAsm,"114101-SIF-M3-MLP-9424_A.dwg");var model25=Path.Combine(narrowAsm,"114101-SIF-M3-M-9425_A.dwg");
            WeeklyFakeReader.Write(model24,"M",new RefEdge{Name="Pipe",Saved=src24});WeeklyFakeReader.Write(model24b,"MLP",new RefEdge{Name="Pipe",Saved=src24});WeeklyFakeReader.Write(model25,"Other",new RefEdge{Name="Other",Saved=src25});
            var otherBefore=Files.Hash(mirror25);var otherAssemblyBefore=Files.Hash(model25);
            var narrowConfig=new Settings{LocalModelsRoot=narrowLocal,ConnectorRoot=narrowCloud,ProjectScanFolder=narrowAsm};
            int narrowId=0;WeeklyUpdate Narrow(bool force)=>new WeeklyUpdate(new WeeklyRequest{Storage=Path.Combine(narrow,"State"),OutputRoot=Path.Combine(narrow,"Out"),RunDirectory=Path.Combine(narrow,"Runs","run-"+(++narrowId)),AssemblyFiles=new[]{model24,model24b},SystemCodes=new[]{"9424"},ForceExport=force},narrowConfig,new WeeklyFakeReader());
            var scoped=Narrow(false);scoped.Prepare();
            Check(scoped.Jobs.Count==2&&scoped.Session.Assemblies.All(a=>a.Name.Contains("9424")),"指定9424仅处理对应M和MLP总装，不扩展整个项目");
            Check(Files.Hash(mirror25)==otherBefore&&Files.Hash(model25)==otherAssemblyBefore&&!scoped.Nodes.ContainsKey(mirror25),"限定系统运行不会Pull或改写9425源及总装");
            Check(scoped.Rows.Any(r=>r.Local==mirror24&&(r.Assembly??"").Contains(model24)&&(r.Assembly??"").Contains(model24b)),"逐Xref记录关联的两份完整总装路径");
            foreach(var job in scoped.Jobs){Directory.CreateDirectory(Path.GetDirectoryName(job.Target));File.WriteAllText(job.Target,new string('S',80),Encoding.UTF8);job.Assembly.ExportState="已导出";job.Assembly.ExportHash=Files.Hash(job.Target);}scoped.Finish(scoped.Jobs);
            var skip=Narrow(false);skip.Prepare();Check(skip.Jobs.Count==0,"限定系统仍遵守无变化增量跳过");
            var forced=Narrow(true);forced.Prepare();Check(forced.Jobs.Count==2,"用户明确要求再导出一次时ForceExport只重导选定系统，不要求再次改写源");
            Paths.ConfigureAliases(new Dictionary<string,string>());
        }
    }
}
