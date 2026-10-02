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
  readonly naturalWidth = 160;
  readonly naturalHeight = 100;
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
      expect(
        screen.getByText(/修补预览完成（Worker 实际耗时/),
      ).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "应用修补" })).toBeEnabled();
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();

      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "应用修补" }));
      });
      expect(screen.getByText(/已应用去水印修补/)).toBeInTheDocument();
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
