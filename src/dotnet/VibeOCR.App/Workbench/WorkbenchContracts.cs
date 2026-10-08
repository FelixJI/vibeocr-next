using VibeOCR.Platform.Windows;
using VibeOCR.App.Features.Recognition;

namespace VibeOCR.App.Workbench;

public static class WorkbenchProtocol
{
  public const int Version = 2;
}

public enum WorkbenchRoute
{
  Recognition,
  ImageEdit,
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

public sealed record SelectImageEditFileCommand : WorkbenchCommand;
public sealed record ReadImageEditClipboardCommand : WorkbenchCommand;
public sealed record OpenDroppedImageEditFileCommand(string Path) : WorkbenchCommand;

/// <summary>
/// recognition/imageEdit 会话命令共享的数据合同：同一套宿主共享实现按
/// 通道容器路由，不复制两套截图方法，也不动态交换共享字段。
/// </summary>
public interface IScreenshotSessionTarget
{
  Guid SessionId { get; }
  long Revision { get; }
}

/// <summary>携带 annotation 租约 URI 的会话命令（复制/保存/钉图/识别/文字层）。</summary>
public interface IScreenshotSessionResource : IScreenshotSessionTarget
{
  string ResourceUri { get; }
}

/// <summary>携带归一化排除框的会话命令（钉图/显式识别）。</summary>
public interface IScreenshotSessionExclusions : IScreenshotSessionResource
{
  IReadOnlyList<WorkbenchExclusionBox> ExcludeBoxes { get; }
}

/// <summary>携带文字层选中文本的会话命令（选区复制）。</summary>
public interface IScreenshotSessionSelectionText : IScreenshotSessionTarget
{
  string Text { get; }
}

/// <summary>
/// 单次识别页的"选图编辑"：在 recognition scope 建立编辑会话（同一
/// 截图会话/修订合同），不占用 imageEdit 页的独立会话状态。
/// </summary>
public sealed record OpenRecognitionImageForEditCommand : WorkbenchCommand;

/// <summary>单次识别页的"粘贴编辑"：recognition scope 编辑会话输入。</summary>
public sealed record PasteRecognitionImageForEditCommand : WorkbenchCommand;

public sealed record CaptureRecognitionScreenCommand : WorkbenchCommand;

/// <summary>
/// Pure screenshot capture: open the region picker, publish the captured
/// pixels as a local editing session, and submit no OCR request. Recognition
/// happens only through an explicit <see cref="RecognizeScreenshotImageCommand"/>
/// carrying the session's current revision.
/// </summary>
public sealed record CaptureScreenshotSessionCommand : WorkbenchCommand;
public sealed record CaptureScreenshotTextSessionCommand : WorkbenchCommand;
public sealed record CaptureScrollingScreenshotCommand : WorkbenchCommand;

public sealed record CloseScreenshotSessionCommand : WorkbenchCommand;

/// <summary>
/// Editor-side content revision for the active screenshot session. Every
/// commit/undo/redo publishes a strictly increasing revision; the host drops
/// the previous OCR result association so late responses cannot pollute the
/// new content.
/// </summary>
public sealed record NotifyScreenshotSessionRevisionCommand(
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionTarget;

public sealed record CopyScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionResource;

public sealed record SaveScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionResource;

public sealed record PinScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision,
    IReadOnlyList<WorkbenchExclusionBox> ExcludeBoxes) : WorkbenchCommand,
    IScreenshotSessionExclusions;

/// <summary>
/// [0,1000] normalized recognition-exclusion rectangle supplied with a pin
/// request. The host drops any text-layer line whose box has positive-area
/// overlap with any rectangle, mirroring the workbench in-place layer filter,
/// so pinned selection cannot resurrect boundary-straddling masked lines.
/// </summary>
public sealed record WorkbenchExclusionBox(
    double X,
    double Y,
    double Width,
    double Height)
{
  /// <summary>
  /// 行框与排除矩形是否正面积相交（仅贴边不算）：同一判定供原位层与
  /// 贴图层消费，避免两份语义漂移。
  /// </summary>
  public static bool LineIntersectsBox(
    RecognitionTextLayerLine line, WorkbenchExclusionBox box) =>
    Math.Max(line.X1, line.X2) > box.X &&
    Math.Min(line.X1, line.X2) < box.X + box.Width &&
    Math.Max(line.Y1, line.Y2) > box.Y &&
    Math.Min(line.Y1, line.Y2) < box.Y + box.Height;
}

/// <summary>
/// Explicit recognition of the session's current final ordinary pixels
/// (uploaded through the opaque annotation lease, exclusions NOT baked).
/// The host freezes them as the session display baseline and derives the
/// masked OCR input itself from ExcludeBoxes. Never falls back to the
/// unedited capture, and never uses masked bytes for the display baseline.
/// </summary>
public sealed record RecognizeScreenshotImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision,
    IReadOnlyList<WorkbenchExclusionBox> ExcludeBoxes) : WorkbenchCommand,
    IScreenshotSessionExclusions;

