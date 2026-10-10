using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Inference;
using VibeOCR.Platform.Inference;
using Xunit;
using ZXing;
using ZXing.Common;

namespace VibeOCR.App.Tests;

/// <summary>
/// Real-pixel decode coverage for the local #213 client. Independent samples
/// come from fixtures/codes (Python qrcode[pil]/python-barcode, see
/// scripts/generate_code_decode_fixtures.py) — never from ZXing itself.
/// </summary>
public sealed class LocalQrCodeClientTests
{
  private static string FixturePath(string name) =>
    Path.Combine(AppContext.BaseDirectory, "fixtures", "codes", name);

  private static string UpstreamFixturePath(string name) =>
    Path.Combine(AppContext.BaseDirectory, "fixtures", "codes", "upstream", name);

  private static Task<byte[]> FixtureBytesAsync(string name) =>
    File.ReadAllBytesAsync(FixturePath(name), TestContext.Current.CancellationToken);

  [Fact]
  public async Task DecodesIndependentV1UnicodeQrFixtureAsync()
  {
    // Same payload family as the documented ZXing.Net 0.16.11 detector gap:
    // the independent qrcode[pil] pixels also need the bounded PureBarcode
    // pass when the detector misses the V1 mask.
    await using var client = new LocalQrCodeClient();
    IReadOnlyList<QrCodeDecodedItem> decoded = await client.DecodeAsync(
      await FixtureBytesAsync("qr_v1_unicode.png"), TestContext.Current.CancellationToken);
    QrCodeDecodedItem item = Assert.Single(decoded);
    Assert.Equal("中文🙂", item.Data);
    Assert.Equal("QRCODE", item.Format);
    Assert.False(item.IsUrl);
  }

  [Fact]
  public async Task DecodesIndependentCode128AndEan13FixturesAsync()
  {
    await using var client = new LocalQrCodeClient();
    IReadOnlyList<QrCodeDecodedItem> code128 = await client.DecodeAsync(
      await FixtureBytesAsync("code128_independent.png"), TestContext.Current.CancellationToken);
    QrCodeDecodedItem bar = Assert.Single(code128);
    Assert.Equal("INDEP-128", bar.Data);
    Assert.Equal("CODE128", bar.Format);

    IReadOnlyList<QrCodeDecodedItem> ean13 = await client.DecodeAsync(
      await FixtureBytesAsync("ean13_independent.png"), TestContext.Current.CancellationToken);
    QrCodeDecodedItem ean = Assert.Single(ean13);
    Assert.Equal("5901234123457", ean.Data);
    Assert.Equal("EAN13", ean.Format);
  }

  [Fact]
  public async Task DecodesIndependentOneDimensionalFormatFixturesAsync()
  {
    // AC2: the publicly promised 1D formats are verified against
    // python-barcode-rendered samples, not just naming assertions. Expected
    // values are the Runtime pyzbar baseline, cross-checked with the real
    // runtime decoder on the same pixels: Code 39 keeps its writer checksum
    // char (INDEP-39C), Codabar keeps its A/B start/stop glyphs, and UPC-A
    // surfaces as EAN-13 with a leading zero (pyzbar: 0036000291452).
    await using var client = new LocalQrCodeClient();
    foreach ((string file, string expected, string format) in new[]
    {
      ("code39_independent.png", "INDEP-39C", "CODE39"),
      ("ean8_independent.png", "96385074", "EAN8"),
      ("upca_independent.png", "0036000291452", "EAN13"),
      ("itf_independent.png", "1234567890", "I2/5"),
      ("codabar_independent.png", "A1234567B", "CODABAR"),
    })
    {
      IReadOnlyList<QrCodeDecodedItem> decoded = await client.DecodeAsync(
        await FixtureBytesAsync(file), TestContext.Current.CancellationToken);
      Assert.True(decoded.Count == 1, $"{file} yielded {decoded.Count} results");
      QrCodeDecodedItem item = decoded[0];
      Assert.Equal(expected, item.Data);
      Assert.Equal(format, item.Format);
    }
  }

