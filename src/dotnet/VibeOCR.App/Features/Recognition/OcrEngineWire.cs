using VibeOCR.Contracts.HttpV2;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Maps OCR engine values between the Runtime wire and typed contracts.</summary>
internal static class OcrEngineWire
{
    internal static OcrEngine? Parse(string? wireName) => wireName switch
    {
        // rapidocr 已退役：仅历史记录/旧状态可解析，不再出现在新提交。
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
