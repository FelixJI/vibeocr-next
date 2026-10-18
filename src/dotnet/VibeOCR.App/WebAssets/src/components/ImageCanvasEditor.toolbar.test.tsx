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
import { DEFAULT_EDITOR_PREFERENCES } from "./editorPreferences";

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

/** 按钮/菜单项内矢量图标的 path 数据（区分不同图标）。 */
function iconPathOf(element: HTMLElement): string | null {
  return element.querySelector("svg path")?.getAttribute("d") ?? null;
}

describe("截图现场主排（全部工具+操作）与属性下排", () => {
  it.each([undefined, scene])(
    "常用预设保存操作只在主排，属性/输出行不承载操作（现场=%s）",
    (captureScene) => {
      const props = {
        actions: createActions(),
        canExport: true,
        canRecognize: true,
        source: "https://app.vibeocr/__resource/screenshot.bmp",
        captureScene,
        session: { sessionId: "capture", revision: 0 },
        showAutoTextPreference: false,
        editorSettings: { imageOutputDirectory: "C:/authorized-output" },
      };
      const { rerender } = render(<ImageCanvasEditor {...props} />);
      const mainRow = screen.getByRole("toolbar", { name: "图片编辑工具" });
      const main = within(mainRow);
      const savePreset = main.getByRole("button", {
        name: "按常用预设保存",
      });
      expect(savePreset).toBeEnabled();
      expect(savePreset.closest(".editor-toolbar-main")).toBe(mainRow);
      const outputRow = document.querySelector(
        '.editor-output-settings [role="toolbar"]',
      );
      expect(outputRow).not.toBeNull();
      expect(outputRow).not.toContainElement(savePreset);
      expect(
        within(outputRow as HTMLElement).queryByRole("button", {
          name: "按常用预设保存",
          hidden: true,
        }),
      ).toBeNull();
      fireEvent.click(main.getByRole("button", { name: "矩形" }));
      const properties = within(
        screen.getByRole("toolbar", { name: "当前工具设置" }),
      );
      expect(
        properties.queryByRole("button", {
          name: "按常用预设保存",
        }),
      ).toBeNull();
      rerender(<ImageCanvasEditor {...props} canExport={false} />);
      expect(savePreset).toBeDisabled();
    },
  );

  it("new tools respect custom order, hidden menu and local shortcuts", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        editorSettings={{
          editorPreferences: {
            ...DEFAULT_EDITOR_PREFERENCES,
            order: [
              "magnifier",
              ...DEFAULT_EDITOR_PREFERENCES.order.filter(
                (tool) => tool !== "magnifier",
              ),
            ],
            hidden: ["spotlight", "objectEraser"],
            shortcuts: { spotlight: "s", magnifier: "m", objectEraser: "e" },
          },
        }}
      />,
    );
    const mainRow = screen.getByRole("toolbar", { name: "图片编辑工具" });
    const main = within(mainRow);
    expect(main.getAllByRole("button")[0]).toHaveAccessibleName("放大镜");
    expect(main.queryByRole("button", { name: "聚光灯" })).toBeNull();
    expect(main.queryByRole("button", { name: "橡皮擦" })).toBeNull();
    fireEvent.keyDown(mainRow, { key: "m" });
    expect(main.getByRole("button", { name: "放大镜" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    fireEvent.keyDown(mainRow, { key: "s" });
    expect(screen.getByLabelText("聚光灯暗度")).toBeInTheDocument();
    fireEvent.keyDown(mainRow, { key: "e" });
    expect(screen.getByText(/拖动擦除命中的未锁定对象/)).toBeInTheDocument();
    fireEvent.click(main.getByRole("button", { name: "更多操作" }));
    expect(
      screen.getByRole("menuitem", { name: "聚光灯" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("menuitem", { name: "橡皮擦" }),
    ).toBeInTheDocument();
  });

  it("主排承载全部工具与操作；下排仅当前工具属性，无属性时收起", () => {
    const actions = createActions();
    render(
      <ImageCanvasEditor
        actions={actions}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
        showAutoTextPreference={false}
      />,
    );
    expect(document.querySelector(".editor-toolbar-stack")).not.toBeNull();
    // 主排（图片编辑工具）：全部绘图工具与操作同排，不把工具挪去下排。
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    for (const name of [
      "选择",
      "手形",
      "矩形",
      "椭圆",
      "箭头",
      "画笔",
      "屏蔽",
      "遮挡",
      "聚光灯",
      "放大镜",
      "橡皮擦",
      "去水印",
      "重复匹配",
      "旋转 90°",
      "撤销",
      "重做",
      "复制标注图",
      "保存标注图",
      "识别当前图",
      "贴图",
      "结束会话",
      "更多操作",
    ]) {
      expect(main.getByRole("button", { name })).toBeInTheDocument();
    }
    // 选择工具无属性：下排收起，主排无样式控件。
    expect(screen.queryByRole("toolbar", { name: "当前工具设置" })).toBeNull();
    expect(main.queryByRole("group", { name: "标注颜色" })).toBeNull();
    // 切到矩形：下排只出现颜色/线宽等当前工具属性。
    fireEvent.click(main.getByRole("button", { name: "矩形" }));
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    expect(
      settings.getByRole("group", { name: "标注颜色" }),
    ).toBeInTheDocument();
    expect(settings.getByLabelText("线条宽度")).toBeInTheDocument();
    expect(main.queryByLabelText("线条宽度")).toBeNull();
    // 手形无属性：下排整体收起。
    fireEvent.click(main.getByRole("button", { name: "手形" }));
    expect(screen.queryByRole("toolbar", { name: "当前工具设置" })).toBeNull();
    // 去水印：修补操作（预览）在主排且未选区时禁用；掩膜绘制方式属于
    // 当前工具属性，出现在下排而不是主排。
    fireEvent.click(main.getByRole("button", { name: "去水印" }));
    expect(main.getByRole("button", { name: "预览修补" })).toBeDisabled();
    const inpaintSettings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    expect(
      inpaintSettings.getByLabelText("修补掩膜绘制方式"),
    ).toBeInTheDocument();
    expect(main.queryByLabelText("修补掩膜绘制方式")).toBeNull();
  });

  it("两排按钮统一 Tabler 描边矢量图标；更多菜单按可用状态收纳低频操作", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
        showAutoTextPreference={false}
      />,
    );
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    // 主排按钮均为描边矢量图标（stroke、无填充），无文本裸按钮。
    for (const name of [
      "选择",
      "矩形",
      "画笔",
      "屏蔽",
      "旋转 90°",
      "撤销",
      "重做",
      "复制标注图",
      "保存标注图",
      "识别当前图",
      "贴图",
      "结束会话",
      "更多操作",
    ]) {
      const icon = main.getByRole("button", { name }).querySelector("svg");
      expect(icon).not.toBeNull();
      expect(icon).toHaveAttribute("viewBox", "0 0 24 24");
      expect(icon).toHaveAttribute("fill", "none");
      expect(icon).toHaveAttribute("stroke", "currentColor");
    }
    // 打开更多菜单：无屏蔽/无编辑时清空屏蔽与清除编辑禁用，屏蔽副本不出现。
    fireEvent.click(main.getByRole("button", { name: "更多操作" }));
    const clearExclusionsItem = screen.getByRole("menuitem", {
      name: "清空屏蔽",
    });
    expect(clearExclusionsItem).toHaveAttribute("aria-disabled", "true");
    expect(screen.getByRole("menuitem", { name: "清除编辑" })).toHaveAttribute(
      "aria-disabled",
      "true",
    );
    expect(screen.queryByRole("menuitem", { name: "复制屏蔽副本" })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "保存屏蔽副本" })).toBeNull();
    // 图标语义可区分：工具“屏蔽”与菜单“清空屏蔽”不共用同一矢量。
    expect(iconPathOf(clearExclusionsItem)).toBeTruthy();
    expect(iconPathOf(clearExclusionsItem)).not.toBe(
      iconPathOf(main.getByRole("button", { name: "屏蔽" })),
    );
    // 再次点击触发器关闭菜单：菜单项从无障碍树移除。
    fireEvent.click(main.getByRole("button", { name: "更多操作" }));
    expect(
      screen.queryByRole("menuitem", { name: "清空屏蔽" }),
    ).not.toBeInTheDocument();
  });

  it("屏蔽分支：屏蔽副本收入更多菜单并走会话命令；主排按钮保持图标+可访问名", async () => {
    stubCanvasStack();
    const actions = createActions();
    const { unmount } = render(
      <ImageCanvasEditor
        actions={actions}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{
          sessionId: "capture",
          revision: 0,
          excludeBoxes: [{ x: 0, y: 0, width: 100, height: 100 }],
        }}
        showAutoTextPreference={false}
      />,
    );
    await act(async () => {});
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    // 现场主排不渲染纯文本标签，避免拉长图标行；入口仍是可访问名可达。
    expect(main.getByRole("button", { name: "复制标注图" }).textContent).toBe(
      "",
    );
    fireEvent.click(main.getByRole("button", { name: "更多操作" }));
    // 冻结基准回显屏蔽区后：清空屏蔽可用，屏蔽副本入口出现并可发起命令。
    expect(
      screen.getByRole("menuitem", { name: "清空屏蔽" }),
    ).not.toHaveAttribute("aria-disabled", "true");
    fireEvent.click(screen.getByRole("menuitem", { name: "复制屏蔽副本" }));
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

  it("自动取字偏好仅随取字工具显示：切换工具属性排隐藏/恢复，偏好保留", () => {
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
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    // 会话带取字偏好，但初始选择工具：偏好不撑开属性排。
    expect(screen.queryByRole("toolbar", { name: "当前工具设置" })).toBeNull();
    // 切到取字：自动取字偏好出现在属性排并可勾选。
    fireEvent.click(main.getByRole("button", { name: "取字" }));
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    const checkbox = settings.getByLabelText("自动取字");
    expect(checkbox).not.toBeChecked();
    fireEvent.click(checkbox);
    expect(settings.getByLabelText("自动取字")).toBeChecked();
    // 切回选择：属性排隐藏；切回取字：偏好恢复勾选（状态未丢）。
    fireEvent.click(main.getByRole("button", { name: "选择" }));
    expect(screen.queryByRole("toolbar", { name: "当前工具设置" })).toBeNull();
    fireEvent.click(main.getByRole("button", { name: "取字" }));
    expect(screen.getByLabelText("自动取字")).toBeChecked();
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
      ".editor-toolbar-stack button",
    );
    expect(rows.length).toBeGreaterThan(0);
    for (const button of rows) {
      const name =
        button.getAttribute("aria-label") ?? button.textContent?.trim();
      expect(name, button.outerHTML).toBeTruthy();
    }
  });

  it("取消与导出行为保留：Esc 先退工具再关会话，主排导出走会话命令", async () => {
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
        showAutoTextPreference={false}
      />,
    );
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    // 撤销在空历史上禁用；结束会话仍可直达。
    expect(main.getByRole("button", { name: "撤销" })).toBeDisabled();
    const canvas = screen.getByLabelText("图片检查画布");
    fireEvent.click(main.getByRole("button", { name: "画笔" }));
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.closeScreenshotSession",
    });
    // 从主排发起复制：先等图解码，再真实编码 + 会话复制命令（不改导出协议）。
    await act(async () => {});
    fireEvent.click(main.getByRole("button", { name: "复制标注图" }));
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

