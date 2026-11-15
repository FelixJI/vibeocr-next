using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

internal enum ScrollCapturePhase
{
  Ready,
  WaitingForStability,
  Capturing,
  Reviewing,
  ReviewingSegment,
  Completed,
  UserCancelled,
  ExternalCancelled,
  Failed,
}

internal enum ScrollCaptureUpdate
{
  None,
  FirstAccepted,
  Accepted,
  OverlapRestored,
  OverlapMissing,
  ReverseReview,
  OffsetReview,
  AutomaticReview,
  SegmentReview,
  CorrectionAccepted,
  CorrectionRejected,
  CorrectionLimit,
}

internal sealed record ScrollCaptureReview(CapturedFrame? Pending = null, CapturedFrame? Segment = null);

internal readonly record struct ScrollCaptureChange(ScrollCaptureUpdate Update, ScrollCaptureReview? Review = null);

/// <summary>串行处理采集帧，复核授权和终态发布由同一把锁裁决。</summary>
internal sealed class ScrollCaptureState(CancellationToken external, Func<long>? clock = null) : IDisposable
{
  internal const int TotalTimeoutMs = 180_000;
  internal const int NoProgressTimeoutMs = 30_000;
  internal const int DynamicContentTimeoutMs = 5_000;
  internal const string DeadlineMessage = "已到 3 分钟总时限（包含预览与纠偏等待），本次未完成，未生成长图。";
  private const int AutomaticNoProgressAttempts = 3;

  private readonly CancellationTokenSource _lifecycle = new();
  private readonly TaskCompletionSource<CapturedFrame?> _completion =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
  private volatile ScrollCapturePhase _phase;
  private volatile Exception? _failure;
  private volatile ScrollCaptureReview? _review;
  private Correction? _correction;
  private CapturedFrame? _previous;
  private long _startedAt, _lastStableWitness, _lastProgress;
  private bool _acceptedForCurrentStreak, _scrollOutstanding, _reverseReviewIssued;
  private int _noProgressAttempts;

  private sealed class Correction(ScrollCaptureReview review, int offset)
  {
    public ScrollCaptureReview Review { get; } = review;
    public int Offset { get; } = offset;
    public bool Claimed { get; set; }
  }

  internal object Gate { get; } = new();
  internal CancellationToken Token => _lifecycle.Token;
  internal Task<CapturedFrame?> Completion => _completion.Task;
  internal Exception? Failure => _failure;
  internal ScrollCapturePhase Phase => _phase;
  internal ScrollCaptureReview? Review => _review;
  internal VerticalScrollStitcher? Stitcher { get; private set; }
  internal ScrollCaptureDirection Direction { get; private set; }
  internal int FixedLeading { get; private set; }
  internal int FixedTrailing { get; private set; }
  internal bool Automatic { get; private set; }
  internal bool OverlapMissing { get; private set; }
  internal bool FinishRequested { get; private set; }
  internal bool FinishSamplingStarted { get; private set; }
  internal bool IsActive => Failure is null && !_lifecycle.IsCancellationRequested &&
      !Completion.IsCompleted && !external.IsCancellationRequested;
  internal bool CanPresentCaptureProgress => _review is null && !FinishRequested;
  internal bool CanFinish => IsActive && Stitcher is not null && !FinishRequested && !OverlapMissing &&
      _review is not { Pending: not null } and not { Segment: not null };
  internal bool NoProgressExpired
  {
    get
    {
      lock (Gate)
        return IsActive && _phase != ScrollCapturePhase.Ready && _review is null &&
            Now - _lastProgress >= NoProgressTimeoutMs;
    }
  }
  internal long ElapsedMilliseconds => Now - _startedAt;
  internal bool HasReleasedFrames => Stitcher is null && _previous is null && _review is null && _correction is null;
  internal bool HasCorrection => _correction is not null;
  private long Now => clock?.Invoke() ?? Environment.TickCount64;

  internal bool Start(ScrollCaptureDirection direction, bool automatic, int leading, int trailing)
  {
    lock (Gate)
    {
      if (!IsActive || _phase != ScrollCapturePhase.Ready) return false;
      Direction = direction;
      Automatic = automatic;
      FixedLeading = leading;
      FixedTrailing = trailing;
      _startedAt = _lastProgress = _lastStableWitness = Now;
      _phase = ScrollCapturePhase.WaitingForStability;
      return true;
    }
  }

