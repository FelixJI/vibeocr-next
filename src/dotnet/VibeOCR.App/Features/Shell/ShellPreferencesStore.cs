using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Shell;

public sealed class ShellPreferencesStore(PortableLayout layout)
{
  public bool Load() => AppSettingsStore.ReadForUpdate(layout)["shell"]?["minimize_to_tray"]?.GetValue<bool>() ?? false;

  public void Save(bool enabled)
  {
    JsonObject root = AppSettingsStore.ReadForUpdate(layout);
    JsonObject shell = root["shell"] is null ? new JsonObject() : root["shell"]!.AsObject();
    shell["minimize_to_tray"] = enabled;
    root["shell"] = shell;
    AppSettingsStore.Write(layout, root);
  }
}
