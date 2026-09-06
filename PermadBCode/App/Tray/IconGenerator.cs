using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace PermadB.Tray;

public static class IconGenerator
{
    public static Icon CreateShieldIcon(string state = "active")
    {
        // 32x32 standard tray icon size
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            Color primaryColor;
            Color accentColor;

            switch (state.ToLowerInvariant())
            {
                case "disabled":
                    primaryColor = Color.FromArgb(120, 120, 130);
                    accentColor = Color.FromArgb(70, 70, 80);
                    break;
                case "custom":
                    primaryColor = Color.FromArgb(245, 158, 11); // Amber
                    accentColor = Color.FromArgb(180, 83, 9);
                    break;
                case "active":
                default:
                    primaryColor = Color.FromArgb(16, 185, 129); // Emerald Green
                    accentColor = Color.FromArgb(5, 150, 105);
                    break;
            }

            // Draw shield polygon
            using var path = new GraphicsPath();
            path.AddLine(16, 3, 27, 7);
            path.AddBezier(27, 7, 28, 17, 22, 25, 16, 29);
            path.AddBezier(16, 29, 10, 25, 4, 17, 5, 7);
            path.CloseFigure();

            using (var brush = new LinearGradientBrush(new Point(0, 0), new Point(32, 32), primaryColor, accentColor))
            {
                g.FillPath(brush, path);
            }

            using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f))
            {
                g.DrawPath(pen, path);
            }

            if (state == "disabled")
            {
                // Draw diagonal slash across shield
                using var slashPen = new Pen(Color.FromArgb(239, 68, 68), 2.5f);
                slashPen.StartCap = LineCap.Round;
                slashPen.EndCap = LineCap.Round;
                g.DrawLine(slashPen, 7, 7, 25, 25);
            }
            else
            {
                // Draw sound / dB waveform waves inside shield
                using var wavePen = new Pen(Color.White, 2f);
                wavePen.StartCap = LineCap.Round;
                wavePen.EndCap = LineCap.Round;

                // Center audio bar
                g.DrawLine(wavePen, 16, 11, 16, 21);
                // Left audio bar
                g.DrawLine(wavePen, 11, 14, 11, 18);
                // Right audio bar
                g.DrawLine(wavePen, 21, 13, 21, 19);
            }
        }

        // Convert bitmap to icon via in-memory ICO stream.
        // This ensures the System.Drawing.Icon owns its native HICON handle,
        // preventing premature handle destruction and guaranteeing proper rendering in Windows Shell.
        return ConvertBitmapToIcon(bmp);
    }

    private static Icon ConvertBitmapToIcon(Bitmap bmp)
    {
        using var pngMs = new MemoryStream();
        bmp.Save(pngMs, ImageFormat.Png);
        byte[] pngBytes = pngMs.ToArray();

        using var icoMs = new MemoryStream();
        using (var bw = new BinaryWriter(icoMs, Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR Header
            bw.Write((ushort)0); // Reserved
            bw.Write((ushort)1); // Type 1 = Icon
            bw.Write((ushort)1); // Number of images

            // ICONDIRENTRY (16 bytes)
            bw.Write((byte)bmp.Width);
            bw.Write((byte)bmp.Height);
            bw.Write((byte)0);   // Color palette count (0 if >= 8bpp)
            bw.Write((byte)0);   // Reserved
            bw.Write((ushort)1); // Color planes
            bw.Write((ushort)32);// Bits per pixel
            bw.Write((uint)pngBytes.Length); // Size of image data
            bw.Write((uint)22);  // Offset of image data (6 header + 16 entry)

            // Image Data
            bw.Write(pngBytes);
            bw.Flush();
        }

        icoMs.Position = 0;
        return new Icon(icoMs);
    }
}
