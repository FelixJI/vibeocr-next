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
import { projectPoint } from "./annotationGeometry";

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
}

function stubAnimationFrames(): FrameHarness {
  const frames = new Map<number, FrameRequestCallback>();
  let nextFrame = 0;
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    frames.set(++nextFrame, callback);
    return nextFrame;
  });
  vi.stubGlobal("cancelAnimationFrame", vi.fn());
  return {
    frames,
    flush: () => {
      act(() => {
        const pending = [...frames.values()];
        frames.clear();
        pending.forEach((callback) => callback(0));
      });
    },
  };
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

function editorProps() {
  return {
    actions: createActions(),
    canExport: true,
    canRecognize: true,
    source: "https://app.vibeocr/__resource/capture.png",
    session: { sessionId: "session-geometry", revision: 0 },
    showAutoTextPreference: false,
  };
}

async function renderEditor() {
  const props = editorProps();
  const rendered = render(<ImageCanvasEditor {...props} />);
  await act(async () => {});
  return { ...rendered, actions: props.actions };
}

function allCalls(): RecordedCall[] {
  return state.canvases.flatMap((entry) => entry.calls);
}

function clearCalls() {
  state.canvases.forEach((entry) => {
    entry.calls.length = 0;
  });
}

function drawnRects(): number[][] {
  return allCalls()
    .filter((call) => call.op === "strokeRect")
    .map((call) => call.args as number[]);
}

async function dragDraw(
  canvas: HTMLCanvasElement,
  harness: FrameHarness,
  from: { x: number; y: number },
  to: { x: number; y: number },
  modifiers: { shift?: boolean; alt?: boolean } = {},
) {
  fireEvent.pointerDown(canvas, {
    pointerId: 1,
    clientX: from.x,
    clientY: from.y,
  });
  fireEvent.pointerMove(canvas, {
    pointerId: 1,
    clientX: to.x,
    clientY: to.y,
    shiftKey: modifiers.shift,
    altKey: modifiers.alt,
  });
  harness.flush();
  fireEvent.pointerUp(canvas, {
    pointerId: 1,
    clientX: to.x,
    clientY: to.y,
    shiftKey: modifiers.shift,
    altKey: modifiers.alt,
  });
  harness.flush();
}

async function clickSelect(
  canvas: HTMLCanvasElement,
  harness: FrameHarness,
  at: { x: number; y: number },
) {
  fireEvent.click(screen.getByRole("button", { name: "选择" }));
  fireEvent.pointerDown(canvas, { pointerId: 2, clientX: at.x, clientY: at.y });
  fireEvent.pointerUp(canvas, { pointerId: 2, clientX: at.x, clientY: at.y });
  harness.flush();
}

function revisions(actions: { run: ReturnType<typeof vi.fn> }): number[] {
  return actions.run.mock.calls
    .map((call) => (call[0] as { revision?: number }).revision)
    .filter((value): value is number => typeof value === "number");
}

