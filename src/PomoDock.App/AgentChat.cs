using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;
using PomoDock.Core.Agent;

namespace PomoDock.App;

/// <summary>
/// Agent chat panel: streaming reply lane, activity, plan approve/edit/cancel, receipts+undo,
/// and hold-to-talk voice notes (Fases 1–5 UI surface).
/// </summary>
internal sealed class AgentChat : IDisposable
{
    private readonly MainWindow owner;
    private readonly AgentHostAdapter host;
    private readonly AgentRuntime runtime;
    private readonly Popup popup;
    private readonly Border shell;
    private readonly StackPanel thread;
    private readonly ScrollViewer scroller;
    private readonly TextBox input;
    private readonly StackPanel activityLane;
    private readonly StackPanel planLane;
    private readonly TextBlock preview;
    private readonly Button mic;
    private readonly Button send;
    private AgentConversationBook book;
    private AgentConversation conversation;
    private AgentPlan? pendingPlan;
    private CancellationTokenSource? turnLife;
    private AgentVoiceNote? voice;
    private bool open;

    private AgentChat(MainWindow owner)
    {
        this.owner = owner;
        host = new AgentHostAdapter(owner);
        runtime = new AgentRuntime(host);
        runtime.Activity += OnActivity;
        book = AgentConversations.Load(owner.Store);
        conversation = AgentConversations.EnsureActive(book);

        thread = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        scroller = new ScrollViewer
        {
            Content = thread, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 360
        };
        activityLane = new StackPanel { Margin = new Thickness(10, 0, 10, 6) };
        planLane = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };
        preview = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 6),
            Visibility = Visibility.Collapsed
        };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        input = new TextBox
        {
            MinHeight = 36, MaxHeight = 90, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 6, 0)
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                _ = SubmitAsync();
            }
        };

        send = new Button { Content = "→", Width = 36, Height = 36, FontSize = 16, Padding = new Thickness(0) };
        send.Click += async (_, _) => await SubmitAsync();
        mic = new Button { Content = "🎤", Width = 36, Height = 36, FontSize = 13, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0) };
        mic.PreviewMouseLeftButtonDown += MicDown;
        mic.PreviewMouseLeftButtonUp += MicUp;
        mic.PreviewMouseMove += MicMove;

        var composer = new Grid { Margin = new Thickness(10, 0, 10, 10) };
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(input, 0); Grid.SetColumn(mic, 1); Grid.SetColumn(send, 2);
        composer.Children.Add(input); composer.Children.Add(mic); composer.Children.Add(send);

        var header = new Grid { Margin = new Thickness(12, 10, 8, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = L.T("agent.title"), FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ChromeInk");
        var neu = new Button { Content = L.T("agent.new"), FontSize = 10, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 4, 0) };
        neu.Click += (_, _) => NewConversation();
        var close = new Button { Content = "×", FontSize = 14, Padding = new Thickness(10, 2, 10, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        close.SetResourceReference(Control.ForegroundProperty, "ChromeInk");
        close.Click += (_, _) => Hide();
        Grid.SetColumn(neu, 1); Grid.SetColumn(close, 2);
        var headBar = new Border { Background = (Brush)owner.FindResource("Chrome"), Child = header };
        header.Children.Add(title); header.Children.Add(neu); header.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(headBar);
        body.Children.Add(scroller);
        body.Children.Add(activityLane);
        body.Children.Add(planLane);
        body.Children.Add(preview);
        body.Children.Add(composer);

        shell = new Border
        {
            Width = 420, MaxHeight = 640, BorderThickness = new Thickness(2), Child = body,
            SnapsToDevicePixels = true
        };
        shell.SetResourceReference(Border.BorderBrushProperty, "Edge");
        shell.SetResourceReference(Border.BackgroundProperty, "Surface");

        popup = new Popup
        {
            Child = shell, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade
        };
        popup.Closed += (_, _) => open = false;

        ReloadThread();
    }

    public static AgentChat Attach(MainWindow owner) => new(owner);

    public bool IsOpen => open && popup.IsOpen;

    public void Toggle(FrameworkElement anchor)
    {
        if (IsOpen) { Hide(); return; }
        popup.PlacementTarget = anchor;
        popup.HorizontalOffset = -360;
        popup.VerticalOffset = 6;
        popup.IsOpen = true;
        open = true;
        input.Focus();
    }

    public void Hide()
    {
        popup.IsOpen = false;
        open = false;
        voice?.Cancel();
    }

    public void RefreshLanguage()
    {
        // Titles rebuild on next open / render of plan buttons.
    }

    private void NewConversation()
    {
        conversation = AgentConversations.StartNew(book);
        Persist();
        ReloadThread();
        planLane.Children.Clear();
        activityLane.Children.Clear();
        pendingPlan = null;
    }

    private void ReloadThread()
    {
        thread.Children.Clear();
        foreach (var message in conversation.Messages.Where(m => m.Role is "user" or "assistant"))
            thread.Children.Add(Bubble(message.Role, message.Text));
        scroller.ScrollToEnd();
    }

    private static Border Bubble(string role, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var border = new Border
        {
            Child = block, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 340, BorderThickness = new Thickness(1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Edge");
        border.SetResourceReference(Border.BackgroundProperty, role == "user" ? "Raised" : "Paper");
        return border;
    }

    private async Task SubmitAsync(string? forced = null)
    {
        var text = (forced ?? input.Text).Trim();
        if (text.Length == 0) return;
        input.Text = "";
        preview.Visibility = Visibility.Collapsed;
        preview.Text = "";

        AgentConversations.Append(conversation, new AgentMessage { Role = "user", Text = text });
        Persist();
        thread.Children.Add(Bubble("user", text));
        scroller.ScrollToEnd();
        activityLane.Children.Clear();
        planLane.Children.Clear();

        turnLife?.Cancel();
        turnLife = new CancellationTokenSource();
        var ct = turnLife.Token;

        try
        {
            var plan = runtime.Plan(text);
            pendingPlan = plan;

            if (plan.Steps.Count == 0)
            {
                var reply = await CompleteWithModelAsync(text, ct).ConfigureAwait(true);
                AppendAssistant(reply);
                return;
            }

            RenderPlan(plan);
            if (!plan.NeedsApproval)
            {
                runtime.Approve(plan);
                await RunPlanAsync(plan).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendAssistant(L.T("agent.error", ex.Message));
        }
    }

    private void RenderPlan(AgentPlan plan)
    {
        planLane.Children.Clear();
        var lead = new TextBlock
        {
            Text = L.T("agent.planLead", plan.Understood), FontSize = 11, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        };
        planLane.Children.Add(lead);
        foreach (var step in plan.Steps)
        {
            var row = new CheckBox
            {
                Content = $"{step.Label} · {step.Kind}", IsChecked = step.Enabled, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4), Tag = step.Id
            };
            planLane.Children.Add(row);
        }

        if (!plan.NeedsApproval) return;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var approve = new Button { Content = L.T("agent.approve"), FontSize = 11, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 6, 0) };
        approve.SetResourceReference(Control.BackgroundProperty, "Ink");
        approve.SetResourceReference(Control.ForegroundProperty, "Paper");
        approve.Click += async (_, _) =>
        {
            ApplyEdits(plan);
            runtime.Approve(plan);
            await RunPlanAsync(plan).ConfigureAwait(true);
        };
        var cancel = new Button { Content = L.T("agent.cancel"), FontSize = 11, Padding = new Thickness(12, 7, 12, 7) };
        cancel.Click += (_, _) =>
        {
            runtime.Cancel(plan);
            pendingPlan = null;
            planLane.Children.Clear();
            AppendAssistant(L.T("agent.cancelled"));
        };
        actions.Children.Add(approve);
        actions.Children.Add(cancel);
        planLane.Children.Add(actions);
    }

    private void ApplyEdits(AgentPlan plan)
    {
        var enabled = planLane.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true && box.Tag is string)
            .Select(box => (string)box.Tag!)
            .ToList();
        runtime.EditPlan(plan, enabled);
    }

    private async Task RunPlanAsync(AgentPlan plan)
    {
        var results = await Task.Run(() => runtime.Execute(plan)).ConfigureAwait(true);
        planLane.Children.Clear();
        var reply = string.Join("\n", results.Select(r => r.Summary));
        if (reply.Length == 0) reply = L.T("agent.emptyResult");
        AppendAssistant(reply, plan, results.Select(r => r.Receipt).Where(r => r is not null).Cast<AgentReceipt>().ToList());
        pendingPlan = null;
    }

    private async Task<string> CompleteWithModelAsync(string userText, CancellationToken ct)
    {
        owner.Settings.Agent.Validate();
        if (!owner.Settings.Agent.Enabled)
            return L.T("agent.disabled");

        try
        {
            using var llm = new AgentLlmClient(owner.Settings.Agent);
            var messages = new List<(string Role, string Content)>
            {
                ("system", AgentPrompt.System(host.Memory))
            };
            foreach (var msg in conversation.Messages.TakeLast(owner.Settings.Agent.MaxContextMessages))
            {
                if (msg.Role is "user" or "assistant")
                    messages.Add((msg.Role, msg.Text));
            }
            if (messages.All(m => m.Content != userText) || messages[^1].Content != userText)
                messages.Add(("user", userText));

            var bubble = Bubble("assistant", "");
            thread.Children.Add(bubble);
            scroller.ScrollToEnd();
            var block = (TextBlock)bubble.Child!;
            var assembled = new System.Text.StringBuilder();
            await foreach (var chunk in llm.StreamAsync(messages, ct).ConfigureAwait(true))
            {
                assembled.Append(chunk);
                block.Text = assembled.ToString();
                scroller.ScrollToEnd();
            }
            var text = assembled.ToString().Trim();
            if (text.Length == 0)
                text = await llm.CompleteAsync(messages, ct).ConfigureAwait(true);
            // Replace streaming bubble with persisted message path
            thread.Children.Remove(bubble);
            return string.IsNullOrWhiteSpace(text) ? L.T("agent.emptyResult") : text;
        }
        catch (Exception ex)
        {
            return L.T("agent.llmFailed", ex.Message);
        }
    }

    private void AppendAssistant(string text, AgentPlan? plan = null, List<AgentReceipt>? receipts = null)
    {
        var message = new AgentMessage
        {
            Role = "assistant",
            Text = text,
            Plan = plan,
            Receipts = receipts ?? []
        };
        AgentConversations.Append(conversation, message);
        Persist();
        thread.Children.Add(Bubble("assistant", text));
        if (receipts is { Count: > 0 })
        {
            foreach (var receipt in receipts.Where(r => r.CanUndo))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row.Children.Add(new TextBlock
                {
                    Text = "↩ " + receipt.Summary, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 280
                });
                var undo = new Button { Content = L.T("agent.undo"), FontSize = 10, Padding = new Thickness(8, 4, 8, 4), Tag = receipt.Id };
                undo.Click += (_, _) =>
                {
                    if (runtime.Undo((string)undo.Tag!, out var msg))
                    {
                        undo.IsEnabled = false;
                        OnActivity(new AgentActivity { Kind = AgentActivityKind.Undo, Text = msg, ReceiptId = receipt.Id });
                    }
                };
                row.Children.Add(undo);
                thread.Children.Add(row);
            }
        }
        scroller.ScrollToEnd();
    }

    private void OnActivity(AgentActivity activity)
    {
        owner.Dispatcher.BeginInvoke(() =>
        {
            var line = new TextBlock
            {
                Text = $"· {Label(activity.Kind)} — {activity.Text}",
                FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2)
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            activityLane.Children.Add(line);
            while (activityLane.Children.Count > 8) activityLane.Children.RemoveAt(0);
        });
    }

    private static string Label(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.Understood => L.T("agent.actUnderstood"),
        AgentActivityKind.Consulting => L.T("agent.actConsulting"),
        AgentActivityKind.Planning => L.T("agent.actPlanning"),
        AgentActivityKind.AwaitingApproval => L.T("agent.actAwaiting"),
        AgentActivityKind.Executing => L.T("agent.actExecuting"),
        AgentActivityKind.Receipt => L.T("agent.actReceipt"),
        AgentActivityKind.Undo => L.T("agent.actUndo"),
        AgentActivityKind.Blocked => L.T("agent.actBlocked"),
        AgentActivityKind.Error => L.T("agent.actError"),
        _ => L.T("agent.actReply")
    };

    private void Persist()
    {
        conversation.UpdatedUtc = DateTimeOffset.UtcNow;
        AgentConversations.Save(owner.Store, book);
    }

    // ---- voice note: hold / release / drag-cancel ----

    private Point micOrigin;
    private bool micArmed;

    private void MicDown(object sender, MouseButtonEventArgs e)
    {
        micArmed = true;
        micOrigin = e.GetPosition(mic);
        mic.CaptureMouse();
        voice?.Dispose();
        voice = new AgentVoiceNote(owner, text =>
        {
            preview.Text = text;
            preview.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        });
        voice.Start();
        mic.Content = "■";
        e.Handled = true;
    }

    private void MicMove(object sender, MouseEventArgs e)
    {
        if (!micArmed || voice is null) return;
        var pos = e.GetPosition(mic);
        if (Math.Abs(pos.X - micOrigin.X) > 48 || Math.Abs(pos.Y - micOrigin.Y) > 48)
        {
            voice.Cancel();
            voice = null;
            micArmed = false;
            mic.ReleaseMouseCapture();
            mic.Content = "🎤";
            preview.Text = L.T("agent.voiceCancelled");
            preview.Visibility = Visibility.Visible;
        }
    }

    private async void MicUp(object sender, MouseButtonEventArgs e)
    {
        if (!micArmed) return;
        micArmed = false;
        mic.ReleaseMouseCapture();
        mic.Content = "🎤";
        if (voice is null) return;
        var text = await voice.StopAndTranscribeAsync().ConfigureAwait(true);
        voice.Dispose();
        voice = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            preview.Text = L.T("agent.voiceEmpty");
            preview.Visibility = Visibility.Visible;
            return;
        }
        preview.Text = text;
        preview.Visibility = Visibility.Visible;
        await SubmitAsync(text).ConfigureAwait(true);
    }

    public void Dispose()
    {
        turnLife?.Cancel();
        voice?.Dispose();
        runtime.Activity -= OnActivity;
        popup.IsOpen = false;
    }
}
