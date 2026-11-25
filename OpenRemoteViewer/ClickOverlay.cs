using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SysRect = System.Drawing.Rectangle;

namespace OpenRemoteViewer;

/// <summary>
/// Transparent, topmost, click-through overlay that shows animated red circles.
/// Covers exactly one monitor at a time.
/// </summary>
public class ClickOverlay : Window
{
    private readonly Canvas _canvas;
    private readonly StrokePainter _painter;
    private readonly DispatcherTimer _topmostTimer;
    private double _dpiScale = 1.0;

    public ClickOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        _canvas = new Canvas();
        _painter = new StrokePainter(_canvas);
        Content = _canvas;

        // Windows молча снимает WS_EX_TOPMOST (Win+D, secure desktop, рестарт explorer) —
        // без периодического пере-утверждения оверлей проваливается под окна.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => AssertTopmost();

        Loaded += OnLoaded;
        Closed += (_, _) => _topmostTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = PresentationSource.FromVisual(this);
        _dpiScale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        MakeClickThrough();
        AssertTopmost();
        _topmostTimer.Start();
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void AssertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetMonitor(SysRect bounds)
    {
        Dispatcher.Invoke(() =>
        {
            _painter.Clear();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Left = bounds.X / _dpiScale;
                Top = bounds.Y / _dpiScale;
                Width = bounds.Width / _dpiScale;
                Height = bounds.Height / _dpiScale;
                return;
            }

            // Ставим окно в физических пикселях: у мониторов может быть разный масштаб,
            // а пересчёт через DIP берёт масштаб того экрана, где окно было до переезда.
            // Второй вызов — после того как WPF принял масштаб нового монитора и мог подвинуть размер.
            for (int i = 0; i < 2; i++)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);
                _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
        });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
    }

    /// <summary>
    /// Add a point of a stroke the admin is drawing. Coordinates are pixels
    /// of the currently shared monitor. The stroke fades out 3 seconds after
    /// the last point.
    /// </summary>
    public void AddDrawPoint(DrawPoint p)
    {
        Dispatcher.Invoke(() =>
        {
            if (p.IsStart) AssertTopmost();
            _painter.AddPoint(p.StrokeId, p.IsStart, p.X / _dpiScale, p.Y / _dpiScale);
        });
    }

    /// <summary>
    /// Show a pulsing red circle at the given coordinates (relative to the current monitor, in pixels).
    /// </summary>
    public void ShowClick(int x, int y)
    {
        Dispatcher.Invoke(() =>
        {
            AssertTopmost();
            double dipX = x / _dpiScale;
            double dipY = y / _dpiScale;
            const double radius = 16;

            var circle = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 40, 40)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 255, 0, 0)),
                StrokeThickness = 2.5,
                RenderTransform = new ScaleTransform(1, 1),
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                IsHitTestVisible = false
            };

            Canvas.SetLeft(circle, dipX - radius);
            Canvas.SetTop(circle, dipY - radius);
            _canvas.Children.Add(circle);

            var duration = TimeSpan.FromSeconds(1.4);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var opacityAnim = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
            var scaleXAnim = new DoubleAnimation(1, 2.5, duration) { EasingFunction = ease };
            var scaleYAnim = new DoubleAnimation(1, 2.5, duration) { EasingFunction = ease };

            opacityAnim.Completed += (_, _) => _canvas.Children.Remove(circle);

            circle.BeginAnimation(OpacityProperty, opacityAnim);
            var st = (ScaleTransform)circle.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        });
    }

    #region P/Invoke for click-through

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    #endregion
}
