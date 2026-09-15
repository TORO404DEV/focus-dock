using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

internal static class Dialogs
{
    public static Window Window(Window? owner, string title, double width = 500, double height = 420)
    {
        var window = new Window
        {
            Owner = owner, Title = "POMODOCK / " + title, Width = width, Height = height, MinWidth = 360, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = true, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.CanResizeWithGrip
        };
        // A transparent window keeps its default black foreground, which its content inherits.
        window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Ink");
        // Embedded applications and widget cards can have their own native
        // HWND surfaces. WPF ownership alone may leave a modal below one of
        // those surfaces, so lift every PomoDock modal when its HWND exists
        // and whenever it receives activation.
        KeepAboveChrome(window);
        return window;
    }
    private static void LiftModal(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        // Widget cards use independent WPF/Win32 surfaces. TOP is not enough
        // when one of those popups was created later, so modals temporarily
        // use the topmost band while they are open.
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>Keeps a PomoDock dialog above timer/widget popup HWNDs.</summary>
    public static void LiftAboveChrome(Window window) => LiftModal(window);

    public static void KeepAboveChrome(Window window)
    {
        window.Topmost = true;
        window.ShowInTaskbar = false;
        window.SourceInitialized += (_, _) => LiftModal(window);
        window.ContentRendered += (_, _) => LiftModal(window);
        window.Activated += (_, _) => LiftModal(window);
    }
    public static void Modalize(Window window)
    {
        if (window.Content is not FrameworkElement content || content is ModalSurface) return;
        window.Content = null;
        var frame = new ModalSurface(); frame.SetResourceReference(Border.BackgroundProperty, "Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) }); root.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Background = (Brush)Application.Current.Resources["Chrome"], LastChildFill = true, Cursor = Cursors.SizeAll };
        var close = new Button { Content = "×", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0), BorderThickness = new Thickness(0), Foreground = (Brush)Application.Current.Resources["ChromeInk"], Background = Brushes.Transparent, FontSize = 18 }; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var heading = new TextBlock { Text = window.Title, Foreground = (Brush)Application.Current.Resources["ChromeInk"], FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) }; header.Children.Add(heading);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Button) window.DragMove(); }; close.Click += (_, _) => window.Close();
        root.Children.Add(header); Grid.SetRow(content, 1); root.Children.Add(content); frame.Child = root; window.Content = frame;
    }
    public static void Alert(Window? owner, string title, string message)
    {
        var window = Window(owner ?? Application.Current.MainWindow!, title, 520, 290);
        var stack = new StackPanel { Margin = new Thickness(24) }; stack.Children.Add(Heading(title)); stack.Children.Add(new TextBlock { Text = message, FontSize = 13, Margin = new Thickness(0, 0, 0, 22) }); stack.Children.Add(Button(L.T("common.ok"), () => window.DialogResult = true)); window.Content = stack;
        window.Loaded += (_, _) => Modalize(window); window.ShowDialog();
    }
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 24, FontWeight = FontWeights.Black, Margin = new Thickness(0, 0, 0, 16) };
    public static Button Button(string text, Action action)
    { var button = new Button { Content = text }; button.Click += (_, _) => action(); return button; }
    public static string? Prompt(Window owner, string title, string label, string value = "")
    {
        var window = Window(owner, title, 470, 260);
        var stack = new StackPanel { Margin = new Thickness(22) }; window.Content = stack;
        stack.Children.Add(Heading(title)); stack.Children.Add(new TextBlock { Text = label });
        var input = new TextBox { Text = value }; stack.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Button(L.T("common.save"), () => window.DialogResult = true); save.IsDefault = true;
        buttons.Children.Add(save); var cancel = Button(L.T("common.cancel"), () => window.DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel); stack.Children.Add(buttons);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        window.Loaded += (_, _) => Modalize(window); return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static int Choose(Window owner, string title, string[] options)
    {
        var window = Window(owner, title, 480, 190 + options.Length * 46);
        var stack = new StackPanel { Margin = new Thickness(22) }; window.Content = stack; stack.Children.Add(Heading(title));
        int selected = -1;
        for (int i = 0; i < options.Length; i++)
        { int index = i; var b = Button(options[i], () => { selected = index; window.DialogResult = true; }); b.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(b); }
        window.Loaded += (_, _) => Modalize(window); window.ShowDialog(); return selected;
    }
    public static int ChooseWidget(MainWindow owner)
    {
        var window = Window(owner, L.T("widgets.addTitle"), 780, 820); window.MinWidth = 640; window.MinHeight = 680;
        var root = new Grid { Margin = new Thickness(28, 22, 28, 24) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); window.Content = root;
        var intro = new StackPanel(); intro.Children.Add(Heading(L.T("widgets.addTitle"))); intro.Children.Add(new TextBlock { Text = L.T("widgets.addHelp"), FontSize = 12, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, -8, 0, 20) }); Grid.SetRow(intro, 0); root.Children.Add(intro);
        var guide = new TextBlock { Text = L.T("widgets.addGuide"), FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 0, 0, 8) }; Grid.SetRow(guide, 1); root.Children.Add(guide);
        var launcher = new UniformGrid { Columns = 3, Rows = 4 };
        var entries = new[]
        {
            ("▣", L.T("widgets.windowTitle"), L.T("widgets.windowHelp"), "1"),
            ("◎", L.T("widgets.webTitle"), L.T("widgets.webHelp"), "2"),
            ("✎", L.T("widgets.notesTitle"), L.T("widgets.notesHelp"), "3"),
            ("◒", L.T("widgets.statsTitle"), L.T("widgets.statsHelp"), "4"),
            ("☑", L.T("widgets.todoTitle"), L.T("widgets.todoHelp"), "5"),
            ("↻", L.T("widgets.habitsTitle"), L.T("widgets.habitsHelp"), "6"),
            ("▦", L.T("widgets.calendarTitle"), L.T("widgets.calendarHelp"), "7"),
            ("◈", L.T("widgets.financeTitle"), L.T("widgets.financeHelp"), "8"),
            ("†", L.T("widgets.mementoTitle"), L.T("widgets.mementoHelp"), "9"),
            ("◷", L.T("widgets.timerTitle"), L.T("widgets.timerHelp"), "0")
        };
        int selected = -1;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; int index = i;
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = entry.Item1, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 34, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(new TextBlock { Text = entry.Item2, FontSize = 13, FontWeight = FontWeights.Black, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = entry.Item3, FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap });
            var launch = new Button { Content = content, Tag = entry.Item4, MinHeight = 100, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(16), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, Background = (Brush)Application.Current.Resources["Surface"] };
            if (index == entries.Length - 1 && !owner.CanAddTimerWidget)
            {
                launch.IsEnabled = false;
                launch.ToolTip = L.T("widgets.timerTaken");
                ToolTipService.SetShowOnDisabled(launch, true);
            }
            launch.SetValue(AutomationProperties.NameProperty, entry.Item2); launch.Click += (_, _) => { selected = index; window.DialogResult = true; }; launcher.Children.Add(launch);
        }
        Grid.SetRow(launcher, 2); root.Children.Add(launcher);
        window.PreviewKeyDown += (_, e) =>
        {
            int key = e.Key is >= Key.D1 and <= Key.D9 ? (int)e.Key - (int)Key.D1
                : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? (int)e.Key - (int)Key.NumPad1
                : e.Key is Key.D0 or Key.NumPad0 ? 9
                : -1;
            if (key < 0 || key >= entries.Length) return;
            selected = key;
            if (selected != entries.Length - 1 || owner.CanAddTimerWidget) window.DialogResult = true;
            e.Handled = true;
        };
        window.Loaded += (_, _) => { Modalize(window); Keyboard.Focus(launcher.Children[0]); }; window.ShowDialog(); return selected;
    }
    public static WindowCandidate? PickWindow(Window owner)
    {
        var availableWidth = owner.ActualWidth > 0 ? owner.ActualWidth - 32 : SystemParameters.WorkArea.Width * 0.82;
        var availableHeight = owner.ActualHeight > 0 ? owner.ActualHeight - 72 : SystemParameters.WorkArea.Height * 0.82;
        var window = Window(owner, L.T("widgets.pickTitle"), Math.Clamp(availableWidth, 420, 720), Math.Clamp(availableHeight, 480, 640));
        window.MinWidth = 420; window.MinHeight = 480;
        TextBox filter = null!; ComboBox category = null!; Button refreshButton = null!; TextBlock heading = null!; TextBlock help = null!; TextBlock summary = null!; TextBlock hint = null!; ListBox list = null!; Button embed = null!; Button cancel = null!; Button openTab = null!; Button appTab = null!; TextBox urlBox = null!; FrameworkElement urlRow = null!;
        var candidates = new List<WindowCandidate>();
        var apps = new List<AppLaunchTarget>();
        bool appsMode = false;
        bool launching = false;
        var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) }; window.Content = grid;
        for (int i = 0; i < 7; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var intro = new StackPanel();
        heading = Heading(L.T("widgets.pickHeading"));
        help = new TextBlock { Text = L.T("widgets.pickHelp"), Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 12, Margin = new Thickness(0, -8, 0, 10), TextWrapping = TextWrapping.Wrap };
        intro.Children.Add(heading); intro.Children.Add(help);
        Grid.SetRow(intro, 0); grid.Children.Add(intro);

        var tabs = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
        openTab = new Button { Content = L.T("widgets.pickOpenTab"), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 6, 0), FontSize = 11, FontWeight = FontWeights.Bold };
        appTab = new Button { Content = L.T("widgets.pickAppTab"), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(6, 0, 0, 0), FontSize = 11, FontWeight = FontWeights.Bold };
        tabs.Children.Add(openTab); tabs.Children.Add(appTab);
        Grid.SetRow(tabs, 1); grid.Children.Add(tabs);

        var toolbar = new Grid(); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter = new TextBox { Text = "", ToolTip = L.T("widgets.pickSearch"), Margin = new Thickness(0, 0, 8, 8), MinHeight = 38 };
        filter.SetValue(AutomationProperties.NameProperty, L.T("widgets.pickSearchName"));
        filter.TextChanged += (_, _) => Refresh(); Grid.SetColumn(filter, 0); toolbar.Children.Add(filter);
        category = new ComboBox { ItemsSource = new[] { L.T("widgets.pickAll"), L.T("widgets.pickBrowsers"), L.T("widgets.pickChat"), L.T("widgets.pickDev"), L.T("widgets.pickOther") }, SelectedIndex = 0, Width = 145, Margin = new Thickness(0, 4, 8, 10) };
        category.SelectionChanged += (_, _) => Refresh(); Grid.SetColumn(category, 1); toolbar.Children.Add(category);
        refreshButton = Button(L.T("widgets.pickRefresh"), () => _ = Reload()); refreshButton.Padding = new Thickness(10, 8, 10, 8); Grid.SetColumn(refreshButton, 2); toolbar.Children.Add(refreshButton);
        Grid.SetRow(toolbar, 2); grid.Children.Add(toolbar);

        var urlGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition());
        urlGrid.Children.Add(new TextBlock { Text = L.T("widgets.pickUrl"), FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        urlBox = new TextBox { MinHeight = 36, ToolTip = L.T("widgets.pickUrlHint") };
        urlBox.SetValue(AutomationProperties.NameProperty, L.T("widgets.pickUrlName"));
        Grid.SetColumn(urlBox, 1); urlGrid.Children.Add(urlBox);
        urlRow = urlGrid; urlRow.Visibility = Visibility.Collapsed;
        Grid.SetRow(urlRow, 3); grid.Children.Add(urlRow);

        summary = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap }; Grid.SetRow(summary, 4); grid.Children.Add(summary);
        list = new ListBox { BorderThickness = new Thickness(1), Padding = new Thickness(6), SelectionMode = SelectionMode.Single, ItemTemplate = CandidateTemplate() };
        var selectedStyle = new Style(typeof(ListBoxItem)); selectedStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); selectedStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 6))); selectedStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, (Brush)Application.Current.Resources["Ink"])); selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, (Brush)Application.Current.Resources["Accent"])); selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, (Brush)Application.Current.Resources["AccentInk"])); selectedStyle.Triggers.Add(selectedTrigger); list.ItemContainerStyle = selectedStyle;
        Grid.SetRow(list, 5); grid.Children.Add(list);

        hint = new TextBlock { Text = L.T("widgets.pickNote"), FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap }; Grid.SetRow(hint, 6); grid.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        WindowCandidate? selected = null;
        embed = Button(L.T("widgets.pickEmbed"), () => _ = ConfirmAsync()); embed.IsDefault = true; embed.Padding = new Thickness(14, 9, 14, 9); buttons.Children.Add(embed);
        cancel = Button(L.T("common.cancel"), () => window.Close()); cancel.IsCancel = true; buttons.Children.Add(cancel);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(buttons, 7); grid.Children.Add(buttons);

        static bool IsCategory(WindowCandidate candidate, int group)
        {
            var process = candidate.ProcessName.ToLowerInvariant();
            bool browser = process is "brave" or "chrome" or "msedge" or "firefox" or "opera" or "vivaldi";
            bool chat = process.Contains("telegram") || process.Contains("whatsapp") || process.Contains("discord") || process.Contains("slack");
            bool dev = process is "code" or "devenv" or "githubdesktop" or "notepad++" || process.Contains("studio");
            return group switch
            {
                1 => browser,
                2 => chat,
                3 => dev,
                4 => !browser && !chat && process is not ("code" or "devenv" or "githubdesktop"),
                _ => true
            };
        }
        void PaintTab(Button button, bool on)
        {
            button.Background = (Brush)Application.Current.Resources[on ? "Chrome" : "Surface"];
            button.Foreground = (Brush)Application.Current.Resources[on ? "ChromeInk" : "Ink"];
        }
        void UpdateUrlRow()
        {
            urlRow.Visibility = appsMode && list.SelectedItem is AppLaunchTarget { IsBrowser: true } ? Visibility.Visible : Visibility.Collapsed;
        }
        void SetMode(bool appsSelected)
        {
            if (launching) return;
            appsMode = appsSelected;
            PaintTab(openTab, !appsMode);
            PaintTab(appTab, appsMode);
            category.Visibility = appsMode ? Visibility.Collapsed : Visibility.Visible;
            filter.ToolTip = L.T(appsMode ? "widgets.pickAppSearch" : "widgets.pickSearch");
            filter.SetValue(AutomationProperties.NameProperty, L.T(appsMode ? "widgets.pickAppSearchName" : "widgets.pickSearchName"));
            heading.Text = L.T(appsMode ? "widgets.pickAppHeading" : "widgets.pickHeading");
            help.Text = L.T(appsMode ? "widgets.pickAppHelp" : "widgets.pickHelp");
            hint.Text = L.T(appsMode ? "widgets.pickAppNote" : "widgets.pickNote");
            embed.Content = L.T(appsMode ? "widgets.pickLaunch" : "widgets.pickEmbed");
            Refresh();
            if (appsMode && apps.Count == 0) _ = LoadApps();
        }
        void Refresh()
        {
            var query = filter.Text.Trim();
            if (appsMode)
            {
                var visible = apps.Where(app => string.IsNullOrWhiteSpace(query) || app.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || app.ExePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                list.ItemsSource = visible;
                summary.Text = L.T("widgets.pickAppCount", visible.Count.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
                embed.IsEnabled = visible.Count > 0 && !launching;
            }
            else
            {
                int group = Math.Max(0, category.SelectedIndex);
                var visible = candidates.Where(w => (string.IsNullOrWhiteSpace(query) || w.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) && IsCategory(w, group)).ToList();
                list.ItemsSource = visible;
                summary.Text = L.T("widgets.pickCount", visible.Count.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
                embed.IsEnabled = visible.Count > 0 && !launching;
            }
            UpdateUrlRow();
        }
        async Task LoadCandidates()
        {
            refreshButton.IsEnabled = false; summary.Text = L.T("widgets.pickSearching");
            try { candidates = await Task.Run(WindowLease.Candidates); if (!appsMode) Refresh(); }
            finally { refreshButton.IsEnabled = true; }
        }
        async Task LoadApps()
        {
            refreshButton.IsEnabled = false; summary.Text = L.T("widgets.pickAppSearching");
            try
            {
                apps = AppLaunch.KnownInstalled();
                if (appsMode) Refresh();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                AppLaunch.InvalidateCatalog();
                apps = AppLaunch.Catalog().ToList();
                if (appsMode) Refresh();
            }
            finally { refreshButton.IsEnabled = true; }
        }
        Task Reload() => appsMode ? LoadApps() : LoadCandidates();
        async Task ConfirmAsync()
        {
            if (launching) return;
            if (appsMode)
            {
                if (list.SelectedItem is not AppLaunchTarget app)
                {
                    summary.Text = L.T("widgets.pickAppNeedPick");
                    return;
                }
                launching = true;
                embed.IsEnabled = false;
                cancel.IsEnabled = false;
                summary.Text = L.T("widgets.pickLaunching", app.Name);
                try
                {
                    string typed = urlBox.Text.Trim();
                    string? open = app.IsBrowser ? LaunchUrl.ForBrowser(typed, null) : null;
                    selected = await AppLaunch.OpenNewWindowAsync(app, open, CancellationToken.None);
                    window.DialogResult = true;
                }
                catch (Exception ex)
                {
                    summary.Text = ex.Message;
                    launching = false;
                    embed.IsEnabled = true;
                    cancel.IsEnabled = true;
                }
            }
            else if (list.SelectedItem is WindowCandidate c)
            {
                selected = c;
                window.DialogResult = true;
            }
        }
        openTab.Click += (_, _) => SetMode(false);
        appTab.Click += (_, _) => SetMode(true);
        list.SelectionChanged += (_, _) => UpdateUrlRow();
        list.MouseDoubleClick += (_, _) => _ = ConfirmAsync();
        list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = ConfirmAsync(); e.Handled = true; } };
        filter.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && list.Items.Count > 0) { list.SelectedIndex = 0; list.Focus(); e.Handled = true; } };
        SetMode(false);
        window.Loaded += (_, _) => { Modalize(window); filter.Focus(); _ = LoadCandidates(); };
        window.ShowDialog(); return selected;
    }
    private static DataTemplate CandidateTemplate()
    {
        var template = new DataTemplate(typeof(WindowCandidate));
        var card = new FrameworkElementFactory(typeof(Border)); card.SetValue(Border.BorderBrushProperty, (Brush)Application.Current.Resources["Edge"]); card.SetValue(Border.BorderThicknessProperty, new Thickness(1)); card.SetValue(Border.BackgroundProperty, (Brush)Application.Current.Resources["Surface"]); card.SetValue(Border.PaddingProperty, new Thickness(10, 8, 10, 8));
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); row.SetValue(StackPanel.MinHeightProperty, 52d);
        var icon = new FrameworkElementFactory(typeof(Border)); icon.SetValue(Border.WidthProperty, 36d); icon.SetValue(Border.HeightProperty, 36d); icon.SetValue(Border.BackgroundProperty, (Brush)Application.Current.Resources["Accent"]); icon.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left); icon.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.WidthProperty, 26d); image.SetValue(Image.HeightProperty, 26d); image.SetValue(Image.StretchProperty, Stretch.Uniform); image.SetBinding(Image.SourceProperty, new Binding(nameof(WindowCandidate.Icon))); icon.AppendChild(image); row.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(StackPanel.MarginProperty, new Thickness(10, 0, 0, 0)); text.SetValue(FrameworkElement.WidthProperty, 430d);
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(WindowCandidate.Title))); title.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold); title.SetValue(TextBlock.FontSizeProperty, 13d); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.AppendChild(title);
        var process = new FrameworkElementFactory(typeof(TextBlock)); process.SetBinding(TextBlock.TextProperty, new Binding(nameof(WindowCandidate.ProcessLabel))); process.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas")); process.SetValue(TextBlock.FontSizeProperty, 10d); process.SetValue(TextBlock.ForegroundProperty, (Brush)Application.Current.Resources["Muted"]); text.AppendChild(process);
        row.AppendChild(text); card.AppendChild(row); template.VisualTree = card; return template;
    }
    private sealed class ModalSurface : Border { }
}
