// #213: local QR/barcode decoding replaces the deferred Supervisor round-trip.
// Pixels come from the real Windows BitmapDecoder (scaled to <=4096 px per
// side, mirroring the Runtime decode budget); decoding runs the pinned
// ZXing.Net 0.16.11 reader on a background thread in a bounded pass
// sequence. Generation reuses LocalQrCodeGenerator unchanged.
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;
using ZXing.OneD;
using VibeOCR.App.Features.QrCode;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Inference;

/// <summary>
/// Local (offline) <see cref="IQrCodeClient"/>: decode never waits for, talks
/// to, or depends on the Supervisor. At most one decode runs per service;
/// queued callers cancel immediately on their token, and the running decode
/// checks its token between passes and unwinds for real (no fire-and-forget
/// computation is left behind by an early return).
/// Public results keep the Runtime pyzbar baseline; the accepted exception
/// is RSS Expanded, reported as the pinned reader's (AI)value readable text —
/// RawBytes is null there, so raw GS1 element strings are not recoverable
/// (a raw-preserving decoder boundary would be needed for that).
/// </summary>
public sealed class LocalQrCodeClient(CancellationToken shutdownToken = default)
  : IQrCodeClient
{
  /// <summary>Any side longer than this is proportionally downscaled before
  /// decoding (same budget as the Runtime's pyzbar path).</summary>
  private const int MaximumDecodeDimension = 4096;
  /// <summary>Bounded result count: the workbench state publishes at most 4
  /// items, so 32 caps memory while staying far above any plausible sheet.</summary>
  internal const int MaximumResults = 32;

  private readonly CancellationToken _shutdownToken = shutdownToken;
  private readonly SemaphoreSlim _decodeGate = new(1, 1);
  private int _disposed;

  /// <summary>pyzbar/zbar-compatible display names, normalized only here.</summary>
  private static readonly IReadOnlyDictionary<BarcodeFormat, string> FormatNames =
    new Dictionary<BarcodeFormat, string>
    {
      [BarcodeFormat.QR_CODE] = "QRCODE",
      [BarcodeFormat.CODE_128] = "CODE128",
      [BarcodeFormat.CODE_39] = "CODE39",
      [BarcodeFormat.CODE_93] = "CODE93",
      [BarcodeFormat.EAN_13] = "EAN13",
      [BarcodeFormat.EAN_8] = "EAN8",
      [BarcodeFormat.UPC_A] = "UPCA",
      [BarcodeFormat.UPC_E] = "UPCE",
      [BarcodeFormat.CODABAR] = "CODABAR",
      [BarcodeFormat.ITF] = "I2/5",
      [BarcodeFormat.RSS_14] = "DATABAR",
      [BarcodeFormat.RSS_EXPANDED] = "DATABAR_EXP",
      [BarcodeFormat.DATA_MATRIX] = "DATAMATRIX",
      [BarcodeFormat.PDF_417] = "PDF417",
    };

  public async Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
    ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    if (image.IsEmpty) throw new ArgumentException("解码输入图片为空。", nameof(image));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
    // Queued cancellation returns here immediately (the gate is not taken);
    // the running decode is cancelled only at its pass boundaries.
    await _decodeGate.WaitAsync(linked.Token);
    try
    {
      ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
      (byte[] Bgra, int Width, int Height) pixels = await DecodeImagePixelsAsync(image, linked.Token);
      linked.Token.ThrowIfCancellationRequested();
      // CancellationToken.None: the pass sequence must start and unwind for
      // real at its own boundaries, never be abandoned half-started.
      return await Task.Run(
        () => DecodeFromPixels(pixels.Bgra, pixels.Width, pixels.Height, linked.Token),
        CancellationToken.None);
    }
    finally
    {
      _decodeGate.Release();
    }
  }

  public async Task<QrCodeGeneratedImage> GenerateAsync(
    string data, string format, CancellationToken cancellationToken)
  {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
    return await LocalQrCodeGenerator.GenerateAsync(data, format, linked.Token);
  }

  public ValueTask DisposeAsync()
  {
    // The desktop cancels the shutdown token before disposing (App close
    // path), so queued callers leave the gate immediately and a running
    // decode unwinds at its pass boundary and releases naturally. The gate
    // itself is deliberately not disposed: a Release racing a SemaphoreSlim
    // Dispose would surface ObjectDisposedException to decode callers.
    Interlocked.Exchange(ref _disposed, 1);
    return ValueTask.CompletedTask;
  }

  private static async Task<(byte[] Bgra, int Width, int Height)> DecodeImagePixelsAsync(
    ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
  {
    using var stream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(stream))
    {
      writer.WriteBytes(image.ToArray());
      await writer.StoreAsync();
      writer.DetachStream();
    }
    stream.Seek(0);
    BitmapDecoder decoder;
    try
    {
      decoder = await BitmapDecoder.CreateAsync(stream);
      if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
        throw new InvalidDataException("Input image has no pixels.");
    }
    catch (InvalidDataException) { throw; }
    catch (Exception error)
    {
      throw new InvalidDataException("无法解码输入图片。", error);
    }
    var transform = new BitmapTransform();
    uint width = decoder.PixelWidth, height = decoder.PixelHeight;
    uint longest = Math.Max(width, height);
    if (longest > MaximumDecodeDimension)
    {
      double scale = MaximumDecodeDimension / (double)longest;
      transform.ScaledWidth = Math.Max(1u, (uint)Math.Round(width * scale));
      transform.ScaledHeight = Math.Max(1u, (uint)Math.Round(height * scale));
      (width, height) = (transform.ScaledWidth, transform.ScaledHeight);
    }
    byte[] pixels;
    try
    {
      pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
        BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation,
        ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }
    catch (Exception error)
    {
      throw new InvalidDataException("无法解码输入图片。", error);
    }
    cancellationToken.ThrowIfCancellationRequested();
    return (pixels, checked((int)width), checked((int)height));
  }

  private static IReadOnlyList<QrCodeDecodedItem> DecodeFromPixels(
    byte[] bgra, int width, int height, CancellationToken cancellationToken)
  {
    // BGRA32 is consumed directly; no intermediate RGB buffer is built.
    LuminanceSource source = new RGBLuminanceSource(
      bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
    var collected = new List<QrCodeDecodedItem>();
    foreach (Pass pass in EnumeratePasses(source))
    {
      cancellationToken.ThrowIfCancellationRequested();
      RunDecodePass(pass, collected);
      // Cancelled means cancelled: a found (or final-pass) result must not be
      // reported as success once the caller's token has fired.
      cancellationToken.ThrowIfCancellationRequested();
      // Escalate only while nothing was found; the first pass mirrors the
      // Runtime's single-pass capability.
      if (collected.Count > 0) break;
    }
    return collected;
  }

  private readonly record struct Pass(LuminanceSource Source, bool PureBarcode, bool GlobalHistogram);

  /// <summary>Bounded pass order: hybrid binarizer at 0/90/180/270°, pure
  /// barcode (the documented 0.16.11 V1 detector workaround for single-code
  /// images), global histogram binarizer at the same rotations, then the four
  /// inverted orientations. Rotated/inverted sources are derived lazily, only
  /// when their pass is actually reached.</summary>
  private static IEnumerable<Pass> EnumeratePasses(LuminanceSource source)
  {
    yield return new Pass(source, PureBarcode: false, GlobalHistogram: false);
    LuminanceSource rotated90 = source.rotateCounterClockwise();
    yield return new Pass(rotated90, PureBarcode: false, GlobalHistogram: false);
    LuminanceSource rotated180 = rotated90.rotateCounterClockwise();
    yield return new Pass(rotated180, PureBarcode: false, GlobalHistogram: false);
    LuminanceSource rotated270 = rotated180.rotateCounterClockwise();
    yield return new Pass(rotated270, PureBarcode: false, GlobalHistogram: false);
    yield return new Pass(source, PureBarcode: true, GlobalHistogram: false);
    yield return new Pass(source, PureBarcode: false, GlobalHistogram: true);
    yield return new Pass(rotated90, PureBarcode: false, GlobalHistogram: true);
    yield return new Pass(rotated180, PureBarcode: false, GlobalHistogram: true);
    yield return new Pass(rotated270, PureBarcode: false, GlobalHistogram: true);
    yield return new Pass(source.invert(), PureBarcode: false, GlobalHistogram: false);
    yield return new Pass(rotated90.invert(), PureBarcode: false, GlobalHistogram: false);
    yield return new Pass(rotated180.invert(), PureBarcode: false, GlobalHistogram: false);
    yield return new Pass(rotated270.invert(), PureBarcode: false, GlobalHistogram: false);
  }

  private static void RunDecodePass(Pass pass, List<QrCodeDecodedItem> collected)
  {
    var reader = new BarcodeReaderGeneric(
      reader: null,
      createBinarizer: pass.GlobalHistogram
        ? static source => new GlobalHistogramBinarizer(source)
        : null,
      createRGBLuminanceSource: null)
    {
      AutoRotate = false,
      // Keep the Runtime's Codabar reading: zbar includes the A/B start/stop
      // glyphs in the payload.
      Options = new DecodingOptions
      {
        TryHarder = true,
        PureBarcode = pass.PureBarcode,
        ReturnCodabarStartEnd = true,
      },
    };
    if (pass.PureBarcode)
    {
      Result? result = reader.Decode(pass.Source);
      if (result is not null) CollectResults([result], collected);
      return;
    }
    CollectResults(reader.DecodeMultiple(pass.Source) ?? [], collected);
  }

  /// <summary>Collects decode results with first-seen (format, data) dedupe
  /// and the bounded result cap; exposed for unit tests of
  /// ordering/dedupe/cap semantics.</summary>
  internal static void CollectResults(IEnumerable<Result?> passResults, List<QrCodeDecodedItem> collected)
  {
    foreach (Result? result in passResults)
    {
      if (result is null) continue;
      string data = result.Text ?? string.Empty;
      // Matches the Runtime contract: whitespace-only payloads are dropped.
      if (string.IsNullOrWhiteSpace(data)) continue;
      // Pyzbar-baseline normalizations at this single boundary, so public
      // results keep the Runtime's zbar semantics on reproducible inputs:
      if (result.BarcodeFormat == BarcodeFormat.UPC_EAN_EXTENSION)
        continue; // zbar's default decode never surfaces EAN2/EAN5 add-ons.
      if (result.BarcodeFormat == BarcodeFormat.RSS_14 &&
          data.Length == 14 && data.All(char.IsAsciiDigit))
        data = "01" + data; // zbar emits the GS1 AI 01 element string.
      BarcodeFormat format = result.BarcodeFormat;
      if (result.BarcodeFormat is BarcodeFormat.UPC_A or BarcodeFormat.UPC_E)
      {
        // zbar reports UPC-A/UPC-E as their EAN-13 equivalents (leading 0);
        // UPC-E expands through the pinned reader's own conversion API.
        try
        {
          string upca = result.BarcodeFormat == BarcodeFormat.UPC_E
            ? UPCEReader.convertUPCEtoUPCA(data)
            : data;
          data = "0" + upca;
          format = BarcodeFormat.EAN_13;
        }
        catch (Exception error) when (error is ArgumentException or System.FormatException or IndexOutOfRangeException)
        {
          // Malformed UPC input falls back to the raw reading instead of
          // failing the whole decode.
        }
      }
      string displayName = MapFormatName(format, data);
      bool duplicate = false;
      foreach (QrCodeDecodedItem item in collected)
      {
        if (item.Format == displayName && item.Data == data) { duplicate = true; break; }
      }
      if (duplicate) continue;
      collected.Add(new QrCodeDecodedItem(data, displayName, IsHttpUrl(data)));
      if (collected.Count >= MaximumResults) return;
    }
  }

  /// <summary>Single boundary for pyzbar/zbar-compatible format naming.
  /// UPC-A/UPC-E surface as EAN-13 and add-ons are dropped by
  /// <see cref="CollectResults"/>, so they never reach this mapping.</summary>
  internal static string MapFormatName(BarcodeFormat format, string data) =>
    FormatNames.TryGetValue(format, out string? name) ? name : format.ToString();

  /// <summary>Strict http/https URL check (scheme case-insensitive, host
  /// required), mirroring the Runtime's <c>_is_http_url</c> contract.</summary>
  internal static bool IsHttpUrl(string value)
  {
    if (string.IsNullOrEmpty(value)) return false;
    if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
    try
    {
      return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrEmpty(uri.Host);
    }
    catch (UriFormatException) { return false; }
  }
}
