using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace ExternalDrivePanel;

public sealed class DriveService
{
    private const int VolumeNameBufferLength = 261;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const uint FileAttributeHidden = 0x2;
    private const uint FileAttributeSystem = 0x4;

    public IReadOnlyList<string> ListDriveRoots() => Environment.GetLogicalDrives();

    public (uint VolumeSerialNumber, string VolumeLabel) GetVolumeInfo(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var volumeName = new StringBuilder(VolumeNameBufferLength);
        var fileSystemName = new StringBuilder(VolumeNameBufferLength);

        if (!GetVolumeInformationW(root, volumeName, volumeName.Capacity, out uint serialNumber,
                out _, out _, fileSystemName, fileSystemName.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"볼륨 정보를 읽을 수 없습니다: {root}");
        }

        return (serialNumber, volumeName.ToString());
    }

    public IReadOnlyList<DriveSnapshot> SnapshotDrives()
    {
        var snapshot = new Dictionary<uint, DriveSnapshot>();
        foreach (var root in ListDriveRoots())
        {
            try
            {
                var type = new DriveInfo(root).DriveType;
                // Exclude network/optical devices before attempting volume metadata reads.
                if (!DriveCoordinator.IsCandidate(root, type)) continue;
                var volume = GetVolumeInfo(root);
                snapshot[volume.VolumeSerialNumber] = new(volume.VolumeSerialNumber, root, volume.VolumeLabel, type);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                // 연결은 보이지만 현재 접근할 수 없는 볼륨은 다음 폴링에서 재시도한다.
            }
        }

        return snapshot.Values.ToList();
    }

    public bool IsVisibleFileSystemEntry(string path, bool showHiddenItems = false)
    {
        var attributes = GetFileAttributesW(path);
        return attributes != InvalidFileAttributes
               && (showHiddenItems || (attributes & (FileAttributeHidden | FileAttributeSystem)) == 0);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}
