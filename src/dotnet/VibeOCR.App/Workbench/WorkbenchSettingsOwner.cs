using VibeOCR.App.Web;
using VibeOCR.App.Features.Configuration;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.ComponentModel;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Services;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchSettingsOwner(Func<CancellationToken, Task>? ensureRuntimeReady, Func<bool>? inferenceAttached, Func<SettingsViewModel> settingsFactory, RecoverableLazy<ShellViewModel> shell, ShellActionDispatcher? shellActions, Func<string?> supervisorInstanceId, WorkbenchOperations operations, Func<CancellationToken, Task> refreshFeatureStates, PortableLayout? optionsLayout = null, Func<nint>? windowHandle = null)
{
  public event Action? PinnedTextEnvironmentChanged;
  internal bool Handles(WorkbenchCommand command) => command is SetWorkerIdleCommand or SetEditorPreferencesCommand or PickImageOutputDirectoryCommand or RefreshRuntimeCommand or SetRecognitionDeviceCommand or SetThemeCommand or SetStartupCommand or SetMinimizeToTrayCommand or BeginHotkeyRecordingCommand or EndHotkeyRecordingCommand or SetActionHotkeyCommand or ResetActionHotkeyCommand or SetFloatingToolbarEnabledCommand or SetFloatingToolbarLayoutCommand or SetFloatingToolbarPreferencesCommand or ShowFloatingToolbarCommand or HideFloatingToolbarCommand or SetDownloadSourceCommand or SetMineruConnectionCommand or SetMineruRecognitionCommand or SetDefaultRecognitionModeCommand or PrepareMineruConnectionCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    SetWorkerIdleCommand idle => await SetWorkerIdleAsync(idle, cancellationToken),
    SetEditorPreferencesCommand editor => SetEditorPreferences(editor.Preferences),
    PickImageOutputDirectoryCommand => await PickImageOutputDirectoryAsync(),
    RefreshRuntimeCommand => await RefreshRuntimeAsync(cancellationToken),
    SetRecognitionDeviceCommand choice => await SetDeviceAsync(choice.Device, cancellationToken),
    SetThemeCommand setTheme => SetTheme(setTheme),
    SetStartupCommand startup => SetStartup(startup),
    SetMinimizeToTrayCommand tray => SetMinimizeToTray(tray),
    BeginHotkeyRecordingCommand recording => BeginHotkeyRecording(recording),
    EndHotkeyRecordingCommand recording => EndHotkeyRecording(recording),
    SetActionHotkeyCommand setActionHotkey => SetActionHotkey(setActionHotkey),
    ResetActionHotkeyCommand resetActionHotkey => ResetActionHotkey(resetActionHotkey),
    SetFloatingToolbarEnabledCommand toolbarEnabled => SetFloatingToolbarEnabled(
      toolbarEnabled),
    SetFloatingToolbarLayoutCommand toolbarLayout => SetFloatingToolbarLayout(
      toolbarLayout),
    SetFloatingToolbarPreferencesCommand toolbarPreferences => SetFloatingToolbarPreferences(
      toolbarPreferences),
    ShowFloatingToolbarCommand => ShowFloatingToolbar(),
    HideFloatingToolbarCommand => HideFloatingToolbar(),
    SetDownloadSourceCommand source => await SetSourceAsync(
      source,
      cancellationToken),
    SetMineruConnectionCommand mineru => await SetMineruConnectionAsync(
      mineru,
      cancellationToken),
    SetMineruRecognitionCommand mineruRecognition => await SetMineruRecognitionAsync(
      mineruRecognition,
      cancellationToken),
    SetDefaultRecognitionModeCommand defaultMode => await SetDefaultRecognitionModeAsync(
      defaultMode,
      cancellationToken),
    PrepareMineruConnectionCommand => await PrepareMineruConnectionAsync(
      cancellationToken),
    _ => throw new InvalidOperationException("Unsupported Settings workbench command."),
  };

  private int deviceChanging;
  // 保留已就绪实例的基线，避免首次设置通知误清贴图取字。
  private string pinnedServiceInstance = supervisorInstanceId() ?? string.Empty;
  private SettingsViewModel? settings;
  internal SettingsViewModel? CurrentModel => settings;
  internal SettingsViewModel EnsureModel() => settings ??= CreateSettings();
  internal WorkbenchTheme Theme => theme;
  internal bool IsDeviceChanging => Volatile.Read(ref deviceChanging) != 0;
  private WorkbenchTheme theme = WorkbenchTheme.System;
  private async Task<SettingsWorkbenchState> RefreshRuntimeAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (ensureRuntimeReady is not null) await ensureRuntimeReady(cancellationToken);
    await settings.LoadSnapshotAsync(cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> SetDeviceAsync(string device, CancellationToken cancellationToken)
  {
    if (Interlocked.CompareExchange(ref deviceChanging, 1, 0) != 0)
      throw new InvalidOperationException("计算设备正在验证中。");
    settings ??= CreateSettings();
    try
    {
      if (ensureRuntimeReady is not null) await ensureRuntimeReady(cancellationToken);
      await settings.SetRecognitionDeviceAsync(device, cancellationToken);
      PinnedTextEnvironmentChanged?.Invoke();
    }
    finally { Interlocked.Exchange(ref deviceChanging, 0); }
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetTheme(SetThemeCommand command)
  {
    theme = command.Theme;
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  private SettingsWorkbenchState SetStartup(SetStartupCommand command)
  {
    shell.Value.SetStartWithSystem(command.Enabled);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  private SettingsWorkbenchState SetMinimizeToTray(SetMinimizeToTrayCommand command)
  {
    shell.Value.SetMinimizeToTray(command.Enabled);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  private SettingsWorkbenchState BeginHotkeyRecording(BeginHotkeyRecordingCommand command)
  {
    ShellActions().BeginHotkeyRecording(command.RecordingId);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  internal SettingsWorkbenchState EndHotkeyRecording(EndHotkeyRecordingCommand command)
  {
    ShellActions().EndHotkeyRecording(command.RecordingId);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  internal void EndHotkeyRecording()
  {
    if (shellActions?.IsHotkeyRecording != true) return;
    shellActions.EndHotkeyRecording();
    operations.Publish(settings is null ? SettingsShellState() : SettingsState(settings));
  }

  /// <summary>
  /// 动作键位设置：结果（成功/冲突/保存失败及当前实际注册状态）以设置页
  /// 的动作状态列表回显，命令本身不因键位被拒而报协议错误。
  /// </summary>
  private SettingsWorkbenchState SetActionHotkey(SetActionHotkeyCommand command)
  {
    settings ??= CreateSettings();
    ShellActions().TrySetHotkeyAction(command.ActionId, command.Hotkey, out _);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState ResetActionHotkey(ResetActionHotkeyCommand command)
  {
    settings ??= CreateSettings();
    ShellActions().ResetHotkeyAction(command.ActionId, out _);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetFloatingToolbarEnabled(
    SetFloatingToolbarEnabledCommand command)
  {
    settings ??= CreateSettings();
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    string? error = current.Enabled == command.Enabled
      ? null
      : ApplyToolbar(actions, current with { Enabled = command.Enabled });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState SetFloatingToolbarLayout(
    SetFloatingToolbarLayoutCommand command)
  {
    settings ??= CreateSettings();
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    string? error = current.Edge == command.Edge && current.AutoHide == command.AutoHide
      ? null
      : ApplyToolbar(actions, current with
      {
        Edge = command.Edge,
        AutoHide = command.AutoHide,
      });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState SetFloatingToolbarPreferences(
    SetFloatingToolbarPreferencesCommand command)
  {
    settings ??= CreateSettings();
    if ((command.LingerMs is null && command.Theme is null && command.PeekPixels is null) ||
      command.PeekPixels is < 1 or > FloatingToolbarSettings.MaximumPeekPixels ||
      command.LingerMs is < FloatingToolbarSettings.MinimumLingerMs or > FloatingToolbarSettings.MaximumLingerMs ||
      command.Theme is not (null or "system" or "light" or "dark"))
    {
      return SettingsState(settings, "收起时间须为 100–5000 毫秒，露出像素须为 1–20，主题须为跟随系统、亮色或深色；原设置已保留。");
    }
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    int lingerMs = command.LingerMs ?? current.LingerMs;
    int peekPixels = command.PeekPixels ?? current.PeekPixels;
    FloatingToolbarTheme theme = command.Theme is null
      ? current.Theme : FloatingToolbarSettings.ParseTheme(command.Theme);
    string? error = current.LingerMs == lingerMs && current.Theme == theme && current.PeekPixels == peekPixels
      ? null
      : ApplyToolbar(actions, current with { LingerMs = lingerMs, Theme = theme, PeekPixels = peekPixels });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState ShowFloatingToolbar()
  {
    settings ??= CreateSettings();
    ShellActions().TryShowFloatingToolbar(out string? error);
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState HideFloatingToolbar()
  {
    settings ??= CreateSettings();
    ShellActions().TryHideFloatingToolbar(out string? error);
    return SettingsState(settings, error);
  }

  /// <summary>
  /// 应用悬浮工具栏偏好：保存失败保旧并显式回显错误，不很报成功。
  /// </summary>
  private static string? ApplyToolbar(
    ShellActionDispatcher actions,
    FloatingToolbarSettings next) =>
    actions.TryApplyFloatingToolbar(next, out string? error) ? null : error;

  private ShellActionDispatcher ShellActions() =>
    shellActions ?? throw new InvalidOperationException(
      "Shell actions are unavailable in this mode.");

  private async Task<SettingsWorkbenchState> SetSourceAsync(
    SetDownloadSourceCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetSourceAsync(
      command.Kind,
      command.SourceId,
      cancellationToken);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> SetWorkerIdleAsync(
    SetWorkerIdleCommand command, CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (!await settings.SetWorkerIdleAsync(command.PaddleSeconds, command.MineruSeconds, cancellationToken))
    {
      operations.Publish(SettingsState(settings));
      throw new InvalidOperationException(settings.Status);
    }
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> SetMineruConnectionAsync(
    SetMineruConnectionCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetMineruConnectionAsync(
      command.Mode,
      command.ApiUrl,
      command.ApiKey,
      cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  /// <summary>
  /// 保存全局 MinerU 识别偏好：能力/目录校验在 SettingsViewModel 内
  /// fail closed；偏好不改变目录可用性，无需刷新 health，任务页状态
  /// 经 RecognitionSelection 快照即时携带新偏好。
  /// </summary>
  private async Task<SettingsWorkbenchState> SetMineruRecognitionAsync(
    SetMineruRecognitionCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetMineruRecognitionAsync(
      command.Tier,
      command.OcrMode,
      command.PageRange,
      command.Language,
      cancellationToken);
    return SettingsState(settings);
  }

  /// <summary>
  /// 保存默认识别模式：能力门禁 + 目录 ready 校验在
  /// SettingsViewModel 内 fail closed；保存成功后重发任务页状态，继承
  /// 选项立即回显新默认，在跑/排队任务参数不受影响。
  /// </summary>
  private async Task<SettingsWorkbenchState> SetDefaultRecognitionModeAsync(
    SetDefaultRecognitionModeCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetDefaultRecognitionModeAsync(
      command.ModeId,
      cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> PrepareMineruConnectionAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.PrepareMineruRemoteAsync(cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private Task RefreshRecognitionCatalogStatesAsync(CancellationToken cancellationToken) => refreshFeatureStates(cancellationToken);

  internal async Task RefreshRecognitionCatalogAsync(CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    // Announce the session before capturing its catalog generation. Initial
    // status/property projection must not invalidate that session's first load.
    OnSettingsChanged();
    settings.ClearSelection();
    try
    {
      if (inferenceAttached?.Invoke() != false)
        await settings.RefreshSelectionAsync(cancellationToken);
    }
    finally
    {
      if (!operations.IsDisposed)
        await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    }
  }

  private SettingsViewModel CreateSettings()
  {
    SettingsViewModel model = settingsFactory();
    model.RuntimeStatus.PropertyChanged += OnSettingsPropertyChanged;
    model.PropertyChanged += OnSettingsPropertyChanged;
    return model;
  }

  internal void OnSettingsChanged()
  {
    if (!operations.IsDisposed && settings is not null)
    {
      string service = supervisorInstanceId() ?? string.Empty;
      bool instanceChanged = service != pinnedServiceInstance;
      // 先钉住新实例：失效会同步触发模型 PropertyChanged 重入本方法，
      // 重入必须按已收敛的实例判定，避免重复失效。
      pinnedServiceInstance = service;
      if (instanceChanged)
      {
        PinnedTextEnvironmentChanged?.Invoke();
        // 服务实例更换（切换/崩溃恢复）：旧实例的在途快照与加速器目标
        // 不得继续作为当前状态投影。
        settings.InvalidateSnapshot();
      }
      operations.Publish(SettingsState(settings));
    }
  }

  internal void OnToolbarStateChanged()
  {
    if (!operations.IsDisposed)
      operations.Publish(settings is null ? SettingsShellState() : SettingsState(settings));
  }

  /// <summary>
  /// 运行环境维护（含切换）事件使在途截图会话识别失效：迟到结果
  /// 不得覆盖当前会话。两个通道各自失效，互不牵连；只接现有维护事件，
  /// 不接管环境状态机。
  /// </summary>
  private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args) => OnSettingsChanged();

  private SettingsWorkbenchState SettingsShellState() => new(
    theme,
    false,
    "settings.ready",
    "unknown",
    shell.Value.StartWithSystem,
    Sources: [],
    MinimizeToTray: shell.Value.MinimizeToTray, ShellSettingsError: shell.Value.SettingsError,
    HotkeyActions: HotkeyActionStates(),
    FloatingToolbar: ToolbarProjection(), EditorPreferences: EditorSettings.Preferences, ImageOutputDirectory: ValidOutputDirectory(), EditorPreferencesError: editorPreferencesError);

  private IReadOnlyList<SettingsHotkeyActionState> HotkeyActionStates() =>
    shellActions is null
      ? []
      : [.. shellActions.GetHotkeyActions().Select(status => new SettingsHotkeyActionState(
        status.ActionId,
        status.DisplayName,
        status.ConfiguredHotkey,
        status.RegisteredHotkey,
        status.Error,
        HotkeyActionCatalog.DefaultBinding(status.ActionId)))];

  private SettingsFloatingToolbarState? ToolbarProjection(string? error = null) =>
    shellActions is null
      ? null
      : new SettingsFloatingToolbarState(
        shellActions.ToolbarSettings.Enabled,
        FloatingToolbarSettings.EdgeName(shellActions.ToolbarSettings.Edge),
        shellActions.ToolbarSettings.AutoHide,
        FormatToolbarVisibility(shellActions.ToolbarVisibility),
        error ?? "",
        shellActions.ToolbarSettings.LingerMs,
        FloatingToolbarSettings.ThemeName(shellActions.ToolbarSettings.Theme),
        shellActions.ToolbarSettings.PeekPixels);

  private static string FormatToolbarVisibility(FloatingToolbarVisibility visibility) =>
    visibility switch
    {
      FloatingToolbarVisibility.UserHidden => "userHidden",
      FloatingToolbarVisibility.EdgeHidden => "edgeHidden",
      FloatingToolbarVisibility.Visible => "visible",
      FloatingToolbarVisibility.Suspended => "suspended",
      _ => "disabled",
    };

  /// <summary>
  /// 全局 MinerU 识别偏好投影：值来自 extra.mineru_recognition，
  /// tier/language 可用性来自当前目录（设置页下拉动态数据源）；
  /// 未保存时 Tier/OcrMode/PageRange/Language 为 null，UI 按目录默认展示。
  /// </summary>
  private static SettingsMineruRecognitionState? ProjectMineruRecognition(
    SettingsViewModel viewModel)
  {
    MineruRecognitionState? state = viewModel.MineruRecognition;
    if (state is null) return null;
    RuntimeSelectionService? selection = viewModel.Selection;
    return new SettingsMineruRecognitionState(
      state.Supported,
      state.Stored,
      state.Tier is { } tier ? WireTierName(tier) : null,
      state.OcrMode is { } ocrMode ? WireOcrModeName(ocrMode) : null,
      state.PageRange,
      state.Language,
      state.Invalid,
      state.InvalidReason,
      selection?.SupportsMineruConfig is true
        ? [.. selection.MineruTiers.Select(tier => new SettingsMineruTierOptionState(
            tier.Id, tier.Availability, tier.ReasonCode))]
        : null,
      selection?.SupportsMineruConfig is true ? selection.MineruLanguages : null,
      selection?.MineruDefaultTier);
  }

  private static string WireTierName(MineruTier tier) => tier switch
  {
    MineruTier.Flash => "flash",
    MineruTier.Basic => "basic",
    MineruTier.Standard => "standard",
    MineruTier.Advanced => "advanced",
    _ => tier.ToString(),
  };

  private static string WireOcrModeName(MineruOcrMode mode) => mode switch
  {
    MineruOcrMode.Auto => "auto",
    MineruOcrMode.Txt => "txt",
    MineruOcrMode.Ocr => "ocr",
    _ => mode.ToString(),
  };

  private SettingsWorkbenchState SettingsState(
    SettingsViewModel viewModel,
    string? toolbarError = null) => new(
    theme,
    viewModel.IsBusy,
    viewModel.RestartRequired ? "settings.restartRequired" : "settings.ready",
    viewModel.Backend,
    shell.Value.StartWithSystem,
    [.. viewModel.Sources.Select(source => new SettingsSourceOptionState(
      source.Kind,
      source.Id,
      source.DisplayName,
      source.Selected))],
    MinimizeToTray: shell.Value.MinimizeToTray, ShellSettingsError: shell.Value.SettingsError,
    HotkeyActions: HotkeyActionStates(),
    FloatingToolbar: ToolbarProjection(toolbarError),
    EditorPreferences: EditorSettings.Preferences, ImageOutputDirectory: ValidOutputDirectory(), EditorPreferencesError: editorPreferencesError,
    PaddleIdleSeconds: viewModel.PaddleIdleSeconds,
    MineruIdleSeconds: viewModel.MineruIdleSeconds,
    Workers: [.. viewModel.ResidencyEntries],
    WorkerSettingsWarning: viewModel.WorkerSettingsWarning,
    StatusMessage: viewModel.Status,
    ServiceStatus: viewModel.RuntimeStatus.ServiceStatus,
    MineruConnection: viewModel.MineruConnection is { } connection
      ? new SettingsMineruConnectionState(
        connection.Supported,
        connection.Mode,
        connection.ApiUrl,
        connection.HasApiKey)
      : null,
    DefaultRecognitionMode: viewModel.DefaultRecognitionMode is { } defaultMode
      ? new SettingsDefaultRecognitionModeState(
        defaultMode.Supported,
        defaultMode.ModeId,
        defaultMode.Stored,
        RecognitionModeCatalog.IsRetiredModeId(defaultMode.ModeId) ? "recognition_mode_retired" : null)
      : null,
    MineruRecognition: ProjectMineruRecognition(viewModel),
    RecognitionModes: viewModel.Selection?.SupportsRecognitionModes is true
      ? [.. viewModel.Selection.RecognitionModes.Select(mode => new SettingsRecognitionModeOptionState(
          mode.Id,
          SettingsViewModel.DisplayName(mode.Id),
          mode.Availability,
          mode.ReasonCode))]
      : null,
    RecognitionDevice: viewModel.RecognitionDevice);

  private StoredEditorPreferences? editorSettings;
  private string? editorPreferencesError;
  internal StoredEditorPreferences EditorSettings
  {
    get
    {
      if (editorSettings is not null) return editorSettings;
      try { editorSettings = EditorPreferencesStore.Load(optionsLayout); }
      catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
      {
        editorPreferencesError = "编辑器设置无法读取，原文件已保留：" + error.Message;
        editorSettings = new(EditorPreferences.Default, null);
      }
      return editorSettings;
    }
  }

  private string? ValidOutputDirectory()
  {
    string? directory = EditorSettings.Directory;
    if (directory is null || Directory.Exists(directory)) return directory;
    editorPreferencesError = "输出目录已失效，请重新选择目录。";
    return null;
  }

  private SettingsWorkbenchState SetEditorPreferences(EditorPreferences preferences)
  {
    var next = EditorSettings with { Preferences = preferences };
    try { EditorPreferencesStore.Save(optionsLayout, next); editorSettings = next; editorPreferencesError = null; }
    catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
    { editorPreferencesError = "保存失败，原设置已保留：" + error.Message; }
    return CurrentState();
  }

  private async Task<SettingsWorkbenchState> PickImageOutputDirectoryAsync()
  {
    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
    picker.FileTypeFilter.Add("*");
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle?.Invoke() ?? 0);
    StorageFolder? folder = await picker.PickSingleFolderAsync();
    if (folder is null) return CurrentState();
    if (!Directory.Exists(folder.Path)) throw new DirectoryNotFoundException("请选择有效输出目录。");
    var next = EditorSettings with { Directory = folder.Path };
    EditorPreferencesStore.Save(optionsLayout, next);
    editorSettings = next;
    editorPreferencesError = null;
    return CurrentState();
  }

  internal void ValidatePresetBinding(string presetId, ImageOutputBinding? binding)
  {
    if (binding is null || binding.Preset != EditorSettings.Preferences.Output ||
      presetId != binding.Preset.Id ||
      !string.Equals(binding.Directory, ValidOutputDirectory(), StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("导出期间输出预设或授权目录已变化，本次未保存；请重试。");
  }

  internal async Task SavePresetImageAsync(WorkbenchAnnotationFile image, string presetId, string sourceName, int index, CancellationToken token)
  {
    ImageOutputPreset preset = EditorSettings.Preferences.Output;
    if (presetId != preset.Id) throw new InvalidDataException("输出预设不存在。");
    string directory = ValidOutputDirectory() ?? throw new DirectoryNotFoundException("请先为输出预设选择有效目录。");
    if (image.MediaType != preset.Format) throw new InvalidDataException("上传格式与输出预设不一致。");
    if (image.Width is < 1 or > VibpProjectLimits.MaximumResizeSide || image.Height is < 1 or > VibpProjectLimits.MaximumResizeSide ||
      (long)image.Width * image.Height > VibpProjectLimits.MaximumResizePixels ||
      preset.Width > 0 && (image.Width != preset.Width || image.Height != preset.Height))
      throw new InvalidDataException("上传尺寸与输出预设或像素预算不一致。");
    await EditorPreferencesStore.WriteNewAsync(image.Path, directory, EditorPreferencesStore.FileName(preset, sourceName, index), token);
  }

  internal SettingsWorkbenchState CurrentState() => settings is null ? SettingsShellState() : SettingsState(settings);
  internal void Stop()
  {
    if (settings is not null)
    {
      settings.RuntimeStatus.PropertyChanged -= OnSettingsPropertyChanged;
      settings.PropertyChanged -= OnSettingsPropertyChanged;
    }
    if (shellActions is not null) shellActions.ToolbarStateChanged -= OnToolbarStateChanged;
  }
}
