using VibeOCR.App.Features.Batch;
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

namespace VibeOCR.App.Workbench;

public sealed class DesktopWorkbenchCommandHandler :
  IWorkbenchCommandHandler,
  IWorkbenchStateSource,
  IWorkbenchBootstrapSource,
  IAsyncDisposable
{
  internal WorkbenchSelectionOwner Selection { get; }
  internal WorkbenchResourcesOwner Resources { get; }
  internal WorkbenchPdfOwner Pdf { get; }
  internal WorkbenchImageOwner Image { get; }
  internal WorkbenchBatchOwner Batch { get; }
  internal WorkbenchSettingsOwner Settings { get; }
  internal WorkbenchQrCodeOwner QrCode { get; }
  internal WorkbenchSystemOwner System { get; }

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

  private readonly WorkbenchOperations operations = new();

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
    IStructuredClipboardPlatform? structuredClipboard = null,
    Func<PdfDocumentEntry, Task<PdfCloseDecision>>? confirmPdfClose = null,
    Func<Task<string?>>? pickPdfSavePath = null, Func<Task<string?>>? pickPdfFolder = null,
    Func<string, Task<bool>>? flushPdfPreview = null,
    Func<CancellationToken, Task>? ensureRuntimeReady = null,
    IResultActionPlatform? resultActionPlatform = null,
    ISystemImportReader? systemImportReader = null,
    VibeOCR.App.Web.WorkbenchProjectStore? projectStore = null,
    Func<CancellationToken, Task<string?>>? projectSavePicker = null,
    Func<CancellationToken, Task<string?>>? projectOpenPicker = null,
    IPinSnapshotWriter? pinSnapshotWriter = null,
    IPinHistoryRecorder? pinHistory = null)
  {

    recognitionFactory = recognitionFactory ??
      throw new ArgumentNullException(nameof(recognitionFactory));
    batchFactory = batchFactory ?? throw new ArgumentNullException(nameof(batchFactory));
    qrCodeFactory = qrCodeFactory ??
      throw new ArgumentNullException(nameof(qrCodeFactory));
    pdfFactory = pdfFactory ?? throw new ArgumentNullException(nameof(pdfFactory));
    settingsFactory = settingsFactory ??
      throw new ArgumentNullException(nameof(settingsFactory));
    ArgumentNullException.ThrowIfNull(shellFactory);
    ArgumentNullException.ThrowIfNull(updateFactory);
    var shell = new Lazy<ShellViewModel>(shellFactory);
    diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    resourceBroker = resourceBroker ??
      throw new ArgumentNullException(nameof(resourceBroker));
    resourceRoot = Path.GetFullPath(resourceRoot);
    windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
    annotationStore = annotationStore ??
      throw new ArgumentNullException(nameof(annotationStore));
    annotatedImagePlatform = annotatedImagePlatform ??
      new AnnotatedImagePlatform(windowHandle);
    structuredClipboard = structuredClipboard ??
      new WindowsStructuredClipboardPlatform();
    resultActionPlatform ??= new WindowsResultActionPlatform(windowHandle);
    supervisorInstanceId = supervisorInstanceId ?? (() => null);
    textLayerRecognitionFactory = textLayerRecognitionFactory ?? recognitionFactory;
    confirmPdfClose = confirmPdfClose ?? (_ => Task.FromResult(PdfCloseDecision.Cancel));

    Resources = new WorkbenchResourcesOwner(resourceBroker, resourceRoot);
    Settings = new WorkbenchSettingsOwner(ensureRuntimeReady, inferenceAttached, settingsFactory, shell, shellActions, supervisorInstanceId, operations, RefreshFeatureCatalogStatesAsync, InvalidateImageRecognition, optionsLayout, windowHandle);
    Selection = new WorkbenchSelectionOwner(ensureRuntimeReady, inferenceAttached, optionsLayout, Settings, GetRecognitionTaskEngine);
    Image = new WorkbenchImageOwner(annotatedImagePlatform, annotationStore, ensureRuntimeReady, inferenceAttached, optionsLayout, pinScreenshot, recognitionFactory, resourceBroker, shellActions, structuredClipboard, supervisorInstanceId, textLayerRecognitionFactory, resultActionPlatform, Settings, Resources, Selection, operations, SynchronizeBatchMode, systemImportReader, projectStore, projectSavePicker, projectOpenPicker, pinSnapshotWriter, pinHistory);
    // 工程打开事务终止（取消/超时/替换）经既有状态邮箱发布终态，不触碰 UI 线程。
    Image.ProjectOpenTerminated += Image.OnProjectOpenTerminated;
    Batch = new WorkbenchBatchOwner(batchFactory, windowHandle, Settings, Resources, Selection, operations, annotationStore);
    Pdf = new WorkbenchPdfOwner(confirmPdfClose, flushPdfPreview, optionsLayout, pdfFactory, pickPdfFolder, pickPdfSavePath, windowHandle, Settings, Resources, Selection, operations);
    QrCode = new WorkbenchQrCodeOwner(annotatedImagePlatform, qrCodeFactory, windowHandle, Resources, operations);
    System = new WorkbenchSystemOwner(diagnostics, resourceRoot, shell, structuredClipboard, updateFactory, operations);
    diagnostics.PropertyChanged += System.OnDiagnosticsPropertyChanged;
    if (shellActions is not null) shellActions.ToolbarStateChanged += Settings.OnToolbarStateChanged;

  }

  public IReadOnlyList<WorkbenchState> InitialStates =>
  [
    Image.RecognitionState(false, "recognition.ready"),
    Image.ImageEditState(false, "recognition.ready"),
    Image.PinEditState(false, "pinEdit.ready"),
    Batch.CurrentBatchState(),
    Pdf.EmptyPdfState(),
    new QrCodeWorkbenchState(false, "qrcode.ready", [], null),
    Settings.CurrentState(),
    new UpdateWorkbenchState(false, "update.current", null, false),
    System.AboutState(),
    System.DiagnosticsState(),
  ];

  public event Action<WorkbenchState>? StateChanged { add => operations.StateChanged += value; remove => operations.StateChanged -= value; }
  public event Action<RecognitionTextLayerState?>? ScreenshotTextLayerChanged { add => Image.ScreenshotTextLayerChanged += value; remove => Image.ScreenshotTextLayerChanged -= value; }
  public event Action<Guid, long>? ScreenshotTextLayerInvalidated { add => Image.ScreenshotTextLayerInvalidated += value; remove => Image.ScreenshotTextLayerInvalidated -= value; }
  public event Action<Guid, long>? ScreenshotSessionDetached { add => Image.ScreenshotSessionDetached += value; remove => Image.ScreenshotSessionDetached -= value; }
  public event Action? PinnedTextEnvironmentChanged { add => Settings.PinnedTextEnvironmentChanged += value; remove => Settings.PinnedTextEnvironmentChanged -= value; }
  public event Action<Guid, VibeOCR.Platform.Windows.PhysicalRectangle?>? ScreenshotSessionReady { add => Image.ScreenshotSessionReady += value; remove => Image.ScreenshotSessionReady -= value; }
  internal event Action? ScreenshotCaptureStarting { add => Image.ScreenshotCaptureStarting += value; remove => Image.ScreenshotCaptureStarting -= value; }
  internal event Action? ScreenshotCaptureFinished { add => Image.ScreenshotCaptureFinished += value; remove => Image.ScreenshotCaptureFinished -= value; }
  /// <summary>scene 会话显式提交识别后的内部交接：宿主关闭 scene 编辑窗并导航主窗口；会话与任务保留。</summary>
  internal event Action<Guid>? ScreenshotSceneRecognitionHandoff { add => Image.ScreenshotSceneRecognitionHandoff += value; remove => Image.ScreenshotSceneRecognitionHandoff -= value; }
  /// <summary>
  /// 普通截图动作栏显式识别终态：宿主显示主窗并导航识别承载面（明确
  /// 分支）。仅识别任务终态允许触发；本地输出失败/取消不借用。
  /// </summary>
  internal event Action? ScreenshotSelectionRecognized { add => Image.ScreenshotSelectionRecognized += value; remove => Image.ScreenshotSelectionRecognized -= value; }
  /// <summary>普通截图动作栏“打开设置”意图：放弃本次截图并显式进入现有设置页。</summary>
  internal event Action? ScreenshotSelectionSettingsRequested { add => Image.ScreenshotSelectionSettingsRequested += value; remove => Image.ScreenshotSelectionSettingsRequested -= value; }
  public async ValueTask PrepareBootstrapAsync(CancellationToken cancellationToken)
  {
    try
    {
      await Selection.EnsureSelectionLoadedAsync(cancellationToken);
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
    PdfDocumentEntry? previousContext = Pdf.CaptureContext();
    // 入口门在既有 try/catch 错误边界内执行：预期操作冲突（关闭/切换/修订/
    // 退出确认）按既有 WorkbenchCommandOutcome 回执拒绝，不再穿透桥接层
    // 触发全局 WebView 恢复；finally 同步恢复 pdfContext。
    try
    {
      command = Pdf.BindCommand(command);
      cancellationToken.ThrowIfCancellationRequested();
      WorkbenchState? state = command switch
      {
        _ when Image.Handles(command) => await Image.ExecuteAsync(command, cancellationToken),
        _ when Batch.Handles(command) => await Batch.ExecuteAsync(command, cancellationToken),
        _ when Pdf.Handles(command) => await Pdf.ExecuteAsync(command, cancellationToken),
        _ when QrCode.Handles(command) => await QrCode.ExecuteAsync(command, cancellationToken),
        _ when System.Handles(command) => await System.ExecuteAsync(command, cancellationToken),
        _ when Settings.Handles(command) => await Settings.ExecuteAsync(command, cancellationToken),
        _ => throw new InvalidOperationException("Unsupported desktop workbench command."),
      };
      state = Pdf.NormalizeOutcome(state);
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
    }
    catch (WorkbenchImageOwner.AnnotatedImageOperationCancelledException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "annotation_operation_cancelled",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.annotationOperationCancelled"));
    }
    catch (WorkbenchImageOwner.ScreenshotSessionStaleException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "screenshot_session_stale",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.screenshotSessionStale"));
    }
    catch (PinLimitReachedException)
    {
      // #238 C6：4 张贴图上限专属回执文案（宿主仍防竞态，预检只是前置 UX）。
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "pin_limit_reached",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.pinLimitReached"));
    }
    catch (WorkbenchImageOwner.CaptureInProgressException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "capture_in_progress",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.captureInProgress"));
    }
    catch (WorkbenchImageOwner.ImageImportInProgressException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "image_import_in_progress",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.imageImportInProgress"));
    }
    catch (WorkbenchImageOwner.ImageImportRefusedException error)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "image_import_refused",
          WorkbenchProblemCategory.InvalidCommand,
          false,
          error.MessageKey));
    }
    catch (WorkbenchProjectOpenConflictException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "project_open_conflict",
          WorkbenchProblemCategory.Conflict,
          false,
          "workbench.error.screenshotSessionStale"));
    }
    catch (VibpProjectFormatException)
    {
      // 损坏/版本/资源/解压超限：明确拒绝且不触发全局 WebView 恢复。
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "project_format_invalid",
          WorkbenchProblemCategory.InvalidCommand,
          false,
          "workbench.error.desktopCommandFailed"));
    }
    catch (SystemImportRejectException error)
    {
      // 同步读取阶段即被拒绝（超限/坏格式/无内容）：明确回执，不触碰画布。
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "image_import_refused",
          WorkbenchProblemCategory.InvalidCommand,
          false,
          error.Reason switch
          {
            "oversize" => "workbench.error.imageImportOversize",
            "invalid" => "workbench.error.imageImportInvalid",
            "batch" => "workbench.error.imageImportRefused",
            "empty" => "workbench.error.imageImportEmpty",
            _ => "workbench.error.imageImportRefused",
          }));
    }
    catch (System.Runtime.InteropServices.COMException) when (command is
      PasteImageEditSystemContentCommand or PasteScreenshotSystemContentCommand or
      ImportDroppedImageEditContentCommand or ImportDroppedScreenshotContentCommand)
    {
      return new WorkbenchCommandOutcome([], new WorkbenchProblem(
        "image_import_refused", WorkbenchProblemCategory.Unavailable,
        false, "workbench.error.imageImportInvalid"));
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
      command is (SavePresetImageCommand or PrepareBatchImageCommand or ExportBatchImageCommand or PickImageOutputDirectoryCommand or SetEditorPreferencesCommand) &&
      error is (InvalidDataException or global::System.Text.Json.JsonException or global::System.Runtime.InteropServices.COMException))
    {
      return new WorkbenchCommandOutcome([], new WorkbenchProblem(
        "image_output_refused", WorkbenchProblemCategory.InvalidCommand,
        true, "workbench.error.desktopCommandFailed"));
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
    finally { Pdf.RestoreContext(previousContext); }
  }

  internal static string Truncate(string value, int maximumLength) =>
    value.Length <= maximumLength
      ? value
      : string.Concat(value.AsSpan(0, maximumLength - 1), "…");

  internal static int ClampWindowStart(int requested, int count, int windowSize)
  {
    if (count <= 0) return 0;
    int maximumStart = ((count - 1) / windowSize) * windowSize;
    return Math.Clamp(requested, 0, maximumStart);
  }

  public async ValueTask DisposeAsync()
  {
    if (!operations.BeginDispose()) return;
    Image.Stop(); Batch.Stop(); Pdf.Stop(); QrCode.Stop(); Settings.Stop(); System.Stop();
    await operations.DrainAsync().ConfigureAwait(false);
    Resources.Release(); Batch.Release(); Pdf.Release(); operations.Release();
  }

  private void InvalidateImageRecognition() => Image.InvalidateScreenshotSessionRecognitionOnMaintenance();
  private string? GetRecognitionTaskEngine() => Image.TaskEngine;
  private void SynchronizeBatchMode() => Batch.SynchronizeBatchMode();

  private async Task RefreshFeatureCatalogStatesAsync(CancellationToken cancellationToken)
  {
    await Image.RefreshCatalogStateAsync(cancellationToken);
    operations.Publish(Batch.CurrentBatchState());
    await Pdf.RefreshCatalogStateAsync(cancellationToken);
  }

  public IReadOnlyList<PdfDocumentEntry> PdfDocuments => Pdf.PdfDocuments;
  internal Guid? CurrentImageSessionId => Image.CurrentImageSessionId;
  /// <summary>当前 pending 工程打开描述符（接线测试/Web 阶段状态投影消费）。</summary>
  internal WorkbenchImageOwner.ProjectOpenDescriptor? CurrentProjectOpen => Image.CurrentProjectOpen;
  internal ScreenshotCaptureScene? PendingScreenshotCaptureScene => Image.PendingScreenshotCaptureScene;
  internal ScreenshotCaptureScene? TakeScreenshotCaptureScene(Guid sessionId) => Image.TakeScreenshotCaptureScene(sessionId);
  public Task<RecognitionTextLayerState?> PreparePinnedTextLayerAsync(Guid sessionId, long revision, string imagePath, CancellationToken cancellationToken = default) => Image.PreparePinnedTextLayerAsync(sessionId, revision, imagePath, cancellationToken);
  /// <summary>#238 pinEdit：单编辑槽会话建立与宿主事件转发。</summary>
  internal Task<Guid?> TryOpenPinEditSessionAsync(Guid pinId, byte[] png, CancellationToken cancellationToken = default,
    IReadOnlyList<WorkbenchExclusionBox>? excludeBoxes = null) => Image.TryOpenPinEditSessionAsync(pinId, png, cancellationToken, excludeBoxes);
  internal void PublishPinHistory() => Image.PublishPinHistory();
  internal event Action<Guid, long, Guid>? PinSnapshotApplied
  {
    add => Image.PinSnapshotApplied += value;
    remove => Image.PinSnapshotApplied -= value;
  }
  internal event Action<Guid>? PinEditSessionClosed
  {
    add => Image.PinEditSessionClosed += value;
    remove => Image.PinEditSessionClosed -= value;
  }
  internal event Action? PinManagerRequested
  { add => Image.PinManagerRequested += value; remove => Image.PinManagerRequested -= value; }

  internal event Func<PinHistoryImage, Task>? PinHistoryReopenRequested
  {
    add => Image.PinHistoryReopenRequested += value;
    remove => Image.PinHistoryReopenRequested -= value;
  }
  internal static IReadOnlyList<ScreenshotRecognitionModeEntry> BuildSelectionModeEntries(RecognitionSelectionSnapshot? selection) => WorkbenchImageOwner.BuildSelectionModeEntries(selection);
  internal static string? EffectiveModeIdOrDefault(RecognitionSelectionSnapshot? snapshot, string? taskEngine, bool requireUsable) => WorkbenchSelectionOwner.EffectiveModeIdOrDefault(snapshot, taskEngine, requireUsable);
  public Task<bool> RequestCloseAllPdfAsync() => Pdf.RequestCloseAllPdfAsync();
  internal void EndHotkeyRecording() => Settings.EndHotkeyRecording();
  internal Task RefreshRecognitionCatalogAsync(CancellationToken cancellationToken) => Settings.RefreshRecognitionCatalogAsync(cancellationToken);
}


internal sealed class PinnedTextPreparationException(string message) : Exception(message);

/// <summary>#238 4 张贴图同时上限：专属 problem（C6 用户可见文案）。</summary>
internal sealed class PinLimitReachedException : InvalidOperationException
{
  public PinLimitReachedException() : base("最多同时打开四张贴图，请先关闭一张。") { }
}
