namespace VibeOCR.App.Workbench;

public static class WorkbenchProtocol
{
  public const int Version = 2;
}

public enum WorkbenchRoute
{
  Recognition,
  Batch,
  QrCode,
  Pdf,
  Settings,
  About,
  Diagnostics,
}

public enum WorkbenchStateChange
{
  Replace,
  Remove,
  Reset,
  Ready,
}

public enum WorkbenchProblemCategory
{
  InvalidCommand,
  Unavailable,
  Conflict,
  Internal,
}

public abstract record WorkbenchCommand;

public sealed record NavigateWorkbenchCommand(WorkbenchRoute Route) : WorkbenchCommand;

public sealed record CaptureRecognitionScreenCommand : WorkbenchCommand;

/// <summary>
/// Pure screenshot capture: open the region picker, publish the captured
/// pixels as a local editing session, and submit no OCR request. Recognition
/// happens only through an explicit <see cref="RecognizeScreenshotImageCommand"/>
/// carrying the session's current revision.
/// </summary>
public sealed record CaptureScreenshotSessionCommand : WorkbenchCommand;

public sealed record CloseScreenshotSessionCommand : WorkbenchCommand;

/// <summary>
/// Editor-side content revision for the active screenshot session. Every
/// commit/undo/redo publishes a strictly increasing revision; the host drops
/// the previous OCR result association so late responses cannot pollute the
/// new content.
/// </summary>
public sealed record NotifyScreenshotSessionRevisionCommand(
    Guid SessionId,
    long Revision) : WorkbenchCommand;

public sealed record CopyScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand;

public sealed record SaveScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand;

/// <summary>
/// Explicit recognition of the session's current final PNG (uploaded through
/// the opaque annotation lease). Never falls back to the unedited capture.
/// </summary>
public sealed record RecognizeScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand;

public sealed record SelectRecognitionImageCommand : WorkbenchCommand;

public sealed record RecognizeDroppedFileCommand(string Path) : WorkbenchCommand;

public sealed record ReadRecognitionClipboardCommand : WorkbenchCommand;

public sealed record CancelRecognitionCommand : WorkbenchCommand;

public sealed record CopyRecognitionResultCommand(string Format) : WorkbenchCommand;

public sealed record ExportRecognitionResultCommand(string Format) : WorkbenchCommand;

public sealed record CopyAnnotatedImageCommand(string ResourceUri) : WorkbenchCommand;

public sealed record SaveAnnotatedImageCommand(string ResourceUri) : WorkbenchCommand;

public sealed record AddBatchFilesCommand : WorkbenchCommand;

public sealed record AddDroppedBatchFilesCommand(
  IReadOnlyList<string> Paths) : WorkbenchCommand;

public sealed record ExportBatchCommand(string Format) : WorkbenchCommand;

public sealed record StartBatchCommand : WorkbenchCommand;

public sealed record CancelBatchCommand : WorkbenchCommand;

public sealed record ClearBatchCommand : WorkbenchCommand;

public sealed record MoveBatchItemCommand(Guid ItemId, int Delta) : WorkbenchCommand;

public sealed record RemoveBatchItemCommand(Guid ItemId) : WorkbenchCommand;

public sealed record SetBatchWindowCommand(int Start) : WorkbenchCommand;

public sealed record SetBatchTaskEngineCommand(string? Engine) : WorkbenchCommand;

public sealed record OpenPdfCommand : WorkbenchCommand;

public sealed record OpenDroppedPdfCommand(string Path) : WorkbenchCommand;

public sealed record RotatePdfCommand(int Degrees = 90) : WorkbenchCommand;

public sealed record ClosePdfCommand : WorkbenchCommand;

public sealed record DeletePdfPagesCommand : WorkbenchCommand;

public sealed record OcrPdfPagesCommand : WorkbenchCommand;

public sealed record SavePdfCommand : WorkbenchCommand;

public sealed record SelectPdfPagesCommand(IReadOnlyList<int> Pages) : WorkbenchCommand;

public sealed record SetPdfWindowCommand(int Start) : WorkbenchCommand;

public sealed record GenerateQrCodeCommand(string Text) : WorkbenchCommand;

public sealed record DecodeQrCodeCommand : WorkbenchCommand;

public sealed record DecodeDroppedQrCodeCommand(string Path) : WorkbenchCommand;

public sealed record DecodeQrCodeClipboardCommand : WorkbenchCommand;

public sealed record CancelQrCodeCommand : WorkbenchCommand;

public sealed record ClearQrCodeCommand : WorkbenchCommand;

public sealed record SaveQrCodeCommand : WorkbenchCommand;

