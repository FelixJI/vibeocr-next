// Phase 7B tests: SettingsViewModel v2 residency path.
//
// Verifies plan §7B: Settings reads residency status (TTL/pin/LRU/VRAM) via
// the v2 supervisor client (IInferenceClient.GetResidencyAsync). The legacy
// settings.snapshot path stays covered by SettingsViewModelTests; this file
// is additive.
using VibeOCR.App.Features.Settings;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class SettingsViewModelSupervisorTests
{
    [Fact]
    public async Task LoadResidencyPopulatesV2Observables()
    {
        var fake = new FakeSettingsInferenceClient(defaultTtl: 600, vramTotal: 24576, vramUsed: 2000);
        fake.Entries.Add(new ResidencyEntry
        {
            Pipeline = "OCR",
            Kind = ResidencyKind.SoftTtl,
            ActiveLeases = 1,
            RemainingTtlSeconds = 240,
            EstimatedVramMb = 1200,
        });
        fake.Entries.Add(new ResidencyEntry
        {
            Pipeline = "MinerU",
            Kind = ResidencyKind.Pinned,
            ActiveLeases = 0,
            EstimatedVramMb = 800,
        });
        fake.Pipelines.Add(new PipelineSpec { Name = "OCR", TtlSeconds = null, Pinned = false });
        fake.Pipelines.Add(new PipelineSpec { Name = "MinerU", TtlSeconds = 600, Pinned = false });

        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(600, viewModel.DefaultTtlSeconds);
        Assert.Equal(24576, viewModel.VramTotalMb);
        Assert.Equal(2000, viewModel.VramUsedMb);
        Assert.Equal(2, viewModel.ResidencyEntries.Count);
        Assert.Equal("OCR", viewModel.ResidencyEntries[0].Pipeline);
        Assert.Equal(ResidencyKind.SoftTtl, viewModel.ResidencyEntries[0].Kind);
        Assert.Equal("MinerU", viewModel.ResidencyEntries[1].Pipeline);
        Assert.Equal(ResidencyKind.Pinned, viewModel.ResidencyEntries[1].Kind);
        Assert.Equal(2, viewModel.ResidencyPipelines.Count);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("600", viewModel.Status);
    }

    [Fact]
    public async Task LoadResidencyLocalizesTypedError()
    {
        var fake = new FakeSettingsInferenceClient(
            residencyThrows: new InferenceClientException(HttpV2ErrorCode.OutOfMemory, "oom", true));
        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal("内存或显存不足", viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task LoadResidencyReportsSupervisorDisconnect()
    {
        var fake = new FakeSettingsInferenceClient(
            residencyThrows: new InferenceClientException(HttpV2ErrorCode.BackendUnavailable, "down", true));
        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Supervisor 暂不可用，请重试", viewModel.Status);
    }

    [Fact]
    public async Task LoadSnapshotCompletesSuccessfully()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsInferenceClient(defaultTtl: 300));
        await viewModel.LoadSnapshotAsync(CancellationToken.None);
        Assert.Equal(300, viewModel.DefaultTtlSeconds);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task LoadResidencyEmptyStatusIsHarmless()
    {
        // A supervisor with no loaded models: empty entries/pipelines, no VRAM.
        var fake = new FakeSettingsInferenceClient(defaultTtl: 300, vramTotal: null, vramUsed: null);
        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(300, viewModel.DefaultTtlSeconds);
        Assert.Null(viewModel.VramTotalMb);
        Assert.Null(viewModel.VramUsedMb);
        Assert.Empty(viewModel.ResidencyEntries);
        Assert.Empty(viewModel.ResidencyPipelines);
        Assert.Contains("0", viewModel.Status); // "已驻留管线 0 个"
    }

    // ------------------------------------------------------------------
    [Fact]
    public async Task EmptyEnvironmentCanShowSettingsWithoutReportingFailure()
    {
        var viewModel = new SettingsViewModel(new VibeOCR.App.Inference.DeferredInferenceClient());
        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal("尚未连接运行环境；可配置来源或准备依赖。", viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void UnloadedBackendIsNotProjectedAsATargetDevice()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsInferenceClient());
        Assert.Null(viewModel.Backend);
        // 默认目标 cpu 只是待选值，未读取真实快照前不得冒充当前设备。
        Assert.False(viewModel.CanSwitchBackend);
    }

    [Fact]
    public async Task InvalidateSnapshotDiscardsInFlightRuntimeRead()
    {
        var pending = new TaskCompletionSource<RuntimeStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new GatedRuntimeStatusClient(pending);
        var viewModel = new SettingsViewModel(fake);
        Task load = viewModel.LoadSnapshotAsync(CancellationToken.None);

        // 环境切换/服务实例更换使旧读取失效：迟到读数不得再投影。
        viewModel.InvalidateSnapshot();
        pending.SetResult(ReadyRuntimeStatus());
        await load;

        Assert.Null(viewModel.Backend);
        Assert.False(viewModel.IsBusy);
        // 旧实例的状态文案不再冒充当前状态；用户配置（sources/MinerU）不清空。
        Assert.Equal("服务实例已更换，状态待重新检查。", viewModel.Status);
    }

    [Fact]
    public async Task InvalidatedLoadReleasesBusyOwnershipForTheNextRead()
    {
        var pending = new TaskCompletionSource<RuntimeStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new GatedRuntimeStatusClient(pending);
        var viewModel = new SettingsViewModel(fake);
        Task first = viewModel.LoadSnapshotAsync(CancellationToken.None);
        viewModel.InvalidateSnapshot();
        pending.SetResult(ReadyRuntimeStatus());
        await first;

        // 失效后的新读取正常接管并释放 busy（P2-2 拥有者闭合）。
        await viewModel.LoadSnapshotAsync(CancellationToken.None);

        Assert.False(viewModel.IsBusy);
        Assert.Equal("cpu", viewModel.Backend);
    }

    [Fact]
    public async Task SnapshotReadProjectsBackendFromRealProfile()
    {
        var fake = new GatedRuntimeStatusClient(TaskCompletionSourceFor(
            ReadyRuntimeStatus()));
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(CancellationToken.None);
        Assert.Equal("cpu", viewModel.Backend);
    }

    [Fact]
    public async Task StaleLoadCompletionKeepsTheNewerReadsBusy()
    {
        var client = new SequencedRuntimeStatusClient();
        TaskCompletionSource<RuntimeStatusSnapshot> firstGate = client.Enqueue();
        TaskCompletionSource<RuntimeStatusSnapshot> secondGate = client.Enqueue();
        var viewModel = new SettingsViewModel(client);

        Task first = viewModel.LoadSnapshotAsync(CancellationToken.None);
        Task second = viewModel.LoadSnapshotAsync(CancellationToken.None);

        // 旧读取（first，代际已被 second 递增作废）迟到完成：不得把新读取
        // 的 busy 一并清除（新读取仍在进行中）。
        firstGate.SetResult(ReadyRuntimeStatus());
        await first;
        Assert.True(viewModel.IsBusy);

        secondGate.SetResult(ReadyRuntimeStatus());
        await second;
        Assert.False(viewModel.IsBusy);
        Assert.Equal("cpu", viewModel.Backend);
    }

    [Fact]
    public async Task ReentrantInvalidationDuringBusyStartDoesNotWriteStaleStatus()
    {
        var pending = new TaskCompletionSource<RuntimeStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new GatedRuntimeStatusClient(pending);
        var viewModel = new SettingsViewModel(fake);
        // 模拟同步 PropertyChanged 链上的实例更换失效（重入）：busy 置位
        // 事件内直接作废旧代。
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.IsBusy) && viewModel.IsBusy)
                viewModel.InvalidateSnapshot();
        };

        Task load = viewModel.LoadSnapshotAsync(CancellationToken.None);
        pending.SetResult(ReadyRuntimeStatus());
        await load;

        // 重入失效后不得再写回加载文案/旧状态：终态保持失效语义。
        Assert.False(viewModel.IsBusy);
        Assert.Null(viewModel.Backend);
        Assert.Equal("服务实例已更换，状态待重新检查。", viewModel.Status);
    }

    private static TaskCompletionSource<RuntimeStatusSnapshot> TaskCompletionSourceFor(
        RuntimeStatusSnapshot snapshot)
    {
        var source = new TaskCompletionSource<RuntimeStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(snapshot);
        return source;
    }

    private static RuntimeStatusSnapshot ReadyRuntimeStatus() => new()
    {
        InstanceId = "sup-test",
        ServiceState = RuntimeServiceState.Ready,
        BackendVersion = "0.14.0",
        Profile = new RuntimeProfileStatus
        {
            ProfileId = "win-x64-cpu",
            Accelerator = RuntimeAccelerator.Cpu,
            Components = [],
        },
    };

    private sealed class GatedRuntimeStatusClient(
        TaskCompletionSource<RuntimeStatusSnapshot> pending) : InferenceClientStub, IInferenceClient
    {
        public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(
            CancellationToken cancellationToken) => pending.Task;

        public override Task<ResidencyStatus> GetResidencyAsync(
            CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());
    }

    private sealed class SequencedRuntimeStatusClient : InferenceClientStub, IInferenceClient
    {
        private readonly Queue<TaskCompletionSource<RuntimeStatusSnapshot>> _pending = new();

        public TaskCompletionSource<RuntimeStatusSnapshot> Enqueue()
        {
            var source = new TaskCompletionSource<RuntimeStatusSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(source);
            return source;
        }

        public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(
            CancellationToken cancellationToken) => _pending.Dequeue().Task;

        public override Task<ResidencyStatus> GetResidencyAsync(
            CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());
    }
    // Fakes
    // ------------------------------------------------------------------

    private sealed class FakeSettingsInferenceClient : InferenceClientStub
    {
        private readonly int _defaultTtl;
        private readonly int? _vramTotal;
        private readonly int? _vramUsed;
        private readonly InferenceClientException? _residencyThrows;

        public FakeSettingsInferenceClient(
            int defaultTtl = 300,
            int? vramTotal = null,
            int? vramUsed = null,
            InferenceClientException? residencyThrows = null)
        {
            _defaultTtl = defaultTtl;
            _vramTotal = vramTotal;
            _vramUsed = vramUsed;
            _residencyThrows = residencyThrows;
        }

        public List<ResidencyEntry> Entries { get; } = [];
        public List<PipelineSpec> Pipelines { get; } = [];
        public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken)
        {
            if (_residencyThrows is not null)
            {
                throw _residencyThrows;
            }

            return Task.FromResult(new ResidencyStatus
            {
                DefaultTtlSeconds = _defaultTtl,
                Entries = Entries,
                Pipelines = Pipelines,
                VramTotalMb = _vramTotal,
                VramUsedMb = _vramUsed,
            });
        }

        public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SettingsSnapshot());
    }

}
