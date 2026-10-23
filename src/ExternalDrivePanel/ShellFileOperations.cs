using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ExternalDrivePanel;

public sealed record FileClipboard(string[] Paths, bool Move);

public static class ShellFileOperations
{
    public static async Task SetClipboardAsync(IEnumerable<string> paths, bool cut)
    {
        var names = new StringCollection();
        names.AddRange(paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        if (names.Count == 0) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetFileDropList(names);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(40 * (attempt + 1)); }
        }
    }

    public static FileClipboard? ReadClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return null;
            var move = data.GetData("Preferred DropEffect") is Stream stream && stream.Length >= 4 && ReadEffect(stream) == 2;
            return new(paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), move);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return null; }
    }
    public static async Task ClearClipboardAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.Clear(); return; }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(40 * (attempt + 1)); }
        }
    }
    private static int ReadEffect(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BitConverter.ToInt32(bytes);
    }

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("사용할 수 없는 이름입니다. 경로 구분자나 끝의 점/공백을 빼주세요.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem)
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("Windows에서 예약한 이름은 사용할 수 없습니다.");
        return name;
    }

    public static string UniqueName(string directory, string name)
    {
        ValidateName(name);
        var candidate = name;
        for (var index = 2; File.Exists(Path.Combine(directory, candidate)) || Directory.Exists(Path.Combine(directory, candidate)); index++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}";
        return candidate;
    }

    public static Task<bool> PasteAsync(IntPtr owner, FileClipboard source, string destination)
    {
        var paths = source.Paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !source.Move || !PanelTransferPolicy.SameParent(path, destination)).ToArray();
        // IFileOperation.PerformOperations with nothing queued can return E_UNEXPECTED.
        if (paths.Length == 0) return Task.FromResult(true);
        return PerformAsync(owner, paths.Append(destination), operation =>
        {
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
            WithItem(destination, folder =>
            {
                foreach (var path in paths)
                {
                    if (Directory.Exists(path) && FileCopyService.IsWithin(destination, path))
                        throw new IOException("폴더를 자기 자신이나 하위 폴더에 붙여넣을 수 없습니다.");
                    WithItem(path, item =>
                    {
                        var sameFolder = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? ""),
                            Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase);
                        if (source.Move && sameFolder) return;
                        var name = !source.Move && sameFolder ? UniqueName(destination, Path.GetFileName(path)) : null;
                        Check(source.Move ? operation.MoveItem(item, folder, name, IntPtr.Zero) : operation.CopyItem(item, folder, name, IntPtr.Zero));
                    });
                }
            });
        });
    }

    public static Task<bool> DeleteAsync(IntPtr owner, IReadOnlyList<string> paths) => PerformAsync(owner, paths, operation =>
    {
        foreach (var path in paths)
        {
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is null)
                throw new IOException("드라이브 루트는 삭제할 수 없습니다.");
            WithItem(path, item => Check(operation.DeleteItem(item, IntPtr.Zero)));
        }
    });

    public static Task<bool> RenameAsync(IntPtr owner, IReadOnlyList<string> paths, string name)
    {
        ValidateName(name);
        return PerformAsync(owner, paths, operation =>
        {
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is null)
                    throw new IOException("드라이브 루트의 이름은 여기서 바꿀 수 없습니다.");
                var newName = paths.Count == 1 ? name : $"{name} ({index + 1}){(Directory.Exists(path) ? "" : Path.GetExtension(path))}";
                WithItem(path, item => Check(operation.RenameItem(item, newName, IntPtr.Zero)));
            }
        });
    }

    public static Task<bool> NewAsync(IntPtr owner, string destination, string name, bool directory)
    {
        ValidateName(name);
        return PerformAsync(owner, [destination], operation => WithItem(destination, folder =>
            Check(operation.NewItem(folder, directory ? 0x10u : 0x80u, name, null, IntPtr.Zero))));
    }

    private static async Task<bool> PerformAsync(IntPtr owner, IEnumerable<string> paths, Action<IFileOperation> queue)
    {
        using var operation = FileOperationRegistry.Begin(paths);
        return await DialogActivation.ShowAsync(owner, () => PerformCoreAsync(owner, queue));
    }
    private static async Task<bool> PerformCoreAsync(IntPtr owner, Action<IFileOperation> queue)
    {
        var completed = false;
        await ShellActions.RunStaAsync(() =>
        {
            using var foreground = new NativeDialogForegroundScope(owner);
            var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!)!;
            try
            {
                Check(operation.SetOwnerWindow(owner));
                // No silent/no-confirm flags: use Windows collision, progress and error UI.
                // Explicitly request recycling and warn if a permanent deletion is necessary.
                Check(operation.SetOperationFlags(0x40 | 0x4000 | 0x80000 | 0x20000000));
                queue(operation);
                var result = operation.PerformOperations();
                Check(operation.GetAnyOperationsAborted(out var aborted));
                if (!aborted) Check(result);
                completed = !aborted && result >= 0;
            }
            finally { Marshal.ReleaseComObject(operation); }
        });
        return completed;
    }

    private static void WithItem(string path, Action<IShellItem> action)
    {
        var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
        Check(SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out var item));
        try { action(item); } finally { Marshal.ReleaseComObject(item); }
    }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bind,
        ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bind, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attrs);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, IntPtr name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attrs, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
