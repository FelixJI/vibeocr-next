import { describe, expect, it } from "vitest";

import { InpaintError, inpaintRectangle, type InpaintRect } from "./inpaint";

type Pixel = readonly [number, number, number, number];

function paintImage(
  width: number,
  height: number,
  paint: (x: number, y: number) => Pixel,
): Uint8ClampedArray {
  const rgba = new Uint8ClampedArray(width * height * 4);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const [r, g, b, a] = paint(x, y);
      const p = (y * width + x) * 4;
      rgba[p] = r;
      rgba[p + 1] = g;
      rgba[p + 2] = b;
      rgba[p + 3] = a;
    }
  }
  return rgba;
}

function channel(
  rgba: Uint8ClampedArray,
  width: number,
  x: number,
  y: number,
  offset: number,
): number {
  return rgba[(y * width + x) * 4 + offset] ?? 0;
}

function expectRgbNear(
  rgba: Uint8ClampedArray,
  width: number,
  x: number,
  y: number,
  expected: readonly [number, number, number],
  tolerance: number,
): void {
  for (let c = 0; c < 3; c++) {
    const actual = channel(rgba, width, x, y, c);
    expect(Math.abs(actual - (expected[c] ?? 0))).toBeLessThanOrEqual(
      tolerance,
    );
  }
}

function countByteDiffs(a: Uint8ClampedArray, b: Uint8ClampedArray): number {
  if (a.length !== b.length) return Math.max(a.length, b.length);
  let diffs = 0;
  for (let i = 0; i < a.length; i++) {
    if ((a[i] ?? 0) !== (b[i] ?? 0)) diffs += 1;
  }
  return diffs;
}

interface MaskWindow {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

function scanMaskEffects(
  out: Uint8ClampedArray,
  input: Uint8ClampedArray,
  width: number,
  height: number,
  mask: MaskWindow,
): {
  outsideDiffs: number;
  insideAlphaDiffs: number;
  insideRgbChanged: number;
} {
  let outsideDiffs = 0;
  let insideAlphaDiffs = 0;
  let insideRgbChanged = 0;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const inMask = x >= mask.x0 && x < mask.x1 && y >= mask.y0 && y < mask.y1;
      for (let c = 0; c < 4; c++) {
        const before = input[(y * width + x) * 4 + c] ?? 0;
        const after = out[(y * width + x) * 4 + c] ?? 0;
        if (before === after) continue;
        if (!inMask) outsideDiffs += 1;
        else if (c === 3) insideAlphaDiffs += 1;
        else insideRgbChanged += 1;
      }
    }
  }
  return { outsideDiffs, insideAlphaDiffs, insideRgbChanged };
}

function failureCodeOf(run: () => void): string {
  try {
    run();
  } catch (error) {
    if (error instanceof InpaintError) return error.code;
    throw error;
  }
  throw new Error("期望 inpaintRectangle 抛出 InpaintError");
}

