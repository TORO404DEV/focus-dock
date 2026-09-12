using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Chat-first agent widget: title + icon tools, message feed, composer. Recent chats live in
/// an overlay (not a combo of first messages next to NEW THREAD).
/// </summary>
internal sealed class AgentWindow
{
    private readonly MainWindow owner;
    private readonly Action dismiss;
    private readonly PomoAgent agent;
    private readonly LocalAgentVoice voice;
    private readonly AgentConversationStore conversations;
    private readonly AgentHoldMic mic;
    private AgentChat chat;
    private readonly StackPanel transcript = new();
    private readonly StackPanel threads = new();
    private readonly ScrollViewer scroller = new();
    private readonly ScrollViewer threadScroll = new();
    private readonly TextBox input = new();
    private readonly TextBox historySearch = new();
    private readonly TextBox apiKeyBox = new();
    private readonly Button send = new();
    private readonly Button download = new();
    private readonly Button micButton = new();
    private readonly Button historyButton = new();
    private readonly Button newButton = new();
    private readonly Button voiceButton = new();
    private readonly ProgressBar progress = new();
    private readonly TextBlock status = new();
    private readonly TextBlock empty = new();
    private readonly TextBlock threadTitle = new();
    private readonly TextBlock sendGlyph = new();
    private readonly TextBlock voiceGlyph = new();
    private readonly TextBlock historyGlyph = new();
    private readonly Border setupRow = new();
    private readonly Border historyPanel = new();
    private readonly Grid composerLine = new();
    private readonly Grid stage = new();
    private CancellationTokenSource? operation;
    private AgentPlan? pendingPlan;
    private UIElement? planCard;
    private bool busy;
    private bool closeWhenIdle;
    private bool historyOpen;
    private bool voiceOn;
    private readonly TextBlock downloadTitle = new();
    private readonly TextBlock downloadHint = new();
    private TextBlock? liveBody;
    private UIElement? liveCard;
    private bool liveOpen;
    public FrameworkElement Surface { get; }

    public AgentWindow(MainWindow owner, Action dismiss)
    {
        this.owner = owner;
        this.dismiss = dismiss;
        agent = new PomoAgent(owner, owner.Settings);
        voice = new LocalAgentVoice(owner.Store.DirectoryPath);
        conversations = new AgentConversationStore(owner.Store);
        chat = conversations.LatestOrNew();
        mic = new AgentHoldMic(owner, input);
        voiceOn = owner.Settings.AgentVoiceEnabled;

        var room = BuildRoom();
        historyPanel.Child = BuildHistory();
        historyPanel.Background = Resource("Paper");
        historyPanel.Visibility = Visibility.Collapsed;
        stage.Children.Add(room);
        stage.Children.Add(historyPanel);
        stage.ClipToBounds = true; stage.MinWidth = 0; stage.MinHeight = 0;

        var page = new Grid
        {
            Margin = new Thickness(10, 4, 10, 10),
            ClipToBounds = true,
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 72 });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(BuildChrome());
        progress.Height = 3; progress.Minimum = 0; progress.Maximum = 100;
        progress.Margin = new Thickness(0, 0, 0, 6); progress.Visibility = Visibility.Collapsed;
        Grid.SetRow(progress, 1); page.Children.Add(progress);
        Grid.SetRow(stage, 2); page.Children.Add(stage);
        var composer = BuildComposer();
        Grid.SetRow(composer, 3); page.Children.Add(composer);

