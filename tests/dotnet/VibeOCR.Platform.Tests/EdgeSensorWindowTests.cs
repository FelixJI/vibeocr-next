using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class EdgeSensorWindowTests
{
    private const uint WmMouseActivate = 0x0021;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmMouseMove = 0x0200;

    [Fact]
    public void CreateAppliesMinimalAlphaImmediately()
    {
        var native = new FakeSensorNative();
        using var sensor = new EdgeSensorWindow(native);

        Assert.Equal(0x1234, sensor.Handle);
        Assert.Contains("alpha:1", native.Calls);
        Assert.False(sensor.IsArmed);
    }

    [Fact]
    public void CreateThrowsWhenWindowCreationFails()
    {
        var native = new FakeSensorNative { NextHandle = 0 };
        Assert.Throws<Win32Exception>(() => new EdgeSensorWindow(native));
    }

    [Fact]
    public void CreateThrowsAndDestroysWhenAlphaFails()
    {
        var native = new FakeSensorNative { AlphaResult = false };
        Assert.Throws<Win32Exception>(() => new EdgeSensorWindow(native));
        Assert.Contains("destroy", native.Calls);
    }

    [Fact]
    public void MouseMoveRaisesPointerEntered()
    {
        var native = new FakeSensorNative();
        using var sensor = new EdgeSensorWindow(native);
        int entered = 0;
        sensor.PointerEntered += (_, _) => entered++;

        nint result = sensor.WindowProc(sensor.Handle, WmMouseMove, 0, 0);

        Assert.Equal(0, result);
        Assert.Equal(1, entered);
    }

    [Fact]
    public void MouseActivateReturnsNoActivate()
    {
        var native = new FakeSensorNative();
        using var sensor = new EdgeSensorWindow(native);

        Assert.Equal((nint)3, sensor.WindowProc(sensor.Handle, WmMouseActivate, 0, 0));
    }

    [Fact]
    public void DisplayChangeRaisesEventAndOtherMessagesUseDefaultProc()
    {
        var native = new FakeSensorNative();
        using var sensor = new EdgeSensorWindow(native);
        int changed = 0;
        sensor.DisplayChanged += (_, _) => changed++;

        Assert.Equal(0, sensor.WindowProc(sensor.Handle, WmDisplayChange, 0, 0));
        Assert.Equal(1, changed);
        Assert.Equal(
            (nint)0xABC,
            sensor.WindowProc(sensor.Handle, 0x0010 /* WM_CLOSE */, 0, 0));
    }

    [Fact]
    public void ArmPlacesTopMostAndShowsWithoutActivating()
    {
        var native = new FakeSensorNative();
        using var sensor = new EdgeSensorWindow(native);
        var bounds = new PhysicalRectangle(0, 0, 1920, 2);

        sensor.Arm(bounds);

        Assert.True(sensor.IsArmed);
        Assert.Equal(bounds, native.PlacedBounds);
        Assert.Equal("alpha:1|topmost|show", string.Join('|', native.Calls));
    }

    [Fact]
    public void DisarmHidesSensor()
    {
        var native = new FakeSensorNative();
        using var sensor = new EdgeSensorWindow(native);
        sensor.Arm(new PhysicalRectangle(0, 0, 10, 2));

        sensor.Disarm();

        Assert.False(sensor.IsArmed);
        Assert.Equal("hide", native.Calls[^1]);
    }

    [Fact]
    public void DisposeHidesAndDestroysOnce()
    {
        var native = new FakeSensorNative();
        var sensor = new EdgeSensorWindow(native);

        sensor.Dispose();
        sensor.Dispose();

        Assert.Equal("alpha:1|hide|destroy", string.Join('|', native.Calls));
        Assert.Throws<ObjectDisposedException>(() => sensor.Arm(new PhysicalRectangle(0, 0, 1, 1)));
        Assert.Throws<ObjectDisposedException>(() => sensor.Disarm());
    }

    private sealed class FakeSensorNative : IEdgeSensorNativeMethods
    {
        public nint NextHandle { get; set; } = 0x1234;

        public bool AlphaResult { get; set; } = true;

        public List<string> Calls { get; } = [];

        public PhysicalRectangle? PlacedBounds { get; private set; }

        public string? CreatedClass { get; private set; }

        public nint CreatedProc { get; private set; }

        public nint CreateSensorWindow(string className, nint windowProc)
        {
            CreatedClass = className;
            CreatedProc = windowProc;
            return NextHandle;
        }

        public bool SetAlpha(nint window, byte alpha)
        {
            Calls.Add($"alpha:{alpha}");
            return AlphaResult;
        }

        public bool PlaceTopMost(nint window, PhysicalRectangle bounds)
        {
            PlacedBounds = bounds;
            Calls.Add("topmost");
            return true;
        }

        public bool ShowNoActivate(nint window)
        {
            Calls.Add("show");
            return true;
        }

        public bool HideWindow(nint window)
        {
            Calls.Add("hide");
            return true;
        }

        public bool DestroySensorWindow(nint window, string className)
        {
            Calls.Add("destroy");
            Assert.Equal(CreatedClass, className);
            return true;
        }

        public nint DefaultWindowProc(nint window, uint message, nuint wParam, nint lParam) =>
            0xABC;
    }
}

/// <summary>
/// 真实 Win32 集成：感应条窗口收到注入的 WM_MOUSEMOVE 后必须触发揭示事件，
/// 并保持置顶样式。SendMessage 同步调用窗口过程，无需消息泵。
/// </summary>
public sealed class EdgeSensorWindowWin32Tests
{
    private const uint WmMouseMove = 0x0200;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [Fact]
    public void RealSensorWindowRaisesPointerEnteredOnInjectedMouseMove()
    {
        using var sensor = new EdgeSensorWindow();
        sensor.Arm(new PhysicalRectangle(0, 0, 1600, ScreenEdgeGeometry.SensorThicknessPx));
        int entered = 0;
        sensor.PointerEntered += (_, _) => entered++;

        nint result = SendMessage(sensor.Handle, WmMouseMove, 0, 0);

        Assert.Equal(0, result);
        Assert.Equal(1, entered);
        // WS_EX_NOACTIVATE(0x08000000) 已在扩展样式中，点击不激活。
        const int GwlExStyle = -20;
        long style = GetWindowLongPtrW(sensor.Handle, GwlExStyle);
        Assert.NotEqual(0, style & 0x08000000);
    }
}
