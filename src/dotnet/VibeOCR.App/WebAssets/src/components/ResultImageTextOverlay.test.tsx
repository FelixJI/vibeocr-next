import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  ResultImageTextOverlay,
  type IndexedResultLine,
} from "./ResultImageTextOverlay";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const loadWithSize = (
  image: HTMLImageElement,
  width: number,
  height: number,
) => {
  // jsdom 不解码图片：注入自然尺寸后触发 load（无 RO 时回退 1:1）。
  Object.defineProperties(image, {
    naturalWidth: { value: width, configurable: true },
    naturalHeight: { value: height, configurable: true },
  });
  fireEvent.load(image);
};

function stubResizeObserver() {
  const callbacks: (() => void)[] = [];
  class ResizeObserverStub {
    constructor(callback: () => void) {
      callbacks.push(callback);
    }
    observe(): void {}
    disconnect(): void {}
  }
  vi.stubGlobal("ResizeObserver", ResizeObserverStub);
  return {
    resize: () => callbacks.forEach((callback) => callback()),
  };
}

describe("result image text overlay", () => {
  it("projects result line boxes and low-confidence marks onto the image", () => {
    const lines: IndexedResultLine[] = [
      {
        index: 0,
        line: { text: "高置信行", score: 0.9, bbox: [0, 0, 500, 100] },
      },
      {
        index: 1,
        line: { text: "低置信行", score: 0.58, bbox: [0, 200, 500, 300] },
      },
    ];
    render(
      <ResultImageTextOverlay
        imageUrl="https://resource.vibeocr/result-image.png"
        lines={lines}
        onUpdateLine={vi.fn(async () => true)}
      />,
    );
    const image = screen.getByRole("img", {
      name: "识别结果参考图",
    }) as HTMLImageElement;
    loadWithSize(image, 1000, 600);

    const first = screen.getByLabelText("高置信行");
    expect(first).toHaveStyle({
      left: "0px",
      top: "0px",
      width: "500px",
      height: "60px",
    });
    expect(first).not.toHaveClass("low-confidence");
    expect(first).toBeEmptyDOMElement();
    const low = screen.getByLabelText("低置信行");
    expect(low).toHaveClass("low-confidence");
    expect(low).toHaveAttribute("title", "置信度: 58.0%");
  });

  it("reprojects boxes when the layout resizes through ResizeObserver", () => {
    const observer = stubResizeObserver();
    const lines: IndexedResultLine[] = [
      {
        index: 0,
        line: { text: "行文本", score: null, bbox: [0, 0, 500, 100] },
      },
    ];
    const { container } = render(
      <ResultImageTextOverlay
        imageUrl="https://resource.vibeocr/result-image.png"
        lines={lines}
        onUpdateLine={vi.fn(async () => true)}
      />,
    );
    const image = screen.getByRole("img", {
      name: "识别结果参考图",
    }) as HTMLImageElement;
    loadWithSize(image, 1000, 500);
    const stage = container.querySelector(".result-image-stage") as HTMLElement;
    const line = screen.getByLabelText("行文本");
    expect(line).toHaveStyle({ width: "500px", height: "50px" });

    // 面板/窗口缩放：舞台 1:1 → 1:2 缩小，行框即时重投影。
    Object.defineProperties(stage, {
      clientWidth: { value: 500, configurable: true },
      clientHeight: { value: 250, configurable: true },
    });
    act(() => observer.resize());
    expect(line).toHaveStyle({ width: "250px", height: "25px" });
  });

  it("悬停行框上报行下标，受侧高亮下标时对应行加亮", () => {
    const lines: IndexedResultLine[] = [
      {
        index: 0,
        line: { text: "第一行", score: 0.9, bbox: [0, 0, 500, 100] },
      },
      {
        index: 3,
        line: { text: "第四行", score: 0.9, bbox: [0, 200, 500, 300] },
      },
    ];
    const onHoverLine = vi.fn();
    const view = render(
      <ResultImageTextOverlay
        imageUrl="https://resource.vibeocr/result-image.png"
        lines={lines}
        onHoverLine={onHoverLine}
      />,
    );
    loadWithSize(
      screen.getByRole("img", { name: "识别结果参考图" }) as HTMLImageElement,
      1000,
      600,
    );
    // 图上悬停 → 上报宿主行下标（右侧结果行同步加亮）。
    fireEvent.mouseEnter(screen.getByLabelText("第四行"));
    expect(onHoverLine).toHaveBeenCalledWith(3);
    fireEvent.mouseLeave(screen.getByLabelText("第四行"));
    expect(onHoverLine).toHaveBeenCalledWith(undefined);
    // 右侧悬停回传的 highlightIndex 同步加亮图上对应行框。
    view.rerender(
      <ResultImageTextOverlay
        imageUrl="https://resource.vibeocr/result-image.png"
        lines={lines}
        onHoverLine={onHoverLine}
        highlightIndex={3}
      />,
    );
    expect(screen.getByLabelText("第四行")).toHaveClass("hovered");
    expect(screen.getByLabelText("第一行")).not.toHaveClass("hovered");
  });

  it("starts editing via Enter on a focused line and keeps drafts on failure", async () => {
    const lines: IndexedResultLine[] = [
      {
        index: 1,
        line: { text: "旧行文本", score: 0.8, bbox: [10, 10, 400, 80] },
      },
    ];
    const onUpdateLine = vi.fn(async () => false);
    render(
      <ResultImageTextOverlay
        imageUrl="https://resource.vibeocr/result-image.png"
        lines={lines}
        onUpdateLine={onUpdateLine}
      />,
    );
    loadWithSize(
      screen.getByRole("img", { name: "识别结果参考图" }) as HTMLImageElement,
      1000,
      500,
    );

    // 键盘可达：聚焦行框后 Enter 开始编辑（原索引随行携带，不因无 bbox 行漂移）。
    const trigger = screen.getByLabelText("旧行文本");
    trigger.focus();
    await fireEvent.keyDown(trigger, { key: "Enter" });
    const editor = screen.getByRole("textbox", { name: "编辑第 2 行" });
    await fireEvent.change(editor, { target: { value: "新行文本" } });
    await fireEvent.click(screen.getByRole("button", { name: "保存" }));
    expect(onUpdateLine).toHaveBeenCalledWith(1, "旧行文本", "新行文本");

    // 失败保留草稿可重试，Escape 取消。
    await waitFor(() =>
      expect(screen.getByText(/保存失败/)).toBeInTheDocument(),
    );
    expect(
      (
        screen.getByRole("textbox", {
          name: "编辑第 2 行",
        }) as HTMLTextAreaElement
      ).value,
    ).toBe("新行文本");
    await fireEvent.keyDown(editor, { key: "Escape" });
    expect(
      screen.queryByRole("textbox", { name: "编辑第 2 行" }),
    ).not.toBeInTheDocument();
  });

  it("commits a successful edit and hides the editor", async () => {
    const lines: IndexedResultLine[] = [
      {
        index: 0,
        line: { text: "旧行文本", score: null, bbox: [0, 0, 500, 100] },
      },
    ];
    const onUpdateLine = vi.fn(async () => true);
    render(
      <ResultImageTextOverlay
        imageUrl="https://resource.vibeocr/result-image.png"
        lines={lines}
        onUpdateLine={onUpdateLine}
      />,
    );
    loadWithSize(
      screen.getByRole("img", { name: "识别结果参考图" }) as HTMLImageElement,
      1000,
      500,
    );

    await fireEvent.dblClick(screen.getByLabelText("旧行文本"));
    await fireEvent.change(
      screen.getByRole("textbox", { name: "编辑第 1 行" }),
      {
        target: { value: "新行文本" },
      },
    );
    await fireEvent.click(screen.getByRole("button", { name: "保存" }));

    await waitFor(() =>
      expect(
        screen.queryByRole("textbox", { name: "编辑第 1 行" }),
      ).not.toBeInTheDocument(),
    );
    expect(onUpdateLine).toHaveBeenCalledWith(0, "旧行文本", "新行文本");
  });
});

it("keeps valid bbox lines when another result line has no bbox", () => {
  render(
    <ResultImageTextOverlay
      imageUrl="/result.png"
      lines={[
        { index: 0, line: { text: "无位置", score: null, bbox: null } },
        {
          index: 1,
          line: { text: "有位置", score: 0.9, bbox: [0, 0, 100, 100] },
        },
      ]}
    />,
  );
  loadWithSize(
    screen.getByRole("img", { name: "识别结果参考图" }) as HTMLImageElement,
    1000,
    500,
  );
  expect(screen.queryByText("无位置")).not.toBeInTheDocument();
  expect(screen.getByLabelText("有位置")).toBeInTheDocument();
});
