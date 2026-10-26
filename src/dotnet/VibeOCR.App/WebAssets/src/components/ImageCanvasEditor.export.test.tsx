import { DEFAULT_EDITOR_PREFERENCES } from "./editorPreferences";
import { Profiler, StrictMode } from "react";
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
    const commits = vi.fn();
    const { rerender, unmount } = render(
      <Profiler id="editor" onRender={commits}>
        <ImageCanvasEditor {...props} />
      </Profiler>,
    );
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
    const renderedCalls = (): RecordedCall[] =>
      calls.flatMap((call) =>
        call.op === "drawImage" && call.args[0] instanceof HTMLCanvasElement
          ? (state.canvases.find((entry) => entry.element === call.args[0])
              ?.calls ?? [])
          : [call],
      );
    calls.length = 0;
    fireEvent.click(screen.getByRole("button", { name: "画笔" }));
    fireEvent.pointerDown(canvas, { clientX: 100, clientY: 100 });
    commits.mockClear();
    fireEvent.pointerMove(canvas, { clientX: 120, clientY: 120 });
    const pendingDraftFrame = nextFrame;
    fireEvent.pointerMove(canvas, { clientX: 160, clientY: 160 });
    expect(nextFrame).toBe(pendingDraftFrame);
    expect(commits).not.toHaveBeenCalled();
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
      renderedCalls()
        .filter((call) => call.op === "lineTo")
        .map((call) => call.args),
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
      renderedCalls()
        .filter((call) => call.op === "lineTo")
        .map((call) => call.args),
    ).toEqual([
      [120, 120],
      [160, 160],
    ]);

    // 换图发生在待画帧内：旧帧被取消，旧拖拽的松手不能标注新图。
    fireEvent.pointerDown(canvas, { clientX: 300, clientY: 300 });
    fireEvent.pointerMove(canvas, { clientX: 350, clientY: 350 });
    const oldFrame = nextFrame;
    rerender(
      <Profiler id="editor" onRender={commits}>
        <ImageCanvasEditor {...props} source="https://app.vibeocr/new.png" />
      </Profiler>,
    );
    await act(async () => {});
    expect(cancelFrame).toHaveBeenCalledWith(oldFrame);
    fireEvent.pointerUp(canvas, { clientX: 350, clientY: 350 });
    expect(actions.run).toHaveBeenCalledTimes(1);
    calls.length = 0;
    flushFrame();
    expect(renderedCalls().some((call) => call.op === "lineTo")).toBe(false);
    const image = renderedCalls().find((call) => call.op === "drawImage")!
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

  it("reuses committed pixel effects across drawing frames", async () => {
    stubCanvasStack();
    vi.stubGlobal("PointerEvent", MouseEvent);
    let pending: FrameRequestCallback | undefined;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      pending = callback;
      return 1;
    });
    vi.stubGlobal("cancelAnimationFrame", () => {
      pending = undefined;
    });
    const flush = () =>
      act(() => {
        const callback = pending;
        pending = undefined;
        callback?.(0);
      });
    const { unmount } = render(<ImageCanvasEditor {...editorProps()} />);
    await act(async () => {});
    flush();
    const canvas = screen.getByLabelText("图片检查画布");
    vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
      left: 0,
      top: 0,
      width: 900,
      height: 600,
    } as DOMRect);
    fireEvent.click(screen.getByRole("button", { name: "模糊" }));
    fireEvent.pointerDown(canvas, { clientX: 100, clientY: 100 });
    fireEvent.pointerUp(canvas, { clientX: 300, clientY: 300 });
    flush();
    expect(
      state.canvases
        .flatMap((c) => c.calls)
        .some((call) => call.op === "filter"),
    ).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { clientX: 400, clientY: 100 });
    flush();
    state.canvases.forEach((c) => {
      c.calls.length = 0;
    });
    for (let i = 0; i < 5; i++) {
      fireEvent.pointerMove(canvas, {
        clientX: 450 + i * 10,
        clientY: 200 + i * 10,
      });
      flush();
    }
    fireEvent.pointerCancel(canvas);
    fireEvent.click(screen.getByRole("button", { name: "裁剪" }));
    fireEvent.pointerDown(canvas, { clientX: 50, clientY: 50 });
    for (let i = 0; i < 5; i++) {
      fireEvent.pointerMove(canvas, { clientX: 500 + i * 10, clientY: 400 });
      flush();
    }
    const effectPasses = state.canvases
      .flatMap((c) => c.calls)
      .filter((call) => call.op === "filter").length;
    fireEvent.pointerCancel(canvas);
    state.canvases.forEach((c) => {
      c.calls.length = 0;
    });
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    flush();
    expect(
      state.canvases
        .flatMap((c) => c.calls)
        .filter((call) => call.op === "filter"),
    ).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: "重做" }));
    flush();
    expect(
      state.canvases
        .flatMap((c) => c.calls)
        .filter((call) => call.op === "filter"),
    ).toHaveLength(1);
    unmount();
    expect(effectPasses).toBe(0);
  });

  it("does not auto-encode a second preview after edits settle", async () => {
    vi.useFakeTimers();
    stubCanvasStack();
    const { unmount } = render(
      <ImageCanvasEditor {...editorProps()} autoText={false} />,
    );
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

  it("explains the host-projected retired screenshot default without submitting", async () => {
    stubCanvasStack();
    const actions = createActions();
    const mounted = render(
      <ImageCanvasEditor
        {...editorProps({ actions })}
        recognitionModes={[
          {
            modeId: "",
            displayName: "文字识别",
            availability: "default_retired",
            reasonCode: "recognition_mode_retired",
          },
        ]}
      />,
    );
    const button = await screen.findByRole("button", { name: "识别文字" });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute(
      "title",
      expect.stringContaining("已退役，请重新选择"),
    );
    expect(actions.run).not.toHaveBeenCalled();
    mounted.unmount();
  });

  it.each([
    ["识别文字", "paddle_text"],
    ["表格识别", "paddle_table"],
  ])(
    "submits the completed image with the explicit %s mode",
    async (label, modeId) => {
      stubCanvasStack();
      const actions = createActions();
      // #270：独立公式识别已退役，图片编辑不再提供公式入口。
      const modes = [
        {
          modeId: "paddle_text",
          displayName: "文字识别",
          availability: "ready",
        },
        {
          modeId: "paddle_table",
          displayName: "表格识别",
          availability: "ready",
        },
      ];
      const { unmount } = render(
        <ImageCanvasEditor
          {...editorProps({ actions })}
          {...{ recognitionModes: modes }}
        />,
      );
      fireEvent.click(await screen.findByRole("button", { name: label }));
      await waitFor(() =>
        expect(actions.run).toHaveBeenCalledWith(
          expect.objectContaining({
            type: "recognition.recognizeScreenshotImage",
            modeId,
            sessionId: "session-a",
            revision: 0,
            excludeBoxes: [],
          }),
        ),
      );
      expect(state.toBlobCalls).toBe(1);
      unmount();
    },
  );

  it("freezes the clicked mode while its PNG upload waits for the host", async () => {
    stubCanvasStack();
    let finishUpload: (response: Response) => void = () => {};
    const uploading = vi.fn(
      () =>
        new Promise<Response>((resolve) => {
          finishUpload = resolve;
        }),
    );
    vi.stubGlobal("fetch", uploading);
    const actions = createActions();
    const modes = [
      { modeId: "paddle_text", displayName: "文字识别", availability: "ready" },
      {
        modeId: "paddle_table",
        displayName: "表格识别",
        availability: "ready",
      },
    ];
    const props = editorProps({ actions });
    const { rerender, unmount } = render(
      <ImageCanvasEditor {...props} recognitionModes={modes} />,
    );
    fireEvent.click(await screen.findByRole("button", { name: "表格识别" }));
    await waitFor(() => expect(uploading).toHaveBeenCalledTimes(1));
    rerender(<ImageCanvasEditor {...props} recognitionModes={[]} />);
    finishUpload(
      new Response(
        JSON.stringify({
          resourceUri:
            "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        }),
        { status: 201, headers: { "Content-Type": "application/json" } },
      ),
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.recognizeScreenshotImage",
          modeId: "paddle_table",
          sessionId: "session-a",
          revision: 0,
        }),
      ),
    );
    unmount();
  });

  it("explains missing capabilities and never falls back to a generic recognition submit", async () => {
    stubCanvasStack();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        {...editorProps({ actions })}
        {...{ recognitionModes: [] }}
      />,
    );
    for (const label of ["识别文字", "表格识别"])
      expect(await screen.findByRole("button", { name: label })).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "公式识别" }),
    ).not.toBeInTheDocument();
    expect(screen.getByText(/识别目录未就绪/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "识别文字" })).toBeDisabled();
    expect(state.toBlobCalls).toBe(0);
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
      name: "识别文字",
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
    fireEvent.click(await screen.findByRole("button", { name: "识别文字" }));
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
    fireEvent.click(screen.getByRole("button", { name: "识别文字" }));
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

