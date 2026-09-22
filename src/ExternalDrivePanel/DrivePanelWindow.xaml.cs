using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ExternalDrivePanel;

public partial class DrivePanelWindow : Window
{
    private readonly string _root;
    private readonly DriveService _driveService = new();
    private readonly FavoriteCollection _favorites = new();
    private readonly List<string> _history = [];
    private List<FileSystemEntry> _currentItems = [];
    private int _historyIndex = -1;
    private string _currentPath;
    private FavoriteItem? _activeFavorite;
    private string _sortProperty = nameof(FileSystemEntry.Name);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public DrivePanelWindow(string root, string label)
    {
        InitializeComponent();
        _root = EnsureTrailingSeparator(Path.GetFullPath(root));
        _currentPath = _root;

        var driveName = _root.TrimEnd(Path.DirectorySeparatorChar);
        TitleText.Text = string.IsNullOrWhiteSpace(label) ? driveName : $"{driveName} {label}";
        RootTabButton.Content = driveName;
        FavoriteTabs.ItemsSource = _favorites.Items;
        NavigateTo(_root);
    }

    public void SetPlacement(int index, int windowCount)
    {
        var workArea = SystemParameters.WorkArea;
        Width = workArea.Width / 4;
        Height = workArea.Height / Math.Max(1, windowCount);
        Left = workArea.Right - Width;
        Top = workArea.Top + (Height * index);
    }

    private void NavigateTo(string path, bool addToHistory = true)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsWithinDrive(fullPath) || !Directory.Exists(fullPath) || !LoadDirectory(fullPath))
        {
            return;
        }

        _currentPath = fullPath;
        if (addToHistory)
        {
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            if (_history.Count == 0 || !string.Equals(_history[^1], fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _history.Add(fullPath);
            }
            _historyIndex = _history.Count - 1;
        }

        BuildBreadcrumb();
        UpdateNavigationButtons();
    }

    private bool LoadDirectory(string path)
    {
        try
        {
            _currentItems = Directory.EnumerateFileSystemEntries(path)
                .Where(_driveService.IsVisibleFileSystemEntry)
                .Select(TryCreateEntry)
                .OfType<FileSystemEntry>()
                .ToList();
            ApplySort();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "폴더를 열 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static FileSystemEntry? TryCreateEntry(string path)
    {
        try
        {
            return FileSystemEntry.FromPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ApplySort()
    {
        var direction = _sortDirection == ListSortDirection.Ascending ? 1 : -1;
        _currentItems.Sort((left, right) =>
        {
            var folderOrder = right.IsDirectory.CompareTo(left.IsDirectory);
            if (folderOrder != 0)
            {
                return folderOrder;
            }

            var comparison = _sortProperty switch
            {
                nameof(FileSystemEntry.ModifiedDate) => left.ModifiedDate.CompareTo(right.ModifiedDate),
                nameof(FileSystemEntry.Size) => Nullable.Compare(left.Size, right.Size),
                nameof(FileSystemEntry.Type) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Type, right.Type),
                _ => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
            };
            return comparison * direction;
        });

        var visibleItems = _currentItems.ToList();
        IconItemsList.ItemsSource = visibleItems;
        DetailsGrid.ItemsSource = visibleItems;
    }

    private void BuildBreadcrumb()
    {
        BreadcrumbPanel.Children.Clear();
        AddBreadcrumbButton(_root.TrimEnd(Path.DirectorySeparatorChar), _root);

        var relativePath = Path.GetRelativePath(_root, _currentPath);
        if (relativePath == ".")
        {
            return;
        }

        var accumulated = _root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(4, 3, 4, 0) });
            accumulated = Path.Combine(accumulated, segment);
            AddBreadcrumbButton(segment, accumulated);
        }
    }

    private void AddBreadcrumbButton(string caption, string targetPath)
    {
        var button = new Button
        {
            Content = caption,
            Tag = targetPath,
            Padding = new Thickness(5, 1, 5, 1),
            Background = null,
            BorderThickness = new Thickness(0)
        };
        button.Click += (_, _) => NavigateTo((string)button.Tag);
        BreadcrumbPanel.Children.Add(button);
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = !PathsEqual(_currentPath, _root);
    }

    private void Items_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var entry = sender switch
        {
            ListBox listBox => listBox.SelectedItem as FileSystemEntry,
            DataGrid dataGrid => dataGrid.SelectedItem as FileSystemEntry,
            _ => null
        };

        if (entry is not null)
        {
            OpenEntry(entry);
        }
    }

    private void OpenEntry(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            NavigateTo(entry.FullPath);
        }
        else
        {
            OpenWithShell(entry.FullPath);
        }
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "항목을 열 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Items_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (sender is ListBox listBox && ItemsControl.ContainerFromElement(listBox, source) is ListBoxItem listItem)
        {
            listItem.IsSelected = true;
        }
        else if (sender is DataGrid dataGrid && ItemsControl.ContainerFromElement(dataGrid, source) is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    private FileSystemEntry? GetSelectedEntry() =>
        IconItemsList.Visibility == Visibility.Visible
            ? IconItemsList.SelectedItem as FileSystemEntry
            : DetailsGrid.SelectedItem as FileSystemEntry;

    private void AddFavorite_OnClick(object sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry is not null)
        {
            _favorites.Add(entry.FullPath, entry.IsDirectory);
        }
    }

    private void RemoveFavorite_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: FavoriteItem favorite })
        {
            _favorites.Remove(favorite);
            if (ReferenceEquals(_activeFavorite, favorite))
            {
                _activeFavorite = null;
            }
        }
    }

    private void FavoriteTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FavoriteItem favorite })
        {
            return;
        }

        if (favorite.IsDirectory)
        {
            _activeFavorite = favorite;
            NavigateTo(favorite.Path);
        }
        else
        {
            OpenWithShell(favorite.Path);
        }
    }

    private void RootTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        _activeFavorite = null;
        NavigateTo(_root);
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
        {
            return;
        }

        _historyIndex--;
        NavigateTo(_history[_historyIndex], false);
    }

    private void ForwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1)
        {
            return;
        }

        _historyIndex++;
        NavigateTo(_history[_historyIndex], false);
    }

    private void UpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent is not null && IsWithinDrive(parent.FullName))
        {
            NavigateTo(parent.FullName);
        }
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e) =>
        NavigateTo(_activeFavorite is { IsDirectory: true } ? _activeFavorite.Path : _root);

    private void IconViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        IconItemsList.Visibility = Visibility.Visible;
        DetailsGrid.Visibility = Visibility.Collapsed;
    }

    private void DetailsViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        IconItemsList.Visibility = Visibility.Collapsed;
        DetailsGrid.Visibility = Visibility.Visible;
    }

    private void DetailsGrid_OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var property = e.Column.SortMemberPath;
        if (string.Equals(_sortProperty, property, StringComparison.Ordinal))
        {
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortProperty = property;
            _sortDirection = ListSortDirection.Ascending;
        }

        foreach (var column in DetailsGrid.Columns)
        {
            column.SortDirection = null;
        }
        e.Column.SortDirection = _sortDirection;
        ApplySort();
    }

    private bool IsWithinDrive(string path)
    {
        var fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        return fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}