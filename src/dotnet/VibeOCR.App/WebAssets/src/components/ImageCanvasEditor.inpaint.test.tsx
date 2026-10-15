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
import type { InpaintFailureCode, InpaintResponse } from "./inpaint/inpaint";
import * as maskOperations from "./inpaint/inpaintMaskState";
import { ImageCanvasEditor } from "./ImageCanvasEditor";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// jsdom 不提供 ImageData 构造器：预览提取只用构造参数，stub 足够。
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
  readonly naturalWidth: number = 160;
  readonly naturalHeight: number = 100;
  decoding = "";
  src = "";
  onload: (() => void) | null = null;

  constructor() {
    setTimeout(() => this.onload?.(), 0);
  }
}

interface CapturedRequest {
  generation: number;
  width: number;
  height: number;
  rect: { x: number; y: number; width: number; height: number };
}

class FakeWorker {
  static instances: FakeWorker[] = [];
  onmessage: ((event: { data: InpaintResponse }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  terminated = false;
  readonly requests: CapturedRequest[] = [];

  constructor() {
    FakeWorker.instances.push(this);
  }

  postMessage(message: unknown): void {
    this.requests.push(message as CapturedRequest);
  }

  terminate(): void {
    this.terminated = true;
  }

  succeed(): void {
    const request = this.requests[0];
    if (!request || !this.onmessage) throw new Error("no pending request");
    const rgba = new ArrayBuffer(request.width * request.height * 4);
    this.onmessage({
      data: {
        generation: request.generation,
        ok: true,
        rgba,
        width: request.width,
        height: request.height,
      },
    });
  }

  fail(error: { code: InpaintFailureCode; message: string }): void {
    const request = this.requests[0];
    if (!request || !this.onmessage) throw new Error("no pending request");
    this.onmessage({
      data: {
        generation: request.generation,
        ok: false,
        error,
      },
    });
  }
}

/** 画布 2d 上下文 stub：draw/putImageData 均为 no-op，getImageData 返回
 * 匹配尺寸的零缓冲，满足基线合成与传输路径（像素正确性由 e2e 覆盖）。 */
function installCanvasStubs(): () => void {
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
        return () => undefined;
      },
      set: () => true,
    });
  };
  const getContextDescriptor = Object.getOwnPropertyDescriptor(
    HTMLCanvasElement.prototype,
    "getContext",
  )!;
  Object.defineProperty(HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value: vi.fn(() => fakeContext()),
  });
  return () => {
    Object.defineProperty(
      HTMLCanvasElement.prototype,
      "getContext",
      getContextDescriptor,
    );
  };
}

async function renderEditor(props?: {
  source?: string;
  session?: { sessionId: string; revision: number };
}) {
  const actions = createActions();
  const view = render(
    <ImageCanvasEditor
      actions={actions}
      canExport={true}
      canRecognize={false}
      source={props?.source ?? "https://app.vibeocr/__resource/capture.png"}
      session={props?.session}
    />,
  );
  const canvas = screen.getByLabelText("图片检查画布");
  canvas.getBoundingClientRect = () =>
    ({
      left: 0,
      top: 0,
      width: 900,
      height: 600,
      right: 900,
      bottom: 600,
      x: 0,
      y: 0,
    }) as DOMRect;
  await waitFor(() => {
    // 图片解码完成后 900×600 画布上以 5.625 缩放居中显示 160×100 图。
    fireEvent.click(screen.getByRole("button", { name: "去水印" }));
    expect(screen.getByText(/去水印（Beta）：拖拽框选/)).toBeInTheDocument();
  });
  return { actions, ...view, canvas };
}

function dragSelection(canvas: HTMLElement): void {
  fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 200, clientY: 150 });
  fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 300, clientY: 220 });
  fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
}

