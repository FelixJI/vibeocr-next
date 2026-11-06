using System.Text.Json;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class MineruRemoteWithoutLocalEnginesTests
{
  [Fact]
  public async Task ProductHostEnablesRemoteConfigurationAndPreloadWithoutLocalEngines()
  {
    var inference = new RemoteHostInferenceClient();
    var settings = new SettingsViewModel(inference);
    await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
    await settings.SetMineruConnectionAsync(
      "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);
    Assert.Equal(0, inference.UpdateCalls);

    inference.SwitchToRemoteCapableCatalog();
    await settings.RefreshSelectionAsync(TestContext.Current.CancellationToken);
    await settings.SetMineruConnectionAsync(
      "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);
    Assert.Equal(1, inference.UpdateCalls);
    Assert.True(settings.MineruConnection?.IsRemote);
    JsonElement connection = inference.LastUpdate!.Extra["mineru_connection"];
    Assert.Equal("remote", connection.GetProperty("mode").GetString());
    Assert.Equal(
      "https://mineru.example.com", connection.GetProperty("api_url").GetString());

    // 保存后可触发真实预加载请求（pipelines=MinerU、mineru_document）。
    await settings.PrepareMineruRemoteAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, inference.PreloadCalls);
    Assert.Equal([Wire.RecognitionModeId.MineruDocument], inference.LastPreloadModes);
    Assert.Contains("准备已执行", settings.Status);
  }

  [Fact]
  public async Task MineruConfigStillWaitingWithoutSnapshotKeepsOriginalHint()
  {
    var viewModel = new SettingsViewModel(new DeferredInferenceClient());
    await viewModel.SetMineruConnectionAsync(
      "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);
    // 环境快照未同步（服务启动中）：保留原有等待提示。
    Assert.Equal("运行时目录尚未加载，请先启动识别服务。", viewModel.Status);
  }

  public static TheoryData<string, HttpV2ErrorCode> PreparationFailureCases => new()
  {
    { "mineru_api_endpoint_incompatible", HttpV2ErrorCode.ValidationError },
    { "mineru_api_authentication_failed", HttpV2ErrorCode.ValidationError },
    { "some_other_reason", HttpV2ErrorCode.ValidationError },
    { "mineru_api_endpoint_incompatible", HttpV2ErrorCode.InternalError },
  };

  [Theory]
  [MemberData(nameof(PreparationFailureCases))]
  public void LocalizeMineruPreparationFailureMapsOnlyWhitelistedValidationErrorReasons(
    string reason, HttpV2ErrorCode code)
  {
    var exception = new InferenceClientException(
      code, "upstream said no", retryable: false,
      new Dictionary<string, JsonElement>
      {
        ["reason"] = JsonSerializer.SerializeToElement(reason),
      });

    string status = SettingsViewModel.LocalizeMineruPreparationFailure(exception);

    if (code == HttpV2ErrorCode.ValidationError &&
        reason == "mineru_api_endpoint_incompatible")
    {
      Assert.Contains("根地址", status);
      Assert.Contains("/file_parse", status);
    }
    else if (code == HttpV2ErrorCode.ValidationError &&
             reason == "mineru_api_authentication_failed")
    {
      Assert.Contains("API Key", status);
    }
    else
    {
      // 非白名单 reason 或非 ValidationError：回落既有 v2 本地化。
      Assert.Equal("操作失败", status);
    }
    // 不回显服务正文。
    Assert.DoesNotContain("upstream said", status);
  }

  private sealed class RemoteHostInferenceClient : InferenceClientStub, IInferenceClient
  {
    public int UpdateCalls { get; private set; }
    public int PreloadCalls { get; private set; }
    public IReadOnlyList<Wire.RecognitionModeId>? LastPreloadModes { get; private set; }
    public SettingsSnapshot? LastUpdate { get; private set; }
    private SettingsSnapshot Settings { get; set; } = new();

    public Wire.Health Health { get; set; } = EngineOnlyHealth();

    /// <summary>宿主启动后的目录：声明远程能力与全部模式（Runtime 真实行为）。</summary>
    public void SwitchToRemoteCapableCatalog() => Health = RemoteCapableHealth();

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Health);
    public override Task<SettingsSnapshot> GetSettingsAsync(
      CancellationToken cancellationToken) => Task.FromResult(Settings);
    public override Task<SettingsSnapshot> UpdateSettingsAsync(
      SettingsSnapshot snapshot, CancellationToken cancellationToken)
    {
      UpdateCalls++;
      LastUpdate = snapshot;
      Settings = snapshot;
      return Task.FromResult(snapshot);
    }
    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());
    public Task<ResidencyStatus> PreloadRuntimeAsync(
      Wire.RuntimePreloadRequest request, CancellationToken cancellationToken)
    {
      PreloadCalls++;
      LastPreloadModes = request.RecognitionModes?.ToArray();
      return Task.FromResult(new ResidencyStatus());
    }
    public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(
      CancellationToken cancellationToken) => Task.FromResult(new RuntimeStatusSnapshot
      {
        InstanceId = "sup-remote-host",
        ServiceState = RuntimeServiceState.Ready,
        BackendVersion = "0.14.0",
        Profile = new RuntimeProfileStatus
        {
          ProfileId = "win-x64-cpu",
          Accelerator = RuntimeAccelerator.Cpu,
          Components = [],
        },
      });

    private static Wire.Health EngineOnlyHealth() => new()
    {
      SchemaVersion = 2,
      InstanceId = "sup-remote-host",
      ProtocolVersion = 2,
      Ready = true,
      Draining = false,
      Capabilities = [RuntimeSelectionService.EngineSelectionCapability],
      CapabilityDescriptors = [new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.EngineSelectionCapability,
        Lifecycle = "active", IntroducedIn = "2.0.0", DeprecatedIn = null,
        SunsetAt = null, Replacement = null,
        OcrEngineCatalog = new Wire.OcrEngineCatalog
        {
          Engines = [new Wire.OcrEngineDescriptor
          {
            Id = Wire.OcrEngineId.Paddleocr,
            Availability = Wire.OcrEngineAvailability.Unavailable,
            IncludedInBase = true,
            ReasonCode = "engine_not_installed",
            RequiredComponent = null,
          }],
        },
      }],
    };
  }

  private static Wire.Health RemoteCapableHealth() => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-remote-prepare",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.MineruRemoteApiCapability,
      RuntimeSelectionService.RecognitionModesCapability,
    ],
    CapabilityDescriptors =
    [
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.RecognitionModesCapability,
        Lifecycle = "active",
        IntroducedIn = "2.9.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        RecognitionModeCatalog = new Wire.RecognitionModeCatalog
        {
          Modes =
          [
            Mode(Wire.RecognitionModeId.WindowsText),
            Mode(Wire.RecognitionModeId.PaddleText),
            Mode(Wire.RecognitionModeId.PaddleStructure),
            Mode(Wire.RecognitionModeId.MineruDocument, mineru: true),
            Mode(Wire.RecognitionModeId.PaddleTable),
          ],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(
    Wire.RecognitionModeId id, bool mineru = false)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem, Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.BaseRuntime, Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    // 目录如实保持 preparation_required（本地组件缺失/远程未验证 tier），
    // 不得因远程配置谎标 ready；测试仅消费目录形状。
    return new Wire.RecognitionModeDescriptor
    {
      Id = id,
      Family = family,
      PipelineId = pipeline,
      Engine = engine,
      Provisioning = provisioning,
      Availability = Wire.RecognitionModeAvailability.PreparationRequired,
      ReasonCode = mineru ? "runtime_component_missing" : "runtime_component_missing",
      RequiredComponent = mineru ? "mineru-cpu" : "paddleocr-ort-cpu",
      SupportedOptions = [],
      Lifecycle = new Wire.RecognitionModeLifecycle
      {
        Kind = lifecycle,
        SupportsPreload = mineru,
        SupportsTtl = mineru,
        SupportsPinning = false,
        SupportsRelease = mineru,
      },
    };
  }

}
