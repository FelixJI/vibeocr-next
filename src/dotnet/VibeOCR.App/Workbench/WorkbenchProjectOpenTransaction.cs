using VibeOCR.App.Services;

namespace VibeOCR.App.Workbench;

/// <summary>打开工程 pending 确认事务的身份/状态冲突（迟到、绑定不符、过期或已终结）。</summary>
public sealed class WorkbenchProjectOpenConflictException(string message) : Exception(message);

/// <summary>ack 必须绑定的旧会话身份：会话 id、修订与宿主文档代际。</summary>
public sealed record WorkbenchProjectOpenBinding(Guid SessionId, long Revision, long Generation);

public sealed record WorkbenchProjectPendingOpen(
  Guid PendingId,
  WorkbenchProjectOpenBinding ExpectedBinding,
  DateTimeOffset Deadline);

/// <summary>
/// #233 打开工程的 pending→Web 全部 decode→ack→切换事务：宿主完整有界
/// 解析并发布 pending 描述符后在此登记；Web 后台解码全部资源并回 ack，
/// ack 绑定旧 session/generation/revision 与 pending 身份，宿主验证通过才
/// 允许切换会话。任一时刻最多 1 项 pending；30 秒到期明确取消，不自动
/// 重试；取消/超时/迟到/绑定不符按 reason 精确回收 pending（release 回调
/// 恰好执行一次），旧工程与历史保留。
/// 订阅通知与 release 在锁外执行：订阅方抛异常不阻止既定资源回收
/// （finally 保证 release），异常按调用方路径原样传播；计时器线程路径
/// 经 AppLog 汇总，不使异常终结进程，也不吞调用方路径的产品错误。
/// release 可能从计时器线程触发，回调必须线程安全且幂等。
/// </summary>
public sealed class WorkbenchProjectOpenTransaction : IDisposable
{
  public static readonly TimeSpan AckDeadline = TimeSpan.FromSeconds(30);

  private readonly object gate = new();
  private readonly TimeProvider timeProvider;
  private Pending? pending;
  private bool disposed;

  /// <summary>pending 终结（未切换、资源已回收）观察点：reason ∈
  /// cancelled/timeout/replaced；ack 成功不触发。宿主接线用它发布回执。</summary>
  public event Action<Guid, string>? PendingTerminated;

  public WorkbenchProjectOpenTransaction(TimeProvider? timeProvider = null)
  {
    this.timeProvider = timeProvider ?? TimeProvider.System;
  }

  internal bool HasPending
  {
    get
    {
      lock (gate)
      {
        return pending is not null;
      }
    }
  }

  /// <summary>登记一项 pending：已有 pending 被替换并回收（reason replaced）。
  /// release 回调承担该 pending 的全部资源释放（broker 租约、解包目录等）。</summary>
  public WorkbenchProjectPendingOpen Begin(
    WorkbenchProjectOpenBinding expectedBinding,
    Action? release)
  {
    ArgumentNullException.ThrowIfNull(expectedBinding);
    Termination? replaced;
    WorkbenchProjectPendingOpen open;
    lock (gate)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      replaced = TerminateLocked("replaced");
      open = new(
        Guid.NewGuid(),
        expectedBinding,
        timeProvider.GetUtcNow() + AckDeadline);
      ITimer timer = timeProvider.CreateTimer(
        _ => Expire(open.PendingId),
        null,
        AckDeadline,
        Timeout.InfiniteTimeSpan);
      pending = new Pending(open, release, timer);
    }
    CompleteTermination(replaced);
    return open;
  }

  /// <summary>Web 全部 decode 成功后的 ack：pending 身份、旧会话绑定全部
  /// 匹配且时钟未过期限才提交（资源所有权移交切换方，release 不再执行）；
  /// 绑定不符或已过期限（即使计时器尚未触发）按对应 reason 精确回收该
  /// pending 并抛冲突；未知/迟到 id 抛冲突且不影响当前 pending。</summary>
  public WorkbenchProjectPendingOpen Acknowledge(
    Guid pendingId,
    WorkbenchProjectOpenBinding ackedBinding)
  {
    ArgumentNullException.ThrowIfNull(ackedBinding);
    Termination? terminated = null;
    bool deadlinePassed = false;
    WorkbenchProjectPendingOpen open;
    lock (gate)
    {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (pending is not { } current || current.Open.PendingId != pendingId)
      {
        throw new WorkbenchProjectOpenConflictException(
          "Project open acknowledgement is late or unknown.");
      }
      open = current.Open;
      deadlinePassed = timeProvider.GetUtcNow() >= open.Deadline;
      if (open.ExpectedBinding == ackedBinding && !deadlinePassed)
      {
        current.Timer.Dispose();
        pending = null;
        return open;
      }
      // 绑定不符→cancelled；时钟过期限而计时器未触发→timeout（同一回收路径）。
      terminated = TerminateLocked(deadlinePassed ? "timeout" : "cancelled");
    }
    CompleteTermination(terminated);
    throw new WorkbenchProjectOpenConflictException(
      deadlinePassed
        ? "Project open acknowledgement arrived after the deadline."
        : "Project open acknowledgement does not match the pending session binding.");
  }

  /// <summary>取消一项 pending（用户取消/部分资源失败/保存旧工程）；幂等，
  /// 返回是否确实取消了该 id。订阅方异常传播给取消调用方，release 仍执行。</summary>
  public bool Cancel(Guid pendingId)
  {
    Termination? terminated;
    lock (gate)
    {
      if (pending is not { } current || current.Open.PendingId != pendingId)
      {
        return false;
      }
      terminated = TerminateLocked("cancelled");
    }
    CompleteTermination(terminated);
    return true;
  }

  public void Dispose()
  {
    Termination? terminated;
    lock (gate)
    {
      if (disposed)
      {
        return;
      }
      disposed = true;
      terminated = TerminateLocked("cancelled");
    }
    CompleteTermination(terminated);
  }

  private void Expire(Guid pendingId)
  {
    Termination? terminated;
    lock (gate)
    {
      if (pending is not { } current || current.Open.PendingId != pendingId)
      {
        return;
      }
      terminated = TerminateLocked("timeout");
    }
    // 计时器线程边界：通知/release 的异常经宿主既有错误汇（AppLog）记录，
    // 不终结进程；调用方路径（Begin/Cancel/Acknowledge/Dispose）不受此影响。
    try
    {
      CompleteTermination(terminated);
    }
    catch (Exception error)
    {
      AppLog.Error("Project open pending termination failed on the deadline timer.", error);
    }
  }

  /// <summary>调用方持锁：终结当前 pending（停表 + 置空），不执行任何外部回调。</summary>
  private Termination? TerminateLocked(string reason)
  {
    if (pending is not { } current)
    {
      return null;
    }
    current.Timer.Dispose();
    pending = null;
    return new Termination(current.Release, current.Open.PendingId, reason);
  }

  /// <summary>锁外执行终结回调：先通知（异常向上/向调用方传播），finally
  /// 保证既定 release 一定执行且恰好一次。</summary>
  private void CompleteTermination(Termination? terminated)
  {
    if (terminated is not { } term)
    {
      return;
    }
    try
    {
      PendingTerminated?.Invoke(term.PendingId, term.Reason);
    }
    finally
    {
      term.Release?.Invoke();
    }
  }

  private sealed record Termination(
    Action? Release,
    Guid PendingId,
    string Reason);

  private sealed record Pending(
    WorkbenchProjectPendingOpen Open,
    Action? Release,
    ITimer Timer);
}
