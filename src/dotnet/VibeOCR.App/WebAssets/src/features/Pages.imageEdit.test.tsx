import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions, AppViewState } from "../app/types";
import { ImageEditPage, RecognitionPage } from "./Pages";

afterEach(() => {
  cleanup();
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

// 宿主分离后的独立编辑状态：ImageEditPage 只读 imageEdit feature。
const imageEditWithInput = {
  input: {
    url: "https://app.vibeocr/__resource/edit-capture.png",
    mediaType: "image/png",
    byteLength: 1024,
  },
  screenshotSession: {
    sessionId: "edit-a",
    revision: 0,
    textSelectionRequested: false,
    sceneEditing: false,
  },
};

const editorCapabilities = [
  "recognition.annotation",
  "recognition.screenshotSession",
  "recognition.file",
  "recognition.clipboard",
];

describe("ImageEditPage result isolation", () => {
  it("renders the edit canvas from the imageEdit feature without projecting a shared recognition result", () => {
    // 独立状态接线：编辑页输入来自 imageEdit feature；共享 recognition
    // 的旧结果绝不投影到纯编辑页（AC1 回归 + 状态分离契约）。
    window.location.hash = "#/imageEdit";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const { unmount } = render(
      <ImageEditPage
        actions={actions()}
        viewState={viewState(
          { recognition: recognitionWithResult, imageEdit: imageEditWithInput },
          editorCapabilities,
        )}
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

describe("ImageEditPage keeps the full tool palette", () => {
  it("still offers the complete annotation tool set", () => {
    window.location.hash = "#/imageEdit";
    const { unmount } = render(
      <ImageEditPage
        actions={actions()}
        viewState={viewState(
          { imageEdit: imageEditWithInput },
          editorCapabilities,
        )}
      />,
    );
    // 完整图片编辑页面保留原工具：绘制/遮盖/裁剪等全部可用。
    for (const tool of [
      "选择",
      "手形",
      "矩形",
      "椭圆",
      "箭头",
      "文字",
      "马赛克",
      "模糊",
      "画笔",
      "荧光笔",
      "序号",
      "屏蔽",
      "裁剪",
      "去水印",
    ]) {
      expect(
        screen.getByRole("button", { name: tool }),
        `工具 ${tool} 应保留在完整编辑页`,
      ).toBeInTheDocument();
    }
    unmount();
  });
});

describe("editor command scope wiring", () => {
  it("sends imageEdit-scoped editor commands from the dedicated edit page", async () => {
    // 宿主约定：非 scene 图片编辑页读 imageEdit feature，编辑器会话命令
    // 全部改发 imageEdit.*（与 recognition 同形命令）。
    window.location.hash = "#/imageEdit";
    const run = vi.fn().mockResolvedValue(true);
    const { unmount } = render(
      <ImageEditPage
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
        viewState={viewState(
          { imageEdit: imageEditWithInput },
          editorCapabilities,
        )}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
    await waitFor(() =>
      expect(run).toHaveBeenCalledWith({
        type: "imageEdit.notifyScreenshotRevision",
        sessionId: "edit-a",
        revision: 1,
      }),
    );
    unmount();
  });

  it("keeps recognition-scoped state and commands for the scene editor", async () => {
    // scene（#/imageEdit?scene=1）仍由 recognition feature 承载并发
    // recognition.* 命令（宿主命令约定）。
    window.location.hash = "#/imageEdit?scene=1";
    const run = vi.fn().mockResolvedValue(true);
    const { unmount } = render(
      <ImageEditPage
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
        viewState={viewState(
          {
            recognition: {
              ...recognitionWithResult,
              screenshotSession: {
                sessionId: "session-a",
                revision: 0,
                textSelectionRequested: false,
                sceneEditing: true,
              },
            },
          },
          editorCapabilities,
        )}
      />,
    );
    expect(screen.getByLabelText("图片检查画布")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "旋转 90°" }));
    await waitFor(() =>
      expect(run).toHaveBeenCalledWith({
        type: "recognition.notifyScreenshotRevision",
        sessionId: "session-a",
        revision: 1,
      }),
    );
    unmount();
  });
});

describe("RecognitionPage lean editor", () => {
  it("keeps only the inpaint, rotate and exclude processing tools", () => {
    // 用户项2：单次识别只保留去水印、旋转、屏蔽三类图片处理工具；
    // 其余绘制/遮盖/裁剪工具不进入识别面（选择/取字是必要的交互与
    // 结果复制入口，不是图片处理工具）。
    const { unmount } = render(
      <RecognitionPage
        actions={actions()}
        viewState={viewState({ recognition: recognitionWithResult }, [
          ...editorCapabilities,
          "recognition.results",
        ])}
      />,
    );
    expect(screen.getByRole("button", { name: "去水印" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "屏蔽" })).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "旋转 90°" }),
    ).toBeInTheDocument();
    for (const hidden of [
      "手形",
      "矩形",
      "椭圆",
      "箭头",
      "文字",
      "马赛克",
      "模糊",
      "画笔",
      "荧光笔",
      "序号",
      "裁剪",
    ]) {
      expect(
        screen.queryByRole("button", { name: hidden }),
        `工具 ${hidden} 不应出现在单次识别面`,
      ).toBeNull();
    }
    // 输出变换控件（格式/等比输出缩放/尺寸/大小检查）不进入识别面；
    // 识别/复制/保存默认原始 PNG。
    expect(screen.queryByRole("combobox", { name: "输出格式" })).toBeNull();
    expect(screen.queryByRole("combobox", { name: "等比缩放" })).toBeNull();
    expect(screen.queryByLabelText("输出宽度")).toBeNull();
    expect(screen.queryByRole("button", { name: "检查文件大小" })).toBeNull();
    // 必要的识别面功能保留：显示缩放（不改像素）与显式识别入口。
    expect(screen.getByLabelText("显示缩放")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "识别当前图" }),
    ).toBeInTheDocument();
    unmount();
  });

  it("dispatches recognition-scoped edit entry commands", () => {
    // 移除单次识别对 imageEdit.selectImage/readClipboard 的耦合：
    // 编辑入口改发 recognition scope 专用命令（宿主命令约定分离）。
    const run = vi.fn().mockResolvedValue(true);
    const { unmount } = render(
      <RecognitionPage
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
        viewState={viewState({ recognition: recognitionWithResult }, [
          ...editorCapabilities,
          "recognition.results",
        ])}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "选图编辑" }));
    expect(run).toHaveBeenCalledWith({
      type: "recognition.openImageForEdit",
    });
    fireEvent.click(screen.getByRole("button", { name: "粘贴编辑" }));
    expect(run).toHaveBeenCalledWith({
      type: "recognition.pasteImageForEdit",
    });
    // 不再向 imageEdit scope 发送旧命令。
    expect(
      run.mock.calls.filter(
        (call) =>
          typeof (call[0] as { type?: string }).type === "string" &&
          (call[0] as { type: string }).type.startsWith("imageEdit."),
      ),
    ).toHaveLength(0);
    unmount();
  });
});
