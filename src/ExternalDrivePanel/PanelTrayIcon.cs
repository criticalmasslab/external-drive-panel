using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExternalDrivePanel;

/// <summary>Compact panel/drive mark matching the navy headers and gold favorites accent.</summary>
public static class PanelTrayIcon
{
    public static RenderTargetBitmap Render(int size)
    {
        if (size is < 16 or > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
            var navy = new SolidColorBrush(Color.FromRgb(0x24, 0x34, 0x47));
            var white = new Pen(Brushes.White, 1.5) { LineJoin = PenLineJoin.Round };
            draw.DrawRoundedRectangle(navy, null, new Rect(1, 1, 22, 22), 5, 5);
            draw.DrawRoundedRectangle(null, white, new Rect(5, 5, 14, 14), 2, 2);
            draw.DrawLine(white, new Point(5, 10), new Point(19, 10));
            draw.DrawLine(new Pen(Brushes.White, 1.5), new Point(8, 14.5), new Point(12, 14.5));
            draw.DrawEllipse(Brushes.Gold, null, new Point(16, 14.5), 1.6, 1.6);
            draw.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    public static System.Drawing.Icon Create()
    {
        // Native multi-resolution ICO: render each frame from vector shapes, not by
        // enlarging a 16px bitmap. Windows selects the frame for the tray's current DPI.
        int[] sizes = [16, 20, 24, 32, 40, 48, 64];
        var frames = sizes.Select(size =>
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(Render(size)));
            using var stream = new MemoryStream(); png.Save(stream); return stream.ToArray();
        }).ToArray();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            var offset = 6 + sizes.Length * 16;
            for (var i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        ico.Position = 0;
        using var icon = new System.Drawing.Icon(ico);
        return (System.Drawing.Icon)icon.Clone(); // Own the icon independently of its stream.
    }
}
