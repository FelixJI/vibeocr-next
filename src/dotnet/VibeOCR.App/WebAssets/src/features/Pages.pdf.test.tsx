import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { PdfPage } from "./Pages";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
function setup(patch: Record<string, unknown> = {}) {
  const actions: AppActions & { run: ReturnType<typeof vi.fn> } = {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
  const state: AppViewState = {
    connected: true,
    revision: 1,
    route: "pdf",
    theme: "light",
    capabilities: ["pdf.edit", "pdf.save", "pdf.rotate", "pdf.open"],
    runtimeLabel: "host",
    features: {
      pdf: {
        sessionId: "doc-1",
        pageCount: 70,
        windowStart: 64,
        selectedPage: 69,
        selectedPages: [69],
        revision: 8,
        isModified: true,
        detectedCount: 70,
        textLayerCount: 3,
        canAddTextLayer: true,
        canInspectPage: true,
        canCorrectText: true,
        pages: [
          {
            index: 69,
            statusCode: "pdf.page.done",
            detected: true,
            hasTextLayer: true,
            addedThisSession: false,
          },
        ],
        ...patch,
      },
    },
  };
  const view = render(<PdfPage viewState={state} actions={actions} />);
  return Object.assign(actions, {
    updatePdf: (patch: Record<string, unknown>) =>
      view.rerender(
        <PdfPage
          viewState={{
            ...state,
            features: {
              ...state.features,
              pdf: {
                ...(state.features.pdf as Record<string, unknown>),
                ...patch,
              },
            },
          }}
          actions={actions}
        />,
      ),
  });
}
describe("PDF text layer actions", () => {
  it("selects all 129 pages beyond the visible window and clears without mutation", () => {
    const actions = setup({ pageCount: 129 });
    fireEvent.click(screen.getByRole("button", { name: "全选页面" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.selectAll",
      selected: true,
    });
    fireEvent.click(screen.getByRole("button", { name: "取消选择" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.selectAll",
      selected: false,
    });
  });
  it("uses explicit ranges for both directions and prevents empty-selection book rotation", () => {
    const actions = setup({ selectedPages: [] });
    expect(screen.getByRole("button", { name: "逆时针 90°" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("页面处理范围"), {
      target: { value: "all" },
    });
    fireEvent.click(screen.getByRole("button", { name: "逆时针 90°" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.rotate",
      degrees: -90,
      range: "all",
    });
    fireEvent.click(screen.getByRole("button", { name: "横放" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.orient",
      landscape: true,
      range: "all",
    });
    fireEvent.click(screen.getByRole("button", { name: "自动文字朝向" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.correctOrientation",
      range: "all",
    });
  });
  it("sends an authorized picker command and explicit blank size and revision", () => {
    const actions = setup();
    fireEvent.change(screen.getByLabelText("插入到第几页后（0 为开头）"), {
      target: { value: "70" },
    });
    fireEvent.change(screen.getByLabelText("空白页宽度 pt"), {
      target: { value: "640" },
    });
    fireEvent.click(screen.getByRole("button", { name: "插入空白页" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.insertBlank",
      afterIndex: 69,
      width: 640,
      height: 792,
      revision: 8,
    });
    fireEvent.click(screen.getByRole("button", { name: "插入其他 PDF" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.insertFrom",
      afterIndex: 69,
      revision: 8,
    });
    fireEvent.click(screen.getByRole("button", { name: "第 70 页向前移动" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.movePage",
      fromIndex: 69,
      toIndex: 68,
      revision: 8,
    });
  });
  it("separates extraction from whole-book add and defaults to skip", () => {
    const actions = setup();
    fireEvent.click(screen.getByRole("button", { name: "提取/解析选中页" }));
    expect(actions.run).toHaveBeenLastCalledWith({ type: "pdf.ocrPages" });
    fireEvent.click(screen.getByRole("button", { name: "添加整本文字层" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.addTextLayers",
      range: "all",
      overwrite: false,
    });
    fireEvent.click(
      screen.getByRole("checkbox", { name: "显式覆盖已有文字层（默认跳过）" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "添加选中页文字层" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.addTextLayers",
      range: "selected",
      overwrite: true,
    });
  });
  it("freezes confirmed deletion pages and revision and explains visible text", () => {
    const actions = setup();
    fireEvent.click(screen.getByRole("button", { name: "删除选中文字层" }));
    expect(
      actions.run.mock.calls.every(
        ([command]) => command.type === "pdf.setPreviewPosition",
      ),
    ).toBe(true);
    expect(screen.getByRole("alertdialog")).toHaveTextContent("原有可见文字");
    expect(screen.getByRole("alertdialog")).toHaveTextContent(
      "不是安全脱敏工具",
    );
    fireEvent.click(screen.getByRole("button", { name: "确认删除文字层" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.deleteTextLayers",
      pages: [69],
      revision: 8,
      confirmed: true,
    });
  });
  it("retains extraction when geometry is unavailable", () => {
    setup({ canAddTextLayer: false });
    expect(
      screen.getByRole("button", { name: "添加整本文字层" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "提取/解析选中页" }),
    ).toBeEnabled();
    expect(screen.getByText("已有文字层 / 来源未知")).toBeInTheDocument();
  });
  it("keeps conflicting actions disabled during actual settling", () => {
    const actions = setup({
      isBusy: true,
      phase: "write",
      progressCurrent: 1,
      progressTotal: 4,
    });
    expect(screen.getByRole("button", { name: "保存" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "关闭文档" })).toBeEnabled();
    expect(
      screen.getByRole("button", { name: "添加整本文字层" }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "取消 PDF 操作" }));
    expect(actions.run).toHaveBeenLastCalledWith({ type: "pdf.cancel" });
    expect(screen.getByText(/后台实际收尾后才可继续操作/)).toBeInTheDocument();
  });
});

it("falls back to the old thumbnail without the page inspection capability", () => {
  setup({
    canInspectPage: false,
    canCorrectText: false,
    pages: [
      {
        index: 69,
        statusCode: "pdf.page.done",
        thumbnail: { url: "/old-thumbnail.png", contentType: "image/png" },
      },
    ],
  });
  expect(screen.getByAltText("第 70 页预览")).toHaveAttribute(
    "src",
    "/old-thumbnail.png",
  );
  expect(
    screen.queryByRole("button", { name: "适应页面" }),
  ).not.toBeInTheDocument();
});

it("labels retained structured results and copy as the original recognition revision", () => {
  setup({
    pages: [
      {
        index: 69,
        statusCode: "pdf.page.done",
        recognitionRevision: 7,
        correctedAfterRecognition: true,
        structuredResult: {
          url: "/original-ocr.json",
          mediaType: "application/json",
          byteLength: 128,
        },
      },
    ],
  });
  expect(
    screen.getByText(
      /原始识别结果（修订 7） · 校正前，复制内容保留原始识别文本/,
    ),
  ).toBeInTheDocument();
});

it.each([
  {
    label: "取消选择",
    command: { type: "pdf.selectAll", selected: false },
    patch: { selectedPage: -1, selectedPages: [] },
  },
  {
    label: "关闭文档",
    command: { type: "pdf.close" },
    patch: {
      sessionId: undefined,
      pageCount: 0,
      selectedPage: -1,
      selectedPages: [],
      pages: [],
    },
  },
])(
  "shows the page-selection placeholder after $label",
  async ({ label, command, patch }) => {
    const actions = setup();
    expect(
      screen.getByRole("button", { name: "适应页面" }),
    ).toBeInTheDocument();
    expect(screen.getByText("正在读取当前页高清预览…")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: label }));
    // 关闭前先等待预览状态排空完成，命令异步发出。
    await waitFor(() => expect(actions.run).toHaveBeenLastCalledWith(command));
    actions.updatePdf(patch);
    expect(screen.getByText("选择页面后查看预览。")).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "适应页面" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText("正在读取当前页高清预览…"),
    ).not.toBeInTheDocument();
    expect(screen.queryByText(/第 0 \/ /)).not.toBeInTheDocument();
  },
);

it.each([{ sessionId: undefined }, { selectedPage: 70 }])(
  "does not mount inspection without a valid session and current page: %j",
  (patch) => {
    setup(patch);
    expect(screen.getByText("选择页面后查看预览。")).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "适应页面" }),
    ).not.toBeInTheDocument();
  },
);

describe("PDF close retry entry", () => {
  it("keeps close available for a retained close-failed document and binds its identity", () => {
    const actions = setup({
      sessionId: undefined,
      pageCount: 0,
      selectedPage: -1,
      selectedPages: [],
      pages: [],
      documentId: "33333333333333333333333333333333",
      documents: [
        {
          documentId: "33333333333333333333333333333333",
          name: "late.pdf",
          pageCount: 0,
          isModified: false,
          isBusy: false,
          closeFailed: true,
        },
      ],
    });
    const close = screen.getByRole("button", { name: "关闭文档" });
    expect(close).toBeEnabled();
    expect(screen.getByRole("button", { name: /late\.pdf/ })).toHaveTextContent(
      "关闭失败，可重试",
    );
    fireEvent.click(close);
    expect(actions.run).toHaveBeenCalledWith({
      type: "pdf.close",
      documentId: "33333333333333333333333333333333",
      documentRevision: 8,
    });
  });

  it("keeps close disabled for an actually empty workspace without failed documents", () => {
    setup({
      sessionId: undefined,
      pageCount: 0,
      selectedPage: -1,
      selectedPages: [],
      pages: [],
      documents: [],
    });
    expect(screen.getByRole("button", { name: "关闭文档" })).toBeDisabled();
  });
});

describe("PDF document switching", () => {
  it("does not send the switch command while the last position commit is unconfirmed", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({
          page: 69,
          ocr_blocks: [
            {
              index: 2,
              text: "原文 211",
              score: 0,
              score_unknown: true,
              bbox: [100, 200, 900, 300],
            },
          ],
        }),
      }),
    );
    const a = "11111111111111111111111111111111";
    const b = "22222222222222222222222222222222";
    const actions = setup({
      documentId: a,
      pagePreview: {
        url: "/hd-a.svg",
        mediaType: "image/png",
        byteLength: 100,
      },
      pageInspect: {
        url: "/inspect-a.json",
        mediaType: "application/json",
        byteLength: 200,
      },
      documents: [
        { documentId: a, name: "a.pdf", isModified: true },
        { documentId: b, name: "b.pdf", isModified: false },
      ],
    });
    // 宿主未确认任何位置提交：切换命令不得发出。
    actions.run.mockResolvedValue(false);
    fireEvent.click(await screen.findByRole("button", { name: /原文 211/ }));
    await waitFor(() => expect(actions.run).toHaveBeenCalled());
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "未确认草稿" },
    });
    fireEvent.click(screen.getByRole("button", { name: "b.pdf" }));
    await act(async () => {
      await Promise.resolve();
    });
    expect(
      actions.run.mock.calls.some(
        ([command]) =>
          (command as Record<string, unknown>).type === "pdf.activateDocument",
      ),
    ).toBe(false);
  });

  it("commits the last draft before an immediate A→B→A switch and restores it", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({
          page: 69,
          ocr_blocks: [
            {
              index: 2,
              text: "原文 211",
              score: 0,
              score_unknown: true,
              bbox: [100, 200, 900, 300],
            },
          ],
        }),
      }),
    );
    const a = "11111111111111111111111111111111";
    const b = "22222222222222222222222222222222";
    const actions = setup({
      documentId: a,
      pagePreview: {
        url: "/hd-a.svg",
        mediaType: "image/png",
        byteLength: 100,
      },
      pageInspect: {
        url: "/inspect-a.json",
        mediaType: "application/json",
        byteLength: 200,
      },
      documents: [
        { documentId: a, name: "a.pdf", isModified: true },
        { documentId: b, name: "b.pdf", isModified: false },
      ],
    });
    // 首条草稿相关位置更新在途中：切换命令必须等待排空完成才发出。
    let resolveSlow!: (ok: boolean) => void;
    actions.run.mockImplementationOnce(
      () =>
        new Promise<boolean>((resolve) => {
          resolveSlow = resolve;
        }),
    );
    fireEvent.click(await screen.findByRole("button", { name: /原文 211/ }));
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "最终草稿211" },
    });
    fireEvent.click(screen.getByRole("button", { name: "b.pdf" }));
    // 在途命令未完成：激活命令尚未发出。
    await act(async () => {
      await Promise.resolve();
    });
    expect(
      actions.run.mock.calls.some(
        ([command]) =>
          (command as Record<string, unknown>).type === "pdf.activateDocument",
      ),
    ).toBe(false);
    resolveSlow(true);
    await waitFor(() =>
      expect(
        actions.run.mock.calls.some(
          ([command]) =>
            (command as Record<string, unknown>).type ===
            "pdf.activateDocument",
        ),
      ).toBe(true),
    );
    const calls = actions.run.mock.calls.map(
      ([command]) => command as Record<string, unknown>,
    );
    const activateIndex = calls.findIndex(
      (command) => command.type === "pdf.activateDocument",
    );
    const flushedIndex = calls.findIndex(
      (command, index) =>
        index < activateIndex &&
        command.type === "pdf.setPreviewPosition" &&
        (command.position as { draft?: string }).draft === "最终草稿211",
    );
    // 激活命令发出前，最新草稿已在排空链中完成提交，且是激活前最后一条命令。
    expect(flushedIndex).toBeGreaterThan(-1);
    expect(
      calls.findIndex(
        (command, index) => index > flushedIndex && index < activateIndex,
      ),
    ).toBe(-1);
    // 宿主按提交顺序回读：立即切回 A 时返回已提交的最新草稿。
    actions.updatePdf({
      documentId: b,
      sessionId: "worker-b",
      revision: 3,
      pageCount: 2,
      selectedPage: -1,
      selectedPages: [],
      pages: [],
      canInspectPage: false,
      pagePreview: undefined,
      pageInspect: undefined,
    });
    expect(screen.queryByLabelText("校正文字")).not.toBeInTheDocument();
    actions.updatePdf({
      documentId: a,
      previewPosition: {
        revision: 8,
        page: 69,
        block: 2,
        draft: "最终草稿211",
        zoom: 1.5,
      },
    });
    expect(await screen.findByLabelText("校正文字")).toHaveValue("最终草稿211");
  });
});

