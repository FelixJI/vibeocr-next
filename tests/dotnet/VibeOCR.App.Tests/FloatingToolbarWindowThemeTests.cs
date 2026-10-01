using Microsoft.UI.Xaml;
using VibeOCR.App.Features.FloatingToolbar;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 悬浮工具栏窗口主题解析契约：手动主题不被系统 light/dark 覆盖、高对比
/// 回落系统资源、两套非高对比铬色成对比可辨。真实系统主题/DPI 切换验证
/// 由 root 安全方案统一安排。
/// </summary>
public sealed class FloatingToolbarWindowThemeTests
{
    [Theory]
    [InlineData((int)FloatingToolbarTheme.System, false, (int)ElementTheme.Default)]
    [InlineData((int)FloatingToolbarTheme.Light, false, (int)ElementTheme.Light)]
    [InlineData((int)FloatingToolbarTheme.Dark, false, (int)ElementTheme.Dark)]
    [InlineData((int)FloatingToolbarTheme.System, true, (int)ElementTheme.Default)]
    [InlineData((int)FloatingToolbarTheme.Light, true, (int)ElementTheme.Default)]
    [InlineData((int)FloatingToolbarTheme.Dark, true, (int)ElementTheme.Default)]
    public void RequestedThemeMapsManualSelectionAndHighContrast(
        int theme,
        bool highContrast,
        int expected)
    {
        Assert.Equal(
            (ElementTheme)expected,
            FloatingToolbarWindow.ResolveRequestedTheme((FloatingToolbarTheme)theme, highContrast));
    }

    [Fact]
    public void ChromeColorsProvideDistinctOpaqueContrastPerTheme()
    {
        (Windows.UI.Color darkBackground, Windows.UI.Color darkForeground) =
            FloatingToolbarWindow.ResolveChromeColors(dark: true);
        (Windows.UI.Color lightBackground, Windows.UI.Color lightForeground) =
            FloatingToolbarWindow.ResolveChromeColors(dark: false);

        // 深浅两套底/字互不相同且接近不透明，保证悬停/禁用态可辨。
        Assert.NotEqual(darkBackground, lightBackground);
        Assert.NotEqual(darkForeground, lightForeground);
        Assert.All(
            new[] { darkBackground.A, darkForeground.A, lightBackground.A, lightForeground.A },
            alpha => Assert.InRange(alpha, 200, 255));

        // 深底浅字、浅底深字的亮度关系保持。
        Assert.True(darkBackground.R + darkBackground.G + darkBackground.B
            < darkForeground.R + darkForeground.G + darkForeground.B);
        Assert.True(lightBackground.R + lightBackground.G + lightBackground.B
            > lightForeground.R + lightForeground.G + lightForeground.B);
    }
}
