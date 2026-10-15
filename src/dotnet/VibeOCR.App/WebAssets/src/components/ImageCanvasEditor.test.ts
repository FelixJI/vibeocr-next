import { afterEach, describe, expect, it, vi } from "vitest";

import {
  annotationFill,
  annotationStrokeAlpha,
  constrainBoxDrag,
  constrainLineDrag,
  finalOutputSize,
  imageTransform,
  outputSize,
  projectPoint,
  rotateEditorState,
  rotatedBoundingBox,
  type EditorState,
  type Mark,
} from "./annotationGeometry";
import { uploadAnnotatedImage } from "./annotationHandoff";

afterEach(() => vi.unstubAllGlobals());

describe("annotation stroke/fill opacity resolution", () => {
  const legacy: Mark = {
    id: "fixture",
    tool: "rectangle",
    start: { x: 0, y: 0 },
    end: { x: 40, y: 30 },
  };

  it("keeps legacy marks fully opaque and hollow (old rendering unchanged)", () => {
    expect(annotationStrokeAlpha(legacy)).toBe(1);
    expect(annotationFill(legacy)).toBeUndefined();
  });

  it("resolves 0-100 percent with clamping; 0% is a valid value", () => {
    expect(
      annotationStrokeAlpha({
        ...legacy,
        style: { strokeOpacity: 0 },
      }),
    ).toBe(0);
    expect(
      annotationStrokeAlpha({
        ...legacy,
        style: { strokeOpacity: 60 },
      }),
    ).toBeCloseTo(0.6, 10);
    expect(
      annotationStrokeAlpha({
        ...legacy,
        style: { strokeOpacity: 500 },
      }),
    ).toBe(1);
    expect(
      annotationStrokeAlpha({
        ...legacy,
        style: { strokeOpacity: Number.NaN },
      }),
    ).toBe(1);
  });

  it("resolves fill color/opacity with safe defaults", () => {
    expect(
      annotationFill({ ...legacy, style: { fill: { opacity: 40 } } }),
    ).toEqual({ color: "#ffffff", alpha: 0.4 });
    expect(
      annotationFill({
        ...legacy,
        style: { fill: { color: "#E02020", opacity: 0 } },
      }),
    ).toEqual({ color: "#e02020", alpha: 0 });
    expect(
      annotationFill({ ...legacy, style: { fill: { color: "#12a150" } } }),
    ).toEqual({ color: "#12a150", alpha: 1 });
    expect(annotationFill({ ...legacy, style: {} })).toBeUndefined();
  });

  it("treats fill.enabled=false as hollow while keeping the stored values", () => {
    // 关闭填充：解析为空心（渲染/导出与无 fill 一致），但值随对象保留，
    // 重新开启时从对象自身恢复，不需要任何索引侧表。
    const disabled = {
      ...legacy,
      style: {
        fill: { color: "#ff0000", opacity: 40, enabled: false },
      },
    };
    expect(annotationFill(disabled)).toBeUndefined();
    // 缺省（undefined/true）保持启用：旧字段兼容。
    expect(
      annotationFill({
        ...legacy,
        style: { fill: { color: "#12a150", opacity: 70, enabled: true } },
      }),
    ).toEqual({ color: "#12a150", alpha: 0.7 });
  });
});

describe("annotated image handoff", () => {
  it("uploads PNG bytes to the bounded same-origin host endpoint", async () => {
    const fetch = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          resourceUri:
            "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        }),
        { status: 201, headers: { "Content-Type": "application/json" } },
      ),
    );
    vi.stubGlobal("fetch", fetch);
    const png = new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], {
      type: "image/png",
    });

    await expect(uploadAnnotatedImage(png)).resolves.toBe(
      "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
    );
    expect(fetch).toHaveBeenCalledWith("/__annotation", {
      method: "POST",
      headers: { "Content-Type": "image/png" },
      body: png,
    });
  });

  it("uploads JPEG bytes with the encoded content type", async () => {
    const fetch = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          resourceUri:
            "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        }),
        { status: 201, headers: { "Content-Type": "application/json" } },
      ),
    );
    vi.stubGlobal("fetch", fetch);
    const jpeg = new Blob(
      [new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 16, 74, 70])],
      { type: "image/jpeg" },
    );

    await expect(uploadAnnotatedImage(jpeg)).resolves.toBe(
      "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
    );
    expect(fetch).toHaveBeenCalledWith("/__annotation", {
      method: "POST",
      headers: { "Content-Type": "image/jpeg" },
      body: jpeg,
    });
  });

  it("rejects formats the annotation endpoint cannot receive", async () => {
    vi.stubGlobal("fetch", vi.fn());
    const webp = new Blob([new Uint8Array([0, 0, 0, 0, 0, 0, 0, 0])], {
      type: "image/webp",
    });

    await expect(uploadAnnotatedImage(webp)).rejects.toThrow("PNG or JPEG");
    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });

  it("rejects a host response that does not return an opaque annotation URI", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ resourceUri: "file:///tmp/output.png" }),
          {
            status: 201,
          },
        ),
      ),
    );
    const png = new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], {
      type: "image/png",
    });

    await expect(uploadAnnotatedImage(png)).rejects.toThrow(
      "response is invalid",
    );
  });
});

