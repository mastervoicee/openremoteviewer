using System.Windows;
using System.Windows.Media;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;

namespace OpenRemoteViewer;

/// <summary>
/// The always-visible version / update button used by the main and the admin window.
/// Normal state: "v1.1.0 · Check for updates" — a click checks right now.
/// When a newer release exists the button turns green ("Update to v1.2.0"),
/// a notification pops up once, and a click installs the update.
/// </summary>
public sealed class UpdatePrompt
{
    private static readonly SolidColorBrush Green = new(WpfColor.FromRgb(0x2E, 0x7D, 0x32));

    private readonly WpfButton _button;
    private readonly Func<IReadOnlyDictionary<string, string>> _resume;
    private readonly Action _beforeExit;
    private readonly CancellationToken _ct;
    private readonly System.Windows.Media.Brush _idleBackground;
    private readonly System.Windows.Media.Brush _idleForeground;

    private UpdateInfo? _update;
    private bool _busy;
    private bool _notified;

    /// <param name="resume">Environment for the restarted copy (role, address, password).</param>
    /// <param name="beforeExit">Close connections / windows right before the app exits.</param>
    public UpdatePrompt(WpfButton button,
        Func<IReadOnlyDictionary<string, string>> resume, Action beforeExit, CancellationToken ct)
    {
        _button = button;
        _resume = resume;
        _beforeExit = beforeExit;
        _ct = ct;
        _idleBackground = button.Background;
        _idleForeground = button.Foreground;

        ShowIdle(Lang.CheckUpdates);
        _button.Click += async (_, _) => await OnClickAsync();
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                if (!_busy && _update == null)
                    await CheckAsync(manual: false);
                await Task.Delay(Updater.CheckInterval, _ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckAsync(bool manual)
    {
        if (manual) _button.Content = Lang.Checking;

        var info = await Updater.CheckAsync(_ct);
        if (info == null)
        {
            // при фоновой проверке надпись не трогаем, при ручной — отвечаем явно
            if (manual) ShowIdle(Lang.UpToDate);
            return;
        }

        _update = info;
        ShowAvailable(info);

        if (!_notified)
        {
            _notified = true;
            new AdminNotificationWindow(
                string.Format(Lang.UpdateToastTitle, info.VersionText),
                Lang.UpdateToastText, "⬆", autoCloseSeconds: 15).Show();
        }
    }

    private async Task OnClickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_update == null)
            {
                await CheckAsync(manual: true);
                return;
            }

            var info = _update;
            _button.Content = Lang.Updating;
            try
            {
                await Updater.ApplyAndRestartAsync(info, _resume(), _ct);
                _beforeExit();
                System.Windows.Application.Current.Shutdown();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShowAvailable(info);
                System.Windows.MessageBox.Show(string.Format(Lang.UpdateFailed, ex.Message),
                    Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _busy = false;
        }
    }

    private void ShowIdle(string format)
    {
        _button.Content = string.Format(format, Updater.CurrentText);
        _button.Background = _idleBackground;
        _button.Foreground = _idleForeground;
        _button.FontWeight = FontWeights.Normal;
    }

    private void ShowAvailable(UpdateInfo info)
    {
        _button.Content = string.Format(Lang.UpdateAvailable, info.VersionText);
        _button.Background = Green;
        _button.Foreground = System.Windows.Media.Brushes.White;
        _button.FontWeight = FontWeights.SemiBold;
    }
}
