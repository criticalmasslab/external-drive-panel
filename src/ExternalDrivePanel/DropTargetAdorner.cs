using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ExternalDrivePanel;

// Draw above selection templates/cells without changing their brushes or bindings.
internal sealed class DropTargetAdorner : Adorner
{
    public DropTargetAdorner(UIElement element) : base(element) => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(AdornedElement.RenderSize);
        if (bounds.Width < 2 || bounds.Height < 2) return;
        bounds.Inflate(-1, -1);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(110, 255, 205, 40)),
            new Pen(Brushes.DarkOrange, 2), bounds, 3, 3);
    }
}
