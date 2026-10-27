using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        internal void VerifyNewCloudFilter()
        {
            stateFilter.SelectedIndex = 3;
            WorkflowTestProgram.Check(!onlyExisting.Checked && sourcesGrid.Rows.Count > 0 && sourcesGrid.Rows.Cast<DataGridViewRow>().All(r => ((CloudUpdate)r.DataBoundItem).Plans.Count == 0), "云端新增筛选自动解除存在筛选并显示可下载的新文件");
        }
        internal async Task VerifyImportNewFlow(string name)
        {
            stateFilter.SelectedIndex = 3; var source = Session.Sources.Single(s => s.Name == name); source.Selected = true;
            using (var confirm = new System.Windows.Forms.Timer { Interval = 25 })
            {
                confirm.Tick += (s, e) => { var preview = Application.OpenForms.OfType<NewCloudFilesForm>().FirstOrDefault(); if (preview != null) { preview.DialogResult = DialogResult.OK; preview.Close(); } };
                confirm.Start(); await ImportNewFiles(); confirm.Stop();
            }
            WorkflowTestProgram.Check(Session.Checked && status.Text.StartsWith("新增完成") && modelsGrid.Rows.Count == 1, "真实两栏新增入口执行完成后自动重扫并仅显示对应总装");
            var total = (AssemblyUpdate)modelsGrid.Rows[0].DataBoundItem;
            WorkflowTestProgram.Check(total.Selected && total.UpdatedXrefs.Contains("新增引用：") && string.IsNullOrEmpty(total.PendingXrefs) && Workflow.ExportJobs(Session, System.IO.Path.Combine(Session.LocalModelsRoot, "nwc"), new[] { total }).Count == 1, "新增入口自动勾选受影响总装并接入 NWC 导出规划");
        }
    }
    internal static partial class WorkflowTestProgram
    {
        private static async Task VerifyNewCloudFiles(string evidence)
        {
            Paths.ConfigureAliases(null);
            var root = Path.Combine(evidence, "new-cloud-files"); var models = Path.Combine(root, "Models"); var cloud = Path.Combine(root, "Cloud"); var state = Path.Combine(root, "state");
            var a = Path.Combine(models, "单系统总装文件", "9421_Assembly.dwg"); var b = Path.Combine(models, "单系统总装文件", "9424_Assembly.dwg");
            var source = Path.Combine(cloud, "114101-SIF-M3-M-9421_OCF", "Level 2", "New Pipe.dwg");
            var publicSource = Path.Combine(cloud, "Public.dwg");
            Dwg(a, "assembly-before-a"); Dwg(b, "assembly-before-b"); Dwg(source, "new-pipe"); Dwg(publicSource, "public");
            var originalA = Files.Hash(a); var originalB = Files.Hash(b);
            var config = new Settings { ConnectorRoot = cloud, LocalModelsRoot = models }; var service = new SyncService(config, () => { }, state);
            var session = new WorkflowSession { ConnectorRoot = cloud, LocalModelsRoot = models, ScopeFolder = Path.GetDirectoryName(a), Checked = true };
            session.Assemblies = new[] { a, b }.Select(p => Workflow.ScanAssembly(p, new DwgReader())).ToList();
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Check(session.Sources.All(s => s.Plans.Count == 0), "真正无本地文件的云端新增源可扫描，而非要求已有 Xref");
            using (var ui = new WorkflowForm(session, config, () => { }, () => service))
            {
                ui.StartPosition = FormStartPosition.Manual; ui.Location = new Point(-5000, -5000); ui.Show(); ui.VerifyNewCloudFilter(); ui.Close();
            }
            var batch = await NewCloudFiles.Plan(session, config, session.Sources, service, CancellationToken.None);
            var item = batch.Files.Single(f => f.Cloud == source); var other = batch.Files.Single(f => f.Cloud == publicSource);
            Check(item.X == 6653112.1 && item.Y == 29134629 && item.Z == 0 && item.Scale == 1 && item.Rotation == -4.42, "默认定位使用用户指定 WCS 点及比例一旋转负4.42度");
            item.Scale = 0; Reject(() => NewCloudFiles.ValidateSelection(session, batch), "非正比例不能进入新增执行"); item.Scale = 1;
            Check(item.Targets.SequenceEqual(new[] { a }) && other.Targets.Count == 0, "唯一同系统总装仅预填，公共或不唯一归属必须人工选择");
            Check(item.Local.EndsWith(@"local_114101-SIF-M3-M-9421_OCF\Level 2\New Pipe.dwg") && !File.Exists(item.Local) && Files.Hash(a) == originalA, "新增规划保留云端相对层级，预览前不下载不修改总装");
            Reject(() => NewCloudFiles.ValidateSelection(session, batch), "没有指定目标总装不能确认执行");
            other.Selected = false; item.Targets.Add(b);
            Check(NewCloudFiles.ValidateSelection(session, batch).Single() == item && item.Transfer.AffectedAssemblies.Count == 2, "单份云端新文件支持加入多份明确选择的总装");
            using (var preview = new NewCloudFilesForm(session, batch))
            {
                preview.StartPosition = FormStartPosition.Manual; preview.Location = new Point(-5000, -5000); preview.Show(); preview.Refresh();
                var table = preview.Controls.OfType<IWMF.ColumnAutoModel.ReviewTable<NewCloudFile>>().Single(); var grid = table.Grid;
                var itemRow = grid.Rows.Cast<DataGridViewRow>().Single(r => r.DataBoundItem == item);
                grid.CurrentCell = itemRow.Cells["Scale"]; grid.BeginEdit(true); ((TextBox)grid.EditingControl).Text = "2"; grid.EndEdit();
                grid.CurrentCell = itemRow.Cells["Rotation"]; grid.BeginEdit(true); ((TextBox)grid.EditingControl).Text = "30"; grid.EndEdit();
                Check(item.Scale == 2 && item.Rotation == 30, "新增确认表可以真实编辑逐文件比例和旋转并回写执行对象");
                table.Search.Text = "Public"; table.Search.Clear();
                Check(item.Selected && !other.Selected && item.Scale == 2 && item.Rotation == 30, "新增确认表筛选不丢失勾选及定位编辑");
                using (var bitmap = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(bitmap, new Rectangle(0, 0, preview.Width, preview.Height)); bitmap.Save(Path.Combine(evidence, "new-cloud-files-preview.png"), ImageFormat.Png); }
                Check(preview.Controls.OfType<Label>().Any(l => l.Text.Contains("比例 1") && l.Text.Contains("备份")), "新增预览明确展示修改总装、备份和插入坐标规则");
                Check(preview.Controls.OfType<IWMF.ColumnAutoModel.ReviewTable<NewCloudFile>>().Single().Controls.OfType<DataGridView>().Single().Rows.Count == 2, "新增确认表实际绑定文件行，不是空白占位");
                preview.Close();
            }
            int stagedCount = 0; bool sawCustomPlacement = false;
            AttachmentResult FakeStage(string assembly, List<ReferencePlacement> placements)
            {
                var sources = placements.Select(p => p.Source).ToList();
                if (placements.Any(p => p.Source == item.Local && p.X == 6653112.1 && p.Y == 29134629 && p.Scale == 2 && p.Rotation == 30)) sawCustomPlacement = true;
                stagedCount++;
                var old = DwgReader.Graph.TryGetValue(assembly, out var graph) ? graph : new List<RefEdge>();
                var added = sources.Where(s => !old.Any(e => e.Resolved == s)).ToList();
                if (added.Count == 0) return new AttachmentResult { Added = 0 };
                var staged = assembly + ".fixture.tmp.dwg"; Dwg(staged, "attached-" + stagedCount);
                DwgReader.Graph[assembly] = old.Concat(added.Select(s => new RefEdge { Name = Path.GetFileNameWithoutExtension(s), Saved = s, Resolved = s })).ToList();
                return new AttachmentResult { Added = added.Count, Staged = staged };
            }
            await NewCloudFiles.Run(session, batch, service, CancellationToken.None, _ => { }, FakeStage, state);
            Check(sawCustomPlacement, "自定义定位准确传入生产批次挂接接口");
            Check(Files.Hash(item.Local) == Files.Hash(source) && !File.Exists(other.Local), "只下载确认勾选的新文件到正式 local_ 镜像，不下载未选文件");
            Check(batch.Results.Count == 2 && batch.Results.All(r => r.Added == 1 && r.AfterHash != null && File.Exists(r.Backup)), "多总装逐份新增引用后保存并保留原图备份");
            Check(Files.Hash(batch.Results.Single(r => r.Assembly == a).Backup) == originalA && Files.Hash(batch.Results.Single(r => r.Assembly == b).Backup) == originalB, "总装备份与改写前内容完全一致");
            Check(!session.Checked && session.AddedReferences.Count == 2 && session.UpdatedHashes.ContainsKey(item.Local), "新增成功要求重扫关系并分别记录实际下载与新增引用");
            Check(File.Exists(batch.LogPath) && File.Exists(Path.ChangeExtension(batch.LogPath, ".json")) && File.ReadAllText(batch.LogPath).Contains(batch.Results[0].Backup), "新增批次日志关联下载报告、总装备份和实际执行结果");
            session.Assemblies = new[] { a, b }.Select(p => Workflow.ScanAssembly(p, new DwgReader())).ToList(); session.Checked = true;
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service); Workflow.UpdateAffected(session);
            foreach (var assembly in session.Assemblies) assembly.Selected = true;
            Check(session.Assemblies.All(x => x.UpdatedXrefs.Contains("新增引用：") && x.ExpectedHashes.ContainsKey(item.Local)) && Workflow.ExportJobs(session, Path.Combine(root, "nwc")).Count == 2, "重扫后右侧识别新增引用并进入两份总装 NWC 规划");
            var retry = await NewCloudFiles.Plan(session, config, session.Sources.Where(s => s.Path == source), service, CancellationToken.None); retry.Files[0].Targets = new List<string> { a, b };
            var beforeRetry = Files.Hash(a);
            await NewCloudFiles.Run(session, retry, service, CancellationToken.None, _ => { }, FakeStage, state);
            Check(retry.Results.All(r => r.Added == 0 && r.Backup == null) && Files.Hash(a) == beforeRetry && !retry.Files[0].Transfer.Written, "重试复用相同下载和已有引用，不重复插入或再次改写总装");
            session.Checked = true;
            var publicItem = session.Sources.Single(s => s.Path == publicSource);
            var failed = await NewCloudFiles.Plan(session, config, new[] { publicItem }, service, CancellationToken.None); failed.Files[0].Targets.Add(a);
            await NewCloudFiles.Run(session, failed, service, CancellationToken.None, _ => { }, (_, __) => throw new IOException("模拟挂接失败"), state);
            Check(File.Exists(failed.Files[0].Local) && Files.Hash(a) == beforeRetry && failed.Results.Single().State.Contains("挂接失败") && failed.Results.Single().AfterHash == null, "挂接失败保留已下载文件而不改写原总装，日志不冒充成功");
            Dwg(failed.Files[0].Local, "different-local");
            var refused = false; try { await NewCloudFiles.Plan(session, config, new[] { publicItem }, service, CancellationToken.None); } catch (IOException) { refused = true; }
            Check(refused, "新增入口不绕过不同内容的本地覆盖确认");
            File.Delete(failed.Files[0].Local);
            var locked = await NewCloudFiles.Plan(session, config, new[] { publicItem }, service, CancellationToken.None); locked.Files[0].Targets.Add(a);
            refused = false; try { await NewCloudFiles.Run(session, locked, service, CancellationToken.None, _ => throw new IOException("目标仍打开"), FakeStage, state); } catch (IOException) { refused = true; }
            Check(refused && !File.Exists(locked.Files[0].Local) && File.ReadAllText(locked.LogPath).Contains("目标仍打开"), "目标总装打开时预检阻止，不下载且保留失败日志");
            var changed = await NewCloudFiles.Plan(session, config, new[] { publicItem }, service, CancellationToken.None); changed.Files[0].Targets.Add(a); File.AppendAllText(publicSource, "changed");
            var stagesBefore = stagedCount;
            await NewCloudFiles.Run(session, changed, service, CancellationToken.None, _ => { }, FakeStage, state);
            Check(stagedCount == stagesBefore && !File.Exists(changed.Files[0].Local) && changed.Results.Single().AfterHash == null, "确认后云端内容变化时下载失败，不进入总装挂接");
            config.Baselines.Clear();
            var foreign = await NewCloudFiles.Plan(session, config, new[] { publicItem }, service, CancellationToken.None); foreign.Files[0].Targets.Add(Path.Combine(models, "not-scanned.dwg"));
            Reject(() => NewCloudFiles.ValidateSelection(session, foreign), "拒绝将新文件写入未经本次检查的任意总装");
            foreign.Files[0].Targets.Clear(); foreign.Files[0].Targets.Add(a); Dwg(a, "user-changed-after-preview");
            refused = false; try { await NewCloudFiles.Run(session, foreign, service, CancellationToken.None, _ => { }, FakeStage, state); } catch (IOException) { refused = true; }
            Check(refused && !File.Exists(foreign.Files[0].Local), "确认后总装变化时在下载前阻止覆盖");
            publicItem.AmbiguousLocals.Add(Path.Combine(models, "unknown.dwg"));
            refused = false; try { await NewCloudFiles.Plan(session, config, new[] { publicItem }, service, CancellationToken.None); } catch (IOException) { refused = true; }
            Check(refused, "新增入口不绕过同名歧义或错误引用诊断");
            var uiSource = Path.Combine(cloud, "9450_Extra", "UiNew.dwg"); var uiTotal = Path.Combine(models, "单系统总装文件", "9450_Total.dwg");
            Dwg(uiSource, "ui-new"); Dwg(uiTotal, "empty-assembly");
            session.Assemblies.Add(Workflow.ScanAssembly(uiTotal, new DwgReader())); session.Checked = true;
            session.Sources = await Workflow.Check(session, config, CancellationToken.None, null, service);
            Host.NewStage = FakeStage;
            try
            {
                using (var ui = new WorkflowForm(session, config, () => { }, () => service))
                {
                    ui.StartPosition = FormStartPosition.Manual; ui.Location = new Point(-5000, -5000); ui.Show(); await ui.VerifyImportNewFlow("UiNew.dwg"); ui.Close();
                }
            }
            finally { Host.NewStage = null; }
        }
    }
}
