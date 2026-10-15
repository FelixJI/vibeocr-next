/**
 * 重复内容匹配（纯 CPU，同尺度平移）：在基线像素（原图 + 已应用补丁）
 * 中寻找与用户框选区域平移后近似相同的位置。
 *
 * 契约与边界：
 * - 只支持同尺度平移：候选块与源块同宽高，只比较整数平移偏移；不做
 *   旋转/缩放/镜像，也不做亚像素对齐。
 * - 基线整图 ≤ DUPLICATE_MAX_IMAGE_PIXELS（8MP，与修补同一预算叙事）；
 *   源块 ≤ DUPLICATE_MAX_SOURCE_PIXELS（512×512=0.26MP）。源块灰度方差
 *   低于 DUPLICATE_LOW_TEXTURE_VARIANCE 时拒绝（纯色/近纯色块在任何
 *   平移处都“匹配”，结果无意义）。
 * - 工作量有界：偏移总数 × 源块像素数 ≤ DUPLICATE_WORK_BUDGET
 *   （96M 次像素比较，每次早退）。超出的偏移空间按确定性步长 s 稀疏
 *   采样（s = ceil(sqrt(总量/预算))），网格对齐 (0,0)，可复现；步进
 *   采样可能错过未落在网格上的副本，这是显式资源边界，不是静默错误。
 * - 候选上限 DUPLICATE_MAX_CANDIDATES：按平均误差升序保留，且两两
 *   不重叠、不与源块重叠（部分重叠的平移块会平凡匹配自身边缘）。
 * - 无任何候选时以 "no-duplicates" 显式失败。
 *
 * 资源开销：直接在传入缓冲区上读取，无额外像素副本；响应只携带少量
 * 候选坐标。峰值内存即调用方传入的 32MB 基线（8MP RGBA）。
 */

export interface DuplicateRect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export interface DuplicateCandidate extends DuplicateRect {
  /** 源块与候选块的平均每通道绝对差（0-255），越小越相似。 */
  readonly error: number;
}

export type DuplicateFailureCode =
  | "invalid-image"
  | "invalid-rect"
  | "low-texture"
  | "budget-exceeded"
  | "no-duplicates"
  | "unexpected";

export const DUPLICATE_MAX_IMAGE_PIXELS = 8_000_000;
export const DUPLICATE_MAX_SOURCE_PIXELS = 262_144;
export const DUPLICATE_MAX_CANDIDATES = 8;
/** 平均每通道绝对差 ≤ 6 视为重复（PNG 无损副本为 0；轻微重编码可容忍）。 */
export const DUPLICATE_MATCH_TOLERANCE = 6;
/** 源块灰度方差下限：低于它的近纯色块直接拒绝。 */
export const DUPLICATE_LOW_TEXTURE_VARIANCE = 16;
/** 像素比较总预算：偏移数 × 源块像素数 ≤ 96M。 */
export const DUPLICATE_WORK_BUDGET = 96_000_000;

export class DuplicateError extends Error {
  readonly code: DuplicateFailureCode;

  constructor(code: DuplicateFailureCode, message: string) {
    super(message);
    this.name = "DuplicateError";
    this.code = code;
  }
}

function fail(code: DuplicateFailureCode, message: string): never {
  throw new DuplicateError(code, message);
}

export interface DuplicateRequest {
  /** 调用方递增的请求代；响应原样回带，迟到结果由 UI 丢弃。 */
  readonly generation: number;
  readonly rgba: ArrayBuffer;
  readonly width: number;
  readonly height: number;
  readonly rect: DuplicateRect;
}

export type DuplicateResponse =
  | {
      readonly generation: number;
      readonly ok: true;
      readonly candidates: readonly DuplicateCandidate[];
    }
  | {
      readonly generation: number;
      readonly ok: false;
      readonly error: {
        readonly code: DuplicateFailureCode;
        readonly message: string;
      };
    };

function rectsOverlap(a: DuplicateRect, b: DuplicateRect): boolean {
  return (
    a.x < b.x + b.width &&
    a.x + a.width > b.x &&
    a.y < b.y + b.height &&
    a.y + a.height > b.y
  );
}

/**
 * 在基线像素中寻找源块的重复位置（同尺度平移）。输入缓冲区只读；
 * 任何失败都以 DuplicateError 抛出，不返回部分结果。
 */
