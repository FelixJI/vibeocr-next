using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Workbench;

internal sealed partial class WorkbenchImageOwner
{
  internal sealed record TextLayerSelection(string ModeId, PaddleModeOptions? Options,
    string ServiceInstance, string? EnvironmentId, int? EnvironmentRevision, string Sources);

  private TextLayerSelection? FreezeTextLayerSelection(RecognitionModeOption? mode)
  {
    if (mode is null) return null;
    PaddleModeOptions? options = mode.Id == "paddle_text" &&
      mode.SupportedOptions.Contains("model_preset", StringComparer.Ordinal)
      ? selectionPolicy.GetTaskModeOptions(mode, selectionPolicy.TaskModelPreset) : null;
    ManagedEnvironmentList? environments = settingsOwner.CurrentModel?.Environments?.Snapshot;
    ManagedEnvironment? environment = environments?.Environments.FirstOrDefault(item => item.Id == environments.ActiveId);
    string sources = string.Join(",", settingsOwner.CurrentModel?.Sources.Where(source => source.Selected)
      .Select(source => source.Id).Order(StringComparer.Ordinal).ToArray() ?? []);
    return new(mode.Id, options, supervisorInstanceId() ?? string.Empty,
      environment?.Id, environment?.Revision, sources);
  }

  private bool TextLayerSelectionMatches(TextLayerSelection? frozen)
  {
    TextLayerSelection? current = FreezeTextLayerSelection(FindReadyLocalTextMode(out _));
    // Legacy lightweight layers keep their existing lifetime; ORT layers bind all effective settings.
    return frozen?.Options is null && current?.Options is null || frozen == current;
  }

  private static RecognitionSubmitSelection? TextLayerSubmitSelection(RecognitionModeOption mode, TextLayerSelection? selection)
  {
    if (selection?.Options is null) return null;
    var options = new Dictionary<string, JsonElement>(selection.Options.ToWire(mode), StringComparer.Ordinal)
      { ["local_models_only"] = JsonSerializer.SerializeToElement(true) };
    return new RecognitionSubmitSelection("OCR", mode.Engine, null, options);
  }

  private void InvalidateChangedOrtTextLayers()
  {
    foreach (ImageSessionChannel channel in new[] { recognitionChannel, imageEditChannel, pinEditChannel })
      if (channel.TextLayer is not null && !TextLayerSelectionMatches(channel.TextSelection))
      {
        InvalidateScreenshotTextLayer(channel, channel.Revision);
        PublishTextLayerState(channel, "textlayer.unavailable", "textlayer.selectionChanged");
      }
  }
}
