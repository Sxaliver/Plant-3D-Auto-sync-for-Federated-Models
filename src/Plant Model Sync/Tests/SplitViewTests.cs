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
using Newtonsoft.Json;

namespace IWMF.PlantModelSync
{
    // 隔离界面回归用假宿主，不会打开真实用户 DWG 或触发云端同步。
    internal static class Host
    {
        internal static List<string> LocalFiles;
        public static List<ProjectDrawing> CurrentProjectDrawings() => new List<ProjectDrawing>();
        public static void CheckSourcesClosed(IEnumerable<XrefRow> rows) { }
        public static IDisposable ReleaseReferences(string assembly) => null;
    }
    internal sealed class ProjectDrawing
    {
        public string Part { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }
    internal sealed class DwgReader : IReferenceReader
    {
        public List<RefEdge> Read(string path) => Path.GetFileName(path) == "Assembly.dwg"
            ? Host.LocalFiles.Select(file => new RefEdge { Name = Path.GetFileNameWithoutExtension(file), Saved = file, Resolved = file }).ToList()
            : new List<RefEdge>();
    }
    internal sealed partial class MainForm
    {
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
        private static void Focus(DataGridView view, string property, string value)
        {
            foreach (DataGridViewRow row in view.Rows)
                if (string.Equals(Convert.ToString(row.Cells[property].Value), value, StringComparison.OrdinalIgnoreCase))
                { view.ClearSelection(); view.CurrentCell = row.Cells[property]; row.Selected = true; return; }
            throw new InvalidOperationException("未找到测试行：" + value);
        }
        internal async Task VerifySplitAsync(string evidence)
        {
            Scan(); await RefreshTargets(true);
            Check(rows.Count == 3 && connectorGrid.Rows.Count == 4, "未勾选时也展示完整 Connector 文件列表");
            var snapshots = Host.LocalFiles.Concat(connectorIndex.Values.SelectMany(x => x)).ToDictionary(p => p, Files.Hash);
            var duplicate = rows.Single(r => r.Name == "Shared_Grid.dwg"); duplicate.Selected = true;
            await RefreshTargets();
            Check(string.IsNullOrEmpty(duplicate.CloudPath), "多处同名没有确认时清空映射");
            Focus(grid, "Name", duplicate.Name);
            var chosen = connectorIndex[duplicate.Name].Single(p => p.Contains("Zone B"));
            Focus(connectorGrid, "RelativePath", Paths.Relative(connector.Text, chosen));
            MapSelectedConnector(); await RefreshTargets();
            Check(duplicate.Selected && duplicate.CloudPath == chosen, "右侧指定候选建立对应并保留勾选");
            table.Search.Text = "Pipe"; table.Search.Clear();
            connectorTable.Search.Text = "Zone A"; connectorTable.Search.Clear();
            grid.Sort(grid.Columns["Name"], System.ComponentModel.ListSortDirection.Descending);
            Check(duplicate.Selected && duplicate.CloudPath == chosen, "左右筛选及排序保持同步勾选与人工对应");
            var pipe = rows.Single(r => r.Name == "Pipe_System.dwg"); pipe.Selected = true; Count();
            await RefreshTargets(); Focus(grid, "Name", pipe.Name);
            await localDetails.ShowFileAsync(pipe.LocalPath, "对应同步文件：" + pipe.CloudPath);
            await connectorDetails.ShowFileAsync(pipe.CloudPath, "左侧 Xref：" + pipe.Name);
            Check(localDetails.DetailText.Contains(Files.Hash(pipe.LocalPath)) && connectorDetails.DetailText.Contains(Files.Hash(pipe.CloudPath)), "下方详情区显示各自文件内容哈希");
            Check(localDetails.DetailText.Contains("修改日期") && connectorDetails.DetailText.Contains("AC1032"), "点击详情显示修改日期与 DWG 格式");
            Check(FocusedConnector?.Path == pipe.CloudPath, "点击左侧定位右侧已对应文件");
            Check(snapshots.All(pair => Files.Hash(pair.Key) == pair.Value), "浏览点击与对应操作不改写源文件");
            var folder = FolderLink.Parse(connector.Text);
            var isolated = Path.Combine(evidence, "state"); Directory.CreateDirectory(isolated);
            var configFile = Path.Combine(isolated, "settings.json");
            var service = new SyncService(settings, () => File.WriteAllText(configFile, JsonConvert.SerializeObject(settings), new UTF8Encoding(false)), isolated);
            var plans = await service.Plan(folder, new[] { pipe }, true, CancellationToken.None, null);
            await service.Execute(folder, plans, true, CancellationToken.None, null);
            await RefreshTargets(true);
            Check(pipe.SyncState == SyncFeedback.Synced && Files.Hash(pipe.LocalPath) == Files.Hash(pipe.CloudPath), "真实本地 Push 校验成功后标记已同步");
            Check(connectorFiles.Single(file => file.Path == pipe.CloudPath).SyncState == SyncFeedback.Synced, "右侧对应文件同步显示已同步");
            var style = new DataGridViewCellStyle(); ApplySyncColor(style, pipe.SyncState);
            Check(style.BackColor.G > style.BackColor.R && style.SelectionBackColor.G > style.SelectionBackColor.R, "选中已同步行仍然显示绿色");
            var restored = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(configFile, Encoding.UTF8));
            var reopened = new XrefRow { LocalPath = pipe.LocalPath, CloudPath = pipe.CloudPath };
            SyncFeedback.Refresh(restored, folder, reopened, pipe.CloudPath);
            Check(reopened.SyncState == SyncFeedback.Synced, "重新加载校验记录仍保持已同步");
            File.AppendAllText(pipe.CloudPath, "-connector-edit", Encoding.UTF8);
            SyncFeedback.Refresh(restored, folder, reopened, pipe.CloudPath);
            Check(reopened.SyncState == SyncFeedback.Pending, "Connector 文件变化后取消绿色");
            var backupsBefore = Directory.GetFiles(Path.GetDirectoryName(pipe.LocalPath), "*.pms-*.bak").Length;
            plans = await service.Plan(folder, new[] { pipe }, false, CancellationToken.None, null);
            await service.Execute(folder, plans, false, CancellationToken.None, null);
            Check(pipe.SyncState == SyncFeedback.Synced && Directory.GetFiles(Path.GetDirectoryName(pipe.LocalPath), "*.pms-*.bak").Length == backupsBefore, "Pull完成不生成备份并显示已同步");
            restored = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(configFile, Encoding.UTF8));
            File.AppendAllText(pipe.LocalPath, "-local-edit", Encoding.UTF8);
            SyncFeedback.Refresh(restored, folder, reopened, pipe.CloudPath);
            Check(reopened.SyncState == SyncFeedback.Pending, "本地源文件变化后取消绿色");
            plans = await service.Plan(folder, new[] { pipe }, true, CancellationToken.None, null);
            await service.Execute(folder, plans, true, CancellationToken.None, null);
            var failedPlans = await service.Plan(folder, new[] { duplicate }, true, CancellationToken.None, null);
            File.AppendAllText(duplicate.CloudPath, "-change-after-preview", Encoding.UTF8);
            await service.Execute(folder, failedPlans, true, CancellationToken.None, null);
            await RefreshTargets(true);
            Check(duplicate.SyncState == SyncFeedback.Failed && connectorFiles.Single(file => file.Path == duplicate.CloudPath).SyncState == SyncFeedback.Failed, "预览后目标发生变化时两侧显示失败而非绿色");
            connectorTable.Search.Text = "未关联"; UpdateConnectorStates(); connectorTable.Search.Clear();
            Check(connectorFiles.Single(file => file.Path == pipe.CloudPath).SyncState == SyncFeedback.Synced, "筛选隐藏后重新显示仍保留绿色");
            Focus(grid, "Name", pipe.Name);
            await localDetails.ShowFileAsync(pipe.LocalPath, "同步状态：" + pipe.SyncState);
            await connectorDetails.ShowFileAsync(pipe.CloudPath, "同步状态：已同步（Connector 本地目录）");
            Refresh(); SaveBitmap(Path.Combine(evidence, "split-view.png"));
            Size = MinimumSize; PerformLayout(); Refresh(); SaveBitmap(Path.Combine(evidence, "split-view-minimum.png"));
            var split = Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<SplitContainer>()).Single();
            Check(split.Panel1.Width >= 390 && split.Panel2.Width >= 390, "最小窗口尺寸保留左右两栏");
        }
        private void SaveBitmap(string path)
        {
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, ImageFormat.Png); }
        }
    }
    internal static class SplitViewTestProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(evidence);
            var local = Path.Combine(evidence, "fixtures", "Local"); var cloud = Path.Combine(evidence, "fixtures", "Connector");
            Directory.CreateDirectory(local); Directory.CreateDirectory(cloud);
            void Dwg(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, Encoding.ASCII.GetBytes("AC1032" + content)); }
            var assembly = Path.Combine(local, "Assembly.dwg"); Dwg(assembly, "assembly-fixture");
            Host.LocalFiles = new[] { "Pipe_System.dwg", "Shared_Grid.dwg", "Equipment.dwg" }.Select(name => Path.Combine(local, name)).ToList();
            foreach (var file in Host.LocalFiles) Dwg(file, "local-fixture-" + Path.GetFileName(file));
            Dwg(Path.Combine(cloud, "Pipe_System.dwg"), "connector-fixture-pipe");
            Dwg(Path.Combine(cloud, "Zone A", "Shared_Grid.dwg"), "connector-fixture-a");
            Dwg(Path.Combine(cloud, "Zone B", "Shared_Grid.dwg"), "connector-fixture-b");
            Dwg(Path.Combine(cloud, "Zone B", "Unreferenced.dwg"), "connector-fixture-unreferenced");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            int exit = 0;
            using (var form = new MainForm(new Settings { Assembly = assembly, ConnectorRoot = cloud }, true))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-4000, -4000);
                form.Shown += async (s, e) =>
                {
                    try { await form.VerifySplitAsync(evidence); }
                    catch (Exception ex) { exit = 1; Console.Error.WriteLine(ex); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            return exit;
        }
    }
}
