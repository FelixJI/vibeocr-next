using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VibeOCR.App.Services;
using VibeOCR.Platform.Windows;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Captures stable frames from one fixed window region until completion or cancellation.</summary>
internal static class ScrollCaptureSession
{
  /// <summary>
  /// 在 UI 线程调用；owner 已隐藏，任务栏/悬浮栏管理与 owner 恢复由调用方负责。
  /// 正常完成返回拼接后的选区及实际尺寸；用户取消或关闭控制窗返回 null；
  /// 外部 token 取消抛 OperationCanceledException；失败在用户关闭控制窗后抛出
  /// InvalidOperationException/InvalidDataException，不返回不完整长图。
  /// 返回前控制窗已关闭、全部资源已清理。
  /// </summary>
  internal static async Task<ScreenRegionSelection?> CaptureAsync(
      nint owner,
      PhysicalRectangle bounds,
      CancellationToken cancellationToken,
      Window? selectionWindow = null)
  {
    var session = new Session(owner, bounds, cancellationToken, selectionWindow);
    try
    {
      selectionWindow?.AppWindow.Hide();
      CapturedFrame? frame = await session.RunAsync();
      return frame is null ? null : ScreenRegionPicker.CreateScrollingSelection(session.Bounds, frame);
    }
    finally { session.CloseControlWindow(); }
  }

  /// <summary>
  /// 单次采集会话的全部状态。窗口/按钮在 UI 线程构建，采集循环跑在线程池，
  /// UI 更新统一经 DispatcherQueue；失败状态保留窗口给用户查看，取消/关闭后才向调用方抛出。
  /// </summary>
  internal sealed class Session
  {
    private const int CaptureIntervalMs = 100; // 连续滚动也保留可验证重叠的中间帧。
    // 静止判定为连续两帧完全相同（一次比较）：内容静止后最迟 1 个采集间隔（100ms）内上交；
    // 内容持续变化超过 DynamicContentTimeoutMs 判为动态失败。
    private const int StableComparisonsRequired = 1;
    private const int DynamicContentTimeoutMs = 5000; // 持续变化上限，避免无限忙循环。
    private const int ForegroundRestoreTimeoutMs = 5000; // 完成采样等待源窗口恢复前台的上限，防止无限挂起。
    private const int MinSelectionSidePx = 32; // 方向尚未选择时，两边采用同一最小值；开始时另验滚动轴至少 64px。
    private const int PlacementGapPx = 16; // 控制窗与选区间距（physical px）。
    private const int WindowLogicalWidth = 500; // 紧凑工具栏逻辑宽度。
    private const int PreviewMaxLogicalPx = 220; // 预览框逻辑尺寸上限。
    private const int PreviewMaxDecodePx = PreviewMaxLogicalPx * 2; // 按小尺寸解码，避免保留全帧位图。
    private const int TotalTimeoutMs = 180_000;
    private const int NoProgressTimeoutMs = 30_000;
    private const int AutomaticNoProgressAttempts = 3;
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNearest = 2;

    private readonly nint _owner;
    private PhysicalRectangle _bounds;
    private readonly CancellationToken _external;
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly TaskCompletionSource<CapturedFrame?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _elapsed = new();
    private readonly object _terminalGate = new();

    private Window _window = null!;
    private readonly Window? _selectionWindow;
    private ScrollCaptureMask? _mask;
    private NumberBox? _widthBox, _heightBox;
    private volatile bool _overlapMissing;
    private nint _controlHandle;
    private StackPanel? _toolbarContent;
    private int _acceptedPreviewWidth, _acceptedPreviewHeight;
    private Window? _previewWindow;
    private nint _previewHandle;
    private TextBlock _statusText = null!;
    private TextBlock _errorText = null!;
    private Image _previewImage = null!;
    private Button _finishButton = null!;
    private Button _cancelButton = null!;
    private ToggleButton[] _directionSegments = [];
    private CheckBox _automaticBox = null!;
    private NumberBox _leadingBox = null!;
    private NumberBox _trailingBox = null!;
    private NumberBox _offsetBox = null!;
    private Button _confirmButton = null!;
    private Image _pendingImage = null!;
    private Button _settingsButton = null!;
    private ScrollCaptureDirection _direction;
    private int _fixedLeading, _fixedTrailing;
    private volatile bool _automatic;
    private volatile bool _reviewPending;
    private CorrectionAuthorization? _correctionAuthorization;

    private sealed class CorrectionAuthorization(CapturedFrame frame, int offset)
    {
      public CapturedFrame Frame { get; } = frame;
      public int Offset { get; } = offset;
      public bool Claimed { get; set; }
    }
    private CapturedFrame? _pendingFrame;
    private CapturedFrame? _finalFrame;
    private long _lastProgress;
    private bool _scrollOutstanding;
    private int _noProgressAttempts;
    private bool _reverseReviewIssued;
    private volatile string _scrollInputDiagnostic = "not attempted";

    private nint _source;
    private PhysicalRectangle _sourceRect;
    private uint _sourceDpi;
    private PhysicalRectangle _desktop;

    private VerticalScrollStitcher? _stitcher;
    private CapturedFrame? _lastCaptured; // 只保留前一帧，不保留历史。
    private volatile int _stableStreak;
    private volatile bool _appendedForCurrentStreak;
    private long _lastStableWitness;
    private volatile bool _finishRequested;
    private volatile Exception? _failure;
    private Task? _loop;
    private Task _deadlineTask = Task.CompletedTask;
    private bool _finishSamplingStarted;
    private bool _closed;
    private Task _previewTask = Task.CompletedTask;
    private bool _acceptedPreviewQueued; // _terminalGate：生成中或已排入 UI，尚未交给渲染任务。
    private CancellationTokenRegistration _registration;

    internal Window ControlWindow => _window;
    internal PhysicalRectangle Bounds => _bounds;
    internal string DiagnosticState =>
        $"automatic={_automatic}, review={_reviewPending}, " +
        $"stable={_stableStreak}, appended={_appendedForCurrentStreak}, input={_scrollInputDiagnostic}, failure={_failure?.Message}";
    internal bool HasReleasedCaptureResources => _loop?.IsCompleted != false && _previewTask.IsCompleted &&
        _stitcher is null && _lastCaptured is null && _pendingFrame is null && _finalFrame is null && _correctionAuthorization is null &&
        _mask is null && _previewWindow is null;

    public Session(nint owner, PhysicalRectangle bounds, CancellationToken external, Window? selectionWindow = null)
    {
      _owner = owner;
      _bounds = bounds;
      _external = external;
      _selectionWindow = selectionWindow;
    }

    public async Task<CapturedFrame?> RunAsync()
    {
      if (_owner == 0)
      {
        throw new ArgumentException("需要 owner 窗口句柄。");
      }

      _external.ThrowIfCancellationRequested();
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        throw new PlatformNotSupportedException("滚动截图需要 Windows 10 2004 或更高版本，以排除控制窗及阴影。");
      _bounds.Validate();
      ValidateSelectionSize();
      _desktop = SmartScreenCandidates.CurrentDesktop();
      string? sourceError = IdentifySource();
      try { CreateWindow(); }
      catch
      {
        _mask?.Dispose();
        _mask = null;
        _previewWindow?.Close();
        _previewWindow = null;
        CloseControlWindow();
        _lifecycle.Dispose();
        throw;
      }
      _registration = _external.Register(OnExternalCancelled);
      try
      {
        if (sourceError is not null || _source == _controlHandle)
        {
          Fail(new InvalidOperationException(sourceError ?? "选区中心的源窗口解析失败，请重新框选。"));
        }
        else
        {
          SetReadyUi();
        }

        return await _completion.Task;
      }
      finally
      {
        _lifecycle.Cancel();
        _registration.Dispose();
        Task? loop = _loop;
        if (loop is not null)
        {
          try
          {
            await loop;
          }
          catch
          {
            // RunLoopAsync 内部已把全部异常路由到 _completion，这里不会收到。
          }
        }

        await _deadlineTask;
        // 先等预览渲染收尾再关窗，避免向已关闭窗口写入。
        await _previewTask;
        _previewImage.Source = null;
        _lastCaptured = null;
        _pendingFrame = null;
        _correctionAuthorization = null;
        _finalFrame = null;
        _pendingImage.Source = null;
        _stitcher = null;
        _acceptedPreviewQueued = false;
        _previewWindow?.Close();
        _previewWindow = null;
        _mask?.Dispose();
        _mask = null;
        CloseControlWindow(); // owner/任务栏恢复由调用方负责，此处只关本模块控制窗。
        _lifecycle.Dispose();
      }
    }

    /// <summary>首次 GDI 采集前的选区尺寸守卫：过小无采集意义，过大直接拒绝超限整帧分配。</summary>
    private void ValidateSelectionSize()
    {
      if (_bounds.Width < MinSelectionSidePx ||
          _bounds.Height < MinSelectionSidePx ||
          _bounds.Width > VerticalScrollStitcher.MaximumDimension ||
          _bounds.Height > VerticalScrollStitcher.MaximumDimension ||
          (long)_bounds.Width * _bounds.Height > VerticalScrollStitcher.MaximumPixels)
      {
        throw new InvalidOperationException(
            $"选区尺寸超出滚动截图支持范围（宽≥{MinSelectionSidePx}、高≥{MinSelectionSidePx}，"
            + $"单边≤{VerticalScrollStitcher.MaximumDimension}，"
            + $"总像素≤{VerticalScrollStitcher.MaximumPixels}），请重新框选。");
      }
    }

    /// <summary>WindowFromPoint(选区中心) → GetAncestor(GA_ROOT) 确定源窗口并记录几何基准。</summary>
    private string? IdentifySource()
    {
      var center = new Point { X = _bounds.X + _bounds.Width / 2, Y = _bounds.Y + _bounds.Height / 2 };
      nint hit = WindowFromPoint(center);
      nint root = hit == 0 ? 0 : GetAncestor(hit, GaRoot);
      if (root == 0 || root == _owner)
      {
        return "未能识别选区中心的源窗口，请重新框选。";
      }

      if (SmartScreenCandidates.TryReadWindowBounds(root) is not { } windowRect)
      {
        return "源窗口不可见或已最小化，无法采集。";
      }

      if (!ContainsRect(windowRect, _bounds))
      {
        return "选区超出源窗口范围，请重新框选源窗口内容区域。";
      }

      uint dpi = GetDpiForWindow(root);
      if (dpi == 0)
      {
        return "无法读取源窗口 DPI，请重试。";
      }

      _source = root;
      _sourceRect = windowRect;
      _sourceDpi = dpi;
      return null;
    }

    /// <summary>构建原生 WinUI 控制窗：置顶、禁最大/最小/resize，按选区四边在工作区内找空位。</summary>
    private void CreateWindow()
    {
      _statusText = new TextBlock { Text = "准备中…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
      _errorText = new TextBlock
      {
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
      };
      _directionSegments = CreateDirectionSegments();
      var directionGrid = new Grid();
      for (int index = 0; index < _directionSegments.Length; index++)
      {
        directionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_directionSegments[index], index);
        directionGrid.Children.Add(_directionSegments[index]);
      }
      // 分段控制器：滚动方向是唯一必选项，选定即开始默认自动滚动采集。
      var directionControl = new Border
      {
        Child = directionGrid,
        Padding = new Thickness(2),
        CornerRadius = new CornerRadius(6),
        VerticalAlignment = VerticalAlignment.Center,
      };
      AutomationProperties.SetAutomationId(directionControl, "scroll-capture-direction");
      AutomationProperties.SetName(directionControl, "滚动方向");
      _finishButton = new Button { Content = "完成", IsEnabled = false };
      AutomationProperties.SetAutomationId(_finishButton, "scroll-capture-finish");
      _finishButton.Click += OnFinish;
      _cancelButton = new Button { Content = "取消" };
      AutomationProperties.SetAutomationId(_cancelButton, "scroll-capture-cancel");
      _cancelButton.Click += OnCancel;
      // 可选设置收进 Flyout：默认自动滚动、宽高与固定边缘校准能力保留，主工具栏只留必需动作。
      _automaticBox = new CheckBox { Content = "自动滚动", IsChecked = true };
      AutomationProperties.SetAutomationId(_automaticBox, "scroll-capture-automatic");
      _widthBox = EdgeNumber("宽", "scroll-capture-width");
      _heightBox = EdgeNumber("高", "scroll-capture-height");
      _widthBox.Value = _bounds.Width;
      _heightBox.Value = _bounds.Height;
      _widthBox.Width = _heightBox.Width = 110;
      var resize = new Button { Content = "应用尺寸" };
      AutomationProperties.SetAutomationId(resize, "scroll-capture-resize");
      resize.Click += (_, _) => { try { ApplySelectionDimensions(); } catch (Exception error) { Fail(error); } };
      _leadingBox = EdgeNumber("固定顶部 / 左侧（px）", "scroll-capture-leading");
      _trailingBox = EdgeNumber("固定底部 / 右侧（px）", "scroll-capture-trailing");
      var settings = new StackPanel { Spacing = 6, Width = 240 };
      settings.Children.Add(_automaticBox);
      settings.Children.Add(_widthBox);
      settings.Children.Add(_heightBox);
      settings.Children.Add(resize);
      settings.Children.Add(_leadingBox);
      settings.Children.Add(_trailingBox);
      _settingsButton = new Button { Content = "设置", Flyout = new Flyout { Content = settings } };
      AutomationProperties.SetAutomationId(_settingsButton, "scroll-capture-settings");
      var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
      buttons.Children.Add(directionControl);
      buttons.Children.Add(_finishButton);
      buttons.Children.Add(_cancelButton);
      buttons.Children.Add(_settingsButton);
      var root = new StackPanel { Spacing = 4, Padding = new Thickness(6), Width = WindowLogicalWidth };
      _toolbarContent = root;
      root.Children.Add(buttons);
      root.Children.Add(_statusText);
      _offsetBox = EdgeNumber("纠偏位移（px）", "scroll-capture-offset");
      _offsetBox.Minimum = 1;
      _offsetBox.Visibility = Visibility.Collapsed;
      root.Children.Add(_offsetBox);
      _confirmButton = new Button { Content = "继续", Visibility = Visibility.Collapsed };
      AutomationProperties.SetAutomationId(_confirmButton, "scroll-capture-confirm");
      _confirmButton.Click += OnConfirm;
      root.Children.Add(_confirmButton);
      root.Children.Add(_errorText);
      _window = _selectionWindow ?? new Window();
      _window.Title = "VibeOCR 长截图";
      _window.Content = new ScrollViewer
      {
        Content = root,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
      };
      ConfigureOverlayWindow(_window);
      _controlHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
      root.Measure(new Windows.Foundation.Size(WindowLogicalWidth, double.PositiveInfinity));
      double scale = _sourceDpi > 0 ? _sourceDpi / 96.0 : 1.0;
      if (!PlaceWindow((int)Math.Ceiling(WindowLogicalWidth * scale), (int)Math.Ceiling(root.DesiredSize.Height * scale)))
      {
        throw new InvalidOperationException("选区旁没有工具栏空间，请缩小选区后重试。");
      }
      _previewImage = new Image { Stretch = Stretch.Uniform };
      _pendingImage = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
      var previews = new StackPanel { Spacing = 4 };
      previews.Children.Add(_previewImage);
      previews.Children.Add(_pendingImage);
      _previewWindow = new Window { Content = new Border { Padding = new Thickness(3), Child = previews } };
      try
      {
        ConfigureOverlayWindow(_previewWindow);
        _previewHandle = WinRT.Interop.WindowNative.GetWindowHandle(_previewWindow);
        nint style = GetWindowLongPtrW(_previewHandle, -20);
        SetWindowLongPtrW(_previewHandle, -20, (nint)((long)style | 0x08000020));
        if (!PositionPreview(_bounds.Width, _bounds.Height))
          throw new InvalidOperationException("选区旁没有预览空间，请缩小选区后重试。");
        _mask = new ScrollCaptureMask(_desktop, _bounds);
        _window.Closed += OnWindowClosed;
        _window.Activate();
      }
      catch
      {
        _mask?.Dispose();
        _mask = null;
        throw;
      }
    }

