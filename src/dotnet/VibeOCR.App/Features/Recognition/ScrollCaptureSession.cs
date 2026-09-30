using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
  /// 正常完成返回拼接后的 <see cref="CapturedFrame"/>；用户取消或关闭控制窗返回 null；
  /// 外部 token 取消抛 OperationCanceledException；失败在用户关闭控制窗后抛出
  /// InvalidOperationException/InvalidDataException，不返回不完整长图。
  /// 返回前控制窗已关闭、全部资源已清理。
  /// </summary>
  internal static async Task<CapturedFrame?> CaptureAsync(
      nint owner,
      PhysicalRectangle bounds,
      CancellationToken cancellationToken)
  {
    var session = new Session(owner, bounds, cancellationToken);
    return await session.RunAsync();
  }

  /// <summary>
  /// 单次采集会话的全部状态。窗口/按钮在 UI 线程构建，采集循环跑在线程池，
  /// UI 更新统一经 DispatcherQueue；失败状态保留窗口给用户查看，取消/关闭后才向调用方抛出。
  /// </summary>
  private sealed class Session
  {
    private const int CaptureIntervalMs = 550; // 约 500–600ms 采集节奏。
    // 静止判定为连续两帧完全相同（一次比较）：内容静止后最迟 1 个采集间隔（550ms）内上交；
    // 内容持续变化超过 DynamicContentTimeoutMs 判为动态失败。
    private const int StableComparisonsRequired = 1;
    private const int DynamicContentTimeoutMs = 5000; // 持续变化上限，避免无限忙循环。
    private const int ForegroundRestoreTimeoutMs = 5000; // 完成采样等待源窗口恢复前台的上限，防止无限挂起。
    private const int MinSelectionWidthPx = 32; // 首帧分配前的最小选区宽度。
    private const int MinSelectionHeightPx = 64; // 首帧分配前的最小选区高度。
    private const int PlacementGapPx = 16; // 控制窗与选区间距（physical px）。
    private const int WindowLogicalWidth = 320; // 控制窗固定逻辑宽度，长文本按此换行。
    private const int WindowChromeSlackPx = 60; // 标题栏等非客户区估算冗余。
    private const int PreviewMaxLogicalPx = 220; // 预览框逻辑尺寸上限。
    private const int PreviewMaxDecodePx = PreviewMaxLogicalPx * 2; // 按小尺寸解码，避免保留全帧位图。
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNearest = 2;

    private readonly nint _owner;
    private readonly PhysicalRectangle _bounds;
    private readonly CancellationToken _external;
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly TaskCompletionSource<CapturedFrame?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _elapsed = new();

    private Window _window = null!;
    private nint _controlHandle;
    private TextBlock _statusText = null!;
    private TextBlock _errorText = null!;
    private Image _previewImage = null!;
    private Button _startButton = null!;
    private Button _finishButton = null!;
    private Button _cancelButton = null!;

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
    private bool _finishSamplingStarted;
    private bool _closed;
    private Task _previewTask = Task.CompletedTask;
    private CancellationTokenRegistration _registration;

    public Session(nint owner, PhysicalRectangle bounds, CancellationToken external)
    {
      _owner = owner;
      _bounds = bounds;
      _external = external;
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
      CreateWindow(); // 无合法位置时在开始前直接抛“需缩小选区”。
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

        // 先等预览渲染收尾再关窗，避免向已关闭窗口写入。
        await _previewTask;
        _previewImage.Source = null;
        _lastCaptured = null;
        _stitcher = null;
        CloseControlWindow(); // owner/任务栏恢复由调用方负责，此处只关本模块控制窗。
        _lifecycle.Dispose();
      }
    }

    /// <summary>首次 GDI 采集前的选区尺寸守卫：过小无采集意义，过大直接拒绝超限整帧分配。</summary>
    private void ValidateSelectionSize()
    {
      if (_bounds.Width < MinSelectionWidthPx ||
          _bounds.Height < MinSelectionHeightPx ||
          _bounds.Width > VerticalScrollStitcher.MaximumDimension ||
          _bounds.Height > VerticalScrollStitcher.MaximumDimension ||
          (long)_bounds.Width * _bounds.Height > VerticalScrollStitcher.MaximumPixels)
      {
        throw new InvalidOperationException(
            $"选区尺寸超出滚动截图支持范围（宽≥{MinSelectionWidthPx}、高≥{MinSelectionHeightPx}，"
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
      _statusText = new TextBlock { Text = "准备中…" };
      double aspect = (double)_bounds.Height / Math.Max(1, _bounds.Width);
      int previewWidth = Math.Clamp(_bounds.Width, 80, PreviewMaxLogicalPx);
      int previewHeight = (int)Math.Clamp(Math.Round(previewWidth * aspect), 60, 160);
      _previewImage = new Image { Width = previewWidth, Height = previewHeight, Stretch = Stretch.Uniform };
      _errorText = new TextBlock
      {
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
      };
      _startButton = new Button { Content = "开始", IsEnabled = false };
      AutomationProperties.SetAutomationId(_startButton, "scroll-capture-start");
      _startButton.Click += OnStart;
      _finishButton = new Button { Content = "完成", IsEnabled = false };
      AutomationProperties.SetAutomationId(_finishButton, "scroll-capture-finish");
      _finishButton.Click += OnFinish;
      _cancelButton = new Button { Content = "取消" };
      AutomationProperties.SetAutomationId(_cancelButton, "scroll-capture-cancel");
      _cancelButton.Click += OnCancel;
      var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
      buttons.Children.Add(_startButton);
      buttons.Children.Add(_finishButton);
      buttons.Children.Add(_cancelButton);
      // 固定逻辑宽度 + 有限宽 Measure：长说明/错误文本按该宽度换行，高度随内容计算，
      // 避免 infinite 宽度下按单行测出巨宽、窗口被钳制后内容不重排。
      var root = new StackPanel { Spacing = 8, Padding = new Thickness(12), Width = WindowLogicalWidth };
      root.Children.Add(new TextBlock
      {
        Text = "请确认选区位于单一显示器的源窗口内容区域内，避开标题栏、滚动条等固定元素；"
            + "开始后每次向下滚动一段并停顿，内容静止后自动拼接。",
        TextWrapping = TextWrapping.Wrap,
      });
      root.Children.Add(_statusText);
      root.Children.Add(_previewImage);
      root.Children.Add(_errorText);
      root.Children.Add(buttons);
      _window = new Window { Title = "VibeOCR 滚动截图", Content = root };
      OverlappedPresenter presenter = OverlappedPresenter.Create();
      presenter.IsAlwaysOnTop = true;
      presenter.IsResizable = false;
      presenter.IsMaximizable = false;
      presenter.IsMinimizable = false;
      _window.AppWindow.SetPresenter(presenter);
      _window.AppWindow.IsShownInSwitchers = false;
      _controlHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
      // Keep the controller and its compositor shadow out of captured pixels.
      if (!SetWindowDisplayAffinity(_controlHandle, 0x00000011)) // WDA_EXCLUDEFROMCAPTURE
      {
        int error = Marshal.GetLastPInvokeError();
        _window.Close();
        throw new System.ComponentModel.Win32Exception(error, "无法将滚动截图控制窗排除出屏幕采集。");
      }
      root.Measure(new Windows.Foundation.Size(WindowLogicalWidth, double.PositiveInfinity));
      double scale = _sourceDpi > 0 ? _sourceDpi / 96.0 : 1.0;
      int width = (int)Math.Ceiling(WindowLogicalWidth * scale) + WindowChromeSlackPx;
      int height = (int)Math.Ceiling(root.DesiredSize.Height * scale) + WindowChromeSlackPx;
      if (!PlaceWindow(width, height))
      {
        _window.Close();
        throw new InvalidOperationException("选区周围的工作区内没有足够空间放置控制窗，请缩小选区后重试。");
      }

      _window.Closed += OnWindowClosed;
      _window.Activate();
    }

    /// <summary>
    /// 依次尝试选区下方/右侧/左侧/上方；每次都用真实 GetWindowRect 校验“完全在选区外
    /// 且完全在所在显示器工作区内”，找不到合法位置返回 false。
    /// </summary>
    private bool PlaceWindow(int width, int height)
    {
      PhysicalRectangle work = GetWorkArea(_bounds);
      width = Math.Min(width, work.Width);
      height = Math.Min(height, work.Height);
      foreach (RectInt32 candidate in PlacementCandidates(work, width, height))
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

      return false;
    }

    private RectInt32[] PlacementCandidates(PhysicalRectangle work, int width, int height)
    {
      int centeredX = Math.Clamp(
          _bounds.X + _bounds.Width / 2 - width / 2, work.X, Math.Max(work.X, work.Right - width));
      int centeredY = Math.Clamp(
          _bounds.Y + _bounds.Height / 2 - height / 2, work.Y, Math.Max(work.Y, work.Bottom - height));
      return
      [
          new RectInt32(centeredX, _bounds.Bottom + PlacementGapPx, width, height),
          new RectInt32(_bounds.Right + PlacementGapPx, centeredY, width, height),
          new RectInt32(_bounds.X - PlacementGapPx - width, centeredY, width, height),
          new RectInt32(centeredX, _bounds.Y - PlacementGapPx - height, width, height),
      ];
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
        _elapsed.Start();
        // 完成采样等待源窗口恢复前台的截止时刻；仅本循环线程读写，无需跨线程同步。
        long finishForegroundDeadline = 0;
        while (true)
        {
          _lifecycle.Token.ThrowIfCancellationRequested();
          if (_failure is not null || _completion.Task.IsCompleted)
          {
            return;
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
            if (_failure is not null || _completion.Task.IsCompleted)
            {
              return;
            }

            if (_finishSamplingStarted && _stitcher is not null && _appendedForCurrentStreak)
            {
              CompleteBuild(_stitcher);
              return;
            }
          }
          else
          {
            _stableStreak = 0;
            _appendedForCurrentStreak = false;
            if (now - _lastStableWitness > DynamicContentTimeoutMs)
            {
              Fail(new InvalidDataException("选区内容持续变化（动态内容），始终未能稳定，已停止采集。"));
              return;
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

        if (handle == _controlHandle || handle == _owner)
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
    private void HandleStable(CapturedFrame frame)
    {
      if (_stableStreak < StableComparisonsRequired)
      {
        return;
      }

      if (_stitcher is null)
      {
        _stitcher = new VerticalScrollStitcher(frame);
        _appendedForCurrentStreak = true;
        ReportAccepted(_stitcher, frame);
        return;
      }

      if (_appendedForCurrentStreak)
      {
        return;
      }

      ScrollAppendResult result = _stitcher.Append(frame, _lifecycle.Token);
      switch (result.Status)
      {
        case ScrollAppendStatus.Added:
          _appendedForCurrentStreak = true;
          ReportAccepted(_stitcher, frame);
          break;
        case ScrollAppendStatus.Duplicate:
          _appendedForCurrentStreak = true;
          break;
        case ScrollAppendStatus.Reverse:
          Fail(new InvalidDataException("检测到反向滚动（内容回退），无法继续拼接，已停止采集。"));
          break;
        case ScrollAppendStatus.NoOverlap:
          Fail(new InvalidDataException(
              "新内容与已拼接区域无重叠；请减小每次滚动距离（保留至少四分之一选区高度且不少于 32 像素的重叠），已停止采集。"));
          break;
        case ScrollAppendStatus.Ambiguous:
          Fail(new InvalidDataException(
              "滚动偏移无法确定（内容重复或相似）；请减小每次滚动距离，已停止采集。"));
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

    /// <summary>完成路径：先确保最后一段静止内容已上交（含最后一次滚动），再 BuildFrame。</summary>
    private void CompleteBuild(VerticalScrollStitcher stitcher)
    {
      _elapsed.Stop();
      CapturedFrame frame = stitcher.BuildFrame();
      AppLog.Info(
          $"scroll-capture 完成：frames={stitcher.FrameCount}, " +
          $"size={frame.Width}x{frame.Height}, elapsedMs={(int)_elapsed.ElapsedMilliseconds}");
      VerticalScrollStitcher snapshot = stitcher;
      RunOnUi(() =>
      {
        _statusText.Text = $"已完成：{snapshot.FrameCount} 帧 · 高度 {frame.Height} px";
        _startButton.IsEnabled = false;
        _finishButton.IsEnabled = false;
      });
      _completion.TrySetResult(frame);
    }

    private void ReportAccepted(VerticalScrollStitcher stitcher, CapturedFrame frame)
    {
      string status = $"已收 {stitcher.FrameCount} 帧 · 已拼高度 {stitcher.Height} px";
      byte[]? preview = EncodePreviewBmp(frame);
      RunOnUi(() =>
      {
        _statusText.Text = status;
        _finishButton.IsEnabled = true; // 首个稳定帧进入拼接器后“完成”可用。
        UpdatePreview(preview, frame.Width, frame.Height);
      });
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
        double scale = Math.Min(1.0, (double)PreviewMaxDecodePx / Math.Max(width, height));
        var bitmap = new BitmapImage
        {
          DecodePixelType = DecodePixelType.Physical,
          DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale)),
          DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale)),
        };
        await bitmap.SetSourceAsync(stream);
        if (_lifecycle.IsCancellationRequested)
        {
          return; // 会话已取消，不再触碰窗口控件。
        }

        if (!_lifecycle.IsCancellationRequested)
          _previewImage.Source = bitmap;
      }
      catch (Exception error)
      {
        AppLog.Warn($"scroll-capture 预览解码失败：{error.Message}");
      }
    }

    /// <summary>
    /// 失败状态：显示具体中文错误并停止采集；保留取消/关闭让用户看清错误，“完成”禁用。
    /// 用户取消/关闭后由 CancelFromUi 把该异常抛给调用方，不自动返回不完整长图。
    /// </summary>
    private void Fail(Exception error)
    {
      if (_completion.Task.IsCompleted)
      {
        return;
      }

      _failure = error;
      AppLog.Warn($"scroll-capture 停止：{error.Message}");
      RunOnUi(() =>
      {
        _errorText.Text = error.Message;
        _errorText.Visibility = Visibility.Visible;
        // 固定高度窗口中为错误说明和取消按钮腾出空间。
        _previewImage.Visibility = Visibility.Collapsed;
        _startButton.IsEnabled = false;
        _finishButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _statusText.Text = "采集已停止：请查看上方错误，处理后关闭窗口。";
      });
    }

    private void SetReadyUi()
    {
      _statusText.Text = "就绪：点击“开始”后，在源窗口中向下滚动内容。";
      _startButton.IsEnabled = true;
    }

    private void OnStart(object sender, RoutedEventArgs args)
    {
      if (_loop is not null || _failure is not null || _completion.Task.IsCompleted)
      {
        return; // 防止两轮采集同时运行。
      }

      _startButton.IsEnabled = false;
      _statusText.Text = "采集中：向下滚动一段后停顿，等待内容静止。";
      SetForegroundWindow(_source); // 焦点归还源窗口，由用户手动滚动。
      _loop = Task.Run(RunLoopAsync);
    }

    private void OnFinish(object sender, RoutedEventArgs args)
    {
      if (_stitcher is null || _finishRequested || _failure is not null || _completion.Task.IsCompleted)
      {
        return;
      }

      _finishButton.IsEnabled = false;
      _finishRequested = true;
      _statusText.Text = "正在收尾：正在把源窗口恢复前台，请停止滚动，等待最后一帧稳定。";
    }

    private void OnCancel(object sender, RoutedEventArgs args) => CancelFromUi();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
      _closed = true;
      CancelFromUi();
    }

    private void CancelFromUi()
    {
      _lifecycle.Cancel();
      if (_failure is not null)
      {
        _completion.TrySetException(_failure);
      }
      else
      {
        _completion.TrySetResult(null);
      }
    }

    private void OnExternalCancelled()
    {
      _lifecycle.Cancel();
      DispatcherQueue? queue = _window?.DispatcherQueue;
      queue?.TryEnqueue(() =>
      {
        _completion.TrySetCanceled(_external);
        CloseControlWindow();
      });
    }

    private void CloseControlWindow()
    {
      try
      {
        Window window = _window;
        if (window is null || _closed)
        {
          return;
        }

        if (window.DispatcherQueue.HasThreadAccess)
        {
          window.Close();
        }
        else
        {
          window.DispatcherQueue.TryEnqueue(window.Close);
        }
      }
      catch (Exception error)
      {
        AppLog.Warn($"scroll-capture 关闭控制窗失败：{error.Message}");
      }
    }

    private void RunOnUi(Action action)
    {
      DispatcherQueue? queue = _window?.DispatcherQueue;
      if (queue is null)
      {
        return;
      }

      void Guarded()
      {
        if (_completion.Task.IsCompleted) return;
        try
        {
          action();
        }
        catch (Exception error)
        {
          AppLog.Warn($"scroll-capture UI 更新失败：{error.Message}");
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

    private static bool ContainsRect(PhysicalRectangle outer, PhysicalRectangle inner) =>
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
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

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
