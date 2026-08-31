import { afterEach, describe, expect, it, vi } from "vitest";

import { rotateEditorState, type EditorState } from "./annotationGeometry";
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
