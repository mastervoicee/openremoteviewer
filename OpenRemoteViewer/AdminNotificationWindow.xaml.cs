using System.Windows;

namespace OpenRemoteViewer;

public partial class AdminNotificationWindow : Window
{
    /// <summary>
    /// "Admin connected" notice on the client. It hides by itself: while the admin
    /// is watching, the tray icon keeps showing it, so the card need not stay on screen.
    /// </summary>
    public AdminNotificationWindow()
    {
        InitializeComponent();
        txtNotifTitle.Text = Lang.NotifAdminConnected;
        txtNotifMessage.Text = Lang.NotifScreenViewed;
        btnClose.ToolTip = Lang.NotifClose;
        CloseAfter(8);
    }

    /// <summary>A general notice (e.g. about an update) that closes by itself.</summary>
    public AdminNotificationWindow(string title, string message, string icon, int autoCloseSeconds)
    {
        InitializeComponent();
        txtNotifTitle.Text = title;
        txtNotifMessage.Text = message;
        iconEye.Visibility = Visibility.Collapsed;
        txtNotifIcon.Text = icon;
        txtNotifIcon.Visibility = Visibility.Visible;
        btnClose.ToolTip = Lang.NotifClose;
        CloseAfter(autoCloseSeconds);
    }

    private void CloseAfter(int seconds)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds)
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
