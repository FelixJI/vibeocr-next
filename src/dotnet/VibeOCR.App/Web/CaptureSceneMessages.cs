using System.Text.Json;
using VibeOCR.App.Workbench;

namespace VibeOCR.App.Web;

// Geometry is in physical pixels relative to the frozen virtual desktop.
internal sealed record CaptureSceneConfiguration(
  Guid SurfaceId, Guid CaptureSessionId, int X, int Y, int Width, int Height,
  int DesktopWidth, int DesktopHeight, string InitialTool);

internal sealed record CaptureSceneDescriptor(
  Guid SurfaceId, string CaptureSessionId, string InputUrl, long InputRevision,
  long DocumentGeneration, long BootstrapGeneration,
  int X, int Y, int Width, int Height, int DesktopWidth, int DesktopHeight, string InitialTool);

internal sealed record CapturePixelReport(Guid SessionId, CaptureSceneDescriptor CaptureScene, bool Ready);

internal static class CaptureSceneMessages
{
  internal static bool IsReport(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
    root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
    type.GetString() is "capture.pixelReady" or "capture.pixelFailed";

  internal static CapturePixelReport Parse(string json)
  {
    if (System.Text.Encoding.UTF8.GetByteCount(json) > WorkbenchBridgeCodec.MaxMessageBytes)
      throw new WorkbenchBridgeProtocolException("Capture pixel report exceeds the size limit.");
    try
    {
      using JsonDocument document = JsonDocument.Parse(json);
      JsonElement root = document.RootElement;
      Exact(root, ["version", "kind", "id", "type", "payload"]);
      string? type = root.GetProperty("type").GetString();
      bool ready = type == "capture.pixelReady";
      if (type is not ("capture.pixelReady" or "capture.pixelFailed") ||
        root.GetProperty("version").GetInt32() != WorkbenchProtocol.Version ||
        root.GetProperty("kind").GetString() != "event" ||
        !Guid.TryParse(root.GetProperty("id").GetString(), out _))
        throw new WorkbenchBridgeProtocolException("Invalid capture pixel envelope.");
      JsonElement payload = root.GetProperty("payload");
      Exact(payload, ready ? ["sessionId", "captureScene"] : ["sessionId", "captureScene", "reason"]);
      if (!ready && payload.GetProperty("reason").GetString() is not
        ("sourceDecode" or "backgroundDecode" or "draw" or "identity"))
        throw new WorkbenchBridgeProtocolException("Invalid capture failure reason.");
      Exact(payload.GetProperty("captureScene"), ["surfaceId", "captureSessionId", "inputUrl", "inputRevision",
        "documentGeneration", "bootstrapGeneration", "x", "y", "width", "height", "desktopWidth", "desktopHeight", "initialTool"]);
      var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
      CaptureSceneDescriptor scene = payload.GetProperty("captureScene").Deserialize<CaptureSceneDescriptor>(options)
        ?? throw new WorkbenchBridgeProtocolException("Missing capture scene identity.");
      return new(payload.GetProperty("sessionId").GetGuid(), scene, ready);
    }
    catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
    {
      throw new WorkbenchBridgeProtocolException("Invalid capture pixel payload.", error);
    }
  }

  private static void Exact(JsonElement value, string[] fields)
  {
    if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
      value.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal)) ||
      fields.Any(field => !value.TryGetProperty(field, out _)))
      throw new WorkbenchBridgeProtocolException("Invalid capture pixel fields.");
  }
}
