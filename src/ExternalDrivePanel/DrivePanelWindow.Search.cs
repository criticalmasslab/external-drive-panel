using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExternalDrivePanel;

public partial class DrivePanelWindow
{
    private CancellationTokenSource? _searchRequest;
    private readonly HashSet<Task> _searchTasks = [];
    private NavigationState? _searchReturn;
    private string _searchQuery = "";
    private int _searchVersion;
    private bool _searchRunning, _searchEditing, _searchClosed;

    private void UpdateSearchToolbarMetrics(DpiScale dpi)
    {
        // Match the other vector icons, independent of the monitor's scaling.
        SetMetric("SearchActionWidth", 28 / dpi.DpiScaleX);
        SetMetric("SearchClearWidth", 20 / dpi.DpiScaleX);
        SetMetric("SearchActionHeight", 28 / dpi.DpiScaleY);
        SetMetric("SearchIconSize", 22 / dpi.DpiScaleY);
        SetMetric("SearchCloseIconSize", 13 / dpi.DpiScaleY);
        void SetMetric(string key, double value)
        {
            if (Resources[key] is not double existing || Math.Abs(existing - value) > .001) Resources[key] = value;
        }
    }

    public Task SearchDriveAsync(string query)
    {
        if (_searchClosed) return Task.CompletedTask;
        query = query.Trim();
        if (query.Length == 0) { ExitSearch(); return Task.CompletedTask; }
        var previous = _contentMode == PanelContentMode.Search ? _searchReturn : new NavigationState(_contentMode, _currentPath);
        StopSearch(false);
        _searchReturn = previous; _searchQuery = query;
        SearchTextBox.Text = query;
        _contentMode = PanelContentMode.Search;
        _currentItems = []; ApplySort(); ApplyViewVisibility(); UpdateNavigationButtons();
        SearchStatus.Visibility = Visibility.Visible; SearchStopButton.Visibility = Visibility.Visible;
        SearchStatusText.Text = "검색 중… 해당 드라이브 전체를 확인합니다.";
        ShowItemAddress($"검색: {query} — {_root}");
        var request = new CancellationTokenSource(); _searchRequest = request;
        _searchRunning = true;
        var task = RunSearchAsync(query, _searchVersion, request);
        _searchTasks.Add(task); _ = ObserveSearchAsync(task);
        return task;
    }

    private async Task ObserveSearchAsync(Task task)
    { try { await task; } finally { _searchTasks.Remove(task); } }

    private async Task RunSearchAsync(string query, int version, CancellationTokenSource request)
    {
        var token = request.Token;
        var progress = new Progress<DriveSearchSnapshot>(snapshot =>
        {
            if (version == _searchVersion && !token.IsCancellationRequested && _searchRunning)
                ShowSearchSnapshot(snapshot, true);
        });
        try
        {
            var result = await DriveSearchService.SearchAsync(_root, query, _viewSettings.ShowHiddenItems, progress, token);
            if (version == _searchVersion && !token.IsCancellationRequested) ShowSearchSnapshot(result, false);
        }
        catch (OperationCanceledException)
        {
            if (version == _searchVersion) SearchStatusText.Text = $"검색 중지 · {_currentItems.Count:N0}개 결과";
        }
        catch (Exception ex)
        {
            if (version == _searchVersion) SearchStatusText.Text = "검색할 수 없습니다: " + ex.Message;
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            if (version == _searchVersion)
            { _searchRunning = false; _searchRequest = null; SearchStopButton.Visibility = Visibility.Collapsed; }
            request.Dispose();
        }
    }

    private void ShowSearchSnapshot(DriveSearchSnapshot snapshot, bool running)
    {
        if (snapshot.Items.Count != _currentItems.Count)
        {
            var selected = ItemContextMenuBuilder.SelectedTargets(ActiveItemsView).Select(item => item.Path).ToArray();
            _currentItems = snapshot.Items.ToList(); UpdateFavoriteBadges(); ApplySort(); RestoreSelection(selected);
        }
        SearchStatusText.Text = $"{(running ? "검색 중 · " : "검색 완료 · ")}{_currentItems.Count:N0}개 결과"
            + (snapshot.Limited ? " · 5,000개까지만 표시합니다. 검색어를 좁혀주세요." : $" · {snapshot.Examined:N0}개 확인")
            + (snapshot.Skipped > 0 ? " · 일부 경로 건너뜀" : "");
    }

    private void StopSearch(bool clearText)
    {
        _searchVersion++; _searchRunning = false;
        _searchRequest?.Cancel(); _searchRequest = null;
        if (SearchStatus is not null) SearchStatus.Visibility = Visibility.Collapsed;
        if (clearText)
        {
            _searchEditing = true;
            try { SearchTextBox.Clear(); } finally { _searchEditing = false; }
            _searchQuery = "";
        }
    }

    private void ExitSearch()
    {
        var previous = _searchReturn;
        var wasSearch = _contentMode == PanelContentMode.Search;
        StopSearch(true); _searchReturn = null;
        if (!wasSearch) return;
        if (previous?.Mode == PanelContentMode.Folder && previous.Path is not null && NavigateTo(previous.Path, false)) return;
        ShowFavorites(false);
    }

    private async void Search_OnClick(object sender, RoutedEventArgs e) => await SearchDriveAsync(SearchTextBox.Text);
    private void SearchClear_OnClick(object sender, RoutedEventArgs e) => ExitSearch();
    private void SearchStop_OnClick(object sender, RoutedEventArgs e) => _searchRequest?.Cancel();
    private void SearchText_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchClearButton is null) return;
        SearchClearButton.Visibility = SearchTextBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!_searchEditing && SearchTextBox.Text.Length == 0 && _contentMode == PanelContentMode.Search) ExitSearch();
    }
    private async void Search_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (!e.IsRepeat) await SearchDriveAsync(SearchTextBox.Text); }
        else if (e.Key == Key.Escape) { e.Handled = true; ExitSearch(); }
    }

    private void SearchWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.IsRepeat) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { e.Handled = true; SearchTextBox.Focus(); SearchTextBox.SelectAll(); }
        else if (e.Key == Key.Escape && _contentMode == PanelContentMode.Search)
        { e.Handled = true; ExitSearch(); }
    }
}
