using System.Runtime.InteropServices;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Shell;

/// <summary>模式菜单的一项：ActionId 关联 HotkeyActionCatalog；MonitorDevice
/// 仅显示器项携带；OptionsItem 表示“截图选项…”入口。</summary>
internal sealed record ScreenshotModeMenuItem(
    string Label,
    string? ActionId = null,
    string? MonitorDevice = null,
    IReadOnlyList<ScreenshotModeMenuItem>? Children = null)
{
  internal bool IsOptions =>
      ActionId is null && MonitorDevice is null && Children is null && Label != "-";
}

/// <summary>
/// #236 统一原生截图模式菜单：托盘、悬浮栏右键与 screenshot_menu 热键共用。
/// 纯构造函数 <see cref="BuildItems"/> 可测；Win32 呈现复用托盘 owner 窗口
/// 的 TrackPopupMenu 模式（隐藏宿主 + SetForeground + 结束 WM_NULL）。
/// </summary>
internal static class ScreenshotModeMenu
{
  private const uint MenuFlagString = 0;
  private const uint MenuFlagPopup = 0x0010;
  private const uint MenuFlagSeparator = 0x0800;

  /// <summary>
  /// 按当前显示器构造菜单项：多显示器时“显示器截图”展开为子菜单，单
  /// 显示器直接定位该显示器；始终提供“截图选项…”入口。
  /// </summary>
  public static IReadOnlyList<ScreenshotModeMenuItem> BuildItems(
      IReadOnlyList<ScreenMonitorTarget> monitors)
  {
    var items = new List<ScreenshotModeMenuItem>
        {
            new("全屏截图", HotkeyActionCatalog.ScreenshotFullscreen),
            new("活动窗口截图", HotkeyActionCatalog.ScreenshotWindow),
        };
    if (monitors.Count <= 1)
    {
      items.Add(new ScreenshotModeMenuItem(
          "显示器截图", HotkeyActionCatalog.ScreenshotMonitor, MonitorDevice: monitors.FirstOrDefault()?.DeviceName ?? string.Empty));
    }
    else
    {
      var children = monitors.Select((monitor, index) => new ScreenshotModeMenuItem(
          $"显示器 {index + 1}（{monitor.Bounds.Width}×{monitor.Bounds.Height}" +
          (monitor.IsPrimary ? "，主）" : "）"),
          HotkeyActionCatalog.ScreenshotMonitor,
          MonitorDevice: monitor.DeviceName)).ToArray();
      items.Add(new ScreenshotModeMenuItem("显示器截图", Children: children));
    }

    items.Add(new ScreenshotModeMenuItem("固定区域截图", HotkeyActionCatalog.ScreenshotFixed));
    items.Add(new ScreenshotModeMenuItem("重复上次区域", HotkeyActionCatalog.ScreenshotRepeat));
    items.Add(new ScreenshotModeMenuItem("延时截图", HotkeyActionCatalog.ScreenshotDelayed));
    items.Add(new ScreenshotModeMenuItem("-"));
    items.Add(new ScreenshotModeMenuItem("截图选项…"));
    return items;
  }

  /// <summary>
  /// 在屏幕坐标 显示菜单；返回 null 表示取消。ownerTrackContextMenu 由
  /// 宿主注入（TrayMenuOwnerWindow.TrackContextMenu）。
  /// </summary>
  public static ScreenshotModeMenuItem? Show(
      IReadOnlyList<ScreenshotModeMenuItem> items,
      Func<nint, int, int, int> ownerTrackContextMenu,
      int x,
      int y)
  {
    ArgumentNullException.ThrowIfNull(items);
    ArgumentNullException.ThrowIfNull(ownerTrackContextMenu);
    nint menu = CreatePopupMenu();
    if (menu == 0)
    {
      return null;
    }

    try
    {
      var flat = new List<ScreenshotModeMenuItem>();
      if (!TryAppend(menu, items, flat))
      {
        return null;
      }

      int selected = ownerTrackContextMenu(menu, x, y);
      return selected is > 0 && selected <= flat.Count ? flat[selected - 1] : null;
    }
    finally
    {
      DestroyMenu(menu);
    }
  }

  private static bool TryAppend(
      nint menu, IReadOnlyList<ScreenshotModeMenuItem> items, List<ScreenshotModeMenuItem> flat)
  {
    foreach (ScreenshotModeMenuItem item in items)
    {
      if (item.Label == "-")
      {
        if (!AppendMenuW(menu, MenuFlagSeparator, 0, null)) return false;
        continue;
      }

      if (item.Children is { Count: > 0 } children)
      {
        nint submenu = CreatePopupMenu();
        if (submenu == 0 ||
            !AppendMenuW(menu, MenuFlagPopup | MenuFlagString, (nuint)submenu, item.Label))
        {
          return false;
        }

        if (!TryAppend(submenu, children, flat))
        {
          return false;
        }

        continue;
      }

      flat.Add(item);
      if (!AppendMenuW(menu, MenuFlagString, (nuint)flat.Count, item.Label))
      {
        return false;
      }
    }

    return true;
  }

  [DllImport("user32.dll")]
  private static extern nint CreatePopupMenu();

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DestroyMenu(nint menu);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AppendMenuW(
      nint menu, uint flags, nuint newMenuItem, string? label);
}
