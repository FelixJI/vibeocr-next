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

/** jsdom 无 2d 上下文/图片解码：用最小 fake 驱动真实导出→上传路径。 */
function installCanvasStubs(naturalWidth = 1600, naturalHeight = 900) {
  class FakeImage {
    readonly naturalWidth = naturalWidth;
    readonly naturalHeight = naturalHeight;
    decoding = "";
    src = "";
    onload: (() => void) | null = null;

    constructor() {
      setTimeout(() => this.onload?.(), 0);
    }
  }
  vi.stubGlobal("Image", FakeImage);
  const fakeContext = () =>
    new Proxy<Record<string | symbol, unknown>>(
      {},
      {
        get: (target, property) =>
          property in target ? target[property] : () => undefined,
        set: () => true,
      },
    );
  const descriptors = {
    getContext: Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "getContext",
    )!,
    toBlob: Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "toBlob",
    ),
    getBoundingClientRect: Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "getBoundingClientRect",
    ),
  };
  Object.defineProperty(HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value: vi.fn(() => fakeContext()),
  });
  Object.defineProperty(HTMLCanvasElement.prototype, "toBlob", {
    configurable: true,
    value: (callback: (blob: Blob | null) => void) =>
      callback(
        new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], {
          type: "image/png",
        }),
      ),
  });
  // 画布 1:1 映射：client 坐标即 900×600 内部坐标，便于精确几何断言。
  Object.defineProperty(HTMLCanvasElement.prototype, "getBoundingClientRect", {
    configurable: true,
    value: () => ({
      left: 0,
      top: 0,
      right: 900,
      bottom: 600,
      width: 900,
      height: 600,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    }),
  });
  const fetch = vi.fn().mockResolvedValue(
    new Response(
      JSON.stringify({
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      }),
      { status: 201, headers: { "Content-Type": "application/json" } },
    ),
  );
  vi.stubGlobal("fetch", fetch);
  return {
    fetch,
    restore() {
      Object.defineProperty(
        HTMLCanvasElement.prototype,
        "getContext",
        descriptors.getContext,
      );
      if (descriptors.toBlob) {
        Object.defineProperty(
          HTMLCanvasElement.prototype,
          "toBlob",
          descriptors.toBlob,
        );
      }
      if (descriptors.getBoundingClientRect) {
        Object.defineProperty(
          HTMLCanvasElement.prototype,
          "getBoundingClientRect",
          descriptors.getBoundingClientRect,
        );
      }
    },
  };
}

async function drawOnCanvas(
  from: { x: number; y: number },
  to: { x: number; y: number },
) {
  const canvas = screen.getByLabelText("图片检查画布");
  await act(async () => {
    fireEvent.pointerDown(canvas, {
      pointerId: 1,
      clientX: from.x,
      clientY: from.y,
    });
    await Promise.resolve();
  });
  await act(async () => {
    fireEvent.pointerUp(canvas, {
      pointerId: 1,
      clientX: to.x,
      clientY: to.y,
    });
    await Promise.resolve();
  });
}

