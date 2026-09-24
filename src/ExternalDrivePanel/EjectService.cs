using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExternalDrivePanel;

public sealed class EjectService
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804;
    private const uint IoctlStorageEjectMedia = 0x002D4808;

    public EjectResult TryEject(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return EjectResult.Failed("올바른 드라이브 경로가 아닙니다.");
        }

        var volumePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
        using var handle = CreateFileW(volumePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Failure("볼륨 핸들을 열 수 없습니다");
        }

        if (!DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            return Failure("볼륨을 잠글 수 없습니다. 사용 중인 파일이나 프로그램을 닫아주세요");
        }

        if (!DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            return Failure("볼륨을 분리할 수 없습니다");
        }

        var removal = new PreventMediaRemovalData { PreventMediaRemoval = false };
        if (!DeviceIoControlMedia(handle, IoctlStorageMediaRemoval, ref removal,
                Marshal.SizeOf<PreventMediaRemovalData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            return Failure("미디어 제거 잠금을 해제할 수 없습니다");
        }

        if (!DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            return Failure("장치가 소프트웨어 꺼내기를 지원하지 않거나 꺼내기에 실패했습니다");
        }

        return EjectResult.Succeeded();
    }

    private static EjectResult Failure(string step)
    {
        var error = Marshal.GetLastWin32Error();
        return EjectResult.Failed($"{step}. {new Win32Exception(error).Message} (오류 {error})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreventMediaRemovalData
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool PreventMediaRemoval;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlMedia(
        SafeFileHandle device,
        uint controlCode,
        ref PreventMediaRemovalData inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

public sealed record EjectResult(bool Success, string? FailureReason)
{
    public static EjectResult Succeeded() => new(true, null);
    public static EjectResult Failed(string reason) => new(false, reason);
}