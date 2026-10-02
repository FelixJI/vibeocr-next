using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.App.Services;

namespace VibeOCR.App.Features.Shell;

/// <summary>
/// 应用级统一动作分派：WM_HOTKEY、托盘菜单、悬浮工具栏与主窗入口共用
/// <see cref="HotkeyActionCatalog"/> 的动作 ID。处理器只做导航与既有工作台
/// 命令的编排；识别/截图业务与重入保护由工作台命令层负责——截图入口的
/// 单飞 guard、会话代次取代都在 <c>DesktopWorkbenchCommandHandler</c> 中，
/// 本类不复制也不声称提供这些保护。该具体对象同时承载设置页“快捷操作”
/// 分区所需的动作键位与悬浮工具栏状态/变更入口：登记器与工具栏由宿主经
/// 委托注入，工作台命令处理器直接依赖本对象。
/// </summary>
internal sealed class ShellActionDispatcher
{
    private readonly Func<WindowsHotkeyRegistrar?> _registrar;
    private Guid? _recordingId;
    private readonly IReadOnlyDictionary<string, Func<Task>> _handlers;
    private readonly Func<FloatingToolbarSettings> _toolbarSettings;
    private readonly Func<FloatingToolbarVisibility> _toolbarVisibility;
    private readonly Func<FloatingToolbarSettings, string?> _applyToolbar;
    private readonly Func<string?> _showToolbar;
    private readonly Func<string?> _hideToolbar;
    private readonly Func<IDisposable?> _suspendToolbarForCapture;

    /// <summary>托盘/热键切换结束后同步实际配置与可见性，失败时同样保旧刷新。</summary>
    public event Action? ToolbarStateChanged;

    public ShellActionDispatcher(
        Func<WindowsHotkeyRegistrar?> registrar,
        IReadOnlyDictionary<string, Func<Task>> handlers,
        Func<FloatingToolbarSettings> toolbarSettings,
        Func<FloatingToolbarVisibility> toolbarVisibility,
        Func<FloatingToolbarSettings, string?> applyToolbar,
        Func<string?> showToolbar,
        Func<string?> hideToolbar,
        Func<IDisposable?> suspendToolbarForCapture)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _toolbarSettings = toolbarSettings ?? throw new ArgumentNullException(nameof(toolbarSettings));
        _toolbarVisibility = toolbarVisibility ?? throw new ArgumentNullException(nameof(toolbarVisibility));
        _applyToolbar = applyToolbar ?? throw new ArgumentNullException(nameof(applyToolbar));
        _showToolbar = showToolbar ?? throw new ArgumentNullException(nameof(showToolbar));
        _hideToolbar = hideToolbar ?? throw new ArgumentNullException(nameof(hideToolbar));
        _suspendToolbarForCapture = suspendToolbarForCapture ??
            throw new ArgumentNullException(nameof(suspendToolbarForCapture));
    }

    /// <summary>
    /// 按动作 ID 分派；未知动作或未接线处理器返回 false。执行为
    /// fire-and-forget：处理器自身的异常已按现有入口约定收敛并记录。
    /// </summary>
    public bool TryDispatch(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        if (!HotkeyActionCatalog.IsKnown(actionId) ||
            !_handlers.TryGetValue(actionId, out Func<Task>? handler))
        {
            return false;
        }

        RunQuietly(handler, actionId);
        return true;
    }

    public bool IsHotkeyRecording => _registrar()?.IsRecording == true;

    public void BeginHotkeyRecording(Guid recordingId)
    {
        WindowsHotkeyRegistrar registrar = _registrar() ??
            throw new InvalidOperationException("快捷键服务尚未就绪，请稍后重试。");
        registrar.BeginRecording();
        _recordingId = recordingId;
    }

    public void EndHotkeyRecording(Guid? recordingId = null)
    {
        // 旧页面/控件的迟到结束不能恢复新会话的注册。
        if (recordingId is not null && _recordingId != recordingId) return;
        _recordingId = null;
        _registrar()?.EndRecording();
    }

    /// <summary>全部动作的配置与实际注册状态；登记器未就绪时为空列表。</summary>
    public IReadOnlyList<HotkeyActionStatus> GetHotkeyActions() =>
        _registrar()?.GetActionStatuses() ?? [];

    /// <summary>
    /// 设置动作键位；hotkey 为 null/空白表示禁用。失败保留旧注册并返回
    /// 准确错误（同时记入该动作状态供设置页展示）。
    /// </summary>
    public bool TrySetHotkeyAction(string actionId, string? hotkey, out string? error)
    {
        WindowsHotkeyRegistrar? registrar = _registrar();
        if (registrar is null)
        {
            error = "快捷键服务尚未就绪，请稍后重试。";
            return false;
        }

        EndHotkeyRecording();
        return registrar.SetActionHotkey(actionId, hotkey, out error);
    }

    /// <summary>恢复动作默认键位（仅快捷截图识别有默认绑定）。</summary>
    public bool ResetHotkeyAction(string actionId, out string? error)
    {
        WindowsHotkeyRegistrar? registrar = _registrar();
        if (registrar is null)
        {
            error = "快捷键服务尚未就绪，请稍后重试。";
            return false;
        }

        EndHotkeyRecording();
        return registrar.ResetActionToDefault(actionId, out error);
    }

    /// <summary>当前生效的悬浮工具栏设置（含主动隐藏偏好）。</summary>
    public FloatingToolbarSettings ToolbarSettings => _toolbarSettings();

    /// <summary>悬浮工具栏当前可见档位（以实际运行实例为准）。</summary>
    public FloatingToolbarVisibility ToolbarVisibility => _toolbarVisibility();

    /// <summary>
    /// 实时应用并持久化悬浮工具栏设置。保存失败保留原状态并返回错误；
    /// 启用但工具栏实例未能创建时同样返回错误，不把“已启用”当作实际运行
    /// 成功。
    /// </summary>
    public bool TryApplyFloatingToolbar(FloatingToolbarSettings settings, out string? error)
    {
        error = _applyToolbar(settings);
        return error is null;
    }

    /// <summary>显式显示悬浮工具栏；已关闭等不可用情形返回错误。</summary>
    public bool TryShowFloatingToolbar(out string? error)
    {
        error = _showToolbar();
        return error is null;
    }

    /// <summary>用户主动隐藏悬浮工具栏；未运行时返回错误。</summary>
    public bool TryHideFloatingToolbar(out string? error)
    {
        error = _hideToolbar();
        return error is null;
    }

    /// <summary>
    /// 截图前的临时让位：隐藏工具栏与感应条，返回句柄释放时恢复原态；
    /// 当前无需让位（已关闭/已在避让）时返回 null。
    /// </summary>
    public IDisposable? SuspendFloatingToolbarForCapture() => _suspendToolbarForCapture();

    private async void RunQuietly(Func<Task> handler, string actionId)
    {
        try
        {
            await handler();
        }
        catch (Exception error) when (
            error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Shell action '{actionId}' failed: {error.Message}");
        }
        catch (Exception error)
        {
            AppLog.Error($"Shell action '{actionId}' crashed", error);
        }
        finally
        {
            if (actionId == HotkeyActionCatalog.ToggleToolbar)
                ToolbarStateChanged?.Invoke();
        }
    }
}
