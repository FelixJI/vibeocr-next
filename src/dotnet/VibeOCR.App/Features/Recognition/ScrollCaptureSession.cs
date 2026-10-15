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
  internal sealed class Session
  {
    private const int CaptureIntervalMs = 550; // 约 500–600ms 采集节奏。
    // 静止判定为连续两帧完全相同（一次比较）：内容静止后最迟 1 个采集间隔（550ms）内上交；
    // 内容持续变化超过 DynamicContentTimeoutMs 判为动态失败。
    private const int StableComparisonsRequired = 1;
    private const int DynamicContentTimeoutMs = 5000; // 持续变化上限，避免无限忙循环。
    private const int ForegroundRestoreTimeoutMs = 5000; // 完成采样等待源窗口恢复前台的上限，防止无限挂起。
    private const int MinSelectionSidePx = 32; // 方向尚未选择时，两边采用同一最小值；开始时另验滚动轴至少 64px。
    private const int PlacementGapPx = 16; // 控制窗与选区间距（physical px）。
    private const int WindowLogicalWidth = 320; // 控制窗固定逻辑宽度，长文本按此换行。
    private const int WindowChromeSlackPx = 60; // 标题栏等非客户区估算冗余。
    private const int PreviewMaxLogicalPx = 220; // 预览框逻辑尺寸上限。
    private const int PreviewMaxDecodePx = PreviewMaxLogicalPx * 2; // 按小尺寸解码，避免保留全帧位图。
    private const int TotalTimeoutMs = 180_000;
    private const int NoProgressTimeoutMs = 30_000;
    private const int AutomaticNoProgressAttempts = 3;
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNearest = 2;

    private readonly nint _owner;
    private readonly PhysicalRectangle _bounds;
    private readonly CancellationToken _external;
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly TaskCompletionSource<CapturedFrame?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _elapsed = new();
    private readonly object _terminalGate = new();

    private Window _window = null!;
    private nint _controlHandle;
    private TextBlock _statusText = null!;
    private TextBlock _errorText = null!;
    private Image _previewImage = null!;
    private Button _startButton = null!;
    private Button _finishButton = null!;
    private Button _cancelButton = null!;
    private ComboBox _directionBox = null!;
    private CheckBox _automaticBox = null!;
    private NumberBox _leadingBox = null!;
    private NumberBox _trailingBox = null!;
    private NumberBox _offsetBox = null!;
    private Button _confirmButton = null!;
    private Image _pendingImage = null!;
    private StackPanel _options = null!;
    private ScrollCaptureDirection _direction;
    private int _fixedLeading, _fixedTrailing;
    private volatile bool _automatic;
    private volatile bool _reviewPending;
    private volatile bool _initialPreviewConfirmed;
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
    private CancellationTokenRegistration _registration;

    internal Window ControlWindow => _window;
    internal string DiagnosticState =>
        $"automatic={_automatic}, review={_reviewPending}, confirmed={_initialPreviewConfirmed}, " +
        $"stable={_stableStreak}, appended={_appendedForCurrentStreak}, input={_scrollInputDiagnostic}, failure={_failure?.Message}";
    internal bool HasReleasedCaptureResources => _loop?.IsCompleted != false && _previewTask.IsCompleted &&
        _stitcher is null && _lastCaptured is null && _pendingFrame is null && _finalFrame is null && _correctionAuthorization is null;

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
      _statusText = new TextBlock { Text = "准备中…", TextWrapping = TextWrapping.Wrap };
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
        Text = "仅支持静态、可重叠的普通窗口内容。选择方向与固定边缘范围，首帧预览后确认；"
            + "自动模式会短暂定位光标到选区中心发送标准滚轮；请松开鼠键和 Ctrl/Shift/Alt。不支持游戏、受保护画面、提权窗口或自定义滚动控件。",
        TextWrapping = TextWrapping.Wrap,
      });
      _directionBox = new ComboBox { Header = "滚动方向", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
      foreach (string direction in new[] { "向下", "向上", "向右", "向左" }) _directionBox.Items.Add(direction);
      _directionBox.SelectedIndex = 0;
      AutomationProperties.SetAutomationId(_directionBox, "scroll-capture-direction");
      _automaticBox = new CheckBox { Content = "自动滚动（普通窗口标准滚轮）" };
      AutomationProperties.SetAutomationId(_automaticBox, "scroll-capture-automatic");
      _leadingBox = EdgeNumber("固定顶部 / 左侧（像素）", "scroll-capture-leading");
      _trailingBox = EdgeNumber("固定底部 / 右侧（像素）", "scroll-capture-trailing");
      var edgeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
      _leadingBox.Width = 144;
      _trailingBox.Width = 144;
      edgeRow.Children.Add(_leadingBox);
      edgeRow.Children.Add(_trailingBox);
      _options = new StackPanel { Spacing = 4 };
      _options.Children.Add(_directionBox);
      _options.Children.Add(_automaticBox);
      _options.Children.Add(edgeRow);
      root.Children.Add(_options);
      root.Children.Add(_statusText);
      root.Children.Add(_previewImage);
      _pendingImage = new Image { Width = previewWidth, Height = previewHeight, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
      root.Children.Add(_pendingImage);
      _offsetBox = EdgeNumber("纠偏位移（所选方向的像素数）", "scroll-capture-offset");
      _offsetBox.Minimum = 1;
      _offsetBox.Visibility = Visibility.Collapsed;
      root.Children.Add(_offsetBox);
      _confirmButton = new Button { Content = "确认预览并继续", Visibility = Visibility.Collapsed };
      AutomationProperties.SetAutomationId(_confirmButton, "scroll-capture-confirm");
      _confirmButton.Click += OnConfirm;
      root.Children.Add(_confirmButton);
      root.Children.Add(_errorText);
      root.Children.Add(buttons);
      var scroller = new ScrollViewer
      {
        Content = root,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
      };
      _window = new Window { Title = "VibeOCR 滚动截图", Content = scroller };
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
          if (Environment.TickCount64 - _lastProgress >= NoProgressTimeoutMs)
          {
            Fail(new InvalidOperationException("30 秒没有新增有效内容，已停止；本次未完成，未生成长图。"));
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

            if (!_reviewPending && _finishSamplingStarted && _stitcher is not null && _appendedForCurrentStreak)
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

          if (_automatic && !_reviewPending && !_finishRequested && _appendedForCurrentStreak)
          {
            if (_scrollOutstanding && ++_noProgressAttempts >= AutomaticNoProgressAttempts)
            {
              _automatic = false;
              _scrollOutstanding = false;
              PauseForReview("自动滚动连续 3 次未前进：无法判断是否到达末尾，当前仅捕获已收选段。可确认后手动继续，或点击完成预览选段。", null);
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
        _stitcher = new VerticalScrollStitcher(frame, _direction, _fixedLeading, _fixedTrailing);
        _appendedForCurrentStreak = true;
        ReportAccepted(_stitcher, frame);
        PauseForReview($"请检查首帧。红色预览标出固定顶部/左侧 {_fixedLeading}px，底部/右侧 {_fixedTrailing}px；这些边缘将各保留一次，范围必须仅包含固定内容。确认后开始滚动。", null);
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
          _reverseReviewIssued = false;
          _appendedForCurrentStreak = true;
          _lastProgress = Environment.TickCount64;
          _scrollOutstanding = false;
          _noProgressAttempts = 0;
          ReportAccepted(_stitcher, frame);
          break;
        case ScrollAppendStatus.Duplicate:
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
            PauseForReview("检测到反向滚动：该帧未加入已收选段。请丢弃反向帧后，按所选方向滚回已收边界再继续；也可点击完成预览已有选段。", null);
            RunOnUi(() => _confirmButton.Content = "丢弃反向帧并继续");
          }
          break;
        case ScrollAppendStatus.NoOverlap:
          Fail(new InvalidDataException("没有足够重叠：已检查全部支持的位移，无法用正向纠偏恢复。本次尚未完成，未生成长图；请取消后减少滚动量重试。"));
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

    /// <summary>完成路径：先确保最后一段静止内容已上交（含最后一次滚动），再 BuildFrame。</summary>
    private void CompleteBuild(VerticalScrollStitcher stitcher)
    {
      CapturedFrame frame = stitcher.BuildFrame(_lifecycle.Token);
      AppLog.Info(
          $"scroll-capture 选段预览就绪：frames={stitcher.FrameCount}, " +
          $"size={frame.Width}x{frame.Height}, elapsedMs={(int)_elapsed.ElapsedMilliseconds}");
      VerticalScrollStitcher snapshot = stitcher;
      RunOnUi(() =>
      {
        _statusText.Text = $"已收选段：{snapshot.FrameCount} 帧，等待预览确认";
        _startButton.IsEnabled = false;
        _finishButton.IsEnabled = false;
      });
      _finalFrame = frame;
      _reviewPending = true;
      byte[]? preview = EncodePreviewBmp(frame);
      RunOnUi(() =>
      {
        _statusText.Text = $"选段预览：{frame.Width} × {frame.Height}px。请核对方向、固定栏和接缝；只包含本次已收范围，不保证整个文档已到末尾。";
        _errorText.Visibility = Visibility.Collapsed;
        _confirmButton.Content = "确认选段并送入编辑器";
        _confirmButton.Visibility = Visibility.Visible;
        _confirmButton.IsEnabled = false;
        _previewTask = RenderFinalPreviewAsync(_previewTask, preview, frame);
      });
    }

    private void ReportAccepted(VerticalScrollStitcher stitcher, CapturedFrame frame)
    {
      string status = $"已收 {stitcher.FrameCount} 帧 · {stitcher.Width} × {stitcher.Height} px";
      CapturedFrame stitched = stitcher.BuildFrame(_lifecycle.Token);
      CapturedFrame previewFrame = stitcher.FrameCount == 1 ? MarkFixedEdges(frame) : stitched;
      byte[]? preview = EncodePreviewBmp(previewFrame);
      if (preview is null)
      {
        Fail(new InvalidOperationException("无法生成已收选段预览，本次未完成，请取消后重试。"));
        return;
      }
      RunOnUi(() =>
      {
        _statusText.Text = status;
        _finishButton.IsEnabled = !_reviewPending; // 待拼帧必须先完成自己的预览确认。
        UpdatePreview(preview, stitched.Width, stitched.Height);
      });
    }

    private CapturedFrame MarkFixedEdges(CapturedFrame frame)
    {
      if (_fixedLeading == 0 && _fixedTrailing == 0) return frame;
      byte[] pixels = (byte[])frame.Pixels.Clone();
      bool horizontal = _direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
      int extent = horizontal ? frame.Width : frame.Height;
      for (int y = 0; y < frame.Height; y++)
      {
        _lifecycle.Token.ThrowIfCancellationRequested();
        for (int x = 0; x < frame.Width; x++)
        {
          int axis = horizontal ? x : y;
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
          _previewImage.Source = bitmap;
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
        _startButton.IsEnabled = false;
        _finishButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _statusText.Text = "本次未完成，未生成长图：请查看原因后取消或关闭窗口。";
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
        _statusText.Text = "采集已暂停，等待明确确认；尚未生成成品。";
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
        _confirmButton.Content = pending is null ? "确认预览并继续" : "确认位移并校验拼接";
        _confirmButton.Visibility = Visibility.Visible;
        _confirmButton.IsEnabled = false;
        _finishButton.IsEnabled = pending is null && _initialPreviewConfirmed;
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
        if (bytes is null) { _confirmButton.IsEnabled = true; return; }
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
          _confirmButton.IsEnabled = true;
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
      if (_finalFrame is { } final)
      {
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
      _initialPreviewConfirmed = true;
      _finishButton.IsEnabled = _stitcher is not null;
      _reviewPending = false;
      _statusText.Text = "采集中：按所选方向滚动并停稳；可点击完成预览已收选段。";
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
        int delta = direction is ScrollCaptureDirection.Down or ScrollCaptureDirection.Left ? -120 : 120;
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
            input.Delta == (direction is ScrollCaptureDirection.Down or ScrollCaptureDirection.Left ? -120 : 120),
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
        _startButton = new Button(),
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
        Check((await final._completion.Task)?.Pixels.SequenceEqual(frame.Pixels) == true,
            "Confirmation must return the final decoded frame.");

        manual._stableStreak = StableComparisonsRequired;
        manual.HandleStable(frame);
        await manual._previewTask;
        Check(!manual._finishButton.IsEnabled, "Initial preview must precede finishing.");
        manual.OnConfirm(manual._confirmButton, new RoutedEventArgs());
        Check(manual._finishButton.IsEnabled, "Confirmed first frame must restore the finish button.");
        manual._appendedForCurrentStreak = false;
        manual.HandleStable(frame);
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
    internal static void VerifySelectionGeometrySelfCheck()
    {
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
          session._directionBox = new ComboBox();
          for (int index = 0; index < 4; index++) session._directionBox.Items.Add(index);
          session._directionBox.SelectedIndex = direction == ScrollCaptureDirection.Right ? 0 : 2;
          session._leadingBox = new NumberBox { Value = 0 };
          session._trailingBox = new NumberBox { Value = 0 };
          session._errorText = new TextBlock();
          session.OnStart(session, new RoutedEventArgs());
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
        session._stableStreak = StableComparisonsRequired;
        session.HandleStable(Frame(32));
        await session._previewTask;
        session.OnConfirm(session, new RoutedEventArgs());
        session._appendedForCurrentStreak = false;
        CapturedFrame reverse = Frame(12);
        session.HandleStable(reverse);
        await session._previewTask;
        if (session._pendingFrame is not null || session._offsetBox.Visibility != Visibility.Collapsed ||
            !Equals(session._confirmButton.Content, "丢弃反向帧并继续") || !session._reviewPending || session._automatic)
          throw new InvalidOperationException("A reverse frame must offer explicit discard/continue, never impossible positive correction.");
        if (session._stitcher?.FrameCount != 1 || !session._stitcher.BuildFrame().Pixels.SequenceEqual(Frame(32).Pixels))
          throw new InvalidOperationException("Reverse review must not change the accepted segment.");
        session.OnConfirm(session, new RoutedEventArgs());
        session._appendedForCurrentStreak = false; // 模拟控制窗过渡后重新取得稳定见证。
        session.HandleStable(reverse);
        if (session._reviewPending || session._pendingFrame is not null || session._completion.Task.IsCompleted)
          throw new InvalidOperationException("Discarding a reverse frame must resume manual capture without re-prompting on the same stable frame.");
        session._appendedForCurrentStreak = false;
        session.HandleStable(Frame(52));
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
      foreach ((int width, int height) in new[] { (32, 2160), (2160, 32), (32, 96) })
      {
        var session = CreateSelfCheckSession(width, height);
        try
        {
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
        finally
        {
          session._lifecycle.Cancel();
          await session._previewTask;
          session._pendingImage.Source = null;
          session._window.Close();
          session._lifecycle.Dispose();
        }
      }
    }

    internal static async Task VerifyNoOverlapStopsSelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++) pixels[y * 48 * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      var session = CreateSelfCheckSession();
      try
      {
        CapturedFrame first = Frame(0);
        session._stableStreak = StableComparisonsRequired;
        session.HandleStable(first);
        await session._previewTask;
        session.OnConfirm(session, new RoutedEventArgs());
        session._appendedForCurrentStreak = false;
        session.HandleStable(Frame(128));
        await session._previewTask;
        if (session._failure is null || session._pendingFrame is not null ||
            session._confirmButton.Visibility != Visibility.Collapsed || session._finishButton.IsEnabled)
          throw new InvalidOperationException("Exhaustive NoOverlap must terminate without an impossible correction or partial output.");
        if (session._stitcher?.FrameCount != 1 || !session._stitcher.BuildFrame().Pixels.SequenceEqual(first.Pixels))
          throw new InvalidOperationException("NoOverlap must preserve accepted pixels until cleanup.");
        session.OnConfirm(session, new RoutedEventArgs());
        session.OnFinish(session, new RoutedEventArgs());
        if (session._completion.Task.IsCompleted || session._finishRequested)
          throw new InvalidOperationException("A terminal NoOverlap cannot be confirmed or finished.");
        Exception original = session._failure;
        session.CancelFromUi();
        try { await session._completion.Task; throw new InvalidOperationException("NoOverlap returned partial output."); }
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
          session._stableStreak = StableComparisonsRequired;
          session.HandleStable(first);
          await session._previewTask;
          session.OnConfirm(session, new RoutedEventArgs());
          session._appendedForCurrentStreak = false;
          session.HandleStable(pending);
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
        session._stableStreak = StableComparisonsRequired;
        session.HandleStable(Periodic(96, 0));
        await session._previewTask;
        session.OnConfirm(session, new RoutedEventArgs());
        session._appendedForCurrentStreak = false;
        session.HandleStable(Periodic(96, 2));
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
        session._stableStreak = StableComparisonsRequired;
        session._appendedForCurrentStreak = false;
        session.HandleStable(next);
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
          session._stableStreak = StableComparisonsRequired;
          session.HandleStable(Periodic(96, 0));
          await session._previewTask;
          session.OnConfirm(session, new RoutedEventArgs());
          session._appendedForCurrentStreak = false;
          session.HandleStable(Periodic(96, 2));
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
      _statusText.Text = "就绪：选择方向、滚动模式和固定边缘，点击开始采样首帧。";
      _startButton.IsEnabled = true;
    }

    private void OnStart(object sender, RoutedEventArgs args)
    {
      if (_loop is not null || _failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted)
      {
        return; // 防止两轮采集同时运行。
      }

      int axis = _directionBox.SelectedIndex >= 2 ? _bounds.Width : _bounds.Height;
      if (!double.IsFinite(_leadingBox.Value) || !double.IsFinite(_trailingBox.Value) ||
          _leadingBox.Value != Math.Truncate(_leadingBox.Value) || _trailingBox.Value != Math.Truncate(_trailingBox.Value) ||
          _leadingBox.Value < 0 || _trailingBox.Value < 0 || _leadingBox.Value + _trailingBox.Value > axis - 64)
      {
        _errorText.Text = "固定范围必须是非负整数像素，且至少留下 64 像素滚动内容。";
        _errorText.Visibility = Visibility.Visible;
        return;
      }
      _direction = (ScrollCaptureDirection)_directionBox.SelectedIndex;
      _automatic = _automaticBox.IsChecked == true;
      _fixedLeading = (int)_leadingBox.Value;
      _fixedTrailing = (int)_trailingBox.Value;
      _options.Visibility = Visibility.Collapsed;
      _errorText.Visibility = Visibility.Collapsed;
      _startButton.IsEnabled = false;
      _statusText.Text = "采集中：请保持首帧静止，稍后确认预览。";
      SetForegroundWindow(_source); // 焦点归还源窗口，由用户手动滚动。
      _loop = Task.Run(RunLoopAsync);
    }

    private void OnFinish(object sender, RoutedEventArgs args)
    {
      if (_stitcher is null || _finishRequested || _failure is not null || _lifecycle.IsCancellationRequested || _completion.Task.IsCompleted)
      {
        return;
      }

      if (_pendingFrame is not null || !_initialPreviewConfirmed) return;
      HideReview();
      _reviewPending = false;
      _automatic = false;
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
      lock (_terminalGate)
      {
        _correctionAuthorization = null;
        if (_failure is not null) _completion.TrySetException(_failure);
        else _completion.TrySetResult(null);
      }
    }

    private void OnExternalCancelled()
    {
      _lifecycle.Cancel();
      lock (_terminalGate) _correctionAuthorization = null;
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
