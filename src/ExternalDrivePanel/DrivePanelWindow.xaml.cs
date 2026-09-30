using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExternalDrivePanel;

public partial class DrivePanelWindow : Window
{
    private readonly string _root;
    private readonly PhysicalWindowPlacement _placement;
    private readonly DriveService _driveService = new();
    private readonly GlobalViewSettings _viewSettings;
    private readonly EjectService _ejectService;
    private readonly FavoriteCollection _favorites;
    private readonly DragDropService _dragDrop;
    private readonly List<NavigationState> _history = [];
    private List<FileSystemEntry> _currentItems = [];
    private int _historyIndex = -1;
    private string _currentPath;
    private PanelContentMode _contentMode;
    private bool _isDetailsView;
    private bool _appliedShowHiddenItems;
    private string _sortProperty = nameof(FileSystemEntry.Name);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public DrivePanelWindow(string root, string label, GlobalViewSettings viewSettings, EjectService ejectService)
    {
        _viewSettings = viewSettings;
        _ejectService = ejectService;
        InitializeComponent();
        _placement = new PhysicalWindowPlacement(this);
        _root = EnsureTrailingSeparator(Path.GetFullPath(root));
        _currentPath = _root;

        var driveName = _root.TrimEnd(Path.DirectorySeparatorChar);
        TitleText.Text = string.IsNullOrWhiteSpace(label) ? driveName : $"{driveName} {label}";
        _favorites = new FavoriteCollection();
        _dragDrop = new DragDropService(ContentArea, _root, _favorites, destination =>
        {
            if (_contentMode == PanelContentMode.Folder && FileCopyService.IsWithin(_currentPath, destination))
                LoadDirectory(_currentPath);
        }, new DropDialogs(this));
        Closed += (_, _) => _dragDrop.Dispose();
        DriveRootButton.Content = driveName;
        _favorites.Items.CollectionChanged += (_, _) => UpdateEmptyFavoritesMessage();
        FavoriteItemsList.ItemsSource = _favorites.Items;
        ShowFavorites();
        _appliedShowHiddenItems = !_viewSettings.ShowHiddenItems;
        ApplyGlobalViewSettings();
    }

    public void SetPlacement(LayoutRect rectangle)
    {
        _placement.SetPlacement(rectangle);
    }

    public void ApplyGlobalViewSettings()
    {
        Resources["IconGlyphSize"] = _viewSettings.IconSize;
        _isDetailsView = _viewSettings.ViewMode == GlobalViewMode.Details;
        var hiddenSettingChanged = _appliedShowHiddenItems != _viewSettings.ShowHiddenItems;
        _appliedShowHiddenItems = _viewSettings.ShowHiddenItems;

        if (_contentMode == PanelContentMode.Folder)
        {
            if (hiddenSettingChanged)
            {
                LoadDirectory(_currentPath);
            }
            ApplyFolderViewVisibility();
        }
    }

    private bool ShowFavorites(bool addToHistory = true)
    {
        _contentMode = PanelContentMode.Favorites;
        FavoriteItemsList.Visibility = Visibility.Visible;
        IconItemsList.Visibility = Visibility.Collapsed;
        DetailsGrid.Visibility = Visibility.Collapsed;
        FavoriteItemsList.SelectedItem = null;
        ShowFavoriteAddress(null);

        if (addToHistory)
        {
            AddHistory(new NavigationState(PanelContentMode.Favorites, null));
        }

        UpdateNavigationButtons();
        return true;
    }