describe("inpaintRectangle", () => {
  it("restores a flat background under the mask", () => {
    const width = 32;
    const height = 24;
    const background = [12, 160, 90] as const;
    const rect: InpaintRect = { x: 8, y: 8, width: 12, height: 8 };
    const inside = (x: number, y: number) =>
      x >= rect.x &&
      x < rect.x + rect.width &&
      y >= rect.y &&
      y < rect.y + rect.height;
    const rgba = paintImage(width, height, (x, y) =>
      inside(x, y) ? [240, 30, 220, 255] : [12, 160, 90, 255],
    );
    const original = new Uint8ClampedArray(rgba);

    const out = inpaintRectangle(rgba, width, height, rect);

    // 无修补（原样拷贝）时中心仍是水印色，此断言会失败。
    expectRgbNear(out, width, 14, 12, background, 3);
    expectRgbNear(out, width, 9, 9, background, 3);
    expect(out).not.toBe(rgba);
    expect(out.buffer).not.toBe(rgba.buffer);
    expect(countByteDiffs(rgba, original)).toBe(0);
    const effects = scanMaskEffects(out, rgba, width, height, {
      x0: 8,
      y0: 8,
      x1: 20,
      y1: 16,
    });
    expect(effects.outsideDiffs).toBe(0);
    expect(effects.insideAlphaDiffs).toBe(0);
    expect(effects.insideRgbChanged).toBeGreaterThan(0);
  });

  it("fills a gradient background by local interpolation, not one constant", () => {
    const width = 48;
    const height = 10;
    const gradientRed = (x: number) => Math.round((x * 255) / 47);
    const gradientGreen = (x: number) => Math.round((x * 180) / 47);
    const inside = (x: number, y: number) =>
      x >= 16 && x < 32 && y >= 2 && y < 8;
    const rgba = paintImage(width, height, (x, y) =>
      inside(x, y)
        ? [250, 250, 250, 255]
        : [gradientRed(x), gradientGreen(x), 32, 255],
    );

    const out = inpaintRectangle(rgba, width, height, {
      x: 16,
      y: 2,
      width: 16,
      height: 6,
    });

    expectRgbNear(
      out,
      width,
      20,
      5,
      [gradientRed(20), gradientGreen(20), 32],
      18,
    );
    expectRgbNear(
      out,
      width,
      28,
      5,
      [gradientRed(28), gradientGreen(28), 32],
      18,
    );
    const left = channel(out, width, 20, 5, 0);
    const right = channel(out, width, 28, 5, 0);
    expect(right - left).toBeGreaterThanOrEqual(30);
  });

  it("keeps every outside byte and the original alpha untouched", () => {
    const width = 20;
    const height = 16;
    let state = 0x9e3779b9;
    const next = () => {
      state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
      return state;
    };
    const rgba = paintImage(width, height, (x, y) => [
      next() & 255,
      next() & 255,
      next() & 255,
      ((x * 31 + y * 17) % 257) - 1,
    ]);
    const rect: InpaintRect = { x: 5, y: 3, width: 9, height: 7 };
    const original = new Uint8ClampedArray(rgba);

    const out = inpaintRectangle(rgba, width, height, rect);

    expect(countByteDiffs(rgba, original)).toBe(0);
    const effects = scanMaskEffects(out, rgba, width, height, {
      x0: 5,
      y0: 3,
      x1: 14,
      y1: 10,
    });
    expect(effects.outsideDiffs).toBe(0);
    expect(effects.insideAlphaDiffs).toBe(0);
    expect(effects.insideRgbChanged).toBeGreaterThan(0);
    // 选区内含透明像素：alpha 原样保留，RGB 仍被重算。
    expect(channel(rgba, width, 5, 6, 3)).toBe(0);
    expect(channel(out, width, 5, 6, 3)).toBe(0);
  });

  it("rejects invalid sizes, empty or whole-image masks, and budget overruns", () => {
    const width = 32;
    const height = 24;
    const rgba = paintImage(width, height, () => [10, 20, 30, 255]);

    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, 16, 47, { x: 0, y: 0, width: 4, height: 4 }),
      ),
    ).toBe("invalid-image");
    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, 0, 24, { x: 0, y: 0, width: 4, height: 4 }),
      ),
    ).toBe("invalid-image");
    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, 32.5, 24, { x: 0, y: 0, width: 4, height: 4 }),
      ),
    ).toBe("invalid-image");
    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, width, height, {
          x: 1.5,
          y: 0,
          width: 4,
          height: 4,
        }),
      ),
    ).toBe("invalid-rect");
    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, width, height, {
          x: 40,
          y: 0,
          width: 4,
          height: 4,
        }),
      ),
    ).toBe("empty-mask");
    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, width, height, {
          x: 0,
          y: 0,
          width: 0,
          height: 4,
        }),
      ),
    ).toBe("empty-mask");
    expect(
      failureCodeOf(() =>
        inpaintRectangle(rgba, width, height, {
          x: 0,
          y: 0,
          width: 32,
          height: 24,
        }),
      ),
    ).toBe("mask-covers-image");

    const bigWidth = 1100;
    const bigHeight = 1100;
    const big = paintImage(bigWidth, bigHeight, () => [1, 2, 3, 255]);
    expect(
      failureCodeOf(() =>
        inpaintRectangle(big, bigWidth, bigHeight, {
          x: 0,
          y: 0,
          width: 1001,
          height: 1001,
        }),
      ),
    ).toBe("budget-exceeded");

    const hugeWidth = 2829;
    const hugeHeight = 2829;
    const huge = new Uint8ClampedArray(hugeWidth * hugeHeight * 4);
    expect(
      failureCodeOf(() =>
        inpaintRectangle(huge, hugeWidth, hugeHeight, {
          x: 0,
          y: 0,
          width: 4,
          height: 4,
        }),
      ),
    ).toBe("budget-exceeded");
  });

  it("clamps partially out-of-bounds masks and still fills the effective area", () => {
    const width = 32;
    const height = 24;
    const inTopLeft = (x: number, y: number) => x < 16 && y < 7;
    const rgba = paintImage(width, height, (x, y) =>
      inTopLeft(x, y) ? [250, 60, 60, 255] : [100, 150, 200, 255],
    );
    const original = new Uint8ClampedArray(rgba);

    const out = inpaintRectangle(rgba, width, height, {
      x: -4,
      y: -3,
      width: 20,
      height: 10,
    });

    expectRgbNear(out, width, 3, 3, [100, 150, 200], 3);
    expectRgbNear(out, width, 15, 6, [100, 150, 200], 3);
    const effects = scanMaskEffects(out, original, width, height, {
      x0: 0,
      y0: 0,
      x1: 16,
      y1: 7,
    });
    expect(effects.outsideDiffs).toBe(0);
    expect(effects.insideAlphaDiffs).toBe(0);
    expect(effects.insideRgbChanged).toBeGreaterThan(0);

    // 右下角溢出收敛为 1×1 有效选区：仍成功且只改该像素 RGB。
    const cornerInput = new Uint8ClampedArray(rgba);
    cornerInput.set([250, 60, 60, 255], (23 * width + 31) * 4);
    const cornerOriginal = new Uint8ClampedArray(cornerInput);
    const corner = inpaintRectangle(cornerInput, width, height, {
      x: 31,
      y: 23,
      width: 5,
      height: 5,
    });
    expectRgbNear(corner, width, 31, 23, [100, 150, 200], 3);
    const cornerEffects = scanMaskEffects(
      corner,
      cornerOriginal,
      width,
      height,
      {
        x0: 31,
        y0: 23,
        x1: 32,
        y1: 24,
      },
    );
    expect(cornerEffects.outsideDiffs).toBe(0);
    expect(cornerEffects.insideAlphaDiffs).toBe(0);
    expect(cornerEffects.insideRgbChanged).toBeGreaterThan(0);
    expect(cornerInput).toEqual(cornerOriginal);
  });
});
