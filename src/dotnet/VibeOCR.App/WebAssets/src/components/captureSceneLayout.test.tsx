import type { CaptureScene } from "../bridge/captureScene";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ImageCanvasEditor } from "./ImageCanvasEditor";
import { captureSceneStyle } from "./captureSceneLayout";

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

describe("截图原位编辑", () => {
  it("Esc 先退出当前工具、再次退出现场，输入框的 Esc 不丢失截图", () => {
    const run = vi.fn().mockResolvedValue(true);
    const { unmount } = render(
      <ImageCanvasEditor
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "文字" }));
    fireEvent.keyDown(screen.getByRole("textbox", { name: "标注文字" }), {
      key: "Escape",
    });
    expect(run).not.toHaveBeenCalled();
    const canvas = screen.getByLabelText("图片检查画布");
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(run).not.toHaveBeenCalled();
    fireEvent.keyDown(canvas, { key: "Escape" });
    expect(run).toHaveBeenCalledWith({
      type: "recognition.closeScreenshotSession",
    });
    unmount();
  });
  it("工具栏和空白区域可取消，IME 与按键重复不关闭截图", () => {
    const run = vi.fn().mockResolvedValue(true);
    const { unmount } = render(
      <ImageCanvasEditor
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
        canExport
        canRecognize
        source={scene.inputUrl}
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    const rectangle = screen.getByRole("button", { name: "矩形" });
    fireEvent.click(rectangle);
    fireEvent.keyDown(rectangle, { key: "Escape", isComposing: true });
    expect(rectangle).toHaveAttribute("aria-pressed", "true");
    fireEvent.keyDown(rectangle, { key: "Escape" });
    expect(screen.getByRole("button", { name: "选择" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(run).not.toHaveBeenCalled();
    fireEvent.keyDown(document.body, { key: "Escape", repeat: true });
    expect(run).not.toHaveBeenCalled();
    fireEvent.contextMenu(document.body);
    expect(run).toHaveBeenCalledWith({
      type: "recognition.closeScreenshotSession",
    });
    unmount();
  });
  it("把画布放回框选屏幕位置且保持 DPI 后的选区尺寸", () => {
    const { unmount } = render(
      <ImageCanvasEditor
        actions={{
          run: vi.fn().mockResolvedValue(true),
          navigate: vi.fn(),
          setTheme: vi.fn(),
        }}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    const editor = screen.getByLabelText("图片视口").parentElement!;
    expect(editor).toHaveClass("capture-scene-editor");
    expect(screen.getByLabelText("图片检查画布")).toHaveAttribute(
      "width",
      "900",
    );
    expect(screen.getByLabelText("图片检查画布")).toHaveAttribute(
      "height",
      "400",
    );
    expect(editor.style.getPropertyValue("--capture-x")).toBe(
      `${(400 * window.innerWidth) / 2560}px`,
    );
    expect(editor.style.getPropertyValue("--capture-width")).toBe(
      `${(900 * window.innerWidth) / 2560}px`,
    );
    expect(
      screen.getByRole("button", { name: "保存标注图" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "识别文字" }),
    ).toBeInTheDocument();
    unmount();
  });
  it("多屏负坐标先由 native 转为桌面局部坐标，工具靠屏幕边缘时仍可见", () => {
    const style = captureSceneStyle(
      { ...scene, x: 2000, y: 1000, width: 500, height: 400 },
      1280,
      720,
    )!;
    expect(style["--capture-x"]).toBe("1000px");
    expect(style["--capture-y"]).toBe("500px");
    expect(style["--capture-tools-left"]).toBe("80px");
    // 单排工具栏高度预算 64px：优先放在选区下方，放不下时改到选区上方
    // （y - 64 - 8）；宽度预算 1200px 保证内容自然宽至视口上限。
    expect(style["--capture-tools-height"]).toBe("64px");
    expect(style["--capture-tools-top"]).toBe("428px");
  });
  it("单排工具栏预算不高于视口，窄视口也不放大", () => {
    // 预算始终钳到视口高；过宽内容由容器内水平滚动兑底，不再换行。
    expect(captureSceneStyle(scene, 1280, 90)!["--capture-tools-height"]).toBe(
      "64px",
    );
    expect(captureSceneStyle(scene, 640, 720)!["--capture-tools-height"]).toBe(
      "64px",
    );
  });
  it("选区几乎全屏、工具栏钉底时输出设置翻转到覆盖层上方且完整在视口内", () => {
    // y=0、height=1360（桌面 1440）→ 视口 720 下选区高 680，下方放不下
    // 单排工具栏，回退到底部钉住（720-64=656）。
    const style = captureSceneStyle(
      { ...scene, y: 0, height: 1360 },
      1280,
      720,
    )!;
    expect(style["--capture-tools-top"]).toBe("656px");
    // 656+64+4+132 放不下 → 翻转到覆盖层上方：520 + 132 = 652 ≤ 720。
    expect(style["--capture-settings-top"]).toBe("520px");
    expect(style["--capture-settings-max-height"]).toBe("132px");
  });
  it("现场工具栏单排无设置入口：必要导出参数内联主排", () => {
    const { unmount } = render(
      <ImageCanvasEditor
        actions={{
          run: vi.fn().mockResolvedValue(true),
          navigate: vi.fn(),
          setTheme: vi.fn(),
        }}
        canExport
        canRecognize
        source={scene.inputUrl}
        captureScene={scene}
        session={{ sessionId: "capture", revision: 0 }}
      />,
    );
    // 无独立“输出设置”入口（齿轮/文字 summary 均不渲染）。
    expect(screen.queryByLabelText("输出设置")).not.toBeInTheDocument();
    // 格式/JPEG 质量随输出集群内联；PNG 默认不显示质量选择。
    expect(screen.getByLabelText("输出格式")).toBeInTheDocument();
    expect(screen.queryByLabelText("JPEG 质量")).not.toBeInTheDocument();
    // 圆角开关始终可达（可发现性）。
    expect(
      screen.getByRole("button", { name: "截图直角，点击切换圆角" }),
    ).toBeInTheDocument();
    unmount();
  });
  it("普通图片编辑不使用截图屏幕几何，拒绝超出冻结桌面的选区", () => {
    expect(captureSceneStyle(undefined, 1280, 720)).toBeUndefined();
    expect(captureSceneStyle({ ...scene, x: -1 }, 1280, 720)).toBeUndefined();
    expect(
      captureSceneStyle({ ...scene, width: 3000 }, 1280, 720),
    ).toBeUndefined();
  });
});
