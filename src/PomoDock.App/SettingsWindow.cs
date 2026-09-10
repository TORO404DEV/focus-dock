using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The control panel. Settings are grouped into four rooms instead of one long list, every change
/// lands the moment it is made, and the header shows what all that focus has added up to.
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>Everything this panel can touch, kept so a whole visit can be undone at once.</summary>
    private sealed record Snapshot(
        int Focus, int Short, int Long, int Interval, int Goal, int Noise, int Repeats,
        bool AutoBreak, bool AutoFocus, bool Sound, bool ButtonSounds, bool WhiteNoise, bool Alarm,
        bool Dark, bool ReduceMotion, bool AlwaysOnTop, bool TimerAtBottom,
        string FocusColor, string ShortColor, string LongColor, string Accent,
        string FocusEnd, string BreakEnd, string Reminder, string Click, string Ambient, int AlarmVolume, int EffectsVolume)
    {
        public static Snapshot Of(Settings s) => new(
            s.FocusMinutes, s.ShortMinutes, s.LongMinutes, s.LongInterval, s.DailyGoalMinutes, s.WhiteNoiseVolume, s.AlarmRepeats,
            s.AutoBreak, s.AutoFocus, s.Sound, s.ButtonSounds, s.WhiteNoise, s.AlarmEnabled,
            s.Dark, s.ReduceMotion, s.AlwaysOnTop, s.TimerAtBottom,
            s.FocusColor, s.ShortBreakColor, s.LongBreakColor, s.AccentColor,
            s.FocusEndSound, s.BreakEndSound, s.ReminderSound, s.ClickSound, s.AmbientSound, s.AlarmVolume, s.EffectsVolume);

        public void Restore(Settings s)
        {
            s.FocusMinutes = Focus; s.ShortMinutes = Short; s.LongMinutes = Long; s.LongInterval = Interval;
            s.DailyGoalMinutes = Goal; s.WhiteNoiseVolume = Noise; s.AlarmRepeats = Repeats;
            s.AutoBreak = AutoBreak; s.AutoFocus = AutoFocus; s.Sound = Sound; s.ButtonSounds = ButtonSounds;
            s.WhiteNoise = WhiteNoise; s.AlarmEnabled = Alarm; s.Dark = Dark; s.ReduceMotion = ReduceMotion;
            s.AlwaysOnTop = AlwaysOnTop; s.TimerAtBottom = TimerAtBottom;
            s.FocusColor = FocusColor; s.ShortBreakColor = ShortColor; s.LongBreakColor = LongColor; s.AccentColor = Accent;
            s.FocusEndSound = FocusEnd; s.BreakEndSound = BreakEnd; s.ReminderSound = Reminder; s.ClickSound = Click;
            s.AmbientSound = Ambient; s.AlarmVolume = AlarmVolume; s.EffectsVolume = EffectsVolume;
        }
    }

    /// <summary>Rhythms worth having a name. Anything else is simply "custom".</summary>
    private static (string Name, string Detail, string Story, int Focus, int Short, int Long, int Interval)[] Rhythms =>
    [
        (L.T("settings.rhythmClassic"), "25 · 5 · 15", L.T("settings.rhythmClassicStory"), 25, 5, 15, 4),
        (L.T("settings.rhythmDeep"), "50 · 10 · 20", L.T("settings.rhythmDeepStory"), 50, 10, 20, 3),
        (L.T("settings.rhythmSprint"), "15 · 3 · 10", L.T("settings.rhythmSprintStory"), 15, 3, 10, 4),
        (L.T("settings.rhythmMarathon"), "90 · 20 · 30", L.T("settings.rhythmMarathonStory"), 90, 20, 30, 2)
    ];

    /// <summary>Soft tones that keep the paper look when they fill the whole timer frame.</summary>
    private static readonly string[] Tones =
        ["#D7D9D1", "#BFD7EA", "#F3C4A8", "#DCCFE6", "#CFE4D3", "#EBCFD0", "#E8DFAE", "#C9CBC4"];

    private readonly MainWindow owner;
    private readonly Settings settings;
    private readonly Snapshot opening;
    private readonly StackPanel body = new();
    private readonly StackPanel tabs = new();
    private readonly TextBlock rankTitle = new();
    private readonly TextBlock rankDetail = new();
    private readonly TextBlock statusLine = new();
    private readonly Grid xpTrack = new();
    private readonly Border xpFill = new();
    private readonly FocusRank rank;
    private string section = "rhythm";

    public SettingsWindow(MainWindow owner)
    {
        this.owner = owner;
        settings = owner.Settings;
        opening = Snapshot.Of(settings);
        rank = FocusProfile.Of(owner.Store.Sessions());

        Owner = owner; Title = L.T("settings.title");
        Width = 660; Height = 780; MinWidth = 460; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        // A window of our own gets no Window style, so its content would inherit black text.
        SetResourceReference(ForegroundProperty, "Ink");
        // Embedded app windows own native surfaces; a panel below one of them is unusable.
        Topmost = true; ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        root.Children.Add(Header());

        tabs.Orientation = Orientation.Horizontal;
        tabs.Margin = new Thickness(0, 14, 0, 12);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        // Room for the scrollbar, so no row ends up sliding underneath it.
        body.Margin = new Thickness(0, 0, 8, 0);
        var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 2);
        root.Children.Add(scroller);

        root.Children.Add(Footer());

        Render();
        Dialogs.Modalize(this);
    }

    // ---------------------------------------------------------------- chrome

    private UIElement Header()
    {
        var head = new StackPanel();
        rankTitle.FontFamily = new FontFamily("Consolas");
        rankTitle.FontSize = 22;
        rankTitle.FontWeight = FontWeights.Black;
        head.Children.Add(rankTitle);

        rankDetail.FontSize = 11;
        rankDetail.Foreground = AgendaVisuals.Resource("Muted");
        rankDetail.Margin = new Thickness(0, 2, 0, 9);
        rankDetail.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(rankDetail);

        xpTrack.Height = 8;
        xpTrack.Background = AgendaVisuals.Fade("Line", 45);
        xpFill.Background = AgendaVisuals.Resource("Ink");
        xpFill.HorizontalAlignment = HorizontalAlignment.Left;
        xpTrack.Children.Add(xpFill);
        xpTrack.SizeChanged += (_, _) => PaintRank();
        head.Children.Add(xpTrack);
        return head;
    }

    private void PaintRank() => xpFill.Width = Math.Max(0, xpTrack.ActualWidth * rank.Share);

    private UIElement Footer()
    {
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusLine.FontSize = 10;
        statusLine.Foreground = AgendaVisuals.Resource("Muted");
        statusLine.VerticalAlignment = VerticalAlignment.Center;
        statusLine.TextWrapping = TextWrapping.Wrap;
        statusLine.Margin = new Thickness(0, 0, 12, 0);
        statusLine.Text = L.T("settings.footerHint");
        footer.Children.Add(statusLine);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons, 1);
        var undo = new Button { Content = L.T("settings.undo"), FontSize = 11, Padding = new Thickness(13, 9, 13, 9) };
        undo.ToolTip = L.T("settings.undoTip");
        undo.Click += (_, _) => RestoreOpening();
        buttons.Children.Add(undo);
        var done = new Button
        {
            Content = L.T("common.done"), FontSize = 12, Padding = new Thickness(20, 9, 20, 9), Margin = new Thickness(0),
            Background = AgendaVisuals.Resource("Ink"), Foreground = AgendaVisuals.Resource("Paper"), IsDefault = true
        };
        done.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(done);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 3);
        return footer;
    }

    // ---------------------------------------------------------------- render

    /// <summary>Applies the change and redraws, so what is on screen is always what is stored.</summary>
    private void Changed()
    {
        owner.ApplyLiveSettings();
        Render();
    }

    private void Render()
    {
        string next = FocusProfile.NextName(rank);
        rankTitle.Text = L.T("settings.rank", rank.Level.ToString("00", CultureInfo.InvariantCulture), rank.Name);
        rankDetail.Text = rank.IsHighest
            ? L.T("settings.rankHighest", rank.Hours.ToString("0.#", Strings.Culture), rank.Detail)
            : L.T("settings.rankProgress", rank.Hours.ToString("0.#", Strings.Culture), rank.ToNext.ToString("0.#", Strings.Culture), next);
        PaintRank();

        tabs.Children.Clear();
        foreach (var (key, label) in new[] { ("rhythm", L.T("settings.tabRhythm")), ("sound", L.T("settings.tabSound")), ("look", L.T("settings.tabLook")), ("space", L.T("settings.tabSpace")) })
        {
            bool active = section == key;
            var tab = new Button
            {
                Content = label, FontSize = 10, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 0, 5, 0),
                Background = active ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = active ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
            };
            tab.Click += (_, _) => { section = key; Render(); };
            tabs.Children.Add(tab);
        }

        body.Children.Clear();
        switch (section)
        {
            case "sound": BuildSound(); break;
            case "look": BuildLook(); break;
            case "space": BuildSpace(); break;
            default: BuildRhythm(); break;
        }
    }

    // ---------------------------------------------------------------- sections

    private void BuildRhythm()
    {
        body.Children.Add(Lead(L.T("settings.rhythmLead"), L.T("settings.rhythmHelp")));

        var active = Rhythms.FirstOrDefault(preset =>
            preset.Focus == settings.FocusMinutes && preset.Short == settings.ShortMinutes &&
            preset.Long == settings.LongMinutes && preset.Interval == settings.LongInterval);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var preset in Rhythms)
        {
            bool chosen = preset.Name == active.Name;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = preset.Name, FontSize = 13, FontWeight = FontWeights.Black });
            content.Children.Add(new TextBlock
            {
                Text = preset.Detail, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 3, 0, 4),
                Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
            });
            content.Children.Add(new TextBlock
            {
                Text = preset.Story, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Muted"), Opacity = chosen ? 0.85 : 1
            });
            var card = new Button
            {
                Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 12, 14, 12), MinHeight = 96,
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                Background = chosen ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink"),
                BorderThickness = new Thickness(chosen ? 2.5 : 1.5)
            };
            card.SetValue(AutomationProperties.NameProperty, L.T("settings.rhythmName", preset.Name, preset.Detail));
            var chosenPreset = preset;
            card.Click += (_, _) => ApplyRhythm(chosenPreset);
            grid.Children.Add(card);
        }
        body.Children.Add(grid);
        if (active.Name is null)
            body.Children.Add(new TextBlock
            {
                Text = L.T("settings.custom"),
                FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"),
                Margin = new Thickness(0, 0, 0, 10)
            });

        body.Children.Add(Lead(L.T("settings.numbersLead"), ""));
        body.Children.Add(Stepper(L.T("settings.focusLabel"), L.T("settings.focusHelp"), settings.FocusMinutes, 1, 180, 5, Minutes, value => settings.FocusMinutes = value));
        body.Children.Add(Stepper(L.T("settings.shortLabel"), L.T("settings.shortHelp"), settings.ShortMinutes, 1, 60, 1, Minutes, value => settings.ShortMinutes = value));
        body.Children.Add(Stepper(L.T("settings.longLabel"), L.T("settings.longHelp"), settings.LongMinutes, 1, 120, 5, Minutes, value => settings.LongMinutes = value));
        body.Children.Add(Stepper(L.T("settings.cycleLabel"), L.T("settings.cycleHelp"), settings.LongInterval, 1, 12, 1, value => $"{value}", value => settings.LongInterval = value));
        body.Children.Add(Stepper(L.T("settings.goalLabel"), L.T("settings.goalHelp"), settings.DailyGoalMinutes, 15, 720, 15, Minutes, value => settings.DailyGoalMinutes = value));

        body.Children.Add(Lead(L.T("settings.chainLead"), ""));
        body.Children.Add(Toggle(L.T("settings.autoBreak"), L.T("settings.autoBreakHelp"), settings.AutoBreak, value => settings.AutoBreak = value));
        body.Children.Add(Toggle(L.T("settings.autoFocus"), L.T("settings.autoFocusHelp"), settings.AutoFocus, value => settings.AutoFocus = value));
    }

    private void BuildSound()
    {
        body.Children.Add(Lead(L.T("settings.soundLead"), L.T("settings.soundHelp", SoundLibrary.Catalog.Count)));
        body.Children.Add(Toggle(L.T("settings.soundMaster"), L.T("settings.soundMasterHelp"), settings.Sound, value => settings.Sound = value));

        if (!settings.Sound)
        {
            body.Children.Add(Note(L.T("settings.soundOff")));
            return;
        }

        string Percent(int value) => L.T("settings.percent", value);
        body.Children.Add(Lead(L.T("settings.alarmsLead"), L.T("settings.alarmsHelp")));
        body.Children.Add(Toggle(L.T("settings.alarmEnabled"), L.T("settings.alarmEnabledHelp"), settings.AlarmEnabled, value => settings.AlarmEnabled = value));
        if (settings.AlarmEnabled)
        {
            body.Children.Add(Picker(L.T("settings.focusEnd"), L.T("settings.focusEndHelp"), SoundKind.Alarm, settings.FocusEndSound, value => settings.FocusEndSound = value));
            body.Children.Add(Picker(L.T("settings.breakEnd"), L.T("settings.breakEndHelp"), SoundKind.Alarm, settings.BreakEndSound, value => settings.BreakEndSound = value));
            body.Children.Add(Stepper(L.T("settings.alarmRepeats"), L.T("settings.alarmRepeatsHelp"), settings.AlarmRepeats, 1, 8, 1, value => $"{value}", value => settings.AlarmRepeats = value));
        }
        body.Children.Add(Stepper(L.T("settings.alarmVolume"), L.T("settings.alarmVolumeHelp"), settings.AlarmVolume, 0, 100, 5, Percent, value => settings.AlarmVolume = value));

        body.Children.Add(Lead(L.T("settings.sessionLead"), L.T("settings.sessionHelp")));
        body.Children.Add(Toggle(L.T("settings.ambient"), L.T("settings.ambientHelp"), settings.WhiteNoise, value => settings.WhiteNoise = value));
        if (settings.WhiteNoise)
        {
            body.Children.Add(Picker(L.T("settings.ambientPick"), L.T("settings.ambientPickHelp"), SoundKind.Ambient, settings.AmbientSound, value => settings.AmbientSound = value));
            body.Children.Add(Stepper(L.T("settings.ambientVolume"), "", settings.WhiteNoiseVolume, 0, 100, 5, Percent, value => settings.WhiteNoiseVolume = value));
        }

        body.Children.Add(Lead(L.T("settings.calendarLead"), L.T("settings.calendarHelp")));
        body.Children.Add(Picker(L.T("settings.reminders"), "", SoundKind.Reminder, settings.ReminderSound, value => settings.ReminderSound = value));

        body.Children.Add(Lead(L.T("settings.buttonsLead"), L.T("settings.buttonsHelp")));
        body.Children.Add(Toggle(L.T("settings.buttonSounds"), L.T("settings.buttonSoundsHelp"), settings.ButtonSounds, value => settings.ButtonSounds = value));
        if (settings.ButtonSounds)
        {
            body.Children.Add(Picker(L.T("settings.clickPack"), "", SoundKind.Click, settings.ClickSound, value => settings.ClickSound = value));
            body.Children.Add(Stepper(L.T("settings.clickVolume"), "", settings.EffectsVolume, 0, 100, 5, Percent, value => settings.EffectsVolume = value));
        }
    }

    private void BuildLook()
    {
        body.Children.Add(Lead(L.T("settings.languageLead"), L.T("settings.languageHelp")));
        body.Children.Add(LanguagePicker());

        body.Children.Add(Lead(L.T("settings.lookLead"), L.T("settings.lookHelp")));
        body.Children.Add(Toggle(L.T("settings.dark"), L.T("settings.darkHelp"), settings.Dark, value => settings.Dark = value));
        body.Children.Add(Toggle(L.T("settings.reduceMotion"), L.T("settings.reduceMotionHelp"), settings.ReduceMotion, value => settings.ReduceMotion = value));

        body.Children.Add(Swatches(L.T("settings.colorFocus"), L.T("settings.colorFocusHelp"), settings.FocusColor, value => settings.FocusColor = value));
        body.Children.Add(Swatches(L.T("settings.colorShort"), "", settings.ShortBreakColor, value => settings.ShortBreakColor = value));
        body.Children.Add(Swatches(L.T("settings.colorLong"), "", settings.LongBreakColor, value => settings.LongBreakColor = value));
        body.Children.Add(Swatches(L.T("settings.colorAccent"), L.T("settings.colorAccentHelp"), settings.AccentColor, value => settings.AccentColor = value));
    }

    /// <summary>
    /// The interface language. "Same as the system" is the default and names the language it
    /// currently resolves to, so the choice is never a mystery. Picking one redraws the whole app
    /// at once — this panel included — which is why it renders again from the top.
    /// </summary>
    private UIElement LanguagePicker()
    {
        var row = new Border
        {
            BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(1),
            Background = AgendaVisuals.Resource("Surface"), Padding = new Thickness(13, 11, 11, 12), Margin = new Thickness(0, 0, 0, 6)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = L.T("settings.languageLabel"), FontSize = 13, FontWeight = FontWeights.SemiBold });

        var shelf = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var systemName = Strings.Catalog.First(language => language.Code == Strings.Resolve("system")).Name;
        foreach (var (code, label, detail) in new[] { ("system", L.T("settings.languageSystem"), L.T("settings.languageSystemDetail", systemName)) }
            .Concat(Strings.Catalog.Select(language => (language.Code, language.Name, ""))))
        {
            bool chosen = settings.Language == code;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (detail.Length > 0)
                content.Children.Add(new TextBlock
                {
                    Text = detail, FontSize = 9, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                    Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Muted"), Opacity = chosen ? .85 : 1
                });
            var card = new Button
            {
                Content = content, MinWidth = 134, MinHeight = 46, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(11, 8, 11, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
                Background = chosen ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink"),
                BorderThickness = new Thickness(chosen ? 2.5 : 1.2)
            };
            card.SetValue(AutomationProperties.NameProperty, label);
            string picked = code;
            card.Click += (_, _) =>
            {
                settings.Language = picked;
                Changed();
                statusLine.Text = L.T("settings.languageChanged", Strings.Catalog.First(language => language.Code == Strings.Code).Name);
            };
            shelf.Children.Add(card);
        }
        stack.Children.Add(shelf);
        stack.Children.Add(new TextBlock
        {
            Text = L.T("settings.languageParserNote"), FontSize = 10, Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = AgendaVisuals.Resource("Muted")
        });
        row.Child = stack;
        return row;
    }

    private void BuildSpace()
    {
        body.Children.Add(Lead(L.T("settings.spaceLead"), L.T("settings.spaceHelp")));
        body.Children.Add(Toggle(L.T("settings.timerAtBottom"), L.T("settings.timerAtBottomHelp"), settings.TimerAtBottom, value => settings.TimerAtBottom = value));
        body.Children.Add(Toggle(L.T("settings.alwaysOnTop"), L.T("settings.alwaysOnTopHelp"), settings.AlwaysOnTop, value => settings.AlwaysOnTop = value));

        body.Children.Add(Lead(L.T("settings.dataLead"), L.T("settings.dataHelp")));
        body.Children.Add(Note(owner.Store.DirectoryPath));
        var open = new Button { Content = L.T("settings.openFolder"), FontSize = 11, Padding = new Thickness(13, 9, 13, 9), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(owner.Store.DirectoryPath) { UseShellExecute = true }); }
            catch (Exception ex) { statusLine.Text = L.T("settings.openFolderFailed", ex.Message); }
        };
        body.Children.Add(open);
    }

    private void ApplyRhythm((string Name, string Detail, string Story, int Focus, int Short, int Long, int Interval) preset)
    {
        settings.FocusMinutes = preset.Focus; settings.ShortMinutes = preset.Short;
        settings.LongMinutes = preset.Long; settings.LongInterval = preset.Interval;
        Changed();
        statusLine.Text = L.T("settings.rhythmApplied", preset.Name, preset.Detail);
    }

    /// <summary>Puts every setting back to the moment the panel was opened.</summary>
    private void RestoreOpening()
    {
        opening.Restore(settings);
        owner.ApplyLiveSettings();
        Render();
        statusLine.Text = L.T("settings.undone");
    }

    internal void ShowSection(string key) { section = key; Render(); }
    internal void ChooseRhythm(int index) => ApplyRhythm(Rhythms[Math.Clamp(index, 0, Rhythms.Length - 1)]);
    internal void Undo() => RestoreOpening();

    // ---------------------------------------------------------------- pieces

    private static string Minutes(int value) =>
        value < 60 ? L.T("common.minutesShort", value) : value % 60 == 0 ? L.T("common.hoursShort", value / 60) : L.T("common.hoursMinutes", value / 60, value % 60);

    private UIElement Lead(string title, string detail)
    {
        var block = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        block.Children.Add(new TextBlock
        {
            Text = title, FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Black,
            Foreground = AgendaVisuals.Resource("Muted")
        });
        if (detail.Length > 0)
            block.Children.Add(new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return block;
    }

    private UIElement Note(string text) => new TextBlock
    {
        Text = text, FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4)
    };

    /// <summary>
    /// A shelf of sounds of one kind. A card chooses and plays at once, so the choice is made by
    /// ear rather than by name.
    /// </summary>
    private UIElement Picker(string label, string help, SoundKind kind, string current, Action<string> set)
    {
        var row = new Border
        {
            BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(1),
            Background = AgendaVisuals.Resource("Surface"), Padding = new Thickness(13, 11, 11, 8), Margin = new Thickness(0, 0, 0, 6)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (help.Length > 0)
            stack.Children.Add(new TextBlock { Text = help, FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

        var shelf = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        foreach (var sound in SoundLibrary.OfKind(kind))
        {
            bool chosen = sound.Id == current;
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = (chosen ? "♪  " : "") + sound.Name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new TextBlock
            {
                Text = sound.Detail, FontSize = 9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
                Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Muted"), Opacity = chosen ? 0.85 : 1
            });
            var card = new Button
            {
                Content = content, Width = 134, MinHeight = 58, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 7, 9, 7),
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                Background = chosen ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = chosen ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink"),
                BorderThickness = new Thickness(chosen ? 2.5 : 1.2), ToolTip = L.T("settings.pickAndHear")
            };
            card.SetValue(AutomationProperties.NameProperty, $"{label}: {sound.Name}");
            card.Click += (_, _) =>
            {
                set(sound.Id);
                Changed();
                // During a focus session the new ambience already took over; a preview would only stutter it.
                bool sessionAmbience = kind == SoundKind.Ambient && owner.Timer.Running && owner.Timer.Phase == Phase.Focus;
                if (!sessionAmbience) owner.Sounds.Preview(sound.Id);
                statusLine.Text = L.T("settings.soundChosen", label.ToUpper(Strings.Culture), sound.Name);
            };
            shelf.Children.Add(card);
        }
        stack.Children.Add(shelf);
        row.Child = stack;
        return row;
    }

    /// <summary>A row that reads like a sentence and flips when you click anywhere on it.</summary>
    private UIElement Toggle(string label, string help, bool value, Action<bool> set)
    {
        var row = new Border
        {
            BorderBrush = value ? AgendaVisuals.Resource("Line") : AgendaVisuals.Fade("Line", 70),
            BorderThickness = new Thickness(value ? 1.5 : 1),
            Background = AgendaVisuals.Resource("Surface"),
            Padding = new Thickness(13, 11, 11, 11),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (help.Length > 0)
            text.Children.Add(new TextBlock { Text = help, FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 0) });
        grid.Children.Add(text);

        var pill = new Border
        {
            Width = 54, Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Background = value ? AgendaVisuals.Resource("Ink") : Brushes.Transparent,
            BorderBrush = AgendaVisuals.Resource("Line"), BorderThickness = new Thickness(1.5)
        };
        pill.Child = new Border
        {
            Width = 18, Height = 18, Margin = new Thickness(3),
            HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = value ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Fade("Ink", 110)
        };
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        row.Child = grid;
        row.SetValue(AutomationProperties.NameProperty, label + (value ? L.T("settings.on") : L.T("settings.off")));
        row.MouseLeftButtonDown += (_, _) => { set(!value); Changed(); };
        return row;
    }

    /// <summary>A number with its own bar: step it precisely, or click the bar to jump.</summary>
    private UIElement Stepper(string label, string help, int value, int min, int max, int step, Func<int, string> format, Action<int> set)
    {
        var row = new Border
        {
            BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(1),
            Background = AgendaVisuals.Resource("Surface"), Padding = new Thickness(13, 11, 11, 12), Margin = new Thickness(0, 0, 0, 6)
        };
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (help.Length > 0)
            text.Children.Add(new TextBlock { Text = help, FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 0) });
        head.Children.Add(text);

        void Apply(int candidate)
        {
            int clamped = Math.Clamp(candidate, min, max);
            if (clamped == value) return;
            set(clamped);
            Changed();
        }

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var less = new Button { Content = "−", Width = 30, Height = 30, FontSize = 15, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), IsEnabled = value > min };
        less.SetValue(AutomationProperties.NameProperty, L.T("settings.stepDown", label));
        less.Click += (_, _) => Apply(value - step);
        controls.Children.Add(less);
        controls.Children.Add(new TextBlock
        {
            Text = format(value), FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, MinWidth = 76
        });
        var more = new Button { Content = "+", Width = 30, Height = 30, FontSize = 15, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = value < max };
        more.SetValue(AutomationProperties.NameProperty, L.T("settings.stepUp", label));
        more.Click += (_, _) => Apply(value + step);
        controls.Children.Add(more);
        Grid.SetColumn(controls, 1);
        head.Children.Add(controls);
        stack.Children.Add(head);

        var track = new Grid { Height = 6, Background = AgendaVisuals.Fade("Line", 45), Margin = new Thickness(0, 11, 0, 0), Cursor = Cursors.Hand };
        var fill = new Border { Background = AgendaVisuals.Resource("Ink"), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill);
        track.SizeChanged += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * (value - min) / (double)Math.Max(1, max - min));
        track.MouseLeftButtonDown += (_, e) =>
        {
            double ratio = Math.Clamp(e.GetPosition(track).X / Math.Max(1, track.ActualWidth), 0, 1);
            Apply((int)Math.Round((min + ratio * (max - min)) / step) * step);
        };
        track.SetValue(AutomationProperties.NameProperty, label);
        stack.Children.Add(track);

        row.Child = stack;
        return row;
    }

    /// <summary>A palette instead of a hexadecimal field. "Otro" still accepts an exact code.</summary>
    private UIElement Swatches(string label, string help, string current, Action<string> set)
    {
        var row = new Border
        {
            BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(1),
            Background = AgendaVisuals.Resource("Surface"), Padding = new Thickness(13, 11, 11, 12), Margin = new Thickness(0, 0, 0, 6)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (help.Length > 0)
            stack.Children.Add(new TextBlock { Text = help, FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

        var strip = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        bool known = false;
        foreach (var tone in Tones)
        {
            bool chosen = string.Equals(tone, current, StringComparison.OrdinalIgnoreCase);
            known |= chosen;
            var swatch = new Button
            {
                Width = 42, Height = 30, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0),
                Background = Tone(tone), BorderBrush = AgendaVisuals.Resource("Line"),
                BorderThickness = new Thickness(chosen ? 3.5 : 1), Content = chosen ? "✓" : "", FontSize = 12,
                ToolTip = tone
            };
            swatch.SetValue(AutomationProperties.NameProperty, L.T("settings.colorName", label, tone));
            swatch.Click += (_, _) => { set(tone); Changed(); };
            strip.Children.Add(swatch);
        }
        var other = new Button
        {
            Content = known ? L.T("settings.colorOther") : current.ToUpperInvariant(), FontSize = 10, Height = 30, Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(known ? 1 : 3.5),
            Background = known ? AgendaVisuals.Resource("Surface") : Tone(current),
            ToolTip = L.T("settings.colorOtherTip")
        };
        other.Click += (_, _) =>
        {
            var typed = Dialogs.Prompt(this, L.T("settings.colorExactTitle"), L.T("settings.colorExactLabel"), current);
            if (string.IsNullOrWhiteSpace(typed)) return;
            var value = typed.Trim();
            try { _ = (Color)ColorConverter.ConvertFromString(value)!; }
            catch (Exception) { statusLine.Text = L.T("settings.colorBad"); return; }
            set(value);
            Changed();
        };
        strip.Children.Add(other);
        stack.Children.Add(strip);
        row.Child = stack;
        return row;
    }

    private static Brush Tone(string value)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!); }
        catch (Exception) { return AgendaVisuals.Resource("Surface"); }
    }

}

