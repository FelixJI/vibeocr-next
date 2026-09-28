using System.ComponentModel;
using System.Text;
using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Inference;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace VibeOCR.App.Workbench;

public sealed class DesktopWorkbenchCommandHandler :
  IWorkbenchCommandHandler,
  IWorkbenchStateSource,
  IWorkbenchBootstrapSource,
  IAsyncDisposable
{
  public static IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(
    [
      "recognition.file",
      "recognition.clipboard",
      "recognition.capture",
      "recognition.screenshotSession",
      "recognition.results",
      "recognition.annotation",
      "batch.add",
      "batch.export",
      "batch.run",
      "pdf.open",
      "pdf.rotate",
      "pdf.edit",
      "pdf.save",
      "qrcode.generate",
      "qrcode.decode",
      "qrcode.clipboard",
      "qrcode.save",
      "qrcode.openUrl",
      "about.openProject",
      "runtime.refresh",
      "settings.shell",
      "settings.hotkeys",
      "settings.floatingToolbar",
      "settings.selection",
      "runtime.maintenance",
      "recognition.engine",
      "update.check",
      "update.install",
      "diagnostics.export",
    ],
    StringComparer.Ordinal);

  private readonly Func<RecognitionViewModel> recognitionFactory;
  private readonly Func<BatchViewModel> batchFactory;
  private readonly Func<QrCodeViewModel> qrCodeFactory;
  private readonly Func<PdfViewModel> pdfFactory;
  private readonly Func<SettingsViewModel> settingsFactory;
  private readonly Lazy<ShellViewModel> shell;
  private readonly Lazy<UpdateViewModel> update;
  private readonly VibeOCR.App.ViewModels.DiagnosticsViewModel diagnostics;
  private readonly WorkbenchResourceBroker resourceBroker;
  private readonly string resourceRoot;
  private readonly Func<nint> windowHandle;
  private readonly WorkbenchAnnotationStore annotationStore;
  private readonly IAnnotatedImagePlatform annotatedImagePlatform;
  private readonly Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?>? pinScreenshot;
  private readonly Func<bool>? inferenceAttached;
  private readonly ShellActionDispatcher? shellActions;
  private readonly List<string> generatedFiles = [];
  private readonly HashSet<Task> backgroundOperations = [];
  private readonly HashSet<int> selectedPdfPages = [];
  private readonly Dictionary<int, WorkbenchResourceReference> pdfThumbnails = [];
  private RecognitionViewModel? recognition;
  private RecognitionViewModel? textLayerRecognition;
  private ResultActions? resultActions;
  private Guid? screenshotSessionId;
  private long screenshotSessionRevision;
  private bool screenshotTextSelectionRequested;
  private WorkbenchResourceReference? screenshotSessionInput;
  private WorkbenchResourceReference? screenshotSessionResult;
  private RecognitionTextLayerState? screenshotTextLayer;
  private long screenshotTextGeneration;
  private int captureInFlight;
  private BatchViewModel? batch;
  private string? batchTaskEngine;
  private QrCodeViewModel? qrCode;
  private PdfViewModel? pdf;
  private SettingsViewModel? settings;
  private WorkbenchTheme theme = WorkbenchTheme.System;
  private WorkbenchResourceReference? generatedQrResource;
  private long recognitionGeneration;
  private long batchGeneration;
  private long pdfGeneration;
  private long qrCodeGeneration;
  private long updateGeneration;
  private int batchWindowStart;
  private int pdfWindowStart;
  private int disposed;

  internal DesktopWorkbenchCommandHandler(
    Func<RecognitionViewModel> recognitionFactory,
    Func<BatchViewModel> batchFactory,
    Func<QrCodeViewModel> qrCodeFactory,
    Func<PdfViewModel> pdfFactory,
    Func<SettingsViewModel> settingsFactory,
    Func<ShellViewModel> shellFactory,
    Func<UpdateViewModel> updateFactory,
    VibeOCR.App.ViewModels.DiagnosticsViewModel diagnostics,
    WorkbenchResourceBroker resourceBroker,
    string resourceRoot,
    Func<nint> windowHandle,
    WorkbenchAnnotationStore annotationStore,
    IAnnotatedImagePlatform? annotatedImagePlatform = null,
    Func<bool>? inferenceAttached = null,
    Func<string?>? supervisorInstanceId = null,
    Func<RecognitionViewModel>? textLayerRecognitionFactory = null,
    Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?>? pinScreenshot = null,
    ShellActionDispatcher? shellActions = null)
  {
    this.recognitionFactory = recognitionFactory ??
      throw new ArgumentNullException(nameof(recognitionFactory));
    this.batchFactory = batchFactory ?? throw new ArgumentNullException(nameof(batchFactory));
    this.qrCodeFactory = qrCodeFactory ??
      throw new ArgumentNullException(nameof(qrCodeFactory));
    this.pdfFactory = pdfFactory ?? throw new ArgumentNullException(nameof(pdfFactory));
    this.settingsFactory = settingsFactory ??
      throw new ArgumentNullException(nameof(settingsFactory));
    ArgumentNullException.ThrowIfNull(shellFactory);
    ArgumentNullException.ThrowIfNull(updateFactory);
    shell = new Lazy<ShellViewModel>(shellFactory);
    update = new Lazy<UpdateViewModel>(() =>
    {
      UpdateViewModel viewModel = updateFactory();
      viewModel.PropertyChanged += OnUpdatePropertyChanged;
      return viewModel;
    });
    this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    this.resourceBroker = resourceBroker ??
      throw new ArgumentNullException(nameof(resourceBroker));
    this.resourceRoot = Path.GetFullPath(resourceRoot);
    this.windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
    this.annotationStore = annotationStore ??
      throw new ArgumentNullException(nameof(annotationStore));
    this.annotatedImagePlatform = annotatedImagePlatform ??
      new AnnotatedImagePlatform(this.windowHandle);
    this.inferenceAttached = inferenceAttached;
    this.supervisorInstanceId = supervisorInstanceId ?? (() => null);
    this.textLayerRecognitionFactory = textLayerRecognitionFactory ?? recognitionFactory;
    this.pinScreenshot = pinScreenshot;
    this.shellActions = shellActions;
  }

  private readonly Func<string?> supervisorInstanceId;
  private readonly Func<RecognitionViewModel> textLayerRecognitionFactory;

  public IReadOnlyList<WorkbenchState> InitialStates =>
  [
    RecognitionState(false, "recognition.ready"),
    CurrentBatchState(),
    new PdfWorkbenchState(false, "pdf.empty", 0, -1),
    new QrCodeWorkbenchState(false, "qrcode.ready", [], null),
    settings is null ? SettingsShellState() : SettingsState(settings),
    new UpdateWorkbenchState(false, "update.current", null, false),
    AboutState(),
    DiagnosticsState(),
  ];

  public event Action<WorkbenchState>? StateChanged;
  public event Action<RecognitionTextLayerState?>? ScreenshotTextLayerChanged;

  public async ValueTask PrepareBootstrapAsync(CancellationToken cancellationToken)
  {
    try
    {
      await EnsureSelectionLoadedAsync(cancellationToken);
    }
    catch (InferenceClientNotAttachedException)
    {
      // The window intentionally appears before Supervisor startup completes.
      // Bootstrap exposes no guessed catalog; the first command after attach
      // crosses the same gate and negotiates the authoritative runtime state.
    }
  }

  public async ValueTask<WorkbenchCommandOutcome> ExecuteAsync(
    WorkbenchCommand command,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(command);
    cancellationToken.ThrowIfCancellationRequested();
    try
    {
      WorkbenchState? state = command switch
      {
        SelectRecognitionImageCommand => StartRecognition(
          viewModel => viewModel.RecognizeFileAsync(cancellationToken),
          cancellationToken),
        RecognizeDroppedFileCommand dropped => StartRecognition(
          viewModel => viewModel.RecognizeDroppedFileAsync(
            dropped.Path,
            cancellationToken),
          cancellationToken),
        ReadRecognitionClipboardCommand => StartRecognition(
          viewModel => viewModel.RecognizeClipboardAsync(cancellationToken),
          cancellationToken),
        CaptureRecognitionScreenCommand => StartRecognition(
          viewModel => viewModel.RecognizeScreenshotAsync(cancellationToken),
          cancellationToken,
          screenCapture: true),
        CaptureScreenshotSessionCommand => StartScreenshotSession(false, cancellationToken),
        CaptureScreenshotTextSessionCommand => StartScreenshotSession(true, cancellationToken),
        CloseScreenshotSessionCommand => CloseScreenshotSession(),
        NotifyScreenshotSessionRevisionCommand notify => NotifyScreenshotRevision(notify),
        CopyScreenshotImageCommand copy => await CopyScreenshotImageAsync(
          copy,
          cancellationToken),
        SaveScreenshotImageCommand save => await SaveScreenshotImageAsync(
          save,
          cancellationToken),
        PinScreenshotImageCommand pin => PinScreenshotImage(pin),
        RecognizeScreenshotImageCommand recognize => await StartScreenshotRecognitionAsync(
          recognize,
          cancellationToken),
        PrepareScreenshotTextLayerCommand prepare => await PrepareScreenshotTextLayerAsync(
          prepare,
          cancellationToken),
        CancelScreenshotTextLayerCommand cancelTextLayer => CancelScreenshotTextLayer(
          cancelTextLayer),
        CopyScreenshotSelectionCommand copySelection => await CopyScreenshotSelectionAsync(
          copySelection,
          cancellationToken),
        CancelRecognitionCommand => CancelRecognition(),
        CopyRecognitionResultCommand copy => await CopyRecognitionAsync(
          copy,
          cancellationToken),
        ExportRecognitionResultCommand export => await ExportRecognitionAsync(
          export,
          cancellationToken),
        CopyAnnotatedImageCommand copy => await CopyAnnotatedImageAsync(
          copy,
          cancellationToken),
        SaveAnnotatedImageCommand save => await SaveAnnotatedImageAsync(
          save,
          cancellationToken),
        AddBatchFilesCommand => await AddBatchFilesAsync(cancellationToken),
        AddDroppedBatchFilesCommand dropped => AddDroppedBatchFiles(dropped),
        ExportBatchCommand export => await ExportBatchAsync(export, cancellationToken),
        StartBatchCommand => await StartBatchAsync(cancellationToken),
        CancelBatchCommand => CancelBatch(),
        ClearBatchCommand => ClearBatch(),
        MoveBatchItemCommand move => MoveBatchItem(move),
        RemoveBatchItemCommand remove => RemoveBatchItem(remove),
        SetBatchWindowCommand window => SetBatchWindow(window),
        SetBatchTaskEngineCommand taskEngine => SetBatchTaskEngine(taskEngine),
        OpenPdfCommand => await OpenPdfAsync(cancellationToken),
        OpenDroppedPdfCommand dropped => await OpenDroppedPdfAsync(
          dropped,
          cancellationToken),
        RotatePdfCommand rotate => await RotatePdfAsync(rotate, cancellationToken),
        ClosePdfCommand => ClosePdf(),
        DeletePdfPagesCommand => await DeletePdfPagesAsync(cancellationToken),
        OcrPdfPagesCommand => StartPdfOcr(cancellationToken),
        SavePdfCommand => await SavePdfAsync(cancellationToken),
        SelectPdfPagesCommand select => SelectPdfPages(select),
        SetPdfWindowCommand window => await SetPdfWindowAsync(
          window,
          cancellationToken),
        GenerateQrCodeCommand generate => StartQrCode(
          viewModel =>
          {
            viewModel.GenerateText = generate.Text;
            return viewModel.GenerateAsync(cancellationToken);
          },
          publishGeneratedImage: true,
          cancellationToken),
        DecodeQrCodeCommand => StartQrCode(
          viewModel => viewModel.DecodeAsync(QrCodeInputKind.File, cancellationToken),
          publishGeneratedImage: false,
          cancellationToken),
        DecodeDroppedQrCodeCommand dropped => StartQrCode(
          viewModel => viewModel.DecodeDroppedFileAsync(dropped.Path, cancellationToken),
          publishGeneratedImage: false,
          cancellationToken),
        DecodeQrCodeClipboardCommand => StartQrCode(
          viewModel => viewModel.DecodeAsync(
            QrCodeInputKind.Clipboard,
            cancellationToken),
          publishGeneratedImage: false,
          cancellationToken),
        CancelQrCodeCommand => CancelQrCode(),
        ClearQrCodeCommand => ClearQrCode(),
        SaveQrCodeCommand => await SaveQrCodeAsync(cancellationToken),
        OpenQrCodeUrlCommand openUrl => await OpenQrCodeUrlAsync(
          openUrl,
          cancellationToken),
        OpenProjectPageCommand => await OpenProjectPageAsync(cancellationToken),
        RefreshRuntimeCommand => await RefreshRuntimeAsync(cancellationToken),
        SetThemeCommand setTheme => SetTheme(setTheme),
        SetStartupCommand startup => SetStartup(startup),
        SetActionHotkeyCommand setActionHotkey => SetActionHotkey(setActionHotkey),
        ResetActionHotkeyCommand resetActionHotkey => ResetActionHotkey(resetActionHotkey),
        SetFloatingToolbarEnabledCommand toolbarEnabled => SetFloatingToolbarEnabled(
          toolbarEnabled),
        SetFloatingToolbarLayoutCommand toolbarLayout => SetFloatingToolbarLayout(
          toolbarLayout),
        ShowFloatingToolbarCommand => ShowFloatingToolbar(),
        HideFloatingToolbarCommand => HideFloatingToolbar(),
        SetDownloadSourceCommand source => await SetSourceAsync(
          source,
          cancellationToken),
        SetAcceleratorCommand accelerator => SetAccelerator(accelerator),
        SetRuntimeFeatureCommand feature => SetFeature(feature),
        SetMineruConnectionCommand mineru => await SetMineruConnectionAsync(
          mineru,
          cancellationToken),
        PrepareMineruConnectionCommand => await PrepareMineruConnectionAsync(
          cancellationToken),
        SetTaskEngineCommand taskEngine => SetTaskEngine(taskEngine),
        InstallRuntimeCommand => await InstallRuntimeAsync(cancellationToken),
        ConfirmRuntimeInstallCommand confirm => StartRuntimeInstall(confirm.PlanId, cancellationToken),
        CancelRuntimeMaintenanceCommand => CancelRuntimeMaintenance(),
        RetryRuntimeMaintenanceCommand => await RetryRuntimeMaintenanceAsync(
          cancellationToken),
        CheckUpdateCommand => await CheckUpdateAsync(cancellationToken),
        DownloadUpdateCommand => StartUpdateDownload(cancellationToken),
        CancelUpdateCommand => CancelUpdate(),
        CancelRuntimeForUpdateCommand => await CancelRuntimeForUpdateAsync(cancellationToken),
        ExportDiagnosticsCommand => await ExportDiagnosticsAsync(cancellationToken),
        _ => throw new InvalidOperationException("Unsupported desktop workbench command."),
      };
      // A null state was already published before its background operation started.
      return new WorkbenchCommandOutcome(state is null ? [] : [state], null);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (AnnotatedImageOperationCancelledException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "annotation_operation_cancelled",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.annotationOperationCancelled"));
    }
    catch (ScreenshotSessionStaleException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "screenshot_session_stale",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.screenshotSessionStale"));
    }
    catch (CaptureInProgressException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "capture_in_progress",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.captureInProgress"));
    }
    catch (Exception error) when (
      error is IOException or UnauthorizedAccessException or InvalidOperationException or
        ClipboardBusyException or WorkbenchAnnotationAccessException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "desktop_command_failed",
          WorkbenchProblemCategory.Unavailable,
          true,
          "workbench.error.desktopCommandFailed"));
    }
  }

  private async Task<RecognitionWorkbenchState> CopyAnnotatedImageAsync(
    CopyAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    await annotatedImagePlatform.CopyPngAsync(annotation.Path, cancellationToken);
    return CurrentRecognitionState();
  }

  private async Task<RecognitionWorkbenchState> SaveAnnotatedImageAsync(
    SaveAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    if (!await annotatedImagePlatform.SavePngAsync(annotation.Path, cancellationToken))
    {
      throw new AnnotatedImageOperationCancelledException();
    }
    return CurrentRecognitionState();
  }

  private RecognitionWorkbenchState CurrentRecognitionState()
  {
    if (screenshotSessionId is not null)
    {
      return SessionRecognitionState(
        false,
        screenshotSessionResult is null ? "recognition.session" : "recognition.completed");
    }
    return RecognitionState(
      false,
      recognition is null ? "recognition.ready" : RecognitionStatusCode(recognition));
  }

  /// <summary>当前截图会话的 wire 投影；无会话时为 null。</summary>
  private RecognitionScreenshotSessionState? CurrentScreenshotSession() =>
    screenshotSessionId is { } id
      ? new RecognitionScreenshotSessionState(id.ToString("N"), screenshotSessionRevision,
        screenshotTextSelectionRequested)
      : null;

  /// <summary>会话状态固定复用缓存的基准图/结果资源，避免重发布新 URL 导致编辑器重置。</summary>
  private RecognitionWorkbenchState SessionRecognitionState(
    bool isBusy,
    string statusCode) => new(
      isBusy,
      statusCode,
      screenshotSessionInput,
      screenshotSessionResult,
      RecognitionEngines(),
      recognition?.TaskEngine,
      CurrentScreenshotSession(),
      screenshotTextLayer);

  private void ValidateScreenshotSession(Guid sessionId, long revision)
  {
    if (screenshotSessionId != sessionId || screenshotSessionRevision != revision)
    {
      throw new ScreenshotSessionStaleException();
    }
  }

  /// <summary>取消在途文字层准备并丢弃旧层；编辑/换图/维护/关闭后调用。</summary>
  private void InvalidateScreenshotTextLayer()
  {
    Interlocked.Increment(ref screenshotTextGeneration);
    textLayerRecognition?.Cancel();
    screenshotTextLayer = null;
    ScreenshotTextLayerChanged?.Invoke(null);
  }

  private WorkbenchAnnotationFile TakeScreenshotAnnotation(string resourceUri) =>
    annotationStore.Take(new Uri(resourceUri));

  private RecognitionWorkbenchState? StartScreenshotSession(
    bool textSelectionRequested, CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    // 截图单飞：任一截图入口（主窗/热键/悬浮栏/页面按钮）同时只允许一个
    // 选区会话；拒绝重复必须先于推进 generation/清会话，同步启动段故障
    // 则在此释放 guard，否则截图永久 busy。
    if (Interlocked.Exchange(ref captureInFlight, 1) != 0)
    {
      throw new CaptureInProgressException();
    }

    try
    {
      long generation = Interlocked.Increment(ref recognitionGeneration);
      // 新捕获立即取代旧会话：选取期间旧会话命令一律失效，
      // 换图/重复点击不会把旧图或旧修订带入新会话。
      ClearScreenshotSession();
      InvalidateScreenshotTextLayer();
      recognition.InvalidateResult();
      resultActions = null;
      return PublishStartThenTrack(
        SessionRecognitionState(true, "recognition.running"),
        () => CompleteScreenshotSessionAsync(generation, textSelectionRequested,
          cancellationToken));
    }
    catch
    {
      Interlocked.Exchange(ref captureInFlight, 0);
      throw;
    }
  }

  private async Task CompleteScreenshotSessionAsync(
    long generation,
    bool textSelectionRequested,
    CancellationToken cancellationToken)
  {
    // 所有截图入口（主窗/热键/悬浮栏）统一让位：截图期间工具栏与感应条
    // 均不入画面；结束后恢复原态。无论成功、取消或失败都释放单飞 guard。
    try
    {
      using (shellActions?.SuspendFloatingToolbarForCapture())
      {
        // 纯截图路径：不加载 Runtime 目录（EnsureSelectionLoadedAsync）、
        // 不做 requireUsable 模式协商；Supervisor 未连接/维护中同样可完成。
        await recognition!.CaptureScreenshotSessionAsync(cancellationToken);
        if (generation != Volatile.Read(ref recognitionGeneration))
        {
          return;
        }
        if (recognition.CurrentInput is { } captured)
        {
          WorkbenchResourceReference input = await PublishBytesAsync(
            captured.Data,
            captured.MediaType,
            ExtensionForMediaType(captured.MediaType),
            cancellationToken);
          // PublishBytesAsync 期间取消/新截图会推进 generation：
          // 旧捕获完成不得复活已被取代的会话。
          if (generation != Volatile.Read(ref recognitionGeneration))
          {
            return;
          }

          screenshotSessionId = Guid.NewGuid();
          screenshotSessionRevision = 0;
          screenshotTextSelectionRequested = textSelectionRequested;
          screenshotSessionInput = input;
          screenshotSessionResult = null;
          StateChanged?.Invoke(SessionRecognitionState(false, "recognition.session"));
        }
        else
        {
          // 用户在选区界面取消：不残留会话与遮罩状态；此前的文件输入保持可见。
          StateChanged?.Invoke(await CurrentRecognitionStateAsync(
            "recognition.cancelled",
            cancellationToken));
        }
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(await CurrentRecognitionStateAsync(
          "recognition.cancelled",
          cancellationToken));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot session capture failed", error);
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.failed"));
      }
    }
    finally
    {
      Interlocked.Exchange(ref captureInFlight, 0);
      // 纯截图会话的真实终态（完成/失败/取消）后显示主窗呈现编辑器：
      // #109 热键→编辑器路径必需，隐藏主窗不得保留。仅在真正进入后台的
      // 会话上触发，开始与被拒重入不经过此处；复用同一 ShowWorkbench
      // 动作，不新增完成接口。
      shellActions?.TryDispatch(HotkeyActionCatalog.ShowWorkbench);
    }
  }

  private RecognitionWorkbenchState NotifyScreenshotRevision(
    NotifyScreenshotSessionRevisionCommand command)
  {
    recognition ??= recognitionFactory();
    if (screenshotSessionId != command.SessionId ||
      command.Revision <= screenshotSessionRevision)
    {
      // 未知会话或乱序/过期通知：保留宿主权威状态，不回退修订。
      return SessionRecognitionState(false, SessionStatusCode());
    }
    screenshotSessionRevision = command.Revision;
    // 任何内容修订都使旧识别结果与文字层失效：迟到响应不得覆盖当前内容。
    Interlocked.Increment(ref recognitionGeneration);
    recognition.Cancel();
    recognition.InvalidateResult();
    InvalidateScreenshotTextLayer();
    resultActions = null;
    screenshotSessionResult = null;
    return SessionRecognitionState(false, "recognition.session");
  }

  private string SessionStatusCode() => screenshotSessionResult is null
    ? "recognition.session"
    : "recognition.completed";

  private async Task<RecognitionWorkbenchState> CopyScreenshotImageAsync(
    CopyScreenshotImageCommand command,
    CancellationToken cancellationToken)
  {
    // 快照语义：开始时校验会话/修订并冻结本次导出的 PNG，
    // 原生异步（剪贴板重试）期间允许该快照完成；出口只回报当前投影，
    // 不回写旧会话状态。
    ValidateScreenshotSession(command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    await annotatedImagePlatform.CopyPngAsync(annotation.Path, cancellationToken);
    return CurrentRecognitionState();
  }

  private async Task<RecognitionWorkbenchState> SaveScreenshotImageAsync(
    SaveScreenshotImageCommand command,
    CancellationToken cancellationToken)
  {
    // 与复制同一快照语义：FileSavePicker 等待期间允许冻结的导出落盘，
    // 出口只回报当前投影，不回写旧会话状态。
    ValidateScreenshotSession(command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    if (!await annotatedImagePlatform.SavePngAsync(annotation.Path, cancellationToken))
    {
      throw new AnnotatedImageOperationCancelledException();
    }
    return CurrentRecognitionState();
  }

  private RecognitionWorkbenchState PinScreenshotImage(PinScreenshotImageCommand command)
  {
    ValidateScreenshotSession(command.SessionId, command.Revision);
    if (pinScreenshot is null)
    {
      throw new InvalidOperationException("Desktop pinning is unavailable.");
    }
    WorkbenchAnnotationFile image = TakeScreenshotAnnotation(command.ResourceUri);
    try
    {
      pinScreenshot(image, command.SessionId, command.Revision,
        screenshotTextLayer is { Status: "textlayer.ready" } layer &&
        layer.Binding?.SessionId == command.SessionId.ToString("N") &&
        layer.Binding.Revision == command.Revision &&
        layer.ServiceInstance == (supervisorInstanceId() ?? string.Empty) ? layer : null);
    }
    catch
    {
      image.Dispose();
      throw;
    }
    return CurrentRecognitionState();
  }

  private async Task<RecognitionWorkbenchState?> StartScreenshotRecognitionAsync(
    RecognizeScreenshotImageCommand command,
    CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    ValidateScreenshotSession(command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    // 只把编辑器导出的最终 PNG 送入识别；不回退未编辑基准图。
    byte[] png = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
    // 读取租约期间会话/修订变更则拒绝启动，不消费推理配额。
    ValidateScreenshotSession(command.SessionId, command.Revision);
    var input = new RecognitionInput(
      png,
      "image/png",
      "screenshot-session-final.png",
      "screenshot-session");
    long generation = Interlocked.Increment(ref recognitionGeneration);
    Guid sessionId = command.SessionId;
    long revision = command.Revision;
    resultActions = null;
    screenshotSessionResult = null;
    return PublishStartThenTrack(
      SessionRecognitionState(true, "recognition.running"),
      () => CompleteScreenshotRecognitionAsync(
        generation,
        sessionId,
        revision,
        input,
        cancellationToken));
  }

  private async Task CompleteScreenshotRecognitionAsync(
    long generation,
    Guid sessionId,
    long revision,
    RecognitionInput input,
    CancellationToken cancellationToken)
  {
    try
    {
      // 只有显式识别才同步 Runtime 目录并做 requireUsable 模式协商。
      bool deferredSelection = !await EnsureSelectionLoadedAsync(cancellationToken);
      // 目录加载期间编辑/关闭/换图会推进 generation：
      // 提交前复查，不把旧图发送给 Runtime。
      if (generation != Volatile.Read(ref recognitionGeneration))
      {
        return;
      }
      SynchronizeRecognitionMode(requireUsable: true);
      await recognition!.RecognizeCapturedInputAsync(input, cancellationToken);
      if (deferredSelection)
      {
        try
        {
          await EnsureSelectionLoadedAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
          throw;
        }
        catch (Exception)
        {
          // 识别已完成；目录补载失败只影响引擎列表，不推翻结果。
        }
      }
      if (generation != Volatile.Read(ref recognitionGeneration))
      {
        return;
      }
      if (screenshotSessionId == sessionId && screenshotSessionRevision == revision)
      {
        if (recognition.Result is not null)
        {
          resultActions = recognition.CreateResultActions(
            new WindowsResultActionPlatform(windowHandle));
        }
        WorkbenchResourceReference? result = string.IsNullOrEmpty(recognition.ResultText)
          ? null
          : await PublishBytesAsync(
            Encoding.UTF8.GetBytes(recognition.ResultText),
            "text/plain; charset=utf-8",
            ".txt",
            cancellationToken);
        // 结果资源发布期间编辑/换图/维护会推进 generation：丢弃迟到结果。
        if (generation != Volatile.Read(ref recognitionGeneration) ||
          screenshotSessionId != sessionId ||
          screenshotSessionRevision != revision)
        {
          return;
        }
        screenshotSessionResult = result;
        StateChanged?.Invoke(SessionRecognitionState(
          false,
          RecognitionStatusCode(recognition)));
      }
      else
      {
        // 会话或内容修订已变更：迟到结果丢弃，不覆盖新会话。
        recognition.InvalidateResult();
        resultActions = null;
        screenshotSessionResult = null;
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.expired"));
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.cancelled"));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot session recognition failed", error);
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.failed"));
      }
    }
  }

  private RecognitionWorkbenchState CloseScreenshotSession()
  {
    recognition ??= recognitionFactory();
    Interlocked.Increment(ref recognitionGeneration);
    recognition.Cancel();
    recognition.InvalidateResult();
    resultActions = null;
    InvalidateScreenshotTextLayer();
    ClearScreenshotSession();
    return new RecognitionWorkbenchState(false, "recognition.ready");
  }

  /// <summary>
  /// 准备原位选字文字层：只消费编辑器导出的最终 PNG，且只在目录中存在
  /// ready 的本地轻量文字模式时提交；同一绑定已在准备/已就绪时不重复
  /// 提交任务。提交固定使用轻量文字配置（OCR 管线 + 所选模式引擎 +
  /// 空选项/空 mineru），不继承用户任务级 OCR 选项；不会触发依赖安装、
  /// 远端或重型文档管线。
  /// </summary>
  private async Task<RecognitionWorkbenchState?> PrepareScreenshotTextLayerAsync(
    PrepareScreenshotTextLayerCommand command,
    CancellationToken cancellationToken,
    byte[]? pinnedPng = null)
  {
    recognition ??= recognitionFactory();
    ValidateScreenshotSession(command.SessionId, command.Revision);
    string sessionId = command.SessionId.ToString("N");
    if (screenshotTextLayer?.Binding is { } existing &&
      existing.SessionId == sessionId &&
      existing.Revision == command.Revision &&
      screenshotTextLayer.Status is "textlayer.preparing" or "textlayer.ready")
    {
      return SessionRecognitionState(false, SessionStatusCode());
    }

    if (inferenceAttached?.Invoke() == false)
    {
      screenshotTextLayer = new RecognitionTextLayerState(
        "textlayer.unavailable", "textlayer.serviceUnavailable",
        new RecognitionScreenshotSessionState(sessionId, command.Revision),
        null, supervisorInstanceId(), null);
      return SessionRecognitionState(false, SessionStatusCode());
    }
    await EnsureSelectionLoadedAsync(cancellationToken);
    RecognitionModeOption? mode = FindReadyLocalTextMode(out string? reason);
    if (mode is null)
    {
      screenshotTextLayer = new RecognitionTextLayerState(
        "textlayer.unavailable",
        reason,
        new RecognitionScreenshotSessionState(sessionId, command.Revision),
        null,
        supervisorInstanceId(),
        null);
      return SessionRecognitionState(false, SessionStatusCode());
    }

    byte[] png;
    if (pinnedPng is null)
    {
      using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
      png = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
    }
    else
    {
      png = pinnedPng;
    }
    // 读取租约期间会话/修订变更则拒绝启动，不消费推理配额。
    ValidateScreenshotSession(command.SessionId, command.Revision);
    var input = new RecognitionInput(
      png,
      "image/png",
      "screenshot-text-layer.png",
      "screenshot-text");
    string serviceInstance = supervisorInstanceId() ?? string.Empty;
    long generation = Interlocked.Increment(ref screenshotTextGeneration);
    Guid sessionGuid = command.SessionId;
    long revision = command.Revision;
    screenshotTextLayer = new RecognitionTextLayerState(
      "textlayer.preparing",
      null,
      new RecognitionScreenshotSessionState(sessionId, revision),
      mode.Id,
      serviceInstance,
      null);
    return PublishStartThenTrack(
      SessionRecognitionState(false, SessionStatusCode()),
      () => CompleteScreenshotTextLayerAsync(
        generation,
        sessionGuid,
        revision,
        mode.Id,
        serviceInstance,
        input,
        cancellationToken));
  }

  /// <summary>Explicit pin intent joins the same session/revision OCR task.</summary>
  public async Task PreparePinnedTextLayerAsync(
    Guid sessionId, long revision, string imagePath)
  {
    ValidateScreenshotSession(sessionId, revision);
    if (screenshotTextLayer?.Binding is { } binding &&
      binding.SessionId == sessionId.ToString("N") &&
      binding.Revision == revision &&
      screenshotTextLayer.Status is "textlayer.preparing" or "textlayer.ready") return;
    byte[] png = await File.ReadAllBytesAsync(imagePath);
    RecognitionWorkbenchState? started = await PrepareScreenshotTextLayerAsync(
      new PrepareScreenshotTextLayerCommand(string.Empty, sessionId, revision),
      CancellationToken.None,
      png);
    if (started is not null) StateChanged?.Invoke(started);
  }

  private async Task CompleteScreenshotTextLayerAsync(
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
      bool deferredSelection = !await EnsureSelectionLoadedAsync(cancellationToken);
      if (generation != Volatile.Read(ref screenshotTextGeneration))
      {
        return;
      }
      RecognitionModeOption? mode = FindReadyLocalTextMode(out _);
      if (mode?.Id != modeId)
      {
        // 目录变化（模式不再 ready）：不静默换引擎冒充同一次准备。
        PublishTextLayerState("textlayer.unavailable", "textlayer.modeNotReady");
        return;
      }
      RecognitionViewModel viewModel = textLayerRecognition ??=
        textLayerRecognitionFactory();
      // 独立提交通道：不读写用户任务级 TaskEngine/SetRecognitionMode。
      viewModel.SetRecognitionMode(mode);
      await viewModel.RecognizeCapturedInputAsync(input, cancellationToken);
      if (deferredSelection)
      {
        try
        {
          await EnsureSelectionLoadedAsync(cancellationToken);
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
      if (generation != Volatile.Read(ref screenshotTextGeneration))
      {
        return;
      }
      if (screenshotSessionId != sessionId || screenshotSessionRevision != revision ||
        (supervisorInstanceId() ?? string.Empty) != serviceInstance)
      {
        viewModel.InvalidateResult();
        PublishTextLayerState("textlayer.expired", null);
        return;
      }
      if (viewModel.Result is null)
      {
        PublishTextLayerState(
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
        PublishTextLayerState("textlayer.failed", lineError ?? "textlayer.noLines");
        return;
      }
      // 展示资源就是识别输入的同一最终 PNG 字节。
      WorkbenchResourceReference image = await PublishBytesAsync(
        input.Data,
        "image/png",
        ".png",
        cancellationToken);
      if (generation != Volatile.Read(ref screenshotTextGeneration) ||
        screenshotSessionId != sessionId ||
        screenshotSessionRevision != revision ||
        (supervisorInstanceId() ?? string.Empty) != serviceInstance)
      {
        return;
      }
      screenshotTextLayer = new RecognitionTextLayerState(
        "textlayer.ready",
        null,
        new RecognitionScreenshotSessionState(sessionId.ToString("N"), revision),
        modeId,
        serviceInstance,
        image,
        lines);
      ScreenshotTextLayerChanged?.Invoke(screenshotTextLayer);
      StateChanged?.Invoke(SessionRecognitionState(false, SessionStatusCode()));
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref screenshotTextGeneration))
      {
        PublishTextLayerState("textlayer.cancelled", null);
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot text layer preparation failed", error);
      if (generation == Volatile.Read(ref screenshotTextGeneration))
      {
        PublishTextLayerState("textlayer.failed", null);
      }
    }
  }

  private void PublishTextLayerState(string status, string? reason)
  {
    RecognitionScreenshotSessionState? binding = screenshotSessionId is { } id
      ? new RecognitionScreenshotSessionState(id.ToString("N"), screenshotSessionRevision)
      : null;
    screenshotTextLayer = new RecognitionTextLayerState(
      status,
      reason,
      binding,
      screenshotTextLayer?.ModeId,
      screenshotTextLayer?.ServiceInstance,
      null);
    ScreenshotTextLayerChanged?.Invoke(screenshotTextLayer);
    StateChanged?.Invoke(SessionRecognitionState(false, SessionStatusCode()));
  }

  private RecognitionWorkbenchState CancelScreenshotTextLayer(
    CancelScreenshotTextLayerCommand command)
  {
    recognition ??= recognitionFactory();
    if (screenshotSessionId != command.SessionId)
    {
      throw new ScreenshotSessionStaleException();
    }
    if (screenshotTextLayer is { Status: "textlayer.preparing" })
    {
      Interlocked.Increment(ref screenshotTextGeneration);
      textLayerRecognition?.Cancel();
      PublishTextLayerState("textlayer.cancelled", null);
    }
    return SessionRecognitionState(false, SessionStatusCode());
  }

  private async Task<RecognitionWorkbenchState> CopyScreenshotSelectionAsync(
    CopyScreenshotSelectionCommand command,
    CancellationToken cancellationToken)
  {
    ValidateScreenshotSession(command.SessionId, command.Revision);
    // 复制必须来自当前就绪文字层：旧层/准备中一律 fail closed。
    if (screenshotTextLayer?.Binding is not { } binding ||
      binding.SessionId != command.SessionId.ToString("N") ||
      binding.Revision != command.Revision ||
      screenshotTextLayer.Status != "textlayer.ready" ||
      screenshotTextLayer.ServiceInstance != (supervisorInstanceId() ?? string.Empty))
    {
      throw new ScreenshotSessionStaleException();
    }
    await annotatedImagePlatform.CopyTextAsync(command.Text, cancellationToken);
    return CurrentRecognitionState();
  }

  /// <summary>
  /// 快速取字模式选择：仅目录内 ready 的本地轻量文字模式（base_runtime/
  /// operating_system）；用户已选的模式若恰好可用则优先。preparation_
  /// required/unavailable/advanced_component 一律不用于自动准备。
  /// </summary>
  private RecognitionModeOption? FindReadyLocalTextMode(out string? reason)
  {
    reason = null;
    RuntimeSelectionService? selection = settings?.RecognitionSelection?.Catalog;
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
      if (recognition?.TaskEngine == mode.Id)
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
      lines.Add(new RecognitionTextLayerLine(
        text, bbox[0], bbox[1], bbox[2], bbox[3], order));
      characterCount += text.Length;
    }
    if (lines.Count == 0) return null;
    bool ordered = lines.All(line => line.Order is not null);
    return ordered
      ? [.. lines.OrderBy(line => line.Order).ThenBy(line => line.Y1).ThenBy(line => line.X1)]
      : [.. lines.OrderBy(line => line.Y1).ThenBy(line => line.X1)];
  }

  private void ClearScreenshotSession()
  {
    screenshotSessionId = null;
    screenshotSessionRevision = 0;
    screenshotTextSelectionRequested = false;
    screenshotSessionInput = null;
    screenshotSessionResult = null;
  }

  private RecognitionWorkbenchState? StartRecognition(
    Func<RecognitionViewModel, Task> action,
    CancellationToken cancellationToken,
    bool screenCapture = false)
  {
    recognition ??= recognitionFactory();
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
      long generation = Interlocked.Increment(ref recognitionGeneration);
      // 新输入（文件/剪贴板/拖入/即时截图识别）取代截图会话：
      // 旧会话命令立即失效，避免旧图混入新输入。
      ClearScreenshotSession();
      InvalidateScreenshotTextLayer();
      return PublishStartThenTrack(
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

  private RecognitionWorkbenchState RecognitionState(
    bool isBusy,
    string statusCode,
    WorkbenchResourceReference? input = null,
    WorkbenchResourceReference? result = null)
  {
    SynchronizeRecognitionMode();
    return new(
      isBusy,
      statusCode,
      input,
      result,
      RecognitionEngines(),
      recognition?.TaskEngine,
      CurrentScreenshotSession());
  }

  /// <summary>
  /// Project catalog modes for the recognition page. Only an explicit task
  /// override is selected; an empty choice delegates to the Runtime default.
  /// </summary>
  private IReadOnlyList<RecognitionEngineChoice>? RecognitionEngines()
  {
    RuntimeSelectionService? selection = settings?.RecognitionSelection?.Catalog;
    if (selection is null || (!selection.SupportsEngineSelection && !selection.SupportsRecognitionModes))
    {
      return null;
    }
    recognition ??= recognitionFactory();
    if (selection.SupportsRecognitionModes)
    {
      string? task = recognition.TaskEngine;
      return [.. selection.RecognitionModes.Select(mode => new RecognitionEngineChoice(
        mode.Id, SettingsViewModel.DisplayName(mode.Id), task == mode.Id, task == mode.Id,
        TryProjectMineruConfig(selection, mode.Id, out _) ? mode.Availability : "unavailable",
        mode.Availability == "preparation_required" && mode.RequiredComponent is not null,
        mode.LifecycleKind,
        mode.SupportsPreload, mode.SupportsTtl, mode.SupportsPinning, mode.SupportsRelease))];
    }
    bool isOverride = recognition.TaskEngine is not null;
    return [.. selection.EngineOptions.Select(option => new RecognitionEngineChoice(
      OcrEngineWire.Format(option.Engine),
      SettingsViewModel.DisplayName(option.Engine),
      isOverride && OcrEngineWire.Format(option.Engine) == recognition.TaskEngine,
      isOverride &&
        OcrEngineWire.Format(option.Engine) == recognition.TaskEngine,
      option.Availability.ToString().ToLowerInvariant(),
      !option.IncludedInBase))];
  }

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
      using (screenCapture ? shellActions?.SuspendFloatingToolbarForCapture() : null)
      {
        bool deferredSelection = !await EnsureSelectionLoadedAsync(cancellationToken);
        SynchronizeRecognitionMode(requireUsable: true);
        RecognitionWorkbenchState state = await RunRecognitionAsync(action, cancellationToken);
        if (deferredSelection)
        {
          // The run started before the Supervisor attached; its submit already
          // waited for readiness, so the authoritative catalog can load now and
          // the completion state carries the engine list.
          try
          {
            await EnsureSelectionLoadedAsync(cancellationToken);
            state = state with { Engines = RecognitionEngines() };
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

        if (generation == Volatile.Read(ref recognitionGeneration))
        {
          StateChanged?.Invoke(state);
        }
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(new RecognitionWorkbenchState(
          false,
          "recognition.cancelled"));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Recognition workbench operation failed", error);
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.failed"));
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

  private async Task<RecognitionWorkbenchState> RunRecognitionAsync(
    Func<RecognitionViewModel, Task> action,
    CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    await action(recognition);
    WorkbenchResourceReference? input = null;
    if (recognition.CurrentInput is { } currentInput)
    {
      input = await PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        ExtensionForMediaType(currentInput.MediaType),
        cancellationToken);
    }
    WorkbenchResourceReference? result = string.IsNullOrEmpty(recognition.ResultText)
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(recognition.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    if (recognition.Result is not null)
    {
      resultActions = recognition.CreateResultActions(
        new WindowsResultActionPlatform(windowHandle));
    }
    return RecognitionState(
      recognition.IsBusy,
      RecognitionStatusCode(recognition),
      input,
      result);
  }

  private RecognitionWorkbenchState CancelRecognition()
  {
    recognition ??= recognitionFactory();
    Interlocked.Increment(ref recognitionGeneration);
    recognition.Cancel();
    if (screenshotSessionId is not null)
    {
      // 取消保留会话编辑基准；运行中结果与文字层准备丢弃后可重新触发。
      recognition.InvalidateResult();
      resultActions = null;
      screenshotSessionResult = null;
      InvalidateScreenshotTextLayer();
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

  private async Task<RecognitionWorkbenchState> ExportRecognitionAsync(
    ExportRecognitionResultCommand command,
    CancellationToken cancellationToken)
  {
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
    await actions.ExportAsync(format, cancellationToken);
    return await CurrentRecognitionStateAsync("recognition.exported", cancellationToken);
  }

  private async Task<RecognitionWorkbenchState> CurrentRecognitionStateAsync(
    string statusCode,
    CancellationToken cancellationToken)
  {
    if (screenshotSessionId is not null)
    {
      // 会话模式复用缓存资源：重发布新 URL 会重置编辑器历史。
      return SessionRecognitionState(false, statusCode);
    }
    RecognitionViewModel viewModel = recognition ??
      throw new InvalidOperationException("Recognition is unavailable.");
    WorkbenchResourceReference? input = viewModel.CurrentInput is { } currentInput
      ? await PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        ExtensionForMediaType(currentInput.MediaType),
        cancellationToken)
      : null;
    WorkbenchResourceReference? result = string.IsNullOrEmpty(viewModel.ResultText)
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(viewModel.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    return new RecognitionWorkbenchState(false, statusCode, input, result);
  }

  private async Task<BatchWorkbenchState> AddBatchFilesAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    await batch.PickFilesAsync(cancellationToken);
    return BatchState(batch);
  }

  private BatchWorkbenchState AddDroppedBatchFiles(
    AddDroppedBatchFilesCommand command)
  {
    batch ??= batchFactory();
    batch.AddFiles(command.Paths);
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState> ExportBatchAsync(
    ExportBatchCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    var picker = new FolderPicker
    {
      SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
    };
    picker.FileTypeFilter.Add("*");
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFolder? folder = await picker.PickSingleFolderAsync();
    if (folder is not null)
    {
      await batch.ExportAllAsync(folder.Path, command.Format, cancellationToken);
    }
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState?> StartBatchAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    await EnsureSelectionLoadedAsync(cancellationToken);
    SynchronizeBatchMode(requireUsable: true);
    long generation = Interlocked.Increment(ref batchGeneration);
    BatchWorkbenchState start = new(
      true,
      batch.Items.Count,
      batch.CompletedCount,
      batch.FailedCount);
    return PublishStartThenTrack(start,
      () => CompleteBatchAsync(generation, cancellationToken));
  }

  private async Task CompleteBatchAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      await batch!.StartAsync(cancellationToken);
      BatchWorkbenchState state = BatchState(batch);
      if (generation == Volatile.Read(ref batchGeneration))
      {
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref batchGeneration))
      {
        StateChanged?.Invoke(BatchState(batch!));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Batch workbench operation failed", error);
      if (generation == Volatile.Read(ref batchGeneration))
      {
        StateChanged?.Invoke(BatchState(batch!));
      }
    }
  }

  private BatchWorkbenchState CancelBatch()
  {
    batch ??= batchFactory();
    Interlocked.Increment(ref batchGeneration);
    batch.CancelAll();
    return BatchState(batch);
  }

  private BatchWorkbenchState ClearBatch()
  {
    batch ??= batchFactory();
    Interlocked.Increment(ref batchGeneration);
    batch.ResetTemporaryQueue();
    batchWindowStart = 0;
    return BatchState(batch);
  }

  private BatchWorkbenchState MoveBatchItem(MoveBatchItemCommand command)
  {
    batch ??= batchFactory();
    if (batch.IsRunning)
    {
      throw new InvalidOperationException("A running batch cannot be reordered.");
    }
    batch.Move(command.ItemId, command.Delta);
    return BatchState(batch);
  }

  private BatchWorkbenchState RemoveBatchItem(RemoveBatchItemCommand command)
  {
    batch ??= batchFactory();
    batch.Remove(command.ItemId);
    return BatchState(batch);
  }

  private BatchWorkbenchState SetBatchWindow(SetBatchWindowCommand command)
  {
    batch ??= batchFactory();
    batchWindowStart = ClampWindowStart(command.Start, batch.Items.Count, 40);
    return BatchState(batch);
  }

  private BatchWorkbenchState SetBatchTaskEngine(SetBatchTaskEngineCommand command)
  {
    batch ??= batchFactory();
    if (batch.IsRunning) throw new InvalidOperationException("A running batch cannot change recognition mode.");
    RuntimeSelectionService? selection = settings?.RecognitionSelection?.Catalog;
    if (command.Engine is not null)
    {
      if (selection?.SupportsRecognitionModes is not true)
        throw new RuntimeSelectionException(RuntimeSelectionErrorKind.CapabilityMissing,
          "The runtime does not provide recognition modes.");
      RecognitionModeOption mode = selection.SelectRecognitionMode(command.Engine);
      selection.MineruConfigFor(mode.Id);
    }
    batchTaskEngine = command.Engine;
    SynchronizeBatchMode();
    return BatchState(batch);
  }

  private void SynchronizeBatchMode(bool requireUsable = false)
  {
    RuntimeSelectionService? selection = settings?.RecognitionSelection?.Catalog;
    if (batch is null || selection?.SupportsRecognitionModes is not true)
    {
      batch?.SetRecognitionMode(null);
      return;
    }
    RecognitionModeOption? mode = batchTaskEngine is null ? null : requireUsable
      ? selection.SelectRecognitionMode(batchTaskEngine)
      : selection.FindRecognitionMode(batchTaskEngine);
    MineruConfig? config = requireUsable
      ? selection.MineruConfigFor(mode?.Id)
      : TryProjectMineruConfig(selection, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    batch.SetRecognitionMode(mode, config);
  }

  private async Task<PdfWorkbenchState> OpenPdfAsync(CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    await pdf.OpenAsync(cancellationToken);
    ResetPdfSelection(selectFirstPage: true);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> OpenDroppedPdfAsync(
    OpenDroppedPdfCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    await pdf.OpenPathAsync(command.Path, cancellationToken);
    ResetPdfSelection(selectFirstPage: true);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> RotatePdfAsync(
    RotatePdfCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0)
    {
      await viewModel.RotateAsync(pages, command.Degrees, cancellationToken);
      pdfThumbnails.Clear();
    }
    return await PdfStateAsync(viewModel, cancellationToken);
  }

  private PdfWorkbenchState ClosePdf()
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    pdf.CloseSession();
    ResetPdfSelection(selectFirstPage: false);
    return PdfState(pdf);
  }

  private async Task<PdfWorkbenchState> DeletePdfPagesAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0)
    {
      await viewModel.DeletePagesAsync(pages, cancellationToken);
      ResetPdfSelection(selectFirstPage: true);
    }
    return await PdfStateAsync(viewModel, cancellationToken);
  }

  private async Task<PdfWorkbenchState> OcrPdfPagesAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0)
    {
      await viewModel.StartOcrAsync(pages, overwrite: false, cancellationToken);
    }
    return PdfState(viewModel);
  }

  private PdfWorkbenchState? StartPdfOcr(CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    long generation = Interlocked.Increment(ref pdfGeneration);
    return PublishStartThenTrack(PdfState(pdf) with { IsBusy = true },
      () => CompletePdfOcrAsync(generation, cancellationToken));
  }

  private async Task CompletePdfOcrAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      PdfWorkbenchState state = await OcrPdfPagesAsync(cancellationToken);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        StateChanged?.Invoke((await PdfStateAsync(pdf!, cancellationToken)) with
        {
          StatusCode = state.StatusCode,
        });
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        StateChanged?.Invoke(PdfState(pdf!));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("PDF OCR workbench operation failed", error);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        StateChanged?.Invoke(PdfState(pdf!));
      }
    }
  }

  private async Task<PdfWorkbenchState> SavePdfAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    var picker = new FileSavePicker
    {
      SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
      SuggestedFileName = "vibeocr-output",
    };
    picker.FileTypeChoices.Add("PDF", [".pdf"]);
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFile? file = await picker.PickSaveFileAsync();
    if (file is not null)
    {
      await pdf.SaveAsync(file.Path, cancellationToken);
    }
    return PdfState(pdf);
  }

  private PdfWorkbenchState SelectPdfPages(SelectPdfPagesCommand command)
  {
    pdf ??= pdfFactory();
    if (command.Pages.Any(page => page < 0 || page >= pdf.PageCount))
    {
      throw new InvalidOperationException("The PDF page selection is stale.");
    }
    selectedPdfPages.Clear();
    foreach (int page in command.Pages)
    {
      selectedPdfPages.Add(page);
    }
    pdf.SelectedPage = selectedPdfPages.Order().FirstOrDefault(-1);
    return PdfState(pdf);
  }

  private async Task<PdfWorkbenchState> SetPdfWindowAsync(
    SetPdfWindowCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    pdfWindowStart = ClampWindowStart(command.Start, pdf.PageCount, 64);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private QrCodeWorkbenchState? StartQrCode(
    Func<QrCodeViewModel, Task> action,
    bool publishGeneratedImage,
    CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    long generation = Interlocked.Increment(ref qrCodeGeneration);
    QrCodeWorkbenchState start = QrCodeState(qrCode) with
    {
      IsBusy = true,
      StatusCode = "qrcode.running",
    };
    return PublishStartThenTrack(start,
      () => CompleteQrCodeAsync(
        action, publishGeneratedImage, generation, cancellationToken));
  }

  private async Task CompleteQrCodeAsync(
    Func<QrCodeViewModel, Task> action,
    bool publishGeneratedImage,
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      await action(qrCode!);
      if (generation != Volatile.Read(ref qrCodeGeneration))
      {
        return;
      }
      WorkbenchResourceReference? nextGeneratedResource = generatedQrResource;
      if (publishGeneratedImage)
      {
        if (!qrCode!.GenerateFailed && !string.IsNullOrWhiteSpace(qrCode.GeneratedImageBase64))
        {
          nextGeneratedResource = await PublishBytesAsync(
            Convert.FromBase64String(qrCode.GeneratedImageBase64),
            "image/png",
            ".png",
            cancellationToken);
        }
      }
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        generatedQrResource = nextGeneratedResource;
        QrCodeViewModel currentQrCode = qrCode!;
        QrCodeWorkbenchState state = QrCodeState(currentQrCode);
        StateChanged?.Invoke(!publishGeneratedImage && currentQrCode.DecodeUnavailable
          ? state with { StatusCode = "qrcode.decodeUnavailable" }
          : publishGeneratedImage && currentQrCode.GenerateInvalidInput
            ? state with { StatusCode = "qrcode.invalidInput" }
          : (publishGeneratedImage && currentQrCode.GenerateFailed) ||
            (!publishGeneratedImage && currentQrCode.DecodeFailed)
            ? state with { StatusCode = publishGeneratedImage ? "qrcode.generateFailed" : "qrcode.failed" }
            : state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        StateChanged?.Invoke(QrCodeState(qrCode!) with
        {
          IsBusy = false,
          StatusCode = "qrcode.cancelled",
        });
      }
    }
    catch (Exception error)
    {
      AppLog.Error("QR code workbench operation failed", error);
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        StateChanged?.Invoke(QrCodeState(qrCode!) with
        {
          IsBusy = false,
          StatusCode = publishGeneratedImage ? "qrcode.generateFailed" : "qrcode.failed",
        });
      }
    }
  }

  private QrCodeWorkbenchState CancelQrCode()
  {
    qrCode ??= qrCodeFactory();
    Interlocked.Increment(ref qrCodeGeneration);
    qrCode.Cancel();
    return QrCodeState(qrCode) with
    {
      IsBusy = false,
      StatusCode = "qrcode.cancelled",
    };
  }

  private QrCodeWorkbenchState ClearQrCode()
  {
    qrCode ??= qrCodeFactory();
    Interlocked.Increment(ref qrCodeGeneration);
    qrCode.Cancel();
    qrCode.Codes.Clear();
    qrCode.ReleaseGeneratedImage();
    generatedQrResource = null;
    return QrCodeState(qrCode);
  }

  private async Task<QrCodeWorkbenchState> SaveQrCodeAsync(
    CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    if (!string.IsNullOrEmpty(qrCode.GeneratedImageBase64))
    {
      var commands = new QrCodeSaveCommands(new QrCodeSavePlatform(windowHandle));
      await commands.SaveAsync(
        qrCode.GeneratedImageBase64,
        "qrcode.png",
        cancellationToken);
    }
    return QrCodeState(qrCode);
  }

  private async Task<QrCodeWorkbenchState> OpenQrCodeUrlAsync(
    OpenQrCodeUrlCommand command,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    qrCode ??= qrCodeFactory();
    bool wasDecoded = qrCode.Codes.Any(code =>
      code.IsUrl is true &&
      string.Equals(code.Data, command.Url, StringComparison.Ordinal));
    if (!wasDecoded || !TryParseAllowedQrUri(command.Url, out Uri? uri))
    {
      throw new InvalidOperationException("Only a decoded HTTP URL can be opened.");
    }
    if (!await Launcher.LaunchUriAsync(uri!))
    {
      throw new InvalidOperationException("Windows could not open the decoded URL.");
    }
    return QrCodeState(qrCode);
  }

  private async Task<AboutWorkbenchState> OpenProjectPageAsync(
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!await Launcher.LaunchUriAsync(shell.Value.ProjectUri))
    {
      throw new InvalidOperationException("Windows could not open the project page.");
    }
    return AboutState();
  }

  private async Task<SettingsWorkbenchState> RefreshRuntimeAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.LoadSnapshotAsync(cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetTheme(SetThemeCommand command)
  {
    theme = command.Theme;
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  private SettingsWorkbenchState SetStartup(SetStartupCommand command)
  {
    settings ??= CreateSettings();
    shell.Value.SetStartWithSystem(command.Enabled);
    return SettingsState(settings);
  }

  /// <summary>
  /// 动作键位设置：结果（成功/冲突/保存失败及当前实际注册状态）以设置页
  /// 的动作状态列表回显，命令本身不因键位被拒而报协议错误。
  /// </summary>
  private SettingsWorkbenchState SetActionHotkey(SetActionHotkeyCommand command)
  {
    settings ??= CreateSettings();
    ShellActions().TrySetHotkeyAction(command.ActionId, command.Hotkey, out _);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState ResetActionHotkey(ResetActionHotkeyCommand command)
  {
    settings ??= CreateSettings();
    ShellActions().ResetHotkeyAction(command.ActionId, out _);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetFloatingToolbarEnabled(
    SetFloatingToolbarEnabledCommand command)
  {
    settings ??= CreateSettings();
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    string? error = current.Enabled == command.Enabled
      ? null
      : ApplyToolbar(actions, current with { Enabled = command.Enabled });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState SetFloatingToolbarLayout(
    SetFloatingToolbarLayoutCommand command)
  {
    settings ??= CreateSettings();
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    string? error = current.Edge == command.Edge && current.AutoHide == command.AutoHide
      ? null
      : ApplyToolbar(actions, current with
      {
        Edge = command.Edge,
        AutoHide = command.AutoHide,
      });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState ShowFloatingToolbar()
  {
    settings ??= CreateSettings();
    ShellActions().TryShowFloatingToolbar(out string? error);
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState HideFloatingToolbar()
  {
    settings ??= CreateSettings();
    ShellActions().TryHideFloatingToolbar(out string? error);
    return SettingsState(settings, error);
  }

  /// <summary>
  /// 应用悬浮工具栏偏好：保存失败保旧并显式回显错误，不很报成功。
  /// </summary>
  private static string? ApplyToolbar(
    ShellActionDispatcher actions,
    FloatingToolbarSettings next) =>
    actions.TryApplyFloatingToolbar(next, out string? error) ? null : error;

  private ShellActionDispatcher ShellActions() =>
    shellActions ?? throw new InvalidOperationException(
      "Shell actions are unavailable in this mode.");

  private async Task<SettingsWorkbenchState> SetSourceAsync(
    SetDownloadSourceCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetSourceAsync(
      command.Kind,
      command.SourceId,
      cancellationToken);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetAccelerator(SetAcceleratorCommand command)
  {
    settings ??= CreateSettings();
    settings.SetPendingAccelerator(command.Accelerator);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetFeature(SetRuntimeFeatureCommand command)
  {
    settings ??= CreateSettings();
    settings.SetFeatureEnabled(command.FeatureId, command.Enabled);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> SetMineruConnectionAsync(
    SetMineruConnectionCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetMineruConnectionAsync(
      command.Mode,
      command.ApiUrl,
      command.ApiKey,
      cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> PrepareMineruConnectionAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.PrepareMineruRemoteAsync(cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task RefreshRecognitionCatalogStatesAsync(CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    SynchronizeRecognitionMode();
    RecognitionWorkbenchState current = await CurrentRecognitionStateAsync(
      "recognition.ready", cancellationToken);
    bool isBusy = recognition.IsBusy;
    StateChanged?.Invoke(current with
    {
      StatusCode = RecognitionStatusCode(recognition, isBusy),
      IsBusy = isBusy,
      Engines = RecognitionEngines(),
      TaskEngine = recognition.TaskEngine,
    });
    StateChanged?.Invoke(CurrentBatchState());
  }

  private BatchWorkbenchState CurrentBatchState() => batch is null
    ? new BatchWorkbenchState(false, 0, 0, 0, Engines: BatchEngines())
    : BatchState(batch);

  private IReadOnlyList<RecognitionEngineChoice>? BatchEngines() =>
    RecognitionEngines()?.Select(choice => choice with
    {
      Selected = choice.Engine == batchTaskEngine,
      IsTaskOverride = choice.Engine == batchTaskEngine,
    }).ToArray();

  private RecognitionWorkbenchState SetTaskEngine(SetTaskEngineCommand command)
  {
    recognition ??= recognitionFactory();
    settings ??= CreateSettings();
    RuntimeSelectionService? selection = settings.RecognitionSelection?.Catalog;
    if (string.IsNullOrWhiteSpace(command.Engine)) recognition.TaskEngine = null;
    else if (selection?.SupportsRecognitionModes is true)
    {
      RecognitionModeOption mode = selection.SelectRecognitionMode(command.Engine);
      selection.MineruConfigFor(mode.Id);
      recognition.TaskEngine = mode.Id;
    }
    else if (selection?.SupportsEngineSelection is true && OcrEngineWire.Parse(command.Engine) is OcrEngine engine)
    {
      selection.SelectEngine(engine);
      recognition.TaskEngine = OcrEngineWire.Format(engine);
    }
    else throw new RuntimeSelectionException(RuntimeSelectionErrorKind.CapabilityMissing,
      "The runtime does not provide a recognition selection catalog.");
    return screenshotSessionId is not null
      ? SessionRecognitionState(recognition.IsBusy, SessionStatusCode())
      : RecognitionState(false, RecognitionStatusCode(recognition));
  }

  private void SynchronizeRecognitionMode(bool requireUsable = false)
  {
    RecognitionSelectionSnapshot? snapshot = settings?.RecognitionSelection;
    if (recognition is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      recognition?.SetRecognitionMode(null);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    RecognitionModeOption? Resolve(string? id) => string.IsNullOrWhiteSpace(id)
      ? null
      : requireUsable
        ? selection.SelectRecognitionMode(id)
        : selection.FindRecognitionMode(id);
    RecognitionModeOption? mode = Resolve(recognition.TaskEngine);
    // mineru_document 任务随目录默认 tier 携带类型化 MinerU 4 配置。
    MineruConfig? config = requireUsable
      ? selection.MineruConfigFor(mode?.Id)
      : TryProjectMineruConfig(selection, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    recognition.SetRecognitionMode(mode, config);
  }

  private static bool TryProjectMineruConfig(
    RuntimeSelectionService selection,
    string? modeId,
    out MineruConfig? config)
  {
    try
    {
      config = selection.MineruConfigFor(modeId);
      return true;
    }
    catch (RuntimeSelectionException)
    {
      config = null;
      return false;
    }
  }

  /// <summary>
  /// Load the authoritative runtime selection catalog. While the Supervisor
  /// client is unattached the load is skipped: bootstrap must not block the
  /// window, and a recognition run must capture screenshot/clipboard input
  /// immediately — cold start has no cached catalog and therefore no engine
  /// override, so the default pipeline stays authoritative while the submit
  /// path waits for the Supervisor at the gateway. Returns whether the catalog
  /// was (re)loaded.
  /// </summary>
  private async Task<bool> EnsureSelectionLoadedAsync(CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (inferenceAttached?.Invoke() == false)
    {
      return false;
    }
    // Always cross SettingsViewModel's single-flight gate. A refresh may be
    // replacing an older snapshot even while Selection remains non-null.
    await settings.LoadSelectionAsync(cancellationToken);
    return true;
  }

  private SettingsViewModel CreateSettings()
  {
    SettingsViewModel model = settingsFactory();
    model.Maintenance.StateChanged += OnSettingsChanged;
    model.RuntimeStatus.PropertyChanged += OnSettingsPropertyChanged;
    model.PropertyChanged += OnSettingsPropertyChanged;
    return model;
  }

  private void OnSettingsChanged()
  {
    if (Volatile.Read(ref disposed) == 0 && settings is not null)
    {
      InvalidateScreenshotSessionRecognitionOnMaintenance();
      StateChanged?.Invoke(SettingsState(settings));
    }
  }

  /// <summary>
  /// 运行环境维护（含切换）事件使在途截图会话识别失效：迟到结果
  /// 不得覆盖当前会话。只接现有维护事件，不接管环境状态机。
  /// </summary>
  private void InvalidateScreenshotSessionRecognitionOnMaintenance()
  {
    if (settings?.Maintenance.State.IsRunning != true ||
      screenshotSessionId is null ||
      (recognition is not { IsBusy: true } &&
        screenshotTextLayer is null && screenshotSessionResult is null))
    {
      return;
    }
    Interlocked.Increment(ref recognitionGeneration);
    recognition?.Cancel();
    recognition?.InvalidateResult();
    resultActions = null;
    screenshotSessionResult = null;
    InvalidateScreenshotTextLayer();
    StateChanged?.Invoke(SessionRecognitionState(false, "recognition.expired"));
  }

  private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args) => OnSettingsChanged();

  private SettingsWorkbenchState? StartRuntimeInstall(string planId, CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    return PublishStartThenTrack(SettingsState(settings),
      () => CompleteRuntimeInstallAsync(settings, planId, cancellationToken));
  }

  private async Task CompleteRuntimeInstallAsync(SettingsViewModel model, string planId, CancellationToken cancellationToken)
  {
    await model.ConfirmInstallAsync(planId, cancellationToken);
    if (Volatile.Read(ref disposed) == 0)
    {
      StateChanged?.Invoke(SettingsState(model));
      await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    }
  }

  private async Task<SettingsWorkbenchState> InstallRuntimeAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.PreviewInstallAsync(cancellationToken);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState CancelRuntimeMaintenance()
  {
    settings ??= CreateSettings();
    settings.CancelMaintenance();
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> RetryRuntimeMaintenanceAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.RetryMaintenanceAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task<UpdateWorkbenchState> CheckUpdateAsync(
    CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref updateGeneration);
    await update.Value.CheckAsync(cancellationToken);
    return new UpdateWorkbenchState(
      update.Value.IsBusy,
      update.Value.StatusCode,
      update.Value.LatestVersion,
      update.Value.UpdateAvailable);
  }

  private async Task<UpdateWorkbenchState> DownloadUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.Value.DownloadAndApplyAsync(cancellationToken);
    return UpdateState();
  }

  private UpdateWorkbenchState? StartUpdateDownload(CancellationToken cancellationToken)
  {
    long generation = Interlocked.Increment(ref updateGeneration);
    return PublishStartThenTrack(UpdateState() with { IsBusy = true },
      () => CompleteUpdateDownloadAsync(generation, cancellationToken));
  }

  private async Task CompleteUpdateDownloadAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      UpdateWorkbenchState state = await DownloadUpdateAsync(cancellationToken);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref updateGeneration))
      {
        StateChanged?.Invoke(UpdateState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Update workbench operation failed", error);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        StateChanged?.Invoke(UpdateState());
      }
    }
  }

  private TState? PublishStartThenTrack<TState>(TState start, Func<Task> operation)
    where TState : WorkbenchState
  {
    StateChanged?.Invoke(start);
    Track(operation());
    return null;
  }

  private void Track(Task operation)
  {
    lock (backgroundOperations)
    {
      backgroundOperations.Add(operation);
    }
    _ = operation.ContinueWith(
      completed =>
      {
        lock (backgroundOperations)
        {
          backgroundOperations.Remove(completed);
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);
  }

  private UpdateWorkbenchState CancelUpdate()
  {
    Interlocked.Increment(ref updateGeneration);
    update.Value.Cancel();
    return UpdateState();
  }

  private async Task<UpdateWorkbenchState> CancelRuntimeForUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.Value.CancelRuntimeMaintenanceAndWaitAsync(cancellationToken);
    return UpdateState();
  }

  private async Task<DiagnosticsWorkbenchState> ExportDiagnosticsAsync(
    CancellationToken cancellationToken)
  {
    string destination = Path.Combine(
      Path.GetDirectoryName(resourceRoot) ?? resourceRoot,
      $"vibeocr-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    await diagnostics.ExportAsync(destination, cancellationToken);
    return DiagnosticsState();
  }

  private async Task<WorkbenchResourceReference> PublishBytesAsync(
    byte[] data,
    string mediaType,
    string extension,
    CancellationToken cancellationToken)
  {
    string relative = Path.Combine("session", $"{Guid.NewGuid():N}{extension}");
    string destination = Path.Combine(resourceRoot, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    await File.WriteAllBytesAsync(destination, data, cancellationToken);
    generatedFiles.Add(destination);
    WorkbenchResourceLease lease = resourceBroker.Lease(
      relative,
      mediaType,
      TimeSpan.FromHours(1));
    return new WorkbenchResourceReference(
      lease.Uri.AbsoluteUri,
      mediaType,
      data.LongLength);
  }

  private BatchWorkbenchState BatchState(BatchViewModel viewModel)
  {
    SynchronizeBatchMode();
    batchWindowStart = ClampWindowStart(batchWindowStart, viewModel.Items.Count, 40);
    return new BatchWorkbenchState(
    viewModel.IsRunning,
    viewModel.Items.Count,
    viewModel.CompletedCount,
    viewModel.FailedCount,
    viewModel.Items
      .Skip(batchWindowStart)
      .Take(40)
      .Select(item => new BatchWorkbenchItem(
        item.Id,
        Truncate(item.Name, 80),
        $"batch.item.{item.State.ToString().ToLowerInvariant()}",
        item.Result is null ? null : Truncate(item.Result.Text, 120)))
      .ToArray(),
    batchWindowStart,
    BatchEngines(),
    batchTaskEngine);
  }

  private PdfWorkbenchState PdfState(PdfViewModel viewModel)
  {
    pdfWindowStart = ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    return new PdfWorkbenchState(
      viewModel.IsBusy,
      viewModel.PageCount > 0 ? "pdf.open" : "pdf.empty",
      viewModel.PageCount,
      viewModel.SelectedPage,
      selectedPdfPages.Order().ToArray(),
      Enumerable.Range(
        pdfWindowStart,
        Math.Min(viewModel.PageCount - pdfWindowStart, 64))
      .Select(index => new PdfWorkbenchPage(
        index,
        PdfPageStatus(viewModel, index),
        pdfThumbnails.GetValueOrDefault(index)))
      .ToArray(),
      pdfWindowStart);
  }

  private async Task<PdfWorkbenchState> PdfStateAsync(
    PdfViewModel viewModel,
    CancellationToken cancellationToken)
  {
    pdfWindowStart = ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    int visiblePageEnd = Math.Min(viewModel.PageCount, pdfWindowStart + 64);
    for (int index = pdfWindowStart; index < visiblePageEnd; index++)
    {
      if (pdfThumbnails.ContainsKey(index)) continue;
      byte[]? thumbnail = await viewModel.RenderThumbnailAsync(index, cancellationToken);
      if (thumbnail is { Length: > 0 })
      {
        pdfThumbnails[index] = await PublishBytesAsync(
          thumbnail,
          "image/png",
          ".png",
          cancellationToken);
      }
    }
    return PdfState(viewModel);
  }

  private int[] SelectedPdfPages(PdfViewModel viewModel) => selectedPdfPages
    .Where(page => page >= 0 && page < viewModel.PageCount)
    .Order()
    .ToArray();

  private void ResetPdfSelection(bool selectFirstPage)
  {
    selectedPdfPages.Clear();
    pdfThumbnails.Clear();
    pdfWindowStart = 0;
    if (selectFirstPage && pdf is { PageCount: > 0 })
    {
      selectedPdfPages.Add(0);
      pdf.SelectedPage = 0;
    }
    else if (pdf is not null)
    {
      pdf.SelectedPage = -1;
    }
  }

  private static string PdfPageStatus(PdfViewModel viewModel, int index) =>
    index < viewModel.Pages.Count
      ? $"pdf.page.{viewModel.Pages[index].State.ToString().ToLowerInvariant()}"
      : "pdf.page.none";

  private QrCodeWorkbenchState QrCodeState(QrCodeViewModel viewModel)
  {
    QrCodeWorkbenchResult[] items = viewModel.Codes
      .Take(4)
      .Select(code =>
      {
        bool isComplete = code.Data.Length <= 2048;
        bool isOpenableUrl = isComplete && code.IsUrl is true &&
          TryParseAllowedQrUri(code.Data, out _);
        return new QrCodeWorkbenchResult(
          Truncate(code.Data, 2048),
          Truncate(code.Format, 40),
          isOpenableUrl);
      })
      .ToArray();
    return new QrCodeWorkbenchState(
      viewModel.IsBusy,
      items.Length > 0 ? "qrcode.decoded" : "qrcode.ready",
      [],
      generatedQrResource,
      items);
  }

  private SettingsWorkbenchState SettingsShellState() => new(
    theme,
    false,
    "settings.ready",
    "unknown",
    shell.Value.StartWithSystem,
    Sources: [],
    HotkeyActions: HotkeyActionStates(),
    FloatingToolbar: ToolbarProjection());

  private IReadOnlyList<SettingsHotkeyActionState> HotkeyActionStates() =>
    shellActions is null
      ? []
      : [.. shellActions.GetHotkeyActions().Select(status => new SettingsHotkeyActionState(
        status.ActionId,
        status.DisplayName,
        status.ConfiguredHotkey,
        status.RegisteredHotkey,
        status.Error,
        HotkeyActionCatalog.DefaultBinding(status.ActionId)))];

  private SettingsFloatingToolbarState? ToolbarProjection(string? error = null) =>
    shellActions is null
      ? null
      : new SettingsFloatingToolbarState(
        shellActions.ToolbarSettings.Enabled,
        FloatingToolbarSettings.EdgeName(shellActions.ToolbarSettings.Edge),
        shellActions.ToolbarSettings.AutoHide,
        FormatToolbarVisibility(shellActions.ToolbarVisibility),
        error ?? "");

  private static string FormatToolbarVisibility(FloatingToolbarVisibility visibility) =>
    visibility switch
    {
      FloatingToolbarVisibility.UserHidden => "userHidden",
      FloatingToolbarVisibility.EdgeHidden => "edgeHidden",
      FloatingToolbarVisibility.Visible => "visible",
      FloatingToolbarVisibility.Suspended => "suspended",
      _ => "disabled",
    };

  private SettingsWorkbenchState SettingsState(
    SettingsViewModel viewModel,
    string? toolbarError = null) => new(
    theme,
    viewModel.IsBusy,
    viewModel.RestartRequired ? "settings.restartRequired" : "settings.ready",
    viewModel.Backend,
    shell.Value.StartWithSystem,
    [.. viewModel.Sources.Select(source => new SettingsSourceOptionState(
      source.Kind,
      source.Id,
      source.DisplayName,
      source.Selected))],
    viewModel.PendingBackend,
    viewModel.CanSwitchBackend,
    [.. viewModel.Features.Select(feature => new SettingsFeatureOptionState(
      feature.FeatureId,
      feature.DisplayName,
      feature.Accelerator,
      feature.Selected))],
    HotkeyActions: HotkeyActionStates(),
    FloatingToolbar: ToolbarProjection(toolbarError),
    Maintenance: new SettingsMaintenanceState(
      viewModel.Maintenance.State.IsRunning,
      viewModel.Maintenance.State.StatusCode,
      viewModel.Maintenance.State.OperationId,
      viewModel.Maintenance.State.RequestedComponentIds,
      viewModel.Maintenance.State.EffectiveComponentIds,
      viewModel.Maintenance.State.RequestedSourceIds,
      viewModel.Maintenance.State.EffectiveSourceIds,
      viewModel.Maintenance.State.CanCancel,
      viewModel.Maintenance.State.CanRetry,
      viewModel.Maintenance.State.FailureReason,
      viewModel.Maintenance.State.FailureCode),
    StatusMessage: viewModel.Status,
    ServiceStatus: viewModel.RuntimeStatus.ServiceStatus,
    MaintenanceStatus: viewModel.RuntimeStatus.Status,
    MaintenancePhase: viewModel.RuntimeStatus.Phase,
    ProgressText: viewModel.RuntimeStatus.ProgressText,
    ProgressDetail: viewModel.RuntimeStatus.ProgressDetail,
    ProgressPercent: viewModel.RuntimeStatus.IsProgressIndeterminate ? null : viewModel.RuntimeStatus.ProgressValue,
    CanPreviewInstall: viewModel.Maintenance.SupportsInstallPlan,
    InstallPlan: viewModel.Maintenance.Plan,
    MineruConnection: viewModel.MineruConnection is { } connection
      ? new SettingsMineruConnectionState(
        connection.Supported,
        connection.Mode,
        connection.ApiUrl,
        connection.HasApiKey)
      : null);

  private UpdateWorkbenchState UpdateState() => new(
    update.Value.IsBusy,
    update.Value.StatusCode,
    update.Value.LatestVersion,
    update.Value.UpdateAvailable,
    update.Value.CanCancelRuntimeMaintenance);

  private void OnUpdatePropertyChanged(object? sender, PropertyChangedEventArgs args)
  {
    if (Volatile.Read(ref disposed) == 0 &&
      args.PropertyName == nameof(UpdateViewModel.CanCancelRuntimeMaintenance))
    {
      StateChanged?.Invoke(UpdateState());
    }
  }

  private AboutWorkbenchState AboutState() => new(
    shell.Value.AppVersion,
    shell.Value.License,
    shell.Value.ProjectUri.AbsoluteUri);

  private DiagnosticsWorkbenchState DiagnosticsState() => new(
    diagnostics.SupervisorStatus,
    diagnostics.ProtocolStatus,
    diagnostics.IsReady,
    diagnostics.Milestones
      .OrderBy(milestone => milestone.Name)
      .Select(milestone => milestone.Name)
      .ToArray());

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

  private static string ExtensionForMediaType(string mediaType) => mediaType switch
  {
    "image/jpeg" => ".jpg",
    "image/bmp" => ".bmp",
    "image/webp" => ".webp",
    "image/gif" => ".gif",
    _ => ".png",
  };

  private static string Truncate(string value, int maximumLength) =>
    value.Length <= maximumLength
      ? value
      : string.Concat(value.AsSpan(0, maximumLength - 1), "…");

  private static int ClampWindowStart(int requested, int count, int windowSize)
  {
    if (count <= 0) return 0;
    int maximumStart = ((count - 1) / windowSize) * windowSize;
    return Math.Clamp(requested, 0, maximumStart);
  }

  private static bool TryParseAllowedQrUri(string value, out Uri? uri)
  {
    uri = null;
    bool parsed = value.Length <= 2048 &&
      Uri.TryCreate(value, UriKind.Absolute, out uri);
    return parsed && uri is not null && uri.Scheme is "http" or "https" &&
      string.IsNullOrEmpty(uri.UserInfo);
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0)
    {
      return;
    }
    Interlocked.Increment(ref recognitionGeneration);
    Interlocked.Increment(ref batchGeneration);
    Interlocked.Increment(ref pdfGeneration);
    Interlocked.Increment(ref qrCodeGeneration);
    Interlocked.Increment(ref updateGeneration);
    recognition?.Cancel();
    textLayerRecognition?.Cancel();
    batch?.CancelAll();
    qrCode?.Cancel();
    pdf?.Cancel();
    if (update.IsValueCreated)
    {
      update.Value.PropertyChanged -= OnUpdatePropertyChanged;
      update.Value.Cancel();
      update.Value.Dispose();
    }
    if (settings is not null)
    {
      settings.Maintenance.StateChanged -= OnSettingsChanged;
      settings.RuntimeStatus.PropertyChanged -= OnSettingsPropertyChanged;
      settings.PropertyChanged -= OnSettingsPropertyChanged;
      settings.CancelMaintenance();
    }
    Task[] operations;
    lock (backgroundOperations)
    {
      operations = backgroundOperations.ToArray();
    }
    await Task.WhenAll(operations).ConfigureAwait(false);
    foreach (string file in generatedFiles)
    {
      try
      {
        File.Delete(file);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
    generatedFiles.Clear();
  }

  private sealed class AnnotatedImageOperationCancelledException : Exception
  {
  }

  private sealed class ScreenshotSessionStaleException : Exception
  {
  }

  /// <summary>另一个截图/选区会话仍在进行；命令层单飞 guard 拒绝重入。</summary>
  private sealed class CaptureInProgressException : Exception
  {
  }
}
