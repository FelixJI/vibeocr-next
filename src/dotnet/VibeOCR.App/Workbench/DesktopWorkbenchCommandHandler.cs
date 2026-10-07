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
using Windows.Graphics.Imaging;
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

  /// <summary>
  /// 一个会话通道的宿主持有状态：独立 RecognitionViewModel、代际与会话
  /// 字段。recognition 通道（单次识别页与全部截图入口）与 imageEdit 通道
  /// （图片编辑页）互不影响：sessionId/revision/CurrentInput/结果/取消
  /// 全部按通道隔离，不共享同一 VM。
  /// </summary>
  private sealed class ImageSessionChannel
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
  }

  private readonly ImageSessionChannel recognitionChannel = new();
  private readonly ImageSessionChannel imageEditChannel = new();
  private ResultActions? resultActions;
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
    ImageEditState(false, "recognition.ready"),
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
  /// <summary>
  /// 普通截图动作栏显式识别终态：宿主显示主窗并导航识别承载面（明确
  /// 分支）。仅识别任务终态允许触发；本地输出失败/取消不借用。
  /// </summary>
  internal event Action? ScreenshotSelectionRecognized;
  /// <summary>普通截图动作栏“打开设置”意图：放弃本次截图并显式进入现有设置页。</summary>
  internal event Action? ScreenshotSelectionSettingsRequested;
  internal Guid? CurrentImageSessionId => recognitionChannel.SessionId;

  /// <summary>recognition 通道的识别 VM（懒创建；与 imageEdit 通道实例独立）。</summary>
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
    RecognitionEngines(),
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
  private ImageEditWorkbenchState ImageEditState(
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
      RecognitionEngines(),
      recognitionChannel.ViewModel?.TaskEngine,
      CurrentScreenshotSession(recognitionChannel),
      TextLayer: recognitionChannel.TextLayer,
      StructuredResult: recognitionChannel.SessionResult is null
        ? null
        : recognitionChannel.SessionStructuredResult);

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
    channel.TextLayerViewModel?.Cancel();
    if (channel.TextLayer?.Image is { } image) ReleaseResource(image);
    channel.TextLayer = null;
    if (editedRevision is { } revision && channel.SessionId is { } id)
      ScreenshotTextLayerInvalidated?.Invoke(id, revision);
  }

  private WorkbenchAnnotationFile TakeScreenshotAnnotation(string resourceUri) =>
    annotationStore.Take(new Uri(resourceUri));

  private WorkbenchState? StartImageEdit(
    ImageSessionChannel channel,
    Func<RecognitionViewModel, Task> loadImage,
    CancellationToken cancellationToken)
  {
    _ = channel.ViewModel ??= recognitionFactory();
    long generation = Interlocked.Increment(ref channel.Generation);
    return PublishStartThenTrack(
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
        StateChanged?.Invoke(viewModel.TerminalState == JobState.Failed
          ? SessionState(channel, false, "recognition.inputFailed")
          : CurrentChannelState(channel));
        return;
      }
      if (viewModel.CurrentInput is not { } image) return;
      WorkbenchResourceReference input = await PublishBytesAsync(image.Data, image.MediaType,
        ExtensionForMediaType(image.MediaType), cancellationToken);
      if (generation != Volatile.Read(ref channel.Generation))
      {
        ReleaseResource(input);
        return;
      }
      ClearScreenshotSession(channel);
      InvalidateScreenshotTextLayer(channel);
      if (channel == recognitionChannel) resultActions = null;
      channel.SessionId = Guid.NewGuid();
      channel.Revision = 0;
      channel.SessionInput = input;
      StateChanged?.Invoke(SessionState(channel, false, "recognition.session"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
      OperationCanceledException)
    {
      AppLog.Error("Image edit input failed", error);
      if (generation == Volatile.Read(ref channel.Generation))
        StateChanged?.Invoke(SessionState(channel, false, "recognition.inputFailed"));
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
      long generation = Interlocked.Increment(ref recognitionChannel.Generation);
      // 框选取消或失败时保留旧图、标注、结果与修订；成功捕获才替换。
      ScreenshotCaptureStarting?.Invoke();
      return PublishStartThenTrack(
        SessionState(recognitionChannel, true, "recognition.running"),
        () => CompleteScreenshotSessionAsync(generation, textSelectionRequested, scrolling,
          cancellationToken));
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
          StateChanged?.Invoke(SessionState(recognitionChannel, false,
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
              when captured.SelectionRecognitionMode is { } modeId:
              await CompleteScreenshotSelectionRecognitionAsync(
                generation, captured, modeId, cancellationToken);
              return;
            case ScreenshotSelectionAction.Recognize:
              AppLog.Warn(
                $"Screenshot selection recognition arrived without a typed mode id: {captured.SelectionRecognitionMode}");
              RecognitionVm.ReleaseInput();
              capturedInput = null;
              ReleaseCaptureGuard();
              StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.failed"));
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
              StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.cancelled"));
              ScreenshotSelectionSettingsRequested?.Invoke();
              return;
          }

          WorkbenchResourceReference input = await PublishBytesAsync(
            captured.Data,
            captured.MediaType,
            ExtensionForMediaType(captured.MediaType),
            cancellationToken);
          // PublishBytesAsync 期间取消/新截图会推进 generation：
          // 旧捕获完成不得复活已被取代的会话。
          if (generation != Volatile.Read(ref recognitionChannel.Generation))
          {
            ReleaseResource(input);
            return;
          }

          ClearScreenshotSession(recognitionChannel);
          InvalidateScreenshotTextLayer(recognitionChannel);
          resultActions = null;
          recognitionChannel.SessionId = Guid.NewGuid();
          recognitionChannel.Revision = 0;
          recognitionChannel.TextSelectionRequested = textSelectionRequested;
          recognitionChannel.SceneEditing = true;
          recognitionChannel.SessionInput = input;
          recognitionChannel.SessionResult = null;
          recognitionChannel.ExcludeBoxes = [];
          publishedSession = true;
          ReleaseCaptureGuard();
          StateChanged?.Invoke(SessionState(recognitionChannel, false, "recognition.session"));
        }
        else
        {
          // 用户在选区界面取消：不残留会话与遮罩状态；此前的文件输入保持可见。
          ReleaseCaptureGuard();
          StateChanged?.Invoke(recognitionChannel.SessionId is not null
            ? SessionState(recognitionChannel, false, "recognition.cancelled")
            : await CurrentRecognitionStateAsync("recognition.cancelled", cancellationToken));
        }
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(SessionState(recognitionChannel, false, "recognition.cancelled"));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot session capture failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(SessionState(recognitionChannel, false, "recognition.failed"));
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
        ScreenshotCaptureFinished?.Invoke();
      }
    }
  }

  /// <summary>
  /// 普通截图动作栏的识别菜单投影：只消费宿主当前已加载的真实 typed
  /// 目录（既有 choice/availability/显示名），纯截图不触发目录加载或安装；
  /// 目录未加载时投影为空，由动作栏解释并可显式进入设置。
  /// </summary>
  private ScreenshotSelectionActions BuildSelectionActions()
  {
    _ = RecognitionVm;
    // 纯截图不触发目录加载；这里只确保 settings 实例存在并复用它当前
    // 已加载的真实目录（启动 bootstrap 已加载；冷启动/维护中投影为空）。
    settings ??= CreateSettings();
    return new ScreenshotSelectionActions(
      RecognitionEngines()?
        .Select(choice => new ScreenshotRecognitionModeEntry(
          choice.Engine,
          choice.DisplayName,
          choice.Availability,
          choice.ReasonCode))
        .ToArray() ?? []);
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
    string modeId,
    CancellationToken cancellationToken)
  {
    bool terminalVisible = false;
    try
    {
      // 提交前权威目录：与单次/批量/PDF 同一契约；切换窗口返回 false 时
      // 取消本次提交（沿用本方法 cancel 终态/finally 清理），不猜默认。
      if (!await EnsureSelectionLoadedForSubmitAsync(cancellationToken))
      {
        if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
        RecognitionVm.InvalidateResult();
        resultActions = null;
        ReleaseCaptureGuard();
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.cancelled"));
        terminalVisible = true;
        return;
      }
      if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
      // 动作栏合同：仅 ready 模式可提交；目录在菜单渲染后变化（环境切换）
      // 或需要准备的模式一律拒绝，不自动触发依赖准备/下载。
      RecognitionEngineChoice? chosen = RecognitionEngines()?
        .FirstOrDefault(entry => entry.Engine == modeId);
      if (chosen is not { Availability: "ready" })
      {
        throw new RecognitionModeUnavailableException(
          $"截图识别模式 {modeId} 当前不可用（{chosen?.Availability ?? "未在目录中"}），已拒绝执行；请在设置中准备后重试。");
      }
      // typed 意图冻结为任务模式：模式缺失/不可用直接拒绝，不回退默认；
      // 新版 SynchronizeRecognitionMode 中显式 TaskEngine 优先于继承默认。
      ApplyTaskEngine(modeId);
      SynchronizeRecognitionMode(requireUsable: true);
      // 显式识别取代旧截图会话（关闭旧 scene 编辑窗），不新建编辑会话。
      ClearScreenshotSession(recognitionChannel);
      InvalidateScreenshotTextLayer(recognitionChannel);
      resultActions = null;
      RecognitionWorkbenchState state = await RunRecognitionAsync(
        viewModel => viewModel.RecognizeCapturedInputAsync(input, cancellationToken),
        generation,
        cancellationToken);
      // typed 意图在显式识别链路中的终态由调用方按代际发布。
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        StateChanged?.Invoke(state);
        terminalVisible = true;
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.cancelled"));
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
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
        terminalVisible = true;
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Screenshot selection mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
        terminalVisible = true;
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot selection recognition failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        ReleaseCaptureGuard();
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.failed"));
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
      resultActions = null;
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
        StateChanged?.Invoke(
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
    Interlocked.Increment(ref channel.Generation);
    viewModel.Cancel();
    viewModel.InvalidateResult();
    InvalidateScreenshotTextLayer(channel, previousRevision);
    if (channel == recognitionChannel)
    {
      resultActions = null;
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
    WorkbenchResourceReference frozen = await PublishBytesAsync(
      normalBytes, annotation.MediaType,
      ExtensionForMediaType(annotation.MediaType), cancellationToken);
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
        "screenshot-session-final" + ExtensionForMediaType(ocrMediaType),
        "screenshot-session");
      long generation = Interlocked.Increment(ref channel.Generation);
      Guid sessionId = command.SessionId;
      long revision = command.Revision;
      resultActions = null;
      channel.SessionResult = null;
      // 旧 input 仅在 frozen 成功接管后释放。
      if (channel.SessionInput is { } previousInput) ReleaseResource(previousInput);
      channel.SessionInput = frozen;
      adopted = true;
      channel.ExcludeBoxes = command.ExcludeBoxes;
      if (channel.SceneEditing)
      {
        // 显式识别交接：基准已冻结后再关 scene；会话与已提交任务保留，
        // 结果由主窗口识别承载面展示；失败/取消时编辑基准仍可用。
        channel.SceneEditing = false;
        ScreenshotSceneRecognitionHandoff?.Invoke(sessionId);
      }
      return PublishStartThenTrack(
        SessionState(channel, true, "recognition.running"),
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
    WorkbenchResourceReference frozen = await PublishBytesAsync(
      normalBytes, annotation.MediaType,
      ExtensionForMediaType(annotation.MediaType), cancellationToken);
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
        "image-edit-session-final" + ExtensionForMediaType(ocrMediaType),
        "image-edit");
      // 交接展示副本：未烘焙遮罩的普通像素收编为 recognition 通道当前输入
      //（识别承载面展示/后续回显），与 OCR 输入分开，不移动 imageEdit 通道。
      var displayInput = new RecognitionInput(
        normalBytes,
        annotation.MediaType,
        "image-edit-handoff" + ExtensionForMediaType(annotation.MediaType),
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
      resultActions = null;
      long generation = Interlocked.Increment(ref recognitionChannel.Generation);
      if (channel.SessionInput is { } previousInput) ReleaseResource(previousInput);
      channel.SessionInput = frozen;
      adopted = true;
      channel.ExcludeBoxes = command.ExcludeBoxes;
      StateChanged?.Invoke(RecognitionState(true, "recognition.running"));
      return PublishStartThenTrack(
        ImageEditState(false, "recognition.session"),
        () => CompleteImageEditRecognitionAsync(
          generation,
          input,
          displayInput,
          exclusions,
          cancellationToken));
    }
    finally
    {
      if (!adopted) ReleaseResource(frozen);
    }
  }

  private async Task CompleteImageEditRecognitionAsync(
    long generation,
    RecognitionInput input,
    RecognitionInput displayInput,
    IReadOnlyList<WorkbenchExclusionBox> exclusions,
    CancellationToken cancellationToken)
  {
    try
    {
      // 提交前取得权威目录并严格协商可用模式（与单次/批量/PDF 同一契约）；
      // 环境切换窗口取消本次提交，不以空快照提交。
      if (!await EnsureSelectionLoadedForSubmitAsync(cancellationToken))
      {
        if (generation != Volatile.Read(ref recognitionChannel.Generation)) return;
        RecognitionVm.InvalidateResult();
        resultActions = null;
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.cancelled"));
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
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.cancelled"));
      }
    }
    catch (RecognitionModeUnavailableException error)
    {
      AppLog.Warn($"Image edit recognition mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Image edit recognition selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Image edit recognition handoff failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.failed"));
      }
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
      if (generation != Volatile.Read(ref recognitionChannel.Generation))
      {
        return;
      }
      if (selectionUnavailable)
      {
        RecognitionVm.InvalidateResult();
        resultActions = null;
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.cancelled"));
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
        if (viewModel.Result is not null)
        {
          resultActions = viewModel.CreateResultActions(
            new WindowsResultActionPlatform(windowHandle));
        }
        // 完成结果即使文本为空也发布真实（可能为空的）文本资源：会话终态
        // 可见性（SessionStatusCode 与 StructuredResult 门控）绑定
        // 通道 SessionResult，空文本不得伪装成仍在会话编辑中（#110）。
        WorkbenchResourceReference? result = viewModel.Result is null
          ? null
          : await PublishBytesAsync(
            Encoding.UTF8.GetBytes(viewModel.ResultText),
            "text/plain; charset=utf-8",
            ".txt",
            cancellationToken);
        WorkbenchResourceReference? structured = await PublishStructuredResultAsync(
          viewModel, cancellationToken);
        // 结果资源发布期间编辑/换图/维护会推进 generation：丢弃迟到结果。
        if (generation != Volatile.Read(ref recognitionChannel.Generation) ||
          recognitionChannel.SessionId != sessionId ||
          recognitionChannel.Revision != revision)
        {
          return;
        }
        recognitionChannel.SessionResult = result;
        recognitionChannel.SessionStructuredResult = structured;
        StateChanged?.Invoke(SessionRecognitionState(
          false,
          RecognitionStatusCode(viewModel)));
      }
      else
      {
        // 会话或内容修订已变更：迟到结果丢弃，不覆盖新会话。
        viewModel.InvalidateResult();
        resultActions = null;
        recognitionChannel.SessionResult = null;
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.expired"));
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.cancelled"));
      }
    }
    catch (RecognitionModeUnavailableException error)
    {
      AppLog.Warn($"Screenshot recognition mode refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Screenshot recognition selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Screenshot session recognition failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(SessionRecognitionState(false, "recognition.failed"));
      }
    }
  }

  private WorkbenchState CloseScreenshotSession(ImageSessionChannel channel)
  {
    RecognitionViewModel viewModel = channel.ViewModel ??= recognitionFactory();
    Interlocked.Increment(ref channel.Generation);
    viewModel.Cancel();
    viewModel.InvalidateResult();
    viewModel.ReleaseInput();
    if (channel == recognitionChannel) resultActions = null;
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
    ImageSessionChannel channel,
    IScreenshotSessionResource command,
    CancellationToken cancellationToken,
    byte[]? pinnedPng = null, string pinnedMediaType = "image/png")
  {
    _ = channel.ViewModel ??= recognitionFactory();
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    string sessionId = command.SessionId.ToString("N");
    if (channel.TextLayer?.Binding is { } existing &&
      existing.SessionId == sessionId &&
      existing.Revision == command.Revision &&
      channel.TextLayer.Status is "textlayer.preparing" or "textlayer.ready")
    {
      return SessionState(channel, false, SessionStatusCode(channel));
    }

    if (inferenceAttached?.Invoke() == false)
    {
      channel.TextLayer = new RecognitionTextLayerState(
        "textlayer.unavailable", "textlayer.serviceUnavailable",
        new RecognitionScreenshotSessionState(sessionId, command.Revision),
        null, supervisorInstanceId(), null);
      return SessionState(channel, false, SessionStatusCode(channel));
    }
    await EnsureSelectionLoadedAsync(cancellationToken);
    RecognitionModeOption? mode = FindReadyLocalTextMode(out string? reason);
    if (mode is null)
    {
      channel.TextLayer = new RecognitionTextLayerState(
        "textlayer.unavailable",
        reason,
        new RecognitionScreenshotSessionState(sessionId, command.Revision),
        null,
        supervisorInstanceId(),
        null);
      return SessionState(channel, false, SessionStatusCode(channel));
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
    ValidateScreenshotSession(channel, command.SessionId, command.Revision);
    var input = new RecognitionInput(
      png,
      mediaType,
      "screenshot-text-layer" + ExtensionForMediaType(mediaType),
      "screenshot-text");
    string serviceInstance = supervisorInstanceId() ?? string.Empty;
    long generation = Interlocked.Increment(ref channel.TextGeneration);
    Guid sessionGuid = command.SessionId;
    long revision = command.Revision;
    channel.TextLayer = new RecognitionTextLayerState(
      "textlayer.preparing",
      null,
      new RecognitionScreenshotSessionState(sessionId, revision),
      mode.Id,
      serviceInstance,
      null);
    return PublishStartThenTrack(
      SessionState(channel, false, SessionStatusCode(channel)),
      () => CompleteScreenshotTextLayerAsync(
        channel,
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
      bool deferredSelection = !await EnsureSelectionLoadedAsync(cancellationToken);
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
      WorkbenchResourceReference image = await PublishBytesAsync(
        input.Data,
        input.MediaType,
        ExtensionForMediaType(input.MediaType),
        cancellationToken);
      if (generation != Volatile.Read(ref channel.TextGeneration) ||
        channel.SessionId != sessionId ||
        channel.Revision != revision ||
        (supervisorInstanceId() ?? string.Empty) != serviceInstance)
      {
        ReleaseResource(image);
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
      StateChanged?.Invoke(SessionState(channel, false, SessionStatusCode(channel)));
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
    if (channel.TextLayer?.Image is { } image) ReleaseResource(image);
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
    StateChanged?.Invoke(SessionState(channel, false, SessionStatusCode(channel)));
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

  private void ClearScreenshotSession(ImageSessionChannel channel)
  {
    if (channel.SessionId is { } id)
      ScreenshotSessionDetached?.Invoke(id, channel.Revision);
    if (channel.SessionInput is { } input)
      ReleaseResource(input);
    if (channel.SessionResult is { } result) ReleaseResource(result);
    if (channel.SessionStructuredResult is { } structured) ReleaseResource(structured);
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
      long generation = Interlocked.Increment(ref recognitionChannel.Generation);
      // 新输入（文件/剪贴板/拖入/即时截图识别）取代 recognition 通道的
      // 截图会话：旧会话命令立即失效，避免旧图混入新输入。imageEdit
      // 通道的会话与输入不受影响（两页独立）。
      ClearScreenshotSession(recognitionChannel);
      InvalidateScreenshotTextLayer(recognitionChannel);
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
      recognitionChannel.ViewModel?.TaskEngine,
      CurrentScreenshotSession(recognitionChannel),
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
    RecognitionViewModel viewModel = RecognitionVm;
    if (selection.SupportsRecognitionModes)
    {
      string? task = viewModel.TaskEngine;
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
    bool isOverride = viewModel.TaskEngine is not null;
    return [.. selection.EngineOptions.Select(option => new RecognitionEngineChoice(
      OcrEngineWire.Format(option.Engine),
      SettingsViewModel.DisplayName(option.Engine),
      isOverride && OcrEngineWire.Format(option.Engine) == viewModel.TaskEngine,
      isOverride &&
        OcrEngineWire.Format(option.Engine) == viewModel.TaskEngine,
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
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
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
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (RuntimeSelectionException error)
    {
      AppLog.Warn($"Recognition mode selection refused: {error.Message}");
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
      {
        StateChanged?.Invoke(ModeUnavailableRecognitionState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Recognition workbench operation failed", error);
      if (generation == Volatile.Read(ref recognitionChannel.Generation))
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
    WorkbenchResourceReference? input = null;
    if (viewModel.CurrentInput is { } currentInput)
    {
      input = await PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        ExtensionForMediaType(currentInput.MediaType),
        cancellationToken);
    }
    // 完成结果即使文本为空也保留真实（可能为空的）文本资源：Result 非空
    // 合同绑定真实识别结果，而不是文本长度（#110）。
    WorkbenchResourceReference? result = viewModel.Result is null
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(viewModel.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    WorkbenchResourceReference? structured = await PublishStructuredResultAsync(
      viewModel, cancellationToken);
    if (generation != Volatile.Read(ref recognitionChannel.Generation))
    {
      // 本次运行已被取代（取消/新输入/新交接）：本次发布资源确定性回收，
      // 不改共享 resultActions，也不把旧资源写回新状态。
      if (input is not null) ReleaseResource(input);
      if (result is not null) ReleaseResource(result);
      if (structured is not null) ReleaseResource(structured);
      return RecognitionState(
        viewModel.IsBusy,
        RecognitionStatusCode(viewModel));
    }
    if (viewModel.Result is not null)
    {
      resultActions = viewModel.CreateResultActions(
        new WindowsResultActionPlatform(windowHandle));
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
    return SessionRecognitionState(state.IsBusy, state.StatusCode);
  }

  private RecognitionWorkbenchState CancelRecognition()
  {
    RecognitionViewModel viewModel = RecognitionVm;
    Interlocked.Increment(ref recognitionChannel.Generation);
    viewModel.Cancel();
    if (recognitionChannel.SessionId is not null)
    {
      // 取消保留会话编辑基准；运行中结果与文字层准备丢弃后可重新触发。
      viewModel.InvalidateResult();
      resultActions = null;
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
    if (recognitionChannel.SessionId is not null)
    {
      // 会话模式复用缓存资源：重发布新 URL 会重置编辑器历史。
      return SessionRecognitionState(false, statusCode);
    }
    RecognitionViewModel viewModel = recognitionChannel.ViewModel ??
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
    RecognitionViewModel viewModel = RecognitionVm;
    SynchronizeRecognitionMode();
    RecognitionWorkbenchState current = await CurrentRecognitionStateAsync(
      "recognition.ready", cancellationToken);
    bool isBusy = viewModel.IsBusy;
    StateChanged?.Invoke(current with
    {
      StatusCode = RecognitionStatusCode(viewModel, isBusy),
      IsBusy = isBusy,
      Engines = RecognitionEngines(),
      TaskEngine = viewModel.TaskEngine,
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
  /// <summary>
  /// 任务级模式/引擎严格 resolve：不可用即抛出，不回退默认；普通截图
  /// 动作栏的显式 typed 意图与设置页任务模式选择共用同一语义。
  /// </summary>
  private void ApplyTaskEngine(string? engine)
  {
    settings ??= CreateSettings();
    RuntimeSelectionService? selection = settings.RecognitionSelection?.Catalog;
    if (string.IsNullOrWhiteSpace(engine)) RecognitionVm.TaskEngine = null;
    else if (selection?.SupportsRecognitionModes is true)
    {
      RecognitionModeOption mode = selection.SelectRecognitionMode(engine);
      selection.MineruConfigFor(mode.Id);
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
    RecognitionSelectionSnapshot? snapshot = settings?.RecognitionSelection;
    RecognitionViewModel? viewModel = recognitionChannel.ViewModel;
    if (viewModel is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      if (requireUsable) EnsureUsableModeOverrideOrThrow(snapshot?.Catalog, viewModel?.TaskEngine);
      viewModel?.SetRecognitionMode(null);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = EffectiveModeIdOrDefault(
      snapshot, viewModel.TaskEngine, requireUsable);
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
    viewModel.SetRecognitionMode(mode, config, GetModeOptions(mode));
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
    return await settings.LoadSelectionAsync(cancellationToken);
  }

  /// <summary>
  /// 提交路径的目录加载：Supervisor 未 attach 时等待启动完成（Deferred
  /// 网关内建等待）取得权威目录与已提交默认后才冻结提交参数；读取被
  /// 代际失效丢弃或仍处切换窗口时返回 false，由调用方取消提交而非
  /// 猜默认（不重试）。
  /// </summary>
  private async Task<bool> EnsureSelectionLoadedForSubmitAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (Volatile.Read(ref environmentSwitching) != 0)
      return false;
    return await settings.LoadSelectionAsync(cancellationToken) &&
      Volatile.Read(ref environmentSwitching) == 0;
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
  /// 不得覆盖当前会话。两个通道各自失效，互不牵连；只接现有维护事件，
  /// 不接管环境状态机。
  /// </summary>
  private void InvalidateScreenshotSessionRecognitionOnMaintenance()
  {
    if (settings?.Maintenance.State.IsRunning != true)
    {
      return;
    }
    foreach (ImageSessionChannel channel in (ImageSessionChannel[])
      [recognitionChannel, imageEditChannel])
    {
      if (channel.SessionId is null ||
        (channel.ViewModel is not { IsBusy: true } &&
          channel.TextLayer is null && channel.SessionResult is null))
      {
        continue;
      }
      Interlocked.Increment(ref channel.Generation);
      channel.ViewModel?.Cancel();
      channel.ViewModel?.InvalidateResult();
      if (channel == recognitionChannel) resultActions = null;
      channel.SessionResult = null;
      InvalidateScreenshotTextLayer(channel);
      StateChanged?.Invoke(SessionState(channel, false, "recognition.expired"));
    }
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
    Interlocked.Increment(ref recognitionChannel.Generation);
    Interlocked.Increment(ref imageEditChannel.Generation);
    Interlocked.Increment(ref batchGeneration);
    Interlocked.Increment(ref pdfGeneration);
    Interlocked.Increment(ref qrCodeGeneration);
    Interlocked.Increment(ref updateGeneration);
    recognitionChannel.ViewModel?.Cancel();
    imageEditChannel.ViewModel?.Cancel();
    recognitionChannel.TextLayerViewModel?.Cancel();
    imageEditChannel.TextLayerViewModel?.Cancel();
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
