import { describe, expect, it } from "vitest";

import {
  isLineBindingActive,
  layoutTextBox,
  toImageTextLines,
} from "./imageTextLayerGeometry";

function fakeImage(width: number, height: number): HTMLImageElement {
  const image = new Image();
  Object.defineProperties(image, {
    naturalWidth: { value: width },
    naturalHeight: { value: height },
  });
  return image;
}

describe("toImageTextLines", () => {
  it("keeps valid line blocks and drops malformed host entries", () => {
    const lines = toImageTextLines([
      { text: "你好世界", bbox: [10, 20, 500, 60], order: 1 },
      { text: "", bbox: [0, 0, 100, 100] },
      { text: "短框", bbox: [0, 0, 100] },
      { text: "非数字", bbox: [0, 0, "100", 100] },
      { text: "bbox缺失" },
      "not-an-object",
      null,
    ]);

    expect(lines).toHaveLength(1);
    expect(lines[0]?.text).toBe("你好世界");
  });

  it("rejects boxes outside [0,1000] or degenerate/reversed extents", () => {
    const lines = toImageTextLines([
      { text: "超上界", bbox: [0, 0, 100, 1001] },
      { text: "超右界", bbox: [0, 0, 1001, 100] },
      { text: "负值", bbox: [-10, 0, 100, 100] },
      { text: "零宽", bbox: [100, 100, 100, 200] },
      { text: "零高", bbox: [100, 100, 200, 100] },
      { text: "反向", bbox: [500, 60, 10, 20] },
      { text: "有效", bbox: [10, 20, 500, 60] },
    ]);

    expect(lines.map((line) => line.text)).toEqual(["有效"]);
  });

  it("sorts by engine order when every line carries one", () => {
    const lines = toImageTextLines([
      { text: "第二行", bbox: [0, 200, 400, 260], order: 2 },
      { text: "第一行", bbox: [0, 0, 400, 60], order: 1 },
      { text: "页眉", bbox: [0, 500, 400, 560], order: 0 },
    ]);

    expect(lines.map((line) => line.text)).toEqual([
      "页眉",
      "第一行",
      "第二行",
    ]);
  });

  it("falls back to top-left for the whole list when any order is missing", () => {
    const lines = toImageTextLines([
      { text: "底部带序", bbox: [0, 400, 400, 460], order: 0 },
      { text: "顶部无序", bbox: [0, 0, 400, 60] },
      { text: "右上无序", bbox: [600, 0, 900, 60] },
    ]);

    // 混合有/无 order 时按 order 排序不可传递：整表统一 top→left。
    expect(lines.map((line) => line.text)).toEqual([
      "顶部无序",
      "右上无序",
      "底部带序",
    ]);
  });

  it("treats negative or non-finite order as absent", () => {
    const lines = toImageTextLines([
      { text: "后", bbox: [0, 100, 100, 150], order: -1 },
      { text: "前", bbox: [0, 0, 100, 50], order: Number.NaN },
    ]);

    expect(lines.map((line) => line.text)).toEqual(["前", "后"]);
    expect(lines[0]?.order).toBeUndefined();
  });
});

describe("isLineBindingActive", () => {
  const binding = { sessionId: "session-a", revision: 3 };

  it("is active only when session id and revision both match", () => {
    expect(isLineBindingActive(binding, binding)).toBe(true);
    expect(
      isLineBindingActive(binding, { sessionId: "session-a", revision: 4 }),
    ).toBe(false);
    expect(
      isLineBindingActive(binding, { sessionId: "session-b", revision: 3 }),
    ).toBe(false);
    expect(isLineBindingActive(undefined, binding)).toBe(false);
    expect(isLineBindingActive(binding, undefined)).toBe(false);
  });
});

describe("layoutTextBox", () => {
  it("maps normalized boxes to display pixels at identity scale", () => {
    const box = layoutTextBox([100, 50, 600, 150], fakeImage(1000, 500), {
      width: 1000,
      height: 500,
    });

    // 归一化 y=50 在 500 高的图上是 25px：x 恒等、y 按高归一。
    expect(box).toEqual({ left: 100, top: 25, width: 500, height: 50 });
  });

  it("scales uniformly when the display is half size", () => {
    const box = layoutTextBox([100, 50, 600, 150], fakeImage(1000, 500), {
      width: 500,
      height: 250,
    });

    expect(box).toEqual({ left: 50, top: 12.5, width: 250, height: 25 });
  });

  it("letterboxes the box when aspect ratios differ", () => {
    const box = layoutTextBox([100, 200, 500, 600], fakeImage(1600, 900), {
      width: 900,
      height: 600,
    });

    // 适应缩放 0.5625，水平占满、垂直居中留边 46.875。
    expect(box?.left).toBeCloseTo(90);
    expect(box?.top).toBeCloseTo(148.125);
    expect(box?.width).toBeCloseTo(360);
    expect(box?.height).toBeCloseTo(202.5);
  });

  it("returns undefined for degenerate boxes or an undecoded image", () => {
    expect(
      layoutTextBox([100, 100, 100, 200], fakeImage(1000, 500), {
        width: 1000,
        height: 500,
      }),
    ).toBeUndefined();
    expect(
      layoutTextBox([100, 100, 200, 200], fakeImage(0, 0), {
        width: 1000,
        height: 500,
      }),
    ).toBeUndefined();
  });
});
