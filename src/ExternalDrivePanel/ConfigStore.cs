using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExternalDrivePanel;

public sealed class ConfigStore
{
    public string FilePath { get; }
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public ConfigStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ExternalDeskPanel", "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
                || !root.TryGetProperty("zone", out _) || !root.TryGetProperty("globalView", out _)
                || !root.TryGetProperty("drives", out _))
                throw new JsonException("최상위 설정 스키마가 올바르지 않습니다.");
            var config = root.Deserialize<AppConfig>(Options)
                ?? throw new JsonException("설정이 null입니다.");
            Validate(config);
            return config;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Config load: {ex}");
            try { File.Move(FilePath, FilePath + ".bak", overwrite: true); }
            catch (Exception backupError) { Debug.WriteLine($"Config backup: {backupError}"); }
        }
        catch (Exception ex) { Debug.WriteLine($"Config load: {ex}"); }
        return new();
    }

    public bool Save(AppConfig config)
    {
        try
        {
            Validate(config);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Options);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporaryPath = FilePath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
            else File.Move(temporaryPath, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Config save: {ex}");
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
                || drive.Favorites.Any(favorite => favorite is null || !IsSafeRelativePath(favorite.RelativePath)
                    || favorite.DisplayName is null))
                throw new JsonException("올바르지 않은 드라이브 설정입니다.");
        }
    }
}
