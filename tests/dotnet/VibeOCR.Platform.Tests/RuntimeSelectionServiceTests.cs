using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class RuntimeSelectionServiceTests
{
    [Fact]
    public void ProtocolGoldenSourceKindsProjectWithoutAppAliases()
    {
        RuntimeSelectionService service = new(Health(
            [SourceCapability],
            sources: SourceCatalog(
                ("package_index", "pypi-official", "https://pypi.org/simple"),
                ("package_index", "pypi-tuna", "https://pypi.tuna.tsinghua.edu.cn/simple"),
                ("model_registry", "hf-official", "https://huggingface.co"),
                ("model_registry", "hf-mirror", "https://hf-mirror.com"))));

        Assert.Equal(
            ["package_index", "package_index", "model_registry", "model_registry"],
            service.Sources.Select(source => source.Kind));
    }

    [Fact]
    public void CatalogsProjectFromHealthCapabilityDescriptors()
    {
        RuntimeSelectionService service = new(Health(
            [EngineCapability, SourceCapability, VariantCapability],
            engines: EngineCatalog(
                (Wire.OcrEngineId.Rapidocr, Wire.OcrEngineAvailability.Ready, true, null, null),
                (Wire.OcrEngineId.Paddleocr, Wire.OcrEngineAvailability.PreparationRequired, false, null, "paddle-engine")),
            sources: SourceCatalog(
                ("package_index", "tuna-pypi", "https://mirrors.tuna.example/pypi/simple"),
                ("package_index", "pypi", "https://pypi.org/simple"),
                ("internal-mirror", "mirror-1", "https://example.invalid/simple")),
            variants: VariantCatalog(
                ("document_parsing", "cpu", "doc-parser-cpu"),
                ("document_parsing", "nvidia_cuda", "doc-parser-cuda"))));

        Assert.True(service.SupportsEngineSelection);
        Assert.True(service.SupportsDownloadSources);
        Assert.True(service.SupportsComponentSelection);

        RuntimeEngineOption rapid = service.EngineOptions.Single(
            option => option.Engine == OcrEngine.RapidOcr);
        Assert.True(rapid.IsUsable);
        Assert.True(rapid.IncludedInBase);
        RuntimeEngineOption paddle = service.EngineOptions.Single(
            option => option.Engine == OcrEngine.PaddleOcr);
        Assert.True(paddle.IsUsable);
        Assert.Equal("paddle-engine", paddle.RequiredComponent);

        Assert.Equal(3, service.Sources.Count);
        // 未知 kind 原样保留,不折叠成已知枚举。
        Assert.Contains(service.Sources, source =>
            source.Kind == "internal-mirror" && source.Id == "mirror-1");
        Assert.Equal(2, service.Variants.Count);
    }

    [Fact]
    public void MissingCatalogCapabilityFailsClosedInsteadOfGuessing()
    {
        RuntimeSelectionService service = new(Health([SourceCapability]));

        Assert.False(service.SupportsEngineSelection);
        Assert.False(service.SupportsComponentSelection);
        Assert.Empty(service.EngineOptions);
        Assert.Empty(service.Variants);

        RuntimeSelectionException engineError = Assert.Throws<RuntimeSelectionException>(
            () => service.SelectEngine(OcrEngine.RapidOcr));
        Assert.Equal(RuntimeSelectionErrorKind.CapabilityMissing, engineError.Kind);

        RuntimeSelectionException componentError = Assert.Throws<RuntimeSelectionException>(
            () => service.SelectComponentIds("cpu", ["document_parsing"]));
        Assert.Equal(RuntimeSelectionErrorKind.CapabilityMissing, componentError.Kind);

        RuntimeSelectionException sourceError = Assert.Throws<RuntimeSelectionException>(
            () => service.NormalizeSourceSelection(["tuna-pypi"]));
        Assert.Equal(RuntimeSelectionErrorKind.CapabilityMissing, sourceError.Kind);
    }

    [Fact]
    public void DuplicateCatalogBusinessKeysFailClosed()
    {
        RuntimeSelectionException duplicateSource = Assert.Throws<RuntimeSelectionException>(
            () => new RuntimeSelectionService(Health(
                [SourceCapability],
                sources: SourceCatalog(
                    ("package_index", "tuna-pypi", "https://a.invalid"),
                    ("model_registry", "tuna-pypi", "https://b.invalid")))));
        Assert.Equal(RuntimeSelectionErrorKind.DuplicateCatalogEntry, duplicateSource.Kind);

        RuntimeSelectionException duplicateVariant = Assert.Throws<RuntimeSelectionException>(
            () => new RuntimeSelectionService(Health(
                [VariantCapability],
                variants: VariantCatalog(
                    ("document_parsing", "cpu", "doc-a"),
                    ("document_parsing", "cpu", "doc-b")))));
        Assert.Equal(RuntimeSelectionErrorKind.DuplicateCatalogEntry, duplicateVariant.Kind);

        RuntimeSelectionException duplicateEngine = Assert.Throws<RuntimeSelectionException>(
            () => new RuntimeSelectionService(Health(
                [EngineCapability],
                engines: EngineCatalog(
                    (Wire.OcrEngineId.Rapidocr, Wire.OcrEngineAvailability.Ready, true, null, null),
                    (Wire.OcrEngineId.Rapidocr, Wire.OcrEngineAvailability.Ready, true, null, null)))));
        Assert.Equal(RuntimeSelectionErrorKind.DuplicateCatalogEntry, duplicateEngine.Kind);

        RuntimeSelectionException blankSource = Assert.Throws<RuntimeSelectionException>(
            () => new RuntimeSelectionService(Health(
                [SourceCapability],
                sources: SourceCatalog(("package_index", " ", "https://a.invalid")))));
        Assert.Equal(RuntimeSelectionErrorKind.InvalidCatalogEntry, blankSource.Kind);

        var repeated = new Wire.Health
        {
            SchemaVersion = 2,
            InstanceId = "sup-1",
            ProtocolVersion = 2,
            Ready = true,
            Draining = false,
            Capabilities = [EngineCapability],
            CapabilityDescriptors =
            [
                Descriptor(EngineCapability, engines: EngineCatalog(
                    (Wire.OcrEngineId.Rapidocr, Wire.OcrEngineAvailability.Ready, true, null, null))),
                Descriptor(EngineCapability, engines: EngineCatalog(
                    (Wire.OcrEngineId.Rapidocr, Wire.OcrEngineAvailability.Ready, true, null, null))),
            ],
        };
        RuntimeSelectionException repeatedCatalog = Assert.Throws<RuntimeSelectionException>(
            () => new RuntimeSelectionService(repeated));
        Assert.Equal(RuntimeSelectionErrorKind.InvalidCatalogEntry, repeatedCatalog.Kind);
    }

    [Fact]
    public void SelectEngineValidatesMembershipAndAvailability()
    {
        RuntimeSelectionService service = new(Health(
            [EngineCapability],
            engines: EngineCatalog(
                (Wire.OcrEngineId.Rapidocr, Wire.OcrEngineAvailability.Ready, true, null, null),
                (Wire.OcrEngineId.Windows, Wire.OcrEngineAvailability.Unavailable, false, "language_pack_missing", null),
                (Wire.OcrEngineId.Paddleocr, Wire.OcrEngineAvailability.PreparationRequired, false, null, "paddle-engine"))));

        RuntimeEngineOption ready = service.SelectEngine(OcrEngine.RapidOcr);
        Assert.Equal(OcrEngine.RapidOcr, ready.Engine);
        Assert.True(ready.IsUsable);

        RuntimeEngineOption preparation = service.SelectEngine(OcrEngine.PaddleOcr);
        Assert.True(preparation.IsUsable);
        Assert.Equal("paddle-engine", preparation.RequiredComponent);

        RuntimeSelectionException unavailable = Assert.Throws<RuntimeSelectionException>(
            () => service.SelectEngine(OcrEngine.Windows));
        Assert.Equal(RuntimeSelectionErrorKind.EngineUnavailable, unavailable.Kind);
        Assert.Contains("language_pack_missing", unavailable.Message);
    }

    [Fact]
    public void SourceSelectionAllowsAtMostOneIdPerKind()
    {
        RuntimeSelectionService service = new(Health(
            [SourceCapability],
            sources: SourceCatalog(
                ("package_index", "tuna-pypi", "https://a.invalid"),
                ("package_index", "pypi", "https://b.invalid"),
                ("model_registry", "huggingface", "https://c.invalid"))));

        Assert.Empty(service.NormalizeSourceSelection(null));
        Assert.Equal(
            ["tuna-pypi", "huggingface"],
            service.NormalizeSourceSelection(["tuna-pypi", "huggingface"]));

        RuntimeSelectionException sameKind = Assert.Throws<RuntimeSelectionException>(
            () => service.NormalizeSourceSelection(["tuna-pypi", "pypi"]));
        Assert.Equal(RuntimeSelectionErrorKind.DuplicateSourceKind, sameKind.Kind);

        RuntimeSelectionException duplicateId = Assert.Throws<RuntimeSelectionException>(
            () => service.NormalizeSourceSelection(["tuna-pypi", "tuna-pypi"]));
        Assert.Equal(RuntimeSelectionErrorKind.DuplicateSourceKind, duplicateId.Kind);

        RuntimeSelectionException unknown = Assert.Throws<RuntimeSelectionException>(
            () => service.NormalizeSourceSelection(["aliyun-pypi"]));
        Assert.Equal(RuntimeSelectionErrorKind.UnknownSource, unknown.Kind);
    }

    [Fact]
    public void FeatureSelectionMapsThroughVariantBusinessKey()
    {
        RuntimeSelectionService service = new(Health(
            [VariantCapability],
            variants: VariantCatalog(
                ("document_parsing", "cpu", "doc-parser-cpu"),
                ("document_parsing", "nvidia_cuda", "doc-parser-cuda"),
                ("gpu_runtime", "nvidia_cuda", "doc-parser-cuda"))));

        Assert.Empty(service.SelectComponentIds("cpu", null));
        Assert.Equal(
            ["doc-parser-cpu"],
            service.SelectComponentIds("cpu", ["document_parsing"]));
        // 两个 feature 映射到同一 component 时去重,wire 不接受重复 id。
        Assert.Equal(
            ["doc-parser-cuda"],
            service.SelectComponentIds("nvidia_cuda", ["document_parsing", "gpu_runtime"]));

        RuntimeSelectionException wrongAccelerator = Assert.Throws<RuntimeSelectionException>(
            () => service.SelectComponentIds("cpu", ["gpu_runtime"]));
        Assert.Equal(RuntimeSelectionErrorKind.UnknownFeature, wrongAccelerator.Kind);

        RuntimeSelectionException unknownFeature = Assert.Throws<RuntimeSelectionException>(
            () => service.SelectComponentIds("cpu", ["quantum_parsing"]));
        Assert.Equal(RuntimeSelectionErrorKind.UnknownFeature, unknownFeature.Kind);
    }

    private const string EngineCapability = RuntimeSelectionService.EngineSelectionCapability;
    private const string SourceCapability = RuntimeSelectionService.DownloadSourceCapability;
    private const string VariantCapability = RuntimeSelectionService.ComponentSelectionCapability;

    private static Wire.Health Health(
        string[] capabilities,
        Wire.OcrEngineCatalog? engines = null,
        Wire.DownloadSourceCatalog? sources = null,
        Wire.ComponentVariantCatalog? variants = null) => new()
    {
        SchemaVersion = 2,
        InstanceId = "sup-1",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities = capabilities,
        CapabilityDescriptors =
        [
            .. capabilities.Select(capability => Descriptor(
                capability,
                engines: capability == EngineCapability ? engines : null,
                sources: capability == SourceCapability ? sources : null,
                variants: capability == VariantCapability ? variants : null)),
        ],
    };

    private static Wire.CapabilityDescriptor Descriptor(
        string name,
        Wire.OcrEngineCatalog? engines = null,
        Wire.DownloadSourceCatalog? sources = null,
        Wire.ComponentVariantCatalog? variants = null) => new()
    {
        Name = name,
        Lifecycle = "active",
        IntroducedIn = "2.7.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        OcrEngineCatalog = engines,
        DownloadSourceCatalog = sources,
        ComponentVariantCatalog = variants,
    };

    private static Wire.OcrEngineCatalog EngineCatalog(
        params (Wire.OcrEngineId Id, Wire.OcrEngineAvailability Availability, bool IncludedInBase, string? ReasonCode, string? RequiredComponent)[] engines) => new()
    {
        Engines = [.. engines.Select(engine => new Wire.OcrEngineDescriptor
        {
            Id = engine.Id,
            Availability = engine.Availability,
            IncludedInBase = engine.IncludedInBase,
            ReasonCode = engine.ReasonCode,
            RequiredComponent = engine.RequiredComponent,
        })],
    };

    private static Wire.DownloadSourceCatalog SourceCatalog(
        params (string Kind, string Id, string Endpoint)[] sources) => new()
    {
        Sources = [.. sources.Select(source => new Wire.DownloadSourceDescriptor
        {
            Kind = source.Kind,
            Id = source.Id,
            Endpoint = source.Endpoint,
        })],
    };

    private static Wire.ComponentVariantCatalog VariantCatalog(
        params (string FeatureId, string Accelerator, string ComponentId)[] variants) => new()
    {
        Variants = [.. variants.Select(variant => new Wire.ComponentVariantDescriptor
        {
            FeatureId = variant.FeatureId,
            Accelerator = variant.Accelerator,
            ComponentId = variant.ComponentId,
        })],
    };
}
