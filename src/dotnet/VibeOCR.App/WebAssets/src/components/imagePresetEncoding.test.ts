import { cleanup } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { encodeImagePreset } from "./imagePresetEncoding";
import { DEFAULT_EDITOR_PREFERENCES } from "./editorPreferences";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function stubEnvironment(width: number, height: number) {
  Object.defineProperty(HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value(this: HTMLCanvasElement) {
      const handler: ProxyHandler<object> = {
        get: (_target, prop) => {
          if (typeof prop !== "string") return undefined;
          if (prop === "canvas") return this;
          return () => undefined;
        },
        set: () => true,
      };
      return new Proxy({}, handler) as CanvasRenderingContext2D;
    },
  });
  Object.defineProperty(HTMLCanvasElement.prototype, "toBlob", {
    configurable: true,
    value(this: HTMLCanvasElement, callback: (blob: Blob) => void) {
      queueMicrotask(() =>
        callback(
          new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], {
            type: "image/png",
          }),
        ),
      );
    },
  });
  class FakeImage {
    decoding = "async";
    onload: (() => void) | null = null;
    naturalWidth = width;
    naturalHeight = height;
    private assigned = "";
    set src(value: string) {
      this.assigned = value;
      queueMicrotask(() => this.onload?.());
    }
    get src() {
      return this.assigned;
    }
  }
  vi.stubGlobal("Image", FakeImage);
  vi.stubGlobal(
    "URL",
    Object.assign(Object.create(URL), {
      createObjectUrl: () => "blob:fake",
      revokeObjectUrl: () => undefined,
    }),
  );
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(new Uint8Array([137, 80, 78, 71]), {
        status: 200,
        headers: { "Content-Type": "image/png" },
      }),
    ),
  );
}

describe("预设图片编码的输入/输出预算边界", () => {
  it("16385–32768 边输入可按预设合法缩小输出（不提前被输出预算拒绝）", async () => {
    stubEnvironment(20000, 100);
    const blob = await encodeImagePreset("https://app.vibeocr/__resource/big", {
      ...DEFAULT_EDITOR_PREFERENCES.output,
      width: 16000,
      height: 80,
    });
    expect(blob.type).toBe("image/png");
  });

  it("无预设尺寸时大输入仍按输出预算拒绝目标画布", async () => {
    stubEnvironment(20000, 100);
    await expect(
      encodeImagePreset("https://app.vibeocr/__resource/big", {
        ...DEFAULT_EDITOR_PREFERENCES.output,
      }),
    ).rejects.toThrow(/排版输出超出上限/);
  });

  it("超出宿主解码合同的输入被明确拒绝", async () => {
    stubEnvironment(40000, 10);
    await expect(
      encodeImagePreset("https://app.vibeocr/__resource/too-big", {
        ...DEFAULT_EDITOR_PREFERENCES.output,
        width: 16000,
        height: 4,
      }),
    ).rejects.toThrow(/输入图片超出宿主解码合同/);
  });
});
