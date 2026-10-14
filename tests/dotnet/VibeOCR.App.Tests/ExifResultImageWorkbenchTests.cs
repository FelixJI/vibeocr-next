using System.Text.Json;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// ResultImage（结果参考图）回归：OCR bbox 在存储（未按 EXIF 方向旋转）
/// 像素坐标系，参考图必须统一重编码为无方向 PNG 与之一致；不可解码时不
/// 发布参考图，只保留文本结果，OCR 上传字节不变。
/// </summary>
public sealed class ExifResultImageWorkbenchTests
{
  private const uint StoredWidth = 32;
  private const uint StoredHeight = 16;

  private static readonly byte[] PlainPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

  /// <summary>固定输入桩：全部入口返回同一份真实字节。</summary>
  private sealed class FixedImageInputs(byte[] data, string mediaType) : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => Acquire();

    private Task<RecognitionInput?> Acquire() => Task.FromResult<RecognitionInput?>(new(
      data, mediaType, "photo", "file"));
  }

  /// <summary>提交即完成的识别桩：记录上传内容。</summary>
  private sealed class CompletingInferenceClient : InferenceClientStub
  {
    public List<IReadOnlyList<byte>> UploadedContent { get; } = [];

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      UploadedContent.AddRange(uploads.Values.Select(upload => upload.Content));
      return Task.FromResult(new JobRef
      {
        JobId = "job-exif",
        Items =
        [
          new JobItem
          {
            ItemId = "it-0",
            ClientItemKey = request.Items[0].ClientItemKey,
            Ordinal = 0,
            DisplayName = request.Items[0].DisplayName,
            State = ItemState.Queued,
          },
        ],
      });
    }

    public override Task<JobUpdate> ObserveAsync(
      string jobId,
      int afterSequence,
      CancellationToken cancellationToken) => Task.FromResult(new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Interactive,
          State = JobState.Completed,
        },
        Events = Array.Empty<StageEvent>(),
        Outcomes =
        [
          new ItemOutcome
          {
            ItemId = "it-0",
            State = ItemState.Succeeded,
            Attempt = 1,
            PayloadType = "ocr.v1",
            Payload = new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("exif text"),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  private static DesktopWorkbenchCommandHandler CreateHandler(
    Func<RecognitionViewModel> recognitionFactory,
    string resourceRoot,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    CompletingInferenceClient inference) => new(
    recognitionFactory,
    static () => throw new InvalidOperationException(),
    static () => throw new InvalidOperationException(),
    static () => throw new InvalidOperationException(),
    () => new SettingsViewModel(inference),
    () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
    static () => throw new InvalidOperationException(),
    new DiagnosticsViewModel("test", new PrerequisiteReport([])),
    broker,
    resourceRoot,
    static () => 0,
    annotationStore,
    inferenceAttached: () => false);

  private sealed class StubHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister() { }
  }

  private sealed class StubStartupRegistrar : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }

  private sealed class StateAwaiter<TState> : IDisposable where TState : WorkbenchState
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly Func<TState, bool> predicate;
    private readonly TaskCompletionSource<TState> ready =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateAwaiter(
      DesktopWorkbenchCommandHandler handler,
      Func<TState, bool> predicate)
    {
      this.handler = handler;
      this.predicate = predicate;
      handler.StateChanged += OnStateChanged;
    }

    public Task<TState> Task => ready.Task.WaitAsync(
      TimeSpan.FromSeconds(10),
      TestContext.Current.CancellationToken);

    private void OnStateChanged(WorkbenchState state)
    {
      if (state is TState typed && predicate(typed))
      {
        ready.TrySetResult(typed);
      }
    }

    public void Dispose() => handler.StateChanged -= OnStateChanged;
  }

  /// <summary>四象限纯色（存储网格 TL=红/TR=绿/BL=蓝/BR=黄）的 BGRA 像素。</summary>
  private static byte[] QuadrantPixels(uint width, uint height)
  {
    byte[] pixels = new byte[width * height * 4];
    for (uint y = 0; y < height; y++)
    {
      for (uint x = 0; x < width; x++)
      {
        int offset = (int)((y * width + x) * 4);
        bool right = x >= width / 2;
        bool bottom = y >= height / 2;
        pixels[offset] = (!right && bottom) ? (byte)0xff : (byte)0;
        pixels[offset + 1] = right ? (byte)0xff : (byte)0;
        pixels[offset + 2] = (right == bottom) ? (byte)0xff : (byte)0;
        pixels[offset + 3] = 0xff;
      }
    }
    return pixels;
  }

  /// <summary>真实 JPEG（32×16 四象限），SOI 后插入携带 Orientation=6 的 EXIF APP1 段。</summary>
  private static async Task<byte[]> WriteExifOrientationJpegAsync()
  {
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
      BitmapEncoder.JpegEncoderId, stream);
    encoder.SetPixelData(
      BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
      StoredWidth, StoredHeight, 96, 96, QuadrantPixels(StoredWidth, StoredHeight));
    await encoder.FlushAsync();
    byte[] plain = await ReadStreamBytesAsync(stream);
    byte[] exif =
    [
      0xff, 0xe1, 0x00, 0x22,
      (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
      // 裸 TIFF 头 + IFD0{Orientation=6}
      0x49, 0x49, 0x2a, 0x00, 0x08, 0x00, 0x00, 0x00,
      0x01, 0x00,
      0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00,
    ];
    if (plain.Length < 2 || plain[0] != 0xff || plain[1] != 0xd8)
      throw new InvalidOperationException("JPEG fixture must start with SOI.");
    return [0xff, 0xd8, .. exif, .. plain[2..]];
  }

  private static async Task<byte[]> ReadStreamBytesAsync(InMemoryRandomAccessStream stream)
  {
    using IInputStream read = stream.GetInputStreamAt(0);
    using Stream content = read.AsStreamForRead();
    using var output = new MemoryStream();
    await content.CopyToAsync(output, 81920, TestContext.Current.CancellationToken);
    return output.ToArray();
  }

  private static async Task<byte[]> ReadResourceAsync(
    WorkbenchResourceBroker broker, string url)
  {
    await using WorkbenchResourceResponse response = await broker.OpenAsync(
      new Uri(url), TestContext.Current.CancellationToken);
    using var copy = new MemoryStream();
    await response.Content.CopyToAsync(copy, 81920, TestContext.Current.CancellationToken);
    return copy.ToArray();
  }

  private static async Task<(byte[] Pixels, uint Width, uint Height)> DecodeAsync(
    byte[] data, ExifOrientationMode orientation)
  {
    using var stream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(stream))
    {
      writer.WriteBytes(data);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    stream.Seek(0);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
    byte[] pixels = (await decoder.GetPixelDataAsync(
      BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Straight,
      new BitmapTransform(),
      orientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData();
    return orientation == ExifOrientationMode.RespectExifOrientation
      ? (pixels, decoder.OrientedPixelWidth, decoder.OrientedPixelHeight)
      : (pixels, decoder.PixelWidth, decoder.PixelHeight);
  }

  /// <summary>发布字节必须满足：显示网格（Respect）与 OCR 网格（Ignore）像素一致且为存储尺寸。</summary>
  private static async Task AssertRendersInStoredPixelGridAsync(byte[] reference)
  {
    (byte[] ignore, uint width, uint height) =
      await DecodeAsync(reference, ExifOrientationMode.IgnoreExifOrientation);
    (byte[] respect, uint respectWidth, uint respectHeight) =
      await DecodeAsync(reference, ExifOrientationMode.RespectExifOrientation);
    Assert.Equal(StoredWidth, width);
    Assert.Equal(StoredHeight, height);
    Assert.Equal(ignore, respect);
    // 方向内容自检：存储网格左上象限中心仍是红色（错向/烘焙旋转会落在
    // 其他象限颜色上）。
    int x = (int)(StoredWidth / 4);
    int y = (int)(StoredHeight / 4);
    Assert.InRange(ignore[(int)((y * width + x) * 4)], 0, 48);
    Assert.InRange(ignore[(int)((y * width + x) * 4) + 1], 0, 48);
    Assert.InRange(ignore[(int)((y * width + x) * 4) + 2], 207, 255);
  }

  [Fact]
  public async Task RecognizedExifJpegPublishesResultImageInOcrPixelGrid()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exif-result-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      byte[] jpeg = await WriteExifOrientationJpegAsync();
      // 前提自检：夹具确为带 EXIF 方向的 JPEG（WIC 显示/存储网格不同）。
      (byte[] ignorePixels, uint storedWidth, uint storedHeight) =
        await DecodeAsync(jpeg, ExifOrientationMode.IgnoreExifOrientation);
      (byte[] respectPixels, uint orientedWidth, uint orientedHeight) =
        await DecodeAsync(jpeg, ExifOrientationMode.RespectExifOrientation);
      Assert.Equal(StoredWidth, storedWidth);
      Assert.Equal(StoredHeight, storedHeight);
      Assert.NotEqual(storedWidth, orientedWidth);
      Assert.NotEqual(ignorePixels, respectPixels);

      var inference = new CompletingInferenceClient();
      var inputs = new FixedImageInputs(jpeg, "image/jpeg");
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => recognition, root, broker, annotationStore, inference);

      RecognitionWorkbenchState completed;
      using (var published = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.StatusCode == "recognition.completed" &&
          state.ResultImage is not null))
      {
        await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        completed = await published.Task;
      }

      // 引擎输入与会话编辑基准保持原始 JPEG 字节（OCR 语义与遮罩管线不变）。
      Assert.Equal(jpeg, Assert.Single(inference.UploadedContent));
      Assert.Equal(jpeg, await ReadResourceAsync(broker, completed.Input!.Url));
      await AssertRendersInStoredPixelGridAsync(
        await ReadResourceAsync(broker, completed.ResultImage!.Url));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task PlainPngInputPublishesReferenceImageWithIdenticalPixels()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-png-result-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new FixedImageInputs(PlainPng, "image/png");
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => recognition, root, broker, annotationStore, inference);

      RecognitionWorkbenchState completed;
      using (var published = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.StatusCode == "recognition.completed" &&
          state.ResultImage is not null))
      {
        await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        completed = await published.Task;
      }

      // 普通 PNG：参考图统一重编码为无方向 PNG，像素与输入一致（非原字节断言）。
      byte[] reference = await ReadResourceAsync(broker, completed.ResultImage!.Url);
      (byte[] inputPixels, uint inputWidth, uint inputHeight) =
        await DecodeAsync(PlainPng, ExifOrientationMode.IgnoreExifOrientation);
      (byte[] referencePixels, uint referenceWidth, uint referenceHeight) =
        await DecodeAsync(reference, ExifOrientationMode.IgnoreExifOrientation);
      (byte[] respectPixels, uint respectWidth, uint respectHeight) =
        await DecodeAsync(reference, ExifOrientationMode.RespectExifOrientation);
      Assert.Equal(inputWidth, referenceWidth);
      Assert.Equal(inputHeight, referenceHeight);
      Assert.Equal(inputPixels, referencePixels);
      Assert.Equal(referencePixels, respectPixels);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task UndecodableRecognitionInputKeepsTextResultWithoutResultImage()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-broken-result-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      byte[] broken = [0x09, 0x09, 0x09, 0x09];
      var inference = new CompletingInferenceClient();
      var inputs = new FixedImageInputs(broken, "image/jpeg");
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => recognition, root, broker, annotationStore, inference);

      RecognitionWorkbenchState completed;
      using (var published = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.StatusCode == "recognition.completed" &&
          state.Result is not null))
      {
        await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        completed = await published.Task;
      }

      // 方向不可确认：不发布参考图（null），但文本结果与输入/会话基准照常。
      Assert.Null(completed.ResultImage);
      Assert.NotNull(completed.Result);
      Assert.Equal("exif text", System.Text.Encoding.UTF8.GetString(
        await ReadResourceAsync(broker, completed.Result!.Url)));
      Assert.Equal(broken, Assert.Single(inference.UploadedContent));
      Assert.Equal(broken, await ReadResourceAsync(broker, completed.Input!.Url));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task NormalizeReturnsNullForUndecodableInputAndPropagatesCancellation()
  {
    Assert.Null(await WorkbenchResultReferenceImage.NormalizeAsync(
      [0x09, 0x09, 0x09, 0x09], TestContext.Current.CancellationToken));
    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      WorkbenchResultReferenceImage.NormalizeAsync(PlainPng, cancelled.Token));
  }

  [Fact]
  public async Task PngExifMetadataCannotBypassReferenceNormalization()
  {
    // Pillow 生成的 2×1 红蓝 PNG，eXIf Orientation=6。
    byte[] oriented = Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAGmVYSWZNTQAqAAAACAABARIAAwAAAAEABgAAAAAAANZnS2kAAAAPSURBVHicY/jPwMDA8B8ABwAB/34IsdAAAAAASUVORK5CYII=");
    Assert.True(oriented.AsSpan().IndexOf("eXIf"u8) >= 0);
    var normalized = await WorkbenchResultReferenceImage.NormalizeAsync(
      oriented, TestContext.Current.CancellationToken);
    Assert.NotNull(normalized);
    Assert.Equal(-1, normalized.Value.Data.AsSpan().IndexOf("eXIf"u8));
    var input = await DecodeAsync(oriented, ExifOrientationMode.IgnoreExifOrientation);
    var output = await DecodeAsync(normalized.Value.Data, ExifOrientationMode.RespectExifOrientation);
    Assert.Equal(input.Width, output.Width);
    Assert.Equal(input.Height, output.Height);
    Assert.Equal(input.Pixels, output.Pixels);
  }

  [Fact]
  public async Task SharedEncoderKeepsPngPassthroughByDefault()
  {
    // 二维码既有调用（默认）：PNG→PNG 原字节直通语义不变；重编码选项
    // （生产消费者：结果参考图归一化）产出像素一致的独立 PNG。
    byte[] defaultResult = await QrCodeSavePlatform.EncodeImageAsync(
      PlainPng, BitmapEncoder.PngEncoderId, TestContext.Current.CancellationToken);
    Assert.Equal(PlainPng, defaultResult);
    byte[] bypassed = await QrCodeSavePlatform.EncodeImageAsync(
      PlainPng, BitmapEncoder.PngEncoderId, TestContext.Current.CancellationToken,
      bypassPassthrough: true);
    (byte[] inputPixels, uint width, uint height) =
      await DecodeAsync(PlainPng, ExifOrientationMode.IgnoreExifOrientation);
    (byte[] bypassPixels, uint bypassWidth, uint bypassHeight) =
      await DecodeAsync(bypassed, ExifOrientationMode.IgnoreExifOrientation);
    Assert.Equal(width, bypassWidth);
    Assert.Equal(height, bypassHeight);
    Assert.Equal(inputPixels, bypassPixels);
  }
}