describe("output preset copy acknowledgement", () => {
  it.each([
    [true, false],
    [false, false],
    [true, true],
    [false, true],
  ])(
    "closes only after successful host copy acknowledgement (success=%s, scene=%s)",
    async (success, capture) => {
      stubCanvasStack();
      const actions = createActions();
      let acknowledge: ((value: boolean) => void) | undefined;
      actions.run.mockImplementation((action: { type: string }) =>
        action.type === "recognition.copyScreenshotImage"
          ? new Promise<boolean>((resolve) => {
              acknowledge = resolve;
            })
          : Promise.resolve(true),
      );
      render(
        <ImageCanvasEditor
          {...editorProps({ actions })}
          captureScene={
            capture
              ? {
                  surfaceId: "surface",
                  captureSessionId: "capture",
                  inputUrl: "https://resource.vibeocr/source.png",
                  inputRevision: 0,
                  documentGeneration: 1,
                  bootstrapGeneration: 1,
                  initialTool: "select",
                  x: 0,
                  y: 0,
                  width: 64,
                  height: 32,
                  desktopWidth: 1280,
                  desktopHeight: 720,
                }
              : undefined
          }
          editorSettings={{
            editorPreferences: {
              ...DEFAULT_EDITOR_PREFERENCES,
              output: {
                ...DEFAULT_EDITOR_PREFERENCES.output,
                closeAfterCopy: true,
              },
            },
          }}
        />,
      );
      await waitFor(() =>
        expect(screen.getByText(/原图 64×32/)).toBeInTheDocument(),
      );
      fireEvent.click(
        screen.getByRole("button", {
          name: "复制标注图",
        }),
      );
      await waitFor(() => expect(acknowledge).toBeDefined());
      expect(actions.run).not.toHaveBeenCalledWith({
        type: "recognition.closeScreenshotSession",
      });
      await act(async () => {
        acknowledge!(success);
      });
      if (success && !capture)
        await waitFor(() =>
          expect(actions.run).toHaveBeenCalledWith({
            type: "recognition.closeScreenshotSession",
          }),
        );
      else
        expect(actions.run).not.toHaveBeenCalledWith({
          type: "recognition.closeScreenshotSession",
        });
    },
  );
});

