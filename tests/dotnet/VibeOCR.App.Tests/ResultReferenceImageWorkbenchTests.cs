using System.Text.Json;
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
/// 结果参考图（reference_image）与结果资源生命周期回归：Runtime 预处理
/// 参考图经资产通道原样发布（不二次归一/猜旋转）；显式 unavailable、
/// 字段存在但 null/畸形、缺省且 preproc_angle 非 0、content_list、无行框
/// 均不发布参考图且不回退原图；连续行编辑不泄漏 input/result/structured，
/// 失败/过期不撤销旧资源；合法空模式文本可复制。
/// </summary>
public sealed class ResultReferenceImageWorkbenchTests
{
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

  private sealed class RecordingClipboard : IStructuredClipboardPlatform
  {
    public string? Text { get; private set; }

    public Task WriteTableAsync(string tsv, string html, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("no tables here");

    public Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
      Text = text;
      return Task.CompletedTask;
    }
  }

  /// <summary>可配置识别桩：reference_image 存在性/形状、preproc_angle、content_list、资产字节。</summary>
  private sealed class ReferenceImageInferenceClient : InferenceClientStub
  {
    public bool IncludeReferenceImageKey;
    public JsonElement ReferenceImage = JsonSerializer.SerializeToElement<string?>(null);
    public int? PreprocAngle;
    public bool IncludeContentList;
    public bool IncludeBbox = true;
    public string RawText = "行一\n行二";
    public byte[]? AssetBytes;
    public Exception? AssetError { get; set; }
    public int FetchCalls;
    public List<IReadOnlyList<byte>> UploadedContent { get; } = [];

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      UploadedContent.AddRange(uploads.Values.Select(upload => upload.Content));
      return Task.FromResult(new JobRef
      {
        JobId = "job-reference",
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
      string jobId, int afterSequence, CancellationToken cancellationToken) => Task.FromResult(new JobUpdate
    {
      Snapshot = new JobSnapshot
      {
        JobId = jobId, Kind = JobKind.Recognition, Priority = JobPriority.Interactive,
        State = JobState.Completed,
      },
      Events = Array.Empty<StageEvent>(),
      Outcomes =
      [
        new ItemOutcome
        {
          ItemId = "it-0", State = ItemState.Succeeded, Attempt = 1, PayloadType = "ocr.v1",
          Payload = Payload(),
        },
      ],
      ThroughSequence = afterSequence,
    });

    private Dictionary<string, JsonElement> Payload()
    {
      Dictionary<string, JsonElement> payload = new()
      {
        ["raw_text"] = JsonSerializer.SerializeToElement(RawText),
        ["markdown_text"] = JsonSerializer.SerializeToElement(RawText),
        ["text_blocks"] = JsonSerializer.SerializeToElement(new object[]
        {
          new { text = "行一", score = 0.9, bbox = new[] { 10, 10, 200, 30 } },
          new { text = "行二", score = 0.5, bbox = new[] { 10, 34, 200, 54 } },
        }),
      };
      if (!IncludeBbox)
        payload["text_blocks"] = JsonSerializer.SerializeToElement(new[] { new { text = "行一", score = 0.9 } });
      if (IncludeContentList)
        payload["content_list"] = JsonSerializer.SerializeToElement(new object[] { new { type = "table" } });
      if (PreprocAngle is { } angle) payload["preproc_angle"] = JsonSerializer.SerializeToElement(angle);
      if (IncludeReferenceImageKey) payload["reference_image"] = ReferenceImage;
      return payload;
    }

    public override Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken) =>
      Task.FromResult(new ExportResult(request.OutputPath, 1));

