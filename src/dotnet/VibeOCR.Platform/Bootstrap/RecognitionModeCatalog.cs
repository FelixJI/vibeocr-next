using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// UI-neutral projection of one typed Protocol recognition-mode descriptor.
/// Pipeline and engine remain the existing job wire fields; the mode id is the
/// user-facing semantic choice that binds them together.
/// </summary>
public sealed record RecognitionModeOption(
    string Id,
    string Family,
    string PipelineId,
    OcrEngine? Engine,
    string Provisioning,
    string Availability,
    string? ReasonCode,
    string? RequiredComponent,
    IReadOnlyList<string> SupportedOptions,
    string LifecycleKind,
    bool SupportsPreload,
    bool SupportsTtl,
    bool SupportsPinning,
    bool SupportsRelease)
{
    public bool IsUsable => Availability is "ready" or "preparation_required";
}

/// <summary>
/// Validated Protocol 2.8 recognition-mode catalog. The generated SDK owns the
/// wire shape and enums; this adapter only enforces the cross-field invariants
/// that make each stable mode id unambiguous to the product.
/// </summary>
public sealed record RecognitionModeCatalog(IReadOnlyList<RecognitionModeOption> Modes)
{
    public const string Capability = "ocr.recognition-modes.v1";

    private sealed record ExpectedMode(
        Wire.RecognitionModeFamily Family,
        Wire.ExecutionPipelineId Pipeline,
        Wire.OcrEngineId? Engine,
        Wire.RecognitionModeProvisioning Provisioning,
        Wire.RecognitionModeLifecycleKind LifecycleKind,
        bool SupportsPreload,
        bool SupportsTtl,
        bool SupportsPinning,
        bool SupportsRelease);

    private static readonly IReadOnlyDictionary<Wire.RecognitionModeId, ExpectedMode> Expected =
        new Dictionary<Wire.RecognitionModeId, ExpectedMode>
        {
            [Wire.RecognitionModeId.RapidText] = Mode(
                Wire.RecognitionModeFamily.Text,
                Wire.ExecutionPipelineId.OCR,
                Wire.OcrEngineId.Rapidocr,
                Wire.RecognitionModeProvisioning.BaseRuntime,
                Wire.RecognitionModeLifecycleKind.Unmanaged,
                false, false, false, false),
            [Wire.RecognitionModeId.WindowsText] = Mode(
                Wire.RecognitionModeFamily.Text,
                Wire.ExecutionPipelineId.OCR,
                Wire.OcrEngineId.Windows,
                Wire.RecognitionModeProvisioning.OperatingSystem,
                Wire.RecognitionModeLifecycleKind.Unmanaged,
                false, false, false, false),
            [Wire.RecognitionModeId.PaddleText] = Paddle(
                Wire.RecognitionModeFamily.Text,
                Wire.ExecutionPipelineId.OCR,
                Wire.OcrEngineId.Paddleocr),
            [Wire.RecognitionModeId.PaddleStructure] = Paddle(
                Wire.RecognitionModeFamily.Document,
                Wire.ExecutionPipelineId.PPStructureV3),
            [Wire.RecognitionModeId.PaddleDocumentVl] = Paddle(
                Wire.RecognitionModeFamily.Document,
                Wire.ExecutionPipelineId.PaddleOCRVL),
            [Wire.RecognitionModeId.MineruDocument] = Mode(
                Wire.RecognitionModeFamily.Document,
                Wire.ExecutionPipelineId.MinerU,
                null,
                Wire.RecognitionModeProvisioning.AdvancedComponent,
                Wire.RecognitionModeLifecycleKind.ProcessKeepAlive,
                false, true, false, true),
            [Wire.RecognitionModeId.PaddleTable] = Paddle(
                Wire.RecognitionModeFamily.Specialized,
                Wire.ExecutionPipelineId.TABLERECOGNITION),
            [Wire.RecognitionModeId.PaddleFormula] = Paddle(
                Wire.RecognitionModeFamily.Specialized,
                Wire.ExecutionPipelineId.FORMULARECOGNITION),
        };

    public static RecognitionModeCatalog FromWire(Wire.RecognitionModeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = new List<RecognitionModeOption>(catalog.Modes.Count);
        var ids = new HashSet<Wire.RecognitionModeId>();
        foreach (Wire.RecognitionModeDescriptor descriptor in catalog.Modes)
        {
            if (!ids.Add(descriptor.Id))
            {
                throw Invalid(
                    $"Recognition mode '{ModeId(descriptor.Id)}' is declared more than once.");
            }
            if (!Expected.TryGetValue(descriptor.Id, out ExpectedMode? expected) ||
                descriptor.Family != expected.Family ||
                descriptor.PipelineId != expected.Pipeline ||
                descriptor.Engine != expected.Engine ||
                descriptor.Provisioning != expected.Provisioning ||
                descriptor.Lifecycle.Kind != expected.LifecycleKind ||
                descriptor.Lifecycle.SupportsPreload != expected.SupportsPreload ||
                descriptor.Lifecycle.SupportsTtl != expected.SupportsTtl ||
                descriptor.Lifecycle.SupportsPinning != expected.SupportsPinning ||
                descriptor.Lifecycle.SupportsRelease != expected.SupportsRelease)
            {
                throw Invalid(
                    $"Recognition mode '{ModeId(descriptor.Id)}' violates its execution or lifecycle contract.");
            }
            if (descriptor.SupportedOptions.Any(string.IsNullOrWhiteSpace) ||
                descriptor.SupportedOptions.Distinct(StringComparer.Ordinal).Count() !=
                descriptor.SupportedOptions.Count)
            {
                throw Invalid(
                    $"Recognition mode '{ModeId(descriptor.Id)}' has invalid supported options.");
            }

            result.Add(new RecognitionModeOption(
                ModeId(descriptor.Id),
                Family(descriptor.Family),
                Pipeline(descriptor.PipelineId),
                descriptor.Engine is null ? null : RequestEngine(descriptor.Engine.Value),
                Provisioning(descriptor.Provisioning),
                Availability(descriptor.Availability),
                descriptor.ReasonCode,
                descriptor.RequiredComponent,
                descriptor.SupportedOptions,
                LifecycleKind(descriptor.Lifecycle.Kind),
                descriptor.Lifecycle.SupportsPreload,
                descriptor.Lifecycle.SupportsTtl,
                descriptor.Lifecycle.SupportsPinning,
                descriptor.Lifecycle.SupportsRelease));
        }
        if (ids.Count != Expected.Count)
        {
            throw Invalid("Recognition mode catalog must declare every stable mode exactly once.");
        }
        return new RecognitionModeCatalog(result);
    }

