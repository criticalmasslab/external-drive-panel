using System.IO;

namespace ExternalDrivePanel;

public sealed class FileSystemEntry
{
    private FileSystemEntry(string fullPath, string name, bool isDirectory, DateTime modifiedDate, long? size)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        ModifiedDate = modifiedDate;
        Size = size;
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public DateTime ModifiedDate { get; }
    public long? Size { get; }
    public string Icon => IsDirectory ? "📁" : "📄";
    public string DisplayName => $"{Icon} {Name}";
    public string Type => IsDirectory ? "파일 폴더" : GetFileType(Name);
    public string SizeDisplay => Size is null ? string.Empty : FormatSize(Size.Value);

    public static FileSystemEntry FromPath(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            return new FileSystemEntry(path, info.Name, true, info.LastWriteTime, null);
        }

        var file = new FileInfo(path);
        return new FileSystemEntry(path, file.Name, false, file.LastWriteTime, file.Length);
    }

    private static string GetFileType(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(extension) ? "파일" : $"{extension.TrimStart('.').ToUpperInvariant()} 파일";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unitIndex]}";
    }
}