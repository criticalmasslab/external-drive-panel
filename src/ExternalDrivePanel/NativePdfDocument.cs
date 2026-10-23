using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace ExternalDrivePanel;

/// <summary>Windows' native PDF engine: no browser/runtime download or third-party renderer.</summary>
internal sealed class NativePdfDocument : IAsyncDisposable
{
    private readonly FileStream _file;
    private readonly IRandomAccessStream _stream;
    private PdfDocument? _document;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private NativePdfDocument(FileStream file, IRandomAccessStream stream, PdfDocument document) { _file = file; _stream = stream; _document = document; PageCount = (int)document.PageCount; }
    public int PageCount { get; }

    public static Task<NativePdfDocument> OpenAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        IRandomAccessStream? stream = null;
        try
        {
            if (file.Length > 512L * 1024 * 1024) throw new IOException("큰 PDF입니다. 기본 앱으로 열어 확인해 주세요.");
            stream = file.AsRandomAccessStream();
            var document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(token).ConfigureAwait(false);
            if (document.PageCount == 0) throw new IOException("표시할 PDF 페이지가 없습니다.");
            token.ThrowIfCancellationRequested();
            return new NativePdfDocument(file, stream, document);
        }
        catch { stream?.Dispose(); file.Dispose(); throw; }
    }, token);

    public Task<BitmapSource> RenderAsync(int index, int maxPixels = 1600, CancellationToken token = default) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= PageCount) throw new ArgumentOutOfRangeException(nameof(index));
            using var page = _document!.GetPage((uint)index);
            var size = page.Size;
            var longest = Math.Clamp(maxPixels, 256, 2048);
            var ratio = longest / Math.Max(size.Width, size.Height);
            if (!double.IsFinite(ratio) || ratio <= 0) throw new IOException("PDF 페이지 크기가 올바르지 않습니다.");
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * ratio)), DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * ratio)) };
            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, options).AsTask(token).ConfigureAwait(false);
            output.Seek(0);
            using var imageStream = output.AsStreamForRead();
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = imageStream; image.EndInit(); image.Freeze();
            token.ThrowIfCancellationRequested(); return (BitmapSource)image;
        }
        finally { _gate.Release(); }
    }, token);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                var document = _document; _document = null;
                // This session owns the projection exclusively. Release its native reference
                // before closing its input, rather than leaving teardown to process finalization.
                if (document is WinRT.IWinRTObject native) native.NativeObject.Dispose();
                _stream.Dispose(); _file.Dispose();
            }
        }
        finally { _gate.Release(); }
    }
}
