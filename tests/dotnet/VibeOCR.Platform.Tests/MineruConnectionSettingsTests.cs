using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

/// <summary>
/// /v2/settings extra.mineru_connection 读写适配契约：缺省本地、local 只写
/// mode、remote 校验与 Backend 一致、其他 extra/TTL/source 原样保留。
/// </summary>
public sealed class MineruConnectionSettingsTests
{
    [Fact]
    public void ReadDefaultsToLocalWithoutConnectionExtra()
    {
        MineruConnectionState state = MineruConnectionSettings.Read(
            new SettingsSnapshot(),
            remoteSupported: false);

        Assert.False(state.Supported);
        Assert.Equal("local", state.Mode);
        Assert.Equal(string.Empty, state.ApiUrl);
        Assert.False(state.HasApiKey);
        Assert.False(state.IsRemote);
    }

    [Fact]
    public void ReadProjectsRemoteConnectionWithoutExposingTheKey()
    {
        MineruConnectionState state = MineruConnectionSettings.Read(
            SnapshotWithMineruConnection(new
            {
                mode = "remote",
                api_url = "https://mineru.example.com/subpath",
                api_key = "secret-value",
            }),
            remoteSupported: true);

        Assert.True(state.Supported);
        Assert.True(state.IsRemote);
        Assert.Equal("https://mineru.example.com/subpath", state.ApiUrl);
        Assert.True(state.HasApiKey);
    }

    [Fact]
    public void ReadTreatsUnknownConnectionShapesAsLocal()
    {
        Assert.Equal(
            "local",
            MineruConnectionSettings.Read(
                SnapshotWithMineruConnection(new { mode = "cluster" }),
                remoteSupported: true).Mode);
    }

