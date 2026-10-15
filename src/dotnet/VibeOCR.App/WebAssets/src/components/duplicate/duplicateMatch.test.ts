import { describe, expect, it } from "vitest";

import {
  DuplicateError,
  DUPLICATE_MAX_CANDIDATES,
  findDuplicateRegions,
  type DuplicateRect,
} from "./duplicateMatch";

function paint(
  width: number,
  height: number,
  paintPixel: (
    x: number,
    y: number,
  ) => readonly [number, number, number, number],
): Uint8ClampedArray {
  const rgba = new Uint8ClampedArray(width * height * 4);
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const [r, g, b, a] = paintPixel(x, y);
      const p = (y * width + x) * 4;
      rgba[p] = r;
      rgba[p + 1] = g;
      rgba[p + 2] = b;
      rgba[p + 3] = a;
    }
  }
  return rgba;
}

/** 纹理图案：位置相关的伪随机灰度（方差充足），可精确复制。 */
function textureAt(
  x: number,
  y: number,
): readonly [number, number, number, number] {
  const value =
    (Math.imul(x + 1, 1597334677) ^ Math.imul(y + 7, 2654435761)) >>> 24;
  return [value, (value * 3) % 256, (value * 7) % 256, 255];
}

function failureCodeOf(run: () => void): string {
  try {
    run();
  } catch (error) {
    if (error instanceof DuplicateError) return error.code;
    throw error;
  }
  throw new Error("期望 findDuplicateRegions 抛出 DuplicateError");
}

