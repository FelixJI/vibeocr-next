import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { RecognitionPage } from "./RecognitionPage";

afterEach(() => {
  cleanup();
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
      if (url.endsWith("structured.json"))
        return new Response(JSON.stringify([structuredPayload]), {
          status: 200,
        });
      return new Response("第一段行一\n第一段行二", { status: 200 });
    }),
  );
  return { run, viewState, actions };
}

describe("recognition page result actions", () => {
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
