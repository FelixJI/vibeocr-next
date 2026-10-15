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
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #232 系统内容导入宿主契约：图片/文本体一律 owned 资源化（不内联）、
/// 会话/修订绑定、迟到/取消/超限保留旧画布并精确回收暂存资源、
/// ack/reject/release 幂等、空画布图片走旧基准入口而文本提示先打开图片。
/// </summary>
public sealed class ImageImportWorkbenchTests
{
  private sealed class TaggedInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput([0x21, 0x01], "image/png", "edit.png", "file"));

    public int ClipboardReads { get; private set; }
    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken)
    {
      ClipboardReads++;
      return Task.FromResult<RecognitionInput?>(new RecognitionInput([0x21, 0x02], "image/png", "clipboard.png", "clipboard"));
    }

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      throw new NotSupportedException("capture is not exercised here.");

    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  /// <summary>受控导入读取桩：预设槽 + 等待者握手。先预设则读取立即返回，
  /// 先读取则 Next 完成等待者；不丢失任何一侧时序。</summary>
  private sealed class ControlledImportReader : ISystemImportReader
  {
    private readonly object gate = new();
    private ImportedSystemContent? preset;
    private bool hasPreset;
    private Exception? presetError;
    private TaskCompletionSource<ImportedSystemContent?>? waiter;

    public void Next(ImportedSystemContent? content)
    {
      lock (gate)
      {
        if (waiter is { } pending)
        {
          waiter = null;
          pending.SetResult(content);
        }
        else
        {
          preset = content;
          hasPreset = true;
        }
      }
    }

    public void NextError(Exception error)
    {
      lock (gate)
      {
        if (waiter is { } pending)
        {
          waiter = null;
          pending.SetException(error);
        }
        else
        {
          presetError = error;
        }
      }
    }

    /// <summary>挂起下一次读取（返回可控 TCS）；用于单飞/迟到用例。</summary>
    public TaskCompletionSource<ImportedSystemContent?> Hold()
    {
      TaskCompletionSource<ImportedSystemContent?> held =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (gate)
      {
        waiter = held;
      }
      return held;
    }

    public Task<ImportedSystemContent?> ReadClipboardForImportAsync(
      CancellationToken cancellationToken)
    {
      lock (gate)
      {
        if (presetError is not null)
        {
          Exception error = presetError;
          presetError = null;
          return Task.FromException<ImportedSystemContent?>(error);
        }
        if (hasPreset)
        {
          ImportedSystemContent? content = preset;
          hasPreset = false;
          preset = null;
          return Task.FromResult(content);
        }
        // Hold() 预挂的等待者优先复用；新建的 waiter 必须留在字段里，
        // 否则 Next 找不到等待者只能存 preset，读取端永挂。
        if (waiter is { } held)
        {
          waiter = null;
          return held.Task.WaitAsync(cancellationToken);
        }
        TaskCompletionSource<ImportedSystemContent?> created =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        waiter = created;
        return created.Task.WaitAsync(cancellationToken);
      }
    }

    public Task<ImportedSystemContent?> ReadPackageForImportAsync(
      Windows.ApplicationModel.DataTransfer.DataPackageView view,
      CancellationToken cancellationToken) =>
      throw new NotSupportedException("package reading is not exercised here.");
  }

  private sealed class CompletingInferenceClient : InferenceClientStub;

  private static DesktopWorkbenchCommandHandler CreateHandler(
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    string resourceRoot,
    ISystemImportReader reader,
    TaggedInputService inputs)
  {
    var inference = new CompletingInferenceClient();
    return new DesktopWorkbenchCommandHandler(
      () => new RecognitionViewModel(inference, inputs),
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
      inferenceAttached: () => false,
      systemImportReader: reader);
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

  private sealed class StateAwaiter<TState> : IDisposable where TState : WorkbenchState
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly Func<TState, bool> predicate;
    private readonly TaskCompletionSource<TState> ready =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateAwaiter(DesktopWorkbenchCommandHandler handler, Func<TState, bool> predicate)
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

  private static string TemporaryRoot()
  {
    string root = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-image-import-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
  }

  private static ImportedSystemContent ImageContent(params (int Width, int Height)[] sizes) => new(
    sizes.Select(size => new SystemImportImageItem(
      new byte[8], "image/png", size.Width, size.Height)).ToList(),
    null,
    ImportedSystemContent.StorageItemsSource);

  /// <summary>按内容查找宿主 owned 会话资源（broker URL token 与文件名无关）。</summary>
  private static string[] SessionFiles(string root, byte[] content)
  {
    string directory = Path.Combine(root, "session");
    if (!Directory.Exists(directory)) return [];
    return Directory.GetFiles(directory)
      .Where(file => File.ReadAllBytes(file).SequenceEqual(content))
      .ToArray();
  }

  private static string[] TextSessionFiles(string root, string text) =>
    SessionFiles(root, System.Text.Encoding.UTF8.GetBytes(text));

  /// <summary>建立 imageEdit 会话：沿用旧粘贴基准入口（Tagged 输入）。</summary>
  private static async Task<Guid> EstablishImageEditSessionAsync(
    DesktopWorkbenchCommandHandler handler)
  {
    using var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
      state => state.ScreenshotSession is not null);
    await handler.ExecuteAsync(
      new ReadImageEditClipboardCommand(),
      TestContext.Current.CancellationToken);
    ImageEditWorkbenchState state = await captured.Task;
    Assert.NotNull(state.ScreenshotSession);
    return Guid.Parse(state.ScreenshotSession.SessionId);
  }

  [Fact]
  public async Task EmptyCanvasReadIsSingleFlightAndCloseCancelsItsProvider()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var store = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      await using var handler = CreateHandler(broker, store, root, reader, new TaggedInputService());
      TaskCompletionSource<ImportedSystemContent?> held = reader.Hold();
      Task<WorkbenchCommandOutcome> first = handler.ExecuteAsync(new PasteImageEditSystemContentCommand(), TestContext.Current.CancellationToken).AsTask();
      Task<WorkbenchCommandOutcome>? second = null;
      try
      {
        second = handler.ExecuteAsync(new PasteImageEditSystemContentCommand(), TestContext.Current.CancellationToken).AsTask();
        Assert.True(second.IsCompleted);
        Assert.Equal("image_import_in_progress", (await second).Error?.Code);
        await handler.ExecuteAsync(new CloseImageEditSessionCommand(), TestContext.Current.CancellationToken);
        Assert.Same(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)));
        Assert.Equal("annotation_operation_cancelled", (await first).Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "session")));
      }
      finally
      {
        held.TrySetResult(null);
        reader.Next(null);
        await first;
        if (second is not null) await second;
      }
    }
    finally { Directory.Delete(root, recursive: true); }
  }
  [Fact]
  public async Task EmptyCanvasUnavailableSystemProviderReturnsExplicitFailure()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var store = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      reader.NextError(new System.Runtime.InteropServices.COMException("拖放源已消失"));
      await using var handler = CreateHandler(broker, store, root, reader, new TaggedInputService());
      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(new PasteImageEditSystemContentCommand(), TestContext.Current.CancellationToken);
      Assert.Equal("image_import_refused", outcome.Error?.Code);
      Assert.False(Directory.Exists(Path.Combine(root, "session")));
    }
    finally { Directory.Delete(root, recursive: true); }
  }
  [Fact]
  public async Task CancelledReadRetainsExistingSessionAndPublishesNoPartialResources()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var store = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      await using var handler = CreateHandler(broker, store, root, reader, new TaggedInputService());
      Guid sessionId = await EstablishImageEditSessionAsync(handler);
      using var cancellation = new CancellationTokenSource();
      reader.Hold();
      using var captured = new StateAwaiter<ImageEditWorkbenchState>(handler, state => state.ImportError == "cancelled");
      await handler.ExecuteAsync(new PasteImageEditSystemContentCommand(), cancellation.Token);
      cancellation.Cancel();
      ImageEditWorkbenchState state = await captured.Task;
      Assert.Equal(sessionId.ToString("N"), state.ScreenshotSession!.SessionId);
      Assert.Null(state.Import);
      Assert.Single(Directory.GetFiles(Path.Combine(root, "session")));
    }
    finally { Directory.Delete(root, recursive: true); }
  }
  [Fact]
  public async Task NativeDropKeepsDataPackageAliveUntilItsDeferredReadFinishes()
  {
    string root = TemporaryRoot();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var store = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(broker, store, root, InputService.CreateImportReader(), new TaggedInputService());
      await EstablishImageEditSessionAsync(handler);
      var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
      var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      package.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text, async request =>
      {
        var deferral = request.GetDeferral();
        started.TrySetResult();
        try
        {
          await release.Task;
          request.SetData("真实延迟拖入");
        }
        finally { deferral.Complete(); }
      });
      using var captured = new StateAwaiter<ImageEditWorkbenchState>(handler, state => state.Import is not null);
      Task<WorkbenchCommandOutcome> command = handler.ExecuteAsync(
        new ImportDroppedImageEditContentCommand(package.GetView()), TestContext.Current.CancellationToken).AsTask();
      await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      try { Assert.False(command.IsCompleted); }
      finally { release.TrySetResult(); }
      Assert.Null((await command).Error);
      Assert.Equal("text", Assert.Single((await captured.Task).Import!.Items).Kind);
    }
    finally
    {
      release.TrySetResult();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task TwoNativeHostsKeepImportChannelsAndOwnedResourceReleasesSeparate()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var store = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(broker, store, root, InputService.CreateImportReader(), new TaggedInputService());
      Guid editId = await EstablishImageEditSessionAsync(handler);
      RecognitionWorkbenchState recognition;
      using (var captured = new StateAwaiter<RecognitionWorkbenchState>(handler, state => state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(new PasteRecognitionImageForEditCommand(), TestContext.Current.CancellationToken);
        recognition = await captured.Task;
      }
      Guid recognitionId = Guid.Parse(recognition.ScreenshotSession!.SessionId);
      await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit, handler);
      await using var main = new WebWorkbenchHost(app, broker, store, applicationOwner: false);
      await using var scene = new WebWorkbenchHost(app, broker, store, applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit);
      object mainDocument = new(), sceneDocument = new();
      var mainPosted = new System.Collections.Concurrent.ConcurrentQueue<string>();
      var scenePosted = new System.Collections.Concurrent.ConcurrentQueue<string>();
      main.RegisterBridge(mainDocument, mainPosted.Enqueue, action => { action(); return true; });
      scene.RegisterBridge(sceneDocument, scenePosted.Enqueue, action => { action(); return true; });
      string bootstrap = JsonSerializer.Serialize(new { version = 2, kind = "request", id = Guid.NewGuid(), type = "app.bootstrap", payload = new { } });
      await main.ReceiveWebMessageAsync(bootstrap, mainDocument);
      await scene.ReceiveWebMessageAsync(bootstrap, sceneDocument);
      Assert.Contains(mainPosted, json => json.Contains("app.bootstrap", StringComparison.Ordinal));
      Assert.Contains(scenePosted, json => json.Contains("app.bootstrap", StringComparison.Ordinal));
      var editPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
      editPackage.SetText("独立编辑窗文本");
      ImageEditWorkbenchState edit;
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler, state => state.Import is not null))
      {
        Assert.Null((await handler.ExecuteAsync(new ImportDroppedImageEditContentCommand(editPackage.GetView()), TestContext.Current.CancellationToken)).Error);
        edit = await captured.Task;
      }
      var scenePackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
      scenePackage.SetText("截图现场文本");
      using (var captured = new StateAwaiter<RecognitionWorkbenchState>(handler, state => state.Import is not null))
      {
        Assert.Null((await handler.ExecuteAsync(new ImportDroppedScreenshotContentCommand(scenePackage.GetView()), TestContext.Current.CancellationToken)).Error);
        recognition = await captured.Task;
      }
      Assert.Equal(editId.ToString("N"), edit.Import!.SessionId);
      Assert.Equal(recognitionId.ToString("N"), recognition.Import!.SessionId);
      Assert.NotEqual(edit.Import.Items[0].ResourceUri, recognition.Import.Items[0].ResourceUri);
      await handler.ExecuteAsync(new AckImageEditImportCommand(editId, 0, edit.Import.RequestId), TestContext.Current.CancellationToken);
      await handler.ExecuteAsync(new AckScreenshotImportCommand(recognitionId, 0, recognition.Import.RequestId), TestContext.Current.CancellationToken);
      // 即使拿到另一通道 URI，也只能释放当前通道已采纳的资源。
      await handler.ExecuteAsync(new ReleaseImageEditAssetsCommand(editId, [recognition.Import.Items[0].ResourceUri]), TestContext.Current.CancellationToken);
      Assert.Single(TextSessionFiles(root, "截图现场文本"));
      await handler.ExecuteAsync(new CloseImageEditSessionCommand(), TestContext.Current.CancellationToken);
      Assert.Empty(TextSessionFiles(root, "独立编辑窗文本"));
      Assert.Single(TextSessionFiles(root, "截图现场文本"));
      Assert.All(mainPosted.Concat(scenePosted), json => Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= WorkbenchBridgeCodec.MaxMessageBytes));
      await scene.DisposeAsync();
      Assert.Equal(WorkbenchRoute.ImageEdit, (await app.BootstrapAsync(TestContext.Current.CancellationToken)).Route);
    }
    finally { Directory.Delete(root, recursive: true); }
  }
  [Fact]
  public async Task ActiveSessionImportPublishesOpaqueDescriptorsAndAckKeepsAssets()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);
      Guid sessionId = await EstablishImageEditSessionAsync(handler);

      ImageEditWorkbenchState imported;
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.Import is not null))
      {
        await handler.ExecuteAsync(
          new PasteImageEditSystemContentCommand(),
          TestContext.Current.CancellationToken);
        reader.Next(new ImportedSystemContent(
          [new SystemImportImageItem([1, 2, 3, 4], "image/png", 4, 4)],
          "导入文本",
          ImportedSystemContent.StorageItemsSource));
        imported = await captured.Task;
      }

      Assert.NotNull(imported.ScreenshotSession);
      Assert.NotNull(imported.Import);
      Assert.Equal(sessionId.ToString("N"), imported.Import.SessionId);
      Assert.Equal(imported.ScreenshotSession.Revision, imported.Import.Revision);
      Assert.Equal(2, imported.Import.Items.Count);
      Assert.All(imported.Import.Items, item =>
        Assert.StartsWith("https://app.vibeocr/__resource/", item.ResourceUri, StringComparison.Ordinal));
      Assert.Equal("image", imported.Import.Items[0].Kind);
      Assert.Equal(4, imported.Import.Items[0].PixelWidth);
      Assert.Equal("text", imported.Import.Items[1].Kind);
      Assert.Equal(4, imported.Import.Items[1].TextLength);

      // 错误修订不能采纳资源，否则随后 release 会误删尚未确认的批次。
      await handler.ExecuteAsync(new AckImageEditImportCommand(sessionId,
        imported.ScreenshotSession.Revision + 1, imported.Import.RequestId), TestContext.Current.CancellationToken);
      await handler.ExecuteAsync(new ReleaseImageEditAssetsCommand(sessionId,
        imported.Import.Items.Select(item => item.ResourceUri).ToList()), TestContext.Current.CancellationToken);
      Assert.Single(SessionFiles(root, [1, 2, 3, 4]));
      await handler.ExecuteAsync(new RejectImageEditImportCommand(sessionId,
        imported.ScreenshotSession.Revision + 1, imported.Import.RequestId), TestContext.Current.CancellationToken);
      Assert.Single(SessionFiles(root, [1, 2, 3, 4]));
      // ack 幂等：资源保留在 owned 目录，重复确认无副作用。
      await handler.ExecuteAsync(
        new AckImageEditImportCommand(
          sessionId, imported.ScreenshotSession.Revision, imported.Import.RequestId),
        TestContext.Current.CancellationToken);
      await handler.ExecuteAsync(
        new AckImageEditImportCommand(
          sessionId, imported.ScreenshotSession.Revision, imported.Import.RequestId),
        TestContext.Current.CancellationToken);
      Assert.Single(SessionFiles(root, [1, 2, 3, 4]));
      Assert.Single(TextSessionFiles(root, "导入文本"));

      // release 幂等：释放后文件删除，重复释放不再抛错。
      foreach (int attempt in Enumerable.Range(0, 2))
      {
        await handler.ExecuteAsync(
          new ReleaseImageEditAssetsCommand(
            sessionId, imported.Import.Items.Select(item => item.ResourceUri).ToList()),
          TestContext.Current.CancellationToken);
      }
      Assert.Empty(SessionFiles(root, [1, 2, 3, 4]));
      Assert.Empty(TextSessionFiles(root, "导入文本"));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task RejectAfterPublishReleasesStagedResources()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);
      Guid sessionId = await EstablishImageEditSessionAsync(handler);

      ImageEditWorkbenchState imported;
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.Import is not null))
      {
        await handler.ExecuteAsync(
          new PasteImageEditSystemContentCommand(),
          TestContext.Current.CancellationToken);
        reader.Next(ImageContent((2, 2)));
        imported = await captured.Task;
      }

      Assert.NotNull(imported.ScreenshotSession);
      Assert.NotNull(imported.Import);

      // 拒收（Web 端迟到/取消/解码失败路径）：暂存 owned 文件被精确回收；重复拒收幂等。
      for (int attempt = 0; attempt < 2; attempt++)
      {
        await handler.ExecuteAsync(
          new RejectImageEditImportCommand(
            sessionId, imported.ScreenshotSession.Revision, imported.Import.RequestId),
          TestContext.Current.CancellationToken);
      }
      Assert.Empty(SessionFiles(root, new byte[8]));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task AggregateAssetBudgetCountsPendingAndAdoptedAssets(bool acknowledge)
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);
      Guid sessionId = await EstablishImageEditSessionAsync(handler);

      // 第一批：16 张 1x1 图片（等于资产配额）+ ack 采纳。
      ImageEditWorkbenchState first;
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.Import is not null))
      {
        await handler.ExecuteAsync(
          new PasteImageEditSystemContentCommand(),
          TestContext.Current.CancellationToken);
        reader.Next(ImageContent(Enumerable.Repeat((1, 1), 16).ToArray()));
        first = await captured.Task;
      }
      Assert.NotNull(first.ScreenshotSession);
      Assert.NotNull(first.Import);
      if (acknowledge)
        await handler.ExecuteAsync(
          new AckImageEditImportCommand(
            sessionId, first.ScreenshotSession.Revision, first.Import.RequestId),
          TestContext.Current.CancellationToken);

      // 第二批：再入 1 张即超过 16 asset 聚合上限：整批拒绝，不发布部分描述符。
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.ImportError is not null))
      {
        await handler.ExecuteAsync(
          new PasteImageEditSystemContentCommand(),
          TestContext.Current.CancellationToken);
        reader.Next(ImageContent((1, 1)));
        ImageEditWorkbenchState failed = await captured.Task;
        Assert.Equal("budget", failed.ImportError);
        Assert.Null(failed.Import);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task OversizeReaderRejectsImportAndKeepsCanvas()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);
      Guid sessionId = await EstablishImageEditSessionAsync(handler);

      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.ImportError is "oversize"))
      {
        await handler.ExecuteAsync(
          new PasteImageEditSystemContentCommand(),
          TestContext.Current.CancellationToken);
        reader.NextError(new SystemImportRejectException(
          "oversize", "导入图片超过 16 MiB 上限。"));
        ImageEditWorkbenchState failed = await captured.Task;
        Assert.Null(failed.Import);
        Assert.NotNull(failed.ScreenshotSession);
        Assert.Equal(sessionId.ToString("N"), failed.ScreenshotSession.SessionId);
      }
      // 只有基准输入资源存在：导入未发布任何 owned 文件。
      Assert.Empty(Directory.GetFiles(Path.Combine(root, "session"), "*.txt"));
      Assert.DoesNotContain(Directory.GetFiles(Path.Combine(root, "session"), "*.png"),
        file => new FileInfo(file).Length == 8);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SingleFlightRejectsSecondImportWhileReading()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);
      await EstablishImageEditSessionAsync(handler);

      TaskCompletionSource<ImportedSystemContent?> held = reader.Hold();
      await handler.ExecuteAsync(
        new PasteImageEditSystemContentCommand(),
        TestContext.Current.CancellationToken);
      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new PasteImageEditSystemContentCommand(),
        TestContext.Current.CancellationToken);
      Assert.NotNull(outcome.Error);
      Assert.Equal("image_import_in_progress", outcome.Error.Code);

      held.SetResult(null); // 空内容 → 终态；不再阻塞。
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.ImportError is not null))
      {
        await captured.Task;
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task StaleSessionImportIsDroppedAndResourcesReleased()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);
      await EstablishImageEditSessionAsync(handler);

      TaskCompletionSource<ImportedSystemContent?> held = reader.Hold();
      await handler.ExecuteAsync(
        new PasteImageEditSystemContentCommand(),
        TestContext.Current.CancellationToken);
      // 读取期间换基准（新会话）：旧导入完成后不发布描述符并回收资源。
      using (var replaced = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.ScreenshotSession is not null && state.Import is null && !state.IsBusy))
      {
        await handler.ExecuteAsync(
          new SelectImageEditFileCommand(),
          TestContext.Current.CancellationToken);
        await replaced.Task;
      }
      held.SetResult(ImageContent((3, 3)));
      // 完成不发布任何 import 状态：等一小段时间后确认无暂存文件残留。
      await Task.Delay(200, TestContext.Current.CancellationToken);
      Assert.DoesNotContain(Directory.GetFiles(Path.Combine(root, "session"), "*.png"),
        file => new FileInfo(file).Length == 8);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task EmptyCanvasTextIsRefusedAndBitmapUsesValidatedSnapshot()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var reader = new ControlledImportReader();
      var inputs = new TaggedInputService();
      await using var handler = CreateHandler(broker, annotationStore, root, reader, inputs);

      // 空画布 + 纯文本：明确提示先打开图片，不建会话。
      reader.Next(new ImportedSystemContent([], "只有文本", ImportedSystemContent.TextSource));
      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new PasteImageEditSystemContentCommand(),
        TestContext.Current.CancellationToken);
      Assert.NotNull(outcome.Error);
      Assert.Equal("workbench.error.imageImportOpenFirst", outcome.Error.MessageKey);

      // 空画布复用同一已授权快照建立基准，不再二次读取系统剪贴板。
      using (var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.ScreenshotSession is not null))
      {
        reader.Next(new ImportedSystemContent(
          [new SystemImportImageItem([9, 9, 9, 9], "image/png", 2, 2)],
          null,
          ImportedSystemContent.BitmapSource));
        await handler.ExecuteAsync(
          new PasteImageEditSystemContentCommand(),
          TestContext.Current.CancellationToken);
        ImageEditWorkbenchState state = await captured.Task;
        Assert.Null(state.Import);
        Assert.Equal(0, inputs.ClipboardReads);
        Assert.Single(SessionFiles(root, [9, 9, 9, 9]));
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
