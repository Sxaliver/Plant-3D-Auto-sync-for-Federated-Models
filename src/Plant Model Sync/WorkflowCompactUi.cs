using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed partial class WorkflowForm
    {
        private Control pathFields, modelDetailsPanel;
        private readonly Label pathSummary = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(16, 5, 16, 0), AutoEllipsis = true, AccessibleName = "当前目录摘要，点击展开配置" };
        private readonly ToolTip uiTips = new ToolTip();
        private void TogglePaths() { pathFields.Visible = !pathFields.Visible; UpdatePathSummary(); }
        private void UpdatePathSummary()
        {
            string Short(string text) { if (string.IsNullOrWhiteSpace(text)) return "未设置"; try { return Path.GetFileName(text.TrimEnd('\\', '/')); } catch { return text; } }
            pathSummary.Text = "云端 " + Short(connector.Text) + "   →   本地 " + Short(localRoot.Text) + "   |   总装 " + (string.IsNullOrWhiteSpace(scopeFolder) ? "当前项目" : Short(scopeFolder)) + "   |   NWC " + Short(output.Text);
            uiTips.SetToolTip(pathSummary, "云端：" + connector.Text + "\r\n本地：" + localRoot.Text + "\r\n总装：" + (string.IsNullOrWhiteSpace(scopeFolder) ? "当前项目" : scopeFolder) + "\r\nNWC：" + output.Text);
        }
        private void MenuButton(FlowLayoutPanel parent, string title, Action<ContextMenuStrip> build)
        {
            var button = new RoundedButton { Text = title }; var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };
            button.ContextMenuStrip = menu;
            build(menu); button.Click += (s, e) => menu.Show(button, new Point(0, button.Height)); actions.Add(button); parent.Controls.Add(button);
            Disposed += (s, e) => menu.Dispose();
        }
        private void MenuAction(ContextMenuStrip menu, string title, Action action)
        {
            var item = new ToolStripMenuItem(title); item.Click += (s, e) => { try { action(); } catch (Exception ex) { Error(ex); } }; menu.Items.Add(item);
        }
        private void MenuAsync(ContextMenuStrip menu, string title, Func<Task> action)
        {
            var item = new ToolStripMenuItem(title); item.Click += async (s, e) => { try { await action(); } catch (Exception ex) { Error(ex); } }; menu.Items.Add(item);
        }
        private void SetDetailedView(bool show)
        {
            foreach (var name in new[] { "DisplayPath", "MatchReason", "LocalSummary", "MissingSummary" }) sourcesGrid.Columns[name].Visible = show;
            modelsGrid.Columns["XrefSummary"].Visible = show;
            if (show) modelsGrid.Columns["XrefSummary"].MinimumWidth = 160;
            sourceDetail.Height = show ? 220 : 120;
            if (modelDetailsPanel != null) modelDetailsPanel.Height = show ? 220 : 120;
        }
    }
}
