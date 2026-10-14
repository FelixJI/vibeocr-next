import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions } from "../app/types";
import type { CaptureScene } from "../bridge/captureScene";
import { ImageCanvasEditor } from "./ImageCanvasEditor";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const scene: CaptureScene = {
  surfaceId: "surface",
  captureSessionId: "capture",
  inputUrl: "https://app.vibeocr/__resource/screenshot.bmp",
  inputRevision: 0,
  documentGeneration: 1,
  bootstrapGeneration: 1,
  initialTool: "select",
  x: 400,
  y: 200,
  width: 900,
  height: 400,
  desktopWidth: 2560,
  desktopHeight: 1440,
};

function createActions(): AppActions & { run: ReturnType<typeof vi.fn> } {
  return {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
}

/** 现场导出冒烟所需的最小画布/图像桩（与 interaction 测试同构）。 */
function stubCanvasStack() {
  Object.defineProperty(HTMLCanvasElement.prototype, "getContext", {
    configurable: true,
    value(this: HTMLCanvasElement) {
      const handler: ProxyHandler<object> = {
        // 箭头函数词法捕获方法 this（画布元素），不用局部别名。
        get: (_target, prop) => {
          if (typeof prop !== "string") return undefined;
          if (prop === "canvas") return this;
          if (prop === "getImageData")
            return () => ({ data: new Uint8ClampedArray(4) });
          return () => undefined;
        },
        set: () => true,
      };
      return new Proxy({}, handler) as CanvasRenderingContext2D;
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
    naturalWidth = 900;
    naturalHeight = 400;
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

describe("截图现场工具栏两排布局", () => {
  it("主操作一排（工具+历史+导出），样式与上下文操作一排", () => {
    const actions = createActions();
    render(
      <ImageCanvasEditor
        actions={actions}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    expect(document.querySelector(".editor-scene-toolstack")).not.toBeNull();
    const primary = within(
      screen.getByRole("toolbar", { name: "图片编辑工具" }),
    );
    // 主操作排：标注工具、历史/旋转与导出/会话操作同排。
    for (const name of [
      "选择",
      "矩形",
      "去水印",
      "旋转 90°",
      "撤销",
      "重做",
      "复制标注图",
      "保存标注图",
      "识别当前图",
      "结束会话",
      "清除编辑",
    ]) {
      expect(primary.getByRole("button", { name })).toBeInTheDocument();
    }
    // 样式排：颜色/线宽与自动取字不混入主操作排。
    const secondary = within(
      screen.getByRole("toolbar", { name: "标注样式与辅助操作" }),
    );
    expect(
      secondary.getByRole("group", { name: "标注颜色" }),
    ).toBeInTheDocument();
    expect(secondary.getByLabelText("自动取字")).toBeInTheDocument();
    expect(primary.queryByRole("group", { name: "标注颜色" })).toBeNull();
    expect(primary.queryByLabelText("自动取字")).toBeNull();
    // 样式控件与主操作分离：线宽选择器只在样式排（默认工具 select 不显示，
    // 切换矩形后出现）。
    fireEvent.click(primary.getByRole("button", { name: "矩形" }));
    expect(secondary.getByLabelText("线条宽度")).toBeInTheDocument();
    expect(primary.queryByLabelText("线条宽度")).toBeNull();
  });

  it("图标清晰：清空屏蔽与屏蔽工具图标可区分，去水印操作带预览/应用图标", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    const primary = screen.getByRole("toolbar", {
      name: "图片编辑工具",
    });
    // 工具“屏蔽”与操作“清空屏蔽”语义不同，图标不得共用同一盾形减号。
    expect(
      within(primary)
        .getByRole("button", { name: "屏蔽" })
        .querySelector("svg.lucide-shield-off"),
    ).not.toBeNull();
    expect(
      within(primary)
        .getByRole("button", { name: "清空屏蔽" })
        .querySelector("svg.lucide-shield-x"),
    ).not.toBeNull();
    fireEvent.click(within(primary).getByRole("button", { name: "去水印" }));
    const secondary = screen.getByRole("toolbar", {
      name: "标注样式与辅助操作",
    });
    expect(
      within(secondary)
        .getByRole("button", { name: "预览修补" })
        .querySelector("svg.lucide-eye"),
    ).not.toBeNull();
    expect(
      within(secondary).getByRole("button", { name: "预览修补" }),
    ).toBeDisabled();
  });

  it("屏蔽分支：复制/保存屏蔽副本在现场均为图标+可访问名，不以纯文本撑长第一排", async () => {
    stubCanvasStack();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{
          sessionId: "capture",
          revision: 0,
          excludeBoxes: [{ x: 0, y: 0, width: 100, height: 100 }],
        }}
      />,
    );
    await act(async () => {});
    const primary = within(
      screen.getByRole("toolbar", { name: "图片编辑工具" }),
    );
    expect(
      primary
        .getByRole("button", { name: "复制屏蔽副本" })
        .querySelector("svg.lucide-clipboard-copy"),
    ).not.toBeNull();
    expect(
      primary
        .getByRole("button", { name: "保存屏蔽副本" })
        .querySelector("svg.lucide-shield-off"),
    ).not.toBeNull();
    // 现场不渲染纯文本标签，避免拉长图标行。
    expect(screen.queryByText("复制屏蔽副本")).toBeNull();
    expect(screen.queryByText("保存屏蔽副本")).toBeNull();
    unmount();
  });

  it("两排中的所有按钮均有可访问名称（图标按钮不裸露）", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    const rows = document.querySelectorAll<HTMLElement>(
      ".editor-scene-toolstack button",
    );
    expect(rows.length).toBeGreaterThan(0);
    for (const button of rows) {
      const name =
        button.getAttribute("aria-label") ?? button.textContent?.trim();
      expect(name, button.outerHTML).toBeTruthy();
    }
  });

  it("取消与导出行为保留：Esc 先退工具再关会话，第一排导出走会话命令", async () => {
    stubCanvasStack();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    const primary = within(
      screen.getByRole("toolbar", { name: "图片编辑工具" }),
    );
    // 撤销在空历史上禁用；结束会话仍在第一排可直达。
    expect(primary.getByRole("button", { name: "撤销" })).toBeDisabled();
    const canvas = screen.getByLabelText("图片检查画布");
    fireEvent.click(primary.getByRole("button", { name: "画笔" }));
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.closeScreenshotSession",
    });
    // 从第一排发起复制：先等图解码，再真实编码 + 会话复制命令（不改导出协议）。
    await act(async () => {});
    fireEvent.click(primary.getByRole("button", { name: "复制标注图" }));
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "recognition.copyScreenshotImage",
          sessionId: "capture",
        }),
      ),
    );
    await act(async () => {});
    unmount();
  });
});

describe("普通编辑页工具栏布局保持", () => {
  it("单条可换行工具栏 + 底部导出操作，不产生第二排", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "s", revision: 0 }}
      />,
    );
    expect(
      screen.getAllByRole("toolbar", { name: "图片编辑工具" }),
    ).toHaveLength(1);
    expect(
      screen.queryByRole("toolbar", { name: "标注样式与辅助操作" }),
    ).toBeNull();
    expect(document.querySelector(".editor-scene-toolstack")).toBeNull();
    // 导出操作仍由底部 footer 承载，且不重复渲染。
    expect(screen.getAllByRole("button", { name: "复制标注图" })).toHaveLength(
      1,
    );
    expect(
      document
        .querySelector(".editor-footer")!
        .querySelector('button[aria-label="复制标注图"]'),
    ).not.toBeNull();
    // 旧换行锚点（break span）保留，普通页仍按内容自动折行。
    expect(
      document.querySelector(".editor-toolbar .editor-toolbar-break"),
    ).not.toBeNull();
  });
});
