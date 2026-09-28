using System.Windows;

namespace OpenRemoteViewer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        Lang.Set(settings.Language);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(string.Format(Lang.ErrorPrefix, args.Exception.Message),
                Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Contains("--autoconnect"))
        {
            if (settings.RememberSettings && !string.IsNullOrEmpty(settings.GetPassword()))
            {
                if (settings.Role == "Admin" && !string.IsNullOrEmpty(settings.IP))
                {
                    new AdminWindow(settings.IP, settings.Port, settings.GetPassword()).Show();
                    return;
                }
                if (settings.Role == "Client")
                {
                    new ClientWindow(settings.Port, settings.GetPassword(), startMinimized: true).Show();
                    return;
                }
            }
        }

        new MainWindow().Show();
    }
}
