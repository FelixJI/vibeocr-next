/**
 * 局部 CPU 修补算法（Beta）：二值掩膜内像素的邻域颜色扩散填充。
 *
 * 输入为 RGBA 像素、ROI（整数像素包围盒）与 ROI 尺寸的二值掩膜
 * （0/255，非 0 视为选中）。算法只读取掩膜外的原始 RGB（含 ROI 外
 * 与掩膜内的“洞”），从已知边界向内逐波扩散，再做有界次的拉普拉斯
 * 平滑（Gauss-Seidel 交替扫描）；从不读取掩膜内原始 RGB，不是对原
 * 水印做模糊。纯色/缓变背景的小掩膜可实际恢复背景颜色；复杂纹理
 * 只有有限质量，不宣称无损，也不是 Telea 等论文算法的精确实现。
 *
 * 契约与边界：
 * - 只改写掩膜内像素的 RGB；掩膜外每字节不变（含 ROI 内的洞与 ROI
 *   外全部像素），掩膜内 alpha 保持原值，也不悄悄外扩。
 * - 输入缓冲区只读；返回全新像素缓冲区。
 * - 掩膜为空、掩膜覆盖整图（没有任何已知像素可参考）直接失败。
 * - 预算：整图 ≤ INPAINT_MAX_IMAGE_PIXELS（8MP）、ROI ≤
 *   INPAINT_MAX_ROI_PIXELS（1MP）、有效掩膜 ≤ INPAINT_MAX_MASK_PIXELS
 *   （1MP），超限直接抛 InpaintError。
 *
 * 资源开销（上限估算）：输出副本 4·width·height 字节（8MP 时 32MB）
 * + 波次/队列两个 Int32Array 共 8·ROI 字节（1MP 时 8MB）。
 * 计算量：全图复制 O(width·height)，扩散 O(4·mask)，平滑更新总次数
 * 受 SMOOTHING_UPDATE_BUDGET 约束（≤ 32M 次邻域更新）；耗时由实际
 * CPU 与掩膜决定；性能验收使用合成图片实测。
 *
 * 历史补丁聚合预算：编辑器侧另以 INPAINT_MAX_TOTAL_PATCH_PIXELS 限制
 * 新提交后存活的唯一补丁像素总量；重复历史引用不重复计算。上述数组
 * 大小不涵盖浏览器画布、解码和预览，不能作为进程内存峰值。
 * 超限显式拒绝新的应用，绝不静默丢弃历史撤销条目。
 */

/** 用户矩形选区：整数像素坐标，允许部分越界（内部 clamp 收敛）。 */
export interface InpaintRect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export type InpaintFailureCode =
  | "invalid-image"
  | "invalid-rect"
  | "empty-mask"
  | "mask-covers-image"
  | "budget-exceeded"
  | "unexpected";

/** 整图像素预算：8MP。 */
export const INPAINT_MAX_IMAGE_PIXELS = 8_000_000;
/** 有效选区像素预算：1MP。 */
export const INPAINT_MAX_MASK_PIXELS = 1_000_000;
/** ROI（掩膜包围盒）分配预算：与有效掩膜同限 1MP，约束 depth/queue
 * 两个 Int32Array（8 字节/像素 → 峰值 8MB）。 */
export const INPAINT_MAX_ROI_PIXELS = 1_000_000;
/** 历史补丁聚合预算：全部存活唯一补丁像素总量 ≤ 8MP。
 * 超限显式拒绝新应用，不删除旧撤销。 */
export const INPAINT_MAX_TOTAL_PATCH_PIXELS = 8_000_000;

/** 平滑总工作量预算：扫过次数 = 预算/选区像素数，夹在区间内。 */
const SMOOTHING_UPDATE_BUDGET = 32_000_000;
const SMOOTHING_MIN_SWEEPS = 8;
const SMOOTHING_MAX_SWEEPS = 256;

export class InpaintError extends Error {
  readonly code: InpaintFailureCode;

  constructor(code: InpaintFailureCode, message: string) {
    super(message);
    this.name = "InpaintError";
    this.code = code;
  }
}

