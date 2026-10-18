using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>#233 打开工程 pending→ack→切换事务：提交/绑定校验/取消/超时/
/// 迟到/替换/销毁的精确回收契约（release 恰好一次、旧工程语义由调用方保持）。</summary>
public sealed class WorkbenchProjectOpenTransactionTests
{
  private static readonly WorkbenchProjectOpenBinding Binding =
    new(Guid.NewGuid(), 7, 3);

  [Fact]
  public void AckWithMatchingBindingCommitsWithoutRelease()
  {
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction();
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    WorkbenchProjectPendingOpen committed = transaction.Acknowledge(
      open.PendingId, Binding with { });

    Assert.Equal(open.PendingId, committed.PendingId);
    Assert.Equal(Binding, committed.ExpectedBinding);
    Assert.False(transaction.HasPending);
    Assert.Equal(0, releases);
  }

  [Fact]
  public void AckWithWrongBindingReleasesPendingAndThrows()
  {
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction();
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    WorkbenchProjectOpenConflictException error = Assert.Throws<
      WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(open.PendingId, Binding with { Revision = Binding.Revision + 1 }));

    Assert.Contains("binding", error.Message);
    Assert.Equal(1, releases);
    Assert.False(transaction.HasPending);
    // 已回收后的重试 ack 是迟到：不再触发 release（恰好一次）。
    Assert.Throws<WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(open.PendingId, Binding));
    Assert.Equal(1, releases);
  }

  [Fact]
  public void UnknownPendingAckDoesNotDisturbCurrentPending()
  {
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction();
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    Assert.Throws<WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(Guid.NewGuid(), Binding));
    Assert.True(transaction.HasPending);
    Assert.Equal(0, releases);

    WorkbenchProjectPendingOpen committed = transaction.Acknowledge(open.PendingId, Binding);
    Assert.Equal(open.PendingId, committed.PendingId);
    Assert.Equal(0, releases);
  }

  [Fact]
  public void TimeoutReleasesPendingExactlyOnce()
  {
    AdjustableTimeProvider clock = new();
    int releases = 0;
    string? reason = null;
    Guid? terminated = null;
    using var transaction = new WorkbenchProjectOpenTransaction(clock);
    transaction.PendingTerminated += (id, why) => { terminated = id; reason = why; };
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    clock.Advance(WorkbenchProjectOpenTransaction.AckDeadline);

    Assert.Equal(1, releases);
    Assert.Equal("timeout", reason);
    Assert.Equal(open.PendingId, terminated);
    Assert.False(transaction.HasPending);
    // 超时后的 ack 是迟到：拒绝且不再重复回收。
    Assert.Throws<WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(open.PendingId, Binding));
    Assert.Equal(1, releases);
  }

  [Fact]
  public void TimeoutBeforeDeadlineKeepsPending()
  {
    AdjustableTimeProvider clock = new();
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction(clock);
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    clock.AdvanceClock(WorkbenchProjectOpenTransaction.AckDeadline - TimeSpan.FromSeconds(1));

    Assert.True(transaction.HasPending);
    Assert.Equal(0, releases);
    Assert.Equal(open.PendingId, transaction.Acknowledge(open.PendingId, Binding).PendingId);
    Assert.Equal(0, releases);
  }

  [Fact]
  public void AcknowledgeAfterDeadlineWithoutTimerRejectsAsTimeout()
  {
    AdjustableTimeProvider clock = new();
    int releases = 0;
    string? reason = null;
    using var transaction = new WorkbenchProjectOpenTransaction(clock);
    transaction.PendingTerminated += (_, why) => reason = why;
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    // 时钟已过期限但计时器未调度：ack 必须按超时拒绝，不得因 timer 延迟而提交。
    clock.AdvanceClock(WorkbenchProjectOpenTransaction.AckDeadline);

    WorkbenchProjectOpenConflictException error = Assert.Throws<
      WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(open.PendingId, Binding));
    Assert.Contains("deadline", error.Message);
    Assert.Equal("timeout", reason);
    Assert.Equal(1, releases);
    Assert.False(transaction.HasPending);
  }

  [Fact]
  public void SubscriberExceptionStillReleasesPendingAndPropagates()
  {
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction();
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);
    transaction.PendingTerminated += (_, _) => throw new InvalidOperationException("subscriber");

    InvalidOperationException error = Assert.Throws<InvalidOperationException>(
      () => transaction.Cancel(open.PendingId));

    Assert.Equal("subscriber", error.Message);
    Assert.Equal(1, releases);
    Assert.False(transaction.HasPending);
    // 取消后的迟到 ack 仍拒绝且不重复回收。
    Assert.Throws<WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(open.PendingId, Binding));
    Assert.Equal(1, releases);
  }

  [Fact]
  public void TimerPathSubscriberFailureStillReleasesWithoutCrashing()
  {
    AdjustableTimeProvider clock = new();
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction(clock);
    transaction.Begin(Binding, () => releases++);
    transaction.PendingTerminated += (_, _) => throw new InvalidOperationException("subscriber");

    // 计时器线程边界：异常经 AppLog 汇总，不终结进程，release 仍恰好一次。
    clock.Advance(WorkbenchProjectOpenTransaction.AckDeadline);

    Assert.Equal(1, releases);
    Assert.False(transaction.HasPending);
  }

  [Fact]
  public void CancelIsIdempotentAndLateAckConflicts()
  {
    int releases = 0;
    using var transaction = new WorkbenchProjectOpenTransaction();
    WorkbenchProjectPendingOpen open = transaction.Begin(Binding, () => releases++);

    Assert.True(transaction.Cancel(open.PendingId));
    Assert.False(transaction.Cancel(open.PendingId));
    Assert.Equal(1, releases);
    Assert.Throws<WorkbenchProjectOpenConflictException>(() =>
      transaction.Acknowledge(open.PendingId, Binding));
    Assert.Equal(1, releases);
  }

  [Fact]
  public void BeginReleasesReplacedPending()
  {
    int first = 0;
    int second = 0;
    string? reason = null;
    using var transaction = new WorkbenchProjectOpenTransaction();
    transaction.PendingTerminated += (_, why) => reason = why;
    WorkbenchProjectPendingOpen firstOpen = transaction.Begin(Binding, () => first++);

    WorkbenchProjectPendingOpen secondOpen = transaction.Begin(
      Binding with { Revision = 9 }, () => second++);

    Assert.NotEqual(firstOpen.PendingId, secondOpen.PendingId);
    Assert.Equal(1, first);
    Assert.Equal(0, second);
    Assert.Equal("replaced", reason);
    Assert.Equal(secondOpen.PendingId, transaction.Acknowledge(
      secondOpen.PendingId, Binding with { Revision = 9 }).PendingId);
    Assert.Equal(1, first);
    Assert.Equal(0, second);
  }

  [Fact]
  public void DisposeReleasesPending()
  {
    int releases = 0;
    var transaction = new WorkbenchProjectOpenTransaction();
    transaction.Begin(Binding, () => releases++);

    transaction.Dispose();

    Assert.Equal(1, releases);
    Assert.Throws<ObjectDisposedException>(() => transaction.Begin(Binding, null));
  }

  private sealed class AdjustableTimeProvider : TimeProvider
  {
    private readonly object timerGate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override ITimer CreateTimer(
      TimerCallback callback,
      object? state,
      TimeSpan dueTime,
      TimeSpan period)
    {
      ManualTimer timer = new(callback, utcNow + dueTime);
      lock (timerGate)
      {
        timers.Add(timer);
      }
      return timer;
    }

    public void Advance(TimeSpan duration)
    {
      utcNow += duration;
      List<ManualTimer> due = [];
      lock (timerGate)
      {
        due.AddRange(timers.Where(timer => !timer.Disposed && timer.Due <= utcNow));
      }
      foreach (ManualTimer timer in due)
      {
        timer.Fire();
      }
    }

    /// <summary>只推进时钟不触发计时器：驱动 timer 延迟下的过期 ack 路径。</summary>
    public void AdvanceClock(TimeSpan duration) => utcNow += duration;

    private sealed class ManualTimer(TimerCallback callback, DateTimeOffset due) : ITimer
    {
      public DateTimeOffset Due { get; } = due;

      public bool Disposed { get; private set; }

      public void Fire() => callback(null);

      public bool Change(TimeSpan dueTime, TimeSpan period) => true;

      public void Dispose() => Disposed = true;

      public ValueTask DisposeAsync()
      {
        Disposed = true;
        return ValueTask.CompletedTask;
      }
    }
  }
}
