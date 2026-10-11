using System.Drawing;
using System.Drawing.Imaging;

namespace Linc.Desktop.Services;

/// <summary>
/// The tray icon's "not connected" look (1.0.0.11, D-083): the same mark, faded, with a small
/// hollow ring in the corner — readable on light and dark taskbars, no extra asset to ship.
/// </summary>
public static class TrayBadge
{
    public static Icon Disconnected(Icon source)
    {
        const int size = 32;
        using var original = new Icon(source, size, size).ToBitmap();
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var faded = new ImageAttributes();
            faded.SetColorMatrix(new ColorMatrix { Matrix33 = 0.45f });
            g.DrawImage(original, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, faded);
            using var ring = new Pen(Color.FromArgb(255, 154, 160, 166), 3);
            g.DrawEllipse(ring, size - 13, size - 13, 10, 10);
        }
        var handle = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }
}
