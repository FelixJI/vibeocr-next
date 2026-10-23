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
    expect(style["--capture-tools-left"]).toBe("520px");
    // 两排工具栏（主操作+样式）高度预算 96px：优先放在选区下方，
    // 放不下时改到选区上方（y - 96 - 8）。
    expect(style["--capture-tools-height"]).toBe("184px");
    expect(style["--capture-tools-top"]).toBe("308px");
  });
  it("两排工具栏的预算足够容纳主操作与样式两行控件", () => {
    // 窄视口也不再把预算压到两行以下（剩余由排内换行+宿主滚动兑底）。
    expect(captureSceneStyle(scene, 1280, 90)!["--capture-tools-height"]).toBe(
      "90px",
    );
    // 窄视口（<900px）两排换行后行数增多，预算自适应放宽到 144px，
    // 避免主操作排被卷进覆盖层折叠区。
    expect(captureSceneStyle(scene, 640, 720)!["--capture-tools-height"]).toBe(
      "184px",
    );
  });
  it("选区几乎全屏、工具栏钉底时输出设置翻转到覆盖层上方且完整在视口内", () => {
    // y=0、height=1360（桌面 1440）→ 视口 720 下选区高 680，下方放不下
    // 工具栏，回退到底部钉住（720-96=624，宽视口预算 96）。
    const style = captureSceneStyle(
      { ...scene, y: 0, height: 1360 },
      1280,
      720,
    )!;
    expect(style["--capture-tools-top"]).toBe("536px");
    // 624+96+4+132 放不下 → 翻转到覆盖层上方：488 + 132 = 620 ≤ 720。
    expect(style["--capture-settings-top"]).toBe("400px");
    expect(style["--capture-settings-max-height"]).toBe("132px");
  });
  it("普通图片编辑不使用截图屏幕几何，拒绝超出冻结桌面的选区", () => {
    expect(captureSceneStyle(undefined, 1280, 720)).toBeUndefined();
    expect(captureSceneStyle({ ...scene, x: -1 }, 1280, 720)).toBeUndefined();
    expect(
      captureSceneStyle({ ...scene, width: 3000 }, 1280, 720),
    ).toBeUndefined();
  });
});
