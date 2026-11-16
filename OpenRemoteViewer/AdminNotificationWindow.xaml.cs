using System.Windows;

namespace OpenRemoteViewer;

public partial class AdminNotificationWindow : Window
{
    public AdminNotificationWindow()
    {
        InitializeComponent();
        txtNotifTitle.Text = Lang.NotifAdminConnected;
        txtNotifMessage.Text = Lang.NotifScreenViewed;
        btnClose.ToolTip = Lang.NotifClose;
    }

    /// <summary>A general notice (e.g. about an update) that closes by itself.</summary>
    public AdminNotificationWindow(string title, string message, string icon, int autoCloseSeconds)
    {
        InitializeComponent();
        txtNotifTitle.Text = title;
        txtNotifMessage.Text = message;
        txtNotifIcon.Text = icon;
        btnClose.ToolTip = Lang.NotifClose;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(autoCloseSeconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width;
        Top = workArea.Bottom - Height;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
