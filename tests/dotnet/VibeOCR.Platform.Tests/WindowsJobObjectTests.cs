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
      TestDirectory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void VerifiedDescendantInstanceExcludesCandidateOlderThanPinnedParent()
  {
    // A PID reused by a previous parent instance leaves stale PPID edges:
    // such a candidate predates the pinned parent instance.
    long parentCreationTime = 1_300_000_000_000_000_000L;
    const int VerifiedParentProcessId = 4242;

    Assert.False(WindowsJobObject.IsVerifiedDescendantInstance(
      parentCreationTime,
      candidateCreationTime: parentCreationTime - 1,
      currentParentProcessId: VerifiedParentProcessId,
      verifiedParentProcessId: VerifiedParentProcessId));
  }

  [Fact]
  public void VerifiedDescendantInstanceExcludesCandidateWhoseRefreshedParentChanged()
  {
    long parentCreationTime = 1_300_000_000_000_000_000L;
    const int VerifiedParentProcessId = 4242;

    Assert.False(WindowsJobObject.IsVerifiedDescendantInstance(
      parentCreationTime,
      candidateCreationTime: parentCreationTime + 1,
      currentParentProcessId: 1337,
      verifiedParentProcessId: VerifiedParentProcessId));
    // The refreshed snapshot no longer lists the candidate PID at all.
    Assert.False(WindowsJobObject.IsVerifiedDescendantInstance(
      parentCreationTime,
      candidateCreationTime: parentCreationTime + 1,
      currentParentProcessId: null,
      verifiedParentProcessId: VerifiedParentProcessId));
  }

  [Fact]
  public void VerifiedDescendantInstanceAcceptsCandidateCreatedAfterPinnedParent()
  {
    long parentCreationTime = 1_300_000_000_000_000_000L;
    const int VerifiedParentProcessId = 4242;

    Assert.True(WindowsJobObject.IsVerifiedDescendantInstance(
      parentCreationTime,
      candidateCreationTime: parentCreationTime + 1,
      currentParentProcessId: VerifiedParentProcessId,
      verifiedParentProcessId: VerifiedParentProcessId));
    // Creation timestamps share tick resolution, so an equal timestamp with
    // a confirmed refreshed edge still identifies the pinned parent instance.
    Assert.True(WindowsJobObject.IsVerifiedDescendantInstance(
      parentCreationTime,
      candidateCreationTime: parentCreationTime,
      currentParentProcessId: VerifiedParentProcessId,
      verifiedParentProcessId: VerifiedParentProcessId));
  }

  [Fact]
  public async Task AssignProcessTreeExpandsThroughDescendantAlreadyInJob()
  {
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-job-preassigned-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    Process? middle = null;
    Process? grandchild = null;
    using var rootProcess = StartRootWithMiddleHelper(root);
    using var job = new WindowsJobObject();
    try
    {
      (int middleId, int grandchildId) = await ReadTreeIdsAsync(rootProcess);
      middle = Process.GetProcessById(middleId);
      grandchild = Process.GetProcessById(grandchildId);

      // The middle descendant is already a job member while its own child
      // predates that membership: enrollment must still expand through it.
      job.Assign(middle);
      job.AssignProcessTree(rootProcess);

      Assert.True(job.TerminateAndWait(TimeSpan.FromSeconds(10)));
      Assert.True(rootProcess.WaitForExit(milliseconds: 10_000));
      Assert.True(middle.WaitForExit(milliseconds: 10_000));
      Assert.True(grandchild.WaitForExit(milliseconds: 10_000));
    }
    finally
    {
      if (grandchild is not null)
      {
        TryKill(grandchild);
        grandchild.Dispose();
      }
      if (middle is not null)
      {
        TryKill(middle);
        middle.Dispose();
      }
      TryKill(rootProcess);
      rootProcess.Dispose();
      TestDirectory.Delete(root, recursive: true);
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
      TestDirectory.Delete(root, recursive: true);
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
    var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = Path.Combine(
          AppContext.BaseDirectory,
          "VibeOCR.Platform.Tests.exe"),
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      },
    };
    process.StartInfo.ArgumentList.Add("--job-object-parent-helper");
    Assert.True(process.Start());
    return process;
  }

  // A three-level tree: powershell root -> helper test host middle -> ping
  // grandchild. The helper reports its own PID through the root, and the
  // grandchild PID through the inherited stdout pipe, so both are known
  // deterministically before the job assignment starts.
  private static Process StartRootWithMiddleHelper(string workingDirectory)
  {
    string powershell = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.System),
      "WindowsPowerShell", "v1.0", "powershell.exe");
    string testHost = Path.Combine(
      AppContext.BaseDirectory,
      "VibeOCR.Platform.Tests.exe");
    var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = powershell,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      },
    };
    process.StartInfo.ArgumentList.Add("-NoProfile");
    process.StartInfo.ArgumentList.Add("-Command");
    process.StartInfo.ArgumentList.Add(
      $"$h = Start-Process -FilePath '{testHost}' " +
      $"-ArgumentList '--job-object-parent-helper' -PassThru -NoNewWindow; " +
      $"\"middle=$($h.Id)\"; Wait-Process -Id $h.Id");
    Assert.True(process.Start());
    return process;
  }

  private static async Task<(int MiddleId, int GrandchildId)> ReadTreeIdsAsync(
    Process rootProcess)
  {
    int? middleId = null;
    int? grandchildId = null;
    while (middleId is null || grandchildId is null)
    {
      string? line = await rootProcess.StandardOutput
        .ReadLineAsync(TestContext.Current.CancellationToken)
        .AsTask()
        .WaitAsync(
          TimeSpan.FromSeconds(15),
          TestContext.Current.CancellationToken);
      if (line is null)
      {
        break;
      }
      if (line.StartsWith("middle=", StringComparison.Ordinal)
        && int.TryParse(line["middle=".Length..], out int parsedMiddle))
      {
        middleId = parsedMiddle;
      }
      else if (int.TryParse(line, out int parsedGrandchild))
      {
        grandchildId = parsedGrandchild;
      }
    }

    Assert.True(
      middleId is not null && grandchildId is not null,
      "The helper process tree did not report its ids.");
    return (middleId!.Value, grandchildId!.Value);
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
