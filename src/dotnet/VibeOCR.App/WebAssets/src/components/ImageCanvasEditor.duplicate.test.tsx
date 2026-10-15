import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions } from "../app/types";
import type { CaptureScene } from "../bridge/captureScene";
import type { DuplicateResponse } from "./duplicate/duplicateMatch";
import { ImageCanvasEditor } from "./ImageCanvasEditor";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

class FakeImageData {
  constructor(
    public data: Uint8ClampedArray,
    public width: number,
    public height: number,
  ) {}
}

function createActions(): AppActions & { run: ReturnType<typeof vi.fn> } {
  return {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
}

class FakeImage {
  readonly naturalWidth = 160;
  readonly naturalHeight = 100;
  decoding = "";
  src = "";
  onload: (() => void) | null = null;

  constructor() {
    queueMicrotask(() => this.onload?.());
  }
}

class FakeWorker {
  static instances: FakeWorker[] = [];
  onmessage: ((event: { data: DuplicateResponse }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  terminated = false;
  readonly requests: unknown[] = [];

  constructor() {
    FakeWorker.instances.push(this);
  }

  postMessage(message: unknown): void {
    this.requests.push(message);
  }

  terminate(): void {
    this.terminated = true;
  }

  respond(body: {
    ok: true;
    candidates: readonly {
      x: number;
      y: number;
      width: number;
      height: number;
      error: number;
    }[];
  }): void {
    const request = this.requests[0] as { generation: number };
    if (!request || !this.onmessage) throw new Error("no pending request");
    this.onmessage({
      data: { generation: request.generation, ...body } as DuplicateResponse,
    });
  }
}

function installCanvasStubs(
  failWhen?: (canvas: HTMLCanvasElement) => boolean,
): () => void {
  if (typeof ImageData === "undefined") {
    vi.stubGlobal("ImageData", FakeImageData);
  }
  const fakeContext = () => {
    const target: Record<string | symbol, unknown> = {};
    return new Proxy(target, {
      get: (current, property) => {
        if (property in current) return current[property];
        if (property === "getImageData") {
          return (_x: number, _y: number, width: number, height: number) => ({
            data: new Uint8ClampedArray(width * height * 4),
            width,
            height,
          });
        }
        if (property === "createImageData") {
          return (width: number, height: number) => ({
            data: new Uint8ClampedArray(width * height * 4),
            width,
            height,
          });
        }
        return () => undefined;
      },
      set: () => true,
    });
  };
  const descriptor = Object.getOwnPropertyDescriptor(
    HTMLCanvasElement.prototype,
    "getContext",
  )!;
  Object.defineProperty(HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value: vi.fn(function (this: HTMLCanvasElement) {
      // 可注入的构造失败：按画布尺寸精确命中目标补丁画布。
      if (failWhen && failWhen(this)) return null;
      return fakeContext();
    }),
  });
  return () => {
    Object.defineProperty(
      HTMLCanvasElement.prototype,
      "getContext",
      descriptor,
    );
  };
}

async function renderEditor(
  session?: { sessionId: string; revision: number },
  captureScene?: CaptureScene,
) {
  const actions = createActions();
  const view = render(
    <ImageCanvasEditor
      actions={actions}
      canExport
      canRecognize={false}
      session={session}
      captureScene={captureScene}
      source="https://app.vibeocr/__resource/capture.png"
    />,
  );
  const canvas = screen.getByLabelText("图片检查画布");
  canvas.getBoundingClientRect = () =>
    ({ left: 0, top: 0, width: 900, height: 600 }) as DOMRect;
  await waitFor(() => {
    fireEvent.click(screen.getByRole("button", { name: "重复匹配" }));
  });
  return { actions, ...view, canvas };
}

function dragSource(canvas: HTMLElement): void {
  fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 200, clientY: 150 });
  fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 260, clientY: 190 });
  fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 260, clientY: 190 });
}

