using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ExternalDrivePanel;

public partial class ZoneSettingsWindow : Window
{
    private readonly ZoneSettings _target, _draft, _original;
    private readonly IReadOnlyList<MonitorDescriptor> _monitors;
    private bool _isInitialized;
    private (int Cols, int Rows, int Col, int Row)? _previewState;
    private readonly PhysicalWindowPlacement _placement;
    private readonly IStartupRegistration _startup;
    private bool _originalStartup;

    public ZoneSettingsWindow(ZoneSettings settings, IReadOnlyList<MonitorDescriptor> monitors, IStartupRegistration? startup = null)
    {
        _startup = startup ?? new StartupRegistration();
        InitializeComponent();
        CopyrightVersion.Text = $"© 2026 criticalmasslab · v{typeof(ZoneSettingsWindow).Assembly.GetName().Version?.ToString(3)}";
        try { StartupCheck.IsChecked = _originalStartup = _startup.IsEnabled; }
        catch (Exception ex) { StartupCheck.IsEnabled = false; StartupMessage.Text = "시작 설정을 읽지 못했습니다: " + ex.Message; }
        UpdateChrome(new DpiScale(1, 1));
        SourceInitialized += (_, _) => UpdateChrome(VisualTreeHelper.GetDpi(this));
        _placement = new PhysicalWindowPlacement(this);
        Loaded += (_, _) =>
        {
            // Fit the actual controls instead of keeping an oversized, fixed 700-DIP canvas.
            // Physical placement still clamps to the owner's monitor work area.
            SettingsFrame.Measure(new Size(390, double.PositiveInfinity));
            _placement.CenterOnOwner(390, Math.Max(320, Math.Ceiling(SettingsFrame.DesiredSize.Height)));
        };
        _target = settings; _draft = settings.Clone(); _original = settings.Clone(); _monitors = monitors;
        GridColsText.Text = _draft.GridCols.ToString(CultureInfo.InvariantCulture);
        GridRowsText.Text = _draft.GridRows.ToString(CultureInfo.InvariantCulture);
        VerticalSplitRadio.IsChecked = _draft.SplitDirection == SplitDirection.Vertical;
        HorizontalSplitRadio.IsChecked = _draft.SplitDirection == SplitDirection.Horizontal;
        UniformMarginCheck.IsChecked = _draft.MarginUniform;
        MarginAllText.Text = Format(_draft.MarginAll); MarginTopText.Text = Format(_draft.MarginTop);
        MarginBottomText.Text = Format(_draft.MarginBottom); MarginLeftText.Text = Format(_draft.MarginLeft); MarginRightText.Text = Format(_draft.MarginRight);
        for (var index = 0; index < monitors.Count; index++)
        {
            var option = new RadioButton { Content = monitors[index].DisplayName, GroupName = "Monitor", Tag = index,
                Margin = new Thickness(0, 2, 0, 2), IsChecked = index == Math.Clamp(_draft.MonitorIndex, 0, monitors.Count - 1) };
            option.Checked += (_, _) => { _draft.MonitorIndex = (int)option.Tag; if (_isInitialized) ApplySettings(); };
            MonitorOptions.Children.Add(option);
        }
        _isInitialized = true; UpdateMarginControls(); RebuildGridPreview();
    }

