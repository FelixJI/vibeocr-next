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
    public const string DownloadSourceCapability = "runtime.download-sources.v1";
    public const string ComponentSelectionCapability = "runtime.component-selection.v1";

    private readonly Wire.OcrEngineCatalog? _engineCatalog;
    private readonly Wire.DownloadSourceCatalog? _sourceCatalog;
    private readonly Wire.ComponentVariantCatalog? _variantCatalog;
    private readonly Dictionary<string, Wire.OcrEngineDescriptor> _engineById;
    private readonly Dictionary<string, Wire.DownloadSourceDescriptor> _sourcesById;
    private readonly Dictionary<(string FeatureId, string Accelerator), string>
        _componentByVariant;

    public RuntimeSelectionService(Wire.Health health)
    {
        ArgumentNullException.ThrowIfNull(health);
        Health = health;
        Wire.OcrEngineCatalog? engineCatalog = null;
        Wire.DownloadSourceCatalog? sourceCatalog = null;
        Wire.ComponentVariantCatalog? variantCatalog = null;
        foreach (Wire.CapabilityDescriptor descriptor in health.CapabilityDescriptors ?? [])
        {
            if (descriptor.OcrEngineCatalog is not null)
            {
                engineCatalog = SingleCatalog(engineCatalog, descriptor.OcrEngineCatalog, "ocr_engine_catalog");
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
        }

        _engineCatalog = engineCatalog;
        _sourceCatalog = sourceCatalog;
        _variantCatalog = variantCatalog;

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
    public bool SupportsDownloadSources => _sourceCatalog is not null;
    public bool SupportsComponentSelection => _variantCatalog is not null;

    /// <summary>Catalog engines in wire order; empty when the capability is absent.</summary>
    public IReadOnlyList<RuntimeEngineOption> EngineOptions
    {
        get
        {
            if (_engineCatalog is null)
            {
                return Array.Empty<RuntimeEngineOption>();
            }
            return [.. _engineCatalog.Engines.Select(ToEngineOption)];
        }
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
