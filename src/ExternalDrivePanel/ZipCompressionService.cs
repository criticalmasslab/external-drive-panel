using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;

namespace ExternalDrivePanel;

public sealed record ZipResult(string? OutputPath, string? Error);

public sealed class ZipCompressionService
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool, string> _create;
    public ZipCompressionService(Func<string, bool, string>? create = null) => _create = create ?? CreateArchive;

    // Reservation happens synchronously, before Task.Run can begin.
    public Task<ZipResult>? TryCreateAsync(string path, bool isDirectory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!_running.TryAdd(key, 0)) return null;
        IDisposable operation;
        try { operation = FileOperationRegistry.Begin([key]); }
        catch { _running.TryRemove(key, out _); throw; }
        return Task.Run(() =>
        {
            try { return new ZipResult(_create(key, isDirectory), null); }
            catch (Exception ex) { return new ZipResult(null, ex.Message); }
            finally { _running.TryRemove(key, out _); operation.Dispose(); }
        });
    }

    public static string UniquePath(string source, bool isDirectory)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var parent = Path.GetDirectoryName(source) ?? throw new IOException("드라이브 루트 전체는 ZIP으로 압축할 수 없습니다.");
        var name = isDirectory ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        if (string.IsNullOrEmpty(name)) name = Path.GetFileName(source);
        var result = Path.Combine(parent, name + ".zip");
        for (var index = 2; File.Exists(result) || Directory.Exists(result); index++)
            result = Path.Combine(parent, $"{name} ({index}).zip");
        return result;
    }

    public static string CreateArchive(string source, bool isDirectory)
    {
        if (isDirectory ? !Directory.Exists(source) : !File.Exists(source)) throw new FileNotFoundException("압축할 경로를 찾을 수 없습니다.", source);
        RejectLinks(source);
        string? output = null;
        try
        {
            FileStream stream;
            while (true)
            {
                var candidate = UniquePath(source, isDirectory);
                try { stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None); output = candidate; break; }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
            using (stream)
            {
                if (isDirectory) ZipFile.CreateFromDirectory(source, stream, CompressionLevel.Optimal, includeBaseDirectory: true);
                else
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                    archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
                }
                stream.Flush(flushToDisk: true);
            }
            return output;
        }
        catch (Exception error)
        {
            // Only remove the file successfully reserved by this operation.
            if (output is not null)
            {
                try { File.Delete(output); }
                catch (Exception cleanup) { throw new IOException($"{error.Message}\n미완성 ZIP 정리 실패: {output}\n{cleanup.Message}", error); }
            }
            throw;
        }
    }

    private static void RejectLinks(string source)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("링크/연결 지점은 압축하지 않습니다: " + path);
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
    }
}
