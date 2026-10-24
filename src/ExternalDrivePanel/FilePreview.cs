using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExternalDrivePanel;

/// <summary>Async shell image with the existing glyph as an immediate, safe fallback.</summary>
public sealed class FilePreview : Grid
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(FilePreview), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty PreviewEnabledProperty = DependencyProperty.Register(nameof(PreviewEnabled), typeof(bool), typeof(FilePreview), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(nameof(FallbackGlyph), typeof(string), typeof(FilePreview), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(FilePreview), new PropertyMetadata(24.0, Changed));
    public string FilePath { get => (string)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public bool PreviewEnabled { get => (bool)GetValue(PreviewEnabledProperty); set => SetValue(PreviewEnabledProperty, value); }
    public string FallbackGlyph { get => (string)GetValue(FallbackGlyphProperty); set => SetValue(FallbackGlyphProperty, value); }
    public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }
    public BitmapSource? PreviewSource => _image.Source as BitmapSource;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly TextBlock _fallback = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private CancellationTokenSource? _request;
    private string? _requestKey;
    private bool _suspended;
    public void Suspend() { _suspended = true; Cancel(); _requestKey = null; }
    public void Resume() { if (!_suspended) return; _suspended = false; Refresh(); }

    public FilePreview()
    {
        IsHitTestVisible = false;
        Children.Add(_fallback); Children.Add(_image);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { _requestKey = null; Cancel(); _image.Source = null; };
        SizeChanged += (_, _) => Refresh();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); Refresh(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((FilePreview)sender).Refresh();
    private void Cancel() { _request?.Cancel(); _request?.Dispose(); _request = null; }
    private async void Refresh()
    {
        _fallback.Text = FallbackGlyph; _fallback.FontSize = GlyphSize;
        if (_suspended || !PreviewEnabled || !IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            Cancel(); _requestKey = null; _image.Source = null; _fallback.Visibility = Visibility.Visible; return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixels = Math.Clamp((int)Math.Ceiling(Math.Max(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY)), 16, 256);
        var key = $"{FilePath}|{pixels}";
        if (_requestKey == key) return;
        Cancel(); _requestKey = key;
        _fallback.Visibility = Visibility.Visible; _image.Source = null;
        var request = _request = new CancellationTokenSource();
        var token = request.Token;
        try
        {
            var source = await ShellPreviewService.LoadAsync(FilePath, pixels, token);
            if (token.IsCancellationRequested || source is null) return;
            _image.Source = source; _fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Preview control: {ex.Message}"); }
    }
}
