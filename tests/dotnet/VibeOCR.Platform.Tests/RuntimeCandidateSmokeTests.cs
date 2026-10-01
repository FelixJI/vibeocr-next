// Runtime candidate AC6 smoke: one real-process round trip against an
// isolated copy of a release candidate product root.
//
// The fact is enabled only when scripts/smoke_runtime_candidate.ps1 exports
// VIBEOCR_RUNTIME_CANDIDATE_SMOKE_ROOT (a fresh unique isolation root holding
// the copied candidate at "<root>/candidate") together with the synthetic
// image path; every regular Platform run stays explicitly skipped. Fail
// closed on timeout, non-success terminal state, or an empty recognition
// result. Nothing under the smoke root is deleted: the isolated state is the
// acceptance evidence.
using System.Security.Cryptography;
using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Runtime.Contracts.Generated;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class RuntimeCandidateSmokeTests
{
    private const string SmokeRootVariable = "VIBEOCR_RUNTIME_CANDIDATE_SMOKE_ROOT";
    private const string SmokeImageVariable = "VIBEOCR_RUNTIME_CANDIDATE_SMOKE_IMAGE";
    private const string ClientItemKey = "synthetic-image";
    private const string ExpectedWord = "VibeOCR";
    private const string ExpectedDigits = "123";

    private static readonly TimeSpan EnsureTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SupervisorStartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ObservePollInterval = TimeSpan.FromMilliseconds(250);

    // SkipUnless 静态属性:普通 Platform 运行没有隔离根与合成图片时
    // 明确 Skipped,由脚本注入的环境唯一启用本 smoke。
    public static bool SmokeEnvironmentConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SmokeRootVariable)) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SmokeImageVariable));

    [Fact(
        Skip = "Runtime candidate smoke 仅由 scripts/smoke_runtime_candidate.ps1 以隔离 "
            + "VIBEOCR_RUNTIME_CANDIDATE_SMOKE_ROOT 与合成图片环境启用。",
        SkipUnless = nameof(SmokeEnvironmentConfigured))]
    public async Task CandidateInstallsBaseRuntimeAndRecognizesSyntheticImage()
    {
        string smokeRoot = RequireEnvironmentVariable(SmokeRootVariable);
        string imagePath = RequireEnvironmentVariable(SmokeImageVariable);
        if (!Directory.Exists(smokeRoot))
        {
            throw new InvalidOperationException($"隔离 smoke 根不存在: {smokeRoot}");
        }
        if (!File.Exists(imagePath))
        {
            throw new InvalidOperationException($"合成图片不存在: {imagePath}");
        }
        string productRoot = Path.Combine(smokeRoot, "candidate");
        if (!Directory.Exists(productRoot))
        {
            throw new InvalidOperationException($"隔离候选副本缺失: {productRoot}");
        }

        // 与生产 --install-root 相同的解析:候选副本自身是稳定安装根。
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(productRoot, "app", "VibeOCR.WinUI.exe"),
            "production",
            installRootOverride: productRoot);
        Assert.Equal(Path.GetFullPath(productRoot), layout.InstallRoot);
        // 一切可变状态必须锚定在隔离副本内;测试不读写用户数据或缓存。
        AssertIsUnder(productRoot, layout.DataRoot, "state root");
        layout.EnsurePortableState();
        Assert.True(File.Exists(layout.ComponentLock), $"缺少组件锁: {layout.ComponentLock}");
        Assert.True(File.Exists(layout.RuntimeManifest), $"缺少运行时清单: {layout.RuntimeManifest}");
        Assert.True(
            File.Exists(layout.RuntimeInstaller),
            $"缺少 Runtime Installer: {layout.RuntimeInstaller}");

        RuntimeInstallerConfiguration configuration =
            RuntimeInstallerConfiguration.ForNext(layout, accelerator: "cpu");
        Assert.Equal(Path.GetFullPath(productRoot), configuration.ProductRoot);
        var client = new RuntimeInstallerClient(configuration);

        using CancellationTokenSource ensureCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ensureCancellation.CancelAfter(EnsureTimeout);
        CancellationToken ensureToken = ensureCancellation.Token;

        // 安装预览限定基础组件:空 install 列表显式选择 base-only 范围,
        // 绝不做 GPU/Paddle/MinerU 全量重装。
        Assert.True(
            client.SupportsInstallPlan,
            "候选 Runtime 不支持安装预览(runtime.install-plan.v1)。");
        Host.RuntimeInstallPlan plan = await client.PreviewInstallAsync(
            new RuntimeInstallSelection { InstallComponentIds = [] },
            "cpu",
            ensureToken);
        Assert.Empty(plan.Blockers);
        Assert.Subset(
            ReadBaseComponentIds(layout.RuntimeManifest),
            plan.EffectiveComponentIds.ToHashSet(StringComparer.Ordinal));

        // 生产启动路径:隔离副本是全新安装,启动选择为空(仅基础组件),
        // ensure 把基础运行环境安装进隔离副本(候选自带离线 base 包)。
        RuntimeInstallSelection startupSelection =
            await client.ReadStartupSelectionAsync(ensureToken);
        IReadOnlyList<string>? startupComponents = startupSelection.InstallComponentIds;
        Assert.NotNull(startupComponents);
        Assert.Empty(startupComponents);
        RuntimeLaunch launch = await client.EnsureAsync(
            startupSelection,
            $"smoke-{Guid.NewGuid():N}",
            progress: null,
            ensureToken);
        Assert.True(File.Exists(launch.PythonExecutable), $"缺少 Python: {launch.PythonExecutable}");
        Assert.True(
            Directory.Exists(launch.WorkingDirectory),
            $"缺少工作目录: {launch.WorkingDirectory}");
        Assert.True(Directory.Exists(launch.ModelRoot), $"缺少模型根: {launch.ModelRoot}");
        Assert.NotEmpty(launch.Environment);
        // Python/模型/运行时存储必须锚定在隔离的 state 存储内。
        AssertIsUnder(layout.DataRoot, launch.PythonExecutable, "python executable");
        AssertIsUnder(layout.DataRoot, launch.ModelRoot, "model root");
        Assert.True(
            launch.Environment.TryGetValue("VIBEOCR_RUNTIME_ROOT", out string? runtimeRootValue),
            "launch 环境缺少 VIBEOCR_RUNTIME_ROOT。");
        Assert.NotNull(runtimeRootValue);
        AssertIsUnder(layout.DataRoot, runtimeRootValue, "runtime root");
        // 后端身份校验:launch 环境声明的产品根必须就是隔离副本。
        Assert.True(
            launch.Environment.TryGetValue("VIBEOCR_PRODUCT_ROOT", out string? launchProductRoot) &&
                !string.IsNullOrWhiteSpace(launchProductRoot) &&
                string.Equals(
                    Path.GetFullPath(launchProductRoot),
                    Path.GetFullPath(productRoot),
                    StringComparison.Ordinal),
            $"launch 环境 VIBEOCR_PRODUCT_ROOT 未指向隔离副本: {launchProductRoot}");
        // 生产 launch 契约:installer 把 supervisor cwd 固定为产品根本身
        // (working_directory = product_root),完整路径相等才是准确预期,
        // 不是其子目录。
        Assert.Equal(
            Path.GetFullPath(productRoot),
            Path.GetFullPath(launch.WorkingDirectory));

        // 与 ManagedEnvironmentSwitchCoordinator 相同:launch.Environment 全量继承,
        // 会话令牌仅经环境变量传递。
        IReadOnlySet<string> requiredCapabilities =
            RuntimeCapabilityRequirements.Read(layout.ComponentLock);
        string sessionToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var supervisor = new InferenceSupervisorProcess(
            new InferenceSupervisorOptions(
                launch.PythonExecutable,
                ["-m", launch.SupervisorModule],
                launch.WorkingDirectory,
                Path.Combine(layout.LogsRoot, "supervisor-smoke.log"),
                SupervisorStartupTimeout,
                requiredCapabilities,
                launch.Environment.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)),
            sessionToken);
        try
        {
            SupervisorReadyEnvelope ready = await supervisor.StartAsync(
                TestContext.Current.CancellationToken);
            await using var inference = new InferenceHttpClient(ready.BaseUrl, sessionToken);

            // health 必须是 wire 2,并真实上报组件锁要求的能力集。
            Wire.Health health = await inference.GetHealthAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpV2Schema.Version, health.SchemaVersion);
            Assert.Equal(RuntimeProtocol.ProtocolVersion, health.ProtocolVersion);
            Assert.True(health.Ready, "Supervisor health 未就绪。");
            Assert.False(health.Draining, "Supervisor health 处于 draining。");
            Assert.Subset(
                health.Capabilities.ToHashSet(StringComparer.Ordinal),
                requiredCapabilities.ToHashSet(StringComparer.Ordinal));

            using CancellationTokenSource recognition = CancellationTokenSource
                .CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            recognition.CancelAfter(RecognitionTimeout);
            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath, recognition.Token);
            var request = new SubmitRequest
            {
                RequestId = $"smoke-{Guid.NewGuid():N}",
                Kind = JobKind.Recognition,
                Priority = JobPriority.Interactive,
                Pipeline = new PipelineSelection { PipelineId = "OCR" },
                Items =
                [
                    new SubmitItem
                    {
                        ClientItemKey = ClientItemKey,
                        Ordinal = 0,
                        DisplayName = Path.GetFileName(imagePath),
                        Source = new Dictionary<string, JsonElement>
                        {
                            ["type"] = JsonSerializer.SerializeToElement("upload.v1"),
                            ["attachment"] = JsonSerializer.SerializeToElement(ClientItemKey),
                        },
                    },
                ],
            };
            JobRef referral = await inference.SubmitAsync(
                request,
                new Dictionary<string, SubmitUpload>
                {
                    [ClientItemKey] = new("image/png", imageBytes),
                },
                recognition.Token);

            ItemOutcome outcome;
            try
            {
                outcome = await ObserveTerminalOutcomeAsync(inference, referral, recognition.Token);
            }
            catch (OperationCanceledException) when (
                !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"OCR 任务未在 {RecognitionTimeout} 内完成。");
            }

            Assert.Equal(ItemState.Succeeded, outcome.State);
            IDictionary<string, JsonElement>? payloadValue = outcome.Payload;
            Assert.NotNull(payloadValue);
            IDictionary<string, JsonElement> payload = payloadValue;
            if (!payload.TryGetValue("raw_text", out JsonElement rawTextElement) ||
                rawTextElement.ValueKind != JsonValueKind.String ||
                rawTextElement.GetString() is not { } rawText ||
                string.IsNullOrWhiteSpace(rawText))
            {
                throw new InvalidOperationException("OCR 结果缺少非空 raw_text 字符串,按 fail closed 处理。");
            }
            Assert.Contains(ExpectedWord, rawText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ExpectedDigits, rawText, StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(layout.OutputRoot, "runtime-candidate-smoke-raw-text.txt"),
                rawText);
        }
        finally
        {
            // 仅处置本次 smoke 拉起的子进程树;隔离证据全部保留。
            supervisor.Dispose();
        }
    }

    private static string RequireEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"缺少必需的 smoke 环境变量 {name}。");
        }
        return value;
    }

    private static void AssertIsUnder(string root, string path, string field)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        Assert.True(
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"{field} 未锚定在隔离副本内: {fullPath}");
    }

    /// <summary>
    /// Mirrors the production base-profile convention used by
    /// <see cref="RuntimeInstallerClient.ReadStartupSelectionAsync"/>: the
    /// win-x64-base profile declares the base-only component closure.
    /// </summary>
    private static HashSet<string> ReadBaseComponentIds(string runtimeManifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(runtimeManifestPath));
        HashSet<string> ids = document.RootElement
            .GetProperty("profiles")
            .GetProperty("win-x64-base")
            .GetProperty("components")
            .EnumerateArray()
            .Select(component => component.GetProperty("component_id").GetString()
                ?? throw new InvalidDataException("runtime manifest 的 component_id 必须是字符串。"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(ids);
        return ids;
    }

    /// <summary>
    /// Observes the single-item recognition job until a terminal snapshot with
    /// no pending deltas. Any non-completed terminal state, protocol drift, or
    /// missing outcome fails closed.
    /// </summary>
    private static async Task<ItemOutcome> ObserveTerminalOutcomeAsync(
        InferenceHttpClient inference,
        JobRef referral,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> clientKeyByItemId = referral.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .ToDictionary(
                item => item.ItemId,
                item => item.ClientItemKey ?? string.Empty,
                StringComparer.Ordinal);
        ItemOutcome? outcome = null;
        JobState? terminalState = null;
        int afterSequence = 0;
        while (terminalState is null)
        {
            JobUpdate update = await inference.ObserveAsync(
                referral.JobId,
                afterSequence,
                cancellationToken);
            if (!string.Equals(update.Snapshot.JobId, referral.JobId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("观察快照属于另一个任务。");
            }
            if (update.ThroughSequence < afterSequence)
            {
                throw new InvalidOperationException("观察游标回退。");
            }
            foreach (ItemOutcome delta in update.Outcomes)
            {
                if (!clientKeyByItemId.TryGetValue(delta.ItemId, out string? clientKey) ||
                    clientKey != ClientItemKey)
                {
                    throw new InvalidOperationException($"OCR 任务返回了未知条目 {delta.ItemId}。");
                }
                if (outcome is not null)
                {
                    throw new InvalidOperationException("OCR 任务返回了重复的条目结果。");
                }
                outcome = delta;
            }

            bool terminal = update.Snapshot.State is
                JobState.Completed or
                JobState.CompletedWithErrors or
                JobState.Cancelled or
                JobState.Failed;
            if (terminal && !update.More)
            {
                terminalState = update.Snapshot.State;
                break;
            }
            if (update.More && update.ThroughSequence <= afterSequence)
            {
                throw new InvalidOperationException("OCR 任务声称还有数据但游标未前进。");
            }
            afterSequence = update.ThroughSequence;
            await Task.Delay(ObservePollInterval, cancellationToken);
        }

        if (terminalState is not JobState.Completed)
        {
            string detail = outcome is { ErrorCode: { } code } ? $"({code})" : string.Empty;
            throw new InvalidOperationException(
                $"OCR 任务终态为 {terminalState}{detail},按 fail closed 处理。");
        }
        if (outcome is null)
        {
            throw new InvalidOperationException(
                $"OCR 任务终态 {referral.JobId} 未返回任何条目结果。");
        }
        return outcome;
    }
}
