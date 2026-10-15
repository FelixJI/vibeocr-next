import { describe, expect, it } from "vitest";

import {
  inpaintMask,
  inpaintRectangle,
  InpaintError,
  type InpaintRect,
} from "./inpaint";

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

describe("inpaintMask", () => {
  it("R1 fills disconnected mask islands from known pixels inside the ROI", () => {
    const roi = { x: 2, y: 2, width: 8, height: 8 };
    const mask = new Uint8Array(64);
    for (const index of [0, 27, 63]) mask[index] = 1;
    const rgba = paintImage(12, 12, (x, y) =>
      mask[(y - 2) * 8 + x - 2] === 1 && x >= 2 && x < 10 && y >= 2 && y < 10
        ? [240, 30, 220, 128]
        : [12, 160, 90, 255],
    );
    const out = inpaintMask(rgba, 12, 12, roi, mask);
    for (let y = 0; y < 12; y++)
      for (let x = 0; x < 12; x++) {
        const selected =
          x >= 2 &&
          x < 10 &&
          y >= 2 &&
          y < 10 &&
          mask[(y - 2) * 8 + x - 2] === 1;
        const pixel = (y * 12 + x) * 4;
        if (selected) {
          expect(Array.from(out.slice(pixel, pixel + 4))).toEqual([
            12, 160, 90, 128,
          ]);
        } else
          expect(Array.from(out.slice(pixel, pixel + 4))).toEqual(
            Array.from(rgba.slice(pixel, pixel + 4)),
          );
      }
  });

  it("R7 fills a full-image ROI using an unmasked known hole", () => {
    const width = 64,
      height = 48;
    const hole = (x: number, y: number) =>
      x >= 27 && x < 37 && y >= 19 && y < 29;
    const rgba = paintImage(width, height, (x, y) =>
      hole(x, y) ? [12, 160, 90, 255] : [240, 30, 220, 255],
    );
    const mask = new Uint8Array(width * height).fill(1);
    for (let y = 19; y < 29; y++)
      for (let x = 27; x < 37; x++) mask[y * width + x] = 0;
    const out = inpaintMask(
      rgba,
      width,
      height,
      { x: 0, y: 0, width, height },
      mask,
    );
    expect(Array.from(out)).toEqual(
      Array.from(paintImage(width, height, () => [12, 160, 90, 255])),
    );
  });

  it("fills only masked pixels: holes and outside bytes stay identical", () => {
    const width = 32;
    const height = 24;
    const inMask = (x: number, y: number) =>
      x >= 8 &&
      x < 20 &&
      y >= 6 &&
      y < 18 &&
      !(x >= 13 && x < 16 && y >= 10 && y < 13);
    const rgba = paintImage(width, height, (x, y) =>
      inMask(x, y) ? [240, 30, 220, 255] : [12, 160, 90, 255],
    );
    const original = new Uint8ClampedArray(rgba);
    const roi: InpaintRect = { x: 8, y: 6, width: 12, height: 12 };
    const mask = new Uint8Array(12 * 12);
    for (let y = 0; y < 12; y += 1) {
      for (let x = 0; x < 12; x += 1) {
        if (x >= 5 && x < 8 && y >= 4 && y < 7) continue; // 洞：保留原水印像素
        mask[y * 12 + x] = 1;
      }
    }

    const out = inpaintMask(rgba, width, height, roi, mask);

    expect(countByteDiffs(rgba, original)).toBe(0);
    for (let y = 0; y < height; y += 1) {
      for (let x = 0; x < width; x += 1) {
        for (let c = 0; c < 4; c += 1) {
          const before = original[(y * width + x) * 4 + c] ?? 0;
          const after = out[(y * width + x) * 4 + c] ?? 0;
          if (inMask(x, y) && !(x >= 13 && x < 16 && y >= 10 && y < 13)) {
            if (c === 3) expect(after).toBe(before);
          } else {
            expect(after, `pixel (${x},${y}) ch${c}`).toBe(before);
          }
        }
      }
    }
    // 掩膜区域中心恢复为背景色。
    expectRgbNear(out, width, 10, 8, [12, 160, 90], 4);
  });

  it("rejects empty masks and whole-image masks", () => {
    const width = 20;
    const height = 16;
    const rgba = paintImage(width, height, () => [10, 20, 30, 255]);
    expect(
      failureCodeOf(() =>
        inpaintMask(
          rgba,
          width,
          height,
          { x: 2, y: 2, width: 8, height: 8 },
          new Uint8Array(64),
        ),
      ),
    ).toBe("empty-mask");
    expect(
      failureCodeOf(() =>
        inpaintMask(
          rgba,
          width,
          height,
          { x: 0, y: 0, width: width, height: height },
          new Uint8Array(width * height).fill(1),
        ),
      ),
    ).toBe("mask-covers-image");
    expect(
      failureCodeOf(() =>
        inpaintMask(
          rgba,
          width,
          height,
          { x: 2, y: 2, width: 8, height: 8 },
          new Uint8Array(9),
        ),
      ),
    ).toBe("invalid-rect");
  });

  it("rejects ROI beyond the allocation budget", () => {
    const width = 1100;
    const height = 1100;
    const rgba = paintImage(width, height, () => [1, 2, 3, 255]);
    const roi: InpaintRect = { x: 0, y: 0, width: 1001, height: 1001 };
    expect(
      failureCodeOf(() =>
        inpaintMask(
          rgba,
          width,
          height,
          roi,
          new Uint8Array(1001 * 1001).fill(1),
        ),
      ),
    ).toBe("budget-exceeded");
  });

  it("rectangle wrapper keeps the legacy rect contract", () => {
    const width = 32;
    const height = 24;
    const rect: InpaintRect = { x: 8, y: 8, width: 12, height: 8 };
    const inside = (x: number, y: number) =>
      x >= rect.x &&
      x < rect.x + rect.width &&
      y >= rect.y &&
      y < rect.y + rect.height;
    const rgba = paintImage(width, height, (x, y) =>
      inside(x, y) ? [240, 30, 220, 255] : [12, 160, 90, 255],
    );
    const out = inpaintRectangle(rgba, width, height, rect);
    expectRgbNear(out, width, 14, 12, [12, 160, 90], 3);
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
});

it("alternating smoothing retains the bounded gradient pixel regression", () => {
  const rgba = paintImage(18, 18, (x, y) => [
    (x * 17 + y * 31) % 256,
    (x * 19 + y * 7) % 256,
    (x * 5 + y * 23) % 256,
    255,
  ]);
  const actual = inpaintRectangle(rgba, 18, 18, {
    x: 1,
    y: 1,
    width: 16,
    height: 16,
  });
  expect([
    channel(actual, 18, 11, 1, 0),
    channel(actual, 18, 12, 1, 1),
    channel(actual, 18, 9, 2, 0),
    channel(actual, 18, 9, 2, 1),
  ]).toEqual([174, 181, 143, 141]);
});