public sealed record OpenQrCodeUrlCommand(string Url) : WorkbenchCommand;

public sealed record OpenProjectPageCommand : WorkbenchCommand;

public sealed record RefreshRuntimeCommand : WorkbenchCommand;

public sealed record CreateEnvironmentCommand(string Name) : WorkbenchCommand;
public sealed record PreviewEnvironmentInstallCommand(string EnvironmentId, string Recipe, string SourceId) : WorkbenchCommand;
public sealed record ConfirmEnvironmentInstallCommand(string PlanId, string SourceId) : WorkbenchCommand;
public sealed record CancelEnvironmentInstallCommand : WorkbenchCommand;
public sealed record InvalidateEnvironmentPlanCommand : WorkbenchCommand;
public sealed record SwitchEnvironmentCommand(string EnvironmentId) : WorkbenchCommand;
public sealed record DeleteEnvironmentCommand(string EnvironmentId) : WorkbenchCommand;
public sealed record RepairEmptyEnvironmentCommand(string EnvironmentId) : WorkbenchCommand;

public sealed record SetThemeCommand(WorkbenchTheme Theme) : WorkbenchCommand;

public sealed record SetStartupCommand(bool Enabled) : WorkbenchCommand;

public sealed record SetHotkeyCommand(string Hotkey) : WorkbenchCommand;

/// <summary>
/// Select the package-index dependency source. A null source id clears the
/// selection and delegates to Backend defaults; other wire kinds are not editable.
/// </summary>
public sealed record SetDownloadSourceCommand(string Kind, string? SourceId) : WorkbenchCommand;

/// <summary>Stage the accelerator for pending feature selection.</summary>
public sealed record SetAcceleratorCommand(string Accelerator) : WorkbenchCommand;

/// <summary>Toggle one optional feature for the pending accelerator.</summary>
public sealed record SetRuntimeFeatureCommand(string FeatureId, bool Enabled) : WorkbenchCommand;

/// <summary>
/// Set the MinerU connection preference in Backend settings
/// extra.mineru_connection. Remote requires the runtime capability
/// ocr.mineru-remote-api.v1; local persists only the mode. ApiKey is
/// three-state: null keeps the stored key (the host merges it back from the
/// current settings), an empty string clears it explicitly, and a non-empty
/// string replaces it. Saving never verifies remote reachability, and the
/// API key never crosses back to the WebView beyond a has-key flag.
/// </summary>
public sealed record SetMineruConnectionCommand(
    string Mode,
    string? ApiUrl,
    string? ApiKey) : WorkbenchCommand;

/// <summary>
/// Verify and prepare the configured remote MinerU service by executing the
/// real runtime preload (pipelines=['MinerU'], recognition_modes=
/// ['mineru_document']) through the Backend, then refreshing the health/tier
/// catalog. The frontend never connects to the remote service directly.
/// </summary>
public sealed record PrepareMineruConnectionCommand : WorkbenchCommand;

/// <summary>
/// Set the task-level recognition mode for the recognition page; null clears
/// the override and delegates to the Runtime default.
/// </summary>
public sealed record SetTaskEngineCommand(string? Engine) : WorkbenchCommand;

/// <summary>Start ensure with the staged explicit component/source intent.</summary>
public sealed record InstallRuntimeCommand : WorkbenchCommand;
public sealed record ConfirmRuntimeInstallCommand(string PlanId) : WorkbenchCommand;

/// <summary>Cancel the running durable maintenance operation.</summary>
public sealed record CancelRuntimeMaintenanceCommand : WorkbenchCommand;

/// <summary>Retry the last failed/cancelled maintenance operation (reuse intent).</summary>
public sealed record RetryRuntimeMaintenanceCommand : WorkbenchCommand;

public sealed record CheckUpdateCommand : WorkbenchCommand;

public sealed record DownloadUpdateCommand : WorkbenchCommand;

public sealed record CancelUpdateCommand : WorkbenchCommand;

/// <summary>Cancel the active Runtime operation before retrying an app update.</summary>
public sealed record CancelRuntimeForUpdateCommand : WorkbenchCommand;

public sealed record ExportDiagnosticsCommand : WorkbenchCommand;

public enum WorkbenchTheme
{
  System,
  Light,
  Dark,
}

public abstract record WorkbenchState
{
  public abstract string Scope { get; }
}

public sealed record ShellWorkbenchState(WorkbenchRoute Route) : WorkbenchState
{
  public override string Scope => "shell";
}

