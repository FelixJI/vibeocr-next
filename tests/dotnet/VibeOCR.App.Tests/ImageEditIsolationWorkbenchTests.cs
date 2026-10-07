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
/// 图片编辑页（imageEdit scope）与单次识别页（recognition scope）的宿主
/// 状态隔离契约：两个页面的上传输入、会话 id/修订、CurrentInput、结果与
/// 取消互不影响；所有截图入口（含 recognition.captureScreen/快捷键）框选
/// 后复用既有选区动作栏；显式识别交接把当前图复制到 recognition 承载面，
/// 不移动/破坏另一页已有输入。
/// </summary>
public sealed class ImageEditIsolationWorkbenchTests
{
  private static readonly byte[] AnnotationPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

  /// <summary>
  /// 输入桩：文件/剪贴板输入按调用次序追加区分字节（两页上传内容不同），
  /// 截图入口记录宿主下发的动作栏目录投影。
  /// </summary>
  private sealed class TaggedInputService(ScreenshotSelectionAction? selectionAction = null)
    : IInputService
  {
    private int fileCalls;
    public ScreenshotSelectionActions? ReceivedActions { get; private set; }

    private RecognitionInput Tagged(string name, string origin)
    {
      byte tag = (byte)(Interlocked.Increment(ref fileCalls) + 1);
      return new RecognitionInput([0x21, tag, 0x42, tag], "image/png", name, origin);
    }

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(Tagged("edit.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(Tagged("clipboard.png", "clipboard"));

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(Tagged("screenshot.png", "screenshot"));

    public Task<RecognitionInput?> CaptureScreenWithActionsAsync(
      ScreenshotSelectionActions? actions,
      CancellationToken cancellationToken)
    {
      ReceivedActions = actions;
      RecognitionInput input = Tagged("screenshot.png", "screenshot");
      return Task.FromResult<RecognitionInput?>(selectionAction is null
        ? input
        : input with { SelectionAction = selectionAction });
    }

    public Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
      CaptureScreenAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  /// <summary>提交即完成的识别桩：记录上传内容。</summary>
  private sealed class CompletingInferenceClient : InferenceClientStub
  {
    public int SubmitCalls;
    public List<IReadOnlyList<byte>> UploadedContent { get; } = [];

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref SubmitCalls);
      UploadedContent.AddRange(uploads.Values.Select(upload => upload.Content));
      return Task.FromResult(new JobRef
      {
        JobId = "job-isolation",
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
              ["raw_text"] = JsonSerializer.SerializeToElement("isolation text"),
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
    CompletingInferenceClient inference,
    IInputService inputs,
    SettingsViewModel? settings = null,
    IAnnotatedImagePlatform? platform = null,
    Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?,
      IReadOnlyList<WorkbenchExclusionBox>>? pinScreenshot = null) => new(
    recognitionFactory,
    static () => throw new InvalidOperationException(),
    static () => throw new InvalidOperationException(),
    static () => throw new InvalidOperationException(),
    settings is null ? () => new SettingsViewModel(inference) : () => settings,
    () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
    static () => throw new InvalidOperationException(),
    new DiagnosticsViewModel("test", new PrerequisiteReport([])),
    broker,
    resourceRoot,
    static () => 0,
    annotationStore,
    platform,
    inferenceAttached: () => false,
    pinScreenshot: pinScreenshot);

  private static string TemporaryRoot()
  {
    string root = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-image-edit-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
  }

  private static WorkbenchAnnotationLease UploadAnnotation(
    WorkbenchAnnotationStore store,
    byte[] png) =>
    store.UploadPngAsync(
        new MemoryStream(png),
        TestContext.Current.CancellationToken)
      .GetAwaiter().GetResult();

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
      TimeSpan.FromSeconds(5),
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

  private sealed class RecordingAnnotatedImagePlatform : IAnnotatedImagePlatform
  {
    public byte[]? CopiedBytes { get; private set; }

    public async Task CopyImageAsync(string sourcePath, CancellationToken cancellationToken)
    {
      CopiedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
    }

    public async Task<bool> SaveImageAsync(string sourcePath, CancellationToken cancellationToken)
    {
      CopiedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
      return true;
    }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken) =>
      throw new NotSupportedException("text clipboard is not exercised here.");
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

  [Fact]
  public async Task CaptureScreenEntryReusesSelectionActionsToolbar()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService(ScreenshotSelectionAction.Edit);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs);

      using (var captured = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureRecognitionScreenCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState state = await captured.Task;
        Assert.NotNull(state.ScreenshotSession);
      }

      // 截图识别入口必须与纯截图入口一样把宿主动作栏目录交给选区 picker；
      // 无显式动作的确认沿用现场编辑会话，不直接提交 OCR。
      Assert.NotNull(inputs.ReceivedActions);
      Assert.Equal(0, inference.SubmitCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task RecognitionEditCommandsCreateRecognitionScopeSessions()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs);

      Guid sessionId;
      using (var ready = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new OpenRecognitionImageForEditCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState state = await ready.Task;
        Assert.False(state.ScreenshotSession!.SceneEditing);
        Assert.Equal(0, state.ScreenshotSession.Revision);
        Assert.NotNull(state.Input);
        sessionId = Guid.Parse(state.ScreenshotSession.SessionId);
      }

      // recognition 自己的编辑命令沿用既有会话修订合同。
      WorkbenchCommandOutcome revised = await handler.ExecuteAsync(
        new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState notified = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(revised.States));
      Assert.Equal(1, notified.ScreenshotSession?.Revision);
      Assert.Equal(0, inference.SubmitCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ImageEditAndRecognitionUploadsAreIsolated()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs);

      // imageEdit 页上传：独立 scope 的会话与输入。
      Guid imageEditSession;
      string imageEditInputUrl;
      using (var imageEditReady = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new SelectImageEditFileCommand(),
          TestContext.Current.CancellationToken);
        ImageEditWorkbenchState imageEdit = await imageEditReady.Task;
        imageEditSession = Guid.Parse(imageEdit.ScreenshotSession!.SessionId);
        imageEditInputUrl = imageEdit.Input!.Url;
        Assert.False(imageEdit.ScreenshotSession.SceneEditing);
      }

      // recognition 页选图编辑：独立会话 id 与输入资源，互不取代。
      Guid recognitionSession;
      using (var recognitionReady = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new OpenRecognitionImageForEditCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState recognition = await recognitionReady.Task;
        recognitionSession = Guid.Parse(recognition.ScreenshotSession!.SessionId);
        Assert.NotEqual(imageEditSession, recognitionSession);
        Assert.NotEqual(imageEditInputUrl, recognition.Input!.Url);
      }

      // recognition 页直接识别（新输入取代 recognition 会话并为新输入建立
      // 自身会话）不影响 imageEdit 会话：修订通知仍被接受，输入与修订保持。
      using (var recognized = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.Result is not null &&
          state.ScreenshotSession is { } session &&
          session.SessionId != recognitionSession.ToString("N")))
      {
        await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        await recognized.Task;
      }
      WorkbenchCommandOutcome imageEditRevised = await handler.ExecuteAsync(
        new NotifyImageEditRevisionCommand(imageEditSession, 1),
        TestContext.Current.CancellationToken);
      ImageEditWorkbenchState notifiedImageEdit = Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(imageEditRevised.States));
      Assert.Equal(1, notifiedImageEdit.ScreenshotSession?.Revision);
      Assert.Equal(imageEditInputUrl, notifiedImageEdit.Input?.Url);

