import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  within,
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
        if (prop === "measureText")
          return { width: String(args[0]).length * 12 };
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

const captureScene = {
  surfaceId: "surface",
  captureSessionId: "session-a",
  inputUrl: "https://app.vibeocr/__resource/capture.png",
  inputRevision: 0,
  documentGeneration: 1,
  bootstrapGeneration: 1,
  initialTool: "select",
  x: 100,
  y: 100,
  width: 800,
  height: 400,
  desktopWidth: 2560,
  desktopHeight: 1440,
};

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
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    await renderEditor({ captureScene });
    expect(screen.queryByRole("button", { name: "裁剪" })).toBeNull();
    cleanup();

    vi.unstubAllGlobals();
    stubCanvasStack();
    stubAnimationFrames();
    await renderEditor();
    expect(screen.getByRole("button", { name: "裁剪" })).toBeInTheDocument();
  });

  it("拖动选区边/角把裁剪提交进历史并影响修订", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor({ captureScene });
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
    stubCanvasStack({ width: 800, height: 400 });
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor({ captureScene });
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

    // 颜色/取色属于当前工具属性：先切到颜色工具，属性排才出现色板。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByRole("button", { name: "常用颜色 #12a150" }));
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

  it("自定义颜色输入接受任意 #rrggbb；颜色按工具独立记忆", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 先切颜色工具，自定义颜色输入才在属性排出现；AC3 起各工具记忆
    // 各自属性：在椭圆工具下设置的颜色供椭圆绘制使用。
    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    fireEvent.change(screen.getByLabelText("自定义颜色"), {
      target: { value: "#00c8ff" },
    });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();

    const styles = allCalls()
      .filter((call) => call.op === "strokeStyle")
      .map((call) => call.args[0]);
    expect(styles).toContain("#00c8ff");
    // 切回椭圆工具：颜色记忆保留（输入框仍显示上次取值）。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    expect(screen.getByLabelText("自定义颜色")).toHaveValue("#00c8ff");
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
    // 屏幕取色入口随颜色工具的属性排出现。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
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

    // 取色入口随颜色工具的属性排出现。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
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

    // 取色入口随颜色工具的属性排出现。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByRole("button", { name: "屏幕取色" }));
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(
      screen.getByText("已取消取色；标注颜色保持不变。"),
    ).toBeInTheDocument();
    state.sampleResult = new Uint8ClampedArray([224, 32, 32, 255]);
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 120, clientY: 80 });
    // 取样已取消：点击落到当前绘图工具，不产生取色/标记提交。
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
  it("R2 rectangle mosaic export scales its legacy cell width once", async () => {
    stubCanvasStack({ width: 1800, height: 1200 });
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    fireEvent.click(screen.getByRole("button", { name: "马赛克" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 56, clientY: 150 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 784, clientY: 202 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 784, clientY: 202 });
    harness.flush();
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "保存标注图" }));
    });
    const natural = allCalls().find(
      (call) =>
        call.op === "drawImage" &&
        call.args[0] instanceof HTMLCanvasElement &&
        call.args[1] === 112 &&
        call.args[2] === 300 &&
        call.args[3] === 1456,
    );
    expect(natural).toBeDefined();
    expect((natural!.args[0] as HTMLCanvasElement).width).toBe(52);
    unmount();
  });

  it.each([false, true])(
    "R3 locked effects and locked groups cannot change intensity (group=%s)",
    async (group) => {
      stubCanvasStack();
      const harness = stubAnimationFrames();
      const { actions, unmount } = await renderEditor();
      const canvas = getEditorCanvas();
      mockCanvasBox(canvas, 900, 600);
      fireEvent.click(screen.getByRole("button", { name: "马赛克" }));
      const drag = (x: number) => {
        fireEvent.pointerDown(canvas, {
          pointerId: 1,
          clientX: x,
          clientY: 100,
        });
        fireEvent.pointerMove(canvas, {
          pointerId: 1,
          clientX: x + 100,
          clientY: 200,
        });
        fireEvent.pointerUp(canvas, {
          pointerId: 1,
          clientX: x + 100,
          clientY: 200,
        });
        harness.flush();
      };
      drag(100);
      if (group) drag(350);
      fireEvent.click(screen.getByRole("button", { name: "选择" }));
      fireEvent.click(screen.getByRole("button", { name: "全选对象" }));
      if (group) fireEvent.click(screen.getByRole("button", { name: "组合" }));
      fireEvent.click(screen.getByRole("button", { name: "锁定" }));
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: "0" },
      });
      vi.mocked(actions.run).mockClear();
      const slider = screen.getByLabelText("马赛克与模糊强度");
      fireEvent.change(slider, { target: { value: "2.4" } });
      expect(actions.run).not.toHaveBeenCalled();
      expect(slider).toBeDisabled();
      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: "0" },
      });
      expect(screen.getByRole("button", { name: "锁定" })).toBeEnabled();
      unmount();
    },
  );

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

