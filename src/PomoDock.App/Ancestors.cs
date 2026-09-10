using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PomoDock.App;

/// <summary>
/// Walking up from whatever the mouse reported. A click inside rich text reports a content
/// element — a FlowDocument, a Paragraph, a Run — which is not a visual, and asking the
/// visual tree for its parent throws. Those steps go through the logical tree instead, which
/// lands back on the control hosting the text and lets the walk carry on.
/// </summary>
internal static class Ancestors
{
    public static DependencyObject? Up(DependencyObject current) =>
        current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);

    public static T? Find<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = Up(current))
            if (current is T match) return match;
        return null;
    }
}
