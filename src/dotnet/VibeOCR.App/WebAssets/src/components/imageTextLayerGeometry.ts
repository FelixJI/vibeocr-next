import {
  outputSize,
  projectPoint,
  type CanvasSize,
  type Point,
} from "./annotationGeometry";

/**
 * OCR 文本行：Runtime `OcrResult.text_blocks` 的行级子集。
 * 只有整行归一化框，没有字符级框——字符在行内的水平位置
 * 由浏览器排版近似，不得拆分均分宽度伪造字符 box。
 */
export interface ImageTextLine {
  readonly text: string;
  /** [0,1000] 归一化行框 (x1,y1,x2,y2)，参考系是识别提交的最终 PNG。 */
  readonly bbox: readonly [number, number, number, number];
  /** 引擎给出的阅读顺序（≥0 有效）；仅当全部行都有有效 order 才按 order 排。 */
  readonly order?: number;
}

/** 识别结果绑定的会话与内容修订（识别提交时所依据的画面状态）。 */
export interface ImageTextLineBinding {
  readonly sessionId: string;
  readonly revision: number;
}

/** 显示像素坐标系中的行框 AABB。 */
export interface ImageTextBox {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
}

const BBOX_LIMIT = 1000;

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/**
 * 校验宿主透传的 text_blocks 子集并按阅读顺序整理；无效条目整行丢弃。
 * bbox 必须是 [0,1000] 内的非退化行框（x1<x2、y1<y2），超范围或退化的
 * 框不得进入 DOM。阅读顺序：全部行都有有效 order 才按 order，否则统一
 * top→left（有/无 order 混合时按 order 排序不可传递，不采用）。
 */
export function toImageTextLines(value: unknown): readonly ImageTextLine[] {
  if (!Array.isArray(value)) return [];
  const lines: ImageTextLine[] = [];
  for (const entry of value) {
    if (entry === null || typeof entry !== "object") continue;
    const candidate = entry as {
      text?: unknown;
      bbox?: unknown;
      order?: unknown;
    };
    if (typeof candidate.text !== "string" || candidate.text.length === 0) {
      continue;
    }
    const raw = candidate.bbox;
    if (!Array.isArray(raw) || raw.length !== 4 || !raw.every(isFiniteNumber)) {
      continue;
    }
    const [x1, y1, x2, y2] = raw as [number, number, number, number];
    if (
      x1 < 0 ||
      y1 < 0 ||
      x2 > BBOX_LIMIT ||
      y2 > BBOX_LIMIT ||
      x1 >= x2 ||
      y1 >= y2
    ) {
      continue;
    }
    const order =
      isFiniteNumber(candidate.order) && candidate.order >= 0
        ? candidate.order
        : undefined;
    lines.push({
      text: candidate.text,
      bbox: [x1, y1, x2, y2],
      ...(order !== undefined ? { order } : {}),
    });
  }
  const byEngineOrder = lines.every((line) => line.order !== undefined);
  return lines.sort((a, b) => {
    if (byEngineOrder && a.order !== b.order) {
      return (a.order ?? 0) - (b.order ?? 0);
    }
    const byTop = a.bbox[1] - b.bbox[1];
    return byTop !== 0 ? byTop : a.bbox[0] - b.bbox[0];
  });
}

/**
 * 绑定仍指向当前画面（同会话且修订一致）时才允许渲染。
 * activeSession 的 revision 由编辑器在每次编辑时本地立即推进（不等宿主
 * 回显），root 直接传本地值：编辑、换图或会话关闭后，旧识别行立即不可
 * 复制。
 */
export function isLineBindingActive(
  binding: ImageTextLineBinding | undefined,
  activeSession: ImageTextLineBinding | undefined,
): boolean {
  return (
    binding !== undefined &&
    activeSession !== undefined &&
    binding.sessionId === activeSession.sessionId &&
    binding.revision === activeSession.revision
  );
}

/**
 * 把 [0,1000] 归一化行框映射为显示像素 AABB。
 * 契约冻结：image 必须是与 OCR 同一张最终 PNG（0°、未再旋转/裁剪），
 * 显示为适应缩放居中——projectPoint 固定 0°→0°，与画布 0° 绘制同一
 * 变换。旋转/裁剪后的画面不属于该参考系：root 必须重新导出并识别，
 * 旧层不得跨旋转映射冒充。图像未解码或退化框返回 undefined，由调用方
 * 跳过该行。
 */
export function layoutTextBox(
  bbox: readonly [number, number, number, number],
  image: HTMLImageElement,
  displaySize: CanvasSize,
): ImageTextBox | undefined {
  if (!image.naturalWidth || !image.naturalHeight) return undefined;
  const naturalSize = outputSize(image, 0);
  const project = (x: number, y: number): Point =>
    projectPoint(
      {
        x: (x / 1000) * naturalSize.width,
        y: (y / 1000) * naturalSize.height,
      },
      image,
      0,
      naturalSize,
      0,
      displaySize,
    );
  const first = project(bbox[0], bbox[1]);
  const second = project(bbox[2], bbox[3]);
  const left = Math.min(first.x, second.x);
  const right = Math.max(first.x, second.x);
  const top = Math.min(first.y, second.y);
  const bottom = Math.max(first.y, second.y);
  if (!(right > left) || !(bottom > top)) return undefined;
  return { left, top, width: right - left, height: bottom - top };
}
