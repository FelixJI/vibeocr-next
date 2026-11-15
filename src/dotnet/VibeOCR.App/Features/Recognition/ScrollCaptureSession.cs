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
using static VibeOCR.App.Features.Recognition.ScrollCaptureDriver;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Captures stable frames from one fixed window region until completion or cancellation.</summary>
internal static partial class ScrollCaptureSession
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
  /// WinUI 控制窗与预览。后台采集驱动提交帧，状态机裁决复核与交付。
  /// </summary>
  internal sealed partial class Session
  {
    private const int MinSelectionSidePx = 32; // 方向尚未选择时，两边采用同一最小值；开始时另验滚动轴至少 64px。
    private const int PlacementGapPx = 16; // 控制窗与选区间距（physical px）。
    private const int WindowLogicalWidth = 500; // 紧凑工具栏逻辑宽度。
    private const int PreviewMaxLogicalPx = 220; // 预览框逻辑尺寸上限。
    private const int PreviewMaxDecodePx = PreviewMaxLogicalPx * 2; // 按小尺寸解码，避免保留全帧位图。
    private const uint MonitorDefaultToNearest = 2;

    private readonly nint _owner;
    private PhysicalRectangle _bounds;
    private readonly CancellationToken _external;
    private readonly ScrollCaptureState _state;

    private Window _window = null!;
    private readonly Window? _selectionWindow;
    private ScrollCaptureMask? _mask;
    private NumberBox? _widthBox, _heightBox;
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
    private Source _source;
    private ScrollCaptureDriver? _driver;
    private string? _lastScrollInputDiagnostic;

    private Task? _loop;
    private Task _deadlineTask = Task.CompletedTask;
    private bool _failureUiQueued;
    private bool _closed;
    private Task _previewTask = Task.CompletedTask;
    private bool _acceptedPreviewQueued; // _state.Gate：生成中或已排入 UI，尚未交给渲染任务。
    private AcceptedPreview? _queuedPreview;
    private int _previewedFrameCount;
    private CancellationTokenRegistration _registration;

    private readonly record struct AcceptedPreview(byte[] Bmp, int Width, int Height, int FrameCount);

    internal Window ControlWindow => _window;
    internal PhysicalRectangle Bounds => _bounds;
    internal string DiagnosticState =>
        $"automatic={_state.Automatic}, phase={_state.Phase}, " +
        $"input={_driver?.ScrollInputDiagnostic ?? _lastScrollInputDiagnostic}, failure={_state.Failure?.Message}";
    internal bool HasReleasedCaptureResources => _loop?.IsCompleted != false && _deadlineTask.IsCompleted &&
        _previewTask.IsCompleted && _queuedPreview is null && !_acceptedPreviewQueued &&
        _state.HasReleasedFrames && _driver is null && _mask is null && _previewWindow is null;

    public Session(
        nint owner, PhysicalRectangle bounds, CancellationToken external, Window? selectionWindow = null, Func<long>? clock = null)
    {
      _owner = owner;
      _bounds = bounds;
      _external = external;
      _selectionWindow = selectionWindow;
      _state = new ScrollCaptureState(external, clock);
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
      string? sourceError = FindSource(_owner, _bounds, out _source);
      try { CreateWindow(); }
      catch
      {
        _mask?.Dispose();
        _mask = null;
        _previewWindow?.Close();
        _previewWindow = null;
        CloseControlWindow();
        _state.Dispose();
        throw;
      }
      _registration = _external.Register(OnExternalCancelled);
      try
      {
        if (sourceError is not null || _source.Handle == _controlHandle)
        {
          Fail(new InvalidOperationException(sourceError ?? "选区中心的源窗口解析失败，请重新框选。"));
        }
        else
        {
          SetReadyUi();
        }

        return await _state.Completion;
      }
      finally
      {
        _state.Stop();
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
            // 采集驱动已将异常交给会话终态。
          }
        }

        _lastScrollInputDiagnostic = _driver?.ScrollInputDiagnostic;
        _driver = null;
        await _deadlineTask;
        // 先等预览渲染收尾再关窗，避免向已关闭窗口写入。
        try { await _previewTask; }
        catch (Exception error) { AppLog.Warn($"scroll-capture 预览收尾失败：{error.Message}"); }
        _previewImage.Source = null;
        _pendingImage.Source = null;
        _queuedPreview = null;
        _acceptedPreviewQueued = false;
        _previewWindow?.Close();
        _previewWindow = null;
        _mask?.Dispose();
        _mask = null;
        CloseControlWindow(); // owner/任务栏恢复由调用方负责，此处只关本模块控制窗。
        _state.Dispose();
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
      double scale = _source.Dpi > 0 ? _source.Dpi / 96.0 : 1.0;
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
        _mask = new ScrollCaptureMask(_source.Desktop, _bounds);
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
      double dpiScale = _source.Dpi > 0 ? _source.Dpi / 96.0 : 1;
      PhysicalRectangle work = GetWorkArea(_bounds);
      int sideSpace = Math.Max(work.Right - _bounds.Right, _bounds.X - work.X) - PlacementGapPx - 6;
      bool horizontal = _state.Direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
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
          work, _bounds, ReadControlBounds(_controlHandle, _bounds), physicalWidth, physicalHeight))
      {
        var rect = new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height);
        if (!ContainsRect(work, rect) || SmartScreenCandidates.Intersect(rect, _bounds) is not null ||
            SmartScreenCandidates.Intersect(rect, ReadControlBounds(_controlHandle, _bounds)) is not null) continue;
        _previewWindow.AppWindow.MoveAndResize(candidate);
        if (!GetWindowRect(_previewHandle, out RectL actual)) continue;
        var actualRect = new PhysicalRectangle(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
        if (!ContainsRect(work, actualRect) || SmartScreenCandidates.Intersect(actualRect, _bounds) is not null ||
            SmartScreenCandidates.Intersect(actualRect, ReadControlBounds(_controlHandle, _bounds)) is not null) continue;
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
      _mask = new ScrollCaptureMask(_source.Desktop, _bounds);
      _acceptedPreviewWidth = _bounds.Width;
      _acceptedPreviewHeight = _bounds.Height;
      if (!EnsurePreviewVisible()) return;
      _errorText.Visibility = Visibility.Collapsed;
    }

    private bool SelectionFitsCaptureArea(PhysicalRectangle bounds) =>
        ContainsRect(_source.WindowBounds, bounds) && ContainsRect(_source.Desktop, bounds);

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
      double scale = _source.Dpi > 0 ? _source.Dpi / 96.0 : 1;
      int height = (int)Math.Ceiling(_toolbarContent.DesiredSize.Height * scale);
      SizeInt32 current = _window.AppWindow.Size;
      if (height != current.Height) PlaceWindow(current.Width, height);
      if (_acceptedPreviewWidth > 0 && _state.IsActive) EnsurePreviewVisible();
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

    private async Task RunLoopAsync()
    {
      _deadlineTask = EnforceDeadlineAsync();
      await _driver!.RunAsync(PresentChange, Fail);
      PresentFailure();
    }

    private void PresentChange(ScrollCaptureChange change, CapturedFrame? frame)
    {
      switch (change.Update)
      {
        case ScrollCaptureUpdate.None:
          // 解码期间跳过中间帧。空闲后的下一次采样补上最新已收选段。
          if (frame is not null && _state.Review is null && _state.Stitcher is { } current &&
              current.FrameCount > Volatile.Read(ref _previewedFrameCount))
            QueueAcceptedPreview(current, frame);
          break;
        case ScrollCaptureUpdate.FirstAccepted:
          ReportAccepted(_state.Stitcher!, frame!);
          RunOnUi(() =>
          {
            if (_state.Review is not null || _state.Stitcher?.FrameCount != 1) return;
            _statusText.Text = _state.Automatic
                ? "已收首帧 · 自动滚动中，完成后点击“完成”"
                : "已收首帧 · 请按所选方向滚动源窗口，完成后点击“完成”";
          });
          break;
        case ScrollCaptureUpdate.Accepted:
          RunOnUi(() => { if (_state.Review is null && !_state.OverlapMissing) _errorText.Visibility = Visibility.Collapsed; });
          ReportAccepted(_state.Stitcher!, frame!);
          break;
        case ScrollCaptureUpdate.OverlapRestored:
          RunOnUi(() =>
          {
            if (_state.Review is not null || _state.OverlapMissing) return;
            _errorText.Visibility = Visibility.Collapsed;
            _finishButton.IsEnabled = _state.CanFinish;
          });
          break;
        case ScrollCaptureUpdate.OverlapMissing:
          RunOnUi(() =>
          {
            if (_state.Review is not null || !_state.OverlapMissing) return;
            _finishButton.IsEnabled = false;
            _errorText.Text = "滚动过快或内容变化，请向回滚动到预览末尾，再缓慢继续；长时间无新增时将要求确认交付已收选段。";
            _errorText.Visibility = Visibility.Visible;
          });
          break;
        case ScrollCaptureUpdate.ReverseReview:
          PresentReview(change.Review!,
              "检测到反向滚动：该帧未加入已收选段。请丢弃反向帧后，按所选方向滚回已收边界再继续；也可点击完成直接交付已收选段。",
              "丢弃反向帧并继续");
          break;
        case ScrollCaptureUpdate.OffsetReview:
          PresentReview(change.Review!,
              "无法可靠拼接（重复纹理导致偏移歧义），自动滚动已停止，本次尚未完成。上图为已收选段，下图为待拼帧；输入所选方向的真实位移并明确确认。完整重叠必须一致，否则拒绝；不能确认时请取消重选。",
              "确认位移并校验拼接");
          break;
        case ScrollCaptureUpdate.AutomaticReview:
          PresentReview(change.Review!,
              "自动滚动连续 3 次未前进：无法判断是否到达末尾，当前仅捕获已收选段。可确认后手动继续，或点击完成直接交付已收选段。",
              "确认预览并继续");
          break;
        case ScrollCaptureUpdate.SegmentReview:
          PresentSegmentReview(change.Review!);
          break;
        case ScrollCaptureUpdate.CorrectionAccepted:
          ReportAccepted(_state.Stitcher!, frame!);
          RunOnUi(() => { if (_state.Review is null) HideReview(); });
          break;
        case ScrollCaptureUpdate.CorrectionRejected:
        case ScrollCaptureUpdate.CorrectionLimit:
          RunOnUi(() =>
          {
            if (!_state.IsCurrentReview(change.Review!)) return;
            _errorText.Text = change.Update == ScrollCaptureUpdate.CorrectionLimit
                ? "该位移会超过拼接上限，请选择其他可行位移；未修改已收图像。不能确认时请取消。"
                : "纠偏未通过完整重叠校验，请核对位移；未修改已收图像。取消可丢弃本次采集。";
            _offsetBox.IsEnabled = true;
            _confirmButton.IsEnabled = true;
          });
          break;
      }
    }

    private void PresentSegmentReview(ScrollCaptureReview review)
    {
      CapturedFrame frame = review.Segment!;
      byte[]? preview = EncodePreviewBmp(frame);
      int count = _state.Stitcher?.FrameCount ?? 0;
      AppLog.Info($"scroll-capture 缺帧边界待确认：frames={count}, size={frame.Width}x{frame.Height}, elapsedMs={_state.ElapsedMilliseconds}");
      RunOnUi(() =>
      {
        if (!_state.IsCurrentReview(review)) return;
        _statusText.Text = $"已收选段：{count} 帧 · 缺少可靠重叠，需确认交付";
        _errorText.Text = "已长时间没有新增内容，且当前画面与已收选段之间缺少可靠重叠。确认仅交付已收选段（不含中间缺失内容），或取消放弃本次采集。";
        _errorText.Visibility = Visibility.Visible;
        _confirmButton.Content = "确认交付已收选段";
        _confirmButton.Visibility = Visibility.Visible;
        _confirmButton.IsEnabled = false;
        _finishButton.IsEnabled = false;
        _previewTask = RenderFinalPreviewAsync(_previewTask, preview, review);
      });
    }

    private void ReportAccepted(VerticalScrollStitcher stitcher, CapturedFrame frame)
    {
      string status = $"已收 {stitcher.FrameCount} 帧 · {stitcher.Width} × {stitcher.Height} px";
      RunOnUi(() =>
      {
        if (_state.Review is not null) return;
        _statusText.Text = status;
        _finishButton.IsEnabled = _state.CanFinish;
      });
      QueueAcceptedPreview(stitcher, frame);
    }

    private void QueueAcceptedPreview(VerticalScrollStitcher stitcher, CapturedFrame frame)
    {
      lock (_state.Gate)
      {
        if (_acceptedPreviewQueued || !_previewTask.IsCompleted || !_state.IsActive) return;
        _acceptedPreviewQueued = true;
      }
      try
      {
        if (frame.Stride != checked(frame.Width * 4) || frame.Pixels.LongLength != (long)frame.Stride * frame.Height)
        {
          Fail(new InvalidOperationException("无法生成已收选段预览，本次未完成，请取消后重试。"));
          lock (_state.Gate) _acceptedPreviewQueued = false;
          return;
        }
        if (BuildAcceptedPreview(stitcher, frame.Width, frame.Height) is not { } preview)
        {
          Fail(new InvalidOperationException("无法生成已收选段预览，本次未完成，请取消后重试。"));
          lock (_state.Gate) _acceptedPreviewQueued = false;
          return;
        }
        lock (_state.Gate)
        {
          if (!_state.IsActive)
          {
            _acceptedPreviewQueued = false;
            return;
          }
          _queuedPreview = preview;
        }
        DispatcherQueue queue = _window.DispatcherQueue;
        if (queue.HasThreadAccess) PresentAcceptedPreview();
        else if (!queue.TryEnqueue(PresentAcceptedPreview)) ClearAcceptedPreviewQueue();
      }
      catch
      {
        ClearAcceptedPreviewQueue();
        throw;
      }
    }

    private void PresentAcceptedPreview()
    {
      lock (_state.Gate)
      {
        var queued = _queuedPreview;
        ClearAcceptedPreviewQueue();
        if (!_state.IsActive || !_previewTask.IsCompleted || queued is not { } preview) return;
        Volatile.Write(ref _previewedFrameCount, preview.FrameCount);
        _previewTask = RenderPreviewAsync(preview.Bmp, preview.Width, preview.Height);
      }
    }

    private AcceptedPreview? BuildAcceptedPreview(VerticalScrollStitcher stitcher, int sourceWidth, int sourceHeight)
    {
      CapturedFrame thumbnail = stitcher.BuildPreview(PreviewMaxDecodePx, _state.Token);
      if (stitcher.FrameCount == 1)
        thumbnail = MarkFixedEdges(
            thumbnail, sourceWidth, sourceHeight, _state.Direction, _state.FixedLeading, _state.FixedTrailing, _state.Token);
      return EncodePreviewBmp(thumbnail) is { } bmp
          ? new AcceptedPreview(bmp, thumbnail.Width, thumbnail.Height, stitcher.FrameCount)
          : null;
    }

    private void ClearAcceptedPreviewQueue()
    {
      lock (_state.Gate)
      {
        _queuedPreview = null;
        _acceptedPreviewQueued = false;
      }
    }

    private static CapturedFrame MarkFixedEdges(
        CapturedFrame frame, int sourceWidth, int sourceHeight, ScrollCaptureDirection direction,
        int fixedLeading, int fixedTrailing, CancellationToken cancellationToken)
    {
      if (fixedLeading == 0 && fixedTrailing == 0) return frame;
      byte[] pixels = (byte[])frame.Pixels.Clone();
      bool horizontal = direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
      int extent = horizontal ? sourceWidth : sourceHeight;
      int previewExtent = horizontal ? frame.Width : frame.Height;
      for (int y = 0; y < frame.Height; y++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        for (int x = 0; x < frame.Width; x++)
        {
          int axis = (int)((long)(horizontal ? x : y) * extent / previewExtent);
          if (axis >= fixedLeading && axis < extent - fixedTrailing) continue;
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

    private async Task RenderPreviewAsync(byte[] bmp, int width, int height, ScrollCaptureReview? review = null)
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
        lock (_state.Gate)
        {
          if (!_state.IsActive || (review is not null && !_state.IsCurrentReview(review))) return;
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
      _state.Fail(error);
      PresentFailure();
    }

    private void PresentFailure()
    {
      if (_state.Failure is not { } error) return;
      lock (_state.Gate)
      {
        if (_failureUiQueued) return;
        _failureUiQueued = true;
        ClearAcceptedPreviewQueue();
      }
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
        int remaining = (int)Math.Max(0, ScrollCaptureState.TotalTimeoutMs - _state.ElapsedMilliseconds);
        await Task.Delay(remaining, _state.Token);
        Fail(new InvalidOperationException(ScrollCaptureState.DeadlineMessage));
      }
      catch (OperationCanceledException) { }
    }

    private async Task RenderFinalPreviewAsync(Task previous, byte[]? bytes, ScrollCaptureReview review)
    {
      await previous;
      if (!_state.IsCurrentReview(review)) return;
      if (bytes is null)
      {
        Fail(new InvalidOperationException("无法生成选段预览，本次未完成，请重试。"));
        return;
      }
      CapturedFrame frame = review.Segment!;
      await RenderPreviewAsync(bytes, frame.Width, frame.Height, review);
      lock (_state.Gate)
      {
        if (_state.IsCurrentReview(review)) _confirmButton.IsEnabled = true;
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

    private void PresentReview(ScrollCaptureReview review, string message, string confirmLabel)
    {
      CapturedFrame? pending = review.Pending;
      byte[]? preview = pending is null ? null : EncodePreviewBmp(pending);
      if (pending is not null && preview is null)
      {
        Fail(new InvalidOperationException("无法生成待拼预览，本次未完成，请取消后重试。"));
        return;
      }
      AcceptedPreview? accepted = null;
      if (_state.Stitcher is { } current && current.FrameCount > Volatile.Read(ref _previewedFrameCount))
      {
        // 复核按钮开启前，用户必须能看到与待确认帧对应的最新已收边界。
        accepted = BuildAcceptedPreview(current, _bounds.Width, _bounds.Height);
        if (accepted is null)
        {
          Fail(new InvalidOperationException("无法生成已收选段预览，本次未完成，请取消后重试。"));
          return;
        }
      }
      RunOnUi(() =>
      {
        if (!_state.IsCurrentReview(review)) return;
        _statusText.Text = "已暂停 · 确认后继续";
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
        _confirmButton.Content = confirmLabel;
        _confirmButton.Visibility = Visibility.Visible;
        _confirmButton.IsEnabled = false;
        _finishButton.IsEnabled = _state.CanFinish;
        _offsetBox.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        _offsetBox.IsEnabled = true;
        if (pending is not null && _state.Stitcher is { } stitcher)
        {
          _offsetBox.Maximum = stitcher.MaximumOffset;
          _offsetBox.Value = Math.Max(1, stitcher.MaximumOffset / 2);
          _pendingImage.Visibility = Visibility.Visible;
        }
        _previewTask = RenderReviewPreviewAsync(_previewTask, preview, review, accepted);
      });
    }

    private async Task RenderReviewPreviewAsync(
        Task previous, byte[]? bytes, ScrollCaptureReview review, AcceptedPreview? accepted = null)
    {
      await previous;
      if (!_state.IsCurrentReview(review)) return;
      if (accepted is { } latest && latest.FrameCount > Volatile.Read(ref _previewedFrameCount))
      {
        await RenderPreviewAsync(latest.Bmp, latest.Width, latest.Height, review);
        if (_state.IsCurrentReview(review)) Volatile.Write(ref _previewedFrameCount, latest.FrameCount);
      }
      lock (_state.Gate)
      {
        if (!_state.IsCurrentReview(review)) return;
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
        CapturedFrame pending = review.Pending!;
        BitmapImage bitmap = CreatePreviewBitmap(pending.Width, pending.Height);
        await bitmap.SetSourceAsync(stream);
        lock (_state.Gate)
        {
          if (!_state.IsCurrentReview(review)) return;
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
      if (_state.Review is not { } review || !_confirmButton.IsEnabled || !_state.IsCurrentReview(review)) return;
      if (!EnsurePreviewVisible()) return;
      if (review.Segment is not null)
      {
        string? sourceViolation = _driver is { } driver ? driver.GuardBeforeCapture() : "源窗口已关闭，已停止采集。";
        if (sourceViolation is not null) { Fail(new InvalidOperationException(sourceViolation)); return; }
        if (!_state.ConfirmSegment(review)) PresentFailure();
        return;
      }
      if (review.Pending is not null)
      {
        double offset = _offsetBox.Value;
        if (!double.IsFinite(offset) || offset != Math.Truncate(offset) || offset < 1 || offset > _offsetBox.Maximum)
        {
          _errorText.Text = "纠偏位移必须是范围内的整数像素。";
          return;
        }
        lock (_state.Gate)
        {
          if (!_state.AuthorizeCorrection(review, (int)offset)) return;
          _confirmButton.IsEnabled = false;
          _offsetBox.IsEnabled = false;
        }
        return;
      }
      lock (_state.Gate)
      {
        if (!_state.ResumeReview(review)) return;
        HideReview();
        _finishButton.IsEnabled = _state.CanFinish;
        _statusText.Text = "滚动源窗口 · 完成后核对结果";
      }
      ScrollCaptureDriver.RestoreForeground(_state, _source.Handle);
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
      if (_loop is not null || !_state.IsActive)
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
      if (!_state.Start((ScrollCaptureDirection)index, _automaticBox?.IsChecked == true,
          (int)_leadingBox.Value, (int)_trailingBox.Value)) return;
      if (_settingsButton is not null) _settingsButton.IsEnabled = false;
      foreach (ToggleButton segment in _directionSegments) segment.IsEnabled = false;
      _errorText.Visibility = Visibility.Collapsed;
      _statusText.Text = _state.Automatic
          ? "采集中：自动滚动中，可随时手动微调，完成后点击“完成”。"
          : "采集中：请按所选方向滚动源窗口，完成后点击“完成”。";
      ScrollCaptureDriver.RestoreForeground(_state, _source.Handle); // 焦点归还源窗口以接收滚轮输入。
      _driver = new ScrollCaptureDriver(_state, _bounds, _source, _owner, _controlHandle, _previewHandle, _mask);
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

    private void OnFinish(object sender, RoutedEventArgs args)
    {
      lock (_state.Gate)
      {
        if (!_state.TryRequestFinish()) return;
        HideReview();
        _finishButton.IsEnabled = false;
        _statusText.Text = "请停止滚动，正在收尾…";
      }
    }

    private void OnCancel(object sender, RoutedEventArgs args) => CancelFromUi();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
      _closed = true;
      if (!_state.Completion.IsCompleted) CancelFromUi();
    }

    private void CancelFromUi()
    {
      _state.Cancel();
      ClearAcceptedPreviewQueue();
    }

    private void OnExternalCancelled() =>
        OnExternalCancelled(action => _window?.DispatcherQueue.TryEnqueue(() => action()) == true);

    private void OnExternalCancelled(Func<Action, bool> enqueueClose)
    {
      // 终态不依赖 UI 队列存活；已发生的故障和外部取消由同一把锁裁决。
      CancelFromUi();
      try { enqueueClose(CloseControlWindow); }
      catch (Exception error) { AppLog.Warn($"scroll-capture 取消后无法请求关闭窗口：{error.Message}"); }
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
        lock (_state.Gate)
        {
          if (_state.Completion.IsCompleted || (!allowAfterFailure && !_state.IsActive)) return;
          try
          {
            action();
            if (_loop is not null && _automaticBox is not null) _automaticBox.IsChecked = _state.Automatic;
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
    private struct MonitorInfo
    {
      public uint CbSize;
      public RectL RcMonitor;
      public RectL RcWork;
      public uint DwFlags;
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref RectL rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo information);
  }
}
