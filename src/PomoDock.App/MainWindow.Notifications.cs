using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

public partial class MainWindow
{
    private NotificationStore notifications = null!;
    private readonly DispatcherTimer notificationBannerLife = new() { Interval = TimeSpan.FromSeconds(7) };

    private void InitializeNotifications()
    {
        notifications = new NotificationStore(Store);
        notifications.Changed += RefreshNotificationChrome;
        notificationBannerLife.Tick += (_, _) => HideNotificationBanner();
        NotificationBanner.MouseEnter += (_, _) => notificationBannerLife.Stop();
        NotificationBanner.MouseLeave += (_, _) =>
        {
            if (NotificationBanner.Visibility == Visibility.Visible) notificationBannerLife.Start();
        };
        NotificationsPopup.Opened += (_, _) =>
        {
            RenderNotificationHistory();
            notifications.MarkAllRead();
            BringPopupToFront(NotificationsPopup);
        };
        RefreshNotificationChrome();
    }

    /// <summary>
    /// Records and surfaces a due reminder. This deliberately does not touch its task: firing a
    /// reminder is information, while finishing the task remains an explicit user decision.
    /// </summary>
    internal void NotificationArrived(ReminderCue cue, DateTime now)
    {
        notifications.Add(cue, now);
        NotificationBannerLead.Text = L.T(cue.Event.TaskId is null
            ? "notifications.bannerEvent"
            : "notifications.bannerTask");
        NotificationBannerTitle.Text = cue.Event.Title;
        NotificationBanner.Visibility = Visibility.Visible;
        NotificationBanner.Opacity = 1;
        if (!Settings.ReduceMotion)
        {
            NotificationBanner.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        NotificationBannerPopup.IsOpen = true;
        BringPopupToFront(NotificationBannerPopup);
        notificationBannerLife.Stop();
        notificationBannerLife.Start();
    }

    private void NotificationsClick(object sender, RoutedEventArgs e)
    {
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
        if (NotificationsPopup.IsOpen) HideNotificationBanner();
        e.Handled = true;
    }

    private void NotificationBannerClick(object sender, MouseButtonEventArgs e)
    {
        HideNotificationBanner();
        NotificationsPopup.IsOpen = true;
        e.Handled = true;
    }

    private void DismissNotificationBanner(object sender, RoutedEventArgs e)
    {
        HideNotificationBanner();
        e.Handled = true;
    }

    private void CloseNotifications(object sender, RoutedEventArgs e)
    {
        NotificationsPopup.IsOpen = false;
        e.Handled = true;
    }

    private void HideNotificationBanner()
    {
        notificationBannerLife.Stop();
        NotificationBannerPopup.IsOpen = false;
        NotificationBanner.BeginAnimation(OpacityProperty, null);
        NotificationBanner.Visibility = Visibility.Collapsed;
    }

    private void CloseNotificationCenter()
    {
        notificationBannerLife.Stop();
        NotificationsPopup.IsOpen = false;
        HideNotificationBanner();
    }

    private void RefreshNotificationChrome()
    {
        if (notifications is null) return;
        int unread = notifications.History.Unread;
        NotificationsButton.Content = unread > 0 ? $"🔔 {Math.Min(unread, 99)}" : "🔔";
        NotificationsButton.ToolTip = unread > 0
            ? L.T("notifications.openUnread", unread)
            : L.T("notifications.open");
        NotificationHistoryTitle.Text = L.T("notifications.title");
        NotificationHistoryMeta.Text = L.T("notifications.meta", notifications.History.Items.Count, unread);
        NotificationsButton.SetValue(AutomationProperties.NameProperty, NotificationsButton.ToolTip?.ToString() ?? L.T("notifications.open"));
        if (NotificationsPopup.IsOpen) RenderNotificationHistory();
    }

    private void RenderNotificationHistory()
    {
        NotificationHistoryList.Children.Clear();
        var items = notifications.History.Items;
        if (items.Count == 0)
        {
            NotificationHistoryList.Children.Add(new TextBlock
            {
                Text = L.T("notifications.empty"),
                Foreground = ResourceBrush("Muted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 16, 8, 20)
            });
            return;
        }

        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var marker = new Border
            {
                Background = item.Read ? ResourceBrush("Line") : ResourceBrush("Accent")
            };
            row.Children.Add(marker);

            string kind = L.T(item.TaskId is null ? "notifications.event" : "notifications.task");
            string delivered = item.DeliveredLocal.ToString("ddd dd MMM · HH:mm", Strings.Culture).ToUpper(Strings.Culture);
            var copy = new StackPanel { Margin = new Thickness(11, 10, 12, 10) };
            copy.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });
            copy.Children.Add(new TextBlock
            {
                Text = $"{kind}  ·  {delivered}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8.5,
                Foreground = ResourceBrush("Muted"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            var frame = new Border
            {
                BorderBrush = ResourceBrush("Edge"),
                BorderThickness = new Thickness(1),
                Background = ResourceBrush("Paper"),
                Child = row
            };
            NotificationHistoryList.Children.Add(frame);
        }
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    internal bool IsNotificationCenterOpen => NotificationsPopup.IsOpen;
    internal int NotificationHistoryCount => notifications.History.Items.Count;
    internal int NotificationUnreadCount => notifications.History.Unread;
    internal bool IsNotificationBannerVisible => NotificationBannerPopup.IsOpen;
    internal FrameworkElement NotificationBannerSurface => NotificationBanner;
    internal FrameworkElement NotificationHistorySurface => (FrameworkElement)NotificationsPopup.Child;
    internal void OpenNotificationsForDiagnostics() => NotificationsPopup.IsOpen = true;
    internal void CloseNotificationsForDiagnostics() => NotificationsPopup.IsOpen = false;
}
