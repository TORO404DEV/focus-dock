using PomoDock.Core;

namespace PomoDock.Web;

public partial class MainWindow
{
    private bool pageMapOpen;

    private void PageMapClick() => TogglePageMap();

    internal void TogglePageMap()
    {
        if (pageMapOpen) HidePageMap();
        else ShowPageMap();
    }

    private void ShowPageMap()
    {
        if (pageMapOpen || pageTransitioning || IsWorkspaceNavigationBlocked) return;
        CloseNotificationCenter();
        pageMapOpen = true;
    }

    private void HidePageMap() => pageMapOpen = false;

    /// <summary>The grid of the map: every page, plus the empty cells a page with content can grow into.</summary>
    private (int MinCol, int MinRow, int Cols, int Rows, HashSet<(int Col, int Row)> Add) PageMapGrid()
    {
        var pages = Settings.WorkspacePages;
        int minCol = pages.Min(page => page.Col);
        int maxCol = pages.Max(page => page.Col);
        int minRow = pages.Min(page => page.Row);
        int maxRow = pages.Max(page => page.Row);
        var addCells = AddableMapCells();
        if (addCells.Count > 0)
        {
            minCol = Math.Min(minCol, addCells.Min(cell => cell.Col));
            maxCol = Math.Max(maxCol, addCells.Max(cell => cell.Col));
            minRow = Math.Min(minRow, addCells.Min(cell => cell.Row));
            maxRow = Math.Max(maxRow, addCells.Max(cell => cell.Row));
        }
        return (minCol, minRow, maxCol - minCol + 1, maxRow - minRow + 1, addCells);
    }

    private HashSet<(int Col, int Row)> AddableMapCells()
    {
        var cells = new HashSet<(int, int)>();
        foreach (var page in Settings.WorkspacePages.Where(page => page.HasContent))
        {
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int col = page.Col + dx, row = page.Row + dy;
                if (FindPageIndex(col, row) is null) cells.Add((col, row));
            }
        }
        return cells;
    }

    /// <summary>A page's widgets as small blocks, placed by share of the canvas so any tile size reads the same.</summary>
    private string MapWidgetStyle(WidgetConfig widget, bool timer)
    {
        double logicalW = canvasWidth > 1 ? canvasWidth : 720;
        double logicalH = canvasHeight > 1 ? canvasHeight : 900;
        double width = Math.Clamp((widget.Width > 0 ? widget.Width : (timer ? 560 : 280)) / logicalW, .04, 1);
        double height = Math.Clamp((widget.Collapsed ? 42 : widget.Height > 0 ? widget.Height : (timer ? 360 : 180)) / logicalH, .03, 1);
        double x = Math.Clamp(widget.X / logicalW, 0, Math.Max(0, 1 - width));
        double y = Math.Clamp(widget.Y / logicalH, 0, Math.Max(0, 1 - height));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"left:{x * 100:0.##}%;top:{y * 100:0.##}%;width:{width * 100:0.##}%;height:{height * 100:0.##}%");
    }

    private string PageMapTip(WorkspacePage page) =>
        L.T("pages.pageTipDelete", page.Name, page.WidgetCount, page.WidgetCount == 1 ? L.T("pages.widgetOne") : L.T("pages.widgetMany"));

    private void OpenPageFromMap(int index)
    {
        HidePageMap();
        if (index != currentPageIndex) JumpToWorkspacePage(index);
    }

    private void CreatePageFromMap(int col, int row)
    {
        if (FindPageIndex(col, row) is not null) return;
        var neighbor = Settings.WorkspacePages.FirstOrDefault(page =>
            page.HasContent && Math.Abs(page.Col - col) + Math.Abs(page.Row - row) == 1);
        if (neighbor is null) return;
        int neighborIndex = Settings.WorkspacePages.IndexOf(neighbor);
        HidePageMap();
        if (neighborIndex != currentPageIndex) JumpToWorkspacePage(neighborIndex);
        int dx = col - CurrentPage.Col, dy = row - CurrentPage.Row;
        if (dx == 0 && dy == 0) return;
        CommitWorkspacePageChange(dx, dy, null, true);
    }
}
