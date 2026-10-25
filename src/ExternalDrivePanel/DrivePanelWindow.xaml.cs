using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExternalDrivePanel;

public partial class DrivePanelWindow : Window, IEjectParticipant
{
    private readonly string _root;
    private readonly PhysicalWindowPlacement _placement;
    private readonly GlobalViewSettings _viewSettings;
    private readonly EjectService _ejectService;
    private readonly FavoriteCollection _favorites;
    public int FavoriteCount => _favorites.Items.Count;
    public int LayoutItemCount => _contentMode == PanelContentMode.Favorites ? FavoriteCount : _currentItems.Count;
    public FavoriteLayoutProfile FavoriteLayoutProfile
    {
        get
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var list = _contentMode == PanelContentMode.Favorites ? FavoriteItemsList : IconItemsList;
            return new(LayoutItemCount, EffectiveViewMode == GlobalViewMode.Details,
                (double)Resources["IconTileWidth"], (double)Resources["IconTileHeight"], dpi.DpiScaleX, dpi.DpiScaleY,
                list.Padding.Left + list.Padding.Right,
                _contentMode == PanelContentMode.Search ? SearchStatus.DesiredSize.Height : 0);
        }
    }
    public event EventHandler? LayoutMetricsChanged;
    private readonly DragDropService _dragDrop;
    private readonly ItemContextMenuBuilder _itemMenus;
    private readonly MarqueeSelection[] _marquee;
    private readonly FileCommandBar _fileCommands;
    private readonly PanelFileDragSource _fileDrag;
    private readonly FavoriteFileMonitor _favoriteMonitor;
    private readonly QuickPreviewController _quickPreview;
    private readonly List<NavigationState> _history = [];
    private List<FileSystemEntry> _currentItems = [];
    private int _historyIndex = -1;
    private string _currentPath;
    private PanelContentMode _contentMode;
    private bool _isDetailsView;
    private GlobalViewMode? _viewModeOverride;
    private bool _isEjecting;
    private List<FavoriteDetailsRow> _favoriteRows = [];
    private bool _appliedShowHiddenItems;
    private string? _preferredVolumeLabel;
    private string _sortProperty = nameof(FileSystemEntry.Name);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public DrivePanelWindow(string root, string label, GlobalViewSettings viewSettings, EjectService ejectService,
        DriveConfig? savedSettings = null)
    {
        _viewSettings = viewSettings;
        _viewModeOverride = savedSettings?.ViewMode;
        _ejectService = ejectService;
        InitializeComponent();
        UpdateChromeHeight(new DpiScale(1, 1));
        SourceInitialized += (_, _) => UpdateChromeHeight(System.Windows.Media.VisualTreeHelper.GetDpi(this));
        Loaded += (_, _) => UpdateChromeHeight(System.Windows.Media.VisualTreeHelper.GetDpi(this));
        _placement = new PhysicalWindowPlacement(this);
        _root = EnsureTrailingSeparator(Path.GetFullPath(root));
        CapacityBar.RootPath = _root;
        Closed += (_, _) => CapacityBar.Dispose();
        _currentPath = _root;

        _preferredVolumeLabel = savedSettings?.PreferredLabel;
        UpdateVolumeLabel(label);
        var titleMenu = new ContextMenu();
        var renameVolume = new MenuItem { Header = "볼륨 이름 변경…" };
        renameVolume.Click += async (_, _) =>
        {
            string fileSystem;
            try { fileSystem = new DriveInfo(_root).DriveFormat; }
            catch (Exception ex) { DialogActivation.Show(this, () => MessageBox.Show(this, ex.Message, "볼륨 정보를 읽을 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning)); return; }
            var name = NamePromptWindow.Ask(this, "외장하드 볼륨 이름 변경", VolumeLabel, validate: value => VolumeLabelService.Validate(value, fileSystem));
            if (name is null) return;
            renameVolume.IsEnabled = false;
            try
            {
                var result = await VolumeLabelService.RenameAsync(_root, name, new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (result.Cancelled) return;
                _preferredVolumeLabel = result.PreferredLabel;
                UpdateVolumeLabel(result.ActualLabel);
                VolumeLabelChanged?.Invoke(result.ActualLabel);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { DialogActivation.Show(this, () => MessageBox.Show(this, ex.Message, "볼륨 이름 변경 실패", MessageBoxButton.OK, MessageBoxImage.Warning)); }
            finally { renameVolume.IsEnabled = true; }
        };
        titleMenu.Items.Add(renameVolume); DriveTitleButton.ContextMenu = titleMenu;
        _favorites = new FavoriteCollection();
        _dragDrop = new DragDropService(ContentArea, _root, _favorites, destination =>
        {
            _ = CapacityBar.RefreshAsync();
            if (_contentMode == PanelContentMode.Folder && FileCopyService.IsWithin(_currentPath, destination))
                LoadDirectory(_currentPath);
            else if (_contentMode == PanelContentMode.Favorites) RefreshFavoriteRows();
            else if (_contentMode == PanelContentMode.Search) _ = SearchDriveAsync(_searchQuery);
        }, new DropDialogs(this), () => _contentMode == PanelContentMode.Folder ? _currentPath : null);
        Closed += (_, _) => _dragDrop.Dispose();
        FavoriteItemsList.ItemsSource = _favorites.Items;
        if (savedSettings is not null)
        {
            _sortProperty = savedSettings.SortKey;
            _sortDirection = savedSettings.SortReverse ? ListSortDirection.Descending : ListSortDirection.Ascending;
            foreach (var favorite in savedSettings.Favorites)
            {
                if (!ConfigStore.IsSafeRelativePath(favorite.RelativePath)) continue;
                var path = Path.GetFullPath(Path.Combine(_root, favorite.RelativePath));
                if (FileCopyService.IsWithin(path, _root))
                    _favorites.Add(path, favorite.IsDirectory ?? Directory.Exists(path), favorite.DisplayName);
            }
        }
        _favorites.Changed += (_, _) => { RefreshFavoriteRows(); UpdateEmptyFavoritesMessage(); UpdateFavoriteBadges(); };
        _favorites.Changed += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        _itemMenus = new ItemContextMenuBuilder(this, _root, _favorites,
            () => _contentMode == PanelContentMode.Favorites,
            target => { if (target.IsDirectory) NavigateTo(target.Path); else OpenWithShell(target.Path); },
            ShowItemAddress,
            parent =>
            {
                _ = CapacityBar.RefreshAsync();
                if (_contentMode == PanelContentMode.Folder && PathsEqual(_currentPath, parent)) LoadDirectory(_currentPath);
                else if (_contentMode == PanelContentMode.Favorites) RefreshFavoriteRows();
                else if (_contentMode == PanelContentMode.Search) _ = SearchDriveAsync(_searchQuery);
            },
            (message, error) => NotificationRequested?.Invoke(message, error), FavoriteItemsList, IconItemsList, DetailsGrid);
        Closed += (_, _) => _itemMenus.Dispose();
        _marquee = [new(FavoriteItemsList), new(IconItemsList), new(DetailsGrid)];
        _fileDrag = new PanelFileDragSource(this, _root, RefreshAfterShell, FavoriteItemsList, IconItemsList, DetailsGrid);
        Closed += (_, _) => _fileDrag.Dispose();
        Closed += (_, _) => { foreach (var selection in _marquee) selection.Dispose(); };
        foreach (var column in DetailsGrid.Columns)
            column.SortDirection = column.SortMemberPath == _sortProperty ? _sortDirection : null;
        ShowFavorites();
        _appliedShowHiddenItems = !_viewSettings.ShowHiddenItems;
        ApplyGlobalViewSettings();
        _fileCommands = new FileCommandBar(this, () => ItemContextMenuBuilder.SelectedTargets(ActiveItemsView),
            () => _contentMode == PanelContentMode.Folder ? _currentPath : _contentMode == PanelContentMode.Favorites ? _root : "", RefreshAfterShell,
            [FavoriteItemsList, IconItemsList, DetailsGrid]);
        _itemMenus.FileCommandRequested = _fileCommands.ExecuteAsync;
        _favoriteMonitor = new FavoriteFileMonitor(_root, _favorites, Dispatcher);
        Loaded += (_, _) => _favoriteMonitor.ReconcileMissing();
        _fileCommands.DeletedPaths = _favoriteMonitor.ConfirmDeleted;
        Closed += (_, _) => _favoriteMonitor.Dispose();
        _quickPreview = new QuickPreviewController(this, FavoriteItemsList, IconItemsList, DetailsGrid);
        Closed += (_, _) => _quickPreview.Dispose();
        Closed += (_, _) => _fileCommands.Dispose();
        PreviewKeyDown += SearchWindow_OnKeyDown;
        Closed += (_, _) => { _searchClosed = true; StopSearch(false); };
    }

    public event EventHandler? SettingsChanged;
    public event Action<string>? VolumeLabelChanged;
    public string VolumeLabel { get; private set; } = "";
    public void UpdateVolumeLabel(string label)
    {
        var preference = _preferredVolumeLabel;
        _preferredVolumeLabel = VolumeLabelService.MatchingPreference(label, preference);
        label = _preferredVolumeLabel ?? label;
        VolumeLabel = label;
        var driveName = _root.TrimEnd(Path.DirectorySeparatorChar);
        var title = string.IsNullOrWhiteSpace(label) ? driveName : $"{driveName} {label}";
        if (TitleText.Text != title) TitleText.Text = title;
        if (preference != _preferredVolumeLabel) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateChromeHeight(DpiScale dpi)
    {
        Resources["PanelChromeHeight"] = LayoutCalculator.TopBarHeight / dpi.DpiScaleY;
        Resources["CapacityBarHeight"] = 24 / dpi.DpiScaleY;
        Resources["CapacityFontSize"] = 12 / dpi.DpiScaleY;
        HeaderChromeMetrics.Apply(this, dpi);
        UpdateSearchToolbarMetrics(dpi);
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateChromeHeight(newDpi);
        LayoutMetricsChanged?.Invoke(this, EventArgs.Empty);
    }
    public event Action<string, bool>? NotificationRequested;
    public string RootPath => _root;
    public GlobalViewMode EffectiveViewMode => PanelViewPolicy.EffectiveMode(_viewSettings.ViewMode, _viewModeOverride);
    public void ClearViewModeOverride() => _viewModeOverride = null;

    private void RefreshAfterShell()
    {
        _ = CapacityBar.RefreshAsync();
        if (_contentMode == PanelContentMode.Search) { _ = SearchDriveAsync(_searchQuery); return; }
        var paths = ItemContextMenuBuilder.SelectedTargets(ActiveItemsView).Select(item => item.Path).ToArray();
        if (_contentMode == PanelContentMode.Favorites) RefreshFavoriteRows();
        else if (Directory.Exists(_currentPath)) LoadDirectory(_currentPath);
        else ShowFavorites();
        RestoreSelection(paths);
    }

    public DriveConfig CaptureSettings(string label) => new()
    {
        LastKnownLabel = label,
        PreferredLabel = _preferredVolumeLabel,
        SortKey = _sortProperty,
        SortReverse = _sortDirection == ListSortDirection.Descending,
        ViewMode = _viewModeOverride,
        Favorites = _favorites.Items.Where(item => FileCopyService.IsWithin(item.Path, _root))
            .Select(item => new FavoriteConfig
            {
                RelativePath = Path.GetRelativePath(_root, item.Path),
                DisplayName = item.Name,
                IsDirectory = item.IsDirectory
            }).ToList()
    };

    public void SetPlacement(LayoutRect rectangle)
    {
        _placement.SetPlacement(rectangle);
    }

    public void ApplyGlobalViewSettings()
    {
        var selectedPaths = ItemContextMenuBuilder.SelectedTargets(ActiveItemsView).Select(item => item.Path).ToArray();
        Resources["IconGlyphSize"] = _viewSettings.IconSize;
        Resources["ShowFilePreviews"] = _viewSettings.ShowPreviews;
        // The wrap slot, not just its glyph, must shrink/grow with the shared icon size.
        // A fixed two-line caption keeps rows aligned without sacrificing readable names.
        Resources["IconGlyphHeight"] = Math.Ceiling(_viewSettings.IconSize * 1.4);
        Resources["IconTileWidth"] = (_viewSettings.IconSize * 2) + 28;
        Resources["IconTileHeight"] = Math.Ceiling(_viewSettings.IconSize * 1.4) + 48;
        Resources["FavoriteBadgeSize"] = Math.Clamp(_viewSettings.IconSize * .4, 8, 16);
        _isDetailsView = EffectiveViewMode == GlobalViewMode.Details;
        var hiddenSettingChanged = _appliedShowHiddenItems != _viewSettings.ShowHiddenItems;
        _appliedShowHiddenItems = _viewSettings.ShowHiddenItems;

        if (_contentMode == PanelContentMode.Folder)
        {
            if (hiddenSettingChanged)
            {
                LoadDirectory(_currentPath);
            }
        }
        else if (_contentMode == PanelContentMode.Search)
        {
            if (hiddenSettingChanged) _ = SearchDriveAsync(_searchQuery);
        }
        else RefreshFavoriteRows();
        ApplyViewVisibility();
        RestoreSelection(selectedPaths);
    }

    private bool ShowFavorites(bool addToHistory = true)
    {
        StopSearch(true);
        _favoriteMonitor?.ReconcileMissing();
        _contentMode = PanelContentMode.Favorites;
        RefreshFavoriteRows();
        ApplyViewVisibility();
        SelectFavorite(null);

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
        StopSearch(true);
        _currentPath = fullPath;
        ApplyViewVisibility();

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
            _currentItems = new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
                { IgnoreInaccessible = false, AttributesToSkip = _viewSettings.ShowHiddenItems ? 0 : FileAttributes.Hidden | FileAttributes.System })
                .Select(TryCreateEntry)
                .OfType<FileSystemEntry>()
                .ToList();
            UpdateFavoriteBadges();
            ApplySort();
            LayoutMetricsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DialogActivation.Show(this, () => MessageBox.Show(this, ex.Message, "폴더를 열 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning));
            return false;
        }
    }

    private static FileSystemEntry? TryCreateEntry(FileSystemInfo info)
    {
        try
        {
            return FileSystemEntry.FromInfo(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void UpdateFavoriteBadges()
    {
        var favorites = _favorites.Items.Select(item => Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _currentItems) item.IsFavorite = favorites.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.FullPath)));
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
        if (_contentMode != PanelContentMode.Favorites) DetailsGrid.ItemsSource = visibleItems;
    }

    private void ApplyViewVisibility()
    {
        var home = _contentMode == PanelContentMode.Favorites;
        SearchLocationColumn.Visibility = _contentMode == PanelContentMode.Search ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.Background = home ? System.Windows.Media.Brushes.SlateGray : System.Windows.Media.Brushes.Transparent;
        HomeIcon.Foreground = home ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.White;
        HomeIcon.Kind = home ? "HomeFill" : "Home";
        HomeButton.ToolTip = home ? "즐겨찾기 홈 (현재 화면)" : "즐겨찾기 홈";
        FavoriteItemsList.Visibility = home && !_isDetailsView ? Visibility.Visible : Visibility.Collapsed;
        IconItemsList.Visibility = !home && !_isDetailsView ? Visibility.Visible : Visibility.Collapsed;
        DetailsGrid.Visibility = _isDetailsView ? Visibility.Visible : Visibility.Collapsed;
        DetailsGrid.ItemsSource = home ? _favoriteRows : IconItemsList.ItemsSource;
        DetailsGrid.CanUserSortColumns = !home;
        foreach (var column in DetailsGrid.Columns)
            column.SortDirection = !home && column.SortMemberPath == _sortProperty ? _sortDirection : null;
        ViewModeIcon.Kind = _isDetailsView ? "List" : "Grid";
        ViewModeButton.ToolTip = _isDetailsView ? "현재: 상세 보기 — 클릭하면 이 패널 아이콘 보기" : "현재: 아이콘 보기 — 클릭하면 이 패널 상세 보기";
        UpdateEmptyFavoritesMessage();
        LayoutMetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshFavoriteRows()
    {
        var selected = _contentMode == PanelContentMode.Favorites
            ? ItemContextMenuBuilder.SelectedTargets(ActiveItemsView).Select(item => item.Path).ToArray() : [];
        _favoriteRows = _favorites.Items.Select(item => new FavoriteDetailsRow(item)).ToList();
        if (_contentMode == PanelContentMode.Favorites)
        {
            DetailsGrid.ItemsSource = _favoriteRows;
            RestoreSelection(selected);
        }
    }

    private FavoriteItem? GetSelectedFavorite() => _contentMode != PanelContentMode.Favorites ? null
        : _isDetailsView ? (DetailsGrid.SelectedItem as FavoriteDetailsRow)?.Favorite
        : FavoriteItemsList.SelectedItem as FavoriteItem;

    private void SelectFavorite(FavoriteItem? favorite)
    {
        FavoriteItemsList.UnselectAll();
        if (_contentMode == PanelContentMode.Favorites) DetailsGrid.UnselectAll();
        FavoriteItemsList.SelectedItem = favorite;
        DetailsGrid.SelectedItem = _contentMode == PanelContentMode.Favorites
            ? _favoriteRows.FirstOrDefault(row => ReferenceEquals(row.Favorite, favorite)) : null;
        ShowFavoriteAddress(favorite);
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
            BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            accumulated = Path.Combine(accumulated, segment);
            AddBreadcrumbButton(segment, accumulated);
        }
    }

    private void ShowFavoriteAddress(FavoriteItem? favorite) => ShowItemAddress(favorite?.Path ?? "즐겨찾기");

    private void AddressScroll_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer { ScrollableWidth: > 0 } scroll) return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void ShowItemAddress(string path)
    {
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Children.Add(new TextBlock
        {
            Text = path,
            ToolTip = path,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
    }

    private void AddBreadcrumbButton(string caption, string targetPath)
    {
        var button = new Button
        {
            Content = caption,
            ToolTip = targetPath,
            Tag = targetPath,
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = null,
            BorderThickness = new Thickness(0)
        };
        button.Click += (_, _) => NavigateTo((string)button.Tag);
        BreadcrumbPanel.Children.Add(button);
    }

    private void UpdateEmptyFavoritesMessage()
    {
        EmptyFavoritesMessage.Visibility = PanelViewPolicy.ShowEmptyFavorites(_contentMode == PanelContentMode.Favorites, _favorites.Items.Count)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateNavigationButtons()
    {
        _fileCommands?.RefreshState();
        BackButton.IsEnabled = _contentMode == PanelContentMode.Search || _historyIndex > 0;
        ForwardButton.IsEnabled = _contentMode != PanelContentMode.Search && _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = _contentMode == PanelContentMode.Folder && !PathsEqual(_currentPath, _root);
    }

    private void FavoriteItemsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_contentMode == PanelContentMode.Favorites)
        {
            ShowFavoriteAddress(GetSelectedFavorite());
        }
    }

    private void FavoriteItemsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DragDropService.FindTarget(e.OriginalSource as DependencyObject).Container?.DataContext is FavoriteItem favorite)
            OpenFavorite(favorite);
    }

    private void OpenFavorite(FavoriteItem favorite)
    {
        if (favorite.IsDirectory)
        {
            if (!Directory.Exists(favorite.Path))
            {
                ShowMissingFavorite(favorite.Path);
                return;
            }
            NavigateTo(favorite.Path);
        }
        else
        {
            if (!File.Exists(favorite.Path))
            {
                ShowMissingFavorite(favorite.Path);
                return;
            }
            OpenWithShell(favorite.Path);
        }
    }

    private void ShowMissingFavorite(string path) => DialogActivation.Show(this,
        () => MessageBox.Show(this, $"경로를 찾을 수 없습니다.\n{path}", "즐겨찾기",
            MessageBoxButton.OK, MessageBoxImage.Information));

    private void Items_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_contentMode == PanelContentMode.Favorites)
        {
            if (DragDropService.FindTarget(e.OriginalSource as DependencyObject).Container?.DataContext is FavoriteDetailsRow row)
                OpenFavorite(row.Favorite);
            return;
        }
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
            ShellActions.OpenFile(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            DialogActivation.Show(this, () => MessageBox.Show(this, ex.Message, "항목을 열 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private void Items_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_contentMode == PanelContentMode.Favorites) ShowFavoriteAddress(GetSelectedFavorite());
        else
        {
            var item = _isDetailsView ? DetailsGrid.SelectedItem : IconItemsList.SelectedItem;
            if (item is FileSystemEntry entry) ShowItemAddress(entry.FullPath);
            else if (_contentMode == PanelContentMode.Search) ShowItemAddress($"검색: {_searchQuery} — {_root}");
            else BuildFolderBreadcrumb();
        }
    }

    private void ContentArea_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        if (e.ChangedButton == MouseButton.Left && !ReferenceEquals(e.OriginalSource, ContentArea)) return;
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers != ModifierKeys.None) return;
        if (_contentMode != PanelContentMode.Favorites || e.OriginalSource is not DependencyObject source) return;
        if (DragDropService.FindTarget(source).Container is not null) return;
        // Headers and scrollbars are controls, not empty list space.
        for (var current = source; current is not null; current = current is System.Windows.Media.Visual
            ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.DataGridColumnHeader)
                return;
        SelectFavorite(null);
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contentMode == PanelContentMode.Search) { ExitSearch(); return; }
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

    private System.Windows.Controls.Primitives.Selector ActiveItemsView => _isDetailsView ? DetailsGrid
        : _contentMode == PanelContentMode.Favorites ? FavoriteItemsList : IconItemsList;

    private void RestoreSelection(IEnumerable<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = ItemContextMenuBuilder.SelectedItems(ActiveItemsView);
        selected.Clear();
        foreach (var item in ActiveItemsView.Items.Cast<object>())
            if (ItemMenuTarget.FromItem(item) is { } target && wanted.Contains(target.Path)) selected.Add(item);
    }

    private void AddFavoritesButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var update = _favorites.BeginUpdate();
        var selected = ItemContextMenuBuilder.SelectedTargets(ActiveItemsView);
        if (selected.Count == 0 && _contentMode == PanelContentMode.Folder)
            selected = [new ItemMenuTarget(_currentPath, true)];
        foreach (var target in selected)
            if (target.IsDirectory ? Directory.Exists(target.Path) : File.Exists(target.Path))
                _favorites.Add(target.Path, target.IsDirectory);
    }

    private void DriveTitleButton_OnClick(object sender, RoutedEventArgs e) => NavigateTo(_root);
    private async void EjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isEjecting) return;
        _isEjecting = true;
        try
        {
            var driveName = _root.TrimEnd(Path.DirectorySeparatorChar);
            if (DialogActivation.Show(this, () => MessageBox.Show(this,
                $"{driveName} 외장하드를 안전하게 꺼낼까요?\n같은 물리 장치의 다른 파티션도 함께 제거됩니다.\n진행 중인 패널 파일 작업이 있으면 완료 후 제거합니다.",
                "안전하게 꺼내기", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) != MessageBoxResult.Yes) return;
            var result = EjectRequested is { } eject ? await eject(_root)
                : await EjectWorkflow.RunAsync(_root, _ejectService, [this]);
            var owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : this;
            if (result.Success)
                DialogActivation.Show(owner, () => MessageBox.Show(owner, "안전하게 제거되었습니다.", "안전하게 꺼내기", MessageBoxButton.OK, MessageBoxImage.Information));
            else
                DialogActivation.Show(owner, () => MessageBox.Show(owner,
                $"안전하게 제거하지 못했습니다.\n\n{result.FailureReason}\n\n작업표시줄의 하드웨어 안전 제거 아이콘을 이용해주세요.",
                "꺼내기 실패", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch (Exception ex) { if (IsVisible) DialogActivation.Show(this, () => MessageBox.Show(this, ex.Message, "꺼내기 실패")); }
        finally { _isEjecting = false; }
    }

    public async Task PrepareForShutdownAsync()
    {
        _searchRequest?.Cancel();
        await Task.WhenAll(_searchTasks.ToArray());
        await _quickPreview.CloseAsync();
    }

    private void ViewModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetPanelViewMode(_isDetailsView ? GlobalViewMode.Icon : GlobalViewMode.Details);
    }

    private void SetPanelViewMode(GlobalViewMode mode)
    {
        _viewModeOverride = mode;
        ApplyGlobalViewSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DetailsGrid_OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_contentMode == PanelContentMode.Favorites) return;
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
        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
        Folder,
        Search
    }

    private sealed record NavigationState(PanelContentMode Mode, string? Path);
}
