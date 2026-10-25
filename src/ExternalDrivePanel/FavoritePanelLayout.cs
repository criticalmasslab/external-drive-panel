namespace ExternalDrivePanel;

/// <summary>Shared by the detail grid and its physical-pixel space calculation.</summary>
public static class PanelListMetrics
{
    public const double RowHeight = 24;
    public const double HeaderHeight = 24;
}

/// <summary>UI slot sizes are DIPs; the layout result is always physical pixels.</summary>
public sealed record FavoriteLayoutProfile(int Count, bool Details, double TileWidth, double TileHeight,
    double DpiScaleX, double DpiScaleY, double HorizontalPadding = 2, double ExtraHeaderHeight = 0)
{
    public (double Minimum, double Desired, double Content) Measure(double width)
    {
        // Header, capacity, toolbar and address heights are fixed physical pixels.
        // Address margins, captions and content slots scale with the monitor's DPI.
        var chrome = 42 + 24 + 42 + 34 + (8 + ExtraHeaderHeight) * DpiScaleY;
        var padding = 8 * DpiScaleY; // Room for the list's padding and pixel rounding.
        var header = Details ? PanelListMetrics.HeaderHeight * DpiScaleY : 0;
        if (Count == 0)
        {
            var hint = 64 * DpiScaleY;
            return (chrome + header + hint + padding, chrome + header + hint + padding, hint);
        }
        // Fit against the list's own padding. Reserving a scrollbar before it
        // is needed incorrectly adds rows and makes the apparent whitespace uneven.
        var columns = Math.Max(1, Math.Floor((width / DpiScaleX - HorizontalPadding) / TileWidth));
        var rows = Details ? Count : Math.Ceiling(Count / columns);
        var row = (Details ? PanelListMetrics.RowHeight : TileHeight) * DpiScaleY;
        return (chrome + header + row + padding, chrome + header + rows * row + padding, rows * row);
    }
}

public static class FavoritePanelLayout
{
    public static double[] Weights(double width, double height, IReadOnlyList<FavoriteLayoutProfile> profiles)
    {
        if (profiles.Count == 0) return [];
        if (profiles.All(profile => profile.Count == 0)) return Enumerable.Repeat(1.0 / profiles.Count, profiles.Count).ToArray();
        var sizes = profiles.Select(profile => profile.Measure(width)).ToArray();
        var minimum = sizes.Sum(size => size.Minimum);
        var desired = sizes.Sum(size => size.Desired);
        if (height >= desired)
        {
            // Equal whitespace as a percentage of occupied content, rather than
            // the same pixel allowance for a one-row and a many-row panel.
            var extra = height - desired;
            var content = sizes.Sum(size => size.Content);
            return sizes.Select(size => (size.Desired + extra * size.Content / content) / height).ToArray();
        }
        if (height >= minimum)
        {
            var extra = height - minimum;
            var need = desired - minimum;
            return sizes.Select(size => (size.Minimum + extra * (size.Desired - size.Minimum) / need) / height).ToArray();
        }
        // Too little room even for one row each: keep the group inside its zone.
        return sizes.Select(size => size.Minimum / minimum).ToArray();
    }
}
