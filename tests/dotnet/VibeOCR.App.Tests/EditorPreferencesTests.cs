using System.Text.Json;
using VibeOCR.App.Features.Configuration;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class EditorPreferencesTests : IDisposable
{
  private readonly string root = Path.Combine(Path.GetTempPath(), $"vibeocr-editor-prefs-{Guid.NewGuid():N}");
  private PortableLayout Layout()
  {
    Directory.CreateDirectory(root);
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.Next.exe"), "production");
    layout.EnsurePortableState();
    return layout;
  }

  [Fact]
  public void RoundTripPreservesOtherSettingsAndRejectsCorruptionWithoutWriting()
  {
    PortableLayout layout = Layout();
    File.WriteAllText(layout.ConfigFile, "{\"other\":{\"keep\":true}}");
    EditorPreferences preferences = EditorPreferences.Default with
    {
      Output = new(NameTemplate: "{name}-{index}", Format: "image/jpeg", Width: 1200, Height: 800, CloseAfterCopy: true),
      Hidden = ["pen"],
      Shortcuts = new Dictionary<string, string> { ["pen"] = "p" },
    };
    EditorPreferencesStore.Save(layout, new(preferences, root));
    StoredEditorPreferences loaded = EditorPreferencesStore.Load(layout);
    Assert.Equal("{name}-{index}", loaded.Preferences.Output.NameTemplate);
    Assert.True(loaded.Preferences.Output.CloseAfterCopy);
    Assert.Equal(root, loaded.Directory);
    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(layout.ConfigFile));
    Assert.True(json.RootElement.GetProperty("other").GetProperty("keep").GetBoolean());
    string broken = "{\"image_editor\": broken}";
    File.WriteAllText(layout.ConfigFile, broken);
    Assert.ThrowsAny<JsonException>(() => EditorPreferencesStore.Load(layout));
    Assert.ThrowsAny<JsonException>(() => EditorPreferencesStore.Save(layout, new(preferences, root)));
    Assert.Equal(broken, File.ReadAllText(layout.ConfigFile));
  }

  [Fact]
  public void NewToolsAreConfigurableAndLegacyOrderIsLoadedWithoutLosingSettings()
  {
    PortableLayout layout = Layout();
    string[] newTools = ["spotlight", "magnifier", "objectEraser"];
    EditorPreferences customized = EditorPreferences.Default with
    {
      Hidden = newTools,
      Shortcuts = new Dictionary<string, string> { ["spotlight"] = "s", ["magnifier"] = "m", ["objectEraser"] = "e" },
    };
    EditorPreferencesStore.Save(layout, new(customized, root));
    Assert.Equal(23, EditorPreferencesStore.Load(layout).Preferences.Order.Count);
    EditorPreferences legacy = customized with
    {
      Order = EditorPreferences.Tools.Except(newTools).Reverse().ToArray(),
      Hidden = ["pen"],
      Shortcuts = new Dictionary<string, string> { ["pen"] = "p" },
    };
    string content = JsonSerializer.Serialize(new { image_editor = new StoredEditorPreferences(legacy, root) }, EditorPreferencesStore.Json);
    File.WriteAllText(layout.ConfigFile, content);
    StoredEditorPreferences loaded = EditorPreferencesStore.Load(layout);
    Assert.Equal(legacy.Order.Concat(newTools), loaded.Preferences.Order);
    Assert.Equal(legacy.Hidden, loaded.Preferences.Hidden);
    Assert.Equal("p", loaded.Preferences.Shortcuts["pen"]);
    Assert.Equal(root, loaded.Directory);
    Assert.Equal(content, File.ReadAllText(layout.ConfigFile));
    // Missing arbitrary known tools is corruption, not a legacy schema.
    File.WriteAllText(layout.ConfigFile, JsonSerializer.Serialize(new { image_editor = new StoredEditorPreferences(legacy with { Order = legacy.Order.Skip(1).ToArray() }, root) }, EditorPreferencesStore.Json));
    Assert.Throws<InvalidDataException>(() => EditorPreferencesStore.Load(layout));
  }

  [Fact]
  public void RejectsDuplicateAndSystemStyleShortcutsAndInvalidOutputBounds()
  {
    EditorPreferences defaults = EditorPreferences.Default;
    defaults.Validate();
    Assert.Throws<InvalidDataException>(() => (defaults with { Shortcuts = new Dictionary<string, string> { ["pen"] = "p", ["text"] = "p" } }).Validate());
    Assert.Throws<InvalidDataException>(() => (defaults with { Shortcuts = new Dictionary<string, string> { ["pen"] = "Ctrl+s" } }).Validate());
    Assert.Throws<InvalidDataException>(() => (defaults with { Output = defaults.Output with { Width = 16384, Height = 16384 } }).Validate());
    Assert.Throws<InvalidDataException>(() => (defaults with { Output = defaults.Output with { NameTemplate = "{unknown}" } }).Validate());
  }

  [Theory]
  [InlineData("../../CON", "_CON.png")]
  [InlineData("name", "name.png")]
  public void NamesAreFinalChecked(string source, string expected)
  {
    Assert.Equal(expected, EditorPreferencesStore.FileName(new(NameTemplate: "{name}"), source, 1));
    string generated = EditorPreferencesStore.FileName(new(NameTemplate: "../{name}:{index}"), source, 2);
    Assert.Equal(generated, Path.GetFileName(generated));
    Assert.DoesNotContain(':', generated);
  }

  [Fact]
  public void FileNameExpandsOnlyOriginalTemplateTokens()
  {
    // 来源文件名里的字面占位符不得被链式 Replace 再次展开。
    Assert.Equal(
      "scan-{name}-{index}-2.png",
      EditorPreferencesStore.FileName(new(NameTemplate: "{name}-{index}"), "scan-{name}-{index}.png", 2));
    Assert.Equal(
      "3-a{date}b.png",
      EditorPreferencesStore.FileName(new(NameTemplate: "{index}-{name}"), "a{date}b.png", 3));
  }

  [Fact]
  public async Task ConcurrentExportsAndSameDirectoryNeverOverwriteOriginal()
  {
    Directory.CreateDirectory(root);
    string original = Path.Combine(root, "original.png");
    await File.WriteAllTextAsync(original, "source pixels", TestContext.Current.CancellationToken);
    string[] paths = await Task.WhenAll(
      EditorPreferencesStore.WriteNewAsync(original, root, "original.png", TestContext.Current.CancellationToken),
      EditorPreferencesStore.WriteNewAsync(original, root, "original.png", TestContext.Current.CancellationToken));
    Assert.Equal(2, paths.Distinct().Count());
    Assert.All(paths, path => Assert.NotEqual(original, path));
    Assert.Equal("source pixels", await File.ReadAllTextAsync(original, TestContext.Current.CancellationToken));
    Assert.All(paths, path => Assert.Equal("source pixels", File.ReadAllText(path)));
    Assert.Empty(Directory.GetFiles(root, "*.tmp"));
  }

  [Fact]
  public async Task MissingDirectoryDoesNotWriteAndCancellationLeavesNoTemporaryOutput()
  {
    Directory.CreateDirectory(root);
    string original = Path.Combine(root, "source.png");
    File.WriteAllText(original, "source");
    await Assert.ThrowsAsync<DirectoryNotFoundException>(() => EditorPreferencesStore.WriteNewAsync(original, Path.Combine(root, "missing"), "output.png", TestContext.Current.CancellationToken));
    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditorPreferencesStore.WriteNewAsync(original, root, "output.png", cancelled.Token));
    Assert.False(File.Exists(Path.Combine(root, "output.png")));
    Assert.Empty(Directory.GetFiles(root, "*.tmp"));
  }

  [Fact]
  public void CompositionIsOptionalAndCannotReferenceUnownedAssetsOrUnknownFormats()
  {
    const string asset = "0123456789abcdef0123456789abcdef";
    var composition = new VibpComposition(20, "gradient", "#ffffff", "#123456", 12, 8, "16:9", "horizontal", [asset], Cut: new("horizontal", 30, 60));
    VibpMark image = new() { Id = "mark-1", Tool = "image", Start = new() { X = 0, Y = 0 }, End = new() { X = 10, Y = 10 }, AssetId = asset };
    VibpProjectRules.ValidateComposition(null, []);
    VibpProjectRules.ValidateComposition(composition, [image]);
    string json = JsonSerializer.Serialize(composition, VibpProjectRules.ManifestJson);
    VibpComposition restored = JsonSerializer.Deserialize<VibpComposition>(json, VibpProjectRules.ManifestJson)!;
    Assert.Equal(composition.Cut, restored.Cut);
    Assert.Equal(asset, Assert.Single(restored.AssetIds));
    Assert.Throws<VibpProjectFormatException>(() => VibpProjectRules.ValidateComposition(composition, []));
    Assert.Throws<VibpProjectFormatException>(() => VibpProjectRules.ValidateComposition(composition with { Background = "future-mode" }, [image]));
    Assert.Throws<VibpProjectFormatException>(() => VibpProjectRules.ValidateComposition(composition with { Cut = new("horizontal", 0, 100) }, [image]));
    Assert.Throws<VibpProjectFormatException>(() => VibpProjectRules.ValidateComposition(composition with { Margin = double.NaN }, [image]));
  }

  public void Dispose()
  {
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
  }
}
