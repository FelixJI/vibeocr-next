import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions } from "../app/types";
import { ImageCanvasEditor } from "./ImageCanvasEditor";

afterEach(() => {
  cleanup();
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
  /** 可选：getImageData 固定返回值（画布取色 fallback 测试用）。 */
  sampleResult: undefined as Uint8ClampedArray | undefined,
};

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
        if (prop === "getImageData" && state.sampleResult) {
          return { data: state.sampleResult };
        }
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
    value: (callback: (blob: Blob) => void) =>
      queueMicrotask(() =>
        callback(
          new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], {
            type: "image/png",
          }),
        ),
      ),
  });
  class FakeImage {
    decoding = "async";
    onload: (() => void) | null = null;
    naturalWidth = natural?.width ?? 900;
    naturalHeight = natural?.height ?? 400;
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

interface FrameHarness {
  readonly frames: Map<number, FrameRequestCallback>;
  readonly flush: () => void;
  readonly cancelFrame: ReturnType<typeof vi.fn>;
}

function stubAnimationFrames(): FrameHarness {
  const frames = new Map<number, FrameRequestCallback>();
  let nextFrame = 0;
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    frames.set(++nextFrame, callback);
    return nextFrame;
  });
  const cancelFrame = vi.fn((id: number) => frames.delete(id));
  vi.stubGlobal("cancelAnimationFrame", cancelFrame);
  return {
    frames,
    cancelFrame,
    flush: () => {
      act(() => {
        const pending = [...frames.values()];
        frames.clear();
        pending.forEach((callback) => callback(0));
      });
    },
  };
}

function stubCaptureScene() {
  vi.stubGlobal("vibeocrCaptureScene", {
    x: 100,
    y: 100,
    width: 800,
    height: 400,
    desktopWidth: 2560,
    desktopHeight: 1440,
  });
}

function mockCanvasBox(
  canvas: HTMLCanvasElement,
  width: number,
  height: number,
) {
  vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
    left: 0,
    top: 0,
    width,
    height,
  } as DOMRect);
}

function getEditorCanvas(): HTMLCanvasElement {
  return screen.getByLabelText("图片检查画布") as HTMLCanvasElement;
}

function editorProps(
  overrides?: Partial<Parameters<typeof ImageCanvasEditor>[0]>,
) {
  return {
    actions: createActions(),
    canExport: true,
    canRecognize: true,
    source: "https://app.vibeocr/__resource/capture.png",
    session: { sessionId: "session-a", revision: 0 },
    ...overrides,
  };
}

async function renderEditor(
  overrides?: Partial<Parameters<typeof ImageCanvasEditor>[0]>,
) {
  const props = editorProps(overrides);
  const rendered = render(<ImageCanvasEditor {...props} />);
  await act(async () => {});
  return { ...rendered, actions: props.actions };
}

function allCalls(): RecordedCall[] {
  return state.canvases.flatMap((entry) => entry.calls);
}

describe("截图现场工具栏与裁剪交互", () => {
  it("截图现场不出现裁剪按钮，普通编辑仍保留", async () => {
    stubCanvasStack();
    stubAnimationFrames();
    stubCaptureScene();
    await renderEditor();
    expect(screen.queryByRole("button", { name: "裁剪" })).toBeNull();
    cleanup();

    vi.unstubAllGlobals();
    stubCanvasStack();
    stubAnimationFrames();
    await renderEditor();
    expect(screen.getByRole("button", { name: "裁剪" })).toBeInTheDocument();
  });

  it("拖动选区边/角把裁剪提交进历史并影响修订", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    stubCaptureScene();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 800, 400);
    harness.flush();

    // 悬停在选区右边缘中点（坐标 (800,200)）应出现 e-resize 光标。
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 792, clientY: 200 });
    expect(canvas.style.cursor).toBe("e-resize");
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 200 });
    expect(canvas.style.cursor).toBe("default");

    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 792, clientY: 200 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 392, clientY: 200 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 392, clientY: 200 });
    harness.flush();

    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.notifyScreenshotRevision",
      sessionId: "session-a",
      revision: 1,
    });
    // 提交后的裁剪虚线框：宽度收缩到 400（800-400），高度不变。
    const cropChrome = state.canvases
      .find((entry) => entry.element === canvas)!
      .calls.filter((call) => call.op === "strokeRect")
      .map((call) => call.args);
    expect(cropChrome).toContainEqual([0, 0, 400, 400]);

    // 撤销恢复整幅选区：不再有 400 宽的裁剪框。
    state.canvases.forEach((entry) => {
      entry.calls.length = 0;
    });
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    const afterUndo = state.canvases
      .find((entry) => entry.element === canvas)!
      .calls.filter((call) => call.op === "strokeRect")
      .map((call) => call.args);
    expect(afterUndo).not.toContainEqual([0, 0, 400, 400]);
    unmount();
  });

  it("拖动选区四角调整裁剪（se 角）", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    stubCaptureScene();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 800, 400);
    harness.flush();

    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 795, clientY: 395 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 300, clientY: 160 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 160 });
    harness.flush();
    const cropChrome = state.canvases
      .find((entry) => entry.element === canvas)!
      .calls.filter((call) => call.op === "strokeRect")
      .map((call) => call.args);
    expect(cropChrome).toContainEqual([0, 0, 305, 165]);
    unmount();
  });
});

