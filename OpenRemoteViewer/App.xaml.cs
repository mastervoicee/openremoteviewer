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

        Updater.CleanupOldVersion();

        // Продолжение работы после самообновления: роль и параметры пришли от прежней копии.
        if (e.Args.Contains(Updater.ResumeArg))
        {
            var role = Environment.GetEnvironmentVariable(Updater.EnvRole);
            var host = Environment.GetEnvironmentVariable(Updater.EnvHost) ?? "";
            var pass = Environment.GetEnvironmentVariable(Updater.EnvPassword) ?? "";
            if (int.TryParse(Environment.GetEnvironmentVariable(Updater.EnvPort), out int port)
                && !string.IsNullOrEmpty(pass))
            {
                if (role == "Admin" && !string.IsNullOrEmpty(host))
                {
                    new AdminWindow(host, port, pass).Show();
                    return;
                }
                if (role == "Client")
                {
                    new ClientWindow(port, pass, startMinimized: true).Show();
                    return;
                }
            }
        }

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
