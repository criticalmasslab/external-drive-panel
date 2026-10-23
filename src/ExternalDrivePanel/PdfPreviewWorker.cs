using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Windows.Media.Imaging;

namespace ExternalDrivePanel;

/// <summary>Read-only PDF worker. No HWND, browser, config access, elevation or output files.</summary>
public static class PdfPreviewWorker
{
    public const string Switch = "--pdf-preview-worker";
    internal const int Magic = 0x51504631;
    internal const int MaxPacket = 32 * 1024 * 1024;
    public static bool IsRequest(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Switch;
    public static int Run(IReadOnlyList<string> args)
    {
        SetErrorMode(0x8003); // Headless worker: report failures through the pipe, never a crash/drive-error dialog.
        using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8, leaveOpen: false);
        NativePdfDocument? document = null;
        try
        {
            if (args.Count != 2) return 87;
            var path = Path.GetFullPath(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
            if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return 87;
            document = NativePdfDocument.OpenAsync(path).GetAwaiter().GetResult();
            output.Write(Magic); output.Write(document.PageCount); output.Flush();
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            while (input.ReadLine() is { } request)
            {
                if (request == "CLOSE") break;
                var parts = request.Split(' ');
                if (parts.Length != 3 || parts[0] != "PAGE" || !int.TryParse(parts[1], out var page)
                    || !int.TryParse(parts[2], out var pixels) || page < 0 || page >= document.PageCount) return 87;
                try
                {
                    var image = document.RenderAsync(page, Math.Clamp(pixels, 256, 2048)).GetAwaiter().GetResult();
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                    using var stream = new MemoryStream(); png.Save(stream);
                    if (stream.Length > MaxPacket) throw new IOException("PDF 이미지가 너무 큽니다.");
                    output.Write((int)stream.Length); output.Write(stream.GetBuffer(), 0, (int)stream.Length); output.Flush();
                }
                catch (Exception ex)
                {
                    var message = Encoding.UTF8.GetBytes(ex.Message);
                    output.Write(-Math.Min(message.Length, 4096)); output.Write(message, 0, Math.Min(message.Length, 4096)); output.Flush();
                }
            }
            return 0;
        }
        catch (Exception ex) { Debug.WriteLine(ex); return 13; }
        finally { if (document is not null) document.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    internal static ProcessStartInfo StartInfo(string path)
    {
        var current = Environment.ProcessPath;
        var executable = current is not null && Assembly.GetEntryAssembly()?.GetName().Name == typeof(App).Assembly.GetName().Name
            ? current : Path.Combine(AppContext.BaseDirectory, "ExternalDrivePanel.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("PDF 미리보기 보조 실행 파일을 찾을 수 없습니다.", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ExternalDrivePanel.dll"));
        start.ArgumentList.Add(Switch); start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
        return start;
    }
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