    public event EventHandler? SettingsSaved;
    private void UpdateChrome(DpiScale dpi)
    {
        HeaderChromeMetrics.Apply(this, dpi);
        Resources["SettingsHeaderHeight"] = LayoutCalculator.TopBarHeight / dpi.DpiScaleY;
        Resources["SettingsFooterHeight"] = 28 / dpi.DpiScaleY;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi); UpdateChrome(newDpi);
    }
    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void Form_OnChanged(object sender, TextChangedEventArgs e) { if (_isInitialized) ApplySettings(); }
    private void Option_OnChanged(object sender, RoutedEventArgs e) { if (_isInitialized) ApplySettings(); }
    private void UniformMargin_OnChanged(object sender, RoutedEventArgs e) { if (_isInitialized) { UpdateMarginControls(); ApplySettings(); } }
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { if (_startup.IsEnabled != _originalStartup) _startup.SetEnabled(_originalStartup); }
        catch (Exception ex) { StartupMessage.Text = "시작 설정을 되돌리지 못했습니다: " + ex.Message; return; }
        _target.CopyFrom(_original); SettingsSaved?.Invoke(this, EventArgs.Empty); DialogResult = false;
    }
    private void Startup_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        try { _startup.SetEnabled(StartupCheck.IsChecked == true); StartupMessage.Text = "현재 사용자 계정에만 적용됩니다."; }
        catch (Exception ex)
        {
            _isInitialized = false;
            try { StartupCheck.IsChecked = _startup.IsEnabled; } catch { StartupCheck.IsChecked = false; }
            _isInitialized = true;
            StartupMessage.Text = "시작 설정을 변경하지 못했습니다: " + ex.Message;
        }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelButton_OnClick(this, new RoutedEventArgs()); return; }
        base.OnPreviewKeyDown(e);
    }
    private void UpdateMarginControls()
    {
        var uniform = UniformMarginCheck.IsChecked == true;
        UniformMarginPanel.Visibility = uniform ? Visibility.Visible : Visibility.Collapsed;
        IndividualMarginPanel.Visibility = uniform ? Visibility.Collapsed : Visibility.Visible;
    }
    private bool ApplySettings()
    {
        if (_monitors.Count == 0) { ValidationMessage.Text = "사용 가능한 모니터를 찾지 못했습니다."; return false; }
        var uniform = UniformMarginCheck.IsChecked == true;
        var all = _draft.MarginAll; var top = _draft.MarginTop; var bottom = _draft.MarginBottom; var left = _draft.MarginLeft; var right = _draft.MarginRight;
        if (!TryGrid(GridColsText.Text, out var columns) || !TryGrid(GridRowsText.Text, out var rows)
            || uniform && !TryMargin(MarginAllText.Text, out all)
            || !uniform && (!TryMargin(MarginTopText.Text, out top) || !TryMargin(MarginBottomText.Text, out bottom)
                || !TryMargin(MarginLeftText.Text, out left) || !TryMargin(MarginRightText.Text, out right)))
        {
            ValidationMessage.Text = "격자는 1~50의 정수, 여백은 0 이상의 숫자를 입력하세요."; return false;
        }
        _draft.GridCols = columns; _draft.GridRows = rows;
        _draft.CellCol = Math.Clamp(_draft.CellCol, 0, columns - 1); _draft.CellRow = Math.Clamp(_draft.CellRow, 0, rows - 1);
        _draft.SplitDirection = HorizontalSplitRadio.IsChecked == true ? SplitDirection.Horizontal : SplitDirection.Vertical;
        _draft.MarginUniform = UniformMarginCheck.IsChecked == true;
        _draft.MarginAll = all; _draft.MarginTop = top; _draft.MarginBottom = bottom; _draft.MarginLeft = left; _draft.MarginRight = right;
        _draft.MonitorIndex = Math.Clamp(_draft.MonitorIndex, 0, _monitors.Count - 1);
        _target.CopyFrom(_draft); ValidationMessage.Text = ""; RebuildGridPreview();
        SettingsSaved?.Invoke(this, EventArgs.Empty); return true;
    }
    private void RebuildGridPreview()
    {
        var state = (_draft.GridCols, _draft.GridRows, _draft.CellCol, _draft.CellRow);
        if (_previewState == state) return;
        _previewState = state;
        SelectionHint.Text = $"선택 영역: {_draft.CellCol + 1}열 · {_draft.CellRow + 1}행 — 칸을 클릭해서 변경";
        GridPreview.Columns = _draft.GridCols; GridPreview.Rows = _draft.GridRows; GridPreview.Children.Clear();
        for (var row = 0; row < _draft.GridRows; row++)
        for (var column = 0; column < _draft.GridCols; column++)
        {
            var cell = new Button { Tag = (column, row), Padding = new Thickness(0), Margin = new Thickness(2),
                MinHeight = 0, ToolTip = $"{column + 1}열 · {row + 1}행",
                Content = _draft.GridCols <= 8 && _draft.GridRows <= 6 ? $"{column + 1}, {row + 1}" : null,
                BorderBrush = column == _draft.CellCol && row == _draft.CellRow ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(191, 197, 204)),
                BorderThickness = new Thickness(1), Foreground = column == _draft.CellCol && row == _draft.CellRow ? Brushes.White : new SolidColorBrush(Color.FromRgb(104, 113, 124)),
                Background = column == _draft.CellCol && row == _draft.CellRow ? new SolidColorBrush(Color.FromRgb(36, 52, 71)) : new SolidColorBrush(Color.FromRgb(244, 245, 247)) };
            cell.Click += (_, _) => { var selected = ((int Column, int Row))cell.Tag; _draft.CellCol = selected.Column; _draft.CellRow = selected.Row; ApplySettings(); };
            GridPreview.Children.Add(cell);
        }
    }
    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static bool TryGrid(string text, out int value) => int.TryParse(text, out value) && value is >= 1 and <= 50;
    private static bool TryMargin(string text, out double value) => double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value >= 0;
}
