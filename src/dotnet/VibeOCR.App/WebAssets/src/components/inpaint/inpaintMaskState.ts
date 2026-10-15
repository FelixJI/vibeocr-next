/**
 * 修补二值掩膜编辑状态（原图整数像素空间）：矩形/笔刷的增减共同维护
 * ROI + 0/255 位图。纯函数、无 DOM，便于直接单测。
 *
 * 契约：
 * - ROI（掩膜包围盒，含已清除的空穴区域）≤ INPAINT_MAX_ROI_PIXELS；
 *   有效掩膜像素 ≤ INPAINT_MAX_MASK_PIXELS。任一超限拒绝整个操作并
 *   保留原状态（显式失败，不静默截断）。
 * - 笔刷以直径 stamp 光栅化（步长 = 半径/2 保证覆盖），总 stamp 像素
 *   工作量受预算约束，超限同样拒绝整个操作。
 * - 图像尺寸外的部分自动裁剪；完全在图像外的操作为无变化成功。
 */

import {
  INPAINT_MAX_MASK_PIXELS,
  INPAINT_MAX_ROI_PIXELS,
  type InpaintRect,
} from "./inpaint";

export interface InpaintMaskState {
  readonly rect: InpaintRect;
  readonly data: Uint8Array;
}

export type InpaintMaskOpResult =
  | {
      readonly ok: true;
      /** 无变化时为 undefined（如空掩膜上移除）：调用方保持/清空当前状态。 */
      readonly state: InpaintMaskState | undefined;
    }
  | {
      readonly ok: false;
      readonly reason: "roi-budget" | "mask-budget" | "work-budget";
    };

/** 笔刷 stamp 光栅化工作量预算（stamp 行跨度像素累计 ≤ 64M）。 */
export const INPAINT_MASK_WORK_BUDGET = 64_000_000;

interface ImageSize {
  readonly width: number;
  readonly height: number;
}

function clampRectToImage(rect: InpaintRect, image: ImageSize): InpaintRect {
  const x0 = Math.max(0, Math.min(image.width, Math.floor(rect.x)));
  const y0 = Math.max(0, Math.min(image.height, Math.floor(rect.y)));
  if (rect.width <= 0 || rect.height <= 0)
    return { x: x0, y: y0, width: 0, height: 0 };
  const x1 = Math.max(
    x0,
    Math.min(image.width, Math.ceil(rect.x + rect.width)),
  );
  const y1 = Math.max(
    y0,
    Math.min(image.height, Math.ceil(rect.y + rect.height)),
  );
  return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
}

function rectsUnion(a: InpaintRect, b: InpaintRect): InpaintRect {
  const x = Math.min(a.x, b.x);
  const y = Math.min(a.y, b.y);
  return {
    x,
    y,
    width: Math.max(a.x + a.width, b.x + b.width) - x,
    height: Math.max(a.y + a.height, b.y + b.height) - y,
  };
}

function intersectRects(a: InpaintRect, b: InpaintRect): InpaintRect {
  const x = Math.max(a.x, b.x);
  const y = Math.max(a.y, b.y);
  return {
    x,
    y,
    width: Math.max(0, Math.min(a.x + a.width, b.x + b.width) - x),
    height: Math.max(0, Math.min(a.y + a.height, b.y + b.height) - y),
  };
}

/** 以新 ROI 重分配并拷贝旧数据。 */
function resizeState(
  state: InpaintMaskState,
  rect: InpaintRect,
): InpaintMaskState {
  const data = new Uint8Array(rect.width * rect.height);
  for (let y = 0; y < state.rect.height; y += 1) {
    for (let x = 0; x < state.rect.width; x += 1) {
      if ((state.data[y * state.rect.width + x] ?? 0) === 0) continue;
      const nx = state.rect.x + x - rect.x;
      const ny = state.rect.y + y - rect.y;
      if (nx < 0 || ny < 0 || nx >= rect.width || ny >= rect.height) continue;
      data[ny * rect.width + nx] = 1;
    }
  }
  return { rect, data };
}

