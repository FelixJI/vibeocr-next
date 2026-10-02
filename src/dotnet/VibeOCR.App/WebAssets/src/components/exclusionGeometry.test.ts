import { describe, expect, it } from "vitest";

import {
  exclusionCoversOutput,
  exclusionNaturalRects,
  exclusionNormalizedRects,
  exclusionOutputRects,
  rectsCoverArea,
  rectIntersectsBox,
  rotateEditorState,
  type EditorState,
  type Mark,
  type Rect,
} from "./annotationGeometry";

function fakeImage(width: number, height: number) {
  const image = new Image();
  Object.defineProperties(image, {
    naturalWidth: { value: width },
    naturalHeight: { value: height },
  });
  return image;
}

function exclusionMark(start: Mark["start"], end: Mark["end"]): Mark {
  return { tool: "exclude", start, end };
}

const IMAGE = fakeImage(1600, 900);
const DISPLAY = { width: 900, height: 600 };

describe("exclusion natural rects", () => {
  it("maps display-space exclusion marks to snapped outward natural rects", () => {
    // 1600×900 在 900×600 画布内等比居中：scale=0.5625，
    // 显示 (100,100)-(300,200) 映射到自然 (177.78,94.44)-(533.33,272.22)，
    // 外扩取整后为 (177,94)-(534,273)。
    const state: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 100, y: 100 }, { x: 300, y: 200 })],
    };
    expect(exclusionNaturalRects(IMAGE, state, DISPLAY)).toEqual([
      { x: 177, y: 94, width: 357, height: 179 },
    ]);
  });

  it("clamps out-of-bounds drafts and drops empty rects", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [
        // 完全越界的草稿：取整并裁剪后为空，不产生掩膜。
        exclusionMark({ x: -500, y: -500 }, { x: -100, y: -100 }),
        // 部分越界：裁剪到画布内。
        exclusionMark({ x: -100, y: -50 }, { x: 100, y: 50 }),
      ],
    };
    expect(exclusionNaturalRects(IMAGE, state, DISPLAY)).toEqual([
      // 自然空间 (-177.78,-172.22)-(177.78,5.56) 外扩取整后裁剪到画布。
      { x: 0, y: 0, width: 178, height: 6 },
    ]);
  });

  it("keeps exclusion geometry consistent through 90° rotation", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 100, y: 100 }, { x: 300, y: 200 })],
    };
    const rotated = rotateEditorState(state, IMAGE, DISPLAY, 90);
    expect(rotated.rotation).toBe(90);
    const rects = exclusionNaturalRects(IMAGE, rotated, DISPLAY);
    // 旋转 90° 后自然输出为 900×1600；显示空间 357×179 的矩形映射为
    // 约 179×357（取整误差 ≤2px）。
    expect(rects).toHaveLength(1);
    expect(Math.abs(rects[0]!.width - 179)).toBeLessThanOrEqual(2);
    expect(Math.abs(rects[0]!.height - 357)).toBeLessThanOrEqual(2);
  });
});

describe("exclusion output rects", () => {
  it("intersects with crop and scales into the resized output space", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 0, y: 0 }, { x: 900, y: 600 })],
      crop: { start: { x: 0, y: 0 }, end: { x: 450, y: 300 } },
      resize: { width: 100, height: 100 },
    };
    // 裁剪映射到自然空间为左半 800×450；全图屏蔽矩形与之相交后
    // 等比缩放到 100×100 输出。
    const rects = exclusionOutputRects(IMAGE, state, DISPLAY);
    expect(rects).toEqual([{ x: 0, y: 0, width: 100, height: 100 }]);
    const normalized = exclusionNormalizedRects(IMAGE, state, DISPLAY);
    expect(normalized).toEqual([{ x: 0, y: 0, width: 1000, height: 1000 }]);
  });

  it("returns an empty list when the exclusion lies outside the crop", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 600, y: 400 }, { x: 880, y: 560 })],
      crop: { start: { x: 0, y: 0 }, end: { x: 450, y: 300 } },
    };
    expect(exclusionOutputRects(IMAGE, state, DISPLAY)).toEqual([]);
  });
});

describe("full-image exclusion coverage", () => {
  it("detects exact coverage by a union of overlapping rects", () => {
    const area: Rect = { x: 0, y: 0, width: 800, height: 450 };
    expect(
      rectsCoverArea(
        [
          { x: 0, y: 0, width: 500, height: 450 },
          { x: 400, y: 0, width: 400, height: 450 },
        ],
        area,
      ),
    ).toBe(true);
    // 遗留 1px 竖向缝隙：未完整覆盖。
    expect(
      rectsCoverArea(
        [
          { x: 0, y: 0, width: 400, height: 450 },
          { x: 401, y: 0, width: 399, height: 450 },
        ],
        area,
      ),
    ).toBe(false);
  });

  it("refuses recognition only when the cropped output is fully covered", () => {
    const fullCanvas: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 0, y: 0 }, { x: 900, y: 600 })],
    };
    expect(exclusionCoversOutput(IMAGE, fullCanvas, DISPLAY)).toBe(true);

    const partial: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 0, y: 0 }, { x: 450, y: 300 })],
    };
    expect(exclusionCoversOutput(IMAGE, partial, DISPLAY)).toBe(false);

    // 两个交叠矩形并集覆盖全图。
    const union: EditorState = {
      rotation: 0,
      marks: [
        exclusionMark({ x: 0, y: 0 }, { x: 600, y: 600 }),
        exclusionMark({ x: 300, y: 0 }, { x: 900, y: 600 }),
      ],
    };
    expect(exclusionCoversOutput(IMAGE, union, DISPLAY)).toBe(true);

    // 裁剪区域内全覆盖（排除区可延伸出裁剪框）也应拒绝识别。
    const cropped: EditorState = {
      rotation: 0,
      marks: [exclusionMark({ x: 0, y: 0 }, { x: 900, y: 600 })],
      crop: { start: { x: 100, y: 50 }, end: { x: 400, y: 250 } },
    };
    expect(exclusionCoversOutput(IMAGE, cropped, DISPLAY)).toBe(true);

    // 旋转后同样判定。
    const rotated = rotateEditorState(fullCanvas, IMAGE, DISPLAY, 90);
    expect(exclusionCoversOutput(IMAGE, rotated, DISPLAY)).toBe(true);
  });
});

describe("line boundary intersection policy", () => {
  const rect: Rect = { x: 100, y: 100, width: 200, height: 50 };

  it("drops lines with positive-area overlap only", () => {
    expect(
      rectIntersectsBox(rect, { x1: 150, y1: 110, x2: 250, y2: 130 }),
    ).toBe(true);
    // 仅贴边（共享边界但无正面积重叠）不算相交。
    expect(
      rectIntersectsBox(rect, { x1: 300, y1: 110, x2: 400, y2: 130 }),
    ).toBe(false);
    expect(rectIntersectsBox(rect, { x1: 0, y1: 150, x2: 250, y2: 200 })).toBe(
      false,
    );
    expect(
      rectIntersectsBox(rect, { x1: 350, y1: 200, x2: 500, y2: 300 }),
    ).toBe(false);
  });
});
