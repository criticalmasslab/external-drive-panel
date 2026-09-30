using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExternalDrivePanel;

public partial class ZoneSettingsWindow : Window
{
    private readonly ZoneSettings _target;
    private readonly ZoneSettings _draft;
    private readonly IReadOnlyList<MonitorDescriptor> _monitors;
    private bool _isInitialized;
    private readonly PhysicalWindowPlacement _placement;

    public ZoneSettingsWindow(ZoneSettings settings, IReadOnlyList<MonitorDescriptor> monitors)
    {
        InitializeComponent();
        _placement = new PhysicalWindowPlacement(this);
        Loaded += (_, _) => _placement.CenterOnOwner(480, 680);
        _target = settings;
        _draft = settings.Clone();
        _monitors = monitors;

        GridColsText.Text = _draft.GridCols.ToString(CultureInfo.InvariantCulture);
        GridRowsText.Text = _draft.GridRows.ToString(CultureInfo.InvariantCulture);
        VerticalSplitRadio.IsChecked = _draft.SplitDirection == SplitDirection.Vertical;
        HorizontalSplitRadio.IsChecked = _draft.SplitDirection == SplitDirection.Horizontal;
        UniformMarginCheck.IsChecked = _draft.MarginUniform;
        MarginAllText.Text = Format(_draft.MarginAll);
        MarginTopText.Text = Format(_draft.MarginTop);
        MarginBottomText.Text = Format(_draft.MarginBottom);
        MarginLeftText.Text = Format(_draft.MarginLeft);
        MarginRightText.Text = Format(_draft.MarginRight);

        BuildMonitorOptions();
        _isInitialized = true;
        UpdateMarginControls();
        RebuildGridPreview();
    }

    public event EventHandler? SettingsSaved;

    private void BuildMonitorOptions()
    {
        MonitorOptions.Children.Clear();
        for (var index = 0; index < _monitors.Count; index++)
        {
            var option = new RadioButton
            {
                Content = _monitors[index].DisplayName,
                GroupName = "Monitor",
                Tag = index,
                Margin = new Thickness(0, 2, 0, 2),
                IsChecked = index == Math.Clamp(_draft.MonitorIndex, 0, Math.Max(0, _monitors.Count - 1))
            };
            option.Checked += MonitorOption_OnChecked;
            MonitorOptions.Children.Add(option);
        }
    }

    private void MonitorOption_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int index })
        {
            _draft.MonitorIndex = index;
        }
    }

    private void GridSize_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            RebuildGridPreview();
        }
    }

    private void RebuildGridPreview()
    {
        var columns = ParsePositiveInt(GridColsText.Text, _draft.GridCols, 12);
        var rows = ParsePositiveInt(GridRowsText.Text, _draft.GridRows, 12);
        _draft.CellCol = Math.Clamp(_draft.CellCol, 0, columns - 1);
        _draft.CellRow = Math.Clamp(_draft.CellRow, 0, rows - 1);
        GridPreview.Columns = columns;
        GridPreview.Rows = rows;
        GridPreview.Children.Clear();

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var cell = new Button
                {
                    Tag = (Column: column, Row: row),
                    Margin = new Thickness(2),
                    BorderBrush = Brushes.SlateGray,
                    BorderThickness = new Thickness(1),
                    Background = column == _draft.CellCol && row == _draft.CellRow
                        ? new SolidColorBrush(Color.FromRgb(70, 130, 190))
                        : Brushes.White
                };
                cell.Click += GridCell_OnClick;
                GridPreview.Children.Add(cell);
            }
        }
    }

    private void GridCell_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ValueTuple<int, int> cell })
        {
            _draft.CellCol = cell.Item1;
            _draft.CellRow = cell.Item2;
            RebuildGridPreview();
        }
    }

    private void UniformMargin_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
        {
            UpdateMarginControls();
        }
    }

    private void UpdateMarginControls()
    {
        var uniform = UniformMarginCheck.IsChecked == true;
        UniformMarginPanel.Visibility = uniform ? Visibility.Visible : Visibility.Collapsed;
        IndividualMarginPanel.Visibility = uniform ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ApplySettings())
        {
            DialogResult = true;
        }
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e) => ApplySettings();

    private bool ApplySettings()
    {
        if (_monitors.Count == 0)
        {
            MessageBox.Show(this, "사용 가능한 모니터를 찾지 못했습니다.", "저장할 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryPositiveInt(GridColsText.Text, out var columns)
            || !TryPositiveInt(GridRowsText.Text, out var rows)
            || !TryNonNegativeDouble(MarginAllText.Text, out var all)
            || !TryNonNegativeDouble(MarginTopText.Text, out var top)
            || !TryNonNegativeDouble(MarginBottomText.Text, out var bottom)
            || !TryNonNegativeDouble(MarginLeftText.Text, out var left)
            || !TryNonNegativeDouble(MarginRightText.Text, out var right))
        {
            MessageBox.Show(this, "격자는 1 이상의 정수, 여백은 0 이상의 숫자로 입력해주세요.", "입력 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _draft.GridCols = Math.Min(columns, 50);
        _draft.GridRows = Math.Min(rows, 50);
        _draft.CellCol = Math.Clamp(_draft.CellCol, 0, _draft.GridCols - 1);
        _draft.CellRow = Math.Clamp(_draft.CellRow, 0, _draft.GridRows - 1);
        _draft.SplitDirection = HorizontalSplitRadio.IsChecked == true ? SplitDirection.Horizontal : SplitDirection.Vertical;
        _draft.MarginUniform = UniformMarginCheck.IsChecked == true;
        _draft.MarginAll = all;
        _draft.MarginTop = top;
        _draft.MarginBottom = bottom;
        _draft.MarginLeft = left;
        _draft.MarginRight = right;
        _draft.MonitorIndex = Math.Clamp(_draft.MonitorIndex, 0, _monitors.Count - 1);

        _target.CopyFrom(_draft);
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static int ParsePositiveInt(string text, int fallback, int maximum) =>
        int.TryParse(text, out var value) && value > 0 ? Math.Min(value, maximum) : Math.Max(1, fallback);
    private static bool TryPositiveInt(string text, out int value) => int.TryParse(text, out value) && value > 0;
    private static bool TryNonNegativeDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) && value >= 0;
}
