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

    [Fact]
    public async Task QrGeneratePostsWirePayloadAndReadsImageAsync()
    {
        var handler = new FakeHandler("""
            {"image":"AQID","media_type":"image/png"}
            """);
        await using var client = new QrCodeHttpClient(Base, "tok", handler);

        QrCodeGeneratedImage image = await client.GenerateAsync(
            "hello", "qrcode", TestContext.Current.CancellationToken);

        Assert.Equal("AQID", image.Base64Png);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/v2/qrcode/generate", handler.LastPath);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("tok", handler.LastAuthorizationParameter);
        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("hello", body.RootElement.GetProperty("data").GetString());
        Assert.Equal("qrcode", body.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task QrDecodePostsWirePayloadAndReadsCodesAsync()
    {
        var handler = new FakeHandler("""
            {"codes":[{"data":"hello","format":"QR","is_url":false}]}
            """);
        await using var client = new QrCodeHttpClient(Base, "tok", handler);

        IReadOnlyList<QrCodeDecodedItem> codes = await client.DecodeAsync(
            "AQID", TestContext.Current.CancellationToken);

        QrCodeDecodedItem code = Assert.Single(codes);
        Assert.Equal("hello", code.Data);
        Assert.Equal("QR", code.Format);
        Assert.False(code.IsUrl);
        Assert.Equal("/v2/qrcode/decode", handler.LastPath);
        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("AQID", body.RootElement.GetProperty("image").GetString());
    }

    [Fact]
    public async Task ExportPostsSnakeCasePayloadAndReadsResultAsync()
    {
        var handler = new FakeHandler("""
            {"output_path":"C:/out.txt","bytes_written":5,"incomplete":true,"images_missing":2}
            """);
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        ExportResult result = await client.ExportAsync(
            new ExportRequest("raw", "markdown", "html", "C:/out.txt", "txt", true, [JsonSerializer.SerializeToElement(new { type = "table", rows = 2 })]),
            TestContext.Current.CancellationToken);

        Assert.Equal("C:/out.txt", result.OutputPath);
        Assert.Equal(5, result.BytesWritten);
        Assert.True(result.Incomplete);
        Assert.Equal(2, result.ImagesMissing);
        Assert.Equal("/v2/export", handler.LastPath);
        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("raw", body.RootElement.GetProperty("raw_text").GetString());
        Assert.Equal("markdown", body.RootElement.GetProperty("markdown_text").GetString());
        Assert.Equal("html", body.RootElement.GetProperty("html_text").GetString());
        Assert.Equal("C:/out.txt", body.RootElement.GetProperty("output_path").GetString());
        Assert.Equal("txt", body.RootElement.GetProperty("format").GetString());
        Assert.True(body.RootElement.GetProperty("overwrite").GetBoolean());
        Assert.Equal(2, body.RootElement.GetProperty("raw_blocks")[0].GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task ResultAssetUsesAuthorizedJobBoundRoute()
    {
        var handler = new FakeHandler("PNG", mediaType: "image/png");
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        byte[] image = await client.FetchResultAssetAsync(
            "job-1", "it-2", "abcdef0123456789abcdef0123456789",
            TestContext.Current.CancellationToken);

        Assert.Equal("PNG", System.Text.Encoding.UTF8.GetString(image));
        Assert.Equal("/v2/jobs/job-1/items/it-2/assets/abcdef0123456789abcdef0123456789", handler.LastPath);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("tok", handler.LastAuthorizationParameter);
        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchResultAssetAsync(
            "../wrong", "it-2", "asset", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PdfMutationsPostWirePayloadsAndReadResultsAsync()
    {
        // Responses are shaped exactly like the generated v2 wire contracts:
        // open -> PdfOpenResponse {session_id, model{file_path, pages}},
        // rotate/delete -> PdfMutationResponse {diff{full_model?}}; without a
        // full_model the authoritative page count comes from the existing
        // /model endpoint, and save -> SaveResponse {path}.
        var handler = new FakeHandler(
        [
            """{"schema_version":2,"instance_id":"sup-1","session_id":"pdf-1","model":{"file_path":"C:/in.pdf","pages":[{"page_index":0},{"page_index":1},{"page_index":2}]}}""",
            """{"schema_version":2,"instance_id":"sup-1","diff":{"structural_change":true,"full_model":{"file_path":"C:/in.pdf","pages":[{"page_index":0},{"page_index":1},{"page_index":2}]}},"extra":null}""",
            """{"schema_version":2,"instance_id":"sup-1","diff":{"structural_change":true},"extra":null}""",
            """{"schema_version":2,"instance_id":"sup-1","file_path":"C:/in.pdf","pages":[{"page_index":0},{"page_index":2}]}""",
            """{"schema_version":2,"instance_id":"sup-1","path":"C:/out.pdf","diff":{"replaced_pages":[]}}""",
            """{"schema_version":2,"instance_id":"sup-1","closed":true}""",
        ]);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PdfSessionOpenResult opened = await client.OpenPdfSessionAsync(
            "C:/in.pdf", null, cancellationToken);
        Assert.Equal("pdf-1", opened.SessionId);
        Assert.Equal(3, opened.PageCount);
        Assert.Equal("C:/in.pdf", opened.FilePath);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/v2/pdf/sessions/open", handler.LastPath);
        using (JsonDocument body = JsonDocument.Parse(handler.LastBody!))
        {
            // OpenRequest is additionalProperties:false with `path` only.
            Assert.Equal("C:/in.pdf", body.RootElement.GetProperty("path").GetString());
            Assert.False(body.RootElement.TryGetProperty("password", out _));
        }

        PdfMutateResult rotated = await client.RotatePdfPagesAsync(
            "pdf-1", [0, 2], 90, cancellationToken);
        Assert.Equal(3, rotated.PageCount);
        Assert.Equal("/v2/pdf/sessions/pdf-1/rotate", handler.LastPath);
        using (JsonDocument body = JsonDocument.Parse(handler.LastBody!))
        {
            Assert.Equal([0, 2], body.RootElement.GetProperty("pages")
                .EnumerateArray().Select(page => page.GetInt32()).ToArray());
            Assert.Equal(90, body.RootElement.GetProperty("angle").GetInt32());
        }

        PdfMutateResult deleted = await client.DeletePdfPagesAsync(
            "pdf-1", [1], cancellationToken);
        // The delete diff carries no full_model, so the authoritative count
        // comes from the existing /model endpoint, not local page arithmetic.
        Assert.Equal(2, deleted.PageCount);
        Assert.Equal("/v2/pdf/sessions/pdf-1/model", handler.LastPath);
        // The /model read-back is a body-less POST (matching the real route),
        // so the delete payload is the second-to-last recorded body.
        string deleteBody = handler.Bodies[^2]!;
        using (JsonDocument body = JsonDocument.Parse(deleteBody))
        {
            Assert.Equal(1, body.RootElement.GetProperty("pages")[0].GetInt32());
            Assert.False(body.RootElement.TryGetProperty("angle", out _));
        }

        string saved = await client.SavePdfAsync(
            "pdf-1", "C:/out.pdf", cancellationToken);
        Assert.Equal("C:/out.pdf", saved);
        Assert.Equal("/v2/pdf/sessions/pdf-1/save", handler.LastPath);
        using (JsonDocument body = JsonDocument.Parse(handler.LastBody!))
        {
            // SaveRequest uses `path`, never `output_path`.
            Assert.Equal("C:/out.pdf", body.RootElement.GetProperty("path").GetString());
            Assert.False(body.RootElement.TryGetProperty("output_path", out _));
        }

        await client.ClosePdfSessionAsync("pdf-1", cancellationToken);
        Assert.Equal("/v2/pdf/sessions/pdf-1/close", handler.LastPath);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
    }

    [Fact]
    public async Task OpenPdfSessionRejectsPasswordWithoutRequestAsync()
    {
        // OpenRequest has no password field; a non-empty password must be
        // refused locally instead of being sent into a server-side 400.
        var handler = new FakeHandler("{}");
        await using var client = new InferenceHttpClient(Base, "tok", handler);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.OpenPdfSessionAsync(
                "C:/in.pdf", "secret", TestContext.Current.CancellationToken));

        Assert.Null(handler.LastPath);
    }

    [Fact]
    public async Task PdfInsertionAndReorderCarryAuthoritativeModelDiff()
    {
        const string response = """{"schema_version":2,"instance_id":"test","diff":{"full_model":{"pages":[{"page_index":0,"rotation":90,"rect":[0,0,500,300]},{"page_index":1,"rotation":0,"rect":[0,0,640,480]}],"is_modified":true},"structural_change":true}}""";
        var handler = new FakeHandler([response, response, response]);
        await using var client = new InferenceHttpClient(Base, "tok", handler);
        PdfMutateResult blank = await client.InsertPdfBlankAsync("pdf-1", 0, 640, 480, CancellationToken.None);
        Assert.Equal("/v2/pdf/sessions/pdf-1/insert_blank", handler.LastPath);
        Assert.Equal(2, blank.Diff!.FullModel!.Pages!.Count);
        using (var body = JsonDocument.Parse(handler.LastBody!))
        { Assert.Equal(0, body.RootElement.GetProperty("after_index").GetInt32()); Assert.Equal(640, body.RootElement.GetProperty("width").GetDouble()); }
        await client.InsertPdfFromAsync("pdf-1", "authorized.pdf", -1, CancellationToken.None);
        Assert.Equal("/v2/pdf/sessions/pdf-1/insert_from", handler.LastPath);
        using (var body = JsonDocument.Parse(handler.LastBody!)) Assert.Equal("authorized.pdf", body.RootElement.GetProperty("source_path").GetString());
        PdfMutateResult reordered = await client.ReorderPdfAsync("pdf-1", [1, 0], CancellationToken.None);
        Assert.Equal("/v2/pdf/sessions/pdf-1/reorder", handler.LastPath);
        Assert.True(reordered.Diff!.StructuralChange);
        using (var body = JsonDocument.Parse(handler.LastBody!)) Assert.Equal(new[] { 1, 0 }, body.RootElement.GetProperty("new_order").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"session_id\":5,\"model\":{\"pages\":[],\"file_path\":\"a.pdf\"}}")]
    [InlineData("{\"session_id\":\"pdf-1\",\"model\":[]}")]
    public async Task MalformedPdfOpenResponseReportsProtocolMismatch(string response)
    {
        await using var client = new InferenceHttpClient(Base, "tok", new FakeHandler(response));
        var error = await Assert.ThrowsAsync<InferenceClientException>(
            () => client.OpenPdfSessionAsync("a.pdf", null, TestContext.Current.CancellationToken));
        Assert.Equal(HttpV2ErrorCode.ProtocolMismatch, error.Code);
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
        private readonly string _mediaType;

        public FakeHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK, string mediaType = "application/json")
            : this([body], statusCode, mediaType)
        {
        }

        public FakeHandler(string[] bodies, HttpStatusCode statusCode = HttpStatusCode.OK, string mediaType = "application/json")
        {
            _bodies = new Queue<string>(bodies);
            _status = statusCode;
            _mediaType = mediaType;
        }

        public HttpMethod? LastMethod { get; private set; }
        public List<string?> Bodies { get; } = [];
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
            Bodies.Add(LastBody);
            string body = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();
            var content = new StringContent(body);
            content.Headers.ContentType = new(_mediaType);
            return new HttpResponseMessage(_status) { Content = content };
        }
    }
}
