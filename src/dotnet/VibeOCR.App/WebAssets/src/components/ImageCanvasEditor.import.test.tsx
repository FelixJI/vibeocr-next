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
import type { EditorImportDescriptor } from "./ImageCanvasEditor";
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

const SESSION_A = { sessionId: "session-a", revision: 0 } as const;

function imageDescriptor(
  requestId: string,
  session: string = SESSION_A.sessionId,
  revision: number = SESSION_A.revision,
): EditorImportDescriptor {
  return {
    requestId,
    sessionId: session,
    revision,
    items: [
      {
        kind: "image",
        assetId: "asset-1",
        resourceUri: "https://app.vibeocr/__resource/import-image",
        byteLength: 16,
        pixelWidth: 4,
        pixelHeight: 4,
      },
      {
        kind: "text",
        assetId: "asset-2",
        resourceUri: "https://app.vibeocr/__resource/import-text",
        byteLength: 12,
      },
    ],
  };
}

/** jsdom 下 Image 不真实解码：src 赋值后异步触发 onload/onerror 桩。 */
function stubImageDecode(
  mode: "ok" | "error" = "ok",
  naturalSize = { width: 40, height: 20 },
) {
  const created: unknown[] = [];
  class StubImage {
    readonly naturalWidth = naturalSize.width;
    readonly naturalHeight = naturalSize.height;
    decoding = "async";
    private source = "";
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    constructor() {
      created.push(this);
    }
    get src(): string {
      return this.source;
    }
    set src(value: string) {
      this.source = value;
      setTimeout(() => (mode === "ok" ? this.onload?.() : this.onerror?.()), 0);
    }
  }
  vi.stubGlobal("Image", StubImage);
  return created;
}

/** 记录型 2D 上下文：jsdom 无像素（getContext→null），直接替为记录代理；
 * 仅断言绘制调用序列，不依赖原实现。 */
function recordCanvasDrawImage(targets: unknown[]): () => void {
  const spy = vi
    .spyOn(HTMLCanvasElement.prototype, "getContext")
    .mockImplementation(function (this: HTMLCanvasElement, type: string) {
      if (type !== "2d") return null;
      return new Proxy({} as CanvasRenderingContext2D, {
        get: (_target, prop) => {
          if (prop === "canvas") return this;
          return (...args: unknown[]) => {
            if (prop === "drawImage") targets.push(args[0]);
            if (prop === "measureText") return { width: 10 };
            return undefined;
          };
        },
      }) as CanvasRenderingContext2D;
    });
  return () => spy.mockRestore();
}

