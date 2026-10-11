// Phase 7B step ④: QR decode/generate client surface for the v2 supervisor.
//
// QR decode/generate are NOT recognition jobs (plan §7B audit), so they get
// their own seam mirroring IInferenceClient. Decode runs locally in the
// desktop process (VibeOCR.App LocalQrCodeClient); QrCodeHttpClient remains
// the wire-compatible implementation over the supervisor's /v2/qrcode
// endpoints, converting to base64 only at that boundary (#213).
using VibeOCR.Contracts.HttpV2;

namespace VibeOCR.Platform.Inference;

/// <summary>One decoded QR/barcode result.</summary>
public sealed record QrCodeDecodedItem(string Data, string Format, bool IsUrl);

/// <summary>Result of a QR/barcode generation request.</summary>
public sealed record QrCodeGeneratedImage(string Base64Png, string MediaType);

/// <summary>
/// Transport-neutral QR client used by QrCodeViewModel. The input/output
/// payloads mirror the v2 supervisor's /v2/qrcode/decode and
/// /v2/qrcode/generate endpoints; implementations own any encoding (e.g.
/// base64) required by their transport.
/// </summary>
public interface IQrCodeClient : IAsyncDisposable
{
    /// <summary>Decode QR/barcode(s) from raw encoded image bytes (PNG/JPEG/...).</summary>
    Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken);

    /// <summary>Generate a QR/barcode image from text. Returns base64-encoded PNG.</summary>
    Task<QrCodeGeneratedImage> GenerateAsync(string data, string format, CancellationToken cancellationToken);
}
