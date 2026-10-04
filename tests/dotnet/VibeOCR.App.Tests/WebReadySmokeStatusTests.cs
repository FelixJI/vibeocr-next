using System.Text.Json;
using VibeOCR.App.Services;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WebReadySmokeStatusTests
{
  [Fact]
  public void FailureReplacesPendingStageWithoutLeakingExceptionDetails()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-web-health-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      string health = Path.Combine(root, "health.json");
      WebReadySmokeStatus.Write(health, "starting", "bootstrap-received");
      var error = new InvalidOperationException("fixture private path and token must not be emitted");
      WebReadySmokeStatus.Write(health, "failed", "webview-initializing", error);
      string json = File.ReadAllText(health);
      using JsonDocument report = JsonDocument.Parse(json);
      Assert.Equal(1, report.RootElement.GetProperty("schema_version").GetInt32());
      Assert.Equal("failed", report.RootElement.GetProperty("state").GetString());
      Assert.Equal("webview-initializing", report.RootElement.GetProperty("stage").GetString());
      Assert.Equal("InvalidOperationException", report.RootElement.GetProperty("error_type").GetString());
      Assert.Equal(error.HResult, report.RootElement.GetProperty("error_code").GetInt32());
      Assert.DoesNotContain("token", json);
      Assert.DoesNotContain("bootstrap-received", json);
      Assert.False(File.Exists(health + ".writing"));
    }
    finally { Directory.Delete(root, recursive: true); }
  }
}
