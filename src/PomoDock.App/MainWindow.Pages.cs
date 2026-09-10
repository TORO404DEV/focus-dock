using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

public partial class MainWindow
{
    private readonly Dictionary<Guid, List<WidgetCard>> pageCards = [];
    private readonly DispatcherTimer statusReset = new() { Interval = TimeSpan.FromSeconds(3) };
    private int currentPageIndex;
    private bool workspacePagesLoaded;
    private bool pageTransitioning;

    private WorkspacePage CurrentPage => Settings.WorkspacePages[currentPageIndex];
    private WidgetConfig? CurrentTimerWidget => CurrentPage.TimerWidget;
    internal bool CanAddTimerWidget => CurrentTimerWidget is null;

    private void InitializeWorkspacePages()
    {
        currentPageIndex = Math.Clamp(Settings.ActiveWorkspacePage, 0, Settings.WorkspacePages.Count - 1);
        cards = [];
        pageTransitioning = true;
        foreach (var config in CurrentPage.Widgets) AddCard(config, false);
        pageTransitioning = false;
        pageCards[CurrentPage.Id] = cards;
        workspacePagesLoaded = true;
        ArrangeCards();
        foreach (var card in cards) ShowInteractionOverlay(card);
        UpdatePageNavigation();
    }

    private void SaveCurrentWorkspacePage()
    {
        if (!workspacePagesLoaded) return;
        CurrentPage.Widgets = cards.Select(card => card.Config).ToList();
        if (CurrentTimerWidget is { } timer && !double.IsNaN(Canvas.GetLeft(TimerFrame)))
        {
            timer.X = Canvas.GetLeft(TimerFrame);
            timer.Y = Canvas.GetTop(TimerFrame);
            timer.Width = TimerFrame.Width;
            timer.Height = TimerFrame.Height;
        }
        Settings.ActiveWorkspacePage = currentPageIndex;
        // Keep the previous single-page fields current for old backups and
        // installations that have not learned about workspace pages yet.
        Settings.Widgets = CurrentPage.Widgets;
        if (CurrentTimerWidget is not null)
        {
            Settings.TimerWidget = CurrentTimerWidget;
            Settings.TimerPositionCustomized = CurrentPage.TimerPositionCustomized;
        }
    }

    private void PreviousPageClick(object sender, RoutedEventArgs e) => SwitchWorkspacePage(currentPageIndex - 1);
    private void NextPageClick(object sender, RoutedEventArgs e) => SwitchWorkspacePage(currentPageIndex + 1);
    private void AddPageClick(object sender, RoutedEventArgs e) => AddBlankWorkspacePage();

    private bool AddBlankWorkspacePage()
    {
        if (pageTransitioning) return false;
        SaveState();
        if (Settings.WorkspacePages.Any(page => !page.HasContent))
        {
            Status("AÑADE UN WIDGET A LA PÁGINA VACÍA PARA DESBLOQUEAR OTRA.");
            Pulse(AddPageButton);
            return false;
        }
        var page = new WorkspacePage { Name = $"PÁGINA {Settings.WorkspacePages.Count + 1:00}" };
        Settings.WorkspacePages.Add(page);
        SwitchWorkspacePage(Settings.WorkspacePages.Count - 1);
        return true;
    }

    private void AddTimerWidget()
    {
        if (CurrentTimerWidget is not null)
        {
            Status("ESTA PÁGINA YA TIENE UN TEMPORIZADOR.");
            return;
        }
        CurrentPage.TimerWidget = new WidgetConfig { Kind = "timer", Title = "POMODORO" };
        CurrentPage.TimerPositionCustomized = false;
        TimerFrame.Visibility = Visibility.Visible;
        ArrangeTimerWidget();
        UpdatePageNavigation();
        SaveState();
        AnimateWidgetArrival(TimerFrame);
    }

    private void SwitchWorkspacePage(int target)
    {
        if (pageTransitioning || target < 0 || target >= Settings.WorkspacePages.Count || target == currentPageIndex) return;
        int direction = target > currentPageIndex ? 1 : -1;
        SaveState();
        CancelTimerGesture();
        HideTimerOverlay();
        foreach (var card in cards)
        {
            HideInteractionOverlay(card);
            HideOverlay(card);
            card.Visibility = Visibility.Collapsed;
        }
        TimerFrame.Visibility = Visibility.Collapsed;
        pageCards[CurrentPage.Id] = cards;

        currentPageIndex = target;
        Settings.ActiveWorkspacePage = target;
        pageTransitioning = true;
        if (!pageCards.TryGetValue(CurrentPage.Id, out var nextCards))
        {
            cards = [];
            foreach (var config in CurrentPage.Widgets) AddCard(config, false);
            pageCards[CurrentPage.Id] = cards;
        }
        else cards = nextCards;
        foreach (var card in cards) card.Visibility = Visibility.Visible;
        ArrangeCards();
        UpdatePageNavigation();
        SaveState();
        AnimatePageArrival(direction);
    }

