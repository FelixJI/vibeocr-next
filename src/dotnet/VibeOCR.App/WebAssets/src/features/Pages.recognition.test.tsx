import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { feature } from "./FeatureValues";
import { RecognitionPage } from "./RecognitionPage";

afterEach(() => {
  cleanup();
  localStorage.removeItem("recognition.previewCollapsed");
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const structuredPayload = {
  type: "lines",
  schema_version: 1,
  lines: [
    { text: "第一段行一", score: 0.9, bbox: [10, 10, 400, 40] },
    { text: "第一段行二", score: 0.61, bbox: [10, 50, 400, 80] },
  ],
  modes: {
    raw: "第一段行一\n第一段行二",
    line: "第一段行一\n第一段行二",
    merged: "第一段行一第一段行二",
    smart: "第一段行一\n\n第一段行二",
  },
};

function setup(withResultImage: boolean) {
  // jsdom 无真实焦点：文档级命令前置需要 hasFocus() 为真。
  vi.spyOn(document, "hasFocus").mockReturnValue(true);
  const run = vi.fn().mockResolvedValue(true);
  const actions: AppActions = {
    run,
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
  const viewState: AppViewState = {
    connected: true,
    revision: 1,
    route: "recognition",
    theme: "light",
    capabilities: [
      "recognition.file",
      "recognition.clipboard",
      "recognition.results",
    ],
    runtimeLabel: "host",
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "https://resource.vibeocr/input.png",
          mediaType: "image/png",
          byteLength: 4,
        },
        result: {
          url: "https://resource.vibeocr/result.txt",
          mediaType: "text/plain",
          byteLength: 4,
        },
        structuredResult: {
          url: "https://resource.vibeocr/structured.json",
          mediaType: "application/json",
          byteLength: 4,
        },
        ...(withResultImage
          ? {
              resultImage: {
                url: "https://resource.vibeocr/result-image.png",
                mediaType: "image/png",
                byteLength: 4,
              },
            }
          : {}),
      },
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("structured"))
        return new Response(JSON.stringify([structuredPayload]), {
          status: 200,
        });
      return new Response("第一段行一\n第一段行二", { status: 200 });
    }),
  );
  return { run, viewState, actions };
}

