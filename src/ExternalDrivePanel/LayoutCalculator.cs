namespace ExternalDrivePanel;

public sealed class LayoutCalculator
{
    public const double TopBarHeight = 42;

    public LayoutRect CalculateCell(MonitorDescriptor monitor, ZoneSettings settings)
    {
        var columns = Math.Max(1, settings.GridCols);
        var rows = Math.Max(1, settings.GridRows);
        var column = Math.Clamp(settings.CellCol, 0, columns - 1);
        var row = Math.Clamp(settings.CellRow, 0, rows - 1);
        var cellWidth = monitor.Width / (double)columns;
        var cellHeight = monitor.Height / (double)rows;

        var leftMargin = settings.MarginUniform ? settings.MarginAll : settings.MarginLeft;
        var rightMargin = settings.MarginUniform ? settings.MarginAll : settings.MarginRight;
        var topMargin = settings.MarginUniform ? settings.MarginAll : settings.MarginTop;
        var bottomMargin = settings.MarginUniform ? settings.MarginAll : settings.MarginBottom;

        var x = monitor.Left + (cellWidth * column) + Math.Max(0, leftMargin);
        var y = monitor.Top + (cellHeight * row) + Math.Max(0, topMargin);
        var width = Math.Max(1, cellWidth - Math.Max(0, leftMargin) - Math.Max(0, rightMargin));
        var height = Math.Max(1, cellHeight - Math.Max(0, topMargin) - Math.Max(0, bottomMargin));
        return new LayoutRect(x, y, width, height);
    }

    public LayoutRect CalculateTopBar(LayoutRect cell) =>
        new(cell.X, cell.Y, cell.Width, Math.Min(TopBarHeight, cell.Height));

    public IReadOnlyList<LayoutRect> CalculatePanels(LayoutRect cell, int panelCount, SplitDirection direction)
    {
        if (panelCount <= 0)
        {
            return [];
        }

        var contentTop = cell.Y + Math.Min(TopBarHeight, cell.Height);
        var contentHeight = Math.Max(1, cell.Height - Math.Min(TopBarHeight, cell.Height));
        var panels = new List<LayoutRect>(panelCount);

        if (direction == SplitDirection.Horizontal)
        {
            var width = cell.Width / panelCount;
            for (var index = 0; index < panelCount; index++)
            {
                panels.Add(new LayoutRect(cell.X + (width * index), contentTop, width, contentHeight));
            }
        }
        else
        {
            var height = contentHeight / panelCount;
            for (var index = 0; index < panelCount; index++)
            {
                panels.Add(new LayoutRect(cell.X, contentTop + (height * index), cell.Width, height));
            }
        }

        return panels;
    }
}

public sealed record LayoutRect(double X, double Y, double Width, double Height);