describe("普通编辑页同构：工具+操作上排、属性下排", () => {
  it("导出操作并入主排，footer 不再承载按钮；属性排按工具收起", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "s", revision: 0 }}
        showAutoTextPreference={false}
      />,
    );
    // 单一主排工具栏：工具与全部操作同排。
    expect(
      screen.getAllByRole("toolbar", { name: "图片编辑工具" }),
    ).toHaveLength(1);
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    for (const name of [
      "选择",
      "矩形",
      "裁剪",
      "旋转 90°",
      "清空屏蔽",
      "撤销",
      "重做",
      "复制标注图",
      "保存标注图",
      "贴图",
      "识别当前图",
      "结束会话",
      "清除编辑",
    ]) {
      expect(main.getByRole("button", { name })).toBeInTheDocument();
    }
    expect(
      main.getByRole("button", { name: "复制标注图" }).querySelector("svg"),
    ).not.toBeNull();
    // footer 只承载状态提示，不再有导出按钮。
    expect(document.querySelector(".editor-footer button")).toBeNull();
    // 选择工具无属性：下排收起；切矩形后颜色/线宽出现在属性排。
    expect(screen.queryByRole("toolbar", { name: "当前工具设置" })).toBeNull();
    fireEvent.click(main.getByRole("button", { name: "矩形" }));
    const settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    expect(
      settings.getByRole("group", { name: "标注颜色" }),
    ).toBeInTheDocument();
    expect(settings.getByLabelText("线条宽度")).toBeInTheDocument();
    expect(main.queryByLabelText("线条宽度")).toBeNull();
    // 普通页保留文本标签（图标+文字）。
    expect(main.getByRole("button", { name: "矩形" })).toHaveTextContent(
      "矩形",
    );
  });
});

