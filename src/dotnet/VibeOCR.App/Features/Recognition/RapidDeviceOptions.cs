using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// Rapid 系模式的 pipeline <c>device</c> 选项（auto|cpu|cuda）。null 完全省略
/// wire 字段（Runtime 缺省 auto）；cuda 不可用时由 Runtime fail closed 拒绝，
/// 桌面不推导设备可用性，也不静默降 CPU。与 Paddle 选项分开持久化。
/// </summary>
public sealed record RapidDeviceOptions
{
    public const string Auto = "auto";
    public const string Cpu = "cpu";
    public const string Cuda = "cuda";

    public static readonly IReadOnlyList<string> WireValues = [Auto, Cpu, Cuda];

    /// <summary>null = 跟随 Runtime 缺省（auto），wire 上省略 device 字段。</summary>
    public string? Device { get; init; }

    /// <summary>持久值非法/目录收紧时回退缺省，不让整页崩溃。</summary>
    internal static RapidDeviceOptions Load(PortableLayout layout, string modeId)
    {
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonNode? stored = root["recognition_device_options"]?[modeId];
        if (stored is null) return new();
        try
        {
            string? device = stored.Deserialize<string>(JsonOptions);
            return device is null || IsValid(device) ? new RapidDeviceOptions { Device = device } : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    internal void Save(PortableLayout layout, string modeId)
    {
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonObject modes = root["recognition_device_options"] as JsonObject ?? new();
        modes[modeId] = JsonSerializer.SerializeToNode(Device, JsonOptions);
        root["recognition_device_options"] = modes;
        AppSettingsStore.Write(layout, root);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new();

    internal static bool IsValid(string device) => WireValues.Contains(device, StringComparer.Ordinal);

    /// <summary>提交冻结点严格投影：不支持或非法值抛出拒绝提交（#110 AC2）。</summary>
    public IReadOnlyDictionary<string, JsonElement> ToWire(RecognitionModeOption mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (Device is null) return new Dictionary<string, JsonElement>();
        if (!mode.SupportedOptions.Contains("device", StringComparer.Ordinal))
            throw new ArgumentException($"Mode {mode.Id} does not support device.");
        if (!IsValid(Device))
            throw new ArgumentException("Unsupported device value.");
        return new Dictionary<string, JsonElement>
        {
            ["device"] = JsonSerializer.SerializeToElement(Device),
        };
    }

    /// <summary>状态展示投影：不支持/非法时过滤回退默认，不抛异常。</summary>
    public IReadOnlyDictionary<string, JsonElement> ProjectWire(RecognitionModeOption mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (Device is null ||
            !mode.SupportedOptions.Contains("device", StringComparer.Ordinal) ||
            !IsValid(Device)) return new Dictionary<string, JsonElement>();
        return new Dictionary<string, JsonElement>
        {
            ["device"] = JsonSerializer.SerializeToElement(Device),
        };
    }
}
