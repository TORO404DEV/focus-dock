using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Shared ledger on the canvas. The card is only a lens: closing it never deletes a movement.
/// The entry field stays pinned to the bottom at every size; the movement list is the only part that scrolls.
/// </summary>
internal sealed class FinanceBoard : Grid, IReskinnable
{
    private static readonly FontFamily Ui = new("Consolas");
    private static readonly FontFamily Display = new("Consolas");
    private static readonly SolidColorBrush StampBlack = Freeze(Color.FromRgb(0x17, 0x19, 0x16));
    private static readonly SolidColorBrush StampPaper = Freeze(Color.FromRgb(0xF4, 0xF1, 0xE8));
    private bool Night => Theme.Luminance(Theme.Of("Paper")) < 0.45;
    private Color IncomeColor => Night ? Color.FromRgb(0x8F, 0xC9, 0xA0) : Color.FromRgb(0x1E, 0x7A, 0x46);
    private Color ExpenseColor => Night ? Color.FromRgb(0xE3, 0x8B, 0x84) : Color.FromRgb(0xB2, 0x3B, 0x32);
    private SolidColorBrush Income => Freeze(IncomeColor);
    private SolidColorBrush Expense => Freeze(ExpenseColor);
    private Brush On(Color color) => Theme.Luminance(color) > 0.58 ? StampBlack : StampPaper;

    private readonly record struct Shape(
        bool Wide, bool Toolbar, bool Summary, bool Chart, bool Categories, bool Hint, bool Composer,
        double ChartH, double Width, double Height);

    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly FinanceStore store;
    private readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private readonly TextBox input = new();
    private readonly TextBlock placeholder = new();
    private readonly TextBlock hint = new();
    private readonly Button draftCurrency = new();
    private readonly Button addButton = new();
    private readonly Border composerDock = new();

    private readonly Grid header = new();
    private readonly Button backButton;
    private readonly Button nextButton;
    private readonly TextBlock monthLabel = new();
    private readonly TextBlock headerNet = new();
    private readonly StackPanel currencyToggles = new() { Orientation = Orientation.Horizontal };

