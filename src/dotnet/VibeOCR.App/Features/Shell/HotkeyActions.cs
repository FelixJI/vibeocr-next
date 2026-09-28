namespace VibeOCR.App.Features.Shell;

/// <summary>
/// 全局快捷键动作目录：热键、悬浮栏、托盘与主窗口共用应用级动作稳定 ID，
/// WM_HOTKEY 按实际注册 ID 解析回动作后统一分派。旧配置字段
/// global_screenshot 的语义原样迁移到 screenshot_recognize（默认
/// Ctrl+Alt+Q），其余动作默认不绑定。
/// </summary>
internal static class HotkeyActionCatalog
{
    public const string ScreenshotEdit = "screenshot_edit";
    public const string ScreenshotRecognize = "screenshot_recognize";
    public const string ClipboardRecognize = "clipboard_recognize";
    public const string ToggleToolbar = "toggle_toolbar";
    public const string ShowWorkbench = "show_workbench";

    /// <summary>hotkeys 节点中的旧版单动作字段名，保持同步以便旧版本回滚。</summary>
    public const string LegacyGlobalScreenshotField = "global_screenshot";

    /// <summary>配置中的动作节点名：hotkeys.actions。</summary>
    public const string ActionsFieldName = "actions";

    /// <summary>设置界面的稳定展示顺序。</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        ScreenshotEdit,
        ScreenshotRecognize,
        ClipboardRecognize,
        ToggleToolbar,
        ShowWorkbench,
    ];

    private static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ScreenshotEdit] = "截图编辑",
            [ScreenshotRecognize] = "快捷截图识别",
            [ClipboardRecognize] = "剪贴板识别",
            [ToggleToolbar] = "悬浮栏显示/隐藏",
            [ShowWorkbench] = "打开工作台",
        };

    public static bool IsKnown(string actionId) =>
        DisplayNames.ContainsKey(actionId);

    public static string DisplayName(string actionId) =>
        DisplayNames.TryGetValue(actionId, out string? name)
            ? name
            : throw new ArgumentException($"未知快捷键动作：{actionId}", nameof(actionId));

    /// <summary>
    /// 动作默认键位：仅 screenshot_recognize 携带旧默认 Ctrl+Alt+Q，
    /// 其余动作默认未绑定（null）。
    /// </summary>
    public static string? DefaultBinding(string actionId) =>
        actionId == ScreenshotRecognize ? "Ctrl+Alt+Q" : null;
}

/// <summary>
/// 单个动作的配置与实际注册状态快照：RegisteredHotkey 为空表示当前没有
/// 真实 WM_HOTKEY 注册（配置与实际可能因键位被占用而不同）。
/// </summary>
internal sealed record HotkeyActionStatus(
    string ActionId,
    string DisplayName,
    string? ConfiguredHotkey,
    string? RegisteredHotkey,
    string? Error);
