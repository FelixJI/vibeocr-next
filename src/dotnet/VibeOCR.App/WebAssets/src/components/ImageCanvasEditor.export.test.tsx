import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions } from "../app/types";
import { ImageCanvasEditor } from "./ImageCanvasEditor";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function createActions(): AppActions & { run: ReturnType<typeof vi.fn> } {
  return {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
}

/** 记录型 2D 上下文：jsdom 无像素，按调用序列断言绘制语义。 */
type RecordedCall = { op: string; args: unknown[] };

interface RecordedCanvas {
  element: HTMLCanvasElement;
  calls: RecordedCall[];
}

const state = {
  canvases: [] as RecordedCanvas[],
  toBlobCalls: 0,
};

function fullCanvasFills(): string[] {
  const fills: string[] = [];
  for (const recorded of state.canvases) {
    const width = recorded.element.width;
    const height = recorded.element.height;
    let style = "";
    for (const call of recorded.calls) {
      if (call.op === "fillStyle") style = String(call.args[0]);
      if (call.op !== "fillRect") continue;
      const [x, y, w, h] = call.args as [number, number, number, number];
      if (x === 0 && y === 0 && w === width && h === height) fills.push(style);
    }
  }
  return fills;
}

function fillStyles(): string[] {
  const styles: string[] = [];
  for (const recorded of state.canvases) {
    for (const call of recorded.calls) {
      if (call.op === "fillStyle") styles.push(String(call.args[0]));
    }
  }
  return styles;
}

function proxyFor(
  canvas: HTMLCanvasElement,
  calls: RecordedCall[],
): CanvasRenderingContext2D {
  const handler: ProxyHandler<RecordedCall[]> = {
    get(target, prop) {
      if (prop === "canvas") return canvas;
      if (typeof prop !== "string") return undefined;
      return (...args: unknown[]) => {
        calls.push({ op: prop, args });
        return undefined;
      };
    },
    set(target, prop, value) {
      calls.push({ op: String(prop), args: [value] });
      return true;
    },
  };
  return new Proxy(calls, handler) as unknown as CanvasRenderingContext2D;
}

function stubCanvasStack(natural?: { width: number; height: number }) {
  state.canvases = [];
  state.toBlobCalls = 0;
  Object.defineProperty(HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value: function (this: HTMLCanvasElement) {
      const holder = this as unknown as { __calls?: RecordedCall[] };
      if (holder.__calls) return proxyFor(this, holder.__calls);
      const calls: RecordedCall[] = [];
      holder.__calls = calls;
      state.canvases.push({ element: this, calls });
      return proxyFor(this, calls);
    },
  });
  Object.defineProperty(HTMLCanvasElement.prototype, "toBlob", {
    configurable: true,
    value: function (
      this: HTMLCanvasElement,
      callback: (blob: Blob) => void,
      format?: string,
    ) {
      state.toBlobCalls += 1;
      const jpeg = format === "image/jpeg";
      const bytes = jpeg
        ? new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 16, 74, 70])
        : new Uint8Array([0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10]);
      queueMicrotask(() =>
        callback(
          new Blob([bytes], { type: jpeg ? "image/jpeg" : "image/png" }),
        ),
      );
    },
  });
  // Image：同步“解码”成功，使编辑器进入可导出状态。
  class FakeImage {
    decoding = "async";
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    naturalWidth = natural?.width ?? 64;
    naturalHeight = natural?.height ?? 32;
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
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          resourceUri:
            "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        }),
        { status: 201, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
}

function editorProps(overrides?: {
  onRecognitionSubmitted?: () => void;
  actions?: AppActions & { run: ReturnType<typeof vi.fn> };
}) {
  return {
    actions: overrides?.actions ?? createActions(),
    canExport: true,
    canRecognize: true,
    source: "https://app.vibeocr/__resource/capture.png",
    session: { sessionId: "session-a", revision: 0 },
    onRecognitionSubmitted: overrides?.onRecognitionSubmitted,
  };
}

describe("editor export pixels and previews", () => {
  it("coalesces a pen burst without losing its committed points and cancels stale frames", async () => {
    stubCanvasStack();
    vi.stubGlobal("PointerEvent", MouseEvent);
    const frames = new Map<number, FrameRequestCallback>();
    let nextFrame = 0;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      frames.set(++nextFrame, callback);
      return nextFrame;
    });
    const cancelFrame = vi.fn((id: number) => frames.delete(id));
    vi.stubGlobal("cancelAnimationFrame", cancelFrame);
    const flushFrame = () => {
      act(() => {
        const pending = [...frames.values()];
        frames.clear();
        pending.forEach((callback) => callback(0));
      });
    };
    const actions = createActions();
    const props = editorProps({ actions });
    const { rerender, unmount } = render(<ImageCanvasEditor {...props} />);
    await act(async () => {});
    flushFrame();
    const canvas = screen.getByLabelText("图片检查画布");
    vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
      x: 0,
      y: 0,
      left: 0,
      top: 0,
      right: 900,
      bottom: 600,
      width: 900,
      height: 600,
      toJSON: () => ({}),
    });
    const calls = state.canvases.find(
      (entry) => entry.element === canvas,
    )!.calls;
    calls.length = 0;
    fireEvent.click(screen.getByRole("button", { name: "画笔" }));
    fireEvent.pointerDown(canvas, { clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { clientX: 120, clientY: 120 });
    fireEvent.pointerMove(canvas, { clientX: 160, clientY: 160 });
    fireEvent.pointerUp(canvas, { clientX: 160, clientY: 160 });
    expect(calls).toHaveLength(0);
    expect(frames.size).toBe(1);
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.notifyScreenshotRevision",
      sessionId: "session-a",
      revision: 1,
    });
    flushFrame();
    expect(calls.filter((call) => call.op === "clearRect")).toHaveLength(1);
    expect(
      calls.filter((call) => call.op === "lineTo").map((call) => call.args),
    ).toEqual([
      [120, 120],
      [160, 160],
    ]);

    // 取消只丢弃草稿：之后的 pointerup 不提交内容，也不绘制残留笔迹。
    fireEvent.pointerDown(canvas, { clientX: 200, clientY: 200 });
    fireEvent.pointerMove(canvas, { clientX: 250, clientY: 250 });
    fireEvent.pointerCancel(canvas);
    fireEvent.pointerUp(canvas, { clientX: 250, clientY: 250 });
    expect(actions.run).toHaveBeenCalledTimes(1);
    calls.length = 0;
    flushFrame();
    expect(
      calls.filter((call) => call.op === "lineTo").map((call) => call.args),
    ).toEqual([
      [120, 120],
      [160, 160],
    ]);

    // 换图发生在待画帧内：旧帧被取消，旧拖拽的松手不能标注新图。
    fireEvent.pointerDown(canvas, { clientX: 300, clientY: 300 });
    fireEvent.pointerMove(canvas, { clientX: 350, clientY: 350 });
    const oldFrame = nextFrame;
    rerender(
      <ImageCanvasEditor {...props} source="https://app.vibeocr/new.png" />,
    );
    await act(async () => {});
    expect(cancelFrame).toHaveBeenCalledWith(oldFrame);
    fireEvent.pointerUp(canvas, { clientX: 350, clientY: 350 });
    expect(actions.run).toHaveBeenCalledTimes(1);
    calls.length = 0;
    flushFrame();
    expect(calls.some((call) => call.op === "lineTo")).toBe(false);
    const image = calls.find((call) => call.op === "drawImage")!
      .args[0] as HTMLImageElement;
    expect(image.src).toBe("https://app.vibeocr/new.png");

    fireEvent.pointerDown(canvas, { clientX: 400, clientY: 400 });
    expect(frames.size).toBe(1);
    const lastFrame = nextFrame;
    unmount();
    expect(cancelFrame).toHaveBeenCalledWith(lastFrame);
    expect(frames.size).toBe(0);
    expect(state.toBlobCalls).toBe(0);
  });

  it("does not auto-encode a second preview after edits settle", async () => {
    vi.useFakeTimers();
    stubCanvasStack();
    const { unmount } = render(<ImageCanvasEditor {...editorProps()} />);
    await vi.advanceTimersByTimeAsync(800);
    expect(state.toBlobCalls).toBe(0);
    expect(screen.queryByAltText("最终输出预览")).toBeNull();
    expect(screen.queryByAltText("屏蔽导出预览")).toBeNull();
    unmount();
    vi.useRealTimers();
  });

  it("keeps PNG export free of any baked opaque background fill", async () => {
    stubCanvasStack();
    const { unmount } = render(<ImageCanvasEditor {...editorProps()} />);
    const check = await screen.findByRole("button", { name: "检查文件大小" });
    fireEvent.click(check);
    await waitFor(() =>
      expect(screen.getByText(/文件大小：/)).toBeInTheDocument(),
    );
    // 旧实现把整幅画布烧成 #161616；PNG 导出不得存在任何整幅不透明填充。
    expect(fullCanvasFills()).toHaveLength(0);
    unmount();
  });

  it("composites an explicit white background for JPEG export", async () => {
    stubCanvasStack();
    const { unmount } = render(<ImageCanvasEditor {...editorProps()} />);
    fireEvent.change(screen.getByLabelText("输出格式"), {
      target: { value: "image/jpeg" },
    });
    const check = await screen.findByRole("button", { name: "检查文件大小" });
    fireEvent.click(check);
    await waitFor(() =>
      expect(screen.getByText(/文件大小：/)).toBeInTheDocument(),
    );
    expect(fullCanvasFills()).toContain("#ffffff");
    unmount();
  });

  it("hands off after an explicit recognition submit succeeds", async () => {
    stubCanvasStack();
    const actions = createActions();
    const submitted = vi.fn();
    const { unmount } = render(
      <ImageCanvasEditor
        {...editorProps({ onRecognitionSubmitted: submitted, actions })}
      />,
    );
    const recognize = await screen.findByRole("button", {
      name: "识别当前图",
    });
    fireEvent.click(recognize);
    await waitFor(() => expect(submitted).toHaveBeenCalledTimes(1));
    expect(actions.run).toHaveBeenCalledWith(
      expect.objectContaining({
        type: "recognition.recognizeScreenshotImage",
        excludeBoxes: [],
      }),
    );
    unmount();
  });

  it("uploads the unbaked ordinary export and forwards normalized exclusion boxes", async () => {
    stubCanvasStack();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor {...editorProps({ actions })} />,
    );
    const canvas = (await screen.findByLabelText(
      "图片检查画布",
    )) as HTMLCanvasElement;
    // jsdom 无布局：提供真实边界，使 pointer 事件坐标 1:1 进入画布空间。
    canvas.getBoundingClientRect = () =>
      ({
        left: 0,
        top: 0,
        width: canvas.width,
        height: canvas.height,
        right: canvas.width,
        bottom: canvas.height,
      }) as DOMRect;
    fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
    // 图像显示区约在 y∈[75,525]（64×32 等比居中）；拖拽须落在图像内。
    fireEvent.pointerDown(canvas, { pointerId: 9, clientX: 100, clientY: 200 });
    fireEvent.pointerUp(canvas, { pointerId: 9, clientX: 300, clientY: 400 });
    fireEvent.click(await screen.findByRole("button", { name: "识别当前图" }));
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.recognizeScreenshotImage",
        }),
      ),
    );
    const call = actions.run.mock.calls.find(
      ([action]) =>
        (action as { type?: string }).type ===
        "recognition.recognizeScreenshotImage",
    )?.[0] as { excludeBoxes?: unknown[] };
    // 旧实现上传烘焙白像素副本且不带 excludeBoxes；现在上传普通像素 + 框。
    expect(Array.isArray(call.excludeBoxes)).toBe(true);
    expect(call.excludeBoxes).toHaveLength(1);
    expect(fillStyles()).not.toContain("#ffffff");
    unmount();
  });

  it("round-trips rotated portrait baselines: seeded mask position matches the frozen box", async () => {
    // 横图旋转成竖图后的冻结基准：屏蔽框必须按竖图尺寸回投到正确位置。
    stubCanvasStack({ width: 32, height: 64 });
    const actions = createActions();
    const frozenBox = { x: 250, y: 0, width: 500, height: 1000 };
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{
          sessionId: "session-portrait",
          revision: 0,
          excludeBoxes: [frozenBox],
        }}
      />,
    );
    await waitFor(
      () =>
        expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeEnabled(),
      { timeout: 4000 },
    );
    fireEvent.click(screen.getByRole("button", { name: "识别当前图" }));
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.recognizeScreenshotImage",
        }),
      ),
    );
    const call = actions.run.mock.calls.find(
      ([action]) =>
        (action as { type?: string }).type ===
        "recognition.recognizeScreenshotImage",
    )?.[0] as {
      excludeBoxes?: { x: number; y: number; width: number; height: number }[];
    };
    const seeded = call.excludeBoxes?.[0];
    // 回投后再次归一化应回到冻结框（容差 1 个千分点）。
    expect(seeded?.x).toBeCloseTo(frozenBox.x, 0);
    expect(seeded?.y).toBeCloseTo(frozenBox.y, 0);
    expect(seeded?.width).toBeCloseTo(frozenBox.width, 0);
    expect(seeded?.height).toBeCloseTo(frozenBox.height, 0);
    unmount();
  });

  it("does not seed from a stale image when the baseline switches quickly", async () => {
    stubCanvasStack();
    const actions = createActions();
    const excludeBoxes = [{ x: 0, y: 0, width: 500, height: 1000 }];
    const { rerender, unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/first.png"
        session={{ sessionId: "session-switch", revision: 0, excludeBoxes }}
      />,
    );
    // 新基准在旧图解码完成前到达：不得用旧图尺寸播种。
    rerender(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/second.png"
        session={{ sessionId: "session-switch", revision: 0, excludeBoxes }}
      />,
    );
    await waitFor(
      () =>
        expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeEnabled(),
      { timeout: 4000 },
    );
    // 恰好一次播种（新基准），且不产生多余修订通知。
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({
        type: "recognition.notifyScreenshotRevision",
      }),
    );
    unmount();
  });

  it("seeds editable exclusion marks from the frozen baseline without notifying revisions", async () => {
    stubCanvasStack();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{
          sessionId: "session-seed",
          revision: 0,
          excludeBoxes: [{ x: 0, y: 0, width: 500, height: 1000 }],
        }}
      />,
    );
    await waitFor(
      () =>
        expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeEnabled(),
      { timeout: 4000 },
    );
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({
        type: "recognition.notifyScreenshotRevision",
      }),
    );
    unmount();
  });
});
