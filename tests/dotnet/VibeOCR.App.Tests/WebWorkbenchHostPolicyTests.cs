using System.Text;
using VibeOCR.App.Web;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WebWorkbenchHostPolicyTests
{
  [Theory]
  [InlineData("https://app.vibeocr/")]
  [InlineData("https://app.vibeocr/index.html")]
  [InlineData("https://app.vibeocr/index.html#/pdf")]
  public void NavigationAllowsOnlyTheWorkbenchDocument(string url) =>
    Assert.True(WebWorkbenchHost.IsNavigationAllowed(new Uri(url)));

  [Theory]
  [InlineData("https://app.vibeocr/other.html")]
  [InlineData("https://app.vibeocr/index.html?debug=1")]
  [InlineData("https://example.test/index.html")]
  [InlineData("http://app.vibeocr/index.html")]
  [InlineData("https://user@app.vibeocr/index.html")]
  public void NavigationRejectsEveryOtherTarget(string url) =>
    Assert.False(WebWorkbenchHost.IsNavigationAllowed(new Uri(url)));

  [Fact]
  public void RecoveryAllowsOneAutomaticAttemptPerReadyEpisode()
  {
    var policy = new WorkbenchRecoveryPolicy();

    Assert.Equal(WorkbenchRecoveryAction.Reload, policy.RegisterFailure());
    Assert.Equal(
      WorkbenchRecoveryAction.ShowNativeRecovery,
      policy.RegisterFailure());

    policy.MarkReady();

    Assert.Equal(WorkbenchRecoveryAction.Reload, policy.RegisterFailure());
  }

  [Fact]
  public async Task BufferResourceReleasesSourceAfterCopy()
  {
    byte[] expected = Encoding.UTF8.GetBytes("resource-content");
    TrackingStream source = new(expected);
    WorkbenchResourceResponse response = new(
      "text/plain; charset=utf-8",
      expected.Length,
      source);

    using IRandomAccessStream buffered = await WebWorkbenchHost.BufferResourceAsync(
      response,
      TestContext.Current.CancellationToken);

    Assert.True(source.IsDisposed);
    using Stream reader = buffered.AsStreamForRead();
    using MemoryStream copy = new();
    await reader.CopyToAsync(copy, TestContext.Current.CancellationToken);
    Assert.Equal(expected, copy.ToArray());
  }

  [Fact]
  public async Task BufferResourceReleasesSourceWhenCopyFails()
  {
    TrackingStream source = new([1], failReads: true);
    WorkbenchResourceResponse response = new(
      "application/octet-stream",
      1,
      source);

    Exception? error = await Record.ExceptionAsync(async () =>
    {
      using IRandomAccessStream _ = await WebWorkbenchHost.BufferResourceAsync(
        response,
        TestContext.Current.CancellationToken);
    });

    Assert.NotNull(error);
    Assert.True(source.IsDisposed);
  }

  private sealed class TrackingStream(
    byte[] content,
    bool failReads = false) : MemoryStream(content)
  {
    public bool IsDisposed { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
      ThrowIfReadFails();
      return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
      ThrowIfReadFails();
      return base.Read(buffer);
    }

    public override Task<int> ReadAsync(
      byte[] buffer,
      int offset,
      int count,
      CancellationToken cancellationToken)
    {
      if (failReads)
      {
        return Task.FromException<int>(new IOException("read failed"));
      }
      return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default)
    {
      if (failReads)
      {
        return ValueTask.FromException<int>(new IOException("read failed"));
      }
      return base.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        IsDisposed = true;
      }
      base.Dispose(disposing);
    }

    private void ThrowIfReadFails()
    {
      if (failReads)
      {
        throw new IOException("read failed");
      }
    }
  }
}
