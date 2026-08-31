using VibeOCR.App.Features.Recognition;
using VibeOCR.Contracts.HttpV2;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class OcrEngineWireTests
{
    [Theory]
    [InlineData(OcrEngine.RapidOcr, "rapidocr")]
    [InlineData(OcrEngine.Windows, "windows")]
    [InlineData(OcrEngine.PaddleOcr, "paddleocr")]
    public void StableEngineValuesRoundTrip(OcrEngine engine, string wireName)
    {
        Assert.Equal(wireName, OcrEngineWire.Format(engine));
        Assert.Equal(engine, OcrEngineWire.Parse(wireName));
    }

    [Fact]
    public void UnknownEngineIsNotSilentlyReplaced() =>
        Assert.Null(OcrEngineWire.Parse("not-an-engine"));
}