      // 会话命令按 scope 隔离：recognition 会话命令不认 imageEdit 会话，
      // imageEdit 会话命令不认 recognition 会话（fail closed）。
      WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, imageEditSession, 1, []),
        TestContext.Current.CancellationToken)).Error?.Code);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeImageEditImageCommand(lease.ResourceUri.AbsoluteUri, recognitionSession, 0, []),
        TestContext.Current.CancellationToken)).Error?.Code);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ImageEditSessionCommandsUseOwnRevisionContract()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      var platform = new RecordingAnnotatedImagePlatform();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs, platform: platform);

      Guid sessionId;
      using (var ready = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new ReadImageEditClipboardCommand(),
          TestContext.Current.CancellationToken);
        sessionId = Guid.Parse((await ready.Task).ScreenshotSession!.SessionId);
      }

      // 修订推进：旧修订复制拒绝，当前修订复制成功并消费一次性租约。
      Assert.Null((await handler.ExecuteAsync(
        new NotifyImageEditRevisionCommand(sessionId, 2),
        TestContext.Current.CancellationToken)).Error);
      WorkbenchAnnotationLease stale = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new CopyImageEditImageCommand(stale.ResourceUri.AbsoluteUri, sessionId, 0),
        TestContext.Current.CancellationToken)).Error?.Code);
      WorkbenchAnnotationLease current = UploadAnnotation(annotationStore, AnnotationPng);
      WorkbenchCommandOutcome copied = await handler.ExecuteAsync(
        new CopyImageEditImageCommand(current.ResourceUri.AbsoluteUri, sessionId, 2),
        TestContext.Current.CancellationToken);
      Assert.Null(copied.Error);
      ImageEditWorkbenchState echoed = Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(copied.States));
      Assert.Equal(2, echoed.ScreenshotSession?.Revision);
      Assert.Equal(AnnotationPng, platform.CopiedBytes);
      Assert.Throws<WorkbenchAnnotationAccessException>(() =>
        annotationStore.Take(current.ResourceUri));

      // 关闭会话：宿主输入释放，后续修订 fail closed。
      WorkbenchCommandOutcome closed = await handler.ExecuteAsync(
        new CloseImageEditSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(closed.States)).ScreenshotSession);
      WorkbenchCommandOutcome late = await handler.ExecuteAsync(
        new NotifyImageEditRevisionCommand(sessionId, 3),
        TestContext.Current.CancellationToken);
      Assert.Null(Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(late.States)).ScreenshotSession);
      Assert.Equal(0, inference.SubmitCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ImageEditExplicitRecognizeCopiesIntoRecognitionScope()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs);

      Guid sessionId;
      string initialInputUrl;
      using (var ready = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new SelectImageEditFileCommand(),
          TestContext.Current.CancellationToken);
        ImageEditWorkbenchState initial = await ready.Task;
        sessionId = Guid.Parse(initial.ScreenshotSession!.SessionId);
        initialInputUrl = initial.Input!.Url;
      }

      // 显式识别交接：当前图复制到 recognition 承载面（结果与输入副本）；
      // imageEdit 会话保留（不 busy、修订不变），可继续编辑/重试。冻结
      // 基准发布为新 URL：前端以 source 变化重置编辑历史并按 excludeBoxes
      // 重建屏蔽标记，旧标注不会二次烘焙。
      var exclusion = new WorkbenchExclusionBox(1, 2, 3, 4);
      WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
      using (var settled = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null &&
          state.Input?.Url != initialInputUrl))
      using (var completed = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.Result is not null))
      {
        Assert.Null((await handler.ExecuteAsync(
          new RecognizeImageEditImageCommand(
            lease.ResourceUri.AbsoluteUri, sessionId, 0, [exclusion]),
          TestContext.Current.CancellationToken)).Error);
        RecognitionWorkbenchState recognition = await completed.Task;
        Assert.Equal("recognition.completed", recognition.StatusCode);
        Assert.NotNull(recognition.Input);
        // 交接命令冻结的排除框随 B 独立快照会话收编：编辑器在新基准上
        // 重建屏蔽标记，后续识别/取字提交同一屏蔽合同。
        Assert.NotNull(recognition.ScreenshotSession);
        WorkbenchExclusionBox handoffBoxes = Assert.Single(
          recognition.ScreenshotSession.ExcludeBoxes ?? []);
        Assert.Equal(exclusion, handoffBoxes);
        ImageEditWorkbenchState frozen = await settled.Task;
        Assert.Equal(0, frozen.ScreenshotSession!.Revision);
        WorkbenchExclusionBox echoed = Assert.Single(
          frozen.ScreenshotSession.ExcludeBoxes ?? []);
        Assert.Equal(exclusion, echoed);
        // OCR 输入是宿主烘焙白色遮罩后的副本，而不是未遮罩原图。
        Assert.All(inference.UploadedContent, bytes => Assert.NotEqual(AnnotationPng, bytes));
      }

      // 后续内容修订只作用于 imageEdit 通道：recognition 承载面的交接结果是
      // 独立快照（两页独立），不被后续编辑取消/擦除。
      WorkbenchCommandOutcome revised = await handler.ExecuteAsync(
        new NotifyImageEditRevisionCommand(sessionId, 1),
        TestContext.Current.CancellationToken);
      ImageEditWorkbenchState notified = Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(revised.States));
      Assert.Equal(1, notified.ScreenshotSession?.Revision);
      WorkbenchCommandOutcome engineCleared = await handler.ExecuteAsync(
        new SetTaskEngineCommand(null),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState projected = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(engineCleared.States));
      // 交接结果仍存活：状态码保持 completed（回显本身不携带资源）。
      Assert.Equal("recognition.completed", projected.StatusCode);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ImageEditHandoffReplacesRecognitionSessionWithIndependentSnapshot()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs);

      // 先在 recognition 页识别 A：建立 A 会话与结果。
      Guid sessionA;
      string inputA;
      using (var completedA = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.Result is not null && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState stateA = await completedA.Task;
        sessionA = Guid.Parse(stateA.ScreenshotSession!.SessionId);
        inputA = stateA.Input!.Url;
      }

      // imageEdit 页上传 B 并显式识别：交接必须取代 recognition 通道的
      // A 会话，为 B 建立独立快照会话（新会话 id、B 副本输入、B 结果）。
      Guid sessionB;
      using (var imageEditReady = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new SelectImageEditFileCommand(),
          TestContext.Current.CancellationToken);
        ImageEditWorkbenchState imageEdit = await imageEditReady.Task;
        Assert.NotEqual(sessionA.ToString("N"), imageEdit.ScreenshotSession!.SessionId);
      }
      WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
      using (var completedB = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.Result is not null &&
          state.ScreenshotSession is { } session && session.SessionId != sessionA.ToString("N")))
      {
        Assert.Null((await handler.ExecuteAsync(
          new RecognizeImageEditImageCommand(lease.ResourceUri.AbsoluteUri,
            Guid.Parse(Assert.IsType<ImageEditWorkbenchState>(
              await LatestImageEditStateAsync(handler)).ScreenshotSession!.SessionId), 0, []),
          TestContext.Current.CancellationToken)).Error);
        RecognitionWorkbenchState stateB = await completedB.Task;
        sessionB = Guid.Parse(stateB.ScreenshotSession!.SessionId);
        Assert.NotEqual(sessionA, sessionB);
        Assert.NotEqual(inputA, stateB.Input!.Url);
      }

      // 重投影（任务模式回显）携带 B 会话与输入，不再附着旧 A。
      WorkbenchCommandOutcome reprojected = await handler.ExecuteAsync(
        new SetTaskEngineCommand(null),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState projection = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(reprojected.States));
      Assert.Equal(sessionB.ToString("N"), projection.ScreenshotSession?.SessionId);
      Assert.Equal("recognition.completed", projection.StatusCode);

      // 旧 A 会话命令 fail closed；imageEdit 会话保持独立可用。
      WorkbenchAnnotationLease staleLease = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeScreenshotImageCommand(staleLease.ResourceUri.AbsoluteUri, sessionA, 0, []),
        TestContext.Current.CancellationToken)).Error?.Code);
      WorkbenchCommandOutcome imageEditEcho = await handler.ExecuteAsync(
        new NotifyImageEditRevisionCommand(
          Guid.Parse(Assert.IsType<ImageEditWorkbenchState>(
            await LatestImageEditStateAsync(handler)).ScreenshotSession!.SessionId), 1),
        TestContext.Current.CancellationToken);
      Assert.Equal(1, Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(imageEditEcho.States)).ScreenshotSession?.Revision);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>零副作用查询：空会话修订命令的回显携带当前 imageEdit 投影。</summary>
  private static async Task<ImageEditWorkbenchState> LatestImageEditStateAsync(
    DesktopWorkbenchCommandHandler handler)
  {
    WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
      new NotifyImageEditRevisionCommand(Guid.Empty, 0),
      TestContext.Current.CancellationToken);
    return Assert.IsType<ImageEditWorkbenchState>(Assert.Single(outcome.States));
  }

  [Fact]
  public async Task PlainRecognitionInputKeepsEditableSessionForPostProcessing()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        () => new RecognitionViewModel(inference, inputs),
        root, broker, annotationStore, inference, inputs);

      // 普通选图仍即选即识别（自动语义不变），完成后建立 recognition 自身
      // 编辑会话：去水印/旋转/屏蔽等后处理之后可显式识别当前图。
      Guid sessionId;
      using (var completed = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.Result is not null &&
          state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState state = await completed.Task;
        sessionId = Guid.Parse(state.ScreenshotSession!.SessionId);
        Assert.False(state.ScreenshotSession.SceneEditing);
        Assert.Equal(0, state.ScreenshotSession.Revision);
        Assert.Equal(1, inference.SubmitCalls);
      }

      // 编辑修订推进：会话修订合同沿用，旧结果失效。
      WorkbenchCommandOutcome revised = await handler.ExecuteAsync(
        new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
        TestContext.Current.CancellationToken);
      Assert.Equal(1, Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(revised.States)).ScreenshotSession!.Revision);

      // 后处理导出后显式识别当前图：结果重新发布，会话保留。
      WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
      using (var recognized = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => !state.IsBusy && state.Result is not null))
      {
        Assert.Null((await handler.ExecuteAsync(
          new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 1, []),
          TestContext.Current.CancellationToken)).Error);
        await recognized.Task;
      }
      Assert.Equal(2, inference.SubmitCalls);
      Assert.Equal(AnnotationPng, inference.UploadedContent[1]);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
