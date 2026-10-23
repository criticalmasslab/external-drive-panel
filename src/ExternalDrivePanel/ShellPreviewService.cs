using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ExternalDrivePanel;

/// <summary>Windows' thumbnail/associated-icon provider, isolated from both WPF and file operations.</summary>
public static class ShellPreviewService
{
    private static readonly Lazy<Task<Dispatcher>> Worker = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = OleInitialize(IntPtr.Zero);
            if (result < 0) { ready.SetException(Marshal.GetExceptionForHR(result)!); return; }
            try { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }
            finally { if (result >= 0) OleUninitialize(); }
        }) { IsBackground = true, Name = "File previews STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task;
    });
    // Accessed only on Worker. Bounded to avoid retaining thousands of large bitmaps.
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();

    public static Task<BitmapSource?> LoadAsync(string path, int pixels, CancellationToken cancellation = default) => LoadCoreAsync(path, pixels, false, cancellation);
    public static Task<BitmapSource?> LoadThumbnailAsync(string path, int pixels, CancellationToken cancellation = default) => LoadCoreAsync(path, pixels, true, cancellation);
    private static async Task<BitmapSource?> LoadCoreAsync(string path, int pixels, bool thumbnailOnly, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(path) || cancellation.IsCancellationRequested) return null;
        var worker = await Worker.Value;
        try { return await worker.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested) return null;
            try
            {
                var file = new FileInfo(path);
                var size = Math.Clamp(pixels, 16, thumbnailOnly ? 1024 : 256);
                var key = $"{path}|{size}|{thumbnailOnly}|{file.LastWriteTimeUtc.Ticks}|{(file.Exists ? file.Length : 0)}";
                if (Cache.TryGetValue(key, out var cached)) return cached;
                var image = Extract(path, size, thumbnailOnly);
                if (image is null) return null;
                if (size > 256) return image; // Large quick-look pages are owned only by their popup.
                while (Cache.Count >= 256 && Order.TryDequeue(out var oldest)) Cache.Remove(oldest);
                Cache[key] = image; Order.Enqueue(key); return image;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Preview: {ex.Message}"); return null; }
        }, DispatcherPriority.Background, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
    }

    private static BitmapSource? Extract(string path, int size, bool thumbnailOnly)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory? factory = null; IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory is null) return null;
            // RESIZETOFIT: thumbnail when supported, otherwise the associated shell icon.
            // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, thumbnailOnly ? 8u : 0u, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); return source;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory? factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
