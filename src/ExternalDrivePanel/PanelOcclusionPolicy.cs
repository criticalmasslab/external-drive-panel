namespace ExternalDrivePanel;

public sealed record OcclusionWindow(nint Handle, LayoutRect Bounds, bool IsPanel);

/// <summary>Physical rectangles only; snapshots are ordered front to back.</summary>
public static class PanelOcclusionPolicy
{
    public const double EdgeDepth = 42, MinimumLength = 84;

    public static bool HasAccessibleEdge(LayoutRect group, IReadOnlyList<OcclusionWindow> windows, IReadOnlyList<LayoutRect> screens)
    {
        var visible = new List<LayoutRect>();
        var covers = new List<LayoutRect>();
        foreach (var window in windows)
        {
            if (!window.IsPanel) { if (Intersection(window.Bounds, group) is { } cover) covers.Add(cover); continue; }
            var pieces = screens.Select(screen => Intersection(window.Bounds, screen)).OfType<LayoutRect>().ToList();
            foreach (var cover in covers)
            {
                pieces = pieces.SelectMany(piece => Subtract(piece, cover)).ToList();
                if (pieces.Count > 2048) return false; // Bound pathological fragmented desktops.
                if (pieces.Count == 0) break;
            }
            visible.AddRange(pieces);
            if (visible.Count > 2048) return false;
        }
        if (group.Width < EdgeDepth || group.Height < EdgeDepth) return false;
        return CoveredStrip(new(group.X, group.Y, group.Width, EdgeDepth), visible, true)
            || CoveredStrip(new(group.X, group.Y + group.Height - EdgeDepth, group.Width, EdgeDepth), visible, true)
            || CoveredStrip(new(group.X, group.Y, EdgeDepth, group.Height), visible, false)
            || CoveredStrip(new(group.X + group.Width - EdgeDepth, group.Y, EdgeDepth, group.Height), visible, false);
    }

    // Sweep along each edge. Merge coverage across adjoining panel windows, so
    // artificial window boundaries do not break one genuinely clickable strip.
    private static bool CoveredStrip(LayoutRect strip, List<LayoutRect> visible, bool horizontal)
    {
        var pieces = visible.Select(rect => Intersection(rect, strip)).OfType<LayoutRect>().ToArray();
        var edges = pieces.SelectMany(rect => horizontal ? new[] { rect.X, rect.X + rect.Width } : new[] { rect.Y, rect.Y + rect.Height }).Distinct().Order().ToArray();
        var low = horizontal ? strip.Y : strip.X;
        var high = low + EdgeDepth;
        var run = 0.0;
        for (var i = 0; i + 1 < edges.Length; i++)
        {
            var midpoint = (edges[i] + edges[i + 1]) / 2;
            var spans = pieces.Where(rect => horizontal ? rect.X <= midpoint && rect.X + rect.Width >= midpoint : rect.Y <= midpoint && rect.Y + rect.Height >= midpoint)
                .Select(rect => horizontal ? (Start: rect.Y, End: rect.Y + rect.Height) : (Start: rect.X, End: rect.X + rect.Width)).OrderBy(span => span.Start);
            var end = low;
            foreach (var span in spans) { if (span.Start > end) break; end = Math.Max(end, span.End); }
            run = end >= high ? run + edges[i + 1] - edges[i] : 0;
            if (run >= MinimumLength) return true;
        }
        return false;
    }

    public static LayoutRect? Intersection(LayoutRect a, LayoutRect b)
    {
        var x = Math.Max(a.X, b.X); var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width); var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > x && bottom > y ? new(x, y, right - x, bottom - y) : null;
    }
    private static IEnumerable<LayoutRect> Subtract(LayoutRect rect, LayoutRect cover)
    {
        var cut = Intersection(rect, cover);
        if (cut is null) { yield return rect; yield break; }
        if (cut.Y > rect.Y) yield return new(rect.X, rect.Y, rect.Width, cut.Y - rect.Y);
        if (cut.Y + cut.Height < rect.Y + rect.Height) yield return new(rect.X, cut.Y + cut.Height, rect.Width, rect.Y + rect.Height - cut.Y - cut.Height);
        if (cut.X > rect.X) yield return new(rect.X, cut.Y, cut.X - rect.X, cut.Height);
        if (cut.X + cut.Width < rect.X + rect.Width) yield return new(cut.X + cut.Width, cut.Y, rect.X + rect.Width - cut.X - cut.Width, cut.Height);
    }
}

public sealed class RevealButtonDelay
{
    private TimeSpan? _coveredSince;
    public bool Update(bool covered, TimeSpan now)
    {
        if (!covered) { _coveredSince = null; return false; }
        _coveredSince ??= now;
        return now - _coveredSince >= TimeSpan.FromMilliseconds(500);
    }
}
