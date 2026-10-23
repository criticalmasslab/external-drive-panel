using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace ExternalDrivePanel;

/// <summary>Bounded, event-only local diagnostics. Never records messages, paths or file contents.</summary>
public sealed class DiagnosticLog : IDisposable
{
    public static DiagnosticLog? Current { get; set; }
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExternalDeskPanel", "Logs");
    private readonly Channel<string> _entries = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly string _directory;
    private readonly int _maxBytes;
    private readonly Task _writer;
    public Task Completion => _writer;

    public DiagnosticLog(string directory, int maxBytes = 512 * 1024)
    {
        _directory = directory; _maxBytes = Math.Max(4096, maxBytes);
        _writer = Task.Run(WriteAsync);
    }
    public static void Record(string operation, Exception? error = null) => Current?.Enqueue(operation, error);
    public void Enqueue(string operation, Exception? error = null)
    {
        // Callers supply fixed operation identifiers, never user input or exception.Message.
        var frames = error is null ? "" : string.Join(" > ", new StackTrace(error, false).GetFrames().Take(8)
            .Select(frame => frame.GetMethod()).Select(method => method?.DeclaringType?.Name + "." + method?.Name));
        var detail = error is null ? "" : $" {error.GetType().Name} 0x{error.HResult:X8} {frames}";
        var entry = $"{DateTimeOffset.UtcNow:O} {operation}{detail}";
        _entries.Writer.TryWrite(entry[..Math.Min(1800, entry.Length)]);
    }
    private async Task WriteAsync()
    {
        try
        {
            await foreach (var entry in _entries.Reader.ReadAllAsync())
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(entry) + 2 > _maxBytes)
                {
                    var first = Path.Combine(_directory, "app.1.log");
                    if (File.Exists(first)) File.Move(first, Path.Combine(_directory, "app.2.log"), overwrite: true);
                    File.Move(path, first, overwrite: true);
                }
                await File.AppendAllTextAsync(path, entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Diagnostic log unavailable: {ex.GetType().Name}"); }
    }
    public void Complete() => _entries.Writer.TryComplete();
    public void Dispose()
    {
        Complete();
        try { _writer.Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
