using System.Text.Json;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 上次区域的显示器布局快照：仅保存有语义的物理矩形与主屏标记，不保存
/// hash/identity/指纹。布局变化时按语义比对整体拒绝，不做静默钳制。
/// </summary>
public sealed record MonitorLayoutSnapshot(
    int X, int Y, int Width, int Height, bool IsPrimary);

public sealed record LastRegionCapture(
    PhysicalRectangle Bounds,
    IReadOnlyList<MonitorLayoutSnapshot> Monitors);

/// <summary>
/// #236 截图选项：延时 1–60 秒（默认 3）；光标默认关闭；窗口边框默认保留
/// （可见边框语义，见 ScreenTargetQuery）。FixedRegion 是用户显式配置的
/// 固定区域，LastRegion 是每次框选后自动更新的上次区域，两者独立。
/// 文件为 DataRoot 下的 capture-preferences.json，经 AtomicFile 原子写入。
/// </summary>
public sealed record CapturePreferences(
    int DelaySeconds = 3,
    bool IncludeCursor = false,
    string WindowBoundsMode = ScreenTargetQuery.WindowBoundsFrame,
    PhysicalRectangle? FixedRegion = null,
    LastRegionCapture? LastRegion = null)
{
  public int EffectiveDelaySeconds => Math.Clamp(DelaySeconds, 1, 60);

  public string EffectiveWindowBoundsMode =>
      string.Equals(WindowBoundsMode, ScreenTargetQuery.WindowBoundsClient, StringComparison.Ordinal)
          ? ScreenTargetQuery.WindowBoundsClient
          : ScreenTargetQuery.WindowBoundsFrame;
}

public interface ICapturePreferencesStore
{
  CapturePreferences Load();

  void Save(CapturePreferences preferences);

  /// <summary>框选成功后保存上次区域（仅坐标与布局快照，绝不保存像素）。</summary>
  void SaveLastRegion(PhysicalRectangle bounds);
}

public sealed class CapturePreferencesStore(PortableLayout layout) : ICapturePreferencesStore
{
  private const string FileName = "capture-preferences.json";