describe("final output size", () => {
  const image = new Image();
  Object.defineProperties(image, {
    naturalWidth: { value: 1600 },
    naturalHeight: { value: 900 },
  });
  const display = { width: 900, height: 600 };

  it("returns the rotated natural size without crop or resize", () => {
    expect(finalOutputSize(image, { rotation: 0, marks: [] }, display)).toEqual(
      { width: 1600, height: 900 },
    );
    expect(
      finalOutputSize(image, { rotation: 90, marks: [] }, display),
    ).toEqual({ width: 900, height: 1600 });
  });

  it("maps a display-space crop into natural output pixels", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [],
      crop: { start: { x: 100, y: 50 }, end: { x: 500, y: 350 } },
    };

    const size = finalOutputSize(image, state, display);

    // 显示缩放 0.5625：裁剪映射回原图后为 711.1×533.3 像素。
    expect(size).toEqual({ width: 711, height: 533 });
  });

  it("applies the specified resize after crop", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [],
      crop: { start: { x: 100, y: 50 }, end: { x: 500, y: 350 } },
      resize: { width: 320, height: 240 },
    };

    expect(finalOutputSize(image, state, display)).toEqual({
      width: 320,
      height: 240,
    });
  });

  it("keeps the specified output size through rotation", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [],
      resize: { width: 640, height: 480 },
    };

    const rotated = rotateEditorState(state, image, display, 90);

    expect(rotated.resize).toEqual({ width: 640, height: 480 });
    expect(finalOutputSize(image, rotated, display)).toEqual({
      width: 640,
      height: 480,
    });
  });
});

describe("arbitrary rotation bounds", () => {
  const image = new Image();
  Object.defineProperties(image, {
    naturalWidth: { value: 1600 },
    naturalHeight: { value: 900 },
  });

  it("keeps exact quarter-turn sizes as width/height swap", () => {
    expect(rotatedBoundingBox(1600, 900, 0)).toEqual({
      width: 1600,
      height: 900,
    });
    expect(rotatedBoundingBox(1600, 900, 90)).toEqual({
      width: 900,
      height: 1600,
    });
    expect(rotatedBoundingBox(1600, 900, 180)).toEqual({
      width: 1600,
      height: 900,
    });
    expect(outputSize(image, 270)).toEqual({ width: 900, height: 1600 });
  });

  it("keeps odd-sized quarter turns exact without epsilon drift", () => {
    // 奇数尺寸四直角：三角函数 epsilon 不得把 81×41 变成 82×42。
    expect(rotatedBoundingBox(81, 41, 0)).toEqual({ width: 81, height: 41 });
    expect(rotatedBoundingBox(81, 41, 90)).toEqual({ width: 41, height: 81 });
    expect(rotatedBoundingBox(81, 41, 180)).toEqual({ width: 81, height: 41 });
    expect(rotatedBoundingBox(81, 41, 270)).toEqual({ width: 41, height: 81 });
    expect(rotatedBoundingBox(81, 41, 360)).toEqual({ width: 81, height: 41 });
  });

  it("uses the axis-aligned bounding box for arbitrary angles", () => {
    // 旧实现任意角退化为未旋转尺寸；导出/显示必须按旋转包围盒容纳内容。
    expect(rotatedBoundingBox(1600, 900, 45)).toEqual({
      width: 1768,
      height: 1768,
    });
    expect(outputSize(image, 30)).toEqual({ width: 1836, height: 1580 });
  });

  it("fits the display transform to the rotated bounding box", () => {
    const transform = imageTransform(image, 45, { width: 884, height: 884 });
    expect(transform.scale).toBeCloseTo(0.5, 5);
  });
});

