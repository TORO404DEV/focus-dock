using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace PomoDock.App;

public partial class MainWindow
{
    // Routed-input regression, with a real foreign HWND attached by Diagnostics.
    // Pump the dispatcher between down/up: this is the interval in which the
    // old background callback destroyed the timer's native surface and capture.
    internal async Task VerifyTimerInputAsync(WidgetCard embedded, Action<bool, string> assert)
    {
        if (!DiagnosticMode) throw new InvalidOperationException("Diagnostics only");
        static nint Surface(FrameworkElement element) => (PresentationSource.FromVisual(element) as HwndSource)?.Handle ?? 0;
        static void MouseEvent(UIElement element, RoutedEvent routedEvent)
            => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent });
        Width = 900; Height = 940; UpdateLayout(); Activate();
        Sounds.StopNoise(); Settings.Sound = false; Settings.WhiteNoise = false;
        if (!CurrentWorkspacePageHasTimer) AddTimerForDiagnostics();
        CurrentWorkspacePageForDiagnostics.TimerPositionCustomized = true;
        var timerWidget = CurrentWorkspacePageForDiagnostics.TimerWidget!;
        timerWidget.X = 30; timerWidget.Y = 40;
        timerWidget.Width = 430; timerWidget.Height = 330;
        ArrangeTimerWidget(); UpdateLayout();

        try
        {
            for (int cycle = 0; cycle < 3; cycle++)
            {
                BringCardToFront(embedded);
                await Task.Delay(40);
                for (int click = 0; click < 2; click++)
                {
                    var source = Surface(StartButton);
                    bool running = Timer.Running;
                    assert(StartButton.CaptureMouse(), $"timer button captures input ({cycle}/{click})");
                    MouseEvent(StartButton, Mouse.PreviewMouseDownEvent);
                    await Task.Delay(70);
                    assert(Surface(StartButton) == source && StartButton.IsMouseCaptured,
                        $"timer button keeps its HWND and capture while pressed ({cycle}/{click})");
                    MouseEvent(StartButton, Mouse.PreviewMouseUpEvent);
                    StartButton.ReleaseMouseCapture();
                    StartButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Task.Delay(50);
                    assert(Timer.Running != running, $"timer start/pause callback works with embedded app ({cycle}/{click})");
                }
            }

            foreach (bool canvas in new[] { true, false })
            foreach (var edge in new[] { "MOVE", "N", "S", "E", "W", "NW", "NE", "SW", "SE" })
            {
                if (canvas) BringCardToFront(embedded); else BringTimerToFront();
                timerWidget.X = 70; timerWidget.Y = 70;
                timerWidget.Width = 430; timerWidget.Height = 360;
                ArrangeTimerWidget(); UpdateLayout();
                var element = edge == "MOVE" ? (UIElement)TimerMoveHeader : timerGestureHandles.First(h =>
                    System.Windows.Automation.AutomationProperties.GetName(h) == "Redimensionar temporizador " + edge);
                var source = Surface(TimerFrame);
                assert(element.CaptureMouse(), $"timer {edge} captures input (canvas={canvas})");
                MouseEvent(element, Mouse.PreviewMouseDownEvent);
                if (edge != "MOVE") element.RaiseEvent(new DragStartedEventArgs(0, 0));
                await Task.Delay(60);
                assert(timerResizing && element.IsMouseCaptured && Surface(TimerFrame) == source,
                    $"timer {edge} keeps capture and surface during gesture (canvas={canvas})");
                UpdateTimerGesture(new Point(timerPointerStart.X + 18, timerPointerStart.Y + 18));
                bool changed = edge == "MOVE" ? Math.Abs(Canvas.GetLeft(TimerFrame) - 88) < .01 && Math.Abs(Canvas.GetTop(TimerFrame) - 88) < .01
                    : TimerFrame.Width != 430 || TimerFrame.Height != 360;
                assert(changed, $"timer {edge} changes geometry with embedded window (canvas={canvas}; x={Canvas.GetLeft(TimerFrame)}, y={Canvas.GetTop(TimerFrame)}, w={TimerFrame.Width}, h={TimerFrame.Height}; start={timerStartX},{timerStartY},{timerStartWidth},{timerStartHeight})");
                MouseEvent(element, Mouse.PreviewMouseUpEvent);
                element.ReleaseMouseCapture();
                EndTimerGesture();
                await Task.Delay(50);
            }
            var savedSettings = Store.Read<PomoDock.Core.Settings>("settings")!;
            savedSettings.Validate();
            var saved = savedSettings.WorkspacePages[savedSettings.ActiveWorkspacePage].TimerWidget!;
            assert(saved.Width == TimerFrame.Width && saved.Height == TimerFrame.Height,
                "timer gesture persists final geometry with embedded window");
        }
        finally
        {
            Mouse.Capture(null); CancelTimerGesture();
            if (Timer.Running) ToggleTimer();
            HideTimerOverlay();
        }
    }
}
