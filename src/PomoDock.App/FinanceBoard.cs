using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Shared ledger on the canvas: mixed USD/MXN, a display currency, a rate, and a period chart.
/// Closing the card never deletes a movement; every finance widget is a lens on the same book.
/// </summary>
internal sealed class FinanceBoard : Grid, IReskinnable
{
    private readonly MainWindow owner;
    private readonly FinanceStore store;
    private readonly TextBox input = new();
    private readonly TextBlock monthLabel = new();
    private readonly TextBlock netLabel = new();
    private readonly TextBlock incomeLabel = new();
    private readonly TextBlock expenseLabel = new();
    private readonly TextBlock streakLabel = new();
    private readonly TextBlock rateLabel = new();
    private readonly TextBlock projectedLabel = new();
    private readonly Border incomeFill = new();
    private readonly Grid track = new();
    private readonly Grid seriesHost = new();
    private readonly StackPanel slices = new();
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel list = new();
    private readonly TextBlock hint = new();
    private readonly StackPanel currencyToggles = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel spanToggles = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel draftToggles = new() { Orientation = Orientation.Horizontal };
    private DateOnly cursor = DateOnly.FromDateTime(DateTime.Today);
    private FinanceSpan span = FinanceSpan.Month;
    private string view = "moves";
    private string draftCurrency = MoneyFx.Mxn;
    private bool listening;
    private bool fetchingRate;

    public FinanceBoard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner;
        store = FinanceStore.For(owner.Store);
        draftCurrency = store.Book.Currency;

        Margin = new Thickness(6, 8, 6, 4);
        for (int i = 0; i < 9; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        Children.Add(BuildMonthBar());
        Grid.SetRow(spanToggles, 1); Children.Add(spanToggles);
        var hero = BuildHero();
        Grid.SetRow(hero, 2); Children.Add(hero);
        seriesHost.Height = 92;
        seriesHost.Margin = new Thickness(0, 2, 0, 6);
        Grid.SetRow(seriesHost, 3); Children.Add(seriesHost);
        SizeChanged += (_, _) => seriesHost.Height = Math.Clamp(ActualHeight * 0.16, 72, 200);
        slices.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(slices, 4); Children.Add(slices);
        Grid.SetRow(tabs, 5); Children.Add(tabs);
        var scroller = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 6); Children.Add(scroller);
        var composer = BuildComposer();
        Grid.SetRow(composer, 7); Children.Add(composer);