    private static ExpectedMode Paddle(
        Wire.RecognitionModeFamily family,
        Wire.ExecutionPipelineId pipeline,
        Wire.OcrEngineId? engine = null) =>
        Mode(
            family,
            pipeline,
            engine,
            Wire.RecognitionModeProvisioning.AdvancedComponent,
            Wire.RecognitionModeLifecycleKind.ModelResidency,
            true, true, true, true);

    private static ExpectedMode Mode(
        Wire.RecognitionModeFamily family,
        Wire.ExecutionPipelineId pipeline,
        Wire.OcrEngineId? engine,
        Wire.RecognitionModeProvisioning provisioning,
        Wire.RecognitionModeLifecycleKind lifecycleKind,
        bool supportsPreload,
        bool supportsTtl,
        bool supportsPinning,
        bool supportsRelease) =>
        new(
            family,
            pipeline,
            engine,
            provisioning,
            lifecycleKind,
            supportsPreload,
            supportsTtl,
            supportsPinning,
            supportsRelease);

    private static string ModeId(Wire.RecognitionModeId value) => value switch
    {
        Wire.RecognitionModeId.RapidText => "rapid_text",
        Wire.RecognitionModeId.WindowsText => "windows_text",
        Wire.RecognitionModeId.PaddleText => "paddle_text",
        Wire.RecognitionModeId.PaddleStructure => "paddle_structure",
        Wire.RecognitionModeId.PaddleDocumentVl => "paddle_document_vl",
        Wire.RecognitionModeId.MineruDocument => "mineru_document",
        Wire.RecognitionModeId.PaddleTable => "paddle_table",
        Wire.RecognitionModeId.PaddleFormula => "paddle_formula",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown recognition mode."),
    };

    private static string Family(Wire.RecognitionModeFamily value) => value switch
    {
        Wire.RecognitionModeFamily.Text => "text",
        Wire.RecognitionModeFamily.Document => "document",
        Wire.RecognitionModeFamily.Specialized => "specialized",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown recognition family."),
    };

    private static string Pipeline(Wire.ExecutionPipelineId value) => value switch
    {
        Wire.ExecutionPipelineId.OCR => "OCR",
        Wire.ExecutionPipelineId.PPStructureV3 => "PP-StructureV3",
        Wire.ExecutionPipelineId.PaddleOCRVL => "PaddleOCR-VL",
        Wire.ExecutionPipelineId.MinerU => "MinerU",
        Wire.ExecutionPipelineId.TABLERECOGNITION => "TABLE_RECOGNITION",
        Wire.ExecutionPipelineId.FORMULARECOGNITION => "FORMULA_RECOGNITION",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown execution pipeline."),
    };

    private static OcrEngine RequestEngine(Wire.OcrEngineId value) => value switch
    {
        Wire.OcrEngineId.Rapidocr => OcrEngine.RapidOcr,
        Wire.OcrEngineId.Windows => OcrEngine.Windows,
        Wire.OcrEngineId.Paddleocr => OcrEngine.PaddleOcr,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown OCR engine."),
    };

    private static string Provisioning(Wire.RecognitionModeProvisioning value) => value switch
    {
        Wire.RecognitionModeProvisioning.BaseRuntime => "base_runtime",
        Wire.RecognitionModeProvisioning.OperatingSystem => "operating_system",
        Wire.RecognitionModeProvisioning.AdvancedComponent => "advanced_component",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown provisioning kind."),
    };

    private static string Availability(Wire.RecognitionModeAvailability value) => value switch
    {
        Wire.RecognitionModeAvailability.Ready => "ready",
        Wire.RecognitionModeAvailability.PreparationRequired => "preparation_required",
        Wire.RecognitionModeAvailability.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown availability."),
    };

    private static string LifecycleKind(Wire.RecognitionModeLifecycleKind value) => value switch
    {
        Wire.RecognitionModeLifecycleKind.Unmanaged => "unmanaged",
        Wire.RecognitionModeLifecycleKind.ModelResidency => "model_residency",
        Wire.RecognitionModeLifecycleKind.ProcessKeepAlive => "process_keep_alive",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown lifecycle kind."),
    };

    private static InvalidDataException Invalid(string message) => new(message);
}
