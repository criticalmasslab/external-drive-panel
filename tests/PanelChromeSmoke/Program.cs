using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var monitorIndex = 0;
                foreach (var monitor in new MonitorService().GetMonitors())
                {
                    var viewSettings = new GlobalViewSettings();
                    var panel = new DrivePanelWindow(root, "Chrome test", viewSettings, new EjectService());
                    ((TextBlock)panel.FindName("TitleText")).Text = "D: 외장하드 아주 긴 볼륨 이름으로 말줄임 표시 확인";
                    var globalBar = new TopBarWindow(viewSettings);
                    globalBar.SetPlacement(new LayoutRect(monitor.Left + 30, monitor.Top + 5, 480, LayoutCalculator.TopBarHeight));
                    globalBar.Show();
                    panel.SetPlacement(new LayoutRect(monitor.Left + 30, monitor.Top + 40, 480, 500));
                    panel.Show();
                    foreach (var width in new[] { 360, 400, 480, 760 })
                    {
                        panel.SetPlacement(new LayoutRect(monitor.Left + 30, monitor.Top + 40, width, 500));
                        await panel.Dispatcher.InvokeAsync(() => panel.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                        var dpi = VisualTreeHelper.GetDpi(panel);
                        var header = (Border)panel.FindName("PanelHeader");
                        var toolbar = (Border)panel.FindName("FileToolbar");
                        var capacity = (VolumeCapacityBar)panel.FindName("CapacityBar");
                        Require(Math.Abs(capacity.ActualHeight * dpi.DpiScaleY - 24) < 1
                            && Math.Abs(Bounds(panel, "CapacityBar").Top - header.ActualHeight) < 1
                            && Math.Abs(Bounds(panel, "FileToolbar").Top - Bounds(panel, "CapacityBar").Bottom) < 1,
                            "24px capacity bar sits directly between header and toolbar");
                        Require(Math.Abs(header.ActualHeight * dpi.DpiScaleY - LayoutCalculator.TopBarHeight) < .1
                            && Math.Abs(toolbar.ActualHeight * dpi.DpiScaleY - LayoutCalculator.TopBarHeight) < .1, "header and toolbar match global bar height");
                        var title = Bounds(panel, "TitleText");
                        var back = Bounds(panel, "BackButton");
                        var details = Bounds(panel, "ViewModeButton");
                        Require(panel.FindName("DriveRootButton") is null, "separate drive root button removed");
                        Require(((TextBlock)panel.FindName("TitleText")).TextTrimming == TextTrimming.CharacterEllipsis, "long title uses ellipsis");
                        foreach (var name in new[] { "BackButton", "ForwardButton", "UpButton", "AddFavoritesButton", "ViewModeButton" })
                        {
                            var bounds = Bounds(panel, name);
                            Require(Math.Abs(bounds.Width * dpi.DpiScaleX - 34) < .1 && Math.Abs(bounds.Height * dpi.DpiScaleY - 34) < .1,
                                "panel header buttons retain 34 physical pixel squares");
                        }
                        await globalBar.Dispatcher.InvokeAsync(() => globalBar.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                        var globalDpi = VisualTreeHelper.GetDpi(globalBar);
                        foreach (var name in new[] { "HiddenButton", "ViewModeButton" })
                        {
                            var bounds = Bounds(globalBar, name);
                            Require(Math.Abs(bounds.Width * globalDpi.DpiScaleX - 34) < .1 && Math.Abs(bounds.Height * globalDpi.DpiScaleY - 34) < .1,
                                "global and panel header buttons share the same physical size");
                        }
                        Require(Math.Abs((panel.ActualWidth - details.Right) * dpi.DpiScaleX
                            - (globalBar.ActualWidth - Bounds(globalBar, "ViewModeButton").Right) * globalDpi.DpiScaleX) < .1,
                            "last icons share the exact same right inset");
                        Require(Math.Abs(details.Top * dpi.DpiScaleY - Bounds(globalBar, "ViewModeButton").Top * globalDpi.DpiScaleY) < .1,
                            "main and panel icons share the same vertical inset");
                        Require(title.Left < back.Left && title.Right <= back.Left + 1 && Math.Abs((title.Top + title.Bottom) / 2 - (back.Top + back.Bottom) / 2) < 1,
                            "left title and right navigation share a header without overlap");
                        Require(details.Right <= panel.ActualWidth && Math.Abs(back.Top - details.Top) < .1, "header navigation stays on one row");
                        var buttons = new[] { "NewItemButton", "CutButton", "CopyButton", "PasteButton", "RenameButton", "ShareButton", "DeleteButton" }.Select(name => Bounds(panel, name)).ToArray();
                        Require(buttons.All(rect => Math.Abs(rect.Top - buttons[0].Top) < .1 && rect.Left >= 0 && rect.Right <= panel.ActualWidth), "every file action stays visible on one row");
                        Require(buttons.Zip(buttons.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left + .1), "file buttons never overlap");
                        Require(((Button)panel.FindName("CopyButton")).BorderThickness == new Thickness(0), "file toolbar remains borderless");
                        foreach (var name in new[] { "NewItemButton", "CutButton", "CopyButton", "PasteButton", "RenameButton", "ShareButton", "DeleteButton" })
                        {
                            var button = (Button)panel.FindName(name);
                            Require(Math.Abs(button.ActualWidth * dpi.DpiScaleX - 34) < .1, "file toolbar restores original 34px button width");
                            Require(button.Content is SymbolIcon icon && icon.ActualWidth * dpi.DpiScaleX >= 21
                                && button.ToolTip is string && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)),
                                "all file actions use a single consistent icon with tooltip and accessible name");
                        }
                        var searchIcon = (SymbolIcon)((Button)panel.FindName("SearchButton")).Content;
                        Require(Math.Abs(searchIcon.ActualWidth * dpi.DpiScaleX - 22) < .1, "magnifier matches the 22px toolbar icon family");
                        Require(Bounds(panel, "BreadcrumbPanel").Top >= header.ActualHeight + toolbar.ActualHeight, "address below the two compact rows");
                        var addressBounds = Bounds(panel, "AddressBox");
                        var contentTop = Bounds(panel, "ContentArea").Top;
                        var nested = Path.Combine(root, "하위 폴더", new string('가', 45));
                        Directory.CreateDirectory(nested);
                        foreach (var path in new[] { root, nested })
                        {
                            typeof(DrivePanelWindow).GetMethod("NavigateTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                                .Invoke(panel, [path, true]);
                            await panel.Dispatcher.InvokeAsync(() => panel.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                            var navigatedAddress = Bounds(panel, "AddressBox");
                            Require(Math.Abs(navigatedAddress.Top - addressBounds.Top) < .1 && navigatedAddress.Height == addressBounds.Height
                                && Math.Abs(Bounds(panel, "ContentArea").Top - contentTop) < .1,
                                "root and long nested paths never change address height or shift the file list");
                        }
                        ((Button)panel.FindName("HomeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await panel.Dispatcher.InvokeAsync(() => panel.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                        Require(Bounds(panel, "AddressBox").Height == addressBounds.Height && ((Border)panel.FindName("AddressBox")).Height == 32,
                            "favorites return retains the same fixed address height");
                        if (monitorIndex == 0 && width == 480)
                        {
                            var image = new RenderTargetBitmap((int)Math.Round(panel.ActualWidth * dpi.DpiScaleX), (int)Math.Round(panel.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                            image.Render(panel);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                            using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "chrome-preview.png")); encoder.Save(stream);
                        }
                        Console.WriteLine($"PASS monitor {monitorIndex}, DPI {dpi.DpiScaleY}, width {width}px: 42px rows, 34px buttons, vector icons and aligned right/vertical insets.");
                    }
                    var modeButton = (Button)panel.FindName("ViewModeButton");
                    modeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(panel.EffectiveViewMode == GlobalViewMode.Details && ((SymbolIcon)modeButton.Content).Kind == "List", "single panel button toggles to details");
                    modeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(panel.EffectiveViewMode == GlobalViewMode.Icon && ((SymbolIcon)modeButton.Content).Kind == "Grid", "single panel button toggles back to icons");
                    var globalModeButton = (Button)globalBar.FindName("ViewModeButton");
                    globalModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(viewSettings.ViewMode == GlobalViewMode.Details && ((SymbolIcon)globalModeButton.Content).Kind == "List", "single global button toggles to details");
                    globalModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(viewSettings.ViewMode == GlobalViewMode.Icon && ((SymbolIcon)globalModeButton.Content).Kind == "Grid", "single global button toggles back to icons");
                    ((Button)panel.FindName("DriveTitleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(((ListBox)panel.FindName("IconItemsList")).Visibility == Visibility.Visible,
                        "drive title click leaves favorites and opens drive root");
                    var currentPath = (string)typeof(DrivePanelWindow).GetField("_currentPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                    Require(string.Equals(Path.TrimEndingDirectorySeparator(currentPath), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase),
                        "drive title navigates to exactly this drive root");
                    panel.Close(); globalBar.Close(); monitorIndex++;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); Directory.Delete(root, true); }
        }));
        app.Run(); return result;
    }
    private static Rect Bounds(FrameworkElement owner, string name)
    {
        var element = (FrameworkElement)owner.FindName(name);
        return element.TransformToAncestor(owner).TransformBounds(new Rect(element.RenderSize));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