describe("recognition page result actions", () => {
  it("shows the asynchronous retired-mode terminal with a reselect instruction", () => {
    const { viewState, actions, run } = setup(false);
    render(
      <RecognitionPage
        actions={actions}
        viewState={{
          ...viewState,
          features: {
            recognition: {
              isBusy: false,
              statusCode: "recognition.modeRetired",
              taskEngine: "paddle_formula",
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(
        "所选识别模式已退役，请在设置或任务面板重新选择可用模式。",
      ),
    ).toBeVisible();
    expect(run).not.toHaveBeenCalled();
  });

  it.each(["选图编辑", "粘贴编辑"])(
    "%s succeeds into expanded editor; cancellation preserves preview",
    async (label) => {
      const { run, viewState, actions } = setup(false);
      const { rerender } = render(
        <RecognitionPage viewState={viewState} actions={actions} />,
      );
      const update = (recognition: Readonly<Record<string, unknown>>) =>
        rerender(
          <RecognitionPage
            viewState={{ ...viewState, features: { recognition } }}
            actions={actions}
          />,
        );
      const initial = feature(viewState, "recognition");
      // 编辑导入收纳在图片面板的“编辑导入”菜单：先展开菜单再触发命令；
      // 预览折叠后面板隐藏，导入入口随面板一起收起（展开按钮回到顶栏）。
      const importForEdit = () => {
        fireEvent.click(screen.getByRole("button", { name: "编辑导入" }));
        fireEvent.click(screen.getByRole("menuitem", { name: label }));
      };
      importForEdit();
      await waitFor(() => expect(run).toHaveBeenCalledTimes(1));
      update({ ...initial, isBusy: true, statusCode: "recognition.running" });
      fireEvent.click(screen.getByRole("button", { name: "收起图片预览" }));
      expect(
        screen.getByRole("button", { name: "展开图片预览" }),
      ).toBeInTheDocument();
      update({ ...initial, isBusy: false });
      expect(
        screen.getByRole("button", { name: "展开图片预览" }),
      ).toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "展开图片预览" }));
      importForEdit();
      await waitFor(() => expect(run).toHaveBeenCalledTimes(2));
      update({ ...initial, isBusy: true, statusCode: "recognition.running" });
      update({
        ...initial,
        isBusy: false,
        statusCode: "recognition.session",
        screenshotSession: {
          sessionId: "0123456789abcdef0123456789abcdef",
          revision: 0,
        },
      });
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "收起图片预览" }),
        ).toBeInTheDocument(),
      );
      expect(screen.getByRole("button", { name: "编辑图片" })).toHaveAttribute(
        "aria-pressed",
        "true",
      );
      expect(localStorage.getItem("recognition.previewCollapsed")).toBe(
        "false",
      );
    },
  );

  it("uses the frozen result image for proofreading, never the session input", async () => {
    const { viewState, actions } = setup(true);
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    const image = await screen.findByRole("img", {
      name: "识别结果参考图",
    });
    expect(image).toHaveAttribute(
      "src",
      "https://resource.vibeocr/result-image.png",
    );
  });

  it("默认 raw 结果与 bbox 双向悬停高亮，新结果不残留旧 hover", async () => {
    const { viewState, actions } = setup(true);
    const { rerender } = render(
      <RecognitionPage viewState={viewState} actions={actions} />,
    );
    // 默认 raw 模式：原文整段保留，可映射行是可悬停 span。
    const rawSpan = await screen.findByText("第一段行二");
    // jsdom 不解码图片：注入自然尺寸并触发 load，行框才会投影。
    const image = screen.getByRole("img", { name: "识别结果参考图" });
    Object.defineProperties(image, {
      naturalWidth: { value: 800, configurable: true },
      naturalHeight: { value: 400, configurable: true },
    });
    fireEvent.load(image);
    await fireEvent.mouseEnter(rawSpan);
    expect(rawSpan).toHaveClass("line-hovered");
    // 图上对应 bbox 行框同步加亮（同一宿主行下标 1）。
    const overlayLine = screen.getByLabelText("第一段行二");
    expect(overlayLine).toHaveClass("hovered");
    await fireEvent.mouseLeave(rawSpan);
    expect(rawSpan).not.toHaveClass("line-hovered");
    expect(overlayLine).not.toHaveClass("hovered");
    // 图上悬停 → 右侧 raw 原文对应片段同步加亮。
    await fireEvent.mouseEnter(overlayLine);
    expect(rawSpan).toHaveClass("line-hovered");
    // 新结果（structured 身份变化）：悬停状态清空，不残留旧高亮。
    const nextViewState: AppViewState = {
      ...viewState,
      features: {
        ...viewState.features,
        recognition: {
          ...(viewState.features.recognition as Record<string, unknown>),
          structuredResult: {
            url: "https://resource.vibeocr/structured-2.json",
            mediaType: "application/json",
            byteLength: 4,
          },
        },
      },
    };
    rerender(<RecognitionPage viewState={nextViewState} actions={actions} />);
    const nextSpan = await screen.findByText("第一段行二");
    expect(nextSpan).not.toHaveClass("line-hovered");
    // 校对层随绑定键重挂：新图重新触发 load 后行框无残留高亮。
    const nextImage = screen.getByRole("img", { name: "识别结果参考图" });
    Object.defineProperties(nextImage, {
      naturalWidth: { value: 800, configurable: true },
      naturalHeight: { value: 400, configurable: true },
    });
    fireEvent.load(nextImage);
    expect(screen.getByLabelText("第一段行二")).not.toHaveClass("hovered");
  });

  it("hides the bbox proofreading view when resultImage is absent", async () => {
    const { viewState, actions } = setup(false);
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    await screen.findByText(/共 2 行/);
    expect(
      screen.queryByRole("img", { name: "识别结果参考图" }),
    ).not.toBeInTheDocument();
  });

  it("copies through the current layout mode with Ctrl+C when nothing is selected", async () => {
    const { viewState, actions } = setup(true);
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    await screen.findByText(/共 2 行/);
    await fireEvent.change(screen.getByRole("combobox", { name: "排版模式" }), {
      target: { value: "merged" },
    });
    await fireEvent.keyDown(window, { key: "c", ctrlKey: true });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.copyLines",
      resourceUri: "https://resource.vibeocr/structured.json",
      mode: "merged",
    });
  });

  it("does not hijack Ctrl+C when the user has a text selection", async () => {
    const { viewState, actions } = setup(true);
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    const target = await screen.findByText(/共 2 行/);
    const range = document.createRange();
    range.selectNode(target);
    const selection = document.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
    await fireEvent.keyDown(window, { key: "c", ctrlKey: true });
    expect(actions.run).not.toHaveBeenCalled();
    selection.removeAllRanges();
  });

  it("左侧单一图片区承载校对：绑定冻结结果图、无第二张图、右侧不重复渲染", async () => {
    const { viewState, actions } = setup(true);
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    const inputPanel = screen.getByRole("region", { name: "输入图像" });
    // 校对层默认展示在左侧输入面板，图源是冻结结果图而非会话基准图。
    const image = await within(inputPanel).findByRole("img", {
      name: "识别结果参考图",
    });
    expect(image).toHaveAttribute(
      "src",
      "https://resource.vibeocr/result-image.png",
    );
    expect(screen.getAllByRole("img", { name: "识别结果参考图" })).toHaveLength(
      1,
    );
    const resultPanel = screen.getByRole("region", {
      name: /^识别结果$/,
    });
    expect(
      within(resultPanel).queryByRole("img", { name: "识别结果参考图" }),
    ).toBeNull();
  });

  it("模式切换不丢编辑状态；校对模式不消费文档级 Ctrl+S", async () => {
    const { viewState, actions } = setup(true);
    const viewStateWithAnnotation = {
      ...viewState,
      capabilities: [...viewState.capabilities, "recognition.annotation"],
    };
    render(
      <RecognitionPage viewState={viewStateWithAnnotation} actions={actions} />,
    );
    await screen.findByRole("img", { name: "识别结果参考图" });
    const ctrlS = () => {
      const event = new KeyboardEvent("keydown", {
        key: "s",
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };
    // 校对模式：编辑器仅隐藏，文档级 Ctrl+S 挂起（不 preventDefault）。
    expect(ctrlS()).toBe(false);
    // 本地编辑手势同步挂起：Space 不进入平移（滚动默认行为保留）。
    const viewport = screen.getByLabelText("图片视口");
    fireEvent.keyDown(viewport, { code: "Space", key: " " });
    expect(viewport).not.toHaveClass("is-panning");
    fireEvent.keyUp(viewport, { code: "Space", key: " " });
    // 切到编辑：校对层卸载，工具栏可达，旋转产生可撤销历史。
    fireEvent.click(screen.getByRole("button", { name: "编辑图片" }));
    expect(screen.queryByRole("img", { name: "识别结果参考图" })).toBeNull();
    const toolbar = () =>
      within(screen.getByRole("toolbar", { name: "图片编辑工具" }));
    fireEvent.click(toolbar().getByRole("button", { name: "旋转 90°" }));
    expect(toolbar().getByRole("button", { name: "撤销" })).toBeEnabled();
    // 编辑模式 Ctrl+S 被编辑器接管。
    expect(ctrlS()).toBe(true);
    // 编辑模式本地手势恢复：Space 进入平移，抬起退出。
    fireEvent.keyDown(viewport, { code: "Space", key: " " });
    expect(viewport).toHaveClass("is-panning");
    fireEvent.keyUp(viewport, { code: "Space", key: " " });
    expect(viewport).not.toHaveClass("is-panning");
    // 往返校对/编辑：编辑器不卸载，历史保留。
    fireEvent.click(screen.getByRole("button", { name: "图像校对" }));
    expect(
      screen.getByRole("img", { name: "识别结果参考图" }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "编辑图片" }));
    expect(toolbar().getByRole("button", { name: "撤销" })).toBeEnabled();
  });

  it.each(["resultImage", "structuredResult"] as const)(
    "新结果的 %s 变化恢复校对，同一结果更新保留编辑模式",
    async (changedResource) => {
      const { viewState, actions } = setup(true);
      const { rerender } = render(
        <RecognitionPage viewState={viewState} actions={actions} />,
      );
      await screen.findByRole("img", { name: "识别结果参考图" });
      fireEvent.click(screen.getByRole("button", { name: "编辑图片" }));
      rerender(
        <RecognitionPage
          viewState={{ ...viewState, revision: 2 }}
          actions={actions}
        />,
      );
      expect(screen.getByRole("button", { name: "编辑图片" })).toHaveAttribute(
        "aria-pressed",
        "true",
      );
      const url =
        changedResource === "resultImage"
          ? "https://resource.vibeocr/next-result-image.png"
          : "https://resource.vibeocr/next-structured.json";
      rerender(
        <RecognitionPage
          viewState={{
            ...viewState,
            revision: 3,
            features: {
              ...viewState.features,
              recognition: {
                ...feature(viewState, "recognition"),
                [changedResource]: {
                  url,
                  mediaType:
                    changedResource === "resultImage"
                      ? "image/png"
                      : "application/json",
                  byteLength: 4,
                },
              },
            },
          }}
          actions={actions}
        />,
      );
      const image = await screen.findByRole("img", {
        name: "识别结果参考图",
      });
      expect(image).toHaveAttribute(
        "src",
        changedResource === "resultImage"
          ? url
          : "https://resource.vibeocr/result-image.png",
      );
      expect(screen.getByRole("button", { name: "图像校对" })).toHaveAttribute(
        "aria-pressed",
        "true",
      );
    },
  );

  it("falls back to plain copy for non-lines results", async () => {
    const { viewState, actions } = setup(false);
    // 结构化资源不可读（fetch 抛错）→ 无 lines：Ctrl+C 走 plain。
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new Error("offline");
      }),
    );
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    await waitFor(() =>
      expect(
        screen.queryByRole("combobox", { name: "排版模式" }),
      ).not.toBeInTheDocument(),
    );
    await fireEvent.keyDown(window, { key: "c", ctrlKey: true });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.copy",
      format: "plain",
    });
  });

  it("keeps empty mode strings as legitimate values", async () => {
    const { viewState, actions } = setup(true);
    // 契约：所有 string 都是合法值（raw/merged 可为空串）；仅缺失才禁用。
    const payload = {
      ...structuredPayload,
      modes: {
        raw: "第一段行一\n第一段行二",
        line: "第一段行一\n第一段行二",
        merged: "",
        smart: undefined,
      },
    };
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.endsWith("structured.json"))
          return new Response(JSON.stringify([payload]), { status: 200 });
        return new Response("第一段行一\n第一段行二", { status: 200 });
      }),
    );
    render(<RecognitionPage viewState={viewState} actions={actions} />);
    await screen.findByText(/共 2 行/);
    expect(
      (screen.getByRole("option", { name: "一段" }) as HTMLOptionElement)
        .disabled,
    ).toBe(false);
    expect(
      (screen.getByRole("option", { name: "智能分段" }) as HTMLOptionElement)
        .disabled,
    ).toBe(true);
    // 选空串模式：原样展示空文本，复制提交该模式（宿主决定语义）。
    await fireEvent.change(screen.getByRole("combobox", { name: "排版模式" }), {
      target: { value: "merged" },
    });
    await fireEvent.keyDown(window, { key: "c", ctrlKey: true });
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.copyLines",
      resourceUri: "https://resource.vibeocr/structured.json",
      mode: "merged",
    });
  });
});
