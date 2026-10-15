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

  it.each([
    { style: "hollow", double: false, length: 12 },
    { style: "hollow", double: true, length: 30 },
    { style: "solid", double: false, length: 12 },
    { style: "solid", double: true, length: 30 },
  ])(
    "短箭头 $style / 双头 $double 的轴杆不反向且不越过端点",
    async ({ style, double, length }) => {
      stubCanvasStack();
      const harness = stubAnimationFrames();
      const { unmount } = await renderEditor();
      const canvas = getEditorCanvas();
      mockCanvasBox(canvas, 900, 600);
      harness.flush();
      fireEvent.click(screen.getByRole("button", { name: "箭头" }));
      fireEvent.change(screen.getByLabelText("线条宽度"), {
        target: { value: "8" },
      });
      fireEvent.change(screen.getByLabelText("箭头样式"), {
        target: { value: style },
      });
      if (double) fireEvent.click(screen.getByLabelText("双向箭头"));
      clearCalls();
      await dragDraw(
        canvas,
        harness,
        { x: 100, y: 100 },
        { x: 100 + length, y: 100 },
      );
      const calls = allCalls();
      // 轴杆只有一段 lineTo；三角端头有两段，不混入端头边线。
      const shafts = calls.flatMap((call, index) =>
        call.op === "moveTo" &&
        calls[index + 1]?.op === "lineTo" &&
        calls[index + 2]?.op !== "lineTo"
          ? [[call.args[0] as number, calls[index + 1]!.args[0] as number]]
          : [],
      );
      expect(shafts.length).toBeGreaterThan(0);
      for (const [start, end] of shafts) {
        expect(start).toBeGreaterThanOrEqual(100);
        expect(end).toBeLessThanOrEqual(100 + length);
        expect(end).toBeGreaterThanOrEqual(start!);
      }
      unmount();
    },
  );

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
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    expect(settings.queryByRole("button", { name: "重置属性" })).toBeNull();
    fireEvent.click(main.getByRole("button", { name: "重置属性" }));
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

describe("短线段端点命中（C1 回归）", () => {
  it("长度小于双命中圈的直线/箭头：点击 end 侧拖动的是 end 而不是 start", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 直线 (300,200)→(310,204)：长度≈10.8 < 2×12，两端命中圈重叠。
    fireEvent.click(screen.getByRole("button", { name: "直线" }));
    await dragDraw(canvas, harness, { x: 300, y: 200 }, { x: 310, y: 204 });
    await clickSelect(canvas, harness, { x: 305, y: 202 });
    // 按在 end 附近 (311,205)：距 end≈1.4、距 start≈11.2 → 应命中 end。
    // 旧实现无条件优先 start，会把起点拖走。
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 311, clientY: 205 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 500, clientY: 300 });
    harness.flush();
    clearCalls();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 500, clientY: 300 });
    harness.flush();
    // 提交后帧：轴杆 lineTo(500,300)；旧实现拖走 start 时线段终点仍 (310,204)。
    const lineTos = allCalls()
      .filter((call) => call.op === "lineTo")
      .map((call) => call.args);
    expect(lineTos).toContainEqual([500, 300]);
    expect(lineTos).not.toContainEqual([310, 204]);

    // 箭头短线同构：共享同一 endpointHandle。前一个直线仍处于选中态，
    // 用对象下拉直接选中箭头，避免旧选择的手柄抢先命中。
    fireEvent.click(screen.getByRole("button", { name: "箭头" }));
    await dragDraw(canvas, harness, { x: 300, y: 200 }, { x: 308, y: 203 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    fireEvent.pointerDown(canvas, { pointerId: 4, clientX: 309, clientY: 204 });
    fireEvent.pointerMove(canvas, { pointerId: 4, clientX: 560, clientY: 260 });
    harness.flush();
    clearCalls();
    fireEvent.pointerUp(canvas, { pointerId: 4, clientX: 560, clientY: 260 });
    harness.flush();
    const arrowTos = allCalls()
      .filter((call) => call.op === "lineTo")
      .map((call) => call.args);
    expect(arrowTos).toContainEqual([560, 260]);
    expect(arrowTos).not.toContainEqual([308, 203]);
    unmount();
  });
});

