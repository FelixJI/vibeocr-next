using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Services;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchSelectionOwner(Func<CancellationToken, Task>? ensureRuntimeReady, Func<bool>? inferenceAttached, PortableLayout? optionsLayout, WorkbenchSettingsOwner settingsOwner, Func<string?> currentTaskEngine)
{
  private readonly Dictionary<string, PaddleModeOptions> modeOptions = new(StringComparer.Ordinal);
  private readonly Dictionary<string, RapidDeviceOptions> modeDeviceOptions = new(StringComparer.Ordinal);
  internal string? TaskModelPreset { get; set; }
  internal string? RuntimeDefaultModeId =>
    settingsOwner.CurrentModel?.RecognitionSelection?.DefaultRecognitionModeId;

  /// <summary>
  /// 单次/批量/PDF 共用的继承解析：显式本次 override 优先；能力已声明时
  /// 绑定回显默认，Invalid/Unread 在提交协商时明确拒绝并指向设置修复，
  /// 不猜 rapid_text；仅能力缺失走旧语义。
  /// </summary>
  internal static string? EffectiveModeIdOrDefault(
    RecognitionSelectionSnapshot? snapshot,
    string? taskEngine,
    bool requireUsable)
  {
    if (taskEngine is not null || snapshot is null)
    {
      return taskEngine;
    }
    switch (snapshot.DefaultMode)
    {
      case RuntimeDefaultModeBinding.Bound:
        return snapshot.DefaultRecognitionModeId;
      case RuntimeDefaultModeBinding.NotApplicable:
        return null;
      default:
        if (requireUsable)
        {
          throw new RecognitionModeUnavailableException(
            "当前默认识别模式无效或尚未读取，已拒绝按通用文字识别执行；请在设置 · 识别默认模式中重新选择并保存。");
        }
        return null;
    }
  }

  /// <summary>
  /// Project catalog modes for the recognition page. Only an explicit task
  /// override is IsTaskOverride; the committed runtime default is Selected
  /// while no override is present, and an empty choice still delegates to it.
  /// </summary>
  internal IReadOnlyList<RecognitionEngineChoice>? RecognitionEngines(string? taskModelPreset = null)
  {
    RecognitionSelectionSnapshot? engineSnapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    RuntimeSelectionService? selection = engineSnapshot?.Catalog;
    if (selection is null || (!selection.SupportsEngineSelection && !selection.SupportsRecognitionModes))
    {
      return null;
    }
    string? taskEngine = currentTaskEngine();
    if (selection.SupportsRecognitionModes)
    {
      string? task = taskEngine;
      string? defaultId = RuntimeDefaultModeId;
      return [.. selection.RecognitionModes.Select(mode => new RecognitionEngineChoice(
        mode.Id, SettingsViewModel.DisplayName(mode.Id),
        task == mode.Id || (task is null && mode.Id == defaultId), task == mode.Id,
        TryProjectMineruConfig(engineSnapshot, mode.Id, out _) ? mode.Availability : "unavailable",
        mode.Availability == "preparation_required" && mode.RequiredComponent is not null,
        mode.LifecycleKind,
        mode.SupportsPreload, mode.SupportsTtl, mode.SupportsPinning, mode.SupportsRelease, mode.Family, mode.SupportedOptions, GetProjectedOptions(mode), mode.ReasonCode,
        IsDefault: mode.Id == defaultId,
        TaskModelPreset: taskModelPreset,
        DefaultModelPreset: mode.SupportedOptions.Contains("model_preset", StringComparer.Ordinal)
          ? GetModeOptions(mode)?.ModelPreset ?? "small" : null))];
    }
    bool isOverride = taskEngine is not null;
    return [.. selection.EngineOptions.Select(option => new RecognitionEngineChoice(
      OcrEngineWire.Format(option.Engine),
      SettingsViewModel.DisplayName(option.Engine),
      isOverride && OcrEngineWire.Format(option.Engine) == taskEngine,
      isOverride &&
        OcrEngineWire.Format(option.Engine) == taskEngine,
      option.Availability.ToString().ToLowerInvariant(),
      !option.IncludedInBase))];
  }

  internal void SetModeOptions(string modeId, PaddleModeOptions options) => modeOptions[modeId] = options;

  internal void SetDeviceOptions(string modeId, RapidDeviceOptions options) => modeDeviceOptions[modeId] = options;

  /// <summary>device 选项存取：支持面由目录 supported_options 真值门控，不在宿主重复模式清单。</summary>
  internal RapidDeviceOptions? GetDeviceOptions(RecognitionModeOption? mode)
  {
    if (mode?.SupportedOptions.Contains("device", StringComparer.Ordinal) is not true ||
        mode.Id.StartsWith("paddle_", StringComparison.Ordinal)) return null;
    if (!modeDeviceOptions.TryGetValue(mode.Id, out RapidDeviceOptions? options))
    {
      options = optionsLayout is null ? new() : RapidDeviceOptions.Load(optionsLayout, mode.Id);
      modeDeviceOptions.Add(mode.Id, options);
    }
    return options;
  }

  /// <summary>提交冻结点合并投影：两类选项严格 ToWire，不支持/越界在此拒绝（#110 AC2）。</summary>
  internal IReadOnlyDictionary<string, System.Text.Json.JsonElement>? GetWireOptions(
    RecognitionModeOption? mode)
  {
    if (mode is null) return null;
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? paddle = GetTaskModeOptions(mode, TaskModelPreset)?.ToWire(mode);
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? device = GetDeviceOptions(mode)?.ToWire(mode);
    if (paddle is null || paddle.Count == 0) return device is null || device.Count == 0 ? null : device;
    if (device is null || device.Count == 0) return paddle;
    var merged = new Dictionary<string, System.Text.Json.JsonElement>(paddle, StringComparer.Ordinal);
    foreach (var pair in device) merged.Add(pair.Key, pair.Value);
    return merged;
  }

  /// <summary>状态展示合并投影：各自按目录过滤，不支持字段回退默认显示。</summary>
  internal IReadOnlyDictionary<string, System.Text.Json.JsonElement>? GetProjectedOptions(
    RecognitionModeOption? mode)
  {
    if (mode is null) return null;
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? paddle = GetModeOptions(mode)?.ProjectWire(mode);
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? device = GetDeviceOptions(mode)?.ProjectWire(mode);
    if (paddle is null || paddle.Count == 0) return device is null || device.Count == 0 ? null : device;
    if (device is null || device.Count == 0) return paddle;
    var merged = new Dictionary<string, System.Text.Json.JsonElement>(paddle, StringComparer.Ordinal);
    foreach (var pair in device) merged.Add(pair.Key, pair.Value);
    return merged;
  }

  internal PaddleModeOptions? GetModeOptions(RecognitionModeOption? mode)
  {
    if (mode?.Id.StartsWith("paddle_", StringComparison.Ordinal) is not true) return null;
    if (!modeOptions.TryGetValue(mode.Id, out PaddleModeOptions? options))
    {
      options = optionsLayout is null ? new() : PaddleModeOptions.Load(optionsLayout, mode.Id);
      modeOptions.Add(mode.Id, options);
    }
    return options;
  }

  internal static void ValidateTaskModelPreset(string? preset)
  {
    if (preset is not null and not ("tiny" or "small" or "medium"))
      throw new ArgumentException("Unsupported task model preset.");
  }

  internal PaddleModeOptions? GetTaskModeOptions(RecognitionModeOption? mode, string? preset)
  {
    ValidateTaskModelPreset(preset);
    PaddleModeOptions? options = GetModeOptions(mode);
    // A task override never writes the persisted default and only applies where declared.
    return preset is not null && options is not null &&
      mode?.SupportedOptions.Contains("model_preset", StringComparer.Ordinal) is true
      ? options with { ModelPreset = preset } : options;
  }

  internal static bool TryProjectMineruConfig(
    RecognitionSelectionSnapshot? snapshot,
    string? modeId,
    out MineruConfig? config)
  {
    try
    {
      config = snapshot?.MineruConfigFor(modeId);
      return true;
    }
    catch (RuntimeSelectionException)
    {
      config = null;
      return false;
    }
  }

  internal static RecognitionModeOption? TryFindRecognitionMode(
    RuntimeSelectionService selection,
    string modeId)
  {
    try
    {
      return selection.FindRecognitionMode(modeId);
    }
    catch (RuntimeSelectionException)
    {
      // 环境切换后旧选择可能不在新目录：状态投影不崩溃；提交路径由
      // requireUsable 协商拒绝，不静默降级。
      return null;
    }
  }

  /// <summary>
  /// 显式选择的识别模式（非 legacy 引擎 id）在模式目录缺失/不可用时拒绝
  /// 提交：可恢复失败并指向环境，绝不静默回退通用文字识别。仅未选择
  /// （Runtime 默认）或旧版引擎 id 的兼容路径允许默认管线提交。
  /// </summary>
  internal static void EnsureUsableModeOverrideOrThrow(
    RuntimeSelectionService? selection,
    string? taskEngine)
  {
    if (string.IsNullOrWhiteSpace(taskEngine) || OcrEngineWire.Parse(taskEngine) is not null)
    {
      return;
    }
    throw new RecognitionModeUnavailableException(selection is null
      ? $"任务识别模式 {taskEngine} 需要的识别模式目录尚未加载（运行环境可能未就绪），已拒绝按通用文字识别执行；请在设置中检查运行环境后重试。"
      : $"当前运行环境不支持识别模式 {taskEngine}，已拒绝按通用文字识别执行；请切换或准备对应运行环境。");
  }

  /// <summary>
  /// Load the authoritative runtime selection catalog. While the Supervisor
  /// client is unattached the load is skipped: bootstrap must not block the
  /// window, and recognition commands re-load through
  /// <see cref="EnsureSelectionLoadedForSubmitAsync"/> before freezing submit
  /// parameters, so no submit path depends on the gateway default. Returns
  /// whether the catalog was (re)loaded.
  /// </summary>
  internal async Task<bool> EnsureSelectionLoadedAsync(CancellationToken cancellationToken)
  {
    settingsOwner.EnsureModel();
    if (settingsOwner.IsEnvironmentSwitching) return false;
    if (inferenceAttached?.Invoke() == false)
    {
      return false;
    }
    // Always cross SettingsViewModel's single-flight gate. A refresh may be
    // replacing an older snapshot even while Selection remains non-null.
    return await settingsOwner.EnsureModel().LoadSelectionAsync(cancellationToken);
  }

  /// <summary>
  /// 提交路径的目录加载：Supervisor 未 attach 时等待启动完成（Deferred
  /// 网关内建等待）取得权威目录与已提交默认后才冻结提交参数；读取被
  /// 代际失效丢弃或仍处切换窗口时返回 false，由调用方取消提交而非
  /// 猜默认（不重试）。
  /// </summary>
  internal async Task<bool> EnsureSelectionLoadedForSubmitAsync(
    CancellationToken cancellationToken)
  {
    settingsOwner.EnsureModel();
    if (ensureRuntimeReady is not null) await ensureRuntimeReady(cancellationToken);
    settingsOwner.OnSettingsChanged();
    if (settingsOwner.IsEnvironmentSwitching)
    {
      AppLog.Warn("Recognition submit cancelled: environment switch refresh is still running.");
      return false;
    }
    bool loaded = await settingsOwner.EnsureModel().LoadSelectionAsync(cancellationToken);
    bool switching = settingsOwner.IsEnvironmentSwitching;
    if (!loaded || switching)
      AppLog.Warn(switching
        ? "Recognition submit cancelled: environment switch began during catalog loading."
        : "Recognition submit cancelled: catalog was invalidated during loading.");
    return loaded && !switching;
  }

  /// <summary>
  /// 采集型识别的延迟冻结回调：输入采集后按启动时意图（跟随默认，忽略
  /// 等待期间的新 UI 选择）在权威目录上解析提交参数；环境切换窗口返回
  /// null 取消本次提交；目录不支持模式时保持旧 Backend 的 OCR 缺省兼容。
  /// </summary>
  internal Func<CancellationToken, Task<RecognitionSubmitSelection?>>
    AwaitSelectionBeforeFreeze() => async ct =>
  {
    if (!await EnsureSelectionLoadedForSubmitAsync(ct))
      return null;
    RecognitionSelectionSnapshot? snapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    if (snapshot?.Catalog.SupportsRecognitionModes is not true)
      return new RecognitionSubmitSelection("OCR", null, null, null);
    RuntimeSelectionService selection = snapshot.Catalog;
    string? defaultId = EffectiveModeIdOrDefault(
      snapshot, taskEngine: null, requireUsable: true);
    RecognitionModeOption? mode = string.IsNullOrWhiteSpace(defaultId)
      ? null
      : selection.SelectRecognitionMode(defaultId);
    MineruConfig? config = mode is null ? null : snapshot.MineruConfigFor(mode.Id);
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? options = GetWireOptions(mode);
    return new RecognitionSubmitSelection(
      mode?.PipelineId ?? "OCR", mode?.Engine, config, options);
  };

}
