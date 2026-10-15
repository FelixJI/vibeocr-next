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