describe("首帧输入身份", () => {
  it("拒绝与 bootstrap 资源不相同的源图，保持有界失败", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    const harness = stubAnimationFrames();
    const reportCapturePixels = vi.fn();
    const actions = { ...createActions(), reportCapturePixels };
    const wrong = {
      ...captureScene,
      inputUrl: "https://app.vibeocr/__resource/another",
    };
    const { unmount } = await renderEditor({ actions, captureScene: wrong });
    harness.flush();
    expect(reportCapturePixels).toHaveBeenCalledExactlyOnceWith(
      wrong,
      "identity",
    );
    unmount();
    harness.flush();
    expect(reportCapturePixels).toHaveBeenCalledTimes(1);
  });
});

describe("Windows document commands on the editor surface (Ctrl+S / Ctrl+W)", () => {
  function focusDocument(focused: boolean) {
    Object.defineProperty(document, "hasFocus", {
      configurable: true,
      value: () => focused,
    });
  }

  afterEach(() => {
    delete (document as { hasFocus?: () => boolean }).hasFocus;
  });

  it("Ctrl+S enters the same save entry as the toolbar button", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    focusDocument(true);
    const { actions } = await renderEditor();
    const canvas = getEditorCanvas();
    fireEvent.keyDown(canvas, { key: "s", ctrlKey: true, bubbles: true });
    // 与“保存标注图”按钮同一回调：导出前同步提示出现（按钮可用条件一致）。
    expect(
      screen.getByText("正在生成标注图片并打开系统保存窗口……"),
    ).toBeInTheDocument();
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.closeScreenshotSession" }),
    );
  });

  it("document commands still run when focus is outside the editor container", async () => {
    // 回归：Ctrl+S/W 曾只挂在 .canvas-editor 容器 onKeyDown 上，焦点在
    // 工作台“选择图片”按钮或导航时不触发；现与 PdfPage 一样注册 window。
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    focusDocument(true);
    const { actions } = await renderEditor();
    const outside = document.createElement("button");
    outside.type = "button";
    document.body.appendChild(outside);
    outside.focus();
    try {
      fireEvent.keyDown(window, { key: "w", ctrlKey: true });
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.closeScreenshotSession",
      });
      fireEvent.keyDown(window, { key: "s", ctrlKey: true });
      expect(
        screen.getByText("正在生成标注图片并打开系统保存窗口……"),
      ).toBeInTheDocument();
    } finally {
      outside.remove();
    }
  });

  it("Ctrl+W does not close an image without a screenshot session", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    focusDocument(true);
    const { actions } = await renderEditor({ session: undefined });
    fireEvent.keyDown(getEditorCanvas(), { key: "w", ctrlKey: true });
    fireEvent.keyDown(window, { key: "w", ctrlKey: true });
    expect(actions.run).not.toHaveBeenCalled();
  });
  it("Ctrl+W ends the session through the existing close command", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    focusDocument(true);
    const { actions } = await renderEditor();
    fireEvent.keyDown(getEditorCanvas(), {
      key: "w",
      ctrlKey: true,
      bubbles: true,
    });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.closeScreenshotSession",
    });
  });

  it("IME composition keys do not trigger save/close/undo layers", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    focusDocument(true);
    const { actions } = await renderEditor();
    const canvas = getEditorCanvas();
    const imeCommand = (key: string) => {
      const event = new KeyboardEvent("keydown", {
        key,
        ctrlKey: true,
        bubbles: true,
      });
      Object.defineProperty(event, "keyCode", { value: 229 });
      fireEvent(canvas, event);
    };
    imeCommand("w");
    imeCommand("s");
    fireEvent.keyDown(canvas, {
      key: "w",
      ctrlKey: true,
      isComposing: true,
      bubbles: true,
    });
    expect(actions.run).not.toHaveBeenCalled();
    expect(
      screen.queryByText("正在生成标注图片并打开系统保存窗口……"),
    ).not.toBeInTheDocument();
  });

  it("hidden or unfocused pages do not run editor document commands", async () => {
    stubCanvasStack({ width: 800, height: 400 });
    stubAnimationFrames();
    focusDocument(false);
    const { actions } = await renderEditor();
    fireEvent.keyDown(getEditorCanvas(), {
      key: "w",
      ctrlKey: true,
      bubbles: true,
    });
    expect(actions.run).not.toHaveBeenCalled();

    focusDocument(true);
    Object.defineProperty(document, "hidden", {
      configurable: true,
      get: () => true,
    });
    fireEvent.keyDown(getEditorCanvas(), {
      key: "w",
      ctrlKey: true,
      bubbles: true,
    });
    delete (document as { hidden?: boolean }).hidden;
    expect(actions.run).not.toHaveBeenCalled();
  });
});

