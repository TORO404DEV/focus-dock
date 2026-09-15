using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PomoDock.App;

/// <summary>
/// Tiny toolbar drawings that follow the button ink, so a report is a chart and tasks are a list
/// instead of two similar blocks of bars.
/// </summary>
internal static class ChromeIcon
{
    public static FrameworkElement Report()
    {
        var canvas = Board();
        canvas.Children.Add(Bar(1, 9, 3.4, 6));
        canvas.Children.Add(Bar(6.3, 3, 3.4, 12));
        canvas.Children.Add(Bar(11.6, 6.5, 3.4, 8.5));
        return Wrap(canvas);
    }

    public static FrameworkElement Tasks()
    {
        var canvas = Board();
        canvas.Children.Add(Box(0.8, 1.2, 4.4, 4.4));
        canvas.Children.Add(Tick(1.5, 2.4));
        canvas.Children.Add(Line(6.6, 2.8, 8.6, 1.6));
        canvas.Children.Add(Box(0.8, 9.4, 4.4, 4.4));
        canvas.Children.Add(Line(6.6, 11, 8.6, 1.6));
        return Wrap(canvas);
    }

    public static FrameworkElement PageMap()
    {
        var canvas = Board();
        canvas.Children.Add(Box(0.6, 0.6, 6.4, 6.4));
        canvas.Children.Add(Box(9, 0.6, 6.4, 6.4));
        canvas.Children.Add(Box(0.6, 9, 6.4, 6.4));
        canvas.Children.Add(Box(9, 9, 6.4, 6.4));
        return Wrap(canvas);
    }

    /// <summary>Fill the canvas: a square with outward corners, not a hollow □ glyph.</summary>
    public static FrameworkElement Expand()
    {
        var canvas = Board();
        canvas.Children.Add(Box(3.2, 3.2, 9.6, 9.6));
        canvas.Children.Add(Corner(0.4, 0.4, 4.2, 1.5, true, true));
        canvas.Children.Add(Corner(0.4, 0.4, 4.2, 1.5, true, false));
        canvas.Children.Add(Corner(0.4, 0.4, 4.2, 1.5, false, true));
        canvas.Children.Add(Corner(0.4, 0.4, 4.2, 1.5, false, false));
        return Wrap(canvas, 20);
    }

    /// <summary>Restore from canvas fill: two overlapping squares.</summary>
    public static FrameworkElement Restore()
    {
        var canvas = Board();
        canvas.Children.Add(Box(4.2, 0.8, 10.2, 10.2));
        canvas.Children.Add(Box(0.8, 4.6, 10.2, 10.2));
        return Wrap(canvas, 20);
    }

    public static FrameworkElement Collapse()
    {
        var canvas = Board();
        canvas.Children.Add(Line(2.2, 7.1, 11.6, 1.8));
        return Wrap(canvas, 18);
    }

    public static FrameworkElement Close()
    {
        var canvas = Board();
        canvas.Children.Add(Stroke("M2.2,2.2 L13.8,13.8"));
        canvas.Children.Add(Stroke("M13.8,2.2 L2.2,13.8"));
        return Wrap(canvas, 16);
    }

    private static Canvas Board() => new() { Width = 16, Height = 16, IsHitTestVisible = false };

    private static Viewbox Wrap(Canvas canvas, double size = 16) => new()
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Child = canvas,
        IsHitTestVisible = false
    };

    private static Rectangle Bar(double x, double y, double width, double height)
    {
        var bar = new Rectangle { Width = width, Height = height, RadiusX = 0.4, RadiusY = 0.4 };
        Ink(bar);
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
        return bar;
    }

    private static Rectangle Box(double x, double y, double width, double height)
    {
        var box = new Rectangle { Width = width, Height = height, StrokeThickness = 1.7 };
        InkStroke(box);
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        return box;
    }

    private static Rectangle Line(double x, double y, double width, double height)
    {
        var line = new Rectangle { Width = width, Height = height, RadiusX = 0.5, RadiusY = 0.5 };
        Ink(line);
        Canvas.SetLeft(line, x);
        Canvas.SetTop(line, y);
        return line;
    }

    private static Path Tick(double x, double y)
    {
        var tick = new Path
        {
            Data = Geometry.Parse("M0.4,2.1 L1.6,3.4 L4.2,0.4"),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square,
            StrokeLineJoin = PenLineJoin.Miter
        };
        InkStroke(tick);
        Canvas.SetLeft(tick, x);
        Canvas.SetTop(tick, y);
        return tick;
    }

    private static Path Stroke(string data)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square
        };
        InkStroke(path);
        return path;
    }

    private static Canvas Corner(double x, double y, double length, double thickness, bool left, bool top)
    {
        var group = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false };
        double across = left ? x : 16 - x - length;
        double down = top ? y : 16 - y - length;
        group.Children.Add(BarAt(across, top ? y : 16 - y - thickness, length, thickness));
        group.Children.Add(BarAt(left ? x : 16 - x - thickness, down, thickness, length));
        return group;
    }

    private static Rectangle BarAt(double x, double y, double width, double height)
    {
        var bar = Bar(x, y, width, height);
        return bar;
    }

    private static void Ink(Shape shape) =>
        shape.SetBinding(Shape.FillProperty, ButtonInk());

    private static void InkStroke(Shape shape)
    {
        shape.SetBinding(Shape.StrokeProperty, ButtonInk());
        shape.Fill = Brushes.Transparent;
    }

    private static Binding ButtonInk() => new(nameof(Control.Foreground))
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
    };
}