/// <summary>
/// Prepares the in-place selectable text layer for the session's current
/// final PNG. Runs only a catalog-ready local lightweight text mode; never
/// installs dependencies or contacts remote services, and does not touch the
/// user's task-level recognition mode.
/// </summary>
public sealed record PrepareScreenshotTextLayerCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionResource;

public sealed record CancelScreenshotTextLayerCommand(
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionTarget;

/// <summary>Copies a DOM selection made on the text layer (only the selected text).</summary>
public sealed record CopyScreenshotSelectionCommand(
    Guid SessionId,
    long Revision,
    string Text) : WorkbenchCommand, IScreenshotSessionSelectionText;

// ---- imageEdit scope：独立图片编辑会话命令（与 recognition 会话同一数据
// 约定，但只作用于 imageEdit 通道的会话/修订）。 ----

public sealed record NotifyImageEditRevisionCommand(
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionTarget;

public sealed record CloseImageEditSessionCommand : WorkbenchCommand;

public sealed record CopyImageEditImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionResource;

public sealed record SaveImageEditImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionResource;

public sealed record PinImageEditImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision,
    IReadOnlyList<WorkbenchExclusionBox> ExcludeBoxes) : WorkbenchCommand,
    IScreenshotSessionExclusions;

/// <summary>
/// imageEdit 会话的显式识别交接：验证/冻结 imageEdit 会话基准并按排除框
/// 生成 OCR 输入，但识别任务与结果发布到 recognition scope（识别承载面）；
/// imageEdit 会话保留冻结基准供继续编辑/重试。
/// </summary>
public sealed record RecognizeImageEditImageCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision,
    IReadOnlyList<WorkbenchExclusionBox> ExcludeBoxes) : WorkbenchCommand,
    IScreenshotSessionExclusions;