describe("形状边框/填充不透明度（canvas 调用契约）", () => {
  function clearCalls() {
    state.canvases.forEach((entry) => {
      entry.calls.length = 0;
    });
  }

  async function drawRectangle(
    harness: FrameHarness,
    canvas: HTMLCanvasElement,
  ) {
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
  }

  it("默认矩形保持旧空心效果：无填充、边框完全不透明", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    clearCalls();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await drawRectangle(harness, canvas);

    // 旧对象/默认样式：不产生任何 fillRect，globalAlpha 全为 1。
    const calls = allCalls();
    expect(calls.some((call) => call.op === "fillRect")).toBe(false);
    const alphas = calls
      .filter((call) => call.op === "globalAlpha")
      .map((call) => call.args[0]);
    expect(alphas.length).toBeGreaterThan(0);
    expect(new Set(alphas)).toEqual(new Set([1]));
    expect(calls.find((call) => call.op === "strokeRect")?.args).toEqual([
      100, 100, 300, 200,
    ]);
    unmount();
  });

  it("矩形填充与边框不透明度按序写入真实绘制调用（先填充后描边）", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByLabelText("填充"));
    fireEvent.change(screen.getByLabelText("填充颜色"), {
      target: { value: "#ff0000" },
    });
    const fillOpacity = screen.getByLabelText("填充不透明度");
    fireEvent.change(fillOpacity, { target: { value: "40" } });
    fireEvent.blur(fillOpacity);
    const strokeOpacity = screen.getByLabelText("边框不透明度");
    fireEvent.change(strokeOpacity, { target: { value: "60" } });
    fireEvent.blur(strokeOpacity);
    clearCalls();
    await drawRectangle(harness, canvas);

    // 含 fillRect 的离屏缓存画布承载真实绘制序列。
    const recorded = state.canvases.find((entry) =>
      entry.calls.some((call) => call.op === "fillRect"),
    )!;
    expect(recorded).toBeDefined();
    const calls = recorded.calls;
    const fillAlpha = calls.findIndex(
      (call) => call.op === "globalAlpha" && call.args[0] === 0.4,
    );
    expect(fillAlpha).toBeGreaterThanOrEqual(0);
    const afterFillAlpha = calls.slice(fillAlpha);
    const fillStyleIndex = afterFillAlpha.findIndex(
      (call) => call.op === "fillStyle" && call.args[0] === "#ff0000",
    );
    expect(fillStyleIndex).toBeGreaterThanOrEqual(0);
    expect(
      afterFillAlpha
        .slice(fillStyleIndex)
        .find((call) => call.op === "fillRect")?.args,
    ).toEqual([100, 100, 300, 200]);
    // 边框不透明度 60%：globalAlpha 0.6 在填充之后、strokeRect 之前生效。
    const strokeAlpha = calls.findIndex(
      (call) => call.op === "globalAlpha" && call.args[0] === 0.6,
    );
    expect(strokeAlpha).toBeGreaterThan(fillAlpha);
    expect(
      calls.slice(strokeAlpha).find((call) => call.op === "strokeRect")?.args,
    ).toEqual([100, 100, 300, 200]);
    unmount();
  });

  it("荧光笔复用边框不透明度且不破坏自身 35% 透明语义", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "荧光笔" }));
    // 旧默认（100%）：保持 0.35 不变。
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 200, clientY: 150 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 200, clientY: 150 });
    harness.flush();
    expect(
      allCalls().some(
        (call) => call.op === "globalAlpha" && call.args[0] === 0.35,
      ),
    ).toBe(true);

    // 50% 边框不透明度：与 0.35 相乘 → 0.175。
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("边框不透明度"), {
      target: { value: "50" },
    });
    fireEvent.blur(screen.getByLabelText("边框不透明度"));
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 2, clientX: 200, clientY: 150 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 200, clientY: 150 });
    harness.flush();
    expect(
      allCalls().some(
        (call) => call.op === "globalAlpha" && call.args[0] === 0.175,
      ),
    ).toBe(true);
    unmount();
  });

  it("选中已有矩形修改边框/填充属性入历史，撤销逐级恢复", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 先创建一个默认空心矩形。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await drawRectangle(harness, canvas);
    expect(actions.run).toHaveBeenCalledTimes(1);

    // 切回选择工具并点选该矩形：属性排按标记样式展开。
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 200, clientY: 150 });
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 200, clientY: 150 });
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    expect(settings.getByLabelText("边框不透明度")).toBeInTheDocument();
    expect(settings.getByLabelText("填充")).not.toBeChecked();

    // 边框不透明度 0% 有效：写入真实 globalAlpha 0。
    const opacity = settings.getByLabelText("边框不透明度");
    fireEvent.change(opacity, { target: { value: "0" } });
    fireEvent.blur(opacity);
    expect(actions.run).toHaveBeenCalledTimes(2);
    clearCalls();
    harness.flush();
    expect(
      allCalls().some(
        (call) => call.op === "globalAlpha" && call.args[0] === 0,
      ),
    ).toBe(true);

    // 开启填充：填充像素出现在绘制流中。
    fireEvent.click(settings.getByLabelText("填充"));
    expect(actions.run).toHaveBeenCalledTimes(3);
    clearCalls();
    harness.flush();
    expect(allCalls().some((call) => call.op === "fillRect")).toBe(true);

    // 改填充色保留已有不透明度（50），改不透明度保留已有色：
    // 不得用绘制默认值覆盖另一维。
    fireEvent.change(settings.getByLabelText("填充颜色"), {
      target: { value: "#ff0000" },
    });
    clearCalls();
    harness.flush();
    const fillCalls = () => allCalls();
    const redFill = fillCalls().findIndex(
      (call) => call.op === "fillStyle" && call.args[0] === "#ff0000",
    );
    expect(redFill).toBeGreaterThanOrEqual(0);
    expect(
      fillCalls()
        .slice(Math.max(0, redFill - 3), redFill + 1)
        .some((call) => call.op === "globalAlpha" && call.args[0] === 0.5),
    ).toBe(true);
    const markOpacity = settings.getByLabelText("填充不透明度");
    fireEvent.change(markOpacity, { target: { value: "80" } });
    fireEvent.blur(markOpacity);
    clearCalls();
    harness.flush();
    const redFill2 = fillCalls().findIndex(
      (call) => call.op === "fillStyle" && call.args[0] === "#ff0000",
    );
    expect(redFill2).toBeGreaterThanOrEqual(0);
    expect(
      fillCalls()
        .slice(Math.max(0, redFill2 - 3), redFill2 + 1)
        .some((call) => call.op === "globalAlpha" && call.args[0] === 0.8),
    ).toBe(true);

    // 逐级撤销验证恢复链：80% → 50%（保留原色）→ 原色 #ffffff/50 →
    // 空心 → 原始矩形；不跳过任何一级历史检查。
    const stepUndo = (expectFill?: { color: string; alpha: number }) => {
      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      clearCalls();
      harness.flush();
      const calls = allCalls();
      if (expectFill) {
        const fillIndex = calls.findIndex(
          (call) =>
            call.op === "fillStyle" && call.args[0] === expectFill.color,
        );
        expect(fillIndex).toBeGreaterThanOrEqual(0);
        expect(
          calls
            .slice(Math.max(0, fillIndex - 3), fillIndex + 1)
            .some(
              (call) =>
                call.op === "globalAlpha" && call.args[0] === expectFill.alpha,
            ),
        ).toBe(true);
      } else {
        expect(calls.some((call) => call.op === "fillRect")).toBe(false);
      }
    };
    stepUndo({ color: "#ff0000", alpha: 0.5 });
    stepUndo({ color: "#ffffff", alpha: 0.5 });
    stepUndo();
    expect(allCalls().some((call) => call.op === "strokeRect")).toBe(true);
    // 撤销清除选择：对象入口保留，未选中时不显示样式属性。
    expect(screen.getByLabelText("选择标注对象")).toHaveValue("");
    expect(screen.queryByLabelText("边框不透明度")).toBeNull();
    // 再撤销：边框回到完全不透明。
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    clearCalls();
    harness.flush();
    expect(
      allCalls()
        .filter((call) => call.op === "globalAlpha")
        .every((call) => call.args[0] === 1),
    ).toBe(true);
    expect(allCalls().some((call) => call.op === "strokeRect")).toBe(true);
    unmount();
  });
});

