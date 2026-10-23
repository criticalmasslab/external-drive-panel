using System.Globalization;
using System.IO;

namespace ExternalDrivePanel;

public sealed record DriveSnapshot(uint Serial, string Root, string Label, DriveType Type);
public sealed record DriveNotice(uint Serial, long Connection);
public sealed record DrivePlan(IReadOnlyList<DriveSnapshot> VisibleDrives, IReadOnlyList<DriveNotice> Notices, bool ConfigChanged);

/// <summary>Pure registration/polling decisions. Does not create windows or write files.</summary>
public sealed class DriveCoordinator
{
    private readonly AppConfig _config;
    private readonly HashSet<uint> _askedThisConnection = [];
    private readonly Dictionary<uint, long> _connections = [];
    private Dictionary<uint, DriveSnapshot> _connected = [];
    private long _nextConnection;

    public DriveCoordinator(AppConfig config) => _config = config;

    public IReadOnlyDictionary<uint, DriveSnapshot> Connected => _connected;
    public IReadOnlyList<string> RegisteredOrder => _config.DriveOrder
        .Concat(_config.Drives.Keys).Distinct(StringComparer.Ordinal)
        .Where(key => _config.Drives.TryGetValue(key, out var drive) && drive.Registered).ToList();
    public IReadOnlyList<DriveSnapshot> ManualCandidates => _connected.Values
        .Where(drive => !IsRegistered(drive.Serial)).OrderBy(drive => drive.Root, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Key(uint serial) => serial.ToString(CultureInfo.InvariantCulture);
    public static bool IsCandidate(string root, DriveType type) =>
        !string.Equals(root.TrimEnd('\\', '/'), "C:", StringComparison.OrdinalIgnoreCase)
        && type is not (DriveType.CDRom or DriveType.Network or DriveType.Ram);

    public bool IsRegistered(uint serial) => _config.Drives.TryGetValue(Key(serial), out var drive) && drive.Registered;

    public DrivePlan Evaluate(IEnumerable<DriveSnapshot> snapshot)
    {
        var connected = snapshot.Where(drive => IsCandidate(drive.Root, drive.Type))
            .GroupBy(drive => drive.Serial).ToDictionary(group => group.Key, group => group.First());
        foreach (var serial in _connections.Keys.Except(connected.Keys).ToList())
        {
            _connections.Remove(serial);
            _askedThisConnection.Remove(serial);
        }
        foreach (var serial in connected.Keys)
            if (!_connections.ContainsKey(serial)) _connections[serial] = ++_nextConnection;
        _connected = connected;

        var order = RegisteredOrder.ToList();
        var changed = !_config.DriveOrder.SequenceEqual(order);
        _config.DriveOrder = order;
        foreach (var drive in connected.Values.Where(drive => IsRegistered(drive.Serial)))
        {
            var saved = _config.Drives[Key(drive.Serial)];
            var preference = VolumeLabelService.MatchingPreference(drive.Label, saved.PreferredLabel);
            if (saved.PreferredLabel != preference) { saved.PreferredLabel = preference; changed = true; }
            if (saved.LastKnownLabel != drive.Label) { saved.LastKnownLabel = drive.Label; changed = true; }
        }
        var notices = new List<DriveNotice>();
        if (_config.NotifyNewDrive)
        {
            foreach (var drive in ManualCandidates)
                if (!_config.IgnoredDrives.Contains(Key(drive.Serial)) && _askedThisConnection.Add(drive.Serial))
                    notices.Add(new DriveNotice(drive.Serial, _connections[drive.Serial]));
        }
        var visible = order.Select(key => uint.Parse(key, CultureInfo.InvariantCulture))
            .Where(serial => connected.ContainsKey(serial) && !_config.Drives[Key(serial)].Hidden)
            .Select(serial => connected[serial]).ToList();
        return new DrivePlan(visible, notices, changed);
    }

    public bool CanPrompt(DriveNotice notice) => _config.NotifyNewDrive
        && _connections.TryGetValue(notice.Serial, out var connection) && connection == notice.Connection
        && !IsRegistered(notice.Serial) && !_config.IgnoredDrives.Contains(Key(notice.Serial));

    public bool Register(uint serial)
    {
        if (!_connected.TryGetValue(serial, out var connected) || IsRegistered(serial)) return false;
        var key = Key(serial);
        if (!_config.Drives.TryGetValue(key, out var drive)) _config.Drives[key] = drive = new();
        drive.Registered = true;
        drive.Hidden = false;
        drive.LastKnownLabel = connected.Label;
        _config.DriveOrder.RemoveAll(value => value == key);
        _config.DriveOrder.Add(key);
        _config.IgnoredDrives.RemoveAll(value => value == key);
        _askedThisConnection.Add(serial);
        return true;
    }

    public bool Ignore(DriveNotice notice)
    {
        if (!CanPrompt(notice)) return false;
        _config.IgnoredDrives.Add(Key(notice.Serial));
        return true;
    }

    public bool ToggleHidden(string key)
    {
        if (!_config.Drives.TryGetValue(key, out var drive) || !drive.Registered) return false;
        drive.Hidden = !drive.Hidden;
        return true;
    }

    public bool Unregister(string key)
    {
        if (!_config.Drives.TryGetValue(key, out var drive) || !drive.Registered) return false;
        _config.Drives.Remove(key);
        _config.DriveOrder.RemoveAll(value => value == key);
        var serial = uint.Parse(key, CultureInfo.InvariantCulture);
        if (_connected.ContainsKey(serial)) _askedThisConnection.Add(serial);
        return true;
    }

    public string DisplayName(string key)
    {
        _config.Drives.TryGetValue(key, out var saved);
        if (_connected.TryGetValue(uint.Parse(key, CultureInfo.InvariantCulture), out var drive))
        {
            var displayed = VolumeLabelService.MatchingPreference(drive.Label, saved?.PreferredLabel) ?? drive.Label;
            return drive.Root.TrimEnd('\\', '/') + (string.IsNullOrWhiteSpace(displayed) ? "" : " " + displayed);
        }
        var label = saved?.PreferredLabel ?? saved?.LastKnownLabel ?? "";
        return "(연결 안 됨) " + (string.IsNullOrWhiteSpace(label) ? "외장하드" : label);
    }
}
