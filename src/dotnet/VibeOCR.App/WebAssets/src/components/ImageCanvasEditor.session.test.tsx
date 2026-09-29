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

describe("screenshot session editor wiring", () => {
  it("describes an empty local text result without offering retry", () => {
    const { unmount } = render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-empty", revision: 0 }}
        textLayer={{
          status: "textlayer.empty",
          reason: "textlayer.noLines",
          binding: { sessionId: "session-empty", revision: 0 },
        }}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "取字" }));
    expect(screen.getByText("图片中没有可选择的文字。")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "重新准备文字层" })).toBeNull();
    unmount();
  });
  it("keeps hand and Space panning separate from content edits", () => {
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
    const canvas = screen.getByLabelText("图片检查画布");
    const stage = screen.getByLabelText("图片视口");
    fireEvent.click(screen.getByRole("button", { name: "手形" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.change(screen.getByLabelText("显示缩放"), {
      target: { value: "2" },
    });
    expect(actions.run).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.keyDown(stage, { code: "Space" });
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 100, clientY: 100 });
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.keyUp(stage, { code: "Space" });
    fireEvent.pointerDown(canvas, { pointerId: 3, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 3, clientX: 100, clientY: 100 });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.notifyScreenshotRevision",
      sessionId: "session-a",
      revision: 1,
    });
    unmount();
  });

  it("notifies the host with increasing content revisions and resets per session", () => {
    const actions = createActions();
    const { rerender, unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );

    const canvas = screen.getByLabelText("图片检查画布");
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.notifyScreenshotRevision",
      sessionId: "session-a",
      revision: 1,
    });

    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.notifyScreenshotRevision",
      sessionId: "session-a",
      revision: 2,
    });

    // 换图/新会话：修订从宿主回显值重新计数，旧会话 id 不再出现更高修订。
    rerender(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/capture-2.png"
        session={{ sessionId: "session-b", revision: 0 }}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "序号" }));
    const freshCanvas = screen.getByLabelText("图片检查画布");
    fireEvent.pointerDown(freshCanvas, {
      pointerId: 2,
      clientX: 200,
      clientY: 200,
    });
    fireEvent.pointerUp(freshCanvas, {
      pointerId: 2,
      clientX: 210,
      clientY: 210,
    });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.notifyScreenshotRevision",
      sessionId: "session-b",
      revision: 1,
    });
    expect(
      actions.run.mock.calls.every(
        ([action]) =>
          (action as { sessionId?: string }).sessionId !== "session-a" ||
          (action as { revision?: number }).revision !== 3,
      ),
    ).toBe(true);
    unmount();
  });

  it("exposes session-only explicit recognition and close actions", () => {
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 4 }}
      />,
    );

    expect(screen.getByRole("button", { name: "识别当前图" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "结束会话" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.closeScreenshotSession",
    });
    unmount();
  });

  it("prepares the in-place text layer from the exported final PNG", async () => {
    class FakeImage {
      readonly naturalWidth = 1600;
      readonly naturalHeight = 900;
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
    const getContextDescriptor = Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "getContext",
    )!;
    const toBlobDescriptor = Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "toBlob",
    );
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

    const actions = createActions();
    const { rerender, unmount } = render(
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
        await new Promise((resolve) => setTimeout(resolve, 550));
      });
      expect(actions.run).not.toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.prepareScreenshotTextLayer",
        }),
      );
      rerender(
        <ImageCanvasEditor
          actions={actions}
          autoText={true}
          canExport={true}
          canRecognize={true}
          source="https://app.vibeocr/__resource/capture.png"
          session={{ sessionId: "session-a", revision: 0 }}
        />,
      );
      await waitFor(
        () =>
          expect(actions.run).toHaveBeenCalledWith(
            expect.objectContaining({
              type: "recognition.prepareScreenshotTextLayer",
              sessionId: "session-a",
              revision: 0,
            }),
          ),
        { timeout: 2500, interval: 50 },
      );
      expect(vi.mocked(fetch)).toHaveBeenCalledWith(
        "/__annotation",
        expect.objectContaining({ method: "POST" }),
      );
      unmount();
    } finally {
      Object.defineProperty(
        HTMLCanvasElement.prototype,
        "getContext",
        getContextDescriptor,
      );
      if (toBlobDescriptor) {
        Object.defineProperty(
          HTMLCanvasElement.prototype,
          "toBlob",
          toBlobDescriptor,
        );
      }
    }
  });

  it("shows the ready layer over the final PNG and copies only the selection", async () => {
    class FakeImage {
      readonly naturalWidth = 1600;
      readonly naturalHeight = 900;
      decoding = "";
      src = "";
      onload: (() => void) | null = null;

      constructor() {
        setTimeout(() => this.onload?.(), 0);
      }
    }
    vi.stubGlobal("Image", FakeImage);

    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
        textLayer={{
          status: "textlayer.ready",
          binding: { sessionId: "session-a", revision: 0 },
          modeId: "rapid_text",
          image: {
            url: "https://app.vibeocr/__resource/final.png",
            mediaType: "image/png",
            byteLength: 100,
          },
          lines: [
            { text: "你好世界", x1: 0, y1: 0, x2: 500, y2: 100, order: 0 },
            {
              text: "second line",
              x1: 100,
              y1: 200,
              x2: 900,
              y2: 300,
              order: 1,
            },
          ],
        }}
      />,
    );
    try {
      fireEvent.click(screen.getByRole("button", { name: "取字" }));
      const finalImage = await screen.findByAltText("截图最终画面");
      expect(finalImage).toHaveAttribute(
        "src",
        "https://app.vibeocr/__resource/final.png",
      );
      // 画布退让：标注手势不与选字手势叠在同一个表面上。
      expect(screen.getByLabelText("图片检查画布")).toHaveStyle({
        display: "none",
      });
      await waitFor(() =>
        expect(screen.getByText("你好世界")).toBeInTheDocument(),
      );

      const firstText = screen.getByText("你好世界").firstChild!;
      const range = document.createRange();
      range.setStart(firstText, 1);
      range.setEnd(firstText, 3);
      const selection = document.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
      document.dispatchEvent(new Event("selectionchange"));

      const copyButton = await screen.findByRole("button", {
        name: "复制所选",
      });
      fireEvent.click(copyButton);
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.copyScreenshotSelection",
        sessionId: "session-a",
        revision: 0,
        text: "好世",
      });
      // Native selection has advanced, but React has not processed selectionchange yet.
      // The context menu must freeze the new DOM substring, not the previous state.
      range.setStart(firstText, 0);
      range.setEnd(firstText, 1);
      selection.removeAllRanges();
      selection.addRange(range);
      fireEvent.contextMenu(firstText.parentElement!, {
        clientX: 20,
        clientY: 30,
      });
      const menuButton = screen.getAllByRole("button", { name: "复制所选" })[1];
      expect(menuButton).toBeDefined();
      fireEvent.pointerDown(menuButton!);
      fireEvent.click(menuButton!);
      expect(actions.run).toHaveBeenCalledTimes(2);
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "recognition.copyScreenshotSelection",
        sessionId: "session-a",
        revision: 0,
        text: "你",
      });
      actions.run.mockResolvedValueOnce(false);
      fireEvent.click(copyButton);
      await waitFor(() =>
        expect(screen.getByText(/复制所选文字失败/)).toBeInTheDocument(),
      );
      selection.removeAllRanges();
      fireEvent.click(copyButton);
      expect(actions.run).toHaveBeenCalledTimes(3);
      unmount();
    } finally {
      document.getSelection()?.removeAllRanges();
    }
  });

  it("invalidates the layer on local revision advance and re-prepares", async () => {
    class FakeImage {
      readonly naturalWidth = 1600;
      readonly naturalHeight = 900;
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
    const getContextDescriptor = Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "getContext",
    )!;
    const toBlobDescriptor = Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "toBlob",
    );
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

    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        autoText={true}
        canExport={true}
        canRecognize={true}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
        textLayer={{
          status: "textlayer.ready",
          binding: { sessionId: "session-a", revision: 0 },
          modeId: "rapid_text",
          image: {
            url: "https://app.vibeocr/__resource/final.png",
            mediaType: "image/png",
            byteLength: 100,
          },
          lines: [
            { text: "你好世界", x1: 0, y1: 0, x2: 500, y2: 100, order: 0 },
          ],
        }}
      />,
    );
    try {
      fireEvent.click(screen.getByRole("button", { name: "矩形" }));
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 1));
      });
      const canvas = screen.getByLabelText("图片检查画布");
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 100,
        clientY: 100,
      });
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 1,
      });
      // 本地修订即时失效：最终 PNG 视图立即退场，不亮旧层冒充。
      await waitFor(() =>
        expect(screen.queryByAltText("截图最终画面")).toBeNull(),
      );
      // 自动取字：新修订重新导出并准备（同修订只一次）。
      await waitFor(
        () =>
          expect(actions.run).toHaveBeenCalledWith(
            expect.objectContaining({
              type: "recognition.prepareScreenshotTextLayer",
              sessionId: "session-a",
              revision: 1,
            }),
          ),
        { timeout: 2500, interval: 50 },
      );
      unmount();
    } finally {
      Object.defineProperty(
        HTMLCanvasElement.prototype,
        "getContext",
        getContextDescriptor,
      );
      if (toBlobDescriptor) {
        Object.defineProperty(
          HTMLCanvasElement.prototype,
          "toBlob",
          toBlobDescriptor,
        );
      }
    }
  });

  it("does not send the exported PNG when the session changes during a delayed upload", async () => {
    // jsdom 无 2d 上下文/图片解码：用最小 fake 驱动真实导出→上传路径。
    class FakeImage {
      readonly naturalWidth = 1600;
      readonly naturalHeight = 900;
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
    const getContextDescriptor = Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "getContext",
    )!;
    const toBlobDescriptor = Object.getOwnPropertyDescriptor(
      HTMLCanvasElement.prototype,
      "toBlob",
    );
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
    let releaseUpload!: (response: Response) => void;
    const uploadGate = new Promise<Response>((resolve) => {
      releaseUpload = resolve;
    });
    vi.stubGlobal(
      "fetch",
      vi.fn(() => uploadGate),
    );

    const actions = createActions();
    const { rerender, unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={true}
        canRecognize={false}
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "session-a", revision: 0 }}
      />,
    );
    try {
      // 等 fake 解码完成，imageRef 就绪后开始导出。
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 1));
      });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "复制标注图" }));
        await Promise.resolve();
      });
      expect(vi.mocked(fetch)).toHaveBeenCalled();

      // 上传在途时换图/换会话：旧 PNG 不得贴上新会话发出。
      rerender(
        <ImageCanvasEditor
          actions={actions}
          canExport={true}
          canRecognize={false}
          source="https://app.vibeocr/__resource/capture-2.png"
          session={{ sessionId: "session-b", revision: 0 }}
        />,
      );
      await act(async () => {
        releaseUpload(
          new Response(
            JSON.stringify({
              resourceUri:
                "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
            }),
            { status: 201, headers: { "Content-Type": "application/json" } },
          ),
        );
        await new Promise((resolve) => setTimeout(resolve, 1));
      });

      expect(actions.run).not.toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.copyScreenshotImage",
        }),
      );
      expect(actions.run).not.toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.copyAnnotatedImage",
        }),
      );
      await waitFor(() =>
        expect(
          screen.getByText(/导出期间内容或会话已变化，本次未发送/),
        ).toBeVisible(),
      );
      unmount();
    } finally {
      Object.defineProperty(
        HTMLCanvasElement.prototype,
        "getContext",
        getContextDescriptor,
      );
      if (toBlobDescriptor) {
        Object.defineProperty(
          HTMLCanvasElement.prototype,
          "toBlob",
          toBlobDescriptor,
        );
      }
    }
  });
});
