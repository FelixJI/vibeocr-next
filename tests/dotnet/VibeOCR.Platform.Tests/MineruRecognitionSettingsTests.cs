using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.Platform.Tests;

/// <summary>
/// /v2/settings extra.mineru_recognition 读写适配契约：缺省投影、全量
/// 校验、其他 extra/TTL/下载源原样保留，以及非法持久值 fail closed 标记
/// （不静默当作目录默认）。
/// </summary>
public sealed class MineruRecognitionSettingsTests
{
    [Fact]
    public void ReadProjectsUnstoredDefaultsWhenExtraIsMissing()
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            new SettingsSnapshot(),
            supported: true);

        Assert.True(state.Supported);
        Assert.False(state.Stored);
        Assert.Null(state.Tier);
        Assert.Null(state.OcrMode);
        Assert.Null(state.PageRange);
        Assert.Null(state.Language);
        Assert.False(state.Invalid);
    }

    [Fact]
    public void ReadWithoutCapabilityHidesStoredValues()
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier = "flash",
                ocr_mode = "txt",
                page_range = "1-3",
                language = "korean",
            }),
            supported: false);

        Assert.False(state.Supported);
        Assert.False(state.Stored);
        Assert.Null(state.Tier);
    }

    [Fact]
    public void ReadProjectsStoredPreferenceValues()
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier = "flash",
                ocr_mode = "txt",
                page_range = "1-3,r5",
                language = "korean",
            }),
            supported: true);

        Assert.True(state.Supported);
        Assert.True(state.Stored);
        Assert.Equal(MineruTier.Flash, state.Tier);
        Assert.Equal(MineruOcrMode.Txt, state.OcrMode);
        Assert.Equal("1-3,r5", state.PageRange);
        Assert.Equal("korean", state.Language);
        Assert.False(state.Invalid);
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("")]
    public void ReadMarksUnknownTierInvalidInsteadOfDowngrading(string tier)
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier,
                ocr_mode = "auto",
                page_range = "all",
                language = "ch",
            }),
            supported: true);

        Assert.True(state.Stored);
        Assert.True(state.Invalid);
        Assert.NotNull(state.InvalidReason);
        Assert.Null(state.Tier);
    }

    [Fact]
    public void ReadParsesEveryOcrMode()
    {
        foreach ((string wire, MineruOcrMode expected) in new (string, MineruOcrMode)[]
        {
            ("auto", MineruOcrMode.Auto),
            ("txt", MineruOcrMode.Txt),
            ("ocr", MineruOcrMode.Ocr),
        })
        {
            MineruRecognitionState state = MineruRecognitionSettings.Read(
                SnapshotWithMineruRecognition(new
                {
                    tier = "basic",
                    ocr_mode = wire,
                    page_range = "all",
                    language = "ch",
                }),
                supported: true);

            Assert.False(state.Invalid);
            Assert.Equal(expected, state.OcrMode);
        }
    }

    [Fact]
    public void ReadMarksUnknownOcrModeInvalid()
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier = "basic",
                ocr_mode = "hybrid",
                page_range = "all",
                language = "ch",
            }),
            supported: true);

        Assert.True(state.Invalid);
        Assert.Null(state.OcrMode);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("1")]
    [InlineData("1-3")]
    [InlineData("r1")]
    [InlineData("r1-r3")]
    [InlineData("1,3-5,r2")]
    [InlineData("10-3,1")]
    public void ReadAcceptsCanonicalPageRanges(string pageRange)
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier = "basic",
                ocr_mode = "auto",
                page_range = pageRange,
                language = "ch",
            }),
            supported: true);

        Assert.False(state.Invalid);
        Assert.Equal(pageRange, state.PageRange);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1-")]
    [InlineData("-3")]
    [InlineData("1,,2")]
    [InlineData("all,1")]
    [InlineData("1,all")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1-3;5")]
    [InlineData("01")]
    [InlineData("r")]
    public void ReadMarksNonCanonicalPageRangesInvalid(string pageRange)
    {
        MineruRecognitionState state = MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier = "basic",
                ocr_mode = "auto",
                page_range = pageRange,
                language = "ch",
            }),
            supported: true);

        Assert.True(state.Invalid);
        Assert.Null(state.PageRange);
    }

    [Fact]
    public void ReadMarksMissingOrNonStringFieldsInvalid()
    {
        Assert.True(MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new { tier = "basic" }),
            supported: true).Invalid);
        Assert.True(MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition(new
            {
                tier = 7,
                ocr_mode = "auto",
                page_range = "all",
                language = "ch",
            }),
            supported: true).Invalid);
        Assert.True(MineruRecognitionSettings.Read(
            SnapshotWithMineruRecognition("basic"),
            supported: true).Invalid);
    }

    [Fact]
    public async Task ApplyPersistsValuesAndPreservesOtherSettings()
    {
        var fake = new SettingsClientFake
        {
            Settings = new SettingsSnapshot
            {
                Residency = new SettingsResidency
                {
                    DefaultTtlSeconds = 600,
                    Pipelines =
                    [
                        new PipelineSpec { Name = "OCR", TtlSeconds = 600 },
                    ],
                },
                Extra = new Dictionary<string, JsonElement>
                {
                    ["mineru_connection"] = JsonSerializer.SerializeToElement(new
                    {
                        mode = "remote",
                        api_url = "https://mineru.example.com",
                        api_key = "stored-key",
                    }),
                    ["legacy_flag"] = JsonSerializer.SerializeToElement(true),
                },
                DownloadSourceIds = ["pypi"],
            },
        };

        SettingsSnapshot updated = await MineruRecognitionSettings.ApplyAsync(
            fake,
            new MineruRecognitionPreference(
                MineruTier.Flash,
                MineruOcrMode.Txt,
                "1-3",
                "korean"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.UpdateCalls);
        JsonElement stored = updated.Extra["mineru_recognition"];
        Assert.Equal("flash", stored.GetProperty("tier").GetString());
        Assert.Equal("txt", stored.GetProperty("ocr_mode").GetString());
        Assert.Equal("1-3", stored.GetProperty("page_range").GetString());
        Assert.Equal("korean", stored.GetProperty("language").GetString());
        // 其他 extra 键、TTL 与下载源偏好原样保留。
        Assert.True(updated.Extra["legacy_flag"].GetBoolean());
        Assert.Equal(
            "https://mineru.example.com",
            updated.Extra["mineru_connection"].GetProperty("api_url").GetString());
        Assert.Equal(600, updated.Residency.DefaultTtlSeconds);
        Assert.Equal(["pypi"], updated.DownloadSourceIds);
    }

    [Fact]
    public async Task ApplyRejectsInvalidPreferenceBeforeAnyWrite()
    {
        var fake = new SettingsClientFake();

        await Assert.ThrowsAsync<ArgumentException>(() => MineruRecognitionSettings.ApplyAsync(
            fake,
            new MineruRecognitionPreference(
                MineruTier.Basic,
                MineruOcrMode.Auto,
                "0-3",
                "ch"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => MineruRecognitionSettings.ApplyAsync(
            fake,
            new MineruRecognitionPreference(
                MineruTier.Basic,
                MineruOcrMode.Auto,
                new string('1', MineruRecognitionSettings.MaxPageRangeLength + 1),
                "ch"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => MineruRecognitionSettings.ApplyAsync(
            fake,
            new MineruRecognitionPreference(
                MineruTier.Basic,
                MineruOcrMode.Auto,
                "all",
                ""),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, fake.UpdateCalls);
        Assert.Equal(0, fake.ReadCalls);
    }

    private static SettingsSnapshot SnapshotWithMineruRecognition(object value) => new()
    {
        Extra = new Dictionary<string, JsonElement>
        {
            ["mineru_recognition"] = JsonSerializer.SerializeToElement(value),
        },
    };

    private sealed class SettingsClientFake : IInferenceClient
    {
        public SettingsSnapshot Settings { get; set; } = new();

        public int UpdateCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public Uri BaseUrl => new("http://127.0.0.1:1");

        public Task<SettingsSnapshot> GetSettingsAsync(
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(Settings);
        }

        public Task<SettingsSnapshot> UpdateSettingsAsync(
            SettingsSnapshot settings,
            CancellationToken cancellationToken)
        {
            UpdateCalls++;
            Settings = settings;
            return Task.FromResult(settings);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JobRef> SubmitAsync(
            SubmitRequest request,
            IReadOnlyDictionary<string, SubmitUpload> uploads,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobUpdate> ObserveAsync(
            string jobId, int afterSequence, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobCommandResult> CommandAsync(
            JobCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ResidencyStatus> GetResidencyAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResidencyStatus());

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ResidencyStatus> PreloadRuntimeAsync(
            Wire.RuntimePreloadRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PdfSessionOpenResult> OpenPdfSessionAsync(
            string path, string? password, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderPdfPageAsync(
            string sessionId, int page, int size, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PdfMutateResult> RotatePdfPagesAsync(
            string sessionId, int[] pages, int angle, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PdfMutateResult> DeletePdfPagesAsync(
            string sessionId, int[] pages, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SavePdfAsync(
            string sessionId, string outputPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ClosePdfSessionAsync(string sessionId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
