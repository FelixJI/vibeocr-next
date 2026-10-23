using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.Contracts.Tests;

public sealed class GeneratedWireEnumTests
{
    [Fact]
    public void GeneratedEnumUsesPinnedWireString()
    {
        Assert.Equal("\"load\"", JsonSerializer.Serialize(ProgressPhase.Load));
        Assert.Equal(
            ProgressPhase.Delete,
            JsonSerializer.Deserialize<ProgressPhase>("\"delete\""));
    }

    [Theory]
    [InlineData("\"LOAD\"")]
    [InlineData("\"unknown\"")]
    [InlineData("0")]
    public void GeneratedEnumRejectsNonWireValues(string json)
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ProgressPhase>(json));
    }

    [Fact]
    public void RecognitionModeMirrorPinsRapidTableWireString()
    {
        // 手写镜像必须与 Python 契约 lock-step：rapid_table 是 residency /
        // pipeline spec wire 的合法值，缺失成员会在反序列化时 fail closed。
        Assert.Equal("\"rapid_table\"", JsonSerializer.Serialize(RecognitionMode.RapidTable));
        Assert.Equal(RecognitionMode.RapidTable, JsonSerializer.Deserialize<RecognitionMode>("\"rapid_table\""));
        Assert.Equal(RecognitionMode.PaddleTable, JsonSerializer.Deserialize<RecognitionMode>("\"paddle_table\""));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RecognitionMode>("\"rapidtable\""));
    }
}
