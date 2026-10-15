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
        if (prop === "getImageData" && state.sampleResult)
          return { data: state.sampleResult };
        if (prop === "measureText")
          return { width: String(args[0] ?? "").length * 12 };
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
  state.sampleResult = undefined;
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

function revisions(actions: { run: ReturnType<typeof vi.fn> }): number[] {
  return actions.run.mock.calls
    .map((call) => (call[0] as { revision?: number }).revision)
    .filter((value): value is number => typeof value === "number");
}

describe("文字、序号和说明对象", () => {
  async function placeText(text = "旧中文\n第二行") {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const rendered = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: text },
    });
    await dragDraw(canvas, harness, { x: 100, y: 220 }, { x: 300, y: 220 });
    return { ...rendered, canvas, harness };
  }

  it("清空前置屏蔽会取消文字草稿，保留原文字和后续矩形，撤销恢复屏蔽", async () => {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
    await dragDraw(canvas, harness, { x: 80, y: 80 }, { x: 150, y: 130 });
    fireEvent.click(screen.getByRole("button", { name: "文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "A原文字" },
    });
    await dragDraw(canvas, harness, { x: 200, y: 220 }, { x: 360, y: 220 });
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.change(screen.getByLabelText("自定义颜色"), {
      target: { value: "#1f6feb" },
    });
    await dragDraw(canvas, harness, { x: 500, y: 300 }, { x: 700, y: 450 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "A*草稿" },
    });
    fireEvent.click(screen.getByRole("button", { name: "清空屏蔽" }));
    harness.flush();
    // 旧实现留下完成入口，点击后会把旧index1的草稿覆写到矩形B。
    const staleFinish = screen.queryByRole("button", { name: "完成文字编辑" });
    if (staleFinish) fireEvent.click(staleFinish);
    const options = () =>
      Array.from(
        screen.getByLabelText("选择标注对象").querySelectorAll("option"),
      ).map((option) => option.textContent);
    expect(options()).toEqual(["未选择", "1 · 文字", "2 · 矩形"]);
    expect(
      screen.queryByRole("button", { name: "完成文字编辑" }),
    ).not.toBeInTheDocument();
    expect(revisions(actions)).toEqual([1, 2, 3, 4]);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("标注文字")).toHaveValue("A原文字");
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    expect(screen.getByLabelText("自定义颜色")).toHaveValue("#1f6feb");
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    expect(options()).toEqual(["未选择", "1 · 屏蔽", "2 · 文字", "3 · 矩形"]);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    expect(screen.getByLabelText("标注文字")).toHaveValue("A原文字");
    unmount();
  });

  it.each(["文字", "说明"])(
    "%s续编时画布取色只改草稿，取消保留旧色、完成一条历史",
    async (label) => {
      stubCanvasStack({ width: 900, height: 600 });
      const harness = stubAnimationFrames();
      const { actions, unmount } = await renderEditor();
      const canvas = getEditorCanvas();
      mockCanvasBox(canvas, 900, 600);
      harness.flush();
      fireEvent.click(screen.getByRole("button", { name: label }));
      fireEvent.change(screen.getByLabelText("标注文字"), {
        target: { value: "取色测试" },
      });
      fireEvent.change(screen.getByLabelText("自定义颜色"), {
        target: { value: "#1f6feb" },
      });
      await dragDraw(canvas, harness, { x: 100, y: 220 }, { x: 300, y: 220 });
      fireEvent.doubleClick(canvas, {
        clientX: label === "文字" ? 110 : 310,
        clientY: 210,
      });
      fireEvent.change(screen.getByLabelText("标注文字"), {
        target: { value: "草稿取色" },
      });
      const pick = () => screen.getByRole("button", { name: "屏幕取色" });
      expect(pick()).toHaveAttribute("title", "取色：点击后在画布上取样颜色");
      state.sampleResult = new Uint8ClampedArray([224, 32, 32, 255]);
      const sample = () => {
        fireEvent.click(pick());
        fireEvent.pointerDown(canvas, {
          pointerId: 1,
          clientX: 700,
          clientY: 500,
        });
        fireEvent.pointerUp(canvas, {
          pointerId: 1,
          clientX: 700,
          clientY: 500,
        });
        harness.flush();
      };
      sample();
      const sampled = state.canvases.find((entry) =>
        entry.calls.some((call) => call.op === "getImageData"),
      );
      expect(
        sampled?.calls.some(
          (call) => call.op === "fillText" && call.args[0] === "草稿取色",
        ),
      ).toBe(true);
      expect(screen.getByLabelText("自定义颜色")).toHaveValue("#e02020");
      expect(revisions(actions)).toEqual([1]);
      expect(
        allCalls().some(
          (call) => call.op === "fillStyle" && call.args[0] === "#e02020",
        ),
      ).toBe(true);
      fireEvent.click(screen.getByRole("button", { name: "取消文字编辑" }));
      harness.flush();
      expect(screen.getByLabelText("自定义颜色")).toHaveValue("#1f6feb");
      expect(revisions(actions)).toEqual([1]);
      fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
      fireEvent.click(pick());
      fireEvent.click(screen.getByRole("button", { name: "取消文字编辑" }));
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 700,
        clientY: 500,
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 700, clientY: 500 });
      harness.flush();
      expect(revisions(actions)).toEqual([1]);
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: "0" },
      });
      expect(screen.getByLabelText("自定义颜色")).toHaveValue("#1f6feb");
      fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
      fireEvent.click(pick());
      fireEvent.keyDown(canvas, { key: "Escape" });
      expect(
        screen.getByText("已取消取色；标注颜色保持不变。"),
      ).toBeInTheDocument();
      expect(
        screen.getByRole("button", { name: "完成文字编辑" }),
      ).toBeInTheDocument();
      sample();
      fireEvent.click(screen.getByRole("button", { name: "完成文字编辑" }));
      harness.flush();
      expect(revisions(actions)).toEqual([1, 2]);
      expect(screen.getByLabelText("自定义颜色")).toHaveValue("#e02020");
      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      harness.flush();
      fireEvent.change(screen.getByLabelText("选择标注对象"), {
        target: { value: "0" },
      });
      expect(screen.getByLabelText("自定义颜色")).toHaveValue("#1f6feb");
      expect(
        screen.getByLabelText("选择标注对象").querySelectorAll("option"),
      ).toHaveLength(2);
      unmount();
    },
  );

  it("双击继续编辑多行文字及字体属性：草稿不入历史，完成一次提交，取消保留旧文字", async () => {
    const { actions, canvas, harness, unmount } = await placeText();
    expect(revisions(actions)).toEqual([1]);
    fireEvent.doubleClick(canvas, { clientX: 110, clientY: 210 });
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "更新中文\nNext" },
    });
    fireEvent.click(screen.getByLabelText("粗体"));
    fireEvent.click(screen.getByLabelText("斜体"));
    fireEvent.click(screen.getByLabelText("下划线"));
    fireEvent.change(screen.getByLabelText("文字字号"), {
      target: { value: "32" },
    });
    fireEvent.change(screen.getByLabelText("文字对齐"), {
      target: { value: "right" },
    });
    fireEvent.click(screen.getByLabelText("文字背景"));
    fireEvent.click(screen.getByLabelText("文字描边"));
    expect(revisions(actions)).toEqual([1]);
    expect(screen.getByRole("button", { name: "保存标注图" })).toBeDisabled();
    clearCalls();
    harness.flush();
    expect(
      allCalls()
        .filter((call) => call.op === "fillText")
        .map((call) => call.args[0]),
    ).toContain("Next");
    expect(allCalls().some((call) => call.op === "strokeText")).toBe(true);
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    fireEvent.click(main.getByRole("button", { name: "完成文字编辑" }));
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2]);
    fireEvent.click(main.getByRole("button", { name: "编辑文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "不应留下" },
    });
    fireEvent.click(main.getByRole("button", { name: "取消文字编辑" }));
    harness.flush();
    expect(screen.getByLabelText("标注文字")).toHaveValue("更新中文\nNext");
    expect(revisions(actions)).toEqual([1, 2]);
    fireEvent.click(main.getByRole("button", { name: "撤销" }));
    harness.flush();
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("标注文字")).toHaveValue("旧中文\n第二行");
    unmount();
  });

  it("无变化完成不产生历史；切工具或旋转取消草稿并保留原文字", async () => {
    const { actions, canvas, harness, unmount } = await placeText("原文字");
    fireEvent.doubleClick(canvas, { clientX: 110, clientY: 210 });
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "临时" },
    });
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "原文字" },
    });
    fireEvent.click(screen.getByRole("button", { name: "完成文字编辑" }));
    expect(revisions(actions)).toEqual([1]);
    fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "草稿" },
    });
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    expect(
      screen.queryByRole("button", { name: "完成文字编辑" }),
    ).not.toBeInTheDocument();
    expect(revisions(actions)).toEqual([1]);
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("标注文字")).toHaveValue("原文字");
    fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "草稿" },
    });
    fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
    harness.flush();
    expect(
      screen.queryByRole("button", { name: "完成文字编辑" }),
    ).not.toBeInTheDocument();
    expect(revisions(actions)).toEqual([1, 2]);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    expect(screen.getByLabelText("标注文字")).toHaveValue("原文字");
    unmount();
  });

  it("文字草稿期间画布删除/撤销不更改原对象，画布Esc可取消", async () => {
    const { actions, canvas, harness, unmount } = await placeText("原文字");
    fireEvent.doubleClick(canvas, { clientX: 110, clientY: 210 });
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "草稿" },
    });
    fireEvent.keyDown(canvas, { key: "Delete" });
    fireEvent.keyDown(canvas, { key: "z", ctrlKey: true });
    expect(revisions(actions)).toEqual([1]);
    fireEvent.keyDown(canvas, { key: "Escape" });
    harness.flush();
    expect(screen.getByLabelText("标注文字")).toHaveValue("原文字");
    expect(
      screen.queryByRole("button", { name: "完成文字编辑" }),
    ).not.toBeInTheDocument();
    expect(revisions(actions)).toEqual([1]);
    unmount();
  });

  it("导出在途时开始续编不会把旧预览发送为当前画面", async () => {
    const { actions, canvas, unmount } = await placeText("原文字");
    let resolveUpload: ((value: Response) => void) | undefined;
    const upload = vi.fn(
      () =>
        new Promise<Response>((resolve) => {
          resolveUpload = resolve;
        }),
    );
    vi.stubGlobal("fetch", upload);
    fireEvent.click(screen.getByRole("button", { name: "保存标注图" }));
    await act(async () => {});
    expect(upload).toHaveBeenCalled();
    fireEvent.doubleClick(canvas, { clientX: 110, clientY: 210 });
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "草稿" },
    });
    await act(async () => {
      resolveUpload!(
        new Response(
          JSON.stringify({
            resourceUri:
              "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
          }),
          { status: 201, headers: { "Content-Type": "application/json" } },
        ),
      );
    });
    expect(
      actions.run.mock.calls.some(
        (call) => "resourceUri" in (call[0] as object),
      ),
    ).toBe(false);
    expect(screen.getByLabelText("标注文字")).toHaveValue("草稿");
    unmount();
  });

  it("IME 组合及229键不完成/取消编辑、不关闭会话或执行文档快捷键", async () => {
    const { actions, canvas, harness, unmount } = await placeText("旧文字");
    fireEvent.doubleClick(canvas, { clientX: 110, clientY: 210 });
    const input = screen.getByLabelText("标注文字");
    fireEvent.change(input, { target: { value: "中文候选" } });
    fireEvent.compositionStart(input);
    for (const key of ["Escape", "Delete", " "])
      fireEvent.keyDown(input, { key });
    fireEvent.keyDown(input, { key: "Enter", ctrlKey: true });
    fireEvent.keyDown(input, { key: "s", ctrlKey: true });
    fireEvent.keyDown(window, { key: "w", ctrlKey: true });
    expect(
      screen.getByRole("button", { name: "完成文字编辑" }),
    ).toBeInTheDocument();
    expect(revisions(actions)).toEqual([1]);
    fireEvent.compositionEnd(input);
    fireEvent.keyDown(input, { key: "Escape", keyCode: 229 });
    expect(screen.getByLabelText("标注文字")).toHaveValue("中文候选");
    fireEvent.keyDown(input, { key: "Escape" });
    harness.flush();
    expect(screen.getByLabelText("标注文字")).toHaveValue("旧文字");
    expect(
      actions.run.mock.calls.some((call) =>
        String((call[0] as { type: string }).type).includes("close"),
      ),
    ).toBe(false);
    unmount();
  });

  it("字体不可用时明确回退系统字体，取消仍保留原样式", async () => {
    const { canvas, harness, unmount } = await placeText();
    fireEvent.doubleClick(canvas, { clientX: 110, clientY: 210 });
    const font = screen.getByLabelText("文字字体");
    fireEvent.change(font, { target: { value: "Unavailable_Font_230" } });
    fireEvent.blur(font);
    expect(
      screen.getByText("字体“Unavailable_Font_230”不可用，已回退到系统字体。"),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("文字字体")).toHaveValue("system-ui");
    fireEvent.click(screen.getByRole("button", { name: "取消文字编辑" }));
    harness.flush();
    expect(screen.getByLabelText("文字字体")).toHaveValue("system-ui");
    unmount();
  });

  it("序号删除中间对象、手改较大值后自动递增且字母不重复", async () => {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "序号" }));
    fireEvent.change(screen.getByLabelText("序号起点"), {
      target: { value: "25" },
    });
    fireEvent.blur(screen.getByLabelText("序号起点"));
    fireEvent.change(screen.getByLabelText("序号格式"), {
      target: { value: "upper" },
    });
    for (const x of [200, 300, 400])
      await dragDraw(canvas, harness, { x, y: 200 }, { x, y: 200 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "1" },
    });
    fireEvent.keyDown(canvas, { key: "Delete" });
    fireEvent.click(screen.getByRole("button", { name: "序号" }));
    await dragDraw(canvas, harness, { x: 500, y: 200 }, { x: 500, y: 200 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "2" },
    });
    expect(screen.getByLabelText("序号值")).toHaveValue(28);
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    fireEvent.change(screen.getByLabelText("序号值"), {
      target: { value: "51" },
    });
    fireEvent.blur(screen.getByLabelText("序号值"));
    fireEvent.click(screen.getByRole("button", { name: "序号" }));
    await dragDraw(canvas, harness, { x: 600, y: 200 }, { x: 600, y: 200 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "3" },
    });
    expect(screen.getByLabelText("序号值")).toHaveValue(52);
    harness.flush();
    expect(
      allCalls().some(
        (call) => call.op === "fillText" && call.args[0] === "AZ",
      ),
    ).toBe(true);
    unmount();
  });
  it("序号合法输入立即生效，空草稿和连续输入不重挂或抢焦点", async () => {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "序号" }));
    fireEvent.change(screen.getByLabelText("序号格式"), {
      target: { value: "upper" },
    });
    const start = screen.getByLabelText("序号起点");
    act(() => start.focus());
    fireEvent.change(start, { target: { value: "25" } });
    fireEvent.change(start, { target: { value: "" } });
    expect(start).toHaveValue(null);
    expect(start).toHaveFocus();
    expect(screen.getByLabelText("序号起点")).toBe(start);
    fireEvent.change(start, { target: { value: "25" } });
    await dragDraw(canvas, harness, { x: 200, y: 200 }, { x: 200, y: 200 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    const value = screen.getByLabelText("序号值");
    expect(value).toHaveValue(25);
    act(() => value.focus());
    clearCalls();
    fireEvent.change(value, { target: { value: "52" } });
    harness.flush();
    expect(screen.getByLabelText("序号值")).toBe(value);
    expect(value).toHaveFocus();
    expect(
      allCalls().some(
        (call) => call.op === "fillText" && call.args[0] === "AZ",
      ),
    ).toBe(true);
    fireEvent.change(value, { target: { value: "" } });
    expect(value).toHaveValue(null);
    expect(value).toHaveFocus();
    expect(revisions(actions)).toEqual([1, 2]);
    fireEvent.change(value, { target: { value: "53" } });
    expect(screen.getByLabelText("序号值")).toBe(value);
    expect(value).toHaveFocus();
    unmount();
  });
  it("说明引线按实际边缘连接，选择命中与该线段一致", async () => {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const { unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "说明" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "abcdefghij\nabcdefghij\nabcdefghij\nabcdefghij" },
    });
    fireEvent.change(screen.getByLabelText("说明样式"), {
      target: { value: "arrow" },
    });
    await dragDraw(canvas, harness, { x: 600, y: 100 }, { x: 300, y: 220 });
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "" },
    });
    await dragDraw(canvas, harness, { x: 500, y: 165 }, { x: 500, y: 165 });
    expect(screen.getByLabelText("选择标注对象")).toHaveValue("0");
    unmount();
  });

  it("序号起点、字母、形状、单对象序号与批量重新编号均可撤销", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "序号" }));
    fireEvent.change(screen.getByLabelText("序号起点"), {
      target: { value: "5" },
    });
    fireEvent.blur(screen.getByLabelText("序号起点"));
    fireEvent.change(screen.getByLabelText("序号格式"), {
      target: { value: "upper" },
    });
    fireEvent.change(screen.getByLabelText("序号形状"), {
      target: { value: "square" },
    });
    await dragDraw(canvas, harness, { x: 200, y: 200 }, { x: 200, y: 200 });
    await dragDraw(canvas, harness, { x: 300, y: 200 }, { x: 300, y: 200 });
    expect(
      allCalls()
        .filter((call) => call.op === "fillText")
        .map((call) => call.args[0]),
    ).toEqual(expect.arrayContaining(["E", "F"]));
    fireEvent.change(screen.getByLabelText("序号起点"), {
      target: { value: "10" },
    });
    fireEvent.blur(screen.getByLabelText("序号起点"));
    clearCalls();
    fireEvent.click(screen.getByRole("button", { name: "重新编号" }));
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2, 3]);
    expect(
      allCalls()
        .filter((call) => call.op === "fillText")
        .map((call) => call.args[0]),
    ).toEqual(expect.arrayContaining(["J", "K"]));
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    fireEvent.change(screen.getByLabelText("序号值"), {
      target: { value: "27" },
    });
    fireEvent.blur(screen.getByLabelText("序号值"));
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2, 3, 4]);
    expect(
      allCalls()
        .filter((call) => call.op === "fillText")
        .map((call) => call.args[0]),
    ).toContain("AA");
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    harness.flush();
    unmount();
  });

  it("气泡/箭头说明可修改内容及指向端点，拖动保留偏心抓取", async () => {
    stubCanvasStack();
    const harness = stubAnimationFrames();
    const { actions, unmount } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: "说明" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "说明内容" },
    });
    await dragDraw(canvas, harness, { x: 100, y: 100 }, { x: 300, y: 220 });
    expect(screen.getByLabelText("说明样式")).toHaveValue("bubble");
    fireEvent.change(screen.getByLabelText("说明样式"), {
      target: { value: "arrow" },
    });
    await dragDraw(canvas, harness, { x: 101, y: 101 }, { x: 151, y: 141 });
    expect(revisions(actions)).toEqual([1, 2, 3]);
    fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
    fireEvent.change(screen.getByLabelText("标注文字"), {
      target: { value: "更新说明\n第二行" },
    });
    fireEvent.click(screen.getByRole("button", { name: "完成文字编辑" }));
    harness.flush();
    expect(revisions(actions)).toEqual([1, 2, 3, 4]);
    expect(
      allCalls()
        .filter((call) => call.op === "fillText")
        .map((call) => call.args[0]),
    ).toContain("更新说明");
    expect(
      allCalls().some(
        (call) =>
          call.op === "lineTo" &&
          Math.abs((call.args[0] as number) - 150) < 0.01 &&
          Math.abs((call.args[1] as number) - 140) < 0.01,
      ),
    ).toBe(true);
    unmount();
  });

  it("裁剪和旋转后仍能从实际文字位置双击编辑", async () => {
    const { canvas, harness, unmount } = await placeText("可定位");
    fireEvent.click(screen.getByRole("button", { name: "裁剪" }));
    await dragDraw(canvas, harness, { x: 50, y: 100 }, { x: 700, y: 500 });
    fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
    clearCalls();
    harness.flush();
    const text = allCalls().find(
      (call) => call.op === "fillText" && call.args[0] === "可定位",
    )!;
    fireEvent.doubleClick(canvas, {
      clientX: (text.args[1] as number) + 5,
      clientY: (text.args[2] as number) - 5,
    });
    expect(
      screen.getByRole("button", { name: "完成文字编辑" }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("标注文字")).toHaveValue("可定位");
    unmount();
  });
});