describe("折线活动段预览（C2 回归）", () => {
  it("拖拽新段与单击后悬停都实时可见；悬停不入历史，Esc 放弃草稿", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "折线" }));
    // 1) 拖拽中的活动段：pointerup 之前画布已绘制到当前拖拽点。
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 300, clientY: 150 });
    harness.flush();
    // 旧实现只遍历已提交 points：拖拽点 (300,150) 不可见。
    expect(
      allCalls()
        .filter((call) => call.op === "lineTo")
        .map((call) => call.args),
    ).toContainEqual([300, 150]);
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 150 });
    harness.flush();

    // 2) 单击加点后悬停：下一段橡皮筋跟随游标，仍只是草稿。
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 400, clientY: 200 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 200 });
    clearCalls();
    fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 520, clientY: 260 });
    harness.flush();
    expect(
      allCalls()
        .filter((call) => call.op === "lineTo")
        .map((call) => call.args),
    ).toContainEqual([520, 260]);
    // 悬停零历史：无修订、撤销仍禁用（悬停点绝不被提交/导出）。
    expect(revisions(actions)).toEqual([]);
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();

    // 3) Esc 放弃草稿（同时切回选择工具）；重新激活折线后正常单击-双击
    //    提交恰好一条历史。
    fireEvent.keyDown(canvas, { key: "Escape" });
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "折线" }));
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 300, clientY: 150 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 300, clientY: 150 });
    fireEvent.doubleClick(canvas, { clientX: 300, clientY: 150 });
    harness.flush();
    expect(revisions(actions)).toEqual([1]);
    unmount();
  });
});

describe("选中对象填充恢复（C3 回归）", () => {
  function fillCalls() {
    return allCalls();
  }

  it("新空心对象启用填充使用所属工具记忆，而非选择工具默认", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 矩形工具记忆：填充 #e02020/80 后关闭（记忆保留值）。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByLabelText("填充"));
    fireEvent.change(screen.getByLabelText("填充颜色"), {
      target: { value: "#e02020" },
    });
    const opacity = screen.getByLabelText("填充不透明度");
    fireEvent.change(opacity, { target: { value: "80" } });
    fireEvent.blur(opacity);
    fireEvent.click(screen.getByLabelText("填充"));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });

    // 选择工具下给空心对象开启填充：恢复矩形工具记忆值。
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    expect(settings.getByLabelText("填充")).not.toBeChecked();
    fireEvent.click(settings.getByLabelText("填充"));
    // 旧实现写入选择工具默认白/50。
    expect(settings.getByLabelText("填充颜色")).toHaveValue("#e02020");
    expect(settings.getByLabelText("填充不透明度")).toHaveValue(80);
    clearCalls();
    harness.flush();
    const calls = fillCalls();
    const fillIndex = calls.findIndex(
      (call) => call.op === "fillStyle" && call.args[0] === "#e02020",
    );
    expect(fillIndex).toBeGreaterThanOrEqual(0);
    expect(
      calls
        .slice(Math.max(0, fillIndex - 3), fillIndex + 1)
        .some((call) => call.op === "globalAlpha" && call.args[0] === 0.8),
    ).toBe(true);
    unmount();
  });

  it("已有自定义填充关闭再开启恢复对象原值，undo/redo 逐级可回", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 画一个带工具记忆填充的矩形，再把对象填充改成自定义值。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByLabelText("填充"));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 400, y: 300 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    const settings = () =>
      within(screen.getByRole("toolbar", { name: "当前工具设置" }));
    fireEvent.change(settings().getByLabelText("填充颜色"), {
      target: { value: "#ff0000" },
    });
    const opacity = settings().getByLabelText("填充不透明度");
    fireEvent.change(opacity, { target: { value: "40" } });
    fireEvent.blur(opacity);
    expect(revisions(actions)).toHaveLength(3);

    // 关闭：值保留在对象自身（enabled=false 渲染为空心）。
    fireEvent.click(settings().getByLabelText("填充"));
    expect(revisions(actions)).toHaveLength(4);
    expect(settings().getByLabelText("填充")).not.toBeChecked();

    // 撤销回到启用（自定义值仍在）；重做回到关闭。撤销/重做会清除选择，
    // 经对象下拉重新选中后断言。
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(settings().getByLabelText("填充")).toBeChecked();
    expect(settings().getByLabelText("填充颜色")).toHaveValue("#ff0000");
    expect(settings().getByLabelText("填充不透明度")).toHaveValue(40);
    fireEvent.click(screen.getByRole("button", { name: "重做" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(settings().getByLabelText("填充")).not.toBeChecked();

    // 重新开启：恢复对象关闭前的自定义值（旧实现丢成白/50）。
    fireEvent.click(settings().getByLabelText("填充"));
    expect(settings().getByLabelText("填充颜色")).toHaveValue("#ff0000");
    expect(settings().getByLabelText("填充不透明度")).toHaveValue(40);
    unmount();
  });

  it("删除对象使索引重排后，重开填充仍恢复该对象自身值且互不污染", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // rect0：椭圆 #12a150/70；rect1：矩形工具记忆填充后改为 #ff0000。
    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    fireEvent.click(screen.getByLabelText("填充"));
    fireEvent.change(screen.getByLabelText("填充颜色"), {
      target: { value: "#12a150" },
    });
    const ellipseOpacity = screen.getByLabelText("填充不透明度");
    fireEvent.change(ellipseOpacity, { target: { value: "70" } });
    fireEvent.blur(ellipseOpacity);
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 300, y: 260 });
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.click(screen.getByLabelText("填充"));
    await dragDraw(canvas, harness, { x: 350, y: 100 }, { x: 550, y: 260 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    const settings = () =>
      within(screen.getByRole("toolbar", { name: "当前工具设置" }));
    fireEvent.change(settings().getByLabelText("填充颜色"), {
      target: { value: "#ff0000" },
    });

    // 两对象分别关闭填充：值随各自样式保存。
    fireEvent.click(settings().getByLabelText("填充"));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    fireEvent.click(settings().getByLabelText("填充"));

    // 删除 rect0（索引重排，rect1 由 1 变 0）。
    fireEvent.keyDown(canvas, { key: "Delete" });
    harness.flush();
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });

    // 重新开启 rect1 的填充：恢复其自身 #ff0000（旧索引侧表方案在重排
    // 后会错配成别的对象或工具默认）。
    fireEvent.click(settings().getByLabelText("填充"));
    expect(settings().getByLabelText("填充颜色")).toHaveValue("#ff0000");
    expect(settings().getByLabelText("填充不透明度")).toHaveValue(50);
    unmount();
  });
});