describe("不透明度数值输入契约", () => {
  it("清空/越界不写 0；Enter 提交任意整数并写入真实绘制", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    const opacity = screen.getByLabelText("边框不透明度");
    expect(opacity).toHaveValue(100);
    // 清空后 blur：不意外写 0，显示回退已提交值 100，也不产生修订。
    fireEvent.change(opacity, { target: { value: "" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(100);
    expect(actions.run).not.toHaveBeenCalled();
    // 越界同样不写回。
    fireEvent.change(opacity, { target: { value: "101" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(100);
    // Enter 提交任意整数 37（非预设档位）：真实焦点下 Enter 触发 blur 提交，
    // 提交结果由后续绘制的 globalAlpha 0.37 证明（不经 fireEvent.blur 兜底）。
    act(() => {
      opacity.focus();
    });
    fireEvent.change(opacity, { target: { value: "37" } });
    fireEvent.keyDown(opacity, { key: "Enter" });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    expect(actions.run).toHaveBeenCalledTimes(1);
    expect(
      state.canvases
        .flatMap((entry) => entry.calls)
        .some((call) => call.op === "globalAlpha" && call.args[0] === 0.37),
    ).toBe(true);
    unmount();
  });
});

describe("OBJ-03 对象比例装饰", () => {
  it("荧光笔最小宽随对象缩小到80%，一次撤销恢复12px", async () => {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    fireEvent.click(screen.getByRole("button", { name: "荧光笔" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 200, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 100 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    fireEvent.click(screen.getByRole("button", { name: "缩小选区" }));
    for (const holder of state.canvases) holder.calls.length = 0;
    harness.flush();
    expect(
      allCalls().some(
        (call) =>
          call.op === "lineWidth" &&
          Math.abs(Number(call.args[0]) - 9.6) < 0.00001,
      ),
    ).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    for (const holder of state.canvases) holder.calls.length = 0;
    harness.flush();
    expect(
      allCalls().some((call) => call.op === "lineWidth" && call.args[0] === 12),
    ).toBe(true);
    unmount();
  });
  it.each([
    ["dashed", [15, 10]],
    ["dotted", [2.5, 7.5]],
  ] as const)(
    "%s的固定线节随125%%对象放大，线宽不重复缩放",
    async (lineStyle, expected) => {
      stubCanvasStack({ width: 900, height: 600 });
      const harness = stubAnimationFrames();
      const { unmount } = await renderEditor();
      const canvas = getEditorCanvas();
      mockCanvasBox(canvas, 900, 600);
      fireEvent.click(screen.getByRole("button", { name: "直线" }));
      fireEvent.change(screen.getByLabelText("线型"), {
        target: { value: lineStyle },
      });
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 100,
        clientY: 100,
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 100 });
      fireEvent.click(screen.getByRole("button", { name: "选择" }));
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: "0" },
      });
      fireEvent.click(screen.getByRole("button", { name: "放大选区" }));
      for (const holder of state.canvases) holder.calls.length = 0;
      harness.flush();
      expect(
        allCalls().some(
          (call) =>
            call.op === "setLineDash" &&
            JSON.stringify(call.args[0]) === JSON.stringify(expected),
        ),
      ).toBe(true);
      expect(
        allCalls().some(
          (call) => call.op === "lineWidth" && call.args[0] === 3.75,
        ),
      ).toBe(true);
      unmount();
    },
  );
});

describe("PR249 不透明度草稿提交", () => {
  async function prepareTwoObjects() {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const rendered = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByLabelText("填充"));
    const drawBox = (x: number) => {
      fireEvent.pointerDown(canvas, { pointerId: 1, clientX: x, clientY: 100 });
      fireEvent.pointerUp(canvas, {
        pointerId: 1,
        clientX: x + 100,
        clientY: 180,
      });
    };
    const setFill = (color: string, opacity: string) => {
      fireEvent.change(screen.getByLabelText("填充颜色"), {
        target: { value: color },
      });
      fireEvent.change(screen.getByLabelText("填充不透明度"), {
        target: { value: opacity },
      });
      fireEvent.blur(screen.getByLabelText("填充不透明度"));
    };
    setFill("#e02020", "20");
    drawBox(100);
    setFill("#1f6feb", "80");
    drawBox(400);
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    return { ...rendered, canvas, harness };
  }
  it.each(["边框不透明度", "填充不透明度"])(
    "%s输入3→37只在失焦提交一次，非法草稿不新增历史",
    async (label) => {
      const { actions, unmount } = await prepareTwoObjects();
      const initial = label === "边框不透明度" ? 100 : 20;
      const input = screen.getByLabelText(label);
      vi.mocked(actions.run).mockClear();
      fireEvent.change(input, { target: { value: "3" } });
      fireEvent.change(input, { target: { value: "37" } });
      expect(actions.run).not.toHaveBeenCalled();
      fireEvent.blur(input);
      expect(actions.run).toHaveBeenCalledTimes(1);
      fireEvent.change(input, { target: { value: "" } });
      fireEvent.blur(input);
      expect(input).toHaveValue(37);
      fireEvent.change(input, { target: { value: "101" } });
      fireEvent.blur(input);
      expect(input).toHaveValue(37);
      expect(actions.run).toHaveBeenCalledTimes(1);
      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: "0" },
      });
      expect(screen.getByLabelText(label)).toHaveValue(initial);
      unmount();
    },
  );
  it("填充草稿跨选区提交仍保留原对象颜色，且只提交一次", async () => {
    const { actions, unmount } = await prepareTwoObjects();
    const input = screen.getByLabelText("填充不透明度");
    vi.mocked(actions.run).mockClear();
    fireEvent.change(input, { target: { value: "37" } });
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    fireEvent.blur(input);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("填充颜色")).toHaveValue("#e02020");
    expect(screen.getByLabelText("填充不透明度")).toHaveValue(37);
    expect(actions.run).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    expect(screen.getByLabelText("填充颜色")).toHaveValue("#1f6feb");
    expect(screen.getByLabelText("填充不透明度")).toHaveValue(80);
    unmount();
  });
  it.each(["删除", "锁定"])(
    "草稿目标%s后不写入对象或新绘图工具记忆",
    async (action) => {
      const { canvas, unmount } = await prepareTwoObjects();
      fireEvent.change(screen.getByLabelText("填充不透明度"), {
        target: { value: "37" },
      });
      if (action === "删除") fireEvent.keyDown(canvas, { key: "Delete" });
      else fireEvent.click(screen.getByRole("button", { name: "锁定" }));
      fireEvent.click(screen.getByRole("button", { name: "矩形" }));
      fireEvent.blur(screen.getByLabelText("填充不透明度"));
      expect(screen.getByLabelText("填充不透明度")).toHaveValue(80);
      if (action === "锁定") {
        fireEvent.click(screen.getByRole("button", { name: "选择" }));
        fireEvent.change(screen.getByLabelText("选择标注对象"), {
          target: { value: "0" },
        });
        expect(screen.getByLabelText("填充不透明度")).toHaveValue(20);
      }
      unmount();
    },
  );
});

