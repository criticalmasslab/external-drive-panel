namespace ExternalDrivePanel;

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
public sealed record LayoutResizeHit(ResizeEdges Edges, int Boundary = -1);
public sealed record LayoutResizeGesture(LayoutResizeHit Hit, double X, double Y, LayoutRect Cell, double[] Weights, int Revision);

/// <summary>Session-only geometry in physical desktop pixels. Zone Apply explicitly resets it.</summary>
public sealed class PanelLayoutState
{
    // Keep the prior usable content allowance after adding the 24px capacity row.
    public const double MinimumPanelHeight = 164;
    private LayoutRect? _defaultCell;
    private SplitDirection _direction;
    private double[] _weights = [];
    private FavoriteLayoutProfile[]? _favoriteProfiles;
    private bool _responsive = true;
    private int _revision;
    public LayoutRect Cell { get; private set; } = new(0, 0, 480, 800);
    public SplitDirection Direction => _direction;
    private double _footerHeight;
    public double FooterHeight
    {
        get => _footerHeight;
        set { if (_footerHeight == value) return; if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _footerHeight = value; _revision++; }
    }
    private double ContentHeader => LayoutCalculator.TopBarHeight + FooterHeight;
    public LayoutRect Footer => new(Cell.X, Math.Max(Cell.Y + Math.Min(Cell.Height, LayoutCalculator.TopBarHeight), Cell.Y + Cell.Height - FooterHeight), Cell.Width,
        Math.Min(FooterHeight, Math.Max(0, Cell.Height - LayoutCalculator.TopBarHeight)));

    public void Reset() { _defaultCell = null; _favoriteProfiles = null; _revision++; }

    public void ResetDistribution() { _favoriteProfiles = null; _weights = []; _revision++; }

    public void Configure(LayoutRect cell, IReadOnlyList<FavoriteLayoutProfile> profiles, SplitDirection direction, bool responsive = true)
    {
        var changed = _defaultCell != cell || _direction != direction || _responsive != responsive || _weights.Length != profiles.Count
            || (responsive && (_favoriteProfiles is null || !_favoriteProfiles.SequenceEqual(profiles)));
        Configure(cell, profiles.Count, direction);
        if (!changed) return;
        _favoriteProfiles = profiles.ToArray();
        _responsive = responsive;
        if (!responsive)
            _weights = Enumerable.Repeat(1.0 / Math.Max(1, profiles.Count), profiles.Count).ToArray();
        else if (direction == SplitDirection.Vertical && profiles.Count > 0)
        {
            var extent = Math.Max(1, Cell.Height - ContentHeader);
            _weights = FavoritePanelLayout.Weights(Cell.Width, extent, profiles);
        }
        _revision++;
    }

    public void Configure(LayoutRect cell, int count, SplitDirection direction)
    {
        if (_defaultCell != cell || _direction != direction)
        {
            Cell = cell; _defaultCell = cell; _direction = direction; _weights = []; _revision++;
        }
        if (_weights.Length != count) { _weights = Enumerable.Repeat(1.0 / Math.Max(1, count), count).ToArray(); _revision++; }
    }

    public IReadOnlyList<LayoutRect> Panels()
    {
        var left = Math.Round(Cell.X);
        var right = Math.Round(Cell.X + Cell.Width);
        var top = Math.Round(Cell.Y + Math.Min(Cell.Height, LayoutCalculator.TopBarHeight));
        var bottom = Math.Max(top + 1, Math.Round(Footer.Y));
        var horizontal = _direction == SplitDirection.Horizontal;
        var start = horizontal ? left : top;
        var end = horizontal ? right : bottom;
        var edge = start;
        var position = 0.0;
        var result = new List<LayoutRect>();
        for (var i = 0; i < _weights.Length; i++)
        {
            position += _weights[i];
            // Round a shared boundary ONCE and use the exact same integer on
            // both sides. Never reconstitute the final edge from summed weights:
            // a .5px tie +/- floating-point error can otherwise flip by a pixel.
            var next = i == _weights.Length - 1 ? end : Math.Round(start + (end - start) * position);
            result.Add(horizontal ? new(edge, top, next - edge, bottom - top)
                : new(left, edge, right - left, next - edge));
            edge = next;
        }
        return result;
    }

