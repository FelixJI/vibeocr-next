using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Platform.Bootstrap;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class RuntimeInstallerClientTests
{
    [Fact]
    public async Task CompatibleQueryUsesRuntimeRecipeAndPreservesTypedResult()
    {
        const string response = """
            {"protocol_version":2,"response_kind":"environment","action":"find_compatible","result":{
              "recipe":"rapidocr-cpu","recipe_lock":"bound-lock",
              "recipes":[{"id":"rapidocr-cpu","display_name":"RapidOCR · CPU",
                "configured_recognition_types":["text"],"accelerator":"cpu","target_device":"cpu",
                "python_version":"3.13.15","abi":"cp313","platform":"win_amd64","scope_id":"base",
                "component_ids":["runtime_host","rapidocr-base"],"recipe_lock":"bound-lock",
                "dependencies":["rapidocr==3.7.0"],"dependency_origin":"bundled_pack",
                "python_origin":"product_bundle","runtime_wheel_origin":"product_bundle"}],
              "selected":{"environment_id":"abc","environment_revision":2,"recipe":"rapidocr-cpu",
                "selection_reason":"active_environment"},
              "environments":[{"environment_id":"abc","name":"用户名称","revision":2,"status":"installed",
                "recipe":"rapidocr-cpu","active":true,"selected":true,"reason_code":"selected_active_environment"}]
            }}
            """;
        var runner = new QueueRunner(
            new RuntimeInstallerProcessResult(0, response, ""),
            new RuntimeInstallerProcessResult(0,
                """
                {"protocol_version":2,"response_kind":"environment","action":"list","result":{
                  "active_id":null,"active_revision":0,"environments":[],
                  "recipes":[{"id":"rapidocr-cpu","display_name":"RapidOCR · CPU","target_device":"cpu"}],
                  "hardware":{"nvidia_driver":{"status":"unsupported","reason_code":"nvidia_driver_incompatible","driver_version":"527.00"}}
                }}
                """, ""));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        ManagedEnvironmentQueryResult result = await client.FindCompatibleEnvironmentAsync(
            "rapidocr-cpu", TestContext.Current.CancellationToken);

        JsonElement request = Request(Assert.Single(runner.StartInfos));
        Assert.Equal("environment", request.GetProperty("request_kind").GetString());
        Assert.Equal("find_compatible", request.GetProperty("action").GetString());
        Assert.Equal("rapidocr-cpu", request.GetProperty("recipe").GetString());
        Assert.False(request.TryGetProperty("source_ids", out _));
        Assert.Equal("abc", result.Selected?.EnvironmentId);
        Assert.Equal(2, result.Selected?.EnvironmentRevision);
        Assert.Equal("active_environment", result.Selected?.SelectionReason);
        ManagedEnvironmentRecipe recipe = Assert.Single(result.Recipes!);
        Assert.Equal(["rapidocr==3.7.0"], recipe.Dependencies);
        Assert.Equal("win_amd64", recipe.Platform);
        Assert.Equal("cpu", recipe.TargetDevice);
        Assert.Equal("bundled_pack", recipe.DependencyOrigin);
        ManagedEnvironmentQueryMatch match = Assert.Single(result.Environments!);
        Assert.Equal("用户名称", match.Name);
        Assert.True(match.Active);
        Assert.True(match.Selected);
        Assert.Equal("selected_active_environment", match.ReasonCode);

        ManagedEnvironmentList list = await client.ListEnvironmentsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("rapidocr-cpu", Assert.Single(list.Recipes!).Id);
        // 硬件真值只由 Runtime 探测：宿主消费投影，不自行检测。
        Assert.Equal("unsupported", list.Hardware?.NvidiaDriver?.Status);
        Assert.Equal("nvidia_driver_incompatible", list.Hardware?.NvidiaDriver?.ReasonCode);
        Assert.Equal("527.00", list.Hardware?.NvidiaDriver?.DriverVersion);
        Assert.Equal("list", Request(runner.StartInfos[1]).GetProperty("action").GetString());
    }

    [Fact]
    public async Task NamedEnvironmentListTreatsMissingHardwareAsUnknown()
    {
        var client = new RuntimeInstallerClient(Configuration(), new StubRunner(
            new RuntimeInstallerProcessResult(0,
                """{"protocol_version":2,"response_kind":"environment","action":"list","result":{"active_id":null,"active_revision":0,"environments":[],"recipes":[]}}""", "")));

        ManagedEnvironmentList list = await client.ListEnvironmentsAsync(TestContext.Current.CancellationToken);

        // 旧 Runtime payload 不含 hardware：按未探测（unknown）处理，不臆造可用性。
        Assert.Null(list.Hardware);
    }

    [Fact]
    public async Task NamedEnvironmentSwitchUsesFrozenManagerBindingAndCasToken()
    {
        const string prepared = """{"environment_id":"abc","environment_revision":2,"active_id":"old","active_revision":4,"python":"C:\\venv\\python.exe","requires_supervisor":true,"launch":null}""";
        var runner = new QueueRunner(
            new RuntimeInstallerProcessResult(0,
                """{"protocol_version":2,"response_kind":"environment","action":"prepare_switch","result":""" + prepared + "}", ""),
            new RuntimeInstallerProcessResult(0,
                """{"protocol_version":2,"response_kind":"environment","action":"commit_switch","result":{"active_id":"abc","active_revision":5}}""", ""));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        PreparedEnvironmentSwitch token = await client.PrepareEnvironmentSwitchAsync("abc", TestContext.Current.CancellationToken);
        CommittedEnvironmentSwitch committed = await client.CommitEnvironmentSwitchAsync(
            token, new StartedEnvironmentHealth(5432, "sup-test"), TestContext.Current.CancellationToken);

        Assert.Equal("abc", committed.ActiveId);
        JsonElement request = Request(runner.StartInfos[1]);
        Assert.Equal("environment", request.GetProperty("request_kind").GetString());
        Assert.Equal("commit_switch", request.GetProperty("action").GetString());
        Assert.Equal(4, request.GetProperty("prepared").GetProperty("active_revision").GetInt32());
        Assert.Equal(2, request.GetProperty("prepared").GetProperty("environment_revision").GetInt32());
        Assert.Equal(5432, request.GetProperty("started_health").GetProperty("port").GetInt32());
        Assert.False(request.TryGetProperty("started_python", out _));
        Assert.False(request.TryGetProperty("accelerator", out _));
    }

    [Fact]
    public async Task NamedEnvironmentClientRejectsWrongManagerAction()
    {
        var client = new RuntimeInstallerClient(Configuration(), new StubRunner(
            new RuntimeInstallerProcessResult(0,
                """{"protocol_version":2,"response_kind":"environment","action":"delete","result":{"active_id":null,"active_revision":0,"environments":[]}}""", "")));
        await Assert.ThrowsAsync<RuntimeInstallerException>(() =>
            client.ListEnvironmentsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NamedEnvironmentClientReadsDurableInstallFailureWithoutReplacingHealth()
    {
        const string response = """{"protocol_version":2,"response_kind":"environment","action":"list","result":{"active_id":null,"active_revision":0,"environments":[{"id":"abc","name":"A","revision":1,"kind":"venv","status":"empty","python":"python.exe","python_state":"ready","dependency_state":"empty","engine_state":"unavailable","model_state":"not_applicable","service_state":"not_started","reason":null,"last_install_failure":{"phase":"failed","environment_revision":1,"recipe":"rapidocr-cpu","reason_code":"network_error","next_action":"check_source_and_retry","detail":"下载源连接失败。"}}]}}""";
        var client = new RuntimeInstallerClient(Configuration(), new StubRunner(
            new RuntimeInstallerProcessResult(0, response, "")));

        ManagedEnvironmentList list = await client.ListEnvironmentsAsync(TestContext.Current.CancellationToken);
        ManagedEnvironment environment = Assert.Single(list.Environments);
        Assert.Equal("ready", environment.PythonState);
        Assert.Null(environment.Reason);
        Assert.Equal("network_error", environment.LastInstallFailure?.ReasonCode);
        Assert.Equal("下载源连接失败。", environment.LastInstallFailure?.Detail);
        // 历史记录缺失 plan_id 时为 null，不冒认归属。
        Assert.Null(environment.LastInstallFailure?.PlanId);
    }

    [Fact]
    public async Task NamedEnvironmentClientRoundTripsInstallFailurePlanBinding()
    {
        const string response = """{"protocol_version":2,"response_kind":"environment","action":"list","result":{"active_id":null,"active_revision":0,"environments":[{"id":"abc","name":"A","revision":1,"kind":"venv","status":"empty","python":"python.exe","python_state":"ready","dependency_state":"empty","engine_state":"unavailable","model_state":"not_applicable","service_state":"not_started","reason":null,"last_install_failure":{"phase":"failed","environment_revision":1,"recipe":"rapidocr-cpu","reason_code":"install_interrupted","next_action":"preview_again","detail":"Dependency installation was interrupted.","plan_id":"0123456789abcdef0123456789abcdef","requested_source_ids":null,"effective_source_ids":["pypi"]}}]}}""";
        var client = new RuntimeInstallerClient(Configuration(), new StubRunner(
            new RuntimeInstallerProcessResult(0, response, "")));

        ManagedEnvironmentList list = await client.ListEnvironmentsAsync(TestContext.Current.CancellationToken);
        ManagedEnvironment environment = Assert.Single(list.Environments);
        Assert.Equal("0123456789abcdef0123456789abcdef",
            environment.LastInstallFailure?.PlanId);
        Assert.Equal(["pypi"], environment.LastInstallFailure?.EffectiveSourceIds);
    }

    [Fact]
    public void PublishedSdkReadsMeasuredDownloadProgressBeyondTwoGiB()
    {
        const string json = """{"unit":"bytes","current":3221225472,"total":4294967296}""";
        Host.ProgressSnapshot? progress = JsonSerializer.Deserialize<Host.ProgressSnapshot>(json);
        Assert.NotNull(progress);
        Assert.Equal(3221225472L, (long)progress.Current);
        Assert.Equal(4294967296L, (long?)progress.Total);
    }

    [Fact]
    public async Task ConfirmedPlanCannotBeOverriddenByConfiguredAccelerator()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(manifest,
                """{"capabilities":["runtime.maintenance.v2","runtime.install-plan.v1","runtime.capability-metadata.v1"]}""",
                TestContext.Current.CancellationToken);
            JsonNode envelope = JsonNode.Parse(V2LaunchEnvelope("ensure", "runtime.install-plan.v1"))!;
            JsonNode snapshot = JsonNode.Parse(Snapshot(3))!;
            snapshot["operation_state"] = "succeeded";
            snapshot["plan_id"] = "plan-cuda";
            envelope["maintenance"] = snapshot;
            var runner = new StubRunner(new RuntimeInstallerProcessResult(0, envelope.ToJsonString(), ""));
            var client = new RuntimeInstallerClient(Configuration() with { RuntimeManifest = manifest }, runner);
            await client.ConfirmInstallAsync("plan-cuda", "stable-op", cancellationToken: TestContext.Current.CancellationToken);
            JsonElement request = Request(Assert.IsType<ProcessStartInfo>(runner.LastStartInfo));
            Assert.Equal("plan-cuda", request.GetProperty("plan_id").GetString());
            Assert.Equal("stable-op", request.GetProperty("operation_id").GetString());
            Assert.False(request.TryGetProperty("accelerator", out _));
            Assert.False(request.TryGetProperty("install_component_ids", out _));
            Assert.False(request.TryGetProperty("download_source_ids", out _));
        }
        finally { TestDirectory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OldRuntimeRejectsPreviewWithoutStartingAnInstaller()
    {
        var runner = LaunchRunner();
        var client = new RuntimeInstallerClient(Configuration(), runner);
        Assert.False(client.SupportsInstallPlan);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.PreviewInstallAsync(
            new RuntimeInstallSelection { InstallComponentIds = [] }, "cpu", TestContext.Current.CancellationToken));
        Assert.Null(runner.LastStartInfo);
    }

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
        Assert.Equal("vibeocr.runtime.host.main", launch.SupervisorModule);
        Assert.Equal(@"C:\Next", launch.WorkingDirectory);
        Assert.Equal(@"C:\store", launch.Environment["VIBEOCR_RUNTIME_ROOT"]);
        Assert.Null(client.LastMaintenanceSources);
        ProcessStartInfo startInfo = Assert.IsType<ProcessStartInfo>(runner.LastStartInfo);
        Assert.Equal("ensure", Request(startInfo).GetProperty("operation").GetString());
        Assert.False(Request(startInfo).TryGetProperty("accepted_event_streams", out _));
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains("pip", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("torch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartupSelectionUsesInstallerIntentWhenOldComponentsAreDrifted()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = await WriteStartupManifestAsync(root);
            var runner = new QueueRunner(
                new RuntimeInstallerProcessResult(
                    0, StartupInspectEnvelope("""["paddleocr-cuda"]"""), ""),
                new RuntimeInstallerProcessResult(0, LaunchEnvelope(), ""));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest }, runner);

            RuntimeInstallSelection selection = await client.ReadStartupSelectionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(["paddleocr-cuda"], selection.InstallComponentIds);
            await client.EnsureAsync(
                selection, "startup-test", cancellationToken: TestContext.Current.CancellationToken);
            JsonElement request = Request(runner.StartInfos[1]);
            Assert.Equal(
                "paddleocr-cuda",
                request.GetProperty("install_component_ids")[0].GetString());
        }
        finally { TestDirectory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StartupSelectionKeepsFreshInstallBaseOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = await WriteStartupManifestAsync(root);
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                new StubRunner(new RuntimeInstallerProcessResult(
                    0, StartupInspectEnvelope("[]"), "")));

            RuntimeInstallSelection selection = await client.ReadStartupSelectionAsync(
                TestContext.Current.CancellationToken);

            Assert.NotNull(selection.InstallComponentIds);
            Assert.Empty(selection.InstallComponentIds);
        }
        finally { TestDirectory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""["retired-cuda"]""")]
    [InlineData("""["paddleocr-cuda","paddleocr-cuda"]""")]
    public async Task StartupSelectionRejectsMissingOrInvalidInstallerIntent(
        string? startupIds)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = await WriteStartupManifestAsync(root);
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                new StubRunner(new RuntimeInstallerProcessResult(
                    0, StartupInspectEnvelope(startupIds), "")));

            await Assert.ThrowsAsync<RuntimeInstallerException>(
                () => client.ReadStartupSelectionAsync(TestContext.Current.CancellationToken));
        }
        finally { TestDirectory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EnsureProjectsRawMaintenanceSourceFieldsOutsideTheGeneratedHostDto()
    {
        JsonNode envelope = JsonNode.Parse(LaunchEnvelope())!;
        envelope["maintenance"] = JsonNode.Parse(
            """
            {"operation_id":"op-1","sequence":2,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-18T00:00:00Z","requested_download_source_ids":["pypi-tuna","hf-mirror"],"effective_download_source_ids":["pypi-tuna","hf-mirror"]}
            """);
        var client = new RuntimeInstallerClient(
            Configuration(),
            new StubRunner(new RuntimeInstallerProcessResult(0, envelope.ToJsonString(), string.Empty)));

        await client.EnsureAsync(TestContext.Current.CancellationToken);

        RuntimeMaintenanceSourceSnapshot sources = Assert.IsType<RuntimeMaintenanceSourceSnapshot>(
            client.LastMaintenanceSources);
        Assert.Equal(["pypi-tuna", "hf-mirror"], sources.RequestedSourceIds);
        Assert.Equal(["pypi-tuna", "hf-mirror"], sources.EffectiveSourceIds);
    }

    [Fact]
    public async Task NewEnsureClearsSourcesWhenItsResponseOmitsTheSnapshot()
    {
        JsonNode first = JsonNode.Parse(LaunchEnvelope())!;
        first["maintenance"] = JsonNode.Parse(
            """
            {"operation_id":"op-1","sequence":2,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-18T00:00:00Z","requested_download_source_ids":["pypi-tuna"],"effective_download_source_ids":["pypi"]}
            """);
        var runner = new QueueRunner(
            new RuntimeInstallerProcessResult(0, first.ToJsonString(), string.Empty),
            new RuntimeInstallerProcessResult(0, LaunchEnvelope(), string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        await client.EnsureAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(client.LastMaintenanceSources);

        await client.EnsureAsync(TestContext.Current.CancellationToken);

        Assert.Null(client.LastMaintenanceSources);
    }

    [Fact]
    public async Task NewRetryClearsSourcesWhenItsResponseOmitsTheSnapshot()
    {
        JsonNode first = JsonNode.Parse(LaunchEnvelope())!;
        first["maintenance"] = JsonNode.Parse(
            """
            {"operation_id":"op-1","sequence":2,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-18T00:00:00Z","requested_download_source_ids":["pypi-tuna"],"effective_download_source_ids":["pypi"]}
            """);
        const string retry =
            """
            {"protocol_version":2,"ok":true,"operation":"repair","state":null,"launch":null,"error":null,"maintenance":{"operation_id":"op-2","sequence":1,"operation":"repair","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-18T00:00:01Z"}}
            """;
        var runner = new QueueRunner(
            new RuntimeInstallerProcessResult(0, first.ToJsonString(), string.Empty),
            new RuntimeInstallerProcessResult(0, retry, string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);
        await client.EnsureAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(client.LastMaintenanceSources);

        await client.RetryAsync(
            "op-1",
            "op-2",
            "retry-command",
            TestContext.Current.CancellationToken);

        Assert.Null(client.LastMaintenanceSources);
    }

    [Fact]
    public async Task ObserveWithoutSourcesKeepsTheCurrentOperationSnapshot()
    {
        JsonNode first = JsonNode.Parse(LaunchEnvelope())!;
        first["maintenance"] = JsonNode.Parse(
            """
            {"operation_id":"op-1","sequence":2,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-18T00:00:00Z","requested_download_source_ids":["pypi-tuna"],"effective_download_source_ids":["pypi"]}
            """);
        const string observed =
            """
            {"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"op-1","snapshot":{"operation_id":"op-1","sequence":3,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-18T00:00:01Z"},"events":[],"oldest_sequence":1,"through_sequence":2,"more":false,"replay_expires_at":null}
            """;
        var runner = new QueueRunner(
            new RuntimeInstallerProcessResult(0, first.ToJsonString(), string.Empty),
            new RuntimeInstallerProcessResult(0, observed, string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);
        await client.EnsureAsync(TestContext.Current.CancellationToken);

        await client.ObserveAsync(
            "op-1",
            2,
            cancellationToken: TestContext.Current.CancellationToken);

        RuntimeMaintenanceSourceSnapshot sources = Assert.IsType<RuntimeMaintenanceSourceSnapshot>(
            client.LastMaintenanceSources);
        Assert.Equal(["pypi-tuna"], sources.RequestedSourceIds);
        Assert.Equal(["pypi"], sources.EffectiveSourceIds);
    }

    [Fact]
    public async Task EnsureOptsIntoNdjsonAndReportsMaintenanceEventsWhenCapabilityIsBound()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """
                {
                  "capabilities":["runtime.maintenance.v1"],
                  "profiles":{"win-x64-cpu":{"components":[
                    {"component_id":"ocr_engine","display_name":"OCR engine","version":"3.7.0"}
                  ]}}
                }
                """,
                TestContext.Current.CancellationToken);
            string eventLine = """{"protocol_version":2,"event_version":1,"event_type":"progress","operation":"ensure","snapshot":{"operation_id":"op-1","sequence":2,"operation":"ensure","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","component_id":"ocr_engine","updated_at":"2026-08-05T00:00:00Z","progress":{"unit":"steps","current":1,"total":3}},"message_code":"runtime.installing","message_args":null,"fallback_message":"Installing"}""";
            string compactEnvelope = JsonSerializer.Serialize(
                JsonDocument.Parse(LaunchEnvelope()).RootElement);
            var runner = new StubRunner(new RuntimeInstallerProcessResult(
                0,
                eventLine + Environment.NewLine + compactEnvelope,
                string.Empty));
            var progress = new CaptureProgress();
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.EnsureAsync(progress, TestContext.Current.CancellationToken);

            JsonElement request = Request(runner.LastStartInfo!);
            Assert.Equal(
                "ndjson.v1",
                request.GetProperty("accepted_event_streams")[0].GetString());
            Host.RuntimeMaintenanceEvent update = Assert.Single(progress.Events);
            Assert.Equal("ocr_engine", update.Snapshot.ComponentId);
            Assert.Equal(1, update.Snapshot.Progress?.Current);
            Host.RuntimeProfileDescriptor profile =
                Assert.IsType<Host.RuntimeProfileDescriptor>(client.ReadProfileDescriptor());
            Assert.Equal("win-x64-cpu", profile.ProfileId);
            Assert.Equal("OCR engine", Assert.Single(profile.Components).DisplayName);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V2MaintenanceBindsOperationIdentityAndComponentRepairScope()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-v2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """
                {"capabilities":["runtime.maintenance.v2","runtime.component-repair.v1","runtime.capability-metadata.v1"]}
                """,
                TestContext.Current.CancellationToken);
            JsonNode envelope = JsonNode.Parse(V2LaunchEnvelope(
                "repair",
                "runtime.maintenance.v2",
                "runtime.component-repair.v1"))!;
            envelope["capability_descriptors"] = JsonNode.Parse(
                """
                [{"name":"runtime.maintenance.v2","lifecycle":"deprecated","introduced_in":"2.3.0","deprecated_in":"2.9.0","sunset_at":"2027-01-01T00:00:00Z","replacement":"runtime.maintenance.v3"}]
                """);
            var runner = new StubRunner(new RuntimeInstallerProcessResult(
                0,
                envelope.ToJsonString(),
                string.Empty));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.RepairComponentsAsync(
                "stable-operation",
                ["ocr_engine"],
                cancellationToken: TestContext.Current.CancellationToken);

            JsonElement request = Request(runner.LastStartInfo!);
            Assert.Equal("ndjson.v2", request.GetProperty("accepted_event_streams")[0].GetString());
            Assert.Equal("stable-operation", client.LastOperationId);
            Assert.Equal("stable-operation", request.GetProperty("operation_id").GetString());
            Assert.Equal("ocr_engine", request.GetProperty("component_ids")[0].GetString());
            Assert.False(request.TryGetProperty("install_component_ids", out _));
            Assert.False(request.TryGetProperty("download_source_ids", out _));
            string?[] requiredCapabilities = request
                .GetProperty("required_capabilities")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            Assert.Equal("runtime.maintenance.v2", requiredCapabilities[0]);
            Assert.Equal("runtime.component-repair.v1", requiredCapabilities[1]);
            Assert.Equal(
                new[] { "runtime.maintenance.v2", "runtime.component-repair.v1" },
                client.NegotiatedCapabilities);
            RuntimeCapabilityDescriptor descriptor = Assert.Single(
                client.CapabilityDescriptors);
            Assert.Equal("deprecated", descriptor.Lifecycle);
            Assert.Equal("runtime.maintenance.v3", descriptor.Replacement);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WriteSelectionManifestAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-select-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string manifest = Path.Combine(root, "runtime-manifest.json");
        await File.WriteAllTextAsync(
            manifest,
            """
            {"capabilities":["runtime.maintenance.v2","runtime.component-selection.v1","runtime.download-sources.v1"]}
            """,
            TestContext.Current.CancellationToken);
        return manifest;
    }

    [Fact]
    public async Task EnsureSelectionSendsInstallAndSourceIdsWithoutRepairField()
    {
        string manifest = await WriteSelectionManifestAsync();
        try
        {
            var runner = new StubRunner(new RuntimeInstallerProcessResult(
                0,
                V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                string.Empty));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.EnsureAsync(
                new RuntimeInstallSelection
                {
                    InstallComponentIds = ["document_parsing"],
                    DownloadSourceIds = ["tuna-pypi"],
                },
                "selection-op",
                cancellationToken: TestContext.Current.CancellationToken);

            JsonElement request = Request(runner.LastStartInfo!);
            Assert.Equal("selection-op", request.GetProperty("operation_id").GetString());
            Assert.Equal(
                "document_parsing",
                request.GetProperty("install_component_ids")[0].GetString());
            Assert.Equal(
                "tuna-pypi",
                request.GetProperty("download_source_ids")[0].GetString());
            Assert.False(request.TryGetProperty("component_ids", out _));
        }
        finally
        {
            TestDirectory.Delete(Path.GetDirectoryName(manifest)!, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureSelectionKeepsEmptyInstallListAsExplicitBaseOnlyScope()
    {
        string manifest = await WriteSelectionManifestAsync();
        try
        {
            var runner = new StubRunner(new RuntimeInstallerProcessResult(
                0,
                V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                string.Empty));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.EnsureAsync(
                new RuntimeInstallSelection { InstallComponentIds = [] },
                "base-only-op",
                cancellationToken: TestContext.Current.CancellationToken);

            JsonElement request = Request(runner.LastStartInfo!);
            Assert.True(
                request.TryGetProperty("install_component_ids", out JsonElement installIds));
            Assert.Equal(0, installIds.GetArrayLength());
            Assert.False(request.TryGetProperty("download_source_ids", out _));
            Assert.False(request.TryGetProperty("component_ids", out _));
        }
        finally
        {
            TestDirectory.Delete(Path.GetDirectoryName(manifest)!, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureSelectionFailsClosedWithoutSelectionCapabilities()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-noselect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2"]}""",
                TestContext.Current.CancellationToken);
            var runner = new StubRunner(new RuntimeInstallerProcessResult(
                0,
                V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                string.Empty));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            RuntimeInstallerException componentError =
                await Assert.ThrowsAsync<RuntimeInstallerException>(
                    () => client.EnsureAsync(
                        new RuntimeInstallSelection { InstallComponentIds = ["document_parsing"] },
                        "op-components",
                        cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("component selection", componentError.Message);

            RuntimeInstallerException sourceError =
                await Assert.ThrowsAsync<RuntimeInstallerException>(
                    () => client.EnsureAsync(
                        new RuntimeInstallSelection { DownloadSourceIds = ["tuna-pypi"] },
                        "op-sources",
                        cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("download source selection", sourceError.Message);

            Assert.Null(runner.LastStartInfo);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetrySelectionReplacesIntentExplicitlyAndReusesItWhenOmitted()
    {
        string manifest = await WriteSelectionManifestAsync();
        try
        {
            string envelope(string operationId) =>
                $$$"""
                {"protocol_version":2,"ok":true,"operation":"repair","state":null,"launch":null,"error":null,"maintenance":{"operation_id":"{{{operationId}}}","sequence":1,"operation":"ensure","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-17T00:00:00Z"}}
                """;
            var runner = new QueueRunner(
                new RuntimeInstallerProcessResult(0, envelope("op-2"), string.Empty),
                new RuntimeInstallerProcessResult(0, envelope("op-3"), string.Empty));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.RetryAsync(
                "op-1",
                "op-2",
                new RuntimeInstallSelection
                {
                    InstallComponentIds = ["document_parsing"],
                    DownloadSourceIds = ["pypi"],
                },
                "retry-command-1",
                TestContext.Current.CancellationToken);
            await client.RetryAsync(
                "op-2",
                "op-3",
                selection: null,
                "retry-command-2",
                TestContext.Current.CancellationToken);

            JsonElement explicitRequest = Request(runner.StartInfos[0]);
            Assert.Equal("retry", explicitRequest.GetProperty("command").GetString());
            Assert.Equal(
                "document_parsing",
                explicitRequest.GetProperty("install_component_ids")[0].GetString());
            Assert.Equal(
                "pypi",
                explicitRequest.GetProperty("download_source_ids")[0].GetString());
            JsonElement reuseRequest = Request(runner.StartInfos[1]);
            Assert.False(reuseRequest.TryGetProperty("install_component_ids", out _));
            Assert.False(reuseRequest.TryGetProperty("download_source_ids", out _));
        }
        finally
        {
            TestDirectory.Delete(Path.GetDirectoryName(manifest)!, recursive: true);
        }
    }

    [Fact]
    public void InstallSelectionRejectsEmptySourcesAndDuplicateOrBlankIds()
    {
        Assert.Throws<ArgumentException>(
            () => new RuntimeInstallSelection { DownloadSourceIds = [] });
        Assert.Throws<ArgumentException>(
            () => new RuntimeInstallSelection { InstallComponentIds = ["a", "a"] });
        Assert.Throws<ArgumentException>(
            () => new RuntimeInstallSelection { DownloadSourceIds = [" ", "tuna-pypi"] });

        RuntimeInstallSelection baseOnly = new() { InstallComponentIds = [] };
        Assert.Empty(baseOnly.InstallComponentIds!);
        Assert.Null(baseOnly.DownloadSourceIds);
    }

    [Fact]
    public async Task V2MaintenanceSuppressesAtLeastOnceDuplicateSequences()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-v2-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2"]}""",
                TestContext.Current.CancellationToken);
            string eventLine = """{"schema_version":2,"protocol_version":2,"event_version":1,"event_type":"progress","sequence":1,"operation":"ensure","snapshot":{"operation_id":"op-1","sequence":1,"operation":"ensure","operation_state":"running","phase":"prepare_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:00Z"},"message_code":"runtime.preparing"}""";
            string compactEnvelope = V2LaunchEnvelope(
                "ensure", "runtime.maintenance.v2");
            string output = eventLine + Environment.NewLine + eventLine +
                Environment.NewLine + compactEnvelope;
            var progress = new CaptureProgress();
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                new StubRunner(new RuntimeInstallerProcessResult(0, output, string.Empty)));

            await client.EnsureAsync(progress, TestContext.Current.CancellationToken);

            Assert.Single(progress.Events);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V2MaintenanceReplaysSequenceGapBeforeReturning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-v2-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2"]}""",
                TestContext.Current.CancellationToken);
            string liveEvent = MaintenanceEvent(3);
            string firstPage = $$"""
                {"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{{Snapshot(3)}},"events":[{{MaintenanceEvent(1)}}],"oldest_sequence":1,"through_sequence":1,"more":true,"replay_expires_at":null}
                """;
            string secondPage = $$"""
                {"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{{Snapshot(3)}},"events":[{{MaintenanceEvent(2)}},{{MaintenanceEvent(3)}}],"oldest_sequence":1,"through_sequence":3,"more":false,"replay_expires_at":null}
                """;
            var runner = new QueueRunner(
                new RuntimeInstallerProcessResult(
                    0,
                    liveEvent + Environment.NewLine +
                        V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                    string.Empty),
                new RuntimeInstallerProcessResult(0, firstPage, string.Empty),
                new RuntimeInstallerProcessResult(0, secondPage, string.Empty));
            var progress = new CaptureProgress();
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.EnsureAsync(
                "stable-op",
                progress,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                new[] { 1, 2, 3 },
                progress.Events.Select(item => item.Snapshot.Sequence));
            Assert.Equal(3, runner.StartInfos.Count);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V2OmitsUngatedCapabilityMetadataAndRejectsNegotiationFields()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-v2-gates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2","runtime.component-repair.v1"]}""",
                TestContext.Current.CancellationToken);
            var runner = new StubRunner(new RuntimeInstallerProcessResult(
                0,
                V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                string.Empty));
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);

            await client.EnsureAsync(
                "stable-op",
                cancellationToken: TestContext.Current.CancellationToken);

            JsonElement request = Request(runner.LastStartInfo!);
            Assert.False(request.TryGetProperty("required_capabilities", out _));
            RuntimeInstallerException error = await Assert.ThrowsAsync<RuntimeInstallerException>(
                () => client.RepairComponentsAsync(
                    "repair-op",
                    ["ocr_engine"],
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("negotiation metadata", error.Message);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelRetryAndObserveUseDurableControlEnvelopes()
    {
        var runner = new QueueRunner(
            new RuntimeInstallerProcessResult(
                0,
                """{"protocol_version":2,"ok":true,"operation":"repair","state":null,"launch":null,"error":null,"maintenance":{"operation_id":"op-1","sequence":2,"operation":"repair","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:00Z"}}""",
                string.Empty),
            new RuntimeInstallerProcessResult(
                0,
                """{"protocol_version":2,"ok":true,"operation":"repair","state":null,"launch":null,"error":null,"maintenance":{"operation_id":"op-2","sequence":1,"operation":"repair","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:00Z"}}""",
                string.Empty),
            new RuntimeInstallerProcessResult(
                0,
                """{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"op-2","snapshot":{"operation_id":"op-2","sequence":1,"operation":"repair","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:00Z"},"events":[],"oldest_sequence":1,"through_sequence":1,"more":false,"replay_expires_at":null}""",
                string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        await client.CancelAsync(
            "op-1",
            "cancel-command-1",
            4,
            TestContext.Current.CancellationToken);
        await client.RetryAsync(
            "op-1",
            "op-2",
            "retry-command-1",
            TestContext.Current.CancellationToken);
        RuntimeMaintenanceObserveEnvelope observed = await client.ObserveAsync(
            "op-2",
            1,
            cancellationToken: TestContext.Current.CancellationToken);

        JsonElement cancel = Request(runner.StartInfos[0]);
        JsonElement retry = Request(runner.StartInfos[1]);
        JsonElement observe = Request(runner.StartInfos[2]);
        Assert.Equal("cancel", cancel.GetProperty("command").GetString());
        Assert.Equal("cancel-command-1", cancel.GetProperty("command_id").GetString());
        Assert.Equal(4, cancel.GetProperty("expected_sequence").GetInt64());
        Assert.Equal("retry", retry.GetProperty("command").GetString());
        Assert.Equal("retry-command-1", retry.GetProperty("command_id").GetString());
        Assert.Equal("op-2", retry.GetProperty("new_operation_id").GetString());
        Assert.Equal("observe", observe.GetProperty("request_kind").GetString());
        Assert.Equal("op-2", observed.OperationId);
    }

    [Fact]
    public async Task ObserveRejectsMissingSnapshotAsTypedProtocolError()
    {
        var runner = new StubRunner(new RuntimeInstallerProcessResult(
            0,
            """{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"op-1","events":[],"oldest_sequence":1,"through_sequence":0,"more":false,"replay_expires_at":null}""",
            string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeInstallerException error = await Assert.ThrowsAsync<RuntimeInstallerException>(
            () => client.ObserveAsync(
                "op-1",
                0,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("valid snapshot", error.Message);
    }

    [Fact]
    public async Task ObserveAcceptsEmptyPageWhenCursorIsAheadOfJournal()
    {
        var runner = new StubRunner(new RuntimeInstallerProcessResult(
            0,
            """{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"op-1","snapshot":{"operation_id":"op-1","sequence":5,"operation":"repair","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:00Z"},"events":[],"oldest_sequence":1,"through_sequence":5,"more":false,"replay_expires_at":null}""",
            string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeMaintenanceObserveEnvelope page = await client.ObserveAsync(
            "op-1",
            10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
        Assert.Equal(5, page.ThroughSequence);
    }

    [Fact]
    public async Task ObserveRejectsSnapshotBehindThroughSequence()
    {
        var runner = new StubRunner(new RuntimeInstallerProcessResult(
            0,
            $$"""{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{{Snapshot(1)}},"events":[{{MaintenanceEvent(1)}},{{MaintenanceEvent(2)}}],"oldest_sequence":1,"through_sequence":2,"more":false,"replay_expires_at":null}""",
            string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeInstallerException error = await Assert.ThrowsAsync<RuntimeInstallerException>(
            () => client.ObserveAsync(
                "stable-op",
                0,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("cursor is invalid", error.Message);
    }

    [Fact]
    public async Task V2CancellationRequiresTerminalCancelledSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-v2-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2"]}""",
                TestContext.Current.CancellationToken);
            var runner = new CancellationRunner();
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);
            using var cancellation = new CancellationTokenSource();
            Task<RuntimeLaunch> operation = client.EnsureAsync(
                "stable-op",
                cancellationToken: cancellation.Token);
            await runner.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            JsonElement command = Request(runner.StartInfos[1]);
            Assert.Equal("cancel", command.GetProperty("command").GetString());
            Assert.Equal("cancel-stable-op", command.GetProperty("command_id").GetString());
            Assert.False(command.TryGetProperty("expected_sequence", out _));
            Assert.Equal(4, runner.StartInfos.Count);
            Assert.False(runner.OwnedToken.IsCancellationRequested);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V2CancellationRaceReturnsSuccessWhenTerminalSnapshotSucceeded()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-v2-cancel-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2"]}""",
                TestContext.Current.CancellationToken);
            var runner = new SuccessRaceCancellationRunner();
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);
            using var cancellation = new CancellationTokenSource();
            Task<RuntimeLaunch> operation = client.EnsureAsync(
                "stable-op",
                cancellationToken: cancellation.Token);
            await runner.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

            cancellation.Cancel();

            RuntimeLaunch launch = await operation;
            Assert.Equal("C:\\store\\python.exe", launch.PythonExecutable);
            Assert.Equal(3, runner.StartInfos.Count);
            Assert.False(runner.OwnedToken.IsCancellationRequested);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V2CancellationRejectedAfterCommitWaitsForSucceededSnapshot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"vibeocr-v2-cancel-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root, "runtime-manifest.json");
            await File.WriteAllTextAsync(
                manifest,
                """{"capabilities":["runtime.maintenance.v2"]}""",
                TestContext.Current.CancellationToken);
            var runner = new CommitRaceCancellationRunner();
            var client = new RuntimeInstallerClient(
                Configuration() with { RuntimeManifest = manifest },
                runner);
            using var cancellation = new CancellationTokenSource();
            Task<RuntimeLaunch> operation = client.EnsureAsync(
                "stable-op",
                cancellationToken: cancellation.Token);
            await runner.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

            cancellation.Cancel();

            RuntimeLaunch launch = await operation;
            Assert.Equal(@"C:\store\python.exe", launch.PythonExecutable);
            Assert.Equal(3, runner.StartInfos.Count);
            Assert.False(runner.OwnedToken.IsCancellationRequested);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
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
                """{"protocol_version":2,"ok":false,"operation":"ensure","error":{"code":"busy","canonical_code":"RUNTIME_BUSY","category":"transient","message":"hash mismatch","retryable":true,"retry_after":2,"detail":{"owner":"other"}}}""",
                string.Empty));
        var client = new RuntimeInstallerClient(Configuration(), runner);

        RuntimeInstallerException error = await Assert.ThrowsAsync<RuntimeInstallerException>(
            () => client.EnsureAsync(TestContext.Current.CancellationToken));

        Assert.Contains("hash mismatch", error.Message);
        Assert.Equal("RUNTIME_BUSY", error.CanonicalCode);
        Assert.Equal("transient", error.Category);
        Assert.True(error.Retryable);
        Assert.Equal(2, error.RetryAfter);
    }

    [Fact]
    public async Task CommandRunnerHonorsCancellationBeforeReadingInstallerBindings()
    {
        var runner = new RuntimeInstallerCommandRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(new ProcessStartInfo(), cancellation.Token));
    }

    [Theory]
    [InlineData("accepted", 0)]
    [InlineData("rejected", 7)]
    [InlineData("completed", 0)]
    [InlineData("missing", 0)]
    public async Task EnvironmentCancellationWaitsForReceiptAndPreservesTerminalResult(
        string outcome, int exitCode)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        Process? installerProcess = null;
        try
        {
            string script = Path.Combine(root, "installer.ps1");
            await File.WriteAllTextAsync(script, $$"""
                param([Parameter(ValueFromRemainingArguments=$true)][string[]] $remaining)
                [Console]::WriteLine("pid:$PID")
                [Console]::WriteLine('null')
                [Console]::WriteLine('ready')
                $command = [Console]::ReadLine()
                if ($command -ne 'cancel') { exit 90 }
                if ('{{outcome}}' -in @('accepted', 'rejected')) {
                    [Console]::WriteLine('{"environment_cancel":"{{outcome}}"}')
                }
                if ('{{outcome}}' -in @('accepted', 'missing')) { Start-Sleep -Seconds 60 }
                [Console]::WriteLine('terminal-envelope')
                exit {{exitCode}}
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
                "--environment-cancel-control" }) startInfo.ArgumentList.Add(argument);
            Task<RuntimeInstallerProcessResult> running = RuntimeInstallerCommandRunner.RunProcessAsync(
                startInfo, line =>
                {
                    if (line.StartsWith("pid:", StringComparison.Ordinal))
                        installerProcess = Process.GetProcessById(int.Parse(line[4..],
                            System.Globalization.CultureInfo.InvariantCulture));
                    if (line == "ready") cancellation.Cancel();
                }, cancellation.Token);
            if (outcome == "accepted")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            }
            else if (outcome == "missing")
            {
                RuntimeInstallerException error = await Assert.ThrowsAsync<RuntimeInstallerException>(
                    () => running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
                Assert.Contains("取消未获确认", error.Message);
            }
            else
            {
                RuntimeInstallerProcessResult result = await running.WaitAsync(
                    TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(exitCode, result.ExitCode);
                Assert.Contains("terminal-envelope", result.StandardOutput);
                Assert.DoesNotContain("environment_cancel", result.StandardOutput);
            }
            Assert.NotNull(installerProcess);
            Assert.True(installerProcess.HasExited);
        }
        finally
        {
            installerProcess?.Dispose();
            TestDirectory.Delete(root, recursive: true);
        }
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
            TestDirectory.Delete(root, recursive: true);
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
            TestDirectory.Delete(root, recursive: true);
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
            TestDirectory.Delete(root, recursive: true);
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

    [Theory]
    [InlineData("initialize_default", true)]
    [InlineData("list", false)]
    public async Task DefaultInitializationPreservesManagerBindingAndRejectsWrongAction(string action, bool succeeds)
    {
        var runner = new StubRunner(new RuntimeInstallerProcessResult(0,
            $"{{\"protocol_version\":2,\"response_kind\":\"environment\",\"action\":\"{action}\",\"result\":{{\"active_id\":null,\"active_revision\":0,\"environments\":[]}}}}", ""));
        var client = new RuntimeInstallerClient(Configuration(), runner);
        if (succeeds)
            Assert.Empty((await client.InitializeDefaultEnvironmentAsync(TestContext.Current.CancellationToken)).Environments);
        else
            await Assert.ThrowsAsync<RuntimeInstallerException>(() => client.InitializeDefaultEnvironmentAsync(TestContext.Current.CancellationToken));
        JsonElement request = Request(runner.LastStartInfo!);
        Assert.Equal("initialize_default", request.GetProperty("action").GetString());
        Assert.Equal("environment", request.GetProperty("request_kind").GetString());
        Assert.Equal(Configuration().ProductRoot, request.GetProperty("product_root").GetString());
        Assert.Equal(Configuration().ComponentLock, request.GetProperty("component_lock").GetString());
        Assert.Equal(Configuration().RuntimeManifest, request.GetProperty("runtime_manifest").GetString());
        Assert.False(request.TryGetProperty("environment_id", out _));
    }

    [Fact]
    public async Task IndependentSourcePreferencesRoundTripWithoutSharedField()
    {
        var runner = new StubRunner(new RuntimeInstallerProcessResult(0,
            """{"protocol_version":2,"response_kind":"environment","action":"set_sources","result":{"active_id":null,"active_revision":0,"environments":[],"resolved_default_sources":[{"kind":"paddleocr_model_registry","id":"paddleocr-bos","display_name":"百度 BOS","origin":"global_default"}]}}""", ""));
        var client = new RuntimeInstallerClient(Configuration(), runner);
        ManagedEnvironmentList result = await client.SetEnvironmentSourcesAsync(
            "abc", "pypi", "paddleocr-bos", "mineru-modelscope", TestContext.Current.CancellationToken);
        JsonElement request = Request(runner.LastStartInfo!);
        Assert.Equal("abc", request.GetProperty("environment_id").GetString());
        Assert.Equal("paddleocr-bos", request.GetProperty("paddleocr_model_source_id").GetString());
        Assert.Equal("mineru-modelscope", request.GetProperty("mineru_model_source_id").GetString());
        Assert.False(request.TryGetProperty("model_source_id", out _));
        Assert.Equal("paddleocr-bos", Assert.Single(result.ResolvedDefaultSources!).Id);
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

    private static async Task<string> WriteStartupManifestAsync(string root)
    {
        string path = Path.Combine(root, "runtime-manifest.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "capabilities":["runtime.maintenance.v2","runtime.component-selection.v1"],
              "profiles":{
                "win-x64-base":{"components":[
                  {"component_id":"rapidocr-base"},
                  {"component_id":"runtime_host"}
                ]},
                "win-x64-cu126":{"components":[
                  {"component_id":"rapidocr-base"},
                  {"component_id":"paddleocr-cuda"},
                  {"component_id":"runtime_host"}
                ]}
              }
            }
            """,
            TestContext.Current.CancellationToken);
        return path;
    }

    private static string StartupInspectEnvelope(string? startupIds)
    {
        JsonNode envelope = JsonNode.Parse(InspectEnvelope())!;
        envelope["state"]!["status"] = "missing";
        envelope["state"]!["integrity"] = "not-installed";
        envelope["state"]!["accelerator"] = "nvidia_cuda";
        if (startupIds is not null)
        {
            envelope["state"]!["startup_install_component_ids"] =
                JsonNode.Parse(startupIds);
        }
        envelope["profile"] = JsonNode.Parse(
            """
            {
              "profile_id":"win-x64-cu126",
              "accelerator":"nvidia_cuda",
              "components":[
                {"component_id":"rapidocr-base","display_name":"RapidOCR","actual_state":"drifted","drift_reason":"identity_mismatch"},
                {"component_id":"paddleocr-cuda","display_name":"PaddleOCR","actual_state":"drifted","drift_reason":"identity_mismatch"},
                {"component_id":"runtime_host","display_name":"Runtime host","actual_state":"drifted","drift_reason":"identity_mismatch"}
              ]
            }
            """);
        return envelope.ToJsonString();
    }

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
            "supervisor_module": "vibeocr.runtime.host.main",
            "working_directory": "C:\\Next",
            "model_root": "C:\\store\\models",
            "environment": {
              "VIBEOCR_RUNTIME_ROOT": "C:\\store",
              "VIBEOCR_MODEL_ROOT": "C:\\store\\models"
            }
          }
        }
        """;

    private static string V2LaunchEnvelope(
        string operation,
        params string[] negotiatedCapabilities)
    {
        JsonNode root = JsonNode.Parse(LaunchEnvelope(operation))!;
        root["negotiated_capabilities"] = JsonSerializer.SerializeToNode(
            negotiatedCapabilities);
        return root.ToJsonString();
    }

    private static string Snapshot(int sequence) =>
        $$"""{"operation_id":"stable-op","sequence":{{sequence}},"operation":"ensure","operation_state":"running","phase":"prepare_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:0{{sequence}}Z"}""";

    private static string MaintenanceEvent(int sequence) =>
        $$"""{"schema_version":2,"protocol_version":2,"event_version":1,"event_type":"progress","sequence":{{sequence}},"operation":"ensure","snapshot":{{Snapshot(sequence)}},"message_code":"runtime.preparing"}""";

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
            "{\"product\":{\"runtime_manifest_sha256\":\"" +
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

    private sealed class QueueRunner(params RuntimeInstallerProcessResult[] results)
        : IRuntimeInstallerCommandRunner
    {
        private readonly Queue<RuntimeInstallerProcessResult> _results = new(results);
        public List<ProcessStartInfo> StartInfos { get; } = [];

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CaptureProgress : IProgress<Host.RuntimeMaintenanceEvent>
    {
        public List<Host.RuntimeMaintenanceEvent> Events { get; } = [];
        public void Report(Host.RuntimeMaintenanceEvent value) => Events.Add(value);
    }

    private sealed class CancellationRunner : IRuntimeInstallerCommandRunner
    {
        private readonly TaskCompletionSource<RuntimeInstallerProcessResult> _operation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ProcessStartInfo> StartInfos { get; } = [];
        public CancellationToken OwnedToken { get; private set; }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            if (StartInfos.Count == 2)
            {
                _operation.TrySetResult(new RuntimeInstallerProcessResult(
                    1,
                    """{"protocol_version":2,"ok":false,"operation":"ensure","error":{"code":"install_failed","message":"cancelled","retryable":false}}""",
                    string.Empty));
                return Task.FromResult(new RuntimeInstallerProcessResult(
                    0,
                    """{"protocol_version":2,"ok":true,"operation":"ensure","maintenance":{"operation_id":"stable-op","sequence":2,"operation":"ensure","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:01Z"}}""",
                    string.Empty));
            }
            if (StartInfos.Count == 3)
            {
                return Task.FromResult(new RuntimeInstallerProcessResult(
                    0,
                    $$"""{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{"operation_id":"stable-op","sequence":3,"operation":"ensure","operation_state":"cancelled","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:02Z"},"events":[{{MaintenanceEvent(2)}}],"oldest_sequence":1,"through_sequence":2,"more":true,"replay_expires_at":null}""",
                    string.Empty));
            }
            return Task.FromResult(new RuntimeInstallerProcessResult(
                0,
                $$"""{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{"operation_id":"stable-op","sequence":3,"operation":"ensure","operation_state":"cancelled","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:02Z"},"events":[{{MaintenanceEvent(3)}}],"oldest_sequence":1,"through_sequence":3,"more":false,"replay_expires_at":null}""",
                string.Empty));
        }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            Action<string>? standardOutputLine,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            OwnedToken = cancellationToken;
            standardOutputLine?.Invoke(MaintenanceEvent(1));
            Started.TrySetResult();
            return _operation.Task;
        }
    }

    private sealed class SuccessRaceCancellationRunner : IRuntimeInstallerCommandRunner
    {
        private readonly TaskCompletionSource<RuntimeInstallerProcessResult> _operation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ProcessStartInfo> StartInfos { get; } = [];
        public CancellationToken OwnedToken { get; private set; }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            if (StartInfos.Count == 2)
            {
                _operation.TrySetResult(new RuntimeInstallerProcessResult(
                    0,
                    V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                    string.Empty));
                return Task.FromResult(new RuntimeInstallerProcessResult(
                    0,
                    """{"protocol_version":2,"ok":true,"operation":"ensure","maintenance":{"operation_id":"stable-op","sequence":2,"operation":"ensure","operation_state":"running","phase":"install_profile","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:01Z"}}""",
                    string.Empty));
            }
            return Task.FromResult(new RuntimeInstallerProcessResult(
                0,
                $$"""{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{"operation_id":"stable-op","sequence":3,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:02Z"},"events":[{{MaintenanceEvent(1)}},{{MaintenanceEvent(2)}},{{MaintenanceEvent(3)}}],"oldest_sequence":1,"through_sequence":3,"more":false,"replay_expires_at":null}""",
                string.Empty));
        }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            Action<string>? standardOutputLine,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            OwnedToken = cancellationToken;
            Started.TrySetResult();
            return _operation.Task;
        }
    }

    private sealed class CommitRaceCancellationRunner : IRuntimeInstallerCommandRunner
    {
        private readonly TaskCompletionSource<RuntimeInstallerProcessResult> _operation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ProcessStartInfo> StartInfos { get; } = [];
        public CancellationToken OwnedToken { get; private set; }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            if (StartInfos.Count == 2)
            {
                _operation.TrySetResult(new RuntimeInstallerProcessResult(
                    0,
                    V2LaunchEnvelope("ensure", "runtime.maintenance.v2"),
                    string.Empty));
                return Task.FromResult(new RuntimeInstallerProcessResult(
                    1,
                    """{"protocol_version":2,"ok":false,"error":{"code":"operation_not_cancellable","canonical_code":"RUNTIME_OPERATION_NOT_CANCELLABLE","category":"conflict","message":"operation is committing","retryable":false}}""",
                    string.Empty));
            }
            return Task.FromResult(new RuntimeInstallerProcessResult(
                0,
                $$"""{"protocol_version":2,"ok":true,"request_kind":"observe","operation_id":"stable-op","snapshot":{"operation_id":"stable-op","sequence":3,"operation":"ensure","operation_state":"succeeded","phase":"commit_runtime","profile_id":"win-x64-cpu","updated_at":"2026-08-05T00:00:02Z"},"events":[{{MaintenanceEvent(1)}},{{MaintenanceEvent(2)}},{{MaintenanceEvent(3)}}],"oldest_sequence":1,"through_sequence":3,"more":false,"replay_expires_at":null}""",
                string.Empty));
        }

        public Task<RuntimeInstallerProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            Action<string>? standardOutputLine,
            CancellationToken cancellationToken)
        {
            StartInfos.Add(startInfo);
            OwnedToken = cancellationToken;
            Started.TrySetResult();
            return _operation.Task;
        }
    }
}
