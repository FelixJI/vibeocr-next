using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VibeOCR.App.Features.Shell;

/// <summary>
/// Shell view model for tray/hotkey/about. The platform services (tray icon,
/// global hotkey, single instance) were built in Task 2.6; this view model
/// orchestrates their visibility, hotkey conflict persistence, and the about
/// panel (version/license/links). It does not switch the production updater.
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private bool _trayVisible = true;
    private bool _startWithSystem;
    private bool _minimizeToTray;
    private string _settingsError = string.Empty;
    private readonly ShellPreferencesStore? _preferences;
    private string _registeredHotkey = "Ctrl+Alt+Q";
    private string _pendingHotkey = "Ctrl+Alt+Q";
    private string _hotkeyStatus = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tray icon visibility (show/hide/quit).</summary>
    public bool TrayVisible { get => _trayVisible; set => SetField(ref _trayVisible, value); }

    /// <summary>Boot-at-login toggle backed by a startWithSystem persistence hook.</summary>
    public bool StartWithSystem { get => _startWithSystem; set => SetField(ref _startWithSystem, value); }
    public bool MinimizeToTray { get => _minimizeToTray; private set => SetField(ref _minimizeToTray, value); }
    public string SettingsError { get => _settingsError; private set => SetField(ref _settingsError, value); }

    public string RegisteredHotkey { get => _registeredHotkey; private set => SetField(ref _registeredHotkey, value); }
    public string PendingHotkey { get => _pendingHotkey; set => SetField(ref _pendingHotkey, value); }
    public string HotkeyStatus { get => _hotkeyStatus; private set => SetField(ref _hotkeyStatus, value); }

    public string AppVersion { get; } = typeof(ShellViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public string License { get; } = "MIT";
    public Uri ProjectUri { get; } = new("https://github.com/FelixJI/vibeocr-next", UriKind.Absolute);

    private readonly IHotkeyRegistrar _hotkeyRegistrar;
    private readonly IStartupRegistrar _startupRegistrar;
    private readonly Action _hideWindow;
    private readonly Action _quitApplication;

    public ShellViewModel(
        IHotkeyRegistrar hotkeyRegistrar,
        IStartupRegistrar startupRegistrar,
        Action? hideWindow = null,
        Action? quitApplication = null,
        string initialHotkey = "Ctrl+Alt+Q",
        ShellPreferencesStore? preferences = null)
    {
        _hotkeyRegistrar = hotkeyRegistrar ?? throw new ArgumentNullException(nameof(hotkeyRegistrar));
        _startupRegistrar = startupRegistrar ?? throw new ArgumentNullException(nameof(startupRegistrar));
        _hideWindow = hideWindow ?? (() => { });
        _quitApplication = quitApplication ?? (() => { });
        _registeredHotkey = initialHotkey;
        _pendingHotkey = initialHotkey;
        _preferences = preferences;
        try { StartWithSystem = _startupRegistrar.IsEnabled; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { SettingsError = "无法读取开机自启动状态。"; }
        try { MinimizeToTray = preferences?.Load() ?? false; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        { SettingsError = "无法读取托盘设置，原配置已保留。"; }
    }

    /// <summary>Register the configured shortcut without claiming a failed binding is active.</summary>
    public void InitializeHotkey()
    {
        RegisteredHotkey = string.Empty;
        ApplyHotkey();
    }

    /// <summary>
    /// Apply the pending hotkey. A conflict (registrar returns false) surfaces
    /// a localized status and leaves the registered hotkey unchanged.
    /// </summary>
    public void ApplyHotkey()
    {
        string candidate = PendingHotkey.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            HotkeyStatus = "快捷键不能为空";
            return;
        }
        if (candidate == RegisteredHotkey)
        {
            HotkeyStatus = "快捷键未改变";
            return;
        }
        bool accepted = _hotkeyRegistrar.Register(candidate, out string? conflict);
        if (!accepted)
        {
            HotkeyStatus = string.IsNullOrEmpty(conflict)
                ? "快捷键冲突，注册失败"
                : $"快捷键冲突：{conflict}";
            return;
        }
        RegisteredHotkey = candidate;
        HotkeyStatus = "快捷键已更新";
    }

    public void SetStartWithSystem(bool enabled)
    {
        bool ok = _startupRegistrar.SetEnabled(enabled);
        if (ok) StartWithSystem = enabled;
        SettingsError = ok ? string.Empty : "开机自启动设置失败，请检查 Windows 启动项权限。";
    }

    public void SetMinimizeToTray(bool enabled)
    {
        try
        {
            _preferences?.Save(enabled);
            MinimizeToTray = enabled;
            SettingsError = string.Empty;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        { SettingsError = "托盘设置保存失败，原配置已保留。"; }
    }

    public void OnMinimized()
    {
        if (MinimizeToTray) HideToTray();
    }

    public void HideToTray()
    {
        _hideWindow();
        TrayVisible = true;
    }

    public void Quit()
    {
        _hotkeyRegistrar.Unregister();
        _quitApplication();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public interface IHotkeyRegistrar
{
    bool Register(string hotkey, out string? conflict);
    void Unregister();
}

public interface IStartupRegistrar
{
    bool IsEnabled { get; }
    bool SetEnabled(bool enabled);
}

public sealed class ShellCommands
{
    private readonly ShellViewModel _shell;
    public ShellCommands(ShellViewModel shell) => _shell = shell;
    public void ApplyHotkey() => _shell.ApplyHotkey();
    public void Quit() => _shell.Quit();
}
