using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Configuration;

public sealed record ImageOutputPreset(
  string Id = "default", string NameTemplate = "{name}-{date}", string Format = "image/png",
  double Quality = 0.9, int Width = 0, int Height = 0, bool CloseAfterCopy = false);

public sealed record EditorPreferences(
  ImageOutputPreset Output,
  IReadOnlyList<string> Order,
  IReadOnlyList<string> Hidden,
  IReadOnlyDictionary<string, string> Shortcuts)
{
  public static readonly string[] Tools = ["select", "hand", "textSelect", "rectangle", "ellipse", "arrow", "line", "polyline", "text", "callout", "mosaic", "blur", "solid", "pen", "highlighter", "numbering", "exclude", "crop", "inpaint", "dupmatch"];
  public static EditorPreferences Default => new(new(), Tools.ToArray(), [], new Dictionary<string, string>());

  public void Validate()
  {
    if (Output is null || Output.Id != "default" || Output.NameTemplate is null ||
      Output.NameTemplate.Length is < 1 or > 100 ||
      Output.Format is not ("image/png" or "image/jpeg") ||
      !double.IsFinite(Output.Quality) || Output.Quality is < 0.1 or > 1 ||
      Output.Width is < 0 or > 16384 || Output.Height is < 0 or > 16384 ||
      (Output.Width == 0) != (Output.Height == 0) || (long)Output.Width * Output.Height > 64_000_000 ||
      Regex.IsMatch(Regex.Replace(Output.NameTemplate, "\\{(name|date|index)\\}", ""), "[{}]") ||
      Order is null || Hidden is null || Shortcuts is null ||
      Order.Count != Tools.Length || !Order.ToHashSet(StringComparer.Ordinal).SetEquals(Tools) ||
      Hidden.Distinct().Count() != Hidden.Count || Hidden.Any(tool => !Tools.Contains(tool)) ||
      Shortcuts.Any(pair => !Tools.Contains(pair.Key) || !Regex.IsMatch(pair.Value ?? "", "^[a-z]$")) ||
      Shortcuts.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Shortcuts.Count)
      throw new InvalidDataException("编辑器设置无效或快捷键冲突；原设置已保留。");
  }
}

internal sealed record StoredEditorPreferences(EditorPreferences Preferences, string? Directory);

internal static class EditorPreferencesStore
{
  internal static readonly JsonSerializerOptions Json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
  };

  internal static StoredEditorPreferences Load(PortableLayout? layout)
  {
    if (layout is null) return new(EditorPreferences.Default, null);
    JsonNode? node = AppSettingsStore.ReadForUpdate(layout)["image_editor"];
    if (node is null) return new(EditorPreferences.Default, null);
    StoredEditorPreferences stored = node.Deserialize<StoredEditorPreferences>(Json)
      ?? throw new InvalidDataException("编辑器设置损坏，原文件已保留。");
    if (stored.Preferences is null) throw new InvalidDataException("编辑器设置损坏，原文件已保留。");
    stored.Preferences.Validate();
    return stored;
  }

  internal static void Save(PortableLayout? layout, StoredEditorPreferences stored)
  {
    if (layout is null) throw new InvalidOperationException("设置存储不可用。");
    if (stored.Preferences is null) throw new InvalidDataException("编辑器设置损坏，原文件已保留。");
    stored.Preferences.Validate();
    JsonObject root = AppSettingsStore.ReadForUpdate(layout);
    root["image_editor"] = JsonSerializer.SerializeToNode(stored, Json);
    AppSettingsStore.Write(layout, root);
  }

  internal static string FileName(ImageOutputPreset preset, string sourceName, int index)
  {
    string name = preset.NameTemplate.Replace("{name}", Path.GetFileNameWithoutExtension(sourceName), StringComparison.Ordinal)
      .Replace("{date}", DateTime.Now.ToString("yyyyMMdd"), StringComparison.Ordinal)
      .Replace("{index}", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    name = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
    if (name.Length > 120) name = name[..120];
    if (string.IsNullOrWhiteSpace(name)) name = "vibeocr";
    string stem = name.Split('.')[0];
    if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) name = "_" + name;
    return name + (preset.Format == "image/jpeg" ? ".jpg" : ".png");
  }

  internal static async Task<string> WriteNewAsync(string source, string directory, string fileName, CancellationToken cancellationToken)
  {
    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("输出目录失效，请重新选择。");
    string temporary = Path.Combine(directory, $".vibeocr-{Guid.NewGuid():N}.tmp");
    try
    {
      await using (FileStream input = File.OpenRead(source))
      await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
      {
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
      }
      for (int index = 0; index < 1000; index++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        string candidate = Path.Combine(directory, index == 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}_{index}{Path.GetExtension(fileName)}");
        try { File.Move(temporary, candidate, overwrite: false); return candidate; }
        catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
      }
      throw new IOException("同名文件过多，未覆盖任何已有文件。");
    }
    finally
    {
      try { File.Delete(temporary); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }
}
