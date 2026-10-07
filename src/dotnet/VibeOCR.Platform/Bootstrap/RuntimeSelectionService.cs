using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>Discriminating failure kinds produced by catalog validation.</summary>
public enum RuntimeSelectionErrorKind
{
    /// <summary>The runtime does not advertise the capability owning a catalog.</summary>
    CapabilityMissing,

    /// <summary>A catalog entry has blank business fields or a catalog repeats.</summary>
    InvalidCatalogEntry,

    /// <summary>A catalog declares a duplicate business key.</summary>
    DuplicateCatalogEntry,

    /// <summary>The requested engine id is not in the engine catalog.</summary>
    UnknownEngine,

    /// <summary>The engine exists but is currently unusable; fail closed.</summary>
    EngineUnavailable,

    /// <summary>The requested download source id is not in the source catalog.</summary>
    UnknownSource,

    /// <summary>More than one selected source for the same source kind.</summary>
    DuplicateSourceKind,

    /// <summary>No component variant matches the feature for the accelerator.</summary>
    UnknownFeature,
}

/// <summary>Catalog-driven selection error; never falls back to a guessed default.</summary>
public sealed class RuntimeSelectionException : InvalidOperationException
{
    public RuntimeSelectionException(RuntimeSelectionErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public RuntimeSelectionErrorKind Kind { get; }
}

/// <summary>
/// One catalog engine projected onto the request-side engine enum. Availability
/// is data for the caller; only <see cref="SelectEngine"/> enforces policy.
/// </summary>
public sealed record RuntimeEngineOption(
    OcrEngine Engine,
    Wire.OcrEngineAvailability Availability,
    bool IncludedInBase,
    string? ReasonCode,
    string? RequiredComponent)
{
    public bool IsUsable =>
        Availability is Wire.OcrEngineAvailability.Ready
        or Wire.OcrEngineAvailability.PreparationRequired;
}

/// <summary>MinerU tier 目录投影：稳定 wire id + 可用性，供设置页动态渲染。</summary>
public sealed record MineruTierOption(
    string Id,
    string Availability,
    string? ReasonCode)
{
    public bool IsUsable =>
        Availability is "ready" or "preparation_required";
}

/// <summary>
/// UI-neutral selection module over a runtime health snapshot. It owns catalog
/// structural validation (unique engine ids, globally unique source ids,
/// unique feature+accelerator variants) and maps user preferences to the
/// request-side engine selection and the immutable maintenance install intent.
/// Endpoints and Python package names stay opaque; only stable ids cross this
/// boundary.
/// </summary>
public sealed class RuntimeSelectionService
{
    public const string EngineSelectionCapability = "ocr.engine-selection.v1";
    public const string RecognitionModesCapability = RecognitionModeCatalog.Capability;
    public const string DownloadSourceCapability = "runtime.download-sources.v1";
    public const string ComponentSelectionCapability = "runtime.component-selection.v1";
    /// <summary>
    /// 远程 MinerU API 能力，直接引用 Protocol SDK 常量（Protocol 2.9.0
    /// 新增）；只有声明该能力的 Backend 才接受 extra.mineru_connection
    /// 的远程写入，旧 Backend 明确失败。
    /// </summary>
    public const string MineruRemoteApiCapability =
        VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.OCR_MINERU_REMOTE_API_V1;
    public const string MineruConfigCapability =
        VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.OCR_MINERU_CONFIG_V1;
    /// <summary>
    /// 默认识别模式持久化能力（ocr.default-recognition-mode.v1，Protocol
    /// 2.10.0 新增）。只有声明该能力的 Backend 才接受
    /// extra.default_recognition_mode 写入；旧 Backend 不写未知键。
    /// </summary>
    public const string DefaultRecognitionModeCapability =
        VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.OCR_DEFAULT_RECOGNITION_MODE_V1;

    private readonly Wire.OcrEngineCatalog? _engineCatalog;
    private readonly Wire.DownloadSourceCatalog? _sourceCatalog;
    private readonly Wire.ComponentVariantCatalog? _variantCatalog;
    private readonly Wire.MineruConfigCatalog? _mineruConfigCatalog;
    private readonly Dictionary<string, Wire.OcrEngineDescriptor> _engineById;
    private readonly Dictionary<string, Wire.DownloadSourceDescriptor> _sourcesById;
    private readonly Dictionary<(string FeatureId, string Accelerator), string>
        _componentByVariant;
    private readonly RecognitionModeCatalog? _recognitionModes;