describe("AC1 矩形/椭圆八点缩放与拖拽约束", () => {
  it("选中矩形后拖角手柄缩放：一次拖动恰好一条历史，几何更新", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    expect(revisions(actions)).toEqual([1]);

    // 选中后拖 se 角手柄 (400,300) → (500,380)：矩形扩为 400×280。
    await clickSelect(canvas, harness, { x: 200, y: 200 });
    clearCalls();
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 400, clientY: 300 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 500, clientY: 380 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 500, clientY: 380 });
    harness.flush();
    expect(drawnRects()).toContainEqual([100, 100, 400, 280]);
    // 一次拖动恰好一条历史（绘制 1 + 缩放 1）。
    expect(revisions(actions)).toEqual([1, 2]);
    // 撤销一次回到未缩放几何。
    clearCalls();
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    expect(drawnRects()).toContainEqual([100, 100, 300, 200]);
    unmount();
  });

  it("拖角手柄越过反向边界自动翻转，不产生负尺寸", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    await clickSelect(canvas, harness, { x: 200, y: 200 });
    // 拖 nw 手柄 (100,100) 越过 se 角到 (550,450)：翻转为 (400,300)-(550,450)。
    clearCalls();
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 550, clientY: 450 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 550, clientY: 450 });
    harness.flush();
    expect(drawnRects()).toContainEqual([400, 300, 150, 150]);
    unmount();
  });

  it("Shift 拖拽约束正方形/正圆，Alt 以起点为中心绘制", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(
      canvas,
      harness,
      { x: 100, y: 100 },
      { x: 400, y: 300 },
      { shift: true },
    );
    // dx=300、dy=200 → 取较大边 300：正方形 300×300。
    expect(drawnRects()).toContainEqual([100, 100, 300, 300]);

    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    await dragDraw(
      canvas,
      harness,
      { x: 200, y: 200 },
      { x: 300, y: 260 },
      { alt: true },
    );
    // Alt 中心绘制：起点为中心，(200,200)±(100,60) → 200×120。
    const ellipses = allCalls().filter((call) => call.op === "ellipse");
    // 中心 (200,200)、rx=100、ry=60。
    expect(
      ellipses.some(
        (call) =>
          Math.abs((call.args[0] as number) - 200) < 0.01 &&
          Math.abs((call.args[1] as number) - 200) < 0.01 &&
          Math.abs((call.args[2] as number) - 100) < 0.01 &&
          Math.abs((call.args[3] as number) - 60) < 0.01,
      ),
    ).toBe(true);
    unmount();
  });

  it("箭头端点可拖拽调整，Shift 吸附 45°；直线绘制同约束", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 直线：Shift 从 (100,100) 拖到 (400,300) → 吸附 45°。
    fireEvent.click(screen.getByRole("button", { name: "直线" }));
    await dragDraw(
      canvas,
      harness,
      { x: 100, y: 100 },
      { x: 400, y: 300 },
      { shift: true },
    );
    const length = Math.hypot(300, 200);
    const expected = {
      x: 100 + length / Math.SQRT2,
      y: 100 + length / Math.SQRT2,
    };
    const lines = allCalls().filter((call) => call.op === "lineTo");
    expect(
      lines.some(
        (call) =>
          Math.abs((call.args[0] as number) - expected.x) < 0.01 &&
          Math.abs((call.args[1] as number) - expected.y) < 0.01,
      ),
    ).toBe(true);

    // 箭头：绘制后选中，拖 end 端点到 (500,200)。
    fireEvent.click(screen.getByRole("button", { name: "箭头" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    expect(revisions(actions)).toEqual([1, 2]);
    await clickSelect(canvas, harness, { x: 250, y: 200 });
    clearCalls();
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 400, clientY: 300 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 500, clientY: 200 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 500, clientY: 200 });
    harness.flush();
    const strokes = allCalls().filter((call) => call.op === "lineTo");
    expect(
      strokes.some(
        (call) =>
          Math.abs((call.args[0] as number) - 500) < 0.01 &&
          Math.abs((call.args[1] as number) - 200) < 0.01,
      ),
    ).toBe(true);
    expect(revisions(actions)).toEqual([1, 2, 3]);
    unmount();
  });
});

describe("AC2 直线/折线、线型、圆角与箭头端头", () => {
  it("矩形线型可选虚线/点线，写入真实 setLineDash 节奏", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.change(screen.getByLabelText("线型"), {
      target: { value: "dashed" },
    });
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    const dashPatterns = () =>
      allCalls()
        .filter((call) => call.op === "setLineDash")
        .map((call) => call.args[0]);
    expect(dashPatterns()).toContainEqual([12, 8]);

    // 点线：[2,6] 且圆头端帽。
    clearCalls();
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("线型"), {
      target: { value: "dotted" },
    });
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    expect(dashPatterns()).toContainEqual([2, 6]);
    expect(
      allCalls().some(
        (call) => call.op === "lineCap" && call.args[0] === "round",
      ),
    ).toBe(true);
    unmount();
  });

  it("矩形圆角走 roundRect 路径且半径受短边夹取", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.change(screen.getByLabelText("矩形圆角"), {
      target: { value: "12" },
    });
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    const rounds = allCalls().filter((call) => call.op === "roundRect");
    expect(rounds.length).toBeGreaterThan(0);
    expect(rounds.some((call) => call.args[4] === 12)).toBe(true);
    // 反向拖拽（start>end）也归一化绘制，不产生负宽高。
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    await dragDraw(canvas, harness, { x: 400, y: 300 }, { x: 100, y: 100 });
    expect(
      allCalls()
        .filter((call) => call.op === "roundRect")
        .some(
          (call) =>
            (call.args[0] as number) === 100 &&
            (call.args[1] as number) === 100 &&
            (call.args[2] as number) === 300 &&
            (call.args[3] as number) === 200,
        ),
    ).toBe(true);
    unmount();
  });

  it("箭头可选实心/空心与单向/双向端头", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 实心双向：端头三角填充，两端各一个。
    fireEvent.click(screen.getByRole("button", { name: "箭头" }));
    fireEvent.change(screen.getByLabelText("箭头样式"), {
      target: { value: "solid" },
    });
    fireEvent.click(screen.getByLabelText("双向箭头"));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    // 三角端头有真实 fill（旧开放头从不 fill）。
    expect(allCalls().some((call) => call.op === "fill")).toBe(true);
    // closePath ×2（两端三角），轴杆因双向缩短不触两端。
    expect(
      allCalls().filter((call) => call.op === "closePath").length,
    ).toBeGreaterThanOrEqual(2);

    // 空心单向：无 fill，仅闭合描边三角 ×1。
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.click(screen.getByLabelText("双向箭头"));
    fireEvent.change(screen.getByLabelText("箭头样式"), {
      target: { value: "hollow" },
    });
    clearCalls();
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    expect(allCalls().some((call) => call.op === "fill")).toBe(false);
    expect(
      allCalls().filter((call) => call.op === "closePath").length,
    ).toBeGreaterThanOrEqual(1);
    unmount();
  });

  it("折线：单击加点、双击提交恰好一条历史；Enter 同效", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "折线" }));
    // 第一点。
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    harness.flush();
    // 第二、三点。
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 300, clientY: 120 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 120 });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 260, clientY: 280 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 260, clientY: 280 });
    harness.flush();
    // 草稿未提交：无历史。
    expect(revisions(actions)).toEqual([]);
    // Enter 提交：三点折线一条历史。
    fireEvent.keyDown(canvas, { key: "Enter" });
    harness.flush();
    expect(revisions(actions)).toEqual([1]);
    const lineTos = allCalls()
      .filter((call) => call.op === "lineTo")
      .map((call) => call.args);
    expect(lineTos).toContainEqual([300, 120]);
    expect(lineTos).toContainEqual([260, 280]);

    // 双击路径：两次单击后 dblclick 提交。
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 200, clientY: 200 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 200, clientY: 200 });
    harness.flush();
    expect(revisions(actions)).toEqual([1]);
    fireEvent.doubleClick(canvas);
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    unmount();
  });
});

