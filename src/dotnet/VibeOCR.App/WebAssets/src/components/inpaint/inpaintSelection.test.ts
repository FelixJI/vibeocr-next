import { describe, expect, it } from "vitest";

import {
  naturalRectToDisplay,
  preflightSelection,
  selectionToNaturalRect,
} from "./inpaintSelection";

const DISPLAY = { width: 900, height: 600 };

function imageOf(width: number, height: number) {
  return { naturalWidth: width, naturalHeight: height };
}

describe("selectionToNaturalRect", () => {
  it("maps a display drag to the unrotated natural pixel grid (snaps outward)", () => {
    const image = imageOf(80, 40);
    const rect = selectionToNaturalRect(
      { x: 100, y: 100 },
      { x: 200, y: 180 },
      image,
      0,
      DISPLAY,
    );
    // scale = min(900/80, 600/40) = 11.25，中心 (450,300)。
    expect(rect).toEqual({ x: 8, y: 2, width: 10, height: 8 });
  });

  it("maps through a 90° view rotation to the same unrotated image space", () => {
    const image = imageOf(80, 40);
    const rect = selectionToNaturalRect(
      { x: 400, y: 200 },
      { x: 500, y: 400 },
      image,
      90,
      DISPLAY,
    );
    // scale = min(900/40, 600/80) = 7.5；显示坐标经 -90° 反旋转回原图空间。
    expect(rect).toEqual({ x: 26, y: 13, width: 28, height: 14 });
  });

  it("clamps partially out-of-image drags instead of failing", () => {
    const image = imageOf(80, 40);
    const rect = selectionToNaturalRect(
      { x: -500, y: -500 },
      { x: 150, y: 150 },
      image,
      0,
      DISPLAY,
    );
    expect(rect).toEqual({ x: 0, y: 0, width: 14, height: 7 });
  });

  it("round-trips natural rect through display projection (± 1px snap)", () => {
    const image = imageOf(80, 40);
    const rect = { x: 26, y: 13, width: 28, height: 14 };
    for (const rotation of [0, 90, 180, 270]) {
      const display = naturalRectToDisplay(rect, image, rotation, DISPLAY);
      const back = selectionToNaturalRect(
        display.start,
        display.end,
        image,
        rotation,
        DISPLAY,
      );
      // 外扩取整保证往返后仍完整覆盖原矩形，且每边至多 1px 额外范围。
      expect(back.x).toBeLessThanOrEqual(rect.x);
      expect(back.y).toBeLessThanOrEqual(rect.y);
      expect(back.x + back.width).toBeGreaterThanOrEqual(rect.x + rect.width);
      expect(back.y + back.height).toBeGreaterThanOrEqual(rect.y + rect.height);
      expect(back.width - rect.width).toBeLessThanOrEqual(2);
      expect(back.height - rect.height).toBeLessThanOrEqual(2);
    }
  });
});

describe("preflightSelection", () => {
  it("rejects empty selections, whole-image masks, and budget overruns", () => {
    const image = imageOf(80, 40);
    expect(
      preflightSelection({ x: 0, y: 0, width: 0, height: 8 }, image),
    ).toMatchObject({ ok: false, code: "empty-mask" });
    expect(
      preflightSelection({ x: 0, y: 0, width: 80, height: 40 }, image),
    ).toMatchObject({ ok: false, code: "mask-covers-image" });
    expect(
      preflightSelection({ x: 0, y: 0, width: 1001, height: 1001 }, image),
    ).toMatchObject({ ok: false, code: "budget-exceeded" });
    expect(
      preflightSelection(
        { x: 0, y: 0, width: 4, height: 4 },
        imageOf(2829, 2829),
      ),
    ).toMatchObject({ ok: false, code: "budget-exceeded" });
  });

  it("accepts a normal selection and returns the rect unchanged", () => {
    const rect = { x: 8, y: 2, width: 10, height: 8 };
    expect(preflightSelection(rect, imageOf(80, 40))).toEqual({
      ok: true,
      rect,
    });
  });
});