    private static void ConfigureOverlayWindow(Window window)
    {
      OverlappedPresenter presenter = OverlappedPresenter.Create();
      presenter.SetBorderAndTitleBar(false, false);
      presenter.IsAlwaysOnTop = true;
      presenter.IsResizable = false;
      presenter.IsMaximizable = false;
      presenter.IsMinimizable = false;
      window.AppWindow.SetPresenter(presenter);
      window.AppWindow.IsShownInSwitchers = false;
      nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
      if (!SetWindowDisplayAffinity(handle, 0x00000011))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "无法将长截图遮罩排除出采集。");
    }

    private bool PositionPreview(int width, int height)
    {
      if (_previewWindow is null) return true; // 自检的内嵌预览由宿主控件布局。
      _acceptedPreviewWidth = width;
      _acceptedPreviewHeight = height;
      double dpiScale = _sourceDpi > 0 ? _sourceDpi / 96.0 : 1;
      PhysicalRectangle work = GetWorkArea(_bounds);
      int sideSpace = Math.Max(work.Right - _bounds.Right, _bounds.X - work.X) - PlacementGapPx - 6;
      bool horizontal = _direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
      double maxWidth = Math.Clamp(sideSpace / dpiScale, 48, horizontal ? 380 : 140);
      bool pending = _pendingImage.Visibility == Visibility.Visible && _pendingImage.Source is BitmapImage;
      double preferredScale = horizontal ? 96.0 / height : 140.0 / width;
      double scale = Math.Min(1, Math.Min(preferredScale, Math.Min(maxWidth / width, (pending ? 180.0 : 380.0) / height)));
      _previewImage.Width = Math.Max(1, width * scale);
      _previewImage.Height = Math.Max(1, height * scale);
      if (pending && _pendingImage.Source is BitmapImage pendingBitmap)
      {
        double pendingScale = Math.Min(1, Math.Min(maxWidth / Math.Max(1, pendingBitmap.PixelWidth), 180.0 / Math.Max(1, pendingBitmap.PixelHeight)));
        _pendingImage.Width = Math.Max(1, pendingBitmap.PixelWidth * pendingScale);
        _pendingImage.Height = Math.Max(1, pendingBitmap.PixelHeight * pendingScale);
      }
      int physicalWidth = (int)Math.Ceiling((Math.Max(_previewImage.Width, pending ? _pendingImage.Width : 0) + 6) * dpiScale);
      int physicalHeight = (int)Math.Ceiling((_previewImage.Height + (pending ? _pendingImage.Height + 4 : 0) + 6) * dpiScale);
      foreach (RectInt32 candidate in PreviewPlacementCandidates(
          work, _bounds, ControlRectangle(), physicalWidth, physicalHeight))
      {
        var rect = new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height);
        if (!ContainsRect(work, rect) || SmartScreenCandidates.Intersect(rect, _bounds) is not null ||
            SmartScreenCandidates.Intersect(rect, ControlRectangle()) is not null) continue;
        _previewWindow.AppWindow.MoveAndResize(candidate);
        if (!GetWindowRect(_previewHandle, out RectL actual)) continue;
        var actualRect = new PhysicalRectangle(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
        if (!ContainsRect(work, actualRect) || SmartScreenCandidates.Intersect(actualRect, _bounds) is not null ||
            SmartScreenCandidates.Intersect(actualRect, ControlRectangle()) is not null) continue;
        ShowWindow(_previewHandle, 8);
        if (SetWindowPos(_previewHandle, -1, 0, 0, 0, 0, 0x0013) && IsWindowVisible(_previewHandle)) return true;
      }
      _previewWindow.AppWindow.Hide();
      return false;
    }

    private bool EnsurePreviewVisible()
    {
      if (_previewWindow is null || (_acceptedPreviewWidth > 0 && PositionPreview(_acceptedPreviewWidth, _acceptedPreviewHeight))) return true;
      Fail(new InvalidOperationException("选区旁没有预览空间，无法确认截图。请取消并缩小选区后重试。"));
      return false;
    }

    private void ApplySelectionDimensions()
    {
      if (_loop is not null || _widthBox is null || _heightBox is null) return;
      double width = _widthBox.Value, height = _heightBox.Value;
      if (!double.IsFinite(width) || !double.IsFinite(height) || width != Math.Truncate(width) || height != Math.Truncate(height) ||
          width < MinSelectionSidePx || height < MinSelectionSidePx ||
          width > VerticalScrollStitcher.MaximumDimension || height > VerticalScrollStitcher.MaximumDimension || width * height > VerticalScrollStitcher.MaximumPixels ||
          !SelectionFitsCaptureArea(_bounds with { Width = (int)width, Height = (int)height }))
      {
        _errorText.Text = "尺寸需位于源窗口与桌面范围内，且为整数并不超过像素上限。";
        _errorText.Visibility = Visibility.Visible;
        return;
      }
      PhysicalRectangle previous = _bounds;
      _bounds = _bounds with { Width = (int)width, Height = (int)height };
      SizeInt32 size = _window.AppWindow.Size;
      if (!PlaceWindow(size.Width, size.Height))
      {
        _bounds = previous;
        PlaceWindow(size.Width, size.Height);
        _errorText.Text = "选区旁没有工具栏空间，请减小尺寸。";
        _errorText.Visibility = Visibility.Visible;
        return;
      }
      _mask?.Dispose();
      _mask = new ScrollCaptureMask(_desktop, _bounds);
      _acceptedPreviewWidth = _bounds.Width;
      _acceptedPreviewHeight = _bounds.Height;
      if (!EnsurePreviewVisible()) return;
      _errorText.Visibility = Visibility.Collapsed;
    }

    private bool SelectionFitsCaptureArea(PhysicalRectangle bounds) =>
        ContainsRect(_sourceRect, bounds) && ContainsRect(_desktop, bounds);