describe("小矩形八点手柄最近命中（D1 回归）", () => {
  it("8×8 矩形点击 se 命中 se 而非 nw：拖拽扩展到 (400,200)-(500,300)", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 400, y: 200 }, { x: 408, y: 208 });
    await clickSelect(canvas, harness, { x: 404, y: 204 });
    // se 角 (408,208)：距 nw (400,200) ≈11.3 也在命中圈内（重叠），
    // 旧实现固定顺序首命中 nw，会把左上角拖走。
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 408, clientY: 208 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 500, clientY: 300 });
    harness.flush();
    clearCalls();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 500, clientY: 300 });
    harness.flush();
    expect(drawnRects()).toContainEqual([400, 200, 100, 100]);
    unmount();
  });

  it("8×8 椭圆与屏蔽共用同一最近命中：se 手柄真实拖动", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 椭圆：拖 se 后包围盒 (400,200)-(500,300) → 中心 (450,250) r50。
    fireEvent.click(screen.getByRole("button", { name: "椭圆" }));
    await dragDraw(canvas, harness, { x: 400, y: 200 }, { x: 408, y: 208 });
    await clickSelect(canvas, harness, { x: 404, y: 204 });
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 408, clientY: 208 });
    fireEvent.pointerMove(canvas, { pointerId: 3, clientX: 500, clientY: 300 });
    harness.flush();
    clearCalls();
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 500, clientY: 300 });
    harness.flush();
    const ellipses = allCalls()
      .filter((call) => call.op === "ellipse")
      .map((call) => call.args);
    expect(ellipses).toContainEqual([450, 250, 50, 50, 0, 0, Math.PI * 2]);

    // 屏蔽小矩形：八点手柄同样最近命中。旧选择（椭圆）的手柄会抢先
    // 命中点击，先点空白取消选择再选中屏蔽。
    fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
    await dragDraw(canvas, harness, { x: 400, y: 200 }, { x: 408, y: 208 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 700, clientY: 450 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 700, clientY: 450 });
    harness.flush();
    await clickSelect(canvas, harness, { x: 404, y: 204 });
    fireEvent.pointerDown(canvas, { pointerId: 4, clientX: 408, clientY: 208 });
    fireEvent.pointerMove(canvas, { pointerId: 4, clientX: 500, clientY: 300 });
    harness.flush();
    clearCalls();
    fireEvent.pointerUp(canvas, { pointerId: 4, clientX: 500, clientY: 300 });
    harness.flush();
    expect(drawnRects()).toContainEqual([400, 200, 100, 100]);
    unmount();
  });
});