describe("preset upload ownership after host acknowledgement", () => {
  it.each(["succeeded", "rejected", "unconfirmed", "fallback"] as const)(
    "cleans only the terminated rejected operation (%s)",
    async (receipt) => {
      stubCanvasStack();
      let acknowledge!: (
        value: "succeeded" | "rejected" | "unconfirmed",
      ) => void;
      const pending = new Promise<"succeeded" | "rejected" | "unconfirmed">(
        (resolve) => {
          acknowledge = resolve;
        },
      );
      const runWithReceipt = vi.fn(() => pending);
      const actions = {
        ...createActions(),
        runWithReceipt: receipt === "fallback" ? undefined : runWithReceipt,
      };
      if (receipt === "fallback") actions.run.mockResolvedValue(false);
      render(
        <ImageCanvasEditor
          {...editorProps({ actions })}
          editorSettings={{ imageOutputDirectory: "C:/authorized-output" }}
        />,
      );
      await waitFor(() =>
        expect(screen.getByText(/原图 64×32/)).toBeInTheDocument(),
      );
      fireEvent.click(screen.getByRole("button", { name: "按常用预设保存" }));
      if (receipt !== "fallback") {
        await waitFor(() => expect(runWithReceipt).toHaveBeenCalledOnce());
        expect(
          vi
            .mocked(fetch)
            .mock.calls.filter(([, init]) => init?.method === "DELETE"),
        ).toHaveLength(0);
        await act(async () => {
          acknowledge(receipt);
        });
      }
      await waitFor(() =>
        expect(
          screen.getByText(
            receipt === "succeeded"
              ? /已按常用预设保存副本/
              : receipt === "rejected"
                ? /预设输出未完成/
                : /预设保存结果尚未确认/,
          ),
        ).toBeVisible(),
      );
      const deletes = vi
        .mocked(fetch)
        .mock.calls.filter(([, init]) => init?.method === "DELETE");
      expect(deletes).toHaveLength(receipt === "rejected" ? 1 : 0);
      if (receipt === "rejected")
        expect(deletes[0]).toEqual([
          "/__annotation/0123456789abcdef0123456789abcdef",
          { method: "DELETE" },
        ]);
    },
  );
});