describe("image canvas editor system import", () => {
  it("merge integration rejects an import when brush commit validation refuses, without adopting assets or history", async () => {
    stubImageDecode("ok", { width: 16000, height: 4000 });
    recordCanvasDrawImage([]);
    const actions = createActions();
    const props = {
      actions,
      canExport: true,
      canRecognize: false,
      source: "https://app.vibeocr/__resource/base.png",
      session: SESSION_A,
    };
    const { rerender, unmount } = render(<ImageCanvasEditor {...props} />);
    // 图像信息收进“使用帮助与图像信息”：展开后断言尺寸就绪。
    fireEvent.click(screen.getByText("使用帮助与图像信息"));
    await waitFor(() =>
      expect(screen.getByText(/原图 16000×4000/)).toBeVisible(),
    );
    const canvas = screen.getByLabelText("图片检查画布") as HTMLCanvasElement;
    vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
      left: 0,
      top: 0,
      width: 900,
      height: 600,
    } as DOMRect);
    fireEvent.click(screen.getByRole("button", { name: "模糊" }));
    fireEvent.change(screen.getByLabelText("马赛克与模糊绘制方式"), {
      target: { value: "brush" },
    });
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 400, clientY: 300 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 410, clientY: 300 });
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    actions.run.mockClear();
    // A changed logical viewport makes the existing natural-pixel blur halo exceed 4MP.
    // Import must consume the real commit(false) result, rather than acknowledge adoption.
    canvas.dataset.coordinateWidth = "450";
    const descriptor = imageDescriptor(
      "budget-rejection",
      SESSION_A.sessionId,
      1,
    );
    rerender(
      <ImageCanvasEditor
        {...props}
        systemImport={{ ...descriptor, items: [descriptor.items[0]!] }}
      />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.rejectImport",
        sessionId: SESSION_A.sessionId,
        revision: 1,
        requestId: descriptor.requestId,
      }),
    );
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.ackImport" }),
    );
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.notifyScreenshotRevision" }),
    );
    expect(screen.getByLabelText("选择标注对象")).toHaveValue("0");
    expect(
      screen.getByText(/笔刷效果超出8MP暂存或4MP单块采样预算/),
    ).toBeVisible();
    expect(screen.queryByText(/已导入/)).toBeNull();
    delete canvas.dataset.coordinateWidth;
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.releaseAssets" }),
    );
    actions.run.mockClear();
    unmount();
    await act(async () => {
      await Promise.resolve();
    });
    // rejectImport owns disposal of unacknowledged URIs; they must not remain in adopted GC.
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.releaseAssets" }),
    );
  });

  it.each(["queued-mask", "finished-mask", "duplicate-draft"] as const)(
    "merge integration preserves %s while rejecting an incoming batch",
    async (draft) => {
      stubImageDecode("ok", { width: 900, height: 600 });
      recordCanvasDrawImage([]);
      const frames = new Map<number, FrameRequestCallback>();
      let frameId = 0;
      vi.stubGlobal(
        "requestAnimationFrame",
        (callback: FrameRequestCallback) => {
          frames.set(++frameId, callback);
          return frameId;
        },
      );
      vi.stubGlobal("cancelAnimationFrame", (id: number) => frames.delete(id));
      const actions = createActions();
      const props = {
        actions,
        canExport: true,
        canRecognize: false,
        source: "https://app.vibeocr/__resource/base.png",
        session: SESSION_A,
      };
      const { rerender, unmount } = render(<ImageCanvasEditor {...props} />);
      fireEvent.click(screen.getByText("使用帮助与图像信息"));
      await waitFor(() =>
        expect(screen.getByText(/原图 900×600/)).toBeVisible(),
      );
      const canvas = screen.getByLabelText("图片检查画布");
      vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
        left: 0,
        top: 0,
        width: 900,
        height: 600,
      } as DOMRect);
      fireEvent.click(
        screen.getByRole("button", {
          name: draft === "duplicate-draft" ? "重复匹配" : "去水印",
        }),
      );
      if (draft !== "duplicate-draft")
        fireEvent.change(screen.getByLabelText("修补掩膜绘制方式"), {
          target: { value: "brush-add" },
        });
      fireEvent.pointerDown(canvas, {
        pointerId: 1,
        clientX: 200,
        clientY: 200,
      });
      if (draft === "finished-mask")
        fireEvent.pointerUp(canvas, {
          pointerId: 1,
          clientX: 240,
          clientY: 220,
        });
      const descriptor = imageDescriptor("during-" + draft);
      rerender(
        <ImageCanvasEditor
          {...props}
          systemImport={{ ...descriptor, items: [descriptor.items[0]!] }}
        />,
      );
      await waitFor(() =>
        expect(actions.run).toHaveBeenCalledWith({
          type: "recognition.rejectImport",
          sessionId: SESSION_A.sessionId,
          revision: 0,
          requestId: descriptor.requestId,
        }),
      );
      expect(actions.run).not.toHaveBeenCalledWith(
        expect.objectContaining({ type: "recognition.ackImport" }),
      );
      expect(actions.run).not.toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.notifyScreenshotRevision",
        }),
      );
      if (draft !== "finished-mask")
        fireEvent.pointerUp(canvas, {
          pointerId: 1,
          clientX: 240,
          clientY: 220,
        });
      expect(
        screen.getByRole("button", {
          name: draft === "duplicate-draft" ? "查找重复" : "预览修补",
        }),
      ).toBeEnabled();
      expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
      unmount();
    },
  );

  it("draws selection chrome around an adopted image", async () => {
    stubImageDecode();
    const rectangles: unknown[][] = [];
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockImplementation(
      function (this: HTMLCanvasElement) {
        return new Proxy({} as CanvasRenderingContext2D, {
          get: (_target, property) => {
            if (property === "canvas") return this;
            return (...args: unknown[]) => {
              if (property === "strokeRect") rectangles.push(args);
              if (property === "measureText") return { width: 10 };
            };
          },
        });
      },
    );
    const descriptor = imageDescriptor("selected-image");
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={{ ...descriptor, items: [descriptor.items[0]!] }}
      />,
    );
    await waitFor(() =>
      expect(
        rectangles.some(
          (rectangle) => rectangle[2] === 50 && rectangle[3] === 30,
        ),
      ).toBe(true),
    );
  });
  it("retires all sixteen adopted image URIs on real unmount", async () => {
    stubImageDecode();
    const actions = createActions();
    const descriptor = imageDescriptor("full-quota");
    const items = Array.from({ length: 16 }, (_, index) => ({
      ...descriptor.items[0]!,
      assetId: `asset-${index}`,
      resourceUri: `https://app.vibeocr/__resource/image-${index}`,
    }));
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={{ ...descriptor, items }}
      />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({ type: "recognition.ackImport" }),
      ),
    );
    unmount();
    await act(async () => {
      await Promise.resolve();
    });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.releaseAssets",
      sessionId: SESSION_A.sessionId,
      resourceUris: items.map((item) => item.resourceUri),
    });
  });

  it("retires a pending descriptor immediately and releases a late acknowledgement", async () => {
    stubImageDecode();
    let resolveAck!: (accepted: boolean) => void;
    const actions = createActions();
    actions.run.mockImplementation((action: { type: string }) =>
      action.type === "recognition.ackImport"
        ? new Promise<boolean>((resolve) => {
            resolveAck = resolve;
          })
        : Promise.resolve(true),
    );
    const descriptor = imageDescriptor("ack-during-retirement");
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={{ ...descriptor, items: [descriptor.items[0]!] }}
      />,
    );
    await waitFor(() => expect(resolveAck).toBeDefined());
    unmount();
    await act(async () => {
      await Promise.resolve();
    });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.rejectImport",
      sessionId: SESSION_A.sessionId,
      revision: 0,
      requestId: descriptor.requestId,
    });
    actions.run.mockClear();
    await act(async () => resolveAck(true));
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.releaseAssets",
      sessionId: SESSION_A.sessionId,
      resourceUris: [descriptor.items[0]!.resourceUri],
    });
  });

  it("rejects delayed decoding during an object move and preserves the unfinished drag", async () => {
    stubImageDecode("ok", { width: 900, height: 600 });
    recordCanvasDrawImage([]);
    let resolveText!: (value: { ok: boolean; text(): Promise<string> }) => void;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise((resolve) => {
            resolveText = resolve;
          }),
      ),
    );
    const actions = createActions();
    const props = {
      actions,
      canExport: false,
      canRecognize: false,
      source: "https://app.vibeocr/__resource/base.png",
      session: SESSION_A,
    };
    const { rerender, unmount } = render(<ImageCanvasEditor {...props} />);
    const canvas = screen.getByLabelText("图片检查画布");
    vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
      left: 0,
      top: 0,
      width: 900,
      height: 600,
    } as DOMRect);
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 110, clientY: 90 });
    const descriptor = imageDescriptor("during-move", SESSION_A.sessionId, 1);
    rerender(
      <ImageCanvasEditor
        {...props}
        systemImport={{ ...descriptor, items: [descriptor.items[1]!] }}
      />,
    );
    await waitFor(() => expect(resolveText).toBeDefined());
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.change(screen.getByLabelText("选择标注对象"), {
      target: { value: "0" },
    });
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 50, clientY: 50 });
    fireEvent.pointerMove(canvas, { pointerId: 2, clientX: 80, clientY: 80 });
    await act(async () =>
      resolveText({ ok: true, text: () => Promise.resolve("迟到导入") }),
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.rejectImport",
        sessionId: SESSION_A.sessionId,
        revision: 1,
        requestId: descriptor.requestId,
      }),
    );
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type: string }).type === "recognition.ackImport",
      ),
    ).toBe(false);
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 80, clientY: 80 });
    expect(
      actions.run.mock.calls.filter(
        ([action]) =>
          (action as { type: string }).type ===
          "recognition.notifyScreenshotRevision",
      ),
    ).toHaveLength(2);
    unmount();
  });

  it("keeps copied image pixels after undo and redo truncation until the clipboard is replaced", async () => {
    const images = stubImageDecode();
    const targets: unknown[] = [];
    recordCanvasDrawImage(targets);
    const actions = createActions();
    const descriptor = imageDescriptor("copied-image");
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={{ ...descriptor, items: [descriptor.items[0]!] }}
      />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({ type: "recognition.ackImport" }),
      ),
    );
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    fireEvent.click(screen.getByRole("button", { name: "复制对象" }));
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    const canvas = screen.getByLabelText("图片检查画布");
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 80 });
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type: string }).type === "recognition.releaseAssets",
      ),
    ).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "选择" }));
    targets.length = 0;
    fireEvent.click(screen.getByRole("button", { name: "粘贴对象" }));
    await waitFor(() =>
      expect(targets.some((target) => images.includes(target))).toBe(true),
    );
    unmount();
  });

  it("rejects an in-flight decode after source/session replacement at the same revision", async () => {
    stubImageDecode();
    let resolveText!: (value: { ok: boolean; text(): Promise<string> }) => void;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise((resolve) => {
            resolveText = resolve;
          }),
      ),
    );
    const actions = createActions();
    const descriptor = imageDescriptor("req-replaced");
    const { rerender, unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={descriptor}
      />,
    );
    await waitFor(() => expect(resolveText).toBeDefined());
    rerender(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/replacement.png"
        session={{ sessionId: "session-b", revision: 0 }}
      />,
    );
    await act(async () =>
      resolveText({ ok: true, text: () => Promise.resolve("迟到") }),
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.rejectImport",
        sessionId: SESSION_A.sessionId,
        revision: 0,
        requestId: descriptor.requestId,
      }),
    );
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type: string }).type === "recognition.ackImport",
      ),
    ).toBe(false);
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
    unmount();
  });

  it("commits one history entry after decoding and acknowledges once", async () => {
    const createdImages = stubImageDecode();
    const drawImageTargets: unknown[] = [];
    const restoreContext = recordCanvasDrawImage(drawImageTargets);
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: () => Promise.resolve("导入文本"),
    });
    vi.stubGlobal("fetch", fetchMock);
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={imageDescriptor("req-1")}
      />,
    );

    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.ackImport",
        sessionId: SESSION_A.sessionId,
        revision: SESSION_A.revision,
        requestId: "req-1",
      }),
    );
    // 一次批量导入一次历史：内容修订只推进一次。
    expect(
      actions.run.mock.calls.filter(
        ([action]) =>
          (action as { type?: string }).type ===
          "recognition.notifyScreenshotRevision",
      ),
    ).toHaveLength(1);
    // 空白画布上导入即成为对象：可撤销（历史新增一项），新编辑截断重做后
    // 触发导入资产 GC（含重做分支扫描），宿主资源被精确释放。
    fireEvent.click(screen.getByRole("button", { name: "撤销" }));
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type?: string }).type ===
            "recognition.notifyScreenshotRevision" &&
          (action as { revision?: number }).revision === 2,
      ),
    ).toBe(true);
    // 撤销后重做分支仍引用资产：尚不释放。
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type?: string }).type === "recognition.releaseAssets" &&
          (action as { resourceUris: string[] }).resourceUris.some(
            (uri) => uri === "https://app.vibeocr/__resource/import-image",
          ),
      ),
    ).toBe(false);
    const canvas = screen.getByLabelText("图片检查画布");
    // 导入位图进入同一绘制分派：预览画布 drawImage 收到已解码 asset。
    await waitFor(() => {
      expect(
        drawImageTargets.some((target) => createdImages.includes(target)),
      ).toBe(true);
    });
    restoreContext();
    fireEvent.click(screen.getByRole("button", { name: "矩形" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 100, clientY: 80 });
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.releaseAssets",
        sessionId: SESSION_A.sessionId,
        resourceUris: ["https://app.vibeocr/__resource/import-image"],
      }),
    );
    unmount();
  });

  it("is idempotent under replay of the same import descriptor", async () => {
    stubImageDecode();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        text: () => Promise.resolve("导入文本"),
      }),
    );
    const actions = createActions();
    const { rerender, unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={imageDescriptor("req-replay")}
      />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.ackImport",
          requestId: "req-replay",
        }),
      ),
    );
    // 状态重放（同文档 bootstrap/订阅重发）：不二次追加、不二次 ack。
    rerender(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={imageDescriptor("req-replay")}
      />,
    );
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(
      actions.run.mock.calls.filter(
        ([action]) =>
          (action as { type?: string }).type === "recognition.ackImport",
      ),
    ).toHaveLength(1);
    expect(
      actions.run.mock.calls.filter(
        ([action]) =>
          (action as { type?: string }).type ===
          "recognition.notifyScreenshotRevision",
      ),
    ).toHaveLength(1);
    unmount();
  });

  it("rejects a stale import without touching the current canvas", async () => {
    stubImageDecode();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={{ sessionId: "session-b", revision: 3 }}
        systemImport={imageDescriptor("req-stale")}
      />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.rejectImport",
        sessionId: SESSION_A.sessionId,
        revision: SESSION_A.revision,
        requestId: "req-stale",
      }),
    );
    // 拒收不产生任何内容修订或 ack。
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type?: string }).type ===
            "recognition.notifyScreenshotRevision" ||
          (action as { type?: string }).type === "recognition.ackImport",
      ),
    ).toBe(false);
    unmount();
  });

  it("rejects and keeps the canvas when image decoding fails", async () => {
    stubImageDecode("error");
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport={false}
        canRecognize={false}
        source="https://app.vibeocr/__resource/base.png"
        session={SESSION_A}
        systemImport={{
          ...imageDescriptor("req-broken"),
          items: [imageDescriptor("req-broken").items[0]!],
        }}
      />,
    );
    // 图像资源 onerror：本批拒收，不产生 ack，旧画布无任何修订。
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.rejectImport",
        sessionId: SESSION_A.sessionId,
        revision: SESSION_A.revision,
        requestId: "req-broken",
      }),
    );
    expect(
      actions.run.mock.calls.some(
        ([action]) =>
          (action as { type?: string }).type === "recognition.ackImport" ||
          (action as { type?: string }).type ===
            "recognition.notifyScreenshotRevision",
      ),
    ).toBe(false);
    unmount();
  });
});

