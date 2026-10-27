using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IWMF.PlantModelSync
{
    internal sealed class UpdateReportRow
    {
        public string Time { get; set; }
        public string SourceModifiedTime { get; set; }
        public string LocalModifiedTime { get; set; }
        public string Operation { get; set; }
        public string File { get; set; }
        public string Local { get; set; }
        public string Connector { get; set; }
        public string Assembly { get; set; }
        public string State { get; set; }
        public string Backup { get; set; }
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public string Details { get; set; }
    }
    internal sealed class OneClickReport
    {
        public string Format { get; set; } = "IWMF.OneClickUpdate.v1";
        public string Path { get; set; }
        public string State { get; set; } = "准备执行";
        public string Error { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public List<string> ChildLogs { get; set; } = new List<string>();
        public List<UpdateReportRow> Rows { get; set; } = new List<UpdateReportRow>();
        public static OneClickReport Create(string storage)
        {
            var folder = System.IO.Path.Combine(storage, "Logs", "OneClickUpdates"); Directory.CreateDirectory(folder);
            var report = new OneClickReport { Path = System.IO.Path.Combine(folder, "one-click-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json") };
            report.Save(); return report;
        }
        public void Save()
        {
            var text = JsonConvert.SerializeObject(this, Formatting.Indented); var temporary = Path + ".tmp";
            System.IO.File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (System.IO.File.Exists(Path)) System.IO.File.Replace(temporary, Path, null); else System.IO.File.Move(temporary, Path);
        }
    }
    internal static class UpdateReports
    {
        internal static string FileTime(DateTime? utc) => utc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz") : "";
        private static string Value(JToken token, params string[] names)
        {
            foreach (var name in names) { var v = token[name]; if (v != null && v.Type != JTokenType.Null) return v is JArray array ? string.Join(" | ", array.Select(x => x.ToString())) : v.ToString(); }
            return "";
        }
        public static List<UpdateReportRow> Read(string path)
        {
            path = System.IO.Path.ChangeExtension(path, ".json");
            var root = JToken.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8));
            if (root is JObject o && (string)o["Format"] == "IWMF.OneClickUpdate.v1") return o["Rows"]?.ToObject<List<UpdateReportRow>>() ?? new List<UpdateReportRow>();
            var rows = new List<UpdateReportRow>();
            var time = root is JObject ? Value(root, "recordedUtc", "startedUtc") : "";
            if (root is JArray nwc)
                foreach (var t in nwc) rows.Add(new UpdateReportRow { Time = time, Operation = (bool?)t["manualSelection"] == true ? "手动 NWC 导出" : "NWC 导出", File = System.IO.Path.GetFileName(Value(t, "target")), Local = Value(t, "target"), Assembly = Value(t, "assembly"), State = Value(t, "result"), AfterHash = Value(t, "outputHash"), Details = ((bool?)t["currentLocal"] == true ? "当前本地模型；不检查同步，不承诺引用完整性；" : (bool?)t["manualSelection"] == true ? "手动选择本地快照，不代表本轮更新；" : "") + Value(t, "updatedXrefs") });
            else
            {
                foreach (var t in root["files"] as JArray ?? new JArray())
                {
                    var local = Value(t, "local", "Local"); var verified = Value(t, "verified");
                    rows.Add(new UpdateReportRow { Time = time, Operation = root["direction"] != null ? Value(root, "direction") : "新增源下载", File = System.IO.Path.GetFileName(local), Local = local, Connector = Value(t, "connector", "Cloud"), Assembly = Value(t, "affectedAssemblies", "Targets"), State = Value(t, "result", "State") + (verified.Length == 0 ? "" : "；校验=" + verified), Backup = Value(t, "backupPath"), BeforeHash = Value(t, "beforeLocalHash"), AfterHash = Value(t, "expectedAfterHash", "hash"), Details = Value(t, "match") + " " + Value(t, "placement") });
                    rows[rows.Count - 1].SourceModifiedTime = FileTime(t["connectorModifiedUtc"]?.ToObject<DateTime?>());
                    rows[rows.Count - 1].LocalModifiedTime = FileTime(t["afterLocalModifiedUtc"]?.ToObject<DateTime?>() ?? t["localModifiedUtc"]?.ToObject<DateTime?>());
                }
                foreach (var t in root["assemblies"] as JArray ?? new JArray())
                {
                    var local = Value(t, "Assembly");
                    rows.Add(new UpdateReportRow { Time = time, Operation = "新增 Xref", File = System.IO.Path.GetFileName(local), Local = local, Assembly = local, State = Value(t, "State"), Backup = Value(t, "Backup"), BeforeHash = Value(t, "BeforeHash"), AfterHash = Value(t, "AfterHash"), Details = Value(t, "Sources") });
                }
            }
            if (rows.Count == 0) rows.Add(new UpdateReportRow { Time = time, Operation = "批次", State = root is JObject ? Value(root, "state") : "无明细", Details = root is JObject ? Value(root, "error") : "" });
            return rows;
        }
        public static UpdateReportRow Transfer(TransferPlan plan, string operation)
        {
            return new UpdateReportRow { Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz"), SourceModifiedTime = FileTime(plan.ConnectorModifiedUtc), LocalModifiedTime = FileTime(plan.AfterLocalModifiedUtc ?? plan.LocalModifiedUtc), Operation = operation, File = System.IO.Path.GetFileName(plan.Local), Local = plan.Local, Connector = plan.ConnectorPath, Assembly = string.Join(" | ", plan.AffectedAssemblies), State = !plan.StartedUtc.HasValue ? "未执行：" + (plan.Blocked ?? "未勾选 / 已取消") : plan.Row.Result ?? plan.Blocked ?? "执行中", Backup = plan.BackupPath, BeforeHash = plan.LocalHash, AfterHash = plan.Verified ? (plan.Written ? plan.ConnectorHash : plan.LocalHash) : "", Details = "写入=" + plan.Written + "；校验=" + plan.Verified + "；" + plan.Row.MatchStatus + (plan.DateComparison ? "；按修改日期，云端为准；更新前本地 UTC=" + plan.LocalModifiedUtc?.ToString("o") + "；Connector 源 UTC=" + plan.ConnectorModifiedUtc?.ToString("o") + "；更新后本地 UTC=" + plan.AfterLocalModifiedUtc?.ToString("o") + "；操作时间不是文件修改时间" : "") };
        }
    }
}