    public RuntimeSelectionService(Wire.Health health)
    {
        ArgumentNullException.ThrowIfNull(health);
        Health = health;
        Wire.OcrEngineCatalog? engineCatalog = null;
        Wire.RecognitionModeCatalog? recognitionModeCatalog = null;
        Wire.DownloadSourceCatalog? sourceCatalog = null;
        Wire.ComponentVariantCatalog? variantCatalog = null;
        Wire.MineruConfigCatalog? mineruConfigCatalog = null;
        foreach (Wire.CapabilityDescriptor descriptor in health.CapabilityDescriptors ?? [])
        {
            if (descriptor.OcrEngineCatalog is not null)
            {
                engineCatalog = SingleCatalog(engineCatalog, descriptor.OcrEngineCatalog, "ocr_engine_catalog");
            }
            if (descriptor.RecognitionModeCatalog is not null)
            {
                if (descriptor.Name != RecognitionModesCapability)
                {
                    throw Error(
                        RuntimeSelectionErrorKind.InvalidCatalogEntry,
                        "Recognition mode catalog is attached to the wrong capability descriptor.");
                }
                recognitionModeCatalog = SingleCatalog(
                    recognitionModeCatalog,
                    descriptor.RecognitionModeCatalog,
                    "recognition_mode_catalog");
            }
            if (descriptor.DownloadSourceCatalog is not null)
            {
                sourceCatalog = SingleCatalog(
                    sourceCatalog, descriptor.DownloadSourceCatalog, "download_source_catalog");
            }
            if (descriptor.ComponentVariantCatalog is not null)
            {
                variantCatalog = SingleCatalog(
                    variantCatalog, descriptor.ComponentVariantCatalog, "component_variant_catalog");
            }
            if (descriptor.MineruConfigCatalog is not null)
            {
                mineruConfigCatalog = SingleCatalog(
                    mineruConfigCatalog, descriptor.MineruConfigCatalog, "mineru_config_catalog");
            }
        }

        _engineCatalog = engineCatalog;
        bool advertisesRecognitionModes = health.Capabilities.Contains(
            RecognitionModesCapability,
            StringComparer.Ordinal);
        if (advertisesRecognitionModes != (recognitionModeCatalog is not null))
        {
            throw Error(
                RuntimeSelectionErrorKind.InvalidCatalogEntry,
                "Recognition mode capability and catalog must be advertised together.");
        }
        _recognitionModes = recognitionModeCatalog is null
            ? null
            : RecognitionModeCatalog.FromWire(recognitionModeCatalog);
        _sourceCatalog = sourceCatalog;
        _variantCatalog = variantCatalog;
        _mineruConfigCatalog = mineruConfigCatalog;

        _engineById = [];
        foreach (Wire.OcrEngineDescriptor engine in engineCatalog?.Engines ?? [])
        {
            if (!_engineById.TryAdd(engine.Id.ToString(), engine))
            {
                throw Error(
                    RuntimeSelectionErrorKind.DuplicateCatalogEntry,
                    $"Engine catalog declares engine '{engine.Id}' more than once.");
            }
        }

        _sourcesById = [];
        foreach (Wire.DownloadSourceDescriptor source in sourceCatalog?.Sources ?? [])
        {
            if (string.IsNullOrWhiteSpace(source.Id) ||
                string.IsNullOrWhiteSpace(source.Kind) ||
                string.IsNullOrWhiteSpace(source.Endpoint))
            {
                throw Error(
                    RuntimeSelectionErrorKind.InvalidCatalogEntry,
                    "Download source catalog contains a blank entry field.");
            }
            // Protocol 只要求 source id 跨 kind 全局唯一。
            if (!_sourcesById.TryAdd(source.Id, source))
            {
                throw Error(
                    RuntimeSelectionErrorKind.DuplicateCatalogEntry,
                    $"Download source catalog declares id '{source.Id}' more than once.");
            }
        }

        _componentByVariant = [];
        foreach (Wire.ComponentVariantDescriptor variant in variantCatalog?.Variants ?? [])
        {
            if (string.IsNullOrWhiteSpace(variant.FeatureId) ||
                string.IsNullOrWhiteSpace(variant.Accelerator) ||
                string.IsNullOrWhiteSpace(variant.ComponentId))
            {
                throw Error(
                    RuntimeSelectionErrorKind.InvalidCatalogEntry,
                    "Component variant catalog contains a blank entry field.");
            }
            if (!_componentByVariant.TryAdd(
                    (variant.FeatureId, variant.Accelerator),
                    variant.ComponentId))
            {
                throw Error(
                    RuntimeSelectionErrorKind.DuplicateCatalogEntry,
                    $"Component variant catalog declares feature '{variant.FeatureId}' "
                    + $"for accelerator '{variant.Accelerator}' more than once.");
            }
        }
    }

