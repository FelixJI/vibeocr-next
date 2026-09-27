// HTTP v2 DTO records mirroring the Python vibeocr.protocol.v2 dataclasses.
// Field order and nesting follow each to_payload() method in dtos.py exactly.
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeOCR.Contracts.HttpV2;

public sealed record JobItem
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public required ItemState State { get; init; }
    public int Attempt { get; init; }
    public string? Error { get; init; }
    public string? ClientItemKey { get; init; }
    public int Ordinal { get; init; }
    public string? SourceItemId { get; init; }
}

public sealed record JobSummary
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Cancelled { get; init; }
    public int Total { get; init; }
}

public sealed record ProgressSnapshot
{
    public required ProgressUnit Unit { get; init; }
    public required long Current { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Total { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EstimatedRemainingSeconds { get; init; }
}

public sealed record StageEvent
{
    public required int Sequence { get; init; }
    public required string Stage { get; init; }
    public string? ItemId { get; init; }
    public string? Timestamp { get; init; }
    /// <summary>Arbitrary event detail. Defaults to an empty object on the wire.</summary>
    public IDictionary<string, JsonElement>? Detail { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProgressSnapshot? Progress { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageCode { get; init; }
}

public sealed record JobRef
{
    public required string JobId { get; init; }
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public string? InstanceId { get; init; }
    public JobState State { get; init; } = JobState.Accepted;
    public IReadOnlyList<JobItem> Items { get; init; } = Array.Empty<JobItem>();
}

public sealed record JobSnapshot
{
    public required string JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required JobPriority Priority { get; init; }
    public required JobState State { get; init; }
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public string? InstanceId { get; init; }
    public string? CreatedAt { get; init; }
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public string? Stage { get; init; }
    public int ProgressCurrent { get; init; }
    public int ProgressTotal { get; init; }
    public IReadOnlyList<JobItem> Items { get; init; } = Array.Empty<JobItem>();
    public JobSummary Summary { get; init; } = new();
    public string? CancelRequestedAt { get; init; }
    public CancelMode? CancelMode { get; init; }
    public bool Degraded { get; init; }
    public int EventSequence { get; init; }
    public bool ResultAvailable { get; init; }
    public string? RequestId { get; init; }
    public string? SourceJobId { get; init; }
    public PipelineSelection? Pipeline { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProgressSnapshot? Progress { get; init; }
}

/// <summary>
/// Typed MinerU 4 configuration (ocr.mineru-config.v1), carried by the
/// optional PipelineSelection.Mineru block. Tier is required; omitting the
/// other fields selects auto / all / the upstream default language hint ch,
/// and the wire payload always carries the effective values. Language is only
/// an upstream OCR hint. The block is only valid for mineru_parse jobs on the
/// MinerU pipeline and must not be combined with legacy options or Engine.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MineruConfig
{
    public const string AllPages = "all";
    public const string DefaultLanguage = "ch";

    public MineruConfig() { }

    [JsonConstructor]
    [SetsRequiredMembers]
    public MineruConfig(
        MineruTier tier,
        MineruOcrMode ocrMode = MineruOcrMode.Auto,
        string pageRange = AllPages,
        string language = DefaultLanguage)
    {
        Tier = tier;
        OcrMode = ocrMode;
        PageRange = pageRange;
        Language = language;
    }

    [JsonRequired]
    public required MineruTier Tier { get; init; }
    public MineruOcrMode OcrMode { get; init; } = MineruOcrMode.Auto;
    public string PageRange { get; init; } = AllPages;
    public string Language { get; init; } = DefaultLanguage;
}

public sealed record PipelineSelection
{
    public required string PipelineId { get; init; }
    public int OptionsVersion { get; init; } = 1;
    public IDictionary<string, JsonElement> Options { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <summary>
    /// Explicit OCR engine for the plain-text OCR pipeline. Null omits the
    /// wire field (the request schema rejects an explicit null) and lets the
    /// Backend apply its own default engine.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OcrEngine? Engine { get; init; }

    /// <summary>
    /// Typed MinerU 4 configuration guarded by the ocr.mineru-config.v1
    /// capability. Null omits the wire field and keeps the legacy payload
    /// shape; when present the Backend rejects mixing it with non-empty
    /// legacy Options or Engine using VALIDATION_ERROR.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MineruConfig? Mineru { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitItem
{
    public required string ClientItemKey { get; init; }
    public required int Ordinal { get; init; }
    public required string DisplayName { get; init; }
    public required IDictionary<string, JsonElement> Source { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRequest
{
    public required string RequestId { get; init; }
    public required JobKind Kind { get; init; }
    public required JobPriority Priority { get; init; }
    public required PipelineSelection Pipeline { get; init; }
    public required IReadOnlyList<SubmitItem> Items { get; init; }
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public IDictionary<string, JsonElement> Parameters { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record ItemOutcome
{
    public required string ItemId { get; init; }
    public required ItemState State { get; init; }
    public required int Attempt { get; init; }
    public string? PayloadType { get; init; }
    public IDictionary<string, JsonElement>? Payload { get; init; }
    public string? ErrorCode { get; init; }
    public IDictionary<string, JsonElement> ErrorDetail { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record JobUpdate
{
    public required JobSnapshot Snapshot { get; init; }
    public required IReadOnlyList<StageEvent> Events { get; init; }
    public required IReadOnlyList<ItemOutcome> Outcomes { get; init; }
    public required int ThroughSequence { get; init; }
    public bool More { get; init; }
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JobCommand
{
    public required string CommandId { get; init; }
    public required JobCommandKind Kind { get; init; }
    public required string JobId { get; init; }
    public IReadOnlyList<string> ItemIds { get; init; } = Array.Empty<string>();
    public JobPriority? PriorityOverride { get; init; }
}

public sealed record ResultEntry
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Free-form result payload. Defaults to an empty object on the wire.</summary>
    public IDictionary<string, JsonElement> Payload { get; init; } = new Dictionary<string, JsonElement>();
    public string? ErrorCode { get; init; }
}

public sealed record PipelineSpec
{
    public required string Name { get; init; }
    public int? TtlSeconds { get; init; }
    public bool Pinned { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecognitionMode? RecognitionMode { get; init; }
}

public sealed record ResidencyEntry
{
    public required string Pipeline { get; init; }
    public required ResidencyKind Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecognitionMode? RecognitionMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecognitionResourceKind? ResourceKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceId { get; init; }
    public int ActiveLeases { get; init; }
    public int? RemainingTtlSeconds { get; init; }
    public int? EstimatedVramMb { get; init; }
    public EvictionReason EvictionReason { get; init; } = EvictionReason.None;
}

public sealed record ResidencyStatus
{
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public int DefaultTtlSeconds { get; init; } = 300;
    public IReadOnlyList<ResidencyEntry> Entries { get; init; } = Array.Empty<ResidencyEntry>();
    public IReadOnlyList<PipelineSpec> Pipelines { get; init; } = Array.Empty<PipelineSpec>();
    public int? VramTotalMb { get; init; }
    public int? VramUsedMb { get; init; }
}

public sealed record RuntimeSourceIdentity
{
    public required string BackendVersion { get; init; }
    public required string BackendSourceSha { get; init; }
    public required string RuntimeManifestSha256 { get; init; }
    public required string ProtocolVersion { get; init; }
    public required string ProtocolManifestSha256 { get; init; }
}

public sealed record RuntimeMaintenanceRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationId { get; init; }
    public required RuntimeMaintenanceOperation Operation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfileId { get; init; }
    /// <summary>
    /// Confirmed install plan id (runtime.install-plan.v1). Valid for ensure
    /// only, requires an explicit OperationId, and is mutually exclusive with
    /// ProfileId, ComponentIds, InstallComponentIds and DownloadSourceIds.
    /// Null keeps the legacy semantics without plan confirmation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ComponentIds { get; init; }
    /// <summary>
    /// Manual install scope for ensure (runtime.component-selection.v1) as
    /// stable component ids. Null omits the wire field (Backend default set),
    /// while an empty list explicitly selects no optional components.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InstallComponentIds { get; init; }
    /// <summary>
    /// Download source ids snapshotted into this ensure operation. Null uses
    /// the current Backend setting/default; at most one id is valid per kind.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DownloadSourceIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }
}

public sealed record RuntimeMaintenanceCommand
{
    public required string CommandId { get; init; }
    public required RuntimeMaintenanceCommandKind Command { get; init; }
    public required string TargetOperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewOperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpectedSequence { get; init; }
    /// <summary>
    /// Fresh install plan id (runtime.install-plan.v1) for a retry that
    /// replaces the source operation's plan confirmation. Invalid for cancel
    /// and mutually exclusive with the selection overrides; when present,
    /// RequiredCapabilities must contain runtime.install-plan.v1.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanId { get; init; }
    /// <summary>
    /// On retry, explicitly re-selects a still-compatible install scope
    /// (runtime.component-selection.v1). Null omits the wire field and reuses
    /// the source operation's normalized intent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InstallComponentIds { get; init; }
    /// <summary>
    /// On retry, explicitly replaces the source operation's normalized
    /// download source intent. Null reuses it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DownloadSourceIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }
}

public sealed record RuntimeComponentStatus
{
    public required string ComponentId { get; init; }
    public required string DisplayName { get; init; }
    public required RuntimeComponentState State { get; init; }
    public string? Version { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeComponentDesiredState? DesiredState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DesiredVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeComponentActualState? ActualState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActualVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeDriftReason? DriftReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Repairable { get; init; }
}

public sealed record RuntimeProfileStatus
{
    public required string ProfileId { get; init; }
    public required RuntimeAccelerator Accelerator { get; init; }
    public IReadOnlyList<RuntimeComponentStatus> Components { get; init; } =
        Array.Empty<RuntimeComponentStatus>();
}

public sealed record RuntimeMaintenanceStatus
{
    public required string OperationId { get; init; }
    public string? SourceOperationId { get; init; }
    public required int Sequence { get; init; }
    public required RuntimeMaintenanceOperation Operation { get; init; }
    public required RuntimeOperationState OperationState { get; init; }
    public required RuntimeMaintenancePhase Phase { get; init; }
    public required string ProfileId { get; init; }
    public string? ComponentId { get; init; }
    public required string UpdatedAt { get; init; }
    public ProgressSnapshot? Progress { get; init; }
    public string? MessageCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequestedComponentIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EffectiveComponentIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequestedDownloadSourceIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EffectiveDownloadSourceIds { get; init; }
    /// <summary>Install plan id echo when this operation confirmed a previewed plan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanId { get; init; }
}

[JsonConverter(typeof(RuntimeInstallPlanRequestJsonConverter))]
public sealed record RuntimeInstallPlanRequest
{
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }
    /// <summary>Null keeps the persistent preference or product default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeAccelerator? Accelerator { get; init; }
    /// <summary>
    /// Manual install scope (runtime.component-selection.v1): null omits the
    /// wire field (Backend default), an empty list explicitly selects no
    /// optional components.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InstallComponentIds { get; init; }
    /// <summary>
    /// Download source selection (runtime.download-sources.v1); must be
    /// non-empty when present, null omits the field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DownloadSourceIds { get; init; }
}

public sealed record RuntimeInstallPlanResponse : IJsonOnDeserialized
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    [JsonRequired]
    public required RuntimeInstallPlan Plan { get; init; }
    [JsonRequired]
    public IReadOnlyList<string> NegotiatedCapabilities { get; init; } = Array.Empty<string>();

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (SchemaVersion != 2 || Plan is null)
            throw new JsonException("Invalid install plan response.");
        InstallPlanResponseValidation.Ids(NegotiatedCapabilities);
    }
}

public sealed record RuntimeInstallPlan : IJsonOnDeserialized
{
    [JsonRequired]
    public required string PlanId { get; init; }
    [JsonRequired]
    public required string ExpiresAt { get; init; }
    [JsonRequired]
    public required RuntimeAccelerator Accelerator { get; init; }
    [JsonRequired]
    public required string ProfileId { get; init; }
    /// <summary>Nullable request echo: null means omitted, empty means explicit empty selection.</summary>
    [JsonRequired]
    public required IReadOnlyList<string>? RequestedComponentIds { get; init; }
    [JsonRequired]
    public IReadOnlyList<string> EffectiveComponentIds { get; init; } = Array.Empty<string>();
    /// <summary>Nullable request echo: null means omitted, never an empty list.</summary>
    [JsonRequired]
    public required IReadOnlyList<string>? RequestedDownloadSourceIds { get; init; }
    [JsonRequired]
    public IReadOnlyList<string> EffectiveDownloadSourceIds { get; init; } = Array.Empty<string>();
    [JsonRequired]
    public required RuntimeSourceIdentity Source { get; init; }
    [JsonRequired]
    public IReadOnlyList<RuntimeInstallPlanComponent> Components { get; init; } =
        Array.Empty<RuntimeInstallPlanComponent>();
    [JsonRequired]
    public IReadOnlyList<RuntimeInstallPlanBlocker> Blockers { get; init; } =
        Array.Empty<RuntimeInstallPlanBlocker>();
    [JsonRequired]
    public required RuntimeInstallPlanCost Cost { get; init; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        InstallPlanResponseValidation.Text(PlanId);
        InstallPlanResponseValidation.Text(ProfileId);
        if (!DateTimeOffset.TryParse(ExpiresAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)
            || Source is null || Cost is null || Components is null || Blockers is null
            || Components.Any(item => item is null) || Blockers.Any(item => item is null))
            throw new JsonException("Invalid install plan fields.");
        InstallPlanResponseValidation.Ids(Components.Select(item => item.ComponentId).ToArray());
        InstallPlanResponseValidation.Ids(EffectiveComponentIds);
        InstallPlanResponseValidation.Ids(EffectiveDownloadSourceIds);
        if (RequestedComponentIds is not null)
            InstallPlanResponseValidation.Ids(RequestedComponentIds);
        if (RequestedDownloadSourceIds is not null)
            InstallPlanResponseValidation.Ids(RequestedDownloadSourceIds, allowEmpty: false);
    }
}

public sealed record RuntimeInstallPlanComponent : IJsonOnDeserialized
{
    [JsonRequired]
    public required string ComponentId { get; init; }
    [JsonRequired]
    public required RuntimeInstallPlanAction Action { get; init; }
    [JsonRequired]
    public required RuntimeInstallPlanDependencyState DependencyState { get; init; }
    [JsonRequired]
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();

    void IJsonOnDeserialized.OnDeserialized()
    {
        InstallPlanResponseValidation.Text(ComponentId);
        InstallPlanResponseValidation.Ids(ReasonCodes);
    }
}

public sealed record RuntimeInstallPlanBlocker : IJsonOnDeserialized
{
    [JsonRequired]
    public required string Code { get; init; }
    /// <summary>Optional stable component id the blocker is about.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentId { get; init; }
    [JsonRequired]
    public required string NextAction { get; init; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        InstallPlanResponseValidation.Text(Code);
        InstallPlanResponseValidation.Text(NextAction);
        if (ComponentId is not null) InstallPlanResponseValidation.Text(ComponentId);
    }
}

public sealed record RuntimeInstallPlanCost : IJsonOnDeserialized
{
    /// <summary>Plan-wide deduplicated total; null means honestly unknown.</summary>
    [JsonRequired]
    public required long? DownloadBytes { get; init; }
    /// <summary>Plan-wide deduplicated total; null means honestly unknown.</summary>
    [JsonRequired]
    public required long? AdditionalDiskBytes { get; init; }
    [JsonRequired]
    public IReadOnlyList<string> UnknownReasonCodes { get; init; } = Array.Empty<string>();

    void IJsonOnDeserialized.OnDeserialized()
    {
        InstallPlanResponseValidation.Ids(UnknownReasonCodes);
        if (DownloadBytes < 0 || AdditionalDiskBytes < 0
            || ((DownloadBytes is null || AdditionalDiskBytes is null) && UnknownReasonCodes.Count == 0))
            throw new JsonException("Unknown install cost requires a reason; known cost must be non-negative.");
    }
}

public sealed record RuntimeMaintenanceReceipt
{
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public required string OperationId { get; init; }
    public required RuntimeMaintenanceStatus Snapshot { get; init; }
    public IReadOnlyList<string> NegotiatedCapabilities { get; init; } = Array.Empty<string>();
}

public sealed record RuntimeMaintenanceEvent
{
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public required RuntimeMaintenanceEventType EventType { get; init; }
    public required int Sequence { get; init; }
    public required RuntimeMaintenanceOperation Operation { get; init; }
    public required RuntimeMaintenanceStatus Snapshot { get; init; }
    public required string MessageCode { get; init; }
    public IDictionary<string, string> MessageArgs { get; init; } =
        new Dictionary<string, string>();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackMessage { get; init; }
}

public sealed record RuntimeMaintenanceUpdate
{
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public required string OperationId { get; init; }
    public required RuntimeMaintenanceStatus Snapshot { get; init; }
    public IReadOnlyList<RuntimeMaintenanceEvent> Events { get; init; } =
        Array.Empty<RuntimeMaintenanceEvent>();
    public required int OldestSequence { get; init; }
    public required int ThroughSequence { get; init; }
    public required bool More { get; init; }
    public string? ReplayExpiresAt { get; init; }
}

public sealed record RuntimeStatusSnapshot
{
    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public required string InstanceId { get; init; }
    public required RuntimeServiceState ServiceState { get; init; }
    public required string BackendVersion { get; init; }
    public required RuntimeProfileStatus Profile { get; init; }
    public RuntimeMaintenanceStatus? Maintenance { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeSourceIdentity? Source { get; init; }
}

/// <summary>
/// The nested ``residency`` object inside SettingsSnapshot. Kept as its own
/// record so the wire shape exactly matches the Python to_payload() nesting.
/// </summary>
public sealed record SettingsResidency
{
    public int DefaultTtlSeconds { get; init; } = 300;
    public IReadOnlyList<PipelineSpec> Pipelines { get; init; } = Array.Empty<PipelineSpec>();
}

public sealed record SettingsSnapshot
{
    private IReadOnlyList<string>? _downloadSourceIds;

    public int SchemaVersion { get; init; } = HttpV2Schema.Version;
    public SettingsResidency Residency { get; init; } = new();
    /// <summary>Extra backend settings (transport-neutral key/value bag).</summary>
    public IDictionary<string, JsonElement> Extra { get; init; } = new Dictionary<string, JsonElement>();
    /// <summary>
    /// The user's download source selection (runtime.download-sources.v1) as
    /// stable source ids, at most one per source kind. Null or an empty list
    /// serializes as omission and delegates to the Backend-declared defaults;
    /// unknown ids fail closed.
    /// </summary>
    [JsonPropertyName("download_source_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DownloadSourceIds
    {
        get => _downloadSourceIds is { Count: > 0 } ? _downloadSourceIds : null;
        init => _downloadSourceIds = value;
    }
}

public sealed record HttpV2ErrorPayload
{
    public required int SchemaVersion { get; init; }
    public string? InstanceId { get; init; }
    public required HttpV2ErrorCode Code { get; init; }
    public required string Message { get; init; }
    public required ErrorCategory Category { get; init; }
    public required bool Retryable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfter { get; init; }
    /// <summary>Typed error detail. Defaults to an empty object on the wire.</summary>
    public IDictionary<string, JsonElement> Detail { get; init; } = new Dictionary<string, JsonElement>();
    public string? JobId { get; init; }
}


internal static class InstallPlanResponseValidation
{
    internal static void Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw new JsonException("Install plan text fields must be non-empty strings.");
    }

    internal static void Ids(IReadOnlyList<string>? values, bool allowEmpty = true)
    {
        if (values is null || (!allowEmpty && values.Count == 0))
            throw new JsonException("Invalid install plan id array.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in values)
            if (string.IsNullOrEmpty(item) || !seen.Add(item))
                throw new JsonException("Install plan ids must be unique non-empty strings.");
    }
}
