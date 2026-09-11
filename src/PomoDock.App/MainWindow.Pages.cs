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
    private bool navigationWasBlocked;

    private WorkspacePage CurrentPage => Settings.WorkspacePages[currentPageIndex];
    private WidgetConfig? CurrentTimerWidget => CurrentPage.TimerWidget;
    internal bool CanAddTimerWidget => CurrentTimerWidget is null;
    /// <summary>
    /// A new page can open past either end of the carousel as long as the page already sitting
    /// at that end has something on it. That allows one blank canvas on each side, and never two
    /// blank pages in a row: a blank page on the left no longer blocks creating one on the right.
    /// </summary>
    private bool CanCreateWorkspacePage(int direction)
    {
        var pages = Settings.WorkspacePages;
        if (pages.Count == 0) return true;
        return (direction < 0 ? pages[0] : pages[^1]).HasContent;
    }

    /// <summary>
    /// A dialog owns the workspace while it is visible. Keep every navigation entry point
    /// behind the same gate so wheel, swipe, keyboard, arrows and page dots behave alike.
    /// Reminder toasts and embedded third-party windows are intentionally not owner windows.
    /// </summary>
    private bool IsWorkspaceNavigationBlocked =>
        IsReportModalOpen || !IsEnabled || OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    private void CancelNavigationForOpenWindow(bool pointerDown)
    {
        pointerWasDown = pointerDown;
        swipeCandidate = false;
        if (!swipeActive && !pageTransitioning) return;
        swipeActive = false;
        ResetCarousel(true);
    }

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
        if (IsWorkspaceNavigationBlocked)
        {
            CancelNavigationForOpenWindow((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0);
            return;
        }
        int target = currentPageIndex + direction;
        if (target >= 0 && target < Settings.WorkspacePages.Count) SwitchWorkspacePage(target);
        else if (CanCreateWorkspacePage(direction)) BeginCarouselTransition(direction, null, true, 0);
        else Pulse(PageDockSurface);
    }

    private bool AddBlankWorkspacePage()
    {
        if (IsWorkspaceNavigationBlocked)
        {
            CancelNavigationForOpenWindow((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0);
            return false;
        }
        if (pageTransitioning) return false;
        SaveState();
        if (!CanCreateWorkspacePage(1))
        {
            Status(L.T("pages.lastAlreadyEmpty"));
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
            Status(L.T("pages.timerAlreadyHere"));
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

    /// <summary>
    /// Right-click on a page dot. Confirms first — deleting a page throws its widgets away for
    /// good, unlike closing one, which keeps every note in its history — then deletes it whether
    /// it holds anything or not.
    /// </summary>
    private void DeleteWorkspacePageClick(int index)
    {
        if (IsWorkspaceNavigationBlocked)
        {
            CancelNavigationForOpenWindow((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0);
            return;
        }
        if (pageTransitioning || index < 0 || index >= Settings.WorkspacePages.Count) return;
        if (Settings.WorkspacePages.Count <= 1)
        {
            Status(L.T("pages.cannotDeleteOnly"));
            Pulse(PageDockSurface);
            return;
        }
        var page = Settings.WorkspacePages[index];
        int count = page.WidgetCount;
        string suffix = count == 0 ? "" : count == 1 ? L.T("pages.deleteSuffixOne") : L.T("pages.deleteSuffixMany", count);
        if (Dialogs.Choose(this, L.T("pages.deleteTitle", page.Name.ToUpperInvariant(), suffix), [L.T("pages.deleteConfirm"), L.T("common.cancel")]) != 0) return;
        DeleteWorkspacePage(index);
    }

    /// <summary>
    /// Removes a page outright, live cards and all. A page never seen this session has no cards
    /// to release, only the configs it was saved with; a note among them still gets archived, the
    /// same courtesy closing a single note card gives it.
    /// </summary>
    private void DeleteWorkspacePage(int index)
    {
        var page = Settings.WorkspacePages[index];
        bool removingCurrent = index == currentPageIndex;
        SaveState();

        if (pageCards.Remove(page.Id, out var pageWidgetCards))
        {
            foreach (var card in pageWidgetCards)
            {
                if (card.Config.Kind == "notes") NoteArchiveStore.For(Store).Close(card.Config);
                HideInteractionOverlay(card); HideOverlay(card);
                card.Release();
                WidgetArea.Children.Remove(card);
                PagePreviewArea.Children.Remove(card);
            }
        }
        else
        {
            foreach (var config in page.Widgets.Where(widget => widget.Kind == "notes"))
                NoteArchiveStore.For(Store).Close(config);
        }

        if (removingCurrent)
        {
            CancelTimerGesture();
            HideTimerOverlay();
            TimerFrame.Visibility = Visibility.Collapsed;
            cards = [];
        }

        Settings.WorkspacePages.RemoveAt(index);

        if (removingCurrent)
        {
            currentPageIndex = Math.Min(index, Settings.WorkspacePages.Count - 1);
            if (!pageCards.TryGetValue(CurrentPage.Id, out var nextCards))
            {
                pageTransitioning = true;
                cards = [];
                foreach (var config in CurrentPage.Widgets) AddCard(config, false);
                pageTransitioning = false;
                pageCards[CurrentPage.Id] = cards;
            }
            else cards = nextCards;
            foreach (var card in cards)
            {
                if (!WidgetArea.Children.Contains(card)) WidgetArea.Children.Add(card);
                card.SetCarouselTransition(false);
                card.Visibility = Visibility.Visible;
            }
            if (!WidgetArea.Children.Contains(TimerFrame)) WidgetArea.Children.Add(TimerFrame);
            ArrangeCards();
            foreach (var card in cards) ShowInteractionOverlay(card);
        }
        else if (index < currentPageIndex) currentPageIndex--;

        Settings.ActiveWorkspacePage = currentPageIndex;
        UpdatePageNavigation();
        SaveState();
    }

    private void SwitchWorkspacePage(int target)
    {
        if (IsWorkspaceNavigationBlocked)
        {
            CancelNavigationForOpenWindow((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0);
            return;
        }
        if (pageTransitioning || target < 0 || target >= Settings.WorkspacePages.Count || target == currentPageIndex) return;
        BeginCarouselTransition(target > currentPageIndex ? 1 : -1, target, false, 0);
    }

    private void BeginCarouselTransition(int direction, int? target, bool createPage, double initialOffset)
    {
        if (IsWorkspaceNavigationBlocked) return;
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
        // The widgets that were sliding in are the ones that stay: moving the very same
        // instances onto the canvas is what keeps the landing free of a rebuild.
        foreach (var card in cards)
        {
            if (PagePreviewArea.Children.Contains(card)) PagePreviewArea.Children.Remove(card);
            if (!WidgetArea.Children.Contains(card)) WidgetArea.Children.Add(card);
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

    /// <summary>
    /// The page sliding in shows its real widgets, not a mock of them. Building them here
    /// and handing the same instances over on commit is what removes the flash that used to
    /// happen when the drawn placeholders were replaced by the widgets themselves.
    /// </summary>
    private void RenderPagePreview(WorkspacePage? page)
    {
        DetachPreviewCards();
        PagePreviewArea.Width = Math.Max(1, CarouselViewport.ActualWidth);
        PagePreviewArea.Height = Math.Max(1, CarouselViewport.ActualHeight);
        PagePreviewArea.Visibility = Visibility.Visible;
        if (page is null)
        {
            AddPreviewPlaceholder(L.T("pages.newPage"));
            return;
        }
        foreach (var card in CardsForPage(page))
        {
            // A page that was visited before left its widgets parked on the workspace
            // canvas, collapsed. They have to come off it before they can slide in.
            if (WidgetArea.Children.Contains(card)) WidgetArea.Children.Remove(card);
            card.Visibility = Visibility.Visible;
            // Hosted windows and web panels own native surfaces that cannot ride a
            // sliding canvas, so their content stays hidden until the page lands.
            card.SetCarouselTransition(true);
            PagePreviewArea.Children.Add(card);
        }
        ArrangePreviewCards();
        // The Pomodoro is one live surface. When the page being left does not hold it, it is
        // free to ride the page coming in; only when both pages carry one is it drawn.
        if (page.TimerWidget is { } timer)
        {
            if (CurrentTimerWidget is null) LendTimerToPreview(timer, page.TimerPositionCustomized);
            else AddPreviewCard(timer, true);
        }
        if (page.Widgets.Count == 0 && page.TimerWidget is null) AddPreviewPlaceholder(L.T("pages.emptyTitle"));
    }

    /// <summary>The widgets of a page, built once and kept for as long as the page exists.</summary>
    private List<WidgetCard> CardsForPage(WorkspacePage page)
    {
        if (pageCards.TryGetValue(page.Id, out var existing)) return existing;
        var built = page.Widgets.Select(config => new WidgetCard(this, config)).ToList();
        pageCards[page.Id] = built;
        return built;
    }

    private void ArrangePreviewCards()
    {
        double width = Math.Max(1, PagePreviewArea.Width);
        double height = Math.Max(1, PagePreviewArea.Height);
        foreach (var card in PagePreviewArea.Children.OfType<WidgetCard>())
        {
            card.Width = Math.Clamp(card.Config.Width, 220, Math.Max(220, width));
            card.Height = Math.Clamp(card.Config.Collapsed ? 42 : card.Config.Height, 42, Math.Max(42, height));
            Canvas.SetLeft(card, Math.Clamp(card.Config.X, 0, Math.Max(0, width - card.Width)));
            Canvas.SetTop(card, Math.Clamp(card.Config.Y, 0, Math.Max(0, height - card.Height)));
        }
    }

    /// <summary>
    /// Moves the live Pomodoro onto the incoming page for the length of the slide, laid out
    /// where that page keeps it. Sliding the real clock is what removes the flash at the end.
    /// </summary>
    private void LendTimerToPreview(WidgetConfig timer, bool customized)
    {
        // Same rules as ArrangeTimerWidget, so the clock lands exactly where it slid to.
        double width = Math.Max(1, PagePreviewArea.Width);
        double height = Math.Max(1, PagePreviewArea.Height);
        double frameWidth = Math.Clamp(timer.Width > 0 ? timer.Width : Math.Min(560, width), 360, Math.Max(360, width));
        double frameHeight = Math.Clamp(timer.Height > 0 ? timer.Height : Math.Min(360, height), 300, Math.Max(300, height));
        double x = customized ? timer.X : 12;
        double y = customized ? timer.Y : Settings.TimerAtBottom ? Math.Max(12, height - frameHeight - 12) : 12;
        if (WidgetArea.Children.Contains(TimerFrame)) WidgetArea.Children.Remove(TimerFrame);
        if (!PagePreviewArea.Children.Contains(TimerFrame)) PagePreviewArea.Children.Add(TimerFrame);
        TimerFrame.Visibility = Visibility.Visible;
        TimerFrame.Width = frameWidth;
        TimerFrame.Height = frameHeight;
        Canvas.SetLeft(TimerFrame, Math.Clamp(x, 0, Math.Max(0, width - frameWidth)));
        Canvas.SetTop(TimerFrame, Math.Clamp(y, 0, Math.Max(0, height - frameHeight)));
    }

    /// <summary>Puts the Pomodoro back on the workspace canvas, wherever the slide ended.</summary>
    private void ReclaimTimer()
    {
        if (!PagePreviewArea.Children.Contains(TimerFrame)) return;
        PagePreviewArea.Children.Remove(TimerFrame);
        if (!WidgetArea.Children.Contains(TimerFrame)) WidgetArea.Children.Add(TimerFrame);
    }

    /// <summary>Takes the previewed widgets off the preview canvas without discarding them.</summary>
    private void DetachPreviewCards()
    {
        ReclaimTimer();
        foreach (var card in PagePreviewArea.Children.OfType<WidgetCard>().ToArray()) PagePreviewArea.Children.Remove(card);
        PagePreviewArea.Children.Clear();
    }

    private void AddPreviewPlaceholder(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Muted"],
            Width = Math.Max(1, PagePreviewArea.Width),
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(label, 0);
        Canvas.SetTop(label, Math.Max(0, PagePreviewArea.Height / 2 - 10));
        PagePreviewArea.Children.Add(label);
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
        var header = new Border { Background = (Brush)Application.Current.Resources["Chrome"] };
        header.Child = new TextBlock
        {
            Text = timer ? L.T("timer.pomodoro") : $"{config.Kind.ToUpperInvariant()} / {config.Title}",
            Foreground = (Brush)Application.Current.Resources["ChromeInk"], FontSize = 9, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis
        };
        shell.Children.Add(header);
        var body = new Grid { Background = timer ? PhaseBrush() : (Brush)Application.Current.Resources["Surface"] };
        body.Children.Add(new TextBlock
        {
            Text = timer ? ClockText.Text : config.Kind == "window" ? L.T("pages.appOpen") : config.Title.ToUpperInvariant(),
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
        bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!workspacePagesLoaded || exiting || WindowState == WindowState.Minimized) return;
        if (IsWorkspaceNavigationBlocked)
        {
            navigationWasBlocked = true;
            CancelNavigationForOpenWindow(down);
            return;
        }
        if (navigationWasBlocked)
        {
            // Do not inherit the click that closed the dialog as the beginning of a swipe.
            navigationWasBlocked = false;
            pointerWasDown = down;
            swipeCandidate = false;
            return;
        }
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

    /// <summary>
    /// Dragging the workspace is not typing. Clearing the keyboard alone leaves the caret in
    /// the field, so the focus scope has to be emptied before the focus is dropped.
    /// </summary>
    private void ReleaseTypingFocus()
    {
        if (Keyboard.FocusedElement is not (TextBoxBase or PasswordBox) || Keyboard.FocusedElement is not DependencyObject field) return;
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(field), null);
        Keyboard.ClearFocus();
    }

    private bool CanBeginSwipe(Point point)
    {
        if (IsWorkspaceNavigationBlocked || pageTransitioning || timerResizing || cards.Any(card => card.IsGestureActive)) return false;
        return !IsInteractiveSwipeSource(WidgetArea.InputHitTest(point) as DependencyObject);
    }

    /// <summary>
    /// A page swipe only ever starts from empty canvas, as the README promises — never from
    /// inside a widget. Checking for specific controls (a button, a text box…) missed anything
    /// merely inert: the few pixels of margin around a note's editor, its blank space below the
    /// last line, a plain label in another widget. A card is excluded outright, control or not.
    /// </summary>
    private bool IsInteractiveSwipeSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Ancestors.Up(current))
        {
            if (current is WidgetCard || ReferenceEquals(current, TimerFrame)) return true;
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
            ReleaseTypingFocus();
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
        bool create = targetIndex is null && CanCreateWorkspacePage(direction);
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
        DetachPreviewCards();
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
                Foreground = active ? (Brush)Application.Current.Resources["AccentInk"] : (Brush)Application.Current.Resources["ChromeInk"],
                BorderThickness = new Thickness(0),
                ToolTip = L.T("pages.pageTipDelete", (i + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture), page.WidgetCount, page.WidgetCount == 1 ? L.T("pages.widgetOne") : L.T("pages.widgetMany")),
            };
            AutomationProperties.SetName(button, L.T("pages.openPage", i + 1));
            button.Click += (_, _) => SwitchWorkspacePage(index);
            button.MouseRightButtonUp += (_, e) => { DeleteWorkspacePageClick(index); e.Handled = true; };
            PageButtonStrip.Children.Add(button);
        }
        bool leftOpen = CanCreateWorkspacePage(-1), rightOpen = CanCreateWorkspacePage(1);
        bool hasPrevious = currentPageIndex > 0, hasNext = currentPageIndex < Settings.WorkspacePages.Count - 1;
        PreviousPageButton.IsEnabled = hasPrevious || leftOpen;
        NextPageButton.IsEnabled = hasNext || rightOpen;
        AddPageButton.IsEnabled = rightOpen;
        PreviousPageButton.ToolTip = hasPrevious ? L.T("pages.previous") : leftOpen ? L.T("pages.createLeft") : L.T("pages.needWidgetLeft");
        NextPageButton.ToolTip = hasNext ? L.T("pages.next") : rightOpen ? L.T("pages.createRight") : L.T("pages.needWidgetRight");
        AddPageButton.ToolTip = rightOpen ? L.T("pages.addRight") : L.T("pages.lastEmptyTip");
        ToolTipService.SetShowOnDisabled(AddPageButton, true);
        ToolTipService.SetShowOnDisabled(PreviousPageButton, true);
        ToolTipService.SetShowOnDisabled(NextPageButton, true);
        UpdatePageMeta();
    }

    private void UpdatePageMeta()
    {
        if (!workspacePagesLoaded) return;
        PageMetaText.Text = L.T("pages.meta", (currentPageIndex + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture), Settings.WorkspacePages.Count.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        PageDockSurface.ToolTip = CurrentPage.HasContent
            ? L.T("pages.metaDetail", currentPageIndex + 1, Settings.WorkspacePages.Count, CurrentPage.WidgetCount)
            : L.T("pages.metaEmpty");
    }

    internal int WorkspacePageCount => Settings.WorkspacePages.Count;
    internal int CurrentWorkspacePageIndex => currentPageIndex;
    internal bool WorkspaceNavigationIsBlocked => IsWorkspaceNavigationBlocked;
    internal WorkspacePage CurrentWorkspacePageForDiagnostics => CurrentPage;
    internal bool CurrentWorkspacePageIsBlank => !CurrentPage.HasContent;
    internal bool FirstWorkspacePageIsBlank => !Settings.WorkspacePages[0].HasContent;
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
