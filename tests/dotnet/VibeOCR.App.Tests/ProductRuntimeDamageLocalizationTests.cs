using System.Text.Json;
using VibeOCR.Platform.Inference;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ProductRuntimeDamageLocalizationTests
{
  private static InferenceClientException Error(string? reasonCode)
  {
    var detail = new Dictionary<string, JsonElement>();
    if (reasonCode is not null)
    {
      detail["reason_code"] = JsonDocument.Parse($"\"{reasonCode}\"").RootElement.Clone();
    }
    return new InferenceClientException(
        VibeOCR.Contracts.HttpV2.HttpV2ErrorCode.OcrEngineUnavailable,
        "engine damaged",
        retryable: false,
        detail);
  }

  [Fact]
  public void RecognitionLocalizesProductDamageWithoutModeRewording()
  {
    string damaged = RecognitionViewModel.LocalizeV2(
        Error(InferenceClientException.ProductRuntimeDamagedReason));
    Assert.Contains("随包运行组件损坏", damaged);
    Assert.Contains("重新获取完整安装包", damaged);
    Assert.DoesNotContain("重新选择", damaged);
    Assert.DoesNotContain("准备依赖", damaged);
  }

  [Fact]
  public void RecognitionKeepsStableCodesForOtherReasons()
  {
    Assert.Equal(
        "所选引擎不可用，请重新选择本次识别模式",
        RecognitionViewModel.LocalizeV2(Error("engine_init_failed")));
    Assert.Equal(
        "所选引擎不可用，请重新选择本次识别模式",
        RecognitionViewModel.LocalizeV2(Error(null)));
  }

  [Fact]
  public void SettingsLocalizesProductDamageThroughExceptionOverload()
  {
    var damaged = Error(InferenceClientException.ProductRuntimeDamagedReason);
    Assert.True(damaged.IsProductRuntimeDamaged);
    Assert.Equal(
        InferenceClientException.ProductRuntimeDamagedReason,
        damaged.ReasonCode);
    Assert.Contains("随包运行组件损坏", SettingsViewModel.LocalizeV2(damaged));
    Assert.Equal(
        "所选引擎不可用，请在设置中重选",
        SettingsViewModel.LocalizeV2(Error("engine_not_installed")));
  }
}