describe("findDuplicateRegions", () => {
  it("R10 suppresses overlapping windows before limiting independent copies", () => {
    const rgba = paint(128, 48, (x, y) => {
      if (
        y >= 4 &&
        y < 12 &&
        ((x >= 0 && x < 8) || (x >= 24 && x < 48) || (x >= 80 && x < 88))
      ) {
        const value = ((x + y) % 2) * 255;
        return [value, value, value, 255];
      }
      return [100, 100, 100, 255];
    });
    const matches = findDuplicateRegions(rgba, 128, 48, {
      x: 0,
      y: 4,
      width: 8,
      height: 8,
    });
    expect(matches.map(({ x, y, error }) => ({ x, y, error }))).toEqual(
      [24, 32, 40, 80].map((x) => ({ x, y: 4, error: 0 })),
    );
  });

  it("R10 ranks a later better match first and resolves equal errors in scan order", () => {
    const rgba = paint(128, 48, (x, y) => {
      if (y >= 4 && y < 12) {
        const value = ((x + y) % 2) * 255;
        if ((x >= 0 && x < 8) || (x >= 80 && x < 88))
          return [value, value, value, 255];
        if (x >= 24 && x < 48) {
          const biased = value === 0 ? 2 : 253;
          return [biased, biased, biased, 255];
        }
      }
      return [100, 100, 100, 255];
    });
    const source = { x: 0, y: 4, width: 8, height: 8 };
    const matches = findDuplicateRegions(rgba, 128, 48, source);
    expect(matches.map(({ x, y, error }) => ({ x, y, error }))).toEqual([
      { x: 80, y: 4, error: 0 },
      ...[24, 32, 40].map((x) => ({ x, y: 4, error: 2 })),
    ]);
    expect(findDuplicateRegions(rgba, 128, 48, source)).toEqual(matches);
  });

  it("R10 explicitly rejects excessive match windows instead of returning a truncated search", () => {
    const rgba = paint(256, 64, (x, y) => {
      const value = ((x + y) % 2) * 255;
      return [value, value, value, 255];
    });
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(rgba, 256, 64, {
          x: 0,
          y: 0,
          width: 8,
          height: 8,
        }),
      ),
    ).toBe("budget-exceeded");
  });

  it("R8 retains a valid copy whose entire error is concentrated at screening samples", () => {
    const source = { x: 8, y: 8, width: 32, height: 32 };
    const coordinates = [6, 12, 19, 25];
    const isSample = (x: number, y: number) =>
      coordinates.includes(x) && coordinates.includes(y);
    const rgba = paint(128, 96, (x, y) => {
      if (x >= 8 && x < 40 && y >= 8 && y < 40)
        return isSample(x - 8, y - 8) ? [0, 0, 0, 255] : textureAt(x, y);
      if (x >= 75 && x < 107 && y >= 53 && y < 85)
        return isSample(x - 75, y - 53)
          ? [255, 255, 255, 255]
          : textureAt(x - 75 + 8, y - 53 + 8);
      return textureAt(x, y);
    });
    const match = findDuplicateRegions(rgba, 128, 96, source).find(
      (item) => item.x === 75 && item.y === 53,
    );
    expect(match).toBeDefined();
    expect(match!.error).toBeCloseTo((255 * 16) / 1024);
  });

  it("finds an exact translated copy of a textured block", () => {
    const width = 96;
    const height = 64;
    const block: DuplicateRect = { x: 10, y: 10, width: 12, height: 9 };
    // 把源块像素复制到另一个平移位置（纹理背景下仍可识别）。
    const rgba = paint(width, height, (x, y) => {
      if (x >= 50 && x < 62 && y >= 30 && y < 39) {
        return textureAt(x - 50 + block.x, y - 30 + block.y);
      }
      return textureAt(x, y);
    });

    const candidates = findDuplicateRegions(rgba, width, height, block);

    expect(candidates.length).toBeGreaterThanOrEqual(1);
    const exact = candidates.find(
      (candidate) => candidate.x === 50 && candidate.y === 30,
    );
    expect(exact).toBeDefined();
    expect(exact!.width).toBe(12);
    expect(exact!.height).toBe(9);
    expect(exact!.error).toBeLessThanOrEqual(6);
  });

  it("finds copies above and left of the source without scanning outside the image", () => {
    const source = { x: 60, y: 40, width: 12, height: 9 };
    const rgba = paint(96, 64, (x, y) =>
      x >= 3 && x < 15 && y >= 4 && y < 13
        ? textureAt(x - 3 + source.x, y - 4 + source.y)
        : textureAt(x, y),
    );
    expect(findDuplicateRegions(rgba, 96, 64, source)).toContainEqual({
      x: 3,
      y: 4,
      width: 12,
      height: 9,
      error: 0,
    });
  });

  it("rejects low-texture (near flat) source blocks", () => {
    const width = 48;
    const height = 32;
    const rgba = paint(width, height, () => [200, 200, 201, 255]);
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(rgba, width, height, {
          x: 8,
          y: 8,
          width: 12,
          height: 12,
        }),
      ),
    ).toBe("low-texture");
  });

  it("reports no-duplicates when nothing matches", () => {
    const width = 48;
    const height = 32;
    const rgba = paint(width, height, textureAt);
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(rgba, width, height, {
          x: 4,
          y: 4,
          width: 10,
          height: 10,
        }),
      ),
    ).toBe("no-duplicates");
  });

  it("rejects oversized images and source blocks by budget", () => {
    const huge = 2829;
    const big = new Uint8ClampedArray(huge * huge * 4);
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(big, huge, huge, {
          x: 10,
          y: 10,
          width: 16,
          height: 16,
        }),
      ),
    ).toBe("budget-exceeded");
    const rgba = paint(600, 600, textureAt);
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(rgba, 600, 600, {
          x: 0,
          y: 0,
          width: 520,
          height: 520,
        }),
      ),
    ).toBe("budget-exceeded");
    const small = paint(600, 400, textureAt);
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(small, 600, 400, {
          x: 5,
          y: 5,
          width: 0,
          height: 8,
        }),
      ),
    ).toBe("invalid-rect");
  });

  it("finds non-grid-aligned duplicates that a strided search would miss", () => {
    // root 核实案例：512×512 纹理底图，32×32 源块 (20,20)，完全一致
    // 副本 (75,73)。旧实现确定性步长 2 永不访问奇数偏移，会错误地报
    // no-duplicates；新两阶段搜索访问每个整数偏移，必须找到。
    const width = 512;
    const height = 512;
    const rgba = paint(width, height, textureAt);
    for (let y = 0; y < 32; y += 1) {
      for (let x = 0; x < 32; x += 1) {
        const p = ((73 + y) * width + (75 + x)) * 4;
        const q = ((20 + y) * width + (20 + x)) * 4;
        rgba[p] = rgba[q]!;
        rgba[p + 1] = rgba[q + 1]!;
        rgba[p + 2] = rgba[q + 2]!;
        rgba[p + 3] = 255;
      }
    }

    const candidates = findDuplicateRegions(rgba, width, height, {
      x: 20,
      y: 20,
      width: 32,
      height: 32,
    });

    const exact = candidates.find(
      (candidate) => candidate.x === 75 && candidate.y === 73,
    );
    expect(exact).toBeDefined();
    expect(exact!.error).toBeLessThanOrEqual(6);
  });

  it("explicitly rejects when full-offset screening would exceed the work budget", () => {
    // 6.76MP 图（≤ 8MP 上限）+ 32×32 源块：偏移 6.6M × 16 采样超
    // 筛选预算。旧实现静默步进稀疏网格（漏扫）；新实现显式拒绝。
    const width = 2600;
    const height = 2600;
    const rgba = paint(width, height, textureAt);
    expect(
      failureCodeOf(() =>
        findDuplicateRegions(rgba, width, height, {
          x: 20,
          y: 20,
          width: 32,
          height: 32,
        }),
      ),
    ).toBe("budget-exceeded");
  });

  it("bounds candidates and keeps them non-overlapping and disjoint from source", () => {
    const width = 200;
    const height = 64;
    const block: DuplicateRect = { x: 4, y: 4, width: 8, height: 8 };
    // 在多个位置平铺同一纹理块：候选之间不重叠、不与源重叠，数量有上限。
    const copies: readonly { x: number; y: number }[] = Array.from(
      { length: 20 },
      (_, i) => ({ x: 16 + i * 9, y: 8 + (i % 3) * 12 }),
    );
    const rgba = paint(width, height, (x, y) => {
      for (const copy of copies) {
        if (x >= copy.x && x < copy.x + 8 && y >= copy.y && y < copy.y + 8) {
          return textureAt(x - copy.x + block.x, y - copy.y + block.y);
        }
      }
      return textureAt(x, y);
    });

    const candidates = findDuplicateRegions(rgba, width, height, block);

    expect(candidates.length).toBeLessThanOrEqual(DUPLICATE_MAX_CANDIDATES);
    const overlap = (a: DuplicateRect, b: DuplicateRect) =>
      a.x < b.x + b.width &&
      a.x + a.width > b.x &&
      a.y < b.y + b.height &&
      a.y + a.height > b.y;
    for (let i = 0; i < candidates.length; i += 1) {
      expect(overlap(candidates[i]!, block)).toBe(false);
      for (let j = i + 1; j < candidates.length; j += 1) {
        expect(overlap(candidates[i]!, candidates[j]!)).toBe(false);
      }
      // 误差单调不减（按相似度排序）。
      if (i > 0) {
        expect(candidates[i]!.error).toBeGreaterThanOrEqual(
          candidates[i - 1]!.error,
        );
      }
    }
  });
});