describe("AC3 工具属性记忆、滚轮线宽与快捷键边界", () => {
  it("线宽/线型按工具独立记忆且可重置回默认", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.change(screen.getByLabelText("线条宽度"), {
      target: { value: "8" },
    });
    fireEvent.change(screen.getByLabelText("线型"), {
      target: { value: "dashed" },
    });
    // 折线/椭圆独立：不受矩形工具记忆影响。
    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    expect(settings.getByLabelText("线条宽度")).toHaveValue("3");
    expect(settings.getByLabelText("线型")).toHaveValue("solid");
    // 切回矩形：记忆保留；重置回到默认。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    expect(screen.getByLabelText("线条宽度")).toHaveValue("8");
    expect(screen.getByLabelText("线型")).toHaveValue("dashed");
    fireEvent.click(screen.getByRole("button", { name: "重置属性" }));
    expect(screen.getByLabelText("线条宽度")).toHaveValue("3");
    expect(screen.getByLabelText("线型")).toHaveValue("solid");
    expect(
      screen.getByText("已重置“矩形”工具属性为默认值。"),
    ).toBeInTheDocument();
    // 下排只显示当前工具属性：箭头不出现圆角，矩形不出现箭头样式。
    expect(screen.queryByLabelText("矩形圆角")).toBeInTheDocument();
    expect(screen.queryByLabelText("箭头样式")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "箭头" }));
    expect(screen.queryByLabelText("矩形圆角")).toBeNull();
    expect(screen.queryByLabelText("箭头样式")).toBeInTheDocument();
    // 文字工具：无描边属性，仅字号。
    fireEvent.click(screen.getByRole("button", { name: "文字" }));
    expect(screen.queryByLabelText("线条宽度")).toBeNull();
    expect(screen.queryByLabelText("线型")).toBeNull();
    expect(screen.queryByLabelText("文字字号")).toBeInTheDocument();
    unmount();
  });

  it("画布滚轮调整线宽；焦点在输入框时快捷键不抢输入", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    // 向上滚：3 → 5。
    act(() => {
      canvas.dispatchEvent(
        new WheelEvent("wheel", { deltaY: -100, cancelable: true }),
      );
    });
    await act(async () => {});
    expect(screen.getByText("线条宽度：5px。")).toBeInTheDocument();
    // 向下滚两次：5 → 3 → 2（每次事件后 effect 同步 ref，逐步步进）。
    act(() => {
      canvas.dispatchEvent(
        new WheelEvent("wheel", { deltaY: 120, cancelable: true }),
      );
    });
    await act(async () => {});
    act(() => {
      canvas.dispatchEvent(
        new WheelEvent("wheel", { deltaY: 120, cancelable: true }),
      );
    });
    await act(async () => {});
    expect(screen.getByText("线条宽度：2px。")).toBeInTheDocument();
    clearCalls();
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    expect(
      allCalls().some((call) => call.op === "lineWidth" && call.args[0] === 2),
    ).toBe(true);

    // 折线草稿打开时，输入框内 Enter/Escape 不提交/不取消草稿。
    fireEvent.click(screen.getByRole("button", { name: "折线" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 300, clientY: 120 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 120 });
    const widthInput = screen.getByLabelText("输出宽度");
    act(() => {
      widthInput.focus();
    });
    fireEvent.keyDown(widthInput, { key: "Enter" });
    fireEvent.keyDown(widthInput, { key: "Escape" });
    expect(revisions(actions)).toEqual([1]);
    // 草稿仍在：画布双击（真实提交入口）才入历史。
    fireEvent.doubleClick(canvas);
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    unmount();
  });
});

