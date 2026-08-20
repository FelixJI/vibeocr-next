using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ShellFeatureTests
{
    [Fact]
    public void ApplyHotkeyWithConflictLeavesRegisteredUnchanged()
    {
        var registrar = new FakeHotkeyRegistrar { Accept = false, Conflict = "已占用" };
        var shell = new ShellViewModel(registrar, new FakeStartupRegistrar());

        shell.PendingHotkey = "Ctrl+Shift+Q";
        shell.ApplyHotkey();

        Assert.Equal("Ctrl+Alt+Q", shell.RegisteredHotkey);
        Assert.Contains("已占用", shell.HotkeyStatus);
    }

    [Fact]
    public void ApplyHotkeySuccessUpdatesRegistered()
    {
        var registrar = new FakeHotkeyRegistrar { Accept = true };
        var shell = new ShellViewModel(registrar, new FakeStartupRegistrar());

        shell.PendingHotkey = "Ctrl+Shift+Q";
        shell.ApplyHotkey();

        Assert.Equal("Ctrl+Shift+Q", shell.RegisteredHotkey);
        Assert.Equal("快捷键已更新", shell.HotkeyStatus);
    }

    [Fact]
    public void ApplyHotkeyEmptyRejected()
    {
        var shell = new ShellViewModel(new FakeHotkeyRegistrar(), new FakeStartupRegistrar());
        shell.PendingHotkey = "   ";
        shell.ApplyHotkey();
        Assert.Equal("快捷键不能为空", shell.HotkeyStatus);
    }

    [Fact]
    public void ApplySameHotkeyIsNoOp()
    {
        var shell = new ShellViewModel(new FakeHotkeyRegistrar(), new FakeStartupRegistrar());
        shell.PendingHotkey = shell.RegisteredHotkey;
        shell.ApplyHotkey();
        Assert.Equal("快捷键未改变", shell.HotkeyStatus);
    }

    [Fact]
    public void SetStartWithSystemPersistsAndReflectsFailure()
    {
        var startup = new FakeStartupRegistrar { Ok = false };
        var shell = new ShellViewModel(new FakeHotkeyRegistrar(), startup);

        shell.SetStartWithSystem(true);

        Assert.False(shell.StartWithSystem);
    }

    [Fact]
    public void HotkeyRegistrationKeepsPreviousBindingWhenConfigIsCorrupt()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-hotkey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(root, "VibeOCR.Next.exe"),
            "production");
        layout.EnsurePortableState();
        var native = new FakeHotkeyNativeMethods();
        using var registrar = new WindowsHotkeyRegistrar(
            new GlobalHotkeyService(native),
            layout);
        try
        {
            Assert.True(registrar.Register("Ctrl+Alt+Q", out string? initialConflict));
            Assert.Null(initialConflict);
            Assert.Equal([1], native.ActiveIds);
            const string corruptConfig = "{\"hotkeys\": not-json";
            File.WriteAllText(layout.ConfigFile, corruptConfig);

            bool registered = registrar.Register("Ctrl+Shift+Q", out string? conflict);

            Assert.False(registered);
            Assert.Contains("配置文件已损坏", conflict);
            Assert.Equal([1], native.ActiveIds);
            Assert.Equal(corruptConfig, File.ReadAllText(layout.ConfigFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateCheckSurfacesNewVersion()
    {
        var coordinator = new FakeUpdateCoordinator
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.Available,
                "1.2.0",
                "更快的识别体验"),
        };
        var vm = new UpdateViewModel(coordinator);

        await vm.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.UpdateAvailable);
        Assert.Equal("1.2.0", vm.LatestVersion);
        Assert.Contains("1.2.0", vm.Status);
    }

    [Fact]
    public async Task UpdateDownloadWithoutAvailableIsNoOp()
    {
        var coordinator = new FakeUpdateCoordinator();
        var vm = new UpdateViewModel(coordinator);
        await vm.CheckAsync(TestContext.Current.CancellationToken);

        await vm.DownloadAndApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, vm.Status == "已是最新版本" ? string.Empty : vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UpdateDownloadVerifySuccess()
    {
        var coordinator = new FakeUpdateCoordinator
        {
            CheckResult = new UpdateCheckResult(UpdateCheckStatus.Available, "1.2.0"),
            ApplyResult = new UpdateApplyResult(UpdateApplyStatus.ApplyStarted),
        };
        bool shutdownRequested = false;
        var vm = new UpdateViewModel(coordinator, () => shutdownRequested = true);
        await vm.CheckAsync(TestContext.Current.CancellationToken);

        await vm.DownloadAndApplyAsync(TestContext.Current.CancellationToken);

        Assert.True(shutdownRequested);
        Assert.Contains("即将退出", vm.Status);
    }

    [Fact]
    public async Task UpdateDoesNotExitWhenUpdaterFailsToStart()
    {
        var coordinator = new FakeUpdateCoordinator
        {
            CheckResult = new UpdateCheckResult(UpdateCheckStatus.Available, "1.2.0"),
            ApplyResult = new UpdateApplyResult(
                UpdateApplyStatus.Failed,
                "更新器启动失败，请重试"),
        };
        bool shutdownRequested = false;
        var vm = new UpdateViewModel(coordinator, () => shutdownRequested = true);
        await vm.CheckAsync(TestContext.Current.CancellationToken);

        await vm.DownloadAndApplyAsync(TestContext.Current.CancellationToken);

        Assert.False(shutdownRequested);
        Assert.Contains("启动失败", vm.Status);
    }

    [Fact]
    public void UpdateViewModelPublishesMaintenanceAvailabilityUntilDisposed()
    {
        var maintenance = new ProductMaintenanceCoordinator();
        var vm = new UpdateViewModel(
            new FakeUpdateCoordinator(),
            productMaintenance: maintenance);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        IDisposable runtime = maintenance.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            () => { });
        Assert.True(vm.CanCancelRuntimeMaintenance);
        runtime.Dispose();
        Assert.False(vm.CanCancelRuntimeMaintenance);
        Assert.Equal(
            2,
            changes.Count(name => name == nameof(UpdateViewModel.CanCancelRuntimeMaintenance)));

        vm.Dispose();
        using IDisposable afterDispose = maintenance.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            () => { });
        Assert.Equal(
            2,
            changes.Count(name => name == nameof(UpdateViewModel.CanCancelRuntimeMaintenance)));
    }

    private sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool Accept { get; set; } = true;
        public string? Conflict { get; set; }
        public bool Register(string hotkey, out string? conflict)
        {
            conflict = Conflict;
            return Accept;
        }
        public void Unregister() { }
    }

    private sealed class FakeHotkeyNativeMethods : IHotkeyNativeMethods
    {
        private readonly HashSet<int> _activeIds = [];

        public IReadOnlyCollection<int> ActiveIds => _activeIds.Order().ToArray();

        public bool Register(
            nint windowHandle,
            int id,
            HotkeyModifiers modifiers,
            uint virtualKey) => _activeIds.Add(id);

        public bool Unregister(nint windowHandle, int id) => _activeIds.Remove(id);
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool Ok { get; set; } = true;
        public bool SetEnabled(bool enabled) => Ok;
    }

    private sealed class FakeUpdateCoordinator : IUpdateCoordinator
    {
        public UpdateCheckResult CheckResult { get; set; } =
            new(UpdateCheckStatus.Latest, "0.3.0");
        public UpdateApplyResult ApplyResult { get; set; } =
            new(UpdateApplyStatus.Downloaded);

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CheckResult);

        public Task<UpdateApplyResult> DownloadAndApplyAsync(
            IProgress<int>? progress,
            CancellationToken cancellationToken) => Task.FromResult(ApplyResult);
    }
}
