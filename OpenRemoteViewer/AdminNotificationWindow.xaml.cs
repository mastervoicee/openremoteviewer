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
