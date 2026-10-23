using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExternalDrivePanel;

/// <summary>One scalable, monochrome outline icon family; no font/emoji fallback or bitmap scaling.</summary>
public sealed class SymbolIcon : Control
{
    static SymbolIcon()
    {
        HorizontalAlignmentProperty.OverrideMetadata(typeof(SymbolIcon), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(SymbolIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        WidthProperty.OverrideMetadata(typeof(SymbolIcon), new FrameworkPropertyMetadata(22.0));
        HeightProperty.OverrideMetadata(typeof(SymbolIcon), new FrameworkPropertyMetadata(22.0));
    }
    private static readonly Geometry Gear = CreateGear();
    private static Geometry CreateGear()
    {
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            for (var i = 0; i < 32; i++)
            {
                var angle = (i - .5) * Math.PI / 16;
                var radius = i % 4 is 1 or 2 ? 11 : 8.4;
                var point = new Point(12 + radius * Math.Cos(angle), 12 + radius * Math.Sin(angle));
                if (i == 0) context.BeginFigure(point, false, true); else context.LineTo(point, true, false);
            }
        }
        var group = new GeometryGroup(); group.Children.Add(outline); group.Children.Add(new EllipseGeometry(new Point(12, 12), 3.4, 3.4));
        group.Freeze(); return group;
    }
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string),
        typeof(SymbolIcon), new FrameworkPropertyMetadata("Grid", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly DependencyProperty IsColorizedProperty = DependencyProperty.Register(nameof(IsColorized), typeof(bool),
        typeof(SymbolIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsColorized { get => (bool)GetValue(IsColorizedProperty); set => SetValue(IsColorizedProperty, value); }
    private static readonly Geometry PreviewMountains = CreateFrozenGeometry("M3,17 L9,11 14,16 17,13 21,17 V21 H3 Z");
    private static Geometry CreateFrozenGeometry(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["Back"] = "M19,12 H5 M11,6 L5,12 11,18",
        ["Forward"] = "M5,12 H19 M13,6 L19,12 13,18",
        ["Up"] = "M12,20 V4 M6,10 L12,4 18,10",
        ["Home"] = "M3,11 L12,3 21,11 M6,9 V21 H10 V14 H14 V21 H18 V9",
        ["HomeFill"] = "M2,11 L12,2 22,11 H19 V22 H14 V15 H10 V22 H5 V11 Z",
        ["Favorite"] = "M10,3 L12.5,8 18,9 14,13 15,19 10,16 5,19 6,13 2,9 7.5,8 Z M20,15 V23 M16,19 H24",
        ["StarFill"] = "M12,2 L15,8.5 22,9.5 17,14.5 18.2,22 12,18.3 5.8,22 7,14.5 2,9.5 9,8.5 Z",
        ["Eject"] = "M5,14 L12,4 19,14 Z M5,19 H19",
        ["Grid"] = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z",
        ["List"] = "M4,5 H6 M10,5 H20 M4,12 H6 M10,12 H20 M4,19 H6 M10,19 H20",
        ["Plus"] = "M12,4 V20 M4,12 H20",
        ["Minus"] = "M4,12 H20",
        ["Close"] = "M6,6 L18,18 M18,6 L6,18",
        ["Search"] = "M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M15,15 L21,21",
        ["Minimize"] = "M5,18 H19",
        ["Maximize"] = "M5,5 H19 V19 H5 Z",
        ["Restore"] = "M8,8 H19 V19 H8 Z M5,16 H3 V3 H16 V5",
        ["Eye"] = "M2,12 Q12,-1 22,12 Q12,25 2,12 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12",
        ["Preview"] = "M3,3 H21 V21 H3 Z M3,17 L9,11 14,16 17,13 21,17 M18,7 A2,2 0 1 1 14,7 A2,2 0 1 1 18,7",
        ["Settings"] = "M9,3 H15 L16,6 19,7 22,9 V15 L19,16 18,19 15,22 H9 L8,19 5,18 2,15 V9 L5,8 6,5 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
        ["Cut"] = "M9,8 L20,21 M9,16 L20,3 M9,6 A3,3 0 1 1 3,6 A3,3 0 1 1 9,6 M9,18 A3,3 0 1 1 3,18 A3,3 0 1 1 9,18",
        ["Copy"] = "M8,8 H21 V21 H8 Z M16,5 V3 H3 V16 H5",
        ["Paste"] = "M8,5 H4 V21 H20 V5 H16 M8,3 H16 V7 H8 Z M8,12 H16 M8,16 H14",
        ["Rename"] = "M8,7 H3 V18 H8 M16,7 H21 V18 H16 M9,3 H15 M12,3 V22 M9,22 H15",
        ["Share"] = "M14,3 L21,9 14,15 V11 Q7,11 4,17 Q3,7 14,7 Z M3,17 V21 H21 V16",
        ["Delete"] = "M3,6 H21 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17"
    }.ToDictionary(pair => pair.Key, pair =>
    {
        var geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry;
    });

    public SymbolIcon()
    {
        Focusable = false; IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var shape = Kind == "Settings" ? Gear : Shapes.GetValueOrDefault(Kind);
        if (shape is null) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 26;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        var filled = Kind is "StarFill" or "HomeFill";
        if (Kind == "Preview" && IsColorized)
        {
            drawingContext.DrawRectangle(Brushes.SkyBlue, null, new Rect(3, 3, 18, 18));
            drawingContext.DrawEllipse(Brushes.DarkOrange, null, new Point(16, 7), 2, 2);
            drawingContext.DrawGeometry(Brushes.ForestGreen, null, PreviewMountains);
            drawingContext.DrawRectangle(null, new Pen(Foreground, 1.7), new Rect(3, 3, 18, 18));
        }
        else
            drawingContext.DrawGeometry(filled ? Foreground : null, filled ? null : new Pen(Foreground, 1.7)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            }, shape);
        drawingContext.Pop(); drawingContext.Pop();
    }
}
