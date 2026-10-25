using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ExternalDrivePanel;

public sealed record UsbReconnectTarget(string EjectedDeviceId, string UsbDeviceId, string HubDeviceId, uint Port);
public sealed record DeviceReconnectResult(int Candidates, int Error = 0, bool Cancelled = false);
public interface IDeviceReconnectPlatform
{
    IReadOnlyList<UsbReconnectTarget> FindCandidates();
    Task<int?> RequestAsync(IReadOnlyList<UsbReconnectTarget> targets, nint owner);
    int Cycle(UsbReconnectTarget expected);
}

/// <summary>Explicit user action only. Normal refresh never resets an active device.</summary>
public sealed class DeviceReconnectService(IDeviceReconnectPlatform? platform = null)
{
    public const string HelperSwitch = "--reconnect-ejected-usb";
    public const int MaximumTargets = 8;
    private readonly IDeviceReconnectPlatform _platform = platform ?? new WindowsUsbReconnectPlatform();

    public async Task<DeviceReconnectResult> ReconnectAsync(nint owner)
    {
        try
        {
            var targets = await Task.Run(_platform.FindCandidates);
            if (targets.Count == 0) return new(0);
            if (targets.Count > MaximumTargets || targets.Any(target => !IsValid(target))) return new(0, 87);
            // ShellExecute("runas") can wait for consent before returning a
            // process handle. Keep that wait off the WPF dispatcher as well.
            var result = await Task.Run(() => _platform.RequestAsync(targets, owner));
            return new(targets.Count, result ?? 0, result is null or 1223);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Record("refresh.usb.failed", ex);
            return new(0, ex is Win32Exception native ? native.NativeErrorCode : 50);
        }
    }

    public static bool IsValid(UsbReconnectTarget? target) => target is not null
        && ValidId(target.EjectedDeviceId) && ValidId(target.UsbDeviceId) && ValidId(target.HubDeviceId)
        && (target.EjectedDeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || target.EjectedDeviceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || target.EjectedDeviceId.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase))
        && target.UsbDeviceId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
        && !target.UsbDeviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
        && target.HubDeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && target.Port is >= 1 and <= 255;
    private static bool ValidId(string? value) => value is { Length: > 0 and < 200 }
        && !value.Any(char.IsControl) && value.Split('\\').Length == 3;

    public static string Encode(UsbReconnectTarget target) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target)));
    public static bool TryRunHelper(IReadOnlyList<string> args, out int exitCode, IDeviceReconnectPlatform? platform = null)
    {
        exitCode = 0;
        if (args.Count == 0 || args[0] != HelperSwitch) return false;
        try
        {
            if (args.Count < 2 || args.Count > MaximumTargets + 1) { exitCode = 87; return true; }
            var targets = args.Skip(1).Select(value => value.Length <= 4096
                ? JsonSerializer.Deserialize<UsbReconnectTarget>(Convert.FromBase64String(value)) : null).ToArray();
            if (targets.Any(target => !IsValid(target))
                || targets.Select(target => (target!.HubDeviceId.ToUpperInvariant(), target.Port)).Distinct().Count() != targets.Length)
            { exitCode = 87; return true; }
            platform ??= new WindowsUsbReconnectPlatform();
            foreach (var target in targets)
            {
                // Cycle revalidates identity, held-for-eject status, hub/port and
                // absence of active disks again inside the elevated process.
                exitCode = platform.Cycle(target!);
                if (exitCode != 0) break;
            }
        }
        catch (Exception ex) { DiagnosticLog.Record("refresh.helper.failed", ex); exitCode = 87; }
        return true;
    }

    internal static async Task<int?> ElevateAsync(IReadOnlyList<UsbReconnectTarget> targets, nint owner)
    {
        var host = Environment.ProcessPath ?? throw new IOException("실행 파일을 찾지 못했습니다.");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = true, ErrorDialogParentHandle = owner, WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Path.Combine(AppContext.BaseDirectory, "ExternalDrivePanel.dll");
            if (!File.Exists(assembly)) throw new IOException("실행 어셈블리를 찾지 못했습니다.");
            start.ArgumentList.Add(assembly);
        }
        else if (!Path.GetFileNameWithoutExtension(host).Equals("ExternalDrivePanel", StringComparison.OrdinalIgnoreCase))
            throw new IOException("배포된 앱에서 재연결할 수 있습니다.");
        start.ArgumentList.Add(HelperSwitch);
        foreach (var target in targets) start.ArgumentList.Add(Encode(target));
        try
        {
            using var helper = Process.Start(start) ?? throw new IOException("재연결을 시작하지 못했습니다.");
            await helper.WaitForExitAsync(); return helper.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
    }
}
