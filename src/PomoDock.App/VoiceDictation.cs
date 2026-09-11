using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Adds one reusable microphone to whichever native PomoDock text editor owns the caret. Speech
/// itself belongs to Windows Voice Typing, so it uses the user's installed language, microphone
/// and privacy settings and inserts through the same caret as the keyboard.
/// </summary>
internal sealed class VoiceDictation : IDisposable
{
    private const byte VirtualLeftWindows = 0x5B;
    private const byte VirtualH = 0x48;
    private const uint KeyUp = 0x0002;
    private static VoiceDictation? current;
    private static bool handlersRegistered;

    private readonly MainWindow owner;
    private readonly Popup popup;
    private readonly Button microphone;
    private TextBoxBase? target;
    private Thickness originalPadding;
    private bool paddingReserved;
    private bool disposed;

    private VoiceDictation(MainWindow owner)
    {
        this.owner = owner;
        microphone = new Button
        {
            Width = 27,
            Height = 27,
            Padding = new Thickness(5),
            Margin = new Thickness(0),
            Focusable = false,
            IsTabStop = false,
            BorderThickness = new Thickness(1.25),
            Content = MicrophoneIcon()
        };
        microphone.SetResourceReference(Control.BackgroundProperty, "Raised");
        microphone.SetResourceReference(Control.BorderBrushProperty, "Edge");
        microphone.Click += Start;
        popup = new Popup
        {
            Child = microphone,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Placement = PlacementMode.Relative,
            PopupAnimation = PopupAnimation.Fade
        };
        RefreshLanguage();
    }

    public static VoiceDictation Attach(MainWindow owner)
    {
        current?.Dispose();
        current = new VoiceDictation(owner);
        if (!handlersRegistered)
        {
            EventManager.RegisterClassHandler(typeof(TextBoxBase), Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(InputFocused), true);
            EventManager.RegisterClassHandler(typeof(TextBoxBase), Keyboard.LostKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(InputBlurred), true);
            handlersRegistered = true;
        }
        return current;
    }

    private static void InputFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBoxBase input) current?.Show(input);
    }

    private static void InputBlurred(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBoxBase input || current?.target != input) return;
        input.Dispatcher.BeginInvoke(() =>
        {
            if (current?.target == input && !input.IsKeyboardFocusWithin) current.Hide();
        }, DispatcherPriority.Input);
    }

    private void Show(TextBoxBase input)
    {
        if (disposed || input.IsReadOnly || !input.IsEnabled || input.ActualWidth < 72 || input.ActualHeight < 25)
        {
            Hide();
            return;
        }
        if (target != input)
        {
            Hide();
            target = input;
            originalPadding = input.Padding;
            input.Padding = new Thickness(originalPadding.Left, originalPadding.Top,
                originalPadding.Right + 33, originalPadding.Bottom);
            paddingReserved = true;
            input.SizeChanged += TargetSizeChanged;
            input.IsVisibleChanged += TargetVisibilityChanged;
        }
        RefreshLanguage();
        Position();
        popup.IsOpen = true;
    }

    private void TargetSizeChanged(object sender, SizeChangedEventArgs e) => Position();
    private void TargetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (target is null || !target.IsVisible) Hide();
    }

    private void Position()
    {
        if (target is null) return;
        popup.PlacementTarget = target;
        popup.HorizontalOffset = Math.Max(2, target.ActualWidth - microphone.Width - 5);
        popup.VerticalOffset = Math.Max(2, Math.Min(5, (target.ActualHeight - microphone.Height) / 2));
        // Changing an offset by a fraction forces WPF to reposition an already-open popup after
        // a responsive widget resize, then returns it to the exact pixel.
        if (popup.IsOpen)
        {
            double x = popup.HorizontalOffset;
            popup.HorizontalOffset = x + .01;
            popup.HorizontalOffset = x;
        }
    }

    private void Start(object sender, RoutedEventArgs e)
    {
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
        target.Dispatcher.BeginInvoke(() =>
        {
            if (target is null) return;
            // Win + H toggles the Windows Voice Typing surface and keeps insertion at this caret.
            keybd_event(VirtualLeftWindows, 0, 0, UIntPtr.Zero);
            keybd_event(VirtualH, 0, 0, UIntPtr.Zero);
            keybd_event(VirtualH, 0, KeyUp, UIntPtr.Zero);
            keybd_event(VirtualLeftWindows, 0, KeyUp, UIntPtr.Zero);
            owner.Status(L.T("dictation.listening"));
        }, DispatcherPriority.Input);
        e.Handled = true;
    }

    public void RefreshLanguage() => microphone.ToolTip = L.T("dictation.start");

    private void Hide()
    {
        popup.IsOpen = false;
        if (target is not null)
        {
            target.SizeChanged -= TargetSizeChanged;
            target.IsVisibleChanged -= TargetVisibilityChanged;
            if (paddingReserved) target.Padding = originalPadding;
        }
        paddingReserved = false;
        target = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Hide();
        if (ReferenceEquals(current, this)) current = null;
    }

    private static Viewbox MicrophoneIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var capsule = new Border
        {
            Width = 7,
            Height = 11,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.8),
            Background = Brushes.Transparent
        };
        capsule.SetResourceReference(Border.BorderBrushProperty, "Ink");
        Canvas.SetLeft(capsule, 5.5); Canvas.SetTop(capsule, 1);
        canvas.Children.Add(capsule);
        var stem = new Path
        {
            Data = Geometry.Parse("M3.5,8.5 C3.5,13 6,15 9,15 C12,15 14.5,13 14.5,8.5 M9,15 L9,17 M6,17 L12,17"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square
        };
        stem.SetResourceReference(Shape.StrokeProperty, "Ink");
        canvas.Children.Add(stem);
        return new Viewbox { Width = 16, Height = 16, Child = canvas, Stretch = Stretch.Uniform };
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    internal bool IsVisible => popup.IsOpen;
    internal FrameworkElement Surface => microphone;
    internal bool ReservesTextSpace => target is not null && target.Padding.Right >= originalPadding.Right + 33;
}
