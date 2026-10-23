using System.Windows;
using System.Windows.Controls;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Require(StartupRegistration.BuildCommand(@"C:\Program Files\Panel\ExternalDrivePanel.exe")
                    == "\"C:\\Program Files\\Panel\\ExternalDrivePanel.exe\" --startup", "quoted stable startup command");
                var monitors = new MonitorService().GetMonitors();
                Require(monitors.Count > 0, "monitor available");
                var fake = new StartupFake();
                var window = new ZoneSettingsWindow(new(), monitors, fake);
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var check = (CheckBox)window.FindName("StartupCheck");
                    Require(check.IsChecked == false, "startup defaults to current OS state");
                    check.IsChecked = true;
                    Require(fake.IsEnabled, "startup checkbox applies immediately");
                    ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                window.ShowDialog();
                Require(!fake.IsEnabled && fake.Writes == 2, "cancel restores original startup registration");
                window = new ZoneSettingsWindow(new(), monitors, fake);
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ((CheckBox)window.FindName("StartupCheck")).IsChecked = true;
                    ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                window.ShowDialog();
                Require(fake.IsEnabled, "close keeps startup choice");
                using var updates = new AutoUpdateService();
                updates.Start((_, _) => throw new Exception("Development build must not update"));
                Require(!updates.TryApplyAfterExit(restart: true), "uninstalled copy cannot restart for update");
                Console.WriteLine("PASS deployment smoke; real Run registry and user clipboard untouched.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run(); return result;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class StartupFake : IStartupRegistration
    {
        public bool IsEnabled { get; private set; }
        public int Writes { get; private set; }
        public void SetEnabled(bool enabled) { IsEnabled = enabled; Writes++; }
    }
}
