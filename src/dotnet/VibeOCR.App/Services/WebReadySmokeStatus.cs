using System.Text.Json;

namespace VibeOCR.App.Services;

internal static class WebReadySmokeStatus
{
  public static bool Enabled =>
    Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "web-ready";

  public static void Stage(string stage)
  {
    if (!Enabled) return;
    WriteBestEffort("starting", stage);
  }

  public static void Fail(string stage, Exception error)
  {
    if (!Enabled) return;
    WriteBestEffort("failed", stage, error);
    Environment.Exit(1);
  }

  private static void WriteBestEffort(string state, string stage, Exception? error = null)
  {
    try
    {
      Write(Environment.GetEnvironmentVariable("VIBEOCR_WEB_READY_FILE"), state, stage, error);
    }
    catch (Exception writeError) when (
      writeError is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
      // Diagnostics must not replace the original startup/verification failure.
      AppLog.Warn($"Web workbench smoke health unavailable: {writeError.GetType().Name}");
    }
  }

  internal static void Write(string? path, string state, string stage, Exception? error = null)
  {
    if (string.IsNullOrWhiteSpace(path)) return;
    // The health file crosses a process boundary and may be read at the outer
    // deadline. Replace it atomically so a forced exit cannot leave partial JSON.
    string temporary = path + ".writing";
    File.WriteAllText(temporary, JsonSerializer.Serialize(new
    {
      schema_version = 1,
      state,
      stage,
      error_type = error?.GetType().Name,
      error_code = error?.HResult,
    }));
    File.Move(temporary, path, overwrite: true);
  }
}
