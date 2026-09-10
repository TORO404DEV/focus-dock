using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

public partial class MainWindow
{
    private readonly Dictionary<Guid, List<WidgetCard>> pageCards = [];
    private readonly DispatcherTimer statusReset = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer pageSwipePoll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly TranslateTransform pageSlide = new();
    private readonly TranslateTransform previewSlide = new();
    private int currentPageIndex;
    private bool workspacePagesLoaded;
    private bool pageTransitioning;
    private bool pointerWasDown;
    private bool swipeCandidate;
    private bool swipeActive;
    private Point swipeStart;
    private double swipeStartedAt;
    private double swipeOffset;
    private int swipeDirection;
    private int? swipeTargetIndex;
    private bool swipeCreatesPage;
    private bool swipeDestinationAvailable;

    private WorkspacePage CurrentPage => Settings.WorkspacePages[currentPageIndex];
    private WidgetConfig? CurrentTimerWidget => CurrentPage.TimerWidget;
    internal bool CanAddTimerWidget => CurrentTimerWidget is null;
    private bool CanCreateWorkspacePage => Settings.WorkspacePages.All(page => page.HasContent);

    private void InitializeWorkspacePages()
    {
        currentPageIndex = Math.Clamp(Settings.ActiveWorkspacePage, 0, Settings.WorkspacePages.Count - 1);
        cards = [];
        pageTransitioning = true;
        foreach (var config in CurrentPage.Widgets) AddCard(config, false);
        pageTransitioning = false;
        pageCards[CurrentPage.Id] = cards;
        workspacePagesLoaded = true;
        WidgetArea.RenderTransform = pageSlide;
        PagePreviewArea.RenderTransform = previewSlide;
        ArrangeCards();
        foreach (var card in cards) ShowInteractionOverlay(card);
        UpdatePageNavigation();
        pageSwipePoll.Tick += PageSwipePollTick;
        pointerWasDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
        pageSwipePoll.Start();
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
        Settings.Widgets = CurrentPage.Widgets;
        Settings.TimerWidget = CurrentTimerWidget ?? new WidgetConfig { Kind = "timer", Title = "POMODORO" };
        Settings.TimerPositionCustomized = CurrentTimerWidget is not null && CurrentPage.TimerPositionCustomized;
    }

    private void PreviousPageClick(object sender, RoutedEventArgs e) => NavigateWorkspace(-1);
    private void NextPageClick(object sender, RoutedEventArgs e) => NavigateWorkspace(1);
    private void AddPageClick(object sender, RoutedEventArgs e) => AddBlankWorkspacePage();

    private void NavigateWorkspace(int direction)
    {
        int target = currentPageIndex + direction;
        if (target >= 0 && target < Settings.WorkspacePages.Count) SwitchWorkspacePage(target);
        else if (CanCreateWorkspacePage) BeginCarouselTransition(direction, null, true, 0);
        else Pulse(PageDockSurface);
    }

    private bool AddBlankWorkspacePage()
    {
        if (pageTransitioning) return false;
        SaveState();
        if (!CanCreateWorkspacePage)
        {
            Status("AÑADE UN WIDGET A LA PÁGINA VACÍA PARA DESBLOQUEAR OTRA.");
            Pulse(PageDockSurface);
            return false;
        }
        BeginCarouselTransition(1, null, true, 0);
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
        ArrangeCards();
        UpdatePageNavigation();
        SaveState();
        AnimateWidgetArrival(TimerFrame);
    }

    private void RemoveTimerClick(object sender, RoutedEventArgs e)
    {
        RemoveTimerWidget();
        e.Handled = true;
    }

    private void RemoveTimerWidget()
    {
        if (CurrentTimerWidget is null) return;
        CancelTimerGesture();
        HideTimerOverlay();
        CurrentPage.TimerWidget = null;
        CurrentPage.TimerPositionCustomized = false;
        TimerFrame.Visibility = Visibility.Collapsed;
        ArrangeCards();
        UpdatePageNavigation();
        SaveState();
    }

