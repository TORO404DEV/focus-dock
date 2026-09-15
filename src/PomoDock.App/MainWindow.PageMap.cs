using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

public partial class MainWindow
{
    private bool pageMapOpen;

    private void PageMapClick(object sender, RoutedEventArgs e)
    {
        TogglePageMap();
        e.Handled = true;
    }

    private void HidePageMapClick(object sender, RoutedEventArgs e)
    {
        HidePageMap();
        e.Handled = true;
    }

    internal void TogglePageMap()
    {
        if (pageMapOpen) HidePageMap();
        else ShowPageMap();
    }

    internal bool IsPageMapOpen => pageMapOpen;

    private IEnumerable<WidgetCard> MappedCards()
    {
        foreach (var card in cards) yield return card;
        if (agentCard is { Visibility: Visibility.Visible }) yield return agentCard;
    }

    private void ShowPageMap()
    {
        if (pageMapOpen || pageTransitioning) return;
        if (IsWorkspaceNavigationBlocked) return;
        CloseNotificationCenter();
        HideTimerOverlay();
        foreach (var card in MappedCards())
        {
            HideInteractionOverlay(card);
            HideOverlay(card);
            card.SetCarouselTransition(true);
        }
        pageMapOpen = true;
        PageMapOverlay.Visibility = Visibility.Visible;
        PageMapTitle.Text = L.T("pages.mapTitle");
        PageMapHint.Text = L.T("pages.mapHint");
        PageMapCloseButton.ToolTip = L.T("chrome.pageMapClose");
        RenderPageMap();
        SyncPageMapButton();
    }

    private void HidePageMap()
    {
        if (!pageMapOpen) return;
        pageMapOpen = false;
        PageMapOverlay.Visibility = Visibility.Collapsed;
        PageMapCanvas.Children.Clear();
        foreach (var card in MappedCards())
            card.SetCarouselTransition(false);
        ArrangeCards();
        foreach (var card in cards) ShowInteractionOverlay(card);
        SyncAgentAirspace();
        SyncPageMapButton();
    }

    private void SyncPageMapButton()
    {
        if (PageMapButton is null) return;
        PageMapButton.ToolTip = L.T("chrome.pageMap");
        PageMapButton.SetResourceReference(BackgroundProperty, pageMapOpen ? "Accent" : "Surface");
        PageMapButton.SetResourceReference(ForegroundProperty, pageMapOpen ? "AccentInk" : "Ink");
    }

