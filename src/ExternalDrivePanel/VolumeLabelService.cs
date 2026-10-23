using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ExternalDrivePanel;

public sealed record VolumeLabelInfo(uint Serial, string FileSystem, string Label);
public sealed record VolumeRenameResult(bool Cancelled, string ActualLabel, string? PreferredLabel);

/// <summary>Injectable OS boundary: tests must not relabel real disks or approve UAC.</summary>
public interface IVolumeLabelPlatform
{
    VolumeLabelInfo Read(string root);
    int SetLabel(string root, string label);
    Task<int?> ElevateAsync(string root, string label, uint expectedSerial, IntPtr owner);
}

public static class VolumeLabelService
{
    private const string HelperSwitch = "--rename-volume-label";
    public static string Validate(string label, string fileSystem = "NTFS")
    {
        // exFAT's volume-label entry also holds at most 11 UTF-16 code units.
        var shortLabel = fileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase)
            || fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
            || fileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
        if (label.Length > (shortLabel ? 11 : 32)) throw new ArgumentException($"볼륨 이름은 최대 {(shortLabel ? 11 : 32)}자입니다.");
        var fat = fileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
        var invalid = fat ? "\\/:*?\"<>|+=;[]," : "\\/:*?\"<>|";
        if (label.Any(ch => char.IsControl(ch) || invalid.Contains(ch))) throw new ArgumentException("볼륨 이름에 사용할 수 없는 문자가 있습니다.");
        return label;
    }

    // Preserve spelling only while it still describes the same actual volume name.
    // An external rename to a different name must immediately supersede the preference.
    public static string? MatchingPreference(string actualLabel, string? preferred) =>
        preferred is not null && string.Equals(actualLabel, preferred, StringComparison.OrdinalIgnoreCase) ? preferred : null;

    public static Task<VolumeRenameResult> RenameAsync(string root, string label, IntPtr owner = default, IVolumeLabelPlatform? platform = null) => Task.Run(async () =>
    {
        platform ??= new WindowsPlatform();
        var full = NormalizeRoot(root);
        var before = platform.Read(full);
        Validate(label, before.FileSystem);
        var error = platform.SetLabel(full, label);
        if (error is 5 or 1314) // ACCESS_DENIED / PRIVILEGE_NOT_HELD: ask Windows, never bypass UAC.
        {
            var elevated = await platform.ElevateAsync(full, label, before.Serial, owner);
            if (elevated is null || elevated == 1223) return new VolumeRenameResult(true, before.Label, null);
            error = elevated.Value;
        }
        if (error != 0) throw Failure(error);
        var after = platform.Read(full);
        if (after.Serial != before.Serial) throw Failure(1167); // Device disconnected/replaced while waiting.
        return new VolumeRenameResult(false, after.Label,
            after.Label != label ? MatchingPreference(after.Label, label) : null);
    });

    public static bool TryRunHelper(IReadOnlyList<string> args, out int exitCode, IVolumeLabelPlatform? platform = null)
    {
        exitCode = 0;
        if (args.Count == 0 || args[0] != HelperSwitch) return false;
        try
        {
            if (args.Count != 4 || !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var serial))
            { exitCode = 87; return true; }
            var root = NormalizeRoot(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
            var label = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
            platform ??= new WindowsPlatform();
            var volume = platform.Read(root);
            if (volume.Serial != serial) { exitCode = 1167; return true; }
            Validate(label, volume.FileSystem);
            exitCode = platform.SetLabel(root, label);
        }
        catch (Win32Exception ex) { exitCode = ex.NativeErrorCode; }
        catch (Exception ex) { Debug.WriteLine($"Volume label helper: {ex}"); exitCode = 87; }
        return true;
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var driveRoot = Path.GetPathRoot(full);
        if (driveRoot is null || !string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(driveRoot), StringComparison.OrdinalIgnoreCase)
            || driveRoot.Length != 3 || driveRoot[1] != ':')
            throw new ArgumentException("볼륨의 드라이브 루트 경로가 아닙니다.");
        return driveRoot;
    }
    private static Win32Exception Failure(int error) => new(error,
        $"볼륨 이름을 변경하지 못했습니다. {new Win32Exception(error).Message} (오류 {error})");

    private sealed class WindowsPlatform : IVolumeLabelPlatform
    {
        public VolumeLabelInfo Read(string root)
        {
            var info = new DriveService().GetVolumeInfo(root);
            return new(info.VolumeSerialNumber, new DriveInfo(root).DriveFormat, info.VolumeLabel);
        }
        public int SetLabel(string root, string label) => SetVolumeLabelW(root, label) ? 0 : Marshal.GetLastWin32Error();
        public async Task<int?> ElevateAsync(string root, string label, uint expectedSerial, IntPtr owner)
        {
            var host = Environment.ProcessPath ?? throw new IOException("프로그램 실행 경로를 찾을 수 없습니다.");
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = true, ErrorDialogParentHandle = owner, WorkingDirectory = AppContext.BaseDirectory
            };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var appAssembly = Path.Combine(AppContext.BaseDirectory, "ExternalDrivePanel.dll");
                if (!File.Exists(appAssembly)) throw new IOException("프로그램 어셈블리를 찾을 수 없습니다.");
                start.ArgumentList.Add(appAssembly);
            }
            else if (!Path.GetFileNameWithoutExtension(host).Equals("ExternalDrivePanel", StringComparison.OrdinalIgnoreCase))
                throw new IOException("배포된 ExternalDrivePanel 실행 파일에서 관리자 권한 요청을 사용할 수 있습니다.");
            // No shell scripts/commands, result files, or arbitrary file-write helpers.
            start.ArgumentList.Add(HelperSwitch);
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(root)));
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(label)));
            start.ArgumentList.Add(expectedSerial.ToString(CultureInfo.InvariantCulture));
            try
            {
                using var helper = Process.Start(start) ?? throw new IOException("관리자 권한 이름 변경을 시작하지 못했습니다.");
                await helper.WaitForExitAsync(); return helper.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetVolumeLabelW(string root, string label);
}
