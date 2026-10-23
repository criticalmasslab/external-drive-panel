using System.IO;

namespace ExternalDrivePanel;

/// <summary>Coordinates file writers with graceful exit/ejection. Never force-kills a file operation.</summary>
public static class FileOperationRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<long, (string[] Paths, TaskCompletionSource Done)> Active = [];
    private static readonly Dictionary<long, string?> Paused = [];
    private static long _next;
    private static bool Touches(string[] paths, string? root) => root is null || paths.Any(path =>
        FileCopyService.IsWithin(path, root) || FileCopyService.IsWithin(root, path));

    public static IDisposable Begin(IEnumerable<string> paths)
    {
        var normalized = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (Sync)
        {
            if (Paused.Values.Any(root => Touches(normalized, root)))
                throw new IOException("종료 또는 안전하게 꺼내기를 준비 중입니다. 잠시 후 다시 시도해주세요.");
            var id = ++_next;
            Active[id] = (normalized, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return new Lease(() =>
            {
                TaskCompletionSource? done = null;
                lock (Sync) { if (Active.Remove(id, out var entry)) done = entry.Done; }
                done?.TrySetResult();
            });
        }
    }

    public static bool IsBusy(string? root = null)
    { lock (Sync) return Active.Values.Any(entry => Touches(entry.Paths, root)); }

    public static IDisposable Pause(string? root = null)
    {
        lock (Sync)
        {
            var id = ++_next; Paused[id] = root is null ? null : Path.GetFullPath(root);
            return new Lease(() => { lock (Sync) Paused.Remove(id); });
        }
    }

    public static Task WaitForIdleAsync(string? root = null)
    { lock (Sync) return Task.WhenAll(Active.Values.Where(entry => Touches(entry.Paths, root)).Select(entry => entry.Done.Task)); }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