  [Fact]
  public async Task DecodesIndependentRotatedQrThroughBoundedRotationPassAsync()
  {
    await using var client = new LocalQrCodeClient();
    IReadOnlyList<QrCodeDecodedItem> decoded = await client.DecodeAsync(
      await FixtureBytesAsync("qr_rotated_90.png"), TestContext.Current.CancellationToken);
    QrCodeDecodedItem item = Assert.Single(decoded);
    Assert.Equal("rotated-independent-42", item.Data);
    Assert.Equal("QRCODE", item.Format);
  }

  [Fact]
  public async Task DecodesIndependentMultiCodeCompositeAsync()
  {
    await using var client = new LocalQrCodeClient();
    IReadOnlyList<QrCodeDecodedItem> decoded = await client.DecodeAsync(
      await FixtureBytesAsync("multi_composite.png"), TestContext.Current.CancellationToken);
    Assert.Equal(2, decoded.Count);
    Assert.Contains(decoded, item => item is { Data: "multi-qrcode-independent", Format: "QRCODE" });
    Assert.Contains(decoded, item => item is { Data: "INDEP-128", Format: "CODE128" });
  }

  [Fact]
  public async Task DecodesIndependentUprightAndRotatedOneDimensionalCompositeAsync()
  {
    // Same-image mixed-orientation composite: one upright Code 128 and one
    // 90-degree-rotated Code 39 with distinct payloads. The Runtime pyzbar
    // baseline returns both codes in a single call (measured on these exact
    // pixels: CODE128 MIX-ORIENT-128 + CODE39 MIX-ORIENT-39Y — the writer
    // checksum character included), so the local decode must surface both.
    await using var client = new LocalQrCodeClient();
    IReadOnlyList<QrCodeDecodedItem> decoded = await client.DecodeAsync(
      await FixtureBytesAsync("multi_orientation_composite.png"), TestContext.Current.CancellationToken);
    Assert.Equal(2, decoded.Count);
    Assert.Contains(decoded, item => item is { Data: "MIX-ORIENT-128", Format: "CODE128" });
    Assert.Contains(decoded, item => item is { Data: "MIX-ORIENT-39Y", Format: "CODE39" });
  }

  [Fact]
  public async Task ClassifiesStrictHttpUrlsFromIndependentFixturesAsync()
  {
    await using var client = new LocalQrCodeClient();
    IReadOnlyList<QrCodeDecodedItem> url = await client.DecodeAsync(
      await FixtureBytesAsync("qr_url_https.png"), TestContext.Current.CancellationToken);
    Assert.True(Assert.Single(url).IsUrl);

    IReadOnlyList<QrCodeDecodedItem> script = await client.DecodeAsync(
      await FixtureBytesAsync("qr_not_url.png"), TestContext.Current.CancellationToken);
    Assert.False(Assert.Single(script).IsUrl);
  }

  [Fact]
  public async Task DecodesUpstreamDataBarAndUpceFixturesAgainstPyzbarReferenceAsync()
  {
    // Independent upstream samples (zxing/zxing pinned commit, Apache-2.0;
    // see fixtures/codes/upstream/manifest.json). Expected values are the
    // Runtime pyzbar baseline measured on the same pixels: DATABAR carries
    // the GS1 AI 01 prefix and UPC-E surfaces as its expanded EAN-13
    // equivalent. RSS Expanded keeps the pinned reader's (AI)value readable
    // text: the user-accepted difference — RawBytes is null in the pinned
    // 0.16.11 API, so the original FNC1/GS separators cannot be recovered
    // losslessly (not merely a missing AI table).
    await using var client = new LocalQrCodeClient();

    IReadOnlyList<QrCodeDecodedItem> dataBar = await client.DecodeAsync(
      File.ReadAllBytes(UpstreamFixturePath("rss14-1_1.png")), TestContext.Current.CancellationToken);
    QrCodeDecodedItem rss = Assert.Single(dataBar);
    Assert.Equal("0104412345678909", rss.Data);   // pyzbar baseline
    Assert.Equal("DATABAR", rss.Format);

    IReadOnlyList<QrCodeDecodedItem> upce = await client.DecodeAsync(
      File.ReadAllBytes(UpstreamFixturePath("upce-1_1.png")), TestContext.Current.CancellationToken);
    QrCodeDecodedItem upc = Assert.Single(upce);
    Assert.Equal("0012345000065", upc.Data);      // pyzbar baseline
    Assert.Equal("EAN13", upc.Format);

    IReadOnlyList<QrCodeDecodedItem> code93 = await client.DecodeAsync(
      File.ReadAllBytes(UpstreamFixturePath("code93-1_1.png")), TestContext.Current.CancellationToken);
    QrCodeDecodedItem ninetyThree = Assert.Single(code93);
    Assert.Equal("1234567890", ninetyThree.Data);
    Assert.Equal("CODE93", ninetyThree.Format);

    IReadOnlyList<QrCodeDecodedItem> expanded = await client.DecodeAsync(
      File.ReadAllBytes(UpstreamFixturePath("rssexpanded-1_1.png")), TestContext.Current.CancellationToken);
    QrCodeDecodedItem exp = Assert.Single(expanded);
    Assert.Equal("(11)100224(17)110224(3102)000100", exp.Data);  // see note above
    Assert.Equal("DATABAR_EXP", exp.Format);
  }

