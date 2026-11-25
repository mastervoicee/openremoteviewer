using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace OpenRemoteViewer;

public partial class ClientWindow : Window
{
    private readonly int _port;
    private readonly string _password;
    private TcpListener? _listener;
    private SecureConnection? _connection;
    private ClickOverlay? _overlay;
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _sessionCts;
    private volatile int _currentMonitor;
    private volatile int _lastCursorMonitor = -1;
    private DateTime _lastMonitorCheck = DateTime.MinValue;
    private volatile string _monitorSignature = "";
    private WinForms.NotifyIcon? _trayIcon;
    private AdminNotificationWindow? _notification;
    private bool _isConnected;
    private bool _shutdownRequested;
    private bool _startMinimized;

    public ClientWindow(int port, string password, bool startMinimized = false)
    {
        InitializeComponent();
        Icon = IconHelper.GetWpfIcon();
        Title = Lang.TitleClient;
        txtClientTitle.Text = $"{Lang.TitleClient}  v{Updater.CurrentText}";

        _port = port;
        _password = password;
        _startMinimized = startMinimized;
        SetupTrayIcon();

        if (startMinimized)
        {
            Opacity = 0;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }

        Loaded += async (_, _) =>
        {
            if (_startMinimized)
            {
                _startMinimized = false;
                Hide();
                Opacity = 1;
                ShowInTaskbar = false;
                if (_trayIcon != null)
                    _trayIcon.Visible = true;
            }
            await StartListeningAsync();
        };
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = IconHelper.GetTrayIcon(),
            Text = Lang.TitleClient,
            Visible = false
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Lang.TrayShowWindow, null, (_, _) => ShowFromTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Lang.TrayStopAndExit, null, (_, _) =>
        {
            _shutdownRequested = true;
            Dispatcher.Invoke(Shutdown);
        });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void MinimizeToTray(bool showBalloon = true)
    {
        Hide();
        ShowInTaskbar = false;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            if (showBalloon)
            {
                _trayIcon.ShowBalloonTip(2000, Lang.AppTitle,
                    Lang.TrayRunning,
                    WinForms.ToolTipIcon.Info);
            }
        }
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon != null)
                _trayIcon.Visible = false;
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && !_startMinimized)
        {
            WindowState = WindowState.Normal;
            MinimizeToTray();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownRequested)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Клиент обновляется сам, без участия человека за компьютером: проверка вскоре
    /// после запуска и дальше раз в час. Если в этот момент идёт просмотр — админ
    /// переподключится автоматически через несколько секунд.
    /// </summary>
    private async Task AutoUpdateLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token);

            while (!_cts.IsCancellationRequested)
            {
                var info = await Updater.CheckAsync(_cts.Token);
                if (info != null)
                {
                    try
                    {
                        await Updater.ApplyAndRestartAsync(info, new Dictionary<string, string>
                        {
                            [Updater.EnvRole] = "Client",
                            [Updater.EnvPort] = _port.ToString(),
                            [Updater.EnvPassword] = _password
                        }, _cts.Token);

                        Shutdown();
                        System.Windows.Application.Current.Shutdown();
                        return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch { /* нет прав на папку, обрыв загрузки — попробуем через час */ }
                }

                await Task.Delay(Updater.CheckInterval, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task StartListeningAsync()
    {
        _ = FetchExternalIPAsync();
        _ = AutoUpdateLoopAsync();

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_listener == null && !TryStartListener())
                {
                    // Порт занят чужой программой — не сдаёмся, ждём и пробуем снова.
                    txtClientStatus.Text = string.Format(Lang.PortBusy, _port);
                    txtStatusBar.Text = string.Format(Lang.PortBusy, _port);
                    await Task.Delay(3000, _cts.Token);
                    continue;
                }

                ShowWaitingStatus();

                _connection = await SecureConnection.AcceptAsync(_listener!, _password, _cts.Token);

                if (_connection == null)
                {
                    txtClientStatus.Text = Lang.AuthFailed;
                    txtClientInfo.Text = Lang.WaitingNewConnection;
                    await Task.Delay(500, _cts.Token);
                    continue;
                }

                await HandleSessionAsync();
                CleanupSession();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Любой сбой (в т.ч. умерший сокет) — чистимся и поднимаем слушателя заново,
                // программа не должна оставаться "мёртвой" до ручного перезапуска.
                CleanupSession();
                StopListener();
                txtClientStatus.Text = string.Format(Lang.ErrorPrefix, ex.Message);
                try { await Task.Delay(3000, _cts.Token); } catch { break; }
            }
        }
    }

    private bool TryStartListener()
    {
        // ExclusiveAddressUse не даёт другой программе (node/python/docker и т.п.)
        // перехватить наш уже занятый порт через SO_REUSEADDR — из-за такого перехвата
        // админ подключался "в никуда" и картинка пропадала.
        foreach (bool exclusive in new[] { true, false })
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, _port);
                try { listener.ExclusiveAddressUse = exclusive; } catch { }
                listener.Start();
                _listener = listener;
                return true;
            }
            catch (SocketException) { }
            catch (InvalidOperationException) { }
        }

        _listener = null;
        return false;
    }

    private void StopListener()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private void ShowWaitingStatus()
    {
        var localIPs = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .ToList();

        txtClientStatus.Text = Lang.WaitingConnection;
        txtClientInfo.Text = string.Format(Lang.PortLabel, _port) + "\n"
            + string.Format(Lang.LocalIPLabel, string.Join(", ", localIPs));
        txtStatusBar.Text = Lang.WaitingConnection;
    }

    private async Task HandleSessionAsync()
    {
        _isConnected = true;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _sessionCts.Token;

        txtClientStatus.Text = Lang.Connected;
        txtClientInfo.Text = Lang.ConnectedInfo;
        txtStatusBar.Text = Lang.Connected;

        ShowAdminNotification();

        await SendMonitorListAsync();
        StartOverlay();

        MinimizeToTray(showBalloon: false);

        var streamTask = Task.Run(() => StreamScreenAsync(ct));
        var receiveTask = Task.Run(() => ReceiveCommandsAsync(ct));

        await Task.WhenAny(streamTask, receiveTask);

        _sessionCts.Cancel();

        try { await Task.WhenAll(streamTask, receiveTask); } catch { }
    }

    private void CleanupSession()
    {
        _isConnected = false;
        _lastCursorMonitor = -1;
        _currentMonitor = 0;

        CloseAdminNotification();

        _overlay?.Dispatcher.Invoke(() => { try { _overlay?.Close(); } catch { } });
        _overlay = null;

        _connection?.Dispose();
        _connection = null;

        _sessionCts?.Dispose();
        _sessionCts = null;
    }

    private void SetTrayIconConnected(bool connected)
    {
        if (_trayIcon == null) return;
        _trayIcon.Icon = connected ? IconHelper.GetTrayIconConnected() : IconHelper.GetTrayIcon();
        _trayIcon.Text = connected ? Lang.AdminConnectedTray : Lang.TitleClient;
    }

    private void ShowAdminNotification()
    {
        _notification?.Close();
        _notification = new AdminNotificationWindow();
        _notification.Show();
        SetTrayIconConnected(true);
    }

    private void CloseAdminNotification()
    {
        if (_notification != null)
        {
            try { _notification.Close(); } catch { }
            _notification = null;
        }
        SetTrayIconConnected(false);
    }

    private void StartOverlay()
    {
        var monitors = ScreenCapture.GetMonitors();
        var mon = monitors[Math.Min(_currentMonitor, monitors.Count - 1)];
        _overlay = new ClickOverlay();
        _overlay.Show();
        _overlay.SetMonitor(mon);
    }

    /// <summary>
    /// Мониторы отключили/подключили или сменили разрешение/масштаб во время сеанса
    /// (ноутбук унесли со стола) — шлём админу новый список и переставляем оверлей,
    /// без перезапуска программы и компьютера.
    /// </summary>
    private async Task RefreshMonitorsIfChangedAsync(CancellationToken ct)
    {
        _lastMonitorCheck = DateTime.UtcNow;
        var monitors = ScreenCapture.GetMonitors();
        string signature = string.Join(";", monitors.Select(m => $"{m.X},{m.Y},{m.Width},{m.Height}"));
        if (signature == _monitorSignature) return;

        _monitorSignature = signature;
        if (_currentMonitor >= monitors.Count) _currentMonitor = 0;
        _lastCursorMonitor = -1;

        var mon = monitors[_currentMonitor];
        Dispatcher.Invoke(() => _overlay?.SetMonitor(mon));
        await SendMonitorListAsync(monitors, ct);
    }

    private Task SendMonitorListAsync() =>
        SendMonitorListAsync(ScreenCapture.GetMonitors(), _sessionCts?.Token ?? _cts.Token);

    private async Task SendMonitorListAsync(List<System.Drawing.Rectangle> monitors, CancellationToken ct)
    {
        _monitorSignature = string.Join(";", monitors.Select(m => $"{m.X},{m.Y},{m.Width},{m.Height}"));
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(monitors.Count);
        foreach (var m in monitors)
        {
            bw.Write(m.X);
            bw.Write(m.Y);
            bw.Write(m.Width);
            bw.Write(m.Height);
        }
        await _connection!.SendMessageAsync(MessageType.MonitorList, ms.ToArray(), ct);
    }

    private async Task StreamScreenAsync(CancellationToken ct)
    {
        int blockedTicks = 0;
        int errorStreak = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if ((DateTime.UtcNow - _lastMonitorCheck).TotalSeconds >= 1)
                    await RefreshMonitorsIfChangedAsync(ct);

                int cursorMon = ScreenCapture.GetCursorMonitorIndex();
                if (cursorMon != _lastCursorMonitor)
                {
                    _lastCursorMonitor = cursorMon;
                    await _connection!.SendMessageAsync(
                        MessageType.CursorMonitor,
                        BitConverter.GetBytes(cursorMon), ct);
                }

                byte[]? frame = ScreenCapture.TryCaptureMonitor(_currentMonitor);

                if (frame == null)
                {
                    // Экран временно недоступен: окно UAC/брандмауэра (secure desktop),
                    // блокировка компьютера, смена разрешения. Это НЕ причина рвать сессию —
                    // ждём и пробуем снова, а админу раз в секунду шлём "экран недоступен".
                    if (blockedTicks++ % 5 == 0)
                        await _connection!.SendMessageAsync(MessageType.CaptureBlocked, [], ct);
                    await Task.Delay(200, ct);
                    continue;
                }

                blockedTicks = 0;
                errorStreak = 0;

                await _connection!.SendMessageAsync(MessageType.ScreenFrame, frame, ct);
                await Task.Delay(33, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                // Прочие разовые сбои переживаем; если сыплется подряд — соединение точно мертво.
                if (++errorStreak > 25) break;
                try { await Task.Delay(200, ct); } catch { break; }
            }
        }
    }

    private async Task ReceiveCommandsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (type, payload) = await _connection!.ReceiveMessageAsync(ct);

                if (type == MessageType.Disconnect)
                    return;

                // Ошибка обработки команды не должна ронять сессию — рвём только на сети.
                try
                {
                    switch (type)
                    {
                        case MessageType.Click:
                            HandleClick(payload);
                            break;

                        case MessageType.Draw:
                            if (DrawPoint.TryParse(payload, out var point))
                                _overlay?.AddDrawPoint(point);
                            break;

                        case MessageType.SwitchMonitor:
                            await HandleSwitchMonitorAsync(payload);
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (SocketException) { break; }
                catch { }
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private void HandleClick(byte[] payload)
    {
        if (payload.Length < 8) return;
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms);
        int x = br.ReadInt32();
        int y = br.ReadInt32();
        _overlay?.ShowClick(x, y);
    }

    private async Task HandleSwitchMonitorAsync(byte[] payload)
    {
        if (payload.Length < 4) return;
        int index = BitConverter.ToInt32(payload, 0);
        var monitors = ScreenCapture.GetMonitors();
        if (index < 0 || index >= monitors.Count) return;

        _currentMonitor = index;
        Dispatcher.Invoke(() => _overlay?.SetMonitor(monitors[index]));
        await SendMonitorListAsync();
    }

    private async Task FetchExternalIPAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var externalIP = (await http.GetStringAsync("https://api.ipify.org")).Trim();

            var localIPs = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            Dispatcher.Invoke(() =>
            {
                if (!_isConnected)
                {
                    txtClientInfo.Text = string.Format(Lang.PortLabel, _port) + "\n"
                        + string.Format(Lang.LocalIPLabel, string.Join(", ", localIPs)) + "\n"
                        + string.Format(Lang.ExternalIPLabel, externalIP);
                }
            });
        }
        catch
        {
            Dispatcher.Invoke(() =>
            {
                if (!_isConnected)
                {
                    txtClientInfo.Text = string.Format(Lang.PortLabel, _port) + "\n"
                        + string.Format(Lang.LocalIPLabel, "...") + "\n"
                        + Lang.ExternalIPFailed;
                }
            });
        }
    }

    private void BtnStopClient_Click(object sender, RoutedEventArgs e)
    {
        _shutdownRequested = true;
        Shutdown();
    }

    private void Shutdown()
    {
        _shutdownRequested = true;
        _sessionCts?.Cancel();
        _cts.Cancel();
        CleanupSession();
        try { _listener?.Stop(); } catch { }
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _sessionCts?.Cancel();
        _cts.Cancel();
        CleanupSession();
        try { _listener?.Stop(); } catch { }
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosed(e);
    }
}
