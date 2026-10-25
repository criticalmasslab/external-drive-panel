using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ExternalDrivePanel;

internal static partial class Program
{
    private static async Task TestSizingOptions(App app, PanelLayoutState state, Dictionary<uint, DrivePanelWindow> panels,
        GlobalViewSettings views, string folder, int monitor)
    {
        var zone = Field<ZoneSettings>(app, "_zoneSettings");
        var original = zone.Clone(); var originalView = views.Capture();
        var panel = panels[1];
        try
        {
            zone.ResponsivePanels = false; Call(app, "ResetManualLayout");
            var shrink = state.Begin(new(ResizeEdges.Bottom), 0, 0);
            state.Drag(shrink, 0, Math.Min(700, state.Cell.Height) - state.Cell.Height);
            Call(app, "RepositionWindows");
            await Task.Delay(100);
            var heights = state.Panels().Select(rect => rect.Height).ToArray();
            Require(Math.Abs(heights[0] - heights[1]) <= 1, "fixed mode divides actual windows equally");
            Require((bool)Call(panel, "NavigateTo", folder, true)!, "fixed-mode fixture folder opens");
            await Task.Delay(100);
            Require(state.Panels().Select(rect => rect.Height).SequenceEqual(heights), "folder navigation preserves fixed sizes");
            foreach (var mode in new[] { GlobalViewMode.Icon, GlobalViewMode.Details })
            {
                views.Restore(new GlobalViewConfig { ViewMode = mode, IconSize = 24 });
                await Task.Delay(100);
                Require(state.Panels().Select(rect => rect.Height).SequenceEqual(heights), "view mode preserves fixed sizes");
                var view = (FrameworkElement)panel.FindName(mode == GlobalViewMode.Icon ? "IconItemsList" : "DetailsGrid");
                var scroll = Descendant<ScrollViewer>(view)!;
                var bar = Descendants<ScrollBar>(view).Single(item => item.IsVisible && item.Orientation == Orientation.Vertical);
                Require(Math.Abs(bar.ActualWidth - 8) < .1, "vertical scrollbar is eight DIPs wide in both views");
                await CheckScrolling(scroll, bar, vertical: true);
                SaveScreenshot(app, panels, state, monitor, mode == GlobalViewMode.Icon ? "fixed-icons" : "fixed-details");
                if (mode == GlobalViewMode.Details)
                {
                    var grid = (DataGrid)view; var column = grid.Columns[0]; var width = column.Width; var minimum = column.MinWidth;
                    try
                    {
                        column.MinWidth = 900; column.Width = new DataGridLength(900); await Task.Delay(100);
                        var horizontal = Descendants<ScrollBar>(view).Single(item => item.IsVisible && item.Orientation == Orientation.Horizontal);
                        Require(Math.Abs(horizontal.ActualHeight - 8) < .1, "horizontal scrollbar is also eight DIPs thin");
                        await CheckScrolling(scroll, horizontal, vertical: false);
                    }
                    finally { column.MinWidth = minimum; column.Width = width; }
                }
            }
            zone.ResponsivePanels = true; Call(app, "ResetManualLayout"); await Task.Delay(100);
            Require(state.Panels()[0].Height > state.Panels()[1].Height, "switching responsive mode uses open folder contents immediately");
            Console.WriteLine($"PASS sizing/scrollbars monitor {monitor}: fixed shares, folder/view changes, responsive switch, thin vertical/horizontal bars, page and thumb drag.");
        }
        finally
        {
            Click(panel, "HomeButton");
            zone.CopyFrom(original); views.Restore(originalView); Call(app, "ResetManualLayout");
            await Task.Delay(100);
        }
    }

    private static async Task CheckScrolling(ScrollViewer scroll, ScrollBar bar, bool vertical)
    {
        if (vertical) scroll.ScrollToTop(); else scroll.ScrollToLeftEnd();
        await Task.Delay(50);
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        var resizeType = typeof(App).Assembly.GetType("ExternalDrivePanel.LayoutResizeController")!;
        var scrollSource = resizeType.GetMethod("IsScrollBarSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Require((bool)scrollSource.Invoke(null, new object[] { track.Thumb })!
            && (bool)scrollSource.Invoke(null, new object[] { track.IncreaseRepeatButton })!
            && !(bool)scrollSource.Invoke(null, new object[] { scroll })!,
            "thin thumb/page areas take priority over panel resize grips");
        var command = vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand;
        Require(track.IncreaseRepeatButton.Command == command, "page area has correct orientation command");
        command.Execute(null, bar); await Task.Delay(50);
        Require((vertical ? scroll.VerticalOffset : scroll.HorizontalOffset) > 0, "clicking page area scrolls content");
        if (vertical) scroll.ScrollToTop(); else scroll.ScrollToLeftEnd();
        await Task.Delay(50);
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(vertical ? 0 : 20, vertical ? 20 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(vertical ? 0 : 20, vertical ? 20 : 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Task.Delay(50);
        Require((vertical ? scroll.VerticalOffset : scroll.HorizontalOffset) > 0, "dragging thin thumb scrolls content");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
