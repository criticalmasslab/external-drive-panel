using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExternalDrivePanel;

public sealed record VolumeCapacitySnapshot(ulong TotalBytes, ulong FreeBytes, ulong AvailableBytes)
{
    public ulong UsedBytes => TotalBytes - Math.Min(TotalBytes, FreeBytes);
    public double UsedFraction => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
    public bool IsHighUsage => UsedFraction > .8;
    public string Caption => $"{FormatBytes(TotalBytes)} 중 {FormatBytes(Math.Min(TotalBytes, AvailableBytes))} 사용 가능 · {UsedFraction:P1} 사용";
    public string Description => $"전체 {FormatBytes(TotalBytes)} / 사용 {FormatBytes(UsedBytes)} / 사용 가능 {FormatBytes(Math.Min(TotalBytes, AvailableBytes))} ({UsedFraction:P1} 사용)";

    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    public static VolumeCapacitySnapshot? Read(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && GetDiskFreeSpaceExW(root, out var available, out var total, out var free) && total > 0
                ? new(total, free, available) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { System.Diagnostics.Debug.WriteLine(ex); return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}

/// <summary>One non-animated row; star columns resize without SizeChanged handlers or repeated I/O.</summary>
public sealed class VolumeCapacityBar : Grid, IDisposable
{
    private readonly ColumnDefinition _used = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _free = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly Border _fill = new();
    private readonly TextBlock _caption = new() { Text = "용량 확인 중…", Margin = new Thickness(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
        Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x32, 0x46)) };
    private readonly DispatcherTimer _timer;
    private bool _busy, _disposed;
    private bool _suspended;
    private int _version;
    public string RootPath { get; set; } = "";
    public VolumeCapacitySnapshot? Snapshot { get; private set; }

    public VolumeCapacityBar()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xEC, 0xF1)); ClipToBounds = true;
        ColumnDefinitions.Add(_used); ColumnDefinitions.Add(_free);
        Children.Add(_fill); SetColumnSpan(_caption, 2); Children.Add(_caption);
        _caption.SetResourceReference(TextBlock.FontSizeProperty, "CapacityFontSize");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { if (!_disposed && !_suspended) { _timer.Start(); await RefreshAsync(); } };
        Unloaded += (_, _) => { _timer.Stop(); _version++; };
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _suspended || _busy || string.IsNullOrEmpty(RootPath)) return;
        _busy = true; var version = _version; var root = RootPath;
        try
        {
            var snapshot = await Task.Run(() => VolumeCapacitySnapshot.Read(root));
            if (!_disposed && version == _version && RootPath == root) ApplySnapshot(snapshot);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { _busy = false; }
    }

    public void ApplySnapshot(VolumeCapacitySnapshot? snapshot)
    {
        if (_disposed) return;
        if (snapshot?.TotalBytes == 0) snapshot = null;
        if (Snapshot == snapshot && _caption.Text != "용량 확인 중…") return;
        Snapshot = snapshot;
        var fraction = snapshot?.UsedFraction ?? 0;
        _used.Width = new GridLength(fraction, GridUnitType.Star);
        _free.Width = new GridLength(1 - fraction, GridUnitType.Star);
        // Soft fills keep the same dark text legible over both used/free portions.
        _fill.Background = new SolidColorBrush(snapshot?.IsHighUsage == true
            ? Color.FromRgb(0xE6, 0x8E, 0x8E) : Color.FromRgb(0x86, 0xBE, 0xE8));
        _caption.Text = snapshot?.Caption ?? "용량 정보를 읽을 수 없습니다";
        ToolTip = snapshot?.Description ?? "드라이브가 연결되어 있고 접근 가능한지 확인해 주세요.";
        System.Windows.Automation.AutomationProperties.SetName(this, _caption.Text);
    }

    public async Task SuspendAsync()
    {
        _suspended = true; _version++; _timer.Stop();
        while (_busy) await Task.Delay(10);
    }
    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false;
        if (IsLoaded) _timer.Start();
    }
    public void Dispose() { _disposed = true; _version++; _timer.Stop(); }
}
