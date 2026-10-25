using System.Windows;
using System.Windows.Controls;
using ExternalDrivePanel;

internal static partial class Program
{
    private static async Task TestSearchLayout(App app, PanelLayoutState state, Dictionary<uint, DrivePanelWindow> panels,
        GlobalViewSettings views, string folder, int monitor)
    {
        var panel = panels[1];
        var zone = Field<ZoneSettings>(app, "_zoneSettings");
        var original = zone.Clone(); var originalView = views.Capture();
        try
        {
            foreach (var mode in new[] { GlobalViewMode.Icon, GlobalViewMode.Details })
            {
                views.SetViewMode(mode); await Task.Delay(100);
                var homeHeights = state.Panels().Select(rect => rect.Height).ToArray();
                await panel.SearchDriveAsync(".txt"); await Task.Delay(100);
                Require(panel.FavoriteCount == 2 && panel.LayoutItemCount == 32 && panel.FavoriteLayoutProfile.Count == 32,
                    "search uses its result count rather than favorites count");
                var fullHeight = state.Panels()[0].Height;
                Require(fullHeight > homeHeights[0], "many search results increase panel height automatically");
                var status = (Border)panel.FindName("SearchStatus");
                var statusText = (TextBlock)panel.FindName("SearchStatusText");
                var measured = panel.FavoriteLayoutProfile.ExtraHeaderHeight;
                Require(measured > 0 && Math.Abs(measured - status.ActualHeight) < 1,
                    "visible search status row is included in allocation");
                statusText.Text = string.Concat(Enumerable.Repeat("검색 안내 문구 ", 24)); await Task.Delay(100);
                Require(panel.FavoriteLayoutProfile.ExtraHeaderHeight > measured && state.Panels()[0].Height > fullHeight,
                    "wrapped status height changes reallocate space without changing result count");

                var results = Field<List<FileSystemEntry>>(panel, "_currentItems").ToArray();
                Call(panel, "ShowSearchSnapshot", new DriveSearchSnapshot(results.Take(12).ToArray(), 32, 0, false), true);
                await Task.Delay(100);
                Require(panel.LayoutItemCount == 12 && state.Panels()[0].Height < fullHeight,
                    "streamed search snapshots trigger redistribution before completion");
                Call(panel, "ShowSearchSnapshot", new DriveSearchSnapshot(results, 32, 0, false), false);
                await Task.Delay(100);
                Require(panel.LayoutItemCount == 32 && Math.Abs(state.Panels()[0].Height - fullHeight) <= 1,
                    "completed search restores full result allocation");
                SaveScreenshot(app, panels, state, monitor, mode == GlobalViewMode.Icon ? "search-icons" : "search-details");

                await panel.SearchDriveAsync("no-result-fixture"); await Task.Delay(100);
                Require(panel.LayoutItemCount == 0 && state.Panels()[0].Height < fullHeight,
                    "zero results shrink the panel while keeping the status row");
                await panel.SearchDriveAsync(""); await Task.Delay(100);
                Require(panel.LayoutItemCount == 2 && panel.FavoriteLayoutProfile.ExtraHeaderHeight == 0
                    && state.Panels().Select((rect, i) => Math.Abs(rect.Height - homeHeights[i])).All(delta => delta <= 1),
                    "clearing search restores home proportions and removes status allowance");

                Require((bool)Call(panel, "NavigateTo", folder, true)!, "search can start from folder view");
                await Task.Delay(100); var folderHeights = state.Panels().Select(rect => rect.Height).ToArray();
                await panel.SearchDriveAsync("Visible item 00"); await Task.Delay(100);
                Require(panel.LayoutItemCount == 1, "narrowed query updates the allocation count");
                await panel.SearchDriveAsync(""); await Task.Delay(100);
                Require(panel.LayoutItemCount == 33 && panel.FavoriteLayoutProfile.ExtraHeaderHeight == 0
                    && state.Panels().Select((rect, i) => Math.Abs(rect.Height - folderHeights[i])).All(delta => delta <= 1),
                    "clearing search restores the previously open folder allocation");
                Click(panel, "HomeButton"); await Task.Delay(100);

                zone.ResponsivePanels = false; Call(app, "ResetManualLayout"); await Task.Delay(100);
                var fixedHeights = state.Panels().Select(rect => rect.Height).ToArray();
                await panel.SearchDriveAsync(".txt"); await Task.Delay(100);
                Require(state.Panels().Select(rect => rect.Height).SequenceEqual(fixedHeights), "fixed mode ignores search result count");
                await panel.SearchDriveAsync("no-result-fixture"); await Task.Delay(100);
                Require(state.Panels().Select(rect => rect.Height).SequenceEqual(fixedHeights), "fixed mode stays equal for zero results");
                await panel.SearchDriveAsync(""); zone.ResponsivePanels = true; Call(app, "ResetManualLayout"); await Task.Delay(100);
                Require(Field<AppConfig>(app, "_config").Drives["1"].Favorites.Count == 2, "search allocation does not change saved favorites");
                Console.WriteLine($"PASS search layout monitor {monitor}, {mode}: live/completed/zero results, measured/wrapped status row, clear-to-home/folder, fixed mode unchanged.");
            }
        }
        finally
        {
            await panel.SearchDriveAsync(""); Click(panel, "HomeButton");
            zone.CopyFrom(original); views.SetViewMode(originalView.ViewMode); views.Restore(originalView); Call(app, "ResetManualLayout");
            await Task.Delay(100);
        }
    }
}
