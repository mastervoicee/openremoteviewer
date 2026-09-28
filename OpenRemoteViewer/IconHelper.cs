using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinIcon = System.Drawing.Icon;

namespace OpenRemoteViewer;

public static class IconHelper
{
    private static WinIcon? _trayIcon;
    private static WinIcon? _trayIconRed;
    private static BitmapSource? _wpfIcon;

    public static WinIcon GetTrayIcon()
    {
        if (_trayIcon != null) return _trayIcon;
        _trayIcon = WinIcon.FromHandle(CreateBitmap(32, false).GetHicon());
        return _trayIcon;
    }

    public static WinIcon GetTrayIconRed()
    {
        if (_trayIconRed != null) return _trayIconRed;
        _trayIconRed = WinIcon.FromHandle(CreateBitmap(32, true).GetHicon());
        return _trayIconRed;
    }

    public static BitmapSource GetWpfIcon()
    {
        if (_wpfIcon != null) return _wpfIcon;
        using var bmp = CreateBitmap(64, false);
        _wpfIcon = Imaging.CreateBitmapSourceFromHBitmap(
            bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        _wpfIcon.Freeze();
        return _wpfIcon;
    }

    private static Bitmap CreateBitmap(int size, bool red)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        float pad = size * 0.04f;
        var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);

        Color c1 = red ? Color.FromArgb(200, 30, 30) : Color.FromArgb(0, 100, 210);
        Color c2 = red ? Color.FromArgb(255, 60, 60) : Color.FromArgb(0, 160, 255);

        using var bgBrush = new LinearGradientBrush(rect, c1, c2, LinearGradientMode.ForwardDiagonal);
        g.FillEllipse(bgBrush, rect);

        float fontSize = size * 0.36f;
        using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("ORV", font, textBrush, rect, sf);

        return bmp;
    }
}