public sealed record PrepareImageEditTextLayerCommand(
    string ResourceUri,
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionResource;

public sealed record CancelImageEditTextLayerCommand(
    Guid SessionId,
    long Revision) : WorkbenchCommand, IScreenshotSessionTarget;

public sealed record CopyImageEditSelectionCommand(
    Guid SessionId,
    long Revision,
    string Text) : WorkbenchCommand, IScreenshotSessionSelectionText;

public sealed record CopyImageEditAnnotatedImageCommand(string ResourceUri) : WorkbenchCommand;

public sealed record SaveImageEditAnnotatedImageCommand(string ResourceUri) : WorkbenchCommand;

public sealed record SelectRecognitionImageCommand : WorkbenchCommand;

public sealed record RecognizeDroppedFileCommand(string Path) : WorkbenchCommand;

public sealed record ReadRecognitionClipboardCommand : WorkbenchCommand;

public sealed record CancelRecognitionCommand : WorkbenchCommand;

public sealed record CopyRecognitionResultCommand(string Format) : WorkbenchCommand;

/// <summary>
/// Copy one block of a host-published structured result resource through the
/// native clipboard. The web side only passes the opaque resource URI it was
/// given plus the block index and the desired representation ("table" writes
/// HTML+TSV, "latex" writes the raw formula text); the host re-reads the
/// authoritative file and builds the payload itself.
/// </summary>
public sealed record CopyStructuredResultCommand(
  string ResourceUri,
  int BlockIndex,
  string Format) : WorkbenchCommand;

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

public sealed record SetBatchItemPageRangeCommand(Guid ItemId, string? PageRange) : WorkbenchCommand;

public sealed record SetBatchWindowCommand(int Start) : WorkbenchCommand;

public sealed record SetBatchTaskEngineCommand(string? Engine) : WorkbenchCommand;

/// <summary>
/// Set the task-level recognition mode for the PDF workbench; null clears the
/// override. PDF page OCR shares the same mode/options contract as the
/// recognition and batch pages.
/// </summary>
public sealed record SetPdfTaskEngineCommand(string? Engine) : WorkbenchCommand;

public sealed record OpenPdfCommand : WorkbenchCommand;

public sealed record OpenDroppedPdfCommand(string Path) : WorkbenchCommand;

public sealed record RotatePdfCommand(int Degrees = 90) : WorkbenchCommand;

public sealed record ClosePdfCommand : WorkbenchCommand;

public sealed record DeletePdfPagesCommand : WorkbenchCommand;

public sealed record OcrPdfPagesCommand : WorkbenchCommand;

public sealed record SavePdfCommand : WorkbenchCommand;

public sealed record SelectPdfPagesCommand(IReadOnlyList<int> Pages) : WorkbenchCommand;

public sealed record SetPdfWindowCommand(int Start) : WorkbenchCommand;

public sealed record GenerateQrCodeCommand(string Text, string Format = "qrcode", string CaptionMode = "off", string CaptionText = "") : WorkbenchCommand;

public sealed record DecodeCurrentQrCodeCommand(bool Force = false) : WorkbenchCommand;

public sealed record CopyQrCodeImageCommand : WorkbenchCommand;

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
public sealed record PreviewEnvironmentInstallCommand(string EnvironmentId, string Recipe, string? SourceId) : WorkbenchCommand;
public sealed record ConfirmEnvironmentInstallCommand(string PlanId, string? SourceId) : WorkbenchCommand;
public sealed record SetEnvironmentSourcesCommand(
  string? EnvironmentId, string? PackageSourceId, string? ModelSourceId,
  string? PaddleocrModelSourceId = null, string? MineruModelSourceId = null,
  bool IndependentModelSources = false) : WorkbenchCommand;
public sealed record CancelEnvironmentInstallCommand : WorkbenchCommand;
public sealed record InvalidateEnvironmentPlanCommand : WorkbenchCommand;
public sealed record SwitchEnvironmentCommand(string EnvironmentId) : WorkbenchCommand;
public sealed record DeleteEnvironmentCommand(string EnvironmentId) : WorkbenchCommand;
public sealed record RepairEmptyEnvironmentCommand(string EnvironmentId) : WorkbenchCommand;
/// <summary>推荐配置选择：只读查询配方兼容环境，不创建/不安装/不切换。</summary>
public sealed record FindCompatibleEnvironmentCommand(string Recipe) : WorkbenchCommand;

/// <summary>优先复用兼容环境，否则按精确 ID 准备并预览；明确确认后才安装。</summary>
public sealed record PrepareEnvironmentCommand(string Recipe) : WorkbenchCommand;

/// <summary>
/// 远程模式宿主入口：随包离线基础配方一键就绪。已安装可复用环境直接
/// 切换启动；无可复用环境时新建/复用专用环境并走 Runtime 权威离线
/// 安装事务（取消/CAS/回滚保留），安装完成后切换启动承载识别服务。
/// 不触碰用户已有空环境，不下载识别模型。
/// </summary>
public sealed record PrepareRemoteHostCommand : WorkbenchCommand;

public sealed record SetThemeCommand(WorkbenchTheme Theme) : WorkbenchCommand;

public sealed record SetStartupCommand(bool Enabled) : WorkbenchCommand;

/// <summary>
/// Set the global hotkey for one action in the shell action catalog. A null
/// hotkey disables the binding; the host registers the new key first and
/// releases the old one only after the config is persisted, so a failed save
/// keeps the previous binding active.
/// </summary>
public sealed record SetActionHotkeyCommand(string ActionId, string? Hotkey) : WorkbenchCommand;

/// <summary>Restore an action's default hotkey (only the quick recognition action has one).</summary>
public sealed record ResetActionHotkeyCommand(string ActionId) : WorkbenchCommand;

public sealed record BeginHotkeyRecordingCommand(Guid RecordingId) : WorkbenchCommand;

public sealed record EndHotkeyRecordingCommand(Guid RecordingId) : WorkbenchCommand;

/// <summary>
/// Enable or disable the floating toolbar live. Enabling persists the
/// preference and creates the toolbar; disabling tears it down. The toolbar
/// can always be found again from settings, the tray menu, or the toolbar
/// toggle action.
/// </summary>
public sealed record SetFloatingToolbarEnabledCommand(bool Enabled) : WorkbenchCommand;

/// <summary>Change the floating toolbar dock edge and auto-hide behavior live.</summary>
public sealed record SetFloatingToolbarLayoutCommand(ScreenEdge Edge, bool AutoHide) : WorkbenchCommand;

/// <summary>Patch the collapse delay or native theme preference against the current settings.</summary>
public sealed record SetFloatingToolbarPreferencesCommand(int? LingerMs = null, string? Theme = null, int? PeekPixels = null) : WorkbenchCommand;

/// <summary>Explicitly show the floating toolbar (recover from user-hidden).</summary>
public sealed record ShowFloatingToolbarCommand : WorkbenchCommand;

/// <summary>User-hide the floating toolbar (disarms the edge sensor until found again).</summary>
public sealed record HideFloatingToolbarCommand : WorkbenchCommand;

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
/// Persist the global MinerU recognition preference in Backend settings
/// extra.mineru_recognition（ocr.mineru-config.v1）。All four wire fields
/// are replaced atomically; tier/language availability is enforced against
/// the runtime catalog host-side before any write. Formula/table parsing is
/// always-on in MinerU 4 (no upstream switch) and is therefore not part of
/// this command.
/// </summary>
public sealed record SetMineruRecognitionCommand(
    string Tier,
    string OcrMode,
    string PageRange,
    string Language) : WorkbenchCommand;

/// <summary>
/// Set the task-level recognition mode for the recognition page; null clears
/// the override and delegates to the Runtime default.
/// </summary>
public sealed record SetTaskEngineCommand(string? Engine) : WorkbenchCommand;
public sealed record SetRecognitionOptionsCommand(string ModeId, PaddleModeOptions Options) : WorkbenchCommand;

/// <summary>
/// Persist the default recognition mode in Backend settings
/// extra.default_recognition_mode（ocr.default-recognition-mode.v1）。
/// The runtime capability gate, catalog id and ready availability are all
/// enforced host-side before any write; runtimes without the capability are
/// never sent the key.
/// </summary>
public sealed record SetDefaultRecognitionModeCommand(string ModeId) : WorkbenchCommand;

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

/// <summary>
/// Copy the same redacted diagnostics document as the export to the system
/// clipboard through the host clipboard seam; the web layer never touches
/// navigator.clipboard.
/// </summary>
public sealed record CopyDiagnosticsCommand : WorkbenchCommand;

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
  RecognitionScreenshotSessionState? ScreenshotSession = null,
  RecognitionTextLayerState? TextLayer = null,
  WorkbenchResourceReference? StructuredResult = null) : WorkbenchState
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
  long Revision,
  bool TextSelectionRequested = false,
  bool SceneEditing = false,
  IReadOnlyList<WorkbenchExclusionBox>? ExcludeBoxes = null);