describe("PDF copy export status", () => {
  const exportDocuments = [
    {
      documentId: "11111111111111111111111111111111",
      name: "a.pdf",
      revision: 8,
      status: "saved",
      output: "a.pdf",
    },
    {
      documentId: "22222222222222222222222222222222",
      name: "b.pdf",
      revision: 3,
      status: "unconfirmed",
      output: "b.pdf",
      error: "结果未确认，请检查输出 b.pdf；未自动重试。",
    },
  ];

  it("shows unconfirmed as its own status with the retained output and explicit guidance", () => {
    setup({
      canCopyExport: true,
      documents: [
        {
          documentId: "11111111111111111111111111111111",
          name: "a.pdf",
          isModified: true,
        },
        {
          documentId: "22222222222222222222222222222222",
          name: "b.pdf",
          isModified: true,
        },
      ],
      exportItems: exportDocuments,
    });
    fireEvent.click(screen.getByText("批量导出副本"));
    const item = screen
      .getAllByText(/b\.pdf/)
      .find((element) => element.tagName === "LI");
    expect(item).toBeDefined();
    expect(item).toHaveTextContent("结果未确认");
    expect(item).toHaveTextContent("未自动重试");
  });

  it("keeps retry aligned with the host plan: saved and unconfirmed are not retryable", () => {
    const actions = setup({
      canCopyExport: true,
      documents: [
        {
          documentId: "11111111111111111111111111111111",
          name: "a.pdf",
          isModified: true,
        },
      ],
      exportItems: exportDocuments,
    });
    fireEvent.click(screen.getByText("批量导出副本"));
    expect(screen.getByRole("button", { name: "重试未完成项" })).toBeDisabled();
    actions.updatePdf({
      exportItems: [
        ...exportDocuments,
        {
          documentId: "33333333333333333333333333333333",
          name: "c.pdf",
          revision: 1,
          status: "failed",
          error: "目标冲突",
        },
      ],
    });
    expect(screen.getByRole("button", { name: "重试未完成项" })).toBeEnabled();
  });
});

describe("PDF workspace identity", () => {
  it("binds editing, save and switching to the opaque document identity", async () => {
    const actions = setup({
      documentId: "first",
      canCopyExport: true,
      documents: [
        { documentId: "first", name: "same.pdf", isModified: true },
        { documentId: "second", name: "same.pdf", isModified: false },
      ],
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.save",
      documentId: "first",
      documentRevision: 8,
    });
    fireEvent.click(
      screen.getByRole("button", { name: "另存为并切换保存目标" }),
    );
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.saveAs",
      documentId: "first",
      documentRevision: 8,
    });
    fireEvent.click(screen.getByRole("button", { name: "same.pdf" }));
    await waitFor(() =>
      expect(actions.run).toHaveBeenLastCalledWith({
        type: "pdf.activateDocument",
        documentId: "second",
        documentRevision: 8,
      }),
    );
    expect(
      screen.getByText(/导出副本保留原文档修改状态和保存目标/),
    ).toBeInTheDocument();
  });
});
