import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ImageCanvasEditor } from "./ImageCanvasEditor";
import {
  captureSceneStyle,
  type CaptureSceneGeometry,
} from "./captureSceneLayout";

const scene: CaptureSceneGeometry = {
  x: 400,
  y: 200,
  width: 900,
  height: 400,
  desktopWidth: 2560,
  desktopHeight: 1440,
};
afterEach(() => {
  delete (window as Window & { vibeocrCaptureScene?: CaptureSceneGeometry })
    .vibeocrCaptureScene;
});

describe("截图原位编辑", () => {
  it("Esc 先退出当前工具、再次退出现场，输入框的 Esc 不丢失截图", () => {
    (
      window as Window & { vibeocrCaptureScene?: CaptureSceneGeometry }
    ).vibeocrCaptureScene = scene;
    const run = vi.fn().mockResolvedValue(true);
    const { unmount } = render(
      <ImageCanvasEditor
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
        canExport
        canRecognize
        source="https://app.vibeocr/__resource/screenshot.bmp"
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
  it("把画布放回框选屏幕位置且保持 DPI 后的选区尺寸", () => {
    (
      window as Window & { vibeocrCaptureScene?: CaptureSceneGeometry }
    ).vibeocrCaptureScene = scene;
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
      screen.getByRole("button", { name: "识别当前图" }),
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
    expect(style["--capture-tools-left"]).toBe("400px");
    expect(style["--capture-tools-top"]).toBe("428px");
  });
  it("普通图片编辑不使用截图屏幕几何，拒绝超出冻结桌面的选区", () => {
    expect(captureSceneStyle(undefined, 1280, 720)).toBeUndefined();
    expect(captureSceneStyle({ ...scene, x: -1 }, 1280, 720)).toBeUndefined();
    expect(
      captureSceneStyle({ ...scene, width: 3000 }, 1280, 720),
    ).toBeUndefined();
  });
});
