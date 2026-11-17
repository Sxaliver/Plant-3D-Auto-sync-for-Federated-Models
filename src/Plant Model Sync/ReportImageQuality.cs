using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IWMF.PlantModelSync
{
    internal static class ReportImageQuality
    {
        // 全像素检查，避免固定步长采样恰好漏过细管线；过浅的文字不冒充模型画面。
        internal static bool IsUseful(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width < 2 || bitmap.Height < 2) return false;
            using (var image = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb))
            {
                var bg = image.GetPixel(0, 0); var foreground = 0;
                var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[image.Width * 4];
                    for (int y = 0; y < image.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < row.Length; x += 4)
                            if (Math.Max(Math.Abs(row[x] - bg.B), Math.Max(Math.Abs(row[x + 1] - bg.G), Math.Abs(row[x + 2] - bg.R))) >= 12 && ++foreground >= 32) return true;
                    }
                    return false;
                }
                finally { image.UnlockBits(data); }
            }
        }
    }
}