function fail(code: InpaintFailureCode, message: string): never {
  throw new InpaintError(code, message);
}

function validateImage(
  rgba: Uint8ClampedArray,
  width: number,
  height: number,
): void {
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
  if (width * height > INPAINT_MAX_IMAGE_PIXELS) {
    fail(
      "budget-exceeded",
      `整图 ${width * height} 像素超过预算 ${INPAINT_MAX_IMAGE_PIXELS}`,
    );
  }
}

function validateRoi(
  rect: InpaintRect,
  width: number,
  height: number,
): {
  readonly x0: number;
  readonly y0: number;
  readonly mw: number;
  readonly mh: number;
} {
  if (
    !Number.isInteger(rect.x) ||
    !Number.isInteger(rect.y) ||
    !Number.isInteger(rect.width) ||
    !Number.isInteger(rect.height)
  ) {
    fail("invalid-rect", "选区坐标必须是整数像素");
  }
  if (rect.width <= 0 || rect.height <= 0) {
    fail("empty-mask", "选区宽高必须为正");
  }
  const x0 = Math.max(rect.x, 0);
  const y0 = Math.max(rect.y, 0);
  const x1 = Math.min(rect.x + rect.width, width);
  const y1 = Math.min(rect.y + rect.height, height);
  const mw = x1 - x0;
  const mh = y1 - y0;
  if (mw <= 0 || mh <= 0) {
    fail("empty-mask", "选区完全在图像外，收敛后为空");
  }
  return { x0, y0, mw, mh };
}

/** Worker 请求：rgba 必须是调用方持有的副本（transfer 后原 buffer 被 detach）。 */
export interface InpaintRequest {
  /** 调用方递增的请求代；响应原样回带，迟到结果由 UI 丢弃。 */
  readonly generation: number;
  readonly rgba: ArrayBuffer;
  readonly width: number;
  readonly height: number;
  readonly rect: InpaintRect;
}

/** 掩膜请求：在 InpaintRequest 基础上携带 ROI 尺寸的二值掩膜
 * （Uint8Array，0/255），ROI 即 rect；rect 为掩膜的整数包围盒。
 * mask 缓冲区随消息 transfer，调用方必须传副本。 */
export interface InpaintMaskRequest extends InpaintRequest {
  readonly mask: ArrayBuffer;
}

export function isInpaintMaskRequest(
  request: InpaintRequest,
): request is InpaintMaskRequest {
  return (
    typeof request === "object" &&
    request !== null &&
    (request as InpaintMaskRequest).mask instanceof ArrayBuffer
  );
}

export type InpaintResponse =
  | {
      readonly generation: number;
      readonly ok: true;
      readonly rgba: ArrayBuffer;
      readonly width: number;
      readonly height: number;
    }
  | {
      readonly generation: number;
      readonly ok: false;
      readonly error: {
        readonly code: InpaintFailureCode;
        readonly message: string;
      };
    };

/**
 * 就地修补一个二值掩膜，返回全新像素缓冲区（输入只读、不变）。
 * 掩膜外（含洞与 ROI 外）每字节不变，掩膜内只重算 RGB、alpha 原样。
 * 任意失败（尺寸、掩膜、预算）都以 InpaintError 抛出，不返回半成品像素。
 */
