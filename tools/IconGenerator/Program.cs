using System.IO;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Expected output ICO path.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        using var icon = PanelTrayIcon.Create();
        using var output = File.Create(args[0]);
        icon.Save(output);
    }
}
