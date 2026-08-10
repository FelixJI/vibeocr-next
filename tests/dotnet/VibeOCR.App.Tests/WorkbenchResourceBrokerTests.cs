using System.Text;
using VibeOCR.App.Web;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchResourceBrokerTests : IDisposable
{
  private readonly string resourceRoot = Path.Combine(
    Path.GetTempPath(),
    $"vibeocr-resource-broker-{Guid.NewGuid():N}");

  public WorkbenchResourceBrokerTests()
  {
    Directory.CreateDirectory(resourceRoot);
  }

  [Fact]
  public async Task LeaseUsesOpaqueUriAndOpensReadOnlyContent()
  {
    string sourcePath = Path.Combine(resourceRoot, "preview.png");
    byte[] expected = Encoding.UTF8.GetBytes("preview-content");
    await File.WriteAllBytesAsync(
      sourcePath,
      expected,
      TestContext.Current.CancellationToken);
    using WorkbenchResourceBroker broker = new(resourceRoot);

    WorkbenchResourceLease lease = broker.Lease(
      "preview.png",
      "image/png",
      TimeSpan.FromMinutes(1));

    Assert.StartsWith(
      "https://app.vibeocr/__resource/",
      lease.Uri.AbsoluteUri,
      StringComparison.Ordinal);
    Assert.DoesNotContain("preview", lease.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(resourceRoot, lease.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    await using WorkbenchResourceResponse response = await broker.OpenAsync(
      lease.Uri,
      TestContext.Current.CancellationToken);
    Assert.Equal("image/png", response.ContentType);
    Assert.Equal(expected.Length, response.ContentLength);
    Assert.True(response.Content.CanRead);
    Assert.False(response.Content.CanWrite);
    using MemoryStream copy = new();
    await response.Content.CopyToAsync(
      copy,
      TestContext.Current.CancellationToken);
    Assert.Equal(expected, copy.ToArray());
  }

  [Theory]
  [InlineData("http://app.vibeocr/__resource/{0}")]
  [InlineData("https://other.vibeocr/__resource/{0}")]
  [InlineData("https://app.vibeocr:444/__resource/{0}")]
  [InlineData("https://app.vibeocr/__resource/{0}/extra")]
  [InlineData("https://app.vibeocr/__resource/{0}?download=1")]
  [InlineData("https://app.vibeocr/__resource/{0}#fragment")]
  [InlineData("https://app.vibeocr/__resource/../{0}")]
  public async Task OpenRejectsRequestsOutsideExactSameOriginRoute(string requestTemplate)
  {
    await File.WriteAllTextAsync(
      Path.Combine(resourceRoot, "result.txt"),
      "result",
      TestContext.Current.CancellationToken);
    using WorkbenchResourceBroker broker = new(resourceRoot);
    WorkbenchResourceLease lease = broker.Lease(
      "result.txt",
      "text/plain; charset=utf-8",
      TimeSpan.FromMinutes(1));
    string token = lease.Uri.Segments[^1];
    Uri request = new(string.Format(requestTemplate, token));

    await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
      await broker.OpenAsync(request, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task LeaseRejectsDirectoryEscape()
  {
    string outsidePath = Path.Combine(
      Path.GetDirectoryName(resourceRoot)!,
      $"outside-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(
      outsidePath,
      "outside",
      TestContext.Current.CancellationToken);
    try
    {
      using WorkbenchResourceBroker broker = new(resourceRoot);

      Assert.Throws<WorkbenchResourceAccessException>(() => broker.Lease(
        Path.Combine("..", Path.GetFileName(outsidePath)),
        "text/plain",
        TimeSpan.FromMinutes(1)));
    }
    finally
    {
      File.Delete(outsidePath);
    }
  }

  [Fact]
  public async Task OpenRejectsUnknownExpiredAndRevokedLeases()
  {
    await File.WriteAllTextAsync(
      Path.Combine(resourceRoot, "result.txt"),
      "result",
      TestContext.Current.CancellationToken);
    AdjustableTimeProvider clock = new(
      new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    using WorkbenchResourceBroker broker = new(resourceRoot, clock);
    WorkbenchResourceLease expired = broker.Lease(
      "result.txt",
      "text/plain",
      TimeSpan.FromSeconds(1));
    clock.Advance(TimeSpan.FromSeconds(2));

    await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
      await broker.OpenAsync(expired.Uri, TestContext.Current.CancellationToken));
    await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
      await broker.OpenAsync(
        new Uri("https://app.vibeocr/__resource/00000000000000000000000000000000"),
        TestContext.Current.CancellationToken));

    WorkbenchResourceLease revoked = broker.Lease(
      "result.txt",
      "text/plain",
      TimeSpan.FromMinutes(1));
    Assert.True(broker.Revoke(revoked));
    Assert.False(broker.Revoke(revoked));
    await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
      await broker.OpenAsync(revoked.Uri, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DisposeRevokesLeasesAndStopsFurtherUse()
  {
    await File.WriteAllTextAsync(
      Path.Combine(resourceRoot, "result.txt"),
      "result",
      TestContext.Current.CancellationToken);
    WorkbenchResourceBroker broker = new(resourceRoot);
    WorkbenchResourceLease lease = broker.Lease(
      "result.txt",
      "text/plain",
      TimeSpan.FromMinutes(1));

    broker.Dispose();

    Assert.Throws<ObjectDisposedException>(() => broker.Lease(
      "result.txt",
      "text/plain",
      TimeSpan.FromMinutes(1)));
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await broker.OpenAsync(lease.Uri, TestContext.Current.CancellationToken));
  }

  public void Dispose()
  {
    Directory.Delete(resourceRoot, recursive: true);
  }

  private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration)
    {
      utcNow += duration;
    }
  }
}
