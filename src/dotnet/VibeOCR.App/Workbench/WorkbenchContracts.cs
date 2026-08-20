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

public sealed record SelectRecognitionImageCommand : WorkbenchCommand;

public sealed record RecognizeDroppedFileCommand(string Path) : WorkbenchCommand;

public sealed record ReadRecognitionClipboardCommand : WorkbenchCommand;

public sealed record CancelRecognitionCommand : WorkbenchCommand;

public sealed record CopyRecognitionResultCommand(string Format) : WorkbenchCommand;

public sealed record ExportRecognitionResultCommand(string Format) : WorkbenchCommand;

public sealed record AddBatchFilesCommand : WorkbenchCommand;

public sealed record AddDroppedBatchFilesCommand(
  IReadOnlyList<string> Paths) : WorkbenchCommand;

public sealed record ExportBatchCommand(string Format) : WorkbenchCommand;

public sealed record StartBatchCommand : WorkbenchCommand;

public sealed record CancelBatchCommand : WorkbenchCommand;

public sealed record ClearBatchCommand : WorkbenchCommand;

public sealed record MoveBatchItemCommand(Guid ItemId, int Delta) : WorkbenchCommand;

public sealed record RemoveBatchItemCommand(Guid ItemId) : WorkbenchCommand;

public sealed record SetBatchConcurrencyCommand(int Concurrency) : WorkbenchCommand;

public sealed record SetBatchWindowCommand(int Start) : WorkbenchCommand;

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

public sealed record SetThemeCommand(WorkbenchTheme Theme) : WorkbenchCommand;

public sealed record SetStartupCommand(bool Enabled) : WorkbenchCommand;

public sealed record SetHotkeyCommand(string Hotkey) : WorkbenchCommand;

/// <summary>Persist the global OCR engine preference (wire engine id).</summary>
public sealed record SetOcrEngineCommand(string Engine) : WorkbenchCommand;

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
/// Set the task-level OCR engine override for the recognition page; null
/// clears the override and falls back to the global preference.
/// </summary>
public sealed record SetTaskEngineCommand(string? Engine) : WorkbenchCommand;

/// <summary>Start ensure with the staged explicit component/source intent.</summary>
public sealed record InstallRuntimeCommand : WorkbenchCommand;

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
  string? TaskEngine = null) : WorkbenchState
{
  public override string Scope => "recognition";
}

/// <summary>
/// One selectable engine on the recognition page. <see cref="Selected"/> marks
/// the effective engine (task override or global default) and
/// <see cref="IsTaskOverride"/> distinguishes a task-level override from the
/// persisted global preference.
/// </summary>
public sealed record RecognitionEngineChoice(
  string Engine,
  string DisplayName,
  bool Selected,
  bool IsTaskOverride,
  string Availability,
  bool RequiresDownload);

public sealed record BatchWorkbenchState(
  bool IsRunning,
  int ItemCount,
  int CompletedCount,
  int FailedCount,
  IReadOnlyList<BatchWorkbenchItem>? Items = null,
  int Concurrency = 1,
  int WindowStart = 0) : WorkbenchState
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
  IReadOnlyList<SettingsEngineOptionState>? Engines = null,
  string? SelectedEngine = null,
  bool EngineChoiceRequired = false,
  IReadOnlyList<SettingsSourceOptionState>? Sources = null,
  string PendingBackend = "cpu",
  bool CanSwitchBackend = false,
  IReadOnlyList<SettingsFeatureOptionState>? Features = null,
  SettingsMaintenanceState? Maintenance = null) : WorkbenchState
{
  public override string Scope => "settings";
}

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
  bool CanRetry);

/// <summary>Catalog engine projected for the settings page.</summary>
public sealed record SettingsEngineOptionState(
  string Engine,
  string DisplayName,
  string Availability,
  string? ReasonCode,
  bool RequiresDownload,
  bool Selected);

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
