using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class TestDirectoryTests
{
  [Fact]
  public async Task DeleteWaitsForAReleasedSharingViolation()
  {
    string root = Path.Combine(
        Path.GetTempPath(),
        $"vibeocr-test-directory-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    string lockedPath = Path.Combine(root, "locked.tmp");
    FileStream locked = new(
        lockedPath,
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None);
    Task release = Task.Run(async () =>
    {
      await Task.Delay(150, TestContext.Current.CancellationToken);
      await locked.DisposeAsync();
    }, TestContext.Current.CancellationToken);

    TestDirectory.Delete(root, recursive: true);
    await release;

    Assert.False(Directory.Exists(root));
  }
}
