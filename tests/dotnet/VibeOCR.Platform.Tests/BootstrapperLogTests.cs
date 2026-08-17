using VibeOCR.Bootstrapper;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class BootstrapperLogTests
{
    [Fact]
    public void DefaultDirectoryFollowsThePortableStateRoot()
    {
        Assert.Equal(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state", "logs"),
            BootstrapperLog.DefaultLogDirectory());
    }

    [Fact]
    public void ErrorWritesToTheInitializedLogDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-bootlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        BootstrapperLog.Initialize(root);
        try
        {
            BootstrapperLog.Error("layout.root-conflict: product root is not closed");

            string log = Directory.GetFiles(root, "bootstrapper-*.log").Single();
            string content = File.ReadAllText(log);
            Assert.Contains("[ERROR]", content);
            Assert.Contains("layout.root-conflict", content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializeSwallowsUnusableDirectoryAndWritesStayNoOps()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-bootlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            BootstrapperLog.Initialize(blocker);
            BootstrapperLog.Error("must not throw");

            Assert.Equal(blocker, Directory.GetFiles(root).Single());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
