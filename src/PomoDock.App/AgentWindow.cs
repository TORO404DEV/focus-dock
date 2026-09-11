using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>A conversation room for the local agent, drawn with PomoDock's paper, ink and chrome.</summary>
internal sealed class AgentWindow : Window
{
    private readonly MainWindow owner;
    private readonly LocalAgentModel model;
    private readonly PomoAgent agent;
    private readonly LocalAgentVoice voice;
    private readonly AgentConversationStore conversations;
    private readonly AgentHoldMic mic;
    private AgentChat chat;
    private readonly StackPanel transcript = new();
    private readonly ScrollViewer scroller = new();
    private readonly TextBox input = new();
    private readonly Button send = new();
    private readonly Button download = new();
    private readonly Button micButton = new();
    private readonly ProgressBar progress = new();
    private readonly TextBlock status = new();
    private readonly TextBlock activity = new();
    private readonly CheckBox voiceToggle = new();
    private readonly Border setupRow = new();
    private CancellationTokenSource? operation;
    private AgentPlan? pendingPlan;
    private UIElement? planCard;
    private bool busy;
    private bool closeWhenIdle;

    public AgentWindow(MainWindow owner)
    {
        this.owner = owner;
        model = new LocalAgentModel(owner.Store.DirectoryPath);
        agent = new PomoAgent(owner, model);
        voice = new LocalAgentVoice(owner.Store.DirectoryPath);
        conversations = new AgentConversationStore(owner.Store);
        chat = conversations.LatestOrNew();
        mic = new AgentHoldMic(owner, input);
        Owner = owner; Title = L.T("agent.title"); Width = 720; Height = 820; MinWidth = 520; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip; Topmost = true; ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new Grid { Margin = new Thickness(20, 16, 20, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var chrome = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(1.5), Background = Resource("Chrome"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        status.Text = L.T("agent.subtitle"); status.FontFamily = new FontFamily("Consolas"); status.FontSize = 9; status.FontWeight = FontWeights.Bold;
        status.Foreground = Resource("ChromeInk");
        activity.FontFamily = new FontFamily("Consolas"); activity.FontSize = 8; activity.Foreground = Resource("ChromeInk"); activity.Opacity = .7; activity.Margin = new Thickness(0, 3, 0, 0);
        left.Children.Add(status); left.Children.Add(activity);
        head.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        voiceToggle.Content = "◉  " + L.T("agent.voice"); voiceToggle.IsChecked = owner.Settings.AgentVoiceEnabled;
        voiceToggle.FontFamily = new FontFamily("Consolas"); voiceToggle.FontSize = 9; voiceToggle.Foreground = Resource("ChromeInk"); voiceToggle.Margin = new Thickness(0, 0, 8, 0);
        voiceToggle.Click += (_, _) => { owner.Settings.AgentVoiceEnabled = voiceToggle.IsChecked == true; owner.SaveState(); if (!owner.Settings.AgentVoiceEnabled) voice.Stop(); };
        var fresh = new Button { Content = L.T("agent.newChat"), FontSize = 8, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0), Background = Resource("Chrome"), Foreground = Resource("ChromeInk"), BorderBrush = Resource("ChromeInk") };
        fresh.Click += (_, _) => NewChat();
        right.Children.Add(voiceToggle); right.Children.Add(fresh);
        Grid.SetColumn(right, 1); head.Children.Add(right);
        chrome.Child = head; root.Children.Add(chrome);

        scroller.Content = transcript; scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroller.BorderBrush = Resource("Edge"); scroller.BorderThickness = new Thickness(2); scroller.Padding = new Thickness(14); scroller.Background = Resource("Paper");
        Grid.SetRow(scroller, 1); root.Children.Add(scroller);
        RestoreChat();

        var composer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.MinHeight = 64; input.MaxHeight = 140; input.AcceptsReturn = true; input.TextWrapping = TextWrapping.Wrap;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; input.FontSize = 13; input.Tag = "agent-composer";
        input.ToolTip = L.T("agent.placeholder"); input.SetValue(AutomationProperties.NameProperty, L.T("agent.placeholder"));
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _ = Run(); e.Handled = true; } };
        line.Children.Add(input);
        micButton.Content = "●"; micButton.Width = 52; micButton.Padding = new Thickness(0); micButton.Margin = new Thickness(8, 0, 0, 0); micButton.FontSize = 16;
        micButton.ToolTip = L.T("agent.holdToTalk"); micButton.Focusable = true;
        micButton.PreviewMouseLeftButtonDown += (_, e) => { mic.Begin(micButton); e.Handled = true; };
        micButton.PreviewMouseLeftButtonUp += async (_, e) => { await mic.End(owner.Settings.AgentSendVoiceOnRelease ? Run : null); e.Handled = true; };
        micButton.MouseLeave += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Pressed) mic.Cancel(); };
        micButton.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { mic.Begin(micButton); e.Handled = true; } };
        micButton.PreviewKeyUp += async (_, e) => { if (e.Key == Key.Space) { await mic.End(owner.Settings.AgentSendVoiceOnRelease ? Run : null); e.Handled = true; } };
        Grid.SetColumn(micButton, 1); line.Children.Add(micButton);
        send.Content = L.T("agent.send"); send.Padding = new Thickness(16, 12, 16, 12); send.Margin = new Thickness(8, 0, 0, 0);
        send.Background = Resource("Ink"); send.Foreground = Resource("Paper"); send.Click += async (_, _) => { if (busy) Cancel(); else await Run(); };
        Grid.SetColumn(send, 2); line.Children.Add(send);
        composer.Children.Add(line);

        var setup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        download.Content = L.T("agent.download"); download.Padding = new Thickness(12, 7, 12, 7); download.Click += async (_, _) => await PrepareModel(true);
        progress.Width = 180; progress.Height = 8; progress.Minimum = 0; progress.Maximum = 100; progress.Margin = new Thickness(12, 0, 0, 0); progress.VerticalAlignment = VerticalAlignment.Center;
        setup.Children.Add(download); setup.Children.Add(progress);
        setupRow.Child = setup; setupRow.Margin = new Thickness(0, 4, 0, 0);
        composer.Children.Add(setupRow);
        Grid.SetRow(composer, 2); root.Children.Add(composer);

        Loaded += async (_, _) => { input.Focus(); await PrepareModel(false); };
        Closing += OnClosing;
        Dialogs.Modalize(this);
    }

    private void RestoreChat()
    {
        transcript.Children.Clear();
        if (chat.Messages.Count == 0) AddBubble(L.T("agent.name"), L.T("agent.welcome"), false);
        else foreach (var message in chat.Messages) AddBubble(message.Role == "you" ? L.T("agent.you") : L.T("agent.name"), message.Text, message.Role == "you", message.Receipts, message.Plan, persist: false, at: message.At);
        ScrollEnd();
    }

    private void NewChat()
    {
        conversations.Save();
        chat = conversations.StartNew();
        pendingPlan = null; planCard = null;
        RestoreChat();
    }

    private async Task PrepareModel(bool downloadWhenMissing)
    {
        if (busy || model.IsLoaded) return;
        var file = model.FileStatus;
        if (file.Ready)
        {
            setupRow.Visibility = Visibility.Collapsed;
            await LoadModel();
            return;
        }
        if (file.Partial)
        {
            download.Content = L.T("agent.resumeDownload", Math.Floor(file.Progress * 100));
            download.Visibility = Visibility.Visible; progress.Visibility = Visibility.Visible; progress.Value = file.Progress * 100;
            status.Text = L.T("agent.modelPartial", Math.Floor(file.Progress * 100));
            if (!downloadWhenMissing) return;
        }
        else if (!downloadWhenMissing)
        {
            status.Text = L.T("agent.modelMissing"); download.Visibility = Visibility.Visible; progress.Visibility = Visibility.Visible;
            return;
        }
        await LoadModel(allowDownload: true);
    }

    private async Task LoadModel(bool allowDownload = false)
    {
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true);
        try
        {
            if (allowDownload || !model.IsDownloaded)
            {
                download.Visibility = Visibility.Collapsed; progress.Visibility = Visibility.Visible; setupRow.Visibility = Visibility.Visible;
                var report = new Progress<double>(value => { progress.Value = value * 100; status.Text = L.T("agent.downloading", Math.Floor(value * 100)); });
                await model.DownloadAsync(report, activeOperation.Token);
            }
            status.Text = L.T("agent.loading"); progress.IsIndeterminate = true;
            await model.LoadAsync(activeOperation.Token);
            status.Text = $"{L.T("agent.ready")} · {model.Backend}"; progress.IsIndeterminate = false; setupRow.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { status.Text = model.IsDownloaded ? L.T("agent.ready") : L.T("agent.modelMissing"); }
        catch (Exception ex)
        {
            status.Text = L.T("agent.modelError", ex.Message); setupRow.Visibility = Visibility.Visible; download.Visibility = Visibility.Visible; progress.IsIndeterminate = false;
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
        input.Clear(); AddBubble(L.T("agent.you"), request, true);
        chat.Add("you", request); conversations.Save();
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true);
        var notices = new Progress<AgentNotice>(notice => Dispatcher.Invoke(() =>
        {
            activity.Text = notice.Text;
            status.Text = notice.Kind switch
            {
                AgentEventKind.Reading => L.T("agent.consulting"),
                AgentEventKind.PlanReady => L.T("agent.planReady"),
                AgentEventKind.Executing => notice.Text,
                AgentEventKind.AnswerDelta => L.T("agent.responding"),
                AgentEventKind.Failed => notice.Text,
                _ => L.T("agent.interpreting")
            };
        }));
        try
        {
            var result = await agent.InterpretAsync(request, chat.Messages, notices, activeOperation.Token);
            if (result.NeedsApproval && result.Plan is { } plan)
            {
                pendingPlan = plan;
                ShowPlan(plan);
                status.Text = L.T("agent.needDecision");
                activity.Text = plan.Understood;
                chat.Add("plan", plan.Message, plan: plan); conversations.Save();
            }
            else
            {
                AddBubble(L.T("agent.name"), result.Message, false, result.Receipts);
                chat.Add("agent", result.Message, result.Receipts); conversations.Save();
                status.Text = L.T("agent.ready");
                await Speak(result.Message, activeOperation.Token);
            }
        }
        catch (OperationCanceledException) { status.Text = L.T("agent.cancelled"); }
        catch (Exception ex)
        {
            string message = L.T("agent.error", ex.Message);
            AddError(message, ex.ToString());
            status.Text = L.T("agent.ready");
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

    private void ShowPlan(AgentPlan plan)
    {
        if (planCard is not null) transcript.Children.Remove(planCard);
        var card = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(2), Background = Resource("Surface"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();
        stack.Children.Add(Label(L.T("agent.understood")));
        stack.Children.Add(new TextBlock { Text = plan.Understood, FontSize = 13, Margin = new Thickness(0, 2, 0, 10) });
        stack.Children.Add(Label(L.T("agent.plan")));
        foreach (var step in plan.Steps)
            stack.Children.Add(new TextBlock { Text = "→  " + (step.Label.Length > 0 ? step.Label : step.Tool), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        if (plan.Changes.Length > 0)
            stack.Children.Add(new TextBlock { Text = plan.Changes, FontFamily = new FontFamily("Consolas"), FontSize = 9, Foreground = Resource("Muted"), Margin = new Thickness(0, 10, 0, 0) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var go = new Button { Content = L.T("agent.executePlan"), Padding = new Thickness(14, 8, 14, 8), Background = Resource("Ink"), Foreground = Resource("Paper") };
        var edit = new Button { Content = L.T("agent.editPlan"), Padding = new Thickness(14, 8, 14, 8) };
        var stop = new Button { Content = L.T("agent.cancelPlan"), Padding = new Thickness(14, 8, 14, 8) };
        go.Click += async (_, _) => await ExecutePending();
        edit.Click += (_, _) => { input.Text = plan.Message; input.Focus(); input.CaretIndex = input.Text.Length; };
        stop.Click += (_, _) => CancelPlan();
        actions.Children.Add(go); actions.Children.Add(edit); actions.Children.Add(stop);
        stack.Children.Add(actions);
        card.Child = stack; planCard = card; transcript.Children.Add(card); ScrollEnd();
    }

    private async Task ExecutePending()
    {
        if (pendingPlan is null || busy) return;
        var plan = pendingPlan; pendingPlan = null;
        if (planCard is not null) { transcript.Children.Remove(planCard); planCard = null; }
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true); status.Text = L.T("agent.executing");
        try
        {
            var notices = new Progress<AgentNotice>(notice => Dispatcher.Invoke(() => { activity.Text = notice.Text; status.Text = notice.Text; }));
            var result = await agent.ExecuteAsync(plan, notices, activeOperation.Token);
            AddBubble(L.T("agent.name"), result.Message, false, result.Receipts);
            chat.Add("agent", result.Message, result.Receipts); conversations.Save();
            status.Text = L.T("agent.ready");
            await Speak(result.Message, activeOperation.Token);
        }
        catch (OperationCanceledException) { status.Text = L.T("agent.cancelled"); }
        catch (Exception ex) { AddError(L.T("agent.error", ex.Message), ex.ToString()); }
        finally
        {
            if (ReferenceEquals(operation, activeOperation)) operation = null;
            activeOperation.Dispose();
            SetBusy(false);
            input.Focus();
        }
    }

    private void CancelPlan()
    {
        pendingPlan = null;
        if (planCard is not null) { transcript.Children.Remove(planCard); planCard = null; }
        AddBubble(L.T("agent.name"), L.T("agent.planCancelled"), false);
        chat.Add("agent", L.T("agent.planCancelled")); conversations.Save();
        status.Text = L.T("agent.ready");
    }

    private async Task Speak(string message, CancellationToken cancellationToken)
    {
        if (voiceToggle.IsChecked != true || string.IsNullOrWhiteSpace(message)) return;
        status.Text = L.T("agent.voicePreparing");
        var voiceProgress = new Progress<double>(value => status.Text = value >= .999 ? L.T("agent.voiceSpeaking") : $"{L.T("agent.voicePreparing")} {Math.Floor(value * 100)}%");
        await voice.SpeakAsync(message, Strings.Culture.TwoLetterISOLanguageName, owner.Settings.AgentVoiceSpeed, voiceProgress, cancellationToken);
        status.Text = L.T("agent.ready");
    }

    private void Cancel() { voice.Stop(); mic.Cancel(); operation?.Cancel(); }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        conversations.Save();
        if (busy)
        {
            e.Cancel = true; closeWhenIdle = true; Cancel();
            status.Text = L.T("agent.closing");
            return;
        }
        mic.Dispose(); voice.Dispose(); model.Dispose();
    }

    private void SetBusy(bool value)
    {
        busy = value; download.IsEnabled = !value;
        send.Content = value ? L.T("agent.cancel") : L.T("agent.send");
        send.Background = value ? Resource("Surface") : Resource("Ink");
        send.Foreground = value ? Resource("Ink") : Resource("Paper");
    }

    private void AddBubble(string author, string message, bool mine, IReadOnlyList<AgentActionReceipt>? receipts = null, AgentPlan? plan = null, bool persist = true, DateTime? at = null)
    {
        var card = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(mine ? 1 : 2), Background = mine ? Resource("Accent") : Resource("Surface"), Padding = new Thickness(13, 10, 13, 11), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();
        var meta = new Grid();
        meta.ColumnDefinitions.Add(new ColumnDefinition()); meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.Children.Add(new TextBlock { Text = author, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Black, FontSize = 9, Foreground = Resource("Muted") });
        meta.Children.Add(new TextBlock { Text = (at ?? DateTime.Now).ToString("HH:mm", Strings.Culture), FontFamily = new FontFamily("Consolas"), FontSize = 8, Foreground = Resource("Muted"), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(meta.Children[1], 1);
        stack.Children.Add(meta);
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
        if (receipts is { Count: > 0 })
        {
            stack.Children.Add(Label(L.T("agent.actions")));
            foreach (var receipt in receipts)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = "✓  " + receipt.Summary, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                if (receipt.CanUndo)
                {
                    var undo = new Button { Content = L.T("agent.undo"), FontSize = 8, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) };
                    var copy = receipt;
                    undo.Click += async (_, _) =>
                    {
                        var undone = await agent.UndoAsync(copy, CancellationToken.None);
                        AddBubble(L.T("agent.name"), undone.Message, false, undone.Receipts);
                        chat.Add("agent", undone.Message, undone.Receipts); conversations.Save();
                    };
                    Grid.SetColumn(undo, 1); row.Children.Add(undo);
                }
                stack.Children.Add(row);
            }
        }
        card.Child = stack; transcript.Children.Add(card); ScrollEnd();
    }

    private void AddError(string message, string details)
    {
        var card = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(2), Background = Resource("Surface"), Padding = new Thickness(13, 10, 13, 11), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();
        stack.Children.Add(Label(L.T("agent.name")));
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 8) });
        var detail = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 10, MaxHeight = 140, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var show = new Button { Content = L.T("agent.details"), FontSize = 8, Padding = new Thickness(10, 5, 10, 5) };
        show.Click += (_, _) => detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        var retry = new Button { Content = L.T("agent.retry"), FontSize = 8, Padding = new Thickness(10, 5, 10, 5) };
        retry.Click += async (_, _) => { if (chat.Messages.LastOrDefault(m => m.Role == "you") is { } last) { input.Text = last.Text; await Run(); } };
        var copy = new Button { Content = L.T("agent.copyError"), FontSize = 8, Padding = new Thickness(10, 5, 10, 5) };
        copy.Click += (_, _) => Clipboard.SetText(details);
        actions.Children.Add(show); actions.Children.Add(retry); actions.Children.Add(copy);
        stack.Children.Add(detail); stack.Children.Add(actions);
        card.Child = stack; transcript.Children.Add(card);
        chat.Add("error", message); conversations.Save();
        ScrollEnd();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, FontSize = 8, Foreground = Resource("Muted"), Margin = new Thickness(0, 8, 0, 2) };
    private void ScrollEnd() => Dispatcher.BeginInvoke(() => scroller.ScrollToEnd());
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
