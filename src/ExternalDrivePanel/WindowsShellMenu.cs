using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ExternalDrivePanel;

public sealed record ShellMenuCommand(uint Id, string Label, string Verb, bool Enabled);

// This hosts the supported IContextMenu API (Explorer's classic/"more options" menu).
// Never manufacture extension verbs from file extensions or modify shell registration.
public sealed class WindowsShellMenu : IDisposable
{
    private readonly List<IntPtr> _pidls = [];
    private object? _folder;
    private IContextMenu? _context;
    private IntPtr _menu;
    private bool _disposed;
    private ShellSite? _site;
    private bool _siteConnected;
    private const uint First = 1, Last = 0x6fff;
    public const uint PanelFavorite = 0x7100, PanelCopyPath = 0x7101, PanelZip = 0x7102;
    private const string ContextId = "000214e4-0000-0000-c000-000000000046";

    public static bool CanCombine(IReadOnlyList<string> paths) => paths.Count > 0 && paths.All(path =>
        string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))),
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[0]))), StringComparison.OrdinalIgnoreCase));

    public WindowsShellMenu(IntPtr owner, IReadOnlyList<string> paths, bool background = false, bool connectSite = false)
    {
        if (paths.Count == 0 || (!background && !CanCombine(paths)))
            throw new ArgumentException("서로 다른 폴더의 항목은 개별 Windows 메뉴를 사용해주세요.");
        try
        {
            var iid = new Guid(ContextId);
            if (background)
            {
                Check(SHParseDisplayName(paths[0], IntPtr.Zero, out var absolute, 0, out _));
                _pidls.Add(absolute);
                var folderId = typeof(IShellFolder).GUID;
                Check(SHBindToObject(IntPtr.Zero, absolute, IntPtr.Zero, ref folderId, out _folder));
                Check(((IShellFolder)_folder).CreateViewObject(owner, ref iid, out var obj));
                _context = (IContextMenu)obj;
            }
            else
            {
                var children = new List<IntPtr>();
                foreach (var path in paths)
                {
                    Check(SHParseDisplayName(path, IntPtr.Zero, out var absolute, 0, out _));
                    _pidls.Add(absolute);
                    var folderId = typeof(IShellFolder).GUID;
                    Check(SHBindToParent(absolute, ref folderId, out var parent, out var child));
                    if (_folder is null) _folder = parent; else Marshal.ReleaseComObject(parent);
                    children.Add(child); // Owned by the absolute PIDL, kept until disposal.
                }
                Check(((IShellFolder)_folder!).GetUIObjectOf(owner, (uint)children.Count, children.ToArray(), ref iid, IntPtr.Zero, out var obj));
                _context = (IContextMenu)obj;
            }
            // Modern share and some extension UIs obtain their HWND from the shell
            // site, not CMINVOKECOMMANDINFO.hwnd alone.
            _site = new ShellSite(owner);
            if (connectSite && _context is IObjectWithSite hosted)
            {
                Check(hosted.SetSite(_site));
                _siteConnected = true;
            }
            _menu = CreatePopupMenu();
            if (_menu == IntPtr.Zero) throw new Win32Exception();
            Check(_context.QueryContextMenu(_menu, 0, First, Last,
                0x10 | ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0x100u : 0u))); // CANRENAME / EXTENDEDVERBS
        }
        catch { Dispose(); throw; }
    }

    public IReadOnlyList<ShellMenuCommand> Commands()
    {
        var result = new List<ShellMenuCommand>();
        Read(_menu);
        return result;
        void Read(IntPtr menu)
        {
            for (var index = 0; index < GetMenuItemCount(menu); index++)
            {
                var text = new StringBuilder(1024);
                GetMenuStringW(menu, (uint)index, text, text.Capacity, 0x400);
                var id = GetMenuItemID(menu, index);
                if (id >= First && id <= Last)
                    result.Add(new(id, text.ToString(), Verb(id), (GetMenuState(menu, (uint)index, 0x400) & 3) == 0));
                var child = GetSubMenu(menu, index);
                if (child != IntPtr.Zero) Read(child);
            }
        }
    }

    public string Verb(uint command)
    {
        var buffer = Marshal.AllocHGlobal(2048);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return _context!.GetCommandString((UIntPtr)(command - First), 4, IntPtr.Zero, buffer, 1024) >= 0
                ? Marshal.PtrToStringUni(buffer) ?? "" : ""; // GCS_VERBW
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void AddPanelCommands(bool remove)
    {
        AppendMenuW(_menu, 0x800, 0, null);
        AppendMenuW(_menu, 0, PanelFavorite, remove ? "패널 즐겨찾기에서 제거" : "패널 즐겨찾기에 추가");
        AppendMenuW(_menu, 0, PanelCopyPath, "경로로 복사\tCtrl+Shift+C");
        AppendMenuW(_menu, 0, PanelZip, "패널 ZIP 압축");
    }

    public uint Show(Window owner, Point screenPoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hwnd = new WindowInteropHelper(owner).Handle;
        var source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("창 핸들이 준비되지 않았습니다.");
        var extended = _context as IContextMenu3;
        var older = _context as IContextMenu2;
        IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is not (0x117 or 0x2B or 0x2C or 0x120)) return IntPtr.Zero;
            if (extended is not null && extended.HandleMenuMsg2((uint)message, wParam, lParam, out var result) >= 0)
            { handled = true; return result; }
            if (older is not null && message != 0x120 && older.HandleMenuMsg((uint)message, wParam, lParam) >= 0) handled = true;
            return IntPtr.Zero;
        }
        source.AddHook(Hook);
        try
        {
            owner.Activate();
            return TrackPopupMenuEx(_menu, 0x100 | 0x2, (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y), hwnd, IntPtr.Zero);
        }
        finally { source.RemoveHook(Hook); }
    }

    public void Invoke(IntPtr owner, uint command)
        => DialogActivation.Show(owner, () => InvokeCore(owner, command));

    private void InvokeCore(IntPtr owner, uint command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_siteConnected && _context is IObjectWithSite hosted)
        {
            Check(hosted.SetSite(_site));
            _siteConnected = true;
        }
        var info = new InvokeInfo
        {
            Size = Marshal.SizeOf<InvokeInfo>(), Owner = owner, Verb = (IntPtr)(command - First),
            Show = 1, Mask = 0x100 // CMIC_MASK_NOASYNC: keep COM/PIDL alive until invocation has been handed off.
        };
        var result = _context!.InvokeCommand(ref info);
        if (result != unchecked((int)0x800704C7)) Check(result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_menu != IntPtr.Zero) DestroyMenu(_menu);
        if (_siteConnected && _context is IObjectWithSite hosted) hosted.SetSite(null);
        if (_context is not null) Marshal.ReleaseComObject(_context);
        if (_folder is not null) Marshal.ReleaseComObject(_folder);
        foreach (var pidl in _pidls) Marshal.FreeCoTaskMem(pidl);
        _site = null;
    }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    public static Point CursorPoint()
    {
        if (!GetPhysicalCursorPos(out var point)) throw new Win32Exception();
        return new Point(point.X, point.Y);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalCursorPos(out NativePoint point);

    [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
        [PreserveSig] int GetSite(ref Guid iid, out IntPtr site);
    }
    [ComVisible(true), Guid("00000114-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleWindow
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
    }
    [ComVisible(true), Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellSite(IntPtr owner) : IOleWindow, IServiceProvider
    {
        public int GetWindow(out IntPtr window) { window = owner; return 0; }
        public int ContextSensitiveHelp(bool enter) => unchecked((int)0x80004001);
        public int QueryService(ref Guid service, ref Guid iid, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (iid != typeof(IOleWindow).GUID && iid != typeof(IServiceProvider).GUID) return unchecked((int)0x80004002);
            var unknown = Marshal.GetIUnknownForObject(this);
            try { return Marshal.QueryInterface(unknown, in iid, out result); }
            finally { Marshal.Release(unknown); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public int Size; public uint Mask; public IntPtr Owner, Verb, Parameters, Directory;
        public int Show; public uint HotKey; public IntPtr Icon;
    }
    [ComImport, Guid(ContextId), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr text, uint max);
    }
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr text, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr text, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }
    [ComImport, Guid("000214e6-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr owner, IntPtr bind, IntPtr name, IntPtr eaten, out IntPtr item, IntPtr attrs);
        [PreserveSig] int EnumObjects(IntPtr owner, uint flags, out IntPtr result);
        [PreserveSig] int BindToObject(IntPtr item, IntPtr bind, ref Guid iid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr item, IntPtr bind, ref Guid iid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr first, IntPtr second);
        [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetAttributesOf(uint count, IntPtr items, ref uint attrs);
        [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] items,
            ref Guid iid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object result);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr item, uint attrs, out uint result);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr item, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object parent, out IntPtr child);
    [DllImport("shell32.dll")] private static extern int SHBindToObject(IntPtr folder, IntPtr item, IntPtr bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuStringW(IntPtr menu, uint id, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr reserved);
}