it.each(["finished-duplicate", "finish-during-decode"] as const)(
  "R16 rejects import and preserves the source selection: %s",
  async (timing) => {
    let finishDecode: (() => void) | undefined;
    class ControlledImage {
      naturalWidth = 900;
      naturalHeight = 600;
      decoding = "async";
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      set src(value: string) {
        if (value.includes("import-image"))
          finishDecode = () => this.onload?.();
        else queueMicrotask(() => this.onload?.());
      }
    }
    vi.stubGlobal("Image", ControlledImage);
    recordCanvasDrawImage([]);
    const actions = createActions();
    const props = {
      actions,
      canExport: true,
      canRecognize: false,
      source: "https://app.vibeocr/__resource/base.png",
      session: SESSION_A,
    };
    const { rerender, unmount } = render(<ImageCanvasEditor {...props} />);
    fireEvent.click(screen.getByText("使用帮助与图像信息"));
    await waitFor(() => expect(screen.getByText(/原图 900×600/)).toBeVisible());
    const canvas = screen.getByLabelText("图片检查画布");
    vi.spyOn(canvas, "getBoundingClientRect").mockReturnValue({
      left: 0,
      top: 0,
      width: 900,
      height: 600,
    } as DOMRect);
    fireEvent.click(screen.getByRole("button", { name: "重复匹配" }));
    fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 200, clientY: 200 });
    const finishSelection = () =>
      fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 280, clientY: 260 });
    if (timing === "finished-duplicate") {
      finishSelection();
      expect(screen.getByRole("button", { name: "查找重复" })).toBeEnabled();
    }
    const descriptor = imageDescriptor("R16-" + timing);
    rerender(
      <ImageCanvasEditor
        {...props}
        systemImport={{ ...descriptor, items: [descriptor.items[0]!] }}
      />,
    );
    expect(finishDecode).toBeTypeOf("function");
    if (timing === "finish-during-decode") {
      expect(screen.getByRole("button", { name: "查找重复" })).toBeDisabled();
      finishSelection();
      expect(screen.getByRole("button", { name: "查找重复" })).toBeEnabled();
    }
    await act(async () => finishDecode!());
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "recognition.rejectImport",
        sessionId: SESSION_A.sessionId,
        revision: 0,
        requestId: descriptor.requestId,
      }),
    );
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.ackImport" }),
    );
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.notifyScreenshotRevision" }),
    );
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "recognition.releaseAssets" }),
    );
    expect(screen.getByRole("button", { name: "查找重复" })).toBeEnabled();
    expect(
      screen.getByText(/导入期间画布已变化或存在未完成草稿/),
    ).toBeVisible();
    expect(screen.queryByText(/已导入/)).toBeNull();
    expect(screen.getByRole("button", { name: "撤销" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "清除编辑" })).toBeDisabled();
    // A short replacement gesture explicitly clears the previous source selection.
    fireEvent.pointerDown(canvas, { pointerId: 2, clientX: 200, clientY: 200 });
    fireEvent.pointerUp(canvas, { pointerId: 2, clientX: 200, clientY: 200 });
    expect(screen.getByRole("button", { name: "查找重复" })).toBeDisabled();
    actions.run.mockClear();
    const next = {
      ...descriptor,
      requestId: descriptor.requestId + "-after-clear",
    };
    rerender(
      <ImageCanvasEditor
        {...props}
        systemImport={{ ...next, items: [next.items[0]!] }}
      />,
    );
    await act(async () => finishDecode!());
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.ackImport",
      sessionId: SESSION_A.sessionId,
      revision: 0,
      requestId: next.requestId,
    });
    expect(actions.run).toHaveBeenCalledWith(
      expect.objectContaining({
        type: "recognition.notifyScreenshotRevision",
        revision: 1,
      }),
    );
    unmount();
  },
);
