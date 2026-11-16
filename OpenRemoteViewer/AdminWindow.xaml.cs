using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfCursors = System.Windows.Input.Cursors;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace OpenRemoteViewer;

public partial class AdminWindow : Window
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private SecureConnection? _connection;
    private CancellationTokenSource _cts = new();
    private readonly List<MonitorInfo> _monitors = [];
    private int _currentMonitor;
    private int _clientCursorMonitor = -1;
    private int _frameCount;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;
    private StrokePainter? _painter;
    private int _strokeSeq;
    private int _activeStroke = -1;
    private int _lastSentX = int.MinValue;
    private int _lastSentY = int.MinValue;
    private bool _captureBlocked;
    private bool _closing;

    // Клиент шлёт кадр или «экран недоступен» минимум раз в секунду.
    // Тишина дольше этого срока = соединение мертво, переподключаемся.
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(10);

    private static readonly SolidColorBrush BrushInactive = new(WpfColor.FromRgb(0x3E, 0x3E, 0x42));
    private static readonly SolidColorBrush BrushActive = new(WpfColor.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly SolidColorBrush BrushBorderInactive = new(WpfColor.FromRgb(0x55, 0x55, 0x58));
    private static readonly SolidColorBrush BrushBorderActive = new(WpfColor.FromRgb(0x00, 0xAA, 0xFF));
    private static readonly SolidColorBrush BrushTextLight = new(WpfColor.FromRgb(0xDD, 0xDD, 0xDD));
    private static readonly SolidColorBrush BrushTextDim = new(WpfColor.FromRgb(0x88, 0x88, 0x88));
    private static readonly SolidColorBrush BrushScreenFill = new(WpfColor.FromRgb(0x00, 0x5A, 0x9E));
    private static readonly SolidColorBrush BrushIndicator = new(WpfColor.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush BrushCursorHere = new(WpfColor.FromRgb(0xFF, 0xA7, 0x26));

    public AdminWindow(string host, int port, string password)
    {
        InitializeComponent();
        Icon = IconHelper.GetWpfIcon();
        Title = $"{Lang.TitleAdmin}  v{Updater.CurrentText}";
        txtScreenLabel.Text = Lang.ScreenLabel;
        txtClickHint.Text = Lang.ClickHint + Lang.DrawHint;
        btnDisconnect.Content = Lang.BtnDisconnect;
        txtAdminStatus.Text = Lang.Connecting;
        _host = host;
        _port = port;
        _password = password;
        _painter = new StrokePainter(canvasDraw);
        Loaded += async (_, _) =>
        {
            // Кнопка версии в верхней панели: проверка по нажатию и раз в час,
            // при новой версии становится зелёной и ставит обновление.
            _ = new UpdatePrompt(btnUpdate, ResumeEnvironment, Shutdown, _cts.Token);
            await RunConnectionAsync();
        };
    }

    private Dictionary<string, string> ResumeEnvironment() => new()
    {
        [Updater.EnvRole] = "Admin",
        [Updater.EnvHost] = _host,
        [Updater.EnvPort] = _port.ToString(),
        [Updater.EnvPassword] = _password
    };

    /// <summary>
    /// Connect and keep the session alive: if the connection drops (клиент перезагрузился,
    /// сеть моргнула, туннель отвалился) — переподключаемся сами, без ручного перезапуска.
    /// </summary>
    private async Task RunConnectionAsync()
    {
        bool firstAttempt = true;
        int attempt = 0;

        while (!_closing && !_cts.IsCancellationRequested)
        {
            try
            {
                txtAdminStatus.Text = firstAttempt
                    ? string.Format(Lang.ConnectingTo, _host, _port)
                    : string.Format(Lang.Reconnecting, ++attempt);

                _connection = await SecureConnection.ConnectAsync(_host, _port, _password, _cts.Token);

                if (_connection == null)
                {
                    // Неверный пароль — повторять бессмысленно.
                    txtAdminStatus.Text = Lang.WrongPassword;
                    MessageBox.Show(Lang.AuthFailedMessage,
                        Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                    return;
                }

                firstAttempt = false;
                attempt = 0;
                _captureBlocked = false;
                txtAdminStatus.Text = ConnectedStatus();

                await Task.Run(() => ReceiveLoop(_cts.Token));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (firstAttempt)
            {
                // Первая попытка: скорее всего опечатка в IP/порту — говорим прямо.
                txtAdminStatus.Text = Lang.ConnectionError;
                MessageBox.Show(string.Format(Lang.ConnectionErrorMessage, ex.Message),
                    Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            catch { }

            if (_closing || _cts.IsCancellationRequested) return;

            try { _connection?.Dispose(); } catch { }
            _connection = null;
            _painter?.Clear();
            txtAdminStatus.Text = string.Format(Lang.Reconnecting, attempt + 1);

            try { await Task.Delay(2000, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private string ConnectedStatus() => string.Format(
        _connection?.IsLegacyPeer == true ? Lang.ClientOutdated : Lang.ConnectedTo, _host);

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (type, payload) = await _connection!.ReceiveMessageAsync(ct, SilenceTimeout);

                switch (type)
                {
                    case MessageType.MonitorList:
                        ParseMonitorList(payload);
                        break;
                    case MessageType.ScreenFrame:
                        DisplayFrame(payload);
                        break;
                    case MessageType.CaptureBlocked:
                        if (!_captureBlocked)
                        {
                            _captureBlocked = true;
                            Dispatcher.Invoke(() => txtAdminStatus.Text = Lang.ScreenUnavailable);
                        }
                        break;
                    case MessageType.CursorMonitor:
                        if (payload.Length >= 4)
                        {
                            int idx = BitConverter.ToInt32(payload, 0);
                            if (idx != _clientCursorMonitor)
                            {
                                _clientCursorMonitor = idx;
                                Dispatcher.Invoke(UpdateCursorIndicator);
                            }
                        }
                        break;
                    case MessageType.Disconnect:
                        Dispatcher.Invoke(() => txtAdminStatus.Text = Lang.ClientDisconnected);
                        return;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                Dispatcher.Invoke(() => txtAdminStatus.Text = Lang.ConnectionLost);
                break;
            }
            catch { break; }
        }
    }

    private void ParseMonitorList(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms);
        int count = br.ReadInt32();

        _monitors.Clear();
        for (int i = 0; i < count; i++)
        {
            _monitors.Add(new MonitorInfo
            {
                X = br.ReadInt32(),
                Y = br.ReadInt32(),
                Width = br.ReadInt32(),
                Height = br.ReadInt32()
            });
        }

        Dispatcher.Invoke(() =>
        {
            pnlMonitors.Children.Clear();
            for (int i = 0; i < _monitors.Count; i++)
                pnlMonitors.Children.Add(CreateMonitorButton(i, _monitors[i]));

            _currentMonitor = Math.Min(_currentMonitor, _monitors.Count - 1);
            UpdateMonitorSelection();
        });
    }

    private Border CreateMonitorButton(int index, MonitorInfo monitor)
    {
        var border = new Border
        {
            Background = BrushInactive,
            BorderBrush = BrushBorderInactive,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = WpfCursors.Hand,
            Tag = index
        };

        var stack = new StackPanel
        {
            Orientation = WpfOrientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var iconGrid = new Grid
        {
            Width = 28,
            Height = 20,
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        iconGrid.Children.Add(new WpfRectangle
        {
            Fill = BrushScreenFill,
            RadiusX = 2,
            RadiusY = 2,
            Stroke = BrushBorderInactive,
            StrokeThickness = 1
        });
        iconGrid.Children.Add(new TextBlock
        {
            Text = (index + 1).ToString(),
            Foreground = WpfBrushes.White,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        var label = new TextBlock
        {
            Text = string.Format(Lang.DisplayN, index + 1),
            Foreground = BrushTextLight,
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var resolution = new TextBlock
        {
            Text = $"{monitor.Width} × {monitor.Height}",
            Foreground = BrushTextDim,
            FontSize = 9,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var indicatorPanel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 1)
        };

        var viewIndicator = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = BrushIndicator,
            Margin = new Thickness(2, 0, 2, 0),
            Visibility = Visibility.Collapsed,
            Tag = "indicator",
            ToolTip = Lang.ViewingThisScreen
        };

        var cursorIndicator = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = BrushCursorHere,
            Margin = new Thickness(2, 0, 2, 0),
            Visibility = Visibility.Collapsed,
            Tag = "cursor",
            ToolTip = Lang.CursorHere
        };

        indicatorPanel.Children.Add(viewIndicator);
        indicatorPanel.Children.Add(cursorIndicator);

        stack.Children.Add(iconGrid);
        stack.Children.Add(label);
        stack.Children.Add(resolution);
        stack.Children.Add(indicatorPanel);
        border.Child = stack;

        border.MouseLeftButtonDown += async (_, _) => await SelectMonitorAsync(index);
        border.MouseEnter += (_, _) =>
        {
            if ((int)border.Tag != _currentMonitor)
                border.Background = new SolidColorBrush(WpfColor.FromRgb(0x50, 0x50, 0x55));
        };
        border.MouseLeave += (_, _) =>
        {
            if ((int)border.Tag != _currentMonitor)
                border.Background = BrushInactive;
        };

        return border;
    }

    private void UpdateMonitorSelection()
    {
        for (int i = 0; i < pnlMonitors.Children.Count; i++)
        {
            if (pnlMonitors.Children[i] is not Border border) continue;
            bool active = i == _currentMonitor;

            border.Background = active ? BrushActive : BrushInactive;
            border.BorderBrush = active ? BrushBorderActive : BrushBorderInactive;
            border.BorderThickness = new Thickness(active ? 2 : 1);

            var viewEll = FindIndicator(border, "indicator");
            if (viewEll != null)
                viewEll.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCursorIndicator();
    }

    private void UpdateCursorIndicator()
    {
        for (int i = 0; i < pnlMonitors.Children.Count; i++)
        {
            if (pnlMonitors.Children[i] is not Border border) continue;
            bool hasCursor = i == _clientCursorMonitor;

            var cursorEll = FindIndicator(border, "cursor");
            if (cursorEll != null)
                cursorEll.Visibility = hasCursor ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static Ellipse? FindIndicator(Border border, string tag)
    {
        if (border.Child is not StackPanel sp) return null;
        foreach (var child in sp.Children)
        {
            if (child is StackPanel innerPanel)
            {
                foreach (var inner in innerPanel.Children)
                {
                    if (inner is Ellipse ell && ell.Tag as string == tag)
                        return ell;
                }
            }
            if (child is Ellipse e && e.Tag as string == tag)
                return e;
        }
        return null;
    }

    private async Task SelectMonitorAsync(int index)
    {
        if (_connection == null || index == _currentMonitor) return;

        _currentMonitor = index;
        UpdateMonitorSelection();

        try
        {
            await _connection.SendMessageAsync(
                MessageType.SwitchMonitor,
                BitConverter.GetBytes(_currentMonitor),
                _cts.Token);
        }
        catch { }
    }

    private void DisplayFrame(byte[] jpegData)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(jpegData);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                imgScreen.Source = bi;

                if (_captureBlocked)
                {
                    _captureBlocked = false;
                    txtAdminStatus.Text = ConnectedStatus();
                }

                _frameCount++;
                var now = DateTime.UtcNow;
                if ((now - _lastFpsUpdate).TotalSeconds >= 1.0)
                {
                    txtFps.Text = $"FPS: {_frameCount}";
                    _frameCount = 0;
                    _lastFpsUpdate = now;
                }
            }
            catch { }
        });
    }

    private async void ImgScreen_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_connection == null) return;
        if (!TryMapToClientPixels(e, out int realX, out int realY)) return;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(realX);
        bw.Write(realY);

        try
        {
            await _connection.SendMessageAsync(MessageType.Click, ms.ToArray(), _cts.Token);
        }
        catch { }
    }

    #region Рисование правой кнопкой

    private void Screen_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_connection == null) return;
        if (!TryMapToClientPixels(e, out int x, out int y)) return;

        _activeStroke = ++_strokeSeq;
        _lastSentX = x;
        _lastSentY = y;
        ((UIElement)sender).CaptureMouse();

        SendDrawPoint(new DrawPoint(_activeStroke, true, x, y));
        _painter?.AddPoint(_activeStroke, true, e.GetPosition(canvasDraw).X, e.GetPosition(canvasDraw).Y);
        e.Handled = true;
    }

    private void Screen_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_activeStroke < 0 || e.RightButton != MouseButtonState.Pressed) return;
        if (!TryMapToClientPixels(e, out int x, out int y)) return;

        // не засоряем канал: точки ближе 2 px друг к другу пропускаем
        if (Math.Abs(x - _lastSentX) < 2 && Math.Abs(y - _lastSentY) < 2) return;
        _lastSentX = x;
        _lastSentY = y;

        SendDrawPoint(new DrawPoint(_activeStroke, false, x, y));
        var p = e.GetPosition(canvasDraw);
        _painter?.AddPoint(_activeStroke, false, p.X, p.Y);
    }

    private void Screen_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeStroke < 0) return;
        ((UIElement)sender).ReleaseMouseCapture();
        _activeStroke = -1;
        e.Handled = true;
    }

    private void Screen_LostMouseCapture(object sender, WpfMouseEventArgs e) => _activeStroke = -1;

    private async void SendDrawPoint(DrawPoint point)
    {
        var conn = _connection;
        if (conn == null) return;
        try { await conn.SendMessageAsync(MessageType.Draw, point.ToPayload(), _cts.Token); }
        catch { }
    }

    /// <summary>
    /// Точка мыши → пиксели транслируемого монитора. false, если курсор вне картинки
    /// (чёрные поля по краям при неподходящих пропорциях окна).
    /// </summary>
    private bool TryMapToClientPixels(WpfMouseEventArgs e, out int x, out int y)
    {
        x = y = 0;
        if (_monitors.Count == 0 || imgScreen.Source == null) return false;
        if (imgScreen.ActualWidth <= 0 || imgScreen.ActualHeight <= 0) return false;

        var pos = e.GetPosition(imgScreen);
        if (pos.X < 0 || pos.Y < 0 || pos.X > imgScreen.ActualWidth || pos.Y > imgScreen.ActualHeight)
            return false;

        var mon = _monitors[Math.Min(_currentMonitor, _monitors.Count - 1)];
        x = Math.Clamp((int)(pos.X / imgScreen.ActualWidth * mon.Width), 0, mon.Width - 1);
        y = Math.Clamp((int)(pos.Y / imgScreen.ActualHeight * mon.Height), 0, mon.Height - 1);
        return true;
    }

    #endregion

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e) => Shutdown();

    private void Shutdown()
    {
        _closing = true;
        try
        {
            _connection?.SendMessageAsync(MessageType.Disconnect, [], CancellationToken.None)
                .Wait(500);
        }
        catch { }

        _cts.Cancel();
        _connection?.Dispose();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _cts.Cancel();
        _connection?.Dispose();
        base.OnClosed(e);
    }
}

public class MonitorInfo
{
    public int X, Y, Width, Height;
}
