using System.Net;
using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class InferenceHttpClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:1");

    [Fact]
    public async Task SubmitPostsManifestAndAttachmentsToGenericJobsRouteAsync()
    {
        var handler = new FakeHandler("""
            {"job_id":"job-1","schema_version":2,"instance_id":"sup-1","state":"accepted","items":[]}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        SubmitRequest request = UploadRequest();

        JobRef referral = await client.SubmitAsync(
            request,
            new Dictionary<string, SubmitUpload>
            {
                ["file-a"] = new("image/png", new byte[] { 1, 2, 3 }),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("job-1", referral.JobId);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/v2/jobs", handler.LastPath);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("tok", handler.LastAuthorizationParameter);
        Assert.StartsWith("multipart/form-data", handler.LastContentType);
        Assert.Contains("name=manifest", handler.LastBody);
        Assert.Contains("\"request_id\":\"request-1\"", handler.LastBody);
        Assert.Contains("name=file-a", handler.LastBody);
        Assert.Contains("filename=a.png", handler.LastBody);
    }

    [Fact]
    public async Task SubmitRequiresUploadsToExactlyMatchManifestAsync()
    {
        var handler = new FakeHandler("{}");
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitAsync(
                UploadRequest(),
                new Dictionary<string, SubmitUpload>(),
                TestContext.Current.CancellationToken));

        Assert.Null(handler.LastPath);
    }

    [Fact]
    public async Task ObserveReturnsAtomicJobUpdateAsync()
    {
        var handler = new FakeHandler("""
            {
              "snapshot":{"job_id":"job-1","kind":"recognition","priority":"interactive","state":"running"},
              "events":[{"sequence":4,"stage":"recognize","item_id":"item-1","timestamp":null,"detail":{}}],
              "outcomes":[],
              "through_sequence":4,
              "more":false,
              "schema_version":2
            }
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        JobUpdate update = await client.ObserveAsync(
            "job-1",
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Running, update.Snapshot.State);
        Assert.Equal(4, update.ThroughSequence);
        Assert.Single(update.Events);
        Assert.Equal("/v2/jobs/job-1/observe", handler.LastPath);
        Assert.Equal("?after_sequence=3", handler.LastQuery);
    }

    [Fact]
    public async Task CommandPostsTypedRequestAndParsesCancelResultAsync()
    {
        var handler = new FakeHandler("""
            {
              "schema_version":2,
              "instance_id":"sup-1",
              "command_id":"command-1",
              "kind":"cancel",
              "cancel_mode":"cooperative",
              "job_ref":null
            }
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        var command = new JobCommand
        {
            CommandId = "command-1",
            Kind = JobCommandKind.Cancel,
            JobId = "job-1",
        };

        JobCommandResult result = await client.CommandAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.Equal(JobCommandKind.Cancel, result.Kind);
        Assert.Equal(CancelMode.Cooperative, result.CancelMode);
        Assert.Null(result.JobRef);
        Assert.Equal("/v2/jobs/command", handler.LastPath);
        Assert.Equal("application/json", handler.LastContentType);
        Assert.Contains("\"command_id\":\"command-1\"", handler.LastBody);
        Assert.Contains("\"kind\":\"cancel\"", handler.LastBody);
    }

    [Fact]
    public async Task CommandParsesRetryJobRefAsync()
    {
        var handler = new FakeHandler("""
            {
              "schema_version":2,
              "instance_id":"sup-1",
              "command_id":"command-2",
              "kind":"retry",
              "cancel_mode":null,
              "job_ref":{"job_id":"job-2","schema_version":2,"instance_id":"sup-1","state":"accepted","items":[]}
            }
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        JobCommandResult result = await client.CommandAsync(
            new JobCommand
            {
                CommandId = "command-2",
                Kind = JobCommandKind.Retry,
                JobId = "job-1",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("job-2", result.JobRef?.JobId);
        Assert.Null(result.CancelMode);
    }

    [Fact]
    public async Task RuntimeStatusUsesAuthenticatedHttpEndpointAsync()
    {
        var handler = new FakeHandler("""
            {
              "schema_version":2,
              "instance_id":"sup-1",
              "service_state":"ready",
              "backend_version":"0.9.0",
              "profile":{"profile_id":"win-x64-cpu","accelerator":"cpu","components":[
                {"component_id":"ocr_engine","display_name":"OCR engine","state":"ready","version":"3.7.0"}
              ]},
              "maintenance":null
            }
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        RuntimeStatusSnapshot status = await client.GetRuntimeStatusAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("0.9.0", status.BackendVersion);
        Assert.Equal(RuntimeComponentState.Ready, Assert.Single(status.Profile.Components).State);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/v2/runtime/status", handler.LastPath);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("tok", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task GetSettingsReturnsTypedSnapshotWithoutSourceSelectionAsync()
    {
        var handler = new FakeHandler("""
            {"schema_version":2,"residency":{"default_ttl_seconds":300,"pipelines":[]},"extra":{}}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        SettingsSnapshot snapshot = await client.GetSettingsAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(300, snapshot.Residency.DefaultTtlSeconds);
        Assert.Null(snapshot.DownloadSourceIds);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/v2/settings", handler.LastPath);
    }

    [Fact]
    public async Task UpdateSettingsPutsSnapshotAndReturnsUpdatedStateAsync()
    {
        var handler = new FakeHandler("""
            {"schema_version":2,"residency":{"default_ttl_seconds":600,"pipelines":[]},"extra":{},"download_source_ids":["tuna-pypi"]}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        SettingsSnapshot updated = await client.UpdateSettingsAsync(
            new SettingsSnapshot
            {
                Residency = new SettingsResidency { DefaultTtlSeconds = 600 },
                DownloadSourceIds = ["tuna-pypi"],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("/v2/settings", handler.LastPath);
        Assert.Equal("application/json", handler.LastContentType);
        Assert.Contains("\"download_source_ids\":[\"tuna-pypi\"]", handler.LastBody);
        Assert.Equal(["tuna-pypi"], updated.DownloadSourceIds);
    }

    [Fact]
    public async Task UpdateSettingsOmitsEmptySourceListOnTheWireAsync()
    {
        var handler = new FakeHandler("""
            {"schema_version":2,"residency":{"default_ttl_seconds":300,"pipelines":[]},"extra":{}}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        await client.UpdateSettingsAsync(
            new SettingsSnapshot { DownloadSourceIds = [] },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("download_source_ids", handler.LastBody!);
    }

    [Fact]
    public async Task GetHealthParsesCapabilityCatalogsAsync()
    {
        var handler = new FakeHandler("""
            {
              "schema_version": 2,
              "instance_id": "sup-1",
              "protocol_version": 2,
              "ready": true,
              "draining": false,
              "capabilities": ["ocr.recognition.v2", "ocr.engine-selection.v1", "runtime.download-sources.v1"],
              "capability_descriptors": [
                {"name":"ocr.engine-selection.v1","lifecycle":"active","introduced_in":"2.6.0","deprecated_in":null,"sunset_at":null,"replacement":null,
                 "ocr_engine_catalog":{"engines":[
                   {"id":"rapidocr","availability":"ready","included_in_base":true,"reason_code":null,"required_component":null},
                   {"id":"paddleocr","availability":"preparation_required","included_in_base":false,"reason_code":null,"required_component":"paddle-engine"}
                 ]}},
                {"name":"runtime.download-sources.v1","lifecycle":"active","introduced_in":"2.7.0","deprecated_in":null,"sunset_at":null,"replacement":null,
                 "download_source_catalog":{"sources":[
                   {"kind":"package_index","id":"tuna-pypi","endpoint":"https://mirrors.tuna.example/pypi/simple"},
                   {"kind":"internal-mirror","id":"mirror-1","endpoint":"https://example.invalid/simple"}
                 ]}}
              ]
            }
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        Wire.Health health = await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/v2/health", handler.LastPath);
        Assert.True(health.Ready);
        RuntimeSelectionService selection = new(health);
        Assert.Equal(2, selection.EngineOptions.Count);
        Assert.Equal(OcrEngine.RapidOcr, selection.EngineOptions[0].Engine);
        Assert.Contains(selection.Sources, source =>
            source.Kind == "internal-mirror" && source.Id == "mirror-1");
    }

    [Fact]
    public async Task ApplySourcePreferenceRoundTripsValidatedIdsIntoBackendSettingsAsync()
    {
        var handler = new FakeHandler(
        [
            """
            {"schema_version":2,"residency":{"default_ttl_seconds":600,"pipelines":[]},"extra":{"theme":"dark"},"download_source_ids":["pypi"]}
            """,
            """
            {"schema_version":2,"residency":{"default_ttl_seconds":600,"pipelines":[]},"extra":{"theme":"dark"},"download_source_ids":["tuna-pypi","huggingface"]}
            """,
            """
            {"schema_version":2,"residency":{"default_ttl_seconds":600,"pipelines":[]},"extra":{"theme":"dark"}}
            """,
        ]);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        RuntimeSelectionService selection = new(HealthWithSources(
            ("package_index", "tuna-pypi", "https://a.invalid"),
            ("model_registry", "huggingface", "https://b.invalid")));

        SettingsSnapshot updated = await selection.ApplySourcePreferenceAsync(
            client,
            ["tuna-pypi", "huggingface"],
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("/v2/settings", handler.LastPath);
        Assert.Contains("\"download_source_ids\":[\"tuna-pypi\",\"huggingface\"]", handler.LastBody);
        // 复用当前 snapshot,residency/extra 不丢失;endpoint 永不写入。
        Assert.Contains("\"default_ttl_seconds\":600", handler.LastBody);
        Assert.Contains("\"theme\":\"dark\"", handler.LastBody);
        Assert.DoesNotContain("https://a.invalid", handler.LastBody!);
        Assert.Equal(["tuna-pypi", "huggingface"], updated.DownloadSourceIds);

        await selection.ApplySourcePreferenceAsync(
            client,
            sourceIds: null,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("download_source_ids", handler.LastBody!);
    }

    [Fact]
    public async Task ModelRegistrySourcesSelectPerKindAndPersistAsync()
    {
        // 补验 §6 矩阵:Hugging Face/ModelScope 经同一 Backend Settings 通道
        // 单选持久化,package_index 与 model_registry 各自独立。
        string updatedBody = """
            {"schema_version":2,"residency":{"default_ttl_seconds":300,"pipelines":[]},"extra":{}}
            """;
        var handler = new FakeHandler(updatedBody);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        RuntimeSelectionService selection = new(HealthWithSources(
            ("package_index", "tuna-pypi", "https://a.invalid"),
            ("model_registry", "huggingface", "https://huggingface.co"),
            ("model_registry", "modelscope", "https://www.modelscope.cn")));

        await selection.ApplySourcePreferenceAsync(
            client,
            ["tuna-pypi", "modelscope"],
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Contains(
            "\"download_source_ids\":[\"tuna-pypi\",\"modelscope\"]",
            handler.LastBody);
    }

    [Fact]
    public async Task ApplySourcePreferenceFailsClosedForUnknownSourceAsync()
    {
        var handler = new FakeHandler("""
            {"schema_version":2,"residency":{"default_ttl_seconds":300,"pipelines":[]},"extra":{}}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        RuntimeSelectionService selection = new(HealthWithSources(
            ("package_index", "tuna-pypi", "https://a.invalid")));

        RuntimeSelectionException error = await Assert.ThrowsAsync<RuntimeSelectionException>(
            () => selection.ApplySourcePreferenceAsync(
                client,
                ["aliyun-pypi"],
                TestContext.Current.CancellationToken));

        Assert.Equal(RuntimeSelectionErrorKind.UnknownSource, error.Kind);
        Assert.Null(handler.LastMethod);
    }

    private static Wire.Health HealthWithSources(
        params (string Kind, string Id, string Endpoint)[] sources) => new()
    {
        SchemaVersion = 2,
        InstanceId = "sup-1",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities = [RuntimeSelectionService.DownloadSourceCapability],
        CapabilityDescriptors =
        [
            new Wire.CapabilityDescriptor
            {
                Name = RuntimeSelectionService.DownloadSourceCapability,
                Lifecycle = "active",
                IntroducedIn = "2.7.0",
                DeprecatedIn = null,
                SunsetAt = null,
                Replacement = null,
                DownloadSourceCatalog = new Wire.DownloadSourceCatalog
                {
                    Sources = [.. sources.Select(source => new Wire.DownloadSourceDescriptor
                    {
                        Kind = source.Kind,
                        Id = source.Id,
                        Endpoint = source.Endpoint,
                    })],
                },
            },
        ],
    };

    [Fact]
    public async Task SubmitSendsTaskEngineOverrideAndOmitsItWhenNullAsync()
    {
        var handler = new FakeHandler("""
            {"job_id":"job-1","schema_version":2,"instance_id":"sup-1","state":"accepted","items":[]}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        SubmitRequest request = UploadRequest();

        await client.SubmitAsync(
            request,
            new Dictionary<string, SubmitUpload>
            {
                ["file-a"] = new("image/png", new byte[] { 1, 2, 3 }),
            },
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"engine\"", handler.LastBody);

        request = UploadRequest() with
        {
            Pipeline = new PipelineSelection
            {
                PipelineId = "OCR",
                Engine = OcrEngine.PaddleOcr,
            },
        };
        await client.SubmitAsync(
            request,
            new Dictionary<string, SubmitUpload>
            {
                ["file-a"] = new("image/png", new byte[] { 1, 2, 3 }),
            },
            TestContext.Current.CancellationToken);
        Assert.Contains("\"engine\":\"paddleocr\"", handler.LastBody);
    }

    [Fact]
    public async Task TypedErrorIsRaisedOnNonSuccessAsync()
    {
        var handler = new FakeHandler("""
            {"schema_version":2,"instance_id":"sup-1","code":"OUT_OF_MEMORY","message":"oom","category":"oom","retryable":true,"detail":{},"job_id":"job-1"}
            """, statusCode: HttpStatusCode.InsufficientStorage);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        InferenceClientException exception = await Assert.ThrowsAsync<InferenceClientException>(
            () => client.ObserveAsync(
                "job-1",
                0,
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpV2ErrorCode.OutOfMemory, exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public void ConstructorRejectsNonLoopback()
    {
        Assert.Throws<ArgumentException>(
            () => new InferenceHttpClient(new Uri("http://10.0.0.5:9"), "tok"));
    }

    private static SubmitRequest UploadRequest() => new()
    {
        RequestId = "request-1",
        Kind = JobKind.Recognition,
        Priority = JobPriority.Interactive,
        Pipeline = new PipelineSelection { PipelineId = "OCR" },
        Items =
        [
            new SubmitItem
            {
                ClientItemKey = "client-a",
                Ordinal = 0,
                DisplayName = "a.png",
                Source = new Dictionary<string, JsonElement>
                {
                    ["type"] = JsonSerializer.Deserialize<JsonElement>("\"upload.v1\""),
                    ["attachment"] = JsonSerializer.Deserialize<JsonElement>("\"file-a\""),
                },
            },
        ],
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        private readonly HttpStatusCode _status;

        public FakeHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
            : this([body], statusCode)
        {
        }

        public FakeHandler(string[] bodies, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _bodies = new Queue<string>(bodies);
            _status = statusCode;
        }

        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastQuery { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            LastQuery = request.RequestUri?.Query;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            string body = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();
            var content = new StringContent(body);
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(_status) { Content = content };
        }
    }
}
