using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ExternalDrivePanel;

public partial class QuickPreviewWindow : Window
{
    private readonly PhysicalWindowPlacement _placement;
    private IReadOnlyList<ItemMenuTarget> _items = [];
    private int _index, _page, _loadVersion, _pageVersion;
    private double _zoom = 1;
    private CancellationTokenSource? _loadRequest, _pageRequest;
    private WindowsPdfPreview? _pdf;
    private MediaElement? _media;
    private readonly DispatcherTimer _mediaTimer;
    private bool _playing, _closed;
    private string _fileSummary = "";
    private readonly HashSet<Task> _pending = [];
    private Task _pdfCleanup = Task.CompletedTask;
    public string? CurrentPath => _items.Count > 0 ? _items[_index].Path : null;
    public int PdfPageIndex => _page;
    public int PdfPageCount => _pdf?.PageCount ?? 0;
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;
    public bool HasItems(IReadOnlyList<ItemMenuTarget> items, int index) => _index == index && _items.SequenceEqual(items);

    public QuickPreviewWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            PreviewMaximizeIcon.Kind = maximized ? "Restore" : "Maximize";
            PreviewMaximizeButton.ToolTip = maximized ? "이전 크기로 복원" : "최대화";
            System.Windows.Automation.AutomationProperties.SetName(PreviewMaximizeButton, maximized ? "이전 크기로 복원" : "최대화");
        };
        UpdateChrome(new DpiScale(1, 1));
        SourceInitialized += (_, _) =>
        {
            UpdateChrome(VisualTreeHelper.GetDpi(this));
            // Keep transient PDF/image/media composition off vendor GPU teardown paths.
            // The main panels retain their existing hardware rendering mode.
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source?.CompositionTarget is { } target) target.RenderMode = RenderMode.SoftwareOnly;
        };
        _placement = new PhysicalWindowPlacement(this);
        Loaded += (_, _) => { _placement.CenterOnOwner(720, 560); Focus(); };
        PreviewBody.SizeChanged += (_, _) => FitImage();
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _mediaTimer.Tick += (_, _) => UpdateMediaPosition();
        Closed += (_, _) =>
        {
            _closed = true; Cancel(ref _loadRequest); Cancel(ref _pageRequest);
            ReleasePdf(); StopMedia(); PreviewImage.Source = null; TextPreview.Clear(); _items = [];
        };
    }
    private void UpdateChrome(DpiScale dpi)
    { HeaderChromeMetrics.Apply(this, dpi); Resources["PreviewHeaderHeight"] = LayoutCalculator.TopBarHeight / dpi.DpiScaleY; }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); UpdateChrome(newDpi); }

    public Task ShowItemsAsync(IReadOnlyList<ItemMenuTarget> items, int index)
    {
        if (_closed || items.Count == 0) return Task.CompletedTask;
        _items = items.ToArray(); _index = Math.Clamp(index, 0, _items.Count - 1);
        CurrentLoad = Track(LoadCurrentAsync()); return CurrentLoad;
    }
    private Task Track(Task task) { _pending.Add(task); _ = UntrackAsync(task); return task; }
    private async Task UntrackAsync(Task task)
    {
        try { await task; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { _pending.Remove(task); }
    }
    public async Task CloseAndReleaseAsync()
    {
        if (!_closed) Close();
        try { await Task.WhenAll(_pending.ToArray()); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        await _pdfCleanup;
    }
    private static void Cancel(ref CancellationTokenSource? source) { source?.Cancel(); source?.Dispose(); source = null; }
    private void ReleasePdf()
    {
        var old = _pdf; _pdf = null;
        if (old is not null)
        {
            var cleanup = DisposePdfAsync(old);
            _pdfCleanup = _pdfCleanup.IsCompleted ? cleanup : Task.WhenAll(_pdfCleanup, cleanup);
        }
    }
    private static async Task DisposePdfAsync(WindowsPdfPreview document)
    { try { await document.DisposeAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); } }

    private async Task LoadCurrentAsync()
    {
        var version = ++_loadVersion;
        Cancel(ref _loadRequest); Cancel(ref _pageRequest); ReleasePdf(); StopMedia();
        _loadRequest = new CancellationTokenSource(); var token = _loadRequest.Token;
        var target = _items[_index];
        FileTitle.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.Path));
        FilePathText.Text = target.Path; FilePathText.ToolTip = target.Path;
        FilePosition.Text = $"{_index + 1} / {_items.Count}";
        PreviousFileButton.IsEnabled = _index > 0; NextFileButton.IsEnabled = _index + 1 < _items.Count;
        PreviewImage.Source = null; TextPreview.Clear();
        ImageScroll.Visibility = TextPreview.Visibility = InformationPanel.Visibility = MediaPreview.Visibility = Visibility.Collapsed;
        PageControls.Visibility = ZoomControls.Visibility = Visibility.Collapsed;
        LoadingMessage.Visibility = Visibility.Visible; PreviewStatus.Text = "불러오는 중…"; OpenInAppButton.IsEnabled = false;
        _zoom = 1; _page = 0;
        try
        {
            var info = await Task.Run(() => QuickPreviewFiles.Information(target), token);
            if (token.IsCancellationRequested || version != _loadVersion) return;
            _fileSummary = $"{info.Type} · {info.Size} · {info.Modified}";
            OpenInAppButton.IsEnabled = true;
            switch (QuickPreviewPolicy.Kind(target.Path, target.IsDirectory))
            {
                case QuickPreviewKind.Pdf:
                    var document = await WindowsPdfPreview.OpenAsync(target.Path, token);
                    if (token.IsCancellationRequested || version != _loadVersion) { await document.DisposeAsync(); return; }
                    _pdf = document;
                    PageControls.Visibility = Visibility.Visible;
                    await ShowPdfPageAsync(0);
                    break;
                case QuickPreviewKind.Image:
                    BitmapSource? source;
                    try { source = await QuickPreviewFiles.ReadImageAsync(target.Path, token); }
                    catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException)
                    { source = await ShellPreviewService.LoadThumbnailAsync(target.Path, 1024, token); }
                    if (token.IsCancellationRequested) return;
                    if (source is null) ShowInformation(info, "이 이미지 형식은 현재 설치된 Windows 코덱으로 미리볼 수 없습니다.");
                    else ShowImage(source, _fileSummary + " · 이미지 (GIF/TIFF는 첫 프레임)");
                    break;
                case QuickPreviewKind.Text:
                    var text = await QuickPreviewFiles.ReadTextAsync(target.Path, token);
                    if (token.IsCancellationRequested) return;
                    TextPreview.Text = text.Content; TextPreview.ScrollToHome(); TextPreview.Visibility = Visibility.Visible;
                    PreviewStatus.Text = $"{text.EncodingName} · 읽기 전용" + (text.Truncated ? " · 앞부분 128 KB만 표시" : "");
                    break;
                case QuickPreviewKind.Media:
                    StartMedia(target.Path); PreviewStatus.Text = _fileSummary + " · 재생 버튼을 눌러 시작";
                    break;
                default:
                    var thumbnail = target.IsDirectory ? null : await ShellPreviewService.LoadThumbnailAsync(target.Path, 1024, token);
                    if (token.IsCancellationRequested) return;
                    if (thumbnail is not null) ShowImage(thumbnail, _fileSummary + " · Windows 썸네일 (문서 전체 아님)");
                    else ShowInformation(info, target.IsDirectory ? "폴더는 기본 정보만 표시합니다." : "이 형식의 본문 미리보기는 지원하지 않습니다. 기본 앱으로 열어 확인할 수 있습니다.");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && version == _loadVersion)
            {
                var message = QuickPreviewPolicy.Kind(target.Path, target.IsDirectory) == QuickPreviewKind.Pdf
                    ? "PDF를 미리볼 수 없습니다. 암호 보호·손상 또는 지원되지 않는 문서일 수 있습니다. 기본 앱으로 열어 확인해 주세요."
                    : "미리보기를 표시할 수 없습니다. 파일이 없거나 접근할 수 없거나 지원되지 않는 형식일 수 있습니다.";
                ShowInformation(null, message + "\n\n" + ex.Message);
                OpenInAppButton.IsEnabled = File.Exists(target.Path) || Directory.Exists(target.Path);
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
        finally { if (!token.IsCancellationRequested && version == _loadVersion) LoadingMessage.Visibility = Visibility.Collapsed; }
    }

    public Task NavigatePdfAsync(int index)
    {
        CurrentLoad = Track(ShowPdfPageAsync(index)); return CurrentLoad;
    }
    private async Task ShowPdfPageAsync(int index)
    {
        if (_pdf is null || _loadRequest is null || index < 0 || index >= _pdf.PageCount || _closed) return;
        Cancel(ref _pageRequest); _pageRequest = CancellationTokenSource.CreateLinkedTokenSource(_loadRequest.Token);
        var token = _pageRequest.Token; var document = _pdf; var version = ++_pageVersion;
        _page = index; PagePosition.Text = $"{_page + 1} / {document.PageCount}쪽";
        PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = _page + 1 < document.PageCount;
        LoadingMessage.Visibility = Visibility.Visible;
        try
        {
            var source = await document.RenderAsync(index, 1600, token);
            if (token.IsCancellationRequested || version != _pageVersion) return;
            InformationPanel.Visibility = Visibility.Collapsed;
            ShowImage(source, _fileSummary + $" · {_page + 1} / {document.PageCount}쪽");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowInformation(null, "PDF 페이지를 표시할 수 없습니다.\n" + ex.Message); }
        finally { if (!token.IsCancellationRequested && version == _pageVersion) LoadingMessage.Visibility = Visibility.Collapsed; }
    }
    private void ShowImage(BitmapSource image, string status)
    {
        PreviewImage.Source = image; ImageScroll.Visibility = ZoomControls.Visibility = Visibility.Visible;
        PreviewStatus.Text = status; ImageScroll.ScrollToHome(); FitImage();
    }
    private void FitImage()
    {
        if (PreviewImage.Source is not BitmapSource source) return;
        var width = Math.Max(100, PreviewBody.ActualWidth - 40); var height = Math.Max(100, PreviewBody.ActualHeight - 40);
        var fit = Math.Min(1, Math.Min(width / source.PixelWidth, height / source.PixelHeight));
        PreviewImage.Width = source.PixelWidth * fit * _zoom; PreviewImage.Height = source.PixelHeight * fit * _zoom;
        FitButton.Content = Math.Abs(_zoom - 1) < .01 ? "맞춤" : $"{_zoom:0.##}배";
    }
    private void ShowInformation(PreviewFileInfo? info, string reason)
    {
        ImageScroll.Visibility = TextPreview.Visibility = MediaPreview.Visibility = ZoomControls.Visibility = Visibility.Collapsed;
        InformationTitle.Text = info?.Name ?? FileTitle.Text;
        InformationText.Text = (info is null ? "" : $"{info.Type}\n크기: {info.Size}\n수정: {info.Modified}\n\n") + reason;
        InformationPanel.Visibility = Visibility.Visible; PreviewStatus.Text = info is null ? "미리보기 불가" : _fileSummary;
    }
    private void StartMedia(string path)
    {
        _media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
        _media.MediaOpened += Media_OnOpened; _media.MediaFailed += Media_OnFailed; _media.MediaEnded += Media_OnEnded;
        MediaHost.Children.Add(_media); MediaPreview.Visibility = Visibility.Visible; PlayButton.Content = "재생"; PlayButton.IsEnabled = false;
        MediaPosition.Text = "준비 중…";
        _media.Source = new Uri(path); _media.Pause(); // Initialize paused. Never autoplay audio when pressing Space.
    }
    private void StopMedia()
    {
        _mediaTimer?.Stop(); _playing = false;
        if (_media is null) return;
        _media.MediaOpened -= Media_OnOpened; _media.MediaFailed -= Media_OnFailed; _media.MediaEnded -= Media_OnEnded;
        try { _media.Stop(); _media.Close(); _media.Source = null; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { MediaHost.Children.Clear(); _media = null; }
    }
    private void Media_OnOpened(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, _media)) { PlayButton.IsEnabled = true; UpdateMediaPosition(); } }
    private void Media_OnFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _media) || _closed) return;
        StopMedia(); ShowInformation(null, "Windows에서 이 미디어를 재생할 수 없습니다. 코덱이 없거나 파일이 손상되었을 수 있습니다. 기본 앱으로 열어 확인해 주세요.");
    }
    private void Media_OnEnded(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(sender, _media)) { _media!.Pause(); _media.Position = TimeSpan.Zero; _playing = false; _mediaTimer.Stop(); PlayButton.Content = "재생"; UpdateMediaPosition(); } }
    private void UpdateMediaPosition()
    {
        if (_media is null) return;
        MediaPosition.Text = _media.NaturalDuration.HasTimeSpan ? $"{_media.Position:hh\\:mm\\:ss} / {_media.NaturalDuration.TimeSpan:hh\\:mm\\:ss}" : "Windows 코덱 기반 재생";
    }
    private void Play_OnClick(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        if (_playing) { _media.Pause(); _mediaTimer.Stop(); } else { _media.Play(); _mediaTimer.Start(); }
        _playing = !_playing; PlayButton.Content = _playing ? "일시정지" : "재생";
    }
    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { Maximize_OnClick(sender, e); e.Handled = true; }
        else DragMove();
    }
    private void Minimize_OnClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
    private void PreviousFile_OnClick(object sender, RoutedEventArgs e) => MoveFile(-1);
    private void NextFile_OnClick(object sender, RoutedEventArgs e) => MoveFile(1);
    private void MoveFile(int delta) { if (_index + delta >= 0 && _index + delta < _items.Count) _ = ShowItemsAsync(_items, _index + delta); }
    private void PreviousPage_OnClick(object sender, RoutedEventArgs e) => _ = NavigatePdfAsync(_page - 1);
    private void NextPage_OnClick(object sender, RoutedEventArgs e) => _ = NavigatePdfAsync(_page + 1);
    private void ZoomIn_OnClick(object sender, RoutedEventArgs e) { _zoom = Math.Min(8, _zoom * 1.25); FitImage(); }
    private void ZoomOut_OnClick(object sender, RoutedEventArgs e) { _zoom = Math.Max(.25, _zoom / 1.25); FitImage(); }
    private void Fit_OnClick(object sender, RoutedEventArgs e) { _zoom = 1; FitImage(); }
    private void OpenInApp_OnClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || !OpenInAppButton.IsEnabled) return;
        var target = _items[_index];
        try { if (target.IsDirectory) ShellActions.OpenInExplorer(target.Path); else ShellActions.OpenFile(target.Path); Close(); }
        catch (Exception ex) { ShowInformation(null, "기본 앱으로 열지 못했습니다.\n" + ex.Message); }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None) { base.OnPreviewKeyDown(e); return; }
        if (e.Key is Key.Escape or Key.Space) { e.Handled = true; Close(); return; }
        if (e.Key == Key.Enter) { e.Handled = true; OpenInApp_OnClick(this, new()); return; }
        if (e.Key is Key.Up or Key.Down && !TextPreview.IsKeyboardFocusWithin) { e.Handled = true; MoveFile(e.Key == Key.Up ? -1 : 1); return; }
        if (_pdf is not null && e.Key is Key.Left or Key.Right) { e.Handled = true; _ = NavigatePdfAsync(_page + (e.Key == Key.Left ? -1 : 1)); return; }
        if (ZoomControls.IsVisible && e.Key is Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus or Key.D0 or Key.NumPad0)
        {
            e.Handled = true;
            if (e.Key is Key.D0 or Key.NumPad0) Fit_OnClick(this, new());
            else if (e.Key is Key.Add or Key.OemPlus) ZoomIn_OnClick(this, new()); else ZoomOut_OnClick(this, new());
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
