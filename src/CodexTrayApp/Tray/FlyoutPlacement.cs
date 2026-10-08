namespace CodexTrayApp.Tray;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>A rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Pure placement maths for the flyout: which taskbar edge to hug and where to put the window.</summary>
public static class FlyoutPlacement
{
    /// <summary>The work area is the monitor minus the taskbar, so the missing strip tells where the taskbar is.</summary>
    public static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect work)
    {
        if (work.Bottom < monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (work.Top > monitor.Top)
        {
            return TaskbarEdge.Top;
        }

        if (work.Left > monitor.Left)
        {
            return TaskbarEdge.Left;
        }

        return work.Right < monitor.Right ? TaskbarEdge.Right : TaskbarEdge.Bottom;
    }

    /// <summary>Top-left corner for a flyout of the given size, anchored at a point (the clicked icon) and kept inside the work area.</summary>
    public static (int X, int Y, TaskbarEdge Edge) Compute(PixelRect monitor, PixelRect work, int anchorX, int anchorY, int width, int height, int margin)
    {
        var edge = DetectEdge(monitor, work);
        var (x, y) = edge switch
        {
            TaskbarEdge.Bottom => (anchorX - (width / 2), work.Bottom - height - margin),
            TaskbarEdge.Top => (anchorX - (width / 2), work.Top + margin),
            TaskbarEdge.Left => (work.Left + margin, anchorY - (height / 2)),
            _ => (work.Right - width - margin, anchorY - (height / 2)),
        };

        return (
            Clamp(x, work.Left + margin, work.Right - width - margin),
            Clamp(y, work.Top + margin, work.Bottom - height - margin),
            edge);
    }

    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