  internal void PauseSampling()
  {
    lock (Gate)
    {
      if (IsActive && _review is null) ResetStableWindow();
    }
  }

  private void ResetStableWindow()
  {
    _previous = null;
    _lastStableWitness = Now;
    _phase = ScrollCapturePhase.WaitingForStability;
  }

  // 合成输入可以继续复用原缓冲区，原生驱动直接转移新采样帧的所有权。
  internal ScrollCaptureChange ObserveFrame(CapturedFrame frame) =>
      ObserveOwnedFrame(frame with { Pixels = (byte[])frame.Pixels.Clone() });

  internal ScrollCaptureChange ObserveOwnedFrame(CapturedFrame frame, Action? beforeFailureNotification = null)
  {
    if (!IsActive || _review is not null || _phase == ScrollCapturePhase.Ready) return default;
    long now = Now;
    CapturedFrame? previous = _previous;
    bool identical = previous is not null &&
        previous.Width == frame.Width && previous.Height == frame.Height &&
        previous.Stride == frame.Stride && previous.PixelFormat == frame.PixelFormat &&
        frame.Pixels.AsSpan().SequenceEqual(previous.Pixels);
    bool shouldMatch;
    lock (Gate)
    {
      if (!IsActive || _review is not null) return default;
      _previous = frame;
      _phase = identical ? ScrollCapturePhase.Capturing : ScrollCapturePhase.WaitingForStability;
      if (identical) _lastStableWitness = now;
      else _acceptedForCurrentStreak = false;
      shouldMatch = !_acceptedForCurrentStreak && (identical || Stitcher is not null);
    }
    if (!shouldMatch)
    {
      if (!identical) CheckDynamicContent(now);
      return default;
    }

    if (Stitcher is null)
    {
      VerticalScrollStitcher stitcher;
      try { stitcher = new VerticalScrollStitcher(frame, Direction, FixedLeading, FixedTrailing); }
      catch (InvalidDataException error)
      {
        Fail(new InvalidDataException(
            "首帧沿滚动方向没有可辨识的内容变化，无法定位滚动偏移；请扩大选区或选择内容更丰富的区域，已停止采集。",
            error));
        return default;
      }
      lock (Gate)
      {
        if (!IsActive) return default;
        Stitcher = stitcher;
        _acceptedForCurrentStreak = true;
        _lastProgress = Now;
        return new(ScrollCaptureUpdate.FirstAccepted);
      }
    }

    ScrollAppendStatus status = Stitcher.Append(frame, Token).Status;
    ScrollCaptureChange change = default;
    Exception? errorToReport = null;
    lock (Gate)
    {
      if (!IsActive) return default;
      // 重绘中的不匹配帧不终止采集；动态超时仍适用。
      if (identical || status is ScrollAppendStatus.Added or ScrollAppendStatus.Duplicate or ScrollAppendStatus.LimitReached)
      {
        switch (status)
        {
          case ScrollAppendStatus.Added:
            OverlapMissing = false;
            _lastStableWitness = _lastProgress = Now;
            _reverseReviewIssued = false;
            _acceptedForCurrentStreak = true;
            _scrollOutstanding = false;
            _noProgressAttempts = 0;
            change = new(ScrollCaptureUpdate.Accepted);
            break;
          case ScrollAppendStatus.Duplicate:
            if (OverlapMissing) change = new(ScrollCaptureUpdate.OverlapRestored);
            OverlapMissing = false;
            _reverseReviewIssued = false;
            _acceptedForCurrentStreak = true;
            break;
          case ScrollAppendStatus.Reverse:
            StopAutomatic();
            _acceptedForCurrentStreak = true;
            if (!_reverseReviewIssued)
            {
              _reverseReviewIssued = true;
              change = OpenReview(ScrollCaptureUpdate.ReverseReview);
            }
            break;
          case ScrollAppendStatus.NoOverlap:
            StopAutomatic();
            OverlapMissing = true;
            _acceptedForCurrentStreak = true;
            change = new(ScrollCaptureUpdate.OverlapMissing);
            break;
          case ScrollAppendStatus.Ambiguous:
            StopAutomatic();
            change = OpenReview(ScrollCaptureUpdate.OffsetReview, frame);
            break;
          case ScrollAppendStatus.FixedContentChanged:
            errorToReport = new InvalidDataException("用户指定的固定边缘发生变化，不能安全移除；本次未完成，请重新框选固定范围。");
            break;
          case ScrollAppendStatus.LowTexture:
            errorToReport = new InvalidDataException("选区内容纹理不足，无法定位滚动偏移；请扩大选区或选择内容更丰富的区域，已停止采集。");
            break;
          case ScrollAppendStatus.LimitReached:
            errorToReport = new InvalidDataException("已达到拼接上限（像素/尺寸/帧数限制）；请缩小选区或减少滚动量，已停止采集。");
            break;
          default:
            errorToReport = new InvalidDataException($"拼接器返回未知状态 {status}，已停止采集。");
            break;
        }
      }
      if (errorToReport is not null) RecordFailure(errorToReport);
    }
    if (errorToReport is not null)
    {
      try { beforeFailureNotification?.Invoke(); }
      finally { _lifecycle.Cancel(); }
    }
    if (!identical) CheckDynamicContent(now);
    return change;
  }