describe("颜色工具：常用色、自定义与屏幕取色", () => {
  it("常用色色板直接切换标注颜色", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "常用颜色 #12a150" }));
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();

    const styles = allCalls()
      .filter((call) => call.op === "strokeStyle")
      .map((call) => call.args[0]);
    expect(styles).toContain("#12a150");
    // 色板选中态：只有当前色处于按下。
    expect(
      screen.getByRole("button", { name: "常用颜色 #12a150" }),
    ).toHaveAttribute("aria-pressed", "true");
    expect(
      screen.getByRole("button", { name: "常用颜色 #e02020" }),
    ).toHaveAttribute("aria-pressed", "false");
    unmount();
  });

  it("自定义颜色输入接受任意 #rrggbb", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.change(screen.getByLabelText("自定义颜色"), {
      target: { value: "#00c8ff" },
    });
    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();

    const styles = allCalls()
      .filter((call) => call.op === "strokeStyle")
      .map((call) => call.args[0]);
    expect(styles).toContain("#00c8ff");
    unmount();
  });

  it("支持 EyeDropper 时提供屏幕取色并应用结果色", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const open = vi.fn().mockResolvedValue({ sRGBHex: "#1234ab" });
    vi.stubGlobal(
      "EyeDropper",
      class {
        open = open;
      },
    );
    const { unmount } = await renderEditor();
    expect(
      screen.getByRole("button", { name: "屏幕取色" }),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "屏幕取色" }));
    await act(async () => {});
    await waitForStroke("#1234ab", harness, unmount);
  });

  it("真实图像首帧后设置 frameReady 供宿主门控揭示", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const props = editorProps();
    const { rerender, unmount } = render(<ImageCanvasEditor {...props} />);
    await act(async () => {});
    const canvas = getEditorCanvas();
    // 图像已解码（naturalWidth 可用）但未 flush rAF：尚未 ready。
    expect(canvas.dataset.frameReady).toBeUndefined();
    harness.flush();
    expect(canvas.dataset.frameReady).toBe("true");

    // 换图：resetKey 清理标记，新首帧就绪后重新标记。
    rerender(
      <ImageCanvasEditor {...props} source="https://app.vibeocr/new.png" />,
    );
    await act(async () => {});
    expect(canvas.dataset.frameReady).toBeUndefined();
    harness.flush();
    expect(canvas.dataset.frameReady).toBe("true");
    unmount();
  });

  it("不支持 EyeDropper 时提供一次性画布取色且不产生标记", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    const pick = screen.getByRole("button", { name: "屏幕取色" });
    expect(pick).toHaveAttribute("title", "取色：点击后在画布上取样颜色");
    fireEvent.click(pick);
    expect(screen.getByText(/取色模式：在画布上点击/)).toBeInTheDocument();
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 300, clientY: 200 });
    expect(canvas.style.cursor).toBe("crosshair");

    state.sampleResult = new Uint8ClampedArray([31, 111, 235, 255]);
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 120, clientY: 80 });
    expect(screen.getByText("已从画布取色 #1f6feb。")).toBeInTheDocument();
    // 取样点击不产生任何标注/修订。
    expect(actions.run).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();

    // 取到的色作为后续标注颜色，且写入无 chrome 的离屏采样。
    const sampled = state.canvases.find(
      (entry) =>
        entry.element !== canvas &&
        entry.calls.some((call) => call.op === "getImageData"),
    );
    expect(sampled).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 2, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 400, clientY: 300 });
    harness.flush();
    const styles = allCalls()
      .filter((call) => call.op === "strokeStyle")
      .map((call) => call.args[0]);
    expect(styles).toContain("#1f6feb");
    state.sampleResult = undefined;
    unmount();
  });

  it("Esc 取消画布取色且不改变颜色", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "屏幕取色" }));
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(
      screen.getByText("已取消取色；标注颜色保持不变。"),
    ).toBeInTheDocument();
    state.sampleResult = new Uint8ClampedArray([224, 32, 32, 255]);
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 120, clientY: 80 });
    // 取样已取消：点击落到普通选择工具，不产生取色/标记。
    expect(screen.queryByText(/已从画布取色/)).toBeNull();
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
    state.sampleResult = undefined;
    unmount();
  });
});

