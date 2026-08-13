using System.Text;
using VibeOCR.App.Services;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class AtomicFileTests
{
  [Fact]
  public void FailedWritePreservesExistingTargetAndCleansTemporaryFile()
  {
    string directory = Path.Combine(Path.GetTempPath(), $"atomic-{Guid.NewGuid():N}");
    string path = Path.Combine(directory, "settings.json");
    Directory.CreateDirectory(directory);
    File.WriteAllText(path, "original");
    try
    {
      Assert.Throws<IOException>(() => AtomicFile.Write(path, stream =>
      {
        stream.Write(Encoding.UTF8.GetBytes("partial"));
        throw new IOException("write failed");
      }));

      Assert.Equal("original", File.ReadAllText(path));
      Assert.Empty(Directory.EnumerateFiles(directory, ".settings.json.*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }
}
