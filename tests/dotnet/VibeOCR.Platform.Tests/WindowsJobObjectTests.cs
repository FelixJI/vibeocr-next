using System.Diagnostics;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class WindowsJobObjectTests
{
  [Fact]
  public async Task AssignProcessTreeEnrollsDescendantStartedBeforeAssignment()
  {
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-job-tree-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    Process? child = null;
    using var parent = StartParentWithExistingChild(root);
    using var job = new WindowsJobObject();
    try
    {
      string? childIdLine = await parent.StandardOutput
        .ReadLineAsync(TestContext.Current.CancellationToken)
        .AsTask()
        .WaitAsync(
          TimeSpan.FromSeconds(5),
          TestContext.Current.CancellationToken);
      Assert.True(int.TryParse(childIdLine, out int childId));
      child = Process.GetProcessById(childId);

      job.AssignProcessTree(parent);

      Assert.True(job.TerminateAndWait(TimeSpan.FromSeconds(5)));
      Assert.True(parent.WaitForExit(milliseconds: 5_000));
      Assert.True(child.WaitForExit(milliseconds: 5_000));
    }
    finally
    {
      if (child is not null)
      {
        TryKill(child);
        child.Dispose();
      }
      TryKill(parent);
      parent.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

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
      first.Dispose();
      second.Dispose();
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

  private static Process StartParentWithExistingChild(string workingDirectory)
  {
    string script = Path.Combine(workingDirectory, "start-child.ps1");
    File.WriteAllLines(
      script,
      [
        "$ping = Join-Path $env:SystemRoot 'System32\\ping.exe'",
        "$child = Start-Process -FilePath $ping -ArgumentList @('127.0.0.1', '-t') "
          + "-WorkingDirectory $PSScriptRoot -PassThru",
        "Write-Output $child.Id",
        "[Console]::Out.Flush()",
        "$child.WaitForExit()",
      ]);
    string powerShell = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.System),
      "WindowsPowerShell",
      "v1.0",
      "powershell.exe");
    var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = powerShell,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      },
    };
    process.StartInfo.ArgumentList.Add("-NoProfile");
    process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
    process.StartInfo.ArgumentList.Add("Bypass");
    process.StartInfo.ArgumentList.Add("-File");
    process.StartInfo.ArgumentList.Add(script);
    Assert.True(process.Start());
    return process;
  }

  private static void TryKill(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
      process.WaitForExit(milliseconds: 5_000);
    }
    catch (InvalidOperationException)
    {
      // The process already exited between the check and cleanup.
    }
  }
}
