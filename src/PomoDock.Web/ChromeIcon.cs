using Microsoft.AspNetCore.Components;

namespace PomoDock.Web;

/// <summary>
/// Tiny toolbar drawings that follow the button ink, so a report is a chart and tasks are a list
/// instead of two similar blocks of bars. Same 16-unit board and strokes as the desktop's.
/// </summary>
internal static class ChromeIcon
{
    private static MarkupString Board(string body, int size = 16) =>
        new(FormattableString.Invariant($"<svg class=\"icon\" style=\"width:{size}px;height:{size}px\" viewBox=\"0 0 16 16\" aria-hidden=\"true\">{body}</svg>"));

    private static string Box(double x, double y, double w, double h) =>
        FormattableString.Invariant($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\"/>");

    private static string Bar(double x, double y, double w, double h, double r = 0.4) =>
        FormattableString.Invariant($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"{r}\" fill=\"currentColor\"/>");

    private static string Stroke(string data, double width = 1.8) =>
        FormattableString.Invariant($"<path d=\"{data}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"{width}\" stroke-linecap=\"square\"/>");

    public static MarkupString Report() => Board(Bar(1, 9, 3.4, 6) + Bar(6.3, 3, 3.4, 12) + Bar(11.6, 6.5, 3.4, 8.5));

    public static MarkupString Tasks() => Board(
        Box(0.8, 1.2, 4.4, 4.4) + Stroke("M1.9,4.5 L3.1,5.8 L5.7,2.8", 1.4) + Bar(6.6, 2.8, 8.6, 1.6, 0.5) +
        Box(0.8, 9.4, 4.4, 4.4) + Bar(6.6, 11, 8.6, 1.6, 0.5));

    public static MarkupString PageMap() => Board(Box(0.6, 0.6, 6.4, 6.4) + Box(9, 0.6, 6.4, 6.4) + Box(0.6, 9, 6.4, 6.4) + Box(9, 9, 6.4, 6.4));

    /// <summary>Fill the canvas: a square with outward corners, not a hollow □ glyph.</summary>
    public static MarkupString Expand() => Board(
        Box(3.2, 3.2, 9.6, 9.6) +
        Bar(0.4, 0.4, 4.2, 1.5, 0) + Bar(0.4, 0.4, 1.5, 4.2, 0) +
        Bar(11.4, 0.4, 4.2, 1.5, 0) + Bar(14.1, 0.4, 1.5, 4.2, 0) +
        Bar(0.4, 14.1, 4.2, 1.5, 0) + Bar(0.4, 11.4, 1.5, 4.2, 0) +
        Bar(11.4, 14.1, 4.2, 1.5, 0) + Bar(14.1, 11.4, 1.5, 4.2, 0), 20);

    /// <summary>Hold to talk: a capsule on its stand, the desktop's Segoe microphone redrawn.</summary>
    public static MarkupString Mic() => Board(
        FormattableString.Invariant($"<rect x=\"5.3\" y=\"0.9\" width=\"5.4\" height=\"9\" rx=\"2.7\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\"/>") +
        Stroke("M2.6,7.4 C2.6,11 5,12.8 8,12.8 C11,12.8 13.4,11 13.4,7.4", 1.6) + Bar(7.2, 12.6, 1.6, 2.4, 0) + Bar(4.8, 14.3, 6.4, 1.4, 0.4));

    /// <summary>Restore from canvas fill: two overlapping squares.</summary>
    public static MarkupString Restore() => Board(Box(4.2, 0.8, 10.2, 10.2) + Box(0.8, 4.6, 10.2, 10.2), 20);

    public static MarkupString Collapse() => Board(Bar(2.2, 7.1, 11.6, 1.8, 0.5), 18);

    public static MarkupString Close() => Board(Stroke("M2.2,2.2 L13.8,13.8") + Stroke("M13.8,2.2 L2.2,13.8"));
}
