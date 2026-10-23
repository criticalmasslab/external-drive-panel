using System.IO;

namespace ExternalDrivePanel;

public enum CopyConflictChoice { Overwrite, Skip, Cancel }

public sealed record CopyReport(int FilesCopied, int ItemsSkipped, bool Cancelled);

public sealed class FileCopyService
{
    public CopyReport Copy(IEnumerable<string> sources, string destination,
        Func<string, string, CopyConflictChoice> resolveConflict, CancellationToken cancellationToken)
    {
        var sourcePaths = sources.ToArray();
        using var operation = FileOperationRegistry.Begin(sourcePaths.Append(destination));
        var copied = 0;
        var skipped = 0;
        var cancelled = false;
        try
        {
            RejectLinks(destination);
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
            foreach (var source in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
                var name = Path.GetFileName(normalized);
                if (string.IsNullOrEmpty(name)) throw new IOException("드라이브 전체는 복사할 수 없습니다.");
                var target = Path.Combine(destination, name);
                if (IsWithin(target, normalized)) throw new IOException("자기 자신이나 하위 폴더로 복사할 수 없습니다.");
                CopyEntry(normalized, target);
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        return new CopyReport(copied, skipped, cancelled);

        void CopyEntry(string source, string target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(source);
            RejectLinks(target);
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source)) throw new FileNotFoundException("원본 항목을 찾을 수 없습니다.", source);
            var exists = File.Exists(target) || Directory.Exists(target);
            if (exists)
            {
                var choice = resolveConflict(source, target);
                cancellationToken.ThrowIfCancellationRequested();
                if (choice == CopyConflictChoice.Cancel) throw new OperationCanceledException();
                if (choice == CopyConflictChoice.Skip) { skipped++; return; }
                if (isDirectory != Directory.Exists(target))
                    throw new IOException($"파일과 폴더의 종류가 달라 덮어쓸 수 없습니다: {target}");
            }
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                foreach (var child in Directory.EnumerateFileSystemEntries(source))
                    CopyEntry(child, Path.Combine(target, Path.GetFileName(child)));
            }
            else
            {
                File.Copy(source, target, overwrite: exists);
                copied++;
            }
        }
    }

    public static bool IsWithin(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"링크 또는 연결 지점은 복사하지 않습니다: {current}");
        }
    }
}
