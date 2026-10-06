using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using VibeOCR.Platform.Windows;
using VibeOCR.App.Services;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;

namespace VibeOCR.App.Features.Recognition;

public sealed record ScreenRegionSelection(
    PhysicalRectangle Bounds,
    byte[] Bgra,
    int Stride,
    int? PixelHeight = null)
{
    internal ScreenshotCaptureScene? CaptureScene { get; init; }

    /// <summary>
    /// 普通截图入口动作栏选择的显式动作；null 或 <see cref="ScreenshotSelectionAction.Edit"/>
    /// 表示沿用既有确认语义（现场编辑宿主）。专用直接入口不设置该值。
    /// </summary>
    internal ScreenshotSelectionAction? SelectionAction { get; init; }

    /// <summary>动作栏识别菜单显式选择的 typed 模式/引擎 id；仅 Recognize 动作携带。</summary>
    internal string? RecognitionModeId { get; init; }
}

/// <summary>普通截图选区动作阶段可选动作；沿 selection/input 一路承载到宿主，不新增服务抽象。</summary>
public enum ScreenshotSelectionAction
{
    /// <summary>进入既有现场编辑宿主（普通入口的可见主动作）。</summary>
    Edit,

    /// <summary>按动作栏菜单显式选择的 typed 识别模式提交一次识别。</summary>
    Recognize,

    /// <summary>复制选区像素（复用既有会话本地输出）。</summary>
    Copy,

    /// <summary>保存选区像素（复用既有会话本地输出）。</summary>
    Save,

    /// <summary>把选区像素钉到桌面贴图（复用既有会话本地输出）。</summary>
    Pin,

    /// <summary>放弃本次截图并显式进入现有设置页。</summary>
    OpenSettings,
}

/// <summary>
/// 动作栏识别菜单的一条宿主动态 typed 目录投影：只携带既有 choice 的
/// id/显示名/availability，picker 不复制引擎支持表或高级配方。
/// </summary>
public sealed record ScreenshotRecognitionModeEntry(
    string ModeId,
    string DisplayName,
    string Availability,
    string? ReasonCode = null);

/// <summary>
/// 普通截图入口的选区动作请求（宿主当前目录投影）。null 表示专用直接
/// 入口：保留既有立即确认语义，不显示动作栏。
/// </summary>
public sealed record ScreenshotSelectionActions(
    IReadOnlyList<ScreenshotRecognitionModeEntry> Modes);

/// <summary>冻结桌面仅属于本次截图；确认后移交给同一窗口的编辑器。</summary>
internal sealed class ScreenshotCaptureScene : IDisposable
{
    private readonly ScreenshotOwnerRestoration ownerRestoration = new();
    private bool closed;
    internal ScreenshotCaptureScene(Window window, Grid root,
        PhysicalRectangle desktop, PhysicalRectangle bounds)
    {
        Window = window;
        Root = root;
        Desktop = desktop;
        Bounds = bounds;
        Window.Closed += OnClosed;
    }
    internal Window Window { get; }
    internal Grid Root { get; }
    internal PhysicalRectangle Desktop { get; }
    internal PhysicalRectangle Bounds { get; }

    internal void RestoreOwnerOnClose(Action restore)
    {
        ownerRestoration.Set(restore);
    }

    internal void TransferOwnerRestorationTo(ScreenshotCaptureScene successor) =>
        ownerRestoration.TransferTo(successor.ownerRestoration);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Window.Closed -= OnClosed;
        closed = true;
        Root.Children.Clear();
        ownerRestoration.Restore();
    }

    public void Dispose()
    {
        if (closed) return;
        Window.Close();
    }
}

internal sealed class ScreenshotOwnerRestoration
{
    private Action? restore;
    private bool restored;

    internal void Set(Action callback)
    {
        if (restored) { callback(); return; }
        restore = callback;
    }

    internal void TransferTo(ScreenshotOwnerRestoration successor)
    {
        if (ReferenceEquals(this, successor)) return;
        if (Interlocked.Exchange(ref restore, null) is { } callback) successor.Set(callback);
    }

    internal void Restore()
    {
        restored = true;
        Interlocked.Exchange(ref restore, null)?.Invoke();
    }
}

public interface IScreenRegionPicker
{
    Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 普通入口：携带动作栏目录投影调用。默认回落到直接入口行为，
    /// 供专用识别/选字与自检 picker 保持既有立即确认语义。
    /// </summary>
    Task<ScreenRegionSelection?> PickAsync(
        ScreenshotSelectionActions actions,
        CancellationToken cancellationToken) => PickAsync(cancellationToken);
}

/// <summary>
/// Lightweight region selector for the WinUI screenshot workflow.  It takes a
/// single frozen snapshot after hiding the main window, then crops from that
/// snapshot so the application cannot reappear in the final OCR input.
/// </summary>
public sealed class ScreenRegionPicker(Func<nint> ownerWindow, bool scrolling = false) : IScreenRegionPicker
{
    private sealed class SelectionCanvas : Canvas
    {
      public void SetCursor(InputSystemCursorShape shape)
      {
        // Pointer capture can change the native cursor without changing the requested shape.
        ProtectedCursor = InputSystemCursor.Create(shape);
      }
    }

