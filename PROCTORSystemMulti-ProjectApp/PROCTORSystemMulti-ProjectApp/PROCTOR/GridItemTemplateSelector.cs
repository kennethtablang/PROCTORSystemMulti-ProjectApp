namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>Dashboard grid: a live student tile, or (lab layout) an empty / offline seat.</summary>
public sealed class GridItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Tile { get; set; }
    public DataTemplate? Seat { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        (item is StudentTile ? Tile : Seat) ?? throw new InvalidOperationException("Grid templates are missing.");
}