  private void CheckDynamicContent(long sampledAt)
  {
    if (sampledAt - _lastStableWitness > DynamicContentTimeoutMs)
      Fail(new InvalidDataException("选区内容持续变化（动态内容），始终未能稳定，已停止采集。"));
  }

  private void StopAutomatic()
  {
    Automatic = false;
    _scrollOutstanding = false;
  }

  internal bool TryRequestFinish()
  {
    lock (Gate)
    {
      if (!CanFinish) return false;
      _review = null;
      StopAutomatic();
      FinishRequested = true;
      _phase = ScrollCapturePhase.WaitingForStability;
      return true;
    }
  }

  internal bool BeginFinishSampling()
  {
    lock (Gate)
    {
      if (!IsActive || !FinishRequested || FinishSamplingStarted) return false;
      FinishSamplingStarted = true;
      ResetStableWindow();
      _acceptedForCurrentStreak = false;
      return true;
    }
  }

  internal bool CanPublishFinishedSample => IsActive && _phase == ScrollCapturePhase.Capturing &&
      FinishSamplingStarted && Stitcher is not null && _acceptedForCurrentStreak && !OverlapMissing;

  internal bool CanScrollAutomatically => IsActive && _phase == ScrollCapturePhase.Capturing &&
      Automatic && !FinishRequested && _acceptedForCurrentStreak;

  internal ScrollCaptureChange CheckAutomaticProgress()
  {
    lock (Gate)
    {
      if (!CanScrollAutomatically || !_scrollOutstanding || ++_noProgressAttempts < AutomaticNoProgressAttempts)
        return default;
      StopAutomatic();
      return OpenReview(ScrollCaptureUpdate.AutomaticReview);
    }
  }

  internal void WheelSent()
  {
    lock (Gate)
    {
      if (!CanScrollAutomatically) return;
      _scrollOutstanding = true;
      ResetStableWindow();
      _acceptedForCurrentStreak = false;
    }
  }

  internal void RejectAutomaticScroll()
  {
    lock (Gate)
    {
      if (!CanScrollAutomatically) return;
      RecordFailure(new InvalidOperationException(
          "标准滚轮输入被拒绝：请松开鼠键及 Ctrl/Shift/Alt，并确认目标支持滚轮且未提权；本次未完成。"));
    }
    _lifecycle.Cancel();
  }

  internal ScrollCaptureChange OpenReview(
      ScrollCaptureUpdate update, CapturedFrame? pending = null, CapturedFrame? segment = null)
  {
    lock (Gate)
    {
      if (!IsActive) return default;
      _correction = null;
      _review = new ScrollCaptureReview(pending, segment);
      _phase = segment is null ? ScrollCapturePhase.Reviewing : ScrollCapturePhase.ReviewingSegment;
      return new(update, _review);
    }
  }

  internal bool IsCurrentReview(ScrollCaptureReview review)
  {
    lock (Gate) return IsActive && ReferenceEquals(_review, review) && _correction is null;
  }

  internal bool ResumeReview(ScrollCaptureReview review)
  {
    lock (Gate)
    {
      if (!IsCurrentReview(review) || review.Pending is not null || review.Segment is not null) return false;
      ResetStableWindow();
      _lastProgress = Now;
      _review = null;
      return true;
    }
  }

  internal bool AuthorizeCorrection(ScrollCaptureReview review, int offset)
  {
    lock (Gate)
    {
      if (!IsCurrentReview(review) || review.Pending is null || Stitcher is null ||
          offset < 1 || offset > Stitcher.MaximumOffset) return false;
      _correction = new Correction(review, offset);
      return true;
    }
  }

