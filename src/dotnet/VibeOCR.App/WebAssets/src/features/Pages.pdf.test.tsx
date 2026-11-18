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
function setup(
  patch: Record<string, unknown> = {},
  capabilities: readonly string[] = [
    "pdf.edit",
    "pdf.save",
    "pdf.rotate",
    "pdf.open",
  ],
) {
  let nativeFlush: (() => Promise<void>) | undefined;
  let retiredNativeFlush: (() => Promise<void>) | undefined;
  const actions: AppActions & { run: ReturnType<typeof vi.fn> } = {
    registerPdfPreviewFlush: (_id, flush) => {
      nativeFlush = flush;
      return () => {
        retiredNativeFlush = flush;
        if (nativeFlush === flush) nativeFlush = undefined;
      };
    },
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
  const state: AppViewState = {
    connected: true,
    revision: 1,
    route: "pdf",
    theme: "light",
    capabilities,
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
  // 低频页操作/插入/参数收进“高级与导出”：默认展开供各用例直接触达；
  // 空状态不渲染该入口。
  const advanced = screen.queryByText("高级与导出");
  if (advanced) fireEvent.click(advanced);
  return Object.assign(actions, {
    unmount: () => view.unmount(),
    openFromNative: () =>
      ((nativeFlush ?? retiredNativeFlush)?.() ?? Promise.resolve()).then(
        () => actions.run({ type: "pdf.open" }),
        () => false,
      ),
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
    fireEvent.change(screen.getByLabelText("方向操作范围"), {
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
    // 关闭文档后主区回到空状态；仅取消选择时保留预览占位。
    if ((patch as { pageCount?: number }).pageCount === 0) {
      expect(
        screen.getByText("打开 PDF 后在此检查页面、提取结果并添加文字层。"),
      ).toBeInTheDocument();
    } else {
      expect(screen.getByText("选择页面后查看预览。")).toBeInTheDocument();
    }
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

  it("only offers open for an actually empty workspace without failed documents", () => {
    setup({
      sessionId: undefined,
      pageCount: 0,
      selectedPage: -1,
      selectedPages: [],
      pages: [],
      documents: [],
    });
    expect(
      screen.queryByRole("button", { name: "关闭文档" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /^保存$/ }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: /^页面$/ }),
    ).not.toBeInTheDocument();
  });
});

describe("PDF document switching", () => {
  it.each(["switch", "open", "native", "unmounted"] as const)(
    "does not send %s while the last position commit is unconfirmed",
    async (operation) => {
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
      if (operation === "unmounted") {
        actions.unmount();
        void actions.openFromNative();
      } else if (operation === "native") void actions.openFromNative();
      else
        fireEvent.click(
          screen.getByRole("button", {
            name: operation === "switch" ? "b.pdf" : "添加 PDF",
          }),
        );
      await act(async () => {
        await Promise.resolve();
      });
      expect(
        actions.run.mock.calls.some(
          ([command]) =>
            (command as Record<string, unknown>).type ===
            (operation === "switch" ? "pdf.activateDocument" : "pdf.open"),
        ),
      ).toBe(false);
    },
  );

  it.each(["switch", "open", "native", "unmounted"] as const)(
    "commits the last draft before %s and immediate return to A",
    async (operation) => {
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
      if (operation === "unmounted") {
        actions.unmount();
        void actions.openFromNative();
      } else if (operation === "native") void actions.openFromNative();
      else
        fireEvent.click(
          screen.getByRole("button", {
            name: operation === "switch" ? "b.pdf" : "添加 PDF",
          }),
        );
      // 在途命令未完成：激活命令尚未发出。
      await act(async () => {
        await Promise.resolve();
      });
      expect(
        actions.run.mock.calls.some(
          ([command]) =>
            (command as Record<string, unknown>).type ===
            (operation === "switch" ? "pdf.activateDocument" : "pdf.open"),
        ),
      ).toBe(false);
      resolveSlow(true);
      await waitFor(() =>
        expect(
          actions.run.mock.calls.some(
            ([command]) =>
              (command as Record<string, unknown>).type ===
              (operation === "switch" ? "pdf.activateDocument" : "pdf.open"),
          ),
        ).toBe(true),
      );
      const calls = actions.run.mock.calls.map(
        ([command]) => command as Record<string, unknown>,
      );
      const activateIndex = calls.findIndex(
        (command) =>
          command.type ===
          (operation === "switch" ? "pdf.activateDocument" : "pdf.open"),
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
      if (operation === "unmounted") return;
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
      expect(await screen.findByLabelText("校正文字")).toHaveValue(
        "最终草稿211",
      );
    },
  );
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
    const saveAsForSecond =
      ".pdf-page:has(.pdf-documents button:nth-child(2)[aria-pressed='true']) " +
      "button[data-document-id='second'][data-revision='3']";
    expect(document.querySelector(saveAsForSecond)).toBeNull();

    actions.updatePdf({ documentId: "second", revision: 3 });
    const saveAs = screen.getByRole("button", { name: "另存为并切换保存目标" });
    expect(document.querySelector(saveAsForSecond)).toBe(saveAs);
    fireEvent.click(saveAs);
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.saveAs",
      documentId: "second",
      documentRevision: 3,
    });
    expect(
      screen.getByText(/导出副本保留原文档修改状态和保存目标/),
    ).toBeInTheDocument();
  });
});

it("preserves A confirmed draft through a second switch before restored resources load", async () => {
  const a = "11111111111111111111111111111111",
    b = "22222222222222222222222222222222";
  let finish!: (response: unknown) => void;
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finish = resolve;
          }),
      )
      .mockResolvedValue({
        ok: true,
        json: async () => ({
          page: 69,
          ocr_blocks: [
            { index: 2, text: "原文 R4", bbox: [100, 200, 900, 300] },
          ],
        }),
      }),
  );
  const confirmed = {
    block: 2,
    draft: "A确认草稿 R4TAIL",
    originalText: "原文 R4",
    left: 140,
    top: 80,
    zoom: 1.5,
    revision: 8,
    page: 69,
  };
  const documents = [
    { documentId: a, name: "a.pdf" },
    { documentId: b, name: "b.pdf" },
  ];
  const actions = setup({
    documentId: a,
    documents,
    previewPosition: confirmed,
    pagePreview: { url: "/a.png", mediaType: "image/png", byteLength: 100 },
    pageInspect: {
      url: "/a.json",
      mediaType: "application/json",
      byteLength: 100,
    },
  });
  let stored: unknown = confirmed;
  actions.run.mockImplementation(async (action) => {
    if (action.type === "pdf.setPreviewPosition" && action.documentId === a)
      stored = action.position;
    return true;
  });
  for (const [action] of actions.run.mock.calls)
    if (action.type === "pdf.setPreviewPosition" && action.documentId === a)
      stored = action.position;
  // A 恢复还未结束，用户再次切 B；真实 Pages 接缝会先排空编辑器。
  fireEvent.click(screen.getByRole("button", { name: "b.pdf" }));
  await waitFor(() =>
    expect(actions.run).toHaveBeenCalledWith(
      expect.objectContaining({ type: "pdf.activateDocument", documentId: b }),
    ),
  );
  expect(stored).toMatchObject(confirmed);
  actions.updatePdf({
    documentId: b,
    sessionId: "b-worker",
    selectedPage: -1,
    pages: [],
    canInspectPage: false,
    pageInspect: undefined,
    pagePreview: undefined,
  });
  fireEvent.click(screen.getByRole("button", { name: "a.pdf" }));
  await waitFor(() =>
    expect(actions.run).toHaveBeenCalledWith(
      expect.objectContaining({ type: "pdf.activateDocument", documentId: a }),
    ),
  );
  actions.updatePdf({
    documentId: a,
    documents,
    previewPosition: stored,
    pagePreview: { url: "/a.png", mediaType: "image/png", byteLength: 100 },
    pageInspect: {
      url: "/a.json",
      mediaType: "application/json",
      byteLength: 100,
    },
  });
  expect(await screen.findByLabelText("校正文字")).toHaveValue(confirmed.draft);
  await act(async () => {
    finish({ ok: true, json: async () => ({ page: 69, ocr_blocks: [] }) });
  });
  expect(screen.getByLabelText("校正文字")).toHaveValue(confirmed.draft);
});