describe("形状边框/填充属性排（按工具 gating，保留全工具主排）", () => {
  it("边框不透明度随描边工具；填充开关仅矩形/椭圆；透明度 0% 为有效档位", () => {
    render(
      <ImageCanvasEditor
        actions={createActions()}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/capture.png"
        session={{ sessionId: "s", revision: 0 }}
        showAutoTextPreference={false}
      />,
    );
    const main = within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    // 主排仍承载全部工具：新增属性只进下排，不挤占主排。
    for (const name of [
      "选择",
      "矩形",
      "椭圆",
      "箭头",
      "文字",
      "画笔",
      "荧光笔",
      "裁剪",
    ]) {
      expect(main.getByRole("button", { name })).toBeInTheDocument();
    }

    fireEvent.click(main.getByRole("button", { name: "矩形" }));
    let settings = within(
      screen.getByRole("toolbar", { name: "当前工具设置" }),
    );
    const opacity = settings.getByLabelText("边框不透明度");
    expect(opacity).toHaveValue(100);
    // 0/100 边界与任意整数：0 有效提交；空值/越界不写回（不意外写 0）。
    fireEvent.change(opacity, { target: { value: "0" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(0);
    fireEvent.change(opacity, { target: { value: "" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(0);
    fireEvent.change(opacity, { target: { value: "150" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(0);
    fireEvent.change(opacity, { target: { value: "100" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(100);
    fireEvent.change(opacity, { target: { value: "37" } });
    fireEvent.blur(opacity);
    expect(opacity).toHaveValue(37);
    // 填充默认关闭（旧空心默认）：颜色/不透明度输入不出现；开启后可达。
    expect(settings.getByLabelText("填充")).not.toBeChecked();
    expect(settings.queryByLabelText("填充颜色")).toBeNull();
    fireEvent.click(settings.getByLabelText("填充"));
    settings = within(screen.getByRole("toolbar", { name: "当前工具设置" }));
    expect(settings.getByLabelText("填充颜色")).toBeInTheDocument();
    expect(settings.getByLabelText("填充不透明度")).toHaveValue(50);

    // 椭圆同构：独立边框不透明度 + 填充三件套。
    fireEvent.click(main.getByRole("button", { name: "椭圆" }));
    settings = within(screen.getByRole("toolbar", { name: "当前工具设置" }));
    expect(settings.getByLabelText("边框不透明度")).toBeInTheDocument();
    expect(settings.getByLabelText("填充")).toBeInTheDocument();

    // 箭头/画笔/荧光笔复用边框不透明度但不提供填充；文字无描边属性。
    for (const name of ["箭头", "画笔", "荧光笔"]) {
      fireEvent.click(main.getByRole("button", { name }));
      settings = within(screen.getByRole("toolbar", { name: "当前工具设置" }));
      expect(settings.getByLabelText("边框不透明度")).toBeInTheDocument();
      expect(settings.queryByLabelText("填充")).toBeNull();
    }
    fireEvent.click(main.getByRole("button", { name: "文字" }));
    settings = within(screen.getByRole("toolbar", { name: "当前工具设置" }));
    expect(settings.queryByLabelText("边框不透明度")).toBeNull();
    expect(settings.queryByLabelText("填充")).toBeNull();
  });
});
