import { compositionLayout } from "./canvasComposition";
import {
  ANNOTATION_HANDLE_HIT_RADIUS,
  finalOutputSize,
  cropAdjustBase,
  mappedCropRect,
  outputSize,
  projectPoint,
  type CanvasSize,
  type EditorState,
} from "./annotationGeometry";
import { markBoundsOf } from "./annotationObjects";
import type { ImageTextBox } from "./imageTextLayerGeometry";

/** 最终 PNG 行框逆投影到编辑画布；标注与裁剪手柄优先接收指针。 */
export function editorTextBox(
  bbox: readonly [number, number, number, number],
  image: HTMLImageElement,
  state: EditorState,
  display: CanvasSize,
  viewport: CanvasSize,
  assets?: ReadonlyMap<string, HTMLImageElement>,
  context?: CanvasRenderingContext2D,
  scene = false,
): ImageTextBox | undefined {
  const base = finalOutputSize(image, state, display);
  const layout = state.composition
    ? compositionLayout(base, state.composition, assets, state.marks)
    : undefined;
  const size = layout?.size ?? base;
  const origin = layout?.placements[0] ?? { x: 0, y: 0, ...base };
  const x1 = (bbox[0] * size.width) / 1000 - origin.x;
  const y1 = (bbox[1] * size.height) / 1000 - origin.y;
  const x2 = (bbox[2] * size.width) / 1000 - origin.x;
  const y2 = (bbox[3] * size.height) / 1000 - origin.y;
  // 副本/背景文字在完整排版预览中选择；主画布只投影有对应编辑坐标的原图。
  if (x1 < 0 || y1 < 0 || x2 > origin.width || y2 > origin.height)
    return undefined;
  const cut = layout?.cut;
  const first = cut?.axis === "vertical" ? x1 : y1;
  const last = cut?.axis === "vertical" ? x2 : y2;
  if (cut && first < cut.from && last > cut.from) return undefined;
  const undoCut = (value: number, axis: "horizontal" | "vertical") =>
    cut?.axis === axis && value >= cut.from ? value + cut.to - cut.from : value;
  const crop = mappedCropRect(image, state, display);
  const natural = outputSize(image, state.rotation);
  const project = (x: number, y: number) =>
    projectPoint(
      {
        x: crop.x + (undoCut(x, "vertical") * crop.width) / base.width,
        y: crop.y + (undoCut(y, "horizontal") * crop.height) / base.height,
      },
      image,
      state.rotation,
      natural,
      state.rotation,
      display,
    );
  const a = project(x1, y1),
    b = project(x2, y2);
  const box = { x1: a.x, y1: a.y, x2: b.x, y2: b.y };
  if (
    state.marks.some((mark) => {
      if (mark.locked || mark.tool === "exclude") return false;
      const bounds = markBoundsOf(mark, context);
      // 手柄半径包含边界；仅贴到拖动区也必须让对象优先。
      return (
        box.x2 >= bounds.x - ANNOTATION_HANDLE_HIT_RADIUS &&
        box.x1 <= bounds.x + bounds.width + ANNOTATION_HANDLE_HIT_RADIUS &&
        box.y2 >= bounds.y - ANNOTATION_HANDLE_HIT_RADIUS &&
        box.y1 <= bounds.y + bounds.height + ANNOTATION_HANDLE_HIT_RADIUS
      );
    })
  )
    return undefined;
  // 与真实手柄命中半径共用边界，选字不能覆盖裁剪拖动区。
  const cropBase = cropAdjustBase(state, display, scene);
  if (
    cropBase &&
    [
      a.x - Math.min(cropBase.start.x, cropBase.end.x),
      a.y - Math.min(cropBase.start.y, cropBase.end.y),
      Math.max(cropBase.start.x, cropBase.end.x) - b.x,
      Math.max(cropBase.start.y, cropBase.end.y) - b.y,
    ].some((gap) => gap <= ANNOTATION_HANDLE_HIT_RADIUS)
  )
    return undefined;
  return {
    left: (a.x * viewport.width) / display.width,
    top: (a.y * viewport.height) / display.height,
    width: ((b.x - a.x) * viewport.width) / display.width,
    height: ((b.y - a.y) * viewport.height) / display.height,
  };
}