    public Wire.Health Health { get; }

    public bool SupportsEngineSelection => _engineCatalog is not null;
    public bool SupportsRecognitionModes => _recognitionModes is not null;
    public bool SupportsDownloadSources => _sourceCatalog is not null;
    public bool SupportsComponentSelection => _variantCatalog is not null;

    /// <summary>
    /// Backend 是否声明远程 MinerU API（ocr.mineru-remote-api.v1）。
    /// </summary>
    public bool SupportsMineruRemoteApi => Health.Capabilities.Contains(
        MineruRemoteApiCapability,
        StringComparer.Ordinal);

    /// <summary>
    /// Backend 是否声明类型化 MinerU 4 配置（ocr.mineru-config.v1）。只有声明
    /// 该能力的 Backend 才接受 pipeline.mineru 块。
    /// </summary>
    public bool SupportsMineruConfig => _mineruConfigCatalog is not null;

    /// <summary>
    /// Backend 是否声明默认识别模式持久化（ocr.default-recognition-mode.v1）。
    /// 未声明时设置页只读展示兼容说明，不向旧 Backend 写入未知键。
    /// </summary>
    public bool SupportsDefaultRecognitionMode => Health.Capabilities.Contains(
        DefaultRecognitionModeCapability,
        StringComparer.Ordinal);

    /// <summary>
    /// mineru_document 任务的类型化配置：tier 和语言必须来自可用目录，
    /// 其余字段用 auto/all 生效值；不发送任何遗留 engine 选项。非
    /// mineru_document 或未声明 ocr.mineru-config.v1 时返回 null（完全省略
    /// mineru 块，保持遗留 payload 形态）。
    /// </summary>
    public MineruConfig? MineruConfigFor(string? modeId) =>
        MineruConfigFor(modeId, preference: null);

    /// <summary>
    /// 带 persisted 偏好的解析：偏好内的 tier/language 仍须在当前目录内
    /// 可用（未知或不可用 fail closed，不静默降级目录默认）；偏好为 null
    /// 时保持目录默认行为。extra.mineru_recognition 的语法校验由
    /// MineruRecognitionSettings 负责，本方法只做目录层校验。
    /// </summary>
    public MineruConfig? MineruConfigFor(
        string? modeId,
        MineruRecognitionPreference? preference)
    {
        if (_mineruConfigCatalog is null ||
            !string.Equals(modeId, "mineru_document", StringComparison.Ordinal))
        {
            return null;
        }
        return preference is null
            ? MineruConfigForDefaults(_mineruConfigCatalog)
            : MineruConfigForPreference(_mineruConfigCatalog, preference);
    }

    private static MineruConfig MineruConfigForDefaults(Wire.MineruConfigCatalog catalog)
    {
        MineruTier tier = ToRequestTier(catalog.DefaultTier);
        Wire.MineruTierDescriptor[] defaults = [.. catalog.Tiers.Where(
            item => item.Id == catalog.DefaultTier)];
        if (defaults.Length != 1)
        {
            throw Error(RuntimeSelectionErrorKind.InvalidCatalogEntry,
                "MinerU default tier is missing or duplicated in the runtime catalog.");
        }
        if (defaults[0].Availability is not (
            Wire.MineruTierAvailability.Ready or
            Wire.MineruTierAvailability.PreparationRequired))
        {
            throw Error(RuntimeSelectionErrorKind.EngineUnavailable,
                "MinerU default tier is unavailable.");
        }
        string? language = catalog.Languages.Contains("ch", StringComparer.Ordinal)
            ? "ch"
            : catalog.Languages.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (language is null)
        {
            throw Error(RuntimeSelectionErrorKind.InvalidCatalogEntry,
                "MinerU catalog does not declare a usable language.");
        }
        return new MineruConfig(tier, MineruOcrMode.Auto, "all", language);
    }

