using Microsoft.AspNetCore.Components;

namespace PomoDock.Web;

public partial class MainWindow
{
    /// <summary>
    /// Windows a widget opens of its own — a habit's detail, its cadence — drawn at the top of the
    /// page. Inside a card they would be clipped by it and carried along by every page slide.
    /// </summary>
    private readonly List<(object Owner, RenderFragment Body)> panels = [];

    internal void ShowPanel(object owner, RenderFragment body)
    {
        panels.RemoveAll(panel => panel.Owner == owner);
        panels.Add((owner, body));
        StateHasChanged();
    }

    internal void ClosePanel(object owner)
    {
        if (panels.RemoveAll(panel => panel.Owner == owner) > 0) _ = InvokeAsync(StateHasChanged);
    }
}
