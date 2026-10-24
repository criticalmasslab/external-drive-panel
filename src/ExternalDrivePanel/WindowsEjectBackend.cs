using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ExternalDrivePanel;

/// <summary>Read-only discovery followed by exactly one removable-device PnP request.</summary>
public sealed class WindowsEjectBackend : IEjectBackend
{
    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    private sealed record Disk(uint DevInst, uint? Number);
    public EjectTarget Resolve(string root)
    {
        if (!EjectService.IsDriveRoot(root)) throw new IOException("드라이브 루트를 확인해주세요.");
        root = char.ToUpperInvariant(root[0]) + ":\\";
        var type = new DriveInfo(root).DriveType;
        if (type is not (DriveType.Fixed or DriveType.Removable)) throw new IOException("이 드라이브는 안전 제거 대상이 아닙니다.");
        var number = ReadDiskNumber($@"\\.\{root[..2]}");
        // Block OS/app/config storage, including a second partition on that disk.
        var protectedPaths = new[] { Environment.SystemDirectory, Environment.ProcessPath,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        foreach (var protectedRoot in protectedPaths.Where(path => !string.IsNullOrEmpty(path)).Select(path => Path.GetPathRoot(path!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!EjectService.IsDriveRoot(protectedRoot)) throw new IOException("시스템 저장장치를 확인하지 못해 안전 제거를 중단했습니다.");
            if (number == ReadDiskNumber($@"\\.\{protectedRoot![..2]}"))
                throw new IOException("Windows 또는 프로그램·설정이 있는 물리 디스크는 제거할 수 없습니다.");
        }
        var disks = EnumerateDisks();
        var disk = disks.SingleOrDefault(disk => disk.Number == number) ?? throw new IOException("드라이브에 해당하는 Windows 장치를 찾지 못했습니다.");
        var candidate = EjectSafetyPolicy.Select(Ancestors(disk.DevInst), node => disks.Any(other => other.DevInst != disk.DevInst && IsDescendant(other.DevInst, node)));
        var serial = new DriveService().GetVolumeInfo(root).VolumeSerialNumber;
        return new(root, number, candidate.DevInst, candidate.DeviceId, serial);
    }
    private static IEnumerable<EjectNode> Ancestors(uint node)
    {
        var visited = new HashSet<uint>();
        for (var depth = 0; depth < 12 && visited.Add(node); depth++)
        {
            var id = DeviceId(node);
            var service = StringProperty(node, 5);
            if (EjectSafetyPolicy.IsBoundary(id, service)) yield break;
            yield return new(node, id, service, Capabilities(node));
            if (CM_Get_Parent(out var parent, node, 0) != 0) break;
            node = parent;
        }
    }
    public EjectResult Request(EjectTarget target)
    {
        if (!string.Equals(DeviceId(target.DevInst), target.DeviceId, StringComparison.OrdinalIgnoreCase))
            return EjectResult.Failed("장치 식별 정보가 바뀌어 제거를 중단했습니다.");
        // Resolve has closed every volume/interface handle before this call.
        var name = new StringBuilder(260);
        var code = CM_Request_Device_EjectW(target.DevInst, out var veto, name, name.Capacity, 0);
        return DescribeResult(code, veto, name.ToString());
    }
    public static EjectResult DescribeResult(uint code, uint veto, string name = "")
    {
        if (code == 0) return EjectResult.Succeeded();
        var reason = veto switch
        {
            2 => "Windows가 장치의 열린 핸들을 정리 중입니다. 잠시 후 다시 시도해주세요.",
            3 => "프로그램이 이 장치를 사용 중이라고 Windows에 응답했습니다.",
            4 => "Windows 서비스가 이 장치를 사용 중입니다.",
            5 => "장치에 열린 파일 또는 폴더 핸들이 남아 있다고 Windows가 보고했습니다.",
            6 or 7 or 11 => "장치 또는 드라이버가 안전 제거 요청을 거부했습니다.",
            1 or 8 or 10 => "장치 또는 드라이버가 이 방식의 안전 제거를 지원하지 않습니다.",
            12 => "현재 사용자 세션에 장치 제거 권한이 없습니다. Windows의 하드웨어 안전 제거 메뉴를 사용해주세요.",
            13 => "장치 연결 상태가 바뀌었습니다. 연결 상태를 다시 확인해주세요.",
            _ when code == 0x33 => "Windows가 장치 제거 권한을 거부했습니다. 하드웨어 안전 제거 메뉴를 사용해주세요.",
            _ => "Windows가 안전 제거 요청을 완료하지 못했습니다."
        };
        if (veto is 3 or 4 or 7 or 11 && !string.IsNullOrWhiteSpace(name)) reason += "\n대상: " + name;
        DiagnosticLog.Record($"eject.pnp.cr{code:X}.veto{veto}");
        return EjectResult.Failed($"{reason}\n(PnP 코드 0x{code:X}, 거부 유형 {veto})");
    }
    private static uint ReadDiskNumber(string path)
    {
        using var handle = CreateFileW(path, 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "장치 정보를 조회할 수 없습니다.");
        if (!DeviceIoControl(handle, 0x002D1080, 0, 0, out var number, Marshal.SizeOf<StorageDeviceNumber>(), out var returned, 0)
            || returned < Marshal.SizeOf<StorageDeviceNumber>()) throw new Win32Exception(Marshal.GetLastWin32Error(), "물리 디스크 번호를 조회할 수 없습니다.");
        if (number.Type != 7) throw new IOException("일반 디스크 장치가 아닙니다.");
        return number.Number;
    }
    private static List<Disk> EnumerateDisks()
    {
        var guid = DiskInterface;
        using var set = SetupDiGetClassDevsW(ref guid, 0, 0, 0x12);
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var disks = new List<Disk>();
        for (uint index = 0; index < 256; index++)
        {
            var entry = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref entry))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) return disks;
                throw new Win32Exception(error);
            }
            var info = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
            _ = SetupDiGetDeviceInterfaceDetailW(set, ref entry, 0, 0, out var size, ref info);
            if (size is < 8 or > 65536) throw new IOException("장치 인터페이스 정보가 올바르지 않습니다.");
            var detail = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref entry, detail, size, out _, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var path = Marshal.PtrToStringUni(detail + 4)!;
                uint? number = null;
                try { number = ReadDiskNumber(path); } catch (Win32Exception) { } catch (IOException) { }
                disks.Add(new(info.DevInst, number));
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        throw new IOException("장치 수가 안전 조회 범위를 초과했습니다.");
    }
    private static bool IsDescendant(uint node, uint ancestor)
    {
        var visited = new HashSet<uint>();
        for (var depth = 0; depth < 32 && visited.Add(node); depth++)
        {
            if (node == ancestor) return true;
            if (DeviceId(node).StartsWith("HTREE\\ROOT\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (CM_Get_Parent(out var parent, node, 0) != 0) throw new IOException("다른 디스크의 연결 계층을 확인하지 못했습니다.");
            node = parent;
        }
        throw new IOException("장치 계층을 안전하게 확인할 수 없습니다.");
    }
    private static string DeviceId(uint node)
    {
        var value = new StringBuilder(200);
        if (CM_Get_Device_IDW(node, value, value.Capacity, 0) != 0) throw new IOException("장치 식별 정보를 읽지 못했습니다.");
        return value.ToString();
    }
    private static uint Capabilities(uint node)
    {
        var size = 4u;
        if (CM_Get_DevNode_Registry_PropertyW(node, 0x10, out var type, out uint value, ref size, 0) != 0 || type != 4 || size != 4)
            throw new IOException("장치 제거 가능 여부를 확인할 수 없습니다.");
        return value;
    }
    private static string StringProperty(uint node, uint property)
    {
        var value = new StringBuilder(256); var size = 512u;
        if (CM_Get_DevNode_Registry_PropertyTextW(node, property, out var type, value, ref size, 0) != 0 || type != 1)
            throw new IOException("장치 드라이버 정보를 확인할 수 없습니다.");
        return value.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct StorageDeviceNumber { public uint Type, Number, Partition; }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
    private sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceInfoSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, int inputSize, out StorageDeviceNumber output, int outputSize, out int returned, nint overlapped);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern DeviceInfoSet SetupDiGetClassDevsW(ref Guid guid, nint enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoSet set, nint info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetailW(DeviceInfoSet set, ref InterfaceData data, nint detail, uint size, out uint required, ref DeviceInfoData info);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Request_Device_EjectW(uint node, out uint veto, StringBuilder name, int length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, out uint value, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Registry_PropertyTextW(uint node, uint property, out uint type, StringBuilder value, ref uint size, uint flags);
}
