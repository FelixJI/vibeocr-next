import { afterEach, describe, expect, it, vi } from "vitest";

import {
  finalOutputSize,
  rotateEditorState,
  type EditorState,
} from "./annotationGeometry";
import { uploadAnnotatedImage } from "./annotationHandoff";

afterEach(() => vi.unstubAllGlobals());

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
});
