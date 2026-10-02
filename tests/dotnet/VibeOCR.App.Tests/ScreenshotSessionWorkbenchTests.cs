using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
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
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Tests;

/// <summary>
/// #106 纯截图会话契约：捕获零 OCR、内容修订失效、旧 revision/旧会话拒绝、
/// 显式识别只消费编辑器导出的最终 PNG、迟到响应不覆盖新会话。
/// </summary>
public sealed class ScreenshotSessionWorkbenchTests
{
  [Theory]
  [InlineData("data:text/html;charset=utf-8;base64,PGI+dGVzdDwvYj4=", true)]
  [InlineData("data:text/html,<script>alert(1)</script>", false)]
  [InlineData("https://example.invalid", false)]
  [InlineData("about:blank", false)]
  public void PinnedNavigationAcceptsOnlyTheHostGeneratedDocument(string uri, bool allowed)
  {
    const string expected = "data:text/html;charset=utf-8;base64,PGI+dGVzdDwvYj4=";
    Assert.Equal(allowed, PinnedImageWindow.IsDocumentNavigationAllowed(uri, expected));
    Assert.False(PinnedImageWindow.IsDocumentNavigationAllowed(uri, null));
  }

  private static readonly byte[] AnnotationPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
  private static readonly byte[] CaptureBytes = [9, 8, 7, 6, 5];

  private sealed class FixedCaptureInput : IInputService
  {
    public int CaptureCalls;
    public bool CancelCapture;
    public bool FailCapture;

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(CaptureBytes, "image/png", "capture.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref CaptureCalls);
      if (FailCapture) throw new IOException("Synthetic capture failed.");
      if (CancelCapture) return Task.FromResult<RecognitionInput?>(null);
      return Task.FromResult<RecognitionInput?>(
        new RecognitionInput(CaptureBytes, "image/bmp", "screenshot.bmp", "screenshot"));
    }

    public Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
      CaptureScreenAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  /// <summary>Records submitted uploads; every submit completes with raw text.</summary>
  private class RecordingRecognitionClient : InferenceClientStub
  {
    public List<IReadOnlyList<byte>> UploadedContent { get; } = [];
    public List<string?> UploadedMediaTypes { get; } = [];

