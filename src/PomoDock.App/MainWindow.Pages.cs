using System.Text.Json;
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
    private int swipeDx;
    private int swipeDy;
    private int? swipeTargetIndex;
    private bool swipeCreatesPage;
    private bool swipeDestinationAvailable;
    private bool navigationWasBlocked;

    private WorkspacePage CurrentPage => Settings.WorkspacePages[currentPageIndex];
    private WidgetConfig? CurrentTimerWidget => CurrentPage.TimerWidget;
    internal bool CanAddTimerWidget => CurrentTimerWidget is null;

    /// <summary>
    /// A blank neighbor can open in any empty cardinal cell next to the current page, but only
    /// when this page already holds something. That keeps one blank canvas per edge of the live
    /// page and never stacks two empty pages in a row from the same spot.
    /// </summary>
    private bool CanCreateWorkspacePage(int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || (dx != 0 && dy != 0)) return false;
        if (!CurrentPage.HasContent) return false;
        return FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy) is null;
    }

    private int? FindPageIndex(int col, int row)
    {
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            var page = Settings.WorkspacePages[i];
            if (page.Col == col && page.Row == row) return i;
        }
        return null;
    }

    private static (int Dx, int Dy) StepToward(WorkspacePage from, WorkspacePage to)
    {
        int dx = Math.Sign(to.Col - from.Col);
        int dy = Math.Sign(to.Row - from.Row);
        if (dx != 0 && dy != 0)
        {
            // Jumping to a diagonal neighbour via the dock: prefer the larger axis so the slide
            // still reads as a single-direction carousel move.
            return Math.Abs(to.Col - from.Col) >= Math.Abs(to.Row - from.Row) ? (dx, 0) : (0, dy);
        }
        if (dx == 0 && dy == 0) return (1, 0);
        return (dx, dy);
    }

    /// <summary>
    /// A dialog owns the workspace while it is visible. Keep every navigation entry point
    /// behind the same gate so wheel, swipe, keyboard, arrows and page dots behave alike.
    /// Reminder toasts and embedded third-party windows are intentionally not owner windows.
    /// </summary>
    private bool IsWorkspaceNavigationBlocked =>
        IsReportModalOpen || IsNotificationCenterOpen || !IsEnabled || OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

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
        CurrentPage.Widgets = cards.Where(card => card.Config.Kind != "agent").Select(card => card.Config).ToList();
        if (agentCard is not null)
        {
            PersistWidget(agentCard, false);
            Settings.AgentWidget = agentCard.Config;
        }
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

    private void PreviousPageClick(object sender, RoutedEventArgs e) => NavigateWorkspace(-1, 0);
    private void NextPageClick(object sender, RoutedEventArgs e) => NavigateWorkspace(1, 0);
    private void UpPageClick(object sender, RoutedEventArgs e) => NavigateWorkspace(0, -1);
    private void DownPageClick(object sender, RoutedEventArgs e) => NavigateWorkspace(0, 1);
    private void AddPageClick(object sender, RoutedEventArgs e) => AddBlankWorkspacePage();

    private void NavigateWorkspace(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (IsWorkspaceNavigationBlocked)
        {
            CancelNavigationForOpenWindow((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0);
            return;
        }
        int? target = FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy);
        if (target is { } index) SwitchWorkspacePage(index);
        else if (CanCreateWorkspacePage(dx, dy)) BeginCarouselTransition(dx, dy, null, true, 0);
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
        if (!CanCreateWorkspacePage(1, 0))
        {
            Status(L.T("pages.lastAlreadyEmpty"));
            Pulse(PageDockSurface);
            return false;
        }
        BeginCarouselTransition(1, 0, null, true, 0);
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
        var (dx, dy) = StepToward(CurrentPage, Settings.WorkspacePages[target]);
        BeginCarouselTransition(dx, dy, target, false, 0);
    }

    private void JumpToWorkspacePage(int target)
    {
        if (target < 0 || target >= Settings.WorkspacePages.Count || target == currentPageIndex) return;
        if (pageTransitioning) ResetCarousel(true);
        var (dx, dy) = StepToward(CurrentPage, Settings.WorkspacePages[target]);
        CommitWorkspacePageChange(dx, dy, target, false);
    }

    private void BeginCarouselTransition(int dx, int dy, int? target, bool createPage, double initialOffset)
    {
        if (IsWorkspaceNavigationBlocked) return;
        if (pageTransitioning) return;
        PrepareCarousel(dx, dy, target, createPage);
        swipeOffset = initialOffset;
        ApplyCarouselOffset(initialOffset);
        if (Settings.ReduceMotion)
        {
            CommitWorkspacePageChange(dx, dy, target, createPage);
            return;
        }
        // Current page exits opposite the step; the preview rides in from that edge.
        double pageTarget = -(dx != 0 ? dx : dy) * CarouselExtent(dx, dy);
        AnimateCarousel(pageTarget, commit: true);
    }

    private double CarouselExtent(int dx, int dy) =>
        dx != 0 ? Math.Max(1, CarouselViewport.ActualWidth) : Math.Max(1, CarouselViewport.ActualHeight);

    private void ApplyCarouselOffset(double offset)
    {
        if (swipeDx != 0)
        {
            pageSlide.X = offset;
            pageSlide.Y = 0;
            previewSlide.X = offset + swipeDx * Math.Max(1, CarouselViewport.ActualWidth);
            previewSlide.Y = 0;
        }
        else
        {
            pageSlide.X = 0;
            pageSlide.Y = offset;
            previewSlide.X = 0;
            previewSlide.Y = offset + swipeDy * Math.Max(1, CarouselViewport.ActualHeight);
        }
    }

    private void PrepareCarousel(int dx, int dy, int? target, bool createPage)
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
        swipeDx = dx;
        swipeDy = dy;
        swipeTargetIndex = target;
        swipeCreatesPage = createPage;
        swipeDestinationAvailable = target is not null || createPage;
        RenderPagePreview(target is { } index ? Settings.WorkspacePages[index] : null);
    }

    private void CommitWorkspacePageChange(int dx, int dy, int? target, bool createPage)
    {
        var oldPage = CurrentPage;
        pageCards[oldPage.Id] = cards;
        foreach (var card in cards) card.Visibility = Visibility.Collapsed;
        TimerFrame.Visibility = Visibility.Collapsed;

        if (createPage)
        {
            var page = new WorkspacePage
            {
                Name = $"PÁGINA {Settings.WorkspacePages.Count + 1:00}",
                Col = oldPage.Col + dx,
                Row = oldPage.Row + dy
            };
            Settings.WorkspacePages.Add(page);
            target = Settings.WorkspacePages.Count - 1;
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
            card.Height = card.Config.Collapsed ? 42 : Math.Clamp(card.Config.Height, 42, Math.Max(42, height));
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
            bool horizontal = Math.Abs(dx) >= 12 && Math.Abs(dx) > Math.Abs(dy) * 1.15;
            bool vertical = Math.Abs(dy) >= 12 && Math.Abs(dy) > Math.Abs(dx) * 1.15;
            if (!horizontal && !vertical) return;
            swipeActive = true;
            ReleaseTypingFocus();
            if (horizontal) ConfigurePointerSwipe(dx < 0 ? 1 : -1, 0);
            else ConfigurePointerSwipe(0, dy < 0 ? 1 : -1);
        }

        int newDx = swipeDx != 0 ? (dx < 0 ? 1 : -1) : 0;
        int newDy = swipeDy != 0 ? (dy < 0 ? 1 : -1) : 0;
        if (newDx != swipeDx || newDy != swipeDy) ConfigurePointerSwipe(newDx, newDy);

        double extent = CarouselExtent(swipeDx, swipeDy);
        double delta = swipeDx != 0 ? dx : dy;
        if (swipeDestinationAvailable)
        {
            swipeOffset = Math.Clamp(delta, -extent, extent);
            ApplyCarouselOffset(swipeOffset);
        }
        else
        {
            swipeOffset = Math.Sign(delta) * Math.Min(extent * .10, Math.Sqrt(Math.Abs(delta)) * 5);
            if (swipeDx != 0) { pageSlide.X = swipeOffset; pageSlide.Y = 0; }
            else { pageSlide.X = 0; pageSlide.Y = swipeOffset; }
            PagePreviewArea.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfigurePointerSwipe(int dx, int dy)
    {
        int? targetIndex = FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy);
        bool create = targetIndex is null && CanCreateWorkspacePage(dx, dy);
        if (!pageTransitioning) PrepareCarousel(dx, dy, targetIndex, create);
        else
        {
            swipeDx = dx;
            swipeDy = dy;
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
        double extent = CarouselExtent(swipeDx, swipeDy);
        double threshold = Math.Min(150, Math.Max(58, extent * .16));
        bool commit = swipeDestinationAvailable &&
            (Math.Abs(swipeOffset) >= threshold || Math.Abs(swipeOffset / elapsed) >= 720 && Math.Abs(swipeOffset) >= 30);
        if (Settings.ReduceMotion)
        {
            if (commit) CommitWorkspacePageChange(swipeDx, swipeDy, swipeTargetIndex, swipeCreatesPage);
            else ResetCarousel(true);
            return;
        }
        int step = swipeDx != 0 ? swipeDx : swipeDy;
        AnimateCarousel(commit ? -step * extent : 0, commit);
    }

    private void AnimateCarousel(double pageTarget, bool commit)
    {
        double extent = CarouselExtent(swipeDx, swipeDy);
        bool horizontal = swipeDx != 0;
        double current = horizontal ? pageSlide.X : pageSlide.Y;
        double distance = Math.Abs(pageTarget - current);
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(110 + 130 * distance / extent, 110, 240));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DependencyProperty axis = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        double previewTarget = commit
            ? pageTarget + (horizontal ? swipeDx : swipeDy) * extent
            : (horizontal ? swipeDx : swipeDy) * extent;

        // Clear the idle axis so a vertical move never keeps a leftover horizontal drift.
        if (horizontal) { pageSlide.Y = 0; previewSlide.Y = 0; }
        else { pageSlide.X = 0; previewSlide.X = 0; }

        var pageAnimation = new DoubleAnimation(current, pageTarget, duration) { EasingFunction = ease };
        var previewAnimation = new DoubleAnimation(horizontal ? previewSlide.X : previewSlide.Y, previewTarget, duration) { EasingFunction = ease };
        pageAnimation.Completed += (_, _) =>
        {
            pageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            pageSlide.BeginAnimation(TranslateTransform.YProperty, null);
            previewSlide.BeginAnimation(TranslateTransform.XProperty, null);
            previewSlide.BeginAnimation(TranslateTransform.YProperty, null);
            if (commit) CommitWorkspacePageChange(swipeDx, swipeDy, swipeTargetIndex, swipeCreatesPage);
            else ResetCarousel(true);
        };
        pageSlide.BeginAnimation(axis, pageAnimation);
        previewSlide.BeginAnimation(axis, previewAnimation);
    }

    private void ResetCarousel(bool restoreOverlays)
    {
        pageSlide.BeginAnimation(TranslateTransform.XProperty, null);
        pageSlide.BeginAnimation(TranslateTransform.YProperty, null);
        previewSlide.BeginAnimation(TranslateTransform.XProperty, null);
        previewSlide.BeginAnimation(TranslateTransform.YProperty, null);
        pageSlide.X = 0;
        pageSlide.Y = 0;
        previewSlide.X = 0;
        previewSlide.Y = 0;
        PagePreviewArea.Visibility = Visibility.Collapsed;
        DetachPreviewCards();
        pageTransitioning = false;
        swipeDestinationAvailable = false;
        swipeTargetIndex = null;
        swipeCreatesPage = false;
        swipeDx = 0;
        swipeDy = 0;
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

        int minCol = Settings.WorkspacePages.Min(page => page.Col);
        int maxCol = Settings.WorkspacePages.Max(page => page.Col);
        int minRow = Settings.WorkspacePages.Min(page => page.Row);
        int maxRow = Settings.WorkspacePages.Max(page => page.Row);
        int cols = maxCol - minCol + 1;
        int rows = maxRow - minRow + 1;

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var byCell = Settings.WorkspacePages
            .Select((page, index) => (page, index))
            .ToDictionary(entry => (entry.page.Col, entry.page.Row), entry => entry);

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int col = minCol; col <= maxCol; col++)
            {
                if (!byCell.TryGetValue((col, row), out var entry))
                {
                    var spacer = new Border { Width = 22, Height = 22, Margin = new Thickness(1), Opacity = 0 };
                    Grid.SetColumn(spacer, col - minCol);
                    Grid.SetRow(spacer, row - minRow);
                    grid.Children.Add(spacer);
                    continue;
                }

                int index = entry.index;
                var page = entry.page;
                bool active = index == currentPageIndex;
                string coord = $"{col - minCol + 1},{row - minRow + 1}";
                var button = new Button
                {
                    Content = active ? "●" : "•", Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(1),
                    FontFamily = new FontFamily("Consolas"), FontSize = active ? 12 : 9, FontWeight = FontWeights.Bold,
                    Background = active ? (Brush)Application.Current.Resources["Accent"] : Brushes.Transparent,
                    Foreground = active ? (Brush)Application.Current.Resources["AccentInk"] : (Brush)Application.Current.Resources["ChromeInk"],
                    BorderThickness = new Thickness(0),
                    ToolTip = L.T("pages.pageTipDelete", coord, page.WidgetCount, page.WidgetCount == 1 ? L.T("pages.widgetOne") : L.T("pages.widgetMany")),
                };
                AutomationProperties.SetName(button, L.T("pages.openPage", coord));
                button.Click += (_, _) => SwitchWorkspacePage(index);
                button.MouseRightButtonUp += (_, e) => { DeleteWorkspacePageClick(index); e.Handled = true; };
                Grid.SetColumn(button, col - minCol);
                Grid.SetRow(button, row - minRow);
                grid.Children.Add(button);
            }
        }
        PageButtonStrip.Children.Add(grid);

        bool leftOpen = CanCreateWorkspacePage(-1, 0), rightOpen = CanCreateWorkspacePage(1, 0);
        bool upOpen = CanCreateWorkspacePage(0, -1), downOpen = CanCreateWorkspacePage(0, 1);
        bool hasLeft = FindPageIndex(CurrentPage.Col - 1, CurrentPage.Row) is not null;
        bool hasRight = FindPageIndex(CurrentPage.Col + 1, CurrentPage.Row) is not null;
        bool hasUp = FindPageIndex(CurrentPage.Col, CurrentPage.Row - 1) is not null;
        bool hasDown = FindPageIndex(CurrentPage.Col, CurrentPage.Row + 1) is not null;

        PreviousPageButton.IsEnabled = hasLeft || leftOpen;
        NextPageButton.IsEnabled = hasRight || rightOpen;
        UpPageButton.IsEnabled = hasUp || upOpen;
        DownPageButton.IsEnabled = hasDown || downOpen;
        AddPageButton.IsEnabled = rightOpen;
        PreviousPageButton.ToolTip = hasLeft ? L.T("pages.previous") : leftOpen ? L.T("pages.createLeft") : L.T("pages.needWidgetLeft");
        NextPageButton.ToolTip = hasRight ? L.T("pages.next") : rightOpen ? L.T("pages.createRight") : L.T("pages.needWidgetRight");
        UpPageButton.ToolTip = hasUp ? L.T("pages.up") : upOpen ? L.T("pages.createUp") : L.T("pages.needWidgetUp");
        DownPageButton.ToolTip = hasDown ? L.T("pages.down") : downOpen ? L.T("pages.createDown") : L.T("pages.needWidgetDown");
        AddPageButton.ToolTip = rightOpen ? L.T("pages.addRight") : L.T("pages.lastEmptyTip");
        ToolTipService.SetShowOnDisabled(AddPageButton, true);
        ToolTipService.SetShowOnDisabled(PreviousPageButton, true);
        ToolTipService.SetShowOnDisabled(NextPageButton, true);
        ToolTipService.SetShowOnDisabled(UpPageButton, true);
        ToolTipService.SetShowOnDisabled(DownPageButton, true);
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
    internal bool FirstWorkspacePageIsBlank
    {
        get
        {
            int minCol = Settings.WorkspacePages.Min(page => page.Col);
            return Settings.WorkspacePages.Where(page => page.Col == minCol).Any(page => !page.HasContent);
        }
    }
    internal bool CurrentWorkspacePageHasTimer => CurrentTimerWidget is not null;

    internal AgentToolResult DeleteEmptyPagesFromAgent()
    {
        if (Settings.WorkspacePages.Count <= 1)
            return AgentResult(false, L.T("pages.cannotDeleteOnly"));
        var empty = Settings.WorkspacePages
            .Select((page, index) => (page, index))
            .Where(item => !item.page.HasContent)
            .Select(item => item.index)
            .ToList();
        if (empty.Count == 0)
            return AgentResult(false, L.T("agent.noEmptyPages"));
        int keep = Settings.WorkspacePages.FindIndex(page => page.HasContent);
        if (keep < 0) keep = 0;
        empty.RemoveAll(index => index == keep);
        if (empty.Count == 0)
            return AgentResult(false, L.T("pages.cannotDeleteOnly"));
        if (currentPageIndex != keep) JumpToWorkspacePage(keep);
        foreach (int index in empty.OrderByDescending(index => index))
        {
            if (index < 0 || index >= Settings.WorkspacePages.Count) continue;
            if (index == currentPageIndex) continue;
            if (Settings.WorkspacePages[index].HasContent) continue;
            DeleteWorkspacePage(index);
        }
        Pulse(PageDockSurface);
        Status(L.T("agent.deletedEmptyPages", empty.Count));
        return AgentResult(true, L.T("agent.deletedEmptyPages", empty.Count));
    }

    /// <summary>Deletes one page (empty or not), same as right-click on a page dot.</summary>
    internal AgentToolResult DeletePageFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int index, out var error)) return error!;
        if (Settings.WorkspacePages.Count <= 1)
            return AgentResult(false, L.T("pages.cannotDeleteOnly"));
        var page = Settings.WorkspacePages[index];
        int count = page.WidgetCount;
        string suffix = count == 0 ? "" : count == 1 ? L.T("pages.deleteSuffixOne") : L.T("pages.deleteSuffixMany", count);
        if (!AgentPlanApproved
            && Dialogs.Choose(this, L.T("pages.deleteTitle", page.Name.ToUpperInvariant(), suffix), [L.T("pages.deleteConfirm"), L.T("common.cancel")]) != 0)
            return AgentResult(false, EsPages() ? "Cancelaste borrar la página." : "Page delete cancelled.");
        string name = page.Name;
        DeleteWorkspacePage(index);
        return AgentResult(true, EsPages() ? $"Eliminé la página «{name}»." : $"Deleted page “{name}”.");
    }

    internal AgentToolResult SetWebKeepAliveFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args) ?? cards.FirstOrDefault(item => item.Config.Kind == "web");
        if (card is null || card.Config.Kind != "web")
            return AgentResult(false, EsPages() ? "No hay widget web." : "No web widget.");
        bool keep = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("keep_alive", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            keep = flag.GetBoolean();
        else if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("keepalive", out flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            keep = flag.GetBoolean();
        card.SetKeepAlive(keep);
        return AgentResult(true, keep
            ? (EsPages() ? $"Keep-alive activado en «{card.Config.Title}»." : $"Keep-alive on for “{card.Config.Title}”.")
            : (EsPages() ? $"Keep-alive desactivado en «{card.Config.Title}»." : $"Keep-alive off for “{card.Config.Title}”."));
    }

    internal AgentToolResult AddPageFromAgent()
    {
        if (pageTransitioning) return AgentResult(false, L.T("agent.pageBusy"));
        if (!CurrentPage.HasContent)
            return AgentResult(false, L.T("agent.pageAlreadyEmpty"));
        int dx = 0, dy = 0;
        if (CanCreateWorkspacePage(1, 0)) dx = 1;
        else if (CanCreateWorkspacePage(0, 1)) dy = 1;
        else if (CanCreateWorkspacePage(-1, 0)) dx = -1;
        else if (CanCreateWorkspacePage(0, -1)) dy = -1;
        else return AgentResult(false, L.T("pages.lastAlreadyEmpty"));
        CommitWorkspacePageChange(dx, dy, null, true);
        ShowCreatedWork(null);
        return AgentResult(true, L.T("agent.pageCreated", (currentPageIndex + 1).ToString()));
    }

    internal AgentToolResult GotoPageFromAgent(JsonElement args)
    {
        int? index = ResolvePageIndexFromAgent(args);
        if (index is null)
            return AgentResult(false, EsPages() ? "No encontré esa página." : "Page not found.");
        if (index.Value != currentPageIndex) JumpToWorkspacePage(index.Value);
        Pulse(PageDockSurface);
        var page = CurrentPage;
        bool home = Settings.HomePageId == page.Id;
        return AgentResult(true, EsPages()
            ? $"Abrí «{page.Name}» (página {currentPageIndex + 1}){(home ? ", tu casa" : "")}."
            : $"Opened “{page.Name}” (page {currentPageIndex + 1}){(home ? ", your home" : "")}.");
    }

    internal AgentToolResult RenamePageFromAgent(JsonElement args)
    {
        int? index = ResolvePageIndexFromAgent(args) ?? currentPageIndex;
        if (index is null || index < 0 || index >= Settings.WorkspacePages.Count)
            return AgentResult(false, EsPages() ? "No encontré esa página." : "Page not found.");
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "new_name");
        if (name.Length == 0) name = TextArg(args, "title");
        name = name.Trim();
        if (name.Length == 0)
            return AgentResult(false, EsPages() ? "Falta el nuevo nombre de la página." : "Missing the new page name.");
        var page = Settings.WorkspacePages[index.Value];
        string previous = page.Name;
        page.Name = name;
        bool makeHome = BoolArg(args, "home", IsHomeAlias(name));
        if (makeHome) Settings.HomePageId = page.Id;
        SaveState();
        UpdatePageNavigation();
        return AgentResult(true, EsPages()
            ? $"Renombré la página {index.Value + 1}: «{previous}» → «{page.Name}»{(makeHome ? " (casa)" : "")}."
            : $"Renamed page {index.Value + 1}: “{previous}” → “{page.Name}”{(makeHome ? " (home)" : "")}.");
    }

    internal AgentToolResult SetHomePageFromAgent(JsonElement args)
    {
        int? index = ResolvePageIndexFromAgent(args) ?? currentPageIndex;
        if (index is null || index < 0 || index >= Settings.WorkspacePages.Count)
            return AgentResult(false, EsPages() ? "No encontré esa página." : "Page not found.");
        var page = Settings.WorkspacePages[index.Value];
        Settings.HomePageId = page.Id;
        SaveState();
        return AgentResult(true, EsPages()
            ? $"«{page.Name}» es ahora tu página principal (casa)."
            : $"“{page.Name}” is now your home page.");
    }

    /// <summary>Resolves page by number, name, or home aliases (casa / principal / home).</summary>
    private int? ResolvePageIndexFromAgent(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;

        if (args.TryGetProperty("page", out var pageValue))
        {
            if (pageValue.ValueKind == JsonValueKind.Null) { /* fall through */ }
            else if (pageValue.TryGetInt32(out int number))
                return number >= 1 && number <= Settings.WorkspacePages.Count ? number - 1 : null;
            else if (pageValue.ValueKind == JsonValueKind.String)
            {
                string raw = pageValue.GetString()?.Trim() ?? "";
                if (int.TryParse(raw, out number))
                    return number >= 1 && number <= Settings.WorkspacePages.Count ? number - 1 : null;
                int? byName = FindPageIndexByNameOrAlias(raw);
                if (byName is not null) return byName;
            }
        }

        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length > 0)
        {
            int? byName = FindPageIndexByNameOrAlias(name);
            if (byName is not null) return byName;
        }

        if (BoolArg(args, "home", false) || IsHomeAlias(TextArg(args, "to")))
            return FindHomePageIndex();

        return null;
    }

    private int? FindPageIndexByNameOrAlias(string raw)
    {
        string needle = raw.Trim();
        if (needle.Length == 0) return null;
        if (IsHomeAlias(needle)) return FindHomePageIndex();

        // "página 3" / "page 3"
        var match = System.Text.RegularExpressions.Regex.Match(needle, @"^(?:p[aá]gina|page)\s*0*(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
            return number >= 1 && number <= Settings.WorkspacePages.Count ? number - 1 : null;

        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            if (string.Equals(Settings.WorkspacePages[i].Name, needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            if (Settings.WorkspacePages[i].Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }

    private int? FindHomePageIndex()
    {
        if (Settings.HomePageId is { } id)
        {
            int found = Settings.WorkspacePages.FindIndex(page => page.Id == id);
            if (found >= 0) return found;
        }
        // Fallback: page named like home, else first page.
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            if (IsHomeAlias(Settings.WorkspacePages[i].Name)) return i;
        }
        return Settings.WorkspacePages.Count > 0 ? 0 : null;
    }

    private static bool IsHomeAlias(string? value)
    {
        string key = (value ?? "").Trim().ToLowerInvariant();
        return key is "casa" or "home" or "principal" or "inicio" or "main" or "página principal" or "pagina principal" or "página casa" or "pagina casa";
    }

    private static bool BoolArg(JsonElement args, string key, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()?.Trim().ToLowerInvariant() ?? "";
            if (text is "true" or "1" or "yes" or "si" or "sí") return true;
            if (text is "false" or "0" or "no") return false;
        }
        return fallback;
    }

    internal AgentToolResult FocusWidgetFromAgent(JsonElement args)
    {
        string title = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("title", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";
        if (title.Length == 0) return AgentResult(false, L.T("agent.widgetMissing"));
        var match = Settings.WorkspacePages
            .Select((page, index) => (page, index, widget: page.Widgets.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault(item => item.widget is not null);
        if (match.widget is null) return AgentResult(false, L.T("agent.widgetMissing"));
        if (match.index != currentPageIndex) JumpToWorkspacePage(match.index);
        ShowCreatedWork(match.widget.Id);
        return AgentResult(true, L.T("agent.showingWork") + " · " + match.widget.Title);
    }

    internal AgentToolResult RemoveTimerFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int index, out var error)) return error!;
        if (index != currentPageIndex) JumpToWorkspacePage(index);
        if (CurrentTimerWidget is null)
            return AgentResult(false, EsPages() ? "Esa página no tiene temporizador." : "That page has no timer.");
        RemoveTimerWidget();
        Pulse(PageDockSurface);
        return AgentResult(true, EsPages() ? $"Quité el temporizador de la página {currentPageIndex + 1}." : $"Removed the timer from page {currentPageIndex + 1}.");
    }

    internal AgentToolResult RemoveWidgetFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int pageIndex, out var error)) return error!;
        if (pageIndex != currentPageIndex) JumpToWorkspacePage(pageIndex);
        SyncWorkspaceForAgent();
        string title = TextArg(args, "title");
        string kind = TextArg(args, "kind").ToLowerInvariant();
        kind = kind switch
        {
            "nota" or "note" or "notes" => "notes",
            "tareas" or "todo" => "todo",
            "hábito" or "habitos" or "hábitos" or "habits" => "habits",
            "agenda" or "calendar" => "calendar",
            "finanzas" or "finance" => "finance",
            "stats" or "enfoque" => "stats",
            _ => kind
        };
        var card = cards.FirstOrDefault(item =>
            (title.Length > 0 && item.Config.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            || (kind.Length > 0 && item.Config.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            || (title.Length > 0 && item.Config.Kind.Equals(title.ToLowerInvariant() switch
            {
                "nota" or "note" => "notes",
                "tareas" or "todo" => "todo",
                "hábitos" or "habitos" or "habits" => "habits",
                "agenda" or "calendar" => "calendar",
                "finanzas" or "finance" => "finance",
                "stats" or "enfoque" => "stats",
                _ => title
            }, StringComparison.OrdinalIgnoreCase)));
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        string name = card.Config.Title;
        RemoveCard(card);
        return AgentResult(true, EsPages() ? $"Quité {name}." : $"Removed {name}.");
    }

    /// <summary>Removes every content widget on the current (or named) page. Does not remove the agent chat.</summary>
    internal AgentToolResult ClearWidgetsFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int pageIndex, out var error)) return error!;
        if (pageIndex != currentPageIndex) JumpToWorkspacePage(pageIndex);
        SyncWorkspaceForAgent();
        bool includeTimer = false;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("include_timer", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            includeTimer = flag.GetBoolean();

        var victims = cards.Where(card => card.Config.Kind != "agent").ToList();
        if (victims.Count == 0 && !(includeTimer && CurrentTimerWidget is not null))
            return AgentResult(true, EsPages()
                ? $"La página {pageIndex + 1} ya no tiene widgets de contenido."
                : $"Page {pageIndex + 1} already has no content widgets.");

        var names = victims.Select(card => $"{card.Config.Kind}:{card.Config.Title}").ToList();
        foreach (var card in victims) RemoveCard(card);
        if (includeTimer && CurrentTimerWidget is not null)
        {
            names.Add("timer:POMODORO");
            using var timerArgs = JsonDocument.Parse($"{{\"page\":{pageIndex + 1}}}");
            RemoveTimerFromAgent(timerArgs.RootElement);
        }
        return AgentResult(true, EsPages()
            ? $"Quité {names.Count} widget(s) de la página {pageIndex + 1}: {string.Join(", ", names)}."
            : $"Removed {names.Count} widget(s) from page {pageIndex + 1}: {string.Join(", ", names)}.");
    }

    internal void SyncWorkspaceForAgent() => SaveCurrentWorkspacePage();

    internal IReadOnlyList<object> DescribeCurrentScreenWidgets()
    {
        SyncWorkspaceForAgent();
        return cards.Where(card => card.Config.Kind != "agent")
            .Select(card => (object)new { card.Config.Id, card.Config.Kind, card.Config.Title })
            .ToList();
    }

    internal string DescribeCurrentScreenForAgent()
    {
        SyncWorkspaceForAgent();
        var widgets = cards.Where(card => card.Config.Kind != "agent")
            .Select(card => $"{card.Config.Kind}:{card.Config.Title}")
            .ToList();
        string list = widgets.Count == 0
            ? (EsPages() ? "(ninguno)" : "(none)")
            : string.Join(", ", widgets);
        string timer = CurrentTimerWidget is null
            ? (EsPages() ? "no" : "no")
            : (EsPages() ? "sí" : "yes");
        return EsPages()
            ? $"Página actual {currentPageIndex + 1}/{Settings.WorkspacePages.Count} «{CurrentPage.Name}»{(Settings.HomePageId == CurrentPage.Id ? " [casa]" : "")}. Widgets visibles ahora: {list}. Timer: {timer}."
            : $"Current page {currentPageIndex + 1}/{Settings.WorkspacePages.Count} “{CurrentPage.Name}”{(Settings.HomePageId == CurrentPage.Id ? " [home]" : "")}. Widgets visible now: {list}. Timer: {timer}.";
    }

    internal AgentToolResult RenameWidgetFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args);
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        string previous = card.Config.Title;
        string title = TextArg(args, "new_title");
        if (title.Length == 0) title = TextArg(args, "name");
        if (title.Length == 0) return AgentResult(false, EsPages() ? "Falta el nuevo título." : "Missing the new title.");
        card.SetTitle(title);
        return AgentResult(true, EsPages() ? $"Renombré «{previous}» a «{title}»." : $"Renamed “{previous}” to “{title}”.");
    }

    internal AgentToolResult CollapseWidgetFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args);
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        bool collapsed = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("collapsed", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            collapsed = value.GetBoolean();
        _ = card.SetCollapsedAsync(collapsed);
        return AgentResult(true, collapsed
            ? (EsPages() ? $"Contraté «{card.Config.Title}»." : $"Collapsed “{card.Config.Title}”.")
            : (EsPages() ? $"Expandí «{card.Config.Title}»." : $"Expanded “{card.Config.Title}”."));
    }

    internal AgentToolResult ResizeWidgetFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args);
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        double width = card.Config.Width, height = card.Config.Height;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("width", out var w) && w.TryGetDouble(out double left)) width = left;
            if (args.TryGetProperty("height", out var h) && h.TryGetDouble(out double top)) height = top;
        }
        card.SetSize(width, height);
        return AgentResult(true, EsPages()
            ? $"Redimensioné «{card.Config.Title}» a {card.Config.Width:0}×{card.Config.Height:0}."
            : $"Resized “{card.Config.Title}” to {card.Config.Width:0}×{card.Config.Height:0}.");
    }

    internal AgentToolResult SetWebUrlFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args) ?? cards.FirstOrDefault(item => item.Config.Kind == "web");
        if (card is null || card.Config.Kind != "web") return AgentResult(false, EsPages() ? "No hay widget web." : "No web widget.");
        string url = TextArg(args, "url");
        if (url.Length == 0) url = TextArg(args, "value");
        if (!card.SetWebUrl(url)) return AgentResult(false, EsPages() ? "La URL debe ser https." : "URL must be https.");
        return AgentResult(true, EsPages() ? $"URL: {card.Config.Value}" : $"URL set: {card.Config.Value}");
    }

    internal AgentToolResult ReloadWebFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args) ?? cards.FirstOrDefault(item => item.Config.Kind == "web");
        if (card is null || card.Config.Kind != "web") return AgentResult(false, EsPages() ? "No hay widget web." : "No web widget.");
        card.ReloadWeb();
        return AgentResult(true, EsPages() ? $"Recargué «{card.Config.Title}»." : $"Reloaded “{card.Config.Title}”.");
    }

    internal AgentToolResult FullscreenFromAgent(JsonElement args)
    {
        bool? want = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            want = value.GetBoolean();
        if (want is null || fullscreen != want.Value) ToggleFullscreen();
        return AgentResult(true, fullscreen
            ? (EsPages() ? "Pantalla completa activada." : "Fullscreen on.")
            : (EsPages() ? "Pantalla completa desactivada." : "Fullscreen off."));
    }

    internal AgentToolResult DismissNotificationsFromAgent(JsonElement args)
    {
        string idText = TextArg(args, "id");
        if (idText.Length > 0 && Guid.TryParse(idText, out var id))
        {
            if (!notifications.MarkRead(id))
                return AgentResult(false, EsPages() ? "No encontré esa notificación." : "Notification not found.");
            RefreshNotificationChrome();
            return AgentResult(true, EsPages() ? "Notificación marcada como leída." : "Notification marked read.");
        }
        notifications.MarkAllRead();
        RefreshNotificationChrome();
        return AgentResult(true, EsPages() ? "Notificaciones marcadas como leídas." : "Notifications marked read.");
    }

    internal AgentToolResult DeleteLayoutFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length == 0 || !Settings.Layouts.ContainsKey(name))
            return AgentResult(false, EsPages() ? "No encontré ese layout." : "Layout not found.");
        if (!AgentPlanApproved
            && Dialogs.Choose(this, EsPages() ? $"¿Borrar layout «{name}»?" : $"Delete layout “{name}”?",
                [EsPages() ? "BORRAR" : "DELETE", L.T("common.cancel")]) != 0)
            return AgentResult(false, EsPages() ? "Cancelaste borrar el layout." : "Layout delete cancelled.");
        Settings.Layouts.Remove(name);
        SaveState();
        return AgentResult(true, EsPages() ? $"Layout borrado: {name}" : $"Layout deleted: {name}");
    }

    internal AgentToolResult OpenPanelFromAgent(JsonElement args)
    {
        string panel = TextArg(args, "panel");
        if (panel.Length == 0) panel = TextArg(args, "name");
        panel = panel.Trim().ToLowerInvariant();
        switch (panel)
        {
            case "settings" or "ajustes" or "config":
                ShowWorkspaceDialog(new SettingsWindow(this));
                ApplyLiveSettings();
                SaveState();
                return AgentResult(true, EsPages() ? "Abrí ajustes." : "Opened settings.");
            case "tasks" or "tareas" or "focus_tasks" or "enfoque":
                ShowWorkspaceDialog(new TasksWindow(this));
                return AgentResult(true, EsPages() ? "Abrí tareas de enfoque." : "Opened focus tasks.");
            case "layouts" or "layout":
                ShowWorkspaceDialog(new LayoutsWindow(this));
                return AgentResult(true, EsPages() ? "Abrí layouts." : "Opened layouts.");
            case "report" or "informe" or "historial":
                ShowReportModal();
                return AgentResult(true, EsPages() ? "Abrí el informe." : "Opened the report.");
            case "agent" or "agente":
                ShowAgentWidget();
                return AgentResult(true, EsPages() ? "Abrí el agente." : "Opened the agent.");
            case "notifications" or "notificaciones" or "inbox":
                NotificationsPopup.IsOpen = true;
                return AgentResult(true, EsPages() ? "Abrí las notificaciones." : "Opened notifications.");
            default:
                return AgentResult(false, EsPages()
                    ? "Panel desconocido. Usa: settings, tasks, layouts, report, agent, notifications."
                    : "Unknown panel. Use: settings, tasks, layouts, report, agent, notifications.");
        }
    }

    internal AgentToolResult OpenDataFolderFromAgent()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Store.DirectoryPath) { UseShellExecute = true });
            return AgentResult(true, Store.DirectoryPath);
        }
        catch (Exception ex)
        {
            return AgentResult(false, EsPages() ? $"No pude abrir la carpeta: {ex.Message}" : $"Could not open folder: {ex.Message}");
        }
    }

    internal AgentToolResult SetCalendarViewFromAgent(JsonElement args)
    {
        string view = TextArg(args, "view");
        if (view.Length == 0) view = TextArg(args, "mode");
        var card = FindAgentCard(args) ?? cards.FirstOrDefault(item => item.Config.Kind == "calendar");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de calendario en esta página." : "No calendar widget on this page.");
        if (!card.SetCalendarView(view))
            return AgentResult(false, EsPages() ? "Vista inválida. Usa month, week o agenda." : "Invalid view. Use month, week, or agenda.");
        return AgentResult(true, EsPages() ? $"Vista del calendario: {view}." : $"Calendar view: {view}.");
    }

    internal AgentToolResult NavigateCalendarFromAgent(JsonElement args)
    {
        string to = TextArg(args, "to");
        if (to.Length == 0) to = TextArg(args, "when");
        if (to.Length == 0) to = TextArg(args, "direction");
        var card = FindAgentCard(args) ?? cards.FirstOrDefault(item => item.Config.Kind == "calendar");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de calendario." : "No calendar widget.");
        if (!card.NavigateCalendar(to))
            return AgentResult(false, EsPages() ? "Destino inválido. Usa prev, next, today o yyyy-MM." : "Invalid target. Use prev, next, today, or yyyy-MM.");
        return AgentResult(true, EsPages() ? $"Calendario en: {to}." : $"Calendar at: {to}.");
    }

    internal AgentToolResult SetCalendarShowDoneFromAgent(JsonElement args)
    {
        bool show = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("show", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            show = flag.GetBoolean();
        else if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("show_done", out var alt)
            && alt.ValueKind is JsonValueKind.True or JsonValueKind.False)
            show = alt.GetBoolean();
        var card = FindAgentCard(args) ?? cards.FirstOrDefault(item => item.Config.Kind == "calendar");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de calendario." : "No calendar widget.");
        card.SetCalendarShowDone(show);
        return AgentResult(true, show
            ? (EsPages() ? "Muestro hechos en el calendario." : "Showing completed calendar items.")
            : (EsPages() ? "Oculto hechos en el calendario." : "Hiding completed calendar items."));
    }

    internal AgentToolResult ApplyRhythmFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "rhythm");
        name = name.Trim().ToLowerInvariant();
        var preset = name switch
        {
            "classic" or "clasico" or "clásico" or "25" => (25, 5, 15, 4, "classic"),
            "deep" or "profundo" or "50" => (50, 10, 20, 3, "deep"),
            "sprint" or "15" => (15, 3, 10, 4, "sprint"),
            "marathon" or "maraton" or "maratón" or "90" => (90, 20, 30, 2, "marathon"),
            _ => (0, 0, 0, 0, "")
        };
        if (preset.Item5.Length == 0)
            return AgentResult(false, EsPages()
                ? "Ritmo desconocido. Usa classic, deep, sprint o marathon."
                : "Unknown rhythm. Use classic, deep, sprint, or marathon.");
        Settings.FocusMinutes = preset.Item1;
        Settings.ShortMinutes = preset.Item2;
        Settings.LongMinutes = preset.Item3;
        Settings.LongInterval = preset.Item4;
        ApplyLiveSettings();
        SaveState();
        return AgentResult(true, EsPages()
            ? $"Ritmo {preset.Item5}: {preset.Item1} · {preset.Item2} · {preset.Item3}."
            : $"Rhythm {preset.Item5}: {preset.Item1} · {preset.Item2} · {preset.Item3}.");
    }

    internal WidgetCard? ResolveNotesCardForAgent(string title)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { title, kind = "notes" }));
        return FindAgentCard(doc.RootElement);
    }

    internal AgentToolResult SaveLayoutFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length == 0) return AgentResult(false, EsPages() ? "Falta el nombre del layout." : "Missing layout name.");
        SaveState();
        var snapshot = JsonSerializer.Deserialize<List<WidgetConfig>>(JsonSerializer.Serialize(CurrentPage.Widgets)) ?? [];
        Settings.Layouts[name] = snapshot;
        SaveState();
        return AgentResult(true, EsPages() ? $"Layout guardado: {name}" : $"Layout saved: {name}");
    }

    internal AgentToolResult LoadLayoutFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length == 0 || !Settings.Layouts.TryGetValue(name, out var layout))
            return AgentResult(false, EsPages() ? "No encontré ese layout." : "Layout not found.");
        LoadLayout(layout);
        return AgentResult(true, EsPages() ? $"Layout cargado: {name}" : $"Layout loaded: {name}");
    }

    internal AgentToolResult ListLayoutsFromAgent()
    {
        var names = Settings.Layouts.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        string summary = names.Count == 0
            ? (EsPages() ? "No hay layouts guardados." : "No saved layouts.")
            : string.Join(", ", names);
        return new AgentToolResult(false, summary, JsonSerializer.Serialize(new { success = true, changed = false, summary, data = names }), true);
    }

    internal AgentToolResult NotificationsFromAgent(JsonElement args)
    {
        string command = TextArg(args, "command").ToLowerInvariant();
        if (command is "dismiss" or "read" or "mark_read")
        {
            notifications.MarkAllRead();
            RefreshNotificationChrome();
            return AgentResult(true, EsPages() ? "Notificaciones marcadas como leídas." : "Notifications marked read.");
        }
        var items = notifications.History.Items.Take(20).Select(item => new
        {
            item.Id, item.Title, item.Read, item.ScheduledLocal, item.DeliveredLocal
        }).ToList();
        string summary = EsPages()
            ? $"{items.Count} notificaciones · {notifications.History.Unread} sin leer"
            : $"{items.Count} notifications · {notifications.History.Unread} unread";
        return new AgentToolResult(false, summary, JsonSerializer.Serialize(new { success = true, changed = false, summary, data = items }), true);
    }

    private WidgetCard? FindAgentCard(JsonElement args)
    {
        string title = TextArg(args, "title");
        string kind = TextArg(args, "kind").ToLowerInvariant();
        kind = kind switch
        {
            "nota" or "note" or "notes" => "notes",
            "tareas" or "todo" => "todo",
            "hábito" or "habitos" or "hábitos" or "habits" => "habits",
            "agenda" or "calendar" => "calendar",
            "finanzas" or "finance" => "finance",
            "stats" or "enfoque" => "stats",
            "web" or "navegador" => "web",
            "ventana" or "window" => "window",
            _ => kind
        };
        if (title.Length == 0 && kind.Length == 0) return null;

        bool Matches(WidgetConfig widget) =>
            (title.Length > 0 && widget.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            || (kind.Length > 0 && widget.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

        // Prefer an explicit page when provided; otherwise search every page and jump.
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("page", out _)
            && TryAgentPage(args, out int pageIndex, out _)
            && pageIndex >= 0)
        {
            if (pageIndex != currentPageIndex) JumpToWorkspacePage(pageIndex);
            SyncWorkspaceForAgent();
            return cards.FirstOrDefault(item => Matches(item.Config));
        }

        for (int index = 0; index < Settings.WorkspacePages.Count; index++)
        {
            var match = Settings.WorkspacePages[index].Widgets.FirstOrDefault(Matches);
            if (match is null) continue;
            if (index != currentPageIndex) JumpToWorkspacePage(index);
            SyncWorkspaceForAgent();
            return cards.FirstOrDefault(item => item.Config.Id == match.Id)
                ?? cards.FirstOrDefault(item => Matches(item.Config));
        }

        return cards.FirstOrDefault(item => Matches(item.Config));
    }

    private bool TryAgentPage(JsonElement args, out int index, out AgentToolResult? error)
    {
        index = currentPageIndex;
        error = null;
        int page = currentPageIndex + 1;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("page", out var value))
        {
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.TryGetInt32(out int number)) page = number;
            else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) page = number;
        }
        index = page - 1;
        if (index < 0 || index >= Settings.WorkspacePages.Count)
        {
            error = AgentResult(false, L.T("agent.pageMissing", page));
            return false;
        }
        return true;
    }

    private static string TextArg(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";

    private static bool EsPages() => Strings.Culture.TwoLetterISOLanguageName == "es";

    private static AgentToolResult AgentResult(bool changed, string summary) =>
        new(changed, summary, JsonSerializer.Serialize(new { success = true, changed, summary }), true);
    internal bool EmptyPageIsFrameless => Welcome.BorderThickness == new Thickness(0);
    internal bool AddPageForDiagnostics() => AddBlankWorkspacePage();
    internal void AddTimerForDiagnostics() => AddTimerWidget();
    internal void RemoveTimerForDiagnostics() => RemoveTimerWidget();
    internal void SwitchPageForDiagnostics(int index) => SwitchWorkspacePage(index);
    internal void NavigatePageForDiagnostics(int direction) => NavigateWorkspace(direction, 0);
    internal void NavigatePageForDiagnostics(int dx, int dy) => NavigateWorkspace(dx, dy);
    internal bool ShowSwipeMidpointForDiagnostics(int direction) => ShowSwipeMidpointForDiagnostics(direction, 0);
    internal bool ShowSwipeMidpointForDiagnostics(int dx, int dy)
    {
        int? target = FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy);
        if (pageTransitioning || target is null) return false;
        PrepareCarousel(dx, dy, target, false);
        swipeActive = true;
        double extent = CarouselExtent(dx, dy);
        swipeOffset = -(dx != 0 ? dx : dy) * extent * .48;
        ApplyCarouselOffset(swipeOffset);
        return PagePreviewArea.Visibility == Visibility.Visible && Math.Abs(swipeOffset) > 20;
    }
    internal void CancelSwipeForDiagnostics()
    {
        swipeActive = false;
        swipeCandidate = false;
        ResetCarousel(true);
    }
}
