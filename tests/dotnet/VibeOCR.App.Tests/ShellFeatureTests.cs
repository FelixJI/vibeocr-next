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
    public void StartupConflictDoesNotClaimAnActiveHotkeyAndAllowsSameKeyRetry()
    {
        var registrar = new FakeHotkeyRegistrar { Accept = false, Conflict = "已占用" };
        var shell = new ShellViewModel(registrar, new FakeStartupRegistrar());
        shell.InitializeHotkey();
        Assert.Empty(shell.RegisteredHotkey);
        Assert.Equal("Ctrl+Alt+Q", shell.PendingHotkey);
        Assert.Contains("已占用", shell.HotkeyStatus);
        registrar.Accept = true;
        shell.ApplyHotkey();
        Assert.Equal("Ctrl+Alt+Q", shell.RegisteredHotkey);
        Assert.Equal("快捷键已更新", shell.HotkeyStatus);
    }

    [Fact]
    public void StartupRegistersConfiguredShortcut()
    {
        var shell = new ShellViewModel(new FakeHotkeyRegistrar { Accept = true },
            new FakeStartupRegistrar(), initialHotkey: "Ctrl+Shift+F8");
        shell.InitializeHotkey();
        Assert.Equal("Ctrl+Shift+F8", shell.RegisteredHotkey);
    }
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
    public void TrayPreferencePersistsAndOnlyHidesWhenEnabled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-tray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.Next.exe"), "production");
        layout.EnsurePortableState();
        try
        {
            File.WriteAllText(layout.ConfigFile, "{\"untouched\":42}");
            var store = new ShellPreferencesStore(layout);
            int hidden = 0;
            var shell = new ShellViewModel(new FakeHotkeyRegistrar(), new FakeStartupRegistrar(),
                hideWindow: () => hidden++, preferences: store);
            shell.OnMinimized();
            Assert.Equal(0, hidden);
            shell.SetMinimizeToTray(true);
            var reopened = new ShellViewModel(new FakeHotkeyRegistrar(), new FakeStartupRegistrar(),
                hideWindow: () => hidden++, preferences: store);
            Assert.True(reopened.MinimizeToTray);
            reopened.OnMinimized();
            Assert.Equal(1, hidden);
            Assert.Contains("\"untouched\": 42", File.ReadAllText(layout.ConfigFile));
            File.WriteAllText(layout.ConfigFile, "{invalid");
            reopened.SetMinimizeToTray(false);
            Assert.True(reopened.MinimizeToTray);
            Assert.NotEmpty(reopened.SettingsError);
            Assert.Equal("{invalid", File.ReadAllText(layout.ConfigFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StartupSelectionSurvivesShellRecreation()
    {
        var registrar = new FakeStartupRegistrar();
        var shell = new ShellViewModel(new FakeHotkeyRegistrar(), registrar);
        shell.SetStartWithSystem(true);
        Assert.True(new ShellViewModel(new FakeHotkeyRegistrar(), registrar).StartWithSystem);
    }

    [Fact]
    public void FailedStartupDisableKeepsActualSelection()
    {
        var registrar = new FakeStartupRegistrar();
        var shell = new ShellViewModel(new FakeHotkeyRegistrar(), registrar);
        shell.SetStartWithSystem(true);
        registrar.Ok = false;
        shell.SetStartWithSystem(false);
        Assert.True(shell.StartWithSystem);
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
    public void AboutMetadataReflectsRepositoryFacts()
    {
        var shell = new ShellViewModel(new FakeHotkeyRegistrar(), new FakeStartupRegistrar());

        Assert.Equal("MIT", shell.License);
        Assert.Equal(
            "https://github.com/FelixJI/vibeocr-next",
            shell.ProjectUri.AbsoluteUri);
    }

    [Fact]
    public void InitialUpdateStateIsUncheckedInsteadOfClaimingLatest()
    {
        var vm = new UpdateViewModel(new FakeUpdateCoordinator());

        // 未检查时不得误报已是最新：受限网络机器上的误导性“最新”曾让用户
        // 放弃排查真实无法访问的更新源。
        Assert.Equal("update.unchecked", vm.StatusCode);
        Assert.False(vm.UpdateAvailable);
        Assert.Null(vm.LatestVersion);
    }

    [Fact]
    public async Task CheckFailureSurfacesUnderstandableErrorInsteadOfWaitingForever()
    {
        var coordinator = new FailingUpdateCoordinator();
        var vm = new UpdateViewModel(coordinator);

        await vm.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("update.error", vm.StatusCode);
        Assert.False(vm.UpdateAvailable);
        Assert.Contains("无法连接更新源", vm.StatusMessage);
        Assert.Contains("无法连接更新源", vm.Status);
    }

    [Fact]
    public void SetSourceResetsPreviousCheckResultForNewSource()
    {
        var coordinator = new SelectableUpdateCoordinator();
        var vm = new UpdateViewModel(coordinator)
        {
        };
        Assert.Single(vm.Sources!);

        vm.SetSource("custom", "url_prefix", "https://mirror.example.com/vibeocr");

        Assert.Equal("update.unchecked", vm.StatusCode);
        Assert.False(vm.UpdateAvailable);
        Assert.Null(vm.LatestVersion);
        Assert.Equal(2, vm.Sources!.Count);
        Assert.Contains(vm.Sources!, source => source.Id == "custom" && source.Selected);
        Assert.Equal("custom", coordinator.LastSelectedSourceId);
        Assert.Equal("url_prefix", coordinator.LastSelectedKind);
        Assert.Equal("https://mirror.example.com/vibeocr", coordinator.LastSelectedUrl);
    }

    [Fact]
    public async Task SetSourceIsRefusedWhileUpdateOperationIsBusy()
    {
        var coordinator = new BlockingUpdateCoordinator();
        var vm = new UpdateViewModel(coordinator);
        Task check = vm.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsBusy);

        Assert.Throws<UpdateSourceBusyException>(
            () => vm.SetSource("direct", null, null));

        coordinator.Release();
        await check;
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
        public bool IsEnabled { get; private set; }
        public bool SetEnabled(bool enabled)
        {
            if (Ok) IsEnabled = enabled;
            return Ok;
        }
    }

    private sealed class FailingUpdateCoordinator : IUpdateCoordinator
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateCheckResult(
                UpdateCheckStatus.Error, ErrorMessage: "无法连接更新源。"));

        public Task<UpdateApplyResult> DownloadAndApplyAsync(
            IProgress<int>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateApplyResult(UpdateApplyStatus.Failed));

        public UpdateSourceCatalog LoadSources() => new(
            [new UpdateSourceOption(
                "direct", "direct", "官方直连（GitHub）",
                VelopackFeedFactory.DirectBaseUrl, Selected: true)],
            VelopackFeedFactory.DirectBaseUrl);

        public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url) =>
            LoadSources();
    }

    private sealed class SelectableUpdateCoordinator : IUpdateCoordinator
    {
        public string? LastSelectedSourceId { get; private set; }
        public string? LastSelectedKind { get; private set; }
        public string? LastSelectedUrl { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Latest, "0.3.0"));

        public Task<UpdateApplyResult> DownloadAndApplyAsync(
            IProgress<int>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateApplyResult(UpdateApplyStatus.Downloaded));

        public UpdateSourceCatalog LoadSources() => new(
            [new UpdateSourceOption(
                "direct", "direct", "官方直连（GitHub）",
                VelopackFeedFactory.DirectBaseUrl, Selected: true)],
            VelopackFeedFactory.DirectBaseUrl);

        public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url)
        {
            LastSelectedSourceId = sourceId;
            LastSelectedKind = kind;
            LastSelectedUrl = url;
            return new(
            [
                new UpdateSourceOption(
                    "direct", "direct", "官方直连（GitHub）",
                    VelopackFeedFactory.DirectBaseUrl, Selected: false),
                new UpdateSourceOption(
                    "custom", kind ?? "url_prefix", "自定义镜像前缀",
                    url ?? "https://mirror.example.com/vibeocr", Selected: true),
            ],
            $"{url}/https://github.com/FelixJI/vibeocr-next/releases/latest/download/");
        }
    }

    private sealed class BlockingUpdateCoordinator : IUpdateCoordinator
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return new UpdateCheckResult(UpdateCheckStatus.Latest, "0.3.0");
        }

        public Task<UpdateApplyResult> DownloadAndApplyAsync(
            IProgress<int>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateApplyResult(UpdateApplyStatus.Downloaded));

        public UpdateSourceCatalog LoadSources() => new(
            [new UpdateSourceOption(
                "direct", "direct", "官方直连（GitHub）",
                VelopackFeedFactory.DirectBaseUrl, Selected: true)],
            VelopackFeedFactory.DirectBaseUrl);

        public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url) =>
            LoadSources();
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

        public UpdateSourceCatalog LoadSources() => new(
            [new UpdateSourceOption(
                "direct", "direct", "官方直连（GitHub）",
                VelopackFeedFactory.DirectBaseUrl, Selected: true)],
            VelopackFeedFactory.DirectBaseUrl);

        public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url) =>
            LoadSources();
    }
}
