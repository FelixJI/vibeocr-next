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
