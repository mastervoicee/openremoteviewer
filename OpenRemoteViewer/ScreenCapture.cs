using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace OpenRemoteViewer;

public static class ScreenCapture
{
    public static List<Rectangle> GetMonitors()
    {
        return WinForms.Screen.AllScreens
            .OrderBy(s => s.Bounds.X)
            .ThenBy(s => s.Bounds.Y)
            .Select(s => s.Bounds)
            .ToList();
    }

    public static int GetCursorMonitorIndex()
    {
        GetCursorPos(out var pt);
        var monitors = GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Contains(pt.X, pt.Y))
                return i;
        }
        return 0;
    }

    /// <summary>
    /// Capture a monitor as JPEG. Returns null when the screen is temporarily not
    /// capturable — UAC / firewall prompt (secure desktop), locked workstation,
    /// session switch, display mode change. This is a normal, recoverable state:
    /// the caller must keep the session alive and simply retry.
    /// </summary>
    public static byte[]? TryCaptureMonitor(int monitorIndex)
    {
        try
        {
            return CaptureMonitor(monitorIndex);
        }
        catch
        {
            return null;
        }
    }

    public static byte[] CaptureMonitor(int monitorIndex)
    {
        var monitors = GetMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            monitorIndex = 0;

        var bounds = monitors[monitorIndex];
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 60L);
        bmp.Save(ms, encoder, encoderParams);
        return ms.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
