using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App;

public sealed partial class App
{
  // 独立应用进程具有 Program.Main 的 STA/XAML 初始化；不在 xunit 宿主内启动第二个 Application。
  private async Task RunCaptureUiSmokeAsync()
  {
    string root = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_UI_ROOT")
        ?? throw new InvalidOperationException("Capture UI smoke requires its isolated root.");
    string result = Path.Combine(root, "result.txt");
    try
    {
      string scenario = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_UI_SCENARIO") ?? "";
      var layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "production");
      layout.EnsurePortableState();
      var store = new CapturePreferencesStore(layout);
      switch (scenario)
      {
        case "ScrollCaptureCorrectionAuthorizationStaysWithItsPendingFrame":
          await ScrollCaptureSession.Session.VerifyCorrectionAuthorizationSelfCheckAsync();
          break;
        case "ScrollCaptureAmbiguousChoicesPreserveStateAndAllowFeasibleCorrection":
          await ScrollCaptureSession.Session.VerifyAmbiguousCorrectionsSelfCheckAsync();
          break;
        case "ScrollCaptureNoOverlapStopsWithoutImpossibleCorrection":
          await ScrollCaptureSession.Session.VerifyNoOverlapStopsSelfCheckAsync();
          break;
        case "ScrollCaptureKeepsFirstFailureAcrossDeadlineAndConcurrentStops":
          await ScrollCaptureSession.Session.VerifyFirstFailureSelfCheckAsync();
          break;
        case "ScrollCaptureAllowsNarrowHorizontalSelectionAndEnforcesAxisLimits":
          ScrollCaptureSession.Session.VerifySelectionGeometrySelfCheck();
          break;
        case "ScrollCaptureReverseFrameCanBeDiscardedBeforeForwardResume":
          await ScrollCaptureSession.Session.VerifyReverseRecoverySelfCheckAsync();
          break;
        case "ScrollCapturePendingPreviewBoundsBothDimensionsWithoutUpscaling":
          await ScrollCaptureSession.Session.VerifyPendingPreviewBoundsSelfCheckAsync();
          break;
        case "ScrollCapturePreviewWaitsForDecodeAndRejectsPendingEncodingFailure":
          await ScrollCaptureSession.Session.VerifyPreviewGateSelfCheckAsync();
          break;
        case "ScrollCaptureAutoScrollRequiresPreviewAndReturnsConfirmedSegment":
        case "ScrollCaptureCancellationDiscardsFramesAndClosesController":
        case "ScrollCaptureVanishedSourceFailsWithoutReturningPartialFrame":
          await VerifyScrollCaptureUiAsync(scenario);
          break;
        case "OptionsDialogSavesMergedOptionsAndKeepsFreshLastRegion":
          VerifyCaptureOptionsMerge(store);
          break;
        case "OptionsDialogShowsSaveErrorsAndFallsBackForInvalidInput":
          VerifyCaptureOptionsErrors(store);
          break;
        case "DelayedOverlayCancelStopsCountdownAndReturnsFalse":
          await VerifyDelayedCaptureCancellationAsync();
          break;
        case "DelayedOverlayCompletesAfterConfiguredSeconds":
          await VerifyDelayedCaptureKeepsNewForegroundAsync();
          break;
        case "DelayedOverlayEscKeyCancelsAndRestoresForeground":
          await VerifyDelayedCaptureEscKeyAsync();
          break;
        case "RegionPickerRejectsChangedMonitorLayoutWithSameDesktopBounds":
          await VerifyRegionPickerLayoutChangeAsync();
          break;
        default:
          throw new InvalidOperationException("Unknown capture UI smoke scenario.");
      }
      File.WriteAllText(result, "passed");
      Environment.Exit(0);
    }
    catch (Exception error)
    {
      File.WriteAllText(result, error.ToString());
      Environment.Exit(1);
    }
  }

  private static async Task VerifyScrollCaptureUiAsync(string scenario)
  {
    var rows = new Microsoft.UI.Xaml.Controls.StackPanel();
    for (int index = 0; index < 50; index++)
      rows.Children.Add(new Microsoft.UI.Xaml.Controls.Border
      {
        Height = 24,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, (byte)(index * 5), (byte)(255 - index * 3), (byte)(index * 2))),
        Child = new Microsoft.UI.Xaml.Controls.TextBlock { Text = $"synthetic scroll row {index:D3}" },
      });
    var viewer = new Microsoft.UI.Xaml.Controls.ScrollViewer
    {
      Content = rows,
      VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden,
      HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
    };
    int wheelEvents = 0;
    viewer.AddHandler(UIElement.PointerWheelChangedEvent,
        new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => wheelEvents++), handledEventsToo: true);
    var source = new Window { Title = "Owned synthetic scroll source", Content = viewer };
    var owner = new Window { Content = new Microsoft.UI.Xaml.Controls.Grid() };
    nint sourceHandle = WinRT.Interop.WindowNative.GetWindowHandle(source);
    nint ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
    bool sourceClosed = false;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    Task<CapturedFrame?>? capture = null;
    try
    {
      PhysicalRectangle monitor = DesktopScreenQuery.GetPrimaryMonitor();
      source.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(monitor.X + 80, monitor.Y + 80, 360, 320));
      await ActivateAndWaitForegroundAsync(source, sourceHandle);
      await Task.Delay(150, cancellation.Token);
      PhysicalRectangle client = ScreenTargetQuery.TryGetClientBounds(sourceHandle)
          ?? throw new InvalidOperationException("Owned scroll source has no client bounds.");
      var bounds = new PhysicalRectangle(client.X + 16, client.Y + 16, client.Width - 32, client.Height - 32);
      var session = new ScrollCaptureSession.Session(ownerHandle, bounds, cancellation.Token);
      capture = session.RunAsync();
      nint controllerHandle = WinRT.Interop.WindowNative.GetWindowHandle(session.ControlWindow);
      await Task.Delay(150, cancellation.Token);
      var root = (DependencyObject)session.ControlWindow.Content;
      Microsoft.UI.Xaml.Controls.Button Button(string id) =>
          FindScrollSmokeControl<Microsoft.UI.Xaml.Controls.Button>(root, id);
      void Invoke(string id)
      {
        var peer = new ButtonAutomationPeer(Button(id));
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
      }
      bool success = scenario == "ScrollCaptureAutoScrollRequiresPreviewAndReturnsConfirmedSegment";
      FindScrollSmokeControl<Microsoft.UI.Xaml.Controls.CheckBox>(root, "scroll-capture-automatic").IsChecked = success;
      Invoke("scroll-capture-start");
      var confirm = Button("scroll-capture-confirm");
      await WaitForScrollUiAsync(() => confirm.Visibility == Visibility.Visible && confirm.IsEnabled, cancellation.Token);
      Check(viewer.VerticalOffset == 0 && !capture.IsCompleted, "Initial preview must precede scrolling/output.");
      if (scenario == "ScrollCaptureCancellationDiscardsFramesAndClosesController")
      {
        Invoke("scroll-capture-cancel");
        Check(await capture is null, "Cancellation after the first accepted frame must not return a partial image.");
      }
      else
      {
        if (!success)
        {
          source.Close();
          sourceClosed = true;
          await WaitForScrollUiAsync(() => !Button("scroll-capture-finish").IsEnabled && confirm.Visibility == Visibility.Collapsed,
              cancellation.Token);
          Invoke("scroll-capture-cancel");
          try { await capture; throw new InvalidOperationException("Vanished source returned a partial image."); }
          catch (InvalidOperationException error)
          {
            Check(error.Message.Contains("源窗口", StringComparison.Ordinal), "Failure must identify the source window.");
          }
        }
        else
        {
          Invoke("scroll-capture-confirm");
          try { await WaitForScrollUiAsync(() => viewer.VerticalOffset >= 32, cancellation.Token); }
          catch (InvalidOperationException error)
          {
            throw new InvalidOperationException(
                $"{error.Message} offset={viewer.VerticalOffset}, wheelEvents={wheelEvents}; {session.DiagnosticState}", error);
          }
          Check(wheelEvents > 0, "Automatic scrolling must enter the real XAML pointer wheel input pipeline.");
          await Task.Delay(1400, cancellation.Token);
          Invoke("scroll-capture-finish");
          await WaitForScrollUiAsync(() => confirm.Visibility == Visibility.Visible && confirm.IsEnabled &&
              Equals(confirm.Content, "确认选段并送入编辑器"), cancellation.Token);
          Check(!capture.IsCompleted, "Final preview must require explicit user confirmation.");
          Invoke("scroll-capture-confirm");
          CapturedFrame? output = await capture;
          Check(output is not null && output.Width == bounds.Width && output.Height > bounds.Height,
              "Automatic standard scrolling must return the confirmed multi-frame segment.");
        }
      }
      Check(session.HasReleasedCaptureResources, "The capture loop and retained frames must be released before returning.");
      Check(!IsWindowVisible(controllerHandle),
          "Capture controller must be closed before returning.");
    }
    finally
    {
      cancellation.Cancel();
      if (capture is not null) { try { await capture; } catch (Exception) { } }
      if (!sourceClosed) source.Close();
      ScreenRegionPicker.RestoreOwnerState(ownerHandle, visible: true, minimized: false, foreground: 0);
      Check(IsWindowVisible(ownerHandle), "Owner must be restorable after success, cancellation or failure.");
      owner.Close();
    }
  }

  private static T FindScrollSmokeControl<T>(DependencyObject root, string id) where T : FrameworkElement
  {
    if (root is T found && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(found) == id) return found;
    for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
    {
      try { return FindScrollSmokeControl<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index), id); }
      catch (KeyNotFoundException) { }
    }
    throw new KeyNotFoundException($"Owned scroll control was not found: {id}");
  }

  private static async Task WaitForScrollUiAsync(Func<bool> ready, CancellationToken cancellationToken)
  {
    for (int attempt = 0; attempt < 120; attempt++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (ready()) return;
      await Task.Delay(50, cancellationToken);
    }
    throw new InvalidOperationException("Owned scroll UI did not reach the expected state within 6 seconds.");
  }
  private static void VerifyCaptureOptionsMerge(CapturePreferencesStore store)
  {
    store.SaveLastRegion(new PhysicalRectangle(5, 6, 100, 80));
    var dialog = new CaptureOptionsDialog(store);
    try
    {
      dialog.Show(0);
      store.SaveLastRegion(new PhysicalRectangle(9, 9, 42, 24));
      dialog.DelayInput.Value = 8;
      dialog.CursorInput.IsOn = true;
      dialog.BoundsInput.SelectedIndex = 1;
      dialog.FixedXInput.Value = -12;
      dialog.FixedYInput.Value = -34;
      dialog.FixedWInput.Value = 320;
      dialog.FixedHInput.Value = 240;
      Check(dialog.TrySave() is null, "Valid options must save.");
      CapturePreferences loaded = store.Load();
      Check(loaded.EffectiveDelaySeconds == 8, "Delay must round-trip.");
      Check(loaded.IncludeCursor, "Cursor option must round-trip.");
      Check(loaded.EffectiveWindowBoundsMode == ScreenTargetQuery.WindowBoundsClient,
          "Client bounds option must round-trip.");
      Check(loaded.FixedRegion == new PhysicalRectangle(-12, -34, 320, 240),
          "Fixed region must retain negative coordinates.");
      Check(loaded.LastRegion?.Bounds == new PhysicalRectangle(9, 9, 42, 24),
          "Saving options must preserve a newer last region.");
    }
    finally { CloseSmokeDialog(dialog); }
  }

  private static void VerifyCaptureOptionsErrors(CapturePreferencesStore store)
  {
    var failing = new CaptureOptionsDialog(new CaptureSmokeFailingStore());
    var invalid = new CaptureOptionsDialog(store);
    var nan = new CaptureOptionsDialog(store);
    try
    {
      failing.Show(0);
      failing.DelayInput.Value = 9;
      Check(failing.TrySave() is not null, "Storage failure must be reported.");
      Check(failing.ErrorText.Visibility == Visibility.Visible, "Storage error must be visible.");
      Check(failing.Window is not null && failing.DelayInput.Value == 9,
          "Failed save must preserve window and pending input.");

      invalid.Show(0);
      invalid.DelayInput.Value = double.NaN;
      invalid.FixedWInput.Value = -5;
      Check(invalid.TrySave() is not null && invalid.ErrorText.Text.Contains("正数", StringComparison.Ordinal),
          "Invalid region dimensions must be rejected visibly.");
      invalid.FixedWInput.Value = 100;
      invalid.FixedHInput.Value = 50;
      Check(invalid.TrySave() is null, "Corrected region must save.");

      nan.Show(0);
      nan.DelayInput.Value = double.NaN;
      Check(nan.TrySave() is null, "NaN delay must safely fall back.");
      Check(store.Load().EffectiveDelaySeconds == 3, "NaN delay must use the default.");
    }
    finally
    {
      CloseSmokeDialog(nan);
      CloseSmokeDialog(invalid);
      CloseSmokeDialog(failing);
    }
  }

  private static async Task VerifyDelayedCaptureKeepsNewForegroundAsync()
  {
    var original = new Window { Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "capture A" } };
    var target = new Window { Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "capture B" } };
    try
    {
      nint originalHandle = WinRT.Interop.WindowNative.GetWindowHandle(original);
      nint targetHandle = WinRT.Interop.WindowNative.GetWindowHandle(target);
      await ActivateAndWaitForegroundAsync(original, originalHandle);
      Task<bool> pending = DelayedCaptureOverlay.RunAsync(1, CancellationToken.None);
      target.Activate();
      await WaitForCaptureUiAsync(() => ScreenTargetQuery.GetForegroundWindowHandle() == targetHandle);
      Check(await pending, "Countdown must complete after its configured delay.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == targetHandle,
          "Successful countdown must keep the user's newly selected foreground B.");
    }
    finally
    {
      target.Close();
      original.Close();
    }
  }

  private static async Task VerifyRegionPickerLayoutChangeAsync()
  {
    PhysicalRectangle desktop = ScreenTargetQuery.VirtualDesktop();
    int split = desktop.Width / 2;
    int shift = Math.Min(360, desktop.Height / 3);
    var primary = new ScreenMonitorTarget(new PhysicalRectangle(
        desktop.X, desktop.Y, split, desktop.Height), true, "synthetic-primary");
    var secondary = new ScreenMonitorTarget(new PhysicalRectangle(
        desktop.X + split, desktop.Y, desktop.Width - split, desktop.Height - shift),
        false, "synthetic-secondary");
    ScreenMonitorTarget[] before = [primary, secondary];
    ScreenMonitorTarget[] after = [primary, secondary with { Bounds = secondary.Bounds with { Y = desktop.Y + shift } }];
    var owner = new Window { Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "capture layout owner" } };
    owner.Activate();
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
    int queries = 0;
    var native = new SyntheticCapturePixels();
    var picker = new ScreenRegionPicker(() => handle, native, () => ++queries <= 2 ? before : after);
    try
    {
      Check(ScreenTargetQuery.VirtualDesktop() == desktop, "Synthetic fixture must not reconfigure the desktop.");
      ScreenRegionSelection? selection = null;
      try
      {
        selection = await picker.PickAsync(CancellationToken.None);
        throw new InvalidOperationException("Region picker accepted a changed monitor layout with unchanged outer bounds.");
      }
      catch (CaptureValidationException error)
      {
        Check(error.Message.Contains("显示器布局", StringComparison.Ordinal), "Picker must report layout changes.");
        Check(error.StatusCode == "recognition.captureRefused.layoutChanged",
            "Picker layout refusal must carry a stable status code.");
        Check(queries >= 3 && native.Captures == 1, "Regression must reach real picker confirmation after freezing.");
        Check(IsWindowVisible(handle), "Rejected region must restore owner visibility.");
      }
      finally { selection?.CaptureScene?.Dispose(); }
    }
    finally { owner.Close(); }
  }

  private sealed class SyntheticCapturePixels : IScreenCaptureNativeMethods
  {
    internal int Captures;
    public byte[] CaptureBgra(PhysicalRectangle bounds)
    {
      Captures++;
      return new byte[checked(bounds.Width * bounds.Height * 4)];
    }
  }

  private static async Task VerifyDelayedCaptureCancellationAsync()
  {
    var owner = new Window { Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "capture smoke owner" } };
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
    try
    {
      await ActivateAndWaitForegroundAsync(owner, handle);
      var input = new InputService(() => handle);
      try
      {
        await input.CaptureAreaAsync(new ScreenshotCaptureRequest(
            ScreenshotCaptureKind.ActiveWindow, TargetWindow: unchecked((nint)0xDEAD)),
            CancellationToken.None);
        throw new InvalidOperationException("Vanished capture target was accepted.");
      }
      catch (CaptureValidationException)
      {
        Check(IsWindowVisible(handle), "Direct capture failure must restore owner visibility.");
        Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle,
            "Direct capture failure must restore owned foreground.");
      }
      using (var stopped = new CancellationTokenSource())
      {
        stopped.Cancel();
        try
        {
          await input.CaptureAreaAsync(new ScreenshotCaptureRequest(ScreenshotCaptureKind.Fullscreen),
              stopped.Token);
          throw new InvalidOperationException("Cancelled capture was accepted.");
        }
        catch (OperationCanceledException)
        {
          Check(IsWindowVisible(handle), "Cancelled direct capture must restore owner visibility.");
        }
      }
      var overlay = new DelayedCaptureOverlay(30);
      Task<bool> pending = overlay.ShowAsync(CancellationToken.None);
      await WaitForCaptureUiAsync(() => overlay.CancelButton.IsLoaded);
      var peer = new ButtonAutomationPeer(overlay.CancelButton);
      ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
      Check(!await pending, "Cancel button must stop countdown.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle, "Cancel must restore owned foreground.");

      var closed = new DelayedCaptureOverlay(30);
      Task<bool> closing = closed.ShowAsync(CancellationToken.None);
      closed.Window.Close();
      Check(!await closing, "Closing the countdown window must cancel.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle, "Close must restore owned foreground.");

      using var token = new CancellationTokenSource();
      Task<bool> cancelled = DelayedCaptureOverlay.RunAsync(30, token.Token);
      token.Cancel();
      Check(!await cancelled, "Cancellation token must stop countdown.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle, "Token cancellation must restore foreground.");
    }
    finally { owner.Close(); }
  }

  /// <summary>
  /// #245 C2：真实键盘路径验证——SendInput 注入真实 ESC 按下/抬起（倒计时
  /// 窗为前台），断言取消生效并恢复宿主前台；不调用任何内部取消方法。
  /// </summary>
  private static async Task VerifyDelayedCaptureEscKeyAsync()
  {
    var owner = new Window { Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "capture esc owner" } };
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
    try
    {
      await ActivateAndWaitForegroundAsync(owner, handle);
      var overlay = new DelayedCaptureOverlay(30);
      Task<bool> pending = overlay.ShowAsync(CancellationToken.None);
      await WaitForCaptureForegroundAsync(overlay);
      Check(SendRealEscapeKey() == 2, "Both real ESC keydown and keyup must be accepted.");
      Check(!await pending.WaitAsync(TimeSpan.FromSeconds(10)), "Real ESC key must cancel the countdown.");
      await WaitForCaptureUiAsync(() => ScreenTargetQuery.GetForegroundWindowHandle() == handle);
    }
    finally { owner.Close(); }
  }

  private static async Task WaitForCaptureForegroundAsync(DelayedCaptureOverlay overlay)
  {
    for (int attempt = 0; attempt < 200; attempt++)
    {
      if (ScreenTargetQuery.GetForegroundWindowHandle() == overlay.Handle && overlay.CancelButton.IsLoaded)
        return;
      await Task.Delay(25);
    }
    throw new InvalidOperationException("Countdown window never became the foreground target.");
  }

  private static uint SendRealEscapeKey()
  {
    const uint keyEventfKeyUp = 0x0002;
    var down = new SmokeKeyInput { Type = 1, VirtualKey = 0x1B };
    var up = new SmokeKeyInput { Type = 1, VirtualKey = 0x1B, Flags = keyEventfKeyUp };
    return SendInput(2, [down, up], Marshal.SizeOf<SmokeKeyInput>());
  }

  [StructLayout(LayoutKind.Explicit)]
  private struct SmokeKeyInput
  {
    [FieldOffset(0)] public uint Type;
    [FieldOffset(8)] public ushort VirtualKey;
    [FieldOffset(10)] public ushort Scan;
    [FieldOffset(12)] public uint Flags;
    [FieldOffset(16)] public uint Time;
    [FieldOffset(24)] public nint ExtraInfo;
    [FieldOffset(32)] private readonly long _padding;
  }

  [DllImport("user32.dll", SetLastError = true)]
  private static extern uint SendInput(uint count, SmokeKeyInput[] inputs, int size);

  /// <summary>
  /// 单次激活并等待真实前台；拒绝时如实失败并携带当时前台窗口诊断
  /// （不重试、不削弱断言；前台许可与启动环境的关系由主代理复测裁决）。
  /// </summary>
  private static async Task ActivateAndWaitForegroundAsync(Window window, nint handle)
  {
    window.Activate();
    for (int poll = 0; poll < 40; poll++)
    {
      if (ScreenTargetQuery.GetForegroundWindowHandle() == handle) return;
      await Task.Delay(25);
    }

    nint current = ScreenTargetQuery.GetForegroundWindowHandle();
    throw new InvalidOperationException(
        $"Capture UI foreground activation was denied; owner={handle} current={current} " +
        $"title='{(current == 0 ? "" : GetWindowTextDiagnostics(current))}'.");
  }

  private static string GetWindowTextDiagnostics(nint window)
  {
    var buffer = new System.Text.StringBuilder(256);
    return GetWindowTextW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "<no title>";
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetWindowTextW(nint window, System.Text.StringBuilder text, int capacity);

  private static async Task WaitForCaptureUiAsync(Func<bool> ready)
  {
    for (int attempt = 0; attempt < 40; attempt++)
    {
      if (ready()) return;
      await Task.Delay(25);
    }
    throw new InvalidOperationException("Capture UI did not reach its expected state.");
  }

  private static void CloseSmokeDialog(CaptureOptionsDialog dialog)
  {
    try { dialog.Window?.Close(); } catch (COMException) { }
  }

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindowVisible(nint window);

  private static void Check(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }

  private sealed class CaptureSmokeFailingStore : ICapturePreferencesStore
  {
    public CapturePreferences Load() => new();
    public void Save(CapturePreferences preferences) => throw new IOException("simulated disk full");
    public void SaveLastRegion(PhysicalRectangle bounds) =>
        throw new InvalidOperationException("Options dialog must not save last region.");
  }
}