    private bool NavigateTo(string path, bool addToHistory = true)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsWithinDrive(fullPath) || !Directory.Exists(fullPath) || !LoadDirectory(fullPath))
        {
            return false;
        }

        _contentMode = PanelContentMode.Folder;
        _currentPath = fullPath;
        FavoriteItemsList.Visibility = Visibility.Collapsed;
        ApplyFolderViewVisibility();

        if (addToHistory)
        {
            AddHistory(new NavigationState(PanelContentMode.Folder, fullPath));
        }

        BuildFolderBreadcrumb();
        UpdateNavigationButtons();
        return true;
    }

    private void AddHistory(NavigationState state)
    {
        if (_historyIndex >= 0 && StatesEqual(_history[_historyIndex], state))
        {
            return;
        }

        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        }

        _history.Add(state);
        _historyIndex = _history.Count - 1;
    }

    private bool RestoreHistory(NavigationState state) =>
        state.Mode == PanelContentMode.Favorites
            ? ShowFavorites(false)
            : state.Path is not null && NavigateTo(state.Path, false);

    private bool LoadDirectory(string path)
    {
        try
        {
            _currentItems = Directory.EnumerateFileSystemEntries(path)
                .Where(path => _driveService.IsVisibleFileSystemEntry(path, _viewSettings.ShowHiddenItems))
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

    private void ApplyFolderViewVisibility()
    {
        IconItemsList.Visibility = _isDetailsView ? Visibility.Collapsed : Visibility.Visible;
        DetailsGrid.Visibility = _isDetailsView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildFolderBreadcrumb()
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

    private void ShowFavoriteAddress(FavoriteItem? favorite)
    {
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Children.Add(new TextBlock
        {
            Text = favorite?.Path ?? "즐겨찾기",
            Margin = new Thickness(5, 2, 5, 2),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
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

    private void UpdateEmptyFavoritesMessage()
    {
        EmptyFavoritesMessage.Visibility = _contentMode == PanelContentMode.Favorites && _favorites.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = _contentMode == PanelContentMode.Folder && !PathsEqual(_currentPath, _root);
        IconViewButton.IsEnabled = _contentMode == PanelContentMode.Folder;
        DetailsViewButton.IsEnabled = _contentMode == PanelContentMode.Folder;
    }

    private void FavoriteItemsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_contentMode == PanelContentMode.Favorites)
        {
            ShowFavoriteAddress(FavoriteItemsList.SelectedItem as FavoriteItem);
        }
    }

    private void FavoriteItemsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoriteItemsList.SelectedItem is not FavoriteItem favorite)
        {
            return;
        }

        if (favorite.IsDirectory)
        {
            NavigateTo(favorite.Path);
        }
        else
        {
            OpenWithShell(favorite.Path);
        }
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
        _isDetailsView
            ? DetailsGrid.SelectedItem as FileSystemEntry
            : IconItemsList.SelectedItem as FileSystemEntry;

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
        if (FavoriteItemsList.SelectedItem is FavoriteItem favorite && _favorites.Remove(favorite))
        {
            FavoriteItemsList.SelectedItem = null;
        }
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
        {
            return;
        }

        var targetIndex = _historyIndex - 1;
        if (RestoreHistory(_history[targetIndex]))
        {
            _historyIndex = targetIndex;
            UpdateNavigationButtons();
        }
    }

    private void ForwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1)
        {
            return;
        }

        var targetIndex = _historyIndex + 1;
        if (RestoreHistory(_history[targetIndex]))
        {
            _historyIndex = targetIndex;
            UpdateNavigationButtons();
        }
    }

    private void UpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contentMode != PanelContentMode.Folder)
        {
            return;
        }

        var parent = Directory.GetParent(_currentPath);
        if (parent is not null && IsWithinDrive(parent.FullName))
        {
            NavigateTo(parent.FullName);
        }
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e) => ShowFavorites();

    private void DriveRootButton_OnClick(object sender, RoutedEventArgs e) => NavigateTo(_root);
    private void EjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        var driveName = _root.TrimEnd(Path.DirectorySeparatorChar);
        var answer = MessageBox.Show(this, $"{driveName} 외장하드를 안전하게 꺼낼까요?", "안전하게 꺼내기",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var result = _ejectService.TryEject(_root);
        if (result.Success)
        {
            MessageBox.Show("안전하게 제거되었습니다.", "안전하게 꺼내기", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(this,
                $"안전하게 제거하지 못했습니다.\n\n{result.FailureReason}\n\n작업표시줄의 하드웨어 안전 제거 아이콘을 이용해주세요.",
                "꺼내기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void IconViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contentMode == PanelContentMode.Folder)
        {
            _viewSettings.SetViewMode(GlobalViewMode.Icon);
        }
    }

    private void DetailsViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contentMode == PanelContentMode.Folder)
        {
            _viewSettings.SetViewMode(GlobalViewMode.Details);
        }
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

    private static bool StatesEqual(NavigationState left, NavigationState right) =>
        left.Mode == right.Mode
        && (left.Mode == PanelContentMode.Favorites
            || string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private enum PanelContentMode
    {
        Favorites,
        Folder
    }

    private sealed record NavigationState(PanelContentMode Mode, string? Path);
}
