using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class TestDirectoryTests
{
  [Fact]
  public void DeleteWaitsForAReleasedSharingViolation()
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
    // 锁的释放只允许依赖内核唤醒：thread-pool 饥饿会拖后 Task.Delay 的延续，
    // 在 CI 高负载下曾超过 Delete 约 1 秒的重试预算。
    Thread release = new(() =>
    {
      Thread.Sleep(150);
      locked.Dispose();
    });
    release.Start();

    TestDirectory.Delete(root, recursive: true);
    release.Join();

    Assert.False(Directory.Exists(root));
  }
}
