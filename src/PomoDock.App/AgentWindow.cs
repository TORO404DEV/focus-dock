using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>A focused command room for the local agent, drawn with the same paper/ink system.</summary>
internal sealed class AgentWindow : Window
{
    private readonly MainWindow owner;
    private readonly LocalAgentModel model;
    private readonly PomoAgent agent;
    private readonly LocalAgentVoice voice;
    private readonly StackPanel transcript = new();
    private readonly ScrollViewer scroller = new();
    private readonly TextBox input = new();
    private readonly Button send = new();
    private readonly Button download = new();
    private readonly ProgressBar progress = new();
    private readonly TextBlock status = new();
    private readonly CheckBox voiceToggle = new();
    private CancellationTokenSource? operation;
    private bool busy;
    private bool closeWhenIdle;

    public AgentWindow(MainWindow owner)
    {
        this.owner = owner;
        model = new LocalAgentModel(owner.Store.DirectoryPath);
        agent = new PomoAgent(owner, model);
        voice = new LocalAgentVoice(owner.Store.DirectoryPath);
        Owner = owner; Title = L.T("agent.title"); Width = 700; Height = 760; MinWidth = 500; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip; Topmost = true; ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = L.T("agent.title"), FontFamily = new FontFamily("Consolas"), FontSize = 25, FontWeight = FontWeights.Black });
        heading.Children.Add(new TextBlock { Text = L.T("agent.subtitle"), FontFamily = new FontFamily("Consolas"), FontSize = 9, Foreground = Resource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(heading);
        var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        status.FontFamily = new FontFamily("Consolas"); status.FontSize = 9; status.FontWeight = FontWeights.Bold; status.HorizontalAlignment = HorizontalAlignment.Right;
        status.Foreground = Resource("Muted"); state.Children.Add(status);
        voiceToggle.Content = "◉  " + L.T("agent.voice"); voiceToggle.IsChecked = owner.Settings.AgentVoiceEnabled; voiceToggle.FontFamily = new FontFamily("Consolas"); voiceToggle.FontSize = 9;
        voiceToggle.HorizontalAlignment = HorizontalAlignment.Right; voiceToggle.Margin = new Thickness(0, 5, 0, 0);
        voiceToggle.Click += (_, _) => { owner.Settings.AgentVoiceEnabled = voiceToggle.IsChecked == true; owner.SaveState(); if (!owner.Settings.AgentVoiceEnabled) voice.Stop(); };
        state.Children.Add(voiceToggle); Grid.SetColumn(state, 1); header.Children.Add(state); root.Children.Add(header);

        scroller.Content = transcript; scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroller.BorderBrush = Resource("Line"); scroller.BorderThickness = new Thickness(1); scroller.Padding = new Thickness(14); scroller.Background = Resource("Paper");
        Grid.SetRow(scroller, 1); root.Children.Add(scroller);
        AddMessage(L.T("agent.name"), L.T("agent.welcome"), false);

        var composer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        composer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); composer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition()); composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.MinHeight = 74; input.MaxHeight = 150; input.AcceptsReturn = true; input.TextWrapping = TextWrapping.Wrap; input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        input.ToolTip = L.T("agent.placeholder"); input.SetValue(AutomationProperties.NameProperty, L.T("agent.placeholder")); composer.Children.Add(input);
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _ = Run(); e.Handled = true; } };
        send.Content = L.T("agent.send"); send.Padding = new Thickness(16, 12, 16, 12); send.Margin = new Thickness(8, 0, 0, 0); send.VerticalAlignment = VerticalAlignment.Stretch;
        send.Click += async (_, _) => { if (busy) Cancel(); else await Run(); }; Grid.SetColumn(send, 1); composer.Children.Add(send);

        var setup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        download.Content = L.T("agent.download"); download.Padding = new Thickness(12, 7, 12, 7); download.Click += async (_, _) => await PrepareModel(true);
        setup.Children.Add(download); progress.Width = 180; progress.Height = 8; progress.Minimum = 0; progress.Maximum = 100; progress.Margin = new Thickness(12, 0, 0, 0); progress.VerticalAlignment = VerticalAlignment.Center; setup.Children.Add(progress);
        Grid.SetRow(setup, 1); Grid.SetColumnSpan(setup, 2); composer.Children.Add(setup);
        Grid.SetRow(composer, 2); root.Children.Add(composer);

        Loaded += async (_, _) => { input.Focus(); await PrepareModel(false); };
        Closing += OnClosing;
        Dialogs.Modalize(this);
    }

    private async Task PrepareModel(bool downloadWhenMissing)
    {
        if (busy || model.IsLoaded) return;
        if (!model.IsDownloaded && !downloadWhenMissing)
        {
            status.Text = L.T("agent.modelMissing"); download.Visibility = Visibility.Visible; progress.Visibility = Visibility.Visible; return;
        }
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true);
        try
        {
            if (!model.IsDownloaded)
            {
                download.Visibility = Visibility.Collapsed; progress.Visibility = Visibility.Visible;
                var report = new Progress<double>(value => { progress.Value = value * 100; status.Text = L.T("agent.downloading", Math.Floor(value * 100)); });
                await model.DownloadAsync(report, activeOperation.Token);
            }
            status.Text = L.T("agent.loading"); progress.IsIndeterminate = true;
            await model.LoadAsync(activeOperation.Token);
            status.Text = $"{L.T("agent.ready")} · {model.Backend}"; progress.IsIndeterminate = false; progress.Visibility = Visibility.Collapsed; download.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { status.Text = model.IsDownloaded ? L.T("agent.ready") : L.T("agent.modelMissing"); }
        catch (Exception ex)
        {
            status.Text = L.T("agent.modelError", ex.Message); download.Visibility = Visibility.Visible; progress.IsIndeterminate = false;
            Dialogs.Alert(this, L.T("agent.title"), L.T("agent.modelError", ex.Message));
        }
        finally
        {
            if (ReferenceEquals(operation, activeOperation)) operation = null;
            activeOperation.Dispose();
            SetBusy(false);
            if (closeWhenIdle) _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private async Task Run()
    {
        if (busy) return;
        var request = input.Text.Trim(); if (request.Length == 0) return;
        if (!model.IsLoaded)
        {
            await PrepareModel(true);
            if (!model.IsLoaded) return;
        }
        input.Clear(); AddMessage(L.T("agent.you"), request, true);
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true); status.Text = L.T("agent.working");
        try
        {
            var result = await agent.RunAsync(request, text => Dispatcher.InvokeAsync(() => status.Text = text), activeOperation.Token);
            AddMessage(L.T("agent.name"), result.Message, false, result.Receipts);
            status.Text = L.T("agent.ready");
            if (voiceToggle.IsChecked == true)
            {
                status.Text = L.T("agent.voicePreparing");
                var voiceProgress = new Progress<double>(value => status.Text = value >= .999
                    ? L.T("agent.voiceSpeaking")
                    : $"{L.T("agent.voicePreparing")} {Math.Floor(value * 100)}%");
                status.Text = L.T("agent.voicePreparing");
                await voice.SpeakAsync(result.Message, Strings.Culture.TwoLetterISOLanguageName, owner.Settings.AgentVoiceSpeed, voiceProgress, activeOperation.Token);
                status.Text = L.T("agent.ready");
            }
        }
        catch (OperationCanceledException) { status.Text = L.T("agent.ready"); }
        catch (Exception ex)
        {
            string message = L.T("agent.error", ex.Message); AddMessage(L.T("agent.name"), message, false);
            status.Text = message; Dialogs.Alert(this, L.T("agent.title"), message);
        }
        finally
        {
            if (ReferenceEquals(operation, activeOperation)) operation = null;
            activeOperation.Dispose();
            SetBusy(false);
            input.Focus();
            if (closeWhenIdle) _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private void Cancel() { voice.Stop(); operation?.Cancel(); }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            closeWhenIdle = true;
            Cancel();
            status.Text = Strings.Culture.TwoLetterISOLanguageName == "es" ? "CERRANDO CON SEGURIDAD…" : "CLOSING SAFELY…";
            return;
        }
        voice.Dispose();
        model.Dispose();
    }

    private void SetBusy(bool value)
    {
        busy = value; input.IsEnabled = !value; download.IsEnabled = !value;
        send.Content = value ? L.T("agent.cancel") : L.T("agent.send");
    }

    private void AddMessage(string author, string message, bool mine, IReadOnlyList<AgentReceipt>? receipts = null)
    {
        var card = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(1), Background = mine ? Resource("Accent") : Resource("Surface"), Padding = new Thickness(13, 10, 13, 11), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = author, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Black, FontSize = 9, Foreground = Resource("Muted") });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 5, 0, 0) });
        if (receipts is { Count: > 0 })
        {
            stack.Children.Add(new TextBlock { Text = L.T("agent.actions"), FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, FontSize = 8, Foreground = Resource("Muted"), Margin = new Thickness(0, 10, 0, 3) });
            foreach (var receipt in receipts) stack.Children.Add(new TextBlock { Text = "✓  " + receipt.Summary, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        card.Child = stack; transcript.Children.Add(card);
        Dispatcher.BeginInvoke(() => scroller.ScrollToEnd());
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