    private void SwitchWorkspacePage(int target)
    {
        if (pageTransitioning || target < 0 || target >= Settings.WorkspacePages.Count || target == currentPageIndex) return;
        BeginCarouselTransition(target > currentPageIndex ? 1 : -1, target, false, 0);
    }

    private void BeginCarouselTransition(int direction, int? target, bool createPage, double initialOffset)
    {
        if (pageTransitioning) return;
        PrepareCarousel(direction, target, createPage);
        swipeOffset = initialOffset;
        pageSlide.X = initialOffset;
        previewSlide.X = initialOffset + direction * Math.Max(1, CarouselViewport.ActualWidth);
        if (Settings.ReduceMotion)
        {
            CommitWorkspacePageChange(direction, target, createPage);
            return;
        }
        AnimateCarousel(-direction * Math.Max(1, CarouselViewport.ActualWidth), 0, true);
    }

    private void PrepareCarousel(int direction, int? target, bool createPage)
    {
        SaveState();
        CancelTimerGesture();
        HideTimerOverlay();
        foreach (var card in cards)
        {
            HideInteractionOverlay(card);
            HideOverlay(card);
            card.SetCarouselTransition(true);
        }
        pageTransitioning = true;
        swipeDirection = direction;
        swipeTargetIndex = target;
        swipeCreatesPage = createPage;
        swipeDestinationAvailable = target is not null || createPage;
        RenderPagePreview(target is { } index ? Settings.WorkspacePages[index] : null);
    }

    private void CommitWorkspacePageChange(int direction, int? target, bool createPage)
    {
        var oldPage = CurrentPage;
        pageCards[oldPage.Id] = cards;
        foreach (var card in cards) card.Visibility = Visibility.Collapsed;
        TimerFrame.Visibility = Visibility.Collapsed;

        if (createPage)
        {
            var page = new WorkspacePage { Name = $"PÁGINA {Settings.WorkspacePages.Count + 1:00}" };
            if (direction < 0)
            {
                Settings.WorkspacePages.Insert(0, page);
                target = 0;
            }
            else
            {
                Settings.WorkspacePages.Add(page);
                target = Settings.WorkspacePages.Count - 1;
            }
        }
        if (target is null) { ResetCarousel(true); return; }

        currentPageIndex = target.Value;
        Settings.ActiveWorkspacePage = currentPageIndex;
        if (!pageCards.TryGetValue(CurrentPage.Id, out var nextCards))
        {
            cards = [];
            foreach (var config in CurrentPage.Widgets) AddCard(config, false);
            pageCards[CurrentPage.Id] = cards;
        }
        else cards = nextCards;
        foreach (var card in cards)
        {
            card.SetCarouselTransition(false);
            card.Visibility = Visibility.Visible;
        }
        ResetCarousel(false);
        ArrangeCards();
        UpdatePageNavigation();
        SaveState();
        foreach (var card in cards) ShowInteractionOverlay(card);
        UpdateOverlayPositions();
    }

    private void RenderPagePreview(WorkspacePage? page)
    {
        PagePreviewArea.Children.Clear();
        PagePreviewArea.Width = Math.Max(1, CarouselViewport.ActualWidth);
        PagePreviewArea.Height = Math.Max(1, CarouselViewport.ActualHeight);
        PagePreviewArea.Visibility = Visibility.Visible;
        if (page is null) return;
        foreach (var config in page.Widgets) AddPreviewCard(config, false);
        if (page.TimerWidget is { } timer) AddPreviewCard(timer, true);
    }