describe("image editor exclusion regions", () => {
  it("offers the exclude tool without masked-copy actions until regions exist", async () => {
    const stubs = installCanvasStubs();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      // 无排除区：行为与旧流程一致，不出现屏蔽副本入口。
      expect(screen.getByRole("button", { name: "屏蔽" })).toBeEnabled();
      expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeDisabled();
      expect(screen.queryByRole("button", { name: "复制屏蔽副本" })).toBeNull();
      expect(screen.queryByRole("button", { name: "保存屏蔽副本" })).toBeNull();

      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 100, y: 100 }, { x: 300, y: 200 });
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 1,
      });
      expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeEnabled();
      expect(
        screen.getByRole("button", { name: "复制屏蔽副本" }),
      ).toBeInTheDocument();
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("clears only exclusion regions and keeps them undoable", async () => {
    const stubs = installCanvasStubs();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 100, y: 100 }, { x: 300, y: 200 });
      // 普通矩形标注与排除区共存。
      fireEvent.click(screen.getByRole("button", { name: "矩形" }));
      await drawOnCanvas({ x: 400, y: 100 }, { x: 600, y: 200 });
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 2,
      });

      // 清空屏蔽只移除排除区（普通标注仍在：清除编辑仍可用），入历史可撤销。
      fireEvent.click(screen.getByRole("button", { name: "清空屏蔽" }));
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 3,
      });
      expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "清除编辑" })).toBeEnabled();
      expect(screen.queryByRole("button", { name: "保存屏蔽副本" })).toBeNull();

      fireEvent.click(screen.getByRole("button", { name: "撤销" }));
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 4,
      });
      expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeEnabled();
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("submits recognition once and refuses the fully masked image", async () => {
    // 900×600 图在 900×600 画布内为恒等映射，便于精确构造整图覆盖。
    const stubs = installCanvasStubs(900, 600);
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 0, y: 0 }, { x: 900, y: 300 });

      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "识别当前图" }));
      });
      await waitFor(() => expect(stubs.fetch).toHaveBeenCalledTimes(1));
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.recognizeScreenshotImage",
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        sessionId: "session-a",
        revision: 1,
      });
      await waitFor(() =>
        expect(
          screen.getByText(/已提交识别当前图（image\/png/),
        ).toBeInTheDocument(),
      );

      // 补第二个排除区覆盖剩余区域：整图被屏蔽，拒绝提交且不产生新上传。
      await drawOnCanvas({ x: 0, y: 300 }, { x: 900, y: 600 });
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 2,
      });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "识别当前图" }));
      });
      expect(
        screen.getByText(
          "整张图都在屏蔽区内：没有可识别内容，已取消本次识别；原图与屏蔽区保持不变，可撤销后重试。",
        ),
      ).toBeInTheDocument();
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 60));
      });
      expect(stubs.fetch).toHaveBeenCalledTimes(1);
      expect(
        actions.run.mock.calls.filter(
          (call) =>
            (call[0] as { type?: string }).type ===
            "recognition.recognizeScreenshotImage",
        ),
      ).toHaveLength(1);
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("resizes a selected exclusion via its edge handle", async () => {
    // 900×600 图在 900×600 画布内为恒等映射：手柄命中与几何可直接推算。
    const stubs = installCanvasStubs(900, 600);
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      // 先建立上半区排除区。
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 0, y: 0 }, { x: 900, y: 300 });
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 1,
      });

      // 选择工具单击选中排除区（拖拽距离 <2 不产生移动提交）。
      fireEvent.click(screen.getByRole("button", { name: "选择" }));
      await drawOnCanvas({ x: 100, y: 150 }, { x: 101, y: 151 });
      // 拖动下边中点手柄 (450,300) 到画布底部：矩形扩展为整图。
      await drawOnCanvas({ x: 450, y: 300 }, { x: 450, y: 600 });
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 2,
      });

      // 缩放后的排除区覆盖整图：识别被拒绝且无上传。这证明手柄缩放真实
      // 改变了排除几何，而不只是界面预览。
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "识别当前图" }));
      });
      expect(
        screen.getByText(
          "整张图都在屏蔽区内：没有可识别内容，已取消本次识别；原图与屏蔽区保持不变，可撤销后重试。",
        ),
      ).toBeInTheDocument();
      expect(stubs.fetch).not.toHaveBeenCalled();
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("sends normalized exclusion boxes with the pin command", async () => {
    const stubs = installCanvasStubs(900, 600);
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      // (0,0)-(450,300) 恒等映射 → 归一化 [0,0,500,500]。
      await drawOnCanvas({ x: 0, y: 0 }, { x: 450, y: 300 });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "贴图" }));
      });
      await waitFor(() =>
        expect(actions.run).toHaveBeenCalledWith(
          expect.objectContaining({
            type: "recognition.pinScreenshotImage",
            sessionId: "session-a",
            revision: 1,
            excludeBoxes: [{ x: 0, y: 0, width: 500, height: 500 }],
          }),
        ),
      );
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("rejects the in-flight recognition upload when the mask changes mid-export", async () => {
    // 上传在掩膜变更之后才返回：旧实现（不校验导出上下文）会把旧像素
    // 贴上新修订发出；本测试要求拒绝且不发送识别命令。
    let resolveUpload: ((value: Response) => void) | undefined;
    const stubs = installCanvasStubs(900, 600);
    const controlledFetch = vi.fn().mockImplementation(
      () =>
        new Promise<Response>((resolve) => {
          resolveUpload = resolve;
        }),
    );
    vi.stubGlobal("fetch", controlledFetch);
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 0, y: 0 }, { x: 100, y: 100 });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "识别当前图" }));
      });
      // 上法在途时扩大掩膜：修订推进，旧导出必须被丢弃。
      await drawOnCanvas({ x: 0, y: 0 }, { x: 900, y: 600 });
      await act(async () => {
        resolveUpload?.(
          new Response(
            JSON.stringify({
              resourceUri:
                "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
            }),
            { status: 201, headers: { "Content-Type": "application/json" } },
          ),
        );
        await new Promise((resolve) => setTimeout(resolve, 60));
      });
      expect(
        await screen.findByText(
          "导出期间内容或会话已变化，本次未发送；请在新画面重试。",
        ),
      ).toBeInTheDocument();
      expect(
        actions.run.mock.calls.filter(
          (call) =>
            (call[0] as { type?: string }).type ===
            "recognition.recognizeScreenshotImage",
        ),
      ).toHaveLength(0);
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("invalidates a ready text layer and clears residue when the image changes", async () => {
    const stubs = installCanvasStubs();
    const actions = createActions();
    const { rerender, unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
        textLayer={{
          status: "textlayer.ready",
          binding: { sessionId: "session-a", revision: 0 },
          image: {
            url: "https://app.vibeocr/__resource/layer.png",
            mediaType: "image/png",
            byteLength: 2048,
          },
          lines: [{ text: "旧图文字", x1: 400, y1: 400, x2: 900, y2: 500 }],
        }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      // 修订 0 的层与当前修订一致：取字模式下可见。
      fireEvent.click(screen.getByRole("button", { name: "取字" }));
      expect(await screen.findByText("旧图文字")).toBeInTheDocument();

      // 排除区变更推进修订：旧层立即失效，行文本从 DOM 移除。
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 100, y: 100 }, { x: 300, y: 200 });
      fireEvent.click(screen.getByRole("button", { name: "取字" }));
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 50));
      });
      expect(screen.queryByText("旧图文字")).toBeNull();

      // 换图：上一张的排除区不残留（屏蔽副本入口消失），修订从宿主回显重计。
      rerender(
        <ImageCanvasEditor
          actions={actions}
          autoText={false}
          canExport={true}
          canRecognize={true}
          source="https://app.vibeocr/__resource/capture-2.png"
          session={{ sessionId: "session-b", revision: 0 }}
        />,
      );
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      expect(screen.queryByRole("button", { name: "保存屏蔽副本" })).toBeNull();
      expect(screen.getByRole("button", { name: "清空屏蔽" })).toBeDisabled();
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("caps exclusion creation at the bridge limit with an in-place message", async () => {
    const stubs = installCanvasStubs(900, 600);
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={true}
        canRecognize={false}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      for (let index = 0; index < 64; index += 1) {
        await drawOnCanvas(
          { x: (index % 8) * 40 + 10, y: Math.floor(index / 8) * 40 + 10 },
          { x: (index % 8) * 40 + 30, y: Math.floor(index / 8) * 40 + 30 },
        );
      }
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 64,
      });

      // 第 65 个：提示并拒绝，不丢既有标记，也不推进修订。
      await drawOnCanvas({ x: 400, y: 400 }, { x: 500, y: 500 });
      expect(
        screen.getByText("屏蔽区已达上限 64 个；请先删除或清空后再新增。"),
      ).toBeInTheDocument();
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 64,
      });
      expect(
        actions.run.mock.calls.filter(
          (call) =>
            (call[0] as { type?: string }).type ===
            "recognition.notifyScreenshotRevision",
        ),
      ).toHaveLength(64);
    } finally {
      unmount();
      stubs.restore();
    }
  });

  it("drops text-layer lines that intersect exclusion rects", async () => {
    const stubs = installCanvasStubs();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={false}
        canExport={false}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
        textLayer={{
          status: "textlayer.ready",
          binding: { sessionId: "session-a", revision: 1 },
          image: {
            url: "https://app.vibeocr/__resource/layer.png",
            mediaType: "image/png",
            byteLength: 2048,
          },
          lines: [
            // 与排除区（归一化约 110.6..333.7 × 104.4..303.3）正面积相交。
            { text: "屏蔽边界内文字", x1: 150, y1: 120, x2: 400, y2: 280 },
            { text: "保留的正常行", x1: 400, y1: 400, x2: 900, y2: 500 },
            // 仅贴边：不与排除区正面积相交，保留。
            { text: "贴边保留行", x1: 340, y1: 310, x2: 800, y2: 380 },
          ],
        }}
      />,
    );
    try {
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 30));
      });
      fireEvent.click(screen.getByRole("button", { name: "屏蔽" }));
      await drawOnCanvas({ x: 100, y: 100 }, { x: 300, y: 200 });
      fireEvent.click(screen.getByRole("button", { name: "取字" }));
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 60));
      });
      expect(await screen.findByText("保留的正常行")).toBeInTheDocument();
      expect(screen.getByText("贴边保留行")).toBeInTheDocument();
      expect(screen.queryByText("屏蔽边界内文字")).toBeNull();
    } finally {
      unmount();
      stubs.restore();
    }
  });
});