export function findDuplicateRegions(
  rgba: Uint8ClampedArray,
  width: number,
  height: number,
  source: DuplicateRect,
): readonly DuplicateCandidate[] {
  if (
    !Number.isInteger(width) ||
    !Number.isInteger(height) ||
    width <= 0 ||
    height <= 0 ||
    rgba.length !== width * height * 4
  ) {
    fail(
      "invalid-image",
      `图像尺寸或缓冲区不匹配：${width}×${height}，buffer ${rgba.length} 字节`,
    );
  }
  if (width * height > DUPLICATE_MAX_IMAGE_PIXELS) {
    fail(
      "budget-exceeded",
      `整图 ${width * height} 像素超过重复匹配上限 ${DUPLICATE_MAX_IMAGE_PIXELS}`,
    );
  }
  if (
    !Number.isInteger(source.x) ||
    !Number.isInteger(source.y) ||
    !Number.isInteger(source.width) ||
    !Number.isInteger(source.height)
  ) {
    fail("invalid-rect", "源选区坐标必须是整数像素");
  }
  const x0 = Math.max(source.x, 0);
  const y0 = Math.max(source.y, 0);
  const x1 = Math.min(source.x + source.width, width);
  const y1 = Math.min(source.y + source.height, height);
  const bw = x1 - x0;
  const bh = y1 - y0;
  if (bw <= 0 || bh <= 0) {
    fail("invalid-rect", "源选区完全在图像外");
  }
  if (bw * bh > DUPLICATE_MAX_SOURCE_PIXELS) {
    fail(
      "budget-exceeded",
      `源选区 ${bw * bh} 像素超过重复匹配上限 ${DUPLICATE_MAX_SOURCE_PIXELS}`,
    );
  }
  const block: DuplicateRect = { x: x0, y: y0, width: bw, height: bh };
  const count = bw * bh;

  // 源块灰度方差：近纯色块拒绝（低纹理）。
  let sum = 0;
  let sumSquares = 0;
  for (let y = 0; y < bh; y += 1) {
    for (let x = 0; x < bw; x += 1) {
      const p = ((y0 + y) * width + (x0 + x)) * 4;
      const gray =
        (((rgba[p] ?? 0) + (rgba[p + 1] ?? 0) + (rgba[p + 2] ?? 0)) / 3) | 0;
      sum += gray;
      sumSquares += gray * gray;
    }
  }
  const variance = Math.max(0, sumSquares / count - (sum / count) ** 2);
  if (variance < DUPLICATE_LOW_TEXTURE_VARIANCE) {
    fail(
      "low-texture",
      "所选区域纹理太少（近似纯色），无法可靠匹配重复内容；请框选包含明确图案的范围。",
    );
  }

  // 有界偏移网格：总比较次数 = 偏移数 × 块像素数 ≤ 预算。
  const offsetsX = width - bw + 1;
  const offsetsY = height - bh + 1;
  const stride = Math.max(
    1,
    Math.ceil(Math.sqrt((offsetsX * offsetsY * count) / DUPLICATE_WORK_BUDGET)),
  );
  const tolerance = DUPLICATE_MATCH_TOLERANCE * count; // 每×3通道累计容差
  const best: DuplicateCandidate[] = [];

  for (let dy = 0; dy < offsetsY; dy += stride) {
    for (let dx = 0; dx < offsetsX; dx += stride) {
      const cx = x0 + dx;
      const cy = y0 + dy;
      if (dx === 0 && dy === 0) continue;
      const candidate: DuplicateRect = {
        x: cx,
        y: cy,
        width: bw,
        height: bh,
      };
      if (rectsOverlap(candidate, block)) continue;
      // 源块与候选块逐像素比较（RGB），累计误差超容差即早退。
      let error = 0;
      compare: for (let y = 0; y < bh; y += 1) {
        for (let x = 0; x < bw; x += 1) {
          const p = ((y0 + y) * width + (x0 + x)) * 4;
          const q = ((cy + y) * width + (cx + x)) * 4;
          error +=
            Math.abs((rgba[p] ?? 0) - (rgba[q] ?? 0)) +
            Math.abs((rgba[p + 1] ?? 0) - (rgba[q + 1] ?? 0)) +
            Math.abs((rgba[p + 2] ?? 0) - (rgba[q + 2] ?? 0));
          if (error > tolerance) break compare;
        }
      }
      if (error > tolerance) continue;
      const entry: DuplicateCandidate = {
        ...candidate,
        error: error / (count * 3),
      };
      // 有序插入并截断到候选上限。
      let slot = best.length;
      while (slot > 0 && (best[slot - 1]?.error ?? 0) > entry.error) {
        slot -= 1;
      }
      if (slot < DUPLICATE_MAX_CANDIDATES) {
        best.splice(slot, 0, entry);
        if (best.length > DUPLICATE_MAX_CANDIDATES) best.pop();
      }
    }
  }
  // 非极大抑制：按误差升序保留两两不重叠的候选。
  const kept: DuplicateCandidate[] = [];
  for (const candidate of best) {
    if (kept.some((existing) => rectsOverlap(existing, candidate))) continue;
    kept.push(candidate);
    if (kept.length >= DUPLICATE_MAX_CANDIDATES) break;
  }
  if (kept.length === 0) {
    fail("no-duplicates", "未找到所选内容的重复位置；可调整框选范围后重试。");
  }
  return kept;
}