async function waitForStroke(
  color: string,
  harness: FrameHarness,
  unmount: () => void,
) {
  const canvas = getEditorCanvas();
  mockCanvasBox(canvas, 900, 600);
  fireEvent.click(screen.getByRole("button", { name: "矩形" }));
  fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
  fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
  harness.flush();
  fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
  harness.flush();
  const styles = allCalls()
    .filter((call) => call.op === "strokeStyle")
    .map((call) => call.args[0]);
  expect(styles).toContain(color);
  unmount();
}

describe("马赛克与模糊强度", () => {
  it("马赛克强度改变块尺寸：弱块小、强块大", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    const drag = () => {
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 100,
        clientY: 50,
      });
      fireEvent.pointerMove(canvas, {
        pointerId: 1,
        clientX: 500,
        clientY: 350,
      });
      harness.flush();
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 500, clientY: 350 });
      harness.flush();
    };

    fireEvent.click(screen.getByRole("button", { name: "马赛克" }));
    fireEvent.change(screen.getByLabelText("马赛克与模糊强度"), {
      target: { value: "1" },
    });
    drag();
    // 弱：400×300 区域 / 8px 块 → 50×38 下采样面。
    expect(state.canvases.some((entry) => entry.element.width === 50)).toBe(
      true,
    );
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("马赛克与模糊强度"), {
      target: { value: "3" },
    });
    state.canvases.forEach((entry) => {
      entry.calls.length = 0;
    });
    drag();
    // 强：400×300 区域 / 26px 块 → 16×12 下采样面。
    expect(state.canvases.some((entry) => entry.element.width === 16)).toBe(
      true,
    );
    unmount();
  });

  it("模糊强度改变滤波半径：弱 5px、强 20px", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    const drag = () => {
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 100,
        clientY: 50,
      });
      fireEvent.pointerMove(canvas, {
        pointerId: 1,
        clientX: 500,
        clientY: 350,
      });
      harness.flush();
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 500, clientY: 350 });
      harness.flush();
    };

    fireEvent.click(screen.getByRole("button", { name: "模糊" }));
    fireEvent.change(screen.getByLabelText("马赛克与模糊强度"), {
      target: { value: "1" },
    });
    drag();
    const filters = () =>
      allCalls()
        .filter((call) => call.op === "filter")
        .map((call) => call.args[0]);
    expect(filters()).toContain("blur(5px)");
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("马赛克与模糊强度"), {
      target: { value: "3" },
    });
    drag();
    expect(filters()).toContain("blur(20px)");
    unmount();
  });
});

describe("切换编辑按钮的绘制合并回归", () => {
  it("连续切换工具合并为单帧且不重算像素效果", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 先提交一个模糊标记：产生一次 filter 通过。
    fireEvent.click(screen.getByRole("button", { name: "模糊" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 50 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 300, clientY: 250 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 250 });
    harness.flush();
    const effectPasses = allCalls().filter(
      (call) => call.op === "filter",
    ).length;
    expect(effectPasses).toBeGreaterThan(0);

    state.canvases.forEach((entry) => {
      entry.calls.length = 0;
    });
    // 连续切换 4 个工具按钮：不得逐帧重绘/重采样。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByRole("button", { name: "箭头" }));
    fireEvent.click(screen.getByRole("button", { name: "文字" }));
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    expect(harness.frames.size).toBe(1);
    harness.flush();

    // 像素效果零重算：全部来自缓存前缀。
    expect(allCalls().filter((call) => call.op === "filter")).toHaveLength(0);
    // 单帧内 clearRect 紧跟缓存画布回贴，不出现可见空白帧。
    const mainCalls = state.canvases.find(
      (entry) => entry.element === canvas,
    )!.calls;
    const cleared = mainCalls.findIndex((call) => call.op === "clearRect");
    expect(cleared).toBeGreaterThanOrEqual(0);
    const restored = mainCalls.findIndex(
      (call, index) =>
        index > cleared &&
        call.op === "drawImage" &&
        call.args[0] instanceof HTMLCanvasElement,
    );
    expect(restored).toBeGreaterThan(cleared);
    unmount();
  });
});
