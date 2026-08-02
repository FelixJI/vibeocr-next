using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class RuntimeInstallerClientTests
{
    [Fact]
    public async Task EnsureUsesOnlyInstallerLaunchContract()
    {
        var runner = new StubRunner(
            new RuntimeInstallerProcessResult(
                0,
                LaunchEnvelope(),
                string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeLaunch launch = await client.EnsureAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\store\python.exe", launch.PythonExecutable);
        Assert.Equal("vibeocr.backend.supervisor.main", launch.SupervisorModule);
        Assert.Equal(@"C:\Next", launch.WorkingDirectory);
        Assert.Equal(@"C:\store", launch.Environment["VIBEOCR_RUNTIME_ROOT"]);
        Assert.Equal("ensure", Request(runner.LastStartInfo!).GetProperty("operation").GetString());
        Assert.DoesNotContain(
            runner.LastStartInfo.ArgumentList,
            argument => argument.Contains("pip", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("torch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectParsesIntegrityWithoutDerivingRuntimePaths()
    {
        var runner = new StubRunner(
            new RuntimeInstallerProcessResult(
                0,
                InspectEnvelope(),
                string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeInspection inspection = await client.InspectAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("ready", inspection.Status);
        Assert.Equal("verified", inspection.Integrity);
        Assert.Equal("cpu", inspection.Accelerator);
        Assert.Equal("inspect", Request(runner.LastStartInfo!).GetProperty("operation").GetString());
    }

    [Fact]
    public async Task ExplicitPortableLayoutBindingIsForwardedWithoutParentDiscovery()
    {
        RuntimeInstallerConfiguration configuration = Configuration() with
        {
            PortableLayoutManifest = @"C:\bundle\portable-layout.json",
            ProductId = "next",
        };
        var runner = LaunchRunner("repair");
        var client = new RuntimeInstallerClient(configuration, runner);

        await client.RepairAsync(TestContext.Current.CancellationToken);

        JsonElement request = Request(runner.LastStartInfo!);
        Assert.Equal(@"C:\bundle\portable-layout.json", request.GetProperty("layout_manifest").GetString());
        Assert.Equal("next", request.GetProperty("product_id").GetString());
        Assert.Equal("repair", request.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task StandaloneInvocationDoesNotGuessSharedLayout()
    {
        var runner = LaunchRunner();
        var client = new RuntimeInstallerClient(Configuration(), runner);

        await client.EnsureAsync(TestContext.Current.CancellationToken);

        JsonElement request = Request(runner.LastStartInfo!);
        Assert.False(request.TryGetProperty("layout_manifest", out _));
        Assert.False(request.TryGetProperty("product_id", out _));
    }

    [Fact]
    public async Task InstallerErrorIsReportedFromJson()
    {
        var runner = new StubRunner(
            new RuntimeInstallerProcessResult(
                1,
                """{"protocol_version":2,"ok":false,"operation":"ensure","error":{"code":"install_failed","message":"hash mismatch","retryable":false}}""",
                string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeInstallerException error = await Assert.ThrowsAsync<RuntimeInstallerException>(
            () => client.EnsureAsync(TestContext.Current.CancellationToken));

        Assert.Contains("hash mismatch", error.Message);
    }

    [Fact]
    public async Task CommandRunnerRejectsTamperedBoundInstallerBeforeExecution()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"vibeocr-installer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(
                root,
                "vibeocr-runtime-installer.exe");
            string manifest = Path.Combine(root, "runtime-manifest.json");
            string componentLock = Path.Combine(root, "component-lock.json");
            await File.WriteAllBytesAsync(
                executable,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            byte[] manifestBytes = Encoding.UTF8.GetBytes(
                "{\"installer\":{\"executable_sha256\":\"" +
                new string('0', 64) +
                "\"}}");
            await File.WriteAllBytesAsync(
                manifest,
                manifestBytes,
                TestContext.Current.CancellationToken);
            await WriteComponentLockAsync(componentLock, manifestBytes);
            ProcessStartInfo startInfo = BoundStartInfo(
                executable,
                componentLock,
                manifest);

            var runner = new RuntimeInstallerCommandRunner();
            RuntimeInstallerException error =
                await Assert.ThrowsAsync<RuntimeInstallerException>(
                    () => runner.RunAsync(
                        startInfo,
                        TestContext.Current.CancellationToken));

            Assert.Contains("SHA-256 mismatch", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CommandRunnerRejectsTamperedRuntimeManifestBeforeReadingInstallerHash()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"vibeocr-manifest-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(
                root,
                "vibeocr-runtime-installer.exe");
            string manifest = Path.Combine(root, "runtime-manifest.json");
            string componentLock = Path.Combine(root, "component-lock.json");
            byte[] executableBytes = [1, 2, 3];
            await File.WriteAllBytesAsync(
                executable,
                executableBytes,
                TestContext.Current.CancellationToken);
            byte[] committedManifestBytes = Encoding.UTF8.GetBytes(
                "{\"installer\":{\"executable_sha256\":\"" +
                Sha256(executableBytes) +
                "\"}}");
            await WriteComponentLockAsync(
                componentLock,
                committedManifestBytes);
            byte[] tamperedManifestBytes =
            [
                .. committedManifestBytes,
                (byte)' ',
            ];
            await File.WriteAllBytesAsync(
                manifest,
                tamperedManifestBytes,
                TestContext.Current.CancellationToken);
            ProcessStartInfo startInfo = BoundStartInfo(
                executable,
                componentLock,
                manifest);

            var runner = new RuntimeInstallerCommandRunner();
            RuntimeInstallerException error =
                await Assert.ThrowsAsync<RuntimeInstallerException>(
                    () => runner.RunAsync(
                        startInfo,
                        TestContext.Current.CancellationToken));

            Assert.Contains("Runtime manifest SHA-256 mismatch", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CommandRunnerRejectsRenamedInstallerInsteadOfSkippingVerification()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"vibeocr-renamed-installer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(root, "renamed-installer.exe");
            string manifest = Path.Combine(root, "runtime-manifest.json");
            string componentLock = Path.Combine(root, "component-lock.json");
            await File.WriteAllBytesAsync(
                executable,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            byte[] manifestBytes = Encoding.UTF8.GetBytes(
                "{\"installer\":{\"executable_sha256\":\"" +
                new string('0', 64) +
                "\"}}");
            await File.WriteAllBytesAsync(
                manifest,
                manifestBytes,
                TestContext.Current.CancellationToken);
            await WriteComponentLockAsync(componentLock, manifestBytes);
            ProcessStartInfo startInfo = BoundStartInfo(
                executable,
                componentLock,
                manifest);

            var runner = new RuntimeInstallerCommandRunner();
            RuntimeInstallerException error =
                await Assert.ThrowsAsync<RuntimeInstallerException>(
                    () => runner.RunAsync(
                        startInfo,
                        TestContext.Current.CancellationToken));

            Assert.Contains(
                "Runtime Installer executable SHA-256 mismatch",
                error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NextDefaultsPointOnlyToCommittedReleaseBindings()
    {
        PortableLayout layout = PortableLayout.Resolve(
            @"C:\Next\VibeOCR.Next.exe",
            "production");

        RuntimeInstallerConfiguration configuration =
            RuntimeInstallerConfiguration.ForNext(
                layout,
                accelerator: "nvidia_cuda",
                executable: @"C:\Next\runtime-installer\installer.exe");

        Assert.Equal(@"C:\Next", configuration.ProductRoot);
        Assert.Equal(@"C:\Next\component-lock.json", configuration.ComponentLock);
        Assert.Equal(
            @"C:\Next\backend\runtime-manifest.json",
            configuration.RuntimeManifest);
        Assert.Equal("nvidia_cuda", configuration.Accelerator);
    }

    private static RuntimeInstallerConfiguration Configuration() =>
        new(
            @"C:\Next\runtime-installer\vibeocr-runtime-installer.exe",
            @"C:\Next",
            @"C:\Next\component-lock.json",
            @"C:\Next\backend\runtime-manifest.json",
            "cpu");

    private static StubRunner LaunchRunner(string operation = "ensure") =>
        new(
            new RuntimeInstallerProcessResult(
                0,
                LaunchEnvelope(operation),
                string.Empty));

    private static JsonElement Request(ProcessStartInfo startInfo)
    {
        int index = startInfo.ArgumentList.ToList().IndexOf("--request-json");
        Assert.True(index >= 0);
        using JsonDocument document = JsonDocument.Parse(startInfo.ArgumentList[index + 1]);
        return document.RootElement.Clone();
    }

    private static string InspectEnvelope() =>
        """
        {
          "protocol_version": 2,
          "ok": true,
          "operation": "inspect",
          "state": {
            "status": "ready",
            "runtime_root": "C:\\store\\runtime",
            "accelerator": "cpu",
            "manifest_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "backend_version": "0.7.0",
            "integrity": "verified"
          },
          "launch": null
        }
        """;

    private static string LaunchEnvelope(string operation = "ensure") =>
        $$"""
        {
          "protocol_version": 2,
          "ok": true,
          "operation": "{{operation}}",
          "state": {
            "status": "ready",
            "runtime_root": "C:\\store\\runtime",
            "accelerator": "cpu",
            "manifest_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "backend_version": "0.7.0",
            "integrity": "verified"
          },
          "launch": {
            "python_executable": "C:\\store\\python.exe",
            "supervisor_module": "vibeocr.backend.supervisor.main",
            "working_directory": "C:\\Next",
            "model_root": "C:\\store\\models",
            "environment": {
              "VIBEOCR_RUNTIME_ROOT": "C:\\store",
              "VIBEOCR_MODEL_ROOT": "C:\\store\\models"
            }
          }
        }
        """;

    private static ProcessStartInfo BoundStartInfo(
        string executable,
        string componentLock,
        string manifest)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--request-json");
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(new
        {
            protocol_version = 2,
            operation = "inspect",
            product_root = Path.GetDirectoryName(manifest),
            component_lock = componentLock,
            runtime_manifest = manifest,
            accelerator = "cpu",
        }));
        return startInfo;
    }

    private static async Task WriteComponentLockAsync(
        string componentLock,
        byte[] manifestBytes)
    {
        await File.WriteAllTextAsync(
            componentLock,
            "{\"backend\":{\"runtime_manifest_sha256\":\"" +
            Sha256(manifestBytes) +
            "\"}}",
            TestContext.Current.CancellationToken);
    }

    private static string Sha256(byte[] value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed class StubRunner(RuntimeInstallerProcessResult result)
        : IRuntimeInstallerCommandRunner
    {
        public ProcessStartInfo? LastStartInfo { get; private set; }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            LastStartInfo = startInfo;
            return Task.FromResult(result);
        }
    }
}