    private static MineruConfig MineruConfigForPreference(
        Wire.MineruConfigCatalog catalog,
        MineruRecognitionPreference preference)
    {
        Wire.MineruTierId tierId = ToWireTierId(preference.Tier);
        Wire.MineruTierDescriptor[] matches = [.. catalog.Tiers.Where(
            item => item.Id == tierId)];
        if (matches.Length == 0)
        {
            throw Error(RuntimeSelectionErrorKind.UnknownEngine,
                $"MinerU tier '{tierId}' is not in the runtime catalog.");
        }
        if (matches.Length > 1)
        {
            throw Error(RuntimeSelectionErrorKind.InvalidCatalogEntry,
                $"MinerU catalog declares tier '{tierId}' more than once.");
        }
        if (matches[0].Availability is not (
            Wire.MineruTierAvailability.Ready or
            Wire.MineruTierAvailability.PreparationRequired))
        {
            throw Error(RuntimeSelectionErrorKind.EngineUnavailable,
                $"MinerU tier '{tierId}' is unavailable"
                + (string.IsNullOrWhiteSpace(matches[0].ReasonCode)
                    ? "."
                    : $" ({matches[0].ReasonCode})."));
        }
        if (!catalog.Languages.Contains(preference.Language, StringComparer.Ordinal))
        {
            throw Error(RuntimeSelectionErrorKind.UnknownEngine,
                $"MinerU language '{preference.Language}' is not offered by the runtime catalog.");
        }
        return new MineruConfig(
            preference.Tier, preference.OcrMode, preference.PageRange, preference.Language);
    }

    private static Wire.MineruTierId ToWireTierId(MineruTier tier) => tier switch
    {
        MineruTier.Flash => Wire.MineruTierId.Flash,
        MineruTier.Basic => Wire.MineruTierId.Basic,
        MineruTier.Standard => Wire.MineruTierId.Standard,
        MineruTier.Advanced => Wire.MineruTierId.Advanced,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown MinerU tier."),
    };

    private static MineruTier ToRequestTier(Wire.MineruTierId tier) => tier switch
    {
        Wire.MineruTierId.Basic => MineruTier.Basic,
        Wire.MineruTierId.Flash => MineruTier.Flash,
        Wire.MineruTierId.Standard => MineruTier.Standard,
        Wire.MineruTierId.Advanced => MineruTier.Advanced,
        _ => throw Error(RuntimeSelectionErrorKind.InvalidCatalogEntry,
            "MinerU catalog declares an unknown default tier."),
    };

    /// <summary>指定 MinerU tier 是否在目录中可用；未声明目录时 false。</summary>
    public bool IsMineruTierUsable(MineruTier tier)
    {
        if (_mineruConfigCatalog is null) return false;
        Wire.MineruTierId id = tier switch
        {
            MineruTier.Basic => Wire.MineruTierId.Basic,
            MineruTier.Flash => Wire.MineruTierId.Flash,
            MineruTier.Standard => Wire.MineruTierId.Standard,
            MineruTier.Advanced => Wire.MineruTierId.Advanced,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown MinerU tier."),
        };
        return _mineruConfigCatalog.Tiers.Any(item =>
            item.Id == id &&
            item.Availability is Wire.MineruTierAvailability.Ready
                or Wire.MineruTierAvailability.PreparationRequired);
    }

    /// <summary>目录内 MinerU tier 投影（设置页动态可用性数据源）；无目录时为空。</summary>
    public IReadOnlyList<MineruTierOption> MineruTiers =>
        _mineruConfigCatalog is { } catalog
            ? [.. catalog.Tiers.Select(ToTierOption)]
            : Array.Empty<MineruTierOption>();

    /// <summary>目录声明的 MinerU 语言（上游 OCR hint 值原样）；无目录时为空。</summary>
    public IReadOnlyList<string> MineruLanguages =>
        _mineruConfigCatalog?.Languages ?? Array.Empty<string>();

    /// <summary>目录默认 tier 的稳定 wire id；未声明目录时为 null。</summary>
    public string? MineruDefaultTier =>
        _mineruConfigCatalog is null ? null : TierIdName(_mineruConfigCatalog.DefaultTier);

