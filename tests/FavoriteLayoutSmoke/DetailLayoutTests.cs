using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ExternalDrivePanel;

internal static partial class Program
{
    private static async Task TestLocalDetails(App app, PanelLayoutState state, Dictionary<uint, DrivePanelWindow> panels, GlobalViewSettings views)
    {
        var original = views.Capture();
        try
        {
            views.Restore(new GlobalViewConfig { IconSize = 32 });
            Call(app, "ResetManualLayout"); await Task.Delay(100);
            var before = state.Panels()[0].Height;
            Click(panels[1], "ViewModeButton"); await Task.Delay(100);
            Require(panels[1].EffectiveViewMode == GlobalViewMode.Details && panels[2].EffectiveViewMode == GlobalViewMode.Icon,
                "local detail click changes only that panel");
            var after = state.Panels()[0].Height;
            Require(Math.Abs(after - before) >= 1, "local detail click triggers automatic redistribution without resetting settings");
            var grid = (DataGrid)panels[1].FindName("DetailsGrid");
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var header = Descendant<DataGridColumnHeadersPresenter>(grid)!;
            var profile = panels[1].FavoriteLayoutProfile;
            Require(row.ActualHeight == PanelListMetrics.RowHeight && header.ActualHeight == PanelListMetrics.HeaderHeight,
                "detail renderer and allocation share actual row and header heights");
            Require(Math.Abs(profile.Measure(state.Cell.Width).Content - row.ActualHeight * profile.Count * profile.DpiScaleY) < .1,
                "allocation uses the rendered detail rows, not an inflated estimate");
            var expected = 142 + 16 * profile.DpiScaleY + header.ActualHeight * profile.DpiScaleY
                + row.ActualHeight * profile.Count * profile.DpiScaleY;
            Require(Math.Abs(profile.Measure(state.Cell.Width).Desired - expected) < .1, "detail headers and content match real grid geometry");
            Console.WriteLine($"PASS local details: first panel {before} -> {after}px; shared row/header {row.ActualHeight}/{header.ActualHeight} DIPs, no settings reset.");
            SaveScreenshot(app, panels, state, 0, "mixed-details");
            Click(panels[2], "ViewModeButton"); await Task.Delay(100);
            Require(panels.Values.All(panel => panel.EffectiveViewMode == GlobalViewMode.Details), "both panels enter detail view");
            Require(state.Panels()[1].Height > state.Panels()[0].Height, "detail panels retain count-based proportions");
        }
        finally { views.SetViewMode(original.ViewMode); views.Restore(original); Call(app, "ResetManualLayout"); await Task.Delay(100); }
    }
}