  public CapturePreferences Load()
  {
    string path = Path.Combine(layout.DataRoot, FileName);
    try
    {
      if (!File.Exists(path))
      {
        return new CapturePreferences();
      }

      using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return new CapturePreferences();
      }

      int delay = 3;
      if (root.TryGetProperty("delaySeconds", out JsonElement delayElement) &&
          delayElement.ValueKind == JsonValueKind.Number &&
          delayElement.TryGetInt32(out int parsedDelay))
      {
        delay = parsedDelay;
      }

      bool cursor = TryGetBoolean(root, "includeCursor");
      string bounds = ScreenTargetQuery.WindowBoundsFrame;
      if (root.TryGetProperty("windowBoundsMode", out JsonElement boundsElement) &&
          boundsElement.ValueKind == JsonValueKind.String)
      {
        bounds = boundsElement.GetString() ?? bounds;
      }

      PhysicalRectangle? fixedRegion =
          ReadRectangle(root, "fixedRegion");
      LastRegionCapture? last = null;
      if (root.TryGetProperty("lastRegion", out JsonElement lastElement) &&
          lastElement.ValueKind == JsonValueKind.Object &&
          ReadRectangle(lastElement, "bounds") is { } lastBounds &&
          lastElement.TryGetProperty("monitors", out JsonElement monitorsElement) &&
          monitorsElement.ValueKind == JsonValueKind.Array)
      {
        var monitors = new List<MonitorLayoutSnapshot>();
        foreach (JsonElement monitor in monitorsElement.EnumerateArray())
        {
          if (monitor.ValueKind == JsonValueKind.Object &&
              ReadRectangle(monitor, null) is { } monitorBounds)
          {
            monitors.Add(new MonitorLayoutSnapshot(
                monitorBounds.X,
                monitorBounds.Y,
                monitorBounds.Width,
                monitorBounds.Height,
                TryGetBoolean(monitor, "isPrimary")));
          }
        }

        last = new LastRegionCapture(lastBounds, monitors);
      }

      return new CapturePreferences(delay, cursor, bounds, fixedRegion, last);
    }
    catch (Exception error) when (
        error is IOException or JsonException or UnauthorizedAccessException)
    {
      // 选项不可读时回落默认值；上次区域/固定区域一并视为不可信。
      return new CapturePreferences();
    }
  }

  public void Save(CapturePreferences preferences) => AtomicFile.WriteAllText(
      Path.Combine(layout.DataRoot, FileName),
      JsonSerializer.Serialize(new
      {
        delaySeconds = preferences.EffectiveDelaySeconds,
        includeCursor = preferences.IncludeCursor,
        windowBoundsMode = preferences.EffectiveWindowBoundsMode,
        fixedRegion = WriteRectangle(preferences.FixedRegion),
        lastRegion = preferences.LastRegion is null ? null : new
        {
          bounds = WriteRectangle(preferences.LastRegion.Bounds),
          monitors = preferences.LastRegion.Monitors.Select(monitor => new
          {
            x = monitor.X,
            y = monitor.Y,
            width = monitor.Width,
            height = monitor.Height,
            isPrimary = monitor.IsPrimary,
          }),
        },
      }));

  public void SaveLastRegion(PhysicalRectangle bounds)
  {
    CapturePreferences current = Load();
    Save(current with { LastRegion = SnapshotRegion(bounds) });
  }

  /// <summary>把当前显示器布局快照进区域记录（供保存上次/固定区域复用）。</summary>
  public static LastRegionCapture SnapshotRegion(PhysicalRectangle bounds) => new(
      bounds,
      ScreenTargetQuery.EnumerateMonitors()
          .Select(monitor => new MonitorLayoutSnapshot(
              monitor.Bounds.X,
              monitor.Bounds.Y,
              monitor.Bounds.Width,
              monitor.Bounds.Height,
              monitor.IsPrimary))
          .ToArray());

  /// <summary>区域是否完全落在当前桌体内（越界一律拒绝，不钳制）。</summary>
  public static bool IsRegionInsideDesktop(PhysicalRectangle region, PhysicalRectangle desktop)
  {
    if (region.Width <= 0 || region.Height <= 0)
    {
      return false;
    }

    return region.X >= desktop.X && region.Y >= desktop.Y &&
        (long)region.X + region.Width <= (long)desktop.X + desktop.Width &&
        (long)region.Y + region.Height <= (long)desktop.Y + desktop.Height;
  }

  /// <summary>
  /// 上次区域是否仍可用于当前布局：显示器矩形集合（顺序无关）完全一致
  /// 且区域完全落在虚拟桌面内。任一不满足即明确拒绝。
  /// </summary>
  public static bool IsLastRegionUsable(
      LastRegionCapture last,
      IReadOnlyList<ScreenMonitorTarget> currentMonitors,
      PhysicalRectangle virtualDesktop)
  {
    var snapshot = last.Monitors
        .Select(monitor => (monitor.X, monitor.Y, monitor.Width, monitor.Height, monitor.IsPrimary))
        .OrderBy(item => item.X).ThenBy(item => item.Y)
        .ThenBy(item => item.Width).ThenBy(item => item.Height).ThenBy(item => item.IsPrimary)
        .ToArray();
    var current = currentMonitors
        .Select(monitor => (monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width,
            monitor.Bounds.Height, monitor.IsPrimary))
        .OrderBy(item => item.X).ThenBy(item => item.Y)
        .ThenBy(item => item.Width).ThenBy(item => item.Height).ThenBy(item => item.IsPrimary)
        .ToArray();
    return snapshot.Length == current.Length && snapshot.SequenceEqual(current) &&
        IsRegionInsideDesktop(last.Bounds, virtualDesktop);
  }

  private static bool TryGetBoolean(JsonElement parent, string name) =>
      parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

  private static PhysicalRectangle? ReadRectangle(JsonElement parent, string? name)
  {
    JsonElement element = name is null ? parent : default;
    if (name is not null &&
        (!parent.TryGetProperty(name, out element) || element.ValueKind != JsonValueKind.Object))
    {
      return null;
    }

    if (element.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    if (!element.TryGetProperty("x", out JsonElement x) || x.ValueKind != JsonValueKind.Number ||
        !x.TryGetInt32(out int rx) ||
        !element.TryGetProperty("y", out JsonElement y) || y.ValueKind != JsonValueKind.Number ||
        !y.TryGetInt32(out int ry) ||
        !element.TryGetProperty("width", out JsonElement w) || w.ValueKind != JsonValueKind.Number ||
        !w.TryGetInt32(out int rw) ||
        !element.TryGetProperty("height", out JsonElement h) || h.ValueKind != JsonValueKind.Number ||
        !h.TryGetInt32(out int rh))
    {
      return null;
    }

    return new PhysicalRectangle(rx, ry, rw, rh);
  }

  private static object? WriteRectangle(PhysicalRectangle? rectangle) => rectangle is null
      ? null
      : new
      {
        x = rectangle.Value.X,
        y = rectangle.Value.Y,
        width = rectangle.Value.Width,
        height = rectangle.Value.Height,
      };
}
