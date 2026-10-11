using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ImageEditorRetirementTests
{
  [Fact]
  public async Task HeldDisposalOnlyAllowsNewestStillCurrentSessionAfterDrain()
  {
    var retirement = new ImageEditorRetirement();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    retirement.Track(release.Task);
    retirement.Track(release.Task);
    Guid firstSession = Guid.NewGuid(), latestSession = Guid.NewGuid();
    long first = retirement.BeginReplacement();
    var constructed = new List<Guid>();
    async Task Construct(long request, Guid session)
    {
      await retirement.Drain;
      if (retirement.IsCurrent(request, session, latestSession, false)) constructed.Add(session);
    }
    Task stale = Construct(first, firstSession);
    long latest = retirement.BeginReplacement();
    Task current = Construct(latest, latestSession);
    Assert.False(stale.IsCompleted);
    Assert.False(current.IsCompleted);
    Assert.Empty(constructed);
    release.SetResult();
    await Task.WhenAll(stale, current);
    Assert.Equal([latestSession], constructed);
    Assert.False(retirement.IsCurrent(latest, latestSession, Guid.NewGuid(), false));
    Assert.False(retirement.IsCurrent(latest, latestSession, latestSession, true));
    retirement.CancelReplacement();
    Assert.False(retirement.IsCurrent(latest, latestSession, latestSession, false));
  }

  [Fact]
  public async Task FailedDrainCannotBeHiddenByAnotherCompletedDisposal()
  {
    var retirement = new ImageEditorRetirement();
    var failure = new InvalidOperationException("native disposal failed");
    retirement.Track(Task.FromException(failure));
    retirement.Track(Task.CompletedTask);
    InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await retirement.Drain);
    Assert.Same(failure, observed);
  }
}