    private void AnimatePageArrival(int direction)
    {
        void Finish()
        {
            WidgetArea.BeginAnimation(OpacityProperty, null);
            WidgetArea.Opacity = 1;
            WidgetArea.RenderTransform = Transform.Identity;
            WidgetArea.IsHitTestVisible = true;
            pageTransitioning = false;
            foreach (var card in cards) ShowInteractionOverlay(card);
            UpdateOverlayPositions();
        }
        WidgetArea.IsHitTestVisible = false;
        if (Settings.ReduceMotion) { Finish(); return; }
        double offset = Math.Min(90, Math.Max(38, WidgetArea.ActualWidth * .10)) * direction;
        var scale = new ScaleTransform(.965, .965);
        var slide = new TranslateTransform(offset, 0);
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(slide);
        WidgetArea.RenderTransform = transforms;
        WidgetArea.Opacity = .30;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.965, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.965, 1, duration) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset, 0, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(.30, 1, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => Finish();
        WidgetArea.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateWidgetArrival(FrameworkElement element)
    {
        if (Settings.ReduceMotion) return;
        element.RenderTransformOrigin = new Point(.5, .5);
        var scale = new ScaleTransform(.94, .94);
        element.RenderTransform = scale;
        element.Opacity = .35;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.94, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.94, 1, duration) { EasingFunction = ease });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, duration) { EasingFunction = ease });
    }

    private static void Pulse(FrameworkElement element)
    {
        var animation = new DoubleAnimation(1, .45, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private void UpdatePageNavigation()
    {
        if (!workspacePagesLoaded) return;
        PageButtonStrip.Children.Clear();
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            int index = i;
            var page = Settings.WorkspacePages[i];
            bool active = i == currentPageIndex;
            var button = new Button
            {
                Content = $"{(active ? "●" : "○")} {i + 1:00}",
                Width = 48,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 2, 0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Background = active ? (Brush)Application.Current.Resources["Accent"] : Brushes.Transparent,
                Foreground = active ? (Brush)Application.Current.Resources["Ink"] : (Brush)Application.Current.Resources["Paper"],
                BorderBrush = (Brush)Application.Current.Resources["Paper"],
                ToolTip = $"{page.Name} · {page.WidgetCount} {(page.WidgetCount == 1 ? "widget" : "widgets")}" 
            };
            AutomationProperties.SetName(button, $"Abrir {page.Name}");
            button.Click += (_, _) => SwitchWorkspacePage(index);
            PageButtonStrip.Children.Add(button);
        }
        PreviousPageButton.IsEnabled = currentPageIndex > 0;
        NextPageButton.IsEnabled = currentPageIndex < Settings.WorkspacePages.Count - 1;
        bool unlocked = Settings.WorkspacePages.All(page => page.HasContent);
        AddPageButton.IsEnabled = unlocked;
        AddPageButton.Background = unlocked ? (Brush)Application.Current.Resources["Accent"] : (Brush)Application.Current.Resources["Surface"];
        AddPageButton.ToolTip = unlocked ? "Añadir una página en blanco" : "Añade al menos un widget a cada página para desbloquear otra";
        ToolTipService.SetShowOnDisabled(AddPageButton, true);
        UpdatePageMeta();
    }

    private void UpdatePageMeta()
    {
        if (!workspacePagesLoaded) return;
        var page = CurrentPage;
        PageMetaText.Text = page.HasContent
            ? $"{page.Name}  ·  {page.WidgetCount:00} {(page.WidgetCount == 1 ? "WIDGET" : "WIDGETS")}"
            : $"{page.Name}  ·  AÑADE 1 WIDGET";
    }

    internal int WorkspacePageCount => Settings.WorkspacePages.Count;
    internal WorkspacePage CurrentWorkspacePageForDiagnostics => CurrentPage;
    internal bool CurrentWorkspacePageIsBlank => !CurrentPage.HasContent;
    internal bool CurrentWorkspacePageHasTimer => CurrentTimerWidget is not null;
    internal bool AddPageForDiagnostics() => AddBlankWorkspacePage();
    internal void AddTimerForDiagnostics() => AddTimerWidget();
    internal void SwitchPageForDiagnostics(int index) => SwitchWorkspacePage(index);
}