describe("Shift 角拖拽按原比例归一（D2 回归）", () => {
  async function shiftDragCorner(
    canvas: HTMLCanvasElement,
    harness: ReturnType<typeof stubAnimationFrames>,
    from: { x: number; y: number },
    to: { x: number; y: number },
    pointerId: number,
  ) {
    fireEvent.pointerDown(canvas, {
      pointerId,
      clientX: from.x,
      clientY: from.y,
    });
    fireEvent.pointerMove(canvas, {
      pointerId,
      clientX: to.x,
      clientY: to.y,
      shiftKey: true,
    });
    harness.flush();
    clearCalls();
    fireEvent.pointerUp(canvas, {
      pointerId,
      clientX: to.x,
      clientY: to.y,
      shiftKey: true,
    });
    harness.flush();
  }

  it("宽主导 2:1 矩形 dy=80 时宽到 160（旧实现错缩成 80×40）", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 260, y: 180 });
    await clickSelect(canvas, harness, { x: 180, y: 140 });
    // se 总位移后 corner=(120,180)：相对锚 (100,100) 总向量 dx=20/dy=80。
    await shiftDragCorner(
      canvas,
      harness,
      { x: 260, y: 180 },
      { x: 120, y: 180 },
      3,
    );
    expect(drawnRects()).toContainEqual([100, 100, 160, 80]);
    unmount();
  });

  it("高矩形 1:2 宽轴主导：dx=160 时按比例放大到 160×320（旧实现不变/错缩）", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 180, y: 260 });
    await clickSelect(canvas, harness, { x: 140, y: 180 });
    // se 总向量 dx=160/dy=40：宽轴换算后 160×320；旧实现 max 尺寸当高
    // 再乘比例 → 80×160（尺寸不变，不会提交新几何）。
    await shiftDragCorner(
      canvas,
      harness,
      { x: 180, y: 260 },
      { x: 260, y: 140 },
      3,
    );
    expect(drawnRects()).toContainEqual([100, 100, 160, 320]);
    unmount();
  });

  it("高矩形 1:2 高轴位移 dx=50/dy=80：宽度换算不丢大轴输入（40→50）", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 180, y: 260 });
    await clickSelect(canvas, harness, { x: 140, y: 180 });
    // dy=80 名义高主导，但 dx=50 > dy×ratio=40：宽度必须采纳 50（高
    // 100）；旧实现把 max 尺寸当高再乘比例 → 40×80，丢弃宽轴输入。
    await shiftDragCorner(
      canvas,
      harness,
      { x: 180, y: 260 },
      { x: 150, y: 180 },
      3,
    );
    expect(drawnRects()).toContainEqual([100, 100, 50, 100]);
    unmount();
  });

  it("Shift 角拖拽越过锚点：翻转到对侧且尺寸仍按比例", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 宽矩形 2:1，se 拖过锚点到左上 (60,60)：总向量 (-40,-40)，
    // 宽轴换算 80×40，矩形翻转到锚点左上。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 260, y: 180 });
    await clickSelect(canvas, harness, { x: 180, y: 140 });
    await shiftDragCorner(
      canvas,
      harness,
      { x: 260, y: 180 },
      { x: 60, y: 60 },
      3,
    );
    expect(drawnRects()).toContainEqual([20, 60, 80, 40]);
    unmount();
  });
});

describe("整体状态操作放弃进行中草稿（D3 回归）", () => {
  it("清除编辑后折线草稿不再渲染，Enter/双击不复活", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    // 先提交一个矩形使“清除编辑”可用，再开始折线草稿。
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 260, y: 180 });
    fireEvent.click(screen.getByRole("button", { name: "折线" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 300 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 300 });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 300, clientY: 350 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 350 });
    harness.flush();
    expect(revisions(actions)).toEqual([1]);

    fireEvent.click(screen.getByRole("button", { name: "清除编辑" }));
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    // EMPTY 后草稿不得继续叠加渲染（旧实现仍可见）。
    clearCalls();
    harness.flush();
    const lineTos = allCalls()
      .filter((call) => call.op === "lineTo")
      .map((call) => call.args);
    expect(lineTos).not.toContainEqual([300, 350]);
    // Enter/双击均不复活旧草稿（旧实现会再产生一条历史）。
    fireEvent.keyDown(canvas, { key: "Enter" });
    harness.flush();
    fireEvent.doubleClick(canvas, { clientX: 300, clientY: 350 });
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    unmount();
  });

  it("整图旋转后草稿不留在旧坐标渲染，Enter 不复活", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "折线" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 300 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 300 });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 300, clientY: 350 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 300, clientY: 350 });
    harness.flush();

    fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
    harness.flush();
    expect(revisions(actions)).toEqual([1]);
    // 旋转后画面不得残留旧坐标草稿（旧实现叠加渲染）。
    clearCalls();
    harness.flush();
    const lineTos = allCalls()
      .filter((call) => call.op === "lineTo")
      .map((call) => call.args);
    expect(lineTos).not.toContainEqual([300, 350]);
    fireEvent.keyDown(canvas, { key: "Enter" });
    harness.flush();
    fireEvent.doubleClick(canvas, { clientX: 300, clientY: 350 });
    harness.flush();
    expect(revisions(actions)).toEqual([1]);
    unmount();
  });
});
