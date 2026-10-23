using System.IO;

namespace ExternalDrivePanel;

/// <summary>OS-owned file lease, scoped to the config path; helpers bypass normal app startup.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lease;
    private SingleInstanceGuard(FileStream lease) => _lease = lease;
    public static SingleInstanceGuard? TryAcquire(string configPath)
    {
        var path = Path.GetFullPath(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new(new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }
    public void Dispose() => _lease.Dispose();
}
