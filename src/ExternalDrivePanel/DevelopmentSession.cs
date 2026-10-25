using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ExternalDrivePanel;

/// <summary>Opt-in local preview. Never replaces the installed application or its configuration.</summary>
public sealed class DevelopmentSession : IDisposable
{
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExternalDeskPanel-Development", "config.json");
    private static string EventName => @"Local\ExternalDrivePanel.Development." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigPath.ToUpperInvariant())))[..24];
    private readonly EventWaitHandle _exit = new(false, EventResetMode.AutoReset, EventName);
    private readonly RegisteredWaitHandle _registration;

    public DevelopmentSession(Action requestExit) => _registration = ThreadPool.RegisterWaitForSingleObject(
        _exit, (_, _) => requestExit(), null, Timeout.Infinite, executeOnlyOnce: false);

    public static bool RequestExit()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName, out var signal)) return false;
        using (signal) return signal.Set();
    }

    public void Dispose() { _registration.Unregister(null); _exit.Dispose(); }
}
