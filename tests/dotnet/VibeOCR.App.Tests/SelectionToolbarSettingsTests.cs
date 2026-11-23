using System.Text.Json;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class SelectionToolbarSettingsTests : IDisposable
{
  private readonly string root = Path.Combine(Path.GetTempPath(), $"vibeocr-selection-toolbar-{Guid.NewGuid():N}");
  private PortableLayout Layout()
  {
    Directory.CreateDirectory(root);
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.Next.exe"), "production");
    layout.EnsurePortableState();
    return layout;
  }

  [Fact]
  public void RoundTripPreservesOtherSettingsAndClampsToVocabulary()
  {
    PortableLayout layout = Layout();
    File.WriteAllText(layout.ConfigFile, "{\"other\":{\"keep\":true}}");
    SelectionToolbarSettings.Save(layout,
      ["text", "unknown-tool", "text", "mosaic", "rectangle"]);
    Assert.Equal(
      ["text", "mosaic", "rectangle"],
      SelectionToolbarSettings.Load(layout));
    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(layout.ConfigFile));
    Assert.True(json.RootElement.GetProperty("other").GetProperty("keep").GetBoolean());
  }

  [Fact]
  public void MissingNodeFallsBackToDefaultAndCorruptionDoesNotThrow()
  {
    PortableLayout layout = Layout();
    // 无节点：默认序列。
    Assert.Equal(SelectionToolbarSettings.DefaultTools, SelectionToolbarSettings.Load(layout));
    // 空 tools：回默认。
    File.WriteAllText(layout.ConfigFile, "{\"screenshot_toolbar\":{\"tools\":[]}}");
    Assert.Equal(SelectionToolbarSettings.DefaultTools, SelectionToolbarSettings.Load(layout));
    // 损坏 JSON：字段级容错回默认，不抛异常。
    File.WriteAllText(layout.ConfigFile, "{ broken");
    Assert.Equal(SelectionToolbarSettings.DefaultTools, SelectionToolbarSettings.Load(layout));
  }

  [Fact]
  public void EmptySaveFallsBackToDefault()
  {
    PortableLayout layout = Layout();
    SelectionToolbarSettings.Save(layout, ["unknown-only"]);
    Assert.Equal(SelectionToolbarSettings.DefaultTools, SelectionToolbarSettings.Load(layout));
  }

  public void Dispose()
  {
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
  }
}
