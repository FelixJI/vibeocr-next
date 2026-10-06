using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using VibeOCR.Platform.Inference;
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
      "recognition.scrollCapture",
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
      "qrcode.copyImage",
      "qrcode.openUrl",
      "about.openProject",
      "runtime.refresh",
      "runtime.environments",
      "settings.shell",
      "settings.hotkeys",
      "settings.floatingToolbar",
      "settings.selection",
      "runtime.maintenance",
      "recognition.engine",
      "recognition.options",
      "update.check",
      "update.install",
      "diagnostics.export",
      "diagnostics.copy",
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
  private readonly PortableLayout? optionsLayout;
  private readonly Dictionary<string, PaddleModeOptions> modeOptions = new(StringComparer.Ordinal);
  private readonly Func<nint> windowHandle;
  private readonly WorkbenchAnnotationStore annotationStore;
  private readonly IAnnotatedImagePlatform annotatedImagePlatform;
  private readonly Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?,
    IReadOnlyList<WorkbenchExclusionBox>>? pinScreenshot;
  private readonly Func<bool>? inferenceAttached;
  private readonly ShellActionDispatcher? shellActions;
  private readonly List<string> generatedFiles = [];
  private readonly Dictionary<string, string> resourceFiles = new(StringComparer.Ordinal);
  private readonly HashSet<Task> backgroundOperations = [];
  private readonly HashSet<int> selectedPdfPages = [];
  private readonly Dictionary<int, WorkbenchResourceReference> pdfThumbnails = [];
  private readonly Dictionary<string, string> structuredResourceFiles = new(StringComparer.Ordinal);
  private readonly Dictionary<Guid, (RecognizeResponse Result, WorkbenchResourceReference Reference)> batchStructured = [];
  private readonly Dictionary<int, (RecognizeResponse Result, WorkbenchResourceReference Reference)> pdfStructured = [];
  private readonly IStructuredClipboardPlatform structuredClipboard;
  private RecognitionViewModel? recognition;
  private RecognitionViewModel? textLayerRecognition;
  private ResultActions? resultActions;
  private Guid? screenshotSessionId;
  private long screenshotSessionRevision;
  private bool screenshotTextSelectionRequested;
  private bool screenshotSceneEditing;
  private WorkbenchResourceReference? screenshotSessionInput;
  private WorkbenchResourceReference? screenshotSessionResult;
  private WorkbenchResourceReference? screenshotSessionStructuredResult;
  /// <summary>冻结显示基准附带的归一化排除框：仅供前端初始重建屏蔽标记。</summary>
  private IReadOnlyList<WorkbenchExclusionBox> screenshotSessionExcludeBoxes = [];
  private RecognitionTextLayerState? screenshotTextLayer;
  private long screenshotTextGeneration;
  private int captureInFlight;
  private int environmentSwitching;
  private BatchViewModel? batch;
  private string? batchTaskEngine;
  private bool batchExportIncomplete;
  private string? pdfTaskEngine;
  private QrCodeViewModel? qrCode;
  private PdfViewModel? pdf;
  private SettingsViewModel? settings;
  private WorkbenchTheme theme = WorkbenchTheme.System;
  private WorkbenchResourceReference? generatedQrResource;
  private long recognitionGeneration;
  private long batchGeneration;
  private long pdfGeneration;
  private long qrCodeGeneration;
  private long publishedQrRevision;
  private string? qrPreviewPath;
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
    Action<WorkbenchAnnotationFile, Guid, long, RecognitionTextLayerState?,
      IReadOnlyList<WorkbenchExclusionBox>>? pinScreenshot = null,
    ShellActionDispatcher? shellActions = null,
    PortableLayout? optionsLayout = null,
    IStructuredClipboardPlatform? structuredClipboard = null)
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
    this.diagnostics.PropertyChanged += OnDiagnosticsPropertyChanged;
    this.resourceBroker = resourceBroker ??
      throw new ArgumentNullException(nameof(resourceBroker));
    this.resourceRoot = Path.GetFullPath(resourceRoot);
    this.windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
    this.annotationStore = annotationStore ??
      throw new ArgumentNullException(nameof(annotationStore));
    this.annotatedImagePlatform = annotatedImagePlatform ??
      new AnnotatedImagePlatform(this.windowHandle);
    this.structuredClipboard = structuredClipboard ??
      new WindowsStructuredClipboardPlatform();
    this.inferenceAttached = inferenceAttached;
    this.supervisorInstanceId = supervisorInstanceId ?? (() => null);
    pinnedServiceInstance = this.supervisorInstanceId() ?? string.Empty;
    this.textLayerRecognitionFactory = textLayerRecognitionFactory ?? recognitionFactory;
    this.pinScreenshot = pinScreenshot;
    this.shellActions = shellActions;
    if (shellActions is not null)
      shellActions.ToolbarStateChanged += OnToolbarStateChanged;
    this.optionsLayout = optionsLayout;
  }

  private readonly Func<string?> supervisorInstanceId;
  private readonly Func<RecognitionViewModel> textLayerRecognitionFactory;
  private string pinnedServiceInstance = string.Empty;
  private bool pinMaintenanceNotified;

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
  public event Action<Guid, long>? ScreenshotTextLayerInvalidated;
  public event Action<Guid, long>? ScreenshotSessionDetached;
  public event Action? PinnedTextEnvironmentChanged;
  public event Action<Guid, VibeOCR.Platform.Windows.PhysicalRectangle?>? ScreenshotSessionReady;
  internal event Action? ScreenshotCaptureStarting;
  internal event Action? ScreenshotCaptureFinished;
  /// <summary>scene 会话显式提交识别后的内部交接：宿主关闭 scene 编辑窗并导航主窗口；会话与任务保留。</summary>
  internal event Action<Guid>? ScreenshotSceneRecognitionHandoff;
  internal Guid? CurrentImageSessionId => screenshotSessionId;
  internal ScreenshotCaptureScene? PendingScreenshotCaptureScene => recognition?.CurrentInput?.CaptureScene;
  internal ScreenshotCaptureScene? TakeScreenshotCaptureScene(Guid sessionId) =>
    screenshotSessionId == sessionId ? recognition?.CurrentInput?.TakeCaptureScene() : null;

  /// <summary>
  /// Supervisor 连接/就绪/失败终态变更时同步广播诊断投影：宿主快照不
  /// 得滞留在 bootstrap 时的“正在连接”。仅监听代表健康变更的
  /// SupervisorStatus 单属性，避免一次 UpdateSupervisor 的四个通知各
  /// 广播一次。
  /// </summary>
  private void OnDiagnosticsPropertyChanged(object? sender, PropertyChangedEventArgs args)
  {
    if (Volatile.Read(ref disposed) == 0 &&
      args.PropertyName is nameof(VibeOCR.App.ViewModels.DiagnosticsViewModel.SupervisorStatus)
        or nameof(VibeOCR.App.ViewModels.DiagnosticsViewModel.DeviceEvidence))
    {
      StateChanged?.Invoke(DiagnosticsState());
    }
  }

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
          viewModel => viewModel.RecognizeFileAsync(
            cancellationToken, AwaitSelectionBeforeFreeze()),
          cancellationToken),
        RecognizeDroppedFileCommand dropped => StartRecognition(
          viewModel => viewModel.RecognizeDroppedFileAsync(
            dropped.Path,
            cancellationToken,
            AwaitSelectionBeforeFreeze()),
          cancellationToken),
        ReadRecognitionClipboardCommand => StartRecognition(
          viewModel => viewModel.RecognizeClipboardAsync(
            cancellationToken, AwaitSelectionBeforeFreeze()),
          cancellationToken),
        CaptureRecognitionScreenCommand => StartRecognition(
          viewModel => viewModel.RecognizeScreenshotAsync(
            cancellationToken, AwaitSelectionBeforeFreeze()),
          cancellationToken,
          screenCapture: true),
        SelectImageEditFileCommand => StartImageEdit(
          viewModel => viewModel.OpenImageForEditAsync(cancellationToken), cancellationToken),
        ReadImageEditClipboardCommand => StartImageEdit(
          viewModel => viewModel.PasteImageForEditAsync(cancellationToken), cancellationToken),
        OpenDroppedImageEditFileCommand drop => StartImageEdit(
          viewModel => viewModel.DropImageForEditAsync(drop.Path, cancellationToken), cancellationToken),
        CaptureScreenshotSessionCommand => StartScreenshotSession(false, cancellationToken),
        CaptureScreenshotTextSessionCommand => StartScreenshotSession(true, cancellationToken),
        CaptureScrollingScreenshotCommand => StartScreenshotSession(false, cancellationToken, scrolling: true),
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
        CopyStructuredResultCommand copyStructured => await CopyStructuredResultAsync(
          copyStructured,
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
        AddDroppedBatchFilesCommand dropped => await AddDroppedBatchFilesAsync(
          dropped,
          cancellationToken),
        ExportBatchCommand export => await ExportBatchAsync(export, cancellationToken),
        StartBatchCommand => await StartBatchAsync(cancellationToken),
        CancelBatchCommand => CancelBatch(),
        ClearBatchCommand => ClearBatch(),
        MoveBatchItemCommand move => await MoveBatchItemAsync(move, cancellationToken),
        RemoveBatchItemCommand remove => await RemoveBatchItemAsync(remove, cancellationToken),
        SetBatchWindowCommand window => await SetBatchWindowAsync(window, cancellationToken),
        SetBatchTaskEngineCommand taskEngine => SetBatchTaskEngine(taskEngine),
        SetPdfTaskEngineCommand pdfTaskEngine => SetPdfTaskEngine(pdfTaskEngine),
        OpenPdfCommand => await OpenPdfAsync(cancellationToken),
        OpenDroppedPdfCommand dropped => await OpenDroppedPdfAsync(
          dropped,
          cancellationToken),
        RotatePdfCommand rotate => await RotatePdfAsync(rotate, cancellationToken),
        ClosePdfCommand => ClosePdf(),
        DeletePdfPagesCommand => await DeletePdfPagesAsync(cancellationToken),
        OcrPdfPagesCommand => await StartPdfOcr(cancellationToken),
        SavePdfCommand => await SavePdfAsync(cancellationToken),
        SelectPdfPagesCommand select => SelectPdfPages(select),
        SetPdfWindowCommand window => await SetPdfWindowAsync(
          window,
          cancellationToken),
        GenerateQrCodeCommand generate => StartQrCode(
          viewModel =>
          {
            viewModel.GenerateText = generate.Text;
            viewModel.GenerateFormat = generate.Format;
            viewModel.CaptionMode = generate.CaptionMode switch { "payload" => QrCodeCaptionMode.Payload, "custom" => QrCodeCaptionMode.Custom, _ => QrCodeCaptionMode.Off };
            viewModel.CaptionText = generate.CaptionText;
            return viewModel.GenerateAsync(cancellationToken);
          },
          publishGeneratedImage: true,
          cancellationToken),
        CopyQrCodeImageCommand => await CopyQrCodeImageAsync(cancellationToken),
        DecodeCurrentQrCodeCommand current => StartQrCode(
          viewModel => viewModel.DecodeCurrentPreviewAsync(current.Force, cancellationToken),
          publishGeneratedImage: false,
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
        CancelQrCodeCommand => await CancelQrCodeAsync(cancellationToken),
        ClearQrCodeCommand => ClearQrCode(),
        SaveQrCodeCommand => await SaveQrCodeAsync(cancellationToken),
        OpenQrCodeUrlCommand openUrl => await OpenQrCodeUrlAsync(
          openUrl,
          cancellationToken),
        OpenProjectPageCommand => await OpenProjectPageAsync(cancellationToken),
        RefreshRuntimeCommand => await RefreshRuntimeAsync(cancellationToken),
        CreateEnvironmentCommand create => await RunEnvironmentAsync(
          environment => environment.CreateAsync(create.Name, cancellationToken), cancellationToken),
        SetEnvironmentSourcesCommand setEnvironmentSources => await RunEnvironmentAsync(
          environment => setEnvironmentSources.IndependentModelSources
            ? environment.SetSourcesAsync(setEnvironmentSources.EnvironmentId,
                setEnvironmentSources.PackageSourceId, setEnvironmentSources.PaddleocrModelSourceId,
                setEnvironmentSources.MineruModelSourceId, cancellationToken)
            : environment.SetSourcesAsync(setEnvironmentSources.EnvironmentId,
                setEnvironmentSources.PackageSourceId, setEnvironmentSources.ModelSourceId,
                cancellationToken), cancellationToken),
        PreviewEnvironmentInstallCommand preview => await RunEnvironmentAsync(
          environment => environment.PreviewAsync(preview.EnvironmentId, preview.Recipe,
            preview.SourceId, cancellationToken), cancellationToken),
        ConfirmEnvironmentInstallCommand confirmEnvironment => StartEnvironmentOperation(
          environment => environment.InstallAsync(confirmEnvironment.PlanId,
            confirmEnvironment.SourceId, cancellationToken)),
        CancelEnvironmentInstallCommand => CancelEnvironmentInstall(),
        InvalidateEnvironmentPlanCommand => InvalidateEnvironmentPlan(),
        SwitchEnvironmentCommand switchEnvironment => StartEnvironmentOperation(
          environment => environment.SwitchAsync(switchEnvironment.EnvironmentId, cancellationToken),
          refreshCatalog: true),
        DeleteEnvironmentCommand deleteEnvironment => await RunEnvironmentAsync(
          environment => environment.DeleteAsync(deleteEnvironment.EnvironmentId, cancellationToken), cancellationToken),
        RepairEmptyEnvironmentCommand repair => await RunEnvironmentAsync(
          environment => environment.RepairEmptyAsync(repair.EnvironmentId, cancellationToken), cancellationToken),
        FindCompatibleEnvironmentCommand findCompatible => await RunEnvironmentAsync(
          environment => environment.FindCompatibleAsync(findCompatible.Recipe, cancellationToken), cancellationToken),
        SetThemeCommand setTheme => SetTheme(setTheme),
        SetStartupCommand startup => SetStartup(startup),
        BeginHotkeyRecordingCommand recording => BeginHotkeyRecording(recording),
        EndHotkeyRecordingCommand recording => EndHotkeyRecording(recording),
        SetActionHotkeyCommand setActionHotkey => SetActionHotkey(setActionHotkey),
        ResetActionHotkeyCommand resetActionHotkey => ResetActionHotkey(resetActionHotkey),
        SetFloatingToolbarEnabledCommand toolbarEnabled => SetFloatingToolbarEnabled(
          toolbarEnabled),
        SetFloatingToolbarLayoutCommand toolbarLayout => SetFloatingToolbarLayout(
          toolbarLayout),
        SetFloatingToolbarPreferencesCommand toolbarPreferences => SetFloatingToolbarPreferences(
          toolbarPreferences),
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
        SetDefaultRecognitionModeCommand defaultMode => await SetDefaultRecognitionModeAsync(
          defaultMode,
          cancellationToken),
        PrepareMineruConnectionCommand => await PrepareMineruConnectionAsync(
          cancellationToken),
        SetTaskEngineCommand taskEngine => SetTaskEngine(taskEngine),
        SetRecognitionOptionsCommand options => SetRecognitionOptions(options),
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
        CopyDiagnosticsCommand => await CopyDiagnosticsAsync(cancellationToken),
        _ => throw new InvalidOperationException("Unsupported desktop workbench command."),
      };
      // A null state was already published before its background operation started.
      return new WorkbenchCommandOutcome(state is null ? [] : [state], null);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (RecognitionModeUnavailableException error)
    {
      // 严格模式合同：显式选择的模式在当前环境不可用时，提交可恢复地失败
      // 并指向环境；不静默回退通用文字识别。
      AppLog.Warn($"Recognition mode override refused: {error.Message}");
      return new WorkbenchCommandOutcome([], new WorkbenchProblem(
        "recognition_mode_unavailable", WorkbenchProblemCategory.Unavailable, true,
        "workbench.error.recognitionModeUnavailable"));
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Recognition mode selection refused: {error.Message}");
      return new WorkbenchCommandOutcome([], new WorkbenchProblem(
        "recognition_mode_unavailable", WorkbenchProblemCategory.Unavailable, true,
        "workbench.error.recognitionModeUnavailable"));
    }
    catch (ArgumentException) when (command is SetRecognitionOptionsCommand)
    {
      return new WorkbenchCommandOutcome([], new WorkbenchProblem(
        "recognition_options_invalid", WorkbenchProblemCategory.InvalidCommand, false,
        "workbench.error.invalidCommand"));
    }    catch (AnnotatedImageOperationCancelledException)
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
    catch (ClipboardBusyException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "clipboard_busy",
          WorkbenchProblemCategory.Unavailable,
          true,
          "workbench.error.clipboardBusy"));
    }
    catch (Exception error) when (
      error is IOException or UnauthorizedAccessException or InvalidOperationException or
        WorkbenchAnnotationAccessException or WorkbenchResourceAccessException or RuntimeInstallerException)
    {
      if (command is SetFloatingToolbarPreferencesCommand)
        AppLog.Error("Floating toolbar preferences failed", error);
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

  /// <summary>
  /// 严格模式合同的终态：显式选择的模式不可用时保留引擎目录、用户选择与
  /// 截图会话基准，状态指向环境修复；不发布虚假成功或丢失选择。
  /// </summary>
  private RecognitionWorkbenchState ModeUnavailableRecognitionState() => new(
    false,
    "recognition.modeUnavailable",
    screenshotSessionId is null ? null : screenshotSessionInput,
    screenshotSessionId is null ? null : screenshotSessionResult,
    RecognitionEngines(),
    recognition?.TaskEngine,
    CurrentScreenshotSession());

  /// <summary>当前截图会话的 wire 投影；无会话时为 null。</summary>
  private RecognitionScreenshotSessionState? CurrentScreenshotSession() =>
    screenshotSessionId is { } id
      ? new RecognitionScreenshotSessionState(id.ToString("N"), screenshotSessionRevision,
        screenshotTextSelectionRequested, screenshotSceneEditing,
        screenshotSessionExcludeBoxes)
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
      TextLayer: screenshotTextLayer,
      StructuredResult: screenshotSessionResult is null ? null : screenshotSessionStructuredResult);

  private void ValidateScreenshotSession(Guid sessionId, long revision)
  {
    if (screenshotSessionId != sessionId || screenshotSessionRevision != revision)
    {
      throw new ScreenshotSessionStaleException();
    }
  }

  /// <summary>取消在途文字层准备并丢弃旧层；编辑/换图/维护/关闭后调用。</summary>
  private void InvalidateScreenshotTextLayer(long? editedRevision = null)
  {
    Interlocked.Increment(ref screenshotTextGeneration);
    textLayerRecognition?.Cancel();
    if (screenshotTextLayer?.Image is { } image) ReleaseResource(image);
    screenshotTextLayer = null;
    if (editedRevision is { } revision && screenshotSessionId is { } id)
      ScreenshotTextLayerInvalidated?.Invoke(id, revision);
  }

  private WorkbenchAnnotationFile TakeScreenshotAnnotation(string resourceUri) =>
    annotationStore.Take(new Uri(resourceUri));

  private RecognitionWorkbenchState? StartImageEdit(
    Func<RecognitionViewModel, Task> loadImage, CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    long generation = Interlocked.Increment(ref recognitionGeneration);
    return PublishStartThenTrack(
      SessionRecognitionState(true, "recognition.running"),
      () => CompleteImageEditAsync(loadImage, generation, cancellationToken));
  }

  private async Task CompleteImageEditAsync(
    Func<RecognitionViewModel, Task> loadImage, long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      await loadImage(recognition!);
      if (generation != Volatile.Read(ref recognitionGeneration)) return;
      if (recognition!.TerminalState is JobState.Cancelled or JobState.Failed)
      {
        StateChanged?.Invoke(recognition.TerminalState == JobState.Failed
          ? SessionRecognitionState(false, "recognition.inputFailed")
          : CurrentRecognitionState());
        return;
      }
      if (recognition.CurrentInput is not { } image) return;
      WorkbenchResourceReference input = await PublishBytesAsync(image.Data, image.MediaType,
        ExtensionForMediaType(image.MediaType), cancellationToken);
      if (generation != Volatile.Read(ref recognitionGeneration))
      {
        ReleaseResource(input);
        return;
      }
      ClearScreenshotSession();
      InvalidateScreenshotTextLayer();
      resultActions = null;
      screenshotSessionId = Guid.NewGuid();
      screenshotSessionRevision = 0;
      screenshotSessionInput = input;
      StateChanged?.Invoke(SessionRecognitionState(false, "recognition.session"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
      OperationCanceledException)
    {
      AppLog.Error("Image edit input failed", error);
      if (generation == Volatile.Read(ref recognitionGeneration))
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.inputFailed"));
    }
  }

  private RecognitionWorkbenchState? StartScreenshotSession(
    bool textSelectionRequested, CancellationToken cancellationToken, bool scrolling = false)
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
      // 框选取消或失败时保留旧图、标注、结果与修订；成功捕获才替换。
      ScreenshotCaptureStarting?.Invoke();
      return PublishStartThenTrack(
        SessionRecognitionState(true, "recognition.running"),
        () => CompleteScreenshotSessionAsync(generation, textSelectionRequested, scrolling,
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
    bool scrolling,
    CancellationToken cancellationToken)
  {
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
        if (scrolling)
          await recognition!.CaptureScrollingScreenshotSessionAsync(cancellationToken);
        else
          await recognition!.CaptureScreenshotSessionAsync(cancellationToken);
        if (generation != Volatile.Read(ref recognitionGeneration))
        {
          return;
        }
        if (recognition.TerminalState is JobState.Cancelled or JobState.Failed)
        {
          StateChanged?.Invoke(SessionRecognitionState(false,
            recognition.TerminalState is JobState.Failed ? "recognition.failed" : "recognition.cancelled"));
          return;
        }
        if (recognition.CurrentInput is { } captured)
        {
          capturedInput = captured;
          WorkbenchResourceReference input = await PublishBytesAsync(
            captured.Data,
            captured.MediaType,
            ExtensionForMediaType(captured.MediaType),
            cancellationToken);
          // PublishBytesAsync 期间取消/新截图会推进 generation：
          // 旧捕获完成不得复活已被取代的会话。
          if (generation != Volatile.Read(ref recognitionGeneration))
          {
            ReleaseResource(input);
            return;
          }

          ClearScreenshotSession();
          InvalidateScreenshotTextLayer();
          resultActions = null;
          screenshotSessionId = Guid.NewGuid();
          screenshotSessionRevision = 0;
          screenshotTextSelectionRequested = textSelectionRequested;
          screenshotSceneEditing = true;
          screenshotSessionInput = input;
          screenshotSessionResult = null;
          screenshotSessionExcludeBoxes = [];
          publishedSession = true;
          StateChanged?.Invoke(SessionRecognitionState(false, "recognition.session"));
        }
        else
        {
          // 用户在选区界面取消：不残留会话与遮罩状态；此前的文件输入保持可见。
          StateChanged?.Invoke(screenshotSessionId is not null
            ? SessionRecognitionState(false, "recognition.cancelled")
            : await CurrentRecognitionStateAsync("recognition.cancelled", cancellationToken));
        }
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
      AppLog.Error("Screenshot session capture failed", error);
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.failed"));
      }
    }
    finally
    {
      Interlocked.Exchange(ref captureInFlight, 0);
      try
      {
        if (publishedSession && generation == Volatile.Read(ref recognitionGeneration) &&
            screenshotSessionId is { } id)
          ScreenshotSessionReady?.Invoke(id, recognition?.CurrentInput?.CaptureBounds);
      }
      finally
      {
        capturedInput?.DisposeCaptureScene();
        ScreenshotCaptureFinished?.Invoke();
      }
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
    long previousRevision = screenshotSessionRevision;
    screenshotSessionRevision = command.Revision;
    // 任何内容修订都使旧识别结果与文字层失效：迟到响应不得覆盖当前内容。
    Interlocked.Increment(ref recognitionGeneration);
    recognition.Cancel();
    recognition.InvalidateResult();
    InvalidateScreenshotTextLayer(previousRevision);
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
    await annotatedImagePlatform.CopyImageAsync(annotation.Path, cancellationToken);
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
    if (!await annotatedImagePlatform.SaveImageAsync(annotation.Path, cancellationToken))
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
      // 与原位行同一正面积相交策略：跨边界的行不进入贴图可选层；
      // 被完全屏蔽的文字因输入像素已覆盖本就不在层内。
      RecognitionTextLayerState? layer =
        screenshotTextLayer is { Status: "textlayer.ready" } ready &&
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
    return CurrentRecognitionState();
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

  private async Task<RecognitionWorkbenchState?> StartScreenshotRecognitionAsync(
    RecognizeScreenshotImageCommand command,
    CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    ValidateScreenshotSession(command.SessionId, command.Revision);
    using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
    // 上传的是未烘焙遮罩的普通最终像素：同一份字节兼当新的显示基准，
    // 也是无遮罩时的 OCR 输入；不重复全图上传。
    byte[] normalBytes = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
    // 读取租约期间会话/修订变更则拒绝启动，不消费推理配额。
    ValidateScreenshotSession(command.SessionId, command.Revision);
    // 冻结普通显示基准：发布同字节新资源（遮罩绝不烧入）。
    WorkbenchResourceReference frozen = await PublishBytesAsync(
      normalBytes, annotation.MediaType,
      ExtensionForMediaType(annotation.MediaType), cancellationToken);
    bool adopted = false;
    try
    {
      if (screenshotSessionId != command.SessionId ||
        screenshotSessionRevision != command.Revision)
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
      if (screenshotSessionId != command.SessionId ||
        screenshotSessionRevision != command.Revision)
      {
        throw new ScreenshotSessionStaleException();
      }
      var input = new RecognitionInput(
        ocrBytes,
        ocrMediaType,
        "screenshot-session-final" + ExtensionForMediaType(ocrMediaType),
        "screenshot-session");
      long generation = Interlocked.Increment(ref recognitionGeneration);
      Guid sessionId = command.SessionId;
      long revision = command.Revision;
      resultActions = null;
      screenshotSessionResult = null;
      // 旧 input 仅在 frozen 成功接管后释放。
      if (screenshotSessionInput is { } previousInput) ReleaseResource(previousInput);
      screenshotSessionInput = frozen;
      adopted = true;
      screenshotSessionExcludeBoxes = command.ExcludeBoxes;
      if (screenshotSceneEditing)
      {
        // 显式识别交接：基准已冻结后再关 scene；会话与已提交任务保留，
        // 结果由主窗口识别承载面展示；失败/取消时编辑基准仍可用。
        screenshotSceneEditing = false;
        ScreenshotSceneRecognitionHandoff?.Invoke(sessionId);
      }
      return PublishStartThenTrack(
        SessionRecognitionState(true, "recognition.running"),
        () => CompleteScreenshotRecognitionAsync(
          generation,
          sessionId,
          revision,
          input,
          cancellationToken));
    }
    finally
    {
      // 未能接管（陈旧/遮罩失败/取消/发布异常）时释放 frozen，不泄漏资源。
      if (!adopted) ReleaseResource(frozen);
    }
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
      // 输入（会话导出图）已就绪：未 attach 时等待权威目录；环境切换窗口
      // 取消本次提交，不以空快照提交（Rapid 回退）。
      bool selectionUnavailable =
        !await EnsureSelectionLoadedForSubmitAsync(cancellationToken);
      // 目录加载期间编辑/关闭/换图会推进 generation：
      // 提交前复查，不把旧图发送给 Runtime。
      if (generation != Volatile.Read(ref recognitionGeneration))
      {
        return;
      }
      if (selectionUnavailable)
      {
        recognition?.InvalidateResult();
        resultActions = null;
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.cancelled"));
        return;
      }
      SynchronizeRecognitionMode(requireUsable: true);
      await recognition!.RecognizeCapturedInputAsync(input, cancellationToken);
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
        // 完成结果即使文本为空也发布真实（可能为空的）文本资源：会话终态
        // 可见性（SessionStatusCode 与 StructuredResult 门控）绑定
        // screenshotSessionResult，空文本不得伪装成仍在会话编辑中（#110）。
        WorkbenchResourceReference? result = recognition.Result is null
          ? null
          : await PublishBytesAsync(
            Encoding.UTF8.GetBytes(recognition.ResultText),
            "text/plain; charset=utf-8",
            ".txt",
            cancellationToken);
        WorkbenchResourceReference? structured = await PublishStructuredResultAsync(
          recognition, cancellationToken);
        // 结果资源发布期间编辑/换图/维护会推进 generation：丢弃迟到结果。
        if (generation != Volatile.Read(ref recognitionGeneration) ||
          screenshotSessionId != sessionId ||
          screenshotSessionRevision != revision)
        {
          return;
        }
        screenshotSessionResult = result;
        screenshotSessionStructuredResult = structured;
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
    catch (RecognitionModeUnavailableException error)
    {
      AppLog.Warn($"Screenshot recognition mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Screenshot recognition selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
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
    recognition.ReleaseInput();
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
    byte[]? pinnedPng = null, string pinnedMediaType = "image/png")
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
    string mediaType = pinnedMediaType;
    if (pinnedPng is null)
    {
      using WorkbenchAnnotationFile annotation = TakeScreenshotAnnotation(command.ResourceUri);
      png = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
      mediaType = annotation.MediaType;
    }
    else
    {
      png = pinnedPng;
    }
    // 读取租约期间会话/修订变更则拒绝启动，不消费推理配额。
    ValidateScreenshotSession(command.SessionId, command.Revision);
    var input = new RecognitionInput(
      png,
      mediaType,
      "screenshot-text-layer" + ExtensionForMediaType(mediaType),
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

  /// <summary>
  /// An active pin joins the editor's task. Once its session is gone, explicit
  /// pin text recognition uses only that pin's frozen PNG and never publishes
  /// a result into the current editor session.
  /// </summary>
  public async Task<RecognitionTextLayerState?> PreparePinnedTextLayerAsync(
    Guid sessionId, long revision, string imagePath,
    CancellationToken cancellationToken = default)
  {
    if (screenshotSessionId == sessionId && screenshotSessionRevision == revision)
    {
      if (screenshotTextLayer?.Binding is { } binding &&
        binding.SessionId == sessionId.ToString("N") &&
        binding.Revision == revision &&
        screenshotTextLayer.Status is "textlayer.preparing" or "textlayer.ready") return null;
      byte[] activePng = await File.ReadAllBytesAsync(imagePath, cancellationToken);
      RecognitionWorkbenchState? started = await PrepareScreenshotTextLayerAsync(
        new PrepareScreenshotTextLayerCommand(string.Empty, sessionId, revision),
        CancellationToken.None,
        activePng, Path.GetExtension(imagePath) == ".jpg" ? "image/jpeg" : "image/png");
      if (started is not null) StateChanged?.Invoke(started);
      return null;
    }

    // No session-dependent generation or result cache is read below. The pin
    // retains its own final PNG lease even after close/new capture/redaction.
    if (inferenceAttached?.Invoke() == false)
      throw new PinnedTextPreparationException("本地识别服务尚未就绪。");
    await EnsureSelectionLoadedAsync(cancellationToken);
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
        PublishTextLayerState(lineError is null ? "textlayer.empty" : "textlayer.failed",
          lineError ?? "textlayer.noLines");
        return;
      }
      // 展示资源就是识别输入的同一最终 PNG 字节。
      WorkbenchResourceReference image = await PublishBytesAsync(
        input.Data,
        input.MediaType,
        ExtensionForMediaType(input.MediaType),
        cancellationToken);
      if (generation != Volatile.Read(ref screenshotTextGeneration) ||
        screenshotSessionId != sessionId ||
        screenshotSessionRevision != revision ||
        (supervisorInstanceId() ?? string.Empty) != serviceInstance)
      {
        ReleaseResource(image);
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
    if (screenshotTextLayer?.Image is { } image) ReleaseResource(image);
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
    if (screenshotSessionId is { } id)
      ScreenshotSessionDetached?.Invoke(id, screenshotSessionRevision);
    if (screenshotSessionInput is { } input)
      ReleaseResource(input);
    if (screenshotSessionResult is { } result) ReleaseResource(result);
    if (screenshotSessionStructuredResult is { } structured) ReleaseResource(structured);
    screenshotSessionStructuredResult = null;
    screenshotSessionId = null;
    screenshotSessionRevision = 0;
    screenshotTextSelectionRequested = false;
    screenshotSceneEditing = false;
    screenshotSessionInput = null;
    screenshotSessionResult = null;
    screenshotSessionExcludeBoxes = [];
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
    WorkbenchResourceReference? result = null,
    WorkbenchResourceReference? structured = null)
  {
    SynchronizeRecognitionMode();
    return new(
      isBusy,
      statusCode,
      input,
      result,
      RecognitionEngines(),
      recognition?.TaskEngine,
      CurrentScreenshotSession(),
      StructuredResult: structured);
  }

  /// <summary>Runtime 回显的已提交默认识别模式 id；仅 Bound 状态非空。</summary>
  private string? RuntimeDefaultModeId =>
    settings?.RecognitionSelection?.DefaultRecognitionModeId;

  /// <summary>
  /// 单次/批量/PDF 共用的继承解析：显式本次 override 优先；能力已声明时
  /// 绑定回显默认，Invalid/Unread 在提交协商时明确拒绝并指向设置修复，
  /// 不猜 rapid_text；仅能力缺失走旧语义。
  /// </summary>
  internal static string? EffectiveModeIdOrDefault(
    RecognitionSelectionSnapshot? snapshot,
    string? taskEngine,
    bool requireUsable)
  {
    if (taskEngine is not null || snapshot is null)
    {
      return taskEngine;
    }
    switch (snapshot.DefaultMode)
    {
      case RuntimeDefaultModeBinding.Bound:
        return snapshot.DefaultRecognitionModeId;
      case RuntimeDefaultModeBinding.NotApplicable:
        return null;
      default:
        if (requireUsable)
        {
          throw new RecognitionModeUnavailableException(
            "当前默认识别模式无效或尚未读取，已拒绝按通用文字识别执行；请在设置 · 识别默认模式中重新选择并保存。");
        }
        return null;
    }
  }

  /// <summary>
  /// Project catalog modes for the recognition page. Only an explicit task
  /// override is IsTaskOverride; the committed runtime default is Selected
  /// while no override is present, and an empty choice still delegates to it.
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
      string? defaultId = RuntimeDefaultModeId;
      return [.. selection.RecognitionModes.Select(mode => new RecognitionEngineChoice(
        mode.Id, SettingsViewModel.DisplayName(mode.Id),
        task == mode.Id || (task is null && mode.Id == defaultId), task == mode.Id,
        TryProjectMineruConfig(selection, mode.Id, out _) ? mode.Availability : "unavailable",
        mode.Availability == "preparation_required" && mode.RequiredComponent is not null,
        mode.LifecycleKind,
        mode.SupportsPreload, mode.SupportsTtl, mode.SupportsPinning, mode.SupportsRelease, mode.Family, mode.SupportedOptions, GetModeOptions(mode)?.ProjectWire(mode), mode.ReasonCode,
        IsDefault: mode.Id == defaultId))];
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
      RecognitionWorkbenchState state;
      using (screenCapture ? shellActions?.SuspendFloatingToolbarForCapture() : null)
      {
        bool deferredSelection = !await EnsureSelectionLoadedAsync(cancellationToken);
        SynchronizeRecognitionMode(requireUsable: true);
        state = await RunRecognitionAsync(action, cancellationToken);
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

      }
      // 完成状态发布前恢复悬浮栏，调用方看到终态时截图让位已结束。
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(state);
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
    catch (RecognitionModeUnavailableException error)
    {
      // 严格模式合同：模式不可用时保留引擎目录与用户选择，指向环境修复。
      AppLog.Warn($"Recognition mode override refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Recognition mode selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
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
    // 完成结果即使文本为空也保留真实（可能为空的）文本资源：Result 非空
    // 合同绑定真实识别结果，而不是文本长度（#110）。
    WorkbenchResourceReference? result = recognition.Result is null
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(recognition.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    WorkbenchResourceReference? structured = await PublishStructuredResultAsync(
      recognition, cancellationToken);
    if (recognition.Result is not null)
    {
      resultActions = recognition.CreateResultActions(
        new WindowsResultActionPlatform(windowHandle));
    }
    return RecognitionState(
      recognition.IsBusy,
      RecognitionStatusCode(recognition),
      input,
      result,
      structured: structured);
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
    if (!structuredResourceFiles.TryGetValue(command.ResourceUri, out string? path) ||
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
    ExportResult? export = await actions.ExportAsync(format, cancellationToken);
    return await CurrentRecognitionStateAsync(
      export?.Incomplete is true ? "recognition.exportedIncomplete" : "recognition.exported",
      cancellationToken);
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
    // 同上：真实完成结果即使文本为空也保留结果资源（#110）。
    WorkbenchResourceReference? result = viewModel.Result is null
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(viewModel.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    WorkbenchResourceReference? structured = await PublishStructuredResultAsync(
      viewModel, cancellationToken);
    return RecognitionState(false, statusCode, input, result, structured);
  }

  private async Task<BatchWorkbenchState> AddBatchFilesAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    await batch.PickFilesAsync(cancellationToken);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState> AddDroppedBatchFilesAsync(
    AddDroppedBatchFilesCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    batch.AddFiles(command.Paths);
    return await BatchStateAsync(batch, cancellationToken);
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
      IReadOnlyList<ExportResult> exports = await batch.ExportAllAsync(
        folder.Path, command.Format, cancellationToken);
      batchExportIncomplete = exports.Any(result => result.Incomplete is true);
    }
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState?> StartBatchAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    // 提交前取得权威目录与已提交默认：未 attach 时等待 Supervisor 启动；
    // 环境切换窗口取消本次提交，不以空快照冻结批量提交参数。
    if (!await EnsureSelectionLoadedForSubmitAsync(cancellationToken))
    {
      Interlocked.Increment(ref batchGeneration);
      return BatchState(batch);
    }
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
      BatchWorkbenchState state = await BatchStateAsync(batch, cancellationToken);
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
    batchExportIncomplete = false;
    batchWindowStart = 0;
    batchStructured.Clear();
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState> MoveBatchItemAsync(
    MoveBatchItemCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    if (batch.IsRunning)
    {
      throw new InvalidOperationException("A running batch cannot be reordered.");
    }
    batch.Move(command.ItemId, command.Delta);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState> RemoveBatchItemAsync(
    RemoveBatchItemCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    batch.Remove(command.ItemId);
    batchStructured.Remove(command.ItemId);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState> SetBatchWindowAsync(
    SetBatchWindowCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    batchWindowStart = ClampWindowStart(command.Start, batch.Items.Count, 40);
    return await BatchStateAsync(batch, cancellationToken);
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
    RecognitionSelectionSnapshot? snapshot = settings?.RecognitionSelection;
    if (batch is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      if (requireUsable) EnsureUsableModeOverrideOrThrow(selection: snapshot?.Catalog, taskEngine: batchTaskEngine);
      batch?.SetRecognitionMode(null, taskModeId: batchTaskEngine);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = EffectiveModeIdOrDefault(
      snapshot, batchTaskEngine, requireUsable);
    RecognitionModeOption? mode = string.IsNullOrWhiteSpace(effectiveId) ? null : requireUsable
      ? selection.SelectRecognitionMode(effectiveId)
      : TryFindRecognitionMode(selection, effectiveId);
    MineruConfig? config = requireUsable
      ? selection.MineruConfigFor(mode?.Id)
      : TryProjectMineruConfig(selection, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    batch.SetRecognitionMode(mode, config, GetModeOptions(mode), batchTaskEngine);
  }

  /// <summary>
  /// PDF 页面 OCR 与单次/批量共享同一模式合同：切换目录/环境后重投影，
  /// 提交前 requireUsable 严格协商；显式选择的模式在当前环境不可用时
  /// 拒绝提交并指向环境，不静默回退通用文字识别。
  /// </summary>
  private void SynchronizePdfMode(bool requireUsable = false)
  {
    RecognitionSelectionSnapshot? snapshot = settings?.RecognitionSelection;
    if (pdf is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      if (requireUsable) EnsureUsableModeOverrideOrThrow(snapshot?.Catalog, pdfTaskEngine);
      pdf?.SetRecognitionMode(null, taskModeId: pdfTaskEngine);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = EffectiveModeIdOrDefault(
      snapshot, pdfTaskEngine, requireUsable);
    RecognitionModeOption? mode = string.IsNullOrWhiteSpace(effectiveId) ? null : requireUsable
      ? selection.SelectRecognitionMode(effectiveId)
      : TryFindRecognitionMode(selection, effectiveId);
    MineruConfig? config = requireUsable
      ? selection.MineruConfigFor(mode?.Id)
      : TryProjectMineruConfig(selection, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    pdf.SetRecognitionMode(mode, GetModeOptions(mode), pdfTaskEngine);
  }

  private PdfWorkbenchState SetPdfTaskEngine(SetPdfTaskEngineCommand command)
  {
    pdf ??= pdfFactory();
    if (pdf.IsBusy) throw new InvalidOperationException("A running PDF OCR cannot change recognition mode.");
    RuntimeSelectionService? selection = settings?.RecognitionSelection?.Catalog;
    if (command.Engine is not null)
    {
      if (selection?.SupportsRecognitionModes is not true)
        throw new RuntimeSelectionException(RuntimeSelectionErrorKind.CapabilityMissing,
          "The runtime does not provide recognition modes.");
      RecognitionModeOption mode = selection.SelectRecognitionMode(command.Engine);
      selection.MineruConfigFor(mode.Id);
    }
    pdfTaskEngine = command.Engine;
    SynchronizePdfMode();
    return PdfState(pdf);
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
      pdfStructured.Clear();
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

  private async Task<PdfWorkbenchState?> StartPdfOcr(CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    // 与单次/批量相同：提交前加载权威目录并严格协商可用模式；用户已显式
    // 选择 Paddle 模式而目录/环境不可用时，这里会抛出可恢复的
    // RecognitionModeUnavailableException，拒绝按默认 OCR 静默提交。
    if (!await EnsureSelectionLoadedForSubmitAsync(cancellationToken))
    {
      // 环境切换窗口：取消本次提交，不以空快照提交（Rapid 回退）。
      Interlocked.Increment(ref pdfGeneration);
      return PdfState(pdf);
    }
    SynchronizePdfMode(requireUsable: true);
    long generation = Interlocked.Increment(ref pdfGeneration);
    return PublishStartThenTrack(
      PdfState(pdf) with { IsBusy = true, StatusCode = PdfStatusCode(pdf, isBusy: true) },
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
      long previewRevision = qrCode!.PreviewRevision;
      string? nextPreviewPath = qrPreviewPath;
      if (previewRevision != publishedQrRevision && qrCode.GeneratedImageBase64 is { } preview)
      {
        (nextGeneratedResource, nextPreviewPath) = await PublishFileAsync(
          Convert.FromBase64String(preview), qrCode.PreviewMediaType,
          qrCode.PreviewMediaType == "image/jpeg" ? ".jpg" : ".png", cancellationToken);
      }
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        generatedQrResource = nextGeneratedResource;
        qrPreviewPath = nextPreviewPath;
        publishedQrRevision = previewRevision;
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

  private async Task<QrCodeWorkbenchState> CancelQrCodeAsync(CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    Interlocked.Increment(ref qrCodeGeneration);
    qrCode.Cancel();
    if (qrCode.HasPreview && publishedQrRevision != qrCode.PreviewRevision)
      await CompleteQrCodeAsync(_ => Task.CompletedTask, false, Volatile.Read(ref qrCodeGeneration), cancellationToken);
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
    qrPreviewPath = null;
    return QrCodeState(qrCode);
  }

  private async Task<QrCodeWorkbenchState> CopyQrCodeImageAsync(CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    if (qrPreviewPath is null || publishedQrRevision != qrCode.PreviewRevision) throw new InvalidOperationException("当前没有可复制的预览图片。");
    await annotatedImagePlatform.CopyImageAsync(qrPreviewPath, cancellationToken);
    return QrCodeState(qrCode) with { StatusCode = "qrcode.copied" };
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

  private async Task<SettingsWorkbenchState> RunEnvironmentAsync(
    Func<ManagedEnvironmentSettings, Task> action,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    settings ??= CreateSettings();
    ManagedEnvironmentSettings environments = settings.Environments
      ?? throw new InvalidOperationException("运行环境管理器不可用。");
    await action(environments);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState? StartEnvironmentOperation(
    Func<ManagedEnvironmentSettings, Task> action,
    bool refreshCatalog = false)
  {
    settings ??= CreateSettings();
    ManagedEnvironmentSettings environments = settings.Environments
      ?? throw new InvalidOperationException("运行环境管理器不可用。");
    if (refreshCatalog)
    {
      Interlocked.Exchange(ref environmentSwitching, 1);
      settings.ClearSelection();
      // 切换尚未提交，失效文案不得声称实例已更换；结束后按实际当前服务回读。
      settings.InvalidateSnapshot("服务状态待重新检查。");
    }
    return PublishStartThenTrack(SettingsState(settings), async () =>
    {
      bool completed = false;
      try
      {
        if (refreshCatalog)
          await RefreshRecognitionCatalogStatesAsync(CancellationToken.None);
        await action(environments);
        completed = true;
      }
      catch (Exception error) when (error is not OperationCanceledException)
      {
        AppLog.Warn($"Environment operation failed: {error.GetType().Name}: {error.Message}");
      }
      finally
      {
        if (refreshCatalog)
        {
          try
          {
            if (Volatile.Read(ref disposed) == 0)
            {
              if (completed)
              {
                await RefreshRecognitionCatalogAsync(CancellationToken.None);
                // 以新环境重新读取设置快照：Backend/驻留等旧实例投影不得
                // 继续冒充当前状态。
                await settings.LoadSnapshotAsync(CancellationToken.None);
              }
              else
              {
                // 失败同样按实际当前服务回读；不刷环境列表，
                // 避免其中性成功文案覆盖切换失败的根因。
                await settings.LoadSnapshotAsync(refreshEnvironments: false, CancellationToken.None);
                await RefreshRecognitionCatalogStatesAsync(CancellationToken.None);
              }
            }
          }
          catch (Exception error) when (error is not OperationCanceledException)
          {
            AppLog.Warn($"Recognition catalog refresh failed: {error.GetType().Name}: {error.Message}");
          }
          finally { Interlocked.Exchange(ref environmentSwitching, 0); }
        }
      }
    });
  }

  private SettingsWorkbenchState CancelEnvironmentInstall()
  {
    settings ??= CreateSettings();
    settings.Environments?.CancelInstall();
    return SettingsState(settings);
  }

  private SettingsWorkbenchState InvalidateEnvironmentPlan()
  {
    settings ??= CreateSettings();
    settings.Environments?.InvalidatePlan();
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

  private SettingsWorkbenchState BeginHotkeyRecording(BeginHotkeyRecordingCommand command)
  {
    ShellActions().BeginHotkeyRecording(command.RecordingId);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  private SettingsWorkbenchState EndHotkeyRecording(EndHotkeyRecordingCommand command)
  {
    ShellActions().EndHotkeyRecording(command.RecordingId);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  internal void EndHotkeyRecording()
  {
    if (shellActions?.IsHotkeyRecording != true) return;
    shellActions.EndHotkeyRecording();
    StateChanged?.Invoke(settings is null ? SettingsShellState() : SettingsState(settings));
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

  private SettingsWorkbenchState SetFloatingToolbarPreferences(
    SetFloatingToolbarPreferencesCommand command)
  {
    settings ??= CreateSettings();
    if ((command.LingerMs is null && command.Theme is null && command.PeekPixels is null) ||
      command.PeekPixels is < 1 or > FloatingToolbarSettings.MaximumPeekPixels ||
      command.LingerMs is < FloatingToolbarSettings.MinimumLingerMs or > FloatingToolbarSettings.MaximumLingerMs ||
      command.Theme is not (null or "system" or "light" or "dark"))
    {
      return SettingsState(settings, "收起时间须为 100–5000 毫秒，露出像素须为 1–20，主题须为跟随系统、亮色或深色；原设置已保留。");
    }
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    int lingerMs = command.LingerMs ?? current.LingerMs;
    int peekPixels = command.PeekPixels ?? current.PeekPixels;
    FloatingToolbarTheme theme = command.Theme is null
      ? current.Theme : FloatingToolbarSettings.ParseTheme(command.Theme);
    string? error = current.LingerMs == lingerMs && current.Theme == theme && current.PeekPixels == peekPixels
      ? null
      : ApplyToolbar(actions, current with { LingerMs = lingerMs, Theme = theme, PeekPixels = peekPixels });
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
    if (command.Kind == "package_index") settings.Environments?.InvalidatePlan();
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

  /// <summary>
  /// 保存默认识别模式：能力门禁 + 目录 ready 校验在
  /// SettingsViewModel 内 fail closed；保存成功后重发任务页状态，继承
  /// 选项立即回显新默认，在跑/排队任务参数不受影响。
  /// </summary>
  private async Task<SettingsWorkbenchState> SetDefaultRecognitionModeAsync(
    SetDefaultRecognitionModeCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetDefaultRecognitionModeAsync(
      command.ModeId,
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
    StateChanged?.Invoke(pdf is null
      ? new PdfWorkbenchState(false, "pdf.empty", 0, -1,
          Engines: PdfEngines(), TaskEngine: pdfTaskEngine)
      : PdfState(pdf));
  }

  internal async Task RefreshRecognitionCatalogAsync(CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    settings.ClearSelection();
    try
    {
      if (inferenceAttached?.Invoke() != false)
        await settings.RefreshSelectionAsync(cancellationToken);
    }
    finally
    {
      if (Volatile.Read(ref disposed) == 0)
        await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    }
  }

  private BatchWorkbenchState CurrentBatchState() => batch is null
    ? new BatchWorkbenchState(false, 0, 0, 0, Engines: BatchEngines())
    : BatchState(batch);

  private IReadOnlyList<RecognitionEngineChoice>? BatchEngines()
  {
    string? defaultId = RuntimeDefaultModeId;
    return RecognitionEngines()?.Select(choice => choice with
    {
      // IsDefault 从目录投影继承；Selected 仅显式选择或（未覆盖时）默认。
      Selected = choice.Engine == batchTaskEngine ||
        (batchTaskEngine is null && choice.Engine == defaultId),
      IsTaskOverride = choice.Engine == batchTaskEngine,
    }).ToArray();
  }

  private PaddleModeOptions? GetModeOptions(RecognitionModeOption? mode)
  {
    if (mode?.Id.StartsWith("paddle_", StringComparison.Ordinal) is not true) return null;
    if (!modeOptions.TryGetValue(mode.Id, out PaddleModeOptions? options))
    {
      options = optionsLayout is null ? new() : PaddleModeOptions.Load(optionsLayout, mode.Id);
      modeOptions.Add(mode.Id, options);
    }
    return options;
  }

  private RecognitionWorkbenchState SetRecognitionOptions(SetRecognitionOptionsCommand command)
  {
    settings ??= CreateSettings();
    RecognitionModeOption mode = settings.RecognitionSelection?.Catalog.FindRecognitionMode(command.ModeId)
      ?? throw new InvalidOperationException("Recognition mode is absent from this environment.");
    command.Options.ToWire(mode);
    if (optionsLayout is not null) command.Options.Save(optionsLayout, mode.Id);
    modeOptions[mode.Id] = command.Options;
    SynchronizeRecognitionMode();
    SynchronizeBatchMode();
    return CurrentRecognitionState();
  }
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
      if (requireUsable) EnsureUsableModeOverrideOrThrow(snapshot?.Catalog, recognition?.TaskEngine);
      recognition?.SetRecognitionMode(null);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = EffectiveModeIdOrDefault(
      snapshot, recognition.TaskEngine, requireUsable);
    RecognitionModeOption? Resolve(string? id) => string.IsNullOrWhiteSpace(id)
      ? null
      : requireUsable
        ? selection.SelectRecognitionMode(id)
        : TryFindRecognitionMode(selection, id);
    RecognitionModeOption? mode = Resolve(effectiveId);
    // mineru_document 任务随目录默认 tier 携带类型化 MinerU 4 配置。
    MineruConfig? config = requireUsable
      ? selection.MineruConfigFor(mode?.Id)
      : TryProjectMineruConfig(selection, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    recognition.SetRecognitionMode(mode, config, GetModeOptions(mode));
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

  private static RecognitionModeOption? TryFindRecognitionMode(
    RuntimeSelectionService selection,
    string modeId)
  {
    try
    {
      return selection.FindRecognitionMode(modeId);
    }
    catch (RuntimeSelectionException)
    {
      // 环境切换后旧选择可能不在新目录：状态投影不崩溃；提交路径由
      // requireUsable 协商拒绝，不静默降级。
      return null;
    }
  }

  /// <summary>
  /// 显式选择的识别模式（非 legacy 引擎 id）在模式目录缺失/不可用时拒绝
  /// 提交：可恢复失败并指向环境，绝不静默回退通用文字识别。仅未选择
  /// （Runtime 默认）或旧版引擎 id 的兼容路径允许默认管线提交。
  /// </summary>
  private static void EnsureUsableModeOverrideOrThrow(
    RuntimeSelectionService? selection,
    string? taskEngine)
  {
    if (string.IsNullOrWhiteSpace(taskEngine) || OcrEngineWire.Parse(taskEngine) is not null)
    {
      return;
    }
    throw new RecognitionModeUnavailableException(selection is null
      ? $"任务识别模式 {taskEngine} 需要的识别模式目录尚未加载（运行环境可能未就绪），已拒绝按通用文字识别执行；请在设置中检查运行环境后重试。"
      : $"当前运行环境不支持识别模式 {taskEngine}，已拒绝按通用文字识别执行；请切换或准备对应运行环境。");
  }

  /// <summary>
  /// Load the authoritative runtime selection catalog. While the Supervisor
  /// client is unattached the load is skipped: bootstrap must not block the
  /// window, and recognition commands re-load through
  /// <see cref="EnsureSelectionLoadedForSubmitAsync"/> before freezing submit
  /// parameters, so no submit path depends on the gateway default. Returns
  /// whether the catalog was (re)loaded.
  /// </summary>
  private async Task<bool> EnsureSelectionLoadedAsync(CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (Volatile.Read(ref environmentSwitching) != 0) return false;
    if (inferenceAttached?.Invoke() == false)
    {
      return false;
    }
    // Always cross SettingsViewModel's single-flight gate. A refresh may be
    // replacing an older snapshot even while Selection remains non-null.
    await settings.LoadSelectionAsync(cancellationToken);
    return true;
  }

  /// <summary>
  /// 提交路径的目录加载：Supervisor 未 attach 时等待启动完成（Deferred
  /// 网关内建等待）取得权威目录与已提交默认后才冻结提交参数；环境切换
  /// 窗口返回 false，由调用方取消提交而非猜默认。
  /// </summary>
  private async Task<bool> EnsureSelectionLoadedForSubmitAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (Volatile.Read(ref environmentSwitching) != 0)
      return false;
    await settings.LoadSelectionAsync(cancellationToken);
    return true;
  }

  /// <summary>
  /// 采集型识别的延迟冻结回调：输入采集后按启动时意图（跟随默认，忽略
  /// 等待期间的新 UI 选择）在权威目录上解析提交参数；环境切换窗口返回
  /// null 取消本次提交；目录不支持模式时保持旧 Backend 的 OCR 缺省兼容。
  /// </summary>
  private Func<CancellationToken, Task<RecognitionSubmitSelection?>>
    AwaitSelectionBeforeFreeze() => async ct =>
  {
    if (!await EnsureSelectionLoadedForSubmitAsync(ct))
      return null;
    RecognitionSelectionSnapshot? snapshot = settings?.RecognitionSelection;
    if (snapshot?.Catalog.SupportsRecognitionModes is not true)
      return new RecognitionSubmitSelection("OCR", null, null, null);
    RuntimeSelectionService selection = snapshot.Catalog;
    string? defaultId = EffectiveModeIdOrDefault(
      snapshot, taskEngine: null, requireUsable: true);
    RecognitionModeOption? mode = string.IsNullOrWhiteSpace(defaultId)
      ? null
      : selection.SelectRecognitionMode(defaultId);
    MineruConfig? config = mode is null ? null : selection.MineruConfigFor(mode.Id);
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? options =
      mode is null ? null : GetModeOptions(mode)?.ToWire(mode);
    return new RecognitionSubmitSelection(
      mode?.PipelineId ?? "OCR", mode?.Engine, config, options);
  };

  private SettingsViewModel CreateSettings()
  {
    SettingsViewModel model = settingsFactory();
    if (model.Environments is { } environments)
      environments.StateChanged += OnSettingsChanged;
    model.Maintenance.StateChanged += OnSettingsChanged;
    model.RuntimeStatus.PropertyChanged += OnSettingsPropertyChanged;
    model.PropertyChanged += OnSettingsPropertyChanged;
    return model;
  }

  private void OnSettingsChanged()
  {
    if (Volatile.Read(ref disposed) == 0 && settings is not null)
    {
      string service = supervisorInstanceId() ?? string.Empty;
      bool maintaining = settings.Maintenance.State.IsRunning;
      bool instanceChanged = service != pinnedServiceInstance;
      if (instanceChanged || (maintaining && !pinMaintenanceNotified))
        PinnedTextEnvironmentChanged?.Invoke();
      pinnedServiceInstance = service;
      pinMaintenanceNotified = maintaining;
      if (instanceChanged)
      {
        // 服务实例更换（切换/维护停止/崩溃恢复）：旧实例的在途快照与加速
        // 器目标不得继续作为当前状态投影。
        settings.InvalidateSnapshot();
      }
      InvalidateScreenshotSessionRecognitionOnMaintenance();
      StateChanged?.Invoke(SettingsState(settings));
    }
  }

  private void OnToolbarStateChanged()
  {
    if (Volatile.Read(ref disposed) == 0)
      StateChanged?.Invoke(settings is null ? SettingsShellState() : SettingsState(settings));
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
    RejectLegacyMaintenanceForManagedSettings(settings);
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
    RejectLegacyMaintenanceForManagedSettings(settings);
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
    RejectLegacyMaintenanceForManagedSettings(settings);
    await settings.RetryMaintenanceAsync(cancellationToken);
    return SettingsState(settings);
  }

  private static void RejectLegacyMaintenanceForManagedSettings(SettingsViewModel model)
  {
    if (model.Environments is not null)
      throw new InvalidOperationException("请为指定环境预览锁定配方后安装。");
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

  /// <summary>
  /// 复制诊断详情：复用导出的同一脱敏文档，经既有平台剪贴板接缝写入；
  /// busy 失败走既有 clipboard_busy 问题反馈，不新增第二条剪贴板路径。
  /// </summary>
  private async Task<DiagnosticsWorkbenchState> CopyDiagnosticsAsync(
    CancellationToken cancellationToken)
  {
    await structuredClipboard.WriteTextAsync(
      diagnostics.BuildRedactedExportJson(),
      cancellationToken);
    return DiagnosticsState();
  }

  private Task<WorkbenchResourceReference?> PublishStructuredResultAsync(
    RecognitionViewModel viewModel,
    CancellationToken cancellationToken) =>
    PublishStructuredResultAsync(
      viewModel.Result?.ContentBlocks,
      viewModel.FetchResultAssetAsync,
      cancellationToken);

  /// <summary>
  /// 结构化结果与授权图片资产的唯一发布路径：单次/批量项/PDF 页共用，
  /// 不复制三套。图片经 job/item/asset 授权取回后以 opaque 资源发布；
  /// 最终 JSON 也注册进剪贴板复制的资源表，供 CopyStructuredResultAsync
  /// 重读权威内容。
  /// </summary>
  private async Task<WorkbenchResourceReference?> PublishStructuredResultAsync(
    System.Text.Json.JsonElement[]? blocks,
    Func<string, string, string, CancellationToken, Task<byte[]>>? fetchAsset,
    CancellationToken cancellationToken)
  {
    if (blocks is not { Length: > 0 } content) return null;
    JsonArray json = [];
    int imageCount = 0;
    foreach (JsonElement block in content)
    {
      JsonNode? node = JsonNode.Parse(block.GetRawText());
      if (node is not JsonObject item) continue;
      if (item["image"] is JsonObject asset)
      {
        // resource 只承载宿主发布的 opaque 资源引用：wire 原样携带的
        // 不可信副本先清除，成功取回后再写回权威引用。
        asset.Remove("resource");
        if (++imageCount > 32)
        {
          asset["available"] = false;
          asset["reason"] = "本项图片数量超过 32 张预览上限";
        }
        else if (asset["available"]?.GetValue<bool>() is not false &&
          fetchAsset is not null &&
          asset["job_id"] is JsonValue job && job.TryGetValue<string>(out string? jobId) &&
          asset["item_id"] is JsonValue sourceItem && sourceItem.TryGetValue<string>(out string? itemId) &&
          asset["asset_id"] is JsonValue identity && identity.TryGetValue<string>(out string? assetId))
        {
          try
          {
            byte[] bytes = await fetchAsset(
              jobId!, itemId!, assetId!, cancellationToken);
            WorkbenchResourceReference reference = await PublishBytesAsync(
              bytes, "image/png", ".png", cancellationToken);
            asset["resource"] = JsonSerializer.SerializeToNode(new
            {
              url = reference.Url,
              mediaType = reference.MediaType,
              byteLength = reference.ByteLength,
            });
          }
          // HttpClient 超时按图片降级；调用者取消仍向上传播。
          catch (Exception error) when (error is IOException or
            ArgumentException or NotSupportedException or InferenceClientException or
            HttpRequestException or VibeOCR.Runtime.Client.RuntimeClientException or
            InvalidDataException or InferenceClientNotAttachedException ||
            error is OperationCanceledException { InnerException: TimeoutException } &&
            !cancellationToken.IsCancellationRequested)
          {
            asset["available"] = false;
            asset["reason"] = "结果图片已失效或无法读取";
          }
        }
      }
      json.Add(item);
    }
    (WorkbenchResourceReference structured, string structuredPath) =
      await PublishFileAsync(
        Encoding.UTF8.GetBytes(json.ToJsonString()),
        "application/json; charset=utf-8", ".json", cancellationToken);
    structuredResourceFiles[structured.Url] = structuredPath;
    return structured;
  }

  private void ReleaseResource(WorkbenchResourceReference resource)
  {
    if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out Uri? uri)) return;
    resourceBroker.Revoke(new WorkbenchResourceLease(uri, DateTimeOffset.MaxValue));
    // Published input files belong to the app and are retained only while leased.
    if (resourceFiles.TryGetValue(resource.Url, out string? path))
    {
      try
      {
        File.Delete(path);
        resourceFiles.Remove(resource.Url);
        generatedFiles.Remove(path);
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }

  private async Task<WorkbenchResourceReference> PublishBytesAsync(
    byte[] data,
    string mediaType,
    string extension,
    CancellationToken cancellationToken) =>
    (await PublishFileAsync(data, mediaType, extension, cancellationToken)).Reference;

  private async Task<(WorkbenchResourceReference Reference, string Path)> PublishFileAsync(
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
    resourceFiles[lease.Uri.AbsoluteUri] = destination;
    return (new WorkbenchResourceReference(
      lease.Uri.AbsoluteUri,
      mediaType,
      data.LongLength), destination);
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
        item.Result is null ? null : Truncate(item.Result.Text, 120),
        item.State == BatchItemState.Completed && item.Result is not null
          ? batchStructured.GetValueOrDefault(item.Id).Reference
          : null))
      .ToArray(),
    batchWindowStart,
    BatchEngines(),
    batchTaskEngine,
    batchExportIncomplete);
  }

  /// <summary>
  /// 可视窗口内已完成项的结构化结果发布：以 Result 对象身份为键缓存，
  /// 重跑/移除后旧引用不再复用；与单次/PDF 共用同一发布路径。
  /// </summary>
  private async Task<BatchWorkbenchState> BatchStateAsync(
    BatchViewModel viewModel,
    CancellationToken cancellationToken)
  {
    batchWindowStart = ClampWindowStart(batchWindowStart, viewModel.Items.Count, 40);
    foreach (BatchItemViewModel item in viewModel.Items.Skip(batchWindowStart).Take(40))
    {
      if (item.State != BatchItemState.Completed ||
        item.Result?.ContentBlocks is not { Length: > 0 })
      {
        batchStructured.Remove(item.Id);
        continue;
      }
      if (batchStructured.TryGetValue(item.Id, out var cached) &&
        ReferenceEquals(cached.Result, item.Result))
      {
        continue;
      }
      WorkbenchResourceReference? reference = await PublishStructuredResultAsync(
        item.Result.ContentBlocks,
        viewModel.FetchResultAssetAsync,
        cancellationToken);
      if (reference is null)
      {
        batchStructured.Remove(item.Id);
      }
      else
      {
        batchStructured[item.Id] = (item.Result, reference);
      }
    }
    return BatchState(viewModel);
  }

  private PdfWorkbenchState PdfState(PdfViewModel viewModel)
  {
    SynchronizePdfMode();
    pdfWindowStart = ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    return new PdfWorkbenchState(
      viewModel.IsBusy,
      PdfStatusCode(viewModel),
      viewModel.PageCount,
      viewModel.SelectedPage,
      selectedPdfPages.Order().ToArray(),
      Enumerable.Range(
        pdfWindowStart,
        Math.Min(viewModel.PageCount - pdfWindowStart, 64))
      .Select(index => new PdfWorkbenchPage(
        index,
        PdfPageStatus(viewModel, index),
        pdfThumbnails.GetValueOrDefault(index),
        index < viewModel.Pages.Count &&
          viewModel.Pages[index].State == PdfPageState.Done &&
          viewModel.Pages[index].Result is not null
          ? pdfStructured.GetValueOrDefault(index).Reference
          : null))
      .ToArray(),
      pdfWindowStart,
      PdfEngines(),
      pdfTaskEngine);
  }

  /// <summary>
  /// PDF workbench 状态码：上次已结束操作的原生语义问题码（open/save/
  /// mutate/OCR 的失败/取消）优先于 PageCount 会话投影，不再被 pdf.empty/
  /// pdf.open 抹掉；运行中沿用既有会话投影。仅消费固定语义码，不解析中文
  /// Status，也不把含本地保存路径/异常细节的 Status 发送给 Web。
  /// </summary>
  private static string PdfStatusCode(PdfViewModel viewModel, bool? isBusy = null)
  {
    if (isBusy ?? viewModel.IsBusy)
    {
      return viewModel.PageCount > 0 ? "pdf.open" : "pdf.empty";
    }
    return viewModel.TerminalIssue switch
    {
      PdfIssueKind.BackendUnavailable => "pdf.backendUnavailable",
      PdfIssueKind.OutOfMemory => "pdf.outOfMemory",
      PdfIssueKind.Cancelled => "pdf.cancelled",
      PdfIssueKind.Failed => "pdf.failed",
      _ => viewModel.PageCount > 0 ? "pdf.open" : "pdf.empty",
    };
  }

  private IReadOnlyList<RecognitionEngineChoice>? PdfEngines()
  {
    string? defaultId = RuntimeDefaultModeId;
    return RecognitionEngines()?.Select(choice => choice with
    {
      // IsDefault 从目录投影继承；Selected 仅显式选择或（未覆盖时）默认。
      Selected = choice.Engine == pdfTaskEngine ||
        (pdfTaskEngine is null && choice.Engine == defaultId),
      IsTaskOverride = choice.Engine == pdfTaskEngine,
    }).ToArray();
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
    // 已完成页的结构化结果与缩略图同窗口发布：以 Result 对象身份缓存，
    // 旋转/删除/重开文档时整体失效，与单次/批量共用同一发布路径。
    for (int index = pdfWindowStart; index < visiblePageEnd; index++)
    {
      if (index >= viewModel.Pages.Count) break;
      RecognizeResponse? result = viewModel.Pages[index].Result;
      if (result?.ContentBlocks is not { Length: > 0 })
      {
        pdfStructured.Remove(index);
        continue;
      }
      if (pdfStructured.TryGetValue(index, out var cached) &&
        ReferenceEquals(cached.Result, result))
      {
        continue;
      }
      WorkbenchResourceReference? reference = await PublishStructuredResultAsync(
        result.ContentBlocks,
        viewModel.FetchResultAssetAsync,
        cancellationToken);
      if (reference is null)
      {
        pdfStructured.Remove(index);
      }
      else
      {
        pdfStructured[index] = (result, reference);
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
    pdfStructured.Clear();
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
      items.Length > 0 ? "qrcode.decoded" : viewModel.HasPreview && !viewModel.NeedsPreviewDecode ? "qrcode.noCodes" : "qrcode.ready",
      [],
      generatedQrResource,
      items,
      viewModel.PreviewRevision,
      viewModel.NeedsPreviewDecode,
      viewModel.GenerateInvalidInput ? viewModel.GenerateStatus : viewModel.DecodeStatus);
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
        error ?? "",
        shellActions.ToolbarSettings.LingerMs,
        FloatingToolbarSettings.ThemeName(shellActions.ToolbarSettings.Theme),
        shellActions.ToolbarSettings.PeekPixels);

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
    ProgressActive: viewModel.RuntimeStatus.IsOperationActive,
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
      : null,
    DefaultRecognitionMode: viewModel.DefaultRecognitionMode is { } defaultMode
      ? new SettingsDefaultRecognitionModeState(
        defaultMode.Supported,
        defaultMode.ModeId,
        defaultMode.Stored)
      : null,
    RecognitionModes: viewModel.Selection?.SupportsRecognitionModes is true
      ? [.. viewModel.Selection.RecognitionModes.Select(mode => new SettingsRecognitionModeOptionState(
          mode.Id,
          SettingsViewModel.DisplayName(mode.Id),
          mode.Availability,
          mode.ReasonCode))]
      : null,
    Environments: viewModel.Environments?.Snapshot?.Environments.Select(item =>
      new SettingsEnvironmentState(
        item.Id, item.Name, item.Revision, item.Kind, item.Status,
        item.PythonState, item.DependencyState, item.EngineState, item.ModelState,
        item.ServiceState,
        item.ConfiguredRecognitionTypes ?? [],
        item.TargetDevice, item.ActualDevice, item.Reason,
        item.PythonVersion, item.Abi, item.Python, item.Path, item.DiskBytes,
        item.SourceIds ?? [],
        item.OverrideSourceIds ?? [],
        item.UnknownSourceIds ?? [],
        item.ResolvedSources?.Select(source => new SettingsEnvironmentResolvedSourceState(
          source.Kind, source.Id, source.DisplayName, source.Origin)).ToArray(),
        item.LastInstallFailure is { } failure
          ? new SettingsEnvironmentInstallFailureState(
            failure.Phase, failure.EnvironmentRevision, failure.Recipe,
            failure.ReasonCode, failure.NextAction, failure.Detail,
            failure.RequestedSourceIds, failure.EffectiveSourceIds)
          : null)).ToArray(),
    ActiveEnvironmentId: viewModel.Environments?.Snapshot?.ActiveId,
    EnvironmentPlan: viewModel.Environments?.Plan is { } environmentPlan
      ? new SettingsEnvironmentPlanState(
        environmentPlan.PlanId, environmentPlan.EnvironmentId,
        environmentPlan.Recipe, environmentPlan.SourceIds,
        environmentPlan.Dependencies ?? [],
        environmentPlan.RequestedRecipe ?? environmentPlan.Recipe,
        environmentPlan.RequestedSourceIds,
        environmentPlan.EnvironmentRevision,
        environmentPlan.Sources?.Select(source => new SettingsEnvironmentPlanSourceState(
          source.Id, source.Kind, source.DisplayName, source.Endpoint,
          source.Requested, source.InheritedFrom, source.Usage, source.ActualEndpoint)).ToArray(),
        environmentPlan.DependencyOrigin, environmentPlan.PythonOrigin,
        environmentPlan.RuntimeWheelOrigin)
      : null,
    EnvironmentStatus: viewModel.Environments?.Status ?? "",
    EnvironmentBusy: viewModel.Environments?.IsBusy ?? false,
    EnvironmentSources: viewModel.Environments?.Snapshot?.Sources?.Select(source =>
      new SettingsEnvironmentSourceState(
        source.Id, source.Kind, source.DisplayName, source.Endpoint, source.IsDefault)).ToArray(),
    EnvironmentDefaultSourceIds: viewModel.Environments?.Snapshot?.DefaultSourceIds,
    EnvironmentResolvedDefaultSources: viewModel.Environments?.Snapshot?.ResolvedDefaultSources?.Select(source =>
      new SettingsEnvironmentResolvedSourceState(source.Kind, source.Id, source.DisplayName, source.Origin)).ToArray(),
    EnvironmentUnknownDefaultSourceIds: viewModel.Environments?.Snapshot?.UnknownDefaultSourceIds,
    EnvironmentPackageSourceIds: viewModel.Environments?.Snapshot?.PackageSourceIds,
    EnvironmentCanCancelInstall: viewModel.Environments?.CanCancelInstall ?? false,
    EnvironmentRecipes: viewModel.Environments?.Snapshot?.Recipes?.Select(recipe =>
      new SettingsEnvironmentRecipeState(
        recipe.Id, recipe.DisplayName, recipe.ConfiguredRecognitionTypes,
        recipe.Accelerator, recipe.TargetDevice, recipe.PythonVersion, recipe.Abi,
        recipe.Platform, recipe.ScopeId, recipe.ComponentIds, recipe.RecipeLock,
        recipe.Dependencies, recipe.DependencyOrigin, recipe.PythonOrigin,
        recipe.RuntimeWheelOrigin)).ToArray(),
    EnvironmentHardware: viewModel.Environments?.Snapshot?.Hardware is { } hardware
      ? new SettingsEnvironmentHardwareState(
        hardware.NvidiaDriver?.Status ?? "unknown",
        hardware.NvidiaDriver?.ReasonCode,
        hardware.NvidiaDriver?.DriverVersion)
      // 旧 Runtime payload 无 hardware：诚实按未探测呈现，不臆造可用性。
      : new SettingsEnvironmentHardwareState("unknown"),
    EnvironmentCompatibility: viewModel.Environments?.Compatibility is { } compatibility
      ? new SettingsEnvironmentCompatibilityState(
        compatibility.Recipe,
        compatibility.Selected?.EnvironmentId,
        compatibility.Selected?.EnvironmentRevision,
        compatibility.Selected?.SelectionReason,
        compatibility.Environments?.Select(match => new SettingsEnvironmentQueryMatchState(
          match.EnvironmentId, match.Name, match.Revision, match.Status,
          match.Active, match.Selected, match.ReasonCode)).ToArray())
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
      .ToArray(),
    diagnostics.DeviceEvidence);

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
      if (settings.Environments is { } environments)
        environments.StateChanged -= OnSettingsChanged;
      settings.Maintenance.StateChanged -= OnSettingsChanged;
      settings.RuntimeStatus.PropertyChanged -= OnSettingsPropertyChanged;
      settings.PropertyChanged -= OnSettingsPropertyChanged;
      settings.CancelMaintenance();
    }
    diagnostics.PropertyChanged -= OnDiagnosticsPropertyChanged;
    if (shellActions is not null)
      shellActions.ToolbarStateChanged -= OnToolbarStateChanged;
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
    structuredResourceFiles.Clear();
    batchStructured.Clear();
    pdfStructured.Clear();
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

internal sealed class PinnedTextPreparationException(string message) : Exception(message);
