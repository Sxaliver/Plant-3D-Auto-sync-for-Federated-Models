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

namespace IWMF.PlantModelSync
{
    internal sealed partial class NwcTimestampCompareForm
    {
        internal async Task VerifyTimestampUi(string l, string c, string evidence)
        {
            local.Text = l; connector.Text = c; await RefreshDates();
            WorkflowTestProgram.Check(table.Grid.Columns.Cast<DataGridViewColumn>().Count(column => column.Visible) == 5 && !table.Grid.Columns["Local"].Visible && !table.Grid.Columns["Connector"].Visible, "日期页默认仅显示文件、两侧时间及状态，完整路径保留于详情");
            WorkflowTestProgram.Check(rows.Count > 0 && table.Grid.Columns["Selected"].HeaderCell == selectionHeader, "NWC日期检查独立浏览两侧目录并使用表头勾选框");
            table.Search.Text = "Newer.nwc"; selectionHeader.ToggleVisible();
            WorkflowTestProgram.Check(rows.Single(r => r.Name == "Newer.nwc").Selected && rows.Count(r => r.Selected) == 1, "日期表头仅勾选当前可见的建议上传项");
            table.Search.Text = "Equal.nwc"; selectionHeader.ToggleVisible();
            WorkflowTestProgram.Check(rows.Single(r => r.Name == "Newer.nwc").Selected && !rows.Single(r => r.Name == "Equal.nwc").Selected, "表头不勾选日期相同项且保留筛选隐藏勾选");
            table.Search.Text = "Newer.nwc"; selectionHeader.ToggleVisible();
            WorkflowTestProgram.Check(rows.All(r => !r.Selected), "表头第二次点击清除当前可见的勾选");
            table.Search.Clear(); Refresh();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "nwc-dates-only.png"), ImageFormat.Png); }
            Size = MinimumSize; PerformLayout(); Refresh();
            WorkflowTestProgram.Check(table.Grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width) <= table.Grid.ClientSize.Width, "日期页最小窗口不需要横向滚动即可比较文件与两侧时间");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(evidence, "compact-nwc-minimum.png"), ImageFormat.Png); }
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyNwcTimestamps(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "date-only-fixtures"); var l = Path.Combine(root, "LocalNwc"); var c = Path.Combine(root, "ConnectorNwc");
            var date = new DateTime(2026, 10, 23, 2, 3, 4, 123, DateTimeKind.Utc);
            string Nwc(string folder, string name, DateTime time, string content = "不是NWC，仅为日期比较样例") { var file = Path.GetFullPath(Path.Combine(folder, name)); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, content, new UTF8Encoding(false)); File.SetLastWriteTimeUtc(file, time); return file; }
            Nwc(l, "Newer.nwc", date.AddSeconds(2)); Nwc(c, "Newer.nwc", date);
            Nwc(l, "Equal.nwc", date, "甲"); Nwc(c, "Equal.nwc", date, "内容不同的乙");
            Nwc(l, "Older.nwc", date); Nwc(c, "Older.nwc", date.AddSeconds(3));
            Nwc(l, "LocalOnly.nwc", date); Nwc(c, "CloudOnly.nwc", date);
            List<NwcTimestampRow> Read() => NwcTimestampCompare.Check(l, c, CancellationToken.None);
            var first = Read();
            Check(first.Single(r => r.Name == "Newer.nwc").State == "本地较新，建议上传", "按修改日期准确标记本地较新的NWC");
            Check(first.Single(r => r.Name == "Older.nwc").State == "云端较新", "按修改日期区分云端较新的NWC，不倒置上传建议");
            Check(first.Single(r => r.Name == "Equal.nwc").State == "日期相同（未比较内容）", "即使内容不同也只按日期显示，并明确不宣称内容相同");
            Check(first.Single(r => r.Name == "LocalOnly.nwc").RecommendUpload && first.Single(r => r.Name == "CloudOnly.nwc").State.StartsWith("本地缺失"), "NWC日期比较区分两侧缺失及建议上传，不触发导出");
            Check(first.Single(r => r.Name == "Newer.nwc").LocalDate.StartsWith("2026-10-23 10:03:06.123 +08:00"), "日期以新加坡时区和毫秒展示，比较仍使用UTC");
            var old = Nwc(l, "2026-10-20/History.nwc", date); var latest = Nwc(l, "2026-10-22/History.nwc", date.AddMinutes(1)); Nwc(c, "History.nwc", date);
            var history = Read().Single(r => r.Name == "History.nwc");
            Check(history.Local == latest && history.Details.Contains(old) && history.State.StartsWith("本地较新"), "日期批次目录同名NWC取修改时间最新一份并列出历史候选");
            Nwc(l, "A/Duplicate.nwc", date); Nwc(l, "B/Duplicate.nwc", date); Nwc(c, "Duplicate.nwc", date);
            Check(Read().Where(r => r.Local != null && r.Name == "Duplicate.nwc").All(r => r.State.Contains("歧义") && !r.RecommendUpload), "非日期目录重复同名不能擅自按日期配对");
            Nwc(l, "A/Exact.nwc", date.AddSeconds(1)); Nwc(l, "B/Exact.nwc", date); Nwc(c, "A/Exact.nwc", date); Nwc(c, "B/Exact.nwc", date);
            Check(Read().Where(r => r.Name == "Exact.nwc").Count() == 2 && Read().Where(r => r.Name == "Exact.nwc").All(r => !r.State.Contains("歧义")), "重复同名仍可按两侧精确相对子路径区分");
            Reject(() => NwcTimestampCompare.Check(l, l, CancellationToken.None), "日期比较拒绝同一路径自比");
            Reject(() => NwcTimestampCompare.Check(l, Path.Combine(l, "A"), CancellationToken.None), "日期比较拒绝互相包含的目录");
            Reject(() => NwcTimestampCompare.Check(l, Path.Combine(root, "不存在"), CancellationToken.None), "NWC云端目录未指定或不存在时明确提示，不回退DWG目录");
            using (var form = new NwcTimestampCompareForm("", "")) { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.Show(); await form.VerifyTimestampUi(l, c, evidence); form.Close(); }

            var models = Path.Combine(root, "Models"); var a = Path.Combine(models, "单系统总装文件", "114101-SIF-M3-M-9424_System.dwg"); var x = Path.Combine(models, "Part.dwg"); var outside = Path.Combine(c, "外部引用.dwg");
            Dwg(a, "fixture"); Dwg(x, "fixture"); Dwg(outside, "outside");
            DwgReader.Graph[a] = new List<RefEdge> { new RefEdge { Name = "Part", Saved = x, Resolved = x } };
            var reads = DwgReader.Reads.TryGetValue(outside, out var oldReads) ? oldReads : 0;
            var localRows = LocalAssemblyCheck.Check(models, new DwgReader(), CancellationToken.None);
            Check(localRows.Count == 17 && localRows.Single(r => r.Local == a).State == "本地引用检查通过" && localRows.All(r => r.Connector == null), "本地DWG检查无需Connector配置，仅检查固定17项本地文件");
            DwgReader.Graph[a].Add(new RefEdge { Name = "外部引用", Saved = outside, Resolved = outside });
            localRows = LocalAssemblyCheck.Check(models, new DwgReader(), CancellationToken.None);
            Check(localRows.Single(r => r.Local == a).Details.Contains("本地模型根目录外") && (!DwgReader.Reads.TryGetValue(outside, out var newReads) || newReads == reads), "本地检查识别外部引用但不展开Connector路径");
            Check(new Settings().ConnectorNwcRoot == "" && new Settings().NwcCompareLocalRoot == "", "NWC比较目录独立存储，默认不把DWG云端路径当NWC目录");
            VerifySelectionHeader(evidence);
        }
        private static void VerifySelectionHeader(string evidence)
        {
            var rows = new List<NwcTimestampRow> { new NwcTimestampRow { Name = "A", RecommendUpload = true }, new NwcTimestampRow { Name = "B", RecommendUpload = true }, new NwcTimestampRow { Name = "Blocked", RecommendUpload = false } };
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false }; grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", DataPropertyName = "Selected", Width = 55 }); grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", Width = 250 });
            var header = new VisibleSelectionHeader<NwcTimestampRow>(r => r.RecommendUpload, r => r.Selected, (r, v) => r.Selected = v); grid.Columns[0].HeaderCell = header;
            var table = new IWMF.ColumnAutoModel.ReviewTable<NwcTimestampRow>(grid, r => r.Name, r => "全部");
            using (var form = new Form { Width = 700, Height = 450, StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) })
            {
                form.Controls.Add(table); table.SetRows(rows); form.Show(); header.ToggleVisible();
                Check(rows.Take(2).All(r => r.Selected) && !rows[2].Selected && header.VisibleState == CheckState.Checked, "表头全选仅选择当前可操作行");
                table.Search.Text = "A"; header.ToggleVisible();
                Check(!rows[0].Selected && rows[1].Selected, "表头清除只影响当前可见行，不清除隐藏行");
                table.Search.Clear(); Check(header.VisibleState == CheckState.Indeterminate, "部分勾选时表头显示混合状态");
                header.ToggleVisible(); Check(rows[0].Selected && rows[1].Selected, "混合状态点击表头补齐当前可操作项");
                header.ToggleVisible(); Check(rows.All(r => !r.Selected), "全选状态点击表头清除可见项");
                table.Search.Text = "no-match"; header.ToggleVisible(); Check(rows.All(r => !r.Selected), "空筛选结果的表头不会修改任何勾选");
                table.Search.Clear(); grid.Enabled = false; header.ToggleVisible(); Check(rows.All(r => !r.Selected), "任务执行时禁用表格也禁用表头操作"); grid.Enabled = true;
                form.Refresh(); using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(evidence, "table-header-checkbox.png"), ImageFormat.Png); } form.Close();
            }
        }
    }
}
