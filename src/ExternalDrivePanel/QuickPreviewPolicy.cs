using System.IO;

namespace ExternalDrivePanel;

public enum QuickPreviewKind { Image, Pdf, Text, Media, Information }

public static class QuickPreviewPolicy
{
    private static readonly HashSet<string> Images = Set(".jpg .jpeg .jpe .png .bmp .gif .tif .tiff .ico .wdp .jxr .heic .heif .webp .avif");
    private static readonly HashSet<string> Text = Set(ItemMenuModel.TextExtensions + " .c .cpp .h .hpp .rs .go .toml .sh .svg .xaml .sln .csproj .vb .vue .jsx .tsx .env .editorconfig .gitignore");
    private static readonly HashSet<string> Media = Set(".mp4 .m4v .mov .wmv .avi .mpg .mpeg .mp3 .wav .wma .m4a .aac .flac");
    private static HashSet<string> Set(string extensions) => new(extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    public static QuickPreviewKind Kind(string path, bool isDirectory = false)
    {
        if (isDirectory) return QuickPreviewKind.Information;
        var ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return QuickPreviewKind.Pdf;
        if (Images.Contains(ext)) return QuickPreviewKind.Image;
        if (Text.Contains(ext) || Text.Contains(Path.GetFileName(path))) return QuickPreviewKind.Text;
        if (Media.Contains(ext)) return QuickPreviewKind.Media;
        return QuickPreviewKind.Information;
    }
}
