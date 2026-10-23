using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExternalDrivePanel;

// Blank-space rubber-band selection. Item clicks (including Ctrl/Shift) remain native WPF.
public sealed class MarqueeSelection : IDisposable
{
    private readonly Selector _view;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<object, Rect> _bounds = [];
    private HashSet<object> _original = [];
    private ScrollViewer? _scroll;
    private Point _anchor, _start;
    private ModifierKeys _modifiers;
    private bool _tracking, _dragging;
    private SelectionAdorner? _adorner;
    private AdornerLayer? _layer;

    public MarqueeSelection(Selector view)
    {
        _view = view;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => Update(), view.Dispatcher);
        _timer.Stop();
        view.PreviewMouseLeftButtonDown += Down;
        view.PreviewMouseMove += Move;
        view.PreviewMouseLeftButtonUp += Up;
        view.LostMouseCapture += Lost;
        view.PreviewKeyDown += KeyDown;
        view.IsVisibleChanged += VisibilityChanged;
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || DragDropService.FindTarget(source).Container is not null) return;
        for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ScrollBar or DataGridColumnHeader or ButtonBase or Thumb) return;
        _scroll = Descendants<ScrollViewer>(_view).FirstOrDefault();
        _start = e.GetPosition(_view);
        if (!Viewport().Contains(_start)) return;
        _original = ItemContextMenuBuilder.SelectedItems(_view).Cast<object>().ToHashSet();
        _modifiers = Keyboard.Modifiers;
        _anchor = ContentPoint(_start);
        _bounds.Clear();
        CacheBounds();
        _view.Focus();
        if ((_modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) ItemContextMenuBuilder.SelectedItems(_view).Clear();
        _tracking = _view.CaptureMouse();
        e.Handled = true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!_tracking) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Stop(); return; }
        var point = e.GetPosition(_view);
        if (!_dragging && (Math.Abs(point.X - _start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(point.Y - _start.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            _dragging = true;
            _layer = AdornerLayer.GetAdornerLayer(_view);
            if (_layer is not null) { _adorner = new SelectionAdorner(_view) { IsHitTestVisible = false }; _layer.Add(_adorner); }
            _timer.Start();
        }
        if (_dragging) Update();
        e.Handled = true;
    }

    private Rect Viewport()
    {
        var presenter = Descendants<ScrollContentPresenter>(_view).FirstOrDefault();
        return presenter is null ? new Rect(_view.RenderSize) : presenter.TransformToAncestor(_view).TransformBounds(new Rect(presenter.RenderSize));
    }
    private Point ContentPoint(Point point) => new(point.X + (_scroll?.HorizontalOffset ?? 0), point.Y + (_scroll?.VerticalOffset ?? 0));

    private void CacheBounds()
    {
        // Cache logical bounds as rows are realized so scrolling keeps earlier hits selectable.
        foreach (var container in Descendants<FrameworkElement>(_view).Where(element => element is ListBoxItem or DataGridRow))
        {
            if (ItemsControl.ItemsControlFromItemContainer(container) != _view || !container.IsVisible) continue;
            var rect = container.TransformToAncestor(_view).TransformBounds(new Rect(container.RenderSize));
            rect.Offset(_scroll?.HorizontalOffset ?? 0, _scroll?.VerticalOffset ?? 0);
            _bounds[container.DataContext] = rect;
        }
    }

    private void Update()
    {
        if (!_tracking || !_dragging) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed || !_view.IsVisible) { Stop(); return; }
        var point = Mouse.GetPosition(_view);
        var viewport = Viewport();
        if (_scroll is not null)
        {
            if (point.Y < viewport.Top + 18) _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset - 24));
            else if (point.Y > viewport.Bottom - 18) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 24);
            _view.UpdateLayout();
        }
        CacheBounds();
        var clipped = new Point(Math.Clamp(point.X, viewport.Left, viewport.Right), Math.Clamp(point.Y, viewport.Top, viewport.Bottom));
        var rectangle = new Rect(_anchor, ContentPoint(clipped));
        var hits = _bounds.Where(pair => rectangle.IntersectsWith(pair.Value)).Select(pair => pair.Key).ToHashSet();
        var desired = SelectionPolicy.Combine(_original, hits, _modifiers);
        var selected = ItemContextMenuBuilder.SelectedItems(_view);
        foreach (var item in selected.Cast<object>().Where(item => !desired.Contains(item)).ToArray()) selected.Remove(item);
        foreach (var item in _view.Items.Cast<object>().Where(item => desired.Contains(item) && !selected.Contains(item))) selected.Add(item);
        if (_adorner is not null)
        {
            rectangle.Offset(-(_scroll?.HorizontalOffset ?? 0), -(_scroll?.VerticalOffset ?? 0));
            rectangle.Intersect(viewport);
            _adorner.Rectangle = rectangle;
            _adorner.InvalidateVisual();
        }
    }

    private void Up(object sender, MouseButtonEventArgs e) { if (_tracking) { Stop(); e.Handled = true; } }
    private void Lost(object sender, MouseEventArgs e) => Stop();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Stop(); }
    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (!_tracking || e.Key != Key.Escape) return;
        var selected = ItemContextMenuBuilder.SelectedItems(_view);
        selected.Clear();
        foreach (var item in _original.Where(_view.Items.Contains)) selected.Add(item);
        Stop(); e.Handled = true;
    }
    private void Stop()
    {
        _tracking = _dragging = false;
        _timer.Stop();
        if (_adorner is not null) _layer?.Remove(_adorner);
        _adorner = null; _layer = null;
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        _bounds.Clear();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    public void Dispose()
    {
        Stop();
        _view.PreviewMouseLeftButtonDown -= Down; _view.PreviewMouseMove -= Move; _view.PreviewMouseLeftButtonUp -= Up;
        _view.LostMouseCapture -= Lost; _view.PreviewKeyDown -= KeyDown; _view.IsVisibleChanged -= VisibilityChanged;
    }
    private sealed class SelectionAdorner(UIElement element) : Adorner(element)
    {
        public Rect Rectangle { get; set; }
        protected override void OnRender(DrawingContext context)
        {
            if (!Rectangle.IsEmpty) context.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 0, 120, 215)), new Pen(Brushes.DodgerBlue, 1), Rectangle);
        }
    }
}

public static class SelectionPolicy
{
    public static HashSet<object> Combine(IEnumerable<object> original, IEnumerable<object> hits, ModifierKeys modifiers)
    {
        var result = hits.ToHashSet();
        if ((modifiers & ModifierKeys.Control) != 0) result.SymmetricExceptWith(original);
        else if ((modifiers & ModifierKeys.Shift) != 0) result.UnionWith(original);
        return result;
    }
}