    private static MineruTierOption ToTierOption(Wire.MineruTierDescriptor tier) => new(
        TierIdName(tier.Id),
        tier.Availability switch
        {
            Wire.MineruTierAvailability.Ready => "ready",
            Wire.MineruTierAvailability.PreparationRequired => "preparation_required",
            _ => "unavailable",
        },
        tier.ReasonCode);

    private static string TierIdName(Wire.MineruTierId tier) => tier switch
    {
        Wire.MineruTierId.Flash => "flash",
        Wire.MineruTierId.Basic => "basic",
        Wire.MineruTierId.Standard => "standard",
        Wire.MineruTierId.Advanced => "advanced",
        _ => tier.ToString(),
    };

    /// <summary>Catalog engines in wire order; empty when the capability is absent.</summary>
    public IReadOnlyList<RuntimeEngineOption> EngineOptions
    {
        get
        {
            if (_recognitionModes is not null || _engineCatalog is null)
            {
                return Array.Empty<RuntimeEngineOption>();
            }
            return [.. _engineCatalog.Engines.Select(ToEngineOption)];
        }
    }

    public IReadOnlyList<RecognitionModeOption> RecognitionModes =>
        _recognitionModes?.Modes ?? Array.Empty<RecognitionModeOption>();

    public RecognitionModeOption SelectRecognitionMode(string modeId)
    {
        RecognitionModeOption mode = FindRecognitionMode(modeId);
        if (!mode.IsUsable)
            throw Error(RuntimeSelectionErrorKind.EngineUnavailable,
                $"Recognition mode '{modeId}' is unavailable.");
        return mode;
    }

    public RecognitionModeOption FindRecognitionMode(string modeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        RecognitionModeOption? mode = RecognitionModes.SingleOrDefault(item => item.Id == modeId);
        return mode ?? throw Error(RuntimeSelectionErrorKind.UnknownEngine,
            $"Recognition mode '{modeId}' is not in the runtime catalog.");
    }

    /// <summary>Catalog sources (kind stays an open string); empty when absent.</summary>
    public IReadOnlyList<Wire.DownloadSourceDescriptor> Sources =>
        _sourceCatalog?.Sources ?? Array.Empty<Wire.DownloadSourceDescriptor>();

    /// <summary>Catalog feature+accelerator variants; empty when absent.</summary>
    public IReadOnlyList<Wire.ComponentVariantDescriptor> Variants =>
        _variantCatalog?.Variants ?? Array.Empty<Wire.ComponentVariantDescriptor>();

    /// <summary>
    /// Validate a user engine choice against the catalog. Unknown ids and
    /// unavailable engines fail closed; a preparation-required engine is a
    /// legal choice whose maintenance is driven by
    /// <see cref="RequiredComponent"/>.
    /// </summary>
    public RuntimeEngineOption SelectEngine(OcrEngine engine)
    {
        if (_engineCatalog is null)
        {
            throw Missing(EngineSelectionCapability);
        }
        Wire.OcrEngineId wireId = ToWireEngineId(engine);
        if (!_engineById.TryGetValue(wireId.ToString(), out Wire.OcrEngineDescriptor? descriptor))
        {
            throw Error(
                RuntimeSelectionErrorKind.UnknownEngine,
                $"Engine '{wireId}' is not in the runtime engine catalog.");
        }
        RuntimeEngineOption option = ToEngineOption(descriptor);
        if (descriptor.Availability == Wire.OcrEngineAvailability.Unavailable)
        {
            throw Error(
                RuntimeSelectionErrorKind.EngineUnavailable,
                $"Engine '{wireId}' is unavailable"
                + (string.IsNullOrWhiteSpace(descriptor.ReasonCode)
                    ? "."
                    : $" ({descriptor.ReasonCode})."));
        }
        return option;
    }