    private const long MaximumCaptureBytes = 256L << 20;
    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private readonly Func<nint> _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));

    public Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken) =>
        PickAsyncCore(null, cancellationToken);

    public Task<ScreenRegionSelection?> PickAsync(
        ScreenshotSelectionActions actions,
        CancellationToken cancellationToken) =>
        PickAsyncCore(actions ?? throw new ArgumentNullException(nameof(actions)), cancellationToken);

    private async Task<ScreenRegionSelection?> PickAsyncCore(
        ScreenshotSelectionActions? selectionActions,
        CancellationToken cancellationToken)
    {
        nint owner = _ownerWindow();
        bool visible = IsWindowVisible(owner);
        bool minimized = IsIconic(owner);
        nint foreground = GetForegroundWindow();
        ScreenshotCaptureScene? scene = null;
        ShowWindow(owner, 0);
        try
        {
            await Task.Delay(180, cancellationToken);
            PhysicalRectangle desktop = GetVirtualDesktop();
            SmartScreenCandidates candidates = SmartScreenCandidates.Capture(desktop);
            await using var capture = new ScreenCaptureService(Guid.NewGuid());
            CapturedFrame? frame = capture.Capture(desktop, TimeSpan.FromMinutes(1));
            byte[] desktopBgra = capture.Read(frame);
            byte[] desktopBmp = EncodeTopDownBmp(
                desktopBgra,
                desktop.Width,
                desktop.Height,
                frame.Stride);
            BitmapImage? background = await LoadBitmapAsync(
                desktopBmp,
                desktop.Width,
                desktop.Height);
            OverlaySelection? selection = await ShowOverlayAsync(
                desktop,
                background,
                desktopBgra,
                candidates,
                keepForEditing: !scrolling,
                cancellationToken,
                selectionActions: selectionActions);
            if (selection is null)
            {
                return null;
            }

            PhysicalRectangle selected = selection.Bounds;
            scene = selection.Scene;
            if (scrolling)
            {
                // The frozen desktop is only for selection, not a scrolling history.
                frame = null;
                desktopBgra = [];
                desktopBmp = [];
                background = null;
                await Task.Delay(180, cancellationToken);
                CapturedFrame? stitched = await ScrollCaptureSession.CaptureAsync(
                    owner, selected, cancellationToken);
                return stitched is null ? null : new ScreenRegionSelection(
                    selected, stitched.Pixels, stitched.Stride, stitched.Height);
            }

            byte[] cropped = CropBgra(desktopBgra, desktop, selected);
            return new ScreenRegionSelection(
                selected,
                cropped,
                selected.Width * 4)
            {
                CaptureScene = scene,
                SelectionAction = selection.SelectionAction,
                RecognitionModeId = selection.RecognitionModeId,
            };
        }
        catch
        {
            scene?.Dispose();
            scene = null;
            throw;
        }
        finally
        {
            void RestoreOwner() => RestoreOwnerState(owner, visible, minimized, foreground);
            // 确认选区与加载编辑器之间不闪回主窗口。
            if (scene is null) RestoreOwner();
            else scene.RestoreOwnerOnClose(RestoreOwner);
        }
    }

    /// <summary>
    /// 截图后恢复宿主窗口原状态的实际执行路径：可见→按原形态显示（最小化
    /// SW_SHOWMINNOACTIVE=7，普通 SW_SHOWNA=8，均不夺取激活）；原本隐藏
    /// （托盘）→保持隐藏；截图前前台非空句柄时恢复其前台。production
    /// finally 与无 Runtime 的窗口恢复自检共用同一方法。
    /// </summary>
    internal static void RestoreOwnerState(nint owner, bool visible, bool minimized, nint foreground)
    {
        if (visible) ShowWindow(owner, minimized ? 7 : 8);
        if (foreground != 0) SetForegroundWindow(foreground);
    }

    /// <summary>
    /// 无 Runtime 的窗口恢复自检（web-ready 隔离 smoke 调用）：只创建并操作
    /// 自检自身的 WinUI 窗口（初次显示会激活该自建窗口），依次验证普通
    /// 可见、最小化、原本隐藏三态经 <see cref="RestoreOwnerState"/> 的真实
    /// IsWindowVisible/IsIconic 恢复。不采样用户桌面；恢复方法前台传 0，
    /// 不恢复/检查任何外部窗口前台（前台物理一致性 UNVERIFIED）。
    /// </summary>
    internal static async Task VerifyOwnerRestoreSelfCheckAsync(CancellationToken cancellationToken)
    {
        var window = new Window { Content = new Grid() };
        try
        {
            window.AppWindow.Resize(new SizeInt32(240, 160));
            window.Activate();
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            await WaitForSelfCheckStateAsync(
                () => IsWindowVisible(handle) && !IsIconic(handle), "自检窗口初始可见", cancellationToken);

            // 原本可见+普通：隐藏后按 SW_SHOWNA 恢复为可见非最小化。
            ShowWindow(handle, 0);
            await WaitForSelfCheckStateAsync(() => !IsWindowVisible(handle), "自检窗口隐藏", cancellationToken);
            RestoreOwnerState(handle, visible: true, minimized: false, foreground: 0);
            await WaitForSelfCheckStateAsync(
                () => IsWindowVisible(handle) && !IsIconic(handle), "普通可见状态恢复", cancellationToken);

            // 原本可见+最小化：隐藏后按 SW_SHOWMINNOACTIVE 恢复为可见且最小化。
            ShowWindow(handle, 6);
            await WaitForSelfCheckStateAsync(() => IsIconic(handle), "自检窗口最小化", cancellationToken);
            ShowWindow(handle, 0);
            await WaitForSelfCheckStateAsync(() => !IsWindowVisible(handle), "自检窗口再次隐藏", cancellationToken);
            RestoreOwnerState(handle, visible: true, minimized: true, foreground: 0);
            await WaitForSelfCheckStateAsync(
                () => IsWindowVisible(handle) && IsIconic(handle), "最小化状态恢复", cancellationToken);

            // 原本隐藏（托盘）：恢复调用后保持隐藏。准备阶段用 SW_RESTORE
            // 明确解除最小化回到普通可见（SW_SHOWNA 不保证解除最小化），
            // 以便“保持隐藏”结果可区分于未变化；仅自检准备，生产恢复语义
            // 仍是最小化 SW_SHOWMINNOACTIVE / 普通 SW_SHOWNA。
            ShowWindow(handle, 9);
            await WaitForSelfCheckStateAsync(
                () => IsWindowVisible(handle) && !IsIconic(handle), "自检窗口回到普通可见", cancellationToken);
            ShowWindow(handle, 0);
            await WaitForSelfCheckStateAsync(() => !IsWindowVisible(handle), "自检窗口第三次隐藏", cancellationToken);
            RestoreOwnerState(handle, visible: false, minimized: false, foreground: 0);
            await WaitForSelfCheckStateAsync(() => !IsWindowVisible(handle), "托盘隐藏保持隐藏", cancellationToken);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitForSelfCheckStateAsync(
        Func<bool> state, string description, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (state()) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException($"窗口恢复自检未达到预期状态：{description}。");
    }

  private sealed record OverlaySelection(
    PhysicalRectangle Bounds,
    ScreenshotCaptureScene? Scene,
    ScreenshotSelectionAction? SelectionAction = null,
    string? RecognitionModeId = null);

  // 自检仅复用已经捕获的 synthetic 子窗口像素，不采样其他桌面区域。
  internal static async Task<ScreenshotCaptureScene> CreateSyntheticSceneAsync(
    PhysicalRectangle bounds, byte[] pixels, int stride, CancellationToken cancellationToken)
  {
    BitmapImage background = await LoadBitmapAsync(
      EncodeTopDownBmp(pixels, bounds.Width, bounds.Height, stride), bounds.Width, bounds.Height);
    OverlaySelection? selected = await ShowOverlayAsync(bounds, background, pixels,
      SmartScreenCandidates.Capture(bounds), keepForEditing: true, cancellationToken,
      automaticSelection: true);
    ScreenshotCaptureScene scene = selected?.Scene ??
      throw new InvalidOperationException("Synthetic screenshot overlay was cancelled.");
    scene.RestoreOwnerOnClose(static () => { });
    return scene;
  }

  private static async Task<OverlaySelection?> ShowOverlayAsync(
    PhysicalRectangle desktop,
    BitmapImage background,
    byte[] pixels,
    SmartScreenCandidates candidates,
    bool keepForEditing,
    CancellationToken cancellationToken,
    bool automaticSelection = false,
    ScreenshotSelectionActions? selectionActions = null)
  {
    // 普通入口：稳定选区后显示动作栏；专用直接入口保持既有立即确认。
    bool ordinary = selectionActions is not null;
    IReadOnlyList<ScreenshotRecognitionModeEntry> modeEntries =
      selectionActions?.Modes ?? [];
    var completion = new TaskCompletionSource<OverlaySelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
    Windows.Foundation.TypedEventHandler<object, WindowEventArgs> closeHandler =
      (_, _) => completion.TrySetResult(null);
    var session = new ScreenSelectionSession(desktop.Width, desktop.Height);
    var overlay = new Window();
    var root = new Grid { RequestedTheme = ElementTheme.Dark };
    root.Children.Add(new Image { Source = background, Stretch = Stretch.Fill });
    var canvas = new SelectionCanvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    root.Children.Add(canvas);
    // Four shades leave the selected pixels unobscured.
    Rectangle[] shades = Enumerable.Range(0, 4).Select(_ => new Rectangle
    {
      Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)),
      IsHitTestVisible = false,
    }).ToArray();
    foreach (Rectangle shade in shades) canvas.Children.Add(shade);
    var selection = new Rectangle
    {
      Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
      StrokeThickness = 1,
      IsHitTestVisible = false,
      Visibility = Visibility.Collapsed,
    };
    canvas.Children.Add(selection);
    Rectangle[] handles = Enumerable.Range(0, 8).Select(_ => new Rectangle
    {
      Width = 7,
      Height = 7,
      Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
      IsHitTestVisible = false,
      Visibility = Visibility.Collapsed,
    }).ToArray();
    foreach (Rectangle handle in handles) canvas.Children.Add(handle);
    var sizeLabel = new TextBlock();
    var help = new TextBlock
    {
      Text = ordinary
        ? "悬停智能取框，Tab 切换窗口/控件/父级；拖动手动框选\n松开后在动作栏选择 编辑/识别/复制/保存/钉图 · Enter 编辑 · Esc 退出 · 右键返回上一步\n方向键微调 · Shift ×10 · Ctrl+方向键缩放 · Ctrl+Z 撤销 · Ctrl+Y / Ctrl+Shift+Z 重做 · M 放大镜 · Ctrl+C 复制色号"
        : "悬停智能取框，Tab 切换窗口/控件/父级；拖动手动框选\n单击选区 / Enter 确认 · 右键 / Esc 返回或退出 · 方向键微调 · Shift ×10 · Ctrl+方向键缩放\nCtrl+Z 撤销 · Ctrl+Y / Ctrl+Shift+Z 重做 · M 放大镜 · Ctrl+C 复制色号",
      IsHitTestVisible = false,
    };
    var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
    var panel = new StackPanel { Spacing = 8 };
    panel.Children.Add(help);
    panel.Children.Add(sizeLabel);
    panel.Children.Add(toolbar);
    var panelBorder = new Border
    {
      Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 24, 24, 24)),
      Padding = new Thickness(12),
      CornerRadius = new CornerRadius(8),
      Child = panel,
    };
    canvas.Children.Add(panelBorder);
    Button AddButton(string text, Action action)
    {
      var button = new Button { Content = text };
      button.Click += (_, _) => action();
      toolbar.Children.Add(button);
      return button;
    }
    uint? activePointerId = null;
    Button confirm = null!;
    Button undo = null!;
    Button redo = null!;
    SmartScreenCandidates.Window? hoveredWindow = null;
    PhysicalPoint hoveredPoint = default;
    long hoverGeneration = 0;
    bool diagnoseSmartQueries = Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "native-actions-e2e";
    bool queryWaiting = false;
    bool uiaTimedOut = false;
    bool confirming = false;
    IReadOnlyList<SmartControlCandidate> previewControls = [];
    List<SmartControlCandidate> VisibleControls(
      SmartScreenCandidates.Window window, IReadOnlyList<SmartControlCandidate> controls)
    {
      var visible = new List<SmartControlCandidate>();
      foreach (SmartControlCandidate control in controls)
      {
        if (candidates.ClipToCapture(control.Bounds, window) is { } clipped)
          visible.Add(control with { Bounds = clipped });
      }
      return visible;
    }
    void QueueControlQuery()
    {
      if (queryWaiting || uiaTimedOut || confirming || completion.Task.IsCompleted ||
          hoveredWindow is null || session.ManualOnly ||
          session.Selection is not null || session.IsPointerActive)
      {
        if (diagnoseSmartQueries)
          AppLog.Info($"Smart query skipped: window={hoveredWindow?.Handle} point={hoveredPoint} generation={hoverGeneration} waiting={queryWaiting} timedOut={uiaTimedOut} confirming={confirming} completed={completion.Task.IsCompleted} manual={session.ManualOnly} selection={session.Selection} pointerActive={session.IsPointerActive}");
        return;
      }
      _ = QueryControlsAsync();
    }
    async Task QueryControlsAsync()
    {
      SmartScreenCandidates.Window window = hoveredWindow!;
      PhysicalPoint point = hoveredPoint;
      long generation = hoverGeneration;
      queryWaiting = true;
      if (diagnoseSmartQueries)
        AppLog.Info($"Smart query started: window={window.Handle} bounds={window.Bounds} point={point} generation={generation}");
      System.Diagnostics.Stopwatch? queryTimer = diagnoseSmartQueries ? System.Diagnostics.Stopwatch.StartNew() : null;
      IReadOnlyList<SmartControlCandidate> controls;
      bool timedOut = false;
      try
      {
        controls = await SmartControlQuery.Shared
          .QueryAsync(window.Handle, point, window.Bounds)
          .WaitAsync(TimeSpan.FromMilliseconds(200));
      }
      catch (TimeoutException)
      {
        timedOut = true;
        controls = [];
      }
      queryTimer?.Stop();
      if (diagnoseSmartQueries)
        AppLog.Info($"Smart query completed: window={window.Handle} point={point} generation={generation} elapsedMs={queryTimer!.Elapsed.TotalMilliseconds:F3} count={controls.Count} timedOut={timedOut}");
      root.DispatcherQueue.TryEnqueue(() =>
      {
        queryWaiting = false;
        bool accepted = false;
        if (timedOut) uiaTimedOut = true;
        if (!timedOut && generation == hoverGeneration && !confirming &&
            !completion.Task.IsCompleted && !session.ManualOnly &&
            session.Selection is null && !session.IsPointerActive &&
            candidates.IsCurrent(window, SmartScreenCandidates.CurrentDesktop(),
              SmartScreenCandidates.TryReadWindowBounds) &&
            candidates.ClipToCapture(window.Bounds, window) is { } clippedWindow)
        {
          var regions = new List<PhysicalRectangle> { candidates.ToLocal(clippedWindow) };
          previewControls = VisibleControls(window, controls);
          foreach (SmartControlCandidate control in previewControls)
            regions.Add(candidates.ToLocal(control.Bounds));
          session.SetPreview(regions);
          accepted = true;
          Render();
        }
        if (diagnoseSmartQueries)
        {
          string reason = accepted ? "accepted" : timedOut ? "timeout" :
            generation != hoverGeneration ? "generation-changed" : confirming ? "confirming" :
            completion.Task.IsCompleted ? "completed" : session.ManualOnly ? "manual-only" :
            session.Selection is not null ? "manual-selection" : session.IsPointerActive ? "pointer-active" :
            "window-stale-or-unclippable";
          AppLog.Info($"Smart query callback: window={window.Handle} point={point} generation={generation} currentGeneration={hoverGeneration} elapsedMs={queryTimer!.Elapsed.TotalMilliseconds:F3} count={controls.Count} visibleCount={previewControls.Count} timedOut={timedOut} accepted={accepted} reason={reason}");
        }
        if (generation != hoverGeneration) QueueControlQuery();
      });
    }
    void UpdateSmartPreview(PhysicalPoint local)
    {
      if (session.ManualOnly || session.Selection is not null || session.IsPointerActive) return;
      PhysicalPoint point = new(checked(local.X + desktop.X), checked(local.Y + desktop.Y));
      SmartScreenCandidates.Window? window = candidates.Hit(point);
      PhysicalRectangle? clipped = window is not null &&
        candidates.IsCurrent(window, SmartScreenCandidates.CurrentDesktop(),
          SmartScreenCandidates.TryReadWindowBounds)
        ? candidates.ClipToCapture(window.Bounds, window) : null;
      // Repeated PointerMoved events must not discard the current Tab-selected control.
      if (clipped is not null && window == hoveredWindow && point == hoveredPoint) return;
      hoverGeneration++;
      previewControls = [];
      hoveredPoint = point;
      if (window is null || clipped is not { } bounds)
      {
        hoveredWindow = null;
        session.ClearPreview();
      }
      else
      {
        hoveredWindow = window;
        session.SetPreview([candidates.ToLocal(bounds)]);
        QueueControlQuery();
      }
    }
    void Finish(
        bool accept,
        ScreenshotSelectionAction action = ScreenshotSelectionAction.Edit,
        string? recognitionModeId = null) =>
      _ = FinishAsync(accept, action, recognitionModeId);
    void CompleteSelection(
        bool accept,
        ScreenshotSelectionAction action = ScreenshotSelectionAction.Edit,
        string? recognitionModeId = null)
    {
      if (completion.Task.IsCompleted) return;
      PhysicalRectangle? result = accept && session.ActiveSelection is { } rect
          ? rect with { X = rect.X + desktop.X, Y = rect.Y + desktop.Y } : null;
      if (result is { } bounds && action is ScreenshotSelectionAction.Edit && keepForEditing)
      {
        overlay.Closed -= closeHandler;
        canvas.ReleasePointerCaptures();
        // 将纯背景移交给新 root，丢弃选区事件闭包及完整 BGRA 缓冲。
        var sceneRoot = new Grid { RequestedTheme = ElementTheme.Dark };
        UIElement frozenImage = root.Children[0];
        root.Children.Remove(frozenImage);
        sceneRoot.Children.Add(frozenImage);
        var sceneShade = new Canvas { IsHitTestVisible = false };
        foreach (Rectangle shade in shades.Append(selection))
        {
          canvas.Children.Remove(shade);
          sceneShade.Children.Add(shade);
        }
        sceneRoot.Children.Add(sceneShade);
        overlay.Content = sceneRoot;
        completion.TrySetResult(new OverlaySelection(bounds,
          new ScreenshotCaptureScene(overlay, sceneRoot, desktop, bounds)));
      }
      else if (result is { } accepted)
      {
        // 非编辑动作不需要冻结现场：直接关闭选区窗口，交回截图前的桌面。
        completion.TrySetResult(new OverlaySelection(accepted, null,
          action is ScreenshotSelectionAction.Edit ? null : action, recognitionModeId));
        overlay.Close();
      }
      else
      {
        completion.TrySetResult(null);
        overlay.Close();
      }
    }
    async Task FinishAsync(
      bool accept,
      ScreenshotSelectionAction action = ScreenshotSelectionAction.Edit,
      string? recognitionModeId = null,
      bool fixSelectionOnly = false)
    {
      if (completion.Task.IsCompleted || (accept && (confirming || !session.CanConfirm))) return;
      // 普通入口的单击确认只固化候选进入动作阶段；直接入口完成选区。
      void Confirmed()
      {
        if (fixSelectionOnly)
        {
          session.ConfirmPreview();
          Render();
        }
        else
        {
          CompleteSelection(true, action, recognitionModeId);
        }
      }
      if (accept && SmartScreenCandidates.CurrentDesktop() != desktop)
      {
        Finish(false);
        return;
      }
      if (accept && session.Selection is null && hoveredWindow is { } window &&
          !candidates.IsCurrent(window, desktop, SmartScreenCandidates.TryReadWindowBounds))
      {
        session.ClearPreview();
        Render();
        return;
      }
      if (accept && session.Selection is null && session.PreviewIndex > 0 &&
          hoveredWindow is { } controlWindow)
      {
        confirming = true;
        Render();
        long generation = hoverGeneration;
        int selectedIndex = session.PreviewIndex - 1;
        PhysicalRectangle? expected = session.ActiveSelection;
        IReadOnlyList<SmartControlCandidate> before = previewControls;
        IReadOnlyList<SmartControlCandidate> refreshed;
        bool timedOut = false;
        try
        {
          refreshed = await SmartControlQuery.Shared
            .QueryAsync(controlWindow.Handle, hoveredPoint, controlWindow.Bounds)
            .WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
          timedOut = true;
          refreshed = [];
        }
        root.DispatcherQueue.TryEnqueue(() =>
        {
          confirming = false;
          if (timedOut) uiaTimedOut = true;
          if (completion.Task.IsCompleted || generation != hoverGeneration ||
              session.Selection is not null || session.ManualOnly ||
              session.ActiveSelection != expected)
            return;
          IReadOnlyList<SmartControlCandidate> after = VisibleControls(controlWindow, refreshed);
          if (!candidates.IsCurrent(controlWindow, SmartScreenCandidates.CurrentDesktop(),
                SmartScreenCandidates.TryReadWindowBounds) ||
              !SmartControlCandidate.SamePathThrough(before, after, selectedIndex))
          {
            hoverGeneration++;
            hoveredWindow = null;
            session.ReturnToManual();
            Render();
            return;
          }
          Confirmed();
        });
        return;
      }
      if (fixSelectionOnly)
      {
        Confirmed();
        return;
      }
      CompleteSelection(accept, action, recognitionModeId);
    }
    void Back()
    {
      if (session.Back()) Finish(false);
      hoverGeneration++;
      hoveredWindow = null;
      previewControls = [];
      activePointerId = null;
      canvas.ReleasePointerCaptures();
    }
    var magnifierCanvas = new Canvas { Width = 99, Height = 99 };
    SolidColorBrush[] pixelBrushes = Enumerable.Range(0, 121).Select(_ => new SolidColorBrush()).ToArray();
    for (int i = 0; i < pixelBrushes.Length; i++)
    {
      var pixel = new Rectangle { Width = 9, Height = 9, Fill = pixelBrushes[i] };
      Canvas.SetLeft(pixel, i % 11 * 9);
      Canvas.SetTop(pixel, i / 11 * 9);
      magnifierCanvas.Children.Add(pixel);
    }
    var crosshair = new Rectangle
    {
      Width = 11,
      Height = 11,
      Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red),
      StrokeThickness = 2,
    };
    Canvas.SetLeft(crosshair, 44);
    Canvas.SetTop(crosshair, 44);
    magnifierCanvas.Children.Add(crosshair);
    var colorLabel = new TextBlock();
    var magnifierPanel = new StackPanel { Spacing = 4 };
    magnifierPanel.Children.Add(magnifierCanvas);
    magnifierPanel.Children.Add(colorLabel);
    var magnifier = new Border
    {
      Child = magnifierPanel,
      Padding = new Thickness(8),
      IsHitTestVisible = false,
      Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 24, 24, 24)),
      Visibility = Visibility.Collapsed,
    };
    canvas.Children.Add(magnifier);
    bool showMagnifier = true;
    string? colorHex = null;
    Windows.Foundation.Point? lastPoint = null;
    // 普通入口动作栏：编辑是可见主动作（Enter），识别菜单只消费宿主目录
    // 投影；复制/保存/钉图复用既有本地输出。直接入口保持旧工具条。
    List<Button> selectionActionButtons = [];
    MenuFlyout? recognizeMenu = null;
    bool menuOpen = false;
    if (ordinary)
    {
      Button edit = AddButton("编辑 (Enter)", () => Finish(true));
      confirm = edit;
      selectionActionButtons.Add(edit);
      recognizeMenu = new MenuFlyout();
      recognizeMenu.Opened += (_, _) => menuOpen = true;
      recognizeMenu.Closed += (_, _) => menuOpen = false;
      if (modeEntries.Count == 0)
      {
        recognizeMenu.Items.Add(new MenuFlyoutItem
        {
          Text = "识别目录尚未加载，请先打开设置检查运行环境",
          IsEnabled = false,
        });
      }
      else
      {
        foreach (ScreenshotRecognitionModeEntry entry in modeEntries)
        {
          bool ready = string.Equals(entry.Availability, "ready", StringComparison.Ordinal);
          var item = new MenuFlyoutItem
          {
            Text = ready
              ? entry.DisplayName
              : $"{entry.DisplayName} — {UnavailableReason(entry.Availability)}",
            IsEnabled = ready,
          };
          string modeId = entry.ModeId;
          item.Click += (_, _) => Finish(true, ScreenshotSelectionAction.Recognize, modeId);
          recognizeMenu.Items.Add(item);
        }
      }
      recognizeMenu.Items.Add(new MenuFlyoutSeparator());
      var openSettings = new MenuFlyoutItem { Text = "准备识别模式…（打开设置）" };
      openSettings.Click += (_, _) => Finish(true, ScreenshotSelectionAction.OpenSettings);
      recognizeMenu.Items.Add(openSettings);
      var recognize = new Button { Content = "识别", Flyout = recognizeMenu };
      toolbar.Children.Add(recognize);
      selectionActionButtons.Add(recognize);
      selectionActionButtons.Add(AddButton("复制", () => Finish(true, ScreenshotSelectionAction.Copy)));
      selectionActionButtons.Add(AddButton("保存", () => Finish(true, ScreenshotSelectionAction.Save)));
      selectionActionButtons.Add(AddButton("钉图", () => Finish(true, ScreenshotSelectionAction.Pin)));
    }
    else
    {
      confirm = AddButton("确认 (Enter)", () => Finish(true));
      undo = AddButton("撤销", session.Undo);
      redo = AddButton("重做", session.Redo);
    }
    AddButton("重选", () => { if (session.ActiveSelection is not null || session.IsPointerActive) Back(); });
    AddButton("退出 (Esc)", () => Finish(false));
    foreach (Button button in toolbar.Children.OfType<Button>()) button.Click += (_, _) => Render();

    PhysicalPoint ToPhysical(Windows.Foundation.Point point) => new(
        (int)Math.Round(point.X * desktop.Width / Math.Max(1, canvas.ActualWidth)),
        (int)Math.Round(point.Y * desktop.Height / Math.Max(1, canvas.ActualHeight)));
    void UpdateMagnifier(Windows.Foundation.Point point)
    {
      lastPoint = point;
      double pixelsPerDipX = desktop.Width / Math.Max(1, canvas.ActualWidth);
      double pixelsPerDipY = desktop.Height / Math.Max(1, canvas.ActualHeight);
      int handle = session.IsPointerActive ? session.ActiveHandle :
        session.HitHandle(new(point.X, point.Y), pixelsPerDipX, pixelsPerDipY);
      canvas.SetCursor(handle switch
      {
        0 or 7 => InputSystemCursorShape.SizeNorthwestSoutheast,
        2 or 5 => InputSystemCursorShape.SizeNortheastSouthwest,
        1 or 6 => InputSystemCursorShape.SizeNorthSouth,
        3 or 4 => InputSystemCursorShape.SizeWestEast,
        _ => InputSystemCursorShape.Cross,
      });
      PhysicalPoint location = session.SamplePoint(ToPhysical(point), handle);
      int x = Math.Clamp(location.X, 0, desktop.Width - 1);
      int y = Math.Clamp(location.Y, 0, desktop.Height - 1);
      for (int i = 0; i < pixelBrushes.Length; i++)
      {
        int px = Math.Clamp(x + i % 11 - 5, 0, desktop.Width - 1);
        int py = Math.Clamp(y + i / 11 - 5, 0, desktop.Height - 1);
        int offset = (py * desktop.Width + px) * 4;
        pixelBrushes[i].Color = Windows.UI.Color.FromArgb(255, pixels[offset + 2], pixels[offset + 1], pixels[offset]);
      }
      Windows.UI.Color color = pixelBrushes[60].Color;
      colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
      colorLabel.Text = $"{desktop.X + x}, {desktop.Y + y}\n{colorHex}\nRGB {color.R}, {color.G}, {color.B}";
      magnifier.Visibility = showMagnifier ? Visibility.Visible : Visibility.Collapsed;
      Canvas.SetLeft(magnifier, Math.Max(0, point.X + 24 + 180 > canvas.ActualWidth ? point.X - 180 : point.X + 24));
      Canvas.SetTop(magnifier, Math.Max(0, point.Y + 24 + 190 > canvas.ActualHeight ? point.Y - 190 : point.Y + 24));
    }
    void Place(Rectangle rectangle, double x, double y, double w, double h)
    {
      Canvas.SetLeft(rectangle, x); Canvas.SetTop(rectangle, y);
      rectangle.Width = Math.Max(0, w); rectangle.Height = Math.Max(0, h);
    }
    void Render()
    {
      double w = canvas.ActualWidth, h = canvas.ActualHeight;
      double sx = w / desktop.Width, sy = h / desktop.Height;
      PhysicalRectangle? rect = session.ActiveSelection;
      double left = rect?.X * sx ?? 0, top = rect?.Y * sy ?? 0;
      double right = rect?.Right * sx ?? 0, bottom = rect?.Bottom * sy ?? 0;
      Place(shades[0], 0, 0, w, top);
      Place(shades[1], 0, top, left, bottom - top);
      Place(shades[2], right, top, w - right, bottom - top);
      Place(shades[3], 0, bottom, w, h - bottom);
      selection.Visibility = rect is null ? Visibility.Collapsed : Visibility.Visible;
      Place(selection, left, top, right - left, bottom - top);
      (double X, double Y)[] points = rect is { } bounds
        ? ScreenSelectionSession.HandlePoints(bounds) : new (double, double)[8];
      for (int i = 0; i < handles.Length; i++)
      {
        handles[i].Visibility = rect is null ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(handles[i], points[i].X * sx - 3.5);
        Canvas.SetTop(handles[i], points[i].Y * sy - 3.5);
      }
      string source = session.Selection is not null ? "手动" :
        session.ActiveSelection is null ? "" :
        session.PreviewIndex == 0 ? "窗口候选" : $"控件候选 · 层级 {session.PreviewIndex}";
      sizeLabel.Text = rect is { } r
        ? $"{source} · {r.Width} × {r.Height} px · " +
          (ordinary ? "Enter 编辑 / 动作栏选择动作" : "Enter 确认 / 单击")
        : "请选择区域";
      if (ordinary)
      {
        // 动作栏仅在选区已固化（拖拽松开或单击确认智能候选）后可操作；
        // 悬停候选不触发动作，避免鼠标移动换图导致选错。
        bool actionable = session.Selection is not null && !confirming;
        foreach (Button button in selectionActionButtons) button.IsEnabled = actionable;
      }
      else
      {
        confirm.IsEnabled = session.CanConfirm && !confirming;
        undo.IsEnabled = session.CanUndo;
        redo.IsEnabled = session.CanRedo;
      }
      panelBorder.Measure(new Windows.Foundation.Size(w, h));
      double pw = panelBorder.DesiredSize.Width, ph = panelBorder.DesiredSize.Height;
      Canvas.SetLeft(panelBorder, Math.Clamp(left, 0, Math.Max(0, w - pw)));
      Canvas.SetTop(panelBorder, rect is null ? Math.Max(0, h - ph - 20) :
          bottom + ph + 12 <= h ? bottom + 12 : Math.Max(0, top - ph - 12));
      panelBorder.Visibility = session.IsDragging ? Visibility.Collapsed : Visibility.Visible;
      if (lastPoint is { } point) UpdateMagnifier(point);
    }
    void UpdateGesture(Windows.Foundation.Point point)
    {
      bool wasDragging = session.IsDragging;
      session.Move(ToPhysical(point), new(point.X, point.Y));
      if (!wasDragging && session.IsDragging)
      {
        hoverGeneration++;
        hoveredWindow = null;
        previewControls = [];
      }
    }
    // WinUI can route stale pointer events from another window to the overlay.
    bool IsPickerPointer(PointerRoutedEventArgs args) =>
      args.OriginalSource is UIElement source && source.XamlRoot == root.XamlRoot;
    void CancelPointer(PointerRoutedEventArgs args)
    {
      if (!IsPickerPointer(args)) return;
      if (activePointerId != args.Pointer.PointerId) return;
      activePointerId = null;
      session.CancelDrag();
      Render();
    }
    canvas.PointerPressed += (_, args) =>
    {
      if (!IsPickerPointer(args)) return;
      if (!args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed || args.Handled ||
          confirming || completion.Task.IsCompleted || activePointerId is not null) return;
      // Child buttons and the help panel must not start or confirm a selection.
      if (args.OriginalSource is DependencyObject source)
      {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
          if (node == panelBorder) return;
      }
      Windows.Foundation.Point point = args.GetCurrentPoint(canvas).Position;
      lastPoint = point;
      session.Begin(ToPhysical(point), new(point.X, point.Y),
        desktop.Width / Math.Max(1, canvas.ActualWidth),
        desktop.Height / Math.Max(1, canvas.ActualHeight));
      activePointerId = args.Pointer.PointerId;
      if (!canvas.CapturePointer(args.Pointer))
      {
        activePointerId = null;
        session.CancelDrag();
      }
      Render();
      args.Handled = true;
    };
    root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
    {
      if (completion.Task.IsCompleted || !IsPickerPointer(args)) return;
      if (!args.GetCurrentPoint(canvas).Properties.IsRightButtonPressed) return;
      Back(); Render(); args.Handled = true;
    }), true);
    canvas.PointerMoved += (_, args) =>
    {
      if (!IsPickerPointer(args)) return;
      if (activePointerId is { } id && id != args.Pointer.PointerId) return;
      Windows.Foundation.Point point = args.GetCurrentPoint(canvas).Position;
      lastPoint = point;
      UpdateGesture(point);
      if (!confirming) UpdateSmartPreview(ToPhysical(point));
      Render();
    };
    canvas.PointerReleased += (_, args) =>
    {
      if (!IsPickerPointer(args)) return;
      if (activePointerId != args.Pointer.PointerId ||
          args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
      Windows.Foundation.Point point = args.GetCurrentPoint(canvas).Position;
      lastPoint = point;
      UpdateGesture(point);
      bool confirmClick = session.End(ToPhysical(point), new(point.X, point.Y));
      activePointerId = null;
      canvas.ReleasePointerCapture(args.Pointer);
      Render();
      args.Handled = true;
      // 普通入口：单击确认智能候选只固化选区/进入动作阶段（不完成、不
      // OCR）；手动选区内单击维持动作阶段。直接入口保留单击完成。
      if (confirmClick && ordinary) _ = FinishAsync(true, fixSelectionOnly: true);
      else if (confirmClick) Finish(true);
    };
    canvas.PointerCaptureLost += (_, args) => CancelPointer(args);
    canvas.PointerCanceled += (_, args) => CancelPointer(args);
    var keyboardSink = new Button
    {
      Width = 1,
      Height = 1,
      Opacity = 0,
      IsTabStop = true,
      HorizontalAlignment = HorizontalAlignment.Left,
      VerticalAlignment = VerticalAlignment.Top
    };
    root.Children.Add(keyboardSink);
    root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
          if (completion.Task.IsCompleted) return;
          bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
          bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
          int step = shift ? 10 : 1;
          switch (args.Key)
          {
            case VirtualKey.Escape:
              // 普通动作阶段：菜单打开时先关菜单；再次 Esc 取消截图并恢复
              // 原窗口状态。右键保留逐级返回；直接入口 Esc 仍逐级返回。
              if (menuOpen) recognizeMenu?.Hide();
              else if (ordinary) Finish(false);
              else Back();
              break;
            case VirtualKey.Enter: Finish(true); break;
            case VirtualKey.Tab: session.CyclePreview(); break;
            case VirtualKey.Z when control && shift: session.Redo(); break;
            case VirtualKey.Z when control: session.Undo(); break;
            case VirtualKey.Y when control: session.Redo(); break;
            case VirtualKey.Left: session.Adjust(-step, 0, control); break;
            case VirtualKey.Right: session.Adjust(step, 0, control); break;
            case VirtualKey.Up: session.Adjust(0, -step, control); break;
            case VirtualKey.Down: session.Adjust(0, step, control); break;
            case VirtualKey.M when !control:
              showMagnifier = !showMagnifier;
              if (lastPoint is { } point) UpdateMagnifier(point);
              break;
            case VirtualKey.C when control && colorHex is not null:
              try
              {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(colorHex);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                colorLabel.Text += "\n已复制";
              }
              catch (COMException) { colorLabel.Text += "\n剪贴板暂不可用"; }
              break;
            default: return;
          }
          Render(); args.Handled = true;
        }), true);
    canvas.SizeChanged += (_, _) => Render();
    overlay.Content = root;
    overlay.Closed += closeHandler;
    OverlappedPresenter presenter = OverlappedPresenter.Create();
    presenter.SetBorderAndTitleBar(false, false);
    presenter.IsAlwaysOnTop = true;
    presenter.IsResizable = false;
    overlay.AppWindow.SetPresenter(presenter);
    overlay.AppWindow.IsShownInSwitchers = false;
    overlay.AppWindow.MoveAndResize(new RectInt32(desktop.X, desktop.Y, desktop.Width, desktop.Height));
    overlay.Activate();
    nint overlayHandle = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
    try
    {
      if (overlayHandle == nint.Zero)
        throw new InvalidOperationException("无法获取截图选区窗口。");
      // A borderless overlapped window can still have non-client insets. Match
      // the frozen desktop to the client area, where XAML pointer positions live.
      overlay.AppWindow.ResizeClient(new SizeInt32(desktop.Width, desktop.Height));
      NativePoint origin = new();
      if (!ClientToScreen(overlayHandle, ref origin))
        throw new InvalidOperationException("无法读取截图选区客户区原点。");
      PointInt32 position = overlay.AppWindow.Position;
      overlay.AppWindow.Move(new PointInt32(
        checked(position.X + desktop.X - origin.X),
        checked(position.Y + desktop.Y - origin.Y)));
      origin = new();
      if (!ClientToScreen(overlayHandle, ref origin) ||
          !GetClientRect(overlayHandle, out NativeRect client))
        throw new InvalidOperationException("无法校验截图选区客户区。");
      if (origin.X != desktop.X || origin.Y != desktop.Y ||
          client.Right - client.Left != desktop.Width ||
          client.Bottom - client.Top != desktop.Height)
        throw new InvalidOperationException("截图选区客户区与虚拟桌面不一致。");
    }
    catch
    {
      overlay.Close();
      throw;
    }
    // A hotkey can open this window while another application remains foreground.
    // WinUI activation alone does not transfer keyboard focus across processes.
    // Synthetic smoke confirms its selection without input; hidden test launches
    // cannot acquire foreground permission and do not need keyboard ownership.
    if (!automaticSelection &&
        !SetForegroundWindow(overlayHandle) && GetForegroundWindow() != overlayHandle)
    {
      overlay.Close();
      throw new InvalidOperationException("无法将截图选区置于前台，请重试截图。");
    }
    if (!automaticSelection) keyboardSink.Focus(FocusState.Programmatic);
    using CancellationTokenRegistration registration = cancellationToken.Register(() =>
    root.DispatcherQueue.TryEnqueue(() => { completion.TrySetCanceled(cancellationToken); overlay.Close(); }));
    if (automaticSelection)
    {
      session.Begin(new(0, 0));
      session.End(new(desktop.Width, desktop.Height));
      Render();
      // 自检只捕获自己的子窗口，不是整个虚拟桌面；几何已在上方校验。
      // 直接复用确认后的 HWND 移交，不套用真实桌面的变化/候选检查。
      CompleteSelection(true);
    }
    try
    {
      return await completion.Task;
    }
    finally
    {
      // Release the frozen desktop before a possible long scrolling session.
      root.Children.Clear();
    }
  }
    // 只映射目录 availability 的稳定词汇，不复制引擎/配方表；准备类条目
    // 禁用并解释，不自动下载。
    private static string UnavailableReason(string availability) => availability switch
    {
        "preparation_required" => "需要先准备依赖（可在设置中准备）",
        "unavailable" => "当前运行环境不可用",
        _ => "暂不可用",
    };

    public static PhysicalRectangle ScaleSelection(
        PhysicalRectangle desktop,
        double left,
        double top,
        double width,
        double height,
        double canvasWidth,
        double canvasHeight)
    {
        int x = desktop.X + (int)Math.Round(left * desktop.Width / canvasWidth);
        int y = desktop.Y + (int)Math.Round(top * desktop.Height / canvasHeight);
        int right = desktop.X + (int)Math.Round((left + width) * desktop.Width / canvasWidth);
        int bottom = desktop.Y + (int)Math.Round((top + height) * desktop.Height / canvasHeight);
        return new PhysicalRectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static byte[] CropBgra(
        byte[] source,
        PhysicalRectangle desktop,
        PhysicalRectangle selected)
    {
        int sourceStride = checked(desktop.Width * 4);
        int targetStride = checked(selected.Width * 4);
        byte[] cropped = new byte[checked(targetStride * selected.Height)];
        int offsetX = selected.X - desktop.X;
        int offsetY = selected.Y - desktop.Y;
        for (int row = 0; row < selected.Height; row++)
        {
            System.Buffer.BlockCopy(
                source,
                checked((offsetY + row) * sourceStride + offsetX * 4),
                cropped,
                row * targetStride,
                targetStride);
        }

        return cropped;
    }

    private static async Task<BitmapImage> LoadBitmapAsync(byte[] data, int physicalWidth, int physicalHeight)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(data);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage
        {
            // Pin the decode size to the captured physical pixels and interpret it
            // in physical units. Without this, WinUI 3 defaults to
            // DecodePixelType=Logical and auto-scales the decoded bitmap to the
            // effective/logical layout size — so on a >100% DPI display the frozen
            // desktop backdrop is downsampled then stretched back by Stretch.Fill,
            // producing the blurry overlay the screenshot workflow used to show.
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = physicalWidth,
            DecodePixelHeight = physicalHeight,
        };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static byte[] EncodeTopDownBmp(byte[] bgra, int width, int height, int stride)
    {
        int pixelBytes = checked(stride * height);
        byte[] bmp = new byte[checked(54 + pixelBytes)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(-height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)32).CopyTo(bmp, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(bmp, 34);
        bgra.CopyTo(bmp, 54);
        return bmp;
    }

    private static PhysicalRectangle GetVirtualDesktop()
    {
        var desktop = new PhysicalRectangle(
            GetSystemMetrics(VirtualScreenX),
            GetSystemMetrics(VirtualScreenY),
            GetSystemMetrics(VirtualScreenWidth),
            GetSystemMetrics(VirtualScreenHeight));
        desktop.Validate();
        if (checked((long)desktop.Width * desktop.Height * 4) > MaximumCaptureBytes)
        {
            throw new InvalidDataException("虚拟桌面截图超过 256 MiB 限制。");
        }
        return desktop;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

}
