using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #236 截图选项与上次区域持久化契约：默认值/钳制、布局语义比对明确拒绝
/// （不静默钳制）、原子文件往返与损坏回落。
/// </summary>
public class CapturePreferencesTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-capture-prefs-{Guid.NewGuid():N}");

  [Fact]
  public void DefaultsMatchArbitration()
  {
    CapturePreferences preferences = new();

    Assert.Equal(3, preferences.EffectiveDelaySeconds);
    Assert.False(preferences.IncludeCursor);
    Assert.Equal(ScreenTargetQuery.WindowBoundsFrame, preferences.EffectiveWindowBoundsMode);
    Assert.Null(preferences.LastRegion);
  }

  [Theory]
  [InlineData(0, 1)]
  [InlineData(-7, 1)]
  [InlineData(60, 60)]
  [InlineData(999, 60)]
  [InlineData(4, 4)]
  public void DelayIsClampedToConfiguredRange(int saved, int expected)
  {
    Assert.Equal(expected, new CapturePreferences(DelaySeconds: saved).EffectiveDelaySeconds);
  }

  [Fact]
  public void UnknownWindowBoundsModeFallsBackToFrame()
  {
    Assert.Equal(
        ScreenTargetQuery.WindowBoundsFrame,
        new CapturePreferences(WindowBoundsMode: "nonsense").EffectiveWindowBoundsMode);
  }

  [Fact]
  public void LastRegionIsUsableOnlyWhenLayoutMatchesExactlyAndRegionStaysInside()
  {
    var monitor = new ScreenMonitorTarget(new PhysicalRectangle(0, 0, 1920, 1080), true, @"\\.\DISPLAY1");
    var desktop = new PhysicalRectangle(0, 0, 1920, 1080);
    var snapshot = new[]
    {
            new MonitorLayoutSnapshot(0, 0, 1920, 1080, true),
        };
    var inside = new LastRegionCapture(
        new PhysicalRectangle(10, 20, 300, 200), snapshot);

    Assert.True(CapturePreferencesStore.IsLastRegionUsable(inside, [monitor], desktop));

    // 布局变化（分辨率不同）→ 拒绝。
    var resized = new ScreenMonitorTarget(new PhysicalRectangle(0, 0, 2560, 1440), true, @"\\.\DISPLAY1");
    Assert.False(CapturePreferencesStore.IsLastRegionUsable(
        inside, [resized], new PhysicalRectangle(0, 0, 2560, 1440)));

    // 区域越界（负坐标或越出桌面）→ 拒绝，不做钳制。
    var negative = new LastRegionCapture(
        new PhysicalRectangle(-50, 10, 300, 200), snapshot);
    Assert.False(CapturePreferencesStore.IsLastRegionUsable(negative, [monitor], desktop));
    var overflow = new LastRegionCapture(
        new PhysicalRectangle(1700, 1000, 300, 200), snapshot);
    Assert.False(CapturePreferencesStore.IsLastRegionUsable(overflow, [monitor], desktop));

    // 显示器被移除/新增 → 拒绝。
    Assert.False(CapturePreferencesStore.IsLastRegionUsable(
        inside,
        new[] { monitor, new ScreenMonitorTarget(new PhysicalRectangle(1920, 0, 1280, 720), false, @"\\.\DISPLAY2") },
        new PhysicalRectangle(0, 0, 3200, 1080)));

    // 枚举顺序不同但布局一致 → 仍可用（比对与顺序无关）。
    Assert.True(CapturePreferencesStore.IsLastRegionUsable(
        inside,
        new[] { monitor },
        desktop));
  }

  [Theory]
  [InlineData("[1,2,3]")]
  [InlineData("\"not-an-object\"")]
  [InlineData("""{"delaySeconds": "seven"}""")]
  [InlineData("""{"delaySeconds": 3.5}""")]
  [InlineData("""{"delaySeconds": 99999999999999999999}""")]
  [InlineData("""{"fixedRegion": {"x": "a","y": 0,"width": 10,"height": 10}}""")]
  [InlineData("""{"fixedRegion": {"x": 0,"y": 0,"width": 10}}""")]
  [InlineData("""{"lastRegion": {"monitors": [42]}}""")]
  public void MalformedPreferenceNodesFallBackToDefaultsOrKnownValues(string content)
  {
    PortableLayout layout = CreateLayout();
    Directory.CreateDirectory(layout.DataRoot);
    File.WriteAllText(Path.Combine(layout.DataRoot, "capture-preferences.json"), content);
    var store = new CapturePreferencesStore(layout);

    CapturePreferences preferences = store.Load();

    Assert.Equal(3, preferences.EffectiveDelaySeconds);
    Assert.False(preferences.IncludeCursor);
    Assert.Equal(ScreenTargetQuery.WindowBoundsFrame, preferences.EffectiveWindowBoundsMode);
    Assert.Null(preferences.FixedRegion);
    Assert.Null(preferences.LastRegion);
  }

  [Fact]
  public void FixedRegionRoundTripsIndependentlyOfLastRegion()
  {
    PortableLayout layout = CreateLayout();
    var store = new CapturePreferencesStore(layout);
    var fixedRegion = new PhysicalRectangle(100, 200, 640, 480);
    store.Save(new CapturePreferences(FixedRegion: fixedRegion));

    // 框选后 LastRegion 自动更新，FixedRegion 必须保持不变。
    store.SaveLastRegion(new PhysicalRectangle(1, 2, 30, 40));

    CapturePreferences loaded = store.Load();
    Assert.Equal(fixedRegion, loaded.FixedRegion);
    Assert.NotNull(loaded.LastRegion);
    Assert.Equal(new PhysicalRectangle(1, 2, 30, 40), loaded.LastRegion!.Bounds);
  }

  [Fact]
  public void IsRegionInsideDesktopRejectsPartialOverlapAndInvalidSizes()
  {
    var desktop = new PhysicalRectangle(0, 0, 1920, 1080);
    Assert.True(CapturePreferencesStore.IsRegionInsideDesktop(
        new PhysicalRectangle(0, 0, 1920, 1080), desktop));
    Assert.True(CapturePreferencesStore.IsRegionInsideDesktop(
        new PhysicalRectangle(10, 10, 100, 100), desktop));
    Assert.False(CapturePreferencesStore.IsRegionInsideDesktop(
        new PhysicalRectangle(-1, 0, 100, 100), desktop));
    Assert.False(CapturePreferencesStore.IsRegionInsideDesktop(
        new PhysicalRectangle(1900, 0, 100, 100), desktop));
    Assert.False(CapturePreferencesStore.IsRegionInsideDesktop(
        new PhysicalRectangle(0, 0, 0, 100), desktop));
    Assert.False(CapturePreferencesStore.IsRegionInsideDesktop(
        new PhysicalRectangle(0, 0, 100, -5), desktop));
  }

  [Fact]
  public void StoreRoundTripsOptionsAndLastRegionAtomically()
  {
    PortableLayout layout = CreateLayout();
    var store = new CapturePreferencesStore(layout);
    var region = new LastRegionCapture(
        new PhysicalRectangle(5, 6, 100, 80),
        new[] { new MonitorLayoutSnapshot(0, 0, 1920, 1080, true) });

    store.Save(new CapturePreferences(7, true, ScreenTargetQuery.WindowBoundsClient, LastRegion: region));

    CapturePreferences loaded = store.Load();
    Assert.Equal(7, loaded.EffectiveDelaySeconds);
    Assert.True(loaded.IncludeCursor);
    Assert.Equal(ScreenTargetQuery.WindowBoundsClient, loaded.EffectiveWindowBoundsMode);
    Assert.NotNull(loaded.LastRegion);
    Assert.Equal(region.Bounds, loaded.LastRegion!.Bounds);
    Assert.Equal(region.Monitors, loaded.LastRegion.Monitors);
  }

  [Fact]
  public void SaveLastRegionKeepsOtherOptions()
  {
    PortableLayout layout = CreateLayout();
    var store = new CapturePreferencesStore(layout);
    store.Save(new CapturePreferences(DelaySeconds: 12, IncludeCursor: true));

    store.SaveLastRegion(new PhysicalRectangle(1, 2, 30, 40));

    CapturePreferences loaded = store.Load();
    Assert.Equal(12, loaded.EffectiveDelaySeconds);
    Assert.True(loaded.IncludeCursor);
    Assert.NotNull(loaded.LastRegion);
    Assert.Equal(new PhysicalRectangle(1, 2, 30, 40), loaded.LastRegion!.Bounds);
  }

  [Fact]
  public void CorruptFileFallsBackToDefaultsIncludingLastRegion()
  {
    PortableLayout layout = CreateLayout();
    Directory.CreateDirectory(layout.DataRoot);
    File.WriteAllText(
        Path.Combine(layout.DataRoot, "capture-preferences.json"), "{ not-json");
    var store = new CapturePreferencesStore(layout);

    CapturePreferences preferences = store.Load();

    Assert.Equal(new CapturePreferences(), preferences);
  }

  [Fact]
  public void RepeatedSameRegionWithReorderedMonitorsDoesNotRewrite()
  {
    PortableLayout layout = CreateLayout();
    var store = new CapturePreferencesStore(layout);
    var bounds = new PhysicalRectangle(10, 20, 300, 200);
    var primary = new MonitorLayoutSnapshot(0, 0, 1920, 1080, true);
    var secondary = new MonitorLayoutSnapshot(1920, 0, 1280, 720, false);
    string file = Path.Combine(layout.DataRoot, "capture-preferences.json");

    store.SaveLastRegion(new LastRegionCapture(bounds, new[] { primary, secondary }));
    string written = File.ReadAllText(file);
    // 把 mtime 固定到过去：后续任何重写（原子替换）都会重置它；内容一并锁定。
    var pinned = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(file, pinned);

    // 完全相同区域 + 显示器枚举顺序互换 → 不重写。
    store.SaveLastRegion(new LastRegionCapture(bounds, new[] { secondary, primary }));

    Assert.Equal(pinned, File.GetLastWriteTimeUtc(file));
    Assert.Equal(written, File.ReadAllText(file));
    Assert.Equal(bounds, store.Load().LastRegion!.Bounds);
  }

  [Fact]
  public void RealRegionLayoutOrPrimaryChangesRewriteAndLoad()
  {
    PortableLayout layout = CreateLayout();
    var store = new CapturePreferencesStore(layout);
    var primary = new MonitorLayoutSnapshot(0, 0, 1920, 1080, true);
    var secondary = new MonitorLayoutSnapshot(1920, 0, 1280, 720, false);
    string file = Path.Combine(layout.DataRoot, "capture-preferences.json");
    var pinned = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var bounds = new PhysicalRectangle(10, 20, 300, 200);
    store.SaveLastRegion(new LastRegionCapture(bounds, new[] { primary, secondary }));
    File.SetLastWriteTimeUtc(file, pinned);

    // 区域坐标真实变化 → 重写并可读回。
    var moved = new PhysicalRectangle(40, 50, 300, 200);
    store.SaveLastRegion(new LastRegionCapture(moved, new[] { secondary, primary }));
    Assert.NotEqual(pinned, File.GetLastWriteTimeUtc(file));
    Assert.Equal(moved, store.Load().LastRegion!.Bounds);
    File.SetLastWriteTimeUtc(file, pinned);

    // 显示器拓扑变化（移除副屏）→ 重写并可读回。
    store.SaveLastRegion(new LastRegionCapture(moved, new[] { primary }));
    Assert.NotEqual(pinned, File.GetLastWriteTimeUtc(file));
    var loaded = store.Load().LastRegion!;
    Assert.Single(loaded.Monitors);
    store.SaveLastRegion(new LastRegionCapture(moved, new[] { primary, secondary }));
    File.SetLastWriteTimeUtc(file, pinned);

    // 主屏标记变化（同一几何双屏互换主屏）→ 重写并可读回。
    store.SaveLastRegion(new LastRegionCapture(moved, new[] {
            new MonitorLayoutSnapshot(0, 0, 1920, 1080, false),
            new MonitorLayoutSnapshot(1920, 0, 1280, 720, true) }));
    Assert.NotEqual(pinned, File.GetLastWriteTimeUtc(file));
    var flipped = store.Load().LastRegion!;
    Assert.Equal(2, flipped.Monitors.Count);
    Assert.Contains(flipped.Monitors, monitor => monitor.IsPrimary && monitor.X == 1920);
  }

  [Fact]
  public void MissingFileFirstSaveAndCorruptFileSameSaveBothPersist()
  {
    // 缺失首次保存 → 必然写入并可读回。
    PortableLayout layout = CreateLayout();
    var store = new CapturePreferencesStore(layout);
    var region = new LastRegionCapture(
        new PhysicalRectangle(5, 6, 100, 80),
        new[] { new MonitorLayoutSnapshot(0, 0, 1920, 1080, true) });
    store.SaveLastRegion(region);
    string file = Path.Combine(layout.DataRoot, "capture-preferences.json");
    Assert.True(File.Exists(file));
    Assert.Equal(region.Bounds, store.Load().LastRegion!.Bounds);

    // 文件损坏后再次保存同一区域：Load 回落 LastRegion=null，不得误判跳过，
    // 必须真实重写修复为可读状态。
    File.WriteAllText(file, "{ not-json");
    store.SaveLastRegion(region);
    CapturePreferences recovered = store.Load();
    Assert.NotNull(recovered.LastRegion);
    Assert.Equal(region.Bounds, recovered.LastRegion!.Bounds);
  }

  private PortableLayout CreateLayout()
  {
    Directory.CreateDirectory(_root);
    PortableLayout layout = PortableLayout.Resolve(
        Path.Combine(_root, "VibeOCR.Next.exe"), "production");
    layout.EnsurePortableState();
    return layout;
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }
}
