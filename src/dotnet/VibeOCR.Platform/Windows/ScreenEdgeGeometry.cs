namespace VibeOCR.Platform.Windows;

public enum ScreenEdge
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// 贴边停靠的几何纯函数：感应条矩形、工具栏停靠矩形与吸附判定全部基于
/// physical pixel 计算，供悬浮工具栏的贴边隐藏使用。
/// </summary>
public static class ScreenEdgeGeometry
{
    /// <summary>感应条厚度（physical pixel）。取下限以把边缘占用压到可忽略。</summary>
    public const int SensorThicknessPx = 2;

    /// <summary>拖动释放时距边的吸附判定阈值（physical pixel）。</summary>
    public const int DefaultSnapThresholdPx = 24;

    /// <summary>
    /// 停靠隐藏时贴 monitor 指定边的感应条矩形；厚度超过 monitor 尺寸时收缩。
    /// </summary>
    public static PhysicalRectangle GetSensorRectangle(
        PhysicalRectangle monitor,
        ScreenEdge edge,
        int thickness = SensorThicknessPx)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(thickness, 1);
        int clamped = Math.Min(thickness, monitor.Width);
        return edge switch
        {
            ScreenEdge.Top => new PhysicalRectangle(
                monitor.X, monitor.Y, monitor.Width, clamped),
            ScreenEdge.Bottom => new PhysicalRectangle(
                monitor.X, monitor.Bottom - clamped, monitor.Width, clamped),
            ScreenEdge.Left => new PhysicalRectangle(
                monitor.X, monitor.Y, Math.Min(thickness, monitor.Height), monitor.Height),
            ScreenEdge.Right => new PhysicalRectangle(
                monitor.Right - Math.Min(thickness, monitor.Height), monitor.Y,
                Math.Min(thickness, monitor.Height), monitor.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
        };
    }

    /// <summary>
    /// 工具栏停靠矩形：贴 monitor 指定边的内侧、垂直于该边方向居中；尺寸超过
    /// monitor 时收缩到 monitor 内。
    /// </summary>
    public static PhysicalRectangle GetDockedToolbarRectangle(
        PhysicalRectangle monitor,
        ScreenEdge edge,
        int toolbarWidth,
        int toolbarHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(toolbarWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toolbarHeight, 1);
        int width = Math.Min(toolbarWidth, monitor.Width);
        int height = Math.Min(toolbarHeight, monitor.Height);
        return edge switch
        {
            ScreenEdge.Top => new PhysicalRectangle(
                monitor.X + CenteredOffset(monitor.Width, width), monitor.Y, width, height),
            ScreenEdge.Bottom => new PhysicalRectangle(
                monitor.X + CenteredOffset(monitor.Width, width),
                monitor.Bottom - height, width, height),
            ScreenEdge.Left => new PhysicalRectangle(
                monitor.X, monitor.Y + CenteredOffset(monitor.Height, height), width, height),
            ScreenEdge.Right => new PhysicalRectangle(
                monitor.Right - width,
                monitor.Y + CenteredOffset(monitor.Height, height), width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
        };
    }

    /// <summary>
    /// 吸附判定：返回窗口矩形到 monitor 各边中最近且距离不超过阈值的边；
    /// 全部超阈值时返回 null。距离以窗口矩形到边的 gap 计算，越过边记 0。
    /// </summary>
    public static ScreenEdge? FindSnapEdge(
        PhysicalRectangle window,
        PhysicalRectangle monitor,
        int threshold = DefaultSnapThresholdPx)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 0);
        var candidates = new (ScreenEdge Edge, int Gap)[]
        {
            (ScreenEdge.Top, Math.Max(0, window.Y - monitor.Y)),
            (ScreenEdge.Bottom, Math.Max(0, monitor.Bottom - window.Bottom)),
            (ScreenEdge.Left, Math.Max(0, window.X - monitor.X)),
            (ScreenEdge.Right, Math.Max(0, monitor.Right - window.Right)),
        };
        ScreenEdge? best = null;
        int bestGap = int.MaxValue;
        foreach ((ScreenEdge edge, int gap) in candidates)
        {
            if (gap <= threshold && gap < bestGap)
            {
                best = edge;
                bestGap = gap;
            }
        }

        return best;
    }

    private static int CenteredOffset(int total, int size) =>
        Math.Max(0, (total - size) / 2);
}
