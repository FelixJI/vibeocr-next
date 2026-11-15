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
  public void SettingsLocalizesProductDamageThroughSelectionError()
  {
    // #321 R3-1：选择异常携带 product_runtime_damaged 时，设置页选择错误
    // 统一报修，不再提示安装依赖/选择其他引擎。
    var damaged = new VibeOCR.Platform.Bootstrap.RuntimeSelectionException(
        VibeOCR.Platform.Bootstrap.RuntimeSelectionErrorKind.EngineUnavailable,
        "Recognition mode 'paddle_text' is unavailable (product_runtime_damaged).",
        reasonCode: InferenceClientException.ProductRuntimeDamagedReason);
    string message = SettingsViewModel.LocalizeSelection(damaged);
    // 对真实错误引导断言（而非禁字）：报修 + 明确不在线安装。
    Assert.Contains("随包运行组件损坏", message);
    Assert.Contains("重新获取完整安装包", message);
    Assert.Contains("不会在线安装依赖", message);
    Assert.DoesNotContain("安装所需依赖", message);
    Assert.DoesNotContain("其他引擎", message);
  }

  [Fact]
  public void SettingsSelectionKeepsStableKindsForOtherReasons()
  {
    var unavailable = new VibeOCR.Platform.Bootstrap.RuntimeSelectionException(
        VibeOCR.Platform.Bootstrap.RuntimeSelectionErrorKind.EngineUnavailable,
        "Engine 'paddleocr' is unavailable.");
    Assert.Equal("该引擎当前不可用，请先安装所需依赖或选择其他引擎",
        SettingsViewModel.LocalizeSelection(unavailable));
    var retired = new VibeOCR.Platform.Bootstrap.RuntimeSelectionException(
        VibeOCR.Platform.Bootstrap.RuntimeSelectionErrorKind.RecognitionModeRetired,
        "retired");
    Assert.Equal("识别模式已退役，请重新选择可用模式",
        SettingsViewModel.LocalizeSelection(retired));
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