function prepare(
  current: InpaintMaskState | undefined,
  region: InpaintRect,
): { ok: true; state: InpaintMaskState } | { ok: false; reason: "roi-budget" } {
  const union = current ? rectsUnion(current.rect, region) : region;
  if (union.width * union.height > INPAINT_MAX_ROI_PIXELS) {
    return { ok: false, reason: "roi-budget" };
  }
  return {
    ok: true,
    state: current
      ? resizeState(current, union)
      : { rect: region, data: new Uint8Array(region.width * region.height) },
  };
}

function countMasked(state: InpaintMaskState): number {
  let count = 0;
  for (let i = 0; i < state.data.length; i += 1) {
    if (state.data[i] !== 0) count += 1;
  }
  return count;
}

function compactMask(state: InpaintMaskState): InpaintMaskState | undefined {
  const rect = maskBounds(state);
  if (!rect) return undefined;
  return rect.x === state.rect.x &&
    rect.y === state.rect.y &&
    rect.width === state.rect.width &&
    rect.height === state.rect.height
    ? state
    : resizeState(state, rect);
}

/** 矩形增/减：add=true 置 1，否则清 0。 */
export function applyMaskRect(
  current: InpaintMaskState | undefined,
  rect: InpaintRect,
  image: ImageSize,
  add: boolean,
): InpaintMaskOpResult {
  let region = clampRectToImage(rect, image);
  if (
    region.x >= image.width ||
    region.y >= image.height ||
    region.width <= 0 ||
    region.height <= 0
  ) {
    return {
      ok: true,
      state: current,
    };
  }
  if (!add && !current) {
    return { ok: true, state: current };
  }
  if (!add && current) {
    region = intersectRects(region, current.rect);
    if (region.width <= 0 || region.height <= 0)
      return { ok: true, state: current };
  }
  const prepared = add
    ? prepare(current, region)
    : { ok: true as const, state: current! };
  if (!prepared.ok) return prepared;
  const state = prepared.state;
  const next = new Uint8Array(state.data);
  for (let y = region.y; y < region.y + region.height; y += 1) {
    for (let x = region.x; x < region.x + region.width; x += 1) {
      const lx = x - state.rect.x;
      const ly = y - state.rect.y;
      if (lx < 0 || ly < 0 || lx >= state.rect.width || ly >= state.rect.height)
        continue;
      next[ly * state.rect.width + lx] = add ? 1 : 0;
    }
  }
  const result: InpaintMaskState = { rect: state.rect, data: next };
  if (add && countMasked(result) > INPAINT_MAX_MASK_PIXELS) {
    return { ok: false, reason: "mask-budget" };
  }
  return {
    ok: true,
    state: add ? result : compactMask(result),
  };
}

