using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace ExternalDrivePanel;

public sealed record DriveSearchSnapshot(IReadOnlyList<FileSystemEntry> Items, int Examined, int Skipped, bool Limited);

/// <summary>Read-only, cancellable traversal. Does not follow junctions onto other volumes.</summary>
public static class DriveSearchService
{
    public const int MaximumResults = 5000;

    public static bool Matches(string name, bool directory, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return false;
        if (query.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            var extension = query[4..].Trim().TrimStart('*', '.');
            return !directory && extension.Length > 0
                && string.Equals(Path.GetExtension(name), "." + extension, StringComparison.OrdinalIgnoreCase);
        }
        if (query.IndexOfAny(['*', '?']) >= 0)
            return FileSystemName.MatchesSimpleExpression(query, name, ignoreCase: true);
        if (query.StartsWith('.') && !query.Contains(' '))
            return !directory && string.Equals(Path.GetExtension(name), query, StringComparison.OrdinalIgnoreCase);
        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static Task<DriveSearchSnapshot> SearchAsync(string root, string query, bool showHidden,
        IProgress<DriveSearchSnapshot>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<FileSystemEntry>();
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        var examined = 0; var skipped = 0;
        var clock = Stopwatch.StartNew();
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
        DriveSearchSnapshot Snapshot(bool limited = false) => new(items.ToArray(), examined, skipped, limited);
        if (string.IsNullOrWhiteSpace(query)) return Snapshot();
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested(); examined++;
                    try
                    {
                        var attributes = entry.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (isDirectory) pending.Push(entry.FullName);
                        if (Matches(entry.Name, isDirectory, query)) items.Add(FileSystemEntry.FromInfo(entry));
                        if (items.Count >= MaximumResults) return Snapshot(true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { skipped++; Debug.WriteLine(ex); }
                    if (clock.ElapsedMilliseconds >= 250)
                    { progress?.Report(Snapshot()); clock.Restart(); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { skipped++; Debug.WriteLine(ex); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Snapshot();
    }, cancellationToken);
}