describe("restored project export lifecycle", () => {
  it.each([false, true])(
    "retires an upload on unmount without breaking StrictMode restoration (%s)",
    async (strict) => {
      stubCanvasStack();
      const actions = {
        ...createActions(),
        runWithReceipt: vi.fn().mockResolvedValue("succeeded"),
      };
      const props = editorProps({ actions });
      const image = new Image();
      image.src = props.source;
      const projectInstall = {
        token: "restored-project",
        sourceUrl: props.source,
        sourceImage: image,
        savedCanvas: { width: 900, height: 600 },
        state: { rotation: 0, marks: [], patches: [] },
        assets: new Map(),
        patches: new Map(),
      };
      let release!: (response: Response) => void;
      const pending = new Promise<Response>((resolve) => {
        release = resolve;
      });
      const fetchMock = vi.fn(
        (_input: RequestInfo | URL, init?: RequestInit) =>
          init?.method === "DELETE"
            ? Promise.resolve(new Response(null, { status: 204 }))
            : pending,
      );
      vi.stubGlobal("fetch", fetchMock);
      const editor = (
        <ImageCanvasEditor
          {...props}
          projectInstall={projectInstall}
          editorSettings={{ imageOutputDirectory: "C:/authorized-output" }}
        />
      );
      const { unmount } = render(
        strict ? <StrictMode>{editor}</StrictMode> : editor,
      );
      await waitFor(() => expect(screen.getByText(/工程已恢复/)).toBeVisible());
      fireEvent.click(screen.getByRole("button", { name: "按常用预设保存" }));
      await waitFor(() => expect(fetchMock).toHaveBeenCalledOnce());
      unmount();
      await act(async () => {
        release(
          new Response(
            JSON.stringify({
              resourceUri:
                "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
            }),
            { status: 201, headers: { "Content-Type": "application/json" } },
          ),
        );
      });
      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
      expect(fetchMock.mock.calls[1]).toEqual([
        "/__annotation/0123456789abcdef0123456789abcdef",
        { method: "DELETE" },
      ]);
      expect(actions.runWithReceipt).not.toHaveBeenCalled();
      expect(actions.run).not.toHaveBeenCalledWith(
        expect.objectContaining({
          type: "imageEdit.savePresetImage",
        }),
      );
    },
  );
});