describe("AC4 历史、Esc 草稿与变换后命中", () => {
  it("选择属性栏改变画布位置时，单击不移动对象，拖动仅使用指针位移", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    let top = 0;
    canvas.getBoundingClientRect = () =>
      ({ left: 0, top, width: 900, height: 600 }) as DOMRect;
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 250, clientY: 200 });
    top = 29;
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 250, clientY: 200 });
    harness.flush();
    expect(revisions(actions)).toEqual([1]);
    // 工具栏再次发生布局变化时，拖拽仍只有实际指针的 +60/+40。
    clearCalls();
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 250, clientY: 229 });
    top = 58;
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 310, clientY: 269 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 310, clientY: 269 });
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    expect(drawnRects()).toContainEqual([160, 140, 300, 200]);
    unmount();
  });
  it("Esc 放弃绘制中的草稿，不产生历史", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    harness.flush();
    fireEvent.keyDown(canvas, { key: "Escape" });
    harness.flush();
    expect(revisions(actions)).toEqual([]);
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
    // Esc 后回到选择工具且无草稿残留。
    clearCalls();
    harness.flush();
    expect(drawnRects()).toEqual([]);
    unmount();
  });

  it("整图旋转后标注仍可命中并拖动编辑", async () => {
    stubCanvasStack({ width: 800, height: 800 });
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    // 旋转 90°：显示坐标经 projectPoint 旋转，标记中心映射到新位置。
    fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    const fakeImage = { naturalWidth: 800, naturalHeight: 800 };
    const rotated = projectPoint(
      { x: 250, y: 200 },
      fakeImage as HTMLImageElement,
      0,
      { width: 900, height: 600 },
      90,
      { width: 900, height: 600 },
    );
    // 点击旋转后的中心：仍命中并可拖动。
    await clickSelect(canvas, harness, { x: rotated.x, y: rotated.y });
    const settings = screen.queryByRole("toolbar", {
      name: "当前工具设置",
    });
    expect(settings).not.toBeNull();
    clearCalls();
    fireEvent.pointerDown(canvas, {
      pointerId: 3,
      clientX: rotated.x,
      clientY: rotated.y,
    });
    fireEvent.pointerMove(canvas, {
      pointerId: 3,
      clientX: rotated.x + 60,
      clientY: rotated.y + 40,
    });
    harness.flush();
    fireEvent.pointerUp(canvas, {
      pointerId: 3,
      clientX: rotated.x + 60,
      clientY: rotated.y + 40,
    });
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2, 3]);
    unmount();
  });

  it("提交裁剪后标注仍可命中并拖动编辑", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    fireEvent.click(screen.getByRole("button", { name: "裁剪" }));
    await dragDraw(canvas, harness, { x: 50, y: 50 }, { x: 600, y: 500 });
    expect(revisions(actions)).toEqual([1, 2]);
    // 裁剪不改变标记显示坐标：中心 (250,200) 仍命中。
    await clickSelect(canvas, harness, { x: 250, y: 200 });
    expect(
      screen.queryByRole("toolbar", { name: "当前工具设置" }),
    ).not.toBeNull();
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 250, clientY: 200 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 310, clientY: 240 });
    harness.flush();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 310, clientY: 240 });
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2, 3]);
    unmount();
  });

  it("0% 不透明对象不可见但可由对象选择入口恢复", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    const opacity = screen.getByLabelText("边框不透明度");
    fireEvent.change(opacity, { target: { value: "0" } });
    fireEvent.blur(opacity);
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    expect(revisions(actions)).toEqual([1]);
    // 渲染真实写入 globalAlpha 0：输出完全不可见。
    expect(
      allCalls().some(
        (call) => call.op === "globalAlpha" && call.args[0] === 0,
      ),
    ).toBe(true);

    // 不必记住透明标注坐标：选择工具的对象列表可恢复样式。
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(
      screen.getByRole("option", { name: "1 · 矩形" }),
    ).toBeInTheDocument();
    expect(revisions(actions)).toEqual([1]);
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    const markOpacity = settings.getByLabelText("边框不透明度");
    expect(markOpacity).toHaveValue(0);
    fireEvent.change(markOpacity, { target: { value: "100" } });
    fireEvent.blur(markOpacity);
    expect(revisions(actions)).toEqual([1, 2]);
    clearCalls();
    harness.flush();
    expect(
      allCalls().some(
        (call) => call.op === "globalAlpha" && call.args[0] === 1,
      ),
    ).toBe(true);
    unmount();
  });
});