describe("PDF document command shortcuts (Ctrl+O / Ctrl+S / Ctrl+Shift+S / Ctrl+W)", () => {
  /** 显式控制 document.hasFocus：守卫要求页面持有焦点，jsdom 默认值不作证据。 */
  function focusDocument(focused: boolean) {
    Object.defineProperty(document, "hasFocus", {
      configurable: true,
      value: () => focused,
    });
  }
  const shortcut = (key: string, init: Record<string, unknown> = {}) =>
    fireEvent.keyDown(window, { key, ctrlKey: true, bubbles: true, ...init });
  /** 只看文档命令本身：挂载的检查组件会在后台发 pdf.setPreviewPosition。 */
  const documentCommands = (actions: { run: ReturnType<typeof vi.fn> }) =>
    actions.run.mock.calls
      .map(([command]) => command as { type: string })
      .filter((command) =>
        ["pdf.save", "pdf.saveAs", "pdf.open", "pdf.close"].includes(
          command.type,
        ),
      );

  afterEach(() => {
    delete (document as { hasFocus?: () => boolean }).hasFocus;
  });

  it("runs save/saveAs/open/close through the wrapped entry with document binding", async () => {
    focusDocument(true);
    const actions = setup({
      documentId: "doc-1",
      canCopyExport: true,
      documents: [{ documentId: "doc-1", name: "a.pdf", isModified: true }],
    });
    shortcut("s");
    shortcut("S", { shiftKey: true });
    shortcut("o");
    shortcut("w");
    // open/close 经包装入口先排空检查草稿再发出，异步确认后到达。
    await waitFor(() =>
      expect(documentCommands(actions).map((command) => command.type)).toEqual([
        "pdf.save",
        "pdf.saveAs",
        "pdf.open",
        "pdf.close",
      ]),
    );
    const commands = documentCommands(actions);
    expect(commands[0]).toEqual({
      type: "pdf.save",
      documentId: "doc-1",
      documentRevision: 8,
    });
    expect(commands[1]).toEqual({
      type: "pdf.saveAs",
      documentId: "doc-1",
      documentRevision: 8,
    });
    expect(commands[2]).toEqual({ type: "pdf.open" });
    expect(commands[3]).toEqual({
      type: "pdf.close",
      documentId: "doc-1",
      documentRevision: 8,
    });
    // 同一业务入口：工具栏保存按钮与 Ctrl+S 发出完全相同的命令。
    actions.run.mockClear();
    fireEvent.click(screen.getByRole("button", { name: "保存" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "pdf.save",
      documentId: "doc-1",
      documentRevision: 8,
    });
  });

  it("skips commands while busy or without the matching capability", () => {
    focusDocument(true);
    // busy 镜像按钮禁用；capabilities 缺 pdf.save/pdf.open 时打开/保存/关闭都不触发。
    const actions = setup(
      {
        documentId: "doc-1",
        isBusy: true,
        canCopyExport: true,
        documents: [{ documentId: "doc-1", name: "a.pdf" }],
      },
      ["pdf.edit"],
    );
    shortcut("s");
    shortcut("S", { shiftKey: true });
    shortcut("o");
    shortcut("w");
    expect(documentCommands(actions)).toEqual([]);
  });

  it("skips save/saveAs/close for an empty document but keeps open available", () => {
    focusDocument(true);
    const actions = setup({
      documentId: "doc-1",
      pageCount: 0,
      selectedPage: -1,
      pages: [],
      canCopyExport: true,
      documents: [{ documentId: "doc-1", name: "a.pdf" }],
    });
    shortcut("s");
    shortcut("S", { shiftKey: true });
    shortcut("w");
    expect(documentCommands(actions)).toEqual([]);
    shortcut("o");
    expect(documentCommands(actions).map((command) => command.type)).toEqual([
      "pdf.open",
    ]);
  });

  it("does not trigger commands for IME composition, hidden or unfocused pages", () => {
    focusDocument(true);
    const actions = setup({ documentId: "doc-1" });
    // IME 组合中的 Ctrl+S（isComposing 与 keyCode 229 各自成立即让路）。
    fireEvent.keyDown(window, {
      key: "s",
      ctrlKey: true,
      isComposing: true,
      bubbles: true,
    });
    const imeKeyDown = new KeyboardEvent("keydown", {
      key: "s",
      ctrlKey: true,
      bubbles: true,
    });
    Object.defineProperty(imeKeyDown, "keyCode", { value: 229 });
    fireEvent(window, imeKeyDown);
    expect(documentCommands(actions)).toEqual([]);

    Object.defineProperty(document, "hidden", {
      configurable: true,
      get: () => true,
    });
    shortcut("s");
    delete (document as { hidden?: boolean }).hidden;
    focusDocument(false);
    shortcut("s");
    expect(documentCommands(actions)).toEqual([]);
  });
});

describe("PDF page inspection button without thumbnails", () => {
  it("keeps the inspect button usable with placeholder content and selects the page", () => {
    const actions = setup({
      pages: [
        {
          index: 69,
          statusCode: "pdf.page.none",
        },
      ],
    });
    const button = screen.getByRole("button", { name: "检查第 70 页" });
    expect(button).toBeEnabled();
    expect(
      button.querySelector(".pdf-thumbnail-placeholder"),
    ).toHaveTextContent("PDF");
    fireEvent.click(button);
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.setCurrentPage",
      page: 69,
    });
  });
});