for (const tool of ["文字", "序号", "说明"] as const) {
  it(`${tool}字体草稿连续输入后直接绘制使用新字体并保持预览导出一致`, async () => {
    stubCanvasStack({ width: 900, height: 600 });
    const harness = stubAnimationFrames();
    const { actions } = await renderEditor();
    const canvas = getEditorCanvas();
    mockCanvasBox(canvas, 900, 600);
    harness.flush();
    fireEvent.click(screen.getByRole("button", { name: tool }));
    const font = screen.getByLabelText("文字字体");
    font.focus();
    for (const value of ["", "m", "mono", "mono:", "monospace"]) {
      fireEvent.change(font, { target: { value } });
      expect(screen.getByLabelText("文字字体")).toBe(font);
      expect(font).toHaveValue(value);
      expect(font).toHaveFocus();
    }
    clearCalls();
    await dragDraw(canvas, harness, { x: 100, y: 220 }, { x: 300, y: 220 });
    expect(screen.getByLabelText("文字字体")).toHaveValue("monospace");
    expect(revisions(actions)).toEqual([1]);
    expect(
      allCalls().some(
        (call) =>
          call.op === "font" && String(call.args[0]).includes("px monospace,"),
      ),
    ).toBe(true);
    clearCalls();
    fireEvent.click(screen.getByRole("button", { name: "保存标注图" }));
    await act(async () => {});
    expect(
      allCalls().some(
        (call) =>
          call.op === "font" && String(call.args[0]).includes("px monospace,"),
      ),
    ).toBe(true);
    expect(actions.run).toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.saveScreenshotImage" }),
    );
  });
}