    [Fact]
    public async Task ApplyLocalWritesModeOnlyAndPreservesOtherSettings()
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
                    ["legacy_flag"] = JsonSerializer.SerializeToElement(true),
                },
                DownloadSourceIds = ["pypi"],
            },
        };

        SettingsSnapshot updated = await MineruConnectionSettings.ApplyAsync(
            fake, "local", null, null, TestContext.Current.CancellationToken);

        // 只有 mineru_connection 一个键被替换；TTL、来源与其他 extra 保留。
        Assert.Equal(["pypi"], updated.DownloadSourceIds);
        Assert.Equal(600, updated.Residency.DefaultTtlSeconds);
        JsonElement connection = updated.Extra["mineru_connection"];
        Assert.Equal("local", connection.GetProperty("mode").GetString());
        Assert.False(connection.TryGetProperty("api_url", out _));
        Assert.False(connection.TryGetProperty("api_key", out _));
        Assert.True(updated.Extra["legacy_flag"].GetBoolean());
        Assert.Equal(1, fake.UpdateCalls);
    }

    [Fact]
    public async Task ApplyRemoteWritesConnectionFieldsAndPreservesOtherExtras()
    {
        var fake = new SettingsClientFake
        {
            Settings = new SettingsSnapshot
            {
                Extra = new Dictionary<string, JsonElement>
                {
                    ["legacy_flag"] = JsonSerializer.SerializeToElement("keep"),
                },
                DownloadSourceIds = ["tuna-pypi"],
            },
        };

        SettingsSnapshot updated = await MineruConnectionSettings.ApplyAsync(
            fake,
            "remote",
            "http://10.0.0.5:8200/mineru",
            "token-1",
            TestContext.Current.CancellationToken);

        JsonElement connection = updated.Extra["mineru_connection"];
        Assert.Equal("remote", connection.GetProperty("mode").GetString());
        Assert.Equal(
            "http://10.0.0.5:8200/mineru",
            connection.GetProperty("api_url").GetString());
        Assert.Equal("token-1", connection.GetProperty("api_key").GetString());
        Assert.Equal("keep", updated.Extra["legacy_flag"].GetString());
        Assert.Equal(["tuna-pypi"], updated.DownloadSourceIds);
    }

    [Fact]
    public async Task ApplyRemoteAcceptsEmptyApiKeyAndHttpsWithProxyPath()
    {
        var fake = new SettingsClientFake();

        await MineruConnectionSettings.ApplyAsync(
            fake, "remote", "https://gateway.example.com/mineru/", string.Empty,
            TestContext.Current.CancellationToken);

        JsonElement connection = fake.LastUpdate!.Extra["mineru_connection"];
        Assert.Equal(string.Empty, connection.GetProperty("api_key").GetString());
        Assert.Equal(
            "https://gateway.example.com/mineru/",
            connection.GetProperty("api_url").GetString());
    }

    [Fact]
    public async Task ApplyRemoteKeepsStoredKeyWhenApiKeyOmitted()
    {
        var fake = new SettingsClientFake
        {
            Settings = SnapshotWithMineruConnection(new
            {
                mode = "remote",
                api_url = "https://old.example.com",
                api_key = "stored-key",
            }),
        };

        // 未编辑 Key（null）：宿主读回当前设置后合并已存 Key，仅替换 url。
        await MineruConnectionSettings.ApplyAsync(
            fake, "remote", "https://mineru.example.com/updated", null,
            TestContext.Current.CancellationToken);

        JsonElement connection = fake.LastUpdate!.Extra["mineru_connection"];
        Assert.Equal(
            "https://mineru.example.com/updated",
            connection.GetProperty("api_url").GetString());
        Assert.Equal("stored-key", connection.GetProperty("api_key").GetString());
        // Backend 仍收到完整 connection 对象。
        Assert.Equal("remote", connection.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task ApplyRemoteExplicitEmptyKeyClearsStoredKey()
    {
        var fake = new SettingsClientFake
        {
            Settings = SnapshotWithMineruConnection(new
            {
                mode = "remote",
                api_url = "https://old.example.com",
                api_key = "stored-key",
            }),
        };

        await MineruConnectionSettings.ApplyAsync(
            fake, "remote", "https://old.example.com", string.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            string.Empty,
            fake.LastUpdate!.Extra["mineru_connection"]
                .GetProperty("api_key")
                .GetString());
    }

    [Fact]
    public async Task ApplyRemoteAcceptsLongKeysWithoutArbitraryCap()
    {
        string longKey = new string('k', 300);
        var fake = new SettingsClientFake();

        await MineruConnectionSettings.ApplyAsync(
            fake, "remote", "https://mineru.example.com", longKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            longKey,
            fake.LastUpdate!.Extra["mineru_connection"]
                .GetProperty("api_key")
                .GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://mineru.example.com")]
    [InlineData("file://mineru.example.com")]
    [InlineData("http://user:pw@mineru.example.com")]
    [InlineData("https://mineru.example.com/?token=1")]
    [InlineData("https://mineru.example.com/#section")]
    [InlineData("https://mineru.example.com/api endpoint")]
    [InlineData(" https://mineru.example.com")]
    public async Task ApplyRemoteRejectsInvalidServiceRoots(string? apiUrl)
    {
        var fake = new SettingsClientFake();

        await Assert.ThrowsAsync<ArgumentException>(
            () => MineruConnectionSettings.ApplyAsync(
                fake, "remote", apiUrl, string.Empty,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, fake.UpdateCalls);
    }

    [Theory]
    [InlineData("token\r\ntoken")]
    [InlineData("token\ntoken")]
    public async Task ApplyRemoteRejectsLineBreaksInApiKey(string apiKey)
    {
        var fake = new SettingsClientFake();

        await Assert.ThrowsAsync<ArgumentException>(
            () => MineruConnectionSettings.ApplyAsync(
                fake, "remote", "https://mineru.example.com", apiKey,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, fake.UpdateCalls);
    }

    [Fact]
    public async Task ApplyRejectsUnknownModes()
    {
        var fake = new SettingsClientFake();

        await Assert.ThrowsAsync<ArgumentException>(
            () => MineruConnectionSettings.ApplyAsync(
                fake, "cluster", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, fake.UpdateCalls);
    }

    private static SettingsSnapshot SnapshotWithMineruConnection(object value) => new()
    {
        Extra = new Dictionary<string, JsonElement>
        {
            ["mineru_connection"] = JsonSerializer.SerializeToElement(value),
        },
    };

    private sealed class SettingsClientFake : IInferenceClient
    {
        public SettingsSnapshot Settings { get; set; } = new();

        public SettingsSnapshot? LastUpdate { get; private set; }

        public int UpdateCalls { get; private set; }

        public Uri BaseUrl => new("http://127.0.0.1:1");

        public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public Task<SettingsSnapshot> UpdateSettingsAsync(
            SettingsSnapshot settings,
            CancellationToken cancellationToken)
        {
            UpdateCalls++;
            LastUpdate = settings;
            Settings = settings;
            return Task.FromResult(settings);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JobRef> SubmitAsync(
            SubmitRequest request,
            IReadOnlyDictionary<string, SubmitUpload> uploads,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobUpdate> ObserveAsync(
            string jobId,
            int afterSequence,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobCommandResult> CommandAsync(
            JobCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ResidencyStatus> GetResidencyAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResidencyStatus());

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PdfSessionOpenResult> OpenPdfSessionAsync(
            string path,
            string? password,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]> RenderPdfPageAsync(
            string sessionId,
            int page,
            int size,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfMutateResult> RotatePdfPagesAsync(
            string sessionId,
            int[] pages,
            int angle,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfMutateResult> DeletePdfPagesAsync(
            string sessionId,
            int[] pages,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<string> SavePdfAsync(
            string sessionId,
            string outputPath,
            CancellationToken ct) => throw new NotSupportedException();

        public Task ClosePdfSessionAsync(string sessionId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