/** 笔刷增/减：点列（原图空间）沿轨迹以半径 stamp 光栅化。 */
export function applyMaskBrush(
  current: InpaintMaskState | undefined,
  points: readonly { x: number; y: number }[],
  radius: number,
  image: ImageSize,
  add: boolean,
): InpaintMaskOpResult {
  if (points.length === 0) {
    return { ok: true, state: current };
  }
  if (!add && !current) {
    return { ok: true, state: current };
  }
  const r = Math.max(1, Math.floor(radius));
  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;
  for (const at of points) {
    minX = Math.min(minX, at.x);
    minY = Math.min(minY, at.y);
    maxX = Math.max(maxX, at.x);
    maxY = Math.max(maxY, at.y);
  }
  let region = clampRectToImage(
    {
      x: minX - r,
      y: minY - r,
      width: maxX - minX + r * 2 + 2,
      height: maxY - minY + r * 2 + 2,
    },
    image,
  );
  if (region.width <= 0 || region.height <= 0)
    return { ok: true, state: current };
  if (!add && current) {
    region = intersectRects(region, current.rect);
    if (region.width <= 0 || region.height <= 0)
      return { ok: true, state: current };
  }
  const prepared = add
    ? prepare(current, region)
    : { ok: true as const, state: current! };
  if (!prepared.ok) return prepared;
  const state = prepared.state;
  const next = new Uint8Array(state.data);
  const stamp = (cx: number, cy: number): number => {
    let work = 0;
    const top = Math.max(state.rect.y, Math.floor(cy - r));
    const bottom = Math.min(
      state.rect.y + state.rect.height - 1,
      Math.ceil(cy + r),
    );
    for (let y = top; y <= bottom; y += 1) {
      if (Math.abs(y - cy) > r) continue;
      const span = Math.sqrt(r * r - (y - cy) ** 2);
      const left = Math.max(state.rect.x, Math.ceil(cx - span));
      const right = Math.min(
        state.rect.x + state.rect.width - 1,
        Math.floor(cx + span),
      );
      work += add
        ? Math.floor(cx + span) - Math.ceil(cx - span) + 1
        : Math.max(0, right - left + 1);
      for (let x = left; x <= right; x += 1) {
        const lx = x - state.rect.x;
        const ly = y - state.rect.y;
        if (
          lx < 0 ||
          ly < 0 ||
          lx >= state.rect.width ||
          ly >= state.rect.height
        )
          continue;
        next[ly * state.rect.width + lx] = add ? 1 : 0;
      }
    }
    return work;
  };
  let work = 0;
  const step = Math.max(1, r / 2);
  for (let i = 0; i < points.length; i += 1) {
    const from = points[i]!;
    const to = points[i + 1] ?? from;
    const length = Math.hypot(to.x - from.x, to.y - from.y);
    const steps = Math.max(1, Math.ceil(length / step));
    for (let s = 0; s <= steps; s += 1) {
      const t = s / steps;
      work += stamp(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
      if (work > INPAINT_MASK_WORK_BUDGET) {
        return { ok: false, reason: "work-budget" };
      }
    }
  }
  const result: InpaintMaskState = { rect: state.rect, data: next };
  if (add && countMasked(result) > INPAINT_MAX_MASK_PIXELS) {
    return { ok: false, reason: "mask-budget" };
  }
  return {
    ok: true,
    state: add ? result : compactMask(result),
  };
}

/** 按补丁 id 去重统计保留的历史前缀；默认包含全部撤销/重做引用。
 * 新提交传入当前游标，排除将被截断的重做分支；存储由历史 GC 回收。
 * 此数值只约束唯一补丁像素，不代表浏览器或进程的内存峰值。 */
export function totalDistinctPatchPixels(
  entries: readonly {
    readonly patches?: readonly {
      readonly id: number;
      readonly width: number;
      readonly height: number;
    }[];
  }[],
  retainedThrough = entries.length - 1,
): number {
  const seen = new Set<number>();
  let total = 0;
  for (const entry of entries.slice(0, retainedThrough + 1)) {
    for (const patch of entry.patches ?? []) {
      if (seen.has(patch.id)) continue;
      seen.add(patch.id);
      total += patch.width * patch.height;
    }
  }
  return total;
}

/** 掩膜紧致包围盒（无有效像素时 undefined）。 */
export function maskBounds(
  state: InpaintMaskState | undefined,
): InpaintRect | undefined {
  if (!state) return undefined;
  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;
  for (let y = 0; y < state.rect.height; y += 1) {
    for (let x = 0; x < state.rect.width; x += 1) {
      if ((state.data[y * state.rect.width + x] ?? 0) === 0) continue;
      minX = Math.min(minX, x);
      minY = Math.min(minY, y);
      maxX = Math.max(maxX, x);
      maxY = Math.max(maxY, y);
    }
  }
  if (!Number.isFinite(minX)) return undefined;
  return {
    x: state.rect.x + minX,
    y: state.rect.y + minY,
    width: maxX - minX + 1,
    height: maxY - minY + 1,
  };
}

/** 有效掩膜像素数。 */
export function maskCount(state: InpaintMaskState | undefined): number {
  return state ? countMasked(state) : 0;
}
