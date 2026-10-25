namespace ExternalDrivePanel;

public sealed class ZoneSettings
{
    public int MonitorIndex { get; set; }
    public int GridCols { get; set; } = 4;
    public int GridRows { get; set; } = 1;
    public int CellCol { get; set; } = 3;
    public int CellRow { get; set; }
    public SplitDirection SplitDirection { get; set; } = SplitDirection.Vertical;
    public bool ResponsivePanels { get; set; } = true;
    public bool MarginUniform { get; set; } = true;
    public double MarginAll { get; set; }
    public double MarginTop { get; set; }
    public double MarginBottom { get; set; }
    public double MarginLeft { get; set; }
    public double MarginRight { get; set; }

    public ZoneSettings Clone() => (ZoneSettings)MemberwiseClone();

    public void CopyFrom(ZoneSettings source)
    {
        MonitorIndex = source.MonitorIndex;
        GridCols = source.GridCols;
        GridRows = source.GridRows;
        CellCol = source.CellCol;
        CellRow = source.CellRow;
        SplitDirection = source.SplitDirection;
        ResponsivePanels = source.ResponsivePanels;
        MarginUniform = source.MarginUniform;
        MarginAll = source.MarginAll;
        MarginTop = source.MarginTop;
        MarginBottom = source.MarginBottom;
        MarginLeft = source.MarginLeft;
        MarginRight = source.MarginRight;
    }
}

public enum SplitDirection
{
    Vertical,
    Horizontal
}