        Surface = page;
        Surface.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !historyOpen) return;
            ShowHistory(false);
            e.Handled = true;
        };

        mic.RecordingChanged += recording => owner.Dispatcher.Invoke(() =>
        {
            micButton.Background = recording ? Resource("Ink") : Brushes.Transparent;
            micButton.Foreground = recording ? Resource("Paper") : Resource("Ink");
        });

        RestoreChat();
        PaintThreads();
        PaintVoice();
        Surface.Loaded += (_, _) =>
        {
            input.Focus();
            _ = WarmVoice();
            PaintApiSetup();
        };
    }

    private UIElement BuildChrome()
    {
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        threadTitle.FontSize = 13; threadTitle.FontWeight = FontWeights.SemiBold;
        threadTitle.TextTrimming = TextTrimming.CharacterEllipsis; threadTitle.TextWrapping = TextWrapping.NoWrap;
        threadTitle.VerticalAlignment = VerticalAlignment.Center; threadTitle.Margin = new Thickness(2, 0, 8, 0);
        status.FontSize = 11; status.Foreground = Resource("Muted"); status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis; status.Visibility = Visibility.Collapsed;
        status.Margin = new Thickness(2, 2, 8, 0);
        var titles = new StackPanel();
        titles.Children.Add(threadTitle);
        titles.Children.Add(status);
        bar.Children.Add(titles);

        StyleIcon(historyButton, historyGlyph, "\uE81C", L.T("agent.history"));
        historyButton.Click += (_, _) => ToggleHistory();
        StyleIcon(newButton, new TextBlock(), "\uE710", L.T("agent.newChat"));
        newButton.Click += (_, _) => NewChat();
        StyleIcon(voiceButton, voiceGlyph, "\uE767", L.T("agent.voice"));
        voiceButton.Click += (_, _) => ToggleVoice();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(historyButton);
        tools.Children.Add(newButton);
        tools.Children.Add(voiceButton);
        Grid.SetColumn(tools, 1); bar.Children.Add(tools);
        return bar;
    }

    private UIElement BuildHistory()
    {
        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
        var head = new TextBlock
        {
            Text = L.T("agent.threads"), FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = Resource("Muted"), Margin = new Thickness(2, 0, 0, 8)
        };
        DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
        historySearch.FontSize = 13; historySearch.Margin = new Thickness(0, 0, 0, 8);
        historySearch.Padding = new Thickness(9, 8, 9, 8);
        historySearch.SetValue(AutomationProperties.NameProperty, L.T("agent.searchChats"));
        var searchField = AgendaVisuals.WithHint(historySearch, L.T("agent.searchChats"));
        historySearch.TextChanged += (_, _) => PaintThreads();
        DockPanel.SetDock(searchField, Dock.Top); panel.Children.Add(searchField);
        threadScroll.Content = threads; threadScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        threadScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        threadScroll.BorderThickness = new Thickness(0); threadScroll.Background = Brushes.Transparent;
        threadScroll.Padding = new Thickness(0); threadScroll.MinWidth = 0;
        panel.Children.Add(threadScroll);
        return panel;
    }

    private UIElement BuildRoom()
    {
        var room = new Grid { ClipToBounds = true, MinWidth = 0, MinHeight = 0 };
        room.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        room.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 64 });

        download.Content = L.T("agent.saveApiKey"); download.Padding = new Thickness(12, 8, 12, 8);
        download.HorizontalAlignment = HorizontalAlignment.Stretch; download.Margin = new Thickness(0, 8, 0, 0);
        download.Click += (_, _) => SaveApiKeyFromSetup();
        downloadTitle.FontSize = 12; downloadTitle.FontWeight = FontWeights.SemiBold; downloadTitle.TextWrapping = TextWrapping.Wrap;
        downloadTitle.Text = L.T("agent.apiKeyTitle");
        downloadHint.FontSize = 12; downloadHint.Foreground = Resource("Muted"); downloadHint.TextWrapping = TextWrapping.Wrap;
        downloadHint.Margin = new Thickness(0, 4, 0, 8); downloadHint.Text = L.T("agent.apiKeyHint");
        apiKeyBox.FontSize = 13; apiKeyBox.Padding = new Thickness(9, 8, 9, 8);
        apiKeyBox.ToolTip = L.T("agent.apiKeyPlaceholder");
        var setup = new StackPanel();
        setup.Children.Add(downloadTitle);
        setup.Children.Add(downloadHint);
        setup.Children.Add(AgendaVisuals.WithHint(apiKeyBox, L.T("agent.apiKeyPlaceholder")));
        setup.Children.Add(download);
        setupRow.Child = setup;
        setupRow.BorderBrush = Resource("Edge"); setupRow.BorderThickness = new Thickness(1);
        setupRow.Background = Resource("Surface"); setupRow.Padding = new Thickness(10, 10, 10, 10);
        setupRow.Margin = new Thickness(0, 0, 0, 8); setupRow.Visibility = Visibility.Collapsed;
        room.Children.Add(setupRow);

        empty.Text = L.T("agent.emptyChat"); empty.FontSize = 14; empty.Foreground = Resource("Muted");
        empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(8, 28, 8, 16);
        empty.HorizontalAlignment = HorizontalAlignment.Left; empty.Visibility = Visibility.Collapsed;
        var feed = new StackPanel(); feed.Children.Add(empty); feed.Children.Add(transcript);
        scroller.Content = feed; scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroller.BorderThickness = new Thickness(0); scroller.Padding = new Thickness(2, 4, 2, 8);
        scroller.Background = Brushes.Transparent; scroller.MinHeight = 64; scroller.MinWidth = 0;
        scroller.ClipToBounds = true;
        Grid.SetRow(scroller, 1); room.Children.Add(scroller);
        return room;
    }

    private UIElement BuildComposer()
    {
        var composer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        composer.Children.Add(mic.Strip);
        composerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        composerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerLine.MinWidth = 0;
        input.MinHeight = 40; input.MaxHeight = 110; input.AcceptsReturn = true; input.TextWrapping = TextWrapping.Wrap;
        input.MinWidth = 0; input.Margin = new Thickness(0); input.BorderThickness = new Thickness(0);
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; input.FontSize = 13; input.Tag = "agent-composer";
        input.Padding = new Thickness(8, 8, 4, 8); input.Background = Brushes.Transparent;
        input.ToolTip = L.T("agent.placeholder"); input.SetValue(AutomationProperties.NameProperty, L.T("agent.placeholder"));
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            _ = Run(); e.Handled = true;
        };
        var field = AgendaVisuals.WithHint(input, L.T("agent.placeholder"));
        field.MinWidth = 0; field.VerticalAlignment = VerticalAlignment.Center;
        composerLine.Children.Add(field);

        StyleIcon(micButton, new TextBlock(), "\uE720", L.T("agent.holdToTalk"));
        micButton.Width = 36; micButton.Height = 36; micButton.Focusable = true;
        Func<Task>? afterVoice() => owner.Settings.AgentSendVoiceOnRelease ? Run : null;
        micButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            micButton.CaptureMouse();
            mic.Begin(afterVoice());
            e.Handled = true;
        };
        micButton.PreviewMouseMove += (_, e) =>
        {
            if (!mic.Holding || !micButton.IsMouseCaptured) return;
            var point = e.GetPosition(micButton);
            if (point.X < -36 || point.Y < -36 || point.X > micButton.ActualWidth + 36 || point.Y > micButton.ActualHeight + 36)
                mic.Cancel();
        };
        micButton.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            micButton.ReleaseMouseCapture();
            await mic.End(afterVoice());
            e.Handled = true;
        };
        micButton.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { mic.Begin(afterVoice()); e.Handled = true; } };
        micButton.PreviewKeyUp += async (_, e) => { if (e.Key == Key.Space) { await mic.End(afterVoice()); e.Handled = true; } };
        Grid.SetColumn(micButton, 1); composerLine.Children.Add(micButton);

        sendGlyph.Text = "\uE724"; sendGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
        sendGlyph.FontSize = 14; sendGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        sendGlyph.VerticalAlignment = VerticalAlignment.Center; sendGlyph.TextWrapping = TextWrapping.NoWrap;
        send.Content = sendGlyph; send.Width = 36; send.Height = 36; send.Padding = new Thickness(0);
        send.Margin = new Thickness(2, 0, 0, 0); send.BorderThickness = new Thickness(0);
        send.Background = Resource("Ink"); send.Foreground = Resource("Paper");
        send.ToolTip = L.T("agent.send");
        send.Click += async (_, _) => { if (busy) Cancel(); else await Run(); };
        Grid.SetColumn(send, 2); composerLine.Children.Add(send);

        var shell = new Border
        {
            BorderBrush = Resource("Edge"), BorderThickness = new Thickness(1.5),
            Background = Resource("Surface"), Padding = new Thickness(4, 2, 4, 2), Child = composerLine
        };
        composer.Children.Add(shell);
        return composer;
    }

    private static void StyleIcon(Button button, TextBlock glyph, string symbol, string tooltip)
    {
        glyph.Text = symbol; glyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
        glyph.FontSize = 14; glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center; glyph.TextWrapping = TextWrapping.NoWrap;
        button.Content = glyph; button.Width = 32; button.Height = 32; button.Padding = new Thickness(0);
        button.Margin = new Thickness(2, 0, 0, 0); button.BorderThickness = new Thickness(0);
        button.Background = Brushes.Transparent; button.ToolTip = tooltip; button.Cursor = Cursors.Hand;
    }

    private void ToggleHistory()
    {
        ShowHistory(!historyOpen);
        if (historyOpen) historySearch.Focus();
        else input.Focus();
    }

    private void ShowHistory(bool open)
    {
        historyOpen = open;
        historyPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        historyButton.Background = open ? Resource("Ink") : Brushes.Transparent;
        historyButton.Foreground = open ? Resource("Paper") : Resource("Ink");
        if (open) PaintThreads();
    }

    private void ToggleVoice()
    {
        voiceOn = !voiceOn;
        owner.Settings.AgentVoiceEnabled = voiceOn;
        owner.SaveState();
        PaintVoice();
        if (voiceOn) _ = WarmVoice();
        else voice.Stop();
    }

    private void PaintVoice()
    {
        voiceGlyph.Text = voiceOn ? "\uE767" : "\uE74F";
        voiceButton.Background = voiceOn ? Resource("Ink") : Brushes.Transparent;
        voiceButton.Foreground = voiceOn ? Resource("Paper") : Resource("Ink");
        voiceButton.ToolTip = voiceOn ? L.T("agent.voiceOn") : L.T("agent.voiceOff");
        voiceButton.Opacity = 1;
    }

    private void PaintTitle()
    {
        threadTitle.Text = chat.Messages.Count == 0 || string.IsNullOrWhiteSpace(chat.Title)
            ? L.T("agent.newChatTitle")
            : chat.Title;
    }

    private void ShowStatus(string? text)
    {
        bool hide = string.IsNullOrWhiteSpace(text)
            || text == L.T("agent.ready")
            || text.StartsWith(L.T("agent.ready"), StringComparison.Ordinal);
        status.Text = hide ? "" : text ?? "";
        status.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PaintThreads()
    {
        PaintTitle();
        threads.Children.Clear();
        string query = historySearch.Text.Trim();
        var items = conversations.Listed
            .Where(item => item.Messages.Count > 0 || item.Id == chat.Id)
            .Where(item => query.Length == 0 || ThreadTitle(item).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (items.Count == 0)
        {
            threads.Children.Add(new TextBlock
            {
                Text = query.Length > 0 ? L.T("agent.noThreadMatches") : L.T("agent.noThreads"),
                FontSize = 13, Foreground = Resource("Muted"),
                Margin = new Thickness(2, 16, 2, 8), TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var item in items) threads.Children.Add(ThreadRow(item));
    }

    private UIElement ThreadRow(AgentChat item)
    {
        bool on = item.Id == chat.Id;
        var row = new Border
        {
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
            Background = on ? Resource("Ink") : Brushes.Transparent,
            Padding = new Thickness(10, 9, 8, 9), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = ThreadTitle(item), FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            Foreground = on ? Resource("Paper") : Resource("Ink")
        });
        text.Children.Add(new TextBlock
        {
            Text = ThreadWhen(item), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            Foreground = on ? Resource("Paper") : Resource("Muted"), Opacity = on ? .7 : 1
        });
        grid.Children.Add(text);
        var forget = new Button
        {
            Content = "×", FontSize = 14, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = on ? Resource("Paper") : Resource("Muted"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.T("common.delete")
        };
        var id = item.Id;
        forget.Click += (_, e) => { e.Handled = true; DeleteChat(id); };
        Grid.SetColumn(forget, 1); grid.Children.Add(forget);
        row.Child = grid;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            OpenChat(id);
        };
        return row;
    }

    private static string ThreadTitle(AgentChat item) =>
        string.IsNullOrWhiteSpace(item.Title) ? L.T("agent.untitled") : item.Title;

    private static string ThreadWhen(AgentChat item)
    {
        var local = item.UpdatedUtc.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm", Strings.Culture);
        if (local.Date == today.AddDays(-1)) return L.T("agent.yesterday");
        return local.ToString("d MMM", Strings.Culture);
    }

    private void RestoreChat()
    {
        transcript.Children.Clear();
        pendingPlan = null; planCard = null;
        ClearLive();
        var visible = chat.Messages.Where(message => !IsWelcome(message)).ToList();
        empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var message in visible)
        {
            if (message.Plan is { HasMutations: true } plan)
            {
                pendingPlan = plan;
                ShowPlan(plan);
                continue;
            }
            bool mine = message.Role == "you";
            AddBubble(message.Text, mine, message.Receipts, at: message.At);
        }
        ScrollEnd();
    }

    private static bool IsWelcome(AgentChatMessage message) =>
        message.Role != "you" && (message.Text == L.T("agent.welcome") || message.Text.Length == 0);

    private void NewChat()
    {
        ShowHistory(false);
        conversations.ForgetIfEmpty(chat);
        conversations.Save();
        agent.ResetSession();
        chat = conversations.StartNew();
        RestoreChat();
        PaintThreads();
        input.Focus();
    }

    private void OpenChat(Guid id)
    {
        ShowHistory(false);
        if (id == chat.Id) return;
        conversations.ForgetIfEmpty(chat);
        agent.ResetSession();
        chat = conversations.Open(id);
        RestoreChat();
        PaintThreads();
        input.Focus();
    }

    private void DeleteChat(Guid id)
    {
        conversations.Delete(id);
        agent.ResetSession();
        chat = conversations.LatestOrNew();
        RestoreChat();
        PaintThreads();
    }

    private void PaintApiSetup()
    {
        progress.Visibility = Visibility.Collapsed;
        if (DeepSeekAgentModel.IsConfigured(owner.Settings))
        {
            setupRow.Visibility = Visibility.Collapsed;
            ShowStatus(null);
            return;
        }
        setupRow.Visibility = Visibility.Visible;
        downloadTitle.Text = L.T("agent.apiKeyTitle");
        downloadHint.Text = L.T("agent.apiKeyHint");
        download.Content = L.T("agent.saveApiKey");
        download.IsEnabled = true;
        apiKeyBox.Text = "";
        ShowStatus(null);
    }

    private void SaveApiKeyFromSetup()
    {
        DeepSeekAgentModel.SaveKey(owner.Settings, apiKeyBox.Text);
        owner.SaveState();
        agent.ResetSession();
        if (!DeepSeekAgentModel.IsConfigured(owner.Settings))
        {
            ShowStatus(L.T("agent.apiKeyMissing"));
            return;
        }
        setupRow.Visibility = Visibility.Collapsed;
        ShowStatus(L.T("agent.apiKeySaved"));
        _ = WarmVoice();
    }

    private async Task Run()
    {
        if (busy || mic.Holding) return;
        var request = input.Text.Trim(); if (request.Length == 0) return;
        if (pendingPlan is not null && AgentIntent.IsPlanAffirmation(request))
        {
            input.Clear();
            AddBubble(request, true);
            chat.Add("you", request); conversations.Touch(chat); PaintThreads();
            await ExecutePending();
            return;
        }
        if (pendingPlan is not null && AgentIntent.IsPlanRejection(request))
        {
            input.Clear();
            AddBubble(request, true);
            chat.Add("you", request); conversations.Touch(chat); PaintThreads();
            CancelPlan();
            return;
        }
        if (!DeepSeekAgentModel.IsConfigured(owner.Settings))
        {
            PaintApiSetup();
            ShowStatus(L.T("agent.apiKeyMissing"));
            return;
        }
        input.Clear();
        empty.Visibility = Visibility.Collapsed;
        AddBubble(request, true);
        chat.Add("you", request); conversations.Touch(chat); PaintThreads();
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true);
        liveOpen = true;
        var notices = new Progress<AgentNotice>(notice =>
        {
            // BeginInvoke: never block the inference thread waiting on the UI (Invoke deadlocks / freezes "Pensando").
            _ = owner.Dispatcher.BeginInvoke(() =>
            {
                ShowStatus(notice.Kind switch
                {
                    AgentEventKind.Reading => notice.Text,
                    AgentEventKind.PlanReady => L.T("agent.planReady"),
                    AgentEventKind.Executing => notice.Text,
                    AgentEventKind.AnswerDelta => L.T("agent.writing"),
                    AgentEventKind.Done => null,
                    AgentEventKind.Failed => notice.Text,
                    _ => notice.Text.Length > 0 ? notice.Text : L.T("agent.liveWait")
                });
                if (notice.Kind == AgentEventKind.AnswerDelta && notice.Text.Trim().Length > 0)
                    ShowLive(notice.Text, true);
                else if (notice.Kind == AgentEventKind.Interpreting && (liveBody is null || IsWaitLive()))
                    ShowLive(PulseWait(notice.Text), true);
            });
        });
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        heartbeat.Tick += (_, _) =>
        {
            if (!busy || !liveOpen) return;
            if (liveBody is null || IsWaitLive()) ShowLive(PulseWait(L.T("agent.liveWait")), true);
        };
        heartbeat.Start();
        try
        {
            var result = await agent.InterpretAsync(request, chat.Messages, notices, activeOperation.Token);
            if (result.NeedsApproval && result.Plan is { } plan)
            {
                ClearLive();
                bool spanish = Strings.Culture.TwoLetterISOLanguageName == "es";
                // Never voice a "done" claim while the UI still waits for EJECUTAR.
                plan.Message = AgentReply.Pending(plan, spanish);
                pendingPlan = plan;
                ShowPlan(plan);
                ShowStatus(L.T("agent.needDecision"));
                chat.Add("plan", plan.Message, plan: plan); conversations.Touch(chat);
                await Speak(plan.Message, activeOperation.Token);
            }
            else
            {
                ClearLive();
                string spoken = AgentReply.After(result.Message, result.Receipts, Strings.Culture.TwoLetterISOLanguageName == "es");
                AddBubble(spoken, false, result.Receipts);
                chat.Add("agent", spoken, result.Receipts); conversations.Touch(chat);
                ShowStatus(null);
                if (ReferenceEquals(operation, activeOperation)) SetBusy(false);
                await Speak(spoken, activeOperation.Token);
            }
        }
        catch (OperationCanceledException) { ClearLive(); ShowStatus(L.T("agent.cancelled")); }
        catch (Exception ex)
        {
            ClearLive();
            AddError(L.T("agent.error", ex.Message), ex.ToString());
            ShowStatus(null);
            try { File.AppendAllText(Path.Combine(owner.Store.DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} agent-deepseek {ex}\n"); } catch { }
        }
        finally
        {
            heartbeat.Stop();
            ClearLive();
            if (ReferenceEquals(operation, activeOperation))
            {
                operation = null;
                SetBusy(false);
            }
            activeOperation.Dispose();
            PaintThreads();
            input.Focus();
            if (closeWhenIdle) _ = owner.Dispatcher.BeginInvoke(RequestClose);
        }
    }

    private void ShowPlan(AgentPlan plan)
    {
        if (planCard is not null) transcript.Children.Remove(planCard);
        empty.Visibility = Visibility.Collapsed;
        var card = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(2), Background = Resource("Surface"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 48, 12) };
        var stack = new StackPanel();
        stack.Children.Add(Label(L.T("agent.understood")));
        stack.Children.Add(new TextBlock { Text = plan.Understood, FontSize = 13, Margin = new Thickness(0, 2, 0, 10), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(Label(L.T("agent.plan")));
        foreach (var step in plan.Steps)
            stack.Children.Add(new TextBlock { Text = "→  " + (step.Label.Length > 0 ? step.Label : step.Tool), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
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
        var activeOperation = new CancellationTokenSource(); operation = activeOperation; SetBusy(true); ShowStatus(L.T("agent.executing"));
        try
        {
            var notices = new Progress<AgentNotice>(notice => _ = owner.Dispatcher.BeginInvoke(() => ShowStatus(notice.Text)));
            var result = await agent.ExecuteAsync(plan, notices, activeOperation.Token);
            string spoken = AgentReply.After(result.Message, result.Receipts, Strings.Culture.TwoLetterISOLanguageName == "es");
            AddBubble(spoken, false, result.Receipts);
            chat.Add("agent", spoken, result.Receipts); conversations.Touch(chat); PaintThreads();
            ShowStatus(null);
            if (ReferenceEquals(operation, activeOperation)) SetBusy(false);
            await Speak(spoken, activeOperation.Token);
        }
        catch (OperationCanceledException) { ShowStatus(L.T("agent.cancelled")); }
        catch (Exception ex) { AddError(L.T("agent.error", ex.Message), ex.ToString()); }
        finally
        {
            if (ReferenceEquals(operation, activeOperation))
            {
                operation = null;
                SetBusy(false);
            }
            activeOperation.Dispose();
            input.Focus();
        }
    }

    private void CancelPlan()
    {
        pendingPlan = null;
        if (planCard is not null) { transcript.Children.Remove(planCard); planCard = null; }
        AddBubble(L.T("agent.planCancelled"), false);
        chat.Add("agent", L.T("agent.planCancelled")); conversations.Touch(chat); PaintThreads();
        ShowStatus(null);
    }

    private async Task Speak(string message, CancellationToken cancellationToken)
    {
        bool spanish = Strings.Culture.TwoLetterISOLanguageName == "es";
        string spoken = AgentSpeech.Speakable(message, spanish);
        if (!voiceOn || string.IsNullOrWhiteSpace(spoken)) return;
        ShowStatus(L.T("agent.voicePreparing"));
        var voiceProgress = new Progress<double>(value =>
            ShowStatus(value >= .99 ? L.T("agent.voiceSpeaking")
                : value >= .5 ? L.T("agent.voiceRender")
                : L.T("agent.voicePreparing")));
        await voice.SpeakAsync(spoken, Strings.Culture.TwoLetterISOLanguageName, owner.Settings.AgentVoiceSpeed, voiceProgress, cancellationToken);
        ShowStatus(null);
    }

    private async Task WarmVoice()
    {
        if (!voiceOn) return;
        try { await voice.WarmAsync(Strings.Culture.TwoLetterISOLanguageName, CancellationToken.None); }
        catch { }
    }

    private void Cancel() { voice.Stop(); mic.Cancel(); operation?.Cancel(); }

    public void RequestClose()
    {
        conversations.ForgetIfEmpty(chat);
        conversations.Save();
        if (busy || operation is not null)
        {
            closeWhenIdle = true;
            Cancel();
            ShowStatus(L.T("agent.closing"));
            return;
        }
        dismiss();
    }

    public void Shutdown()
    {
        Cancel();
        conversations.ForgetIfEmpty(chat);
        conversations.Save();
        try { agent.ResetSession(); } catch { }
        try { mic.Dispose(); } catch { }
        try { voice.Dispose(); } catch { }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        sendGlyph.Text = value ? "\uE711" : "\uE724";
        send.Content = sendGlyph;
        send.ToolTip = value ? L.T("agent.cancel") : L.T("agent.send");
        send.Background = value ? Resource("Surface") : Resource("Ink");
        send.Foreground = value ? Resource("Ink") : Resource("Paper");
        send.BorderThickness = value ? new Thickness(1.5) : new Thickness(0);
        micButton.IsEnabled = !value;
    }

    private void ShowLive(string text, bool caret)
    {
        if (!liveOpen) return;
        empty.Visibility = Visibility.Collapsed;
        string shown = string.IsNullOrWhiteSpace(text) ? L.T("agent.thinking") : text.Trim();
        if (caret) shown += " ▍";
        if (liveBody is null)
        {
            if (liveCard is not null) transcript.Children.Remove(liveCard);
            liveCard = AddBubble(shown, false);
            liveBody = FindBubbleBody(liveCard);
        }
        else liveBody.Text = shown;
        ScrollEnd();
    }

    private bool IsWaitLive()
    {
        if (liveBody is null) return true;
        string value = liveBody.Text.Replace(" ▍", "").Trim();
        return value.Length == 0 || value == L.T("agent.thinking") || value.StartsWith(L.T("agent.liveWait").TrimEnd('…', '.'), StringComparison.Ordinal);
    }

    private static string PulseWait(string text)
    {
        string baseText = string.IsNullOrWhiteSpace(text) ? L.T("agent.liveWait") : text.Trim().TrimEnd('…', '.');
        return baseText + new string('.', 1 + (Environment.TickCount / 350 % 3));
    }

    private void ClearLive()
    {
        liveOpen = false;
        if (liveCard is not null) transcript.Children.Remove(liveCard);
        liveCard = null;
        liveBody = null;
    }

    private static TextBlock? FindBubbleBody(UIElement card)
    {
        if (card is Grid wrap)
        {
            var border = wrap.Children.OfType<Border>().FirstOrDefault();
            if (border?.Child is StackPanel stack)
                return stack.Children.OfType<TextBlock>().FirstOrDefault(block => Equals(block.Tag, "body"))
                    ?? stack.Children.OfType<TextBlock>().FirstOrDefault();
        }
        return null;
    }

    private UIElement AddBubble(string message, bool mine, IReadOnlyList<AgentActionReceipt>? receipts = null, DateTime? at = null)
    {
        empty.Visibility = Visibility.Collapsed;
        var wrap = new Grid { Margin = new Thickness(mine ? 40 : 0, 0, mine ? 0 : 40, 10) };
        var card = new Border
        {
            BorderBrush = Resource("Edge"), BorderThickness = new Thickness(mine ? 0 : 1),
            Background = mine ? Resource("Ink") : Resource("Surface"),
            Padding = new Thickness(12, 9, 12, 9),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Tag = "body",
            Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = mine ? Resource("Paper") : Resource("Ink")
        });
        stack.Children.Add(new TextBlock
        {
            Text = (at ?? DateTime.Now).ToString("HH:mm", Strings.Culture), FontSize = 10, Margin = new Thickness(0, 5, 0, 0),
            Foreground = mine ? Resource("Paper") : Resource("Muted"), Opacity = .55,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        });
        if (receipts is { Count: > 0 })
        {
            var shown = AgentReply.Visible(receipts).ToList();
            if (shown.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = L.T("agent.actions"), FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, FontSize = 8,
                    Foreground = mine ? Resource("Paper") : Resource("Muted"), Margin = new Thickness(0, 10, 0, 2), Opacity = .75
                });
                foreach (var receipt in shown)
                {
                    var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = "✓  " + receipt.Summary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = mine ? Resource("Paper") : Resource("Ink") });
                    if (receipt.CanUndo)
                    {
                        var undo = new Button { Content = L.T("agent.undo"), FontSize = 8, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) };
                        var copy = receipt;
                        undo.Click += async (_, _) =>
                        {
                            var undone = await agent.UndoAsync(copy, CancellationToken.None);
                            AddBubble(undone.Message, false, undone.Receipts);
                            chat.Add("agent", undone.Message, undone.Receipts); conversations.Touch(chat); PaintThreads();
                        };
                        Grid.SetColumn(undo, 1); row.Children.Add(undo);
                    }
                    stack.Children.Add(row);
                }
            }
        }
        card.Child = stack; wrap.Children.Add(card); transcript.Children.Add(wrap); ScrollEnd();
        return wrap;
    }

    private void AddError(string message, string details)
    {
        var card = new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(2), Background = Resource("Surface"), Padding = new Thickness(13, 10, 13, 11), Margin = new Thickness(0, 0, 56, 12) };
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
        chat.Add("error", message); conversations.Touch(chat);
        ScrollEnd();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, FontSize = 8, Foreground = Resource("Muted"), Margin = new Thickness(0, 8, 0, 2) };
    private void ScrollEnd() => owner.Dispatcher.BeginInvoke(() => scroller.ScrollToEnd());
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