describe("PDF on-demand page demand (canDemandPages)", () => {
  interface FakeEntry {
    target: Element;
    isIntersecting: boolean;
  }
  function installIntersectionObserver() {
    class FakeIntersectionObserver {
      observed: Element[] = [];
      disconnected = false;
      constructor(
        private readonly callback: (
          entries: IntersectionObserverEntry[],
          observer: IntersectionObserver,
        ) => void,
      ) {}
      observe(element: Element) {
        this.observed.push(element);
      }
      unobserve() {}
      disconnect() {
        this.disconnected = true;
        this.observed = [];
      }
      takeRecords() {
        return [];
      }
      trigger(entries: readonly FakeEntry[]) {
        this.callback(
          entries.map((entry) => ({
            target: entry.target,
            isIntersecting: entry.isIntersecting,
          })) as IntersectionObserverEntry[],
          this as unknown as IntersectionObserver,
        );
      }
    }
    const instances: FakeIntersectionObserver[] = [];
    vi.stubGlobal(
      "IntersectionObserver",
      class extends FakeIntersectionObserver {
        constructor(
          callback: (
            entries: IntersectionObserverEntry[],
            observer: IntersectionObserver,
          ) => void,
        ) {
          super(callback);
          instances.push(this);
        }
      },
    );
    return { instances, latest: () => instances[instances.length - 1] };
  }
  const demandPages = [
    { index: 64, statusCode: "pdf.page.none", width: 612, height: 792 },
    { index: 65, statusCode: "pdf.page.none", width: 612, height: 792 },
    {
      index: 69,
      statusCode: "pdf.page.done",
      detected: true,
      hasTextLayer: true,
      addedThisSession: false,
      width: 612,
      height: 792,
      pageKey: 6900,
      contentGeneration: 3,
    },
  ];
  function demandState(patch: Record<string, unknown> = {}) {
    return {
      documentId: "doc-1",
      canDemandPages: true,
      pages: demandPages,
      ...patch,
    };
  }
  const pageItem = (index: number) =>
    document.querySelector(`li[data-page-index="${index}"]`)!;
  const demandCalls = (actions: { run: ReturnType<typeof vi.fn> }) =>
    actions.run.mock.calls
      .map(([command]) => command as Record<string, unknown>)
      .filter((command) => command.type === "pdf.setDemand");
  const settle = (ms = 250) =>
    act(async () => {
      await new Promise((resolve) => setTimeout(resolve, ms));
    });
  function measureFirstThumbnail(edge: number) {
    const first = document.querySelector(
      "li[data-page-index] .pdf-thumbnail-button",
    ) as HTMLElement;
    first.getBoundingClientRect = () =>
      ({ width: 96, height: edge }) as DOMRect;
  }

  it("never sends demands without the capability, even with observer events", async () => {
    installIntersectionObserver();
    const actions = setup({ pages: demandPages });
    await settle();
    expect(demandCalls(actions)).toEqual([]);
  });

  it("reports real visible pages and geometry, not the whole window before callbacks", async () => {
    const observer = installIntersectionObserver();
    const actions = setup(demandState());
    // 初始 observer 未回调：检查页几何可上报，但可见集必须为空（不是整窗 64 页）。
    await waitFor(() => expect(demandCalls(actions).length).toBeGreaterThan(0));
    expect(demandCalls(actions).map((d) => d.visiblePages)).toEqual([[]]);
    measureFirstThumbnail(120);
    observer.latest()!.trigger([
      { target: pageItem(64), isIntersecting: true },
      { target: pageItem(69), isIntersecting: true },
    ]);
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "pdf.setDemand",
          page: 69,
          visiblePages: [64, 69],
          thumbnailSize: 120,
          previewMaxEdge: 456,
          documentId: "doc-1",
          documentRevision: 8,
        }),
      ),
    );
  });

  it("keeps thumbnail demand when the preview becomes unavailable", async () => {
    const observer = installIntersectionObserver();
    const actions = setup(demandState());
    observer
      .latest()!
      .trigger([{ target: pageItem(69), isIntersecting: true }]);
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        page: 69,
        visiblePages: [69],
        previewMaxEdge: 456,
      }),
    );
    actions.updatePdf(demandState({ canInspectPage: false }));
    await waitFor(() => {
      const latest = demandCalls(actions).at(-1);
      expect(latest).toMatchObject({ page: 69, visiblePages: [69] });
      expect(latest).not.toHaveProperty("previewMaxEdge");
    });
  });

  it("updates both demand sizes after consecutive DPR changes", async () => {
    const observer = installIntersectionObserver();
    const queries = new Map<string, EventTarget>();
    vi.stubGlobal("devicePixelRatio", 1);
    vi.stubGlobal("matchMedia", (query: string) => {
      const target = queries.get(query) ?? new EventTarget();
      queries.set(query, target);
      return target;
    });
    const actions = setup(demandState());
    measureFirstThumbnail(120);
    observer
      .latest()!
      .trigger([{ target: pageItem(69), isIntersecting: true }]);
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        thumbnailSize: 120,
        previewMaxEdge: 456,
      }),
    );
    for (const [previous, next] of [
      [1, 1.5],
      [1.5, 2],
    ]) {
      act(() => {
        vi.stubGlobal("devicePixelRatio", next);
        queries
          .get(`(resolution: ${previous}dppx)`)!
          .dispatchEvent(new Event("change"));
      });
      await waitFor(() =>
        expect(demandCalls(actions).at(-1)).toMatchObject({
          thumbnailSize: 120 * next!,
          previewMaxEdge: 456 * next!,
        }),
      );
    }
  });

  it("sends unchanged geometry for a new revision and discards the previous page's edge", async () => {
    installIntersectionObserver();
    const actions = setup(demandState());
    await waitFor(() => expect(demandCalls(actions)).toHaveLength(1));
    actions.updatePdf(demandState({ revision: 9 }));
    await waitFor(() => expect(demandCalls(actions)).toHaveLength(2));
    expect(demandCalls(actions)[1]).toMatchObject({
      page: 69,
      previewMaxEdge: 456,
      documentRevision: 9,
    });
    fireEvent.click(screen.getByRole("button", { name: "100%" }));
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        page: 69,
        previewMaxEdge: 1056,
      }),
    );
    actions.updatePdf(demandState({ revision: 9, selectedPage: 64 }));
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        page: 64,
        previewMaxEdge: 456,
        documentRevision: 9,
      }),
    );
    expect(
      demandCalls(actions).filter((demand) => demand.page === 64),
    ).toHaveLength(1);
  });

  it("deduplicates identical content and coalesces rapid changes into the latest value", async () => {
    const observer = installIntersectionObserver();
    const actions = setup(demandState());
    await waitFor(() => expect(demandCalls(actions).length).toBe(1));
    measureFirstThumbnail(120);
    observer
      .latest()!
      .trigger([{ target: pageItem(64), isIntersecting: true }]);
    await waitFor(() => expect(demandCalls(actions).length).toBe(2));
    // 同内容重投：不再发送。
    observer
      .latest()!
      .trigger([{ target: pageItem(64), isIntersecting: true }]);
    await settle();
    expect(demandCalls(actions)).toHaveLength(2);
    // 去抖窗口内连续变化合并为最新值。
    observer
      .latest()!
      .trigger([{ target: pageItem(65), isIntersecting: true }]);
    observer
      .latest()!
      .trigger([{ target: pageItem(64), isIntersecting: false }]);
    await waitFor(() => expect(demandCalls(actions).length).toBe(3));
    expect(demandCalls(actions)[2]!.visiblePages).toEqual([65]);
    await settle();
    expect(demandCalls(actions)).toHaveLength(3);
  });

  it("keeps a single in-flight demand and sends only the latest pending afterwards", async () => {
    const observer = installIntersectionObserver();
    const actions = setup(demandState());
    let resolveDemand!: (ok: boolean) => void;
    actions.run.mockImplementation((action) => {
      if ((action as Record<string, unknown>).type === "pdf.setDemand")
        return new Promise<boolean>((resolve) => {
          resolveDemand = resolve;
        });
      return Promise.resolve(true);
    });
    // 初始需求在途：可见集为空的那条。
    await waitFor(() => expect(demandCalls(actions).length).toBe(1));
    resolveDemand(true);
    await act(async () => {
      await Promise.resolve();
    });
    measureFirstThumbnail(120);
    observer
      .latest()!
      .trigger([{ target: pageItem(64), isIntersecting: true }]);
    await waitFor(() => expect(demandCalls(actions).length).toBe(2));
    // 第二条在途期间内容更新：不再并发发送。
    observer
      .latest()!
      .trigger([{ target: pageItem(65), isIntersecting: true }]);
    await settle();
    expect(demandCalls(actions)).toHaveLength(2);
    resolveDemand(true);
    await waitFor(() => expect(demandCalls(actions).length).toBe(3));
    expect(demandCalls(actions)[2]!.visiblePages).toEqual([64, 65]);
    await settle();
    expect(demandCalls(actions)).toHaveLength(3);
  });

  it("does not retry unconfirmed demands but accepts later content changes", async () => {
    const observer = installIntersectionObserver();
    const actions = setup(demandState());
    actions.run.mockImplementation(async (action) =>
      (action as Record<string, unknown>).type === "pdf.setDemand"
        ? false
        : true,
    );
    await waitFor(() => expect(demandCalls(actions).length).toBe(1));
    await settle();
    expect(demandCalls(actions)).toHaveLength(1);
    measureFirstThumbnail(120);
    observer
      .latest()!
      .trigger([{ target: pageItem(64), isIntersecting: true }]);
    await waitFor(() => expect(demandCalls(actions).length).toBe(2));
    await settle();
    // 未确认的同内容不重试；新内容仍可发送。
    expect(demandCalls(actions)).toHaveLength(2);
    observer
      .latest()!
      .trigger([{ target: pageItem(65), isIntersecting: true }]);
    await waitFor(() => expect(demandCalls(actions).length).toBe(3));
    expect(demandCalls(actions)[2]!.visiblePages).toEqual([64, 65]);
  });

  it("recreates the observer per transport window and ignores stale callbacks", async () => {
    const observer = installIntersectionObserver();
    const actions = setup(demandState());
    await waitFor(() => expect(demandCalls(actions).length).toBe(1));
    const stale = observer.latest()!;
    const staleTarget = pageItem(64);
    actions.updatePdf({
      windowStart: 0,
      pages: [
        { index: 0, statusCode: "pdf.page.none", width: 612, height: 792 },
        { index: 1, statusCode: "pdf.page.none", width: 612, height: 792 },
      ],
    });
    await waitFor(() => expect(observer.instances.length).toBe(2));
    expect(stale.disconnected).toBe(true);
    // 旧 observer 的迟到回调不能投递到新窗口。
    stale.trigger([{ target: staleTarget, isIntersecting: true }]);
    await settle();
    expect(
      demandCalls(actions).filter(
        (d) => Array.isArray(d.visiblePages) && d.visiblePages.length > 0,
      ),
    ).toEqual([]);
    measureFirstThumbnail(120);
    observer.latest()!.trigger([{ target: pageItem(0), isIntersecting: true }]);
    await waitFor(() =>
      expect(
        demandCalls(actions).some(
          (d) => Array.isArray(d.visiblePages) && d.visiblePages.length > 0,
        ),
      ).toBe(true),
    );
    expect(demandCalls(actions).at(-1)!.visiblePages).toEqual([0]);
  });

  it("labels detectionState without faking a confirmed no-text layer, legacy keeps detected semantics", () => {
    const demandView = setup({
      canDemandPages: true,
      pages: [
        { index: 64, statusCode: "pdf.page.none", detectionState: "unknown" },
        { index: 65, statusCode: "pdf.page.none", detectionState: "detecting" },
        { index: 66, statusCode: "pdf.page.none", detectionState: "failed" },
        {
          index: 67,
          statusCode: "pdf.page.done",
          detectionState: "known",
          hasTextLayer: false,
        },
        {
          index: 68,
          statusCode: "pdf.page.done",
          detectionState: "known",
          hasTextLayer: true,
        },
      ],
    });
    expect(pageItem(64)).toHaveTextContent("待检测");
    expect(pageItem(64)).not.toHaveTextContent("无文字层");
    expect(pageItem(65)).toHaveTextContent("检测中");
    expect(pageItem(66)).toHaveTextContent("检测失败");
    expect(pageItem(67)).toHaveTextContent("无文字层");
    expect(pageItem(68)).toHaveTextContent("已有文字层 / 来源未知");
    demandView.unmount();
    setup({
      pages: [
        { index: 64, statusCode: "pdf.page.none", detectionState: "unknown" },
      ],
    });
    // legacy 无 capability：detectionState 字段被忽略，保持 detected 语义。
    expect(pageItem(64)).toHaveTextContent("检测中");
    expect(pageItem(64)).not.toHaveTextContent("待检测");
  });

  it("allows adding text layers for a few selected pages without whole-book detection in demand mode", () => {
    const demandView = setup({
      canDemandPages: true,
      detectedCount: 3,
      canAddTextLayer: true,
    });
    expect(
      screen.getByRole("button", { name: "添加选中页文字层" }),
    ).toBeEnabled();
    expect(
      screen.getByRole("button", { name: "添加整本文字层" }),
    ).toBeEnabled();
    demandView.unmount();
    setup({
      detectedCount: 3,
      canAddTextLayer: true,
    });
    expect(
      screen.getByRole("button", { name: "添加选中页文字层" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "添加整本文字层" }),
    ).toBeDisabled();
  });

  it("keeps the selected inspection and reports only visible thumbnails across page windows", async () => {
    const observer = installIntersectionObserver();
    let image!: HTMLImageElement;
    const createImage = vi.fn(function () {
      image = document.createElement("img");
      image.decode = async () => undefined;
      return image;
    });
    vi.stubGlobal("Image", createImage);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({
          page: 0,
          ocr_blocks: [
            { index: 2, text: "首页原文", bbox: [100, 200, 900, 300] },
          ],
        }),
      }),
    );
    const page = {
      index: 0,
      statusCode: "pdf.page.done",
      width: 612,
      height: 792,
    };
    const currentPage = { ...page, pageKey: 42, contentGeneration: 7 };
    const firstWindow = [
      currentPage,
      ...Array.from({ length: 63 }, (_, index) => ({
        ...page,
        index: index + 1,
      })),
    ];
    const secondWindow = Array.from({ length: 64 }, (_, index) => ({
      ...page,
      index: index + 64,
    }));
    const actions = setup(
      demandState({
        selectedPage: 0,
        selectedPages: [0],
        pageCount: 128,
        windowStart: 0,
        pages: firstWindow,
        currentPage,
        pagePreview: {
          url: "/page-0.png",
          mediaType: "image/png",
          byteLength: 100,
        },
        pageInspect: {
          url: "/page-0.json",
          mediaType: "application/json",
          byteLength: 200,
        },
      }),
    );
    await act(async () => {
      fireEvent.load(image);
    });
    fireEvent.click(await screen.findByRole("button", { name: /首页原文/ }));
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "首页草稿" },
    });
    fireEvent.click(screen.getByRole("button", { name: "100%" }));
    observer.latest()!.trigger([{ target: pageItem(0), isIntersecting: true }]);
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        page: 0,
        visiblePages: [0],
        previewMaxEdge: 1056,
      }),
    );

    actions.updatePdf({ windowStart: 64, pages: secondWindow, currentPage });
    expect(pageItem(0)).toBeNull();
    expect(observer.latest()!.observed).toHaveLength(64);
    observer.latest()!.trigger([
      { target: pageItem(66), isIntersecting: true },
      { target: pageItem(127), isIntersecting: true },
    ]);
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        page: 0,
        visiblePages: [66, 127],
        previewMaxEdge: 1056,
      }),
    );
    expect(screen.getByAltText("当前第 1 页高清预览")).toBe(image);
    expect(screen.getByLabelText("校正文字")).toHaveValue("首页草稿");
    expect(document.querySelector(".pdf-inspection-sheet")).toHaveStyle({
      width: "816px",
      height: "1056px",
    });

    actions.updatePdf({ windowStart: 0, pages: firstWindow, currentPage });
    observer.latest()!.trigger([{ target: pageItem(3), isIntersecting: true }]);
    await waitFor(() =>
      expect(demandCalls(actions).at(-1)).toMatchObject({
        page: 0,
        visiblePages: [3],
        previewMaxEdge: 1056,
      }),
    );
    expect(screen.getByAltText("当前第 1 页高清预览")).toBe(image);
    expect(screen.getByLabelText("校正文字")).toHaveValue("首页草稿");
    expect(createImage).toHaveBeenCalledTimes(1);
  });

  it("does not remount the inspection when progressive preview URLs change", async () => {
    let image!: HTMLImageElement;
    vi.stubGlobal("Image", function () {
      image = document.createElement("img");
      image.decode = async () => undefined;
      return image;
    });
    installIntersectionObserver();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({
          page: 69,
          ocr_blocks: [
            {
              index: 2,
              text: "原文 6900",
              bbox: [100, 200, 900, 300],
            },
          ],
        }),
      }),
    );
    const actions = setup(
      demandState({
        pagePreview: {
          url: "/low-69.png",
          mediaType: "image/png",
          byteLength: 100,
        },
        pageInspect: {
          url: "/inspect-69.json",
          mediaType: "application/json",
          byteLength: 200,
        },
      }),
    );
    expect(image.getAttribute("src")).toBe("/low-69.png");
    await act(async () => {
      fireEvent.load(image);
    });
    fireEvent.click(await screen.findByRole("button", { name: /原文 6900/ }));
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "渐进草稿" },
    });
    actions.updatePdf({
      pagePreview: {
        url: "/high-69.png",
        mediaType: "image/png",
        byteLength: 300,
      },
    });
    // 未重挂：旧图保留为当前显示，新图走待替换过渡层。
    expect(screen.getByAltText("当前第 70 页高清预览")).toHaveAttribute(
      "src",
      "/low-69.png",
    );
    expect(image.getAttribute("src")).toBe("/high-69.png");
    await act(async () => {
      fireEvent.load(image);
    });
    expect(screen.getByAltText("当前第 70 页高清预览")).toHaveAttribute(
      "src",
      "/high-69.png",
    );
    expect(screen.getByLabelText("校正文字")).toHaveValue("渐进草稿");
  });
});
