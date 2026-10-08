import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { PdfPage } from "./Pages";

afterEach(cleanup);
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
    expect(actions.run).not.toHaveBeenCalled();
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
  ({ label, command, patch }) => {
    const actions = setup();
    expect(
      screen.getByRole("button", { name: "适应页面" }),
    ).toBeInTheDocument();
    expect(screen.getByText("正在读取当前页高清预览…")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: label }));
    expect(actions.run).toHaveBeenLastCalledWith(command);
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
