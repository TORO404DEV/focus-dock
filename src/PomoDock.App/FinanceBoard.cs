using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// A shared ledger on the canvas: this month's cash, fixed costs and subscriptions.
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
    private readonly TextBlock burnLabel = new();
    private readonly TextBlock projectedLabel = new();
    private readonly Border fill = new();
    private readonly Grid track = new();
    private readonly WrapPanel chips = new();
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel list = new();
    private readonly TextBlock hint = new();
    private DateOnly month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string view = "moves";
    private bool listening;

    public FinanceBoard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner;
        store = FinanceStore.For(owner.Store);

        Margin = new Thickness(6, 8, 6, 4);
        for (int i = 0; i < 5; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        Children.Add(BuildMonthBar());
        var hero = BuildHero();
        Grid.SetRow(hero, 1); Children.Add(hero);
        chips.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(chips, 2); Children.Add(chips);
        Grid.SetRow(tabs, 3); Children.Add(tabs);
        var scroller = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 4); Children.Add(scroller);
        var composer = BuildComposer();
        Grid.SetRow(composer, 5); Children.Add(composer);

        Loaded += (_, _) =>
        {
            if (!listening) { store.Changed += Render; listening = true; }
            Render();
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
        var snapshot = book.Month(month);
        string money(decimal value) => FinanceBook.Format(value, book.Currency, Strings.Culture);

        monthLabel.Text = month.ToString("MMM yyyy", Strings.Culture).ToUpper(Strings.Culture);
        netLabel.Text = Signed(snapshot.Recorded.Net, money);
        incomeLabel.Text = "+" + money(snapshot.Recorded.Income);
        expenseLabel.Text = "−" + money(snapshot.Recorded.Expense);
        burnLabel.Text = "−" + money(snapshot.RecurringExpense);
        decimal total = snapshot.Recorded.Income + snapshot.Recorded.Expense;
        fill.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (total > 0 && track.ActualWidth > 0)
            fill.Width = Math.Max(4, track.ActualWidth * (double)(snapshot.Recorded.Income / total));
        bool drift = snapshot.ProjectedNet != snapshot.Recorded.Net;
        projectedLabel.Text = drift ? L.T("finance.projected", Signed(snapshot.ProjectedNet, money)) : "";
        projectedLabel.Visibility = drift ? Visibility.Visible : Visibility.Collapsed;

        PaintChips(snapshot, money);
        PaintTabs();
        list.Children.Clear();
        if (view == "fixed") PaintRecurring(snapshot.Fixed, false, money);
        else if (view == "subs") PaintRecurring(snapshot.Subscriptions, true, money);
        else PaintMoves(snapshot, money);
        UpdateHint();
    }

    private UIElement BuildMonthBar()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = Nav("‹", () => { month = month.AddMonths(-1); Render(); });
        var next = Nav("›", () => { month = month.AddMonths(1); Render(); });
        monthLabel.FontFamily = new FontFamily("Consolas");
        monthLabel.FontSize = 13;
        monthLabel.FontWeight = FontWeights.Black;
        monthLabel.HorizontalAlignment = HorizontalAlignment.Center;
        monthLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(monthLabel, 1); Grid.SetColumn(next, 2);
        row.Children.Add(back); row.Children.Add(monthLabel); row.Children.Add(next);
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
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = L.T("finance.net"), FontFamily = new FontFamily("Consolas"), FontSize = 8, FontWeight = FontWeights.Bold,
            Foreground = AgendaVisuals.Resource("Muted")
        });
        netLabel.FontFamily = new FontFamily("Consolas");
        netLabel.FontSize = 26;
        netLabel.FontWeight = FontWeights.Black;
        netLabel.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(netLabel);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.Children.Add(Figure(L.T("finance.income"), incomeLabel));
        var spent = Figure(L.T("finance.expense"), expenseLabel);
        Grid.SetColumn(spent, 1); split.Children.Add(spent);
        var burn = Figure(L.T("finance.obligations"), burnLabel);
        Grid.SetColumn(burn, 2); split.Children.Add(burn);
        stack.Children.Add(split);

        fill.Background = AgendaVisuals.Resource("Ink");
        fill.HorizontalAlignment = HorizontalAlignment.Left;
        fill.MinWidth = 4;
        track.Height = 6;
        track.Margin = new Thickness(0, 10, 0, 0);
        track.Background = AgendaVisuals.Fade("Line", 45);
        track.Children.Add(fill);
        track.SizeChanged += (_, _) =>
        {
            var snapshot = store.Book.Month(month);
            decimal total = snapshot.Recorded.Income + snapshot.Recorded.Expense;
            if (total <= 0 || track.ActualWidth <= 0) return;
            fill.Width = Math.Max(4, track.ActualWidth * (double)(snapshot.Recorded.Income / total));
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

    private void PaintChips(FinanceMonth snapshot, Func<decimal, string> money)
    {
        chips.Children.Clear();
        chips.Children.Add(Chip(L.T("finance.subsChip", money(snapshot.Subscriptions.Sum(item => item.Item.Amount))), "subs"));
        chips.Children.Add(Chip(L.T("finance.fixedChip", money(snapshot.Fixed.Sum(item => item.Item.Amount))), "fixed"));
    }

    private Button Chip(string text, string target)
    {
        bool on = view == target;
        var button = new Button
        {
            Content = text, FontFamily = new FontFamily("Consolas"), FontSize = 9, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 4),
            Background = on ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
            Foreground = on ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
        };
        button.Click += (_, _) => { view = target; Render(); };
        return button;
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

    private void PaintMoves(FinanceMonth snapshot, Func<decimal, string> money)
    {
        if (snapshot.Entries.Count == 0)
        {
            list.Children.Add(Empty(L.T("finance.emptyMoves")));
            return;
        }
        foreach (var entry in snapshot.Entries) list.Children.Add(MoveRow(entry, money));
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
        text.Children.Add(new TextBlock { Text = L.T("finance.cat." + entry.Category), FontSize = 8, Foreground = AgendaVisuals.Resource("Muted"), FontFamily = new FontFamily("Consolas") });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var amount = new TextBlock
        {
            Text = (entry.Flow == MoneyFlow.Income ? "+" : "−") + money(entry.Amount),
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
            Text = $"{L.T("finance.cat." + item.Category)} · {cadence}",
            FontSize = 8, Foreground = AgendaVisuals.Resource("Muted"), FontFamily = new FontFamily("Consolas")
        });
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(new TextBlock
        {
            Text = (item.Flow == MoneyFlow.Income ? "+" : "−") + money(item.Amount),
            FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        if (!due.Recorded)
        {
            var pay = new Button { Content = L.T("finance.paid"), FontSize = 8, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0) };
            pay.Click += (_, _) => store.MarkPaid(item, month);
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
        var parsed = FinanceQuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today));
        hint.Text = parsed switch
        {
            MoneyEntry entry => L.T(entry.Flow == MoneyFlow.Income ? "finance.hintIncome" : "finance.hintExpense", entry.Title, FinanceBook.Format(entry.Amount, store.Book.Currency, Strings.Culture)),
            RecurringMoney item => L.T(item.Subscription ? "finance.hintSub" : "finance.hintFixed", item.Title, FinanceBook.Format(item.Amount, store.Book.Currency, Strings.Culture)),
            _ => L.T("finance.hint")
        };
    }

    private void Commit()
    {
        var parsed = FinanceQuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today));
        if (parsed is MoneyEntry entry)
        {
            var date = month == new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1)
                ? DateOnly.FromDateTime(DateTime.Today)
                : month;
            store.AddEntry(entry.Flow, entry.Amount, entry.Title, date, entry.Category);
        }
        else if (parsed is RecurringMoney item)
        {
            store.AddRecurring(item.Title, item.Amount, item.Subscription, item.Category, item.Flow, item.DayOfMonth, item.Repeat);
            view = item.Subscription ? "subs" : "fixed";
        }
        else return;
        input.Clear();
    }

    private static string Signed(decimal amount, Func<decimal, string> money) =>
        (amount < 0 ? "−" : amount > 0 ? "+" : "") + money(Math.Abs(amount));

    private static TextBlock Empty(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap
    };
}