describe("inpaint tool wiring", () => {
  it.each([
    ["brush-add", true],
    ["brush-remove", true],
    ["brush-add", false],
    ["brush-remove", false],
  ] as const)(
    "R15 resets the mask before the first %s stroke (preview=%s)",
    async (mode, previewReady) => {
      const restore = installCanvasStubs();
      vi.stubGlobal("Image", FakeImage);
      vi.stubGlobal("Worker", FakeWorker);
      FakeWorker.instances = [];
      try {
        const { canvas, unmount } = await renderEditor();
        dragSelection(canvas);
        fireEvent.click(screen.getByRole("button", { name: "预览修补" }));
        if (previewReady)
          await act(async () => FakeWorker.instances[0]!.succeed());
        fireEvent.change(screen.getByLabelText("修补掩膜绘制方式"), {
          target: { value: mode },
        });
        fireEvent.pointerDown(canvas, {
          pointerId: 1,
          clientX: 700,
          clientY: 450,
        });
        fireEvent.pointerUp(canvas, {
          pointerId: 1,
          clientX: 700,
          clientY: 450,
        });
        const preview = screen.getByRole("button", { name: "预览修补" });
        if (mode === "brush-remove") {
          expect(preview).toBeDisabled();
        } else {
          fireEvent.click(preview);
          const request = FakeWorker.instances[1]!.requests[0]!;
          expect(request.rect.x).toBeGreaterThan(100);
          expect(request.rect.y).toBeGreaterThan(55);
        }
        expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
        unmount();
      } finally {
        restore();
      }
    },
  );

  it("R15 batches all mask trajectory points per frame and flushes pointerup and cancels queued strokes", async () => {
    const restore = installCanvasStubs();
    class LargeImage extends FakeImage {
      override readonly naturalWidth = 1200;
      override readonly naturalHeight = 1000;
    }
    vi.stubGlobal("Image", LargeImage);
    const frames = new Map<number, FrameRequestCallback>();
    let id = 0;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      frames.set(++id, callback);
      return id;
    });
    vi.stubGlobal("cancelAnimationFrame", (frame: number) =>
      frames.delete(frame),
    );
    const paint = vi.spyOn(maskOperations, "applyMaskBrush");
    try {
      const { canvas, unmount } = await renderEditor();
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 100,
        clientY: 50,
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 800, clientY: 550 });
      fireEvent.change(screen.getByLabelText("修补掩膜绘制方式"), {
        target: { value: "brush-add" },
      });
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 200,
        clientY: 200,
      });
      for (let x = 210; x <= 400; x += 10)
        fireEvent.pointerMove(canvas, {
          pointerId: 1,
          clientX: x,
          clientY: 200 + (x % 30),
        });
      expect(paint).not.toHaveBeenCalled();
      act(() => {
        const pending = [...frames.values()];
        frames.clear();
        pending.forEach((cb) => cb(0));
      });
      expect(paint).toHaveBeenCalledTimes(1);
      expect(paint.mock.calls[0]![0]!.data.length).toBeGreaterThan(900_000);
      const expectedPoints = [
        { x: 200, y: 200 },
        ...Array.from({ length: 20 }, (_, i) => {
          const x = 210 + i * 10;
          return { x, y: 200 + (x % 30) };
        }),
      ].map(({ x, y }) => ({ x: (x - 90) / 0.6, y: y / 0.6 }));
      expect(paint.mock.calls[0]![1]).toHaveLength(expectedPoints.length);
      paint.mock.calls[0]![1].forEach((at, index) => {
        expect(at.x).toBeCloseTo(expectedPoints[index]!.x, 10);
        expect(at.y).toBeCloseTo(expectedPoints[index]!.y, 10);
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 450, clientY: 240 });
      expect(paint).toHaveBeenCalledTimes(2);
      expect(paint.mock.calls[1]![1].at(-1)!.x).toBe(600);
      fireEvent.pointerDown(canvas, {
        pointerId: 2,
        clientX: 600,
        clientY: 300,
      });
      fireEvent.pointerCancel(canvas, { pointerId: 2 });
      act(() => {
        const pending = [...frames.values()];
        frames.clear();
        pending.forEach((cb) => cb(0));
      });
      expect(paint).toHaveBeenCalledTimes(2);
      unmount();
    } finally {
      restore();
    }
  });
  it("R9 a first remove rectangle never creates a repair mask or worker", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { actions, unmount, canvas } = await renderEditor();
      await waitFor(() =>
        expect(screen.getByText(/原图 160×100/)).toBeInTheDocument(),
      );
      fireEvent.change(screen.getByLabelText("修补掩膜绘制方式"), {
        target: { value: "rect-remove" },
      });
      dragSelection(canvas);
      const preview = screen.getByRole("button", { name: "预览修补" });
      await act(async () => {
        fireEvent.click(preview);
      });
      expect(FakeWorker.instances).toHaveLength(0);
      expect(preview).toBeDisabled();
      expect(screen.queryByRole("button", { name: "应用修补" })).toBeNull();
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "清除编辑" })).toBeDisabled();
      expect(actions.run).not.toHaveBeenCalled();
      unmount();
    } finally {
      restore();
    }
  });

  it("R6 rejected oversized regions cannot allocate storage or become preview masks", async () => {
    const restore = installCanvasStubs();
    class BigImage extends FakeImage {
      override readonly naturalWidth = 9000;
      override readonly naturalHeight = 6000;
    }
    vi.stubGlobal("Image", BigImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { unmount, canvas } = await renderEditor();
      dragSelection(canvas);
      const allocations: number[] = [];
      const Native = Uint8Array;
      vi.stubGlobal(
        "Uint8Array",
        new Proxy(Native, {
          construct(target, args) {
            if (typeof args[0] === "number") allocations.push(args[0]);
            return Reflect.construct(target, args);
          },
        }),
      );
      fireEvent.click(screen.getByRole("button", { name: "预览修补" }));
      expect(screen.getByText(/修补掩膜工作区超过/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "预览修补" })).toBeDisabled();
      expect(FakeWorker.instances).toHaveLength(0);
      expect(allocations.filter((size) => size > 1_000_000)).toEqual([]);
      unmount();
    } finally {
      restore();
    }
  });

  it("requires an explicit selection before previewing and reports Beta limits", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    try {
      const { unmount } = await renderEditor();
      expect(screen.getByRole("button", { name: "预览修补" })).toBeDisabled();
      expect(screen.queryByRole("button", { name: "应用修补" })).toBeNull();
      expect(
        screen.getByText(/复杂纹理或大面积覆盖效果有限，不是无损还原/),
      ).toBeInTheDocument();
      unmount();
    } finally {
      restore();
    }
  });

  it("previews via worker, applies exactly one history commit, and undoes", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { actions, unmount, canvas } = await renderEditor({
        session: { sessionId: "session-inpaint", revision: 0 },
      });
      dragSelection(canvas);
      expect(screen.getByText(/已框选修补区域/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "预览修补" })).toBeEnabled();

      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "预览修补" }));
      });
      expect(FakeWorker.instances).toHaveLength(1);
      const worker = FakeWorker.instances[0]!;
      expect(worker.requests).toHaveLength(1);
      // 显示空间 (200,150)-(400,300) → 原图像素空间外扩取整矩形。
      expect(worker.requests[0]!.rect).toEqual({
        x: 35,
        y: 23,
        width: 37,
        height: 27,
      });
      expect(screen.getByText(/正在本地修补选中区域/)).toBeInTheDocument();

      await act(async () => {
        worker.succeed();
      });
      expect(screen.getByText(/修补预览完成/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "应用修补" })).toBeEnabled();
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();

      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "应用修补" }));
      });
      expect(screen.getByText(/已应用修补/)).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "撤销" })).toBeEnabled();
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-inpaint",
        revision: 1,
      });

      // 应用后回到初始状态：预览/选区清空，撤销恢复原图。
      expect(screen.queryByRole("button", { name: "应用修补" })).toBeNull();
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      });
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "清除编辑" })).toBeDisabled();
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-inpaint",
        revision: 2,
      });
      unmount();
    } finally {
      restore();
    }
  });

  it("cancel terminates the worker and a late response cannot become a preview", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { unmount, canvas } = await renderEditor();
      dragSelection(canvas);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "预览修补" }));
      });
      const worker = FakeWorker.instances[0]!;
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "取消修补" }));
      });
      expect(worker.terminated).toBe(true);
      expect(
        screen.getByText(/已取消修补；当前图片未被修改/),
      ).toBeInTheDocument();
      // 迟到响应：generation 已推进，绝不产生预览或应用入口。
      await act(async () => {
        worker.succeed();
      });
      expect(screen.queryByText(/修补预览完成/)).toBeNull();
      expect(screen.queryByRole("button", { name: "应用修补" })).toBeNull();
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      unmount();
    } finally {
      restore();
    }
  });

  it("drops responses that arrive after another edit committed", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { unmount, canvas } = await renderEditor();
      dragSelection(canvas);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "预览修补" }));
      });
      const worker = FakeWorker.instances[0]!;
      // 处理期间用户旋转：内容修订推进，在途请求作废。
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
      });
      expect(worker.terminated).toBe(true);
      await act(async () => {
        worker.succeed();
      });
      expect(screen.queryByText(/修补预览完成/)).toBeNull();
      expect(screen.queryByRole("button", { name: "应用修补" })).toBeNull();
      // 旋转本身已入历史（撤销可用），但修补没有写入。
      expect(screen.getByRole("button", { name: "撤销" })).toBeEnabled();
      unmount();
    } finally {
      restore();
    }
  });

  it("resets selection and preview when the source image changes", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { rerender, unmount, canvas } = await renderEditor();
      dragSelection(canvas);
      expect(screen.getByRole("button", { name: "预览修补" })).toBeEnabled();
      rerender(
        <ImageCanvasEditor
          actions={createActions()}
          canExport={true}
          canRecognize={false}
          source="https://app.vibeocr/__resource/next.png"
        />,
      );
      await waitFor(() => {
        expect(screen.getByRole("button", { name: "预览修补" })).toBeDisabled();
      });
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      unmount();
    } finally {
      restore();
    }
  });

  it("surfaces worker failure codes without touching the image", async () => {
    const restore = installCanvasStubs();
    vi.stubGlobal("Image", FakeImage);
    vi.stubGlobal("Worker", FakeWorker);
    FakeWorker.instances = [];
    try {
      const { unmount, canvas } = await renderEditor();
      dragSelection(canvas);
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "预览修补" }));
      });
      const worker = FakeWorker.instances[0]!;
      await act(async () => {
        worker.fail({
          code: "unexpected",
          message: "worker blew up",
        });
      });
      expect(
        screen.getByText(/本地修补失败：worker blew up/),
      ).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      expect(screen.queryByRole("button", { name: "应用修补" })).toBeNull();
      unmount();
    } finally {
      restore();
    }
  });
});
