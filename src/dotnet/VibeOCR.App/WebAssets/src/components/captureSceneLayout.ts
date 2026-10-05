/** 仅截图覆盖层宿主注入，数值均为相对冻结虚拟桌面的物理像素。 */
export interface CaptureSceneGeometry {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly desktopWidth: number;
  readonly desktopHeight: number;
}

export function captureSceneStyle(
  geometry: CaptureSceneGeometry | undefined,
  viewportWidth: number,
  viewportHeight: number,
): Record<string, string> | undefined {
  if (!geometry) return undefined;
  const values = Object.values(geometry);
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
  const toolHeight = Math.min(240, viewportHeight);
  const below = y + height + 8;
  const toolTop =
    below + toolHeight <= viewportHeight
      ? below
      : y >= toolHeight + 8
        ? y - toolHeight - 8
        : Math.max(0, viewportHeight - toolHeight);
  return {
    "--capture-x": `${x}px`,
    "--capture-y": `${y}px`,
    "--capture-width": `${width}px`,
    "--capture-height": `${height}px`,
    "--capture-tools-left": `${Math.min(x, Math.max(0, viewportWidth - 880))}px`,
    "--capture-tools-top": `${toolTop}px`,
    "--capture-tools-height": `${toolHeight}px`,
  };
}

export function nativeCaptureScene(): CaptureSceneGeometry | undefined {
  return (window as Window & { vibeocrCaptureScene?: CaptureSceneGeometry })
    .vibeocrCaptureScene;
}