public sealed class LayoutsWindow : Window
{
    public LayoutsWindow(MainWindow owner)
    {
        Owner = owner; Title = L.T("layouts.title"); Width = 500; Height = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        // A window of our own gets no Window style, so its content would inherit black text.
        SetResourceReference(ForegroundProperty, "Ink");
        var stack = new StackPanel { Margin = new Thickness(22) }; Content = stack; stack.Children.Add(Dialogs.Heading(L.T("layouts.heading")));
        stack.Children.Add(new TextBlock { Text = L.T("layouts.help") });
        var input = new TextBox { Text = L.T("layouts.default") }; stack.Children.Add(input);
        var list = new ListBox { Height = 150, Margin = new Thickness(0, 10, 0, 10), ItemsSource = owner.Settings.Layouts.Keys.ToList() };
        stack.Children.Add(Dialogs.Button(L.T("layouts.save"), () =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            owner.SaveState();
            owner.Settings.Layouts[input.Text.Trim()] = System.Text.Json.JsonSerializer.Deserialize<List<PomoDock.Core.WidgetConfig>>(System.Text.Json.JsonSerializer.Serialize(owner.Settings.Widgets))!;
            owner.SaveState(); list.ItemsSource = owner.Settings.Layouts.Keys.ToList();
        }));
        stack.Children.Add(list);
        stack.Children.Add(Dialogs.Button(L.T("layouts.load"), () => { if (list.SelectedItem is string name) { owner.LoadLayout(owner.Settings.Layouts[name]); Close(); } })); Dialogs.Modalize(this);
    }
}
