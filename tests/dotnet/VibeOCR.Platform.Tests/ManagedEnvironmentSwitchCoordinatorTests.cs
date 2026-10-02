using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class ManagedEnvironmentSwitchCoordinatorTests
{
    [Fact]
    public void SupervisorOptionsUseInstallerLaunchContractVerbatim()
    {
        var launch = new RuntimeLaunch(
            @"D:\shared\runtimes\python.exe",
            "custom.backend.supervisor",
            @"D:\products\next",
            @"D:\shared\models",
            new Dictionary<string, string>
            {
                ["VIBEOCR_RUNTIME_ROOT"] = @"D:\shared\runtimes",
                ["PADDLE_PDX_CACHE_HOME"] = @"D:\shared\paddlex-cache",
            });

        InferenceSupervisorOptions options = ManagedEnvironmentSwitchCoordinator.BuildSupervisorOptions(
            launch,
            @"D:\products\next\data\supervisor.log",
            TimeSpan.FromSeconds(42),
            new HashSet<string>(["ocr.recognition.v2"], StringComparer.Ordinal),
            injectSoakCrash: true);

        Assert.Equal(launch.PythonExecutable, options.FileName);
        Assert.Equal(["-m", launch.SupervisorModule], options.Arguments);
        Assert.Equal(launch.WorkingDirectory, options.WorkingDirectory);
        Assert.Equal(
            launch.Environment["VIBEOCR_RUNTIME_ROOT"],
            options.EnvironmentOverrides!["VIBEOCR_RUNTIME_ROOT"]);
        Assert.Equal(
            "1",
            options.EnvironmentOverrides["VIBEOCR_SUPERVISOR_SOAK_CRASH_AFTER_READY"]);
        Assert.Equal(
            launch.Environment.Count + 1,
            options.EnvironmentOverrides.Count);
        Assert.Equal(
            launch.Environment["PADDLE_PDX_CACHE_HOME"],
            options.EnvironmentOverrides["PADDLE_PDX_CACHE_HOME"]);
        Assert.Contains("ocr.recognition.v2", options.RequiredCapabilities!);
    }

    [Fact]
    public void ProductRuntimeLaunchUsesIsolatedCurrentCodeWithoutBytecodeWrites()
    {
        var launch = new RuntimeLaunch("C:\\env\\python.exe", "vibeocr.runtime.host.main",
            "C:\\product", "C:\\models", new Dictionary<string, string>
            {
                ["VIBEOCR_PRODUCT_CODE_ROOT"] = "C:\\product\\runtime\\backend\\runtime-code",
            });

        IReadOnlyList<string> arguments = ManagedEnvironmentSwitchCoordinator.RuntimeArguments(launch);

        Assert.Equal(["-I", "-B", "-c"], arguments.Take(3));
        Assert.Contains("VIBEOCR_PRODUCT_CODE_ROOT", arguments[3]);
        Assert.Contains("vibeocr.runtime.host.main", arguments[3]);
    }

    [Fact]
    public async Task EmptyEnvironmentCommitsWithoutStartingSupervisor()
    {
        var manager = new FakeManager(new PreparedEnvironmentSwitch(
            "empty", 1, null, 0, "C:\\empty\\Scripts\\python.exe", false, null));
        var coordinator = new ManagedEnvironmentSwitchCoordinator(manager);
        bool published = false;

        ManagedEnvironmentSession? session = await coordinator.SwitchAsync(
            "empty", null, value => { Assert.Null(value); published = true; },
            "unused.log", TimeSpan.FromSeconds(1), new HashSet<string>(),
            TestContext.Current.CancellationToken);

        Assert.Null(session);
        Assert.True(published);
        Assert.Equal(1, manager.CommitCount);
        Assert.Null(manager.LastHealth);
    }

    [Fact]
    public async Task InstalledEnvironmentWithoutManagerLaunchCannotCommit()
    {
        var manager = new FakeManager(new PreparedEnvironmentSwitch(
            "installed", 2, "old", 4, "C:\\installed\\Scripts\\python.exe", true, null));
        var coordinator = new ManagedEnvironmentSwitchCoordinator(manager);
        bool published = false;

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.SwitchAsync(
            "installed", null, _ => published = true,
            "unused.log", TimeSpan.FromSeconds(1), new HashSet<string>(),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, manager.CommitCount);
        Assert.False(published);
    }

    private sealed class FakeManager(PreparedEnvironmentSwitch prepared) : IManagedEnvironmentClient
    {
        public int CommitCount { get; private set; }
        public StartedEnvironmentHealth? LastHealth { get; private set; }

        public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
            string environmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(prepared);

        public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
            PreparedEnvironmentSwitch value, StartedEnvironmentHealth? startedHealth = null,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            LastHealth = startedHealth;
            return Task.FromResult(new CommittedEnvironmentSwitch(value.EnvironmentId, value.ActiveRevision + 1));
        }

        public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
            string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagedEnvironment> InstallEnvironmentAsync(
            ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
            string? environmentId, string? packageSourceId, string? modelSourceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(
            string environmentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
