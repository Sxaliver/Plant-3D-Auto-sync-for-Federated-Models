using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IWMF.PlantModelSync
{
    internal static partial class WorkflowTestProgram
    {
        private static void VerifyLocalRepair(string evidence)
        {
            var area = Path.Combine(evidence, "local-repair"); var root = Path.Combine(area, "Local");
            var cloud = Path.Combine(area, "NewDC", "ACCDocs", "Tenant", "Project", "Project Files", "Models");
            var legacy = Path.Combine(area, "OldDC", "ACCDocs", "Tenant", "Project", "Project Files", "Models");
            var assembly = Path.Combine(root, "Assemblies", "M-9422.dwg");
            var source = Path.Combine(root, "local_System", "SERVICE AIR.dwg");
            var child = Path.Combine(root, "local_System", "Floor.dwg");
            var grand = Path.Combine(root, "local_System", "Grid.dwg");
            var fake = new WeeklyFakeReader();
            RefEdge Edge(string name, string path, bool unloaded = false) => new RefEdge { Name = name, Saved = path, Unloaded = unloaded, Overlay = true };
            void Reset()
            {
                fake.OnStage = null;
                WeeklyFakeReader.Write(assembly, "assembly"); WeeklyFakeReader.Write(source, "source geometry", Edge("Floor", Path.Combine(legacy, "System", "Floor.dwg")));
                WeeklyFakeReader.Write(child, "floor geometry", Edge("Grid", Path.Combine(root, "System", "Grid.dwg")));
                WeeklyFakeReader.Write(grand, "grid geometry");
            }
            LocalAttachmentPlan Plan() => LocalReferenceAttachment.Plan(root, assembly, source, new LocalReferenceRepair(root, cloud, fake));
            AttachmentResult Stage(string owner, List<ReferencePlacement> placements) => new AttachmentResult { Staged = fake.StageAttachments(owner, placements), Added = 1 };
            Reset(); var before = Files.Hash(source); var date = File.GetLastWriteTimeUtc(source); var plan = Plan();
            Check(plan.Repair.Changes.Count == 2 && Files.Hash(source) == before, "预检投影跨层旧 Connector / 旧本地路径，不提前改文件");
            var log = Path.Combine(area, "success.json");
            var result = LocalReferenceAttachment.Run(plan, log, p => { }, Stage, CancellationToken.None);
            Check(result.Added == 1 && File.Exists(result.Backup), "修复后挂接并备份目标总装");
            Check(plan.Repair.Reports.Count == 2 && plan.Repair.Reports.All(r => File.Exists(r.Backup) && r.BeforeHash == Files.Hash(r.Backup)), "每个修改源均保存原始哈希一致的备份");
            Check(WeeklyFakeReader.Load(source).Geometry == "source geometry" && fake.Read(source).Single().Overlay && !fake.Read(source).Single().Unloaded, "路径修复保留源图内容与引用类型状态");
            Check(!Path.IsPathRooted(fake.Read(source).Single().Saved) && Paths.Resolve(source, fake.Read(source).Single().Saved) == child, "源图保存为真实本地相对路径");
            Check(File.GetLastWriteTimeUtc(source) == date && Scanner.Scan(source, root, fake).Warnings.Count == 0, "保留修改日期并在真实保存结果复查通过");
            Check(UpdateReports.Read(log).Count == 3 && UpdateReports.Read(log).Any(r => r.Details.Contains("OldDC")), "日志含逐源备份、旧路径及新路径和挂接结果");
            Check(Plan().Repair.Changes.Count == 0, "已本地化文件再次预检无重复修改");

            Reset(); before = Files.Hash(source); plan = Plan(); fake.OnStage = () => { throw new IOException("模拟暂存失败"); };
            Reject(() => LocalReferenceAttachment.Run(plan, Path.Combine(area, "stage-failure.json"), p => { }, Stage, CancellationToken.None), "暂存失败阻止挂接");
            Check(Files.Hash(source) == before && plan.Repair.Reports.Count == 0, "暂存失败不修改任何原图");
            Reset(); plan = Plan(); var assemblyHash = Files.Hash(assembly);
            Reject(() => LocalReferenceAttachment.Run(plan, Path.Combine(area, "attach-failure.json"), p => { }, (p, refs) => { throw new IOException("模拟挂接失败"); }, CancellationToken.None), "挂接失败不伪报完成");
            Check(Files.Hash(assembly) == assemblyHash && UpdateReports.Read(Path.Combine(area, "attach-failure.json")).Count == 3, "挂接失败保留总装并记录已完成路径修复与备份");

            Reset(); plan = Plan(); before = Files.Hash(source);
            Reject(() => LocalReferenceAttachment.Run(plan, Path.Combine(area, "busy.json"), p => { if (p == child) throw new IOException("已打开"); }, Stage, CancellationToken.None), "任一待改嵌套源已打开时整批阻止");
            Check(Files.Hash(source) == before, "占用预检在所有源替换之前完成");
            Reset(); plan = Plan(); WeeklyFakeReader.Write(grand, "changed");
            Reject(() => LocalReferenceAttachment.Run(plan, Path.Combine(area, "race.json"), p => { }, Stage, CancellationToken.None), "确认后依赖内容变化阻止修复");

            Reset(); WeeklyFakeReader.Write(child, "cycle", Edge("Source", source));
            Reject(() => Plan(), "路径投影后的已加载循环仍阻止");
            Reset(); WeeklyFakeReader.Write(source, "wrong project", Edge("Floor", Path.Combine(legacy.Replace("Project Files", "Other Files"), "System", "Floor.dwg")));
            Reject(() => Plan(), "不同云端目录不以文件名猜测匹配");
            Reset(); WeeklyFakeReader.Write(source, "unknown", Edge("Unknown", Path.Combine(area, "Downloads", "Floor.dwg")));
            Reject(() => Plan(), "下载目录同名文件不自动映射");
            Reset(); var oldLocal = Path.Combine(root, "System", "Grid.dwg"); WeeklyFakeReader.Write(oldLocal, "different grid");
            Reject(() => Plan(), "旧目录和正式镜像均存在时阻止歧义"); File.Delete(oldLocal);
            Reset(); WeeklyFakeReader.Write(child, "unloaded", Edge("Unknown", Path.Combine(area, "absent.dwg"), true));
            plan = Plan(); Check(plan.Warnings.Count > 0, "未定位的卸载嵌套保留警告而非删除或强制加载"); plan.UnloadedWarningsConfirmed = true;
            LocalReferenceAttachment.Run(plan, Path.Combine(area, "unloaded.json"), p => { }, Stage, CancellationToken.None);
            Check(fake.Read(child).Single().Unloaded && !File.Exists(Path.Combine(area, "absent.dwg")), "确认后仍保留原卸载状态与未知路径");
            Check(Scanner.Scan(source, root, fake).Warnings.Count > 0, "路径修复不绕过 NWC 完整扫描");
        }
    }
}
