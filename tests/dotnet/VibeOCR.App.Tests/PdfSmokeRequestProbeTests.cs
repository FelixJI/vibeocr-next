using VibeOCR.App.Inference;

using Xunit;

namespace VibeOCR.App.Tests;

public class PdfSmokeRequestProbeTests
{
  [Fact]
  public async Task MeasuresCompletedRequestsAndReleasesInflight()
  {
    var probe = new PdfSmokeRequestProbe();
    var request = await probe.MeasureAsync("render_preview", "s1", 3, 300,
        () => Task.FromResult(new byte[] { 1, 2, 3 }));
    Assert.Equal([1, 2, 3], request);
    var (events, maxInflight, overflowed) = probe.Snapshot();
    Assert.False(overflowed);
    Assert.Equal(1, maxInflight);
    var entry = Assert.Single(events);
    Assert.Equal("render_preview", entry.Kind);
    Assert.Equal("s1", entry.Session);
    Assert.Equal(3, entry.Page);
    Assert.Equal(300, entry.Param);
    Assert.Equal("completed", entry.Outcome);
    Assert.Null(entry.Error);
    Assert.True(entry.DurationMs >= 0);
  }

  [Fact]
  public async Task RecordsCancelledAndFaultedOutcomesWithoutLeakingInflight()
  {
    var probe = new PdfSmokeRequestProbe();
    await Assert.ThrowsAsync<OperationCanceledException>(() => probe.MeasureAsync(
        "inspect_page", "s1", 0, 0,
        () => Task.FromException<byte[]>(new OperationCanceledException("ct"))));
    await Assert.ThrowsAsync<InvalidOperationException>(() => probe.MeasureAsync(
        "render_preview", "s1", 1, 300,
        () => Task.FromException<byte[]>(new InvalidOperationException("boom"))));
    var (events, _, overflowed) = probe.Snapshot();
    Assert.False(overflowed);
    Assert.Equal(2, events.Count);
    Assert.Equal("cancelled", events[0].Outcome);
    Assert.Equal("faulted", events[1].Outcome);
    Assert.Contains("InvalidOperationException", events[1].Error);
    // 完成/取消/失败后 in-flight 归零：后续串行请求不再抬高 max。
    await probe.MeasureAsync("load_detect", "s1", -1, 0, () => Task.CompletedTask);
    var (_, maxInflight, _) = probe.Snapshot();
    Assert.Equal(1, maxInflight);
  }

  [Fact]
  public async Task TracksConcurrentMaxInflight()
  {
    var probe = new PdfSmokeRequestProbe();
    var gates = Enumerable.Range(0, 4)
        .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
        .ToList();
    var tasks = gates.Select((gate, index) => probe.MeasureAsync(
        "render_thumbnail", "s1", index, 160,
        async () => { await gate.Task; return Array.Empty<byte>(); })).ToList();
    await Task.Yield();
    var (_, maxInflight, _) = probe.Snapshot();
    Assert.Equal(4, maxInflight);
    foreach (var gate in gates) gate.SetResult();
    await Task.WhenAll(tasks);
    var (events, finalMax, _) = probe.Snapshot();
    Assert.Equal(4, finalMax);
    Assert.Equal(4, events.Count);
    Assert.All(events, entry => Assert.Equal("completed", entry.Outcome));
  }

  [Fact]
  public async Task OverflowIsExplicitAndDoesNotSilentlyTruncate()
  {
    var probe = new PdfSmokeRequestProbe();
    for (int index = 0; index < PdfSmokeRequestProbe.MaxEvents + 5; index++)
    {
      await probe.MeasureAsync("inspect_page", "s1", index, 0, () => Task.CompletedTask);
    }
    var (events, _, overflowed) = probe.Snapshot();
    Assert.True(overflowed);
    Assert.Equal(PdfSmokeRequestProbe.MaxEvents, events.Count);
  }

  [Fact]
  public void StaticProbeDefaultsToNullAndVolatileRoundTrip()
  {
    Assert.Null(DeferredInferenceClient.PdfSmokeProbe);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      Assert.Same(probe, DeferredInferenceClient.PdfSmokeProbe);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
    Assert.Null(DeferredInferenceClient.PdfSmokeProbe);
  }
}
