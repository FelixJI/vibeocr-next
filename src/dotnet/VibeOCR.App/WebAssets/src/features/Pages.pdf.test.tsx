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
        pageCount: 70,
        windowStart: 64,
        selectedPage: 69,
        selectedPages: [69],
        revision: 8,
        isModified: true,
        detectedCount: 70,
        textLayerCount: 3,
        canAddTextLayer: true,
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
  render(<PdfPage viewState={state} actions={actions} />);
  return actions;
}
describe("PDF text layer actions", () => {
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
