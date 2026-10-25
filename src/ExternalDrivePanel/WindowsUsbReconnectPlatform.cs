using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ExternalDrivePanel;

/// <summary>Read-only discovery; one validated USB storage port cycle in the UAC helper only.</summary>
public sealed class WindowsUsbReconnectPlatform : IDeviceReconnectPlatform
{
    private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    public IReadOnlyList<UsbReconnectTarget> FindCandidates()
    {
        using var all = SetupDiGetClassDevsW(0, null, 0, 4); // Include held-for-eject devnodes.
        var targets = new List<UsbReconnectTarget>();
        foreach (var node in Enumerate(all, 8192))
        {
            if (CM_Get_DevNode_Status(out var status, out var problem, node, 0) != 0 || (status & 0x400) == 0 || problem != 47) continue;
            try
            {
                var target = Inspect(DeviceId(node));
                _ = HubPath(target.HubDeviceId); // Validate the active hub interface without opening it for writes.
                if (!targets.Any(item => item.HubDeviceId.Equals(target.HubDeviceId, StringComparison.OrdinalIgnoreCase) && item.Port == target.Port)) targets.Add(target);
            }
            catch (IOException) { DiagnosticLog.Record("refresh.usb.unsupported"); }
            if (targets.Count > DeviceReconnectService.MaximumTargets) throw new IOException("재연결 대상이 너무 많습니다.");
        }
        return targets;
    }
    public Task<int?> RequestAsync(IReadOnlyList<UsbReconnectTarget> targets, nint owner) => DeviceReconnectService.ElevateAsync(targets, owner);

