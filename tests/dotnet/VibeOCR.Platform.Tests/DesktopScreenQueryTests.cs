using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class DesktopScreenQueryTests
{
    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    public void DesktopShellWindowsAreNotFullscreenApps(string className)
    {
        // 主窗隐藏到托盘后前台落回桌面：桌面铺满屏幕但不是全屏应用，
        // 感应条不得因此撤防。
        Assert.True(DesktopScreenQuery.IsDesktopShellWindow(className));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WinUI.Window")]
    [InlineData("progman")]
    public void OtherWindowsAreNotDesktopShell(string? className)
    {
        Assert.False(DesktopScreenQuery.IsDesktopShellWindow(className));
    }
}
