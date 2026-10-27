using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        private async Task ExportBrowserSelection()
        {
            var paths = BrowserSelection();
            if (paths.Length > 0) await ExportManualFiles(paths);
        }
        private async Task BrowseNwcExport()
        {
            using (var picker = new OpenFileDialog { Title = "从文件夹手动选择要导出 NWC 的总装 DWG（可多选）", Filter = "AutoCAD 图形 (*.dwg)|*.dwg", Multiselect = true, CheckFileExists = true, InitialDirectory = Directory.Exists(scopeFolder) ? scopeFolder : Directory.Exists(localRoot.Text) ? localRoot.Text : "" })
            {
                if (picker.ShowDialog(this) == DialogResult.OK) await ExportManualFiles(picker.FileNames);
            }
        }
        private async Task ExportManualFiles(IEnumerable<string> files)
        {
            bool ready = false;
            await Busy(async ct =>
            {
                if (string.IsNullOrWhiteSpace(output.Text)) Browse(output);
                if (string.IsNullOrWhiteSpace(output.Text)) return;
                await Task.Yield();
                var jobs = ManualNwcExport.Plan(files, output.Text, null, ct: ct, progress: message => status.Text = message);
                using (var confirm = new ManualNwcExportForm(jobs)) if (confirm.ShowDialog(this) != DialogResult.OK) return;
                Session = Session ?? new WorkflowSession { ConnectorRoot = connector.Text, LocalModelsRoot = localRoot.Text, ScopeFolder = scopeFolder ?? "" };
                Session.OutputFolder = output.Text; ExportRequests = jobs; Save(); ready = true;
            });
            // 退出 Busy 后再关窗，避免任务中的 FormClosing 取消关闭。
            if (ready) { DialogResult = DialogResult.OK; Close(); }
        }
    }
}