    public override Task<byte[]> FetchResultAssetAsync(
      string jobId, string itemId, string assetId, CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref FetchCalls);
      return AssetError is not null
        ? Task.FromException<byte[]>(AssetError)
        : Task.FromResult(AssetBytes ?? []);
    }
  }

  private static async Task<byte[]> SolidPngAsync(uint width, uint height, byte b, byte g, byte r)
  {
    byte[] pixels = new byte[width * height * 4];
    for (int index = 0; index < pixels.Length; index += 4)
    {
      pixels[index] = b;
      pixels[index + 1] = g;
      pixels[index + 2] = r;
      pixels[index + 3] = 0xff;
    }
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
    await encoder.FlushAsync();
    using IInputStream read = stream.GetInputStreamAt(0);
    using Stream content = read.AsStreamForRead();
    using var output = new MemoryStream();
    await content.CopyToAsync(output, 81920, TestContext.Current.CancellationToken);
    return output.ToArray();
  }

  private static async Task<(byte[] Pixels, uint Width, uint Height)> DecodeAsync(byte[] data)
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
    return ((await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
      new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData(),
      decoder.PixelWidth, decoder.PixelHeight);
  }

  private static async Task<byte[]> ReadResourceAsync(WorkbenchResourceBroker broker, string url)
  {
    await using WorkbenchResourceResponse response = await broker.OpenAsync(
      new Uri(url), TestContext.Current.CancellationToken);
    using var copy = new MemoryStream();
    await response.Content.CopyToAsync(copy, 81920, TestContext.Current.CancellationToken);
    return copy.ToArray();
  }

  private static DesktopWorkbenchCommandHandler CreateHandler(
    Func<RecognitionViewModel> recognitionFactory, string root, WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotations, ReferenceImageInferenceClient inference,
    IStructuredClipboardPlatform clipboard, string exportRoot) => new(
    recognitionFactory,
    static () => throw new InvalidOperationException(),
    static () => throw new InvalidOperationException(),
    static () => throw new InvalidOperationException(),
    () => new SettingsViewModel(inference),
    () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
    static () => throw new InvalidOperationException(),
    new DiagnosticsViewModel("test", new PrerequisiteReport([])),
    broker, root, static () => 0, annotations,
    structuredClipboard: clipboard,
    resultActionPlatform: new RootPathExportPlatform(exportRoot),
    inferenceAttached: () => false);

  private sealed class StateAwaiter : IDisposable
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly TaskCompletionSource<RecognitionWorkbenchState> ready =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateAwaiter(DesktopWorkbenchCommandHandler handler, Func<RecognitionWorkbenchState, bool> predicate)
    {
      this.handler = handler;
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState typed && predicate(typed)) ready.TrySetResult(typed);
      };
    }

    public Task<RecognitionWorkbenchState> Task => ready.Task.WaitAsync(
      TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    public void Dispose() { }
  }

  private static async Task<(DesktopWorkbenchCommandHandler Handler, WorkbenchResourceBroker Broker, string Root,
    ReferenceImageInferenceClient Inference, RecognitionWorkbenchState Completed)> RecognizeAsync(
    ReferenceImageInferenceClient inference, byte[] inputPng)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-ref-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var inputs = new FixedInput(inputPng);
    var recognition = new RecognitionViewModel(inference, inputs);
    var broker = new WorkbenchResourceBroker(root);
    var annotations = new WorkbenchAnnotationStore(root);
    DesktopWorkbenchCommandHandler handler = CreateHandler(
      () => recognition, root, broker, annotations, inference, new RecordingClipboard(), root);
    RecognitionWorkbenchState completed;
    using var awaiter = new StateAwaiter(handler,
      state => !state.IsBusy && state.StatusCode == "recognition.completed" && state.Result is not null);
    await handler.ExecuteAsync(new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
    completed = await awaiter.Task;
    return (handler, broker, root, inference, completed);
  }

  private sealed class FixedInput(byte[] png) : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) => Acquire();
    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) => Acquire();
    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) => Acquire();
    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken) => Acquire();
    private Task<RecognitionInput?> Acquire() => Task.FromResult<RecognitionInput?>(
      new RecognitionInput(png, "image/png", "photo.png", "file"));
  }


  /// <summary>导出桩：返回 root 内授权路径，不弹真实保存选择器。</summary>
  private sealed class RootPathExportPlatform(string root) : IResultActionPlatform
  {
    public Task WriteClipboardAsync(RecognitionResultContent result, ResultCopyFormat format, CancellationToken cancellationToken) =>
      throw new NotSupportedException();
    public Task<string?> PickExportPathAsync(ResultExportFormat format, CancellationToken cancellationToken) =>
      Task.FromResult<string?>(Path.Combine(root, "export.txt"));
    public Task<bool> ConfirmOverwriteAsync(string target, CancellationToken cancellationToken) => Task.FromResult(true);
  }
  [Fact]
  public async Task PreprocessedReferenceImageAssetIsPublishedAsIs()
  {
    byte[] input = await SolidPngAsync(32, 16, 0x30, 0x60, 0x90);
    byte[] asset = await SolidPngAsync(16, 32, 0x10, 0x20, 0x40);
    var inference = new ReferenceImageInferenceClient
    {
      IncludeReferenceImageKey = true,
      PreprocAngle = 90,
      AssetBytes = asset,
      ReferenceImage = JsonSerializer.SerializeToElement(new
      {
        job_id = "job-reference", item_id = "it-0", asset_id = "asset-preprocessed",
        media_type = "image/png", width = 16, height = 32, size_bytes = asset.Length,
      }),
    };
    var (handler, broker, root, client, completed) = await RecognizeAsync(inference, input);
    await using (handler)
    {
      try
      {
        Assert.NotNull(completed.ResultImage);
        (byte[] pixels, uint width, uint height) = await DecodeAsync(
          await ReadResourceAsync(broker, completed.ResultImage!.Url));
        // 发布的是 Runtime 预处理资产（旋转后 16×32、资产像素），非原图。
        Assert.Equal(16u, width);
        Assert.Equal(32u, height);
        Assert.Equal((await DecodeAsync(asset)).Pixels, pixels);
        // OCR 上传仍是原始输入字节。
        Assert.Equal(input, Assert.Single(client.UploadedContent));
      }
      finally
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Theory]
  [InlineData(true, "unavailable")]
  [InlineData(true, "null")]
  [InlineData(true, "malformed")]
  [InlineData(true, "asset-content-list")]
  [InlineData(false, "angle")]
  [InlineData(false, "no-bbox")]
  [InlineData(true, "asset-no-bbox")]
  [InlineData(true, "io")]
  [InlineData(true, "timeout")]
  public async Task UnsafeReferenceContextSuppressesReferenceImage(bool includeKey, string mode)
  {
    byte[] input = await SolidPngAsync(32, 16, 0x30, 0x60, 0x90);
    var inference = new ReferenceImageInferenceClient
    {
      IncludeReferenceImageKey = includeKey,
      IncludeBbox = !mode.Contains("no-bbox"),
      AssetError = mode == "io" ? new IOException("asset unavailable") :
        mode == "timeout" ? new OperationCanceledException("asset timeout", new TimeoutException()) : null,
      PreprocAngle = mode.Contains("angle") ? 90 : null,
      ReferenceImage = mode switch
      {
        "unavailable" => JsonSerializer.SerializeToElement(new { available = false, reason = "预处理失败" }),
        "null" => JsonSerializer.SerializeToElement<string?>(null),
        "malformed" => JsonSerializer.SerializeToElement("broken"),
        "asset-content-list" or "asset-no-bbox" or "io" or "timeout" => JsonSerializer.SerializeToElement(new
        {
          job_id = "job-reference", item_id = "it-0", asset_id = "asset-preprocessed",
        }),
        _ => JsonSerializer.SerializeToElement<string?>(null),
      },
    };
    if (mode is "asset-content-list") inference.IncludeContentList = true;
    var (handler, broker, root, client, completed) = await RecognizeAsync(inference, input);
    await using (handler)
    {
      try
      {
        // 显式 unavailable / 存在但 null 或畸形 / content_list 无行框投影
        //（含显式资产也不取）/ 缺省且 angle 非 0：不发布参考图也不回退
        // 原图，文本保留。
        Assert.Null(completed.ResultImage);
        Assert.NotNull(completed.Result);
        Assert.NotNull(completed.StructuredResult);
        // 无行框不取图；读取失败或超时只降级参考图。
        Assert.Equal(mode is "io" or "timeout" ? 1 : 0, client.FetchCalls);
      }
      finally
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public async Task LineEditKeepsReferenceImageAndAvoidsRefetching()
  {
    byte[] input = await SolidPngAsync(32, 16, 0x30, 0x60, 0x90);
    byte[] asset = await SolidPngAsync(16, 32, 0x10, 0x20, 0x40);
    var inference = new ReferenceImageInferenceClient
    {
      IncludeReferenceImageKey = true,
      PreprocAngle = 90,
      AssetBytes = asset,
      ReferenceImage = JsonSerializer.SerializeToElement(new
      {
        job_id = "job-reference", item_id = "it-0", asset_id = "asset-preprocessed",
        media_type = "image/png", width = 16, height = 32, size_bytes = asset.Length,
      }),
    };
    var (handler, broker, root, client, completed) = await RecognizeAsync(inference, input);
    await using (handler)
    {
      try
      {
        string referenceUrl = completed.ResultImage!.Url;
        // 行编辑不改变几何：参考图 URL 承接不变，后续 copy/export 不重新取资产。
        WorkbenchCommandOutcome edit = await handler.ExecuteAsync(
          new UpdateRecognitionLineCommand(completed.StructuredResult!.Url, 0, "行一", "编辑后"),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState edited = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(edit.States));
        Assert.Equal(referenceUrl, edited.ResultImage!.Url);
        WorkbenchCommandOutcome copy = await handler.ExecuteAsync(
          new CopyRecognitionLinesCommand(edited.StructuredResult!.Url, "raw"), TestContext.Current.CancellationToken);
        Assert.Null(copy.Error);
        WorkbenchCommandOutcome export = await handler.ExecuteAsync(
          new ExportRecognitionResultCommand("text"), TestContext.Current.CancellationToken);
        RecognitionWorkbenchState exported = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(export.States));
        Assert.Equal(referenceUrl, exported.ResultImage!.Url);
        // 预处理资产只在识别发布时拉取一次。
        Assert.Equal(1, client.FetchCalls);
      }
      finally
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public async Task MissingReferenceImageWithAngle0PublishesNormalizedOriginal()
  {
    byte[] input = await SolidPngAsync(32, 16, 0x30, 0x60, 0x90);
    var inference = new ReferenceImageInferenceClient();
    var (handler, broker, root, client, completed) = await RecognizeAsync(inference, input);
    await using (handler)
    {
      try
      {
        // 字段缺省且未预处理（angle null）：沿用 EXIF 规范化原图路径。
        Assert.NotNull(completed.ResultImage);
        (byte[] pixels, uint width, uint height) = await DecodeAsync(
          await ReadResourceAsync(broker, completed.ResultImage!.Url));
        Assert.Equal(32u, width);
        Assert.Equal(16u, height);
        Assert.Equal((await DecodeAsync(input)).Pixels, pixels);
      }
      finally
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public async Task ConsecutiveLineEditsStabilizeInputAndRevokeSupersededResults()
  {
    byte[] input = await SolidPngAsync(32, 16, 0x30, 0x60, 0x90);
    var inference = new ReferenceImageInferenceClient();
    var (handler, broker, root, client, first) = await RecognizeAsync(inference, input);
    await using (handler)
    {
      try
      {
        string inputUrl = first.Input!.Url;
        string firstStructured = first.StructuredResult!.Url;
        string firstResult = first.Result!.Url;

        // 失败编辑（旧文本不符）：旧权威资源不被撤销，仍可读取。
        WorkbenchCommandOutcome failed = await handler.ExecuteAsync(
          new UpdateRecognitionLineCommand(firstStructured, 0, "错误旧文本", "新文本"),
          TestContext.Current.CancellationToken);
        Assert.NotNull(failed.Error);
        await ReadResourceAsync(broker, firstStructured);
        await ReadResourceAsync(broker, firstResult);

        // 两次成功编辑：会话输入基准 URL 稳定，被取代的结构化/文本资源撤销。
        WorkbenchCommandOutcome editOne = await handler.ExecuteAsync(
          new UpdateRecognitionLineCommand(firstStructured, 0, "行一", "编辑一"),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState stateOne = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(editOne.States));
        Assert.Equal(inputUrl, stateOne.Input!.Url);
        WorkbenchCommandOutcome editTwo = await handler.ExecuteAsync(
          new UpdateRecognitionLineCommand(stateOne.StructuredResult!.Url, 0, "编辑一", "编辑二"),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState stateTwo = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(editTwo.States));
        Assert.Equal(inputUrl, stateTwo.Input!.Url);
        Assert.NotEqual(stateOne.StructuredResult!.Url, stateTwo.StructuredResult!.Url);
        await Assert.ThrowsAsync<WorkbenchResourceAccessException>(() =>
          ReadResourceAsync(broker, firstStructured));
        await Assert.ThrowsAsync<WorkbenchResourceAccessException>(() =>
          ReadResourceAsync(broker, stateOne.StructuredResult!.Url));
        await ReadResourceAsync(broker, stateTwo.StructuredResult!.Url);
        // 输入从未被撤销且未被重新发布为不同 URL。
        await ReadResourceAsync(broker, inputUrl);
      }
      finally
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  private sealed class SwitchableInput(byte[] png) : IInputService
  {
    public byte[] Current { get; set; } = png;
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) => Acquire();
    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) => Acquire();
    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) => Acquire();
    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken) => Acquire();
    private Task<RecognitionInput?> Acquire() => Task.FromResult<RecognitionInput?>(
      new RecognitionInput(Current, "image/png", "photo.png", "file"));
  }

  [Fact]
  public async Task ConsecutiveInputSwitchRevokesOldInputWithoutFileGrowth()
  {
    var inference = new ReferenceImageInferenceClient();
    var inputs = new SwitchableInput(await SolidPngAsync(32, 16, 0x30, 0x60, 0x90));
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-ref-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var recognition = new RecognitionViewModel(inference, inputs);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var handler = CreateHandler(
      () => recognition, root, broker, annotations, inference, new RecordingClipboard(), root);
    try
    {
      RecognitionWorkbenchState first;
      using (var awaiter = new StateAwaiter(handler,
        state => !state.IsBusy && state.StatusCode == "recognition.completed" && state.Result is not null))
      {
        await handler.ExecuteAsync(new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
        first = await awaiter.Task;
      }
      string firstInput = first.Input!.Url;
      string sessionRoot = Path.Combine(root, "session");
      int filesAfterFirst = Directory.GetFiles(sessionRoot).Length;

      inputs.Current = await SolidPngAsync(16, 8, 0x50, 0x70, 0xa0);
      RecognitionWorkbenchState second;
      using (var awaiter = new StateAwaiter(handler,
        state => !state.IsBusy && state.StatusCode == "recognition.completed" &&
          state.Result is not null && state.Input!.Url != firstInput))
      {
        await handler.ExecuteAsync(new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
        second = await awaiter.Task;
      }

      // 换输入：旧输入 URL 被撤销（丢引用≠清理，ReleaseResource 才删盘）；
      // session 文件数不随连续识别增长。
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(
        () => ReadResourceAsync(broker, firstInput));
      await ReadResourceAsync(broker, second.Input!.Url);
      Assert.Equal(filesAfterFirst, Directory.GetFiles(sessionRoot).Length);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task EmptyRawModeCopiesEmptyString()
  {
    byte[] input = await SolidPngAsync(32, 16, 0x30, 0x60, 0x90);
    var inference = new ReferenceImageInferenceClient { RawText = "" };
    var clipboard = new RecordingClipboard();
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-ref-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var inputs = new FixedInput(input);
    var recognition = new RecognitionViewModel(inference, inputs);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var handler = CreateHandler(
      () => recognition, root, broker, annotations, inference, clipboard, root);
    try
    {
      RecognitionWorkbenchState completed;
      using (var awaiter = new StateAwaiter(handler,
        state => !state.IsBusy && state.StatusCode == "recognition.completed" && state.StructuredResult is not null))
      {
        await handler.ExecuteAsync(new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
        completed = await awaiter.Task;
      }
      // 模式存在但值为空字符串：写入空字符串而不是拒绝；缺失模式仍拒绝。
      WorkbenchCommandOutcome copy = await handler.ExecuteAsync(
        new CopyRecognitionLinesCommand(completed.StructuredResult!.Url, "raw"),
        TestContext.Current.CancellationToken);
      Assert.Null(copy.Error);
      Assert.Equal(string.Empty, clipboard.Text);
      WorkbenchCommandOutcome missing = await handler.ExecuteAsync(
        new CopyRecognitionLinesCommand(completed.StructuredResult!.Url, "nosuchmode"),
        TestContext.Current.CancellationToken);
      Assert.NotNull(missing.Error);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
