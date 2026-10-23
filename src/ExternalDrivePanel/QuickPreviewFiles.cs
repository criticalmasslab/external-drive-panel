using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExternalDrivePanel;

public sealed record PreviewText(string Content, bool Truncated, string EncodingName);
public sealed record PreviewFileInfo(string Path, string Name, string Type, string Size, string Modified);

/// <summary>Bounded, read-only loaders. Never execute scripts, macros, HTML or embedded document content.</summary>
public static class QuickPreviewFiles
{
    public const int MaxTextBytes = 128 * 1024;
    public static PreviewFileInfo Information(ItemMenuTarget target)
    {
        FileSystemInfo info = target.IsDirectory ? new DirectoryInfo(target.Path) : new FileInfo(target.Path);
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("경로를 찾을 수 없습니다.", target.Path);
        return new(target.Path, info.Name, target.IsDirectory ? "폴더" : Path.GetExtension(target.Path).ToUpperInvariant() + " 파일",
            info is FileInfo file ? Size(file.Length) : "폴더", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
    }
    public static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):N1} MB" : $"{bytes / (1024d * 1024 * 1024):N2} GB";

    public static async Task<PreviewText> ReadTextAsync(string path, CancellationToken token = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        var bytes = new byte[MaxTextBytes + 1]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break; count += read;
        }
        var truncated = count > MaxTextBytes; count = Math.Min(count, MaxTextBytes);
        Encoding encoding; var offset = 0;
        if (count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(false, true); offset = 3; }
        else if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        else encoding = new UTF8Encoding(false, true);
        string Decode(Encoding selected)
        {
            var decoder = selected.GetDecoder();
            var chars = new char[selected.GetMaxCharCount(count - offset)];
            var length = decoder.GetChars(bytes, offset, count - offset, chars, 0, flush: !truncated);
            return new string(chars, 0, length);
        }
        string content;
        try { content = Decode(encoding); }
        catch (DecoderFallbackException) when (offset == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            content = Decode(encoding);
        }
        if (content.Contains('\0')) throw new IOException("텍스트로 표시할 수 없는 파일입니다.");
        token.ThrowIfCancellationRequested();
        return new(content, truncated, encoding.WebName);
    }

    public static Task<BitmapSource> ReadImageAsync(string path, CancellationToken token = default) => Task.Run<BitmapSource>(() =>
    {
        token.ThrowIfCancellationRequested();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 256L * 1024 * 1024) throw new IOException("큰 이미지입니다. 기본 앱으로 열어 확인해 주세요.");
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth; var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw new IOException("이미지 해상도가 미리보기 범위를 벗어났습니다.");
        input.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = input;
        if (width >= height) image.DecodePixelWidth = Math.Min(width, 2048); else image.DecodePixelHeight = Math.Min(height, 2048);
        image.EndInit(); image.Freeze(); token.ThrowIfCancellationRequested();
        return image;
    }, token);
}
