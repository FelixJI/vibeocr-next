/** 仅截图覆盖层的 typed bootstrap 提供，数值均为相对冻结虚拟桌面的物理像素。 */
export interface CaptureSceneGeometry {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly desktopWidth: number;
  readonly desktopHeight: number;
  readonly initialTool?: string;
}

/** 现场浮动工具的宽度预算（DIP）：工具栏左缘钳制的唯一事实源。 */
const TOOLS_WIDTH_BUDGET = 880;
/** 输出设置面板的翻转决策预算（换行后约 3 行控件的估算高度）。 */
const SETTINGS_PANEL_BUDGET = 132;

export function captureSceneStyle(
  geometry: CaptureSceneGeometry | undefined,
  viewportWidth: number,
  viewportHeight: number,
): Record<string, string> | undefined {
  if (!geometry) return undefined;
  const values = [
    geometry.x,
    geometry.y,
    geometry.width,
    geometry.height,
    geometry.desktopWidth,
    geometry.desktopHeight,
  ];
  if (
    values.length !== 6 ||
    !values.every(Number.isFinite) ||
    geometry.x < 0 ||
    geometry.y < 0 ||
    geometry.width <= 0 ||
    geometry.height <= 0 ||
    geometry.desktopWidth <= 0 ||
    geometry.desktopHeight <= 0 ||
    geometry.x + geometry.width > geometry.desktopWidth ||
    geometry.y + geometry.height > geometry.desktopHeight ||
    viewportWidth <= 0 ||
    viewportHeight <= 0
  )
    return undefined;
  const sx = viewportWidth / geometry.desktopWidth;
  const sy = viewportHeight / geometry.desktopHeight;
  const x = geometry.x * sx,
    y = geometry.y * sy;
  const width = geometry.width * sx,
    height = geometry.height * sy;
  // 两排工具栏（主操作一排 + 样式一排）的最大高度预算：两行小尺寸
  // 控件加内边距约 84px，64px 单排时代的预算会触发内部滚动。窄视口
  // （<900px）时两排各自换行、行数增多，放宽到 144px 避免主操作排被
  // 卷进折叠区；仍由宿主滚动容器兑底。
  const toolHeight = Math.min(viewportWidth < 900 ? 144 : 96, viewportHeight);
  const below = y + height + 8;
  const toolTop =
    below + toolHeight <= viewportHeight
      ? below
      : y >= toolHeight + 8
        ? y - toolHeight - 8
        : Math.max(0, viewportHeight - toolHeight);
  // 输出设置下拉：优先在覆盖层下方；选区几乎全屏、工具栏钉在视口底部时
  // 下方放不下，翻转到覆盖层上方；实际高度由 CSS max-height+滚动兑底。
  const settingsBelowTop = toolTop + toolHeight + 4;
  const settingsBelowFits =
    settingsBelowTop + SETTINGS_PANEL_BUDGET <= viewportHeight;
  const settingsTop = settingsBelowFits
    ? settingsBelowTop
    : Math.max(4, toolTop - 4 - SETTINGS_PANEL_BUDGET);
  const settingsMaxHeight = Math.max(
    48,
    settingsBelowFits
      ? viewportHeight - settingsTop - 8
      : toolTop - 4 - settingsTop,
  );
  return {
    "--capture-x": `${x}px`,
    "--capture-y": `${y}px`,
    "--capture-width": `${width}px`,
    "--capture-height": `${height}px`,
    "--capture-tools-left": `${Math.min(x, Math.max(0, viewportWidth - TOOLS_WIDTH_BUDGET))}px`,
    "--capture-tools-top": `${toolTop}px`,
    "--capture-tools-height": `${toolHeight}px`,
    "--capture-settings-top": `${settingsTop}px`,
    "--capture-settings-max-height": `${settingsMaxHeight}px`,
  };
}
