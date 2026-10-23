using System.IO;
using Microsoft.Win32;
using Velopack.Locators;

namespace ExternalDrivePanel;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

/// <summary>Current user only. The stable installed launcher survives version replacement.</summary>
public sealed class StartupRegistration : IStartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ExternalDrivePanel";
    public bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(ValueName) is string value && value.Length > 0; }
    }
    public static string LauncherPath
    {
        get
        {
            var locator = VelopackLocator.Current;
            var root = locator?.CurrentlyInstalledVersion is not null ? locator.RootAppDir : null;
            return !string.IsNullOrEmpty(root) ? Path.Combine(root, "ExternalDrivePanel.exe")
                : Environment.ProcessPath ?? throw new IOException("실행 파일 경로를 찾지 못했습니다.");
        }
    }
    public static string BuildCommand(string path) => $"\"{Path.GetFullPath(path)}\" --startup";
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled) key.SetValue(ValueName, BuildCommand(LauncherPath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