/// <summary>
/// 图片编辑页的独立宿主状态（scope "imageEdit"）：会话/修订/文字层与
/// recognition scope 完全隔离；两个页面的上传图片互不影响。字段命名与
/// recognition 会话投影保持一致，供同一套前端解析器消费；无引擎目录
/// （imageEdit 页不提供任务级识别模式选择）。
/// </summary>
public sealed record ImageEditWorkbenchState(
  bool IsBusy,
  string StatusCode,
  WorkbenchResourceReference? Input = null,
  RecognitionScreenshotSessionState? ScreenshotSession = null,
  RecognitionTextLayerState? TextLayer = null) : WorkbenchState
{
  public override string Scope => "imageEdit";
}

/// <summary>
/// In-place selectable text layer bound to one session revision. The lines
/// come from the recognition run over the same final PNG the layer displays;
/// any mismatch (revision, mode, service instance) makes the layer stale.
/// The run always uses the fixed lightweight text configuration: plain OCR
/// pipeline, the bound catalog-ready local mode's engine, and no user OCR
/// options (no rotation/deskew inheritance); changing user task settings
/// never mutates an existing layer.
/// </summary>
public sealed record RecognitionTextLayerState(
  string Status,
  string? Reason,
  RecognitionScreenshotSessionState? Binding,
  string? ModeId,
  string? ServiceInstance,
  WorkbenchResourceReference? Image,
  IReadOnlyList<RecognitionTextLayerLine>? Lines = null);

