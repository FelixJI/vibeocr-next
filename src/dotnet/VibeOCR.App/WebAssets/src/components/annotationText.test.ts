import { describe, expect, it, vi } from "vitest";
import {
  annotationFont,
  numberLabel,
  drawTextAnnotation,
  textAnnotationBounds,
  resolveAnnotationFont,
} from "./annotationText";
import type { Mark } from "./annotationGeometry";
function context() {
  return {
    font: "",
    fillStyle: "",
    save: vi.fn(),
    restore: vi.fn(),
    setLineDash: vi.fn(),
    measureText: vi.fn((text: string) => ({ width: text.length * 10 })),
    fillText: vi.fn(),
    strokeText: vi.fn(),
    fillRect: vi.fn(),
    strokeRect: vi.fn(),
    beginPath: vi.fn(),
    moveTo: vi.fn(),
    lineTo: vi.fn(),
    stroke: vi.fn(),
    fill: vi.fn(),
    arc: vi.fn(),
    rect: vi.fn(),
    closePath: vi.fn(),
  };
}
describe("annotationText", () => {
  it("字母序号支持Z之后的AA和小写，默认保留数字", () => {
    expect(numberLabel(26, "upper")).toBe("Z");
    expect(numberLabel(27, "upper")).toBe("AA");
    expect(numberLabel(52, "lower")).toBe("az");
    expect(numberLabel(8, undefined)).toBe("8");
  });
  it("多行对齐、描边、下划线和背景与测量共用尺寸，导出按比例缩放", () => {
    const ctx = context();
    const mark: Mark = {
      tool: "text",
      start: { x: 10, y: 20 },
      end: { x: 110, y: 40 },
      text: "long\ns",
      style: {
        fontSize: 20,
        textAlign: "right",
        fontUnderline: true,
        textBackground: { color: "#ffffff" },
        textOutline: { color: "#000000", width: 2 },
      },
    };
    const canvas = ctx as unknown as CanvasRenderingContext2D;
    drawTextAnnotation(canvas, mark);
    expect(ctx.fillText).toHaveBeenCalledWith("long", 70, 40);
    expect(ctx.fillText).toHaveBeenCalledWith("s", 100, 65);
    expect(ctx.strokeText).toHaveBeenCalledWith("long", 70, 40);
    expect(ctx.fillRect).toHaveBeenCalledWith(4, 14, 112, 62);
    expect(textAnnotationBounds(canvas, mark)).toEqual({
      x: 4,
      y: 14,
      width: 112,
      height: 62,
    });
    drawTextAnnotation(
      canvas,
      { ...mark, start: { x: 20, y: 40 }, end: { x: 220, y: 80 } },
      2,
    );
    expect(ctx.fillText).toHaveBeenCalledWith("s", 210, 130);
    expect(
      annotationFont({ fontSize: 20, fontBold: true, fontItalic: true }, 2),
    ).toContain("italic 700 40px");
  });
  it.each(["circle", "square", "diamond", "none"] as const)(
    "序号形状%s写入实际路径",
    (shape) => {
      const ctx = context();
      drawTextAnnotation(ctx as unknown as CanvasRenderingContext2D, {
        tool: "numbering",
        start: { x: 100, y: 100 },
        end: { x: 100, y: 100 },
        ordinal: 27,
        style: { numberShape: shape, numberFormat: "upper" },
      });
      expect(ctx.fillText).toHaveBeenCalledWith("AA", 100, 100);
      if (shape === "circle") expect(ctx.arc).toHaveBeenCalled();
      else if (shape === "square") expect(ctx.rect).toHaveBeenCalled();
      else if (shape === "diamond") expect(ctx.closePath).toHaveBeenCalled();
      else expect(ctx.fill).not.toHaveBeenCalled();
    },
  );
  it.each([
    ["右侧", { x: 200, y: 92.5 }, { x: 116, y: 92.5 }],
    ["上方", { x: 105, y: 0 }, { x: 105, y: 74 }],
    ["下方", { x: 105, y: 200 }, { x: 105, y: 111 }],
  ])("说明引线从朝向目标的%s边缘出发，不穿过文字框", (_name, target, joint) => {
    const ctx = context();
    drawTextAnnotation(ctx as unknown as CanvasRenderingContext2D, {
      tool: "callout",
      start: target as { x: number; y: number },
      end: { x: 100, y: 100 },
      text: "X",
      style: {
        fontSize: 20,
        calloutStyle: "arrow",
        textBackground: { enabled: false },
      },
    });
    const expected = joint as { x: number; y: number };
    expect(ctx.moveTo.mock.calls[0]).toEqual([expected.x, expected.y]);
  });
  it("说明目标在文字框内时不绘制引线和箭头头部", () => {
    const ctx = context();
    drawTextAnnotation(ctx as unknown as CanvasRenderingContext2D, {
      tool: "callout",
      start: { x: 105, y: 90 },
      end: { x: 100, y: 100 },
      text: "X",
      style: {
        fontSize: 20,
        calloutStyle: "arrow",
        textBackground: { enabled: false },
      },
    });
    expect(ctx.moveTo).not.toHaveBeenCalled();
    expect(ctx.lineTo).not.toHaveBeenCalled();
  });
  it.each([
    ["circle", "#000000", "#ffffff"],
    ["square", "#1f6feb", "#ffffff"],
    ["diamond", "#111111", "#ffffff"],
    ["circle", "#ffffff", "#161616"],
    ["square", "#888888", "#161616"],
    ["none", "#000000", "#000000"],
  ] as const)("序号%s底形%s使用可读文字%s", (shape, color, expected) => {
    const ctx = context();
    const inks: string[] = [];
    ctx.fillText.mockImplementation(() => {
      inks.push(ctx.fillStyle);
    });
    drawTextAnnotation(ctx as unknown as CanvasRenderingContext2D, {
      tool: "numbering",
      start: { x: 100, y: 100 },
      end: { x: 100, y: 100 },
      ordinal: 1,
      style: { numberShape: shape, color },
    });
    expect(inks).toEqual([expected]);
  });
  it("字体检测不会把不存在字体的浏览器fallback当已安装", () => {
    const ctx = context();
    const canvas = ctx as unknown as CanvasRenderingContext2D;
    expect(resolveAnnotationFont(canvas, "DefinitelyMissing230")).toEqual({
      family: "system-ui",
      available: false,
    });
    ctx.measureText.mockImplementation(() => ({
      width: ctx.font.includes("Installed") ? 50 : 20,
    }));
    expect(resolveAnnotationFont(canvas, "Installed")).toEqual({
      family: "Installed",
      available: true,
    });
    expect(resolveAnnotationFont(null, "system-ui")).toEqual({
      family: "system-ui",
      available: true,
    });
  });
});