  internal ScrollCaptureChange ApplyCorrection(Action? afterClaim = null, Action? afterMatch = null)
  {
    Correction authorization;
    VerticalScrollStitcher stitcher;
    lock (Gate)
    {
      if (!IsActive || Stitcher is null || _correction is not { Claimed: false } request ||
          !ReferenceEquals(request.Review, _review)) return default;
      authorization = request;
      authorization.Claimed = true;
      stitcher = Stitcher;
    }
    try
    {
      afterClaim?.Invoke();
      ScrollAppendStatus status = stitcher.AppendWithOffset(authorization.Review.Pending!, authorization.Offset, Token).Status;
      afterMatch?.Invoke();
      lock (Gate)
      {
        if (!IsActive || !ReferenceEquals(_correction, authorization) ||
            !ReferenceEquals(_review, authorization.Review)) return default;
        _correction = null;
        if (status != ScrollAppendStatus.Added)
        {
          return new(status == ScrollAppendStatus.LimitReached
              ? ScrollCaptureUpdate.CorrectionLimit : ScrollCaptureUpdate.CorrectionRejected, _review);
        }

        _lastProgress = Now;
        ResetStableWindow();
        _acceptedForCurrentStreak = false;
        ScrollCaptureReview? accepted = _review;
        _review = null;
        return new(ScrollCaptureUpdate.CorrectionAccepted, accepted);
      }
    }
    finally
    {
      lock (Gate)
      {
        if (ReferenceEquals(_correction, authorization)) _correction = null;
      }
    }
  }

  internal ScrollCaptureChange EndNoProgress(Action? validateSource = null)
  {
    if (!NoProgressExpired) return default;
    if (Stitcher is null)
    {
      Fail(new InvalidOperationException("30 秒未能捕获稳定首帧，已停止；本次未完成，未生成长图。"));
      return default;
    }
    CapturedFrame frame = Stitcher.BuildFrame(Token);
    validateSource?.Invoke();
    if (OverlapMissing) return OpenReview(ScrollCaptureUpdate.SegmentReview, segment: frame);
    TryPublish(frame, null);
    return default;
  }

  internal bool PublishFinishedSample(Action validateSource)
  {
    if (!CanPublishFinishedSample) return false;
    CapturedFrame frame = Stitcher!.BuildFrame(Token);
    validateSource();
    return TryPublish(frame, null);
  }

  internal bool ConfirmSegment(ScrollCaptureReview review) =>
      review.Segment is { } segment && TryPublish(segment, review);

  private bool TryPublish(CapturedFrame frame, ScrollCaptureReview? review)
  {
    lock (Gate)
    {
      if (Completion.IsCompleted || Failure is not null || _lifecycle.IsCancellationRequested) return false;
      if (external.IsCancellationRequested)
      {
        CompleteCancellation();
      }
      else if (_phase != ScrollCapturePhase.Ready && ElapsedMilliseconds >= TotalTimeoutMs)
      {
        RecordFailure(new InvalidOperationException(DeadlineMessage));
      }
      else
      {
        if (!ReferenceEquals(_review, review)) return false;
        _phase = ScrollCapturePhase.Completed;
        _completion.SetResult(frame);
        return true;
      }
    }
    _lifecycle.Cancel();
    return false;
  }

  internal void Fail(Exception error)
  {
    lock (Gate)
    {
      if (Completion.IsCompleted || Failure is not null || _lifecycle.IsCancellationRequested) return;
      RecordFailure(error);
    }
    _lifecycle.Cancel();
  }

  private void RecordFailure(Exception error)
  {
    _phase = ScrollCapturePhase.Failed;
    _failure = error;
    _correction = null;
  }

  internal void Cancel()
  {
    lock (Gate) CompleteCancellation();
    _lifecycle.Cancel();
  }

  private void CompleteCancellation()
  {
    if (Completion.IsCompleted) return;
    _correction = null;
    if (Failure is not null) _completion.SetException(Failure);
    else if (external.IsCancellationRequested)
    {
      _phase = ScrollCapturePhase.ExternalCancelled;
      _completion.SetCanceled(external);
    }
    else
    {
      _phase = ScrollCapturePhase.UserCancelled;
      _completion.SetResult(null);
    }
  }

  internal void Stop() => _lifecycle.Cancel();

  public void Dispose()
  {
    _lifecycle.Dispose();
    Stitcher = null;
    _previous = null;
    _review = null;
    _correction = null;
  }
}