describe("duplicate match wiring", () => {
  it("R4 capture duplicate actions always have visible text", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { canvas, unmount } = await renderEditor(
        { sessionId: "capture", revision: 0 },
        {
          surfaceId: "surface",
          captureSessionId: "capture",
          inputUrl: "https://app.vibeocr/__resource/capture.png",
          inputRevision: 0,
          documentGeneration: 1,
          bootstrapGeneration: 1,
          initialTool: "select",
          x: 0,
          y: 0,
          width: 160,
          height: 100,
          desktopWidth: 1920,
          desktopHeight: 1080,
        },
      );
      expect(
        screen.getByRole("button", { name: "查找重复" }),
      ).toHaveTextContent("查找重复");
      dragSource(canvas);
      fireEvent.click(screen.getByRole("button", { name: "查找重复" }));
      expect(
        screen.getByRole("button", { name: "取消重复匹配" }),
      ).toHaveTextContent("取消匹配");
      fireEvent.click(screen.getByRole("button", { name: "取消重复匹配" }));
      dragSource(canvas);
      fireEvent.click(screen.getByRole("button", { name: "查找重复" }));
      await act(async () => {
        FakeWorker.instances.at(-1)!.respond({
          ok: true,
          candidates: [{ x: 80, y: 40, width: 10, height: 8, error: 0 }],
        });
      });
      expect(
        screen.getByRole("button", { name: "确认应用重复遮挡" }),
      ).toHaveTextContent("确认应用");
      expect(
        screen.getByRole("button", { name: "放弃重复匹配" }),
      ).toHaveTextContent("放弃匹配");
      unmount();
    } finally {
      restore();
    }
  });

  it("requires a selection before searching", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    try {
      const { unmount } = await renderEditor();
      expect(screen.getByRole("button", { name: "查找重复" })).toBeDisabled();
      unmount();
    } finally {
      restore();
    }
  });

  it("shows candidates for preview only; confirm commits exactly once; undo restores", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { actions, unmount, canvas } = await renderEditor({
        sessionId: "session-dup",
        revision: 0,
      });
      dragSource(canvas);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "查找重复" }));
      });
      const worker = FakeWorker.instances[0]!;
      expect(worker.requests).toHaveLength(1);
      const request = worker.requests[0] as {
        rect: { x: number; y: number; width: number; height: number };
      };
      // 显示空间 (200,150)-(260,190) → 原图像素空间外扩取整矩形。
      expect(request.rect).toEqual({ x: 35, y: 23, width: 12, height: 8 });

      await act(async () => {
        worker.respond({
          ok: true,
          candidates: [
            { x: 80, y: 40, width: 11, height: 8, error: 0.2 },
            { x: 120, y: 60, width: 11, height: 8, error: 1.4 },
          ],
        });
      });
      expect(screen.getByText(/找到 2 处重复内容/)).toBeInTheDocument();
      // 候选逐项增删：取消第二项。
      fireEvent.click(screen.getByRole("checkbox", { name: "候选 2" }));
      // 确认前无任何历史提交。
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      await act(async () => {
        fireEvent.click(
          screen.getByRole("button", { name: "确认应用重复遮挡" }),
        );
      });
      expect(screen.getByText(/已应用 1 处重复内容遮挡/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "撤销" })).toBeEnabled();
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-dup",
        revision: 1,
      });
      // 应用后候选清空，回到待框选状态。
      expect(screen.queryByRole("checkbox", { name: "候选 1" })).toBeNull();
      // 撤销恰好回退这一次提交。
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      });
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "清除编辑" })).toBeDisabled();
      unmount();
    } finally {
      restore();
    }
  });

  it("a mid-batch patch failure cancels the whole apply without partial commits", async () => {
    // 第二个候选构造失败时，第一张补丁也不能进入持久 store。
    const restore = installCanvasStubs((canvas) => canvas.width === 11);
    const storeWrites = vi.spyOn(Map.prototype, "set");
    let retry: (() => void) | undefined;
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { unmount, canvas } = await renderEditor();
      dragSource(canvas);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "查找重复" }));
      });
      const worker = FakeWorker.instances[0]!;
      await act(async () => {
        worker.respond({
          ok: true,
          candidates: [
            { x: 80, y: 40, width: 10, height: 8, error: 0.2 },
            { x: 120, y: 60, width: 11, height: 8, error: 1.4 },
          ],
        });
      });
      await act(async () => {
        fireEvent.click(
          screen.getByRole("button", { name: "确认应用重复遮挡" }),
        );
      });
      expect(
        screen.getByText(/本次应用已整体取消；图片未被修改/),
      ).toBeInTheDocument();
      expect(
        storeWrites.mock.calls.filter(
          ([id, value]) =>
            typeof id === "number" &&
            value instanceof HTMLCanvasElement &&
            value.width === 10 &&
            value.height === 8,
        ),
      ).toHaveLength(0);
      // 历史未变：无部分应用、无孤儿提交。
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "清除编辑" })).toBeDisabled();
      // 候选仍在：恢复构造后可整批重试。
      restore();
      retry = installCanvasStubs();
      await act(async () => {
        fireEvent.click(
          screen.getByRole("button", { name: "确认应用重复遮挡" }),
        );
      });
      expect(screen.getByText(/已应用 2 处重复内容遮挡/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "撤销" })).toBeEnabled();
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      });
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      unmount();
    } finally {
      retry?.();
      restore();
    }
  });
  it("drops late worker responses after the content changed", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { unmount, canvas } = await renderEditor();
      dragSource(canvas);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "查找重复" }));
      });
      const worker = FakeWorker.instances[0]!;
      // 搜索期间用户旋转：修订推进，在途结果作废。
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
      });
      expect(worker.terminated).toBe(true);
      await act(async () => {
        worker.respond({
          ok: true,
          candidates: [{ x: 80, y: 40, width: 11, height: 8, error: 0 }],
        });
      });
      expect(screen.queryByText(/找到 1 处重复内容/)).toBeNull();
      expect(
        screen.queryByRole("button", { name: "确认应用重复遮挡" }),
      ).toBeNull();
      unmount();
    } finally {
      restore();
    }
  });
});
