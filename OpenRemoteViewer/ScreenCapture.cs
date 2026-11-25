using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace OpenRemoteViewer;

public static class ScreenCapture
{
    /// <summary>
    /// Мониторы в физических пикселях, каждый раз заново у Windows.
    /// Не Screen.AllScreens: тот кеширует список и после отключения/подключения
    /// мониторов (ноутбук унесли от док-станции) продолжает отдавать старую раскладку.
    /// </summary>
    public static List<Rectangle> GetMonitors()
    {
        var list = new List<Rectangle>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                var rc = mi.rcMonitor;
                list.Add(new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top));
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
            list.AddRange(WinForms.Screen.AllScreens.Select(s => s.Bounds));

        return list.OrderBy(r => r.X).ThenBy(r => r.Y).ToList();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