    public LayoutResizeHit? HitTest(double x, double y)
    {
        const double grip = 5;
        if (x < Cell.X - 1 || x > Cell.X + Cell.Width + 1 || y < Cell.Y - 1 || y > Cell.Y + Cell.Height + 1) return null;
        var edges = ResizeEdges.None;
        if (Math.Abs(x - Cell.X) <= grip) edges |= ResizeEdges.Left;
        else if (Math.Abs(x - Cell.X - Cell.Width) <= grip) edges |= ResizeEdges.Right;
        if (Math.Abs(y - Cell.Y) <= grip) edges |= ResizeEdges.Top;
        else if (Math.Abs(y - Cell.Y - Cell.Height) <= grip) edges |= ResizeEdges.Bottom;
        if (edges != ResizeEdges.None) return new(edges);
        if (!_responsive) return null; // Equal/fixed mode only permits resizing the whole group.
        var panels = Panels();
        for (var i = 0; i + 1 < panels.Count; i++)
        {
            var panel = panels[i];
            if (_direction == SplitDirection.Vertical && Math.Abs(y - panel.Y - panel.Height) <= grip
                || _direction == SplitDirection.Horizontal && y >= panel.Y && y <= panel.Y + panel.Height && Math.Abs(x - panel.X - panel.Width) <= grip)
                return new(ResizeEdges.None, i);
        }
        return null;
    }

    public LayoutResizeGesture Begin(LayoutResizeHit hit, double x, double y) => new(hit, x, y, Cell, (double[])_weights.Clone(), _revision);

    public void Drag(LayoutResizeGesture gesture, double x, double y)
    {
        // Ignore an obsolete gesture if drives/settings changed while the mouse was captured.
        if (_revision != gesture.Revision || _weights.Length != gesture.Weights.Length) return;
        var dx = x - gesture.X; var dy = y - gesture.Y;
        var original = gesture.Cell;
        _weights = (double[])gesture.Weights.Clone();
        if (gesture.Hit.Boundary >= 0)
        {
            if (!_responsive) return;
            var i = gesture.Hit.Boundary;
            if (i + 1 >= _weights.Length) return;
            var extent = _direction == SplitDirection.Horizontal ? original.Width : Math.Max(1, original.Height - ContentHeader);
            var minimum = Math.Min((_direction == SplitDirection.Horizontal ? 300 : MinimumPanelHeight) / extent, Math.Min(_weights[i], _weights[i + 1]));
            var delta = Math.Clamp((_direction == SplitDirection.Horizontal ? dx : dy) / extent,
                minimum - _weights[i], _weights[i + 1] - minimum);
            _weights[i] += delta; _weights[i + 1] -= delta;
            return;
        }
        var edges = gesture.Hit.Edges;
        var smallestShare = _weights.Length == 0 ? 1 : Math.Max(.001, _weights.Min());
        // Unequal divisions need the same protection as an equal split when the
        // whole group shrinks; otherwise a previously small neighbor can vanish.
        var minWidth = Math.Min(original.Width, _direction == SplitDirection.Horizontal ? 300 / smallestShare : 300);
        var minHeight = Math.Min(original.Height, ContentHeader + (_direction == SplitDirection.Vertical ? MinimumPanelHeight / smallestShare : MinimumPanelHeight));
        var left = original.X; var right = original.X + original.Width;
        var top = original.Y; var bottom = original.Y + original.Height;
        if (edges.HasFlag(ResizeEdges.Left)) left = Math.Min(right - minWidth, left + dx);
        if (edges.HasFlag(ResizeEdges.Right)) right = Math.Max(left + minWidth, right + dx);
        if (edges.HasFlag(ResizeEdges.Top)) top = Math.Min(bottom - minHeight, top + dy);
        if (edges.HasFlag(ResizeEdges.Bottom)) bottom = Math.Max(top + minHeight, bottom + dy);
        Cell = new(left, top, right - left, bottom - top);
    }
}