    /// <summary>成功 outcome 的 payload；子类可覆盖以模拟空文本/纯结构化结果。</summary>
    protected virtual Dictionary<string, JsonElement> OutcomePayload() => new()
    {
      ["raw_text"] = JsonSerializer.SerializeToElement("session text"),
    };

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      UploadedContent.AddRange(uploads.Values.Select(upload => upload.Content));
      UploadedMediaTypes.AddRange(uploads.Values.Select(upload => upload.ContentType));
      return Task.FromResult(new JobRef
      {
        JobId = "job-session",
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
            Payload = OutcomePayload(),
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  /// <summary>#110：真实成功但 raw_text 为空（如公式零输出）。</summary>
  private sealed class EmptyTextRecognitionClient : RecordingRecognitionClient
  {
    protected override Dictionary<string, JsonElement> OutcomePayload() => new()
    {
      ["raw_text"] = JsonSerializer.SerializeToElement(""),
    };
  }

  /// <summary>#110：空文本且仅有非文本结构化区块（公式）。</summary>
  private sealed class EmptyTextStructuredRecognitionClient : RecordingRecognitionClient
  {
    protected override Dictionary<string, JsonElement> OutcomePayload() => new()
    {
      ["raw_text"] = JsonSerializer.SerializeToElement(""),
      ["content_list"] = JsonSerializer.SerializeToElement(new object[]
      {
        new { type = "formula", text = "a+b", bbox = (int[]?)null, block_id = "formula-0" },
      }),
    };
  }

  private sealed class SubmitBlockingRecognitionClient : RecordingRecognitionClient
  {
    private readonly TaskCompletionSource completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => completion.Task;

    public void Complete() => completion.TrySetResult();

    public override async Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      _ = await base.SubmitAsync(request, uploads, cancellationToken);
      await completion.Task.WaitAsync(cancellationToken);
      return new JobRef
      {
        JobId = "job-session",
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
      };
    }
  }

  private static WorkbenchAnnotationLease UploadAnnotation(
    WorkbenchAnnotationStore store,
    byte[] png) =>
    store.UploadPngAsync(
        new MemoryStream(png),
        TestContext.Current.CancellationToken)
      .GetAwaiter().GetResult();

  private static DesktopWorkbenchCommandHandler CreateHandler(
    RecognitionViewModel recognition,
    string resourceRoot,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    SettingsViewModel? settings = null,
    IAnnotatedImagePlatform? platform = null,
    Func<bool>? inferenceAttached = null,
    Func<string?>? supervisorInstanceId = null,
    Func<RecognitionViewModel>? textLayerRecognitionFactory = null,
    Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?>? pinScreenshot = null) => new(
      () => recognition,
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      settings is null
        ? static () => throw new InvalidOperationException()
        : () => settings,
      () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker,
      resourceRoot,
      static () => 0,
      annotationStore,
      platform,
      inferenceAttached,
      supervisorInstanceId,
      textLayerRecognitionFactory,
      pinScreenshot);

  [Fact]
  public async Task TwoPinsOwnIndependentImagesAndShareOneExplicitTextTask()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var textRecognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      var pins = new List<WorkbenchAnnotationFile>();
      await using var handler = CreateHandler(
        recognition, root, broker, annotationStore, settings,
        inferenceAttached: () => true,
        supervisorInstanceId: () => "sup-pin",
        textLayerRecognitionFactory: () => textRecognition,
        pinScreenshot: (file, _, _, _) => pins.Add(file));
      try
      {
        using var capturedAwaiter = new RecognitionStateAwaiter(
          handler, state => state.ScreenshotSession is not null && !state.IsBusy);
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Guid sessionId = Guid.Parse((await capturedAwaiter.Task).ScreenshotSession!.SessionId);

        for (int index = 0; index < 2; index++)
        {
          WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
          WorkbenchCommandOutcome result = await handler.ExecuteAsync(
            new PinScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          Assert.Null(result.Error);
        }
        Assert.Equal(2, pins.Count);
        Assert.NotEqual(pins[0].Path, pins[1].Path);
        Assert.Empty(inference.Requests); // pure pin never starts OCR

        using (var readyAwaiter = new RecognitionStateAwaiter(
          handler, state => state.TextLayer?.Status == "textlayer.ready"))
        {
          await handler.PreparePinnedTextLayerAsync(sessionId, 0, pins[0].Path,
            TestContext.Current.CancellationToken);
          Assert.Equal("rapid_text", (await readyAwaiter.Task).TextLayer?.ModeId);
        }
        await handler.PreparePinnedTextLayerAsync(sessionId, 0, pins[1].Path,
          TestContext.Current.CancellationToken);
        Assert.Single(inference.Requests);

        string secondPath = pins[1].Path;
        pins[0].Dispose();
        Assert.True(File.Exists(secondPath));
        await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);
        Assert.Null(handler.InitialStates.OfType<RecognitionWorkbenchState>()
          .Single().TextLayer);
      }
      finally
      {
        foreach (WorkbenchAnnotationFile pin in pins) pin.Dispose();
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ClosedSessionPinRecognizesItsFrozenPngWithoutRestoringOldEditorLayer()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      WorkbenchAnnotationFile? pin = null;
      await using var handler = CreateHandler(
        recognition, root, broker, annotationStore, settings,
        inferenceAttached: () => true,
        supervisorInstanceId: () => "sup-pin",
        textLayerRecognitionFactory: () => new RecognitionViewModel(inference, inputs),
        pinScreenshot: (file, _, _, _) => pin = file);
      var invalidated = new List<(Guid SessionId, long Revision)>();
      var detachedSessions = new List<(Guid SessionId, long Revision)>();
      handler.ScreenshotTextLayerInvalidated += (id, revision) =>
        invalidated.Add((id, revision));
      handler.ScreenshotSessionDetached += (id, revision) =>
        detachedSessions.Add((id, revision));
      try
      {
        using var firstAwaiter = new RecognitionStateAwaiter(
          handler, state => state.ScreenshotSession is not null && !state.IsBusy);
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Guid firstSession = Guid.Parse((await firstAwaiter.Task).ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        Assert.Null((await handler.ExecuteAsync(
          new PinScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, firstSession, 0),
          TestContext.Current.CancellationToken)).Error);
        Assert.NotNull(pin);
        Assert.Empty(inference.Requests);

        await handler.ExecuteAsync(new CloseScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Assert.Empty(invalidated);
        Assert.Contains((firstSession, 0L), detachedSessions);
        using var nextAwaiter = new RecognitionStateAwaiter(
          handler, state => state.ScreenshotSession is not null && !state.IsBusy);
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Guid nextSession = Guid.Parse((await nextAwaiter.Task).ScreenshotSession!.SessionId);
        Assert.NotEqual(firstSession, nextSession);
        Assert.Empty(invalidated);

        RecognitionTextLayerState detached = Assert.IsType<RecognitionTextLayerState>(
          await handler.PreparePinnedTextLayerAsync(
            firstSession, 0, pin.Path, TestContext.Current.CancellationToken));
        Assert.Equal("textlayer.ready", detached.Status);
        Assert.Equal(firstSession.ToString("N"), detached.Binding?.SessionId);
        Assert.Equal(AnnotationPng, Assert.Single(inference.UploadedContent));
        Assert.Null(handler.InitialStates.OfType<RecognitionWorkbenchState>()
          .Single().TextLayer);
        Assert.Equal(nextSession.ToString("N"), handler.InitialStates
          .OfType<RecognitionWorkbenchState>().Single().ScreenshotSession?.SessionId);
        await handler.ExecuteAsync(new NotifyScreenshotSessionRevisionCommand(nextSession, 1),
          TestContext.Current.CancellationToken);
        Assert.Equal((nextSession, 0L), invalidated[^1]);
        Assert.DoesNotContain(invalidated, binding => binding.SessionId == firstSession);
      }
      finally { pin?.Dispose(); }
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  private static string TemporaryRoot()
  {
    string root = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-session-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
  }

  private sealed class RecognitionStateAwaiter : IDisposable
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly Func<RecognitionWorkbenchState, bool> predicate;
    private readonly TaskCompletionSource<RecognitionWorkbenchState> ready =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecognitionStateAwaiter(
      DesktopWorkbenchCommandHandler handler,
      Func<RecognitionWorkbenchState, bool> predicate)
    {
      this.handler = handler;
      this.predicate = predicate;
      handler.StateChanged += OnStateChanged;
    }

    public Task<RecognitionWorkbenchState> Task => ready.Task.WaitAsync(
      TimeSpan.FromSeconds(5),
      TestContext.Current.CancellationToken);

    private void OnStateChanged(WorkbenchState state)
    {
      if (state is RecognitionWorkbenchState recognition && predicate(recognition))
      {
        ready.TrySetResult(recognition);
      }
    }

    public void Dispose() => handler.StateChanged -= OnStateChanged;
  }

  [Fact]
  public async Task TextCaptureIntentDoesNotLeakIntoNextNativePureCapture()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new ThrowingSubmitClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition, root, broker, annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => true);

      using (var textAwaiter = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.ScreenshotSession?.TextSelectionRequested == true))
      {
        await handler.ExecuteAsync(new CaptureScreenshotTextSessionCommand(),
          TestContext.Current.CancellationToken);
        Assert.True((await textAwaiter.Task).ScreenshotSession!.TextSelectionRequested);
      }
      using (var pureAwaiter = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.ScreenshotSession is
        { TextSelectionRequested: false }))
      {
        // The tray/hotkey route calls this command without visiting the page.
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Assert.False((await pureAwaiter.Task).ScreenshotSession!.TextSelectionRequested);
      }
      Assert.Equal(2, inputs.CaptureCalls);
      Assert.Equal(0, inference.SubmitCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(false, true)]
  [InlineData(true, false)]
  [InlineData(true, true)]
  public async Task RejectedCaptureDoesNotPublishPreviousImage(bool scrolling, bool failed)
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new ThrowingSubmitClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(recognition, root, broker, annotationStore,
        settings: new SettingsViewModel(inference), inferenceAttached: () => false);
      using (var first = new RecognitionStateAwaiter(handler, state => !state.IsBusy))
      {
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Assert.NotNull((await first.Task).ScreenshotSession);
      }
      RecognitionInput? previous = recognition.CurrentInput;
      inputs.CancelCapture = !failed;
      inputs.FailCapture = failed;
      using var rejected = new RecognitionStateAwaiter(handler, state => !state.IsBusy);
      WorkbenchCommand command = scrolling
        ? new CaptureScrollingScreenshotCommand()
        : new CaptureScreenshotSessionCommand();
      await handler.ExecuteAsync(command, TestContext.Current.CancellationToken);
      RecognitionWorkbenchState state = await rejected.Task;
      Assert.Null(state.ScreenshotSession);
      Assert.Equal(failed ? "recognition.failed" : "recognition.cancelled", state.StatusCode);
      Assert.Same(previous, recognition.CurrentInput);
      Assert.Equal(0, inference.SubmitCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CaptureScreenshotSessionPublishesSessionWithoutAnyOcrSubmit()
  {
    string root = TemporaryRoot();
    try
    {
      // 任何推理提交都会让捕获失败：纯截图必须零 OCR/零安装请求。
      var inference = new ThrowingSubmitClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        WorkbenchCommandOutcome started = await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Assert.Null(started.Error);

        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Assert.Equal("recognition.session", captured.StatusCode);
        Assert.Equal(0, captured.ScreenshotSession!.Revision);
        Assert.False(captured.ScreenshotSession.TextSelectionRequested);
        Assert.NotNull(captured.Input);
        Assert.Null(captured.Result);
        Assert.Equal(1, inputs.CaptureCalls);
        Assert.False(recognition.IsBusy);
        Assert.Equal(0, inference.SubmitCalls);
        WorkbenchCommandOutcome modeChanged = await handler.ExecuteAsync(
          new SetTaskEngineCommand(null), TestContext.Current.CancellationToken);
        Assert.Null(modeChanged.Error);
        RecognitionWorkbenchState changed = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(modeChanged.States));
        Assert.Equal(captured.Input, changed.Input);
        Assert.Equal(captured.ScreenshotSession, changed.ScreenshotSession);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class ThrowingSubmitClient : InferenceClientStub
  {
    public int SubmitCalls;

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref SubmitCalls);
      throw new InvalidOperationException("pure capture must not submit OCR");
    }
  }

