using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Shell;

/// <summary>
/// Windows 全局快捷键登记器：一个 GlobalHotkeyService 上按动作目录登记多个
/// WM_HOTKEY，实际注册 ID 与动作一一对应；换键流程为“先注册新键、持久化
/// 成功后才释放旧键”，任一步失败保留原注册并返回准确错误。旧
/// IHotkeyRegistrar（单键）语义映射到 screenshot_recognize 动作，兼容既有
/// Shell 接线。配置读写走 AppSettingsStore，只改 hotkeys 节点，不覆盖其他
/// 字段；hotkeys.global_screenshot 旧字段与 screenshot_recognize 保持同步，
/// 旧版本回滚后键位语义不变。
/// </summary>
internal sealed class WindowsHotkeyRegistrar(
    GlobalHotkeyService service,
    PortableLayout layout) : IHotkeyRegistrar, IDisposable
{
    private const string CorruptConfigMessage =
        "配置文件已损坏，无法保存快捷键；原文件已保留";

    private readonly GlobalHotkeyService _service = service;
    private readonly PortableLayout _layout = layout;
    private readonly object _sync = new();
    private readonly Dictionary<string, ActionBinding> _bindings = [];
    private readonly Dictionary<int, string> _idToAction = [];
    private bool _catalogLoaded;
    private int _nextId;

    private sealed class ActionBinding
    {
        public string? Configured;

        public string? Registered;

        public IDisposable? Registration;

        public int RegisteredId;

        public string? Error;
    }

    // ---- 旧单动作路径：global_screenshot ⇔ screenshot_recognize ----

    public bool Register(string hotkey, out string? conflict) =>
        SetActionHotkey(HotkeyActionCatalog.ScreenshotRecognize, hotkey, out conflict);

    public void Unregister()
    {
        lock (_sync)
        {
            if (_bindings.TryGetValue(HotkeyActionCatalog.ScreenshotRecognize, out ActionBinding? binding))
            {
                ReleaseLocked(binding);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (ActionBinding binding in _bindings.Values)
            {
                ReleaseLocked(binding);
            }

            _idToAction.Clear();
        }

        _service.Dispose();
    }

    // ---- 多动作路径 ----

    /// <summary>
    /// 读取配置并注册所有已绑定动作。配置不可读时不注册任何动作，错误经
    /// GetActionStatuses 呈现；单条注册失败（非法/被占用）不抛出，记录在该
    /// 动作状态里供上层展示与重试；不回写配置。
    /// </summary>
    public void InitializeActions()
    {
        lock (_sync)
        {
            if (EnsureCatalogLoadedLocked() is { } loadError)
            {
                return;
            }

            foreach (string actionId in HotkeyActionCatalog.Actions)
            {
                ActionBinding binding = _bindings[actionId];
                if (binding.Configured is not { Length: > 0 } combo
                    || binding.Registration is not null)
                {
                    continue;
                }

                try
                {
                    (HotkeyModifiers modifiers, uint virtualKey) = Parse(combo);
                    int id = Interlocked.Increment(ref _nextId);
                    IDisposable registration = _service.Register(
                        id,
                        modifiers | HotkeyModifiers.NoRepeat,
                        virtualKey);
                    AdoptLocked(actionId, binding, combo, registration, id);
                }
                catch (Exception error) when (
                    error is ArgumentException or HotkeyRegistrationException or InvalidOperationException)
                {
                    binding.Error = error is HotkeyRegistrationException
                        ? "快捷键注册失败：该组合可能已被其他应用占用。"
                        : error.Message;
                }
            }
        }
    }

    /// <summary>
    /// 按实际注册 ID 解析动作。ID 单调分配且释放后永不复用，被替换的旧 ID
    /// 不会触发任何动作，也不会映射到其他动作。
    /// </summary>
    public bool TryResolveAction(int hotkeyId, [NotNullWhen(true)] out string? actionId)
    {
        lock (_sync)
        {
            if (_idToAction.TryGetValue(hotkeyId, out string? owner))
            {
                actionId = owner;
                return true;
            }

            actionId = null;
            return false;
        }
    }

    /// <summary>全部动作的配置与实际注册状态，供设置界面读取。</summary>
    public IReadOnlyList<HotkeyActionStatus> GetActionStatuses()
    {
        lock (_sync)
        {
            if (EnsureCatalogLoadedLocked() is { } loadError)
            {
                // 配置不可读：绑定未知，不能冒充默认/未绑定成功状态。
                return HotkeyActionCatalog.Actions
                    .Select(actionId => new HotkeyActionStatus(
                        actionId,
                        HotkeyActionCatalog.DisplayName(actionId),
                        ConfiguredHotkey: null,
                        RegisteredHotkey: null,
                        Error: loadError))
                    .ToArray();
            }

            return HotkeyActionCatalog.Actions
                .Select(actionId =>
                {
                    ActionBinding binding = _bindings[actionId];
                    return new HotkeyActionStatus(
                        actionId,
                        HotkeyActionCatalog.DisplayName(actionId),
                        binding.Configured,
                        binding.Registered,
                        binding.Error);
                })
                .ToArray();
        }
    }

    /// <summary>
    /// 设置动作键位：null/空白表示禁用。换键时先注册新键，持久化成功后才
    /// 释放旧键；冲突/重复/非法/保存失败均保留旧注册并返回错误。
    /// </summary>
    public bool SetActionHotkey(string actionId, string? hotkey, out string? error)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        if (!HotkeyActionCatalog.IsKnown(actionId))
        {
            error = $"未知快捷键动作：{actionId}";
            return false;
        }

        string? combo = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey.Trim();
        lock (_sync)
        {
            if (EnsureCatalogLoadedLocked() is { } loadError)
            {
                error = loadError;
                return false;
            }

            ActionBinding binding = _bindings[actionId];
            if (combo is null)
            {
                return DisableLocked(binding, out error);
            }

            return ReplaceLocked(actionId, binding, combo, out error);
        }
    }

    /// <summary>恢复动作默认键位（仅 screenshot_recognize 有默认绑定）。</summary>
    public bool ResetActionToDefault(string actionId, out string? error) =>
        SetActionHotkey(actionId, HotkeyActionCatalog.DefaultBinding(actionId), out error);

    private bool ReplaceLocked(
        string actionId,
        ActionBinding binding,
        string combo,
        out string? error)
    {
        try
        {
            (HotkeyModifiers modifiers, uint virtualKey) = Parse(combo);

            // 已是当前实际注册键位：按解析出的 native 组合判定同一键，
            // 修饰键次序/大小写/CONTROL 别名差异不算变化，无操作成功，
            // 避免重复注册自身组合后误报占用。
            if (binding.Registered is { } registered
                && TryParseCombo(registered, out HotkeyModifiers registeredModifiers, out uint registeredKey, out _)
                && registeredModifiers == modifiers
                && registeredKey == virtualKey)
            {
                error = null;
                return true;
            }

            foreach (KeyValuePair<string, ActionBinding> other in _bindings)
            {
                if (other.Key != actionId
                    && other.Value.Configured is { Length: > 0 } configured
                    && TryParseCombo(configured, out HotkeyModifiers otherModifiers, out uint otherKey, out _)
                    && otherModifiers == modifiers
                    && otherKey == virtualKey)
                {
                    binding.Error = $"快捷键已被动作「{HotkeyActionCatalog.DisplayName(other.Key)}」使用";
                    error = binding.Error;
                    return false;
                }
            }

            int nextId = Interlocked.Increment(ref _nextId);
            IDisposable next = _service.Register(
                nextId,
                modifiers | HotkeyModifiers.NoRepeat,
                virtualKey);
            string? previousConfigured = binding.Configured;
            binding.Configured = combo;
            try
            {
                PersistLocked();
            }
            catch
            {
                binding.Configured = previousConfigured;
                next.Dispose();
                throw;
            }

            AdoptLocked(actionId, binding, combo, next, nextId);
            error = null;
            return true;
        }
        catch (ArgumentException invalid)
        {
            binding.Error = invalid.Message;
            error = invalid.Message;
            return false;
        }
        catch (HotkeyRegistrationException)
        {
            binding.Error = "快捷键注册失败：该组合可能已被其他应用占用。";
            error = binding.Error;
            return false;
        }
        catch (InvalidOperationException conflict)
        {
            binding.Error = conflict.Message;
            error = conflict.Message;
            return false;
        }
        catch (JsonException)
        {
            binding.Error = CorruptConfigMessage;
            error = CorruptConfigMessage;
            return false;
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            binding.Error = $"无法读取或写入配置文件，快捷键未更改：{io.Message}";
            error = binding.Error;
            return false;
        }
    }

    private bool DisableLocked(ActionBinding binding, out string? error)
    {
        if (binding.Configured is null && binding.Registration is null)
        {
            error = null;
            return true;
        }

        try
        {
            // 先持久化禁用，保存失败则保留原注册（旧键继续可用）。
            string? previous = binding.Configured;
            binding.Configured = null;
            try
            {
                PersistLocked();
            }
            catch
            {
                binding.Configured = previous;
                throw;
            }

            ReleaseLocked(binding);
            binding.Error = null;
            error = null;
            return true;
        }
        catch (JsonException)
        {
            binding.Error = CorruptConfigMessage;
            error = CorruptConfigMessage;
            return false;
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            binding.Error = $"无法读取或写入配置文件，快捷键未更改：{io.Message}";
            error = binding.Error;
            return false;
        }
    }

    private void AdoptLocked(
        string actionId,
        ActionBinding binding,
        string combo,
        IDisposable registration,
        int id)
    {
        ReleaseLocked(binding);
        binding.Configured = combo;
        binding.Registration = registration;
        binding.RegisteredId = id;
        binding.Registered = combo;
        binding.Error = null;
        _idToAction[id] = actionId;
    }

    private void ReleaseLocked(ActionBinding binding)
    {
        if (binding.Registration is { } registration)
        {
            if (binding.RegisteredId != 0)
            {
                _idToAction.Remove(binding.RegisteredId);
            }

            registration.Dispose();
        }

        binding.Registration = null;
        binding.RegisteredId = 0;
        binding.Registered = null;
    }

    /// <summary>
    /// 从配置读取各动作绑定：hotkeys.actions 优先（空串表示显式禁用，缺失
    /// 条目回退默认）；节点缺失时旧 global_screenshot 迁移到
    /// screenshot_recognize，语义不变。整体解析成功才一次性提交，失败返回
    /// 错误且保持未加载（下次重试），不产生半读取状态，也不改写坏文件。
    /// </summary>
    private string? EnsureCatalogLoadedLocked()
    {
        if (_catalogLoaded)
        {
            return null;
        }

        Dictionary<string, string?> loaded = [];
        foreach (string actionId in HotkeyActionCatalog.Actions)
        {
            loaded[actionId] = HotkeyActionCatalog.DefaultBinding(actionId);
        }

        try
        {
            if (File.Exists(_layout.ConfigFile))
            {
                JsonObject root = AppSettingsStore.ReadForUpdate(_layout);
                if (root["hotkeys"] is JsonObject hotkeys)
                {
                    JsonObject? actions = hotkeys[HotkeyActionCatalog.ActionsFieldName] as JsonObject;
                    foreach (string actionId in HotkeyActionCatalog.Actions)
                    {
                        JsonNode? entry = actions?[actionId];
                        if (entry is null
                            && actionId == HotkeyActionCatalog.ScreenshotRecognize)
                        {
                            entry = hotkeys[HotkeyActionCatalog.LegacyGlobalScreenshotField];
                        }

                        if (entry is not null)
                        {
                            string text = entry.GetValue<string>();
                            loaded[actionId] =
                                string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                        }
                    }
                }
            }
        }
        catch (Exception error) when (
            error is JsonException or InvalidOperationException
                or InvalidCastException or KeyNotFoundException)
        {
            return "配置文件已损坏，无法读取快捷键设置；原文件已保留";
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            return $"无法读取配置文件，快捷键未更改：{io.Message}";
        }

        foreach (string actionId in HotkeyActionCatalog.Actions)
        {
            if (_bindings.TryGetValue(actionId, out ActionBinding? existing))
            {
                existing.Configured = loaded[actionId];
            }
            else
            {
                _bindings[actionId] = new ActionBinding
                {
                    Configured = loaded[actionId],
                };
            }
        }

        _catalogLoaded = true;
        return null;
    }

    /// <summary>
    /// 持久化 hotkeys 节点：写入全部已知动作（未绑定存空串），保留未知动作
    /// 键与其他字段；global_screenshot 与 screenshot_recognize 同步。
    /// </summary>
    private void PersistLocked()
    {
        JsonObject root = AppSettingsStore.ReadForUpdate(_layout);
        JsonObject hotkeys = root["hotkeys"] as JsonObject ?? [];
        JsonObject actions = hotkeys[HotkeyActionCatalog.ActionsFieldName] as JsonObject ?? [];
        foreach (string actionId in HotkeyActionCatalog.Actions)
        {
            actions[actionId] = _bindings[actionId].Configured ?? "";
        }

        hotkeys[HotkeyActionCatalog.ActionsFieldName] = actions;
        hotkeys[HotkeyActionCatalog.LegacyGlobalScreenshotField] =
            _bindings[HotkeyActionCatalog.ScreenshotRecognize].Configured ?? "";
        root["hotkeys"] = hotkeys;
        AppSettingsStore.Write(_layout, root);
    }

    private static (HotkeyModifiers Modifiers, uint VirtualKey) Parse(string hotkey)
    {
        if (!TryParseCombo(hotkey, out HotkeyModifiers modifiers, out uint virtualKey, out string? error))
        {
            throw new ArgumentException(error, nameof(hotkey));
        }

        return (modifiers, virtualKey);
    }

    /// <summary>
    /// 解析为真实 native 组合：主键仅接受 ASCII A-Z/0-9 与 F1-F24（非 ASCII
    /// 文字不是有效虚拟键码）；仅 Shift 加文字/数字会抢占正常键盘输入，
    /// 禁止注册。比较键位是否相同以解析结果为准，与书写次序/大小写/
    /// CONTROL 别名无关。
    /// </summary>
    private static bool TryParseCombo(
        string hotkey,
        out HotkeyModifiers modifiers,
        out uint virtualKey,
        out string? error)
    {
        modifiers = HotkeyModifiers.None;
        virtualKey = 0;
        error = null;
        string[] tokens = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            error = "快捷键必须包含修饰键和主键。";
            return false;
        }

        HotkeyModifiers parsed = HotkeyModifiers.None;
        foreach (string token in tokens[..^1])
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    parsed |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    parsed |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    parsed |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS":
                    parsed |= HotkeyModifiers.Windows;
                    break;
                default:
                    error = $"不支持的快捷键修饰符：{token}";
                    return false;
            }
        }

        string key = tokens[^1].ToUpperInvariant();
        bool isTextKey = false;
        if (key.Length == 1)
        {
            char character = key[0];
            if (character is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                virtualKey = character;
                isTextKey = true;
            }
            else
            {
                error = $"不支持的快捷键主键：{key}";
                return false;
            }
        }
        else if (key.StartsWith('F') && int.TryParse(key[1..], out int number) && number is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + number - 1);
        }
        else
        {
            error = $"不支持的快捷键主键：{key}";
            return false;
        }

        if (isTextKey && (parsed & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows))
            == HotkeyModifiers.None)
        {
            error = "仅 Shift 加文字/数字键会抢占正常键盘输入，请搭配 Ctrl、Alt 或 Win。";
            return false;
        }

        modifiers = parsed;
        return true;
    }
}

internal sealed class WindowsStartupRegistrar(string bootstrapperPath) : IStartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VibeOCR";
    private readonly string _command = $"\"{bootstrapperPath}\" --profile production";

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, _command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