it("反向创建宽文字经旋转、拖动、续编、取消和撤销仍保留布局宽度", async () => {
  stubCanvasStack({ width: 900, height: 600 });
  const harness = stubAnimationFrames();
  const { actions } = await renderEditor();
  const canvas = getEditorCanvas();
  mockCanvasBox(canvas, 900, 600);
  harness.flush();
  fireEvent.click(screen.getByRole("button", { name: "文字" }));
  fireEvent.change(screen.getByLabelText("标注文字"), {
    target: { value: "X" },
  });
  fireEvent.change(screen.getByLabelText("文字对齐"), {
    target: { value: "center" },
  });
  fireEvent.click(screen.getByLabelText("文字背景"));
  await dragDraw(canvas, harness, { x: 360, y: 300 }, { x: 200, y: 300 });
  const expectWidth = (width: number) =>
    expect(
      allCalls().some(
        (call) =>
          call.op === "fillRect" &&
          Math.abs(Number(call.args[2]) - width) < 0.01,
      ),
    ).toBe(true);
  expectWidth(172);
  fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
  clearCalls();
  harness.flush();
  expectWidth(172);
  clearCalls();
  await dragDraw(canvas, harness, { x: 500, y: 120 }, { x: 540, y: 145 });
  expectWidth(172);
  expect(revisions(actions)).toEqual([1, 2, 3]);
  fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
  fireEvent.change(screen.getByLabelText("标注文字"), {
    target: { value: "YY" },
  });
  fireEvent.click(screen.getByRole("button", { name: "完成文字编辑" }));
  clearCalls();
  harness.flush();
  expectWidth(172);
  fireEvent.click(screen.getByRole("button", { name: "编辑文字" }));
  fireEvent.change(screen.getByLabelText("标注文字"), {
    target: { value: "放弃" },
  });
  fireEvent.click(screen.getByRole("button", { name: "取消文字编辑" }));
  expect(screen.getByLabelText("标注文字")).toHaveValue("YY");
  expect(revisions(actions)).toEqual([1, 2, 3, 4]);
  clearCalls();
  fireEvent.click(screen.getByRole("button", { name: "保存标注图" }));
  await act(async () => {});
  expectWidth(258);
  for (let n = 0; n < 3; n++)
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
  clearCalls();
  harness.flush();
  expectWidth(172);
  fireEvent.change(screen.getByLabelText("选择标注对象"), {
    target: { value: "0" },
  });
  expect(screen.getByLabelText("标注文字")).toHaveValue("X");
});
