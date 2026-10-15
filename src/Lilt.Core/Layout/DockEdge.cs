namespace Lilt.Core.Layout;

public enum DockEdge { Left, Right, Top, Bottom }

public static class DockEdgeExtensions
{
    public static bool IsVertical(this DockEdge edge) => edge is DockEdge.Left or DockEdge.Right;

    public static bool NearEdge(this DockEdge edge, double x, double y, double width, double height, double threshold)
    {
        if (x < 0 || y < 0 || x > width || y > height) return false;
        return edge switch
        {
            DockEdge.Left => x <= threshold,
            DockEdge.Right => x >= width - threshold,
            DockEdge.Top => y <= threshold,
            _ => y >= height - threshold,
        };
    }
}
