import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions, AppViewState } from "../app/types";
import { ImageEditPage, RecognitionPage } from "./Pages";

afterEach(() => {
  window.location.hash = "";
  vi.restoreAllMocks();
});

function actions(): AppActions & { run: ReturnType<typeof vi.fn> } {
  return {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
}

function viewState(
  features: Record<string, unknown>,
  capabilities: readonly string[],
): AppViewState {
  return {
    connected: true,
    revision: 1,
    route: "imageEdit",
    theme: "light",
    capabilities,
    features,
    runtimeLabel: "原生宿主已连接",
  };
}

const recognitionWithResult = {
  input: {
    url: "https://app.vibeocr/__resource/capture.png",
    mediaType: "image/png",
    byteLength: 2048,
  },
  result: {
    url: "https://app.vibeocr/__resource/result.txt",
    mediaType: "text/plain",
    byteLength: 32,
  },
  structuredResult: {
    url: "https://app.vibeocr/__resource/result.json",
    mediaType: "application/json",
    byteLength: 64,
  },
  screenshotSession: {
    sessionId: "session-a",
    revision: 0,
    textSelectionRequested: false,
    sceneEditing: false,
  },
};

describe("ImageEditPage result isolation", () => {
  it("never projects a shared recognition result into the pure edit page", () => {
    // 旧实现读取共享 recognition 的 result 并渲染“识别结果”面板；
    // 先识别再进入纯编辑时会把旧文字投影到编辑界面（AC1 回归）。
    window.location.hash = "#/imageEdit";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const { unmount } = render(
      <ImageEditPage
        actions={actions()}
        viewState={viewState({ recognition: recognitionWithResult }, [
          "recognition.annotation",
          "recognition.screenshotSession",
        ])}
      />,
    );
    expect(
      screen.getByRole("heading", { name: "图片编辑" }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("图片检查画布")).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "识别结果" })).toBeNull();
    expect(screen.queryByText("正在读取结果…")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
    unmount();
    vi.unstubAllGlobals();
  });

  it("keeps the single recognition page as the result host", () => {
    // 对照：识别承载面仍渲染结果面板（由 RecognitionPage 负责）。
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("识别文本")));
    const { unmount } = render(
      <RecognitionPage
        actions={actions()}
        viewState={viewState({ recognition: recognitionWithResult }, [
          "recognition.annotation",
          "recognition.results",
        ])}
      />,
    );
    expect(
      screen.getByRole("region", { name: "识别结果" }),
    ).toBeInTheDocument();
    unmount();
    vi.unstubAllGlobals();
  });
});
