using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ExternalDrivePanel;

public static class ShellActions
{
    public static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public static void OpenInExplorer(string path) => Start("explorer.exe", path);
    public static void EditInNotepad(string path) => Start("notepad.exe", path);
    private static void Start(string executable, string path)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    public static string QuotedPath(string path) => $"\"{System.IO.Path.GetFullPath(path)}\"";
    public static Task CopyPathAsync(string path) => CopyPathsAsync([path]);
    public static async Task CopyPathsAsync(IEnumerable<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths.Select(QuotedPath));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (ExternalException ex)
            {
                Debug.WriteLine($"Clipboard: {ex}");
                if (ex.HResult != unchecked((int)0x800401D0)) return;
            }
            await Task.Delay(30 * (attempt + 1));
        }
    }

    private static readonly Lazy<Task<Dispatcher>> ShellDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = OleInitialize(IntPtr.Zero);
            if (result < 0) { ready.SetException(Marshal.GetExceptionForHR(result)!); return; }
            try
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            finally { OleUninitialize(); }
        }) { IsBackground = true, Name = "Shell actions STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    });

    public static Task OpenWithAsync(IntPtr owner, string path) => InvokeVerbAsync(owner, path, "openas", false);
    public static Task ShowPropertiesAsync(IntPtr owner, string path) => InvokeVerbAsync(owner, path, "properties", true);

    public static async Task RunStaAsync(Action action)
    {
        var dispatcher = await ShellDispatcher.Value;
        await dispatcher.InvokeAsync(action);
    }

    private static async Task InvokeVerbAsync(IntPtr owner, string path, string verb, bool invokeIdList)
    {
        // Keep a dedicated STA message loop alive for native property sheets and COM
        // shell handlers; a slow extension must not block WPF's UI dispatcher.
        var dispatcher = await ShellDispatcher.Value;
        await dispatcher.InvokeAsync(() =>
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = 0x100 | 0x400 | (invokeIdList ? 0xC : 0u), // NOASYNC, FLAG_NO_UI, INVOKEIDLIST
                Owner = owner, Verb = verb, File = path, Show = 1
            };
            if (ShellExecuteExW(ref info)) return;
            var error = Marshal.GetLastWin32Error();
            if (error == 1223) return; // ERROR_CANCELLED
            if (verb == "openas" && error == 1155) // Some Windows builds do not register the openas verb.
            {
                // https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shopenwithdialog
                var open = new OpenAsInfo { File = path, Flags = 0x4 }; // OAIF_EXEC; no default-app registration.
                var result = SHOpenWithDialog(owner, ref open);
                if (result < 0 && result != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(result);
                return; // Cancel is not an error.
            }
            throw new Win32Exception(error);
        });
    }

    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner;
        public string? Verb, File, Parameters, Directory;
        public int Show;
        public IntPtr Instance, IdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor, Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        public string File;
        public string? Class;
        public uint Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
