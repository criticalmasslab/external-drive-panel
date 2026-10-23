using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExternalDrivePanel;

public sealed class ConfigStore
{
    public string FilePath { get; }
    public string BackupPath => Path.ChangeExtension(FilePath, "previous.json");
    private readonly bool _keepBackup;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private readonly object _saveLock = new();
    private Task<bool> _pendingSave = Task.FromResult(true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public ConfigStore(string? filePath = null, bool keepBackup = true)
    {
        _keepBackup = keepBackup;
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ExternalDeskPanel", "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return ReadValidated(FilePath);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Config load: {ex}");
            DiagnosticLog.Record("config.load.invalid", ex);
            try { File.Move(FilePath, FilePath + ".bak", overwrite: true); }
            catch (Exception backupError) { Debug.WriteLine($"Config backup: {backupError}"); }
        }
        catch (Exception ex) { Debug.WriteLine($"Config load: {ex}"); DiagnosticLog.Record("config.load.failed", ex); }
        return new();
    }

    public bool Save(AppConfig config) => SaveAsync(config).GetAwaiter().GetResult();
    public static AppConfig ReadValidated(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new JsonException("설정 파일이 너무 큽니다. (최대 8MB)");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
            || !root.TryGetProperty("zone", out _) || !root.TryGetProperty("globalView", out _) || !root.TryGetProperty("drives", out _))
            throw new JsonException("최상위 설정 스키마가 올바르지 않습니다.");
        var config = root.Deserialize<AppConfig>(Options) ?? throw new JsonException("설정이 null입니다.");
        Validate(config); return config;
    }
    public Task<bool> FlushAsync() { lock (_saveLock) return _pendingSave; }

    public Task<bool> SaveAsync(AppConfig config)
    {
        try
        {
            Validate(config);
            // Snapshot on the caller's thread; the mutable config never crosses threads.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Options);
            if (bytes.Length > MaximumBytes) throw new JsonException("설정 파일이 너무 큽니다.");
            lock (_saveLock)
                return _pendingSave = _pendingSave.ContinueWith(_ => SaveBytes(bytes),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex) { Debug.WriteLine($"Config snapshot: {ex}"); DiagnosticLog.Record("config.snapshot.failed", ex); return Task.FromResult(false); }
    }

    private bool SaveBytes(byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var exists = File.Exists(FilePath);
            var validPrevious = false;
            if (exists)
            {
                // Saves are debounced; identical snapshots do no disk writes or backup rotation.
                if (new FileInfo(FilePath).Length <= MaximumBytes && File.ReadAllBytes(FilePath).AsSpan().SequenceEqual(bytes)) return true;
                if (_keepBackup)
                {
                    try { _ = ReadValidated(FilePath); validPrevious = true; }
                    catch (JsonException) { } // Never replace a good backup with externally corrupted JSON.
                }
            }
            var temporaryPath = FilePath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (exists) File.Replace(temporaryPath, FilePath, validPrevious ? BackupPath : null);
            else File.Move(temporaryPath, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Config save: {ex}");
            DiagnosticLog.Record("config.save.failed", ex);
            return false;
        }
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return !path.Split(['\\', '/']).Any(part => part == "..") && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    private static void Validate(AppConfig config)
    {
        if (config.Version != 1 || config.Zone is null || config.GlobalView is null || config.Drives is null
            || config.DriveOrder is null || config.IgnoredDrives is null
            || config.DriveOrder.Concat(config.IgnoredDrives).Any(serial => !uint.TryParse(serial, out _)))
            throw new JsonException("지원하지 않거나 올바르지 않은 설정 형식입니다.");
        var zone = config.Zone;
        if (zone.MonitorIndex < 0 || zone.GridCols is < 1 or > 50 || zone.GridRows is < 1 or > 50
            || zone.CellCol < 0 || zone.CellCol >= zone.GridCols || zone.CellRow < 0 || zone.CellRow >= zone.GridRows
            || !Enum.IsDefined(zone.SplitDirection)
            || new[] { zone.MarginAll, zone.MarginTop, zone.MarginBottom, zone.MarginLeft, zone.MarginRight }
                .Any(value => !double.IsFinite(value) || value < 0)
            || !Enum.IsDefined(config.GlobalView.ViewMode) || !double.IsFinite(config.GlobalView.IconSize)
            || config.GlobalView.IconSize is < 16 or > 64)
            throw new JsonException("설정 값이 허용 범위를 벗어났습니다.");
        foreach (var (serial, drive) in config.Drives)
        {
            if (!uint.TryParse(serial, out _) || drive is null || drive.Favorites is null
                || (drive.ViewMode is { } mode && !Enum.IsDefined(mode))
                || drive.LastKnownLabel is null || !DriveConfig.IsValidSortKey(drive.SortKey)
                || (drive.PreferredLabel is { } preferred && (preferred.Length > 32 || preferred.Any(char.IsControl)))
                || drive.Favorites.Any(favorite => favorite is null || !IsSafeRelativePath(favorite.RelativePath)
                    || favorite.DisplayName is null))
                throw new JsonException("올바르지 않은 드라이브 설정입니다.");
        }
    }
}
