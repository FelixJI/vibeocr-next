using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeOCR.Contracts.HttpV2;

/// <summary>
/// Strict preview request boundary. Optional JSON fields may be omitted, but
/// explicit null must never be converted into a default or an empty selection.
/// Existing accelerator converters and response DTOs keep their compatibility.
/// </summary>
public sealed class RuntimeInstallPlanRequestJsonConverter : JsonConverter<RuntimeInstallPlanRequest>
{
    public override bool HandleNull => true;

    public override RuntimeInstallPlanRequest Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Install plan request must be an object.");
        IReadOnlyList<string>? capabilities = null;
        IReadOnlyList<string>? components = null;
        IReadOnlyList<string>? sources = null;
        RuntimeAccelerator? accelerator = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new JsonException("Duplicate install plan request field.");
            switch (property.Name)
            {
                case "required_capabilities":
                    capabilities = ReadIds(property.Value, allowEmpty: false);
                    break;
                case "install_component_ids":
                    components = ReadIds(property.Value, allowEmpty: true);
                    break;
                case "download_source_ids":
                    sources = ReadIds(property.Value, allowEmpty: false);
                    break;
                case "accelerator":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        throw new JsonException("accelerator must be a wire string.");
                    accelerator = property.Value.GetString() switch
                    {
                        "cpu" => RuntimeAccelerator.Cpu,
                        "nvidia_cuda" => RuntimeAccelerator.NvidiaCuda,
                        _ => throw new JsonException("Unknown accelerator wire value."),
                    };
                    break;
                default:
                    throw new JsonException("Unknown install plan request field.");
            }
        }
        ValidateCapabilities(capabilities);
        return new RuntimeInstallPlanRequest
        {
            RequiredCapabilities = capabilities!,
            Accelerator = accelerator,
            InstallComponentIds = components,
            DownloadSourceIds = sources,
        };
    }

    public override void Write(
        Utf8JsonWriter writer, RuntimeInstallPlanRequest value, JsonSerializerOptions options)
    {
        if (value is null)
            throw new JsonException("Install plan request must be an object.");
        ValidateCapabilities(value.RequiredCapabilities);
        if (value.InstallComponentIds is not null)
            ValidateIds(value.InstallComponentIds, allowEmpty: true);
        if (value.DownloadSourceIds is not null)
            ValidateIds(value.DownloadSourceIds, allowEmpty: false);
        string? accelerator = value.Accelerator switch
        {
            null => null,
            RuntimeAccelerator.Cpu => "cpu",
            RuntimeAccelerator.NvidiaCuda => "nvidia_cuda",
            _ => throw new JsonException("Unknown accelerator value."),
        };
        writer.WriteStartObject();
        WriteIds(writer, "required_capabilities", value.RequiredCapabilities);
        if (accelerator is not null)
            writer.WriteString("accelerator", accelerator);
        if (value.InstallComponentIds is not null)
            WriteIds(writer, "install_component_ids", value.InstallComponentIds);
        if (value.DownloadSourceIds is not null)
            WriteIds(writer, "download_source_ids", value.DownloadSourceIds);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> ReadIds(JsonElement value, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonException("Selection must be an array, not null.");
        var result = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonException("Selection ids must be strings.");
            result.Add(item.GetString()!);
        }
        ValidateIds(result, allowEmpty);
        return result;
    }

    private static void ValidateIds(IReadOnlyList<string> values, bool allowEmpty)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!allowEmpty && values.Count == 0)
            throw new JsonException("Selection must be non-empty.");
        foreach (string item in values)
            if (string.IsNullOrEmpty(item) || !seen.Add(item))
                throw new JsonException("Selection ids must be unique non-empty strings.");
    }

    private static void ValidateCapabilities(IReadOnlyList<string>? values)
    {
        if (values is null)
            throw new JsonException("required_capabilities is required.");
        ValidateIds(values, allowEmpty: false);
        if (!values.Contains("runtime.install-plan.v1"))
            throw new JsonException("runtime.install-plan.v1 capability is required.");
    }

    private static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}