/// <summary>One OCR text line: [0,1000] normalized line box, no char boxes.</summary>
public sealed record RecognitionTextLayerLine(
  string Text,
  double X1,
  double Y1,
  double X2,
  double Y2,
  int? Order = null);

/// <summary>
/// One selectable recognition mode on the recognition page. <see cref="Selected"/>
/// and <see cref="IsTaskOverride"/> are true only for an explicit task choice;
/// <see cref="IsDefault"/> independently marks the committed runtime default so
/// the inherit option can keep showing it under an override.
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
  bool SupportsRelease = false,
  string? Family = null,
  IReadOnlyList<string>? SupportedOptions = null,
  IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Options = null,
  string? ReasonCode = null,
  bool IsDefault = false);

public sealed record BatchWorkbenchState(
  bool IsRunning,
  int ItemCount,
  int CompletedCount,
  int FailedCount,
  IReadOnlyList<BatchWorkbenchItem>? Items = null,
  int WindowStart = 0,
  IReadOnlyList<RecognitionEngineChoice>? Engines = null,
  string? TaskEngine = null,
  bool ExportIncomplete = false,
  string? InputKindNotice = null) : WorkbenchState
{
  public override string Scope => "batch";
}

public sealed record BatchWorkbenchItem(
  Guid Id,
  string Name,
  string StatusCode,
  string? ResultSummary,
  WorkbenchResourceReference? StructuredResult = null,
  string? PageRange = null,
  bool SupportsPageRange = true);

public sealed record PdfWorkbenchState(
  bool IsBusy,
  string StatusCode,
  int PageCount,
  int SelectedPage,
  IReadOnlyList<int>? SelectedPages = null,
  IReadOnlyList<PdfWorkbenchPage>? Pages = null,
  int WindowStart = 0,
  IReadOnlyList<RecognitionEngineChoice>? Engines = null,
  string? TaskEngine = null) : WorkbenchState
{
  public override string Scope => "pdf";
}

public sealed record PdfWorkbenchPage(
  int Index,
  string StatusCode,
  WorkbenchResourceReference? Thumbnail,
  WorkbenchResourceReference? StructuredResult = null);

public sealed record QrCodeWorkbenchState(
  bool IsBusy,
  string StatusCode,
  IReadOnlyList<string> Results,
  WorkbenchResourceReference? GeneratedResource,
  IReadOnlyList<QrCodeWorkbenchResult>? Items = null,
  long PreviewRevision = 0,
  bool NeedsPreviewDecode = false,
  string? StatusMessage = null) : WorkbenchState
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
  string? Backend,
  bool StartupEnabled,
  IReadOnlyList<SettingsSourceOptionState>? Sources = null,
  string PendingBackend = "cpu",
  bool CanSwitchBackend = false,
  IReadOnlyList<SettingsFeatureOptionState>? Features = null,
  SettingsMaintenanceState? Maintenance = null,
  string StatusMessage = "",
  string ServiceStatus = "",
  string MaintenanceStatus = "",
  string MaintenancePhase = "",
  bool ProgressActive = false,
  string ProgressText = "",
  string ProgressDetail = "",
  double? ProgressPercent = null,
  bool CanPreviewInstall = false,
  VibeOCR.Runtime.Contracts.Generated.Host.RuntimeInstallPlan? InstallPlan = null,
  SettingsMineruConnectionState? MineruConnection = null,
  SettingsDefaultRecognitionModeState? DefaultRecognitionMode = null,
  SettingsMineruRecognitionState? MineruRecognition = null,
  IReadOnlyList<SettingsRecognitionModeOptionState>? RecognitionModes = null,
  IReadOnlyList<SettingsEnvironmentState>? Environments = null,
  string? ActiveEnvironmentId = null,
  SettingsEnvironmentPlanState? EnvironmentPlan = null,
  string EnvironmentStatus = "",
  bool EnvironmentBusy = false,
  IReadOnlyList<SettingsEnvironmentSourceState>? EnvironmentSources = null,
  IReadOnlyList<string>? EnvironmentDefaultSourceIds = null,
  IReadOnlyList<string>? EnvironmentUnknownDefaultSourceIds = null,
  IReadOnlyList<SettingsEnvironmentResolvedSourceState>? EnvironmentResolvedDefaultSources = null,
  IReadOnlyList<string>? EnvironmentPackageSourceIds = null,
  bool EnvironmentCanCancelInstall = false,
  IReadOnlyList<SettingsEnvironmentRecipeState>? EnvironmentRecipes = null,
  SettingsEnvironmentHardwareState? EnvironmentHardware = null,
  SettingsEnvironmentCompatibilityState? EnvironmentCompatibility = null,
  IReadOnlyList<SettingsHotkeyActionState>? HotkeyActions = null,
  SettingsFloatingToolbarState? FloatingToolbar = null) : WorkbenchState
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
  IReadOnlyList<string>? SourceIds = null,
  IReadOnlyList<string>? OverrideSourceIds = null,
  IReadOnlyList<string>? UnknownSourceIds = null,
  IReadOnlyList<SettingsEnvironmentResolvedSourceState>? ResolvedSources = null,
  SettingsEnvironmentInstallFailureState? LastInstallFailure = null);

