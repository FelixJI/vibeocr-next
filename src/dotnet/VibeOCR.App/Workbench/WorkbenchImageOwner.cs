using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchImageOwner(IAnnotatedImagePlatform annotatedImagePlatform, WorkbenchAnnotationStore annotationStore, Func<CancellationToken, Task>? ensureRuntimeReady, Func<bool>? inferenceAttached, PortableLayout? optionsLayout, Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?, IReadOnlyList<WorkbenchExclusionBox>>? pinScreenshot, Func<RecognitionViewModel> recognitionFactory, WorkbenchResourceBroker resourceBroker, ShellActionDispatcher? shellActions, IStructuredClipboardPlatform structuredClipboard, Func<string?> supervisorInstanceId, Func<RecognitionViewModel> textLayerRecognitionFactory, IResultActionPlatform resultActionPlatform, WorkbenchSettingsOwner settingsOwner, WorkbenchResourcesOwner resources, WorkbenchSelectionOwner selectionPolicy, WorkbenchOperations operations, Action synchronizeBatchMode)
{
  internal event Action? ScreenshotSelectionSettingsRequested;
  internal event Action? ScreenshotSelectionRecognized;
  internal event Action<Guid>? ScreenshotSceneRecognitionHandoff;
  internal event Action? ScreenshotCaptureFinished;
  internal event Action? ScreenshotCaptureStarting;
  public event Action<Guid, VibeOCR.Platform.Windows.PhysicalRectangle?>? ScreenshotSessionReady;
  public event Action<Guid, long>? ScreenshotSessionDetached;
  public event Action<Guid, long>? ScreenshotTextLayerInvalidated;
  public event Action<RecognitionTextLayerState?>? ScreenshotTextLayerChanged;
  internal bool Handles(WorkbenchCommand command) => command is SelectRecognitionImageCommand or RecognizeDroppedFileCommand or ReadRecognitionClipboardCommand or CaptureRecognitionScreenCommand or SelectImageEditFileCommand or ReadImageEditClipboardCommand or OpenDroppedImageEditFileCommand or OpenRecognitionImageForEditCommand or PasteRecognitionImageForEditCommand or CaptureScreenshotSessionCommand or CaptureScreenshotTextSessionCommand or CaptureScrollingScreenshotCommand or CloseScreenshotSessionCommand or CloseImageEditSessionCommand or NotifyScreenshotSessionRevisionCommand or NotifyImageEditRevisionCommand or CopyScreenshotImageCommand or CopyImageEditImageCommand or SaveScreenshotImageCommand or SaveImageEditImageCommand or PinScreenshotImageCommand or PinImageEditImageCommand or RecognizeScreenshotImageCommand or RecognizeImageEditImageCommand or PrepareScreenshotTextLayerCommand or PrepareImageEditTextLayerCommand or CancelScreenshotTextLayerCommand or CancelImageEditTextLayerCommand or CopyScreenshotSelectionCommand or CopyImageEditSelectionCommand or CancelRecognitionCommand or CopyRecognitionResultCommand or CopyStructuredResultCommand or CopyRecognitionLinesCommand or CopyStructuredCellsCommand or UpdateRecognitionLineCommand or ExportRecognitionResultCommand or CopyAnnotatedImageCommand or CopyImageEditAnnotatedImageCommand or SaveAnnotatedImageCommand or SaveImageEditAnnotatedImageCommand or SetTaskEngineCommand or SetRecognitionOptionsCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    SelectRecognitionImageCommand => StartRecognition(
      viewModel => viewModel.RecognizeFileAsync(
        cancellationToken, selectionPolicy.AwaitSelectionBeforeFreeze()),
      cancellationToken),
    RecognizeDroppedFileCommand dropped => StartRecognition(
      viewModel => viewModel.RecognizeDroppedFileAsync(
        dropped.Path,
        cancellationToken,
        selectionPolicy.AwaitSelectionBeforeFreeze()),
      cancellationToken),
    ReadRecognitionClipboardCommand => StartRecognition(
      viewModel => viewModel.RecognizeClipboardAsync(
        cancellationToken, selectionPolicy.AwaitSelectionBeforeFreeze()),
      cancellationToken),
    // 截图识别入口（页面按钮/快捷键/托盘）与纯截图同一链路：框选后
    // 复用选区动作栏；直接确认进入现场编辑会话，不再隐式提交识别。
    CaptureRecognitionScreenCommand => StartScreenshotSession(false, cancellationToken),
    SelectImageEditFileCommand => StartImageEdit(
      imageEditChannel,
      viewModel => viewModel.OpenImageForEditAsync(cancellationToken), cancellationToken),
    ReadImageEditClipboardCommand => StartImageEdit(
      imageEditChannel,
      viewModel => viewModel.PasteImageForEditAsync(cancellationToken), cancellationToken),
    OpenDroppedImageEditFileCommand drop => StartImageEdit(
      imageEditChannel,
      viewModel => viewModel.DropImageForEditAsync(drop.Path, cancellationToken), cancellationToken),
    OpenRecognitionImageForEditCommand => StartImageEdit(
      recognitionChannel,
      viewModel => viewModel.OpenImageForEditAsync(cancellationToken), cancellationToken),
    PasteRecognitionImageForEditCommand => StartImageEdit(
      recognitionChannel,
      viewModel => viewModel.PasteImageForEditAsync(cancellationToken), cancellationToken),
    CaptureScreenshotSessionCommand => StartScreenshotSession(false, cancellationToken),
    CaptureScreenshotTextSessionCommand => StartScreenshotSession(true, cancellationToken),
    CaptureScrollingScreenshotCommand => StartScreenshotSession(false, cancellationToken, scrolling: true),
    CloseScreenshotSessionCommand => CloseScreenshotSession(recognitionChannel),
    CloseImageEditSessionCommand => CloseScreenshotSession(imageEditChannel),
    NotifyScreenshotSessionRevisionCommand notify => NotifyScreenshotRevision(
      recognitionChannel, notify),
    NotifyImageEditRevisionCommand notify => NotifyScreenshotRevision(
      imageEditChannel, notify),
    CopyScreenshotImageCommand copy => await CopyScreenshotImageAsync(
      recognitionChannel,
      copy,
      cancellationToken),
    CopyImageEditImageCommand copy => await CopyScreenshotImageAsync(
      imageEditChannel,
      copy,
      cancellationToken),
    SaveScreenshotImageCommand save => await SaveScreenshotImageAsync(
      recognitionChannel,
      save,
      cancellationToken),
    SaveImageEditImageCommand save => await SaveScreenshotImageAsync(
      imageEditChannel,
      save,
      cancellationToken),
    PinScreenshotImageCommand pin => PinScreenshotImage(recognitionChannel, pin),
    PinImageEditImageCommand pin => PinScreenshotImage(imageEditChannel, pin),
    RecognizeScreenshotImageCommand recognize => await StartScreenshotRecognitionAsync(
      recognitionChannel,
      recognize,
      cancellationToken),
    RecognizeImageEditImageCommand recognize => await StartImageEditRecognitionAsync(
      recognize,
      cancellationToken),
    PrepareScreenshotTextLayerCommand prepare => await PrepareScreenshotTextLayerAsync(
      recognitionChannel,
      prepare,
      cancellationToken),
    PrepareImageEditTextLayerCommand prepare => await PrepareScreenshotTextLayerAsync(
      imageEditChannel,
      prepare,
      cancellationToken),
    CancelScreenshotTextLayerCommand cancelTextLayer => CancelScreenshotTextLayer(
      recognitionChannel, cancelTextLayer),
    CancelImageEditTextLayerCommand cancelTextLayer => CancelScreenshotTextLayer(
      imageEditChannel, cancelTextLayer),
    CopyScreenshotSelectionCommand copySelection => await CopyScreenshotSelectionAsync(
      recognitionChannel,
      copySelection,
      cancellationToken),
    CopyImageEditSelectionCommand copySelection => await CopyScreenshotSelectionAsync(
      imageEditChannel,
      copySelection,
      cancellationToken),
    CancelRecognitionCommand => CancelRecognition(),
    CopyRecognitionResultCommand copy => await CopyRecognitionAsync(
      copy,
      cancellationToken),
    CopyStructuredResultCommand copyStructured => await CopyStructuredResultAsync(
      copyStructured,
      cancellationToken),
    CopyRecognitionLinesCommand copyLines => await CopyRecognitionLinesAsync(
      copyLines,
      cancellationToken),
    CopyStructuredCellsCommand copyCells => await CopyStructuredCellsAsync(
      copyCells,
      cancellationToken),
    UpdateRecognitionLineCommand updateLine => await UpdateRecognitionLineAsync(
      updateLine,
      cancellationToken),
    ExportRecognitionResultCommand export => await ExportRecognitionAsync(
      export,
      cancellationToken),
    CopyAnnotatedImageCommand copy => await CopyAnnotatedImageAsync(
      copy,
      cancellationToken),
    CopyImageEditAnnotatedImageCommand copy => await CopyImageEditAnnotatedImageAsync(
      copy,
      cancellationToken),
    SaveAnnotatedImageCommand save => await SaveAnnotatedImageAsync(
      save,
      cancellationToken),
    SaveImageEditAnnotatedImageCommand save => await SaveImageEditAnnotatedImageAsync(
      save,
      cancellationToken),
    SetTaskEngineCommand taskEngine => SetTaskEngine(taskEngine),
    SetRecognitionOptionsCommand options => SetRecognitionOptions(options),
    _ => throw new InvalidOperationException("Unsupported Image workbench command."),
  };

  internal sealed class ImageSessionChannel
  {
    public RecognitionViewModel? ViewModel;
    public long Generation;
    public Guid? SessionId;
    public long Revision;
    public bool TextSelectionRequested;
    public bool SceneEditing;
    public WorkbenchResourceReference? SessionInput;
    public WorkbenchResourceReference? SessionResult;
    public WorkbenchResourceReference? SessionStructuredResult;
    /// <summary>冻结显示基准附带的归一化排除框：仅供前端初始重建屏蔽标记。</summary>
    public IReadOnlyList<WorkbenchExclusionBox> ExcludeBoxes = [];
    public RecognitionTextLayerState? TextLayer;
    public long TextGeneration;
    public RecognitionViewModel? TextLayerViewModel;
    public CancellationTokenSource? TextPreparation;
    public CancellationTokenSource? CaptureDemand;
    public CancellationTokenSource? RecognitionDemand;
  }

  private readonly ImageSessionChannel recognitionChannel = new();
  private readonly ImageSessionChannel imageEditChannel = new();
  private ResultActions? resultActions;
  /// <summary>当前结果最近发布的结构化资源 URL；与新发布/失效同步维护。</summary>
  private WorkbenchResourceReference? latestStructuredResult;
  private string? latestStructuredUri => latestStructuredResult?.Url;

  private void SetStructuredResult(WorkbenchResourceReference? structured)
  {
    if (latestStructuredResult is { } previous && previous.Url != structured?.Url) resources.ReleaseResource(previous);
    latestStructuredResult = structured;
  }
  private WorkbenchResourceReference? resultImage;
  private void SetResultImage(WorkbenchResourceReference? image)
  {
    if (resultImage is { } previous && previous.Url != image?.Url) resources.ReleaseResource(previous);
    resultImage = image;
  }

  private void ClearResultActions()
  {
    resultActions = null;
    SetStructuredResult(null);
    SetResultImage(null);
  }

  private int captureInFlight;

  internal Guid? CurrentImageSessionId => recognitionChannel.SessionId;

  /// <summary>recognition 通道的识别 VM（懒创建；与 imageEdit 通道实例独立）。</summary>
  internal string? TaskEngine => recognitionChannel.ViewModel?.TaskEngine;
  private RecognitionViewModel RecognitionVm =>
    recognitionChannel.ViewModel ??= recognitionFactory();

  internal ScreenshotCaptureScene? PendingScreenshotCaptureScene =>
    recognitionChannel.ViewModel?.CurrentInput?.CaptureScene;
  internal ScreenshotCaptureScene? TakeScreenshotCaptureScene(Guid sessionId) =>
    recognitionChannel.SessionId == sessionId
      ? recognitionChannel.ViewModel?.CurrentInput?.TakeCaptureScene()
      : null;

  /// <summary>
  /// Supervisor 连接/就绪/失败终态变更时同步广播诊断投影：宿主快照不
  /// 得滞留在 bootstrap 时的“正在连接”。仅监听代表健康变更的
  /// SupervisorStatus 单属性，避免一次 UpdateSupervisor 的四个通知各
  /// 广播一次。
  /// </summary>
  private async Task<RecognitionWorkbenchState> CopyAnnotatedImageAsync(
    CopyAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    await annotatedImagePlatform.CopyImageAsync(annotation.Path, cancellationToken);
    return CurrentRecognitionState();
  }

  private async Task<RecognitionWorkbenchState> SaveAnnotatedImageAsync(
    SaveAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    if (!await annotatedImagePlatform.SaveImageAsync(annotation.Path, cancellationToken))
    {
      throw new AnnotatedImageOperationCancelledException();
    }
    return CurrentRecognitionState();
  }

  private async Task<ImageEditWorkbenchState> CopyImageEditAnnotatedImageAsync(
    CopyImageEditAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    await annotatedImagePlatform.CopyImageAsync(annotation.Path, cancellationToken);
    return CurrentImageEditState();
  }

  private async Task<ImageEditWorkbenchState> SaveImageEditAnnotatedImageAsync(
    SaveImageEditAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    if (!await annotatedImagePlatform.SaveImageAsync(annotation.Path, cancellationToken))
    {
      throw new AnnotatedImageOperationCancelledException();
    }
    return CurrentImageEditState();
  }

  private RecognitionWorkbenchState CurrentRecognitionState()
  {
    if (recognitionChannel.SessionId is not null)
    {
      return SessionRecognitionState(
        false,
        recognitionChannel.SessionResult is null ? "recognition.session" : "recognition.completed");
    }
    return RecognitionState(
      false,
      recognitionChannel.ViewModel is null ? "recognition.ready" : RecognitionStatusCode(RecognitionVm));
  }

  /// <summary>imageEdit 通道当前投影（命令出口回显）：有会话回会话状态，无会话回就绪。</summary>
  private ImageEditWorkbenchState CurrentImageEditState() =>
    imageEditChannel.SessionId is not null
      ? ImageEditState(false, SessionStatusCode(imageEditChannel))
      : ImageEditState(false, "recognition.ready");

  /// <summary>通道当前投影（命令出口回显）：按通道目标 scope 发布。</summary>
  private WorkbenchState CurrentChannelState(ImageSessionChannel channel) =>
    channel == imageEditChannel ? CurrentImageEditState() : CurrentRecognitionState();

  /// <summary>
  /// 严格模式合同的终态：显式选择的模式不可用时保留引擎目录、用户选择与
  /// 截图会话基准，状态指向环境修复；不发布虚假成功或丢失选择。
  /// </summary>
  private RecognitionWorkbenchState ModeUnavailableRecognitionState() => new(
    false,
    "recognition.modeUnavailable",
    recognitionChannel.SessionId is null ? null : recognitionChannel.SessionInput,
    recognitionChannel.SessionId is null ? null : recognitionChannel.SessionResult,
    selectionPolicy.RecognitionEngines(),
    recognitionChannel.ViewModel?.TaskEngine,
    CurrentScreenshotSession(recognitionChannel));

  /// <summary>通道当前会话的 wire 投影；无会话时为 null。</summary>
  private RecognitionScreenshotSessionState? CurrentScreenshotSession(
    ImageSessionChannel channel) =>
    channel.SessionId is { } id
      ? new RecognitionScreenshotSessionState(id.ToString("N"), channel.Revision,
        channel.TextSelectionRequested, channel.SceneEditing,
        channel.ExcludeBoxes)
      : null;

  /// <summary>imageEdit 通道状态：与 recognition 会话投影同形，无引擎目录。</summary>
  internal ImageEditWorkbenchState ImageEditState(
    bool isBusy,
    string statusCode) => new(
    isBusy,
    statusCode,
    imageEditChannel.SessionInput,
    CurrentScreenshotSession(imageEditChannel),
    imageEditChannel.TextLayer);

  /// <summary>会话状态固定复用缓存的基准图/结果资源，避免重发布新 URL 导致编辑器重置。</summary>
  private RecognitionWorkbenchState SessionRecognitionState(
    bool isBusy,
    string statusCode) => new(
      isBusy,
      statusCode,
      recognitionChannel.SessionInput,
      recognitionChannel.SessionResult,
      selectionPolicy.RecognitionEngines(),
      recognitionChannel.ViewModel?.TaskEngine,
      CurrentScreenshotSession(recognitionChannel),
      TextLayer: recognitionChannel.TextLayer,
      StructuredResult: recognitionChannel.SessionResult is null
        ? null
        : recognitionChannel.SessionStructuredResult,
      ResultImage: recognitionChannel.SessionResult is null ? null : resultImage);

  /// <summary>通道会话状态：按通道目标 scope 发布（recognition/imageEdit）。</summary>
  private WorkbenchState SessionState(
    ImageSessionChannel channel, bool isBusy, string statusCode) =>
    channel == imageEditChannel
      ? ImageEditState(isBusy, statusCode)
      : SessionRecognitionState(isBusy, statusCode);

  private void ValidateScreenshotSession(
    ImageSessionChannel channel, Guid sessionId, long revision)
  {
    if (channel.SessionId != sessionId || channel.Revision != revision)
    {
      throw new ScreenshotSessionStaleException();
    }
  }

  /// <summary>取消在途文字层准备并丢弃旧层；编辑/换图/维护/关闭后调用。</summary>
  private void InvalidateScreenshotTextLayer(
    ImageSessionChannel channel, long? editedRevision = null)
  {
    Interlocked.Increment(ref channel.TextGeneration);
    channel.TextPreparation?.Cancel();
    channel.TextLayerViewModel?.Cancel();
    if (channel.TextLayer?.Image is { } image) resources.ReleaseResource(image);
    channel.TextLayer = null;
    if (editedRevision is { } revision && channel.SessionId is { } id)
      ScreenshotTextLayerInvalidated?.Invoke(id, revision);
  }

  private static long AdvanceImageGeneration(ImageSessionChannel channel)
  {
    long generation = Interlocked.Increment(ref channel.Generation);
    // 交接准备先于 VM activeRun；换图/修订/关闭也须撤销这段独立等待。
    channel.RecognitionDemand?.Cancel();
    return generation;
  }

  private WorkbenchAnnotationFile TakeScreenshotAnnotation(string resourceUri) =>
    annotationStore.Take(new Uri(resourceUri));

  private WorkbenchState? StartImageEdit(
    ImageSessionChannel channel,
    Func<RecognitionViewModel, Task> loadImage,
    CancellationToken cancellationToken)
  {
    _ = channel.ViewModel ??= recognitionFactory();
    long generation = AdvanceImageGeneration(channel);
    return operations.Start(
      SessionState(channel, true, "recognition.running"),
      () => CompleteImageEditAsync(channel, loadImage, generation, cancellationToken));
  }

  private async Task CompleteImageEditAsync(
    ImageSessionChannel channel,
    Func<RecognitionViewModel, Task> loadImage,
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      RecognitionViewModel viewModel = channel.ViewModel ??= recognitionFactory();
      await loadImage(viewModel);
      if (generation != Volatile.Read(ref channel.Generation)) return;
      if (viewModel.TerminalState is JobState.Cancelled or JobState.Failed)
      {
        operations.PublishTerminal(viewModel.TerminalState == JobState.Failed
          ? SessionState(channel, false, "recognition.inputFailed")
          : CurrentChannelState(channel));
        return;
      }
      if (viewModel.CurrentInput is not { } image) return;
      WorkbenchResourceReference input = await resources.PublishBytesAsync(image.Data, image.MediaType,
        WorkbenchResourcesOwner.ExtensionForMediaType(image.MediaType), cancellationToken);
      if (generation != Volatile.Read(ref channel.Generation))
      {
        resources.ReleaseResource(input);
        return;
      }
      ClearScreenshotSession(channel);
      InvalidateScreenshotTextLayer(channel);
      if (channel == recognitionChannel)
      {
        ClearResultActions();
      }
      channel.SessionId = Guid.NewGuid();
      channel.Revision = 0;
      channel.SessionInput = input;
      operations.PublishTerminal(SessionState(channel, false, "recognition.session"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
      OperationCanceledException)
    {
      AppLog.Error("Image edit input failed", error);
      if (generation == Volatile.Read(ref channel.Generation))
        operations.PublishTerminal(SessionState(channel, false, "recognition.inputFailed"));
    }
  }

  private WorkbenchState? StartScreenshotSession(
    bool textSelectionRequested, CancellationToken cancellationToken, bool scrolling = false)
  {
    _ = RecognitionVm;
    // 截图单飞：任一截图入口（主窗/热键/悬浮栏/页面按钮）同时只允许一个
    // 选区会话；拒绝重复必须先于推进 generation/清会话，同步启动段故障
    // 则在此释放 guard，否则截图永久 busy。
    if (Interlocked.Exchange(ref captureInFlight, 1) != 0)
    {
      throw new CaptureInProgressException();
    }

    try
    {
      var demand = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operations.Token);
      recognitionChannel.CaptureDemand = demand;
      long generation = AdvanceImageGeneration(recognitionChannel);
      // 框选取消或失败时保留旧图、标注、结果与修订；成功捕获才替换。
      ScreenshotCaptureStarting?.Invoke();
      return operations.Start(
        SessionState(recognitionChannel, true, "recognition.running"),
        () => CompleteScreenshotSessionAsync(generation, textSelectionRequested, scrolling,
          demand));
    }
    catch
    {
      Interlocked.Exchange(ref captureInFlight, 0);
      throw;
    }
  }

  /// <summary>
  /// 释放截图单飞 guard：在任何终态/会话状态发布前调用，保证观察者看到
  /// 终态即可重入；动作在途（显式识别/本地输出）仍持 guard。finally 内
  // 的释放保留为同步启动段故障的安全网（幂等）。
  /// </summary>
  private void ReleaseCaptureGuard() => Interlocked.Exchange(ref captureInFlight, 0);

  private async Task CompleteScreenshotSessionAsync(
    long generation,
    bool textSelectionRequested,
    bool scrolling,
    CancellationTokenSource demand)
  {
    CancellationToken cancellationToken = demand.Token;
    // 所有截图入口（主窗/热键/悬浮栏）统一让位：截图期间工具栏与感应条
    // 均不入画面；结束后恢复原态。无论成功、取消或失败都释放单飞 guard。
    bool publishedSession = false;
    RecognitionInput? capturedInput = null;
    try
    {
      using (shellActions?.SuspendFloatingToolbarForCapture())
      {
        // 纯截图路径：不加载 Runtime 目录（EnsureSelectionLoadedAsync）、
        // 不做 requireUsable 模式协商；Supervisor 未连接/维护中同样可完成。
        // 普通入口把宿主当前目录投影交给动作栏；专用入口保持直接确认。
        RecognitionViewModel viewModel = RecognitionVm;
        if (scrolling)
          await viewModel.CaptureScrollingScreenshotSessionAsync(cancellationToken);
        else
          await viewModel.CaptureScreenshotSessionAsync(
            textSelectionRequested ? null : BuildSelectionActions(),
            cancellationToken);
        if (generation != Volatile.Read(ref recognitionChannel.Generation))
        {
          return;
        }
        if (viewModel.TerminalState is JobState.Cancelled or JobState.Failed)
        {
          ReleaseCaptureGuard();
          operations.PublishTerminal(SessionState(recognitionChannel, false,
            viewModel.TerminalState is JobState.Failed ? "recognition.failed" : "recognition.cancelled"));
          return;
        }
        if (viewModel.CurrentInput is { } captured)
        {
          capturedInput = captured;
          // 普通入口动作栏意图：识别/本地输出/打开设置按显式意图完成本次
          // 截图，不创建编辑会话；编辑或无意图沿用既有会话+现场编辑宿主。
          switch (captured.SelectionAction)
          {
            case ScreenshotSelectionAction.Recognize
              when captured.SelectionRecognitionMode is { Length: > 0 } || captured.SelectionRecognitionIntent is not null:
              await CompleteScreenshotSelectionRecognitionAsync(
                generation, captured, captured.SelectionRecognitionMode, cancellationToken);
              return;
            case ScreenshotSelectionAction.Recognize:
              AppLog.Warn(
                $"Screenshot selection recognition arrived without a typed mode id: {captured.SelectionRecognitionMode}");
              RecognitionVm.ReleaseInput();
              capturedInput = null;
              ReleaseCaptureGuard();
              operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.failed"));
              return;
            case ScreenshotSelectionAction.Copy or ScreenshotSelectionAction.Save
              or ScreenshotSelectionAction.Pin:
              await CompleteScreenshotLocalOutputAsync(
                generation, captured, captured.SelectionAction.Value, cancellationToken);
              return;
            case ScreenshotSelectionAction.OpenSettings:
              // 放弃本次捕获并显式进入现有设置页；不残留会话或隐式 OCR。
              RecognitionVm.ReleaseInput();
              capturedInput = null;
              ReleaseCaptureGuard();
              operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.cancelled"));
              ScreenshotSelectionSettingsRequested?.Invoke();
              return;
          }

          WorkbenchResourceReference input = await resources.PublishBytesAsync(
            captured.Data,
            captured.MediaType,
            WorkbenchResourcesOwner.ExtensionForMediaType(captured.MediaType),
            cancellationToken);
          // PublishBytesAsync 期间取消/新截图会推进 generation：
          // 旧捕获完成不得复活已被取代的会话。
          if (generation != Volatile.Read(ref recognitionChannel.Generation))
          {
            resources.ReleaseResource(input);
            return;
          }

          ClearScreenshotSession(recognitionChannel);
          InvalidateScreenshotTextLayer(recognitionChannel);
          ClearResultActions();
          recognitionChannel.SessionId = Guid.NewGuid();
          recognitionChannel.Revision = 0;
          recognitionChannel.TextSelectionRequested = textSelectionRequested;
          recognitionChannel.SceneEditing = true;
          recognitionChannel.SessionInput = input;
          recognitionChannel.SessionResult = null;
          recognitionChannel.ExcludeBoxes = [];
          publishedSession = true;
          ReleaseCaptureGuard();
          operations.PublishTerminal(SessionState(recognitionChannel, false, "recognition.session"));
        }
        else
        {
          // 用户在选区界面取消：不残留会话与遮罩状态；此前的文件输入保持可见。
          ReleaseCaptureGuard();
          operations.PublishTerminal(recognitionChannel.SessionId is not null
            ? SessionState(recognitionChannel, false, "recognition.cancelled")
            : await CurrentRecognitionStateAsync("recognition.cancelled", cancellationToken));
        }
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(SessionState(recognitionChannel, false, "recognition.cancelled"));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot session capture failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(SessionState(recognitionChannel, false, "recognition.failed"));
      }
    }
    finally
    {
      Interlocked.Exchange(ref captureInFlight, 0);
      try
      {
        if (publishedSession &&
            generation == Volatile.Read(ref recognitionChannel.Generation) &&
            recognitionChannel.SessionId is { } id)
          ScreenshotSessionReady?.Invoke(id, recognitionChannel.ViewModel?.CurrentInput?.CaptureBounds);
      }
      finally
      {
        capturedInput?.DisposeCaptureScene();
        if (ReferenceEquals(recognitionChannel.CaptureDemand, demand)) recognitionChannel.CaptureDemand = null;
        demand.Dispose();
        ScreenshotCaptureFinished?.Invoke();
      }
    }
  }

  /// <summary>
  /// 普通截图动作栏的识别图标投影：固定三类语义——文字识别（设置已提交
  /// 的默认引擎）、表格识别、公式识别；任意目录状态下都恰好返回三个条目
  /// （不可用则禁用并指向设置），不会有时一整列、有时单个泛“未就绪”。
  /// 纯截图不触发目录加载或安装。
  /// </summary>
  private ScreenshotSelectionActions BuildSelectionActions()
  {
    _ = RecognitionVm;
    // 纯截图不触发目录加载；这里只确保 settings 实例存在并复用它当前
    // 已加载的真实目录；冷启动只保存分类意图，点击后才准备。
    settingsOwner.EnsureModel();
    return new ScreenshotSelectionActions(
      settingsOwner.EnsureModel().RecognitionSelection is null && ensureRuntimeReady is not null
        ? BuildSelectionModeEntries(null).Select((entry, index) => entry with
        { Intent = (ScreenshotRecognitionIntent)index, Hint = "点击后准备运行环境并识别" }).ToArray()
        : BuildSelectionModeEntries(settingsOwner.EnsureModel().RecognitionSelection), settingsOwner.Theme);
  }

  /// <summary>
  /// 三类图标只消费 Runtime typed 目录自身的分类字段（family/pipeline），
  /// 不在桌面侧复制引擎支持表：表格/公式取 specialized 族中
  /// TABLE_RECOGNITION/FORMULA_RECOGNITION 管线（Protocol v2 稳定投影值）
  /// 声明的模式。文字图标复用 <see cref="EffectiveModeIdOrDefault"/> 的
  /// 既有持久默认解析（Bound→回显 id，其余不猜默认）；解析出的默认属于
  /// 文字语义（text/document 族）才携带其 id 与目录 availability，默认
  /// 缺失/无效/不在当前目录或属于表格/公式语义时显式禁用（空 ModeId +
  /// default_missing/default_invalid 稳定词汇）并指向设置，绝不静默改选
  /// 其他文字引擎，也不把表格引擎冒充文字。控件名称固定为三分类（稳定
  /// 可访问名称），引擎名等细节仅进 Hint（Tooltip）。目录未加载时三类
  /// 统一 catalog_unavailable 禁用。
  /// </summary>
  internal static IReadOnlyList<ScreenshotRecognitionModeEntry> BuildSelectionModeEntries(
    RecognitionSelectionSnapshot? selection)
  {
    RuntimeSelectionService? catalog = selection?.Catalog;
    if (catalog?.SupportsRecognitionModes is not true || catalog.RecognitionModes.Count == 0)
    {
      return
      [
        new ScreenshotRecognitionModeEntry("", "文字识别", "catalog_unavailable", null, "textRecognition"),
        new ScreenshotRecognitionModeEntry("", "表格识别", "catalog_unavailable", null, "tableRecognition"),
        new ScreenshotRecognitionModeEntry("", "公式识别", "catalog_unavailable", null, "formulaRecognition"),
      ];
    }
    IReadOnlyList<RecognitionModeOption> modes = catalog.RecognitionModes;

    RecognitionModeOption? FindMode(string? id) => id is null ? null :
      modes.FirstOrDefault(mode => string.Equals(mode.Id, id, StringComparison.Ordinal));

    List<ScreenshotRecognitionModeEntry> entries = [];
    string? defaultModeId = WorkbenchSelectionOwner.EffectiveModeIdOrDefault(
      selection, taskEngine: null, requireUsable: false);
    if (FindMode(defaultModeId) is { } textMode && textMode.Family is "text" or "document")
    {
      entries.Add(new ScreenshotRecognitionModeEntry(
        textMode.Id,
        "文字识别",
        textMode.Availability,
        textMode.ReasonCode,
        "textRecognition",
        $"文字识别（当前引擎：{SettingsViewModel.DisplayName(textMode.Id)}；可在设置 · 默认识别类型中更改）"));
    }
    else
    {
      // 三类语义显式区分：NotApplicable 且无解析默认=尚未配置；其余
      // （Invalid/Unread/不在目录/specialized 族默认）=当前默认不能用作
      // 文字识别。禁用条目不携带可提交的 modeId，提交路径 fail closed。
      string availability = selection!.DefaultMode is RuntimeDefaultModeBinding.NotApplicable &&
        defaultModeId is null
          ? "default_missing"
          : "default_invalid";
      entries.Add(new ScreenshotRecognitionModeEntry(
        "", "文字识别", availability, null, "textRecognition"));
    }

    static ScreenshotRecognitionModeEntry SpecializedEntry(
      IReadOnlyList<RecognitionModeOption> modes,
      string pipelineId,
      string label,
      string iconKey) =>
      modes.FirstOrDefault(mode => mode.Family == "specialized" &&
        mode.PipelineId == pipelineId) is { } mode
        ? new ScreenshotRecognitionModeEntry(
          mode.Id, label, mode.Availability, mode.ReasonCode, iconKey)
        // 验证后的目录必然声明全部稳定模式；缺失时按目录不可用禁用。
        : new ScreenshotRecognitionModeEntry("", label, "unavailable", null, iconKey);
    entries.Add(SpecializedEntry(modes, "TABLE_RECOGNITION", "表格识别", "tableRecognition"));
    entries.Add(SpecializedEntry(modes, "FORMULA_RECOGNITION", "公式识别", "formulaRecognition"));
    return entries;
  }

  /// <summary>
  /// 普通截图动作栏显式识别：携带唯一 typed 意图，提交前经
  /// <see cref="EnsureSelectionLoadedForSubmitAsync"/> 取得权威目录（未
  /// attach 时等待 Supervisor 启动；环境切换窗口取消本次提交，不以空
  /// 快照提交），再按任务模式严格 resolve（显式 modeId 优先，无默认回退），
  /// 只提交一次任务；新输入取代旧截图会话，终态由主窗识别承载面显式展示。
  /// </summary>
  private async Task CompleteScreenshotSelectionRecognitionAsync(
    long generation,
    RecognitionInput input,
    string? modeId,
    CancellationToken cancellationToken)
  {
    bool terminalVisible = false;
    try
    {
      // 提交前权威目录：与单次/批量/PDF 同一契约；切换窗口返回 false 时
      // 取消本次提交（沿用本方法 cancel 终态/finally 清理），不猜默认。
      if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(cancellationToken))
      {
        if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
        RecognitionVm.InvalidateResult();
        ClearResultActions();
        ReleaseCaptureGuard();
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.cancelled"));
        terminalVisible = true;
        return;
      }
      if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
      if (input.SelectionRecognitionIntent is { } intent)
      {
        ScreenshotRecognitionModeEntry resolved = BuildSelectionModeEntries(settingsOwner.CurrentModel?.RecognitionSelection)[(int)intent];
        modeId = resolved.ModeId;
      }
      // 动作栏合同：仅 ready 模式可提交；目录在菜单渲染后变化（环境切换）
      // 或需要准备的模式一律拒绝，不自动触发依赖准备/下载。
      RecognitionEngineChoice? chosen = selectionPolicy.RecognitionEngines()?
        .FirstOrDefault(entry => entry.Engine == modeId);
      if (chosen is not { Availability: "ready" })
      {
        throw new RecognitionModeUnavailableException(
          $"截图识别模式 {modeId} 当前不可用（{chosen?.Availability ?? "未在目录中"}），已拒绝执行；请在设置中准备后重试。");
      }
      // typed 意图冻结为任务模式：模式缺失/不可用直接拒绝，不回退默认；
      // 新版 SynchronizeRecognitionMode 中显式 TaskEngine 优先于继承默认。
      ApplyTaskEngine(modeId!);
      SynchronizeRecognitionMode(requireUsable: true);
      // 显式识别取代旧截图会话（关闭旧 scene 编辑窗），不新建编辑会话。
      ClearScreenshotSession(recognitionChannel);
      InvalidateScreenshotTextLayer(recognitionChannel);
      ClearResultActions();
      RecognitionWorkbenchState state = await RunRecognitionAsync(
        viewModel => viewModel.RecognizeCapturedInputAsync(input, cancellationToken),
        generation,
        cancellationToken);
      // typed 意图在显式识别链路中的终态由调用方按代际发布。
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        operations.PublishTerminal(state);
        terminalVisible = true;
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.cancelled"));
        terminalVisible = true;
      }
    }
    catch (RecognitionModeUnavailableException error)
    {
      // 严格模式合同：菜单选项与目录在提交瞬间不一致（环境切换等）时
      // 可恢复地失败并指向环境，不静默回退通用文字识别。
      AppLog.Warn($"Screenshot selection mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        operations.PublishTerminal(ModeUnavailableRecognitionState());
        terminalVisible = true;
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Screenshot selection mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        operations.PublishTerminal(ModeUnavailableRecognitionState());
        terminalVisible = true;
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot selection recognition failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.failed"));
        terminalVisible = true;
      }
    }
    finally
    {
      if (terminalVisible) ScreenshotSelectionRecognized?.Invoke();
    }
  }

  /// <summary>
  /// 普通截图动作栏本地输出（复制/保存/钉图）：选区像素编码为 PNG 走既有
  /// annotation 快照与会话命令（快照/剪贴板重试/保存取消/贴图上限语义复用），
  /// 执行后关闭会话；全程不提交识别、不打开现场编辑宿主。
  /// </summary>
  private async Task CompleteScreenshotLocalOutputAsync(
    long generation,
    RecognitionInput input,
    ScreenshotSelectionAction action,
    CancellationToken cancellationToken)
  {
    bool failed = false;
    WorkbenchAnnotationLease? lease = null;
    try
    {
      byte[] png = await QrCodeSavePlatform.EncodeImageAsync(
        input.Data, BitmapEncoder.PngEncoderId, cancellationToken);
      lease = await annotationStore.UploadPngAsync(
        new MemoryStream(png), cancellationToken);
      if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
      ClearScreenshotSession(recognitionChannel);
      InvalidateScreenshotTextLayer(recognitionChannel);
      ClearResultActions();
      recognitionChannel.SessionId = Guid.NewGuid();
      recognitionChannel.Revision = 0;
      recognitionChannel.TextSelectionRequested = false;
      recognitionChannel.SceneEditing = false;
      recognitionChannel.SessionInput = null;
      recognitionChannel.SessionResult = null;
      recognitionChannel.ExcludeBoxes = [];
      Guid sessionId = recognitionChannel.SessionId.Value;
      string resourceUri = lease.ResourceUri.AbsoluteUri;
      switch (action)
      {
        case ScreenshotSelectionAction.Copy:
          await CopyScreenshotImageAsync(
            recognitionChannel,
            new CopyScreenshotImageCommand(resourceUri, sessionId, 0),
            cancellationToken);
          break;
        case ScreenshotSelectionAction.Save:
          await SaveScreenshotImageAsync(
            recognitionChannel,
            new SaveScreenshotImageCommand(resourceUri, sessionId, 0),
            cancellationToken);
          break;
        case ScreenshotSelectionAction.Pin:
          PinScreenshotImage(
            recognitionChannel,
            new PinScreenshotImageCommand(resourceUri, sessionId, 0, []));
          break;
      }
    }
    catch (AnnotatedImageOperationCancelledException)
    {
      // 用户在保存对话框取消：静默结束本次截图，不算失败。
    }
    catch (Exception error)
    {
      AppLog.Error($"Screenshot selection local output failed ({action})", error);
      failed = true;
    }
    finally
    {
      // 快照租约确定性回收：命令已 Take 时 Revoke 对已移除条目为无害
      // no-op；换图/取消/失败（含 Take 之前抛错）不留下未消费 annotation，
      // 不依赖 TTL 清扫。
      if (lease is not null) annotationStore.Revoke(lease.ResourceUri);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        RecognitionWorkbenchState closed = CloseScreenshotSession(recognitionChannel)
          is RecognitionWorkbenchState closedState
            ? closedState
            : new RecognitionWorkbenchState(false, "recognition.ready");
        ReleaseCaptureGuard();
        operations.PublishTerminal(
          failed ? closed with { StatusCode = "recognition.failed" } : closed);
        // 纯本地动作失败沿用捕获失败语义：AppLog + 状态投影，不借识别
        // 终态事件激活主窗（显式主窗交接仅限识别终态与设置入口）。
      }
    }
  }

  private WorkbenchState NotifyScreenshotRevision(
    ImageSessionChannel channel,
    IScreenshotSessionTarget command)
  {
    RecognitionViewModel viewModel = channel.ViewModel ??= recognitionFactory();
    if (channel.SessionId != command.SessionId ||
      command.Revision <= channel.Revision)
    {
      // 未知会话或乱序/过期通知：保留宿主权威状态，不回退修订。
      return SessionState(channel, false, SessionStatusCode(channel));
    }
    long previousRevision = channel.Revision;
    channel.Revision = command.Revision;
    // 任何内容修订都使本通道旧识别结果与文字层失效：迟到响应不得覆盖
    // 当前内容。修订只作用于所属通道：另一页的独立输入/结果不受影响。
    AdvanceImageGeneration(channel);
    viewModel.Cancel();
    viewModel.InvalidateResult();
    InvalidateScreenshotTextLayer(channel, previousRevision);
    if (channel == recognitionChannel)
    {
      ClearResultActions();
      channel.SessionResult = null;
    }
    return SessionState(channel, false, "recognition.session");
  }

  private string SessionStatusCode(ImageSessionChannel channel) =>
    channel.SessionResult is null ? "recognition.session" : "recognition.completed";

  private async Task<WorkbenchState> CopyScreenshotImageAsync(
    ImageSessionChannel channel,
    IScreenshotSessionResource command,
    CancellationToken cancellationToken)
  {
    // 快照语义：开始时校验会话/修订并冻结本次导出的 PNG，
    // 原生异步（剪贴板重试）期间允许该快照完成；出口只回报当前投影，
    // 不回写旧会话状态。
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    await annotatedImagePlatform.CopyImageAsync(annotation.Path, cancellationToken);
    return CurrentChannelState(channel);
  }

  private async Task<WorkbenchState> SaveScreenshotImageAsync(
    ImageSessionChannel channel,
    IScreenshotSessionResource command,
    CancellationToken cancellationToken)
  {
    // 与复制同一快照语义：FileSavePicker 等待期间允许冻结的导出落盘，
    // 出口只回报当前投影，不回写旧会话状态。
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    if (!await annotatedImagePlatform.SaveImageAsync(annotation.Path, cancellationToken))
    {
      throw new AnnotatedImageOperationCancelledException();
    }
    return CurrentChannelState(channel);
  }

  private WorkbenchState PinScreenshotImage(
    ImageSessionChannel channel,
    IScreenshotSessionExclusions command)
  {
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    if (pinScreenshot is null)
    {
      throw new InvalidOperationException("Desktop pinning is unavailable.");
    }
    WorkbenchAnnotationFile image = TakeScreenshotAnnotation(command.ResourceUri);
    try
    {
      // 与原位行同一正面积相交策略：跨边界的行不进入贴图可选层；
      // 被完全屏蔽的文字因输入像素已覆盖本就不在层内。
      RecognitionTextLayerState? layer =
        channel.TextLayer is { Status: "textlayer.ready" } ready &&
        ready.Binding?.SessionId == command.SessionId.ToString("N") &&
        ready.Binding.Revision == command.Revision &&
        ready.ServiceInstance == (supervisorInstanceId() ?? string.Empty)
          ? FilterLayerLines(ready, command.ExcludeBoxes)
          : null;
      pinScreenshot(image, command.SessionId, command.Revision, layer, command.ExcludeBoxes);
    }
    catch
    {
      image.Dispose();
      throw;
    }
    return CurrentChannelState(channel);
  }

  /// <summary>按归一化排除矩形丢弃正面积相交的行；无排除时原样返回。</summary>
  private static RecognitionTextLayerState? FilterLayerLines(
    RecognitionTextLayerState layer, IReadOnlyList<WorkbenchExclusionBox> boxes)
  {
    if (boxes.Count == 0 || layer.Lines is not { Count: > 0 }) return layer;
    List<RecognitionTextLayerLine> kept = [];
    foreach (RecognitionTextLayerLine line in layer.Lines)
    {
      bool intersects = boxes.Any(box =>
        WorkbenchExclusionBox.LineIntersectsBox(line, box));
      if (!intersects) kept.Add(line);
    }
    return kept.Count == layer.Lines.Count
      ? layer
      : layer with { Lines = kept };
  }

  private async Task<WorkbenchState?> StartScreenshotRecognitionAsync(
    ImageSessionChannel channel,
    RecognizeScreenshotImageCommand command,
    CancellationToken cancellationToken)
  {
    _ = RecognitionVm;
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    // 上传的是未烘焙遮罩的普通最终像素：同一份字节兼当新的显示基准，
    // 也是无遮罩时的 OCR 输入；不重复全图上传。
    byte[] normalBytes = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
    // 读取租约期间会话/修订变更则拒绝启动，不消费推理配额。
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    // 冻结普通显示基准：发布同字节新资源（遮罩绝不烧入）。
    WorkbenchResourceReference frozen = await resources.PublishBytesAsync(
      normalBytes, annotation.MediaType,
      WorkbenchResourcesOwner.ExtensionForMediaType(annotation.MediaType), cancellationToken);
    bool adopted = false;
    try
    {
      if (channel.SessionId != command.SessionId ||
        channel.Revision != command.Revision)
      {
        throw new ScreenshotSessionStaleException();
      }
      // OCR 输入：有遮罩时按归一化框生成白色遮罩副本，无遮罩直接同字节。
      byte[] ocrBytes = normalBytes;
      string ocrMediaType = annotation.MediaType;
      if (command.ExcludeBoxes.Count > 0)
      {
        ocrBytes = await PinnedTextMask.CreateMaskedPngBytesAsync(
          annotation.Path, command.ExcludeBoxes, cancellationToken);
        ocrMediaType = "image/png";
      }
      // 全部 await 完成后、接管权威 input 前复验：遮罩生成期间切图/换会话
      // 不得把旧 frozen 写回新会话。
      if (channel.SessionId != command.SessionId ||
        channel.Revision != command.Revision)
      {
        throw new ScreenshotSessionStaleException();
      }
      var input = new RecognitionInput(
        ocrBytes,
        ocrMediaType,
        "screenshot-session-final" + WorkbenchResourcesOwner.ExtensionForMediaType(ocrMediaType),
        "screenshot-session");
      long generation = AdvanceImageGeneration(channel);
      Guid sessionId = command.SessionId;
      long revision = command.Revision;
      ClearResultActions();
      channel.SessionResult = null;
      // 旧 input 仅在 frozen 成功接管后释放。
      if (channel.SessionInput is { } previousInput) resources.ReleaseResource(previousInput);
      channel.SessionInput = frozen;
      adopted = true;
      channel.ExcludeBoxes = command.ExcludeBoxes;
      // 现场窗口关闭会取消其 bridge token；冻结后任务归主应用拥有。
      CancellationToken recognitionToken = channel.SceneEditing
        ? operations.Token : cancellationToken;
      var demand = CancellationTokenSource.CreateLinkedTokenSource(
        recognitionToken, operations.Token);
      channel.RecognitionDemand = demand;
      if (channel.SceneEditing)
      {
        // 显式识别交接：基准已冻结后再关 scene；会话与已提交任务保留，
        // 结果由主窗口识别承载面展示；失败/取消时编辑基准仍可用。
        channel.SceneEditing = false;
        ScreenshotSceneRecognitionHandoff?.Invoke(sessionId);
      }
      return operations.Start(
        SessionState(channel, true, "recognition.running"),
        () => CompleteScreenshotRecognitionAsync(
          generation,
          sessionId,
          revision,
          input,
          demand));
    }
    finally
    {
      // 未能接管（陈旧/遮罩失败/取消/发布异常）时释放 frozen，不泄漏资源。
      if (!adopted) resources.ReleaseResource(frozen);
    }
  }

  /// <summary>
  /// imageEdit 会话的显式识别交接：按 imageEdit 会话/修订校验并冻结其
  /// 显示基准与排除框，把当前图复制到 recognition 通道提交一次任务；
  /// 结果与输入副本发布在 recognition scope（识别承载面），imageEdit
  /// 会话保留可继续编辑。交接结果是独立快照：后续 imageEdit 修订/关闭
  // 不取消或清除 recognition 承载面已发布的结果。
  /// </summary>
  private async Task<WorkbenchState?> StartImageEditRecognitionAsync(
    RecognizeImageEditImageCommand command,
    CancellationToken cancellationToken)
  {
    ImageSessionChannel channel = imageEditChannel;
    _ = channel.ViewModel ??= recognitionFactory();
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    // 与 recognition 会话同一快照语义：读取的是未烘焙遮罩的普通最终像素。
    byte[] normalBytes = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    WorkbenchResourceReference frozen = await resources.PublishBytesAsync(
      normalBytes, annotation.MediaType,
      WorkbenchResourcesOwner.ExtensionForMediaType(annotation.MediaType), cancellationToken);
    bool adopted = false;
    try
    {
      if (channel.SessionId != command.SessionId ||
        channel.Revision != command.Revision)
      {
        throw new ScreenshotSessionStaleException();
      }
      byte[] ocrBytes = normalBytes;
      string ocrMediaType = annotation.MediaType;
      if (command.ExcludeBoxes.Count > 0)
      {
        ocrBytes = await PinnedTextMask.CreateMaskedPngBytesAsync(
          annotation.Path, command.ExcludeBoxes, cancellationToken);
        ocrMediaType = "image/png";
      }
      if (channel.SessionId != command.SessionId ||
        channel.Revision != command.Revision)
      {
        throw new ScreenshotSessionStaleException();
      }
      var input = new RecognitionInput(
        ocrBytes,
        ocrMediaType,
        "image-edit-session-final" + WorkbenchResourcesOwner.ExtensionForMediaType(ocrMediaType),
        "image-edit");
      // 交接展示副本：未烘焙遮罩的普通像素收编为 recognition 通道当前输入
      //（识别承载面展示/后续回显），与 OCR 输入分开，不移动 imageEdit 通道。
      var displayInput = new RecognitionInput(
        normalBytes,
        annotation.MediaType,
        "image-edit-handoff" + WorkbenchResourcesOwner.ExtensionForMediaType(annotation.MediaType),
        "image-edit");
      // 交接命令时刻冻结的排除框随快照携带：recognition B 会话收编后，
      // 后续识别/取字沿用同一屏蔽合同；不异步重读 imageEdit 通道可变框。
      IReadOnlyList<WorkbenchExclusionBox> exclusions = command.ExcludeBoxes;
      // 识别任务在 recognition 通道执行（同一承载面单任务语义）：复制当前
      // 图，不移动/复用 imageEdit 通道的 VM 与输入。交接取代 recognition
      // 通道旧会话：旧会话命令立即失效，B 的副本在完成时建立独立快照
      // 会话；imageEdit 通道的会话/输入不受影响。
      ClearScreenshotSession(recognitionChannel);
      InvalidateScreenshotTextLayer(recognitionChannel);
      ClearResultActions();
      long generation = AdvanceImageGeneration(recognitionChannel);
      if (channel.SessionInput is { } previousInput) resources.ReleaseResource(previousInput);
      channel.SessionInput = frozen;
      adopted = true;
      channel.ExcludeBoxes = command.ExcludeBoxes;
      var demand = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, operations.Token);
      recognitionChannel.RecognitionDemand = demand;
      operations.Publish(RecognitionState(true, "recognition.running"));
      return operations.Start(
        ImageEditState(false, "recognition.session"),
        () => CompleteImageEditRecognitionAsync(
          generation,
          input,
          displayInput,
          exclusions,
          demand));
    }
    finally
    {
      if (!adopted) resources.ReleaseResource(frozen);
    }
  }

  private async Task CompleteImageEditRecognitionAsync(
    long generation,
    RecognitionInput input,
    RecognitionInput displayInput,
    IReadOnlyList<WorkbenchExclusionBox> exclusions,
    CancellationTokenSource demand)
  {
    CancellationToken cancellationToken = demand.Token;
    try
    {
      // 提交前取得权威目录并严格协商可用模式（与单次/批量/PDF 同一契约）；
      // 环境切换窗口取消本次提交，不以空快照提交。
      if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(cancellationToken))
      {
        if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
        RecognitionVm.InvalidateResult();
        ClearResultActions();
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.cancelled"));
        return;
      }
      if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
      SynchronizeRecognitionMode(requireUsable: true);
      RecognitionVm.AdoptInput(displayInput);
      RecognitionWorkbenchState state = await RunRecognitionAsync(
        viewModel => viewModel.RecognizeCapturedInputAsync(input, cancellationToken),
        generation,
        cancellationToken);
      // B 的副本建立 recognition 自身独立快照会话：后续回显（任务模式/
      // 复制结果）携带 B 的会话与输入，不再附着旧 A 会话；代际失配
      //（交接被取代/取消）时不建。交接命令冻结的排除框随快照收编，
      // 编辑器据此重建屏蔽标记，后续识别/取字提交同一屏蔽合同。
      state = AdoptSessionForRecognitionInput(state, generation, exclusions);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.cancelled"));
      }
    }
    catch (RecognitionModeUnavailableException error)
    {
      AppLog.Warn($"Image edit recognition mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Image edit recognition selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(ModeUnavailableRecognitionState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Image edit recognition handoff failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.failed"));
      }
    }
    finally
    {
      if (ReferenceEquals(recognitionChannel.RecognitionDemand, demand))
        recognitionChannel.RecognitionDemand = null;
      demand.Dispose();
    }
  }

  private async Task CompleteScreenshotRecognitionAsync(
    long generation,
    Guid sessionId,
    long revision,
    RecognitionInput input,
    CancellationTokenSource demand)
  {
    CancellationToken cancellationToken = demand.Token;
    try
    {
      // 只有显式识别才同步 Runtime 目录并做 requireUsable 模式协商。
      // 输入（会话导出图）已就绪：未 attach 时等待权威目录；环境切换窗口
      // 取消本次提交，不以空快照提交（Rapid 回退）。
      bool selectionUnavailable =
        !await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(cancellationToken);
      // 目录加载期间编辑/关闭/换图会推进 generation：
      // 提交前复查，不把旧图发送给 Runtime。
      if (generation != Volatile.Read(ref recognitionChannel.Generation))
      {
        return;
      }
      if (selectionUnavailable)
      {
        RecognitionVm.InvalidateResult();
        ClearResultActions();
        operations.PublishTerminal(SessionRecognitionState(false, "recognition.cancelled"));
        return;
      }
      SynchronizeRecognitionMode(requireUsable: true);
      RecognitionViewModel viewModel = RecognitionVm;
      await viewModel.RecognizeCapturedInputAsync(input, cancellationToken);
      if (generation != Volatile.Read(ref recognitionChannel.Generation))
      {
        return;
      }
      if (recognitionChannel.SessionId == sessionId &&
        recognitionChannel.Revision == revision)
      {
        RecognizeResponse? response = viewModel.Result;
        (WorkbenchResourceReference? result, WorkbenchResourceReference? structured, WorkbenchResourceReference? image) =
          await PublishRecognitionResultAsync(viewModel, response, viewModel.ResultInput, cancellationToken);
        // 结果资源发布期间编辑/换图/维护会推进 generation：丢弃迟到结果。
        if (generation != Volatile.Read(ref recognitionChannel.Generation) ||
          recognitionChannel.SessionId != sessionId ||
          recognitionChannel.Revision != revision || !ReferenceEquals(response, viewModel.Result))
        {
          ReleaseResultResources(result, structured, image);
          return;
        }
        resultActions = response is null ? null : viewModel.CreateResultActions(resultActionPlatform);
        SetStructuredResult(structured);
        SetResultImage(image);
        recognitionChannel.SessionResult = result;
        recognitionChannel.SessionStructuredResult = structured;
        operations.PublishTerminal(SessionRecognitionState(
          false,
          RecognitionStatusCode(viewModel)));
      }
      else
      {
        // 会话或内容修订已变更：迟到结果丢弃，不覆盖新会话。
        viewModel.InvalidateResult();
        ClearResultActions();
        recognitionChannel.SessionResult = null;
        operations.PublishTerminal(SessionRecognitionState(false, "recognition.expired"));
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(SessionRecognitionState(false, "recognition.cancelled"));
      }
    }
    catch (RecognitionModeUnavailableException error)
    {
      AppLog.Warn($"Screenshot recognition mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Screenshot recognition selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(ModeUnavailableRecognitionState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot session recognition failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(SessionRecognitionState(false, "recognition.failed"));
      }
    }
    finally
    {
      if (ReferenceEquals(recognitionChannel.RecognitionDemand, demand))
        recognitionChannel.RecognitionDemand = null;
      demand.Dispose();
    }
  }

  private WorkbenchState CloseScreenshotSession(ImageSessionChannel channel)
  {
    RecognitionViewModel viewModel = channel.ViewModel ??= recognitionFactory();
    AdvanceImageGeneration(channel);
    viewModel.Cancel();
    viewModel.InvalidateResult();
    viewModel.ReleaseInput();
    if (channel == recognitionChannel)
      {
        ClearResultActions();
      }
    InvalidateScreenshotTextLayer(channel);
    ClearScreenshotSession(channel);
    return SessionState(channel, false, "recognition.ready");
  }

  /// <summary>
  /// 准备原位选字文字层：只消费编辑器导出的最终 PNG，且只在目录中存在
  /// ready 的本地轻量文字模式时提交；同一绑定已在准备/已就绪时不重复
  /// 提交任务。提交固定使用轻量文字配置（OCR 管线 + 所选模式引擎 +
  /// 空选项/空 mineru），不继承用户任务级 OCR 选项；不会触发依赖安装、
  /// 远端或重型文档管线。
  /// </summary>
  private async Task<WorkbenchState?> PrepareScreenshotTextLayerAsync(
    ImageSessionChannel channel, IScreenshotSessionResource command,
    CancellationToken cancellationToken, byte[]? pinnedPng = null,
    string pinnedMediaType = "image/png")
  {
    _ = channel.ViewModel ??= recognitionFactory();
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    if (channel.TextLayer?.Binding is { } existing &&
        existing.SessionId == command.SessionId.ToString("N") && existing.Revision == command.Revision &&
        channel.TextLayer.Status is "textlayer.preparing" or "textlayer.ready")
      return SessionState(channel, false, SessionStatusCode(channel));
    if (ensureRuntimeReady is null || inferenceAttached?.Invoke() != false)
      await selectionPolicy.EnsureSelectionLoadedAsync(cancellationToken);
    if ((ensureRuntimeReady is null && inferenceAttached?.Invoke() == false) ||
        (settingsOwner.CurrentModel?.RecognitionSelection is not null && FindReadyLocalTextMode(out _) is null))
    {
      _ = FindReadyLocalTextMode(out string? reason);
      channel.TextLayer = new RecognitionTextLayerState("textlayer.unavailable",
        inferenceAttached?.Invoke() == false ? "textlayer.serviceUnavailable" : reason,
        new RecognitionScreenshotSessionState(command.SessionId.ToString("N"), command.Revision),
        null, supervisorInstanceId(), null);
      return SessionState(channel, false, SessionStatusCode(channel));
    }
    var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operations.Token);
    channel.TextPreparation = preparation;
    long generation = Interlocked.Increment(ref channel.TextGeneration);
    channel.TextLayer = new RecognitionTextLayerState("textlayer.preparing", null,
      new RecognitionScreenshotSessionState(command.SessionId.ToString("N"), command.Revision),
      null, null, null);
    return operations.Start(
      SessionState(channel, false, SessionStatusCode(channel)),
      () => PrepareScreenshotTextLayerCoreAsync(channel, command, generation, preparation, pinnedPng, pinnedMediaType));
  }

  private async Task PrepareScreenshotTextLayerCoreAsync(
    ImageSessionChannel channel, IScreenshotSessionResource command, long generation,
    CancellationTokenSource preparation, byte[]? pinnedPng, string mediaType)
  {
    CancellationToken ct = preparation.Token;
    try
    {
      byte[] png;
      if (pinnedPng is null)
      {
        using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
        png = await File.ReadAllBytesAsync(annotation.Path, ct);
        mediaType = annotation.MediaType;
      }
      else png = pinnedPng;
      if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(ct))
        throw new OperationCanceledException(ct);
      ct.ThrowIfCancellationRequested();
      if (generation != Volatile.Read(ref channel.TextGeneration)) return;
      ValidateScreenshotSession(channel, command.SessionId, command.Revision);
      RecognitionModeOption? mode = FindReadyLocalTextMode(out string? reason);
      if (mode is null)
      {
        PublishTextLayerState(channel, "textlayer.unavailable", reason);
        return;
      }
      string instance = supervisorInstanceId() ?? string.Empty;
      channel.TextLayer = channel.TextLayer! with { ModeId = mode.Id, ServiceInstance = instance };
      await CompleteScreenshotTextLayerAsync(channel, generation, command.SessionId, command.Revision,
        mode.Id, instance, new RecognitionInput(png, mediaType,
          "screenshot-text-layer" + WorkbenchResourcesOwner.ExtensionForMediaType(mediaType), "screenshot-text"), ct);
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref channel.TextGeneration)) PublishTextLayerState(channel, "textlayer.cancelled", null);
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot text layer preparation failed", error);
      if (generation == Volatile.Read(ref channel.TextGeneration)) PublishTextLayerState(channel, "textlayer.failed", null);
    }
    finally
    {
      if (ReferenceEquals(channel.TextPreparation, preparation)) channel.TextPreparation = null;
      preparation.Dispose();
    }
  }

  /// <summary>
  /// An active pin joins the editor's task. Once its session is gone, explicit
  /// pin text recognition uses only that pin's frozen PNG and never publishes
  /// a result into the current editor session.
  /// </summary>
  public async Task<RecognitionTextLayerState?> PreparePinnedTextLayerAsync(
    Guid sessionId, long revision, string imagePath,
    CancellationToken cancellationToken = default)
  {
    // 贴图会话可能属于任一通道：按 sessionId 路由到持有它的通道。
    ImageSessionChannel channel =
      recognitionChannel.SessionId == sessionId ? recognitionChannel :
      imageEditChannel.SessionId == sessionId ? imageEditChannel :
      recognitionChannel;
    if (channel.SessionId == sessionId && channel.Revision == revision)
    {
      if (channel.TextLayer?.Binding is { } binding &&
        binding.SessionId == sessionId.ToString("N") &&
        binding.Revision == revision &&
        channel.TextLayer.Status is "textlayer.preparing" or "textlayer.ready") return null;
      byte[] activePng = await File.ReadAllBytesAsync(imagePath, cancellationToken);
      WorkbenchState? started = await PrepareScreenshotTextLayerAsync(
        channel,
        new PrepareScreenshotTextLayerCommand(string.Empty, sessionId, revision),
        CancellationToken.None,
        activePng, Path.GetExtension(imagePath) == ".jpg" ? "image/jpeg" : "image/png");
      if (started is not null) operations.Publish(started);
      return null;
    }

    // No session-dependent generation or result cache is read below. The pin
    // retains its own final PNG lease even after close/new capture/redaction.
    if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(cancellationToken))
      throw new PinnedTextPreparationException("本地识别服务尚未就绪。");
    RecognitionModeOption mode = FindReadyLocalTextMode(out _) ??
      throw new PinnedTextPreparationException("没有已就绪的本地轻量文字引擎，请先在设置中准备。");
    byte[] png = await File.ReadAllBytesAsync(imagePath, cancellationToken);
    string serviceInstance = supervisorInstanceId() ?? string.Empty;
    RecognitionViewModel viewModel = textLayerRecognitionFactory();
    viewModel.SetRecognitionMode(mode);
    await viewModel.RecognizeCapturedInputAsync(
      new RecognitionInput(png, Path.GetExtension(imagePath) == ".jpg" ? "image/jpeg" : "image/png", "pinned-text-layer" + Path.GetExtension(imagePath), "screenshot-text"),
      cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    if ((supervisorInstanceId() ?? string.Empty) != serviceInstance ||
        inferenceAttached?.Invoke() == false ||
        FindReadyLocalTextMode(out _)?.Id != mode.Id)
      throw new PinnedTextPreparationException("本地识别服务或引擎已变化，请重新取字。");
    if (viewModel.Result is null)
      throw new PinnedTextPreparationException("贴图取字未完成，请重试。");
    IReadOnlyList<RecognitionTextLayerLine>? lines =
      ProjectTextLayerLines(viewModel.Result.RawBlocks, out string? lineError);
    if (lines is null)
      throw new PinnedTextPreparationException(lineError == "textlayer.tooLarge"
        ? "文字内容过大，无法安全显示为贴图文字层。"
        : "贴图中没有可选择的文字。");
    return new RecognitionTextLayerState(
      "textlayer.ready", null,
      new RecognitionScreenshotSessionState(sessionId.ToString("N"), revision),
      mode.Id, serviceInstance, null, lines);
  }

  private async Task CompleteScreenshotTextLayerAsync(
    ImageSessionChannel channel,
    long generation,
    Guid sessionId,
    long revision,
    string modeId,
    string serviceInstance,
    RecognitionInput input,
    CancellationToken cancellationToken)
  {
    try
    {
      bool deferredSelection = !await selectionPolicy.EnsureSelectionLoadedAsync(cancellationToken);
      if (generation != Volatile.Read(ref channel.TextGeneration))
      {
        return;
      }
      RecognitionModeOption? mode = FindReadyLocalTextMode(out _);
      if (mode?.Id != modeId)
      {
        // 目录变化（模式不再 ready）：不静默换引擎冒充同一次准备。
        PublishTextLayerState(channel, "textlayer.unavailable", "textlayer.modeNotReady");
        return;
      }
      RecognitionViewModel viewModel = channel.TextLayerViewModel ??=
        textLayerRecognitionFactory();
      // 独立提交通道：不读写用户任务级 TaskEngine/SetRecognitionMode。
      viewModel.SetRecognitionMode(mode);
      await viewModel.RecognizeCapturedInputAsync(input, cancellationToken);
      if (deferredSelection)
      {
        try
        {
          await selectionPolicy.EnsureSelectionLoadedAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
          throw;
        }
        catch (Exception catalogError)
        {
          // 仅覆盖目录补载（影响引擎列表），不覆盖识别本身；失败记录在案。
          AppLog.Warn($"Text layer catalog reload failed: {catalogError.Message}");
        }
      }
      if (generation != Volatile.Read(ref channel.TextGeneration))
      {
        return;
      }
      if (channel.SessionId != sessionId || channel.Revision != revision ||
        (supervisorInstanceId() ?? string.Empty) != serviceInstance)
      {
        viewModel.InvalidateResult();
        PublishTextLayerState(channel, "textlayer.expired", null);
        return;
      }
      if (viewModel.Result is null)
      {
        PublishTextLayerState(
          channel,
          viewModel.TerminalState is JobState.Cancelled
            ? "textlayer.cancelled"
            : "textlayer.failed",
          null);
        return;
      }
      IReadOnlyList<RecognitionTextLayerLine>? lines =
        ProjectTextLayerLines(viewModel.Result.RawBlocks, out string? lineError);
      if (lines is null)
      {
        PublishTextLayerState(channel,
          lineError is null ? "textlayer.empty" : "textlayer.failed",
          lineError ?? "textlayer.noLines");
        return;
      }
      // 展示资源就是识别输入的同一最终 PNG 字节。
      WorkbenchResourceReference image = await resources.PublishBytesAsync(
        input.Data,
        input.MediaType,
        WorkbenchResourcesOwner.ExtensionForMediaType(input.MediaType),
        cancellationToken);
      if (generation != Volatile.Read(ref channel.TextGeneration) ||
        channel.SessionId != sessionId ||
        channel.Revision != revision ||
        (supervisorInstanceId() ?? string.Empty) != serviceInstance)
      {
        resources.ReleaseResource(image);
        return;
      }
      channel.TextLayer = new RecognitionTextLayerState(
        "textlayer.ready",
        null,
        new RecognitionScreenshotSessionState(sessionId.ToString("N"), revision),
        modeId,
        serviceInstance,
        image,
        lines);
      ScreenshotTextLayerChanged?.Invoke(channel.TextLayer);
      operations.PublishTerminal(SessionState(channel, false, SessionStatusCode(channel)));
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref channel.TextGeneration))
      {
        PublishTextLayerState(channel, "textlayer.cancelled", null);
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot text layer preparation failed", error);
      if (generation == Volatile.Read(ref channel.TextGeneration))
      {
        PublishTextLayerState(channel, "textlayer.failed", null);
      }
    }
  }

  private void PublishTextLayerState(
    ImageSessionChannel channel, string status, string? reason)
  {
    if (channel.TextLayer?.Image is { } image) resources.ReleaseResource(image);
    RecognitionScreenshotSessionState? binding = channel.SessionId is { } id
      ? new RecognitionScreenshotSessionState(id.ToString("N"), channel.Revision)
      : null;
    channel.TextLayer = new RecognitionTextLayerState(
      status,
      reason,
      binding,
      channel.TextLayer?.ModeId,
      channel.TextLayer?.ServiceInstance,
      null);
    ScreenshotTextLayerChanged?.Invoke(channel.TextLayer);
    operations.Publish(SessionState(channel, false, SessionStatusCode(channel)));
  }

  private WorkbenchState CancelScreenshotTextLayer(
    ImageSessionChannel channel, IScreenshotSessionTarget command)
  {
    _ = channel.ViewModel ??= recognitionFactory();
    if (channel.SessionId != command.SessionId)
    {
      throw new ScreenshotSessionStaleException();
    }
    if (channel.TextLayer is { Status: "textlayer.preparing" })
    {
      Interlocked.Increment(ref channel.TextGeneration);
      channel.TextPreparation?.Cancel();
      channel.TextLayerViewModel?.Cancel();
      PublishTextLayerState(channel, "textlayer.cancelled", null);
    }
    return SessionState(channel, false, SessionStatusCode(channel));
  }

  private async Task<WorkbenchState> CopyScreenshotSelectionAsync(
    ImageSessionChannel channel,
    IScreenshotSessionSelectionText command,
    CancellationToken cancellationToken)
  {
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    // 复制必须来自当前就绪文字层：旧层/准备中一律 fail closed。
    if (channel.TextLayer?.Binding is not { } binding ||
      binding.SessionId != command.SessionId.ToString("N") ||
      binding.Revision != command.Revision ||
      channel.TextLayer.Status != "textlayer.ready" ||
      channel.TextLayer.ServiceInstance != (supervisorInstanceId() ?? string.Empty))
    {
      throw new ScreenshotSessionStaleException();
    }
    await annotatedImagePlatform.CopyTextAsync(command.Text, cancellationToken);
    return CurrentChannelState(channel);
  }

  /// <summary>
  /// 快速取字模式选择：仅目录内 ready 的本地轻量文字模式（base_runtime/
  /// operating_system）；用户已选的模式若恰好可用则优先。preparation_
  /// required/unavailable/advanced_component 一律不用于自动准备。
  /// </summary>
  private RecognitionModeOption? FindReadyLocalTextMode(out string? reason)
  {
    reason = null;
    RuntimeSelectionService? selection = settingsOwner.CurrentModel?.RecognitionSelection?.Catalog;
    if (selection?.SupportsRecognitionModes is not true)
    {
      reason = "textlayer.noCatalog";
      return null;
    }
    RecognitionModeOption? preferred = null;
    RecognitionModeOption? fallback = null;
    bool anyTextMode = false;
    foreach (RecognitionModeOption mode in selection.RecognitionModes)
    {
      if (mode.Family != "text" || mode.PipelineId != "OCR")
      {
        continue;
      }
      anyTextMode = true;
      if (mode.Provisioning is not ("base_runtime" or "operating_system"))
      {
        continue;
      }
      if (mode.Availability != "ready")
      {
        continue;
      }
      if (recognitionChannel.ViewModel?.TaskEngine == mode.Id)
      {
        preferred ??= mode;
      }
      fallback ??= mode;
    }
    if (preferred is not null || fallback is not null)
    {
      return preferred ?? fallback;
    }
    reason = anyTextMode ? "textlayer.modeNotReady" : "textlayer.noLocalMode";
    return null;
  }

  private const int MaximumTextLayerLines = 2000;
  private const int MaximumTextLayerCharacters = 100_000;

  /// <summary>把 OCR text_blocks 投影为行级几何；越界/退化框整行丢弃。</summary>
  private static IReadOnlyList<RecognitionTextLayerLine>? ProjectTextLayerLines(
    JsonElement[]? blocks, out string? error)
  {
    error = null;
    if (blocks is null || blocks.Length == 0)
    {
      return null;
    }
    List<RecognitionTextLayerLine> lines = [];
    int characterCount = 0;
    foreach (JsonElement block in blocks)
    {
      if (lines.Count >= MaximumTextLayerLines)
      {
        error = "textlayer.tooLarge";
        return null;
      }
      if (block.ValueKind != JsonValueKind.Object ||
        !block.TryGetProperty("text", out JsonElement textElement) ||
        textElement.ValueKind != JsonValueKind.String)
      {
        continue;
      }
      string? text = textElement.GetString();
      if (string.IsNullOrWhiteSpace(text))
      {
        continue;
      }
      if (text.Length > MaximumTextLayerCharacters - characterCount)
      {
        error = "textlayer.tooLarge";
        return null;
      }
      if (!block.TryGetProperty("bbox", out JsonElement bboxElement) ||
        bboxElement.ValueKind != JsonValueKind.Array ||
        bboxElement.GetArrayLength() != 4)
      {
        continue;
      }
      double[] bbox = new double[4];
      bool valid = true;
      for (int index = 0; index < 4; index++)
      {
        JsonElement value = bboxElement[index];
        if (value.ValueKind != JsonValueKind.Number ||
          !value.TryGetDouble(out double coordinate) ||
          !double.IsFinite(coordinate))
        {
          valid = false;
          break;
        }
        bbox[index] = coordinate;
      }
      if (!valid ||
        bbox[0] < 0 || bbox[1] < 0 ||
        bbox[2] > 1000 || bbox[3] > 1000 ||
        bbox[0] >= bbox[2] || bbox[1] >= bbox[3])
      {
        continue;
      }
      int? order = null;
      if (block.TryGetProperty("order", out JsonElement orderElement) &&
        orderElement.ValueKind == JsonValueKind.Number &&
        orderElement.TryGetInt32(out int orderValue) &&
        orderValue >= 0)
      {
        order = orderValue;
      }
      double? score = block.TryGetProperty("score", out JsonElement confidence) &&
        confidence.ValueKind == JsonValueKind.Number && confidence.TryGetDouble(out double number) &&
        double.IsFinite(number) && number is >= 0 and <= 1 ? number : null;
      lines.Add(new RecognitionTextLayerLine(
        text, bbox[0], bbox[1], bbox[2], bbox[3], order, score));
      characterCount += text.Length;
    }
    if (lines.Count == 0) return null;
    bool ordered = lines.All(line => line.Order is not null);
    return ordered
      ? [.. lines.OrderBy(line => line.Order).ThenBy(line => line.Y1).ThenBy(line => line.X1)]
      : [.. lines.OrderBy(line => line.Y1).ThenBy(line => line.X1)];
  }

  private void ClearScreenshotSession(ImageSessionChannel channel)
  {
    if (ReferenceEquals(channel, recognitionChannel)) ClearResultActions();
    if (channel.SessionId is { } id)
      ScreenshotSessionDetached?.Invoke(id, channel.Revision);
    if (channel.SessionInput is { } input)
      resources.ReleaseResource(input);
    if (channel.SessionResult is { } result) resources.ReleaseResource(result);
    if (channel.SessionStructuredResult is { } structured) resources.ReleaseResource(structured);
    channel.SessionStructuredResult = null;
    channel.SessionId = null;
    channel.Revision = 0;
    channel.TextSelectionRequested = false;
    channel.SceneEditing = false;
    channel.SessionInput = null;
    channel.SessionResult = null;
    channel.ExcludeBoxes = [];
  }

  private RecognitionWorkbenchState? StartRecognition(
    Func<RecognitionViewModel, Task> action,
    CancellationToken cancellationToken,
    bool screenCapture = false)
  {
    _ = RecognitionVm;
    // 截图类输入与其他入口共用单飞 guard：选区进行中重复触发（同动作或
    // 跨纯截图/识别）先被拒绝，再推进 generation/清会话；同步启动段
    // 故障在此释放 guard，后台完成由 CompleteRecognitionAsync 的 finally
    // 释放。
    if (screenCapture && Interlocked.Exchange(ref captureInFlight, 1) != 0)
    {
      throw new CaptureInProgressException();
    }

    try
    {
      long generation = AdvanceImageGeneration(recognitionChannel);
      // 新输入（文件/剪贴板/拖入/即时截图识别）取代 recognition 通道的
      // 截图会话：旧会话命令立即失效，避免旧图混入新输入。imageEdit
      // 通道的会话与输入不受影响（两页独立）。
      ClearScreenshotSession(recognitionChannel);
      InvalidateScreenshotTextLayer(recognitionChannel);
      return operations.Start(
        RecognitionState(true, "recognition.running"),
        () => CompleteRecognitionAsync(
          action,
          generation,
          cancellationToken,
          screenCapture));
    }
    catch
    {
      if (screenCapture)
      {
        Interlocked.Exchange(ref captureInFlight, 0);
      }

      throw;
    }
  }

  internal RecognitionWorkbenchState RecognitionState(
    bool isBusy,
    string statusCode,
    WorkbenchResourceReference? input = null,
    WorkbenchResourceReference? result = null,
    WorkbenchResourceReference? structured = null)
  {
    SynchronizeRecognitionMode();
    return new(
      isBusy,
      statusCode,
      input,
      result,
      selectionPolicy.RecognitionEngines(),
      recognitionChannel.ViewModel?.TaskEngine,
      CurrentScreenshotSession(recognitionChannel),
      StructuredResult: structured,
      ResultImage: result is null ? null : resultImage);
  }

  /// <summary>Runtime 回显的已提交默认识别模式 id；仅 Bound 状态非空。</summary>
  private async Task CompleteRecognitionAsync(
    Func<RecognitionViewModel, Task> action,
    long generation,
    CancellationToken cancellationToken,
    bool screenCapture)
  {
    // 截图入口（主窗/热键/悬浮栏）统一让位悬浮工具栏；非截图输入不受影响。
    // 无论成功、取消或失败都释放截图单飞 guard，保证可重试。
    try
    {
      RecognitionWorkbenchState state;
      using (screenCapture ? shellActions?.SuspendFloatingToolbarForCapture() : null)
      {
        bool deferredSelection = !await selectionPolicy.EnsureSelectionLoadedAsync(cancellationToken);
        SynchronizeRecognitionMode(requireUsable: true);
        state = await RunRecognitionAsync(action, generation, cancellationToken);
        // 普通识别输入（选图/粘贴/拖放）完成后建立 recognition 自身编辑
        // 会话：编辑器获得 sessionId/修订合同，去水印/旋转/屏蔽等后处理
        // 之后可显式识别当前图；不改变“选图即识别”的既有自动语义。
        // 代际在 adoption 内部先行校验：结果发布 await 期间被取消/新输入
        // 取代时不建会话、不覆盖新状态。
        state = AdoptSessionForRecognitionInput(state, generation);
        if (deferredSelection)
        {
          // The run started before the Supervisor attached; its submit already
          // waited for readiness, so the authoritative catalog can load now and
          // the completion state carries the engine list.
          try
          {
            await selectionPolicy.EnsureSelectionLoadedAsync(cancellationToken);
            state = state with { Engines = selectionPolicy.RecognitionEngines() };
          }
          catch (OperationCanceledException)
          {
            throw;
          }
          catch (Exception)
          {
            // 本次识别已完成；目录补载失败只影响引擎列表，不推翻结果。
          }
        }

      }
      // 完成状态发布前恢复悬浮栏，调用方看到终态时截图让位已结束。
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(new RecognitionWorkbenchState(
          false,
          "recognition.cancelled"));
      }
    }
    catch (RecognitionModeUnavailableException error)
    {
      // 严格模式合同：模式不可用时保留引擎目录与用户选择，指向环境修复。
      AppLog.Warn($"Recognition mode override refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Recognition mode selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(ModeUnavailableRecognitionState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Recognition workbench operation failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        operations.PublishTerminal(new RecognitionWorkbenchState(false, "recognition.failed"));
      }
    }
    finally
    {
      if (screenCapture)
      {
        Interlocked.Exchange(ref captureInFlight, 0);
        // 截图识别终态（完成/失败/取消）后显示主窗呈现结果：仅在真正
        // 进入后台的会话上触发，拒绝重入的命令不经过此处。复用现有
        // ShowWorkbench 动作，不新增完成接口或轮询。
        shellActions?.TryDispatch(HotkeyActionCatalog.ShowWorkbench);
      }
    }
  }

  private async Task<(WorkbenchResourceReference? Result, WorkbenchResourceReference? Structured,
    WorkbenchResourceReference? Image)> PublishRecognitionResultAsync(
    RecognitionViewModel viewModel, RecognizeResponse? response, RecognitionInput? source,
    CancellationToken cancellationToken)
  {
    WorkbenchResourceReference? result = null, structured = null, image = null;
    try
    {
      if (response is null) return (null, null, null);
      result = await resources.PublishBytesAsync(Encoding.UTF8.GetBytes(response.Text),
        "text/plain; charset=utf-8", ".txt", cancellationToken);
      structured = await resources.PublishStructuredResultAsync(response, viewModel.FetchResultAssetAsync, cancellationToken);
      // 参考图统一重编码为无方向 PNG，与引擎存储像素坐标系一致；失败不发布。
      if (source is not null &&
        await WorkbenchResultReferenceImage.NormalizeAsync(
          source.Data, cancellationToken) is { } reference)
      {
        image = await resources.PublishBytesAsync(reference.Data, reference.MediaType,
          WorkbenchResourcesOwner.ExtensionForMediaType(reference.MediaType), cancellationToken);
      }
      return (result, structured, image);
    }
    catch
    {
      ReleaseResultResources(result, structured, image);
      throw;
    }
  }

  private void ReleaseResultResources(WorkbenchResourceReference? result,
    WorkbenchResourceReference? structured, WorkbenchResourceReference? image)
  {
    if (result is not null) resources.ReleaseResource(result);
    if (structured is not null) resources.ReleaseResource(structured);
    if (image is not null) resources.ReleaseResource(image);
  }

  /// <summary>
  /// 在 recognition 通道上执行一次识别并发布终态投影。结果资源发布的
  /// await 窗口后先核对调用者代际：过期则释放本次 input/result/structured
  /// 资源，不改共享 resultActions/通道状态；未过期才收编共享字段。
  /// </summary>
  private async Task<RecognitionWorkbenchState> RunRecognitionAsync(
    Func<RecognitionViewModel, Task> action,
    long generation,
    CancellationToken cancellationToken)
  {
    RecognitionViewModel viewModel = RecognitionVm;
    await action(viewModel);
    RecognizeResponse? response = viewModel.Result;
    RecognitionInput? source = viewModel.ResultInput;
    WorkbenchResourceReference? input = null;
    if (viewModel.CurrentInput is { } currentInput)
    {
      input = await resources.PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        WorkbenchResourcesOwner.ExtensionForMediaType(currentInput.MediaType),
        cancellationToken);
    }
    WorkbenchResourceReference? result, structured, image;
    try
    {
      (result, structured, image) = await PublishRecognitionResultAsync(viewModel, response, source, cancellationToken);
    }
    catch
    {
      if (input is not null) resources.ReleaseResource(input);
      throw;
    }
    if (generation != Volatile.Read(ref recognitionChannel.Generation) || !ReferenceEquals(response, viewModel.Result))
    {
      // 本次运行已被取代（取消/新输入/新交接）：本次发布资源确定性回收，
      // 不改共享 resultActions，也不把旧资源写回新状态。
      if (input is not null) resources.ReleaseResource(input);
      ReleaseResultResources(result, structured, image);
      return RecognitionState(
        viewModel.IsBusy,
        RecognitionStatusCode(viewModel));
    }
    SetResultImage(image);
    SetStructuredResult(structured);
    if (response is not null)
    {
      resultActions = viewModel.CreateResultActions(
        resultActionPlatform);
    }
    return RecognitionState(
      viewModel.IsBusy,
      RecognitionStatusCode(viewModel),
      input,
      result,
      structured: structured);
  }

  /// <summary>
  /// 为普通识别输入建立 recognition 自身编辑会话：输入/结果资源直接收编
  /// 为会话基准（不重发布 URL，编辑器历史稳定），修订/关闭/显式识别沿用
  /// 既有会话命令。仅在真实完成（有结果）且代际未变（结果发布 await 期间
  /// 未被取消/新输入取代）时建立：失败/取消终态保持既有无会话可见性合同，
  /// 迟到的旧回调不得覆盖新输入状态；无输入或已有会话时不建。交接调用
  // 可随快照携带命令时刻冻结的排除框（不异步重读可变通道状态）。
  /// </summary>
  private RecognitionWorkbenchState AdoptSessionForRecognitionInput(
    RecognitionWorkbenchState state,
    long generation,
    IReadOnlyList<WorkbenchExclusionBox>? excludeBoxes = null)
  {
    if (state.Result is null ||
      generation != Volatile.Read(ref recognitionChannel.Generation) ||
      recognitionChannel.SessionId is not null ||
      recognitionChannel.ViewModel?.CurrentInput is null)
    {
      return state;
    }
    recognitionChannel.SessionId = Guid.NewGuid();
    recognitionChannel.Revision = 0;
    recognitionChannel.TextSelectionRequested = false;
    recognitionChannel.SceneEditing = false;
    recognitionChannel.SessionInput = state.Input;
    recognitionChannel.SessionResult = state.Result;
    recognitionChannel.SessionStructuredResult = state.StructuredResult;
    recognitionChannel.ExcludeBoxes = excludeBoxes ?? [];
    SetStructuredResult(state.StructuredResult);
    return SessionRecognitionState(state.IsBusy, state.StatusCode);
  }

  private RecognitionWorkbenchState CancelRecognition()
  {
    RecognitionViewModel viewModel = RecognitionVm;
    AdvanceImageGeneration(recognitionChannel);
    recognitionChannel.CaptureDemand?.Cancel();
    viewModel.Cancel();
    viewModel.InvalidateResult();
    ClearResultActions();
    if (recognitionChannel.SessionId is not null)
    {
      // 取消保留会话编辑基准；运行中结果与文字层准备丢弃后可重新触发。
      recognitionChannel.SessionResult = null;
      InvalidateScreenshotTextLayer(recognitionChannel);
      return SessionRecognitionState(false, "recognition.cancelled");
    }
    return new RecognitionWorkbenchState(
      false,
      "recognition.cancelled",
      null,
      null);
  }

  private async Task<RecognitionWorkbenchState> CopyRecognitionAsync(
    CopyRecognitionResultCommand command,
    CancellationToken cancellationToken)
  {
    ResultActions actions = resultActions ??
      throw new InvalidOperationException("No recognition result is available.");
    ResultCopyFormat format = command.Format switch
    {
      "rich" => ResultCopyFormat.Rich,
      "markdown" => ResultCopyFormat.Markdown,
      _ => ResultCopyFormat.Plain,
    };
    await actions.CopyAsync(format, cancellationToken);
    return await CurrentRecognitionStateAsync("recognition.copied", cancellationToken);
  }

  /// <summary>
  /// 结构化区块的原生剪贴板复制：Web 只提交宿主发布过的 opaque 资源 URI、
  /// 区块序号与格式；宿主重读权威文件构建 TSV/HTML/LaTeX 负载并经平台
  /// 剪贴板写入（含 busy 重试）。未知资源/越界序号/类型不符一律拒绝，
  /// 不把任意网页文本写入系统剪贴板。
  /// </summary>
  private async Task<RecognitionWorkbenchState?> CopyStructuredResultAsync(
    CopyStructuredResultCommand command,
    CancellationToken cancellationToken)
  {
    if (!resources.structuredResourceFiles.TryGetValue(command.ResourceUri, out string? path) ||
      !File.Exists(path))
    {
      throw new InvalidOperationException(
        "The structured result resource is no longer available.");
    }
    await using WorkbenchResourceResponse resource = await resourceBroker.OpenAsync(
      new Uri(command.ResourceUri), cancellationToken);
    JsonDocument document;
    try
    {
      document = await JsonDocument.ParseAsync(resource.Content, cancellationToken: cancellationToken);
    }
    catch (Exception error) when (error is JsonException or ArgumentException)
    {
      throw new InvalidOperationException(
        "The structured result resource is no longer readable.",
        error);
    }
    using (document)
    {
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Array ||
        command.BlockIndex >= root.GetArrayLength())
      {
        throw new InvalidOperationException("The structured block is no longer available.");
      }
      JsonElement block = root[command.BlockIndex];
      if (command.Format == "table")
      {
        if (!StructuredResultClipboard.TryBuildTable(block, out string? tsv, out string? html))
        {
          throw new InvalidOperationException("The structured block is not a valid table.");
        }
        await structuredClipboard.WriteTableAsync(tsv!, html!, cancellationToken);
      }
      else if (!StructuredResultClipboard.TryGetFormulaText(block, out string? latex) ||
        string.IsNullOrWhiteSpace(latex))
      {
        throw new InvalidOperationException("The structured block is not a valid formula.");
      }
      else
      {
        await structuredClipboard.WriteTextAsync(latex!, cancellationToken);
      }
    }
    return null;
  }

  /// <summary>读取宿主发布的结构化资源并解析为 JSON；缺失/不可解析一律拒绝。</summary>
  private async Task<JsonDocument> OpenStructuredDocumentAsync(
    string resourceUri,
    CancellationToken cancellationToken)
  {
    if (!resources.structuredResourceFiles.TryGetValue(resourceUri, out string? path) ||
      !File.Exists(path))
    {
      throw new InvalidOperationException(
        "The structured result resource is no longer available.");
    }
    await using WorkbenchResourceResponse resource = await resourceBroker.OpenAsync(
      new Uri(resourceUri), cancellationToken);
    try
    {
      return await JsonDocument.ParseAsync(resource.Content, cancellationToken: cancellationToken);
    }
    catch (Exception error) when (error is JsonException or ArgumentException)
    {
      throw new InvalidOperationException(
        "The structured result resource is no longer readable.",
        error);
    }
  }

  /// <summary>
  /// 按模式复制纯文本行结果：宿主重读权威结构化资源里的预计算 modes 文本
  /// 写入平台剪贴板（raw = Runtime raw_text 原样）；资源必须是当前结果
  /// 发布的结构化资源（新结果/编辑后旧 URI 即过期，fail closed）。
  /// </summary>
  private async Task<RecognitionWorkbenchState?> CopyRecognitionLinesAsync(
    CopyRecognitionLinesCommand command,
    CancellationToken cancellationToken)
  {
    EnsureCurrentStructuredResource(command.ResourceUri);
    using JsonDocument document = await OpenStructuredDocumentAsync(
      command.ResourceUri, cancellationToken);
    EnsureCurrentStructuredResource(command.ResourceUri);
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Array)
    {
      throw new InvalidOperationException(
        "The structured result resource is no longer available.");
    }
    foreach (JsonElement block in root.EnumerateArray())
    {
      if (block.ValueKind != JsonValueKind.Object ||
        !block.TryGetProperty("type", out JsonElement type) ||
        type.ValueKind != JsonValueKind.String ||
        type.GetString() != "lines" ||
        !block.TryGetProperty("modes", out JsonElement modes) ||
        modes.ValueKind != JsonValueKind.Object ||
        !modes.TryGetProperty(command.Mode, out JsonElement modeText) ||
        modeText.ValueKind != JsonValueKind.String)
      {
        continue;
      }
      string text = modeText.GetString()!;
      if (text.Length > 0)
      {
        await structuredClipboard.WriteTextAsync(text, cancellationToken);
        return null;
      }
    }
    throw new InvalidOperationException(
      "The structured result does not contain copyable lines.");
  }

  /// <summary>表格子范围的原生复制：合并单元格裁剪到范围边界，Word/Excel 语义一致。</summary>
  private async Task<RecognitionWorkbenchState?> CopyStructuredCellsAsync(
    CopyStructuredCellsCommand command,
    CancellationToken cancellationToken)
  {
    EnsureCurrentStructuredResource(command.ResourceUri);
    using JsonDocument document = await OpenStructuredDocumentAsync(
      command.ResourceUri, cancellationToken);
    EnsureCurrentStructuredResource(command.ResourceUri);
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Array ||
      command.BlockIndex >= root.GetArrayLength())
    {
      throw new InvalidOperationException("The structured block is no longer available.");
    }
    JsonElement block = root[command.BlockIndex];
    if (!StructuredResultClipboard.TryBuildTableRange(
      block,
      command.RowStart,
      command.RowEnd,
      command.ColumnStart,
      command.ColumnEnd,
      out string? tsv,
      out string? html))
    {
      throw new InvalidOperationException("The structured block is not a valid table.");
    }
    await structuredClipboard.WriteTableAsync(tsv!, html!, cancellationToken);
    return null;
  }

  /// <summary>先发布候选资源，复核当前结果后同步提交；失败或过期时回收候选。</summary>
  private async Task<RecognitionWorkbenchState> UpdateRecognitionLineAsync(
    UpdateRecognitionLineCommand command, CancellationToken cancellationToken)
  {
    EnsureCurrentStructuredResource(command.ResourceUri);
    RecognitionViewModel viewModel = RecognitionVm;
    RecognizeResponse expected = viewModel.Result ?? throw new InvalidOperationException("No result is available.");
    long generation = Volatile.Read(ref recognitionChannel.Generation);
    if (!viewModel.TryPrepareLineEdit(command.LineIndex, command.ExpectedOldText, command.NewText,
      out RecognizeResponse? updated, out string? error))
      throw new InvalidOperationException(error);
    ResultActions updatedActions = viewModel.CreateResultActions(resultActionPlatform);
    updatedActions.SetResult(updated!);
    WorkbenchResourceReference? input = null, result = null, structured = null;
    bool committed = false;
    try
    {
      if (recognitionChannel.SessionId is null && viewModel.CurrentInput is { } currentInput)
        input = await resources.PublishBytesAsync(currentInput.Data, currentInput.MediaType,
          WorkbenchResourcesOwner.ExtensionForMediaType(currentInput.MediaType), cancellationToken);
      result = await resources.PublishBytesAsync(Encoding.UTF8.GetBytes(updated!.Text),
        "text/plain; charset=utf-8", ".txt", cancellationToken);
      structured = await resources.PublishStructuredResultAsync(updated, viewModel.FetchResultAssetAsync, cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      EnsureCurrentStructuredResource(command.ResourceUri);
      if (generation != Volatile.Read(ref recognitionChannel.Generation) ||
        !viewModel.TryCommitLineEdit(expected, updated))
        throw new InvalidOperationException("识别结果已更新或失效，请刷新后重试。");
      WorkbenchResourceReference? previousResult = recognitionChannel.SessionResult;
      WorkbenchResourceReference? previousStructured = recognitionChannel.SessionStructuredResult;
      resultActions = updatedActions;
      SetStructuredResult(structured);
      if (recognitionChannel.SessionId is not null)
      {
        recognitionChannel.SessionResult = result;
        recognitionChannel.SessionStructuredResult = structured;
      }
      committed = true;
      if (previousResult is not null) resources.ReleaseResource(previousResult);
      if (previousStructured is not null) resources.ReleaseResource(previousStructured);
      return recognitionChannel.SessionId is not null
        ? SessionRecognitionState(false, "recognition.completed")
        : RecognitionState(false, "recognition.completed", input, result, structured);
    }
    finally
    {
      if (!committed)
      {
        if (input is not null) resources.ReleaseResource(input);
        if (result is not null) resources.ReleaseResource(result);
        if (structured is not null) resources.ReleaseResource(structured);
      }
    }
  }

  /// <summary>
  /// 当前置换守卫：被复制/编辑的资源必须是当前结果发布的结构化资源，
  /// 且权威剪贴板动作仍在（新结果/取消/会话关闭后旧 URI 过期）。
  /// </summary>
  private void EnsureCurrentStructuredResource(string resourceUri)
  {
    if (resultActions is null ||
      !string.Equals(latestStructuredUri, resourceUri, StringComparison.Ordinal))
    {
      throw new InvalidOperationException(
        "识别结果已更新或失效，请刷新后重试。");
    }
  }

  private async Task<RecognitionWorkbenchState> ExportRecognitionAsync(
    ExportRecognitionResultCommand command,
    CancellationToken cancellationToken)
  {
    // 权威快照：选择器 await 期间结果被替换/编辑时拒绝导出旧内容。
    ResultActions actions = resultActions ??
      throw new InvalidOperationException("No recognition result is available.");
    ResultExportFormat format = command.Format switch
    {
      "docx" => ResultExportFormat.Docx,
      "html" => ResultExportFormat.Html,
      "markdown" => ResultExportFormat.Markdown,
      "xlsx" => ResultExportFormat.Xlsx,
      _ => ResultExportFormat.Text,
    };
    // 模式化导出：仅对 lines 结果生效（结构化结果保留原 raw_text）。
    string? rawOverride = null;
    if (command.TextMode is { } mode && mode != StructuredResultClipboard.LineModeRaw)
    {
      RecognizeResponse? response = RecognitionVm.Result;
      if (response is not null &&
        response.ContentBlocks is not { Length: > 0 } &&
        response.RawBlocks is { Length: > 0 } blocks)
      {
        JsonObject? linesBlock = RecognitionLinesBlock.BuildBlock(
          blocks, response.RawText ?? response.Text);
        if (linesBlock is not null &&
          linesBlock["modes"]?[mode] is JsonValue text &&
          text.TryGetValue<string>(out string? modeText) &&
          modeText is { Length: > 0 })
        {
          rawOverride = modeText;
        }
      }
    }
    ExportResult? export = await actions.ExportAsync(
      format,
      cancellationToken,
      rawOverride,
      rawOverride,
      // 选择器 await 期间结果被替换/编辑时，写盘前拒绝导出旧内容。
      () => ReferenceEquals(resultActions, actions));
    return await CurrentRecognitionStateAsync(
      export is null ? "recognition.exportCancelled" : export.Incomplete ? "recognition.exportedIncomplete" : "recognition.exported",
      cancellationToken);
  }

  private async Task<RecognitionWorkbenchState> CurrentRecognitionStateAsync(
    string statusCode,
    CancellationToken cancellationToken)
  {
    if (recognitionChannel.SessionId is not null)
    {
      // 会话模式复用缓存资源：重发布新 URL 会重置编辑器历史。
      return SessionRecognitionState(false, statusCode);
    }
    RecognitionViewModel viewModel = recognitionChannel.ViewModel ??
      throw new InvalidOperationException("Recognition is unavailable.");
    RecognizeResponse? response = viewModel.Result;
    long generation = Volatile.Read(ref recognitionChannel.Generation);
    WorkbenchResourceReference? input = viewModel.CurrentInput is { } currentInput
      ? await resources.PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        WorkbenchResourcesOwner.ExtensionForMediaType(currentInput.MediaType),
        cancellationToken)
      : null;
    WorkbenchResourceReference? result, structured, image;
    try
    {
      (result, structured, image) = await PublishRecognitionResultAsync(viewModel, response, viewModel.ResultInput, cancellationToken);
    }
    catch
    {
      if (input is not null) resources.ReleaseResource(input);
      throw;
    }
    if (generation != Volatile.Read(ref recognitionChannel.Generation) || !ReferenceEquals(response, viewModel.Result))
    {
      if (input is not null) resources.ReleaseResource(input);
      ReleaseResultResources(result, structured, image);
      return CurrentRecognitionState();
    }
    SetStructuredResult(structured);
    SetResultImage(image);
    return RecognitionState(false, statusCode, input, result, structured);
  }

  private RecognitionWorkbenchState SetRecognitionOptions(SetRecognitionOptionsCommand command)
  {
    settingsOwner.EnsureModel();
    RecognitionModeOption mode = settingsOwner.EnsureModel().RecognitionSelection?.Catalog.FindRecognitionMode(command.ModeId)
      ?? throw new InvalidOperationException("Recognition mode is absent from this environment.");
    command.Options.ToWire(mode);
    if (optionsLayout is not null) command.Options.Save(optionsLayout, mode.Id);
    selectionPolicy.SetModeOptions(mode.Id, command.Options);
    SynchronizeRecognitionMode();
    synchronizeBatchMode();
    return CurrentRecognitionState();
  }
  /// <summary>
  /// 任务级模式/引擎严格 resolve：不可用即抛出，不回退默认；普通截图
  /// 动作栏的显式 typed 意图与设置页任务模式选择共用同一语义。
  /// </summary>
  private void ApplyTaskEngine(string? engine)
  {
    settingsOwner.EnsureModel();
    RecognitionSelectionSnapshot? snapshot = settingsOwner.EnsureModel().RecognitionSelection;
    RuntimeSelectionService? selection = snapshot?.Catalog;
    if (string.IsNullOrWhiteSpace(engine)) RecognitionVm.TaskEngine = null;
    else if (snapshot is not null && selection?.SupportsRecognitionModes is true)
    {
      RecognitionModeOption mode = selection.SelectRecognitionMode(engine);
      snapshot.MineruConfigFor(mode.Id);
      RecognitionVm.TaskEngine = mode.Id;
    }
    else if (selection?.SupportsEngineSelection is true && OcrEngineWire.Parse(engine) is OcrEngine engineValue)
    {
      selection.SelectEngine(engineValue);
      RecognitionVm.TaskEngine = OcrEngineWire.Format(engineValue);
    }
    else throw new RuntimeSelectionException(RuntimeSelectionErrorKind.CapabilityMissing,
      "The runtime does not provide a recognition selection catalog.");
  }

  private RecognitionWorkbenchState SetTaskEngine(SetTaskEngineCommand command)
  {
    _ = RecognitionVm;
    ApplyTaskEngine(command.Engine);
    return recognitionChannel.SessionId is not null
      ? SessionRecognitionState(RecognitionVm.IsBusy, SessionStatusCode(recognitionChannel))
      : RecognitionState(false, RecognitionStatusCode(RecognitionVm));
  }

  private void SynchronizeRecognitionMode(bool requireUsable = false)
  {
    RecognitionSelectionSnapshot? snapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    RecognitionViewModel? viewModel = recognitionChannel.ViewModel;
    if (viewModel is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      if (requireUsable) WorkbenchSelectionOwner.EnsureUsableModeOverrideOrThrow(snapshot?.Catalog, viewModel?.TaskEngine);
      viewModel?.SetRecognitionMode(null);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = WorkbenchSelectionOwner.EffectiveModeIdOrDefault(
      snapshot, viewModel.TaskEngine, requireUsable);
    RecognitionModeOption? Resolve(string? id) => string.IsNullOrWhiteSpace(id)
      ? null
      : requireUsable
        ? selection.SelectRecognitionMode(id)
        : WorkbenchSelectionOwner.TryFindRecognitionMode(selection, id);
    RecognitionModeOption? mode = Resolve(effectiveId);
    // mineru_document 任务携带类型化 MinerU 4 配置：全局偏好经快照入口
    // 解析（无偏好/无效偏好按快照语义 fail closed 或回退目录默认）。
    MineruConfig? config = requireUsable
      ? snapshot.MineruConfigFor(mode?.Id)
      : WorkbenchSelectionOwner.TryProjectMineruConfig(snapshot, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    viewModel.SetRecognitionMode(mode, config, selectionPolicy.GetModeOptions(mode));
  }

  private static string RecognitionStatusCode(
    RecognitionViewModel viewModel, bool? isBusy = null) =>
    (isBusy ?? viewModel.IsBusy)
      ? "recognition.running"
      : viewModel.TerminalState switch
      {
        JobState.Failed => "recognition.failed",
        JobState.Cancelled => "recognition.cancelled",
        _ => viewModel.HasResult ? "recognition.completed" : "recognition.ready",
      };

  internal sealed class AnnotatedImageOperationCancelledException : Exception
  {
  }

  internal sealed class ScreenshotSessionStaleException : Exception
  {
  }

  /// <summary>另一个截图/选区会话仍在进行；命令层单飞 guard 拒绝重入。</summary>
  internal sealed class CaptureInProgressException : Exception
  {
  }
  internal void InvalidateScreenshotSessionRecognitionOnMaintenance()
  {
    foreach (ImageSessionChannel channel in (ImageSessionChannel[])
      [recognitionChannel, imageEditChannel])
    {
      if (channel.RecognitionDemand is null &&
        (channel.SessionId is null ||
          (channel.ViewModel is not { IsBusy: true } &&
            channel.TextLayer is null && channel.SessionResult is null)))
      {
        continue;
      }
      WorkbenchImageOwner.AdvanceImageGeneration(channel);
      channel.ViewModel?.Cancel();
      channel.ViewModel?.InvalidateResult();
      if (channel == recognitionChannel)
      {
        ClearResultActions();
      }
      channel.SessionResult = null;
      InvalidateScreenshotTextLayer(channel);
      operations.Publish(SessionState(channel, false, "recognition.expired"));
    }
  }

  internal async Task RefreshCatalogStateAsync(CancellationToken cancellationToken)
  {
    RecognitionViewModel viewModel = RecognitionVm;
    SynchronizeRecognitionMode();
    RecognitionWorkbenchState current = await CurrentRecognitionStateAsync(
      "recognition.ready", cancellationToken);
    bool isBusy = viewModel.IsBusy;
    operations.Publish(current with
    {
      StatusCode = WorkbenchImageOwner.RecognitionStatusCode(viewModel, isBusy),
      IsBusy = isBusy,
      Engines = selectionPolicy.RecognitionEngines(),
      TaskEngine = viewModel.TaskEngine,
    });
  }
  internal void Stop()
  {
    AdvanceImageGeneration(recognitionChannel); AdvanceImageGeneration(imageEditChannel);
    foreach (ImageSessionChannel channel in new[] { recognitionChannel, imageEditChannel })
    {
      channel.ViewModel?.Cancel(); channel.TextPreparation?.Cancel(); channel.CaptureDemand?.Cancel();
      channel.TextLayerViewModel?.Cancel();
    }
  }
}
