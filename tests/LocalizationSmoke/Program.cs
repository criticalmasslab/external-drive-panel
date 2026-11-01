using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;
using Localization = ExternalDrivePanel.Localization;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "Panel-Languages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var entry in Localization.Translations)
            {
                Check(!string.IsNullOrWhiteSpace(entry.English), "translation not empty: " + entry.Id);
                Check(!entry.English.Any(c => c is >= '가' and <= '힣'), "English catalog: " + entry.Id);
                Check(CompositeFormat.Parse(entry.Korean).MinimumArgumentCount == CompositeFormat.Parse(entry.English).MinimumArgumentCount, "format arity: " + entry.Id);
                string[] Arguments(string text) => Regex.Matches(text, @"\{\d+(?:[^}]*)\}").Select(match => match.Value).Order().ToArray();
                Check(Arguments(entry.Korean).SequenceEqual(Arguments(entry.English)), "format tokens: " + entry.Id);
                Localization.SetLanguage("en"); Check(Localization.T(entry.Korean) == entry.English, "English lookup");
                Localization.SetLanguage("ko"); Check(Localization.T(entry.Korean) == entry.Korean, "Korean lookup");
            }
            Check(Localization.T(@"D:\사용자 폴더\My FILE.JPG") == @"D:\사용자 폴더\My FILE.JPG", "user paths unchanged");
            var culture = CultureInfo.CurrentCulture.Name;
            Localization.SetLanguage("en"); Check(CultureInfo.CurrentCulture.Name == culture, "numeric/date culture preserved");
            Check(ItemMenuModel.Build(new(false, false, false, true, true))[0].Label == "Open", "English menu model");
            Localization.SetLanguage("ko");
            var store = new ConfigStore(Path.Combine(temporary, "config.json"));
            Check(store.Save(new AppConfig()), "save default");
            File.WriteAllText(store.FilePath, File.ReadAllText(store.FilePath).Replace("  \"language\": \"ko\",\n", "").Replace("  \"language\": \"ko\",\r\n", ""));
            Check(store.Load().Language == "ko", "legacy config defaults Korean");
            var unknown = store.Load(); unknown.Language = "unknown"; Check(store.Save(unknown) && store.Load().Language == "ko", "unsupported language fallback");
            var root = Directory.CreateDirectory(Path.Combine(temporary, "drive")).FullName + "\\";
            Directory.CreateDirectory(Path.Combine(root, "사용자 Folder"));
            var config = new AppConfig { Language = "en", NotifyNewDrive = false, DriveOrder = ["1"],
                Drives = new() { ["1"] = new() { Registered = true, Favorites = [new() { RelativePath = "사용자 Folder", DisplayName = "사용자 Folder", IsDirectory = true }] } } };
            Check(store.Save(config) && store.Load().Language == "en", "English roundtrip");
            Console.WriteLine($"PASS catalog: {Localization.Translations.Count} translations, exact format tokens, no translated paths, legacy/default/fallback/roundtrip.");
            if (args.Contains("--logic-only")) return 0;
            var app = new App(store, () => [new DriveSnapshot(1, root, "MiXeD 사용자 Volume", DriveType.Removable)]);
            var code = 0;
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try
                {
                    Check(Localization.Language == "en", "startup loads language before windows");
                    var panel = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels")[1];
                    var bar = Field<TopBarWindow>(app, "_topBar");
                    var zone = Field<ZoneSettings>(app, "_zoneSettings");
                    Check(((Button)bar.FindName("SettingsButton")).ToolTip?.ToString() == "Screen area settings", "English main tooltip");
                    Check(((DataGrid)panel.FindName("DetailsGrid")).Columns[0].Header?.ToString() == "Name", "English detail header");
                    Check(panel.VolumeLabel == "MiXeD 사용자 Volume", "volume unchanged");
                    var favorite = Field<FavoriteCollection>(panel, "_favorites").Items[0];
                    Check(favorite.Name == "사용자 Folder", "favorite name unchanged");
                    var beforePath = Field<string>(panel, "_currentPath");
                    var preview = new QuickPreviewWindow { Owner = panel };
                    preview.Show();
                    await preview.ShowItemsAsync([new ItemMenuTarget(favorite.Path, true)], 0);
                    Check(((TextBlock)preview.FindName("InformationText")).Text.Contains("Size:"), "English preview information");
                    foreach (var monitor in new MonitorService().GetMonitors())
                    {
                        var calculator = new LayoutCalculator();
                        bar.SetPlacement(calculator.CalculateTopBar(calculator.CalculateCell(monitor, new ZoneSettings())));
                        var window = new ZoneSettingsWindow(zone, new MonitorService().GetMonitors(), new FakeStartup()) { Owner = bar };
                        window.DisableStartupForDevelopment();
                        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            try
                            {
                                Check(window.Title == "Screen area settings", "English settings title");
                                Capture(window, "settings-english-" + monitor.Left + ".png");
                                ((ComboBox)window.FindName("LanguageChoice")).SelectedValue = "ko";
                                Pump();
                                Check(window.Title == "화면 영역 설정", "settings live Korean");
                                Check(((DataGrid)panel.FindName("DetailsGrid")).Columns[0].Header?.ToString() == "이름", "existing panel live Korean");
                                Check(((Button)bar.FindName("SettingsButton")).ToolTip?.ToString() == "화면 영역 설정", "existing main tooltip live Korean");
                                Check(Field<string>(panel, "_currentPath") == beforePath, "navigation preserved");
                                Check(((TextBlock)preview.FindName("InformationText")).Text.Contains("크기:"), "open preview live translation without reload");
                                ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; window.Close(); }
                        }));
                        window.ShowDialog();
                        Check(Localization.Language == "en", "cancel restores language");
                    }
                    Capture(panel, "panel-english.png");
                    preview.Close();
                    var final = new ZoneSettingsWindow(zone, new MonitorService().GetMonitors(), new FakeStartup()) { Owner = bar };
                    final.Loaded += (_, _) => final.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ((ComboBox)final.FindName("LanguageChoice")).SelectedValue = "ko";
                        ((Button)final.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }));
                    final.ShowDialog();
                    await Task.Delay(600);
                    await store.FlushAsync();
                    Check(store.Load().Language == "ko", "close keeps and debounces language save");
                    Check(favorite.Path == Path.Combine(root, "사용자 Folder"), "favorite path preserved");
                    Console.WriteLine("PASS WPF: startup English, live settings/main/panel translation, Cancel rollback, Close persists, labels/paths/navigation intact.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                finally { app.Shutdown(); }
            }));
            app.InitializeComponent(); app.Run(); return code;
        }
        finally { Localization.SetLanguage("ko"); Directory.Delete(temporary, true); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(file);
    }
    private sealed class FakeStartup : IStartupRegistration { public bool IsEnabled { get; private set; } public void SetEnabled(bool enabled) => IsEnabled = enabled; }
}
