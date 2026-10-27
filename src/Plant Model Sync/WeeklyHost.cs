using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

namespace IWMF.PlantModelSync
{
    internal sealed class WeeklyNativeDwg : IWeeklyDwg
    {
        public List<RefEdge> Read(string path) => new DwgReader().Read(path);
        public string StageAttachments(string owner,List<ReferencePlacement> placements) => Host.StageNewReferences(owner,placements).Staged;
        private static Database Open(string path)
        {
            var db = new Database(false,true);
            try { db.ReadDwgFile(path,FileOpenMode.OpenForReadAndAllShare,false,null); db.CloseInput(true); return db; }
            catch { db.Dispose(); throw; }
        }
        private static string Placements(Database db)
        {
            var values = new List<string>();
            using (var tr=db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach(var id in DwgReader.DirectIds(db))
                {
                    var b=(BlockTableRecord)tr.GetObject(id,OpenMode.ForRead);
                    values.Add(b.Name+"|"+b.IsUnloaded+"|"+b.IsFromOverlayReference);
                    foreach(ObjectId rid in b.GetBlockReferenceIds(true,false))
                    {
                        if (rid.Database != db) continue;
                        var r=(BlockReference)tr.GetObject(rid,OpenMode.ForRead);
                        var owner=(BlockTableRecord)tr.GetObject(r.OwnerId,OpenMode.ForRead);
                        if (owner.IsFromExternalReference || owner.IsDependent) continue;
                        values.Add(b.Name+"|"+r.Handle+"|"+r.BlockTransform+"|"+r.Normal+"|"+r.Layer);
                    }
                }
                tr.Commit();
            }
            return string.Join("\n",values.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal));
        }
        public string Stage(string source,string owner,List<WeeklyRewrite> rewrites)
        {
            var staged=Path.Combine(Path.GetDirectoryName(owner),".pms-weekly-"+Guid.NewGuid().ToString("N")+".dwg");
            try
            {
                if(rewrites.Count==0) { File.Copy(source,staged,false); Files.CheckDwg(staged); return staged; }
                string placements;
                using(var db=Open(source))
                {
                    placements=Placements(db); var direct=DwgReader.DirectIds(db);
                    using(var tr=db.TransactionManager.StartTransaction())
                    {
                        var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
                        foreach(var change in rewrites)
                        {
                            if(!table.Has(change.Name)||!direct.Contains(table[change.Name]))throw new IOException("待修复引用不再是直接引用："+change.Name);
                            var b=(BlockTableRecord)tr.GetObject(table[change.Name],OpenMode.ForWrite);
                            var folder=new Uri(Path.GetDirectoryName(owner)+"\\");
                            b.PathName=Uri.UnescapeDataString(folder.MakeRelativeUri(new Uri(change.Target)).ToString()).Replace('/','\\');
                        }
                        tr.Commit();
                    }
                    db.SaveAs(staged,db.OriginalFileVersion);
                }
                using(var verify=Open(staged))
                {
                    if(Placements(verify)!=placements)throw new IOException("暂存图引用插入变换、类型或加载状态变化，停止替换。");
                    using(var tr=verify.TransactionManager.StartOpenCloseTransaction())
                    {
                        var table=(BlockTable)tr.GetObject(verify.BlockTableId,OpenMode.ForRead);
                        foreach(var change in rewrites)
                        {
                            var b=(BlockTableRecord)tr.GetObject(table[change.Name],OpenMode.ForRead);
                            if(!string.Equals(Paths.Canonical(Paths.Resolve(owner,b.PathName)),change.Target,StringComparison.OrdinalIgnoreCase))throw new IOException("暂存图保存路径校验失败。");
                        }
                        tr.Commit();
                    }
                }
                return staged;
            }
            catch { if(File.Exists(staged))File.Delete(staged); throw; }
        }
    }
    public sealed partial class Commands
    {
        private static Document weeklyOrigin;
        [CommandMethod("PMSWEEKLYRUN",CommandFlags.Session)]
        public void RunWeekly()
        {
            WeeklyUpdate run=null; WeeklyRequest request=null;
            try
            {
                var file=Environment.GetEnvironmentVariable("IWMF_PMS_WEEKLY_REQUEST");
                if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new IOException("周更请求文件不存在；请使用 Run-Weekly.ps1。");
                request=JsonConvert.DeserializeObject<WeeklyRequest>(File.ReadAllText(file,Encoding.UTF8));
                if(request.ProcessId!=Process.GetCurrentProcess().Id)throw new IOException("周更只允许在启动器创建的独立进程运行，不能接管已有 Plant。");
                weeklyOrigin=AcApp.DocumentManager.MdiActiveDocument;
                AcApp.SetSystemVariable("LOGFILEPATH",request.RunDirectory);
                AcApp.SetSystemVariable("LOGFILEMODE",1);
                if(BatchJobs!=null)throw new IOException("批量导出仍在运行。");
                var config=JsonConvert.DeserializeObject<Settings>(File.ReadAllText(request.SettingsPath,Encoding.UTF8));
                run=new WeeklyUpdate(request,config,new WeeklyNativeDwg()); run.Prepare();
                if(run.Jobs.Count==0) { run.Save(); QuitWeekly(request); return; }
                var captured=run;
                BatchCompletion=jobs=>
                {
                    try { captured.Finish(jobs); }
                    catch(Exception ex) { captured.Completed=true; captured.Error=ex.ToString(); captured.State="周更日志 / 账本写入失败"; captured.Save(); }
                    finally { QuitWeekly(request); }
                };
                Host.QueueWorkflowExports(run.Session,run.Jobs);
            }
            catch(Exception ex)
            {
                BatchCompletion=null;
                if(run!=null){run.Completed=true;run.Error=ex.ToString();run.State="周更停止";run.Save();}
                else if(request!=null)WeeklyUpdate.AtomicJson(Path.Combine(request.RunDirectory,"result.json"),new{State="周更启动失败",Completed=true,Error=ex.ToString(),ProcessId=Process.GetCurrentProcess().Id});
                if(request!=null)QuitWeekly(request);
                else AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n周更启动失败："+ex.Message);
            }
        }
        private static void QuitWeekly(WeeklyRequest request)
        {
            // 只退出本次启动器所拥有的独立进程，永远不关闭用户已有进程。
            if(request.ProcessId!=Process.GetCurrentProcess().Id)return;
            var shutdown=Path.Combine(request.RunDirectory,"shutdown.json");
            if(AcApp.DocumentManager.Cast<Document>().Any(d=>d!=weeklyOrigin))
            { WeeklyUpdate.AtomicJson(shutdown,new{SafeToTerminate=false,ProcessId=request.ProcessId,Reason="发现非任务图纸，未关闭独立宿主。"}); return; }
            // 丢弃的只有启动器新建的空白模板，不触碰用户文档，也不弹保存提示。
            bool safe=weeklyOrigin==null || !File.Exists(weeklyOrigin.Name);
            WeeklyUpdate.AtomicJson(shutdown,new{SafeToTerminate=safe,ProcessId=request.ProcessId,Reason="任务已结束，宿主仅剩本次创建的空白模板。"});
            if(!safe)return;
            try { if(weeklyOrigin!=null)weeklyOrigin.CloseAndDiscard();weeklyOrigin=null;AcApp.Quit(); }
            catch(Exception ex) { WeeklyUpdate.AtomicJson(shutdown,new{SafeToTerminate=true,ProcessId=request.ProcessId,Reason="空白宿主关闭异常，交由启动器清理。",Error=ex.Message}); }
        }
    }
}