  [Fact]
  public async Task UpstreamExtensionImagesReportMainCodeOnlyAsync()
  {
    // EAN-13 + add-on images: the Runtime's pyzbar default never surfaces
    // EAN2/EAN5 add-ons as separate results, so the local decoder must not
    // either (upcean-extension-1/1 and /2 measured with pyzbar).
    await using var client = new LocalQrCodeClient();
    foreach ((string file, string expected) in new[]
    {
      ("upcean-extension-1_1.png", "9780735200449"),
      ("upcean-extension-1_2.png", "9780884271789"),
    })
    {
      IReadOnlyList<QrCodeDecodedItem> decoded = await client.DecodeAsync(
        File.ReadAllBytes(UpstreamFixturePath(file)), TestContext.Current.CancellationToken);
      Assert.True(decoded.Count == 1, $"{file} yielded {decoded.Count} results");
      QrCodeDecodedItem item = decoded[0];
      Assert.Equal(expected, item.Data);
      Assert.Equal("EAN13", item.Format);
    }
  }

  [Fact]
  public async Task BlankIndependentFixtureYieldsNoResultsAsync()
  {
    await using var client = new LocalQrCodeClient();
    Assert.Empty(await client.DecodeAsync(
      await FixtureBytesAsync("blank.png"), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DecodesProductGeneratedDefaultPreviewV1QrAsync()
  {
    // The product's own default preview for a short Unicode payload is a V1
    // QR the pinned detector misses; the client's bounded PureBarcode pass
    // must still decode the real generated pixels end to end.
    var generated = await LocalQrCodeGenerator.GenerateAsync("中文🙂", "qrcode", TestContext.Current.CancellationToken);
    await using var client = new LocalQrCodeClient();
    QrCodeDecodedItem item = Assert.Single(await client.DecodeAsync(
      Convert.FromBase64String(generated.Base64Png), TestContext.Current.CancellationToken));
    Assert.Equal("中文🙂", item.Data);
    Assert.Equal("QRCODE", item.Format);
  }

  [Fact]
  public async Task DecodesProductGeneratedV1QrWithCaptionAsync()
  {
    // #213 review contract: V1 + caption (not just the bare PureBarcode
    // case) must keep working through the real client pass sequence.
    var generated = await LocalQrCodeGenerator.GenerateAsync(
      "中文🙂", "qrcode", new QrCodeCaption(QrCodeCaptionMode.Custom, "底部独立说明"), TestContext.Current.CancellationToken);
    await using var client = new LocalQrCodeClient();
    QrCodeDecodedItem item = Assert.Single(await client.DecodeAsync(
      Convert.FromBase64String(generated.Base64Png), TestContext.Current.CancellationToken));
    Assert.Equal("中文🙂", item.Data);
    Assert.Equal("QRCODE", item.Format);
  }

  [Fact]
  public async Task RejectsEmptyAndCorruptImageInputAsync()
  {
    await using var client = new LocalQrCodeClient();
    await Assert.ThrowsAsync<ArgumentException>(() =>
      client.DecodeAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken));
    await Assert.ThrowsAsync<InvalidDataException>(() =>
      client.DecodeAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task QueuedDecodeCancelsImmediatelyAndRunningDecodeUnwindsBetweenPassesAsync()
  {
    // A deterministic high-entropy image keeps all bounded passes busy, so
    // the second (queued) call observes the single-flight gate.
    byte[] noise = await EncodeNoisePngAsync(2048, 2048);
    byte[] fixture = await FixtureBytesAsync("qr_v1_unicode.png");
    await using var client = new LocalQrCodeClient();
    using var running = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Task<IReadOnlyList<QrCodeDecodedItem>> first = client.DecodeAsync(noise, running.Token);
    await Task.Delay(300, TestContext.Current.CancellationToken);

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    // Queued cancellation returns immediately without waiting for the gate.
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      client.DecodeAsync(fixture, cancelled.Token));
    Assert.False(first.IsCompleted);

    // The running decode leaves the gate only after unwinding between
    // passes (real cancellation, not an abandoned computation).
    running.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

    // The gate is free again: a normal decode of real pixels succeeds.
    QrCodeDecodedItem item = Assert.Single(await client.DecodeAsync(
      fixture, TestContext.Current.CancellationToken));
    Assert.Equal("中文🙂", item.Data);
  }

  [Fact]
  public async Task GenerationReusesLocalGeneratorAndHonoursShutdownAsync()
  {
    using var shutdown = new CancellationTokenSource();
    await using var client = new LocalQrCodeClient(shutdown.Token);
    QrCodeGeneratedImage image = await client.GenerateAsync("本地-42", "qrcode", TestContext.Current.CancellationToken);
    Assert.Equal("image/png", image.MediaType);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      client.GenerateAsync("hello", "qrcode", cancelled.Token));
    shutdown.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      client.GenerateAsync("hello", "qrcode", CancellationToken.None));
  }

  [Fact]
  public async Task DisposeDuringInFlightDecodeRefusesNewWorkWithoutStrandingAsync()
  {
    // Dispose runs while a decode is in flight (the desktop cancels the
    // shutdown token first, so the decode unwinds at a pass boundary):
    // no Release/ObjectDisposedException race, no stranded queued caller, and
    // new decode calls fail fast afterwards.
    byte[] noise = await EncodeNoisePngAsync(2048, 2048);
    byte[] fixture = await FixtureBytesAsync("qr_v1_unicode.png");
    await using var client = new LocalQrCodeClient();
    using var running = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Task<IReadOnlyList<QrCodeDecodedItem>> inFlight = client.DecodeAsync(noise, running.Token);
    await Task.Delay(300, TestContext.Current.CancellationToken);

    await client.DisposeAsync();
    await Assert.ThrowsAsync<ObjectDisposedException>(() =>
      client.DecodeAsync(fixture, TestContext.Current.CancellationToken));
    Assert.False(inFlight.IsCompleted);

    running.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      inFlight.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
  }

  [Fact]
  public void FormatNamesAreNormalizedToPyzbarCompatibleDisplayNames()
  {
    // Pure naming assertions. DATABAR/DATABAR_EXP/CODE93 are additionally
    // verified against real fixtures above; UPC-A/UPC-E surface as EAN-13
    // (see CollectResults) and add-ons are dropped, so they never reach a
    // result's display name.
    Assert.Equal("QRCODE", LocalQrCodeClient.MapFormatName(BarcodeFormat.QR_CODE, "x"));
    Assert.Equal("CODE128", LocalQrCodeClient.MapFormatName(BarcodeFormat.CODE_128, "x"));
    Assert.Equal("CODE39", LocalQrCodeClient.MapFormatName(BarcodeFormat.CODE_39, "x"));
    Assert.Equal("CODE93", LocalQrCodeClient.MapFormatName(BarcodeFormat.CODE_93, "x"));
    Assert.Equal("EAN13", LocalQrCodeClient.MapFormatName(BarcodeFormat.EAN_13, "x"));
    Assert.Equal("EAN8", LocalQrCodeClient.MapFormatName(BarcodeFormat.EAN_8, "x"));
    Assert.Equal("UPCA", LocalQrCodeClient.MapFormatName(BarcodeFormat.UPC_A, "x"));
    Assert.Equal("UPCE", LocalQrCodeClient.MapFormatName(BarcodeFormat.UPC_E, "x"));
    Assert.Equal("CODABAR", LocalQrCodeClient.MapFormatName(BarcodeFormat.CODABAR, "x"));
    Assert.Equal("I2/5", LocalQrCodeClient.MapFormatName(BarcodeFormat.ITF, "x"));
    Assert.Equal("DATABAR", LocalQrCodeClient.MapFormatName(BarcodeFormat.RSS_14, "x"));
    Assert.Equal("DATAMATRIX", LocalQrCodeClient.MapFormatName(BarcodeFormat.DATA_MATRIX, "x"));
    Assert.Equal("PDF417", LocalQrCodeClient.MapFormatName(BarcodeFormat.PDF_417, "x"));
    Assert.Equal("AZTEC", LocalQrCodeClient.MapFormatName(BarcodeFormat.AZTEC, "x"));
  }

  [Fact]
  public void StrictUrlClassificationMatchesRuntimeContract()
  {
    foreach (string url in new[]
    {
      "https://example.com/independent", "http://example.com", "HTTPS://EXAMPLE.COM/x",
      // A host-like netloc is valid per the Runtime contract (urlparse netloc).
      "http://nohost",
    })
      Assert.True(LocalQrCodeClient.IsHttpUrl(url));
    foreach (string value in new[]
    {
      "javascript:alert(1)", "file:///C:/x", "ftp://example.com", "example.com",
      "https://", "  https://example.com", "",
    })
      Assert.False(LocalQrCodeClient.IsHttpUrl(value));
  }

  [Fact]
  public void CollectResultsKeepsFirstSeenOrderDeduplicatesAndCapsAsync()
  {
    Result A(string data, BarcodeFormat format) => new(
      data, Array.Empty<byte>(), Array.Empty<ResultPoint>(), format);

    var collected = new List<QrCodeDecodedItem>();
    LocalQrCodeClient.CollectResults(
      [A("first", BarcodeFormat.QR_CODE), A("dup", BarcodeFormat.CODE_128), null,
       A("dup", BarcodeFormat.CODE_128), A("second", BarcodeFormat.EAN_13), A("  ", BarcodeFormat.ITF)],
      collected);
    Assert.Equal(3, collected.Count);
    Assert.Equal("first", collected[0].Data);
    Assert.Equal("dup", collected[1].Data);
    Assert.Equal("second", collected[2].Data);

    var capped = new List<QrCodeDecodedItem>();
    LocalQrCodeClient.CollectResults(
      Enumerable.Range(0, LocalQrCodeClient.MaximumResults + 10)
        .Select(i => A($"code-{i}", BarcodeFormat.CODE_128)), capped);
    Assert.Equal(LocalQrCodeClient.MaximumResults, capped.Count);
  }

  private static async Task<byte[]> EncodeNoisePngAsync(int width, int height)
  {
    // Deterministic LCG noise: no finder patterns, so every bounded pass
    // stays busy without finding anything.
    byte[] bgra = new byte[width * height * 4];
    uint state = 213;
    for (int i = 0; i < bgra.Length; i += 4)
    {
      state = state * 1664525u + 1013904223u;
      byte value = (byte)(state >> 24);
      bgra[i] = bgra[i + 1] = bgra[i + 2] = value;
      bgra[i + 3] = 255;
    }
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
      checked((uint)width), checked((uint)height), 96, 96, bgra);
    await encoder.FlushAsync();
    using var reader = new DataReader(stream.GetInputStreamAt(0));
    uint length = checked((uint)stream.Size);
    await reader.LoadAsync(length);
    byte[] png = new byte[length];
    reader.ReadBytes(png);
    return png;
  }
}