describe("PR249 第二轮对象交互", () => {
  async function prepareSelection() {
    stubCanvasStack({ width: 900, height: 600 });
    stubAnimationFrames();
    const rendered = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    for (const x of [100, 300]) {
      fireEvent.pointerDown(canvas, { pointerId: 1, clientX: x, clientY: 100 });
      fireEvent.pointerUp(canvas, {
        pointerId: 1,
        clientX: x + 100,
        clientY: 180,
      });
    }
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.keyDown(canvas, { key: "a", ctrlKey: true });
    return { ...rendered, canvas };
  }
  it("重复组合不追加历史，一次撤销恢复未分组", async () => {
    const { actions, unmount } = await prepareSelection();
    fireEvent.click(screen.getByRole("button", { name: "组合" }));
    vi.mocked(actions.run).mockClear();
    fireEvent.click(screen.getByRole("button", { name: "组合" }));
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    expect(screen.queryByRole("button", { name: "解组" })).toBeNull();
    unmount();
  });
  it.each(["矩形", "画笔", "折线"])(
    "%s绘制上下文忽略旧选区对象快捷键，Enter仍完成草稿",
    async (tool) => {
      const { actions, canvas, unmount } = await prepareSelection();
      fireEvent.keyDown(canvas, { key: "c", ctrlKey: true });
      fireEvent.click(screen.getByRole("button", { name: tool }));
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 500,
        clientY: 300,
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 550, clientY: 330 });
      vi.mocked(actions.run).mockClear();
      for (const event of [
        { key: "d", ctrlKey: true },
        { key: "v", ctrlKey: true },
        { key: "ArrowRight" },
        { key: "Delete" },
        { key: "a", ctrlKey: true },
        { key: "c", ctrlKey: true },
      ])
        fireEvent.keyDown(canvas, event);
      expect(actions.run).not.toHaveBeenCalled();
      if (tool === "折线") {
        fireEvent.pointerDown(canvas, {
          pointerId: 1,
          clientX: 600,
          clientY: 350,
        });
        fireEvent.pointerUp(canvas, {
          pointerId: 1,
          clientX: 600,
          clientY: 350,
        });
        fireEvent.keyDown(canvas, { key: "Enter" });
        expect(actions.run).toHaveBeenCalledTimes(1);
      }
      fireEvent.click(screen.getByRole("button", { name: "选择" }));
      expect(
        screen.getByLabelText("选择标注对象").querySelectorAll("option"),
      ).toHaveLength(4);
      unmount();
    },
  );
});

