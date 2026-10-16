/**
 * 重复内容匹配（纯 CPU，同尺度平移）：在基线像素（原图 + 已应用补丁）
 * 中寻找与用户框选区域平移后近似相同的位置。
 *
 * 契约与边界：
 * - 只支持同尺度平移：候选块与源块同宽高，只比较整数平移偏移；不做
 *   旋转/缩放/镜像，也不做亚像素对齐。
 * - 基线整图 ≤ DUPLICATE_MAX_IMAGE_PIXELS（8MP，与修补同一预算叙事）；
 *   源块 ≤ DUPLICATE_MAX_SOURCE_PIXELS（512×512=0.26MP）。源块 RGB 通道平均方差与 alpha 方差的较大值
 *   低于 DUPLICATE_LOW_TEXTURE_VARIANCE 时拒绝（纯色/近纯色块在任何
 *   平移处都“匹配”，结果无意义）。
 * - 工作量有界：覆盖全部整数偏移，以十六点/分块下界筛选和有限精比控制成本，
 *   超预算明确拒绝，不返回不完整的搜索结果。
 * - 合格窗口最多 DUPLICATE_MAX_MATCH_WINDOWS，超限明确拒绝。完整扫描后
 *   按平均误差、y、x 升序去除重叠，再取 DUPLICATE_MAX_CANDIDATES 个
 *   候选；候选两两不重叠，也不与源块重叠。
 * - 无任何候选时以 "no-duplicates" 显式失败。
 *
 * 搜索分两阶段，覆盖每一个整数平移偏移（不存在未访问的偏移）：
 * 1. 筛选：小块对每个偏移比较固定 4×4 采样点（十六个像素，确定性
 *    位置，去重），RGB 累计差不超过整块容差且 alpha 样本相同才进入精比。初始筛选总量 ≤ 偏移数 ×
 *    16，超过 DUPLICATE_WORK_BUDGET 时显式 budget-exceeded 拒绝
 *    （图片过大/源块过小），不继续也不遇漏。
 *    大块用 4×4 不重叠 RGB 分块和下界及块内 alpha 样本；源 alpha 有纹理时，
 *    先将 RGB 筛过窗口记入 bitset，再复用积分表存储 alpha，要求各块 alpha 和相等。
 *    两阶段每次实际分块检查共同计入96M预算，不把第二遍扫描当作免费工作。
 *    三通道 Uint32 表与 bitset 联合≤100MB，累计值≤8MP×255。
 * 2. 精比：逐像素比较（RGB 容差与完全相同的 alpha、累计容差早退），总量受
 *    DUPLICATE_PRECISE_BUDGET 约束；相似位置过多导致超限时同样
 *    budget-exceeded 拒绝，不把截断的扫描当作完整结果。
 *
 * 资源开销：直接读取传入的基线（最多 32MB RGBA），无额外像素副本；大块另有联合≤100MB积分表与筛选bitset；
 * 最多存储 4096 个合格窗口的坐标和误差，排序后最多进行 4096×8 次重叠
 * 检查，响应只携带最多 8 个候选坐标。
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
/** 去除重叠前的合格窗口上限：超限失败，避免静默截断或无界存储。 */
export const DUPLICATE_MAX_MATCH_WINDOWS = 4096;
/** 平均每通道绝对差 ≤ 6 视为重复（PNG 无损副本为 0；轻微重编码可容忍）。 */
export const DUPLICATE_MATCH_TOLERANCE = 6;
/** RGB 平均方差与 alpha 方差的较大值下限：低于它的近纯色块直接拒绝。 */
export const DUPLICATE_LOW_TEXTURE_VARIANCE = 16;
/** 筛选检查总预算：RGB 分块/样本与额外 alpha 分块检查合计 ≤96M。 */
export const DUPLICATE_WORK_BUDGET = 96_000_000;
/** 精比阶段逐像素比较总量 ≤ 32M：相似位置过多时显式拒绝。 */
export const DUPLICATE_PRECISE_BUDGET = 32_000_000;
/** 固定 4×4 采样点：确定性位置（块内等分网格中心），不依赖随机数。 */
const DUPLICATE_SCREEN_SAMPLES: readonly { x: number; y: number }[] = [
  { x: 1, y: 1 },
  { x: 2, y: 1 },
  { x: 3, y: 1 },
  { x: 4, y: 1 },
  { x: 1, y: 2 },
  { x: 2, y: 2 },
  { x: 3, y: 2 },
  { x: 4, y: 2 },
  { x: 1, y: 3 },
  { x: 2, y: 3 },
  { x: 3, y: 3 },
  { x: 4, y: 3 },
  { x: 1, y: 4 },
  { x: 2, y: 4 },
  { x: 3, y: 4 },
  { x: 4, y: 4 },
];

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

  // 保持 RGB 平均方差门槛；alpha 自身的纹理也能区分精确 RGBA 匹配。
  const sums = [0, 0, 0, 0];
  const squares = [0, 0, 0, 0];
  for (let y = 0; y < bh; y += 1) {
    for (let x = 0; x < bw; x += 1) {
      const p = ((y0 + y) * width + (x0 + x)) * 4;
      for (let c = 0; c < 4; c += 1) {
        const value = rgba[p + c] ?? 0;
        sums[c] = sums[c]! + value;
        squares[c] = squares[c]! + value * value;
      }
    }
  }
  const variances = sums.map((sum, c) =>
    Math.max(0, squares[c]! / count - (sum / count) ** 2),
  );
  const variance = Math.max(
    (variances[0]! + variances[1]! + variances[2]!) / 3,
    variances[3]!,
  );
  if (variance < DUPLICATE_LOW_TEXTURE_VARIANCE) {
    fail(
      "low-texture",
      "所选区域纹理太少（近似纯色），无法可靠匹配重复内容；请框选包含明确图案的范围。",
    );
  }

  // 阶段 0：筛选预算。覆盖每个整数偏移（步长 1，无未访问偏移）；
  // 筛选成本 = 偏移数 × 16 采样，超过预算时显式拒绝。
  const offsetsX = width - bw + 1;
  const offsetsY = height - bh + 1;
  const offsets = offsetsX * offsetsY;
  if (offsets * DUPLICATE_SCREEN_SAMPLES.length > DUPLICATE_WORK_BUDGET) {
    fail(
      "budget-exceeded",
      `筛选 ${offsets} 个平移偏移超出工作量预算 ${DUPLICATE_WORK_BUDGET}；请缩小图片或增大框选范围后重试。`,
    );
  }
  // 采样点落在块内等分 5×5 网格的内 4×4 交叉点：确定性、对小至 5px
  // 的块也有效（小 4px 以下直接用全部像素比例）。块 < 5px 时方差很难
  // 过低纹理阈值，但采样仍需安全映射。
  const screenSamples = DUPLICATE_SCREEN_SAMPLES.map((sample) => ({
    lx: Math.min(bw - 1, Math.floor((sample.x * bw) / 5)),
    ly: Math.min(bh - 1, Math.floor((sample.y * bh) / 5)),
  })).filter(
    (sample, index, all) =>
      all.findIndex(
        (other) => other.lx === sample.lx && other.ly === sample.ly,
      ) === index,
  );
  const screenCache: number[] = [];
  for (const sample of screenSamples) {
    const p = ((y0 + sample.ly) * width + (x0 + sample.lx)) * 4;
    screenCache.push(
      (rgba[p] ?? 0) +
        (rgba[p + 1] ?? 0) * 256 +
        (rgba[p + 2] ?? 0) * 65536 +
        (rgba[p + 3] ?? 0) * 16777216,
    );
  }
  const screenAt = (lx: number, ly: number, cx: number, cy: number) => {
    const q = ((cy + ly) * width + (cx + lx)) * 4;
    return (
      (rgba[q] ?? 0) +
      (rgba[q + 1] ?? 0) * 256 +
      (rgba[q + 2] ?? 0) * 65536 +
      (rgba[q + 3] ?? 0) * 16777216
    );
  };

  // 大块使用互不重叠的分块 RGB 和之差；三角不等式保证它不超过真实 L1 误差。
  // RGB 筛过窗口用 bitset 记录，再在同一 Uint32Array 前部覆盖构造 alpha 表。
  // 积分表与 bitset 联合最多100MB；8MP×255<2^32，无整数溢出。
  let integral: Uint32Array | undefined;
  let screened: Uint8Array | undefined;
  const stride = (width + 1) * 3;
  const integralSum = (
    x: number,
    y: number,
    w: number,
    h: number,
    c: number,
  ): number => {
    const table = integral!;
    return (
      table[(y + h) * stride + (x + w) * 3 + c]! -
      table[y * stride + (x + w) * 3 + c]! -
      table[(y + h) * stride + x * 3 + c]! +
      table[y * stride + x * 3 + c]!
    );
  };
  const blocks: {
    x: number;
    y: number;
    width: number;
    height: number;
    sums: readonly number[];
    alpha: number;
  }[] = [];
  if (count * DUPLICATE_MATCH_TOLERANCE * 3 >= 16 * 765) {
    const entries = (width + 1) * (height + 1) * 3;
    const bitmapBytes = variances[3]! > 0 ? Math.ceil(offsets / 8) : 0;
    if (
      entries * 4 + bitmapBytes > 100_000_000 ||
      width * height * 255 >= 2 ** 32
    )
      fail("budget-exceeded", "重复匹配积分表超过100MB预算（含筛选bitset）");
    integral = new Uint32Array(entries);
    if (bitmapBytes) screened = new Uint8Array(bitmapBytes);
    for (let y = 0; y < height; y++) {
      const row = [0, 0, 0];
      for (let x = 0; x < width; x++)
        for (let c = 0; c < 3; c++) {
          row[c] = row[c]! + rgba[(y * width + x) * 4 + c]!;
          integral[(y + 1) * stride + (x + 1) * 3 + c] =
            integral[y * stride + (x + 1) * 3 + c]! + row[c]!;
        }
    }
    for (let gy = 0; gy < 4; gy++)
      for (let gx = 0; gx < 4; gx++) {
        const x = Math.floor((gx * bw) / 4),
          y = Math.floor((gy * bh) / 4);
        const w = Math.floor(((gx + 1) * bw) / 4) - x,
          h = Math.floor(((gy + 1) * bh) / 4) - y;
        blocks.push({
          x,
          y,
          width: w,
          height: h,
          sums: [0, 1, 2].map((c) => integralSum(x0 + x, y0 + y, w, h, c)),
          alpha:
            rgba[
              ((y0 + y + Math.floor(h / 2)) * width +
                x0 +
                x +
                Math.floor(w / 2)) *
                4 +
                3
            ]!,
        });
      }
  }

  const tolerance = DUPLICATE_MATCH_TOLERANCE * count * 3; // 每×3通道累计容差
  let screenWork = 0;
  const chargeScreen = () => {
    screenWork += 1;
    if (screenWork > DUPLICATE_WORK_BUDGET)
      fail(
        "budget-exceeded",
        "筛选工作量超过预算 " +
          DUPLICATE_WORK_BUDGET +
          "；请缩小图片或调整源选区后重试。",
      );
  };
  const passesInitialScreen = (cx: number, cy: number): boolean => {
    let error = 0;
    if (integral) {
      for (const part of blocks) {
        chargeScreen();
        const q =
          ((cy + part.y + Math.floor(part.height / 2)) * width +
            cx +
            part.x +
            Math.floor(part.width / 2)) *
          4;
        if (rgba[q + 3] !== part.alpha) return false;
        for (let c = 0; c < 3; c++)
          error += Math.abs(
            part.sums[c]! -
              integralSum(cx + part.x, cy + part.y, part.width, part.height, c),
          );
        if (error > tolerance) return false;
      }
    } else {
      for (let i = 0; i < screenSamples.length; i++) {
        chargeScreen();
        const sample = screenSamples[i]!,
          packed = screenCache[i]!;
        const other = screenAt(sample.lx, sample.ly, cx, cy);
        if (packed >>> 24 !== other >>> 24) return false;
        error +=
          Math.abs((packed & 255) - (other & 255)) +
          Math.abs(((packed >> 8) & 255) - ((other >> 8) & 255)) +
          Math.abs(((packed >> 16) & 255) - ((other >> 16) & 255));
        if (error > tolerance) return false;
      }
    }
    return true;
  };
  const alphaStride = width + 1;
  const alphaSum = (x: number, y: number, w: number, h: number): number => {
    const table = integral!;
    return (
      table[(y + h) * alphaStride + x + w]! -
      table[y * alphaStride + x + w]! -
      table[(y + h) * alphaStride + x]! +
      table[y * alphaStride + x]!
    );
  };
  if (screened) {
    for (let cy = 0; cy < offsetsY; cy++)
      for (let cx = 0; cx < offsetsX; cx++) {
        if (
          rectsOverlap({ x: cx, y: cy, width: bw, height: bh }, block) ||
          !passesInitialScreen(cx, cy)
        )
          continue;
        const index = cy * offsetsX + cx;
        screened[index >> 3] = screened[index >> 3]! | (1 << (index & 7));
      }
    // RGB 分块和已缓存，且窗口筛选已完成；此后不再读取 RGB 积分布局。
    const table = integral!;
    table.fill(0, 0, alphaStride);
    for (let y = 0; y < height; y++) {
      let row = 0;
      table[(y + 1) * alphaStride] = 0;
      for (let x = 0; x < width; x++) {
        row += rgba[(y * width + x) * 4 + 3]!;
        table[(y + 1) * alphaStride + x + 1] =
          table[y * alphaStride + x + 1]! + row;
      }
    }
  }
  const alphaSums = screened
    ? blocks.map((part) =>
        alphaSum(x0 + part.x, y0 + part.y, part.width, part.height),
      )
    : [];
  const matches: DuplicateCandidate[] = [];
  let preciseWork = 0;

  for (let dy = 0; dy < offsetsY; dy += 1) {
    for (let dx = 0; dx < offsetsX; dx += 1) {
      const cx = dx;
      const cy = dy;
      const candidate: DuplicateRect = {
        x: cx,
        y: cy,
        width: bw,
        height: bh,
      };
      if (rectsOverlap(candidate, block)) continue;
      if (screened) {
        const index = cy * offsetsX + cx;
        if ((screened[index >> 3]! & (1 << (index & 7))) === 0) continue;
        let sameAlpha = true;
        for (let i = 0; i < blocks.length; i++) {
          chargeScreen();
          const part = blocks[i]!;
          if (
            alphaSum(cx + part.x, cy + part.y, part.width, part.height) !==
            alphaSums[i]
          ) {
            sameAlpha = false;
            break;
          }
        }
        if (!sameAlpha) continue;
      } else if (!passesInitialScreen(cx, cy)) continue;
      // 阶段 2：逐像素精比（RGB 容差、alpha 必须相同），累计容差早退；总量受精比预算约束。
      let error = 0;
      compare: for (let y = 0; y < bh; y += 1) {
        for (let x = 0; x < bw; x += 1) {
          const p = ((y0 + y) * width + (x0 + x)) * 4;
          const q = ((cy + y) * width + (cx + x)) * 4;
          error +=
            Math.abs((rgba[p] ?? 0) - (rgba[q] ?? 0)) +
            Math.abs((rgba[p + 1] ?? 0) - (rgba[q + 1] ?? 0)) +
            Math.abs((rgba[p + 2] ?? 0) - (rgba[q + 2] ?? 0));
          preciseWork += 1;
          if (preciseWork > DUPLICATE_PRECISE_BUDGET) {
            fail(
              "budget-exceeded",
              `相似位置过多，精比超出预算 ${DUPLICATE_PRECISE_BUDGET}；请缩小框选范围后重试。`,
            );
          }
          if (rgba[p + 3] !== rgba[q + 3]) error = tolerance + 1;
          if (error > tolerance) break compare;
        }
      }
      if (error > tolerance) continue;
      if (matches.length >= DUPLICATE_MAX_MATCH_WINDOWS) {
        fail(
          "budget-exceeded",
          `合格匹配窗口超过上限 ${DUPLICATE_MAX_MATCH_WINDOWS}；请缩小图片或调整源选区后重试。`,
        );
      }
      matches.push({ ...candidate, error: error / (count * 3) });
    }
  }
  // 完整扫描后先排序再去除重叠，避免重叠窗口占满最终候选名额。
  matches.sort((a, b) => a.error - b.error || a.y - b.y || a.x - b.x);
  const kept: DuplicateCandidate[] = [];
  for (const candidate of matches) {
    if (kept.some((existing) => rectsOverlap(existing, candidate))) continue;
    kept.push(candidate);
    if (kept.length >= DUPLICATE_MAX_CANDIDATES) break;
  }
  if (kept.length === 0) {
    fail("no-duplicates", "未找到所选内容的重复位置；可调整框选范围后重试。");
  }
  return kept;
}