    private readonly Grid toolbar = new();
    private readonly StackPanel spanToggles = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };

    private readonly Border summaryPlate = new();
    private readonly StackPanel summary = new();
    private readonly Border incomeChip = new();
    private readonly Border expenseChip = new();
    private readonly Border composerShell = new();
    private readonly TextBlock netCaption = new();
    private readonly TextBlock streakLabel = new();
    private readonly TextBlock rateLabel = new();
    private readonly Button rateRefresh = new();
    private readonly TextBlock netLabel = new();
    private readonly TextBlock currencyMark = new();
    private readonly TextBlock incomeLabel = new();
    private readonly TextBlock expenseLabel = new();
    private readonly TextBlock incomeCaption = new();
    private readonly TextBlock expenseCaption = new();
    private readonly Grid track = new();
    private readonly Border incomeFill = new();
    private readonly TextBlock projectedLabel = new();
    private readonly TextBlock burnLabel = new();
    private readonly Button unpaidButton = new();

    private readonly Grid chartHost = new();
    private readonly StackPanel slices = new();
    private readonly StackPanel list = new();
    private readonly StackPanel scrollStack = new();
    private readonly ScrollViewer scroller;
    private readonly ScrollViewer sideScroll = new();
    private readonly StackPanel side = new();
    private readonly Grid ledger = new();
    private readonly Border ledgerFrame = new();

    private DateOnly cursor = DateOnly.FromDateTime(DateTime.Today);
    private FinanceSpan span = FinanceSpan.Month;
    private int pieDays = 30;
    private string view = "moves";
    private string draftCode = MoneyFx.Mxn;
    private bool listening;
    private bool fetchingRate;
    private bool? wideOn;
    private Shape shape;
    private decimal shownIncome;
    private decimal shownExpense;
    private int unpaidFixed;
    private int unpaidSubs;

    public FinanceBoard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner;
        this.config = config;
        store = FinanceStore.For(owner.Store);
        draftCode = store.Book.Currency;

        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 0;
        MinHeight = 0;
        Margin = new Thickness(0, 4, 0, 0);

        backButton = Nav("‹", () => Shift(-1));
        nextButton = Nav("›", () => Shift(1));
        scroller = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            MinHeight = 0
        };

        BuildHeader();
        BuildSummary();
        summaryPlate.Child = summary;
        summaryPlate.CornerRadius = new CornerRadius(0);
        summaryPlate.Padding = new Thickness(12, 10, 12, 12);
        summaryPlate.Margin = new Thickness(0, 8, 4, 6);
        BuildComposer();
        sideScroll.Content = side;
        sideScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        sideScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        sideScroll.Focusable = false;
        sideScroll.MinHeight = 0;
        ledgerFrame.Child = ledger;
        ledgerFrame.BorderThickness = new Thickness(1, 0, 0, 0);
        ledgerFrame.Padding = new Thickness(12, 0, 0, 0);
        ledgerFrame.SetResourceReference(Border.BorderBrushProperty, "Line");

        chartHost.MinHeight = 0;
        slices.Margin = new Thickness(0, 6, 0, 0);

        track.SizeChanged += (_, _) => SyncTrack();
        settle.Tick += (_, _) => { settle.Stop(); Render(); };
        SizeChanged += (_, _) => { settle.Stop(); settle.Start(); };
        Loaded += (_, _) =>
        {
            if (!listening) { store.Changed += Render; listening = true; }
            Render();
            _ = RefreshRateAsync(false);
        };
        Unloaded += (_, _) =>
        {
            if (listening) { store.Changed -= Render; listening = false; }
            settle.Stop();
        };
        Render();
    }

    public void Reskin() => Render();

    // ---------------------------------------------------------------- layout

    private Shape Measure()
    {
        double w = ActualWidth > 8 ? ActualWidth : Math.Max(220, config.Width - 22);
        double h = ActualHeight > 8 ? ActualHeight : Math.Max(180, config.Height - 54);
        // Wide puts the chart beside the list, so it cannot push the entry field off the card.
        if (w >= 700 && h >= 440)
            return new Shape(true, true, true, true, false, true, true, Math.Clamp(h * 0.36, 200, 280), w, h);

        // Stacked autos are only the header, the toolbar, the summary and the field.
        // The chart and the categories live inside the scrolling list, so a long ledger
        // cannot push the field off the card.
        bool composer = h >= 72;
        bool toolbar = true, summaryOn = true;
        double toolbarH = w < 440 ? 76 : 42;
        double Cost(bool hintLine = false) =>
            40 + (toolbar ? toolbarH : 0) + (summaryOn ? 220 : 0) + (composer ? 58 : 0) + (hintLine ? 18 : 0);
        bool Fits(bool hintLine = false) => Cost(hintLine) <= h - 4;
        if (!Fits()) summaryOn = false;
        if (!Fits()) toolbar = false;
        double star = Math.Max(0, h - Cost());
        bool categories = false;
        bool chart = star >= 160;
        double chartH = chart ? Math.Clamp(star * 0.62, 180, 250) : 0;
        bool hint = composer && w >= 240 && Fits(true);
        return new Shape(false, toolbar, summaryOn, chart, categories, hint, composer, chartH, w, h);
    }

    private void Render()
    {
        shape = Measure();
        if (wideOn != shape.Wide) Mount(shape.Wide);
        ApplyChrome();
        Paint();
    }

    /// <summary>
    /// The list used to live in an Auto row, so each new expense made the board taller than the card
    /// and the entry field was clipped away. The list is the star; the field is an Auto row after it.
    /// </summary>
    private void Mount(bool wide)
    {
        bool focus = input.IsKeyboardFocusWithin;
        Detach(header); Detach(toolbar); Detach(summaryPlate); Detach(chartHost); Detach(slices);
        Detach(scroller); Detach(composerDock); Detach(sideScroll); Detach(ledgerFrame);
        side.Children.Clear();
        ledger.Children.Clear();
        ledger.RowDefinitions.Clear();
        ledger.ColumnDefinitions.Clear();
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        wideOn = wide;

        Detach(list);
        if (!wide)
        {
            scrollStack.Children.Clear();
            scrollStack.Children.Add(chartHost);
            scrollStack.Children.Add(slices);
            scrollStack.Children.Add(list);
            scroller.Content = scrollStack;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
            Slot(header, 0, 0, GridLength.Auto);
            Slot(toolbar, 1, 0, GridLength.Auto);
            Slot(summaryPlate, 2, 0, GridLength.Auto);
            Slot(scroller, 3, 0, new GridLength(1, GridUnitType.Star));
            Slot(composerDock, 4, 0, GridLength.Auto);
        }
        else
        {
            scroller.Content = list;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.92, GridUnitType.Star), MinWidth = 0 });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 0 });
            Slot(header, 0, 0, GridLength.Auto, 2);
            Slot(toolbar, 1, 0, GridLength.Auto, 2);
            side.Children.Add(summaryPlate);
            side.Children.Add(chartHost);
            side.Children.Add(slices);
            Slot(sideScroll, 2, 0, new GridLength(1, GridUnitType.Star));

            ledger.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 0 });
            ledger.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });
            ledger.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 0 });
            SetRow(scroller, 0);
            SetRow(composerDock, 1);
            ledger.Children.Add(scroller);
            ledger.Children.Add(composerDock);
            Slot(ledgerFrame, 2, 1, new GridLength(1, GridUnitType.Star));
        }

        if (focus) Dispatcher.BeginInvoke(() => input.Focus(), DispatcherPriority.Input);
    }

    private void Slot(UIElement element, int row, int column, GridLength height, int columnSpan = 1)
    {
        while (RowDefinitions.Count <= row)
            RowDefinitions.Add(new RowDefinition { MinHeight = 0 });
        RowDefinitions[row].Height = height;
        RowDefinitions[row].MinHeight = 0;
        SetRow(element, row);
        SetColumn(element, column);
        SetColumnSpan(element, columnSpan);
        Children.Add(element);
    }

    private static void Detach(UIElement element)
    {
        switch (element)
        {
            case FrameworkElement { Parent: Panel panel }:
                panel.Children.Remove(element);
                break;
            case FrameworkElement { Parent: ContentControl control }:
                control.Content = null;
                break;
            case FrameworkElement { Parent: Decorator decorator }:
                decorator.Child = null;
                break;
        }
    }

    private void ApplyChrome()
    {
        toolbar.Visibility = shape.Toolbar ? Visibility.Visible : Visibility.Collapsed;
        summaryPlate.Visibility = shape.Summary ? Visibility.Visible : Visibility.Collapsed;
        headerNet.Visibility = shape.Summary ? Visibility.Collapsed : Visibility.Visible;
        chartHost.Visibility = shape.Chart ? Visibility.Visible : Visibility.Collapsed;
        chartHost.Height = shape.Chart ? shape.ChartH : 0;
        slices.Visibility = shape.Categories ? Visibility.Visible : Visibility.Collapsed;
        composerDock.Visibility = shape.Composer ? Visibility.Visible : Visibility.Collapsed;
        incomeCaption.Visibility = shape.Width >= 380 ? Visibility.Visible : Visibility.Collapsed;
        expenseCaption.Visibility = shape.Width >= 380 ? Visibility.Visible : Visibility.Collapsed;
        composerDock.BorderThickness = new Thickness(0);
        composerDock.Padding = new Thickness(0, shape.Height < 220 ? 4 : 8, 0, 0);
        Skin();
        hint.Visibility = shape.Hint && hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- chrome

    private void BuildHeader()
    {
        header.MinHeight = 36;
        header.Margin = new Thickness(0, 2, 0, 0);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        monthLabel.FontFamily = Ui;
        monthLabel.FontSize = 13;
        monthLabel.FontWeight = FontWeights.Black;
        monthLabel.VerticalAlignment = VerticalAlignment.Center;
        monthLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        monthLabel.TextWrapping = TextWrapping.NoWrap;
        monthLabel.Margin = new Thickness(6, 0, 6, 0);
        monthLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        headerNet.FontFamily = Display;
        headerNet.FontSize = 14;
        headerNet.FontWeight = FontWeights.SemiBold;
        headerNet.VerticalAlignment = VerticalAlignment.Center;
        headerNet.TextTrimming = TextTrimming.CharacterEllipsis;
        headerNet.TextWrapping = TextWrapping.NoWrap;
        headerNet.Margin = new Thickness(0, 0, 6, 0);
        headerNet.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        var center = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        DockPanel.SetDock(headerNet, Dock.Right);
        center.Children.Add(headerNet);
        center.Children.Add(monthLabel);

        currencyToggles.VerticalAlignment = VerticalAlignment.Center;
        currencyToggles.Margin = new Thickness(0, 0, 4, 0);
        nextButton.Margin = new Thickness(0);

        SetColumn(center, 1);
        SetColumn(currencyToggles, 2);
        SetColumn(nextButton, 3);
        header.Children.Add(backButton);
        header.Children.Add(center);
        header.Children.Add(currencyToggles);
        header.Children.Add(nextButton);
    }

    private void BuildSummary()
    {
        summary.Margin = new Thickness(0);
        summary.ClipToBounds = false;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var captionRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        netCaption.FontFamily = Ui;
        netCaption.FontSize = 12;
        netCaption.FontWeight = FontWeights.Medium;
        netCaption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        streakLabel.FontFamily = Ui;
        streakLabel.FontSize = 12;
        streakLabel.FontWeight = FontWeights.Medium;
        streakLabel.Margin = new Thickness(8, 0, 0, 0);
        streakLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        captionRow.Children.Add(netCaption);
        captionRow.Children.Add(streakLabel);
        top.Children.Add(captionRow);

        var rate = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rateLabel.FontFamily = Ui;
        rateLabel.FontSize = 12;
        rateLabel.Cursor = Cursors.Hand;
        rateLabel.TextWrapping = TextWrapping.NoWrap;
        rateLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        rateLabel.MouseLeftButtonUp += (_, _) => EditRate();
        rateRefresh.Content = "↻";
        rateRefresh.Width = 22;
        rateRefresh.Height = 22;
        rateRefresh.Padding = new Thickness(0);
        rateRefresh.Margin = new Thickness(2, 0, 0, 0);
        rateRefresh.FontSize = 12;
        rateRefresh.Background = Brushes.Transparent;
        Soften(rateRefresh, 11);
        rateRefresh.SetResourceReference(Button.ForegroundProperty, "Muted");
        rateRefresh.Click += async (_, _) => await RefreshRateAsync(true);
        rate.Children.Add(rateLabel);
        rate.Children.Add(rateRefresh);
        SetColumn(rate, 1);
        top.Children.Add(rate);
        summary.Children.Add(top);

        netLabel.FontFamily = Display;
        netLabel.FontWeight = FontWeights.Black;
        netLabel.TextWrapping = TextWrapping.NoWrap;
        netLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        netLabel.VerticalAlignment = VerticalAlignment.Bottom;
        netLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        currencyMark.FontFamily = Ui;
        currencyMark.FontSize = 13;
        currencyMark.FontWeight = FontWeights.SemiBold;
        currencyMark.Margin = new Thickness(8, 0, 0, 3);
        currencyMark.VerticalAlignment = VerticalAlignment.Bottom;
        currencyMark.TextWrapping = TextWrapping.NoWrap;
        currencyMark.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var hero = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2), MinWidth = 0 };
        DockPanel.SetDock(currencyMark, Dock.Right);
        hero.Children.Add(currencyMark);
        hero.Children.Add(netLabel);
        summary.Children.Add(hero);

        var split = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        split.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 0 });
        split.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 0 });
        FillChip(incomeChip, incomeLabel, incomeCaption, new Thickness(0, 0, 8, 0));
        FillChip(expenseChip, expenseLabel, expenseCaption, new Thickness(0));
        split.Children.Add(incomeChip);
        SetColumn(expenseChip, 1);
        split.Children.Add(expenseChip);
        summary.Children.Add(split);

        incomeFill.HorizontalAlignment = HorizontalAlignment.Left;
        incomeFill.VerticalAlignment = VerticalAlignment.Stretch;
        track.Height = 8;
        track.Margin = new Thickness(0, 12, 0, 0);
        track.ClipToBounds = true;
        track.Children.Add(incomeFill);
        summary.Children.Add(track);

        projectedLabel.FontFamily = Ui;
        projectedLabel.FontSize = 12;
        projectedLabel.TextWrapping = TextWrapping.NoWrap;
        projectedLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        projectedLabel.Margin = new Thickness(0, 6, 0, 0);
        projectedLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        summary.Children.Add(projectedLabel);

        var foot = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        burnLabel.FontFamily = Ui;
        burnLabel.FontSize = 12;
        burnLabel.TextWrapping = TextWrapping.NoWrap;
        burnLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        burnLabel.VerticalAlignment = VerticalAlignment.Center;
        burnLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        unpaidButton.FontFamily = Ui;
        unpaidButton.FontSize = 11;
        unpaidButton.FontWeight = FontWeights.SemiBold;
        unpaidButton.Padding = new Thickness(8, 3, 8, 3);
        unpaidButton.HorizontalAlignment = HorizontalAlignment.Left;
        Soften(unpaidButton, 9);
        unpaidButton.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(unpaidButton, 1);
        unpaidButton.Click += (_, _) =>
        {
            view = unpaidFixed == 0 && unpaidSubs > 0 ? "subs" : "fixed";
            Render();
        };
        foot.Children.Add(burnLabel);
        foot.Children.Add(unpaidButton);
        summary.Children.Add(foot);
    }

    private static void FillChip(Border chip, TextBlock value, TextBlock caption, Thickness margin)
    {
        caption.FontFamily = Ui;
        caption.FontSize = 11;
        caption.FontWeight = FontWeights.Medium;
        caption.Margin = new Thickness(0, 0, 0, 2);
        caption.TextWrapping = TextWrapping.NoWrap;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        value.FontFamily = Display;
        value.FontSize = 15;
        value.FontWeight = FontWeights.SemiBold;
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        var stack = new StackPanel { MinWidth = 0 };
        stack.Children.Add(caption);
        stack.Children.Add(value);
        chip.Child = stack;
        chip.CornerRadius = new CornerRadius(0);
        chip.BorderThickness = new Thickness(2);
        chip.BorderBrush = AgendaVisuals.Resource("Ink");
        chip.Padding = new Thickness(8, 6, 8, 6);
        chip.Margin = margin;
        chip.ClipToBounds = true;
    }

    private void BuildComposer()
    {
        input.Height = 36;
        input.MinWidth = 0;
        input.Margin = new Thickness(0);
        input.Padding = new Thickness(8, 0, 8, 0);
        input.FontFamily = Ui;
        input.FontSize = 14;
        input.BorderThickness = new Thickness(0);
        input.Background = Brushes.Transparent;
        input.TextWrapping = TextWrapping.NoWrap;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Hidden);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { input.Clear(); e.Handled = true; }
        };
        input.TextChanged += (_, _) => UpdateHint();

        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(11, 0, 8, 0);
        placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
        placeholder.TextWrapping = TextWrapping.NoWrap;
        placeholder.FontFamily = Ui;
        placeholder.FontSize = 14;
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var field = new Grid { MinWidth = 0, Margin = new Thickness(2, 0, 4, 0) };
        field.Children.Add(input);
        field.Children.Add(placeholder);

        draftCurrency.MinWidth = 52;
        draftCurrency.Height = 32;
        draftCurrency.Padding = new Thickness(10, 0, 10, 0);
        draftCurrency.Margin = new Thickness(0);
        draftCurrency.FontFamily = Ui;
        draftCurrency.FontSize = 12;
        draftCurrency.FontWeight = FontWeights.SemiBold;
        draftCurrency.Background = Brushes.Transparent;
        Soften(draftCurrency, 12);
        draftCurrency.Click += (_, _) =>
        {
            draftCode = draftCode == MoneyFx.Mxn ? MoneyFx.Usd : MoneyFx.Mxn;
            UpdateHint();
            Render();
        };

        addButton.Content = "+";
        addButton.Width = 36;
        addButton.Height = 36;
        addButton.Padding = new Thickness(0);
        addButton.Margin = new Thickness(0);
        addButton.FontFamily = Ui;
        addButton.FontSize = 20;
        addButton.FontWeight = FontWeights.SemiBold;
        Soften(addButton, 18);
        addButton.Click += (_, _) => Commit();

        var row = new Grid { MinHeight = 36 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(field, 1);
        SetColumn(addButton, 2);
        row.Children.Add(draftCurrency);
        row.Children.Add(field);
        row.Children.Add(addButton);

        hint.FontFamily = Ui;
        hint.FontSize = 12;
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        hint.Margin = new Thickness(2, 4, 0, 0);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        composerShell.CornerRadius = new CornerRadius(0);
        composerShell.Padding = new Thickness(4);
        composerShell.Margin = new Thickness(0, 0, 4, 4);
        composerShell.Child = row;
        var stack = new StackPanel();
        stack.Children.Add(composerShell);
        stack.Children.Add(hint);
        composerDock.Child = stack;
        composerDock.BorderThickness = new Thickness(0);
        composerDock.Margin = new Thickness(0, 4, 0, 2);
    }

    // ---------------------------------------------------------------- paint

    private void Paint()
    {
        var book = store.Book;
        var snapshot = book.Month(cursor);
        var (start, end) = book.SpanRange(span, cursor);
        var inRange = book.Entries.Where(entry => entry.Date >= start && entry.Date <= end)
            .OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.CreatedUtc).ToList();
        shownIncome = span == FinanceSpan.Month ? snapshot.Recorded.Income : inRange.Where(e => e.Flow == MoneyFlow.Income).Sum(book.ToDisplay);
        shownExpense = span == FinanceSpan.Month ? snapshot.Recorded.Expense : inRange.Where(e => e.Flow == MoneyFlow.Expense).Sum(book.ToDisplay);
        decimal net = shownIncome - shownExpense;
        string money(decimal value) => FinanceBook.Format(value, book.Currency, Strings.Culture);

        monthLabel.Text = PeriodTitle(start, end);
        string netText = Signed(net, book.Currency);
        netLabel.Text = netText;
        headerNet.Text = netText;
        currencyMark.Text = book.Currency;
        double column = shape.Wide ? shape.Width * 0.42 : shape.Width;
        int chars = Math.Max(6, netText.Length + 3);
        netLabel.FontSize = Math.Clamp((column - 36) / (chars * 0.62), 20, shape.Wide ? 40 : 34);
        headerNet.FontSize = Math.Clamp(shape.Width / 22, 13, 16);
        var netColor = net < 0 ? ExpenseColor : net > 0 ? IncomeColor : Theme.Of("Ink");
        netLabel.Foreground = Freeze(netColor);
        headerNet.Foreground = Freeze(netColor);

        incomeLabel.Text = "+" + Compact(shownIncome, book.Currency);
        expenseLabel.Text = "−" + Compact(shownExpense, book.Currency);
        incomeLabel.ToolTip = L.T("finance.income");
        expenseLabel.ToolTip = L.T("finance.expense");
        incomeCaption.Text = Soft(L.T("finance.income"));
        expenseCaption.Text = Soft(L.T("finance.expense"));
        netCaption.Text = Soft(L.T("finance.net"));
        incomeLabel.Foreground = On(IncomeColor);
        expenseLabel.Foreground = On(ExpenseColor);
        incomeCaption.Foreground = On(IncomeColor);
        expenseCaption.Foreground = On(ExpenseColor);
        incomeChip.Background = Income;
        expenseChip.Background = Expense;
        incomeChip.BorderBrush = AgendaVisuals.Resource("Ink");
        expenseChip.BorderBrush = AgendaVisuals.Resource("Ink");

        int streak = book.SurplusStreak(cursor);
        streakLabel.Text = streak > 0 ? L.T("finance.streak", streak.ToString(CultureInfo.InvariantCulture)) : "";
        streakLabel.Visibility = streak > 0 && shape.Width >= 520 && shape.Summary ? Visibility.Visible : Visibility.Collapsed;

        rateLabel.Text = L.T("finance.rate", book.UsdToMxn.ToString("0.##", CultureInfo.InvariantCulture));
        rateLabel.ToolTip = L.T("finance.rateTitle");
        rateRefresh.ToolTip = L.T("finance.refresh");

        track.Background = Expense;
        incomeFill.Background = Income;
        track.Visibility = shownIncome + shownExpense > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncTrack();

        bool drift = span == FinanceSpan.Month && snapshot.ProjectedNet != snapshot.Recorded.Net;
        projectedLabel.Text = drift ? L.T("finance.projected", Signed(snapshot.ProjectedNet, book.Currency)) : "";
        projectedLabel.Visibility = drift && shape.Summary ? Visibility.Visible : Visibility.Collapsed;

        decimal fixedTotal = snapshot.Fixed.Sum(item => book.ToDisplay(item.Item));
        decimal subsTotal = snapshot.Subscriptions.Sum(item => book.ToDisplay(item.Item));
        var burn = new List<string>();
        if (fixedTotal > 0) burn.Add(Soft(L.T("finance.tabFixed")) + " " + Compact(fixedTotal, book.Currency));
        if (subsTotal > 0) burn.Add(Soft(L.T("finance.tabSubs")) + " " + Compact(subsTotal, book.Currency));
        burnLabel.Text = string.Join("   ·   ", burn);
        burnLabel.ToolTip = L.T("finance.obligations");
        burnLabel.Visibility = burn.Count > 0 && shape.Summary ? Visibility.Visible : Visibility.Collapsed;

        unpaidFixed = span == FinanceSpan.Month ? snapshot.Fixed.Count(item => !item.Recorded) : 0;
        unpaidSubs = span == FinanceSpan.Month ? snapshot.Subscriptions.Count(item => !item.Recorded) : 0;
        int unpaid = unpaidFixed + unpaidSubs;
        unpaidButton.Content = unpaid > 0 ? Soft(L.T("finance.unpaid", unpaid.ToString(CultureInfo.InvariantCulture))) : "";
        unpaidButton.Background = Expense;
        unpaidButton.Foreground = On(ExpenseColor);
        unpaidButton.BorderBrush = AgendaVisuals.Resource("Ink");
        unpaidButton.Visibility = unpaid > 0 && shape.Summary ? Visibility.Visible : Visibility.Collapsed;

        bool shortLabels = shape.Width < 520;
        FillSegment(currencyToggles, [MoneyFx.Usd, MoneyFx.Mxn], book.Currency, code => { draftCode = code; store.SetCurrency(code); }, font: 12, pad: 8);
        FillSegment(spanToggles,
            [nameof(FinanceSpan.Week), nameof(FinanceSpan.Month), nameof(FinanceSpan.Quarter), nameof(FinanceSpan.Year)],
            span.ToString(),
            key => { span = Enum.Parse<FinanceSpan>(key, true); Render(); },
            key => Soft(L.T("finance.span." + key.ToLowerInvariant())),
            font: 12,
            pad: shortLabels ? 7 : 10);
        FillSegment(tabs,
            ["moves", "fixed", "subs"],
            view,
            key => { view = key; Render(); },
            key => Soft(L.T(shortLabels ? "finance.tab" + TabKey(key) + "Short" : "finance.tab" + TabKey(key))),
            font: 12,
            pad: shortLabels ? 7 : 10);
        PaintToolbar(shape.Width >= 440);

        var (pieStart, pieEnd) = PieRange();
        var spent = book.Categories(pieStart, pieEnd);
        chartHost.Visibility = shape.Chart ? Visibility.Visible : Visibility.Collapsed;
        chartHost.Height = shape.Chart ? shape.ChartH : 0;
        if (shape.Chart) PaintPie(spent, money, pieStart, pieEnd);
        slices.Visibility = Visibility.Collapsed;

        double keep = scroller.VerticalOffset;
        list.Children.Clear();
        if (view == "fixed") PaintRecurring(snapshot.Fixed, false);
        else if (view == "subs") PaintRecurring(snapshot.Subscriptions, true);
        else PaintMoves(span == FinanceSpan.Month ? snapshot.Entries : inRange, money);
        if (keep > 0)
            Dispatcher.BeginInvoke(() => scroller.ScrollToVerticalOffset(keep), DispatcherPriority.Loaded);

        draftCurrency.Content = draftCode;
        draftCurrency.ToolTip = L.T("finance.currency");
        addButton.ToolTip = L.T("common.add");
        AutomationProperties.SetName(addButton, L.T("common.add"));
        placeholder.Text = L.T("finance.placeholder");
        input.ToolTip = L.T("finance.hint");
        AutomationProperties.SetName(input, L.T("finance.placeholder"));
        UpdateHint();
    }

    private static string TabKey(string key) => key switch
    {
        "fixed" => "Fixed",
        "subs" => "Subs",
        _ => "Moves"
    };

    private void PaintToolbar(bool split)
    {
        Detach(spanToggles);
        Detach(tabs);
        toolbar.Children.Clear();
        toolbar.ColumnDefinitions.Clear();
        toolbar.Margin = new Thickness(0, 4, 0, 2);
        tabs.Margin = new Thickness(split ? 0 : 8, 0, 0, 0);
        if (!split)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            wrap.Children.Add(PillTrack(spanToggles));
            wrap.Children.Add(PillTrack(tabs));
            toolbar.Children.Add(wrap);
            return;
        }
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tabTrack = PillTrack(tabs);
        SetColumn(tabTrack, 2);
        toolbar.Children.Add(PillTrack(spanToggles));
        toolbar.Children.Add(tabTrack);
    }

    private string PeriodTitle(DateOnly start, DateOnly end)
    {
        string text = span switch
        {
            FinanceSpan.Week when start.Month == end.Month => $"{start.ToString("d", Strings.Culture)}–{end.ToString("d MMM yyyy", Strings.Culture)}",
            FinanceSpan.Week => $"{start.ToString("d MMM", Strings.Culture)}–{end.ToString("d MMM", Strings.Culture)}",
            FinanceSpan.Quarter => (Strings.Culture.TwoLetterISOLanguageName == "es" ? "T" : "Q") + $"{(start.Month - 1) / 3 + 1} {start.Year}",
            FinanceSpan.Year => start.Year.ToString(CultureInfo.InvariantCulture),
            _ => cursor.ToString("MMMM yyyy", Strings.Culture)
        };
        return text.ToUpper(Strings.Culture);
    }

    private void SyncTrack()
    {
        decimal total = shownIncome + shownExpense;
        if (track.ActualWidth > 0 && track.ActualHeight > 0)
            track.Clip = new RectangleGeometry(new Rect(0, 0, track.ActualWidth, track.ActualHeight));
        if (total <= 0 || track.ActualWidth <= 0) { incomeFill.Width = 0; return; }
        incomeFill.Width = track.ActualWidth * (double)(shownIncome / total);
    }

    private void FillSegment(Panel host, IReadOnlyList<string> keys, string current, Action<string> pick, Func<string, string>? label = null, double font = 9, double pad = 7)
    {
        host.Children.Clear();
        for (int i = 0; i < keys.Count; i++)
        {
            bool on = string.Equals(keys[i], current, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = label?.Invoke(keys[i]) ?? keys[i],
                FontFamily = Ui,
                FontSize = font,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium,
                Padding = new Thickness(pad, 4, pad, 4),
                Margin = new Thickness(1, 0, 1, 0),
                MinHeight = 26,
            Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
            Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink"),
            BorderBrush = AgendaVisuals.Resource("Ink")
        };
        Soften(button, 0);
            string picked = keys[i];
            button.Click += (_, _) => pick(picked);
            host.Children.Add(button);
        }
    }

    private (DateOnly Start, DateOnly End) PieRange()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        return pieDays <= 1 ? (end, end) : (end.AddDays(1 - pieDays), end);
    }

    private void PaintPie(IReadOnlyList<FinanceSlice> items, Func<decimal, string> money, DateOnly start, DateOnly end)
    {
        chartHost.Children.Clear();
        chartHost.RowDefinitions.Clear();
        chartHost.ColumnDefinitions.Clear();
        chartHost.Margin = new Thickness(0, 10, 0, 2);
        chartHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chartHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        var title = new TextBlock
        {
            Text = L.T("finance.pieSpent"),
            FontFamily = Ui,
            FontSize = 12,
            FontWeight = FontWeights.Black,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.NoWrap
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        head.Children.Add(title);
        var windows = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        FillSegment(windows, ["1", "7", "30", "90", "365"], pieDays.ToString(CultureInfo.InvariantCulture),
            key => { pieDays = int.Parse(key, CultureInfo.InvariantCulture); Render(); },
            key => key == "365" ? L.T("finance.pieYear") : L.T("finance.pieDays", key),
            font: 11, pad: 7);
        SetColumn(windows, 1);
        head.Children.Add(windows);
        chartHost.Children.Add(head);

        bool beside = shape.Width >= 380;
        var body = new Grid { MinHeight = 0 };
        if (beside)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        }
        else
        {
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });
        }

        double pieSize = beside
            ? Math.Clamp(shape.ChartH - 44, 108, 210)
            : Math.Clamp(shape.ChartH * 0.58, 96, 150);
        var pie = BuildPie(items, pieSize);
        pie.HorizontalAlignment = HorizontalAlignment.Left;
        pie.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(pie);

        string range = start == end
            ? start.ToString("d MMM", Strings.Culture)
            : $"{start.ToString("d MMM", Strings.Culture)}–{end.ToString("d MMM", Strings.Culture)}";
        var legend = BuildLegend(items, money, range.ToUpper(Strings.Culture));
        if (beside)
        {
            SetColumn(legend, 1);
            legend.Margin = new Thickness(14, 0, 0, 0);
        }
        else
        {
            SetRow(legend, 1);
            legend.Margin = new Thickness(0, 8, 0, 0);
        }
        body.Children.Add(legend);
        SetRow(body, 1);
        chartHost.Children.Add(body);
    }

    private static FrameworkElement BuildPie(IReadOnlyList<FinanceSlice> items, double size)
    {
        double inset = 3;
        double diameter = Math.Max(8, size - inset * 2);
        var canvas = new Canvas { Width = size, Height = size };
        if (items.Count == 0)
        {
            var empty = Ring(diameter);
            Canvas.SetLeft(empty, inset);
            Canvas.SetTop(empty, inset);
            canvas.Children.Add(empty);
            return canvas;
        }
        if (items.Count == 1 || items[0].Share >= 0.999)
        {
            var whole = Ring(diameter);
            whole.Fill = SliceBrush(items[0].Category);
            Canvas.SetLeft(whole, inset);
            Canvas.SetTop(whole, inset);
            canvas.Children.Add(whole);
            return canvas;
        }

        var center = new Point(size / 2, size / 2);
        double radius = diameter / 2;
        double angle = 0;
        foreach (var slice in items)
        {
            double sweep = slice.Share * 360;
            if (sweep < 0.4) { angle += sweep; continue; }
            canvas.Children.Add(SlicePath(center, radius, angle, Math.Min(sweep, 359.9), SliceBrush(slice.Category)));
            angle += sweep;
        }
        return canvas;
    }

    private static Ellipse Ring(double diameter) => new()
    {
        Width = diameter,
        Height = diameter,
        Stroke = AgendaVisuals.Resource("Ink"),
        StrokeThickness = 2.5,
        Fill = AgendaVisuals.Resource("Surface")
    };

    private static Path SlicePath(Point center, double radius, double start, double sweep, Brush fill)
    {
        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(Polar(center, radius, start), true));
        figure.Segments.Add(new ArcSegment(
            Polar(center, radius, start + sweep),
            new Size(radius, radius),
            0,
            sweep > 180,
            SweepDirection.Clockwise,
            true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return new Path
        {
            Data = geometry,
            Fill = fill,
            Stroke = AgendaVisuals.Resource("Ink"),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Miter
        };
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        double rad = (degrees - 90) * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    private static SolidColorBrush SliceBrush(string category)
    {
        var color = category switch
        {
            "rent" => Color.FromRgb(0xC4, 0x6B, 0x4A),
            "food" => Color.FromRgb(0xC4, 0xA3, 0x5A),
            "ai" => Color.FromRgb(0x5C, 0x6B, 0x7A),
            "transport" => Color.FromRgb(0xC4, 0x84, 0x3A),
            "utilities" => Color.FromRgb(0x4F, 0x7C, 0x8A),
            "health" => Color.FromRgb(0xC4, 0x6B, 0x73),
            "software" => Color.FromRgb(0x3A, 0x3F, 0x38),
            "hosting" => Color.FromRgb(0x5E, 0x8A, 0x6A),
            "media" => Color.FromRgb(0xA6, 0x7C, 0x5D),
            _ => Color.FromRgb(0xB0, 0xAA, 0x9E)
        };
        return Freeze(color);
    }

    private FrameworkElement BuildLegend(IReadOnlyList<FinanceSlice> items, Func<decimal, string> money, string range)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var when = new TextBlock
        {
            Text = range,
            FontFamily = Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        stack.Children.Add(when);
        if (items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = L.T("finance.pieEmpty"),
                FontFamily = Ui,
                FontSize = 12,
                FontWeight = FontWeights.Black,
                TextWrapping = TextWrapping.Wrap
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            stack.Children.Add(empty);
            return stack;
        }

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack
        };
        foreach (var slice in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5), MinWidth = 0 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                Background = SliceBrush(slice.Category),
                BorderBrush = AgendaVisuals.Resource("Ink"),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(swatch);
            var name = new TextBlock
            {
                Text = L.T("finance.cat." + slice.Category),
                FontFamily = Ui,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            SetColumn(name, 1);
            row.Children.Add(name);
            var share = new TextBlock
            {
                Text = (slice.Share * 100).ToString("0", Strings.Culture) + "%",
                FontFamily = Ui,
                FontSize = 12,
                FontWeight = FontWeights.Black,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };
            share.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            SetColumn(share, 2);
            row.Children.Add(share);
            var amount = new TextBlock
            {
                Text = Compact(slice.Amount, store.Book.Currency),
                FontFamily = Display,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };
            amount.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SetColumn(amount, 3);
            row.Children.Add(amount);
            stack.Children.Add(row);
        }
        return scroller;
    }

    private void PaintMoves(IReadOnlyList<MoneyEntry> entries, Func<decimal, string> money)
    {
        if (entries.Count == 0)
        {
            list.Children.Add(Empty(L.T("finance.emptyMoves")));
            return;
        }
        DateOnly? last = null;
        foreach (var entry in entries)
        {
            if (entry.Date != last)
            {
                list.Children.Add(DayHead(entry.Date, last is null));
                last = entry.Date;
            }
            list.Children.Add(MoveRow(entry, money));
        }
    }

    private void PaintRecurring(IReadOnlyList<RecurringDue> items, bool subscriptions)
    {
        if (items.Count == 0)
        {
            list.Children.Add(Empty(subscriptions ? L.T("finance.emptySubs") : L.T("finance.emptyFixed")));
            return;
        }
        foreach (var due in items) list.Children.Add(RecurringRow(due));
    }

    private UIElement DayHead(DateOnly day, bool first)
    {
        string text = day.ToString("ddd dd MMM", Strings.Culture).ToUpper(Strings.Culture).Replace(".", "");
        return new TextBlock
        {
            Text = text,
            FontFamily = Ui,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AgendaVisuals.Resource("Muted"),
            Margin = new Thickness(0, first ? 4 : 10, 0, 2),
            TextWrapping = TextWrapping.NoWrap
        };
    }

    private UIElement MoveRow(MoneyEntry entry, Func<decimal, string> money)
    {
        var book = store.Book;
        var row = Line();
        var grid = new Grid { MinWidth = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string sign = entry.Flow == MoneyFlow.Income ? "+" : "−";
        string meta = $"{L.T("finance.cat." + entry.Category)} · {entry.Currency}";
        if (!string.Equals(entry.Currency, book.Currency, StringComparison.OrdinalIgnoreCase))
            meta += " · " + sign + Compact(book.ToDisplay(entry), book.Currency);
        grid.Children.Add(Lines(entry.Title, meta));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Amount(sign + Compact(entry.Amount, entry.Currency), entry.Flow));
        actions.Children.Add(Quiet("×", () => store.RemoveEntry(entry)));
        SetColumn(actions, 1);
        grid.Children.Add(actions);
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && InsideButton(source)) return;
            EditEntry(entry);
        };
        row.Child = grid;
        return row;
    }

    private UIElement RecurringRow(RecurringDue due)
    {
        var item = due.Item;
        var row = Line();
        var grid = new Grid { MinWidth = 0 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string cadence = item.Repeat == MoneyRepeat.Yearly ? L.T("finance.yearly") : L.T("finance.dayOfMonth", item.DayOfMonth);
        var title = new TextBlock
        {
            Text = item.Title,
            FontFamily = Ui,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        grid.Children.Add(title);
        var amount = Amount((item.Flow == MoneyFlow.Income ? "+" : "−") + Compact(item.Amount, item.Currency), item.Flow);
        amount.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(amount, 1);
        grid.Children.Add(amount);

        var meta = new TextBlock
        {
            Text = $"{L.T("finance.cat." + item.Category)} · {cadence} · {item.Currency}",
            FontFamily = Ui,
            FontSize = 12,
            Margin = new Thickness(0, 2, 8, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        SetRow(meta, 1);
        grid.Children.Add(meta);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        if (!due.Recorded)
        {
            var pay = new Button
            {
                Content = L.T("finance.paid"),
                FontFamily = Ui,
                FontSize = 11,
                FontWeight = FontWeights.Black,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0),
                MinHeight = 0,
                Background = Income,
                Foreground = On(IncomeColor),
                BorderBrush = AgendaVisuals.Resource("Ink")
            };
            Soften(pay, 0);
            pay.Click += (_, _) => store.MarkPaid(item, new DateOnly(cursor.Year, cursor.Month, 1));
            actions.Children.Add(pay);
        }
        else
        {
            var done = new TextBlock
            {
                Text = L.T("finance.alreadyPaid"),
                FontFamily = Ui,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };
            done.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            actions.Children.Add(done);
        }
        actions.Children.Add(Quiet("×", () =>
        {
            if (Dialogs.Choose(owner, L.T("finance.archiveTitle"), [L.T("finance.archive"), L.T("common.cancel")]) == 0)
                store.Archive(item);
        }));
        SetRow(actions, 1);
        SetColumn(actions, 1);
        grid.Children.Add(actions);
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && InsideButton(source)) return;
            EditRecurring(due.Item);
        };
        row.Child = grid;
        return row;
    }

    private Border Line() => new()
    {
        BorderBrush = AgendaVisuals.Resource("Ink"),
        BorderThickness = new Thickness(0, 0, 0, 1.5),
        Padding = new Thickness(0, 7, 0, 7),
        Background = Brushes.Transparent
    };

    private static StackPanel Lines(string title, string meta)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 8, 0), MinWidth = 0 };
        var head = new TextBlock
        {
            Text = title,
            FontFamily = Ui,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var sub = new TextBlock
        {
            Text = meta,
            FontFamily = Ui,
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(head);
        text.Children.Add(sub);
        return text;
    }

    private Border Amount(string text, MoneyFlow flow)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = Display,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = On(flow == MoneyFlow.Income ? IncomeColor : ExpenseColor)
        };
        return new Border
        {
            Background = flow == MoneyFlow.Income ? Income : Expense,
            BorderBrush = AgendaVisuals.Resource("Ink"),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = block
        };
    }

    private Button Quiet(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = Ui,
            FontSize = 14,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.SetResourceReference(Button.ForegroundProperty, "Muted");
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Empty(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 8),
        Foreground = AgendaVisuals.Resource("Muted")
    };

    private static Button Nav(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            FontFamily = Ui,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.SetResourceReference(Button.ForegroundProperty, "Ink");
        Soften(button, 15);
        button.Click += (_, _) => action();
        return button;
    }

    // ---------------------------------------------------------------- edit

    private static bool InsideButton(DependencyObject source)
    {
        while (true)
        {
            if (source is Button) return true;
            if (VisualTreeHelper.GetParent(source) is not DependencyObject parent) return false;
            source = parent;
        }
    }

    private void EditEntry(MoneyEntry entry)
    {
        var flow = entry.Flow;
        var category = entry.Category;
        var currency = string.IsNullOrWhiteSpace(entry.Currency) ? store.Book.Currency : entry.Currency;
        var window = Dialogs.Window(owner, L.T("finance.edit"), 420, 500);
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        window.Content = root;

        var title = Field(entry.Title);
        root.Children.Add(title);

        var amountRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        amountRow.ColumnDefinitions.Add(new ColumnDefinition());
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var amount = Field(entry.Amount.ToString("0.##", CultureInfo.InvariantCulture));
        amount.Margin = new Thickness(0, 0, 8, 0);
        amountRow.Children.Add(amount);
        var usd = Chip(MoneyFx.Usd, () => { });
        var mxn = Chip(MoneyFx.Mxn, () => { });
        void PaintMoney()
        {
            Stamp(usd, currency == MoneyFx.Usd);
            Stamp(mxn, currency == MoneyFx.Mxn);
        }
        usd.Click += (_, _) => { currency = MoneyFx.Usd; PaintMoney(); };
        mxn.Click += (_, _) => { currency = MoneyFx.Mxn; PaintMoney(); };
        var money = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        money.Children.Add(usd);
        money.Children.Add(mxn);
        SetColumn(money, 1);
        amountRow.Children.Add(money);
        root.Children.Add(amountRow);

        var flows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var incomeBtn = Chip(L.T("finance.income"), () => { });
        var expenseBtn = Chip(L.T("finance.expense"), () => { });
        var cats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void PaintFlow()
        {
            incomeBtn.Background = flow == MoneyFlow.Income ? Income : AgendaVisuals.Resource("Surface");
            incomeBtn.Foreground = flow == MoneyFlow.Income ? On(IncomeColor) : AgendaVisuals.Resource("Ink");
            expenseBtn.Background = flow == MoneyFlow.Expense ? Expense : AgendaVisuals.Resource("Surface");
            expenseBtn.Foreground = flow == MoneyFlow.Expense ? On(ExpenseColor) : AgendaVisuals.Resource("Ink");
            var allowed = flow == MoneyFlow.Income ? FinanceBook.IncomeCategories : FinanceBook.ExpenseCategories;
            if (!allowed.Contains(category)) category = allowed[0];
            cats.Children.Clear();
            foreach (var key in allowed)
            {
                string picked = key;
                bool on = category == picked;
                var chip = Chip(L.T("finance.cat." + picked), () => { category = picked; PaintFlow(); });
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface");
                chip.Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink");
                cats.Children.Add(chip);
            }
        }
        incomeBtn.Click += (_, _) => { flow = MoneyFlow.Income; PaintFlow(); };
        expenseBtn.Click += (_, _) => { flow = MoneyFlow.Expense; PaintFlow(); };
        incomeBtn.Margin = new Thickness(0, 0, 6, 0);
        flows.Children.Add(incomeBtn);
        flows.Children.Add(expenseBtn);
        root.Children.Add(flows);
        root.Children.Add(cats);

        root.Children.Add(Caption(L.T("finance.editDate")));
        var date = Field(entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        root.Children.Add(date);
        var error = ErrorLine();
        root.Children.Add(error);
        root.Children.Add(SaveRow(window, error, () =>
        {
            if (title.Text.Trim().Length == 0 || !TryMoney(amount.Text, out var value) || !TryDate(date.Text, out var when))
                return false;
            store.UpdateEntry(entry, flow, value, title.Text, when, category, currency);
            return true;
        }));

        PaintMoney();
        PaintFlow();
        window.Loaded += (_, _) => { Dialogs.Modalize(window); title.Focus(); title.SelectAll(); };
        window.ShowDialog();
    }

    private void EditRecurring(RecurringMoney item)
    {
        var category = item.Category;
        var currency = string.IsNullOrWhiteSpace(item.Currency) ? store.Book.Currency : item.Currency;
        var window = Dialogs.Window(owner, L.T("finance.edit"), 420, 460);
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        window.Content = root;
        var title = Field(item.Title);
        root.Children.Add(title);
        var amount = Field(item.Amount.ToString("0.##", CultureInfo.InvariantCulture));
        root.Children.Add(amount);

        var usd = Chip(MoneyFx.Usd, () => { });
        var mxn = Chip(MoneyFx.Mxn, () => { });
        void PaintMoney()
        {
            Stamp(usd, currency == MoneyFx.Usd);
            Stamp(mxn, currency == MoneyFx.Mxn);
        }
        usd.Click += (_, _) => { currency = MoneyFx.Usd; PaintMoney(); };
        mxn.Click += (_, _) => { currency = MoneyFx.Mxn; PaintMoney(); };
        var money = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        money.Children.Add(usd);
        money.Children.Add(mxn);
        root.Children.Add(money);

        var allowed = item.Flow == MoneyFlow.Income ? FinanceBook.IncomeCategories : FinanceBook.ExpenseCategories;
        if (!allowed.Contains(category)) category = allowed[0];
        var cats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void PaintCats()
        {
            cats.Children.Clear();
            foreach (var key in allowed)
            {
                string picked = key;
                bool on = category == picked;
                var chip = Chip(L.T("finance.cat." + picked), () => { category = picked; PaintCats(); });
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface");
                chip.Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink");
                cats.Children.Add(chip);
            }
        }
        root.Children.Add(cats);
        root.Children.Add(Caption(L.T("finance.editDay")));
        var day = Field(item.DayOfMonth.ToString(CultureInfo.InvariantCulture));
        root.Children.Add(day);
        var error = ErrorLine();
        root.Children.Add(error);
        root.Children.Add(SaveRow(window, error, () =>
        {
            if (title.Text.Trim().Length == 0 || !TryMoney(amount.Text, out var value) || !int.TryParse(day.Text.Trim(), out var dayNumber))
                return false;
            store.UpdateRecurring(item, value, title.Text, category, currency, dayNumber);
            return true;
        }));
        PaintMoney();
        PaintCats();
        window.Loaded += (_, _) => { Dialogs.Modalize(window); title.Focus(); title.SelectAll(); };
        window.ShowDialog();
    }

    private static void Stamp(Button button, bool on)
    {
        button.Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface");
        button.Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink");
    }

    private static TextBox Field(string value) => new()
    {
        Text = value,
        FontFamily = Ui,
        FontSize = 14,
        Margin = new Thickness(0),
        MinHeight = 34
    };

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.NoWrap
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return block;
    }

    private TextBlock ErrorLine()
    {
        var block = new TextBlock
        {
            Text = L.T("finance.editInvalid"),
            FontFamily = Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Expense,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        return block;
    }

    private static StackPanel SaveRow(Window window, TextBlock error, Func<bool> commit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var save = Dialogs.Button(L.T("common.save"), () =>
        {
            if (!commit()) { error.Visibility = Visibility.Visible; return; }
            window.DialogResult = true;
        });
        save.IsDefault = true;
        var cancel = Dialogs.Button(L.T("common.cancel"), () => window.DialogResult = false);
        cancel.IsCancel = true;
        row.Children.Add(save);
        row.Children.Add(cancel);
        return row;
    }

    private static Button Chip(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontFamily = Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 6, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static bool TryMoney(string text, out decimal amount)
    {
        string raw = text.Trim().Replace(" ", "").Replace("$", "");
        if (decimal.TryParse(raw, NumberStyles.Number, Strings.Culture, out amount) && amount > 0) return true;
        return decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && amount > 0;
    }

    private static bool TryDate(string text, out DateOnly date)
    {
        string raw = text.Trim();
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        return DateOnly.TryParse(raw, Strings.Culture, DateTimeStyles.None, out date);
    }

    // ---------------------------------------------------------------- input

    private void UpdateHint()
    {
        placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var parsed = FinanceQuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today), draftCode);
        hint.Text = parsed switch
        {
            MoneyEntry entry => L.T(entry.Flow == MoneyFlow.Income ? "finance.hintIncome" : "finance.hintExpense", entry.Title, FinanceBook.Format(entry.Amount, CurrencyOf(entry.Currency), Strings.Culture)),
            RecurringMoney item => L.T(item.Subscription ? "finance.hintSub" : "finance.hintFixed", item.Title, FinanceBook.Format(item.Amount, CurrencyOf(item.Currency), Strings.Culture)),
            _ => input.Text.Trim().Length == 0 ? "" : L.T("finance.hint")
        };
        hint.Visibility = shape.Hint && hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string CurrencyOf(string code) => code.Length > 0 ? MoneyFx.Normalize(code) : draftCode;

    private void Commit()
    {
        var parsed = FinanceQuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today), draftCode);
        if (parsed is MoneyEntry entry)
        {
            var date = span == FinanceSpan.Month && cursor.Year == DateTime.Today.Year && cursor.Month == DateTime.Today.Month
                ? DateOnly.FromDateTime(DateTime.Today)
                : cursor;
            store.AddEntry(entry.Flow, entry.Amount, entry.Title, date, entry.Category, entry.Notes, CurrencyOf(entry.Currency));
        }
        else if (parsed is RecurringMoney item)
        {
            store.AddRecurring(item.Title, item.Amount, item.Subscription, item.Category, item.Flow, item.DayOfMonth, item.Repeat, CurrencyOf(item.Currency));
            view = item.Subscription ? "subs" : "fixed";
        }
        else return;
        input.Clear();
        UpdateHint();
        Dispatcher.BeginInvoke(() =>
        {
            scroller.ScrollToTop();
            input.Focus();
        }, DispatcherPriority.Input);
    }

    private void Shift(int steps)
    {
        cursor = store.Book.Shift(span, cursor, steps);
        Render();
    }

    private void EditRate()
    {
        var typed = Dialogs.Prompt(owner, L.T("finance.rateTitle"), L.T("finance.rateLabel"), store.Book.UsdToMxn.ToString("0.####", CultureInfo.InvariantCulture));
        if (typed is null) return;
        if (decimal.TryParse(typed.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) && rate > 0)
            store.SetRate(rate, true);
    }

    private async Task RefreshRateAsync(bool force)
    {
        if (fetchingRate) return;
        if (!force && store.Book.RateManual) return;
        if (!force && store.Book.RateUpdatedUtc is { } when && DateTime.UtcNow - when < TimeSpan.FromHours(12)) return;
        fetchingRate = true;
        try
        {
            var rate = await FinanceRates.FetchUsdToMxnAsync();
            if (rate is { } value) store.SetRate(value, false);
        }
        finally { fetchingRate = false; }
    }

    internal bool ApplyView(string spanText, string tab, string to)
    {
        bool any = false;
        string spanKey = spanText.Trim().ToLowerInvariant() switch
        {
            "week" or "semana" or "sem" => nameof(FinanceSpan.Week),
            "month" or "mes" => nameof(FinanceSpan.Month),
            "quarter" or "trimestre" or "q" => nameof(FinanceSpan.Quarter),
            "year" or "año" or "ano" or "anual" => nameof(FinanceSpan.Year),
            _ => ""
        };
        if (spanKey.Length > 0 && Enum.TryParse<FinanceSpan>(spanKey, true, out var nextSpan))
        {
            span = nextSpan;
            any = true;
        }
        else if (spanText.Trim().Length > 0)
            return false;

        string tabKey = tab.Trim().ToLowerInvariant() switch
        {
            "moves" or "movimientos" or "gastos" or "entries" => "moves",
            "fixed" or "fijos" => "fixed",
            "subs" or "suscripciones" or "subscriptions" => "subs",
            _ => ""
        };
        if (tabKey.Length > 0)
        {
            view = tabKey;
            any = true;
        }
        else if (tab.Trim().Length > 0)
            return false;

        string dest = to.Trim().ToLowerInvariant();
        if (dest is "today" or "hoy")
        {
            cursor = DateOnly.FromDateTime(DateTime.Today);
            any = true;
        }
        else if (dest is "prev" or "previous" or "anterior" or "back" or "-1")
        {
            cursor = store.Book.Shift(span, cursor, -1);
            any = true;
        }
        else if (dest is "next" or "siguiente" or "forward" or "+1")
        {
            cursor = store.Book.Shift(span, cursor, 1);
            any = true;
        }
        else if (dest.Length > 0)
        {
            string stamp = to.Trim();
            if (stamp.Length == 7) stamp += "-01";
            if (!DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out cursor))
                return false;
            any = true;
        }

        if (!any) return false;
        Render();
        return true;
    }

    private void Skin()
    {
        var ink = AgendaVisuals.Resource("Ink");
        var surface = AgendaVisuals.Resource("Surface");
        summaryPlate.Background = surface;
        summaryPlate.BorderBrush = ink;
        summaryPlate.BorderThickness = new Thickness(2.5);
        summaryPlate.Effect = HardShadow();
        composerShell.Background = surface;
        composerShell.BorderBrush = ink;
        composerShell.BorderThickness = new Thickness(2.5);
        composerShell.Effect = HardShadow();
        ledgerFrame.BorderBrush = ink;
        addButton.Background = ink;
        addButton.Foreground = AgendaVisuals.Resource("Paper");
        addButton.BorderBrush = ink;
        draftCurrency.Background = surface;
        draftCurrency.Foreground = ink;
        draftCurrency.BorderBrush = ink;
    }

    private static DropShadowEffect HardShadow() => new()
    {
        BlurRadius = 0,
        ShadowDepth = 4,
        Direction = 315,
        Opacity = 1,
        Color = Theme.Of("Ink")
    };

    private Border PillTrack(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(0),
        Background = AgendaVisuals.Resource("Surface"),
        BorderBrush = AgendaVisuals.Resource("Ink"),
        BorderThickness = new Thickness(2),
        Padding = new Thickness(2),
        Child = child,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static string Soft(string text) => text;

    private static void Soften(Button button, double radius)
    {
        button.ClearValue(Control.TemplateProperty);
        button.BorderThickness = new Thickness(2);
        button.BorderBrush = AgendaVisuals.Resource("Ink");
        button.FontFamily = Ui;
        button.FocusVisualStyle = null;
        button.Cursor = Cursors.Hand;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string Compact(decimal amount, string currency)
    {
        var culture = currency == MoneyFx.Mxn ? CultureInfo.GetCultureInfo("es-MX") : CultureInfo.GetCultureInfo("en-US");
        return "$" + Math.Abs(amount).ToString("N2", culture);
    }

    private static string Signed(decimal amount, string currency) =>
        (amount < 0 ? "−" : amount > 0 ? "+" : "") + Compact(amount, currency);
}