describe("PR249 主对象恢复与连续样式", () => {
  it("缩放字号提供当前30px选项，查看不提交历史", async () => {
    stubCanvasStack({ width: 900, height: 600 });
    stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    fireEvent.click(screen.getByRole("button", { name: "文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "F2Ab" },
    });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 200 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 200 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    fireEvent.click(screen.getByRole("button", { name: "放大选区" }));
    vi.mocked(actions.run).mockClear();
    expect(screen.getByLabelText("文字字号")).toHaveValue("30");
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("文字字号")).toHaveValue("24");
    unmount();
  });
  async function prepare() {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const rendered = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.change(screen.getByLabelText("矩形圆角"), {
      target: { value: "6" },
    });
    for (const x of [100, 700]) {
      fireEvent.pointerDown(canvas, { pointerId: 1, clientX: x, clientY: 270 });
      fireEvent.pointerUp(canvas, {
        pointerId: 1,
        clientX: x + 100,
        clientY: 330,
      });
    }
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    return { ...rendered, canvas, harness };
  }
  it.each(["0", "1"])(
    "对称离屏组以主对象%s恢复，整组间距与一次历史不变",
    async (primary) => {
      const { actions, canvas, harness, unmount } = await prepare();
      fireEvent.keyDown(canvas, { key: "a", ctrlKey: true });
      fireEvent.click(screen.getByRole("button", { name: "组合" }));
      for (let i = 0; i < 3; i++)
        fireEvent.click(screen.getByRole("button", { name: "放大选区" }));
      harness.flush();
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: primary },
      });
      expect(
        screen.getByLabelText("选择标注对象").querySelectorAll("option")[
          Number(primary) + 1
        ],
      ).toHaveTextContent("画布外");
      vi.mocked(actions.run).mockClear();
      fireEvent.click(screen.getByRole("button", { name: "对象回中心" }));
      for (const holder of state.canvases) holder.calls.length = 0;
      harness.flush();
      const memberX = [
        ...new Set(
          allCalls()
            .filter(
              (call) => call.op === "roundRect" && call.args[2] === 195.3125,
            )
            .map((call) => Number(call.args[0])),
        ),
      ].sort((a, b) => a - b);
      expect(memberX).toEqual(
        primary === "0" ? [352.34375, 1524.21875] : [-819.53125, 352.34375],
      );
      expect(
        screen.getByLabelText("选择标注对象").querySelectorAll("option")[
          Number(primary) + 1
        ],
      ).not.toHaveTextContent("画布外");
      expect(actions.run).toHaveBeenCalledTimes(1);
      expect(screen.getByRole("button", { name: "解组" })).toBeVisible();
      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      harness.flush();
      expect(
        screen.getByLabelText("选择标注对象").querySelectorAll("option")[
          Number(primary) + 1
        ],
      ).toHaveTextContent("画布外");
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: primary },
      });
      fireEvent.click(screen.getByRole("button", { name: "锁定" }));
      expect(screen.queryByRole("button", { name: "对象回中心" })).toBeNull();
      unmount();
    },
  );
  it("缩放后的线宽和圆角显示真实值，查看不提交，改档只更新目标", async () => {
    const { actions, unmount } = await prepare();
    fireEvent.click(screen.getByRole("button", { name: "放大选区" }));
    vi.mocked(actions.run).mockClear();
    expect(screen.getByLabelText("线条宽度")).toHaveValue("3.75");
    expect(screen.getByLabelText("矩形圆角")).toHaveValue("7.5");
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText("线条宽度"), {
      target: { value: "5" },
    });
    fireEvent.change(screen.getByLabelText("矩形圆角"), {
      target: { value: "12" },
    });
    expect(actions.run).toHaveBeenCalledTimes(2);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    expect(screen.getByLabelText("线条宽度")).toHaveValue("3");
    expect(screen.getByLabelText("矩形圆角")).toHaveValue("6");
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("线条宽度")).toHaveValue("5");
    expect(screen.getByLabelText("矩形圆角")).toHaveValue("7.5");
    unmount();
  });
});