public sealed record RecognitionWorkbenchState(
  bool IsBusy,
  string StatusCode,
  WorkbenchResourceReference? Input = null,
  WorkbenchResourceReference? Result = null,
  IReadOnlyList<RecognitionEngineChoice>? Engines = null,
  string? TaskEngine = null,
  RecognitionScreenshotSessionState? ScreenshotSession = null) : WorkbenchState
{
  public override string Scope => "recognition";
}

/// <summary>
/// Active pure-screenshot editing session: the host-assigned session id and
/// the current editor content revision. All session image commands must match
/// both values; mismatched requests fail closed as stale.
/// </summary>
public sealed record RecognitionScreenshotSessionState(
  string SessionId,
  long Revision);

/// <summary>
/// One selectable recognition mode on the recognition page. <see cref="Selected"/>
/// and <see cref="IsTaskOverride"/> are true only for an explicit task choice;
/// otherwise the Runtime default remains selected by omission.
/// </summary>
public sealed record RecognitionEngineChoice(
  string Engine,
  string DisplayName,
  bool Selected,
  bool IsTaskOverride,
  string Availability,
  bool RequiresDownload,
  string LifecycleKind = "unmanaged",
  bool SupportsPreload = false,
  bool SupportsTtl = false,
  bool SupportsPinning = false,
  bool SupportsRelease = false);

public sealed record BatchWorkbenchState(
  bool IsRunning,
  int ItemCount,
  int CompletedCount,
  int FailedCount,
  IReadOnlyList<BatchWorkbenchItem>? Items = null,
  int WindowStart = 0,
  IReadOnlyList<RecognitionEngineChoice>? Engines = null,
  string? TaskEngine = null) : WorkbenchState
{
  public override string Scope => "batch";
}

public sealed record BatchWorkbenchItem(
  Guid Id,
  string Name,
  string StatusCode,
  string? ResultSummary);

public sealed record PdfWorkbenchState(
  bool IsBusy,
  string StatusCode,
  int PageCount,
  int SelectedPage,
  IReadOnlyList<int>? SelectedPages = null,
  IReadOnlyList<PdfWorkbenchPage>? Pages = null,
  int WindowStart = 0) : WorkbenchState
{
  public override string Scope => "pdf";
}

public sealed record PdfWorkbenchPage(
  int Index,
  string StatusCode,
  WorkbenchResourceReference? Thumbnail);

public sealed record QrCodeWorkbenchState(
  bool IsBusy,
  string StatusCode,
  IReadOnlyList<string> Results,
  WorkbenchResourceReference? GeneratedResource,
  IReadOnlyList<QrCodeWorkbenchResult>? Items = null) : WorkbenchState
{
  public override string Scope => "qrcode";
}

public sealed record QrCodeWorkbenchResult(
  string Data,
  string Format,
  bool IsUrl);

public sealed record SettingsWorkbenchState(
  WorkbenchTheme Theme,
  bool IsBusy,
  string StatusCode,
  string Backend,
  bool StartupEnabled,
  string Hotkey,
  IReadOnlyList<SettingsSourceOptionState>? Sources = null,
  string PendingBackend = "cpu",
  bool CanSwitchBackend = false,
  IReadOnlyList<SettingsFeatureOptionState>? Features = null,
  SettingsMaintenanceState? Maintenance = null,
  string HotkeyStatus = "",
  string PendingHotkey = "",
  string StatusMessage = "",
  string ServiceStatus = "",
  string MaintenanceStatus = "",
  string MaintenancePhase = "",
  string ProgressText = "",
  string ProgressDetail = "",
  double? ProgressPercent = null,
  bool CanPreviewInstall = false,
  VibeOCR.Runtime.Contracts.Generated.Host.RuntimeInstallPlan? InstallPlan = null,
  SettingsMineruConnectionState? MineruConnection = null,
  IReadOnlyList<SettingsEnvironmentState>? Environments = null,
  string? ActiveEnvironmentId = null,
  SettingsEnvironmentPlanState? EnvironmentPlan = null,
  string EnvironmentStatus = "",
  bool EnvironmentBusy = false,
  IReadOnlyList<string>? EnvironmentPackageSourceIds = null,
  bool EnvironmentCanCancelInstall = false) : WorkbenchState
{
  public override string Scope => "settings";
}

public sealed record SettingsEnvironmentState(
  string Id,
  string Name,
  int Revision,
  string Kind,
  string Status,
  string PythonState,
  string DependencyState,
  string EngineState,
  string ModelState,
  string ServiceState,
  IReadOnlyList<string> ConfiguredRecognitionTypes,
  string? TargetDevice,
  string? ActualDevice,
  string? Reason,
  string? PythonVersion,
  string? Abi,
  string? Python,
  string? Path,
  long DiskBytes,
  SettingsEnvironmentInstallFailureState? LastInstallFailure = null);

