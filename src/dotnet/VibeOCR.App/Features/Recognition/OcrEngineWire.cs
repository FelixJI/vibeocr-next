using VibeOCR.Contracts.HttpV2;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Maps legacy OCR engine values between the Runtime wire and typed contracts.</summary>
internal static class OcrEngineWire
{
    internal static OcrEngine? Parse(string? wireName) => wireName switch
    {
        "rapidocr" => OcrEngine.RapidOcr,
        "windows" => OcrEngine.Windows,
        "paddleocr" => OcrEngine.PaddleOcr,
        _ => null,
    };

    internal static string Format(OcrEngine engine) => engine switch
    {
        OcrEngine.RapidOcr => "rapidocr",
        OcrEngine.Windows => "windows",
        OcrEngine.PaddleOcr => "paddleocr",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
    };
}