describe("drag constraints (Shift/Alt)", () => {
  it("keeps anchor-corner semantics without modifiers", () => {
    expect(
      constrainBoxDrag(
        { x: 100, y: 100 },
        { x: 400, y: 300 },
        { shift: false, fromCenter: false },
      ),
    ).toEqual({ start: { x: 100, y: 100 }, end: { x: 400, y: 300 } });
  });

  it("shift grows to the larger edge for square/circle", () => {
    // dx=300、dy=200 → 300×300；负方向保持各自符号。
    expect(
      constrainBoxDrag(
        { x: 100, y: 100 },
        { x: 400, y: 300 },
        { shift: true, fromCenter: false },
      ),
    ).toEqual({ start: { x: 100, y: 100 }, end: { x: 400, y: 400 } });
    expect(
      constrainBoxDrag(
        { x: 400, y: 300 },
        { x: 100, y: 100 },
        { shift: true, fromCenter: false },
      ),
    ).toEqual({ start: { x: 400, y: 300 }, end: { x: 100, y: 0 } });
  });

  it("alt expands symmetrically around the anchor", () => {
    expect(
      constrainBoxDrag(
        { x: 200, y: 200 },
        { x: 300, y: 260 },
        { shift: false, fromCenter: true },
      ),
    ).toEqual({ start: { x: 100, y: 140 }, end: { x: 300, y: 260 } });
    // Shift+Alt：正方且中心对称。
    expect(
      constrainBoxDrag(
        { x: 200, y: 200 },
        { x: 300, y: 260 },
        { shift: true, fromCenter: true },
      ),
    ).toEqual({ start: { x: 100, y: 100 }, end: { x: 300, y: 300 } });
  });

  it("snaps line endpoints to 45° multiples while keeping length", () => {
    const snapped = constrainLineDrag(
      { x: 100, y: 100 },
      { x: 400, y: 300 },
      true,
    );
    const length = Math.hypot(300, 200);
    expect(snapped.x).toBeCloseTo(100 + length / Math.SQRT2, 6);
    expect(snapped.y).toBeCloseTo(100 + length / Math.SQRT2, 6);
    // 33.69° 吸附到 45°；近乎水平的角吸附到 0°。
    const flat = constrainLineDrag({ x: 0, y: 0 }, { x: 100, y: 5 }, true);
    expect(flat.y).toBeCloseTo(0, 9);
    expect(flat.x).toBeCloseTo(Math.hypot(100, 5), 6);
    // 不约束时原样返回。
    expect(constrainLineDrag({ x: 0, y: 0 }, { x: 7, y: 9 }, false)).toEqual({
      x: 7,
      y: 9,
    });
  });
});

describe("annotation rotation", () => {
  it("keeps existing marks attached after four quarter turns", () => {
    const image = new Image();
    Object.defineProperties(image, {
      naturalWidth: { value: 1600 },
      naturalHeight: { value: 900 },
    });
    const original: EditorState = {
      rotation: 0,
      marks: [
        {
          id: "fixture",
          tool: "rectangle",
          start: { x: 200, y: 180 },
          end: { x: 420, y: 310 },
        },
      ],
      crop: { start: { x: 160, y: 120 }, end: { x: 700, y: 480 } },
    };
    const size = { width: 900, height: 600 };

    const rotated = [90, 180, 270, 0].reduce(
      (state, rotation) => rotateEditorState(state, image, size, rotation),
      original,
    );

    expect(rotated.rotation).toBe(0);
    expect(rotated.marks[0]?.start.x).toBeCloseTo(200);
    expect(rotated.marks[0]?.start.y).toBeCloseTo(180);
    expect(rotated.marks[0]?.end.x).toBeCloseTo(420);
    expect(rotated.marks[0]?.end.y).toBeCloseTo(310);
    expect(rotated.crop?.start.x).toBeCloseTo(160);
    expect(rotated.crop?.end.y).toBeCloseTo(480);
  });

  it("rotates pen and highlighter point trails with the same mapping", () => {
    const image = new Image();
    Object.defineProperties(image, {
      naturalWidth: { value: 1600 },
      naturalHeight: { value: 900 },
    });
    const size = { width: 900, height: 600 };
    const state: EditorState = {
      rotation: 0,
      marks: [
        {
          id: "fixture",
          tool: "pen",
          start: { x: 200, y: 180 },
          end: { x: 420, y: 310 },
          points: [
            { x: 200, y: 180 },
            { x: 300, y: 240 },
            { x: 420, y: 310 },
          ],
        },
      ],
    };

    // 旧实现只旋转 start/end、遗留未旋转 points；轨迹点必须同一映射。
    const rotated = rotateEditorState(state, image, size, 90);
    const expected = state.marks[0]!.points!.map((at) =>
      projectPoint(at, image, 0, size, 90, size),
    );
    expect(rotated.marks[0]!.points).toHaveLength(3);
    rotated.marks[0]!.points!.forEach((at, index) => {
      expect(at.x).toBeCloseTo(expected[index]!.x);
      expect(at.y).toBeCloseTo(expected[index]!.y);
    });

    // 三次直角旋转回到原位（90→180→270→0）。
    const roundTrip = [180, 270, 0].reduce(
      (current, rotation) => rotateEditorState(current, image, size, rotation),
      rotated,
    );
    roundTrip.marks[0]!.points!.forEach((at, index) => {
      expect(at.x).toBeCloseTo(state.marks[0]!.points![index]!.x);
      expect(at.y).toBeCloseTo(state.marks[0]!.points![index]!.y);
    });
  });
});
