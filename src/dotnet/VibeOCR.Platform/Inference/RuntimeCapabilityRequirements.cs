using System.Text.Json;

namespace VibeOCR.Platform.Inference;

/// <summary>
/// Reads the product-owned Runtime capability baseline from its immutable component lock.
/// </summary>
public static class RuntimeCapabilityRequirements
{
    public static IReadOnlySet<string> Read(string componentLockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentLockPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(componentLockPath));
        if (!document.RootElement.TryGetProperty("required_capabilities", out JsonElement capabilities) ||
            capabilities.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Component lock has no required capability set.");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            string? value = capability.ValueKind == JsonValueKind.String
                ? capability.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(value) || !result.Add(value))
            {
                throw new InvalidDataException("Component lock has an invalid required capability set.");
            }
        }
        if (result.Count == 0)
        {
            throw new InvalidDataException("Component lock has an empty required capability set.");
        }
        return result;
    }
}
