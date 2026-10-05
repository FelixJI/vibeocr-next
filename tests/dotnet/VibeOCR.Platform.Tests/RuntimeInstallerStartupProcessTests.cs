using System.Diagnostics;
using System.Text.Json;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class RuntimeInstallerStartupProcessTests
{
  [Fact]
  public async Task CancelBeforeRuntimeInitializationReapsActualPythonHost()
  {
    string repository = FindRepository();
    string python = Path.Combine(repository, ".venv", "Scripts", "python.exe");
    Assert.True(File.Exists(python), "uv sync --frozen must prepare the repository Python environment.");
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-startup-cancel-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var runner = new StartupRunner(python, repository);
    try
    {
      string manifest = Path.Combine(root, "runtime-manifest.json");
      string script = Path.Combine(root, "startup_gate.py");
      string release = Path.Combine(root, "release");
      string installed = Path.Combine(root, "install-started");
      await File.WriteAllTextAsync(manifest,
        """{"capabilities":["runtime.maintenance.v2"]}""", TestContext.Current.CancellationToken);
      await File.WriteAllTextAsync(script, """
        import json
        import os
        import sys
        import time
        from pathlib import Path
        from vibeocr.runtime.environments import runtime_installer

        request = json.loads(sys.argv[sys.argv.index('--request-json') + 1])
        root = Path(request['product_root'])

        def initialization_gate(request, *, event_sink, startup_cancellation=None):
            # CLI has installed its real stdin listener, but has not built
            # RuntimeControl or registered any maintenance operation.
            print(f'initialization-gate:{os.getpid()}', flush=True)
            while not (root / 'release').exists():
                time.sleep(0.01)
            (root / 'install-started').touch()
            raise AssertionError('cancelled initialization must never reach installation')

        runtime_installer._runtime_control_from_request = initialization_gate
        raise SystemExit(runtime_installer.main())
        """, TestContext.Current.CancellationToken);
      runner.Script = script;
      var client = new RuntimeInstallerClient(new RuntimeInstallerConfiguration(
        python, root, Path.Combine(root, "component-lock.json"), manifest, "cpu"), runner);
      using var cancellation = new CancellationTokenSource();
      Task<RuntimeLaunch> operation = client.EnsureAsync("startup-op", cancellationToken: cancellation.Token);
      int hostPid = await runner.Initializing.Task.WaitAsync(TimeSpan.FromSeconds(15),
        TestContext.Current.CancellationToken);
      cancellation.Cancel();
      OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => operation.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
      Assert.Equal(cancellation.Token, error.CancellationToken);
      Assert.True(runner.OwnedToken.IsCancellationRequested);
      Assert.True(runner.HostRun!.IsCompleted);
      Assert.False(IsRunning(hostPid));
      Assert.Equal(1, runner.HostStarts);
      Assert.Equal(1, runner.CancelRequests);
      Assert.False(File.Exists(installed));
      await File.WriteAllTextAsync(release, "release", TestContext.Current.CancellationToken);
      Assert.False(File.Exists(installed));
      Assert.False(Directory.Exists(Path.Combine(root, "state")));
      // Process exit releases its handles; no task registry or maintenance
      // state was created, and releasing initialization cannot start an install.
    }
    finally
    {
      await runner.ReapFixtureAsync();
      TestDirectory.Delete(root, recursive: true);
    }
  }

  private static string FindRepository()
  {
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
        return directory.FullName;
      directory = directory.Parent;
    }
    throw new InvalidOperationException("Repository root not found.");
  }

  private static bool IsRunning(int pid)
  {
    try
    {
      using Process process = Process.GetProcessById(pid);
      return !process.HasExited;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  private sealed class StartupRunner(string python, string repository) : IRuntimeInstallerCommandRunner
  {
    public string Script { get; set; } = "";
    public TaskCompletionSource<int> Initializing { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken OwnedToken { get; private set; }
    public Task<RuntimeInstallerProcessResult>? HostRun { get; private set; }
    public int HostStarts { get; private set; }
    public int CancelRequests { get; private set; }

    public Task<RuntimeInstallerProcessResult> RunAsync(ProcessStartInfo info, CancellationToken token)
    {
      string request = info.ArgumentList[info.ArgumentList.IndexOf("--request-json") + 1];
      using JsonDocument document = JsonDocument.Parse(request);
      Assert.Equal("cancel", document.RootElement.GetProperty("command").GetString());
      Assert.Equal("startup-op", document.RootElement.GetProperty("target_operation_id").GetString());
      CancelRequests++;
      // The controlled initialization gate precedes the durable registry. This
      // response reproduces the actual public command's unknown-op boundary.
      return Task.FromResult(new RuntimeInstallerProcessResult(1,
        """{"protocol_version":2,"ok":false,"error":{"code":"invalid_request","canonical_code":"RUNTIME_OPERATION_NOT_FOUND","category":"not_found","message":"Runtime operation is not registered","retryable":false}}""", ""));
    }

    public Task<RuntimeInstallerProcessResult> RunAsync(ProcessStartInfo info,
      Action<string>? output, CancellationToken token)
    {
      Assert.True(info.RedirectStandardInput);
      Assert.Contains("--maintenance-cancel-control", info.ArgumentList);
      OwnedToken = token;
      info.FileName = python;
      info.WorkingDirectory = repository;
      info.ArgumentList.Insert(0, Script);
      HostStarts++;
      HostRun = RuntimeInstallerCommandRunner.RunProcessAsync(info, line =>
      {
        const string prefix = "initialization-gate:";
        if (line.StartsWith(prefix, StringComparison.Ordinal))
          Initializing.TrySetResult(int.Parse(line[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture));
        output?.Invoke(line);
      }, token);
      return HostRun;
    }

    public async Task ReapFixtureAsync()
    {
      // Cleanup acts only on the PID reported by this test's own Python host.
      if (Initializing.Task.IsCompletedSuccessfully)
      {
        try
        {
          using Process process = Process.GetProcessById(Initializing.Task.Result);
          if (!process.HasExited) process.Kill(entireProcessTree: true);
          await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
        }
      }
      if (HostRun is not null)
      {
        try { await HostRun.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
      }
    }
  }
}