  [Fact]
  public async Task JpegCopyAndRecognitionConsumeTheSameEncodedSnapshot()
  {
    string root = TemporaryRoot();
    try
    {
      using var encoded = new InMemoryRandomAccessStream();
      BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded);
      encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        2, 1, 96, 96, [0, 0, 255, 255, 0, 255, 0, 255]);
      await encoder.FlushAsync();
      encoded.Seek(0);
      using var stream = encoded.AsStreamForRead();
      using var bytes = new MemoryStream();
      await stream.CopyToAsync(bytes, TestContext.Current.CancellationToken);
      byte[] jpeg = bytes.ToArray();
      var inference = new RecordingRecognitionClient();
      var recognition = new RecognitionViewModel(inference, new FixedCaptureInput());
      var platform = new RecordingAnnotatedImagePlatform();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(recognition, root, broker, annotations,
        new SettingsViewModel(inference), platform);
      Guid sessionId;
      using (var ready = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(new SelectImageEditFileCommand(), TestContext.Current.CancellationToken);
        sessionId = Guid.Parse((await ready.Task).ScreenshotSession!.SessionId);
      }
      WorkbenchAnnotationLease copy = await annotations.UploadImageAsync(new MemoryStream(jpeg),
        "image/jpeg", TestContext.Current.CancellationToken);
      Assert.Null((await handler.ExecuteAsync(new CopyScreenshotImageCommand(copy.ResourceUri.AbsoluteUri,
        sessionId, 0), TestContext.Current.CancellationToken)).Error);
      Assert.Equal(jpeg, platform.CopiedBytes);
      WorkbenchAnnotationLease ocr = await annotations.UploadImageAsync(new MemoryStream(jpeg),
        "image/jpeg", TestContext.Current.CancellationToken);
      using var completed = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.Result is not null);
      Assert.Null((await handler.ExecuteAsync(new RecognizeScreenshotImageCommand(ocr.ResourceUri.AbsoluteUri,
        sessionId, 0), TestContext.Current.CancellationToken)).Error);
      await completed.Task;
      Assert.Equal(jpeg, Assert.Single(inference.UploadedContent));
      Assert.Equal("image/jpeg", Assert.Single(inference.UploadedMediaTypes));
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task FileEditorUsesTheSameRevisionContractWithoutOpeningCaptureScene()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var recognition = new RecognitionViewModel(inference, new FixedCaptureInput());
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(recognition, root, broker, annotations);
      int scenes = 0;
      handler.ScreenshotSessionReady += (_, _) => scenes++;
      using var ready = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null);
      await handler.ExecuteAsync(new SelectImageEditFileCommand(),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState state = await ready.Task;
      Assert.False(state.ScreenshotSession!.SceneEditing);
      Assert.NotNull(state.Input);
      Assert.Empty(inference.UploadedContent);
      Assert.Equal(0, scenes);
      Guid sessionId = Guid.Parse(state.ScreenshotSession.SessionId);
      var changed = await handler.ExecuteAsync(new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
        TestContext.Current.CancellationToken);
      Assert.Equal(1, Assert.IsType<RecognitionWorkbenchState>(Assert.Single(changed.States)).ScreenshotSession!.Revision);
      await handler.ExecuteAsync(new CloseScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(recognition.CurrentInput);
      Assert.Null(handler.CurrentImageSessionId);
      var late = await handler.ExecuteAsync(new NotifyScreenshotSessionRevisionCommand(sessionId, 2),
        TestContext.Current.CancellationToken);
      Assert.Null(Assert.IsType<RecognitionWorkbenchState>(Assert.Single(late.States)).ScreenshotSession);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task RevisionNotifyInvalidatesResultAndRejectsStaleSessionCommands()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var platform = new RecordingAnnotatedImagePlatform();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        platform);

      Guid sessionId;
      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var completedAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null))
        {
          WorkbenchCommandOutcome recognized = await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          Assert.Null(recognized.Error);
          RecognitionWorkbenchState completed = await completedAwaiter.Task;
          Assert.Equal("recognition.completed", completed.StatusCode);
          Assert.NotNull(completed.Result);
        }
      }

      // 内容修订使旧结果失效（包括宿主持有的 resultActions）。
      WorkbenchCommandOutcome notify = await handler.ExecuteAsync(
        new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState notified = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(notify.States));
      Assert.Equal(1, notified.ScreenshotSession?.Revision);
      Assert.Null(notified.Result);
      Assert.False(recognition.HasResult);
      // 结果动作随修订一并失效：复制旧结果必须失败。
      WorkbenchCommandOutcome copyStaleResult = await handler.ExecuteAsync(
        new CopyRecognitionResultCommand("plain"),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", copyStaleResult.Error?.Code);

      // 旧 revision / 未知会话的识别与复制一律拒绝。
      WorkbenchAnnotationLease staleLease = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeScreenshotImageCommand(staleLease.ResourceUri.AbsoluteUri, sessionId, 0),
        TestContext.Current.CancellationToken)).Error?.Code);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeScreenshotImageCommand(
          staleLease.ResourceUri.AbsoluteUri, Guid.NewGuid(), 1),
        TestContext.Current.CancellationToken)).Error?.Code);
      WorkbenchAnnotationLease copyLease = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new CopyScreenshotImageCommand(copyLease.ResourceUri.AbsoluteUri, sessionId, 0),
        TestContext.Current.CancellationToken)).Error?.Code);

      // 当前 revision 的复制仍走原生 clipboard 并消费一次性租约。
      WorkbenchCommandOutcome copied = await handler.ExecuteAsync(
        new CopyScreenshotImageCommand(copyLease.ResourceUri.AbsoluteUri, sessionId, 1),
        TestContext.Current.CancellationToken);
      Assert.Null(copied.Error);
      Assert.Equal(AnnotationPng, platform.CopiedBytes);
      Assert.Throws<WorkbenchAnnotationAccessException>(() =>
        annotationStore.Take(copyLease.ResourceUri));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class RecordingAnnotatedImagePlatform : IAnnotatedImagePlatform
  {
    public byte[]? CopiedBytes { get; private set; }

    public async Task CopyImageAsync(string sourcePath, CancellationToken cancellationToken)
    {
      CopiedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
    }

    public Task<bool> SaveImageAsync(string sourcePath, CancellationToken cancellationToken) =>
      Task.FromResult(true);

    public string? CopiedText { get; private set; }
    public bool TextClipboardBusy { get; set; }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken)
    {
      if (TextClipboardBusy) throw new ClipboardBusyException();
      CopiedText = text;
      return Task.CompletedTask;
    }
  }

  [Fact]
  public async Task RecognizeSubmitsOnlyTheAnnotatedFinalPng()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        long inputBytes = captured.Input!.ByteLength;
        await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var completedAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 1),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState completed = await completedAwaiter.Task;

          // OCR 输入只能是编辑器导出的最终 PNG，不得回退未编辑基准图。
          Assert.Equal(AnnotationPng, Assert.Single(inference.UploadedContent));
          // 会话编辑基准（Input）保持稳定，不会被识别输入替换。
          Assert.Equal(inputBytes, completed.Input?.ByteLength);
          Assert.NotNull(completed.ScreenshotSession);
          Assert.Equal(1, completed.ScreenshotSession.Revision);
        }
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CompletedSessionRecognitionWithEmptyTextStaysTerminalWithoutFabricatedContent()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new EmptyTextRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var completedAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState completed = await completedAwaiter.Task;
          Assert.Equal("recognition.completed", completed.StatusCode);
          // 结果资源承载真实空文本：不造文本，也不得丢失终态可见性。
          Assert.Equal(0, completed.Result!.ByteLength);
          Assert.Null(completed.StructuredResult);
        }

        // 重算投影（命令出口的 SessionStatusCode）同样必须看到完成，
        // 不得退回 recognition.session 伪装仍在会话编辑中。
        WorkbenchCommandOutcome refreshed = await handler.ExecuteAsync(
          new SetTaskEngineCommand(null), TestContext.Current.CancellationToken);
        RecognitionWorkbenchState projected = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(refreshed.States));
        Assert.Equal("recognition.completed", projected.StatusCode);
        Assert.NotNull(projected.Result);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SessionStructuredBlocksSurviveEmptyTextResult()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new EmptyTextStructuredRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var completedAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState completed = await completedAwaiter.Task;
          Assert.Equal("recognition.completed", completed.StatusCode);
          // 空文本不得抑制非文本结构化区块（公式 LaTeX 区块）。
          Assert.NotNull(completed.StructuredResult);
          Assert.True(completed.StructuredResult!.ByteLength > 0);
          string structuredJson = await File.ReadAllTextAsync(
            Directory.EnumerateFiles(Path.Combine(root, "session"), "*.json").Single(),
            TestContext.Current.CancellationToken);
          using JsonDocument blocks = JsonDocument.Parse(structuredJson);
          Assert.Equal("a+b", blocks.RootElement[0].GetProperty("text").GetString());
        }
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task LateRecognitionAfterRevisionChangeIsDropped()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new SubmitBlockingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var busyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.IsBusy))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await busyAwaiter.Task;
        }

        // 识别在途时内容修订：旧响应不得覆盖新内容。
        WorkbenchCommandOutcome notify = await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState notified = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(notify.States));
        Assert.Equal(1, notified.ScreenshotSession?.Revision);
        Assert.False(recognition.IsBusy);

        var published = new List<RecognitionWorkbenchState>();
        void OnStateChanged(WorkbenchState state)
        {
          if (state is RecognitionWorkbenchState recognitionState)
            published.Add(recognitionState);
        }
        handler.StateChanged += OnStateChanged;
        try
        {
          inference.Complete();
          await handler.DisposeAsync();
        }
        finally
        {
          handler.StateChanged -= OnStateChanged;
        }
        Assert.DoesNotContain(published, state => state.Result is not null);
        Assert.DoesNotContain(published, state => state.ScreenshotSession is null);
        Assert.False(recognition.HasResult);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task FileRecognitionSupersedesScreenshotSession()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        // 新输入（文件识别）取代会话：旧会话命令立即失效。
        using (var fileAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null && state.ScreenshotSession is null))
        {
          await handler.ExecuteAsync(
            new SelectRecognitionImageCommand(),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState fileCompleted = await fileAwaiter.Task;
          Assert.Null(fileCompleted.ScreenshotSession);
        }

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        WorkbenchCommandOutcome stale = await handler.ExecuteAsync(
          new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
          TestContext.Current.CancellationToken);
        Assert.Equal("screenshot_session_stale", stale.Error?.Code);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task MaintenanceEventInvalidatesInFlightSessionRecognition()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new SubmitBlockingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var installer = new HangingInstallerClient();
      var settings = new SettingsViewModel(
        new MaintenanceHealthClient(),
        installerFactory: () => installer);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var busyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.IsBusy))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await busyAwaiter.Task;
        }

        // 运行环境维护事件使在途会话识别失效，不覆盖当前会话。
        await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
        using (var expiredAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.StatusCode == "recognition.expired"))
        {
          Task install = RunMaintenanceAsync(settings);
          RecognitionWorkbenchState expired = await expiredAwaiter.Task;
          Assert.NotNull(expired.ScreenshotSession);
          Assert.Equal(0, expired.ScreenshotSession.Revision);
          Assert.Null(expired.Result);
          Assert.False(recognition.IsBusy);

          installer.Release();
          await install;
        }
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task PrepareScreenshotTextLayerRunsFixedLightweightLocalMode()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var textRecognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true,
        supervisorInstanceId: () => "sup-text",
        textLayerRecognitionFactory: () => textRecognition);

      Guid sessionId;
      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var readyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.TextLayer?.Status == "textlayer.ready"))
        {
          await handler.ExecuteAsync(
            new PrepareScreenshotTextLayerCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState ready = await readyAwaiter.Task;

          RecognitionTextLayerState layer = ready.TextLayer!;
          Assert.Equal("rapid_text", layer.ModeId);
          Assert.Equal("sup-text", layer.ServiceInstance);
          Assert.NotNull(layer.Image);
          Assert.Equal(AnnotationPng.Length, layer.Image!.ByteLength);
          RecognitionTextLayerLine[] lines = [.. layer.Lines!];
          Assert.Equal(2, lines.Length);
          Assert.Equal("你好", lines[0].Text);
          Assert.Equal(10, lines[0].X1);
          Assert.Equal(60, lines[0].Y2);
          Assert.Equal(1, lines[1].Order);
        }
      }

      // 固定轻量配置：OCR 管线、rapidocr 引擎、空选项/空 mineru；
      // 输入只能是编辑器导出的最终 PNG。
      SubmitRequest submit = Assert.Single(inference.Requests);
      Assert.Equal(JobKind.Recognition, submit.Kind);
      Assert.Equal(JobPriority.Interactive, submit.Priority);
      Assert.Equal("OCR", submit.Pipeline.PipelineId);
      Assert.Empty(submit.Pipeline.Options);
      Assert.Equal(OcrEngine.RapidOcr, submit.Pipeline.Engine);
      Assert.Equal(AnnotationPng, Assert.Single(inference.UploadedContent));
      // 独立提交通道：用户任务级 TaskEngine 不被污染。
      Assert.Null(recognition.TaskEngine);
      Assert.False(recognition.HasResult);
      Assert.True(textRecognition.HasResult);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task TextLayerPrefersUserTaskEngineAndIgnoresUserChangesAfterReady()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var textRecognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true,
        textLayerRecognitionFactory: () => textRecognition);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        // 目录加载是任务级偏好写入的前置（纯截图捕获不加载目录）。
        await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
        // 用户偏好 windows_text：合法的 ready 本地文字模式，优先采用。
        WorkbenchCommandOutcome taskEngineSet = await handler.ExecuteAsync(
          new SetTaskEngineCommand("windows_text"),
          TestContext.Current.CancellationToken);
        Assert.Null(taskEngineSet.Error);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var readyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.TextLayer?.Status == "textlayer.ready"))
        {
          await handler.ExecuteAsync(
            new PrepareScreenshotTextLayerCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState ready = await readyAwaiter.Task;
          Assert.Equal("windows_text", ready.TextLayer?.ModeId);
        }
        Assert.Equal(OcrEngine.Windows, Assert.Single(inference.Requests).Pipeline.Engine);

        // 准备后改用户任务级选项：既不重跑文字层也不使已就绪层失效（配置固定）。
        WorkbenchCommandOutcome changed = await handler.ExecuteAsync(
          new SetTaskEngineCommand("paddle_text"),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState afterChange = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(changed.States));
        Assert.Equal("textlayer.ready", afterChange.TextLayer?.Status);
        Assert.Equal("windows_text", afterChange.TextLayer?.ModeId);
        Assert.Single(inference.Requests);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task PrepareTextLayerWithoutReadyLocalModeReportsUnavailable()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient(rapidReady: false, windowsReady: false);
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        WorkbenchCommandOutcome prepared = await handler.ExecuteAsync(
          new PrepareScreenshotTextLayerCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState unavailable = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(prepared.States));
        Assert.Equal("textlayer.unavailable", unavailable.TextLayer?.Status);
        Assert.Equal("textlayer.modeNotReady", unavailable.TextLayer?.Reason);
        Assert.Null(unavailable.TextLayer?.Image);
        Assert.Equal(0, inference.SubmitCalls);
        // 不可用时不消费上传租约，也不隐式准备依赖。
        Assert.NotNull(annotationStore.Take(lease.ResourceUri));
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task EmptyLocalOcrReportsNoSelectableTextForEditorAndDetachedPin()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient(noText: true);
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition, root, broker, annotationStore, settings,
        inferenceAttached: () => true,
        textLayerRecognitionFactory: () => new RecognitionViewModel(inference, inputs));
      using var capturedAwaiter = new RecognitionStateAwaiter(
        handler, state => state.ScreenshotSession is not null && !state.IsBusy);
      await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Guid sessionId = Guid.Parse((await capturedAwaiter.Task).ScreenshotSession!.SessionId);
      WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
      using var emptyAwaiter = new RecognitionStateAwaiter(
        handler, state => state.TextLayer?.Status == "textlayer.empty");
      await handler.ExecuteAsync(
        new PrepareScreenshotTextLayerCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
        TestContext.Current.CancellationToken);
      RecognitionTextLayerState empty = (await emptyAwaiter.Task).TextLayer!;
      Assert.Equal("textlayer.noLines", empty.Reason);
      Assert.Null(empty.Lines);

      await handler.ExecuteAsync(new CloseScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      string imagePath = Path.Combine(root, "empty-pin.png");
      await File.WriteAllBytesAsync(imagePath, AnnotationPng,
        TestContext.Current.CancellationToken);
      PinnedTextPreparationException error = await Assert.ThrowsAsync<PinnedTextPreparationException>(
        () => handler.PreparePinnedTextLayerAsync(
          sessionId, 0, imagePath, TestContext.Current.CancellationToken));
      Assert.Equal("贴图中没有可选择的文字。", error.Message);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Theory]
  [InlineData("revision")]
  [InlineData("close")]
  [InlineData("replace")]
  public async Task ExpiringTextLayerReleasesItsFileAndLeaseWithoutReleasingTheCurrentInput(string action)
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(recognition, root, broker, annotations,
        new SettingsViewModel(inference), inferenceAttached: () => true,
        textLayerRecognitionFactory: () => new RecognitionViewModel(inference, inputs));
      RecognitionWorkbenchState captured;
      using (var capture = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(), TestContext.Current.CancellationToken);
        captured = await capture.Task;
      }
      Guid id = Guid.Parse(captured.ScreenshotSession!.SessionId);
      WorkbenchAnnotationLease upload = UploadAnnotation(annotations, AnnotationPng);
      RecognitionWorkbenchState ready;
      using (var layer = new RecognitionStateAwaiter(handler, state => state.TextLayer?.Status == "textlayer.ready"))
      {
        await handler.ExecuteAsync(new PrepareScreenshotTextLayerCommand(upload.ResourceUri.AbsoluteUri, id, 0),
          TestContext.Current.CancellationToken);
        ready = await layer.Task;
      }
      WorkbenchResourceReference image = ready.TextLayer!.Image!;
      Assert.NotEqual(captured.Input!.Url, image.Url);
      string layerPath = Directory.GetFiles(Path.Combine(root, "session"), "*.png").Single();
      await using WorkbenchResourceResponse inFlight = await broker.OpenAsync(new Uri(image.Url),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState current;
      if (action == "replace")
      {
        using var replacement = new RecognitionStateAwaiter(handler,
          state => !state.IsBusy && state.ScreenshotSession is { } session &&
            session.SessionId != captured.ScreenshotSession.SessionId);
        await handler.ExecuteAsync(new SelectImageEditFileCommand(), TestContext.Current.CancellationToken);
        current = await replacement.Task;
      }
      else
      {
        WorkbenchCommand command = action == "close" ? new CloseScreenshotSessionCommand()
          : new NotifyScreenshotSessionRevisionCommand(id, 1);
        var outcome = await handler.ExecuteAsync(command, TestContext.Current.CancellationToken);
        current = Assert.IsType<RecognitionWorkbenchState>(Assert.Single(outcome.States));
      }
      Assert.Null(current.TextLayer);
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
        await broker.OpenAsync(new Uri(image.Url), TestContext.Current.CancellationToken));
      using var oldBytes = new MemoryStream();
      await inFlight.Content.CopyToAsync(oldBytes, TestContext.Current.CancellationToken);
      Assert.Equal(AnnotationPng, oldBytes.ToArray());
      await inFlight.DisposeAsync();
      Assert.False(File.Exists(layerPath));
      if (action != "close")
      {
        Assert.NotNull(current.Input);
        if (action == "revision") Assert.Equal(captured.Input.Url, current.Input!.Url);
        await using var input = await broker.OpenAsync(new Uri(current.Input!.Url),
          TestContext.Current.CancellationToken);
        using var currentBytes = new MemoryStream();
        await input.Content.CopyToAsync(currentBytes, TestContext.Current.CancellationToken);
        Assert.Equal(CaptureBytes, currentBytes.ToArray());
      }
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  private sealed class LeaseClock : TimeProvider
  {
    public Action? BeforeLease;
    public override DateTimeOffset GetUtcNow()
    {
      Action? callback = Interlocked.Exchange(ref BeforeLease, null);
      callback?.Invoke();
      return DateTimeOffset.UtcNow;
    }
  }

  [Fact]
  public async Task RevisionChangingDuringTextImagePublicationDeletesTheLateFile()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var clock = new LeaseClock();
      using var broker = new WorkbenchResourceBroker(root, clock);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(new RecognitionViewModel(inference, inputs),
        root, broker, annotations, new SettingsViewModel(inference), inferenceAttached: () => true,
        textLayerRecognitionFactory: () => new RecognitionViewModel(inference, inputs));
      RecognitionWorkbenchState captured;
      using (var capture = new RecognitionStateAwaiter(handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(), TestContext.Current.CancellationToken);
        captured = await capture.Task;
      }
      Guid id = Guid.Parse(captured.ScreenshotSession!.SessionId);
      using var watcher = new FileSystemWatcher(Path.Combine(root, "session"), "*.png");
      var deleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      watcher.Deleted += (_, args) => deleted.TrySetResult(args.FullPath);
      watcher.EnableRaisingEvents = true;
      clock.BeforeLease = () => handler.ExecuteAsync(new NotifyScreenshotSessionRevisionCommand(id, 1),
        TestContext.Current.CancellationToken).GetAwaiter().GetResult();
      WorkbenchAnnotationLease upload = UploadAnnotation(annotations, AnnotationPng);
      await handler.ExecuteAsync(new PrepareScreenshotTextLayerCommand(upload.ResourceUri.AbsoluteUri, id, 0),
        TestContext.Current.CancellationToken);
      string staleFile = await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken);
      Assert.False(File.Exists(staleFile));
      Assert.Empty(Directory.GetFiles(Path.Combine(root, "session"), "*.png"));
      await using var baseline = await broker.OpenAsync(new Uri(captured.Input!.Url),
        TestContext.Current.CancellationToken);
      Assert.Equal(CaptureBytes.Length, baseline.ContentLength);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task PrepareTextLayerDeduplicatesSameRevision()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var textRecognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true,
        textLayerRecognitionFactory: () => textRecognition);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        WorkbenchAnnotationLease first = UploadAnnotation(annotationStore, AnnotationPng);
        using (var readyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.TextLayer?.Status == "textlayer.ready"))
        {
          await handler.ExecuteAsync(
            new PrepareScreenshotTextLayerCommand(first.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await readyAwaiter.Task;
        }

        WorkbenchAnnotationLease second = UploadAnnotation(annotationStore, AnnotationPng);
        WorkbenchCommandOutcome again = await handler.ExecuteAsync(
          new PrepareScreenshotTextLayerCommand(second.ResourceUri.AbsoluteUri, sessionId, 0),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState deduped = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(again.States));
        Assert.Equal("textlayer.ready", deduped.TextLayer?.Status);
        Assert.Equal(1, inference.SubmitCalls);
        // 去重发生在消费租约之前：第二次上传未被消费。
        Assert.NotNull(annotationStore.Take(second.ResourceUri));
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task LateTextLayerAfterRevisionChangeIsDropped()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new SubmitBlockingTextLayerClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var textRecognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true,
        textLayerRecognitionFactory: () => textRecognition);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var preparingAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.TextLayer?.Status == "textlayer.preparing"))
        {
          await handler.ExecuteAsync(
            new PrepareScreenshotTextLayerCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await preparingAwaiter.Task;
        }

        // 编辑推进修订：旧层立即清除，在途准备不得回填旧结果。
        await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);

        var published = new List<RecognitionWorkbenchState>();
        void OnStateChanged(WorkbenchState state)
        {
          if (state is RecognitionWorkbenchState recognitionState)
            published.Add(recognitionState);
        }
        handler.StateChanged += OnStateChanged;
        try
        {
          inference.Release();
          await handler.DisposeAsync();
        }
        finally
        {
          handler.StateChanged -= OnStateChanged;
        }
        Assert.DoesNotContain(published, state => state.TextLayer?.Status == "textlayer.ready");
        Assert.DoesNotContain(published, state => state.TextLayer?.Lines is not null);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CopyScreenshotSelectionRequiresReadyLayerForCurrentRevision()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new TextLayerRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var textRecognition = new RecognitionViewModel(inference, inputs);
      var settings = new SettingsViewModel(inference);
      var platform = new RecordingAnnotatedImagePlatform();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        platform,
        inferenceAttached: () => true,
        textLayerRecognitionFactory: () => textRecognition);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        // 未就绪时复制选区 fail closed。
        WorkbenchCommandOutcome tooEarly = await handler.ExecuteAsync(
          new CopyScreenshotSelectionCommand(sessionId, 0, "旧"),
          TestContext.Current.CancellationToken);
        Assert.Equal("screenshot_session_stale", tooEarly.Error?.Code);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var readyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.TextLayer?.Status == "textlayer.ready"))
        {
          await handler.ExecuteAsync(
            new PrepareScreenshotTextLayerCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await readyAwaiter.Task;
        }

        WorkbenchCommandOutcome copied = await handler.ExecuteAsync(
          new CopyScreenshotSelectionCommand(sessionId, 0, "好世"),
          TestContext.Current.CancellationToken);
        Assert.Null(copied.Error);
        Assert.Equal("好世", platform.CopiedText);
        platform.TextClipboardBusy = true;
        WorkbenchCommandOutcome busy = await handler.ExecuteAsync(
          new CopyScreenshotSelectionCommand(sessionId, 0, "好世"),
          TestContext.Current.CancellationToken);
        Assert.Equal("clipboard_busy", busy.Error?.Code);
        Assert.Equal("workbench.error.clipboardBusy", busy.Error?.MessageKey);
        platform.TextClipboardBusy = false;

        // 修订前进后：旧 revision 与新 revision 都不能复制旧层内容。
        await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);
        Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
          new CopyScreenshotSelectionCommand(sessionId, 0, "旧"),
          TestContext.Current.CancellationToken)).Error?.Code);
        Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
          new CopyScreenshotSelectionCommand(sessionId, 1, "旧"),
          TestContext.Current.CancellationToken)).Error?.Code);
        Assert.Equal("好世", platform.CopiedText);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static async Task RunMaintenanceAsync(SettingsViewModel settings)
  {
    await settings.PreviewInstallAsync(TestContext.Current.CancellationToken);
    await settings.ConfirmInstallAsync(
      settings.Maintenance.Plan!.PlanId,
      TestContext.Current.CancellationToken);
  }

  private class TextModeHealthClient(bool rapidReady = true, bool windowsReady = true)
    : InferenceClientStub
  {
    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken) =>
      throw new InvalidOperationException("health-only client must not submit");

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new Wire.Health
      {
        SchemaVersion = 2,
        InstanceId = "sup-text",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities = [RuntimeSelectionService.RecognitionModesCapability],
        CapabilityDescriptors =
        [
          new Wire.CapabilityDescriptor
          {
            Name = RuntimeSelectionService.RecognitionModesCapability,
            Lifecycle = "active",
            IntroducedIn = "2.9.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            RecognitionModeCatalog = new Wire.RecognitionModeCatalog
            {
              Modes =
              [
                TextMode(Wire.RecognitionModeId.RapidText,
                  rapidReady
                    ? Wire.RecognitionModeAvailability.Ready
                    : Wire.RecognitionModeAvailability.PreparationRequired,
                  "rapidocr-base"),
                TextMode(Wire.RecognitionModeId.WindowsText,
                  windowsReady
                    ? Wire.RecognitionModeAvailability.Ready
                    : Wire.RecognitionModeAvailability.Unavailable,
                  null),
                TextMode(Wire.RecognitionModeId.PaddleText,
                  Wire.RecognitionModeAvailability.Ready,
                  null),
                TextMode(Wire.RecognitionModeId.PaddleStructure,
                  Wire.RecognitionModeAvailability.Ready,
                  null),
                TextMode(Wire.RecognitionModeId.PaddleDocumentVl,
                  Wire.RecognitionModeAvailability.Ready,
                  null),
                TextMode(Wire.RecognitionModeId.MineruDocument,
                  Wire.RecognitionModeAvailability.Ready,
                  null),
                TextMode(Wire.RecognitionModeId.PaddleTable,
                  Wire.RecognitionModeAvailability.Ready,
                  null),
                TextMode(Wire.RecognitionModeId.PaddleFormula,
                  Wire.RecognitionModeAvailability.Ready,
                  null),
              ],
            },
          },
        ],
      });

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());

    private static Wire.RecognitionModeDescriptor TextMode(
      Wire.RecognitionModeId id,
      Wire.RecognitionModeAvailability availability,
      string? requiredComponent)
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
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
      };
      return new Wire.RecognitionModeDescriptor
      {
        Id = id,
        Family = family,
        PipelineId = pipeline,
        Engine = engine,
        Provisioning = provisioning,
        Availability = availability,
        ReasonCode = availability == Wire.RecognitionModeAvailability.PreparationRequired
          ? "runtime_component_missing"
          : null,
        RequiredComponent = requiredComponent,
        SupportedOptions = [],
        Lifecycle = new Wire.RecognitionModeLifecycle
        {
          Kind = lifecycle,
          SupportsPreload = false,
          SupportsTtl = false,
          SupportsPinning = false,
          SupportsRelease = false,
        },
      };
    }
  }

  /// <summary>Records submissions; outcomes carry real text_blocks geometry.</summary>
  private class TextLayerRecognitionClient(
    bool rapidReady = true, bool windowsReady = true, bool noText = false)
    : TextModeHealthClient(rapidReady, windowsReady)
  {
    public List<SubmitRequest> Requests { get; } = [];
    public List<IReadOnlyList<byte>> UploadedContent { get; } = [];
    public List<string?> UploadedMediaTypes { get; } = [];
    private int submitCalls;

    public int SubmitCalls => submitCalls;

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref submitCalls);
      Requests.Add(request);
      UploadedContent.AddRange(uploads.Values.Select(upload => upload.Content));
      UploadedMediaTypes.AddRange(uploads.Values.Select(upload => upload.ContentType));
      return Task.FromResult(new JobRef
      {
        JobId = "job-text",
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
              ["raw_text"] = JsonSerializer.SerializeToElement(noText ? "" : "你好\nworld"),
              ["text_blocks"] = noText
                ? JsonSerializer.SerializeToElement(Array.Empty<object>())
                : JsonSerializer.SerializeToElement(
                  JsonSerializer.Deserialize<JsonElement>(""""
                  [
                    { "text": "你好", "bbox": [10, 20, 300, 60], "order": 0 },
                    { "text": "world", "bbox": [10, 80, 300, 120], "order": 1 },
                    { "text": "越界框", "bbox": [0, 0, 2000, 100], "order": 2 },
                    { "text": "退化框", "bbox": [10, 10, 10, 50], "order": 3 }
                  ]
                  """")),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  private sealed class SubmitBlockingTextLayerClient : TextLayerRecognitionClient
  {
    private readonly TaskCompletionSource completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => completion.Task;

    public void Release() => completion.TrySetResult();

    public override async Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      JobRef submitted = await base.SubmitAsync(request, uploads, cancellationToken);
      await completion.Task.WaitAsync(cancellationToken);
      return submitted;
    }
  }

  private sealed class HangingInstallerClient : IRuntimeInstallerClient
  {
    private readonly TaskCompletionSource completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SupportsInstallPlan => true;

    public void Release() => completion.TrySetResult();

    public Task<Host.RuntimeInstallPlan> PreviewInstallAsync(
      RuntimeInstallSelection selection,
      string accelerator,
      CancellationToken cancellationToken = default) => Task.FromResult(
      new Host.RuntimeInstallPlan
      {
        PlanId = "session-plan",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
        Accelerator = accelerator == "cpu"
          ? Host.Accelerator.Cpu
          : Host.Accelerator.NvidiaCuda,
        ProfileId = "win-x64-cpu",
        RequestedComponentIds = selection.InstallComponentIds,
        EffectiveComponentIds = selection.InstallComponentIds ?? [],
        RequestedDownloadSourceIds = selection.DownloadSourceIds,
        EffectiveDownloadSourceIds = selection.DownloadSourceIds ?? [],
        Source = new Host.RuntimeSourceIdentity
        {
          BackendVersion = "0.14.0",
          BackendSourceSha = "test",
          RuntimeManifestSha256 = "test",
        },
        Components = [],
        Blockers = [],
        Cost = new Host.RuntimeInstallPlanCost
        {
          DownloadBytes = null,
          AdditionalDiskBytes = null,
          UnknownReasonCodes = [],
        },
      });

    public Task<RuntimeLaunch> ConfirmInstallAsync(
      string planId,
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) => HangAsync(operationId, progress, cancellationToken);

    public Task<RuntimeLaunch> EnsureAsync(
      RuntimeInstallSelection? selection,
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) => HangAsync(operationId, progress, cancellationToken);

    private async Task<RuntimeLaunch> HangAsync(
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress,
      CancellationToken cancellationToken)
    {
      // 维护运行状态由协调器 SetState 驱动；这里只需挂起保持 IsRunning。
      await completion.Task.WaitAsync(cancellationToken);
      return new RuntimeLaunch(
        @"C:\store\python.exe",
        "vibeocr.runtime.host.main",
        @"C:\store",
        @"C:\store\models",
        new Dictionary<string, string>());
    }

    public Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> EnsureAsync(
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> RepairAsync(
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeHostEnvelope> CancelAsync(
      string operationId,
      string commandId,
      long? expectedSequence = null,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeHostEnvelope> RetryAsync(
      string operationId,
      string newOperationId,
      string commandId,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeHostEnvelope> RetryAsync(
      string operationId,
      string newOperationId,
      RuntimeInstallSelection? selection,
      string commandId,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeMaintenanceObserveEnvelope> ObserveAsync(
      string operationId,
      long afterSequence,
      int limit = 128,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();
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

  /// <summary>Health/settings for the maintenance path; jobs never run here.</summary>
  private sealed class MaintenanceHealthClient : InferenceClientStub
  {
    public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new ResidencyStatus());

    public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new RuntimeStatusSnapshot
      {
        InstanceId = "sup-session",
        ServiceState = RuntimeServiceState.Ready,
        BackendVersion = "0.14.0",
        Profile = new RuntimeProfileStatus
        {
          ProfileId = "win-x64-cpu",
          Accelerator = RuntimeAccelerator.Cpu,
          Components = [],
        },
      });

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new Wire.Health
      {
        SchemaVersion = 2,
        InstanceId = "sup-session",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities =
        [
          RuntimeSelectionService.DownloadSourceCapability,
          RuntimeSelectionService.ComponentSelectionCapability,
        ],
        CapabilityDescriptors =
        [
          new Wire.CapabilityDescriptor
          {
            Name = RuntimeSelectionService.DownloadSourceCapability,
            Lifecycle = "active",
            IntroducedIn = "2.7.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            DownloadSourceCatalog = new Wire.DownloadSourceCatalog
            {
              Sources =
              [
                new Wire.DownloadSourceDescriptor
                {
                  Kind = "package_index",
                  Id = "tuna-pypi",
                  Endpoint = "https://mirrors.tuna.example/pypi/simple",
                },
              ],
            },
          },
          new Wire.CapabilityDescriptor
          {
            Name = RuntimeSelectionService.ComponentSelectionCapability,
            Lifecycle = "active",
            IntroducedIn = "2.7.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            ComponentVariantCatalog = new Wire.ComponentVariantCatalog
            {
              Variants =
              [
                new Wire.ComponentVariantDescriptor
                {
                  FeatureId = "document_parsing",
                  Accelerator = "cpu",
                  ComponentId = "document_parsing",
                },
              ],
            },
          },
        ],
      });

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());
  }
}
