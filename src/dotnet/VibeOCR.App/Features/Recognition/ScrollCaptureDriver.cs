using System.Runtime.InteropServices;
using VibeOCR.App.Services;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>在线程池中采集单个受守卫源窗口的画面并发送滚轮输入。</summary>
internal sealed class ScrollCaptureDriver(
    ScrollCaptureState state, PhysicalRectangle bounds, ScrollCaptureDriver.Source source,
    nint owner, nint controlHandle, nint previewHandle, ScrollCaptureMask? mask)
{
  private const int CaptureIntervalMs = 100;
  private const int ForegroundRestoreTimeoutMs = 5_000;

  private const uint GaRoot = 2;
  private readonly ScrollCaptureScrollInput.NativeScrollInput _scrollInput = new();
  internal string ScrollInputDiagnostic { get; private set; } = "not attempted";

  internal readonly record struct Source(nint Handle, PhysicalRectangle WindowBounds, uint Dpi, PhysicalRectangle Desktop);

  internal async Task RunAsync(
      Action<ScrollCaptureChange, CapturedFrame?> update,
      Action<Exception> fail)
  {
    try
    {
      await using var capture = new ScreenCaptureService(Guid.NewGuid());
      long finishForegroundDeadline = 0;
      void ValidateCurrentSource() => CheckSource(GetForegroundWindow());
      while (state.IsActive)
      {
        if (state.ElapsedMilliseconds >= ScrollCaptureState.TotalTimeoutMs)
        {
          fail(new InvalidOperationException("采集超过 3 分钟总时限，已停止；本次未完成，未生成长图。"));
          return;
        }
        if (state.NoProgressExpired)
        {
          ValidateCurrentSource();
          update(state.EndNoProgress(ValidateCurrentSource), null);
          await LogPublishedSegmentAsync(boundary: true);
          return;
        }
        if (state.Review is not null)
        {
          CheckSource(GetForegroundWindow());
          ScrollCaptureChange correction = state.ApplyCorrection(afterMatch: ValidateCurrentSource);
          update(correction, correction.Review?.Pending);
          if (correction.Update == ScrollCaptureUpdate.CorrectionAccepted) RestoreForeground(state, source.Handle);
          await Task.Delay(CaptureIntervalMs, state.Token);
          continue;
        }

        if (state.BeginFinishSampling())
        {
          RestoreForeground(state, source.Handle);
          finishForegroundDeadline = Environment.TickCount64 + ForegroundRestoreTimeoutMs;
        }
        nint foreground = GetForegroundWindow();
        CheckSource(foreground);
        if (foreground != source.Handle)
        {
          // 控制窗交互期间暂停采样和动态计时，仍执行窗口/遮挡守卫。
          state.PauseSampling();
          if (state.FinishSamplingStarted && Environment.TickCount64 >= finishForegroundDeadline)
          {
            fail(new InvalidOperationException("无法将源窗口恢复前台以完成收尾采样（前台切换被系统拒绝），请重试滚动截图。"));
            return;
          }
          await Task.Delay(CaptureIntervalMs, state.Token);
          continue;
        }

        CapturedFrame frame = capture.Capture(bounds, TimeSpan.FromMinutes(1));
        foreground = GetForegroundWindow();
        CheckSource(foreground);
        if (foreground != source.Handle)
        {
          state.PauseSampling();
          await Task.Delay(CaptureIntervalMs, state.Token);
          continue;
        }

        update(state.ObserveOwnedFrame(frame), frame);
        if (state.CanPublishFinishedSample)
        {
          if (state.PublishFinishedSample(ValidateCurrentSource)) await LogPublishedSegmentAsync(boundary: false);
          return;
        }

        if (state.CanScrollAutomatically)
        {
          ScrollCaptureChange stalled = state.CheckAutomaticProgress();
          if (stalled.Review is not null) update(stalled, null);
          else if (state.CanScrollAutomatically)
          {
            CheckSource(GetForegroundWindow());
            if (state.CanScrollAutomatically)
            {
              if (!SendScroll())
              {
                fail(new InvalidOperationException("标准滚轮输入被拒绝：请松开鼠键及 Ctrl/Shift/Alt，并确认目标支持滚轮且未提权；本次未完成。"));
                return;
              }
              state.WheelSent();
            }
          }
        }
        await Task.Delay(CaptureIntervalMs, state.Token);
      }
    }
    catch (SourceChangedException error) { fail(new InvalidOperationException(error.Message)); }
    catch (OperationCanceledException) when (!state.IsActive) { }
    catch (Exception error)
    {
      fail(new InvalidOperationException($"滚动截图采集失败：{error.Message}", error));
    }
  }

  private sealed class SourceChangedException(string message) : Exception(message);

  private async Task LogPublishedSegmentAsync(bool boundary)
  {
    if (state.Completion.IsCompletedSuccessfully && await state.Completion is { } frame)
      AppLog.Info(
          $"scroll-capture 选段交付：frames={state.Stitcher?.FrameCount}, size={frame.Width}x{frame.Height}, " +
          $"boundary={boundary}, elapsedMs={state.ElapsedMilliseconds}");
  }

  private void CheckSource(nint foreground)
  {
    if (GuardBeforeCapture(foreground) is { } error) throw new SourceChangedException(error);
  }

  internal static void RestoreForeground(ScrollCaptureState state, nint source)
  {
    if (state.IsActive) SetForegroundWindow(source);
  }

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetForegroundWindow(nint window);

  internal string? GuardBeforeCapture() => GuardBeforeCapture(GetForegroundWindow());

  /// <summary>WindowFromPoint(选区中心) → GetAncestor(GA_ROOT) 确定源窗口并记录几何基准。</summary>
  internal static string? FindSource(nint owner, PhysicalRectangle bounds, out Source source)
  {
    source = new Source(0, default, 0, SmartScreenCandidates.CurrentDesktop());
    var center = new Point { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
    nint hit = WindowFromPoint(center);
    nint root = hit == 0 ? 0 : GetAncestor(hit, GaRoot);
    if (root == 0 || root == owner)
    {
      return "未能识别选区中心的源窗口，请重新框选。";
    }

    if (SmartScreenCandidates.TryReadWindowBounds(root) is not { } windowRect)
    {
      return "源窗口不可见或已最小化，无法采集。";
    }

    if (SmartScreenCandidates.Intersect(windowRect, bounds) != bounds)
    {
      return "选区超出源窗口范围，请重新框选源窗口内容区域。";
    }

    uint dpi = GetDpiForWindow(root);
    if (dpi == 0)
    {
      return "无法读取源窗口 DPI，请重试。";
    }

    source = source with { Handle = root, WindowBounds = windowRect, Dpi = dpi };
    return null;
  }

  /// <summary>采集前后守卫：前台归属、控制窗未压选区、源窗口几何、上方遮挡。</summary>
  private string? GuardBeforeCapture(nint foreground)
  {
    // 主动收尾切换时 Windows 可短暂返回 NULL；循环在原截止时间内等待，期间不采集。
    if (foreground != source.Handle && foreground != controlHandle &&
        !(foreground == 0 && state.FinishSamplingStarted))
    {
      AppLog.Warn($"scroll-capture foreground guard: actual={foreground}, source={source.Handle}, control={controlHandle}, finishing={state.FinishSamplingStarted}");
      return "源窗口已失去前台（其他窗口被激活），已停止采集。";
    }

    if (SmartScreenCandidates.Intersect(ReadControlBounds(controlHandle, bounds), bounds) is not null)
    {
      return "控制窗被移动到选区内，已停止采集。";
    }

    string? sourceError = ValidateSourceState();
    if (sourceError is not null)
    {
      return sourceError;
    }

    return FindOccluder();
  }

  private string? ValidateSourceState()
  {
    if (source.Handle == 0 || !IsWindow(source.Handle))
    {
      return "源窗口已关闭，已停止采集。";
    }

    if (SmartScreenCandidates.TryReadWindowBounds(source.Handle) is not { } rect)
    {
      return "源窗口不可见或已最小化，已停止采集。";
    }

    if (rect != source.WindowBounds)
    {
      return "源窗口位置或大小发生变化，已停止采集。";
    }

    if (GetDpiForWindow(source.Handle) != source.Dpi)
    {
      return "源窗口 DPI 发生变化，已停止采集。";
    }

    if (SmartScreenCandidates.CurrentDesktop() != source.Desktop)
    {
      return "显示器布局发生变化，已停止采集。";
    }

    return null;
  }

  internal static PhysicalRectangle ReadControlBounds(nint controlHandle, PhysicalRectangle bounds)
  {
    if (controlHandle != 0 && GetWindowRect(controlHandle, out RectL rect) &&
        rect.Right > rect.Left && rect.Bottom > rect.Top)
    {
      return new PhysicalRectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    return bounds; // 读不到控制窗矩形时按“覆盖选区”处理，宁可停止采集。
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
      if (handle == source.Handle)
      {
        return false;
      }

      if (handle == controlHandle || handle == previewHandle || mask?.Contains(handle) == true || handle == owner)
      {
        return true;
      }

      if (SmartScreenCandidates.TryReadWindowBounds(handle) is not { } rect)
      {
        return true;
      }

      if (SmartScreenCandidates.Intersect(rect, bounds) is null)
      {
        return true;
      }

      occluded = "有其他窗口覆盖选区，已停止采集。";
      return false;
    }, 0);
    return occluded;
  }

  private bool SendScroll()
  {
    var center = new ScrollCaptureScrollInput.Point
    {
      X = bounds.X + bounds.Width / 2,
      Y = bounds.Y + bounds.Height / 2,
    };
    bool sent = ScrollCaptureScrollInput.TrySend(
        _scrollInput, center, source.Handle, state.Direction);
    ScrollInputDiagnostic = sent ? "system wheel accepted" : "system wheel refused";
    return sent;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct RectL
  {
    public int Left, Top, Right, Bottom;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Point
  {
    public int X, Y;
  }

  private delegate bool EnumWindowsProc(nint window, nint parameter);

  [DllImport("user32.dll")]
  private static extern nint WindowFromPoint(Point point);

  [DllImport("user32.dll")]
  private static extern nint GetAncestor(nint window, uint flags);

  [DllImport("user32.dll")]
  internal static extern uint GetDpiForWindow(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindow(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static extern bool GetWindowRect(nint window, out RectL rect);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
}
