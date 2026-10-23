using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ExternalDrivePanel;

/// <summary>One quick-look popup per panel; shares the exact selection model of every content view.</summary>
public sealed class QuickPreviewController : IDisposable
{
    private readonly Window _owner;
    private readonly Selector[] _views;
    private bool _disposed;
    private readonly HashSet<Task> _closing = [];
    public QuickPreviewWindow? CurrentWindow { get; private set; }
    public QuickPreviewController(Window owner, params Selector[] views)
    {
        _owner = owner; _views = views;
        owner.PreviewKeyDown += KeyDown;
        foreach (var view in views) view.SelectionChanged += SelectionChanged;
    }
    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || e.Handled || e.IsRepeat || e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None
            || Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        var view = _views.FirstOrDefault(view => view.IsVisible && view.IsKeyboardFocusWithin);
        if (view is null || ItemContextMenuBuilder.SelectedTargets(view).Count == 0) return;
        e.Handled = true;
        if (CurrentWindow is { WindowState: WindowState.Minimized })
        {
            SystemCommands.RestoreWindow(CurrentWindow); CurrentWindow.Activate(); return;
        }
        if (CurrentWindow is not null) CurrentWindow.Close(); else await OpenAsync(view);
    }
    public async Task OpenAsync(Selector view)
    {
        if (_disposed) return;
        var (items, index) = Snapshot(view);
        if (items.Count == 0) return;
        if (CurrentWindow is null)
        {
            var window = new QuickPreviewWindow { Owner = _owner };
            CurrentWindow = window;
            window.Closed += (_, _) =>
            {
                if (CurrentWindow == window) CurrentWindow = null;
                var cleanup = window.CloseAndReleaseAsync(); _closing.Add(cleanup); _ = ObserveCloseAsync(cleanup);
            };
            window.Show(); window.Activate();
        }
        await CurrentWindow.ShowItemsAsync(items, index);
    }
    private static (IReadOnlyList<ItemMenuTarget> Items, int Index) Snapshot(Selector view)
    {
        var selected = ItemContextMenuBuilder.SelectedTargets(view);
        if (selected.Count == 0) return ([], 0);
        var items = selected.Count > 1 ? selected : view.Items.Cast<object>().Select(ItemMenuTarget.FromItem).OfType<ItemMenuTarget>().ToArray();
        var index = items.ToList().FindIndex(item => string.Equals(item.Path, selected[0].Path, StringComparison.OrdinalIgnoreCase));
        return (items, Math.Max(0, index));
    }
    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || CurrentWindow is null || sender is not Selector { IsVisible: true } view) return;
        var (items, index) = Snapshot(view);
        if (items.Count != 0 && !CurrentWindow.HasItems(items, index)) await CurrentWindow.ShowItemsAsync(items, index);
    }
    public async Task CloseAsync()
    {
        CurrentWindow?.Close();
        await Task.WhenAll(_closing.ToArray());
    }
    private async Task ObserveCloseAsync(Task task)
    { try { await task; } finally { _closing.Remove(task); } }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _owner.PreviewKeyDown -= KeyDown;
        foreach (var view in _views) view.SelectionChanged -= SelectionChanged;
        CurrentWindow?.Close(); CurrentWindow = null;
    }
}