describe("effect brush stamps", () => {
  it.each(["马赛克", "模糊"])(
    "%s commits a single click and permits one undo",
    async (label) => {
      stubCanvasStack({ width: 900, height: 600 });
      const harness = stubAnimationFrames();
      const { unmount } = await renderEditor();
      const canvas = getEditorCanvas();
      mockCanvasBox(canvas, 900, 600);
      harness.flush();
      fireEvent.click(screen.getByRole("button", { name: label }));
      fireEvent.change(screen.getByLabelText("马赛克与模糊绘制方式"), {
        target: { value: "brush" },
      });
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 300,
        clientY: 300,
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 300 });
      harness.flush();
      expect(screen.getByRole("button", { name: "撤销" })).toBeEnabled();
      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      unmount();
    },
  );
});

it("rejects a natural-pixel oversized effect before history commit", async () => {
  stubCanvasStack({ width: 8000, height: 8000 });
  const harness = stubAnimationFrames();
  const { unmount } = await renderEditor();
  const canvas = getEditorCanvas();
  mockCanvasBox(canvas, 900, 600);
  harness.flush();
  fireEvent.click(screen.getByRole("button", { name: "模糊" }));
  fireEvent.change(screen.getByLabelText("马赛克与模糊绘制方式"), {
    target: { value: "brush" },
  });
  fireEvent.change(screen.getByLabelText("马赛克与模糊笔刷大小"), {
    target: { value: "64" },
  });
  fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 160, clientY: 10 });
  fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 740, clientY: 590 });
  fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 740, clientY: 590 });
  harness.flush();
  expect(screen.getByText(/笔刷效果超出8MP/)).toBeVisible();
  expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
  unmount();
});
