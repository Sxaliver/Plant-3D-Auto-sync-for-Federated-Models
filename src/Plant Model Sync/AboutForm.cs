using System.Drawing;
using System.Windows.Forms;
using IWMF.ColumnAutoModel;

namespace IWMF.PlantModelSync
{
    internal sealed class AboutForm : Form
    {
        internal const string BackgroundText =
            "项目背景\r\n\r\n" +
            "SO 要求模型按楼层拆分交付，同时还需要提供每个系统的总装文件。\r\n\r\n" +
            "如果只要求分楼层，现有 ACC / Desktop Connector 同步流程即可满足需求。但在本项目流程中，每个系统的总装仍需在本地通过 Xref 组合各楼层 DWG，再导出 NWC 上传。\r\n\r\n" +
            "Auto sync 因此而开发：衔接云端分楼层源文件与本地系统总装，减少手动查找、对比和更新 Xref 的重复操作。\r\n\r\n" +
            "工作流程\r\n\r\n" +
            "云端分楼层 DWG → Pull 到本地作为 Xref → 本地按系统总装 → 导出 NWC → 手动上传 ACC\r\n\r\n" +
            "云端文件同步由 Desktop Connector 负责；插件负责本地对比、更新与总装导出。";

        internal AboutForm()
        {
            Text = "关于 Auto sync"; Width = 780; Height = 510; MinimumSize = new Size(680, 470);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
            var version = typeof(AboutForm).Assembly.GetName().Version.ToString(3);
            var header = ModernUi.Header("Auto sync", "Plant 3D · " + version);
            var content = new TextBox { Text = BackgroundText, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.White, ScrollBars = ScrollBars.Vertical, TabStop = true, AccessibleName = "项目背景与工作流程" };
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(24, 18, 24, 12) }; body.Controls.Add(content);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            var close = new RoundedButton { Text = "关闭", DialogResult = DialogResult.Cancel }; footer.Controls.Add(close);
            Controls.Add(body); Controls.Add(header); Controls.Add(footer); CancelButton = close; AcceptButton = close;
            ModernUi.Apply(this);
            Shown += (s, e) => { content.Select(0, 0); close.Focus(); };
        }
    }
}
