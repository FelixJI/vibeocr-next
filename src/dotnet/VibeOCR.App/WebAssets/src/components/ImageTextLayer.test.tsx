import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { ImageTextLayer, type ImageTextLayerProps } from "./ImageTextLayer";

const BINDING = { sessionId: "session-a", revision: 3 };
const LINES = [
  { text: "你好世界", bbox: [0, 0, 500, 100] as const },
  { text: "second line", bbox: [100, 200, 900, 300] as const },
];

function fakeImage(width: number, height: number): HTMLImageElement {
  const image = new Image();
  Object.defineProperties(image, {
    naturalWidth: { value: width },
    naturalHeight: { value: height },
  });
  return image;
}

function layerProps(
  overrides: Partial<ImageTextLayerProps> = {},
): ImageTextLayerProps {
  return {
    binding: BINDING,
    activeSession: BINDING,
    lines: LINES,
    viewport: {
      image: fakeImage(1000, 500),
      size: { width: 1000, height: 500 },
    },
    ...overrides,
  };
}

afterEach(() => {
  // vitest 未启用 globals：testing-library 不会自动卸载，需显式清理。
  cleanup();
  document.getSelection()?.removeAllRanges();
});

describe("image text layer selection", () => {
  it("renders real DOM text for each line at the projected box", () => {
    render(<ImageTextLayer {...layerProps()} />);

    const first = screen.getByText("你好世界").parentElement!;
    const second = screen.getByText("second line").parentElement!;
    // 归一化框直接映射为显示像素：字号/高度取行框高，宽为行框宽。
    expect(first).toHaveStyle({
      left: "0px",
      top: "0px",
      width: "500px",
      height: "50px",
      fontSize: "50px",
    });
    expect(second).toHaveStyle({
      left: "100px",
      top: "100px",
      width: "800px",
      height: "50px",
    });
  });

  it("selects an inline Chinese substring through a native range", () => {
    render(<ImageTextLayer {...layerProps()} />);

    const firstText = screen.getByText("你好世界").firstChild!;
    const range = document.createRange();
    range.setStart(firstText, 1);
    range.setEnd(firstText, 3);
    const selection = document.getSelection()!;
    selection.addRange(range);

    expect(range.toString()).toBe("好世");
    expect(selection.anchorNode).toBe(firstText);
  });

  it("fits a long mixed line to its measured row box without fixed character widths", () => {
    const line = {
      text: "混合 Mixed words 123，标点",
      bbox: [0, 0, 500, 100] as const,
    };
    const { rerender } = render(
      <ImageTextLayer {...layerProps({ lines: [line] })} />,
    );
    const glyphs = screen.getByText(line.text);
    Object.defineProperty(glyphs, "scrollWidth", {
      value: 1000,
      configurable: true,
    });
    rerender(
      <ImageTextLayer
        {...layerProps({ lines: [{ ...line, text: `${line.text}!` }] })}
      />,
    );
    expect(screen.getByText(`${line.text}!`)).toHaveStyle({
      transform: "scaleX(0.5)",
    });
    expect(glyphs.parentElement).toHaveStyle({ width: "500px" });
  });

  it("reads across lines with an interpretable newline separator", () => {
    render(<ImageTextLayer {...layerProps()} />);

    const firstText = screen.getByText("你好世界").firstChild!;
    const secondText = screen.getByText("second line").firstChild!;
    const range = document.createRange();
    range.setStart(firstText, 1);
    range.setEnd(secondText, 6);
    document.getSelection()!.addRange(range);

    expect(range.toString()).toBe("好世界\nsecond");
  });

  it("returns the same text for a backward selection", () => {
    render(<ImageTextLayer {...layerProps()} />);

    const firstText = screen.getByText("你好世界").firstChild!;
    const secondText = screen.getByText("second line").firstChild!;
    const selection = document.getSelection()!;
    // 锚点在后一行、焦点回到前一行：反向 Selection 与正向读取同一文本。
    selection.setBaseAndExtent(secondText, 4, firstText, 3);

    expect(selection.anchorNode).toBe(secondText);
    expect(selection.focusNode).toBe(firstText);
    expect(selection.getRangeAt(0).toString()).toBe("界\nseco");
  });
});

describe("image text layer invalidation", () => {
  it("removes line text and clears a selection that lives in the layer", () => {
    const { rerender } = render(<ImageTextLayer {...layerProps()} />);

    const firstText = screen.getByText("你好世界").firstChild!;
    const range = document.createRange();
    range.setStart(firstText, 1);
    range.setEnd(firstText, 3);
    document.getSelection()!.addRange(range);
    expect(document.getSelection()!.rangeCount).toBe(1);

    // root 传入的本地修订前进（编辑即本地立即推进，不等宿主回显）：
    // 绑定不再指向当前画面，旧 DOM 选区立即失效。
    rerender(
      <ImageTextLayer
        {...layerProps({
          activeSession: { sessionId: "session-a", revision: 4 },
        })}
      />,
    );

    expect(screen.queryByText("你好世界")).toBeNull();
    expect(screen.queryByText("second line")).toBeNull();
    // 旧 DOM 已移除：即使引擎保留选区记录，其文本也必为空（不可复制）。
    const selection = document.getSelection()!;
    const selectionText =
      selection.rangeCount === 0 ? "" : selection.getRangeAt(0).toString();
    expect(selectionText).toBe("");
  });

  it("renders nothing when the binding never matched the session", () => {
    render(
      <ImageTextLayer
        {...layerProps({
          binding: { sessionId: "session-b", revision: 3 },
        })}
      />,
    );

    expect(screen.queryByText("你好世界")).toBeNull();
  });

  it("skips degenerate boxes instead of rendering a broken line", () => {
    render(
      <ImageTextLayer
        {...layerProps({
          lines: [
            { text: "正常行", bbox: [0, 0, 400, 100] },
            { text: "退化行", bbox: [100, 100, 100, 200] },
          ],
        })}
      />,
    );

    expect(screen.getByText("正常行")).toBeInTheDocument();
    expect(screen.queryByText("退化行")).toBeNull();
  });
});

describe("image text layer must not hijack other copy targets", () => {
  it("keeps input selection and an un-prevented copy event on invalidation", () => {
    const buildTree = (overrides: Partial<ImageTextLayerProps>) => (
      <>
        <input aria-label="外部输入" defaultValue="abcdef" />
        <ImageTextLayer {...layerProps(overrides)} />
      </>
    );

    const { rerender } = render(buildTree({}));
    const input = screen.getByLabelText("外部输入") as HTMLInputElement;
    input.focus();
    input.select();
    expect(input.selectionEnd).toBe(6);

    // 层内无选区：失效清理不得碰输入控件里的选择，copy 也不被拦截。
    rerender(
      buildTree({ activeSession: { sessionId: "session-a", revision: 9 } }),
    );

    expect(input.selectionStart).toBe(0);
    expect(input.selectionEnd).toBe(6);
    expect(fireEvent.copy(input)).toBe(true);
  });
});
