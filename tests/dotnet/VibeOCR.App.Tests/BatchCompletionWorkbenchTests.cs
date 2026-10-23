using VibeOCR.App.Features.Configuration;
using VibeOCR.App.Features.QrCode;
using System.Text.Json;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Web;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class BatchCompletionWorkbenchTests
{
  [Fact]
  public async Task RapidTableDeviceOptionSubmitsOnBatchWire()
  {
    // 批量入口与单次/截图共享同一 device 冻结链：显式 rapid_table 任务携带
    // options.device=cuda，不静默改 CPU。
    var client = new BatchRunClient();
    await using var harness = await CompletionHarness.CreateAsync(client);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.Null((await harness.Handler.ExecuteAsync(new RefreshRuntimeCommand(),
      cancellationToken)).Error);
    BatchWorkbenchState? deviceState = null;
    harness.Handler.StateChanged += state =>
    {
      if (state is BatchWorkbenchState batchState) deviceState = batchState;
    };
    Assert.Null((await harness.Handler.ExecuteAsync(
      new SetModeDeviceCommand("rapid_table", "cuda"), cancellationToken)).Error);
    Assert.Equal("cuda", deviceState!.Engines!.Single(mode => mode.Engine == "rapid_table")
      .Options!["device"].GetString());
    Assert.Null((await harness.Handler.ExecuteAsync(
      new SetBatchTaskEngineCommand("rapid_table"), cancellationToken)).Error);
    harness.Batch.AddFiles([TempPng(harness.Root, "table")]);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    await client.WaitSubmittedAsync(cancellationToken);
    Assert.Equal("RAPID_TABLE", client.LastRequest!.Pipeline.PipelineId);
    Assert.Null(client.LastRequest.Pipeline.Engine);
    Assert.Equal("cuda", client.LastRequest.Pipeline.Options["device"].GetString());
  }

  [Fact]
  public async Task SuccessfulBatchRunsPublishExactlyOneTerminalEachWithoutMailboxResync()
  {
    var client = new BatchRunClient();
    await using var harness = await CompletionHarness.CreateAsync(client);
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    WorkbenchCommandReceipt firstAdded = await harness.AddDroppedFileAsync("first", cancellationToken);
    Assert.True(firstAdded.Ok);
    WorkbenchCommandReceipt firstStarted = await harness.StartAsync(cancellationToken);
    Assert.True(firstStarted.Ok);
    BatchWorkbenchState firstTerminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 成功终态保留：队列 1 项全部完成，且同一操作只有一次 terminal 发布。
    Assert.False(firstTerminal.IsRunning);
    Assert.Equal(1, firstTerminal.ItemCount);
    Assert.Equal(1, firstTerminal.CompletedCount);
    Assert.Equal(0, firstTerminal.FailedCount);
    Assert.Equal("batch.item.completed", Assert.Single(firstTerminal.Items!).StatusCode);
    Assert.Equal(1, client.Submissions);
    Assert.Equal(1, counter.Count);
    // 订阅端：第一次运行只产出一条 batch 终态，未触发 mailbox Resync。
    IReadOnlyList<WorkbenchStateEnvelope> firstDrained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(firstDrained));
    Assert.DoesNotContain(firstDrained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);

    WorkbenchCommandReceipt secondAdded = await harness.AddDroppedFileAsync("second", cancellationToken);
    Assert.True(secondAdded.Ok);
    WorkbenchCommandReceipt secondStarted = await harness.StartAsync(cancellationToken);
    Assert.True(secondStarted.Ok);
    BatchWorkbenchState secondTerminal = await counter.NextTerminal(cancellationToken);
    Assert.False(secondTerminal.IsRunning);
    Assert.Equal(2, secondTerminal.ItemCount);
    Assert.Equal(2, secondTerminal.CompletedCount);
    Assert.Equal(2, client.Submissions);
    Assert.Equal(2, counter.Count);

    // 第二次运行同样只产出一条终态；mailbox 持续消费下没有重投影。
    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(2, cancellationToken);
    Assert.Equal(2, TerminalBatchStates(drained).Count);
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task FailedBatchOutcomePublishesExactlyOneTerminalWithoutMailboxResync()
  {
    var client = new BatchRunClient { OutcomeState = ItemState.Failed };
    await using var harness = await CompletionHarness.CreateAsync(client);
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.True((await harness.AddDroppedFileAsync("failed", cancellationToken)).Ok);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    BatchWorkbenchState terminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 失败终态保留：单文件失败计数进入终态，不重复发布。
    Assert.False(terminal.IsRunning);
    Assert.Equal(1, terminal.ItemCount);
    Assert.Equal(0, terminal.CompletedCount);
    Assert.Equal(1, terminal.FailedCount);
    Assert.Equal("batch.item.failed", Assert.Single(terminal.Items!).StatusCode);
    Assert.Equal(1, client.Submissions);
    Assert.Equal(1, counter.Count);

    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(drained));
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task UserCancelledBatchPublishesExactlyOneTerminalWithoutMailboxResync()
  {
    var client = new BatchRunClient
    {
      ObserveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    await using var harness = await CompletionHarness.CreateAsync(client);
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.True((await harness.AddDroppedFileAsync("cancelled", cancellationToken)).Ok);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    await client.WaitSubmittedAsync(cancellationToken);
    WorkbenchCommandReceipt cancelled = await harness.Application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new CancelBatchCommand()),
      cancellationToken);
    Assert.True(cancelled.Ok);
    BatchWorkbenchState terminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 取消终态保留：队列原样保留、条目为已取消，且只有一次 terminal。
    Assert.False(terminal.IsRunning);
    Assert.Equal(1, terminal.ItemCount);
    Assert.Equal("batch.item.cancelled", Assert.Single(terminal.Items!).StatusCode);
    Assert.Equal(0, terminal.CompletedCount);
    Assert.Equal(0, terminal.FailedCount);
    Assert.Equal(1, client.Submissions);
    Assert.Equal(1, counter.Count);

    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(drained));
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task BatchInputReadFailurePublishesExactlyOneTerminalWithoutMailboxResync()
  {
    var client = new BatchRunClient();
    await using var harness = await CompletionHarness.CreateAsync(
      client, readFailure: new InvalidOperationException("批量输入读取失败"));
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.True((await harness.AddDroppedFileAsync("broken", cancellationToken)).Ok);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    BatchWorkbenchState terminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 后台提交异常不吞终态：仍发布唯一 terminal（不重复、不 resync）。
    Assert.False(terminal.IsRunning);
    Assert.Equal(1, terminal.ItemCount);
    Assert.Equal(0, client.Submissions);
    Assert.Equal(1, counter.Count);

    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(drained));
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task ImageConversionQueueIsIndependentAndRejectsDocumentsAtomically()
  {
    await using var harness = await CompletionHarness.CreateAsync(new BatchRunClient(), outputPreset: true);
    CancellationToken token = TestContext.Current.CancellationToken;
    string image = Path.Combine(harness.Root, "convert.png");
    File.WriteAllBytes(image, VibpProjectFileTests.BuildPng(4, 3));
    harness.Batch.AddFiles([Path.Combine(harness.Root, "recognize.png")]);
    WorkbenchCommandOutcome added = await harness.Handler.ExecuteAsync(new AddDroppedImageConversionFilesCommand([image]), token);
    Assert.Null(added.Error);
    Assert.Equal("imageConversion", Assert.Single(added.States).Scope);
    Assert.Single(harness.Batch.Items);
    Assert.Single(harness.Handler.ImageConversion.Items);
    WorkbenchCommandOutcome rejected = await harness.Handler.ExecuteAsync(new AddDroppedImageConversionFilesCommand([Path.Combine(harness.Root, "other.png"), Path.Combine(harness.Root, "document.pdf")]), token);
    Assert.NotNull(rejected.Error);
    Assert.Single(harness.Handler.ImageConversion.Items);
    await harness.Handler.ExecuteAsync(new ClearBatchCommand(), token);
    Assert.Single(harness.Handler.ImageConversion.Items);
    Assert.Null((await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(0), token)).Error);
    await harness.Handler.ExecuteAsync(new ClearImageConversionCommand(), token);
    Assert.Empty(harness.Handler.ImageConversion.Items);
    Assert.Empty(harness.Batch.Items);
  }

  [Fact]
  public async Task ImagePresetExportConsumesEachLeaseOncePreservesSourceAndFrozenQueueOrder()
  {
    await using var harness = await CompletionHarness.CreateAsync(new BatchRunClient(), outputPreset: true);
    CancellationToken token = TestContext.Current.CancellationToken;
    byte[] first = VibpProjectFileTests.BuildPng(4, 3), second = VibpProjectFileTests.BuildPng(6, 2);
    string firstPath = Path.Combine(harness.Root, "original.png");
    string subdirectory = Directory.CreateDirectory(Path.Combine(harness.Root, "second")).FullName;
    string secondPath = Path.Combine(subdirectory, "original.png");
    File.WriteAllBytes(firstPath, first); File.WriteAllBytes(secondPath, second);
    await harness.Handler.ExecuteAsync(new AddDroppedImageConversionFilesCommand([firstPath, secondPath]), token);
    Guid firstId = harness.Handler.ImageConversion.Items[0].Id, secondId = harness.Handler.ImageConversion.Items[1].Id;
    WorkbenchCommandOutcome prepared = await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(0), token);
    Assert.Null(prepared.Error);
    ImageConversionWorkbenchState projected = Assert.IsType<ImageConversionWorkbenchState>(Assert.Single(prepared.States));
    Assert.Equal(firstId, projected.ExportImageId);
    Assert.NotNull(projected.ExportImage);
    await harness.Handler.ExecuteAsync(new MoveImageConversionItemCommand(firstId, 1), token);
    WorkbenchAnnotationLease lease = await harness.StageImageAsync(first, token);
    WorkbenchCommandOutcome exported = await harness.Handler.ExecuteAsync(new ExportConvertedImageCommand(firstId, lease.ResourceUri.AbsoluteUri, "default", new(new(NameTemplate: "{name}"), harness.Root)), token);
    Assert.Null(exported.Error);
    Assert.Equal(first, File.ReadAllBytes(firstPath));
    Assert.Equal(first, File.ReadAllBytes(Path.Combine(harness.Root, "original_1.png")));
    Assert.Throws<WorkbenchAnnotationAccessException>(() => harness.TakeAnnotation(lease));
    // 质量变化保持格式和尺寸兼容，仍须拒绝旧快照，不能按新设置混合写盘。
    Assert.Null((await harness.Handler.ExecuteAsync(new SetEditorPreferencesCommand(
      EditorPreferences.Default with { Output = new(NameTemplate: "{name}", Quality: 0.7) }), token)).Error);
    Assert.Null((await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(1), token)).Error);
    WorkbenchAnnotationLease staleQuality = await harness.StageImageAsync(second, token);
    Assert.NotNull((await harness.Handler.ExecuteAsync(new ExportConvertedImageCommand(secondId, staleQuality.ResourceUri.AbsoluteUri, "default",
      new(new(NameTemplate: "{name}"), harness.Root)), token)).Error);
    Assert.Throws<WorkbenchAnnotationAccessException>(() => harness.TakeAnnotation(staleQuality));
    Assert.False(File.Exists(Path.Combine(harness.Root, "original_2.png")));
    prepared = await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(1), token);
    Assert.Null(prepared.Error);
    projected = Assert.IsType<ImageConversionWorkbenchState>(Assert.Single(prepared.States));
    Assert.Equal(secondId, projected.ExportImageId);
    lease = await harness.StageImageAsync(second, token);
    WorkbenchCommandOutcome failed = await harness.Handler.ExecuteAsync(new ExportConvertedImageCommand(secondId, lease.ResourceUri.AbsoluteUri, "missing-preset"), token);
    Assert.NotNull(failed.Error);
    Assert.Throws<WorkbenchAnnotationAccessException>(() => harness.TakeAnnotation(lease));
    Assert.Equal(second, File.ReadAllBytes(secondPath));
    Assert.Equal(first, File.ReadAllBytes(Path.Combine(harness.Root, "original_1.png")));
    Assert.False(File.Exists(Path.Combine(harness.Root, "original_2.png")));
    WorkbenchCommandOutcome configured = await harness.Handler.ExecuteAsync(new SetEditorPreferencesCommand(
      EditorPreferences.Default with { Output = new(NameTemplate: "{name}", Width: 2, Height: 2) }), token);
    Assert.Null(configured.Error);
    Assert.Null((await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(1), token)).Error);
    lease = await harness.StageImageAsync(second, token);
    failed = await harness.Handler.ExecuteAsync(new ExportConvertedImageCommand(secondId, lease.ResourceUri.AbsoluteUri, "default", new(new(NameTemplate: "{name}"), harness.Root)), token);
    Assert.NotNull(failed.Error);
    Assert.Throws<WorkbenchAnnotationAccessException>(() => harness.TakeAnnotation(lease));
    Assert.False(File.Exists(Path.Combine(harness.Root, "original_2.png")));
    Assert.Equal(2, harness.Handler.ImageConversion.Items.Count);
  }

  // Pillow-generated two-frame/page samples: first red, then blue; original containers must remain intact.
  [Theory]
  [InlineData("gif", "R0lGODlhAgABAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAgABAAAIBQABAAgIACH5BAEKAAEALAAAAAACAAEAgQAA/wAAAAAAAAAAAAgFAAEACAgAOw==")]
  [InlineData("tif", "SUkqAAgAAAALAAABBAABAAAAAgAAAAEBBAABAAAAAQAAAAIBAwAEAAAAkgAAAAMBAwABAAAAAQAAAAYBAwABAAAAAgAAABEBBAABAAAAmgAAABUBAwABAAAABAAAABYBBAABAAAAAQAAABcBBAABAAAACAAAABwBAwABAAAAAQAAAFIBAwABAAAAAgAAALgAAAAIAAgACAAIAP8AAP//AAD/AAAAAAAAAAAAAAAAAABJSSoACAAAAAsAAAEEAAEAAAACAAAAAQEEAAEAAAABAAAAAgEDAAQAAABCAQAAAwEDAAEAAAABAAAABgEDAAEAAAACAAAAEQEEAAEAAABKAQAAFQEDAAEAAAAEAAAAFgEEAAEAAAABAAAAFwEEAAEAAAAIAAAAHAEDAAEAAAABAAAAUgEDAAEAAAACAAAAAAAAAAgACAAIAAgAAAD//wAA//8AAAAAAAAAAAAAAAAAAA==")]
  [InlineData("tiff", "SUkqAAgAAAALAAABBAABAAAAAgAAAAEBBAABAAAAAQAAAAIBAwAEAAAAkgAAAAMBAwABAAAAAQAAAAYBAwABAAAAAgAAABEBBAABAAAAmgAAABUBAwABAAAABAAAABYBBAABAAAAAQAAABcBBAABAAAACAAAABwBAwABAAAAAQAAAFIBAwABAAAAAgAAALgAAAAIAAgACAAIAP8AAP//AAD/AAAAAAAAAAAAAAAAAABJSSoACAAAAAsAAAEEAAEAAAACAAAAAQEEAAEAAAABAAAAAgEDAAQAAABCAQAAAwEDAAEAAAABAAAABgEDAAEAAAACAAAAEQEEAAEAAABKAQAAFQEDAAEAAAAEAAAAFgEEAAEAAAABAAAAFwEEAAEAAAAIAAAAHAEDAAEAAAABAAAAUgEDAAEAAAACAAAAAAAAAAgACAAIAAgAAAD//wAA//8AAAAAAAAAAAAAAAAAAA==")]
  public async Task ImagePresetNormalizesGifAndTiffFirstFrameToStaticPng(string extension, string encoded)
  {
    await using var harness = await CompletionHarness.CreateAsync(new BatchRunClient(), outputPreset: true);
    CancellationToken token = TestContext.Current.CancellationToken;
    byte[] original = Convert.FromBase64String(encoded);
    string path = Path.Combine(harness.Root, $"original.{extension}");
    File.WriteAllBytes(path, original);
    await harness.Handler.ExecuteAsync(new AddDroppedImageConversionFilesCommand([path]), token);
    Guid id = Assert.Single(harness.Handler.ImageConversion.Items).Id;
    WorkbenchCommandOutcome prepared = await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(0), token);
    Assert.Null(prepared.Error);
    ImageConversionWorkbenchState state = Assert.IsType<ImageConversionWorkbenchState>(Assert.Single(prepared.States));
    Assert.Null(state.ExportImageError);
    Assert.Equal(id, state.ExportImageId);
    Assert.NotNull(state.ExportImage);
    Assert.Equal("image/png", state.ExportImage.MediaType);
    byte[] png = await harness.ReadResourceAsync(state.ExportImage, token);
    using var random = new MemoryStream(png).AsRandomAccessStream();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(token);
    Assert.Equal(BitmapDecoder.PngDecoderId, decoder.DecoderInformation.CodecId);
    Assert.Equal(1u, decoder.FrameCount);
    Assert.Equal(2u, decoder.PixelWidth);
    Assert.Equal(1u, decoder.PixelHeight);
    byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
      new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token)).DetachPixelData();
    Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
    WorkbenchAnnotationLease lease = await harness.StageImageAsync(png, token);
    Assert.Null((await harness.Handler.ExecuteAsync(new ExportConvertedImageCommand(id, lease.ResourceUri.AbsoluteUri, "default", new(new(NameTemplate: "{name}"), harness.Root)), token)).Error);
    Assert.Equal(png, File.ReadAllBytes(Path.Combine(harness.Root, "original.png")));
    Assert.Equal(original, File.ReadAllBytes(path));
    Assert.Throws<WorkbenchAnnotationAccessException>(() => harness.TakeAnnotation(lease));
  }

  [Fact]
  public async Task ImagePresetNormalizesExifOrientationOnce()
  {
    await using var harness = await CompletionHarness.CreateAsync(new BatchRunClient(), outputPreset: true);
    CancellationToken token = TestContext.Current.CancellationToken;
    byte[] jpeg = await QrCodeSavePlatform.EncodeImageAsync(VibpProjectFileTests.BuildPng(2, 1), BitmapEncoder.JpegEncoderId, token);
    byte[] exif = Convert.FromHexString("FFE100224578696600004D4D002A00000008000101120003000000010006000000000000");
    byte[] original = [.. jpeg[..2], .. exif, .. jpeg[2..]];
    string path = Path.Combine(harness.Root, "oriented.jpg");
    File.WriteAllBytes(path, original);
    await harness.Handler.ExecuteAsync(new AddDroppedImageConversionFilesCommand([path]), token);
    WorkbenchCommandOutcome prepared = await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(0), token);
    Assert.Null(prepared.Error);
    ImageConversionWorkbenchState state = Assert.IsType<ImageConversionWorkbenchState>(Assert.Single(prepared.States));
    Assert.Null(state.ExportImageError);
    byte[] png = await harness.ReadResourceAsync(state.ExportImage!, token);
    using var memory = new MemoryStream(png);
    using var random = memory.AsRandomAccessStream();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(token);
    Assert.Equal(1u, decoder.PixelWidth);
    Assert.Equal(2u, decoder.PixelHeight);
    Assert.Equal(decoder.PixelWidth, decoder.OrientedPixelWidth);
    Assert.Equal(original, File.ReadAllBytes(path));
  }

  [Theory]
  [InlineData("jp2", "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAAQAAAAIAAwcHAAAAAAAPY29scgEAAAAAABAAAACVanAyY/9P/1EALwAAAAAAAgAAAAEAAAAAAAAAAAAAAAIAAAABAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAAAEBAAB/1wABEBA/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAAHQAB/5PPtAgBr9+AEAk/34AQCT//2Q==")]
  [InlineData("gif", "AQIDBA==")]
  public async Task ImagePresetReportsUnavailableDecoderWithoutMisclassifyingImage(string extension, string encoded)
  {
    await using var harness = await CompletionHarness.CreateAsync(new BatchRunClient(), outputPreset: true);
    CancellationToken token = TestContext.Current.CancellationToken;
    byte[] original = Convert.FromBase64String(encoded);
    string path = Path.Combine(harness.Root, $"format.{extension}");
    File.WriteAllBytes(path, original);
    await harness.Handler.ExecuteAsync(new AddDroppedImageConversionFilesCommand([path]), token);
    WorkbenchCommandOutcome prepared = await harness.Handler.ExecuteAsync(new PrepareImageConversionCommand(0), token);
    Assert.Null(prepared.Error);
    ImageConversionWorkbenchState state = Assert.IsType<ImageConversionWorkbenchState>(Assert.Single(prepared.States));
    Assert.Equal(harness.Handler.ImageConversion.Items[0].Id, state.ExportImageId);
    if (state.ExportImageError is not null)
    {
      Assert.Equal("decode_unavailable", state.ExportImageError);
      Assert.Null(state.ExportImage);
      Assert.False(File.Exists(Path.Combine(harness.Root, "format.png")));
    }
    else
    {
      Assert.Equal("jp2", extension); // Valid JP2 may be decoded when a system codec is installed.
      Assert.NotNull(state.ExportImage);
      Assert.Equal("image/png", state.ExportImage.MediaType);
    }
    Assert.Equal(original, File.ReadAllBytes(path));
  }

  private static IReadOnlyList<BatchWorkbenchState> TerminalBatchStates(
    IEnumerable<WorkbenchStateEnvelope> envelopes) =>
    envelopes.Select(envelope => envelope.State)
      .OfType<BatchWorkbenchState>()
      .Where(state => state.IsTerminal)
      .ToArray();

  private static string TempPng(string root, string name)
  {
    string path = Path.Combine(root, $"{name}.png");
    File.WriteAllBytes(path, [1, 2, 3, 4]);
    return path;
  }
  private sealed class CompletionHarness : IAsyncDisposable
  {
    private readonly WorkbenchResourceBroker broker;
    private readonly WorkbenchAnnotationStore annotations;
    private readonly string root;
    private readonly IAsyncEnumerator<WorkbenchStateEnvelope> subscription;
    private readonly CancellationTokenSource subscriptionEnd;
    private readonly List<WorkbenchStateEnvelope> drained = [];
    private ValueTask<bool>? firstMove;

    private CompletionHarness(
      DesktopWorkbenchCommandHandler handler,
      WorkbenchApplication application,
      BatchViewModel batch,
      WorkbenchResourceBroker broker,
      WorkbenchAnnotationStore annotations,
      string root,
      CancellationTokenSource subscriptionEnd,
      IAsyncEnumerator<WorkbenchStateEnvelope> subscription)
    {
      Handler = handler;
      Application = application;
      Batch = batch;
      this.broker = broker;
      this.annotations = annotations;
      this.root = root;
      this.subscriptionEnd = subscriptionEnd;
      this.subscription = subscription;
    }

    public DesktopWorkbenchCommandHandler Handler { get; }

    public WorkbenchApplication Application { get; }

    public BatchViewModel Batch { get; }

    public string Root => root;
    public Task<WorkbenchAnnotationLease> StageImageAsync(byte[] bytes, CancellationToken token) => annotations.UploadPngAsync(new MemoryStream(bytes), token);
    public void TakeAnnotation(WorkbenchAnnotationLease lease) { using WorkbenchAnnotationFile image = annotations.Take(lease.ResourceUri); }
    public async Task<byte[]> ReadResourceAsync(WorkbenchResourceReference reference, CancellationToken token)
    {
      await using WorkbenchResourceResponse response = await broker.OpenAsync(new Uri(reference.Url), token);
      using var output = new MemoryStream();
      await response.Content.CopyToAsync(output, token);
      return output.ToArray();
    }


    public static async Task<CompletionHarness> CreateAsync(
      BatchRunClient client,
      Exception? readFailure = null, bool outputPreset = false)
    {
      string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-batch-terminal-{Guid.NewGuid():N}");
      string resourceRoot = Path.Combine(root, "resources");
      Directory.CreateDirectory(resourceRoot);
      var broker = new WorkbenchResourceBroker(resourceRoot);
      var annotations = new WorkbenchAnnotationStore(resourceRoot);
      var batch = new BatchViewModel(client, new HarnessBatchFiles(readFailure));
      PortableLayout? layout = null;
      if (outputPreset)
      {
        layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.Next.exe"), "production");
        layout.EnsurePortableState();
        EditorPreferencesStore.Save(layout, new(EditorPreferences.Default with { Output = new(NameTemplate: "{name}") }, root));
      }
      var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(client, new FixedInputService()),
        () => batch,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new SettingsViewModel(client),
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotations, optionsLayout: layout);
      var application = new WorkbenchApplication(
        ["batch.add", "batch.run"], WorkbenchRoute.Recognition, handler);
      try
      {
        WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
          TestContext.Current.CancellationToken);
        var end = new CancellationTokenSource();
        var harness = new CompletionHarness(
          handler, application, batch, broker, annotations, root, end,
          application.SubscribeAsync(bootstrap.Revision, end.Token)
            .GetAsyncEnumerator(end.Token));
        // async iterator 在首个 MoveNext 时才注册 mailbox。
        if (!outputPreset)
        {
          harness.firstMove = harness.subscription.MoveNextAsync();
          Assert.False(harness.firstMove.Value.IsCompleted,
            "The subscription must be parked on the mailbox signal after registration.");
        }
        return harness;
      }
      catch
      {
        await application.DisposeAsync();
        broker.Dispose();
        annotations.Dispose();
        Directory.Delete(root, recursive: true);
        throw;
      }
    }

    public ValueTask<WorkbenchCommandReceipt> AddDroppedFileAsync(
      string name, CancellationToken cancellationToken) =>
      Application.ExecuteAsync(
        new WorkbenchCommandEnvelope(
          Guid.NewGuid(), new AddDroppedBatchFilesCommand([TempPng(root, name)])),
        cancellationToken);

    public ValueTask<WorkbenchCommandReceipt> StartAsync(CancellationToken cancellationToken) =>
      Application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new StartBatchCommand()),
        cancellationToken);

    // 消费已启动的首个 MoveNext，避免并发推进迭代器。
    public async Task<IReadOnlyList<WorkbenchStateEnvelope>> DrainAsync(
      int expectedTerminals, CancellationToken cancellationToken)
    {
      if (firstMove is not null)
      {
        ValueTask<bool> pending = firstMove.Value;
        firstMove = null;
        bool firstMoved = await pending.AsTask()
          .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(firstMoved,
          "The workbench subscription ended before the batch terminal arrived.");
        lock (drained) drained.Add(subscription.Current);
      }
      while (true)
      {
        lock (drained)
        {
          if (TerminalBatchStates(drained).Count >= expectedTerminals) break;
        }
        bool moved = await subscription.MoveNextAsync().AsTask()
          .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(moved, "The workbench subscription ended before the batch terminal arrived.");
        lock (drained) drained.Add(subscription.Current);
      }
      lock (drained) return [.. drained];
    }

    public async ValueTask DisposeAsync()
    {
      subscriptionEnd.Cancel();
      await subscription.DisposeAsync();
      await Application.DisposeAsync();
      await Handler.DisposeAsync();
      broker.Dispose();
      annotations.Dispose();
      Directory.Delete(root, recursive: true);
      subscriptionEnd.Dispose();
    }
  }

  private sealed class BatchTerminalCounter : IDisposable
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly object gate = new();
    private readonly Queue<BatchWorkbenchState> terminals = new();
    private TaskCompletionSource<BatchWorkbenchState>? pending;

    public BatchTerminalCounter(DesktopWorkbenchCommandHandler handler)
    {
      this.handler = handler;
      handler.StateChanged += OnStateChanged;
    }

    public Task<BatchWorkbenchState> FirstTerminal
    {
      get
      {
        lock (gate) return NextTerminalLocked().Task;
      }
    }

    public int Count { get; private set; }

    public Task<BatchWorkbenchState> NextTerminal(CancellationToken cancellationToken)
    {
      lock (gate) return NextTerminalLocked().Task.WaitAsync(
        TimeSpan.FromSeconds(5), cancellationToken);
    }

    private TaskCompletionSource<BatchWorkbenchState> NextTerminalLocked()
    {
      if (terminals.Count > 0)
      {
        BatchWorkbenchState state = terminals.Dequeue();
        return TaskCompletionSourceFactory.FromResult(state);
      }
      pending ??= new TaskCompletionSource<BatchWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      return pending;
    }

    private void OnStateChanged(WorkbenchState state)
    {
      if (state is not BatchWorkbenchState { IsTerminal: true } terminal) return;
      lock (gate)
      {
        Count++;
        if (pending is not null)
        {
          pending.TrySetResult(terminal);
          pending = null;
        }
        else
        {
          terminals.Enqueue(terminal);
        }
      }
    }

    public void Dispose() => handler.StateChanged -= OnStateChanged;
  }

  private static class TaskCompletionSourceFactory
  {
    public static TaskCompletionSource<BatchWorkbenchState> FromResult(
      BatchWorkbenchState state)
    {
      var source = new TaskCompletionSource<BatchWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      source.TrySetResult(state);
      return source;
    }
  }

  private sealed class HarnessBatchFiles(Exception? readFailure) : IBatchFileSource
  {
    public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    public Task<(byte[] Data, string MediaType)> ReadAsync(
      string path, CancellationToken cancellationToken)
    {
      if (readFailure is not null) throw readFailure;
      return Task.FromResult((File.ReadAllBytes(path), "image/png"));
    }
  }

  private sealed class BatchRunClient : InferenceClientStub
  {
    private IReadOnlyList<JobItem> items = [];

    public Wire.Health Health { get; init; } = ModeHealth();

    public SettingsSnapshot Settings { get; init; } = new()
    {
      Extra = new Dictionary<string, JsonElement>
      {
        ["default_recognition_mode"] = JsonSerializer.SerializeToElement("windows_text"),
      },
    };

    public ItemState OutcomeState { get; init; } = ItemState.Succeeded;

    public TaskCompletionSource? ObserveGate { get; init; }

    public int Submissions { get; private set; }

    public SubmitRequest? LastRequest { get; private set; }

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      Submissions++;
      LastRequest = request;
      items = request.Items.Select((item, index) => new JobItem
      {
        ItemId = $"it-{index}",
        ClientItemKey = item.ClientItemKey,
        Ordinal = item.Ordinal,
        DisplayName = item.DisplayName,
        State = ItemState.Queued,
      }).ToArray();
      return Task.FromResult(new JobRef { JobId = "job-batch-terminal", Items = items });
    }

    public override async Task<JobUpdate> ObserveAsync(
      string jobId, int afterSequence, CancellationToken cancellationToken)
    {
      if (ObserveGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
      return new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Background,
          State = JobState.Completed,
          Items = items,
        },
        Events = [],
        Outcomes = items.Select(item => new ItemOutcome
        {
          ItemId = item.ItemId,
          Attempt = 1,
          State = OutcomeState,
          PayloadType = "ocr.v1",
          Payload = OutcomeState is ItemState.Succeeded
            ? new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("批量识别结果"),
            }
            : null,
          ErrorCode = OutcomeState is ItemState.Succeeded ? null : "engine_failed",
        }).ToArray(),
        ThroughSequence = afterSequence + 1,
      };
    }

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Health);

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Settings);

    public override Task<SettingsSnapshot> UpdateSettingsAsync(
      SettingsSnapshot settings, CancellationToken cancellationToken) =>
      Task.FromResult(settings);

    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());

    public async Task WaitSubmittedAsync(CancellationToken cancellationToken)
    {
      long deadline = Environment.TickCount64 + 5000;
      while (Submissions == 0)
      {
        Assert.True(Environment.TickCount64 < deadline, "The batch job was never submitted.");
        await Task.Delay(10, cancellationToken);
      }
    }
  }

  private static Wire.Health ModeHealth() => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-batch-terminal",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.DefaultRecognitionModeCapability,
      RuntimeSelectionService.RecognitionModesCapability,
    ],
    CapabilityDescriptors =
    [
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.RecognitionModesCapability,
        Lifecycle = "active",
        IntroducedIn = "2.8.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        RecognitionModeCatalog = new Wire.RecognitionModeCatalog
        {
          Modes =
          [
            Mode(Wire.RecognitionModeId.RapidText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.WindowsText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleText,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleStructure,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleDocumentVl,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.MineruDocument,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleTable,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleFormula,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.RapidTable,
              Wire.RecognitionModeAvailability.Ready),
          ],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(
    Wire.RecognitionModeId id,
    Wire.RecognitionModeAvailability availability)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.RapidText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Rapidocr,
        Wire.RecognitionModeProvisioning.BaseRuntime,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleDocumentVl => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PaddleOCRVL, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleFormula => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.FORMULARECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.RapidTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.RAPIDTABLE, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.BaseRuntime,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    bool notReady = availability != Wire.RecognitionModeAvailability.Ready;
    bool mineru = id == Wire.RecognitionModeId.MineruDocument;
    return new Wire.RecognitionModeDescriptor
    {
      Id = id,
      Family = family,
      PipelineId = pipeline,
      Engine = engine,
      Provisioning = provisioning,
      Availability = availability,
      ReasonCode = notReady ? "runtime_component_missing" : null,
      RequiredComponent = notReady ? (mineru ? "mineru-cpu" : "paddleocr-cpu") : null,
      SupportedOptions = id == Wire.RecognitionModeId.RapidTable ? ["device"] : [],
      Lifecycle = new Wire.RecognitionModeLifecycle
      {
        Kind = lifecycle,
        SupportsPreload = mineru,
        SupportsTtl = mineru,
        SupportsPinning = false,
        SupportsRelease = mineru,
      },
    };
  }

  private sealed class FixedInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(
        new RecognitionInput([1, 2, 3, 4], "image/png", "file.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => PickFileAsync(cancellationToken);
  }

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
}