    /// <summary>
    /// Validate selected download source ids: every id must exist in the
    /// catalog and each source kind may carry at most one selection. Null or
    /// empty input means "no explicit selection" and yields an empty list.
    /// </summary>
    public IReadOnlyList<string> NormalizeSourceSelection(
        IReadOnlyCollection<string>? sourceIds)
    {
        if (_sourceCatalog is null)
        {
            throw Missing(DownloadSourceCapability);
        }
        if (sourceIds is null || sourceIds.Count == 0)
        {
            return Array.Empty<string>();
        }
        var selected = new List<string>(sourceIds.Count);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in sourceIds)
        {
            if (string.IsNullOrWhiteSpace(id) ||
                !_sourcesById.TryGetValue(id, out Wire.DownloadSourceDescriptor? source))
            {
                throw Error(
                    RuntimeSelectionErrorKind.UnknownSource,
                    $"Download source '{id}' is not in the runtime source catalog.");
            }
            if (!kinds.Add(source.Kind))
            {
                throw Error(
                    RuntimeSelectionErrorKind.DuplicateSourceKind,
                    $"Source kind '{source.Kind}' has more than one selection "
                    + $"('{source.Id}' and a previous id).");
            }
            selected.Add(source.Id);
        }
        return selected;
    }

    /// <summary>
    /// Map feature ids for one accelerator to component ids via the variant
    /// catalog. Feature ids and component ids stay distinct even when the
    /// current Backend happens to name them identically.
    /// </summary>
    public IReadOnlyList<string> SelectComponentIds(
        string accelerator,
        IReadOnlyCollection<string>? featureIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        if (_variantCatalog is null)
        {
            throw Missing(ComponentSelectionCapability);
        }
        if (featureIds is null || featureIds.Count == 0)
        {
            return Array.Empty<string>();
        }
        var componentIds = new List<string>(featureIds.Count);
        foreach (string featureId in featureIds)
        {
            if (string.IsNullOrWhiteSpace(featureId) ||
                !_componentByVariant.TryGetValue(
                    (featureId, accelerator),
                    out string? componentId))
            {
                throw Error(
                    RuntimeSelectionErrorKind.UnknownFeature,
                    $"Feature '{featureId}' has no component variant for accelerator "
                    + $"'{accelerator}'.");
            }
            componentIds.Add(componentId);
        }
        return componentIds.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Persist the user's download source preference in Backend settings (the
    /// long-term source of truth). The update re-serializes the current
    /// snapshot so residency and extra settings survive; endpoints are never
    /// written.
    /// </summary>
    public async Task<SettingsSnapshot> ApplySourcePreferenceAsync(
        IInferenceClient inference,
        IReadOnlyCollection<string>? sourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inference);
        IReadOnlyList<string> normalized = NormalizeSourceSelection(sourceIds);
        SettingsSnapshot current = await inference
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        SettingsSnapshot updated = current with
        {
            DownloadSourceIds = normalized.Count == 0 ? null : normalized,
        };
        return await inference
            .UpdateSettingsAsync(updated, cancellationToken)
            .ConfigureAwait(false);
    }

    private static T SingleCatalog<T>(T? current, T next, string name) where T : class
    {
        if (current is not null)
        {
            throw Error(
                RuntimeSelectionErrorKind.InvalidCatalogEntry,
                $"Runtime health declares '{name}' on more than one capability descriptor.");
        }
        return next;
    }

    private static RuntimeEngineOption ToEngineOption(Wire.OcrEngineDescriptor descriptor) => new(
        ToRequestEngine(descriptor.Id),
        descriptor.Availability,
        descriptor.IncludedInBase,
        descriptor.ReasonCode,
        descriptor.RequiredComponent);

    private static Wire.OcrEngineId ToWireEngineId(OcrEngine engine) => engine switch
    {
        OcrEngine.RapidOcr => Wire.OcrEngineId.Rapidocr,
        OcrEngine.Windows => Wire.OcrEngineId.Windows,
        OcrEngine.PaddleOcr => Wire.OcrEngineId.Paddleocr,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
    };

    private static OcrEngine ToRequestEngine(Wire.OcrEngineId engine) => engine switch
    {
        Wire.OcrEngineId.Rapidocr => OcrEngine.RapidOcr,
        Wire.OcrEngineId.Windows => OcrEngine.Windows,
        Wire.OcrEngineId.Paddleocr => OcrEngine.PaddleOcr,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
    };

    private static RuntimeSelectionException Missing(string capability) =>
        Error(
            RuntimeSelectionErrorKind.CapabilityMissing,
            $"Runtime health does not advertise '{capability}'.");

    private static RuntimeSelectionException Error(
        RuntimeSelectionErrorKind kind,
        string message) => new(kind, message);
}
