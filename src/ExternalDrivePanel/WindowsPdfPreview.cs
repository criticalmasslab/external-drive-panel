using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace ExternalDrivePanel;

/// <summary>Native Windows PDF, isolated from WPF/vendor GPU lifetime in a private child process.</summary>
public sealed class WindowsPdfPreview : IAsyncDisposable
{
    private readonly Process _worker;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public int PageCount { get; private set; }
    private WindowsPdfPreview(Process worker) => _worker = worker;
    public static async Task<WindowsPdfPreview> OpenAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("PDF 파일을 찾을 수 없습니다.", path);
        var worker = Process.Start(PdfPreviewWorker.StartInfo(path)) ?? throw new IOException("PDF 엔진을 시작하지 못했습니다.");
        var session = new WindowsPdfPreview(worker);
        _ = worker.StandardError.ReadToEndAsync();
        try
        {
            using var cancel = token.Register(session.Terminate);
            if (await session.ReadIntAsync(token).ConfigureAwait(false) != PdfPreviewWorker.Magic) throw new IOException("PDF 엔진 응답이 올바르지 않습니다.");
            session.PageCount = await session.ReadIntAsync(token).ConfigureAwait(false);
            if (session.PageCount is <= 0 or > 1_000_000) throw new IOException("PDF 페이지 수가 올바르지 않습니다.");
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<BitmapSource> RenderAsync(int index, int maxPixels = 1600, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= PageCount) throw new ArgumentOutOfRangeException(nameof(index));
            // Consume the whole packet before reusing the session, even if a newer page was requested.
            token.ThrowIfCancellationRequested();
            await _worker.StandardInput.WriteLineAsync($"PAGE {index} {Math.Clamp(maxPixels, 256, 2048)}").ConfigureAwait(false);
            await _worker.StandardInput.FlushAsync().ConfigureAwait(false);
            var length = await ReadIntAsync(CancellationToken.None).ConfigureAwait(false);
            if (length == 0 || length == int.MinValue || Math.Abs(length) > PdfPreviewWorker.MaxPacket) throw new IOException("PDF 엔진 데이터가 올바르지 않습니다.");
            var packet = new byte[Math.Abs(length)];
            await _worker.StandardOutput.BaseStream.ReadExactlyAsync(packet).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (length < 0) throw new IOException(Encoding.UTF8.GetString(packet));
            return await Task.Run<BitmapSource>(() =>
            {
                using var stream = new MemoryStream(packet, writable: false);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private async Task<int> ReadIntAsync(CancellationToken token)
    {
        var bytes = new byte[4];
        try { await _worker.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new IOException("Windows PDF 엔진이 문서를 읽지 못했거나 종료되었습니다.", ex); }
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
    private void Terminate() { try { if (!_worker.HasExited) _worker.Kill(); } catch (Exception ex) { Debug.WriteLine(ex); } }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        { Terminate(); await _gate.WaitAsync().ConfigureAwait(false); }
        try
        {
            try
            {
                if (!_worker.HasExited)
                {
                    await _worker.StandardInput.WriteLineAsync("CLOSE").ConfigureAwait(false);
                    await _worker.StandardInput.FlushAsync().ConfigureAwait(false);
                    _worker.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await _worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Terminate(); await _worker.WaitForExitAsync().ConfigureAwait(false); }
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); Terminate(); }
            _worker.Dispose();
        }
        finally { _gate.Release(); }
    }
}