        Loaded += (_, _) =>
        {
            if (!listening) { store.Changed += Render; listening = true; }
            Render();
            _ = RefreshRateAsync(false);
        };
        Unloaded += (_, _) =>
        {
            if (listening) { store.Changed -= Render; listening = false; }
        };
        Render();
    }

    public void Reskin() => Render();

    private void Render()
    {
        var book = store.Book;
        var snapshot = book.Month(cursor);
        var (start, end) = book.SpanRange(span, cursor);
        var inRange = book.Entries.Where(entry => entry.Date >= start && entry.Date <= end)
            .OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.CreatedUtc).ToList();
        decimal income = span == FinanceSpan.Month ? snapshot.Recorded.Income : inRange.Where(e => e.Flow == MoneyFlow.Income).Sum(book.ToDisplay);
        decimal expense = span == FinanceSpan.Month ? snapshot.Recorded.Expense : inRange.Where(e => e.Flow == MoneyFlow.Expense).Sum(book.ToDisplay);
        decimal net = income - expense;
        string money(decimal value) => FinanceBook.Format(value, book.Currency, Strings.Culture);

        monthLabel.Text = PeriodTitle(start, end);
        netLabel.Text = Signed(net, money);
        netLabel.Foreground = net < 0 ? AgendaVisuals.Resource("Muted") : AgendaVisuals.Resource("Ink");
        incomeLabel.Text = "+" + money(income);
        expenseLabel.Text = "−" + money(expense);
        int streak = book.SurplusStreak(cursor);
        streakLabel.Text = streak > 0 ? L.T("finance.streak", streak.ToString(CultureInfo.InvariantCulture)) : L.T("finance.streakNone");
        rateLabel.Text = L.T("finance.rate", book.UsdToMxn.ToString("0.####", CultureInfo.InvariantCulture));
        decimal total = income + expense;
        incomeFill.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (total > 0 && track.ActualWidth > 0)
            incomeFill.Width = Math.Max(4, track.ActualWidth * (double)(income / total));
        bool drift = span == FinanceSpan.Month && snapshot.ProjectedNet != snapshot.Recorded.Net;
        projectedLabel.Text = drift ? L.T("finance.projected", Signed(snapshot.ProjectedNet, money)) : "";
        projectedLabel.Visibility = drift ? Visibility.Visible : Visibility.Collapsed;

        PaintToggles(currencyToggles, [MoneyFx.Usd, MoneyFx.Mxn], book.Currency, code => { store.SetCurrency(code); draftCurrency = code; });
        PaintToggles(spanToggles, [nameof(FinanceSpan.Week), nameof(FinanceSpan.Month), nameof(FinanceSpan.Quarter), nameof(FinanceSpan.Year)],
            span.ToString(), key => { span = Enum.Parse<FinanceSpan>(key, true); Render(); },
            key => L.T("finance.span." + key.ToLowerInvariant()));
        PaintToggles(draftToggles, [MoneyFx.Usd, MoneyFx.Mxn], draftCurrency, code => { draftCurrency = code; UpdateHint(); Render(); });
        PaintSeries(book.Series(span, cursor));
        PaintSlices(book.Categories(start, end), money);
        PaintTabs();
        list.Children.Clear();
        if (view == "fixed") PaintRecurring(snapshot.Fixed, false, money);
        else if (view == "subs") PaintRecurring(snapshot.Subscriptions, true, money);
        else PaintMoves(span == FinanceSpan.Month ? snapshot.Entries : inRange, money);
        UpdateHint();
    }

    private string PeriodTitle(DateOnly start, DateOnly end) => span switch
    {
        FinanceSpan.Week => $"{start:dd}–{end:dd MMM}".ToUpper(Strings.Culture),
        FinanceSpan.Quarter => $"Q{(start.Month - 1) / 3 + 1} {start.Year}",
        FinanceSpan.Year => start.Year.ToString(CultureInfo.InvariantCulture),
        _ => cursor.ToString("MMM yyyy", Strings.Culture).ToUpper(Strings.Culture)
    };

    private UIElement BuildMonthBar()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = Nav("‹", () => { cursor = store.Book.Shift(span, cursor, -1); Render(); });
        var next = Nav("›", () => { cursor = store.Book.Shift(span, cursor, 1); Render(); });
        monthLabel.FontFamily = new FontFamily("Consolas");
        monthLabel.FontSize = 13;
        monthLabel.FontWeight = FontWeights.Black;
        monthLabel.HorizontalAlignment = HorizontalAlignment.Center;
        monthLabel.VerticalAlignment = VerticalAlignment.Center;
        currencyToggles.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(monthLabel, 1);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(currencyToggles);
        right.Children.Add(next);
        Grid.SetColumn(right, 2);
        row.Children.Add(back); row.Children.Add(monthLabel); row.Children.Add(right);
        return row;
    }

    private static Button Nav(string text, Action action)
    {
        var button = new Button { Content = text, Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 16 };
        button.Click += (_, _) => action();
        return button;
    }

    private UIElement BuildHero()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var net = new StackPanel();
        net.Children.Add(new TextBlock
        {
            Text = L.T("finance.net"), FontFamily = new FontFamily("Consolas"), FontSize = 8, FontWeight = FontWeights.Bold,
            Foreground = AgendaVisuals.Resource("Muted")
        });
        netLabel.FontFamily = new FontFamily("Consolas");
        netLabel.FontSize = 26;
        netLabel.FontWeight = FontWeights.Black;
        net.Children.Add(netLabel);
        top.Children.Add(net);
        streakLabel.FontFamily = new FontFamily("Consolas");
        streakLabel.FontSize = 8;
        streakLabel.FontWeight = FontWeights.Bold;
        streakLabel.VerticalAlignment = VerticalAlignment.Bottom;
        streakLabel.Margin = new Thickness(8, 0, 0, 4);
        Grid.SetColumn(streakLabel, 1);
        top.Children.Add(streakLabel);
        stack.Children.Add(top);

        var rateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        rateLabel.FontFamily = new FontFamily("Consolas");
        rateLabel.FontSize = 9;
        rateLabel.Foreground = AgendaVisuals.Resource("Muted");
        rateLabel.VerticalAlignment = VerticalAlignment.Center;
        rateLabel.Cursor = Cursors.Hand;
        rateLabel.MouseLeftButtonUp += (_, _) => EditRate();
        var refresh = new Button { Content = "↻", Width = 26, Height = 22, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), FontSize = 11, ToolTip = L.T("finance.refresh") };
        refresh.Click += async (_, _) => await RefreshRateAsync(true);
        rateRow.Children.Add(rateLabel);
        rateRow.Children.Add(refresh);
        stack.Children.Add(rateRow);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.Children.Add(Figure(L.T("finance.income"), incomeLabel));
        var spent = Figure(L.T("finance.expense"), expenseLabel);
        Grid.SetColumn(spent, 1); split.Children.Add(spent);
        stack.Children.Add(split);

        incomeFill.Background = AgendaVisuals.Resource("Ink");
        incomeFill.HorizontalAlignment = HorizontalAlignment.Left;
        incomeFill.MinWidth = 4;
        track.Height = 8;
        track.Margin = new Thickness(0, 10, 0, 0);
        track.Background = AgendaVisuals.Fade("Line", 55);
        track.Children.Add(incomeFill);
        track.SizeChanged += (_, _) =>
        {
            var book = store.Book;
            var (start, end) = book.SpanRange(span, cursor);
            var inRange = book.Entries.Where(entry => entry.Date >= start && entry.Date <= end).ToList();
            decimal income = inRange.Where(e => e.Flow == MoneyFlow.Income).Sum(book.ToDisplay);
            decimal expense = inRange.Where(e => e.Flow == MoneyFlow.Expense).Sum(book.ToDisplay);
            decimal total = income + expense;
            if (total <= 0 || track.ActualWidth <= 0) return;
            incomeFill.Width = Math.Max(4, track.ActualWidth * (double)(income / total));
        };
        stack.Children.Add(track);
        projectedLabel.FontSize = 9;
        projectedLabel.Foreground = AgendaVisuals.Resource("Muted");
        projectedLabel.FontFamily = new FontFamily("Consolas");
        projectedLabel.Margin = new Thickness(0, 6, 0, 0);
        projectedLabel.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(projectedLabel);
        return stack;
    }

    private static StackPanel Figure(string caption, TextBlock value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = caption, FontFamily = new FontFamily("Consolas"), FontSize = 8, FontWeight = FontWeights.Bold,
            Foreground = AgendaVisuals.Resource("Muted")
        });
        value.FontFamily = new FontFamily("Consolas");
        value.FontSize = 12;
        value.FontWeight = FontWeights.Bold;
        value.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(value);
        return stack;
    }

    private void PaintToggles(StackPanel host, IReadOnlyList<string> keys, string current, Action<string> pick, Func<string, string>? label = null)
    {
        host.Children.Clear();
        host.Margin = new Thickness(0, 0, 0, 6);
        foreach (var key in keys)
        {
            bool on = string.Equals(key, current, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = label?.Invoke(key) ?? key,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
            };
            string picked = key;
            button.Click += (_, _) => pick(picked);
            host.Children.Add(button);
        }
    }

    private void PaintSeries(IReadOnlyList<FinanceBar> bars)
    {
        seriesHost.Children.Clear();
        seriesHost.ColumnDefinitions.Clear();
        if (bars.Count == 0) return;
        decimal peak = bars.Max(bar => Math.Max(bar.Income, bar.Expense));
        if (peak <= 0) peak = 1;
        for (int i = 0; i < bars.Count; i++)
        {
            seriesHost.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = bars[i];
            var cell = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 0) };
            var caption = new TextBlock
            {
                Text = bar.Label, FontFamily = new FontFamily("Consolas"), FontSize = 7, FontWeight = FontWeights.Bold,
                Foreground = AgendaVisuals.Resource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
            };
            DockPanel.SetDock(caption, Dock.Bottom);
            cell.Children.Add(caption);
            var pair = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.Children.Add(Column(bar.Income, peak, AgendaVisuals.Resource("Ink")));
            var spent = Column(bar.Expense, peak, AgendaVisuals.Fade("Ink", 90));
            Grid.SetColumn(spent, 1);
            pair.Children.Add(spent);
            cell.Children.Add(pair);
            Grid.SetColumn(cell, i);
            seriesHost.Children.Add(cell);
        }
    }

    private static Border Column(decimal value, decimal peak, Brush fill) => new()
    {
        Background = fill,
        Height = Math.Max(2, 64 * (double)(value / peak)),
        Margin = new Thickness(1, 0, 1, 0),
        VerticalAlignment = VerticalAlignment.Bottom,
        MinWidth = 4
    };

    private void PaintSlices(IReadOnlyList<FinanceSlice> items, Func<decimal, string> money)
    {
        slices.Children.Clear();
        foreach (var slice in items.Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = L.T("finance.cat." + slice.Category), FontFamily = new FontFamily("Consolas"),
                FontSize = 8, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center
            });
            var fill = new Border { Background = AgendaVisuals.Resource("Ink"), HorizontalAlignment = HorizontalAlignment.Left, Height = 8, MinWidth = 4 };
            var bar = new Grid { Height = 8, Background = AgendaVisuals.Fade("Line", 45), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, _) =>
            {
                if (bar.ActualWidth > 0) fill.Width = Math.Max(4, bar.ActualWidth * slice.Share);
            };
            Grid.SetColumn(bar, 1); row.Children.Add(bar);
            var amount = new TextBlock
            {
                Text = money(slice.Amount), FontFamily = new FontFamily("Consolas"), FontSize = 8,
                Foreground = AgendaVisuals.Resource("Muted"), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(amount, 2); row.Children.Add(amount);
            slices.Children.Add(row);
        }
    }

    private void PaintTabs()
    {
        tabs.Children.Clear();
        foreach (var (key, label) in new[] { ("moves", L.T("finance.tabMoves")), ("fixed", L.T("finance.tabFixed")), ("subs", L.T("finance.tabSubs")) })
        {
            bool on = view == key;
            var button = new Button
            {
                Content = label, FontSize = 9, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 5, 6),
                Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
            };
            string picked = key;
            button.Click += (_, _) => { view = picked; Render(); };
            tabs.Children.Add(button);
        }
    }

    private void PaintMoves(IReadOnlyList<MoneyEntry> entries, Func<decimal, string> money)
    {
        if (entries.Count == 0)
        {
            list.Children.Add(Empty(L.T("finance.emptyMoves")));
            return;
        }
        foreach (var entry in entries) list.Children.Add(MoveRow(entry, money));
    }

    private void PaintRecurring(IReadOnlyList<RecurringDue> items, bool subscriptions, Func<decimal, string> money)
    {
        if (items.Count == 0)
        {
            list.Children.Add(Empty(subscriptions ? L.T("finance.emptySubs") : L.T("finance.emptyFixed")));
            return;
        }
        foreach (var due in items) list.Children.Add(RecurringRow(due, money));
    }

    private UIElement MoveRow(MoneyEntry entry, Func<decimal, string> money)
    {
        var book = store.Book;
        var row = new Border { BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 7, 0, 7) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = entry.Date.ToString("dd MMM", Strings.Culture).ToUpper(Strings.Culture),
            FontFamily = new FontFamily("Consolas"), FontSize = 9, Foreground = AgendaVisuals.Resource("Muted"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new StackPanel { Margin = new Thickness(4, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = entry.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string native = (entry.Flow == MoneyFlow.Income ? "+" : "−") + FinanceBook.Format(entry.Amount, entry.Currency, Strings.Culture);
        string meta = $"{L.T("finance.cat." + entry.Category)} · {entry.Currency}";
        if (!string.Equals(entry.Currency, book.Currency, StringComparison.OrdinalIgnoreCase))
            meta += " · " + (entry.Flow == MoneyFlow.Income ? "+" : "−") + money(book.ToDisplay(entry));
        text.Children.Add(new TextBlock { Text = meta, FontSize = 8, Foreground = AgendaVisuals.Resource("Muted"), FontFamily = new FontFamily("Consolas") });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var amount = new TextBlock
        {
            Text = native,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(amount, 2); grid.Children.Add(amount);
        var remove = new Button { Content = "×", Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(0), FontSize = 13 };
        remove.Click += (_, _) => store.RemoveEntry(entry);
        Grid.SetColumn(remove, 3); grid.Children.Add(remove);
        row.Child = grid;
        return row;
    }

    private UIElement RecurringRow(RecurringDue due, Func<decimal, string> money)
    {
        var item = due.Item;
        var row = new Border { BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 7, 0, 7) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string cadence = item.Repeat == MoneyRepeat.Yearly ? L.T("finance.yearly") : L.T("finance.dayOfMonth", item.DayOfMonth);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = $"{L.T("finance.cat." + item.Category)} · {cadence} · {item.Currency}",
            FontSize = 8, Foreground = AgendaVisuals.Resource("Muted"), FontFamily = new FontFamily("Consolas")
        });
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(new TextBlock
        {
            Text = (item.Flow == MoneyFlow.Income ? "+" : "−") + FinanceBook.Format(item.Amount, item.Currency, Strings.Culture),
            FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        if (!due.Recorded)
        {
            var pay = new Button { Content = L.T("finance.paid"), FontSize = 8, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0) };
            pay.Click += (_, _) => store.MarkPaid(item, new DateOnly(cursor.Year, cursor.Month, 1));
            actions.Children.Add(pay);
        }
        else
            actions.Children.Add(new TextBlock
            {
                Text = L.T("finance.alreadyPaid"), FontSize = 8, Foreground = AgendaVisuals.Resource("Muted"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
        var archive = new Button { Content = "×", Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(0), FontSize = 13 };
        archive.Click += (_, _) => { if (Dialogs.Choose(owner, L.T("finance.archiveTitle"), [L.T("finance.archive"), L.T("common.cancel")]) == 0) store.Archive(item); };
        actions.Children.Add(archive);
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        row.Child = grid;
        return row;
    }

    private UIElement BuildComposer()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        draftToggles.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(draftToggles);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.Height = 32; input.Margin = new Thickness(0); input.Padding = new Thickness(9, 0, 9, 0); input.FontSize = 12;
        input.TextWrapping = TextWrapping.NoWrap; input.VerticalContentAlignment = VerticalAlignment.Center;
        input.ToolTip = L.T("finance.hint");
        input.SetValue(AutomationProperties.NameProperty, L.T("finance.placeholder"));
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Hidden);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { input.Clear(); e.Handled = true; }
        };
        input.TextChanged += (_, _) => UpdateHint();
        var field = AgendaVisuals.WithHint(input, L.T("finance.placeholder"));
        field.Margin = new Thickness(0, 0, 6, 0);
        var add = new Button { Content = L.T("common.add"), FontSize = 10, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0), Height = 32 };
        add.Click += (_, _) => Commit();
        Grid.SetColumn(add, 1);
        row.Children.Add(field); row.Children.Add(add);
        stack.Children.Add(row);
        hint.FontSize = 9; hint.Foreground = AgendaVisuals.Resource("Muted"); hint.Margin = new Thickness(2, 4, 0, 0); hint.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(hint);
        return stack;
    }

    private void UpdateHint()
    {
        var parsed = FinanceQuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today), draftCurrency);
        hint.Text = parsed switch
        {
            MoneyEntry entry => L.T(entry.Flow == MoneyFlow.Income ? "finance.hintIncome" : "finance.hintExpense", entry.Title, FinanceBook.Format(entry.Amount, CurrencyOf(entry.Currency), Strings.Culture)),
            RecurringMoney item => L.T(item.Subscription ? "finance.hintSub" : "finance.hintFixed", item.Title, FinanceBook.Format(item.Amount, CurrencyOf(item.Currency), Strings.Culture)),
            _ => L.T("finance.hint")
        };
    }

    private string CurrencyOf(string code) => code.Length > 0 ? MoneyFx.Normalize(code) : draftCurrency;

    private void Commit()
    {
        var parsed = FinanceQuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today), draftCurrency);
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

    private static string Signed(decimal amount, Func<decimal, string> money) =>
        (amount < 0 ? "−" : amount > 0 ? "+" : "") + money(Math.Abs(amount));

    private static TextBlock Empty(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap
    };
}
