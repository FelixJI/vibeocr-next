using System.Diagnostics;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class WindowsJobObjectTests
{
  [Fact]
  public void TerminateAndWaitReturnsAfterEveryAssignedProcessExits()
  {
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-job-object-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    using var first = StartLongLivedProcess(root);
    using var second = StartLongLivedProcess(root);
    using var job = new WindowsJobObject();
    try
    {
      job.Assign(first);
      job.Assign(second);

      Assert.True(job.TerminateAndWait(TimeSpan.FromSeconds(5)));
      Assert.True(first.HasExited);
      Assert.True(second.HasExited);
    }
    finally
    {
      TryKill(first);
      TryKill(second);
      Directory.Delete(root, recursive: true);
    }
  }

  private static Process StartLongLivedProcess(string workingDirectory)
  {
    string ping = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
    var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = ping,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
      },
    };
    process.StartInfo.ArgumentList.Add("127.0.0.1");
    process.StartInfo.ArgumentList.Add("-t");
    Assert.True(process.Start());
    return process;
  }

  private static void TryKill(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill();
        process.WaitForExit(milliseconds: 5_000);
      }
    }
    catch (InvalidOperationException)
    {
      // The process already exited between the check and cleanup.
    }
  }
}