/// <summary>目录内下载源投影：展示名 + 脱敏端点。</summary>
public sealed record SettingsEnvironmentSourceState(
  string Id,
  string Kind,
  string DisplayName,
  string Endpoint,
  bool IsDefault = false);

/// <summary>
/// Runtime 权威配方目录投影（list.recipes）：用途/设备分组与 GPU 门禁在
/// 前端仅消费本投影；目录未同步时为空，不回落臆造组合。
/// </summary>
public sealed record SettingsEnvironmentRecipeState(
  string Id,
  string DisplayName,
  IReadOnlyList<string>? ConfiguredRecognitionTypes = null,
  string? Accelerator = null,
  string? TargetDevice = null,
  string? PythonVersion = null,
  string? Abi = null,
  string? Platform = null,
  string? ScopeId = null,
  IReadOnlyList<string>? ComponentIds = null,
  string? RecipeLock = null,
  IReadOnlyList<string>? Dependencies = null,
  string? DependencyOrigin = null,
  string? PythonOrigin = null,
  string? RuntimeWheelOrigin = null);

/// <summary>
/// 硬件可用性投影（list.hardware）：nvidia 驱动真值只由 Runtime 探测，
/// 宿主/前端不检测硬件；状态缺失时按未探测（unknown）呈现。
/// </summary>
public sealed record SettingsEnvironmentHardwareState(
  string NvidiaDriverStatus,
  string? NvidiaDriverReason = null,
  string? NvidiaDriverVersion = null);

/// <summary>当前选择配方的 find_compatible 只读结果（绑定配方 id，防止旧结果覆盖新选择）。</summary>
public sealed record SettingsEnvironmentCompatibilityState(
  string Recipe,
  string? SelectedEnvironmentId = null,
  int? SelectedEnvironmentRevision = null,
  string? SelectionReason = null,
  IReadOnlyList<SettingsEnvironmentQueryMatchState>? Environments = null);

/// <summary>单环境对查询的评估：selected 标记选中项；reason_code 说明原因。</summary>
public sealed record SettingsEnvironmentQueryMatchState(
  string EnvironmentId,
  string Name,
  int Revision,
  string Status,
  bool Active = false,
  bool Selected = false,
  string? ReasonCode = null);

/// <summary>每 kind 解析：Id 为 null 表示产品默认（模型源无覆盖时官方原生默认，端点未知）。</summary>
public sealed record SettingsEnvironmentResolvedSourceState(
  string Kind,
  string? Id,
  string? DisplayName,
  string Origin);

public sealed record SettingsEnvironmentInstallFailureState(
  string Phase,
  int EnvironmentRevision,
  string Recipe,
  string ReasonCode,
  string NextAction,
  string Detail,
  IReadOnlyList<string>? RequestedSourceIds = null,
  IReadOnlyList<string>? EffectiveSourceIds = null);

