using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Settings;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class OcrEngineSettingsTests : IDisposable
{
    private readonly string _configFile =
        Path.Combine(Path.GetTempPath(), $"vibeocr-engine-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingConfigFileMigratesToRapidOcrDefault()
    {
        OcrEnginePreference preference = OcrEngineSettings.Load(_configFile);

        Assert.Equal(OcrEngine.RapidOcr, preference.Engine);
        Assert.False(preference.RequiresChoice);
    }

    [Fact]
    public void ConfigWithoutEngineKeyMigratesToRapidOcrDefault()
    {
        WriteConfig("""{"schema_version":1,"hotkeys":{"global_screenshot":"Ctrl+Alt+Q"}}""");

        OcrEnginePreference preference = OcrEngineSettings.Load(_configFile);

        Assert.Equal(OcrEngine.RapidOcr, preference.Engine);
        Assert.False(preference.RequiresChoice);
    }

    [Theory]
    [InlineData("rapidocr", OcrEngine.RapidOcr)]
    [InlineData("windows", OcrEngine.Windows)]
    [InlineData("paddleocr", OcrEngine.PaddleOcr)]
    public void KnownEngineValuesLoadTyped(string wireName, OcrEngine expected)
    {
        WriteConfig($$$"""{"ocr":{"engine":"{{{wireName}}}"}}""");

        OcrEnginePreference preference = OcrEngineSettings.Load(_configFile);

        Assert.Equal(expected, preference.Engine);
        Assert.False(preference.RequiresChoice);
    }

    [Fact]
    public void UnknownEngineValueRequiresUserReSelection()
    {
        WriteConfig("""{"ocr":{"engine":"tesseract"}}""");

        OcrEnginePreference preference = OcrEngineSettings.Load(_configFile);

        Assert.Null(preference.Engine);
        Assert.True(preference.RequiresChoice);
        // 未知原值不被覆盖,重选前保持现场。
        Assert.Contains(
            "tesseract",
            File.ReadAllText(_configFile));
    }

    [Fact]
    public void UnreadableConfigRequiresUserReSelection()
    {
        File.WriteAllText(_configFile, "{\"ocr\": not-json");

        OcrEnginePreference preference = OcrEngineSettings.Load(_configFile);

        Assert.Null(preference.Engine);
        Assert.True(preference.RequiresChoice);
    }

    [Fact]
    public void SavePersistsWireNameAndPreservesOtherKeys()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-engine-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");
            layout.EnsurePortableState();
            File.WriteAllText(
                layout.ConfigFile,
            """{"schema_version":1,"hotkeys":{"global_screenshot":"Ctrl+Alt+Q"},"ocr":{"engine":"windows"}}""");

            OcrEngineSettings.Save(layout, OcrEngine.PaddleOcr);

            JsonObject config = JsonNode.Parse(File.ReadAllText(layout.ConfigFile))!.AsObject();
            Assert.Equal("paddleocr", (string?)config["ocr"]!["engine"]);
            Assert.Equal("Ctrl+Alt+Q", (string?)config["hotkeys"]!["global_screenshot"]);
            Assert.Equal(1, (int?)config["schema_version"]);

            OcrEnginePreference reloaded = OcrEngineSettings.Load(layout.ConfigFile);
            Assert.Equal(OcrEngine.PaddleOcr, reloaded.Engine);
            Assert.False(reloaded.RequiresChoice);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManualWireNamesMatchTheProtocolSerializer()
    {
        foreach (OcrEngine engine in Enum.GetValues<OcrEngine>())
        {
            string wireName = JsonSerializer
                .Serialize(engine, HttpV2JsonContext.Default.OcrEngine)
                .Trim('"');
            Assert.Equal(wireName, OcrEngineSettings.ToWireName(engine));
            Assert.Equal(engine, OcrEngineSettings.ToEngine(wireName));
        }
        Assert.Null(OcrEngineSettings.ToEngine("not-an-engine"));
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(_configFile, json);

    public void Dispose()
    {
        if (File.Exists(_configFile))
        {
            File.Delete(_configFile);
        }
    }
}
