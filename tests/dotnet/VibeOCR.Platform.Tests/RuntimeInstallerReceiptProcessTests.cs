using System.Diagnostics;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

[CollectionDefinition("Runtime installer blocked output", DisableParallelization = true)]
public sealed class RuntimeInstallerBlockedOutputCollection;

// Only this test deliberately blocks the process output callback; keep that
// synthetic delay away from other tests sharing the testhost thread pool.
[Collection("Runtime installer blocked output")]
public sealed class RuntimeInstallerReceiptProcessTests
{
  [Fact]
  public async Task RealRunnerReceiptDrainedAfterExitStillCancels()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-maint-drain-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    Process? child = null;
    Task<RuntimeInstallerProcessResult>? running = null;
    try
    {
      string script = Path.Combine(root, "installer.ps1");
      await File.WriteAllTextAsync(script, """
        [Console]::WriteLine("reader-gate:$PID")
        if ([Console]::In.ReadLine() -ne 'cancel') { exit 90 }
        [Console]::WriteLine('{"maintenance_cancel":"pre_registration"}')
        [Console]::WriteLine('{"protocol_version":2,"ok":false,"error":{"canonical_code":"CANCELLED"}}')
        exit 1
        """, TestContext.Current.CancellationToken);
      var startInfo = new ProcessStartInfo
      {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
          "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", script,
        "--maintenance-cancel-control" })
        startInfo.ArgumentList.Add(argument);
      var reading = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
      using var cancellation = new CancellationTokenSource();
      running = RuntimeInstallerCommandRunner.RunProcessAsync(startInfo, line =>
      {
        const string prefix = "reader-gate:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return;
        reading.TrySetResult(int.Parse(line[prefix.Length..],
          System.Globalization.CultureInfo.InvariantCulture));
        // The host exits while its receipt is still behind a slow output consumer.
        Thread.Sleep(TimeSpan.FromSeconds(12));
      }, cancellation.Token);
      int pid = await reading.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
      child = Process.GetProcessById(pid);
      cancellation.Cancel();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
      Assert.True(child.HasExited);
    }
    finally
    {
      await ReapCancellationFixtureAsync(child, running);
      TestDirectory.Delete(root, recursive: true);
    }
  }

  private static async Task ReapCancellationFixtureAsync(
    Process? child, Task<RuntimeInstallerProcessResult>? running)
  {
    if (child is not null)
    {
      if (!child.HasExited) child.Kill(entireProcessTree: true);
      await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
      child.Dispose();
    }
    if (running is not null)
    {
      try { await running.WaitAsync(TimeSpan.FromSeconds(20)); }
      catch (OperationCanceledException) { }
    }
  }

}
