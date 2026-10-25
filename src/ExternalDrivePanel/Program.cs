using Velopack;

namespace ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--development-exit", StringComparer.OrdinalIgnoreCase))
        { DevelopmentSession.RequestExit(); return; }
        // Never apply a pending update before our single-instance/file-writer guards.
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => UpdateStartupRegistration(remove: true))
            .OnFirstRun(_ => UpdateStartupRegistration(remove: false)).Run();
        var development = args.Contains("--development", StringComparer.OrdinalIgnoreCase);
        var app = development ? new App(new ConfigStore(DevelopmentSession.ConfigPath), development: true) : new App();
        app.InitializeComponent();
        app.Run();
    }
    private static void UpdateStartupRegistration(bool remove)
    {
        try
        {
            var startup = new StartupRegistration();
            if (remove || startup.IsEnabled) startup.SetEnabled(!remove);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Startup registration hook: {ex}"); }
    }
}
