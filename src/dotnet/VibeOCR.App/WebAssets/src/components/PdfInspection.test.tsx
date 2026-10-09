import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions } from "../app/types";
import { PdfInspection } from "./PdfInspection";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
const ocr = {
  index: 2,
  text: "原文",
  bbox: [100, 200, 700, 300],
  score: 0,
  score_unknown: true,
  is_manually_edited: true,
};
function props() {
  const actions: AppActions & { run: ReturnType<typeof vi.fn> } = {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
  return {
    page: 0,
    count: 3,
    revision: 7,
    sessionId: "doc-7",
    preview: { url: "/hd.png" },
    inspect: { url: "/inspect.json" },
    status: "pdf.inspect.ready",
    busy: false,
    canEdit: true,
    actions,
  };
}
function resource(payload: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, json: async () => payload }),
  );
}
describe("PDF HD inspection", () => {
  it("scales display coordinates once and submits the exact session/revision/block/old text", async () => {
    resource({ page: 0, ocr_blocks: [ocr] });
    const value = props();
    render(<PdfInspection {...value} />);
    const block = await screen.findByRole("button", {
      name: /OCR · 置信度未知 · 人工修改 · 原文/,
    });
    expect(block).toHaveStyle({
      left: "10%",
      top: "20%",
      width: "60%",
      height: "10%",
    });
    expect(screen.getByAltText("当前第 1 页高清预览")).toHaveAttribute(
      "src",
      "/hd.png",
    );
    fireEvent.click(block);
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "校正 English LONGTAIL" },
    });
    fireEvent.click(screen.getByRole("button", { name: "提交校正" }));
    expect(value.actions.run).toHaveBeenCalledWith({
      type: "pdf.updateBlockText",
      sessionId: "doc-7",
      revision: 7,
      page: 0,
      blockIndex: 2,
      expectedOldText: "原文",
      newText: "校正 English LONGTAIL",
    });
    await waitFor(() =>
      expect(screen.queryByLabelText("校正文字")).not.toBeInTheDocument(),
    );
  });
  it("keeps a rejected draft, cancels it without a write and hides only UI boxes", async () => {
    resource({ page: 0, ocr_blocks: [ocr] });
    const value = props();
    value.actions.run.mockResolvedValue(false);
    render(<PdfInspection {...value} />);
    fireEvent.click(
      await screen.findByRole("button", { name: /人工修改 · 原文/ }),
    );
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "新稿" },
    });
    fireEvent.click(screen.getByRole("button", { name: "提交校正" }));
    await screen.findByText(/校正未确认/);
    expect(screen.getByLabelText("校正文字")).toHaveValue("新稿");
    value.actions.run.mockClear();
    fireEvent.click(screen.getByRole("button", { name: "取消校正" }));
    expect(
      value.actions.run.mock.calls.every(
        ([command]) => command.type === "pdf.setPreviewPosition",
      ),
    ).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: "显示文字框" }));
    expect(
      screen.queryByRole("button", { name: /人工修改 · 原文/ }),
    ).not.toBeInTheDocument();
    expect(screen.getByAltText("当前第 1 页高清预览")).toBeInTheDocument();
  });
  it("shows native source and unknown confidence without allowing fake editing", async () => {
    resource({
      page: 0,
      native_lines: [{ text_preview: "native", bbox: [0, 0, 900, 100] }],
    });
    render(<PdfInspection {...props()} />);
    fireEvent.click(
      await screen.findByRole("button", {
        name: /已有 PDF 文字层 \/ 来源未知 · 置信度未知 · native/,
      }),
    );
    expect(screen.queryByLabelText("校正文字")).not.toBeInTheDocument();
    expect(screen.getByText(/仅检查/)).toBeInTheDocument();
  });
  it("rejects a mismatched page, retries resource failures and supports keyboard paging/zoom", async () => {
    resource({ page: 1, ocr_blocks: [ocr] });
    const value = props();
    render(<PdfInspection {...value} />);
    await screen.findByText("文字框读取失败，请重试当前页。");
    fireEvent.click(screen.getByRole("button", { name: "重试当前页" }));
    expect(value.actions.run).toHaveBeenLastCalledWith({
      type: "pdf.retryPageInspect",
    });
    const region = screen.getByRole("region", { name: "高清 PDF 页面" });
    fireEvent.keyDown(region, { key: "PageDown" });
    expect(value.actions.run).toHaveBeenLastCalledWith({
      type: "pdf.setCurrentPage",
      page: 1,
    });
    fireEvent.click(screen.getByRole("button", { name: "100%" }));
    fireEvent.keyDown(region, { key: "+" });
    expect(screen.getByText("125%")).toBeInTheDocument();
    fireEvent.keyDown(region, { key: "ArrowRight" });
    expect(region.scrollLeft).toBe(60);
  });
  it("ignores a late fetch after a page switch and discards the previous draft", async () => {
    let finish: (value: unknown) => void = () => undefined;
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
          json: async () => ({ page: 1, native_lines: [] }),
        }),
    );
    const value = props();
    const view = render(<PdfInspection key="page0" {...value} />);
    view.rerender(
      <PdfInspection
        key="page1"
        {...value}
        page={1}
        inspect={{ url: "/inspect-1.json" }}
      />,
    );
    finish({ ok: true, json: async () => ({ page: 0, ocr_blocks: [ocr] }) });
    await waitFor(() =>
      expect(screen.getByAltText("当前第 2 页高清预览")).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole("button", { name: /人工修改 · 原文/ }),
    ).not.toBeInTheDocument();
  });
});

it("bounds overlay DOM and refuses truncated OCR preview editing", async () => {
  resource({
    page: 0,
    truncated: true,
    ocr_blocks: Array.from({ length: 12000 }, (_, index) => ({
      ...ocr,
      index,
      text_truncated: true,
    })),
  });
  render(<PdfInspection {...props()} />);
  await waitFor(() =>
    expect(document.querySelectorAll(".pdf-text-box")).toHaveLength(1000),
  );
  expect(screen.getByRole("status")).toHaveTextContent("检查已截断");
  fireEvent.click(document.querySelector(".pdf-text-box")!);
  expect(screen.queryByLabelText("校正文字")).not.toBeInTheDocument();
  expect(screen.getByText(/不能用预览提交校正/)).toBeInTheDocument();
});

it("restores the owning document draft and preview settings without submitting it", async () => {
  resource({ page: 0, ocr_blocks: [ocr] });
  const value = props();
  render(
    <PdfInspection
      {...value}
      position={{
        zoom: 1.5,
        left: 20,
        top: 40,
        showBoxes: true,
        block: 2,
        draft: "恢复草稿202",
        revision: 7,
        page: 0,
      }}
    />,
  );
  await waitFor(() =>
    expect(screen.getByLabelText("校正文字")).toHaveValue("恢复草稿202"),
  );
  expect(
    value.actions.run.mock.calls.every(
      ([command]) => command.type === "pdf.setPreviewPosition",
    ),
  ).toBe(true);
  expect(value.actions.run).toHaveBeenCalledWith(
    expect.objectContaining({
      type: "pdf.setPreviewPosition",
      position: expect.objectContaining({
        zoom: 1.5,
        draft: "恢复草稿202",
        block: 2,
      }),
    }),
  );
});
