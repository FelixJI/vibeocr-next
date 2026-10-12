using Microsoft.UI.Xaml;
using VibeOCR.App.Workbench;

namespace VibeOCR.App;

/// <summary>工作台主题真值 → 元素主题解析。优先级与浮栏一致：高对比激活
/// 时一律 Default（使用系统可见资源），System 跟随系统，Light/Dark 固定。
/// 主窗与活动编辑窗根共用同一解析，不引入第二主题真值。</summary>
internal static class WorkbenchThemePresenter
{
  internal static ElementTheme ResolveRequestedTheme(WorkbenchTheme theme, bool highContrast) =>
    highContrast || theme == WorkbenchTheme.System
      ? ElementTheme.Default
      : theme == WorkbenchTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
}
