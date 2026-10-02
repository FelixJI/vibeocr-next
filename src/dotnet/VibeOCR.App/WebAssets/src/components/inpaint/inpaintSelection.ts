/**
 * 去水印（Beta）选区几何：显示空间拖拽矩形 ↔ 原图像素空间整数矩形。
 *
 * 编辑器画布上的拖拽坐标经 annotationGeometry.projectPoint 映射到
 * 未旋转的原图空间；映射后外扩取整（floor/ceil）保证拖拽覆盖的每个
 * 像素都在有效修补范围内，选区高亮按同一映射回投显示，用户看到的
 * 就是实际会被修改的范围（每边至多多出 1px）。
 *
 * 这里的预算预检与 inpaint.ts 的硬校验同源：UI 先给出即时提示，
 * Worker 侧仍然是权威拒绝点。
 */

import {
  projectPoint,
  type CanvasSize,
  type Point,
} from "../annotationGeometry";
import {
  INPAINT_MAX_IMAGE_PIXELS,
  INPAINT_MAX_MASK_PIXELS,
  type InpaintFailureCode,
  type InpaintRect,
} from "./inpaint";

/** 传入对象的 naturalWidth/Height 即可；单测无需真实解码。 */
export interface NaturalImageSize {
  readonly naturalWidth: number;
  readonly naturalHeight: number;
}

/** 显示空间拖拽矩形（任意方向角点）映射为原图空间整数修补选区。 */
export function selectionToNaturalRect(
  start: Point,
  end: Point,
  image: NaturalImageSize,
  rotation: number,
  displaySize: CanvasSize,
): InpaintRect {
  const naturalSize: CanvasSize = {
    width: image.naturalWidth,
    height: image.naturalHeight,
  };
  const map = (point: Point) =>
    projectPoint(
      point,
      image as HTMLImageElement,
      rotation,
      displaySize,
      0,
      naturalSize,
    );
  const a = map(start);
  const b = map(end);
  const left = Math.min(a.x, b.x);
  const top = Math.min(a.y, b.y);
  const right = Math.max(a.x, b.x);
  const bottom = Math.max(a.y, b.y);
  // 外扩取整：拖拽触及的像素全部纳入；再收敛到图像内。
  const x = Math.max(0, Math.floor(left));
  const y = Math.max(0, Math.floor(top));
  const x1 = Math.min(image.naturalWidth, Math.ceil(right));
  const y1 = Math.min(image.naturalHeight, Math.ceil(bottom));
  return {
    x,
    y,
    width: Math.max(0, x1 - x),
    height: Math.max(0, y1 - y),
  };
}

/** 原图空间整数选区回投显示空间（角点形式），选区高亮与实际范围一致。 */
export function naturalRectToDisplay(
  rect: InpaintRect,
  image: NaturalImageSize,
  rotation: number,
  displaySize: CanvasSize,
): { start: Point; end: Point } {
  const naturalSize: CanvasSize = {
    width: image.naturalWidth,
    height: image.naturalHeight,
  };
  const map = (point: Point) =>
    projectPoint(
      point,
      image as HTMLImageElement,
      0,
      naturalSize,
      rotation,
      displaySize,
    );
  const a = map({ x: rect.x, y: rect.y });
  const b = map({ x: rect.x + rect.width, y: rect.y + rect.height });
  return { start: a, end: b };
}

export type SelectionPreflight =
  | { readonly ok: true; readonly rect: InpaintRect }
  | {
      readonly ok: false;
      readonly code: InpaintFailureCode;
      readonly message: string;
    };

/** 请求 Worker 前的本地预检：空选区、覆盖整图、超预算都直接给出用户提示。 */
export function preflightSelection(
  rect: InpaintRect,
  image: NaturalImageSize,
): SelectionPreflight {
  if (rect.width <= 0 || rect.height <= 0) {
    return {
      ok: false,
      code: "empty-mask",
      message: "选区不在图片内，请重新框选要去水印的区域。",
    };
  }
  const imagePixels = image.naturalWidth * image.naturalHeight;
  if (imagePixels > INPAINT_MAX_IMAGE_PIXELS) {
    return {
      ok: false,
      code: "budget-exceeded",
      message: `图片 ${image.naturalWidth}×${image.naturalHeight}（${(imagePixels / 1_000_000).toFixed(1)}MP）超过去水印支持的上限 8MP，请先缩小图片。`,
    };
  }
  const maskPixels = rect.width * rect.height;
  if (maskPixels > INPAINT_MAX_MASK_PIXELS) {
    return {
      ok: false,
      code: "budget-exceeded",
      message: `有效选区 ${(maskPixels / 1_000_000).toFixed(2)}MP 超过去水印单次上限 1MP（${Math.round(rect.width)}×${Math.round(rect.height)}），请分多次框选较小区域。`,
    };
  }
  if (
    rect.width === image.naturalWidth &&
    rect.height === image.naturalHeight
  ) {
    return {
      ok: false,
      code: "mask-covers-image",
      message: "选区覆盖整张图片，没有周边像素可参考；请缩小选区。",
    };
  }
  return { ok: true, rect };
}

/** Worker/预检失败码 → 用户可读提示；未知码不吞掉，统一归为计算失败。 */
export function describeInpaintFailure(
  code: InpaintFailureCode,
  detail?: string,
): string {
  switch (code) {
    case "invalid-image":
      return "图片尚未解码完成或已变化，无法修补；请等图片显示后重试。";
    case "invalid-rect":
      return "选区坐标无效，请重新框选。";
    case "empty-mask":
      return "选区不在图片内，请重新框选要去水印的区域。";
    case "mask-covers-image":
      return "选区覆盖整张图片，没有周边像素可参考；请缩小选区。";
    case "budget-exceeded":
      return detail
        ? `超出去水印预算：${detail}`
        : "图片或选区超出预算（整图≤8MP、单次选区≤1MP），请缩小后重试。";
    default:
      return detail
        ? `本地修补失败：${detail}当前图片未被修改。`
        : "本地修补失败，当前图片未被修改；请重试或调整选区。";
  }
}
