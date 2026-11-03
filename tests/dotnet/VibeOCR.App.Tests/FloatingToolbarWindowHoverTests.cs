using VibeOCR.App.Features.FloatingToolbar;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 悬浮工具栏按钮悬停命中判定契约：轮询驱动的 PointerOver/Normal 视觉
/// 状态以坐标命中为权威（windowed Popup + NOACTIVATE 下 XAML 悬停不可
/// 靠），边界半开、未命中 -1、空目录安全。真实窗口 hover 由自检与真机
/// 验证覆盖。
/// </summary>
public sealed class FloatingToolbarWindowHoverTests
{
    [Fact]
    public void HitTestButtonReturnsIndexInsideHalfOpenRect()
    {
        Windows.Foundation.Rect[] rects =
        [
            new(0, 4, 36, 36),
            new(40, 4, 36, 36),
        ];

        // 左上角与内部命中第一个；右/下边界半开（贴边属下一个按钮或空隙）。
        Assert.Equal(0, FloatingToolbarWindow.HitTestButton(rects, 0, 4));
        Assert.Equal(0, FloatingToolbarWindow.HitTestButton(rects, 35.9, 39.9));
        Assert.Equal(1, FloatingToolbarWindow.HitTestButton(rects, 40, 4));
        Assert.Equal(1, FloatingToolbarWindow.HitTestButton(rects, 75.9, 39.9));
    }

    [Fact]
    public void HitTestButtonReturnsMinusOneOutsideAllRects()
    {
        Windows.Foundation.Rect[] rects = [new(10, 4, 36, 36)];

        Assert.Equal(-1, FloatingToolbarWindow.HitTestButton(rects, 9.9, 4));
        Assert.Equal(-1, FloatingToolbarWindow.HitTestButton(rects, 46, 4));
        Assert.Equal(-1, FloatingToolbarWindow.HitTestButton(rects, 10, 3.9));
        Assert.Equal(-1, FloatingToolbarWindow.HitTestButton(rects, 10, 40));
        // 未布局的零尺寸按钮（ActualWidth=0）矩形为空，不参与命中。
        Assert.Equal(
            -1,
            FloatingToolbarWindow.HitTestButton([new(10, 4, 0, 0)], 10, 4));
    }

    [Fact]
    public void HitTestButtonHandlesEmptyCatalog()
    {
        Assert.Equal(-1, FloatingToolbarWindow.HitTestButton([], 0, 0));
    }
}