export function inpaintMask(
  rgba: Uint8ClampedArray,
  width: number,
  height: number,
  roi: InpaintRect,
  mask: Uint8Array,
): Uint8ClampedArray<ArrayBuffer> {
  validateImage(rgba, width, height);
  const { x0, y0, mw, mh } = validateRoi(roi, width, height);
  if (mask.length !== mw * mh) {
    fail("invalid-rect", `掩膜尺寸 ${mask.length} 与 ROI ${mw}×${mh} 不匹配`);
  }
  if (mw * mh > INPAINT_MAX_ROI_PIXELS) {
    fail(
      "budget-exceeded",
      `ROI ${mw * mh} 像素超过预算 ${INPAINT_MAX_ROI_PIXELS}`,
    );
  }
  // 掩膜位图与有效像素计数；ROI 外像素恒为已知。
  const selected = new Uint8Array(mw * mh);
  let count = 0;
  for (let i = 0; i < mask.length; i += 1) {
    if ((mask[i] ?? 0) !== 0) {
      selected[i] = 1;
      count += 1;
    }
  }
  if (count === 0) {
    fail("empty-mask", "掩膜内没有任何像素");
  }
  if (count > INPAINT_MAX_MASK_PIXELS) {
    fail(
      "budget-exceeded",
      `有效掩膜 ${count} 像素超过预算 ${INPAINT_MAX_MASK_PIXELS}`,
    );
  }
  if (count === width * height) {
    fail("mask-covers-image", "掩膜覆盖整张图片，没有任何周边像素可参考");
  }

  // 输出为输入的完整副本；此后只写掩膜内像素的 RGB 通道。
  const out = new Uint8ClampedArray(rgba);
  // depth：ROI 内像素的填充波次，-1 表示未填充或已知（掩膜外）；
  // queue：波前队列，每个掩膜像素至多入队一次，循环次数有界。
  const depth = new Int32Array(mw * mh).fill(-1);
  const queue = new Int32Array(count);
  let tail = 0;
  const known = (lx: number, ly: number): boolean => {
    if (lx < 0 || ly < 0 || lx >= mw || ly >= mh) return false; // ROI 外
    return selected[ly * mw + lx] === 0;
  };

  let accR = 0;
  let accG = 0;
  let accB = 0;
  let accW = 0;
  // 采样一个 4-邻像素：掩膜内要求已填充，权重随波次衰减；掩膜外/ROI 外
  // 为已知原始像素，权重 1；图像外无效。值在入队时写好，保证 depth ≥ 0
  // 的掩膜像素一定已重算，绝不读到原水印颜色。
  const sample = (gx: number, gy: number, maskIdx: number): void => {
    let weight = 1;
    if (maskIdx >= 0 && selected[maskIdx] === 1) {
      const d = depth[maskIdx] ?? -1;
      if (d < 0) return;
      weight = 1 / (1 + d);
    } else if (gx < 0 || gy < 0 || gx >= width || gy >= height) {
      return;
    }
    const p = (gy * width + gx) * 4;
    accR += (out[p] ?? 0) * weight;
    accG += (out[p + 1] ?? 0) * weight;
    accB += (out[p + 2] ?? 0) * weight;
    accW += weight;
  };
  const fillWave = (lx: number, ly: number, wave: number): void => {
    const idx = ly * mw + lx;
    if (selected[idx] !== 1 || (depth[idx] ?? -1) !== -1) return;
    const gx = x0 + lx;
    const gy = y0 + ly;
    accR = 0;
    accG = 0;
    accB = 0;
    accW = 0;
    sample(gx - 1, gy, lx > 0 ? idx - 1 : -1);
    sample(gx + 1, gy, lx < mw - 1 ? idx + 1 : -1);
    sample(gx, gy - 1, ly > 0 ? idx - mw : -1);
    sample(gx, gy + 1, ly < mh - 1 ? idx + mw : -1);
    if (accW <= 0) return; // 暂无可用邻居，留待后续波前再试
    const p = (gy * width + gx) * 4;
    out[p] = accR / accW;
    out[p + 1] = accG / accW;
    out[p + 2] = accB / accW;
    depth[idx] = wave;
    queue[tail++] = idx;
  };

  // 种子：与已知像素（掩膜洞、ROI 外、图像外不算——图像外无效）相邻的
  // 掩膜边缘像素（波次 0）。ROI 边界外若仍在图像内即为已知像素。
  for (let ly = 0; ly < mh; ly += 1) {
    for (let lx = 0; lx < mw; lx += 1) {
      if (selected[ly * mw + lx] !== 1) continue;
      if (
        known(lx - 1, ly) ||
        known(lx + 1, ly) ||
        known(lx, ly - 1) ||
        known(lx, ly + 1) ||
        (lx === 0 && x0 > 0) ||
        (lx === mw - 1 && x0 + mw < width) ||
        (ly === 0 && y0 > 0) ||
        (ly === mh - 1 && y0 + mh < height)
      ) {
        fillWave(lx, ly, 0);
      }
    }
  }
  let head = 0;
  while (head < tail) {
    const idx = queue[head++] ?? -1;
    if (idx < 0) continue;
    const lx = idx % mw;
    const ly = (idx - lx) / mw;
    const wave = (depth[idx] ?? 0) + 1;
    if (lx > 0) fillWave(lx - 1, ly, wave);
    if (lx < mw - 1) fillWave(lx + 1, ly, wave);
    if (ly > 0) fillWave(lx, ly - 1, wave);
    if (ly < mh - 1) fillWave(lx, ly + 1, wave);
  }
  if (tail !== count) {
    fail("unexpected", "扩散未覆盖全部掩膜像素");
  }

  // 有界拉普拉斯平滑：只改掩膜内 RGB，掩膜外邻居恒为固定已知值，因此
  // 边界连续。扫描方向交替以保持对称；总更新次数受预算约束。
  const sweeps = Math.max(
    SMOOTHING_MIN_SWEEPS,
    Math.min(SMOOTHING_MAX_SWEEPS, Math.floor(SMOOTHING_UPDATE_BUDGET / count)),
  );
  const rowStride = width * 4;
  for (let sweep = 0; sweep < sweeps; sweep += 1) {
    for (let step = 0; step < count; step += 1) {
      // 掩膜内像素的稳定遍历：queue 恰好含每个掩膜像素一次。
      const idx = queue[step] ?? -1;
      if (idx < 0) continue;
      const lx = idx % mw;
      const ly = (idx - lx) / mw;
      const gx = x0 + lx;
      const gy = y0 + ly;
      const p = (gy * width + gx) * 4;
      let r = 0;
      let g = 0;
      let b = 0;
      let n = 0;
      if (gx > 0) {
        r += out[p - 4] ?? 0;
        g += out[p - 3] ?? 0;
        b += out[p - 2] ?? 0;
        n += 1;
      }
      if (gx < width - 1) {
        r += out[p + 4] ?? 0;
        g += out[p + 5] ?? 0;
        b += out[p + 6] ?? 0;
        n += 1;
      }
      if (gy > 0) {
        const q = p - rowStride;
        r += out[q] ?? 0;
        g += out[q + 1] ?? 0;
        b += out[q + 2] ?? 0;
        n += 1;
      }
      if (gy < height - 1) {
        const q = p + rowStride;
        r += out[q] ?? 0;
        g += out[q + 1] ?? 0;
        b += out[q + 2] ?? 0;
        n += 1;
      }
      if (n > 0) {
        out[p] = r / n;
        out[p + 1] = g / n;
        out[p + 2] = b / n;
      }
    }
  }
  return out;
}

/**
 * 就地修补一个矩形选区（整矩形掩膜）：与 inpaintMask 相同的预算、错误
 * 码与像素契约，兼容旧矩形调用方。
 */
export function inpaintRectangle(
  rgba: Uint8ClampedArray,
  width: number,
  height: number,
  rect: InpaintRect,
): Uint8ClampedArray<ArrayBuffer> {
  validateImage(rgba, width, height);
  const { x0, y0, mw, mh } = validateRoi(rect, width, height);
  const clamped: InpaintRect = { x: x0, y: y0, width: mw, height: mh };
  if (mw * mh > INPAINT_MAX_MASK_PIXELS) {
    fail(
      "budget-exceeded",
      `有效选区 ${mw * mh} 像素超过预算 ${INPAINT_MAX_MASK_PIXELS}`,
    );
  }
  if (mw === width && mh === height) {
    fail("mask-covers-image", "选区覆盖整图，没有任何周边有效像素可扩散");
  }
  return inpaintMask(
    rgba,
    width,
    height,
    clamped,
    new Uint8Array(mw * mh).fill(1),
  );
}
