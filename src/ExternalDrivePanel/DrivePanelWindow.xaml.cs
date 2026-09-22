using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace ExternalDrivePanel;

public partial class DrivePanelWindow : Window
{
    private readonly string _root;

    public DrivePanelWindow(string root, string label)
    {
        InitializeComponent();
        _root = root;

        var driveName = root.TrimEnd(Path.DirectorySeparatorChar);
        TitleText.Text = string.IsNullOrWhiteSpace(label) ? driveName : $"{driveName} {label}";
        LoadItems();
    }

    public void SetPlacement(int index, int windowCount)
    {
        var workArea = SystemParameters.WorkArea;
        Width = workArea.Width / 4;
        Height = workArea.Height / Math.Max(1, windowCount);
        Left = workArea.Right - Width;
        Top = workArea.Top + (Height * index);
    }

    private void LoadItems()
    {
        try
        {
            var directories = Directory.EnumerateDirectories(_root)
                .Select(path => new DriveItem($"📁 {Path.GetFileName(path)}", path));
            var files = Directory.EnumerateFiles(_root)
                .Select(path => new DriveItem($"📄 {Path.GetFileName(path)}", path));

            ItemsList.ItemsSource = directories.Concat(files).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ItemsList.ItemsSource = new[] { new DriveItem($"내용을 읽을 수 없습니다: {ex.Message}", null) };
        }
    }

    private void ItemsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not DriveItem { FullPath: not null } item)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "항목을 열 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record DriveItem(string DisplayName, string? FullPath);
}
