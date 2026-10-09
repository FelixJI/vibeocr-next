import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppAction, AppActions } from "../app/types";
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
    await waitFor(() => expect(value.actions.run).toHaveBeenCalled());
    expect(
      value.actions.run.mock.calls.every(
        ([command]) => command.type === "pdf.setPreviewPosition",
      ),
    ).toBe(true);
    await waitFor(() =>
      expect(value.actions.run).toHaveBeenCalledWith(
        expect.objectContaining({
          type: "pdf.setPreviewPosition",
          position: expect.objectContaining({ draft: "" }),
        }),
      ),
    );
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

describe("preview position update scheduling", () => {
  function deferredActions() {
    const actions: AppActions & { run: ReturnType<typeof vi.fn> } = {
      run: vi.fn<(action: AppAction) => Promise<boolean>>().mockImplementation(
        () =>
          new Promise<boolean>((resolve) => {
            resolveNext = (ok) => resolve(ok);
          }),
      ),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    let resolveNext: (ok: boolean) => void = () => undefined;
    return { actions, resolve: () => resolveNext(true) };
  }

  it("sends later updates after a first send completes and an empty flush runs", async () => {
    resource({ page: 0, ocr_blocks: [ocr] });
    const value = props();
    let registeredFlush: (() => Promise<void>) | null = null;
    render(
      <PdfInspection
        {...value}
        registerPositionFlush={(flush) => {
          registeredFlush = flush;
        }}
      />,
    );
    await waitFor(() => expect(value.actions.run).toHaveBeenCalledTimes(1));
    // 首次发送完成后进行一次空排空：不能把已完成任务留在唯一任务槽里。
    await act(async () => {
      await registeredFlush?.();
    });
    expect(value.actions.run).toHaveBeenCalledTimes(1);
    const region = screen.getByRole("region", { name: "高清 PDF 页面" });
    region.scrollLeft = 42;
    fireEvent.scroll(region);
    await waitFor(() => expect(value.actions.run).toHaveBeenCalledTimes(2));
    expect(value.actions.run).toHaveBeenLastCalledWith({
      type: "pdf.setPreviewPosition",
      position: expect.objectContaining({ left: 42 }),
    });
  });

  it("keeps an unconfirmed update pending and recovers on retry", async () => {
    resource({ page: 0, ocr_blocks: [ocr] });
    const value = props();
    value.actions.run.mockResolvedValueOnce(false).mockResolvedValue(true);
    let registeredFlush: (() => Promise<void>) | null = null;
    render(
      <PdfInspection
        {...value}
        registerPositionFlush={(flush) => {
          registeredFlush = flush;
        }}
      />,
    );
    // 首次发送未确认：值保留，不视为已提交。
    await waitFor(() => expect(value.actions.run).toHaveBeenCalledTimes(1));
    await act(async () => {
      await registeredFlush?.();
    });
    // 重试同值并确认成功。
    await waitFor(() => expect(value.actions.run).toHaveBeenCalledTimes(2));
    expect(value.actions.run).toHaveBeenNthCalledWith(2, {
      type: "pdf.setPreviewPosition",
      position: expect.objectContaining({ page: 0, revision: 7 }),
    });
    // 恢复后新交互继续正常发送。
    const region = screen.getByRole("region", { name: "高清 PDF 页面" });
    region.scrollTop = 21;
    fireEvent.scroll(region);
    await waitFor(() => expect(value.actions.run).toHaveBeenCalledTimes(3));
    expect(value.actions.run).toHaveBeenLastCalledWith({
      type: "pdf.setPreviewPosition",
      position: expect.objectContaining({ top: 21 }),
    });
  });

  it("coalesces high-frequency scroll and draft edits into one in-flight command with the last value", async () => {
    resource({ page: 0, ocr_blocks: [ocr] });
    const { actions, resolve } = deferredActions();
    const value = props();
    value.actions = actions;
    render(<PdfInspection {...value} />);
    await waitFor(() => expect(actions.run).toHaveBeenCalledTimes(1));
    const region = screen.getByRole("region", { name: "高清 PDF 页面" });
    region.scrollLeft = 120;
    fireEvent.scroll(region);
    region.scrollLeft = 240;
    region.scrollTop = 60;
    fireEvent.scroll(region);
    fireEvent.click(
      await screen.findByRole("button", { name: /人工修改 · 原文/ }),
    );
    const draft = screen.getByLabelText("校正文字");
    fireEvent.change(draft, { target: { value: "草稿A" } });
    fireEvent.change(draft, { target: { value: "草稿B" } });
    fireEvent.change(draft, { target: { value: "草稿A" } });
    await act(async () => {
      await Promise.resolve();
    });
    // 单个 in-flight：延迟的 bridge 响应期间不产生第二条命令。
    expect(actions.run).toHaveBeenCalledTimes(1);
    resolve();
    await waitFor(() => expect(actions.run).toHaveBeenCalledTimes(2));
    // 即刻 A→B→A 不丢状态：最后发送的一直是最新值。
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.setPreviewPosition",
      position: expect.objectContaining({
        left: 240,
        top: 60,
        draft: "草稿A",
        revision: 7,
        page: 0,
      }),
    });
  });

  it("drains the final pending position after unmount once the in-flight reply lands", async () => {
    resource({ page: 0, ocr_blocks: [ocr] });
    const { actions, resolve } = deferredActions();
    const value = props();
    value.actions = actions;
    const view = render(<PdfInspection {...value} />);
    await waitFor(() => expect(actions.run).toHaveBeenCalledTimes(1));
    fireEvent.click(
      await screen.findByRole("button", { name: /人工修改 · 原文/ }),
    );
    fireEvent.change(screen.getByLabelText("校正文字"), {
      target: { value: "卸载前草稿" },
    });
    view.unmount();
    // 排空链串行：卸载时最后值仍在链上等待，不与在途命令并发。
    expect(actions.run).toHaveBeenCalledTimes(1);
    resolve();
    await waitFor(() => expect(actions.run).toHaveBeenCalledTimes(2));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "pdf.setPreviewPosition",
      position: expect.objectContaining({ draft: "卸载前草稿" }),
    });
    await act(async () => {
      await Promise.resolve();
    });
    // 不重发旧值，也不产生第三次发送。
    expect(actions.run).toHaveBeenCalledTimes(2);
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
  await waitFor(() =>
    expect(value.actions.run).toHaveBeenCalledWith(
      expect.objectContaining({
        type: "pdf.setPreviewPosition",
        position: expect.objectContaining({
          zoom: 1.5,
          draft: "恢复草稿202",
          block: 2,
        }),
      }),
    ),
  );
});
