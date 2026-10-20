import {
  exclusionOutputRects,
  finalOutputSize,
  rectsCoverArea,
  type CanvasSize,
  type EditorState,
  type Rect,
} from "./annotationGeometry";
import { markBoundsOf } from "./annotationObjects";
import { compositionLayout, type CompositionLayout } from "./canvasComposition";

function snapClipped(rect: Rect, size: CanvasSize): Rect | undefined {
  const x = Math.max(0, Math.min(size.width, Math.floor(rect.x))),
    y = Math.max(0, Math.min(size.height, Math.floor(rect.y)));
  const right = Math.max(
      x,
      Math.min(size.width, Math.ceil(rect.x + rect.width)),
    ),
    bottom = Math.max(
      y,
      Math.min(size.height, Math.ceil(rect.y + rect.height)),
    );
  return right > x && bottom > y
    ? { x, y, width: right - x, height: bottom - y }
    : undefined;
}
export function normalizeOutputRects(
  rects: readonly Rect[],
  size: CanvasSize,
): readonly Rect[] {
  return rects.map((rect) => {
    const x = Math.max(0, Math.min(1000, (rect.x / size.width) * 1000)),
      y = Math.max(0, Math.min(1000, (rect.y / size.height) * 1000));
    const right = Math.max(
        x,
        Math.min(1000, ((rect.x + rect.width) / size.width) * 1000),
      ),
      bottom = Math.max(
        y,
        Math.min(1000, ((rect.y + rect.height) / size.height) * 1000),
      );
    return {
      x,
      y,
      width: Math.min(1000 - x, right - x),
      height: Math.min(1000 - y, bottom - y),
    };
  });
}

export function compositionExclusions(
  image: HTMLImageElement,
  state: EditorState,
  display: CanvasSize,
  assets?: ReadonlyMap<string, HTMLImageElement>,
) {
  const baseSize = finalOutputSize(image, state, display);
  const settings = state.composition;
  // 无排版沿用普通图片的 32768px / 100MP 合同，不额外套排版画布预算。
  const layout: CompositionLayout = settings
    ? compositionLayout(baseSize, settings, assets, state.marks)
    : {
        size: baseSize,
        baseSize,
        cut: undefined,
        placements: [{ x: 0, y: 0, ...baseSize }],
      };
  const origin = layout.placements[0]!;
  const project = (value: number, axis: "horizontal" | "vertical") => {
    const cut = layout.cut;
    if (!cut || cut.axis !== axis) return value;
    return value <= cut.from
      ? value
      : value >= cut.to
        ? value - (cut.to - cut.from)
        : cut.from;
  };
  const mapped: Rect[] = exclusionOutputRects(image, state, display)
    .map((rect) => {
      const x = project(rect.x, "vertical"),
        y = project(rect.y, "horizontal");
      return {
        x: x + origin.x,
        y: y + origin.y,
        width: project(rect.x + rect.width, "vertical") - x,
        height: project(rect.y + rect.height, "horizontal") - y,
      };
    })
    .filter((rect) => rect.width > 0 && rect.height > 0);
  // shortcut: 复用资产与屏蔽区相交时整幅排除；需要部分资产取字时升级为局部像素逆投影。
  const maskedAssets = new Set<string>();
  const masks = state.marks
    .filter((mark) => mark.tool === "exclude")
    .map((mark) => ({
      x: Math.min(mark.start.x, mark.end.x),
      y: Math.min(mark.start.y, mark.end.y),
      width: Math.abs(mark.end.x - mark.start.x),
      height: Math.abs(mark.end.y - mark.start.y),
    }));
  for (const mark of state.marks) {
    if (mark.tool !== "image" || !mark.assetId) continue;
    const rect = markBoundsOf(mark, undefined);
    if (
      masks.some(
        (mask) =>
          mask.width > 0 &&
          mask.height > 0 &&
          rect.x < mask.x + mask.width &&
          rect.x + rect.width > mask.x &&
          rect.y < mask.y + mask.height &&
          rect.y + rect.height > mask.y,
      )
    )
      maskedAssets.add(mark.assetId);
  }
  if (
    settings?.background === "image" &&
    settings.backgroundAssetId &&
    maskedAssets.has(settings.backgroundAssetId)
  )
    mapped.push({ x: 0, y: 0, ...layout.size });
  for (const item of layout.placements)
    if (item.assetId && maskedAssets.has(item.assetId)) mapped.push(item);
  const pixels = mapped
    .map((rect) => snapClipped(rect, layout.size))
    .filter((rect): rect is Rect => rect !== undefined);
  const unique = pixels.filter(
    (rect, index) =>
      pixels.findIndex(
        (other) =>
          other.x === rect.x &&
          other.y === rect.y &&
          other.width === rect.width &&
          other.height === rect.height,
      ) === index,
  );
  if (unique.length > 64)
    throw new Error(
      "排版后的屏蔽区超过 64 个，请减少屏蔽区、放大镜或复用图片后重试；本次未上传。",
    );
  return {
    size: layout.size,
    pixels: unique,
    normalized: normalizeOutputRects(unique, layout.size),
    fullyMasked: rectsCoverArea(unique, { x: 0, y: 0, ...layout.size }),
  };
}