    /// <summary>
    /// 依次尝试选区下方/右侧/左侧/上方；每次都用真实 GetWindowRect 校验“完全在选区外
    /// 且完全在所在显示器工作区内”，找不到合法位置返回 false。
    /// </summary>
    private bool PlaceWindow(int width, int height)
    {
      PointInt32 oldPosition = _window.AppWindow.Position;
      SizeInt32 oldSize = _window.AppWindow.Size;
      PhysicalRectangle work = GetWorkArea(_bounds);
      width = Math.Min(width, work.Width);
      height = Math.Min(height, work.Height);
      foreach (RectInt32 candidate in PlacementCandidates(work, _bounds, width, height))
      {
        _window.AppWindow.MoveAndResize(candidate);
        if (GetWindowRect(_controlHandle, out RectL actual))
        {
          var rect = new PhysicalRectangle(
              actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
          if (ContainsRect(work, rect) && SmartScreenCandidates.Intersect(rect, _bounds) is null)
          {
            return true;
          }
        }
      }

      _window.AppWindow.MoveAndResize(new RectInt32(oldPosition.X, oldPosition.Y, oldSize.Width, oldSize.Height));
      return false;
    }

    private void FitToolbar()
    {
      if (_toolbarContent is null) return;
      _toolbarContent.Measure(new Windows.Foundation.Size(WindowLogicalWidth, double.PositiveInfinity));
      double scale = _sourceDpi > 0 ? _sourceDpi / 96.0 : 1;
      int height = (int)Math.Ceiling(_toolbarContent.DesiredSize.Height * scale);
      SizeInt32 current = _window.AppWindow.Size;
      if (height != current.Height) PlaceWindow(current.Width, height);
      if (_acceptedPreviewWidth > 0 && _failure is null && !_lifecycle.IsCancellationRequested) EnsurePreviewVisible();
    }

    /// <summary>围绕 anchor 四边（下/右/左/上，居中对齐）的候选；仅居中轴被
    /// clamp 到工作区，候选仍可能越界或与其它窗相交，合法性由调用方逐个验证。</summary>
    private static RectInt32[] PlacementCandidates(PhysicalRectangle work, PhysicalRectangle anchor, int width, int height)
    {
      int centeredX = Math.Clamp(
          anchor.X + anchor.Width / 2 - width / 2, work.X, Math.Max(work.X, work.Right - width));
      int centeredY = Math.Clamp(
          anchor.Y + anchor.Height / 2 - height / 2, work.Y, Math.Max(work.Y, work.Bottom - height));
      return
      [
          new RectInt32(centeredX, anchor.Bottom + PlacementGapPx, width, height),
          new RectInt32(anchor.Right + PlacementGapPx, centeredY, width, height),
          new RectInt32(anchor.X - PlacementGapPx - width, centeredY, width, height),
          new RectInt32(centeredX, anchor.Y - PlacementGapPx - height, width, height),
      ];
    }

    /// <summary>
    /// 预览最终尝试顺序：选区四候选按右/左/其它优先级稳定排序在前，
    /// 再追加围绕选区∪工具栏的同算法 fallback（同样优先级排序），
    /// 避免长预览增高后四居中候选全部被工具栏/工作区阻挡。合法性仍由
    /// 调用方的工作区包含、不相交及真实 HWND 复核决定，不把无空间当成功。
    /// </summary>
    internal static RectInt32[] PreviewPlacementCandidates(
        PhysicalRectangle work, PhysicalRectangle bounds, PhysicalRectangle control, int width, int height)
    {
      int Priority(RectInt32 rect) =>
          rect.X >= bounds.Right ? 0 : rect.X + rect.Width <= bounds.X ? 1 : 2;
      RectInt32[] primary = [.. PlacementCandidates(work, bounds, width, height).OrderBy(Priority)];
      PhysicalRectangle union = new(
          Math.Min(bounds.X, control.X),
          Math.Min(bounds.Y, control.Y),
          Math.Max(bounds.Right, control.Right) - Math.Min(bounds.X, control.X),
          Math.Max(bounds.Bottom, control.Bottom) - Math.Min(bounds.Y, control.Y));
      return union == bounds ? primary
          : [.. primary, .. PlacementCandidates(work, union, width, height).OrderBy(Priority)];
    }

    private static PhysicalRectangle GetWorkArea(PhysicalRectangle bounds)
    {
      var area = new RectL
      {
        Left = bounds.X,
        Top = bounds.Y,
        Right = bounds.Right,
        Bottom = bounds.Bottom,
      };
      nint monitor = MonitorFromRect(ref area, MonitorDefaultToNearest);
      if (monitor != 0)
      {
        var info = new MonitorInfo { CbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfoW(monitor, ref info))
        {
          return new PhysicalRectangle(
              info.RcWork.Left,
              info.RcWork.Top,
              info.RcWork.Right - info.RcWork.Left,
              info.RcWork.Bottom - info.RcWork.Top);
        }
      }

      return DesktopScreenQuery.GetPrimaryMonitor();
    }

    /// <summary>采集循环（线程池）。所有结果都路由进 _completion，自身不向调用方抛异常。</summary>
    private async Task RunLoopAsync()
    {
      try
      {
        await using var capture = new ScreenCaptureService(Guid.NewGuid());
        _lastStableWitness = Environment.TickCount64;
        _lastProgress = _lastStableWitness;
        _elapsed.Start();
        _deadlineTask = EnforceDeadlineAsync();
        // 完成采样等待源窗口恢复前台的截止时刻；仅本循环线程读写，无需跨线程同步。
        long finishForegroundDeadline = 0;
        while (true)
        {
          _lifecycle.Token.ThrowIfCancellationRequested();
          if (_failure is not null || _completion.Task.IsCompleted)
          {
            return;
          }

          if (_elapsed.ElapsedMilliseconds >= TotalTimeoutMs)
          {
            Fail(new InvalidOperationException("采集超过 3 分钟总时限，已停止；本次未完成，未生成长图。"));
            return;
          }
          if (NoProgressExpired())
          {
            string? sourceViolation = GuardBeforeCapture(GetForegroundWindow());
            if (sourceViolation is not null) { Fail(new InvalidOperationException(sourceViolation)); return; }
            // 无进展按边界收尾而不是失败：干净状态下直接交付已收选段；
            // 从未捕获到稳定首帧时仍如实停止并说明原因。
            HandleNoProgressBoundary();
            return;
          }
          if (_reviewPending)
          {
            string? sourceViolation = GuardBeforeCapture(GetForegroundWindow());
            if (sourceViolation is not null) { Fail(new InvalidOperationException(sourceViolation)); return; }
            ApplyConfirmedOffset();
            await Task.Delay(CaptureIntervalMs, _lifecycle.Token);
            continue;
          }


          if (_finishRequested && !_finishSamplingStarted)
          {
            _finishSamplingStarted = true;
            DiscardStableWitness();
            _appendedForCurrentStreak = false;
            // 恢复源窗口外观后重新采样；系统拒绝切换时按下面的期限失败。
            SetForegroundWindow(_source);
            finishForegroundDeadline = Environment.TickCount64 + ForegroundRestoreTimeoutMs;
          }
          nint foreground = GetForegroundWindow();
          string? violation = GuardBeforeCapture(foreground);
          if (violation is not null)
          {
            Fail(new InvalidOperationException(violation));
            return;
          }

          if (foreground != _source)
          {
            // 控制窗交互期间暂停采样和动态计时，仍执行窗口/遮挡守卫。
            DiscardStableWitness();
            if (_finishSamplingStarted && Environment.TickCount64 >= finishForegroundDeadline)
            {
              Fail(new InvalidOperationException(
                  "无法将源窗口恢复前台以完成收尾采样（前台切换被系统拒绝），请重试滚动截图。"));
              return;
            }

            await Task.Delay(CaptureIntervalMs, _lifecycle.Token);
            continue;
          }

          CapturedFrame frame = capture.Capture(_bounds, TimeSpan.FromMinutes(1));

          // 采集后复用完整守卫：前台/遮挡/几何任一变化都立即停止。
          foreground = GetForegroundWindow();
          violation = GuardBeforeCapture(foreground);
          if (violation is not null)
          {
            Fail(new InvalidOperationException(violation));
            return;
          }

          if (foreground != _source)
          {
            // 丢弃采集中途控制窗获焦时的过渡帧。
            DiscardStableWitness();
            await Task.Delay(CaptureIntervalMs, _lifecycle.Token);
            continue;
          }

          await ObserveFrameAsync(frame);
          if (_failure is not null || _completion.Task.IsCompleted) return;
          if (!_reviewPending && _finishSamplingStarted && _stitcher is not null && _appendedForCurrentStreak && _stableStreak >= StableComparisonsRequired && !_overlapMissing)
          {
            // 用户显式完成：编辑器就是结果预览，直接交付，不再追加确认层。
            PublishSegment(_stitcher, boundary: false);
            return;
          }

          if (_automatic && !_reviewPending && !_finishRequested && _appendedForCurrentStreak && _stableStreak >= StableComparisonsRequired)
          {
            if (_scrollOutstanding && ++_noProgressAttempts >= AutomaticNoProgressAttempts)
            {
              _automatic = false;
              _scrollOutstanding = false;
              PauseForReview("自动滚动连续 3 次未前进：无法判断是否到达末尾，当前仅捕获已收选段。可确认后手动继续，或点击完成直接交付已收选段。", null);
            }
            else
            {
              string? inputViolation = GuardBeforeCapture(GetForegroundWindow());
              if (inputViolation is not null) { Fail(new InvalidOperationException(inputViolation)); return; }
              if (!SendScroll())
              {
                Fail(new InvalidOperationException("标准滚轮输入被拒绝：请松开鼠键及 Ctrl/Shift/Alt，并确认目标支持滚轮且未提权；本次未完成。"));
                return;
              }
              _scrollOutstanding = true;
              DiscardStableWitness();
              _appendedForCurrentStreak = false;
            }
          }
          await Task.Delay(CaptureIntervalMs, _lifecycle.Token);
        }
      }
      catch (OperationCanceledException)
      {
        // 取消完成路径由 Cancel/关闭/外部 token 负责，不在这里结束 _completion。
      }
      catch (Exception error)
      {
        Fail(new InvalidOperationException($"滚动截图采集失败：{error.Message}", error));
      }
    }

    // 生产循环与 UI 自检共同经过真实帧比较和动态超时判定。
    private async Task ObserveFrameAsync(CapturedFrame frame)
    {
      CapturedFrame? previous = _lastCaptured;
      _lastCaptured = frame;
      bool identical = false;
      if (previous is not null)
      {
        CapturedFrame prior = previous;
        // 纯字节比对放线程池并传 token：取消时立即中止，不占住 UI 线程。
        identical = await Task.Run(
            () => frame.Pixels.AsSpan().SequenceEqual(prior.Pixels), _lifecycle.Token);
      }

      long now = Environment.TickCount64;
      if (identical)
      {
        _lastStableWitness = now;
        _stableStreak++;
        HandleStable(frame);
      }
      else
      {
        _stableStreak = 0;
        _appendedForCurrentStreak = false;
        // 完整重叠就是静态内容的证据；无需等待停止滚动才接收可拼接帧。
        if (_stitcher is not null && !_reviewPending)
          HandleStable(frame, moving: true);
        if (now - _lastStableWitness > DynamicContentTimeoutMs)
        {
          Fail(new InvalidDataException("选区内容持续变化（动态内容），始终未能稳定，已停止采集。"));
          return;
        }
      }

    }

    /// <summary>丢弃过渡帧并重新计算内容稳定时间。</summary>
    private void DiscardStableWitness()
    {
      _lastCaptured = null;
      _stableStreak = 0;
      _lastStableWitness = Environment.TickCount64;
    }

    /// <summary>采集前后守卫：前台归属、控制窗未压选区、源窗口几何、上方遮挡。</summary>
    private string? GuardBeforeCapture(nint foreground)
    {
      // 主动收尾切换时 Windows 可短暂返回 NULL；循环在原截止时间内等待，期间不采集。
      if (foreground != _source && foreground != _controlHandle &&
          !(foreground == 0 && _finishSamplingStarted))
      {
        AppLog.Warn($"scroll-capture foreground guard: actual={foreground}, source={_source}, control={_controlHandle}, finishing={_finishSamplingStarted}");
        return "源窗口已失去前台（其他窗口被激活），已停止采集。";
      }

      if (SmartScreenCandidates.Intersect(ControlRectangle(), _bounds) is not null)
      {
        return "控制窗被移动到选区内，已停止采集。";
      }

      string? source = ValidateSourceState();
      if (source is not null)
      {
        return source;
      }

      return FindOccluder();
    }

    private string? ValidateSourceState()
    {
      if (_source == 0 || !IsWindow(_source))
      {
        return "源窗口已关闭，已停止采集。";
      }

      if (SmartScreenCandidates.TryReadWindowBounds(_source) is not { } rect)
      {
        return "源窗口不可见或已最小化，已停止采集。";
      }

      if (rect != _sourceRect)
      {
        return "源窗口位置或大小发生变化，已停止采集。";
      }

      if (GetDpiForWindow(_source) != _sourceDpi)
      {
        return "源窗口 DPI 发生变化，已停止采集。";
      }

      if (SmartScreenCandidates.CurrentDesktop() != _desktop)
      {
        return "显示器布局发生变化，已停止采集。";
      }

      return null;
    }

    private PhysicalRectangle ControlRectangle()
    {
      if (_controlHandle != 0 && GetWindowRect(_controlHandle, out RectL rect) &&
          rect.Right > rect.Left && rect.Bottom > rect.Top)
      {
        return new PhysicalRectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
      }

      return _bounds; // 读不到控制窗矩形时按“覆盖选区”处理，宁可停止采集。
    }

    /// <summary>
    /// 自顶向下枚举顶层窗口（只读几何，不读标题/文本）：遇到与选区相交的可见、非 cloaked
    /// 窗口即遮挡；遇到源窗口则停止（其上方无遮挡）。不可见/最小化忽略；控制窗与 owner 跳过。
    /// </summary>
    private string? FindOccluder()
    {
      string? occluded = null;
      EnumWindows((handle, _) =>
      {
        if (handle == _source)
        {
          return false;
        }

        if (handle == _controlHandle || handle == _previewHandle || _mask?.Contains(handle) == true || handle == _owner)
        {
          return true;
        }

        if (SmartScreenCandidates.TryReadWindowBounds(handle) is not { } rect)
        {
          return true;
        }

        if (SmartScreenCandidates.Intersect(rect, _bounds) is null)
        {
          return true;
        }

        occluded = "有其他窗口覆盖选区，已停止采集。";
        return false;
      }, 0);
      return occluded;
    }

    /// <summary>连续两帧完全一致即视为静止并上交拼接器；同一段静止内容只上交一次，重复帧不增加接收数。</summary>
    private void HandleStable(CapturedFrame frame, bool moving = false)
    {
      if (!moving && _stableStreak < StableComparisonsRequired)
      {
        return;
      }

      if (_stitcher is null)
      {
        try
        {
          _stitcher = new VerticalScrollStitcher(frame, _direction, _fixedLeading, _fixedTrailing);
        }
        catch (InvalidDataException error)
        {
          Fail(new InvalidDataException(
              "首帧沿滚动方向没有可辨识的内容变化，无法定位滚动偏移；请扩大选区或选择内容更丰富的区域，已停止采集。",
              error));
          return;
        }

        // 方向选定即直达采集：首帧直接进入拼接器，不再弹首帧确认预览；
        // 已收预览持续更新，无进展边界从首帧起算。
        _appendedForCurrentStreak = true;
        _lastProgress = Environment.TickCount64;
        ReportAccepted(_stitcher, frame);
        RunOnUi(() => _statusText.Text = _automatic
            ? "已收首帧 · 自动滚动中，完成后点击“完成”"
            : "已收首帧 · 请按所选方向滚动源窗口，完成后点击“完成”");
        return;
      }

      if (_appendedForCurrentStreak)
      {
        return;
      }

      ScrollAppendResult result = _stitcher.Append(frame, _lifecycle.Token);
      // 动画/重绘中的不匹配帧不终止采集；稳定后才提示恢复或报告真实失败。
      if (moving && result.Status is not (ScrollAppendStatus.Added or ScrollAppendStatus.Duplicate or ScrollAppendStatus.LimitReached)) return;
      switch (result.Status)
      {
        case ScrollAppendStatus.Added:
          _overlapMissing = false;
          _lastStableWitness = Environment.TickCount64;
          RunOnUi(() => _errorText.Visibility = Visibility.Collapsed);
          _reverseReviewIssued = false;
          _appendedForCurrentStreak = true;
          _lastProgress = Environment.TickCount64;
          _scrollOutstanding = false;
          _noProgressAttempts = 0;
          ReportAccepted(_stitcher, frame);
          break;
        case ScrollAppendStatus.Duplicate:
          if (_overlapMissing)
          {
            _overlapMissing = false;
            RunOnUi(() => { _errorText.Visibility = Visibility.Collapsed; _finishButton.IsEnabled = !_reviewPending && !_overlapMissing; });
          }
          _reverseReviewIssued = false;
          _appendedForCurrentStreak = true;
          break;
        case ScrollAppendStatus.Reverse:
          _automatic = false;
          _scrollOutstanding = false;
          _appendedForCurrentStreak = true;
          // 返回已收边界前只暂停一次；明确丢弃后允许手动回到边界或完成已有选段。
          // 不收反向帧、不重置无进展期限，也不把负位移送进只接受正数的纠偏。
          if (!_reverseReviewIssued)
          {
            _reverseReviewIssued = true;
            PauseForReview("检测到反向滚动：该帧未加入已收选段。请丢弃反向帧后，按所选方向滚回已收边界再继续；也可点击完成直接交付已收选段。", null);
            RunOnUi(() => _confirmButton.Content = "丢弃反向帧并继续");
          }
          break;
        case ScrollAppendStatus.NoOverlap:
          _automatic = false;
          _scrollOutstanding = false;
          _overlapMissing = true;
          _appendedForCurrentStreak = true;
          RunOnUi(() =>
          {
            _finishButton.IsEnabled = false;
            _errorText.Text = "滚动过快或内容变化，请向回滚动到预览末尾，再缓慢继续；长时间无新增时将要求确认交付已收选段。";
            _errorText.Visibility = Visibility.Visible;
          });
          break;
        case ScrollAppendStatus.Ambiguous:
          _automatic = false;
          _scrollOutstanding = false;
          PauseForReview("无法可靠拼接（重复纹理导致偏移歧义），自动滚动已停止，本次尚未完成。上图为已收选段，下图为待拼帧；输入所选方向的真实位移并明确确认。完整重叠必须一致，否则拒绝；不能确认时请取消重选。", frame);
          break;
        case ScrollAppendStatus.FixedContentChanged:
          Fail(new InvalidDataException("用户指定的固定边缘发生变化，不能安全移除；本次未完成，请重新框选固定范围。"));
          break;
        case ScrollAppendStatus.LowTexture:
          Fail(new InvalidDataException(
              "选区内容纹理不足，无法定位滚动偏移；请扩大选区或选择内容更丰富的区域，已停止采集。"));
          break;
        case ScrollAppendStatus.LimitReached:
          Fail(new InvalidDataException(
              "已达到拼接上限（像素/尺寸/帧数限制）；请缩小选区或减少滚动量，已停止采集。"));
          break;
        default:
          Fail(new InvalidDataException($"拼接器返回未知状态 {result.Status}，已停止采集。"));
          break;
      }
    }

    private void ApplyConfirmedOffset(Action? afterClaimForTest = null, Action? afterMatchForTest = null)
    {
      CorrectionAuthorization authorization;
      VerticalScrollStitcher current;
      lock (_terminalGate)
      {
        if (_failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted ||
            !_reviewPending || _stitcher is null || _correctionAuthorization is not { Claimed: false } request ||
            !ReferenceEquals(request.Frame, _pendingFrame)) return;
        authorization = request;
        authorization.Claimed = true; // 匹配期间保留占用，重复点击不能授权下一帧。
        current = _stitcher;
      }
      try
      {
        afterClaimForTest?.Invoke();
        CapturedFrame pending = authorization.Frame;
        ScrollAppendResult correction = current.AppendWithOffset(pending, authorization.Offset, _lifecycle.Token);
        afterMatchForTest?.Invoke();
        lock (_terminalGate)
        {
          if (_failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted ||
              !ReferenceEquals(_correctionAuthorization, authorization) || !ReferenceEquals(_pendingFrame, pending)) return;
          _correctionAuthorization = null;
          if (correction.Status == ScrollAppendStatus.Added)
          {
            _pendingFrame = null;
            _reviewPending = false;
          }
        }
        if (correction.Status != ScrollAppendStatus.Added)
        {
          RunOnUi(() =>
          {
            lock (_terminalGate)
            {
              if (!IsCurrentReview(pending) || _correctionAuthorization is not null) return;
              _errorText.Text = correction.Status == ScrollAppendStatus.LimitReached
                ? "该位移会超过拼接上限，请选择其他可行位移；未修改已收图像。不能确认时请取消。"
                : "纠偏未通过完整重叠校验，请核对位移；未修改已收图像。取消可丢弃本次采集。";
              _offsetBox.IsEnabled = true;
              _confirmButton.IsEnabled = true;
            }
          });
          return;
        }
        _lastProgress = Environment.TickCount64;
        DiscardStableWitness();
        _appendedForCurrentStreak = false;
        ReportAccepted(current, pending);
        RunOnUi(() =>
        {
          if (!_reviewPending && _pendingFrame is null) HideReview();
        });
        if (!_lifecycle.IsCancellationRequested && _failure is null) SetForegroundWindow(_source);
      }
      finally
      {
        lock (_terminalGate)
        {
          if (ReferenceEquals(_correctionAuthorization, authorization)) _correctionAuthorization = null;
        }
      }
    }

    /// <summary>交付路径：完成采样后直接合成已收选段并结束会话（boundary=true 表示无进展边界收尾）。</summary>
    private void PublishSegment(VerticalScrollStitcher stitcher, bool boundary)
    {
      CapturedFrame frame = stitcher.BuildFrame(_lifecycle.Token);
      AppLog.Info(
          $"scroll-capture 选段交付：frames={stitcher.FrameCount}, size={frame.Width}x{frame.Height}, " +
          $"boundary={boundary}, elapsedMs={(int)_elapsed.ElapsedMilliseconds}");
      lock (_terminalGate)
      {
        if (_failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted) return;
        _completion.TrySetResult(frame);
      }
    }

    /// <summary>缺帧边界：已收选段与当前画面缺少可靠重叠时，交付必须显式确认，预览展示已收选段。</summary>
    private void CompleteBuild(VerticalScrollStitcher stitcher)
    {
      CapturedFrame frame = stitcher.BuildFrame(_lifecycle.Token);
      AppLog.Info(
          $"scroll-capture 缺帧边界待确认：frames={stitcher.FrameCount}, " +
          $"size={frame.Width}x{frame.Height}, elapsedMs={(int)_elapsed.ElapsedMilliseconds}");
      VerticalScrollStitcher snapshot = stitcher;
      _finalFrame = frame;
      _reviewPending = true;
      byte[]? preview = EncodePreviewBmp(frame);
      RunOnUi(() =>
      {
        _statusText.Text = $"已收选段：{snapshot.FrameCount} 帧 · 缺少可靠重叠，需确认交付";
        _errorText.Text = "已长时间没有新增内容，且当前画面与已收选段之间缺少可靠重叠。确认仅交付已收选段（不含中间缺失内容），或取消放弃本次采集。";
        _errorText.Visibility = Visibility.Visible;
        _confirmButton.Content = "确认交付已收选段";
        _confirmButton.Visibility = Visibility.Visible;
        _confirmButton.IsEnabled = false;
        _finishButton.IsEnabled = false;
        _previewTask = RenderFinalPreviewAsync(_previewTask, preview, frame);
      });
    }

    private void ReportAccepted(VerticalScrollStitcher stitcher, CapturedFrame frame)
    {
      string status = $"已收 {stitcher.FrameCount} 帧 · {stitcher.Width} × {stitcher.Height} px";
      RunOnUi(() =>
      {
        _statusText.Text = status;
        _finishButton.IsEnabled = !_reviewPending && !_overlapMissing;
      });
      lock (_terminalGate)
      {
        if (_acceptedPreviewQueued || !_previewTask.IsCompleted || _failure is not null ||
            _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted) return;
        _acceptedPreviewQueued = true;
      }
      try
      {
        if (frame.Stride != checked(frame.Width * 4) || frame.Pixels.LongLength != (long)frame.Stride * frame.Height)
        {
          Fail(new InvalidOperationException("无法生成已收选段预览，本次未完成，请取消后重试。"));
          lock (_terminalGate) _acceptedPreviewQueued = false;
          return;
        }
        CapturedFrame thumbnail = stitcher.BuildPreview(PreviewMaxDecodePx, _lifecycle.Token);
        if (stitcher.FrameCount == 1) thumbnail = MarkFixedEdges(thumbnail, frame.Width, frame.Height);
        byte[]? preview = EncodePreviewBmp(thumbnail);
        if (preview is null)
        {
          Fail(new InvalidOperationException("无法生成已收选段预览，本次未完成，请取消后重试。"));
          lock (_terminalGate) _acceptedPreviewQueued = false;
          return;
        }
        void Present()
        {
          lock (_terminalGate)
          {
            _acceptedPreviewQueued = false;
            if (_failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted) return;
            UpdatePreview(preview, thumbnail.Width, thumbnail.Height);
          }
        }
        DispatcherQueue queue = _window.DispatcherQueue;
        if (queue.HasThreadAccess) Present();
        else if (!queue.TryEnqueue(Present))
          lock (_terminalGate) _acceptedPreviewQueued = false;
      }
      catch
      {
        lock (_terminalGate) _acceptedPreviewQueued = false;
        throw;
      }
    }

    private CapturedFrame MarkFixedEdges(CapturedFrame frame, int sourceWidth, int sourceHeight)
    {
      if (_fixedLeading == 0 && _fixedTrailing == 0) return frame;
      byte[] pixels = (byte[])frame.Pixels.Clone();
      bool horizontal = _direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
      int extent = horizontal ? sourceWidth : sourceHeight;
      int previewExtent = horizontal ? frame.Width : frame.Height;
      for (int y = 0; y < frame.Height; y++)
      {
        _lifecycle.Token.ThrowIfCancellationRequested();
        for (int x = 0; x < frame.Width; x++)
        {
          int axis = (int)((long)(horizontal ? x : y) * extent / previewExtent);
          if (axis >= _fixedLeading && axis < extent - _fixedTrailing) continue;
          int offset = y * frame.Stride + x * 4;
          pixels[offset] /= 2;
          pixels[offset + 1] /= 2;
          pixels[offset + 2] = (byte)((pixels[offset + 2] + 255) / 2);
        }
      }
      return new CapturedFrame(pixels, frame.Width, frame.Height, frame.Stride, frame.PixelFormat);
    }
    private static byte[]? EncodePreviewBmp(CapturedFrame frame)
    {
      try
      {
        return InputService.EncodeTopDownBmp(frame.Pixels, frame.Width, frame.Height, frame.Stride);
      }
      catch (Exception error)
      {
        AppLog.Warn($"scroll-capture 预览编码失败：{error.Message}");
        return null;
      }
    }

    /// <summary>
    /// 当前稳定帧小预览。BMP 编码在循环线程，解码按预览尺寸且流用 using 释放；
    /// 渲染任务单飞并在清理时 await：上一个未完成时丢弃本次更新，
    /// 避免排队累积或向已关闭窗口写入。
    /// </summary>
    private void UpdatePreview(byte[]? bmp, int width, int height)
    {
      if (bmp is null || bmp.Length == 0 || !_previewTask.IsCompleted || _lifecycle.IsCancellationRequested)
      {
        return;
      }

      _previewTask = RenderPreviewAsync(bmp, width, height);
    }

    private static BitmapImage CreatePreviewBitmap(int width, int height)
    {
      double scale = Math.Min(1.0, (double)PreviewMaxDecodePx / Math.Max(width, height));
      return new BitmapImage
      {
        DecodePixelType = DecodePixelType.Physical,
        DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale)),
        DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale)),
      };
    }

    private async Task RenderPreviewAsync(byte[] bmp, int width, int height)
    {
      try
      {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
          writer.WriteBytes(bmp);
          await writer.StoreAsync();
          writer.DetachStream();
        }

        stream.Seek(0);
        BitmapImage bitmap = CreatePreviewBitmap(width, height);
        await bitmap.SetSourceAsync(stream);
        if (_lifecycle.IsCancellationRequested)
        {
          return; // 会话已取消，不再触碰窗口控件。
        }

        if (!_lifecycle.IsCancellationRequested)
        {
          _previewImage.Source = bitmap;
          _acceptedPreviewWidth = width;
          _acceptedPreviewHeight = height;
          EnsurePreviewVisible();
        }
      }
      catch (Exception error)
      {
        Fail(new InvalidOperationException($"无法显示选段预览，本次未完成：{error.Message}", error));
      }
    }

    /// <summary>
    /// 失败状态：显示具体中文错误并停止采集；保留取消/关闭让用户看清错误，“完成”禁用。
    /// 用户取消/关闭后由 CancelFromUi 把该异常抛给调用方，不自动返回不完整长图。
    /// </summary>
    private void Fail(Exception error)
    {
      lock (_terminalGate)
      {
        if (_completion.Task.IsCompleted || _failure is not null || _lifecycle.IsCancellationRequested) return;
        _failure = error;
        _correctionAuthorization = null;
      }
      // 立即停止正在匹配/解码的工作；错误窗口仍保留到用户取消或关闭，返回最初原因。
      _lifecycle.Cancel();
      AppLog.Warn($"scroll-capture 停止：{error.Message}");
      RunOnUi(() =>
      {
        HideReview();
        _errorText.Text = error.Message;
        _errorText.Visibility = Visibility.Visible;
        // 固定高度窗口中为错误说明和取消按钮腾出空间。
        _previewImage.Visibility = Visibility.Collapsed;
        _previewWindow?.AppWindow.Hide();
        foreach (ToggleButton segment in _directionSegments) segment.IsEnabled = false;
        _finishButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _statusText.Text = "采集已停止 · 取消退出";
      }, allowAfterFailure: true);
    }

    private async Task EnforceDeadlineAsync()
    {
      try
      {
        await Task.Delay(TotalTimeoutMs, _lifecycle.Token);
        Fail(new InvalidOperationException("已到 3 分钟总时限（包含预览与纠偏等待），本次未完成，未生成长图。"));
      }
      catch (OperationCanceledException) { }
    }

    private async Task RenderFinalPreviewAsync(Task previous, byte[]? bytes, CapturedFrame frame)
    {
      await previous;
      if (_lifecycle.IsCancellationRequested || _failure is not null) return;
      if (bytes is null)
      {
        Fail(new InvalidOperationException("无法生成选段预览，本次未完成，请重试。"));
        return;
      }
      await RenderPreviewAsync(bytes, frame.Width, frame.Height);
      lock (_terminalGate)
      {
        if (!_lifecycle.IsCancellationRequested && _failure is null && !_completion.Task.IsCompleted)
          _confirmButton.IsEnabled = true;
      }
    }
    private static NumberBox EdgeNumber(string header, string automationId)
    {
      var box = new NumberBox
      {
        Header = header,
        Minimum = 0,
        Maximum = VerticalScrollStitcher.MaximumDimension,
        Value = 0,
        SmallChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
      };
      AutomationProperties.SetAutomationId(box, automationId);
      return box;
    }

    private void PauseForReview(string message, CapturedFrame? pending)
    {
      lock (_terminalGate)
      {
        if (_failure is not null || _completion.Task.IsCompleted || _lifecycle.IsCancellationRequested) return;
        _correctionAuthorization = null;
        _pendingFrame = pending;
        _reviewPending = true;
      }
      byte[]? preview = pending is null ? null : EncodePreviewBmp(pending);
      if (pending is not null && preview is null)
      {
        Fail(new InvalidOperationException("无法生成待拼预览，本次未完成，请取消后重试。"));
        return;
      }
      RunOnUi(() =>
      {
        if (!IsCurrentReview(pending)) return;
        _statusText.Text = "已暂停 · 确认后继续";
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
        _confirmButton.Content = pending is null ? "确认预览并继续" : "确认位移并校验拼接";
        _confirmButton.Visibility = Visibility.Visible;
        _confirmButton.IsEnabled = false;
        _finishButton.IsEnabled = pending is null;
        _offsetBox.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        _offsetBox.IsEnabled = true;
        if (pending is not null && _stitcher is { } stitcher)
        {
          _offsetBox.Maximum = stitcher.MaximumOffset;
          _offsetBox.Value = Math.Max(1, stitcher.MaximumOffset / 2);
          _pendingImage.Visibility = Visibility.Visible;
        }
        _previewTask = RenderReviewPreviewAsync(_previewTask, preview, pending);
      });
    }

    private bool IsCurrentReview(CapturedFrame? pending)
    {
      lock (_terminalGate)
        return _reviewPending && ReferenceEquals(_pendingFrame, pending) && _correctionAuthorization is null &&
            !_lifecycle.IsCancellationRequested && _failure is null && !_completion.Task.IsCompleted;
    }

    private async Task RenderReviewPreviewAsync(Task previous, byte[]? bytes, CapturedFrame? pending)
    {
      await previous;
      lock (_terminalGate)
      {
        if (!IsCurrentReview(pending)) return;
        if (bytes is null)
        {
          if (EnsurePreviewVisible()) _confirmButton.IsEnabled = true;
          return;
        }
      }
      try
      {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
          writer.WriteBytes(bytes);
          await writer.StoreAsync();
          writer.DetachStream();
        }
        stream.Seek(0);
        BitmapImage bitmap = CreatePreviewBitmap(pending!.Width, pending.Height);
        await bitmap.SetSourceAsync(stream);
        lock (_terminalGate)
        {
          if (!IsCurrentReview(pending)) return;
          _pendingImage.Source = bitmap;
          if (EnsurePreviewVisible()) _confirmButton.IsEnabled = true;
        }
      }
      catch (Exception error) { Fail(new InvalidOperationException($"无法显示待拼预览，本次未完成：{error.Message}", error)); }
    }

    private void HideReview()
    {
      _confirmButton.Visibility = Visibility.Collapsed;
      _confirmButton.IsEnabled = false;
      _offsetBox.Visibility = Visibility.Collapsed;
      _pendingImage.Visibility = Visibility.Collapsed;
      _pendingImage.Source = null;
      _errorText.Visibility = Visibility.Collapsed;
    }

    private void OnConfirm(object sender, RoutedEventArgs args)
    {
      if (!_reviewPending || !_confirmButton.IsEnabled || _failure is not null ||
          _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted) return;
      if (!EnsurePreviewVisible()) return;
      if (_finalFrame is { } final)
      {
        string? sourceViolation = GuardBeforeCapture(GetForegroundWindow());
        if (sourceViolation is not null) { Fail(new InvalidOperationException(sourceViolation)); return; }
        lock (_terminalGate)
        {
          if (_failure is null && !_lifecycle.IsCancellationRequested) _completion.TrySetResult(final);
        }
        return;
      }
      if (_pendingFrame is { } pending)
      {
        double offset = _offsetBox.Value;
        if (!double.IsFinite(offset) || offset != Math.Truncate(offset) || offset < 1 || offset > _offsetBox.Maximum)
        {
          _errorText.Text = "纠偏位移必须是范围内的整数像素。";
          return;
        }
        lock (_terminalGate)
        {
          if (!IsCurrentReview(pending)) return;
          _correctionAuthorization = new CorrectionAuthorization(pending, (int)offset);
          _confirmButton.IsEnabled = false;
          _offsetBox.IsEnabled = false;
        }
        return;
      }
      HideReview();
      _finishButton.IsEnabled = _stitcher is not null;
      // 复核/纠偏等待不属于动态变化，也不占用无进展窗口；恢复采集前丢弃旧见证
      // 并重新起算，避免长等待后立即触发动态超时或边界收尾。总时限保持不变。
      DiscardStableWitness();
      _lastProgress = Environment.TickCount64;
      _reviewPending = false;
      _statusText.Text = "滚动源窗口 · 完成后核对结果";
      SetForegroundWindow(_source);
    }

    private bool SendScroll()
    {
      var center = new Point { X = _bounds.X + _bounds.Width / 2, Y = _bounds.Y + _bounds.Height / 2 };
      bool sent = TrySendScrollInput(new NativeScrollInput(), center, _source, _direction);
      _scrollInputDiagnostic = sent ? "system wheel accepted" : "system wheel refused";
      return sent;
    }

    // XAML 输入管线不保证处理直接 SendMessage 的 WM_MOUSEWHEEL；使用系统鼠标输入。
    // 只在定位前和定位后均通过归属/按键检查时发送，且不覆盖用户在此期间的光标移动。
    private static bool TrySendScrollInput(IScrollInput input, Point center, nint source, ScrollCaptureDirection direction)
    {
      bool CanSend() => input.IsSourceAt(center, source) &&
          !new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12 }.Any(input.IsKeyDown);
      if (!CanSend() || !input.TryGetCursor(out Point original)) return false;
      try
      {
        if (!input.TrySetCursor(center) || !input.TryGetCursor(out Point positioned) ||
            positioned.X != center.X || positioned.Y != center.Y || !CanSend()) return false;
        bool horizontal = direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
        int delta = direction is ScrollCaptureDirection.Down or ScrollCaptureDirection.Left ? -40 : 40;
        return input.SendWheel(horizontal, delta);
      }
      finally
      {
        if (input.TryGetCursor(out Point current) && current.X == center.X && current.Y == center.Y)
          input.TrySetCursor(original);
      }
    }

    private interface IScrollInput
    {
      bool IsSourceAt(Point center, nint source);
      bool IsKeyDown(int key);
      bool TryGetCursor(out Point point);
      bool TrySetCursor(Point point);
      bool SendWheel(bool horizontal, int delta);
    }

    private sealed class NativeScrollInput : IScrollInput
    {
      public bool IsSourceAt(Point center, nint source)
      {
        nint target = WindowFromPoint(center);
        return target != 0 && GetAncestor(target, GaRoot) == source && GetForegroundWindow() == source;
      }
      public bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
      public bool TryGetCursor(out Point point) => GetCursorPos(out point);
      public bool TrySetCursor(Point point) => SetCursorPos(point.X, point.Y);
      public bool SendWheel(bool horizontal, int delta)
      {
        var input = new MouseInput
        {
          Data = unchecked((uint)delta),
          Flags = horizontal ? 0x1000u : 0x0800u, // MOUSEEVENTF_HWHEEL / MOUSEEVENTF_WHEEL
        };
        return SendInput(1, [new Input { Mouse = input }], Marshal.SizeOf<Input>()) == 1;
      }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
      public uint Type; // INPUT_MOUSE = 0; Mouse 是 INPUT 联合体中最大的成员。
      public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
      public int X, Y;
      public uint Data, Flags, Time;
      public nuint ExtraInfo;
    }

    // 不触碰真实桌面的输入边界自检，与预览门控共用既有子进程 seam。
    private static void VerifyScrollInputBoundaries()
    {
      static void Check(bool condition, string message)
      {
        if (!condition) throw new InvalidOperationException(message);
      }
      var center = new Point { X = -80, Y = 120 };
      foreach (ScrollCaptureDirection direction in Enum.GetValues<ScrollCaptureDirection>())
      {
        var input = new SyntheticScrollInput();
        Check(TrySendScrollInput(input, center, 1, direction), "Allowed system wheel must be sent.");
        Check(input.Horizontal == (direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right) &&
            input.Delta == (direction is ScrollCaptureDirection.Down or ScrollCaptureDirection.Left ? -40 : 40),
            "Wheel flags and signs must follow the selected direction.");
        Check(input.Position.X == 12 && input.Position.Y == 34, "Cursor must restore after accepted input.");
      }
      foreach (int key in new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12 })
      {
        var input = new SyntheticScrollInput { PressedKey = key };
        Check(!TrySendScrollInput(input, center, 1, ScrollCaptureDirection.Down) && input.Sends == 0 && input.Moves == 0,
            "Mouse buttons and wheel modifiers must reject input before moving the cursor.");
      }
      var refused = new SyntheticScrollInput { AcceptWheel = false };
      Check(!TrySendScrollInput(refused, center, 1, ScrollCaptureDirection.Down) && refused.Sends == 1 &&
          refused.Position.X == 12 && refused.Position.Y == 34, "Rejected injection must restore the cursor.");
      var switched = new SyntheticScrollInput { RejectAfterMove = true };
      Check(!TrySendScrollInput(switched, center, 1, ScrollCaptureDirection.Down) && switched.Sends == 0 &&
          switched.Position.X == 12, "Source change after positioning must prevent input and restore the cursor.");
      var clipped = new SyntheticScrollInput { ClipCursor = true };
      Check(!TrySendScrollInput(clipped, center, 1, ScrollCaptureDirection.Down) && clipped.Sends == 0,
          "Clipped cursor positioning must never send wheel input at the wrong location.");
      var moved = new SyntheticScrollInput { UserMoves = true };
      Check(TrySendScrollInput(moved, center, 1, ScrollCaptureDirection.Down) &&
          moved.Position.X == 56 && moved.Position.Y == 78, "User cursor movement must never be overwritten.");
    }

    private sealed class SyntheticScrollInput : IScrollInput
    {
      public Point Position = new() { X = 12, Y = 34 };
      public int PressedKey, Moves, Sends, Delta;
      public bool Horizontal, RejectAfterMove, UserMoves, ClipCursor;
      public bool AcceptWheel = true;
      public bool IsSourceAt(Point center, nint source) => !RejectAfterMove || Moves == 0;
      public bool IsKeyDown(int key) => key == PressedKey;
      public bool TryGetCursor(out Point point) { point = Position; return true; }
      public bool TrySetCursor(Point point)
      {
        Position = ClipCursor ? new Point { X = point.X + 1, Y = point.Y } : point;
        Moves++;
        return true;
      }
      public bool SendWheel(bool horizontal, int delta)
      {
        Sends++; Horizontal = horizontal; Delta = delta;
        if (UserMoves) Position = new Point { X = 56, Y = 78 };
        return AcceptWheel;
      }
    }

    private static Session CreateSelfCheckSession(int width = 48, int height = 96)
    {
      var session = new Session(1, new PhysicalRectangle(0, 0, width, height), CancellationToken.None)
      {
        _window = new Window(),
        _statusText = new TextBlock(),
        _errorText = new TextBlock(),
        _previewImage = new Image(),
        _pendingImage = new Image(),
        _offsetBox = new NumberBox(),
        _finishButton = new Button(),
        _cancelButton = new Button(),
        _confirmButton = new Button { IsEnabled = true },
      };
      session._window.Content = session._previewImage;
      return session;
    }

    // 此自检使用真实 WinUI 控件与解码，阻塞上一张预览来验证确认门控，不需要桌面前台权限。
    internal static async Task VerifyPreviewGateSelfCheckAsync()
    {
      VerifyScrollInputBoundaries();
      static void Check(bool condition, string message)
      {
        if (!condition) throw new InvalidOperationException(message);
      }
      byte[] pixels = new byte[48 * 96 * 4];
      for (int y = 0; y < 96; y++) pixels[y * 48 * 4] = (byte)y;
      var frame = new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      var final = CreateSelfCheckSession();
      var pending = CreateSelfCheckSession();
      var first = CreateSelfCheckSession();
      var manual = CreateSelfCheckSession();
      var witness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      try
      {
        final._previewTask = witness.Task;
        final.CompleteBuild(new VerticalScrollStitcher(frame));
        Check(final._confirmButton.Visibility == Visibility.Visible && !final._confirmButton.IsEnabled,
            "Final confirmation must disable immediately, even if the preceding preview enabled it.");
        final.OnConfirm(final._confirmButton, new RoutedEventArgs());
        Check(!final._completion.Task.IsCompleted, "An unfinished preview must not publish its frame.");
        witness.SetResult();
        await final._previewTask;
        Check(final._previewImage.Source is not null && final._confirmButton.IsEnabled,
            "Only the decoded final preview may enable confirmation.");
        final.OnConfirm(final._confirmButton, new RoutedEventArgs());
        Check(final._failure is not null && !final._completion.Task.IsCompletedSuccessfully,
            "Even a decoded final preview must reject confirmation when its source window no longer exists.");

        manual._lastStableWitness = Environment.TickCount64;
        await manual.ObserveFrameAsync(frame);
        await manual.ObserveFrameAsync(frame);
        await manual._previewTask;
        Check(!manual._reviewPending && manual._finishButton.IsEnabled && manual._stitcher?.FrameCount == 1,
            "The first stable frame must be accepted directly without an initial preview review.");
        await manual.ObserveFrameAsync(frame);
        await manual.ObserveFrameAsync(frame);
        Check(manual._finishButton.IsEnabled && manual._stitcher?.FrameCount == 1,
            "Duplicate stable frames must retain single-frame completion without increasing the frame count.");
        manual.OnFinish(manual._finishButton, new RoutedEventArgs());
        Check(manual._finishRequested, "The enabled finish action must request final sampling.");
        var invalid = new CapturedFrame([], 48, 96, 48 * 4, "BGRA8");
        first.ReportAccepted(new VerticalScrollStitcher(frame), invalid);
        first.PauseForReview("synthetic initial frame", null);
        await first._previewTask;
        Check(first._failure is not null && first._errorText.Visibility == Visibility.Visible &&
            !first._confirmButton.IsEnabled && first._confirmButton.Visibility == Visibility.Collapsed,
            "An initial preview encoding failure must not reopen confirmation.");

        pending.PauseForReview("synthetic pending frame", invalid);
        await pending._previewTask;
        Check(pending._failure is not null && pending._errorText.Visibility == Visibility.Visible &&
            pending._errorText.Text.Contains("无法生成待拼预览", StringComparison.Ordinal),
            "A pending-frame encoding failure must produce a visible failure.");
        Check(!pending._confirmButton.IsEnabled && pending._confirmButton.Visibility == Visibility.Collapsed,
            "A failed pending preview must keep confirmation closed.");
        pending.OnConfirm(pending._confirmButton, new RoutedEventArgs());
        Check(!pending._completion.Task.IsCompleted && pending._correctionAuthorization is null,
            "A failed preview must neither publish output nor accept a correction.");
      }
      finally
      {
        witness.TrySetResult();
        foreach (Session session in new[] { final, pending, first, manual })
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._previewImage.Source = null;
          session._window.Close();
          session._lifecycle.Dispose();
        }
      }
    }
    internal static async Task VerifyAcceptedPreviewAllocationSelfCheckAsync()
    {
      const int side = 2048;
      byte[] pixels = new byte[side * side * 4];
      for (int y = 0; y < side; y++) pixels[y * side * 4] = (byte)y;
      var frame = new CapturedFrame(pixels, side, side, side * 4, "BGRA8");
      var stitcher = new VerticalScrollStitcher(frame);
      var session = CreateSelfCheckSession(side, side);
      var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      try
      {
        session._previewTask = busy.Task;
        long before = GC.GetAllocatedBytesForCurrentThread();
        session.ReportAccepted(stitcher, frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AppLog.Info($"scroll-capture busy preview allocation self-check: {allocated} bytes");
        if (allocated > 65536 || !ReferenceEquals(session._previewTask, busy.Task))
          throw new InvalidOperationException($"A busy renderer must skip preview generation before allocation; allocated={allocated} bytes.");
        busy.SetResult();
        Task.Run(() => session.ReportAccepted(stitcher, frame)).GetAwaiter().GetResult();
        if (!session._acceptedPreviewQueued) throw new InvalidOperationException("The worker preview must be queued until the UI can present it.");
        before = GC.GetAllocatedBytesForCurrentThread();
        session.ReportAccepted(stitcher, frame);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 65536) throw new InvalidOperationException("A queued preview must also block further generation.");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session._window.DispatcherQueue.TryEnqueue(() => drained.SetResult()))
          throw new InvalidOperationException("Could not drain the accepted preview callback.");
        await drained.Task;
        await session._previewTask;
        session._fixedLeading = 256;
        session._fixedTrailing = 128;
        CapturedFrame unmarked = stitcher.BuildPreview(PreviewMaxDecodePx);
        foreach (ScrollCaptureDirection direction in new[] { ScrollCaptureDirection.Down, ScrollCaptureDirection.Right })
        {
          session._direction = direction;
          CapturedFrame marked = session.MarkFixedEdges(unmarked, side, side);
          int end = direction == ScrollCaptureDirection.Down ? (marked.Height - 1) * marked.Stride : (marked.Width - 1) * 4;
          int middle = marked.Height / 2 * marked.Stride + marked.Width / 2 * 4;
          if (marked.Pixels[2] == 0 || marked.Pixels[end + 2] == 0 || unmarked.Pixels[2] != 0 ||
              !marked.Pixels.AsSpan(middle, 4).SequenceEqual(unmarked.Pixels.AsSpan(middle, 4)))
            throw new InvalidOperationException("Thumbnail fixed-edge marks must follow physical source coordinates without changing body/source pixels.");
        }
        session._direction = ScrollCaptureDirection.Down;
        before = GC.GetAllocatedBytesForCurrentThread();
        session.ReportAccepted(stitcher, frame);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 3 * PreviewMaxDecodePx * PreviewMaxDecodePx * 4 + 65536)
          throw new InvalidOperationException($"Accepted preview allocation must depend on the thumbnail bound, not source area; allocated={allocated} bytes.");
        await session._previewTask;
        if (session._previewImage.Source is not BitmapImage bitmap ||
            bitmap.PixelWidth > PreviewMaxDecodePx || bitmap.PixelHeight > PreviewMaxDecodePx)
          throw new InvalidOperationException("The encoded accepted preview itself must be bounded, not only its decode request.");
      }
      finally
      {
        busy.TrySetResult();
        session._lifecycle.Cancel();
        await session._previewTask;
        session._previewImage.Source = null;
        session._window.Close();
        session._lifecycle.Dispose();
      }
    }

    internal static async Task VerifyPreviewResumeStableWindowSelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      static CapturedFrame RepaintingFrame(int top)
      {
        CapturedFrame frame = Frame(top);
        frame.Pixels[0] ^= 0xA5; // 重绘像素破坏真实重叠，不能被当作滚动进展接收。
        return frame;
      }
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession() };
      try
      {
        foreach (Session session in sessions)
        {
          session._lastStableWitness = Environment.TickCount64;
          await session.ObserveFrameAsync(Frame(32));
          await session.ObserveFrameAsync(Frame(32));
          await session._previewTask;
          if (session._failure is not null || session._reviewPending || session._stitcher is null ||
              !session._finishButton.IsEnabled)
            throw new InvalidOperationException("The first stable frame must enter the stitcher directly.", session._failure);
          if (ReferenceEquals(session, sessions[1]))
          {
            // 反向帧复核是现存的恢复点：确认后继续必须拿到全新的动态窗口。
            await session.ObserveFrameAsync(Frame(12));
            await session.ObserveFrameAsync(Frame(12));
            await session._previewTask;
            if (!Equals(session._confirmButton.Content, "丢弃反向帧并继续"))
              throw new InvalidOperationException("The recovery case must pause on a real reverse-frame review.");
          }
          else
          {
            // 自动滚动未前进等暂停复核与首帧直达采集共存：确认继续同样要重置动态窗口。
            session.PauseForReview("synthetic pause review", null);
            await session._previewTask;
          }
          session._lastStableWitness = Environment.TickCount64 - DynamicContentTimeoutMs - 1000;
          long progressBefore = session._lastProgress = Environment.TickCount64 - NoProgressTimeoutMs + 1000;
          session._elapsed.Start();
          await Task.Delay(20);
          long elapsedBefore = session._elapsed.ElapsedTicks;
          long confirmedAt = Environment.TickCount64;
          session.OnConfirm(session, new RoutedEventArgs());
          if (session._reviewPending)
            throw new InvalidOperationException("Confirmation must resume capture from the review pause.");
          await session.ObserveFrameAsync(RepaintingFrame(52));
          if (session._failure is not null)
            throw new InvalidOperationException("A changed frame immediately after a long review must get a fresh dynamic window.", session._failure);
          if (session._lastStableWitness < confirmedAt || session._stableStreak != 0)
            throw new InvalidOperationException("Confirmation must discard the pre-review frame and start the full stable-frame window.");
          if (session._lastProgress <= progressBefore || !session._elapsed.IsRunning || session._elapsed.ElapsedTicks < elapsedBefore)
            throw new InvalidOperationException("Review resume must restart the no-progress budget while preserving the total capture budget.");
          if (session.NoProgressExpired())
            throw new InvalidOperationException("A just-resumed capture must not immediately hit the no-progress boundary.");
          session._lastStableWitness = Environment.TickCount64 - DynamicContentTimeoutMs + 1000;
          await session.ObserveFrameAsync(RepaintingFrame(53));
          if (session._failure is not null)
            throw new InvalidOperationException("Changing content inside the fresh five-second window must be allowed.");
          session._lastStableWitness = Environment.TickCount64 - DynamicContentTimeoutMs - 1;
          await session.ObserveFrameAsync(RepaintingFrame(54));
          if (session._failure is not InvalidDataException)
            throw new InvalidOperationException("Changing content beyond the same five-second window must still stop.");
        }
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._previewImage.Source = null;
          session._window.Close();
          session._lifecycle.Dispose();
        }
      }
    }

    internal static async Task VerifyPreviewPlacementSelfCheckAsync()
    {
      PhysicalRectangle work = GetWorkArea(DesktopScreenQuery.GetPrimaryMonitor());
      byte[] pixels = new byte[48 * 96 * 4];
      for (int y = 0; y < 96; y++) pixels[y * 48 * 4] = (byte)y;
      var frame = new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      byte[] bmp = EncodePreviewBmp(frame) ?? throw new InvalidOperationException("Preview fixture encoding failed.");
      string[] stages = ["paused", "pending", "final", "confirm"];
      Session[] sessions = stages.Select(_ => CreateSelfCheckSession()).ToArray();
      try
      {
        foreach ((string stage, Session session) in stages.Zip(sessions))
        {
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.begin");
          session._window.Content = new Grid();
          ConfigureOverlayWindow(session._window);
          session._controlHandle = WinRT.Interop.WindowNative.GetWindowHandle(session._window);
          session._window.AppWindow.MoveAndResize(new RectInt32(work.X + 20, work.Y + 20, 200, 60));
          session._sourceDpi = GetDpiForWindow(session._controlHandle);
          session._bounds = new PhysicalRectangle(work.X + work.Width / 4, work.Y + work.Height / 4, 200, 200);
          var previews = new StackPanel();
          previews.Children.Add(session._previewImage);
          previews.Children.Add(session._pendingImage);
          session._previewWindow = new Window { Content = previews };
          ConfigureOverlayWindow(session._previewWindow);
          session._previewHandle = WinRT.Interop.WindowNative.GetWindowHandle(session._previewWindow);
          session._reviewPending = true;
          session._confirmButton.IsEnabled = false;
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.decode");
          await session.RenderPreviewAsync(bmp, frame.Width, frame.Height);
          await session.RenderReviewPreviewAsync(Task.CompletedTask, null, null);
          if (session._failure is not null || !IsWindowVisible(session._previewHandle) ||
              session._previewImage.Source is null || !session._confirmButton.IsEnabled)
            throw new InvalidOperationException("A decoded preview with legal real HWND geometry must enable paused review confirmation.");

          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.visible");
          // 工具栏仍有自己的矩形，但工作区已无任何可放预览的选区外空间。
          session._bounds = work;
          session._confirmButton.IsEnabled = false;
          if (stage == "paused") await session.RenderReviewPreviewAsync(Task.CompletedTask, null, null);
          else if (stage == "pending")
          {
            session._pendingFrame = frame;
            session._pendingImage.Visibility = Visibility.Visible;
            await session.RenderReviewPreviewAsync(Task.CompletedTask, bmp, frame);
          }
          else if (stage == "final") await session.RenderFinalPreviewAsync(Task.CompletedTask, bmp, frame);
          else
          {
            session._finalFrame = frame;
            session._confirmButton.IsEnabled = true;
            session.OnConfirm(session, new RoutedEventArgs());
          }
          if (session._failure is null || !session._errorText.Text.Contains("预览空间", StringComparison.Ordinal) ||
              session._confirmButton.IsEnabled || session._finishButton.IsEnabled ||
              IsWindowVisible(session._previewHandle) || session._completion.Task.IsCompleted || session._correctionAuthorization is not null)
            throw new InvalidOperationException($"An invisible {stage} preview must fail closed with shrink-selection guidance.");
          session.OnConfirm(session, new RoutedEventArgs());
          if (session._completion.Task.IsCompleted) throw new InvalidOperationException("Hidden preview confirmation returned output.");
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.rejected");
        }
      }
      finally
      {
        foreach ((string stage, Session session) in stages.Zip(sessions))
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._previewWindow?.Close();
          session._window.Close();
          session._lifecycle.Dispose();
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.closed");
        }
      }
    }

    // 回归：30 秒无新增不再把已收合法选段连同错误一起丢弃（原报错“30 秒没有新增有效内容…未生成长图”）。
    internal static async Task VerifyNoProgressBoundarySelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession(), CreateSelfCheckSession(), CreateSelfCheckSession() };
      try
      {
        Session boundary = sessions[0];
        boundary._lastStableWitness = Environment.TickCount64;
        await boundary.ObserveFrameAsync(Frame(32));
        await boundary.ObserveFrameAsync(Frame(32));
        await boundary._previewTask;
        if (boundary._failure is not null || boundary._reviewPending || boundary._stitcher?.FrameCount != 1)
          throw new InvalidOperationException("The boundary regression must start from an accepted single-frame segment.", boundary._failure);
        boundary._lastProgress = Environment.TickCount64 - NoProgressTimeoutMs - 1;
        boundary.HandleNoProgressBoundary();
        if (boundary._failure is not null || boundary._finalFrame is not null || boundary._reviewPending ||
            !boundary._completion.Task.IsCompletedSuccessfully)
          throw new InvalidOperationException(
              "The clean no-progress boundary must return the accepted segment without another preview confirmation.",
              boundary._failure);
        CapturedFrame? output = await boundary._completion.Task;
        if (output?.Pixels.SequenceEqual(Frame(32).Pixels) != true)
          throw new InvalidOperationException("The confirmed boundary segment must return exactly the accepted pixels.");

        Session idle = sessions[1];
        idle._lastProgress = Environment.TickCount64 - NoProgressTimeoutMs - 1;
        idle.HandleNoProgressBoundary();
        if (idle._failure is null || !idle._errorText.Text.Contains("首帧", StringComparison.Ordinal) ||
            idle._finalFrame is not null)
          throw new InvalidOperationException("Without any accepted frame the boundary stop must still report a real first-frame failure.");

        Session reviewing = sessions[2];
        reviewing._lastStableWitness = Environment.TickCount64;
        await reviewing.ObserveFrameAsync(Frame(32));
        await reviewing.ObserveFrameAsync(Frame(32));
        await reviewing._previewTask;
        reviewing.PauseForReview("synthetic review wait", null);
        reviewing._lastProgress = Environment.TickCount64 - NoProgressTimeoutMs - 1;
        if (reviewing._failure is not null)
          throw new InvalidOperationException("The synthetic review pause must succeed.", reviewing._failure);
        if (reviewing.NoProgressExpired())
          throw new InvalidOperationException("Waiting for an explicit user decision must pause the no-progress boundary.");

        Session missing = sessions[3];
        missing._lastStableWitness = Environment.TickCount64;
        await missing.ObserveFrameAsync(Frame(32));
        await missing.ObserveFrameAsync(Frame(32));
        await missing._previewTask;
        missing._overlapMissing = true;
        missing.HandleNoProgressBoundary();
        await missing._previewTask;
        if (missing._failure is not null || missing._finalFrame is null || !missing._reviewPending ||
            missing._completion.Task.IsCompleted || !missing._confirmButton.IsEnabled)
          throw new InvalidOperationException("A missing overlap must require explicit confirmation before returning the accepted segment.", missing._failure);
        missing.OnConfirm(missing._confirmButton, new RoutedEventArgs());
        if (missing._failure is null || missing._completion.Task.IsCompletedSuccessfully)
          throw new InvalidOperationException("Final confirmation must recheck the source after the capture loop has stopped.");
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._previewImage.Source = null;
          session._window.Close();
          session._lifecycle.Dispose();
        }
      }
    }

    internal static void VerifySelectionGeometrySelfCheck()
    {
      ScrollCaptureMask.VerifySelfCheck();
      var boundary = new Session(1, new PhysicalRectangle(-350, -150, 300, 300), CancellationToken.None)
      {
        _sourceRect = new PhysicalRectangle(-500, -300, 1200, 900),
        _desktop = new PhysicalRectangle(-400, -200, 800, 600),
        _widthBox = new NumberBox { Value = 800 },
        _heightBox = new NumberBox { Value = 300 },
        _errorText = new TextBlock(),
      };
      try
      {
        PhysicalRectangle accepted = boundary._bounds;
        if (!boundary.SelectionFitsCaptureArea(accepted)) throw new InvalidOperationException("Valid negative-coordinate selections must remain supported.");
        boundary.ApplySelectionDimensions();
        if (boundary._bounds != accepted || boundary._errorText.Visibility != Visibility.Visible)
          throw new InvalidOperationException("Resizing into a source window's off-desktop area must be rejected before any capture/window mutation.");
        foreach (PhysicalRectangle outside in new[]
        {
          new PhysicalRectangle(-350, -150, 800, 300),
          new PhysicalRectangle(-350, -150, 300, 600),
          new PhysicalRectangle(-450, -150, 100, 100),
          new PhysicalRectangle(-350, -250, 100, 100),
        })
          if (boundary.SelectionFitsCaptureArea(outside)) throw new InvalidOperationException("Every desktop edge must bound resizing even when the source window extends beyond it.");
        boundary._sourceRect = new PhysicalRectangle(-300, -100, 400, 300);
        if (boundary.SelectionFitsCaptureArea(accepted)) throw new InvalidOperationException("The source window boundary must also remain enforced.");
      }
      finally { boundary._lifecycle.Dispose(); }
      foreach ((int width, int height, ScrollCaptureDirection direction) in new[]
          { (200, 40, ScrollCaptureDirection.Right), (40, 200, ScrollCaptureDirection.Down) })
      {
        var session = new Session(1, new PhysicalRectangle(0, 0, width, height), CancellationToken.None);
        try
        {
          session.ValidateSelectionSize();
          byte[] pixels = new byte[width * height * 4];
          for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[(y * width + x) * 4] = (byte)(x + y);
          _ = new VerticalScrollStitcher(new CapturedFrame(pixels, width, height, width * 4, "BGRA8"), direction);
          session._directionSegments = session.CreateDirectionSegments();
          session._leadingBox = new NumberBox { Value = 0 };
          session._trailingBox = new NumberBox { Value = 0 };
          session._errorText = new TextBlock();
          // 分段方向与拼接轴垂直：短轴不足 64px 必须在启动前被拒绝。
          session.StartWithDirection(direction == ScrollCaptureDirection.Right ? 0 : 2);
          if (session._loop is not null || !session._errorText.Text.Contains("64", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected scroll axis must still leave at least 64 pixels before capture starts.");
        }
        finally { session._lifecycle.Dispose(); }
      }
      foreach ((int width, int height) in new[] { (31, 200), (200, 31), (32769, 32), (4000, 2001) })
      {
        var session = new Session(1, new PhysicalRectangle(0, 0, width, height), CancellationToken.None);
        try
        {
          try { session.ValidateSelectionSize(); }
          catch (InvalidOperationException) { continue; }
          throw new InvalidOperationException("Common minimum side and resource limits must remain enforced.");
        }
        finally { session._lifecycle.Dispose(); }
      }
    }

    internal static async Task VerifyReverseRecoverySelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      var session = CreateSelfCheckSession();
      try
      {
        session._automatic = true;
        session._lastStableWitness = Environment.TickCount64;
        await session.ObserveFrameAsync(Frame(32));
        await session.ObserveFrameAsync(Frame(32));
        await session._previewTask;
        CapturedFrame reverse = Frame(12);
        await session.ObserveFrameAsync(reverse);
        await session.ObserveFrameAsync(reverse);
        await session._previewTask;
        if (session._pendingFrame is not null || session._offsetBox.Visibility != Visibility.Collapsed ||
            !Equals(session._confirmButton.Content, "丢弃反向帧并继续") || !session._reviewPending || session._automatic)
          throw new InvalidOperationException("A reverse frame must offer explicit discard/continue, never impossible positive correction.");
        if (session._stitcher?.FrameCount != 1 || !session._stitcher.BuildFrame().Pixels.SequenceEqual(Frame(32).Pixels))
          throw new InvalidOperationException("Reverse review must not change the accepted segment.");
        session.OnConfirm(session, new RoutedEventArgs());
        await session.ObserveFrameAsync(reverse);
        await session.ObserveFrameAsync(reverse);
        if (session._reviewPending || session._pendingFrame is not null || session._completion.Task.IsCompleted)
          throw new InvalidOperationException("Discarding a reverse frame must resume manual capture without re-prompting on the same stable frame.");
        await session.ObserveFrameAsync(Frame(52));
        await session.ObserveFrameAsync(Frame(52));
        await session._previewTask;
        if (session._stitcher.FrameCount != 2 || session._stitcher.Height != 116)
          throw new InvalidOperationException("Forward scrolling after discard must append to the retained accepted segment.");
      }
      finally
      {
        session._lifecycle.Cancel();
        await session._previewTask;
        session._window.Close();
        session._lifecycle.Dispose();
      }
    }

    internal static async Task VerifyPendingPreviewBoundsSelfCheckAsync()
    {
      // 所有用例先创建窗口，避免关闭最后窗口后在下一次异步解码期间退出 App。
      var sessions = new[] { CreateSelfCheckSession(32, 2160), CreateSelfCheckSession(2160, 32), CreateSelfCheckSession(32, 96) };
      try
      {
        foreach (Session session in sessions)
        {
          int width = session._bounds.Width, height = session._bounds.Height;
          var frame = new CapturedFrame(new byte[width * height * 4], width, height, width * 4, "BGRA8");
          session.PauseForReview("synthetic pending preview bounds", frame);
          await session._previewTask;
          BitmapImage? bitmap = session._pendingImage.Source as BitmapImage;
          // PixelWidth/Height 在此报告源尺寸；解码约束由 DecodePixelWidth/Height 表达。
          if (session._failure is not null || bitmap is null ||
              bitmap.DecodePixelWidth < 1 || bitmap.DecodePixelWidth > Math.Min(width, PreviewMaxDecodePx) ||
              bitmap.DecodePixelHeight < 1 || bitmap.DecodePixelHeight > Math.Min(height, PreviewMaxDecodePx) ||
              bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            throw new InvalidOperationException(
                $"Pending preview must bound both decode dimensions by the longest edge without enlarging small frames. " +
                $"source={width}x{height}, decode={bitmap?.DecodePixelWidth}x{bitmap?.DecodePixelHeight}, " +
                $"pixels={bitmap?.PixelWidth}x{bitmap?.PixelHeight}, failure={session._failure?.Message}");
        }
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._pendingImage.Source = null;
          session._window.Close();
          session._lifecycle.Dispose();
        }
      }
    }

    internal static async Task VerifyContinuousScrollRecoverySelfCheckAsync()
    {
      static CapturedFrame Frame(int top, int height = 96)
      {
        byte[] pixels = new byte[48 * height * 4];
        for (int y = 0; y < height; y++)
        {
          pixels[y * 48 * 4] = (byte)(top + y);
          pixels[y * 48 * 4 + 1] = (byte)((top + y) >> 8);
        }
        return new CapturedFrame(pixels, 48, height, 48 * 4, "BGRA8");
      }
      var session = CreateSelfCheckSession();
      try
      {
        session._lastStableWitness = Environment.TickCount64;
        await session.ObserveFrameAsync(Frame(0));
        await session.ObserveFrameAsync(Frame(0));
        await session._previewTask;
        // 连续滚动没有两张相同帧，但每步有经完整校验的重叠。
        foreach (int top in new[] { 40, 80, 120 }) await session.ObserveFrameAsync(Frame(top));
        await session._previewTask;
        if (session._stitcher?.FrameCount != 4 ||
            !session._stitcher.BuildFrame().Pixels.SequenceEqual(Frame(0, 216).Pixels))
          throw new InvalidOperationException("Continuous overlapping scroll frames were skipped before settling.");
        CapturedFrame accepted = session._stitcher.BuildFrame();
        await session.ObserveFrameAsync(Frame(280));
        await session.ObserveFrameAsync(Frame(280));
        await session._previewTask;
        if (session._failure is not null || session._finishButton.IsEnabled ||
            !session._stitcher.BuildFrame().Pixels.SequenceEqual(accepted.Pixels))
          throw new InvalidOperationException("A gap must remain recoverable without changing accepted pixels or allowing completion.");
        session.OnFinish(session, new RoutedEventArgs());
        if (session._finishRequested || session._completion.Task.IsCompleted)
          throw new InvalidOperationException("An unresolved gap must never publish an incomplete long image.");
        // 回滚到与最后接收位置 120 重叠的位置，再向前连接；280 没有被伪造加入。
        await session.ObserveFrameAsync(Frame(160));
        await session.ObserveFrameAsync(Frame(200));
        await session.ObserveFrameAsync(Frame(240));
        await session.ObserveFrameAsync(Frame(280));
        await session._previewTask;
        if (session._failure is not null || !session._finishButton.IsEnabled ||
            !session._stitcher.BuildFrame().Pixels.SequenceEqual(Frame(0, 376).Pixels))
          throw new InvalidOperationException("Returning to an overlapping frame must safely resume and fill every missing strip.");
        session.CancelFromUi();
        if (await session._completion.Task is not null)
          throw new InvalidOperationException("Cancellation must discard recovered output.");
      }
      finally
      {
        session._lifecycle.Cancel();
        await session._previewTask;
        session._window.Close();
        session._lifecycle.Dispose();
      }
    }

    internal static async Task VerifyFirstFailureSelfCheckAsync()
    {
      var session = CreateSelfCheckSession();
      try
      {
        var original = new InvalidOperationException("源窗口已关闭，已停止采集。");
        session.Fail(original);
        session.Fail(new InvalidOperationException("已到 3 分钟总时限（包含预览与纠偏等待），本次未完成，未生成长图。"));
        if (!ReferenceEquals(session._failure, original) || session._errorText.Text != original.Message)
          throw new InvalidOperationException("A late deadline must not overwrite the first capture failure.");
        await Task.Run(() => Parallel.For(0, 16, index =>
            session.Fail(new InvalidOperationException($"concurrent late failure {index}"))));
        session.RunOnUi(() =>
        {
          session._errorText.Text = "late queued correction message";
          session._finishButton.IsEnabled = true;
        });
        if (!ReferenceEquals(session._failure, original) || session._errorText.Text != original.Message)
          throw new InvalidOperationException("Concurrent later failures must retain the first failure and its visible explanation.");
        session.OnConfirm(session, new RoutedEventArgs());
        session.OnFinish(session, new RoutedEventArgs());
        if (session._completion.Task.IsCompleted || session._finishButton.IsEnabled || session._confirmButton.IsEnabled)
          throw new InvalidOperationException("Terminal failure must keep output actions closed.");
        session.CancelFromUi();
        try { await session._completion.Task; throw new InvalidOperationException("Failure returned output."); }
        catch (Exception error) when (ReferenceEquals(error, original)) { }
      }
      finally
      {
        session._lifecycle.Cancel();
        await session._previewTask;
        session._window.Close();
        session._lifecycle.Dispose();
      }
    }

    internal static async Task VerifyAmbiguousCorrectionsSelfCheckAsync()
    {
      static CapturedFrame Periodic(int width, int height, int offset, bool horizontal)
      {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
          for (int x = 0; x < width; x++) pixels[(y * width + x) * 4] = (byte)(((horizontal ? x : y) + offset) % 8);
        return new CapturedFrame(pixels, width, height, width * 4, "BGRA8");
      }
      // 所有测试窗口先创建、统一最后关闭，避免两次 await 之间关闭最后窗口而退出 App。
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession(32764, 32) };
      try
      {
        for (int index = 0; index < sessions.Length; index++)
        {
          bool horizontal = index == 1;
          int width = horizontal ? 32764 : 48, height = horizontal ? 32 : 96;
          int acceptedOffset = horizontal ? 2 : 10, rejectedOffset = horizontal ? 10 : 11;
          Session session = sessions[index];
          CapturedFrame first = Periodic(width, height, 0, horizontal);
          CapturedFrame pending = Periodic(width, height, 2, horizontal);
          session._direction = horizontal ? ScrollCaptureDirection.Right : ScrollCaptureDirection.Down;
          session._lastStableWitness = Environment.TickCount64;
          await session.ObserveFrameAsync(first);
          await session.ObserveFrameAsync(first);
          await session._previewTask;
          await session.ObserveFrameAsync(pending);
          await session.ObserveFrameAsync(pending);
          await session._previewTask;
          if (!ReferenceEquals(session._pendingFrame, pending) || !session._reviewPending ||
              session._offsetBox.Visibility != Visibility.Visible || session._finishButton.IsEnabled)
            throw new InvalidOperationException("Feasible ambiguous matches must expose only the explicit verified correction path.");
          session._offsetBox.Value = 0;
          session.OnConfirm(session, new RoutedEventArgs());
          if (session._correctionAuthorization is not null) throw new InvalidOperationException("Invalid offsets must not reach the matcher.");
          session._offsetBox.Value = rejectedOffset;
          session.OnConfirm(session, new RoutedEventArgs());
          session.ApplyConfirmedOffset();
          if (session._failure is not null || !session._reviewPending || session._stitcher?.FrameCount != 1 ||
              !ReferenceEquals(session._pendingFrame, pending) || !session._stitcher.BuildFrame().Pixels.SequenceEqual(first.Pixels))
            throw new InvalidOperationException("A wrong or oversized choice must preserve ambiguous review and accepted pixels.");
          session._offsetBox.Value = acceptedOffset;
          session.OnConfirm(session, new RoutedEventArgs());
          session.ApplyConfirmedOffset();
          await session._previewTask;
          if (session._failure is not null || session._reviewPending || session._pendingFrame is not null ||
              session._stitcher.FrameCount != 2 || !session._finishButton.IsEnabled || session._completion.Task.IsCompleted)
            throw new InvalidOperationException("Another feasible correction must resume capture without publishing unconfirmed output.");
        }
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._window.Close();
          session._lifecycle.Dispose();
        }
      }
    }

    internal static async Task VerifyCorrectionAuthorizationSelfCheckAsync()
    {
      static CapturedFrame Periodic(int height, int phase)
      {
        byte[] pixels = new byte[48 * height * 4];
        for (int y = 0; y < height; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)((y + phase) % 8);
        return new CapturedFrame(pixels, 48, height, 48 * 4, "BGRA8");
      }
      static async Task DrainUiAsync(Session session)
      {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session._window.DispatcherQueue.TryEnqueue(() => drained.SetResult()))
          throw new InvalidOperationException("Could not drain correction UI callbacks.");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session._previewTask;
      }
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession(), CreateSelfCheckSession() };
      Session session = sessions[0];
      var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task worker = Task.CompletedTask;
      try
      {
        session._lastStableWitness = Environment.TickCount64;
        await session.ObserveFrameAsync(Periodic(96, 0));
        await session.ObserveFrameAsync(Periodic(96, 0));
        await session._previewTask;
        await session.ObserveFrameAsync(Periodic(96, 2));
        await session.ObserveFrameAsync(Periodic(96, 2));
        await session._previewTask;
        session._offsetBox.Value = 10;
        session.OnConfirm(session, new RoutedEventArgs());
        worker = Task.Run(() => session.ApplyConfirmedOffset(afterClaimForTest: () =>
        {
          claimed.SetResult();
          release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }));
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.OnConfirm(session, new RoutedEventArgs()); // 匹配过程中重复点击同一个确认。
        release.SetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        await DrainUiAsync(session);
        CapturedFrame accepted = session._stitcher!.BuildFrame();
        CapturedFrame next = Periodic(96, 4);
        await session.ObserveFrameAsync(next);
        await session.ObserveFrameAsync(next);
        await session._previewTask;
        session.ApplyConfirmedOffset(); // B 尚未确认，不能消费 A 的重复点击。
        CapturedFrame unconfirmed = session._stitcher.BuildFrame();
        if (session._stitcher.FrameCount != 2 || unconfirmed.Height != 106 ||
            !unconfirmed.Pixels.SequenceEqual(accepted.Pixels) || !session._reviewPending ||
            !ReferenceEquals(session._pendingFrame, next) || !session._confirmButton.IsEnabled ||
            session._completion.Task.IsCompleted)
          throw new InvalidOperationException($"A repeated confirmation leaked to the next frame: height={unconfirmed.Height}, frames={session._stitcher.FrameCount}.");
        session._offsetBox.Value = 2;
        session.OnConfirm(session, new RoutedEventArgs());
        session.ApplyConfirmedOffset();
        await session._previewTask;
        CapturedFrame confirmed = session._stitcher.BuildFrame();
        if (session._stitcher.FrameCount != 3 || confirmed.Height != 108 ||
            !confirmed.Pixels.SequenceEqual(Periodic(108, 0).Pixels) || session._reviewPending ||
            session._pendingFrame is not null || session._completion.Task.IsCompleted)
          throw new InvalidOperationException("Only B's own confirmed offset may extend the accepted segment to 108 pixels.");
        for (int index = 1; index < sessions.Length; index++)
        {
          session = sessions[index];
          session._lastStableWitness = Environment.TickCount64;
          await session.ObserveFrameAsync(Periodic(96, 0));
          await session.ObserveFrameAsync(Periodic(96, 0));
          await session._previewTask;
          await session.ObserveFrameAsync(Periodic(96, 2));
          await session.ObserveFrameAsync(Periodic(96, 2));
          await session._previewTask;
          session._offsetBox.Value = 10;
          session.OnConfirm(session, new RoutedEventArgs());
          claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          worker = Task.Run(() => session.ApplyConfirmedOffset(afterMatchForTest: () =>
          {
            claimed.SetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
          }));
          await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
          var original = new InvalidOperationException("source disappeared while correction was matching");
          if (index == 1) session.CancelFromUi();
          else session.Fail(original);
          release.SetResult();
          await worker.WaitAsync(TimeSpan.FromSeconds(5));
          await DrainUiAsync(session);
          if (session._correctionAuthorization is not null || session._confirmButton.IsEnabled ||
              session._finishButton.IsEnabled || !session._reviewPending || session._pendingFrame is null)
            throw new InvalidOperationException("A stopped correction must not resume capture or enable stale output actions.");
          if (index == 1)
          {
            if (await session._completion.Task is not null)
              throw new InvalidOperationException("Cancellation while matching must discard all captured output.");
          }
          else
          {
            if (!ReferenceEquals(session._failure, original) || session._errorText.Text != original.Message)
              throw new InvalidOperationException("A matching completion must retain the original visible failure.");
            session.CancelFromUi();
            try { await session._completion.Task; throw new InvalidOperationException("Failed correction returned output."); }
            catch (Exception error) when (ReferenceEquals(error, original)) { }
          }
        }
      }
      finally
      {
        release.TrySetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Session owned in sessions)
        {
          owned._lifecycle.Cancel();
          await owned._previewTask;
          owned._window.Close();
          owned._lifecycle.Dispose();
        }
      }
    }

    private void SetReadyUi()
    {
      _statusText.Text = "选择滚动方向后立即开始自动滚动采集；如需手动滚动或调整宽高/固定边缘，请先打开“设置”。";
    }

    /// <summary>分段控制器回调：方向是唯一必选项，选定并校验后直达采集循环。</summary>
    private void OnDirectionPicked(object sender, RoutedEventArgs args)
    {
      StartWithDirection(Array.IndexOf(_directionSegments, sender));
    }

    private void StartWithDirection(int index)
    {
      if (_loop is not null || _failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted)
      {
        return; // 防止两轮采集同时运行。
      }

      if (index is < 0 or > 3)
      {
        ResetDirectionSelection();
        return;
      }

      int axis = index >= 2 ? _bounds.Width : _bounds.Height;
      if (!double.IsFinite(_leadingBox.Value) || !double.IsFinite(_trailingBox.Value) ||
          _leadingBox.Value != Math.Truncate(_leadingBox.Value) || _trailingBox.Value != Math.Truncate(_trailingBox.Value) ||
          _leadingBox.Value < 0 || _trailingBox.Value < 0 || _leadingBox.Value + _trailingBox.Value > axis - 64)
      {
        _errorText.Text = "固定范围必须是非负整数像素，且至少留下 64 像素滚动内容。";
        _errorText.Visibility = Visibility.Visible;
        ResetDirectionSelection();
        return;
      }
      _direction = (ScrollCaptureDirection)index;
      _automatic = _automaticBox?.IsChecked == true;
      _fixedLeading = (int)_leadingBox.Value;
      _fixedTrailing = (int)_trailingBox.Value;
      if (_settingsButton is not null) _settingsButton.IsEnabled = false;
      foreach (ToggleButton segment in _directionSegments) segment.IsEnabled = false;
      _errorText.Visibility = Visibility.Collapsed;
      _statusText.Text = _automatic
          ? "采集中：自动滚动中，可随时手动微调，完成后点击“完成”。"
          : "采集中：请按所选方向滚动源窗口，完成后点击“完成”。";
      SetForegroundWindow(_source); // 焦点归还源窗口以接收滚轮输入。
      _loop = Task.Run(RunLoopAsync);
    }

    private void ResetDirectionSelection()
    {
      foreach (ToggleButton segment in _directionSegments) segment.IsChecked = false;
    }

    // 默认样式的 ToggleButton 排成紧凑分段：选定即启动并禁用全部分段，天然单选；
    // 默认样式自带高对比度与选中态，不引入自定义模板或样式抽象。
    private ToggleButton[] CreateDirectionSegments()
    {
      string[] labels = ["向下", "向上", "向右", "向左"];
      string[] ids = ["down", "up", "right", "left"];
      var segments = new ToggleButton[labels.Length];
      for (int index = 0; index < segments.Length; index++)
      {
        var segment = new ToggleButton
        {
          Content = labels[index],
          MinWidth = 52,
          Padding = new Thickness(10, 4, 10, 4),
        };
        AutomationProperties.SetAutomationId(segment, $"scroll-capture-direction-{ids[index]}");
        AutomationProperties.SetName(segment, labels[index]);
        ToolTipService.SetToolTip(segment, $"选择{labels[index]}后立即开始滚动采集");
        segment.Checked += OnDirectionPicked;
        segments[index] = segment;
      }
      return segments;
    }

    /// <summary>30 秒无新增有效内容的边界判定；等待用户复核/纠偏决策期间暂停计时，由总时限兜底。</summary>
    private bool NoProgressExpired() =>
        !_reviewPending && Environment.TickCount64 - _lastProgress >= NoProgressTimeoutMs;

    /// <summary>无进展边界处置：干净收尾直接交付已收选段；缺帧时必须显式确认，不静默伪装完整；无选段则如实失败。</summary>
    private void HandleNoProgressBoundary()
    {
      if (_failure is not null || _completion.Task.IsCompleted || _lifecycle.IsCancellationRequested) return;
      if (_stitcher is null)
      {
        Fail(new InvalidOperationException("30 秒未能捕获稳定首帧，已停止；本次未完成，未生成长图。"));
        return;
      }
      if (_overlapMissing)
      {
        // 当前画面与已收选段之间有缺口：直接交付会把缺帧伪装成完整文档，必须显式确认。
        CompleteBuild(_stitcher);
        return;
      }
      PublishSegment(_stitcher, boundary: true);
    }

    private void OnFinish(object sender, RoutedEventArgs args)
    {
      if (_stitcher is null || _finishRequested || _failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted)
      {
        return;
      }

      if (_pendingFrame is not null || _overlapMissing) return;
      HideReview();
      _reviewPending = false;
      _automatic = false;
      _finishButton.IsEnabled = false;
      _finishRequested = true;
      _statusText.Text = "请停止滚动，正在收尾…";
    }

    private void OnCancel(object sender, RoutedEventArgs args) => CancelFromUi();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
      _closed = true;
      CancelFromUi();
    }

    private void CancelFromUi()
    {
      lock (_terminalGate)
      {
        _correctionAuthorization = null;
        if (_failure is not null) _completion.TrySetException(_failure);
        else if (_external.IsCancellationRequested) _completion.TrySetCanceled(_external);
        else _completion.TrySetResult(null);
      }
      _lifecycle.Cancel();
    }

    private void OnExternalCancelled() =>
        OnExternalCancelled(action => _window?.DispatcherQueue.TryEnqueue(() => action()) == true);

    private void OnExternalCancelled(Func<Action, bool> enqueueClose)
    {
      // 终态不依赖 UI 队列存活；已发生的故障和外部取消由同一把锁裁决。
      CancelFromUi();
      enqueueClose(CloseControlWindow);
    }

    // 无窗口自检：关闭队列仅记录/拒绝操作，不创建 WinUI 控件或占用桌面。
    internal static async Task VerifyExternalCancellationSelfCheckAsync(bool? acceptsQueue, bool closeBeforeCallback, bool failFirst)
    {
      static async Task Verify(bool? acceptsQueue, bool closeBeforeCallback = false, Exception? failure = null)
      {
        using var external = new CancellationTokenSource();
        var session = new Session(1, new PhysicalRectangle(0, 0, 48, 96), external.Token);
        Action? queuedClose = null;
        bool EnqueueClose(Action close)
        {
          if (acceptsQueue == true) queuedClose = close;
          return acceptsQueue == true;
        }
        if (failure is not null) session.Fail(failure);
        using CancellationTokenRegistration registration = external.Token.Register(() =>
        {
          if (closeBeforeCallback) session.CancelFromUi();
          if (acceptsQueue is null) session.OnExternalCancelled();
          else session.OnExternalCancelled(EnqueueClose);
        });
        try
        {
          external.Cancel();
          // 即使 UI 先处理关闭或永远不执行排队操作，终态也必须已经确定。
          if (acceptsQueue == true) session.CancelFromUi();
          if (!session._completion.Task.IsCompleted)
            throw new InvalidOperationException("External cancellation waited for the UI close queue.");
          if (failure is not null)
          {
            try { await session._completion.Task; }
            catch (Exception error) when (ReferenceEquals(error, failure)) { return; }
            throw new InvalidOperationException("External cancellation lost the first failure.");
          }
          if (!session._completion.Task.IsCanceled)
            throw new InvalidOperationException("External cancellation became a null user cancellation.");
          queuedClose?.Invoke();
          try { await session._completion.Task; }
          catch (OperationCanceledException error) when (error.CancellationToken == external.Token) { return; }
          throw new InvalidOperationException("External cancellation lost its token.");
        }
        finally { session._lifecycle.Dispose(); }
      }
      await Verify(acceptsQueue, closeBeforeCallback, failFirst ? new InvalidOperationException("first failure") : null);
      var user = new Session(1, new PhysicalRectangle(0, 0, 48, 96), CancellationToken.None);
      try
      {
        user.CancelFromUi();
        if (await user._completion.Task is not null)
          throw new InvalidOperationException("User cancellation returned partial output.");
      }
      finally { user._lifecycle.Dispose(); }
    }

    internal void CloseControlWindow()
    {
      try
      {
        Window? window = _window ?? _selectionWindow;
        if (window is null || _closed) return;
        void Close()
        {
          if (_closed) return;
          _closed = true;
          window.Close();
        }
        if (window.DispatcherQueue.HasThreadAccess) Close();
        else window.DispatcherQueue.TryEnqueue(Close);
      }
      catch (Exception error)
      {
        AppLog.Warn($"scroll-capture 关闭控制窗失败：{error.Message}");
      }
    }

    private void RunOnUi(Action action, bool allowAfterFailure = false)
    {
      DispatcherQueue? queue = _window?.DispatcherQueue;
      if (queue is null)
      {
        return;
      }

      void Guarded()
      {
        lock (_terminalGate)
        {
          if (_completion.Task.IsCompleted || (!allowAfterFailure &&
              (_failure is not null || _lifecycle.IsCancellationRequested))) return;
          try
          {
            action();
            if (_loop is not null && _automaticBox is not null) _automaticBox.IsChecked = _automatic;
            FitToolbar();
          }
          catch (Exception error)
          {
            AppLog.Warn($"scroll-capture UI 更新失败：{error.Message}");
          }
        }
      }

      if (queue.HasThreadAccess)
      {
        Guarded();
      }
      else
      {
        queue.TryEnqueue(Guarded);
      }
    }

    internal static bool ContainsRect(PhysicalRectangle outer, PhysicalRectangle inner) =>
        outer.X <= inner.X && outer.Y <= inner.Y &&
        outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
      public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
      public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
      public uint CbSize;
      public RectL RcMonitor;
      public RectL RcWork;
      public uint DwFlags;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out RectL rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref RectL rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo information);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
  }
}