public sealed record SettingsEnvironmentPlanState(
  string PlanId,
  string EnvironmentId,
  string Recipe,
  IReadOnlyList<string> SourceIds,
  IReadOnlyList<string> Dependencies,
  string RequestedRecipe,
  IReadOnlyList<string>? RequestedSourceIds = null,
  int EnvironmentRevision = 0,
  IReadOnlyList<SettingsEnvironmentPlanSourceState>? Sources = null,
  string? DependencyOrigin = null,
  string? PythonOrigin = null,
  string? RuntimeWheelOrigin = null);

/// <summary>计划内单来源：请求/继承标记、用途分类与脱敏端点。</summary>
public sealed record SettingsEnvironmentPlanSourceState(
  string Id,
  string Kind,
  string DisplayName,
  string Endpoint,
  bool Requested,
  string InheritedFrom,
  string Usage,
  string? ActualEndpoint = null);

/// <summary>
/// One shell action's hotkey projection for the settings page: the configured
/// combination, the actually registered one (null when registration failed,
/// e.g. the combination is occupied by another app), the last error, and the
/// action's default binding (null when the action has no default key).
/// </summary>
public sealed record SettingsHotkeyActionState(
  string ActionId,
  string DisplayName,
  string? ConfiguredHotkey,
  string? RegisteredHotkey,
  string? Error,
  string? DefaultHotkey);

/// <summary>
/// Floating toolbar projection: enabled/edge/auto-hide mirror the persisted
/// preferences; visibility is the business-level state the UI describes to
/// the user (disabled / userHidden / edgeHidden / visible / suspended);
/// error carries the last apply/show/hide failure with the previous state
/// kept active.
/// </summary>
public sealed record SettingsFloatingToolbarState(
  bool Enabled,
  string Edge,
  bool AutoHide,
  string Visibility,
  string Error = "",
  int LingerMs = 300,
  string Theme = "system",
  int PeekPixels = 2);

/// <summary>
/// MinerU 连接投影；API Key 只以是否已配置出现，明文不进入桥接状态。
/// </summary>
public sealed record SettingsMineruConnectionState(
    bool Supported,
    string Mode,
    string ApiUrl,
    bool HasApiKey);

/// <summary>
/// 默认识别模式投影（#188）：Supported=Backend 是否声明
/// ocr.default-recognition-mode.v1；ModeId 为 Runtime 回显的已提交默认
/// （null=能力未声明或首读未完成，Stored 区分键是否存在）。目录有效性由
/// RecognitionModes 列表交叉判定，不在桥接层复制。
/// </summary>
public sealed record SettingsDefaultRecognitionModeState(
    bool Supported,
    string? ModeId,
    bool Stored);

/// <summary>
/// 全局 MinerU 识别偏好投影（extra.mineru_recognition）：Tiers/Languages
/// 为当前目录动态可用性（设置页下拉数据源，DefaultTier 为未保存时的展示
/// 默认）。Invalid=true 表示持久值语法无法解析，需重新保存修复；公式/
/// 表格在 MinerU 4 恒开启，无开关字段。
/// </summary>
public sealed record SettingsMineruRecognitionState(
    bool Supported,
    bool Stored,
    string? Tier,
    string? OcrMode,
    string? PageRange,
    string? Language,
    bool Invalid = false,
    string? InvalidReason = null,
    IReadOnlyList<SettingsMineruTierOptionState>? Tiers = null,
    IReadOnlyList<string>? Languages = null,
    string? DefaultTier = null);

/// <summary>设置页 MinerU 档位目录项（来自 ocr.mineru-config.v1 目录）。</summary>
public sealed record SettingsMineruTierOptionState(
    string Id,
    string Availability,
    string? ReasonCode);

/// <summary>设置页可选的识别模式目录项（来自 ocr.recognition-modes.v1）。</summary>
public sealed record SettingsRecognitionModeOptionState(
    string Id,
    string DisplayName,
    string Availability,
    string? ReasonCode);

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
  IReadOnlyList<string> Milestones,
  IReadOnlyList<string>? DeviceEvidence = null) : WorkbenchState
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