    private static UsbReconnectTarget Inspect(string id)
    {
        var node = Locate(id); var chain = new List<UsbReconnectNode>(); var visited = new HashSet<uint>();
        for (var depth = 0; depth < 16 && visited.Add(node); depth++)
        {
            var currentId = DeviceId(node);
            if (currentId.StartsWith("HTREE\\ROOT", StringComparison.OrdinalIgnoreCase) || currentId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) break;
            if (CM_Get_DevNode_Status(out var status, out var problem, node, 0) != 0) throw new IOException("장치 상태를 확인하지 못했습니다.");
            var service = StringProperty(node, 5);
            var isUsbStorage = currentId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
                && (service.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) || service.Equals("UASPStor", StringComparison.OrdinalIgnoreCase));
            chain.Add(new(currentId, service, status, problem, isUsbStorage ? DwordProperty(node, 0x1D) : 0,
                isUsbStorage ? DwordProperty(node, 0x10) : 0));
            if (service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)) break;
            if (CM_Get_Parent(out var parent, node, 0) != 0) break;
            node = parent;
        }
        return UsbReconnectPolicy.Select(chain, usbId => HasStartedDescendants(Locate(usbId)), PortMatches);
    }

    private static bool PortMatches(string hubId, string usbId, uint port)
    {
        if (CM_Get_Child(out var child, Locate(hubId), 0) != 0) return false;
        var found = false; var visited = new HashSet<uint>();
        for (var index = 0; index < 1024 && visited.Add(child); index++)
        {
            var id = DeviceId(child);
            if (DwordProperty(child, 0x1D) == port)
            {
                if (!id.Equals(usbId, StringComparison.OrdinalIgnoreCase)) return false;
                found = true;
            }
            var result = CM_Get_Sibling(out var sibling, child, 0);
            if (result == 0x0D) return found; // CR_NO_SUCH_DEVNODE: end of sibling chain.
            if (result != 0) throw new IOException("USB 포트 연결 상태를 확인하지 못했습니다.");
            child = sibling;
        }
        throw new IOException("USB 장치 트리 조회 범위를 초과했습니다.");
    }
    private static bool HasStartedDescendants(uint parent)
    {
        // USB storage may expose an optical reader or other LUN as well as a
        // disk. Protect every started child, not only DiskDrive-class nodes.
        var pending = new Stack<uint>(); var visited = new HashSet<uint>();
        AddChildren(parent);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node) || visited.Count > 1024) throw new IOException("장치 트리 조회 범위를 초과했습니다.");
            if (CM_Get_DevNode_Status(out var status, out _, node, 0) != 0) throw new IOException("장치 상태를 확인하지 못했습니다.");
            if ((status & 8) != 0) return true;
            AddChildren(node);
        }
        return false;

        void AddChildren(uint node)
        {
            var result = CM_Get_Child(out var child, node, 0);
            if (result == 0x0D) return;
            if (result != 0) throw new IOException("장치 트리를 확인하지 못했습니다.");
            for (var index = 0; index < 1024; index++)
            {
                pending.Push(child);
                if (pending.Count > 1024) break;
                result = CM_Get_Sibling(out var next, child, 0);
                if (result == 0x0D) return;
                if (result != 0) throw new IOException("장치 트리를 확인하지 못했습니다.");
                child = next;
            }
            throw new IOException("장치 트리 조회 범위를 초과했습니다.");
        }
    }
    public int Cycle(UsbReconnectTarget expected)
    {
        if (!DeviceReconnectService.IsValid(expected)) return 87;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        try
        {
            if (Inspect(expected.EjectedDeviceId) != expected) return 1167;
            using var hub = CreateFileW(HubPath(expected.HubDeviceId), 0x40000000, 3, 0, 3, 0, 0);
            if (hub.IsInvalid) return Marshal.GetLastWin32Error();
            if (Inspect(expected.EjectedDeviceId) != expected) return 1167; // Identity/port must survive the UAC wait and hub open.
            var data = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(data, unchecked((int)expected.Port)); Marshal.WriteInt32(data + 4, 0);
                // FILE_DEVICE_USB(0x22), function USB_HUB_CYCLE_PORT(273), METHOD_BUFFERED.
                if (!DeviceIoControl(hub, 0x00220444, data, 8, data, 8, out _, 0)) return Marshal.GetLastWin32Error();
                return Marshal.ReadInt32(data + 4) == 0 ? 0 : 31;
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        catch (Win32Exception ex) { return ex.NativeErrorCode; }
        catch (IOException) { return 50; }
    }
    private static string HubPath(string hubId)
    {
        var guid = HubInterface;
        if (CM_Get_Device_Interface_List_SizeW(out var size, ref guid, hubId, 0) != 0 || size is < 2 or > 65536)
            throw new IOException("USB 허브 인터페이스를 확인하지 못했습니다.");
        var buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid, hubId, buffer, size, 0) != 0)
            throw new IOException("USB 허브 인터페이스를 확인하지 못했습니다.");
        var paths = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length != 1 || !paths[0].StartsWith(@"\\?\", StringComparison.Ordinal)
            || !paths[0].Contains(HubInterface.ToString("B"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("USB 허브 인터페이스를 식별하지 못했습니다.");
        return paths[0];
    }
    private static IEnumerable<uint> Enumerate(DeviceSet set, uint limit)
    {
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        for (uint i = 0; i < limit; i++)
        {
            var data = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            if (!SetupDiEnumDeviceInfo(set, i, ref data))
            {
                var error = Marshal.GetLastWin32Error(); if (error == 259) yield break;
                throw new Win32Exception(error);
            }
            yield return data.Node;
        }
        throw new IOException("장치 조회 범위를 초과했습니다.");
    }
    private static uint Locate(string id)
    {
        if (CM_Locate_DevNodeW(out var node, id, 1) != 0) throw new IOException("장치가 더 이상 연결되어 있지 않습니다.");
        return node;
    }
    private static string DeviceId(uint node)
    {
        var value = new StringBuilder(200);
        if (CM_Get_Device_IDW(node, value, value.Capacity, 0) != 0) throw new IOException("장치 식별 정보를 확인하지 못했습니다.");
        return value.ToString();
    }
    private static string StringProperty(uint node, uint property)
    {
        var value = new StringBuilder(256); var size = 512u;
        if (CM_Get_PropertyText(node, property, out var type, value, ref size, 0) != 0 || type != 1) throw new IOException("장치 드라이버를 확인하지 못했습니다.");
        return value.ToString();
    }
    private static uint DwordProperty(uint node, uint property)
    {
        var size = 4u;
        if (CM_Get_PropertyDword(node, property, out var type, out var value, ref size, 0) != 0 || type != 4 || size != 4) throw new IOException("USB 포트를 확인하지 못했습니다.");
        return value;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint Node; public nuint Reserved; }
    private sealed class DeviceSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern DeviceSet SetupDiGetClassDevsW(nint guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(DeviceSet set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Child(out uint child, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_PropertyText(uint node, uint property, out uint type, StringBuilder value, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_PropertyDword(uint node, uint property, out uint type, out uint value, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, string device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string device, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 3)] char[] buffer, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
}