    private void RenderPageMap()
    {
        if (!pageMapOpen || PageMapCanvas is null || !workspacePagesLoaded) return;
        PageMapCanvas.Children.Clear();

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

        int cols = maxCol - minCol + 1;
        int rows = maxRow - minRow + 1;
        double aspect = WidgetArea.ActualWidth > 1 && WidgetArea.ActualHeight > 1
            ? WidgetArea.ActualWidth / WidgetArea.ActualHeight
            : 0.72;
        const double gap = 16;
        double tileWidth = 220;
        double tileHeight = tileWidth / Math.Clamp(aspect, 0.45, 1.1);
        PageMapCanvas.Width = cols * tileWidth + (cols - 1) * gap;
        PageMapCanvas.Height = rows * tileHeight + (rows - 1) * gap;

        var byCell = pages
            .Select((page, index) => (page, index))
            .ToDictionary(entry => (entry.page.Col, entry.page.Row), entry => entry);

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int col = minCol; col <= maxCol; col++)
            {
                double x = (col - minCol) * (tileWidth + gap);
                double y = (row - minRow) * (tileHeight + gap);
                if (byCell.TryGetValue((col, row), out var entry))
                    PlaceMapPage(entry.page, entry.index, x, y, tileWidth, tileHeight);
                else if (addCells.Contains((col, row)))
                    PlaceMapAdd(col, row, x, y, tileWidth, tileHeight);
            }
        }
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

    private void PlaceMapPage(WorkspacePage page, int index, double x, double y, double width, double height)
    {
        bool active = index == currentPageIndex;
        var tile = new Border
        {
            Width = width,
            Height = height,
            BorderThickness = new Thickness(active ? 3 : 1.5),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        tile.SetResourceReference(Border.BorderBrushProperty, active ? "Accent" : "Edge");
        tile.SetResourceReference(Border.BackgroundProperty, "Surface");
        tile.Child = MapPagePreview(page, active);
        tile.ToolTip = L.T("pages.pageTipDelete", page.Name, page.WidgetCount, page.WidgetCount == 1 ? L.T("pages.widgetOne") : L.T("pages.widgetMany"));
        tile.MouseLeftButtonUp += (_, e) => { OpenPageFromMap(index); e.Handled = true; };
        tile.MouseRightButtonUp += (_, e) => { DeleteWorkspacePageClick(index); if (pageMapOpen) RenderPageMap(); e.Handled = true; };
        Canvas.SetLeft(tile, x);
        Canvas.SetTop(tile, y);
        PageMapCanvas.Children.Add(tile);
    }

    private UIElement MapPagePreview(WorkspacePage page, bool active)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Border();
        header.SetResourceReference(Border.BackgroundProperty, active ? "Accent" : "Chrome");
        var title = new TextBlock
        {
            Text = active ? L.T("pages.mapHere") + "  ·  " + page.Name : page.Name,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 9, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentInk" : "ChromeInk");
        header.Child = title;
        root.Children.Add(header);

        var stage = new Canvas { ClipToBounds = true };
        stage.SetResourceReference(Panel.BackgroundProperty, "Paper");
        Grid.SetRow(stage, 1);
        root.Children.Add(stage);

        stage.SizeChanged += (_, _) => PaintMapWidgets(stage, page);
        return root;
    }

    private void PaintMapWidgets(Canvas stage, WorkspacePage page)
    {
        stage.Children.Clear();
        double logicalW = WidgetArea.ActualWidth > 1 ? WidgetArea.ActualWidth : 720;
        double logicalH = WidgetArea.ActualHeight > 1 ? WidgetArea.ActualHeight : 900;
        double stageW = stage.ActualWidth > 1 ? stage.ActualWidth : 200;
        double stageH = stage.ActualHeight > 1 ? stage.ActualHeight : 260;
        double sx = stageW / logicalW;
        double sy = stageH / logicalH;

        if (page.TimerWidget is { } timer)
            stage.Children.Add(MapWidgetRect(timer, true, sx, sy, stageW, stageH));
        foreach (var widget in page.Widgets)
            stage.Children.Add(MapWidgetRect(widget, false, sx, sy, stageW, stageH));

        if (page.Widgets.Count == 0 && page.TimerWidget is null)
        {
            var empty = new TextBlock
            {
                Text = L.T("pages.emptyTitle"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Opacity = 0.55,
                Width = stageW,
                TextAlignment = TextAlignment.Center
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Canvas.SetTop(empty, Math.Max(8, stageH / 2 - 8));
            stage.Children.Add(empty);
        }
    }

    private static Border MapWidgetRect(WidgetConfig widget, bool timer, double sx, double sy, double stageW, double stageH)
    {
        double width = Math.Clamp((widget.Width > 0 ? widget.Width : (timer ? 560 : 280)) * sx, 10, stageW);
        double height = Math.Clamp((widget.Collapsed ? 42 : widget.Height > 0 ? widget.Height : (timer ? 360 : 180)) * sy, 8, stageH);
        double x = Math.Clamp(widget.X * sx, 0, Math.Max(0, stageW - width));
        double y = Math.Clamp(widget.Y * sy, 0, Math.Max(0, stageH - height));
        var rect = new Border
        {
            Width = width,
            Height = height,
            BorderThickness = new Thickness(1),
            Opacity = 0.92
        };
        rect.SetResourceReference(Border.BorderBrushProperty, "Edge");
        rect.SetResourceReference(Border.BackgroundProperty, timer ? "Chrome" : "Raised");
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        return rect;
    }

    private void PlaceMapAdd(int col, int row, double x, double y, double width, double height)
    {
        var tile = new Border
        {
            Width = width,
            Height = height,
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 23, 25, 22)),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        tile.Child = new TextBlock
        {
            Text = "＋",
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.55
        };
        ((TextBlock)tile.Child).SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        tile.ToolTip = L.T("pages.mapAdd");
        tile.MouseLeftButtonUp += (_, e) => { CreatePageFromMap(col, row); e.Handled = true; };
        Canvas.SetLeft(tile, x);
        Canvas.SetTop(tile, y);
        PageMapCanvas.Children.Add(tile);
    }

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
