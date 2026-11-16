using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenRemoteViewer;

public class LangItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Flag { get; set; } = "";
}

public partial class MainWindow : Window
{
    private bool _loading = true;

    private static readonly Dictionary<string, string> Flags = new()
    {
        ["en"] = "🇬🇧", ["ru"] = "🇷🇺", ["de"] = "🇩🇪", ["es"] = "🇪🇸",
        ["fr"] = "🇫🇷", ["pl"] = "🇵🇱", ["cs"] = "🇨🇿", ["lt"] = "🇱🇹", ["lv"] = "🇱🇻", ["et"] = "🇪🇪"
    };

    public MainWindow()
    {
        InitializeComponent();
        Icon = IconHelper.GetWpfIcon();

        foreach (var (code, name) in Lang.Available)
            cmbLanguage.Items.Add(new LangItem
            {
                Code = code,
                Name = name,
                Flag = Flags.GetValueOrDefault(code, "🌐")
            });

        LoadSettings();
        UpdateUI();
        ApplyLanguage();
        _loading = false;

        Closed += (_, _) => _updateCts.Cancel();
        _ = new UpdatePrompt(btnUpdate, () => new Dictionary<string, string>(), () => { }, _updateCts.Token);
    }

    private readonly CancellationTokenSource _updateCts = new();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadSettings()
    {
        var s = AppSettings.Load();
        Lang.Set(s.Language);

        for (int i = 0; i < cmbLanguage.Items.Count; i++)
        {
            if (((LangItem)cmbLanguage.Items[i]).Code == s.Language)
            {
                cmbLanguage.SelectedIndex = i;
                break;
            }
        }

        if (!s.RememberSettings) return;

        if (s.Role == "Admin")
            rbAdmin.IsChecked = true;
        else
            rbClient.IsChecked = true;

        txtIP.Text = s.IP;
        txtPort.Text = s.Port.ToString();
        txtPortClient.Text = s.Port.ToString();
        txtPassword.Password = s.GetPassword();
        chkRemember.IsChecked = s.RememberSettings;
        chkAutoStart.IsChecked = s.AutoStart;
    }

    private void ApplyLanguage()
    {
        txtRoleAdmin.Text = Lang.RoleAdmin.Trim();
        txtRoleClient.Text = Lang.RoleClient.Trim();
        lblIP.Text = Lang.LabelIP.TrimEnd(':').ToUpperInvariant();
        lblPort.Text = Lang.LabelPort.TrimEnd(':').ToUpperInvariant();
        lblPortOnly.Text = Lang.LabelPort.TrimEnd(':').ToUpperInvariant();
        lblPassword.Text = Lang.LabelPassword.TrimEnd(':').ToUpperInvariant();
        lblLanguage.Text = Lang.LabelLanguage.TrimEnd(':').ToUpperInvariant();
        txtRemember.Text = Lang.RememberSettings.Trim();
        txtAutoStart.Text = Lang.AutoStart.Trim();
        btnStart.Content = Lang.BtnStart;
        txtStatus.Text = Lang.WaitingConnection;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (cmbLanguage.SelectedItem is LangItem item)
        {
            Lang.Set(item.Code);
            if (!_loading)
                ApplyLanguage();
        }
    }

    private string GetPort()
    {
        return rbAdmin.IsChecked == true ? txtPort.Text.Trim() : txtPortClient.Text.Trim();
    }

    private void SaveSettings(string password)
    {
        var s = new AppSettings
        {
            Role = rbAdmin.IsChecked == true ? "Admin" : "Client",
            IP = txtIP.Text.Trim(),
            Port = int.TryParse(GetPort(), out int p) ? p : 9876,
            RememberSettings = chkRemember.IsChecked == true,
            AutoStart = chkAutoStart.IsChecked == true,
            Language = Lang.Code
        };

        if (s.RememberSettings)
            s.SetPassword(password);

        s.Save();
        s.ApplyAutoStart();
    }

    private void Role_Changed(object sender, RoutedEventArgs e) => UpdateUI();

    private void UpdateUI()
    {
        if (panelIP == null) return;
        bool isAdmin = rbAdmin.IsChecked == true;
        panelIP.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        panelPortOnly.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

        if (isAdmin)
            txtPort.Text = txtPortClient.Text;
        else
            txtPortClient.Text = txtPort.Text;
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var password = txtPassword.Password.Trim();
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show(Lang.ErrEnterPassword, Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(GetPort(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show(Lang.ErrInvalidPort, Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings(password);

        if (rbAdmin.IsChecked == true)
        {
            var ip = txtIP.Text.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show(Lang.ErrEnterIP, Lang.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            new AdminWindow(ip, port, password).Show();
        }
        else
        {
            new ClientWindow(port, password).Show();
        }

        Close();
    }
}