public sealed record SettingsEnvironmentInstallFailureState(
  string Phase,
  int EnvironmentRevision,
  string Recipe,
  string ReasonCode,
  string NextAction,
  string Detail);

public sealed record SettingsEnvironmentPlanState(
  string PlanId,
  string EnvironmentId,
  string Recipe,
  IReadOnlyList<string> SourceIds,
  IReadOnlyList<string> Dependencies,
  string RequestedRecipe);

/// <summary>
/// MinerU 连接投影；API Key 只以是否已配置出现，明文不进入桥接状态。
/// </summary>
public sealed record SettingsMineruConnectionState(
    bool Supported,
    string Mode,
    string ApiUrl,
    bool HasApiKey);

/// <summary>
/// Durable maintenance operation projection: requested/effective component
/// sets and requested/effective source ids are the installation truth from
/// Backend snapshots. App/UI code only consumes this neutral projection.
/// </summary>
public sealed record SettingsMaintenanceState(
  bool IsRunning,
  string StatusCode,
  string? OperationId,
  IReadOnlyList<string> RequestedComponentIds,
  IReadOnlyList<string> EffectiveComponentIds,
  IReadOnlyList<string> RequestedSourceIds,
  IReadOnlyList<string> EffectiveSourceIds,
  bool CanCancel,
  bool CanRetry,
  string FailureReason = "",
  string FailureCode = "");

/// <summary>Catalog package-index source projected as a single-select option.</summary>
public sealed record SettingsSourceOptionState(
  string Kind,
  string Id,
  string DisplayName,
  bool Selected);

/// <summary>Optional feature for the pending accelerator.</summary>
public sealed record SettingsFeatureOptionState(
  string FeatureId,
  string DisplayName,
  string Accelerator,
  bool Selected);

public sealed record UpdateWorkbenchState(
  bool IsBusy,
  string StatusCode,
  string? LatestVersion,
  bool UpdateAvailable,
  bool CanCancelRuntimeMaintenance = false) : WorkbenchState
{
  public override string Scope => "update";
}

public sealed record AboutWorkbenchState(
  string Version,
  string License,
  string ProjectUrl) : WorkbenchState
{
  public override string Scope => "about";
}

public sealed record DiagnosticsWorkbenchState(
  string SupervisorStatus,
  string ProtocolStatus,
  bool IsReady,
  IReadOnlyList<string> Milestones) : WorkbenchState
{
  public override string Scope => "diagnostics";
}

public sealed record WorkbenchResourceReference(
  string Url,
  string MediaType,
  long ByteLength);

public sealed record WorkbenchCommandEnvelope(Guid Id, WorkbenchCommand Command);

public sealed record WorkbenchProblem(
  string Code,
  WorkbenchProblemCategory Category,
  bool Retryable,
  string MessageKey);

public sealed record WorkbenchCommandReceipt(
  Guid Id,
  long Revision,
  WorkbenchProblem? Error)
{
  public bool Ok => Error is null;
}

public sealed record WorkbenchStateEnvelope(
  long Revision,
  string Scope,
  WorkbenchStateChange Change,
  WorkbenchState? State);

public sealed record WorkbenchBootstrap(
  int ProtocolVersion,
  Guid SessionId,
  long Revision,
  WorkbenchRoute Route,
  IReadOnlyList<WorkbenchStateEnvelope> States,
  IReadOnlySet<string> Capabilities);

public interface IWorkbenchApplication : IAsyncDisposable
{
  ValueTask<WorkbenchBootstrap> BootstrapAsync(CancellationToken cancellationToken);

  ValueTask<WorkbenchCommandReceipt> ExecuteAsync(
    WorkbenchCommandEnvelope envelope,
    CancellationToken cancellationToken);

  IAsyncEnumerable<WorkbenchStateEnvelope> SubscribeAsync(
    long afterRevision,
    CancellationToken cancellationToken);
}

public sealed record WorkbenchCommandOutcome(
  IReadOnlyList<WorkbenchState> States,
  WorkbenchProblem? Error);

public interface IWorkbenchCommandHandler
{
  ValueTask<WorkbenchCommandOutcome> ExecuteAsync(
    WorkbenchCommand command,
    CancellationToken cancellationToken);
}

public interface IWorkbenchStateSource
{
  IReadOnlyList<WorkbenchState> InitialStates { get; }

  event Action<WorkbenchState>? StateChanged;
}

public interface IWorkbenchBootstrapSource
{
  ValueTask PrepareBootstrapAsync(CancellationToken cancellationToken);
}