    private void AddPreviewCard(WidgetConfig config, bool timer)
    {
        double viewportWidth = Math.Max(360, CarouselViewport.ActualWidth);
        double viewportHeight = Math.Max(300, CarouselViewport.ActualHeight);
        double minWidth = timer ? 360 : 220;
        double minHeight = timer ? 300 : 90;
        double width = Math.Clamp(config.Width > 0 ? config.Width : timer ? 560 : 300, minWidth, viewportWidth);
        double height = Math.Clamp(config.Height > 0 ? config.Height : timer ? 360 : 260, minHeight, viewportHeight);
        double x = Math.Clamp(config.X, 0, Math.Max(0, viewportWidth - width));
        double y = Math.Clamp(config.Y, 0, Math.Max(0, viewportHeight - height));
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        shell.RowDefinitions.Add(new RowDefinition());
        var header = new Border { Background = (Brush)Application.Current.Resources["Ink"] };
        header.Child = new TextBlock
        {
            Text = timer ? "POMODORO" : $"{config.Kind.ToUpperInvariant()} / {config.Title}",
            Foreground = (Brush)Application.Current.Resources["Paper"], FontSize = 9, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis
        };
        shell.Children.Add(header);
        var body = new Grid { Background = timer ? PhaseBrush() : (Brush)Application.Current.Resources["Surface"] };
        body.Children.Add(new TextBlock
        {
            Text = timer ? ClockText.Text : config.Kind == "window" ? "APP ABIERTA" : config.Title.ToUpperInvariant(),
            FontFamily = timer ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
            FontSize = timer ? 54 : 16, FontWeight = FontWeights.Bold, Opacity = .45,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(body, 1); shell.Children.Add(body);
        var frame = new Border
        {
            Width = width, Height = height, BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(1.5), Background = (Brush)Application.Current.Resources["Surface"],
            Child = shell, Opacity = .88
        };
        Canvas.SetLeft(frame, x); Canvas.SetTop(frame, y); PagePreviewArea.Children.Add(frame);
    }

    private void PageSwipePollTick(object? sender, EventArgs e)
    {
        if (!workspacePagesLoaded || exiting || !IsEnabled || WindowState == WindowState.Minimized) return;
        bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!TryGetCanvasPointer(out var point))
        {
            if (!down && pointerWasDown) EndPointerSwipe();
            pointerWasDown = down;
            return;
        }
        if (down && !pointerWasDown)
        {
            pointerWasDown = true;
            if (CanBeginSwipe(point))
            {
                swipeCandidate = true;
                swipeStart = point;
                swipeStartedAt = Monotonic;
            }
            return;
        }
        if (down && pointerWasDown && swipeCandidate) UpdatePointerSwipe(point);
        else if (!down && pointerWasDown) EndPointerSwipe();
        pointerWasDown = down;
    }

    private bool TryGetCanvasPointer(out Point point)
    {
        point = default;
        if (!Win32.GetCursorPos(out var screen)) return false;
        try { point = CarouselViewport.PointFromScreen(new Point(screen.X, screen.Y)); }
        catch (InvalidOperationException) { return false; }
        return point.X >= 0 && point.Y >= 0 && point.X <= CarouselViewport.ActualWidth && point.Y <= CarouselViewport.ActualHeight;
    }

    private bool CanBeginSwipe(Point point)
    {
        if (pageTransitioning || IsReportModalOpen || timerResizing || cards.Any(card => card.IsGestureActive)) return false;
        return !IsInteractiveSwipeSource(WidgetArea.InputHitTest(point) as DependencyObject);
    }

    private static bool IsInteractiveSwipeSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or PasswordBox or Selector or ScrollBar or Slider or Thumb) return true;
            if (current is FrameworkElement element && element.Cursor == Cursors.SizeAll) return true;
        }
        return false;
    }

    private void UpdatePointerSwipe(Point point)
    {
        double dx = point.X - swipeStart.X;
        double dy = point.Y - swipeStart.Y;
        if (!swipeActive)
        {
            if (Math.Abs(dy) > 22 && Math.Abs(dy) > Math.Abs(dx)) { swipeCandidate = false; return; }
            if (Math.Abs(dx) < 12 || Math.Abs(dx) < Math.Abs(dy) * 1.15) return;
            swipeActive = true;
            ConfigurePointerSwipe(dx < 0 ? 1 : -1);
        }
        int newDirection = dx < 0 ? 1 : -1;
        if (newDirection != swipeDirection) ConfigurePointerSwipe(newDirection);
        double width = Math.Max(1, CarouselViewport.ActualWidth);
        if (swipeDestinationAvailable)
        {
            swipeOffset = Math.Clamp(dx, -width, width);
            pageSlide.X = swipeOffset;
            previewSlide.X = swipeOffset + swipeDirection * width;
        }
        else
        {
            swipeOffset = Math.Sign(dx) * Math.Min(width * .10, Math.Sqrt(Math.Abs(dx)) * 5);
            pageSlide.X = swipeOffset;
            PagePreviewArea.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfigurePointerSwipe(int direction)
    {
        int target = currentPageIndex + direction;
        int? targetIndex = target >= 0 && target < Settings.WorkspacePages.Count ? target : null;
        bool create = targetIndex is null && CanCreateWorkspacePage;
        if (!pageTransitioning) PrepareCarousel(direction, targetIndex, create);
        else
        {
            swipeDirection = direction;
            swipeTargetIndex = targetIndex;
            swipeCreatesPage = create;
            swipeDestinationAvailable = targetIndex is not null || create;
            RenderPagePreview(targetIndex is { } index ? Settings.WorkspacePages[index] : null);
        }
    }

    private void EndPointerSwipe()
    {
        pointerWasDown = false;
        swipeCandidate = false;
        if (!swipeActive) return;
        swipeActive = false;
        double elapsed = Math.Max(.016, Monotonic - swipeStartedAt);
        double threshold = Math.Min(150, Math.Max(58, CarouselViewport.ActualWidth * .16));
        bool commit = swipeDestinationAvailable &&
            (Math.Abs(swipeOffset) >= threshold || Math.Abs(swipeOffset / elapsed) >= 720 && Math.Abs(swipeOffset) >= 30);
        double width = Math.Max(1, CarouselViewport.ActualWidth);
        if (Settings.ReduceMotion)
        {
            if (commit) CommitWorkspacePageChange(swipeDirection, swipeTargetIndex, swipeCreatesPage);
            else ResetCarousel(true);
            return;
        }
        AnimateCarousel(commit ? -swipeDirection * width : 0, commit ? 0 : swipeDirection * width, commit);
    }

    private void AnimateCarousel(double pageTarget, double previewTarget, bool commit)
    {
        double width = Math.Max(1, CarouselViewport.ActualWidth);
        double distance = Math.Abs(pageTarget - pageSlide.X);
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(110 + 130 * distance / width, 110, 240));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pageAnimation = new DoubleAnimation(pageSlide.X, pageTarget, duration) { EasingFunction = ease };
        var previewAnimation = new DoubleAnimation(previewSlide.X, previewTarget, duration) { EasingFunction = ease };
        pageAnimation.Completed += (_, _) =>
        {
            pageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            previewSlide.BeginAnimation(TranslateTransform.XProperty, null);
            if (commit) CommitWorkspacePageChange(swipeDirection, swipeTargetIndex, swipeCreatesPage);
            else ResetCarousel(true);
        };
        pageSlide.BeginAnimation(TranslateTransform.XProperty, pageAnimation);
        previewSlide.BeginAnimation(TranslateTransform.XProperty, previewAnimation);
    }

    private void ResetCarousel(bool restoreOverlays)
    {
        pageSlide.BeginAnimation(TranslateTransform.XProperty, null);
        previewSlide.BeginAnimation(TranslateTransform.XProperty, null);
        pageSlide.X = 0;
        previewSlide.X = 0;
        PagePreviewArea.Visibility = Visibility.Collapsed;
        PagePreviewArea.Children.Clear();
        pageTransitioning = false;
        swipeDestinationAvailable = false;
        swipeTargetIndex = null;
        swipeCreatesPage = false;
        if (!restoreOverlays) return;
        ArrangeCards();
        foreach (var card in cards)
        {
            card.SetCarouselTransition(false);
            ShowInteractionOverlay(card);
        }
        UpdateOverlayPositions();
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
                Content = active ? "●" : "•", Width = 22, Height = 26, Padding = new Thickness(0), Margin = new Thickness(1, 0, 1, 0),
                FontFamily = new FontFamily("Consolas"), FontSize = active ? 12 : 9, FontWeight = FontWeights.Bold,
                Background = active ? (Brush)Application.Current.Resources["Accent"] : Brushes.Transparent,
                Foreground = active ? (Brush)Application.Current.Resources["Ink"] : (Brush)Application.Current.Resources["Paper"],
                BorderThickness = new Thickness(0),
                ToolTip = $"Página {i + 1:00} · {page.WidgetCount} {(page.WidgetCount == 1 ? "widget" : "widgets")}",
            };
            AutomationProperties.SetName(button, $"Abrir página {i + 1}");
            button.Click += (_, _) => SwitchWorkspacePage(index);
            PageButtonStrip.Children.Add(button);
        }
        bool unlocked = CanCreateWorkspacePage;
        PreviousPageButton.IsEnabled = currentPageIndex > 0 || unlocked;
        NextPageButton.IsEnabled = currentPageIndex < Settings.WorkspacePages.Count - 1 || unlocked;
        AddPageButton.IsEnabled = unlocked;
        PreviousPageButton.ToolTip = currentPageIndex > 0 ? "Página anterior · desliza hacia la derecha" : unlocked ? "Crear página a la izquierda" : "Completa esta página para crear otra";
        NextPageButton.ToolTip = currentPageIndex < Settings.WorkspacePages.Count - 1 ? "Página siguiente · desliza hacia la izquierda" : unlocked ? "Crear página a la derecha" : "Completa esta página para crear otra";
        AddPageButton.ToolTip = unlocked ? "Añadir una página vacía a la derecha" : "Añade al menos un widget a cada página para desbloquear otra";
        ToolTipService.SetShowOnDisabled(AddPageButton, true);
        ToolTipService.SetShowOnDisabled(PreviousPageButton, true);
        ToolTipService.SetShowOnDisabled(NextPageButton, true);
        UpdatePageMeta();
    }

    private void UpdatePageMeta()
    {
        if (!workspacePagesLoaded) return;
        PageMetaText.Text = $"PÁGINA {currentPageIndex + 1:00} / {Settings.WorkspacePages.Count:00}";
        PageDockSurface.ToolTip = CurrentPage.HasContent
            ? $"Página {currentPageIndex + 1} de {Settings.WorkspacePages.Count} · {CurrentPage.WidgetCount} widgets · arrastra el canvas para navegar"
            : "Página vacía · añade un widget o desliza para volver";
    }

    internal int WorkspacePageCount => Settings.WorkspacePages.Count;
    internal WorkspacePage CurrentWorkspacePageForDiagnostics => CurrentPage;
    internal bool CurrentWorkspacePageIsBlank => !CurrentPage.HasContent;
    internal bool CurrentWorkspacePageHasTimer => CurrentTimerWidget is not null;
    internal bool EmptyPageIsFrameless => Welcome.BorderThickness == new Thickness(0);
    internal bool AddPageForDiagnostics() => AddBlankWorkspacePage();
    internal void AddTimerForDiagnostics() => AddTimerWidget();
    internal void RemoveTimerForDiagnostics() => RemoveTimerWidget();
    internal void SwitchPageForDiagnostics(int index) => SwitchWorkspacePage(index);
    internal void NavigatePageForDiagnostics(int direction) => NavigateWorkspace(direction);
    internal bool ShowSwipeMidpointForDiagnostics(int direction)
    {
        int target = currentPageIndex + direction;
        if (pageTransitioning || target < 0 || target >= Settings.WorkspacePages.Count) return false;
        PrepareCarousel(direction, target, false);
        swipeActive = true;
        swipeOffset = -direction * Math.Max(1, CarouselViewport.ActualWidth) * .48;
        pageSlide.X = swipeOffset;
        previewSlide.X = swipeOffset + direction * Math.Max(1, CarouselViewport.ActualWidth);
        return PagePreviewArea.Visibility == Visibility.Visible && Math.Abs(pageSlide.X) > 20;
    }
    internal void CancelSwipeForDiagnostics()
    {
        swipeActive = false;
        swipeCandidate = false;
        ResetCarousel(true);
    }
}
