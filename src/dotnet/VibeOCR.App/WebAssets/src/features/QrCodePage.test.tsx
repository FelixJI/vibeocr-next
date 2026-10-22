import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { QrCodePage } from "./QrCodePage";

const viewState: AppViewState = {
  connected: true,
  revision: 1,
  route: "qrcode",
  theme: "light",
  capabilities: ["qrcode.generate"],
  features: {},
  runtimeLabel: "就绪",
};
const tick = async (ms: number) =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
function setup(state = viewState) {
  const actions: AppActions = {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
  const rendered = render(<QrCodePage viewState={state} actions={actions} />);
  return { ...rendered, actions, input: screen.getByLabelText("输入内容") };
}
beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("automatic QR generation", () => {
  it("debounces content and settings into one latest payload, without replaying snapshots", async () => {
    const { actions, input, rerender } = setup();
    fireEvent.change(input, { target: { value: "a" } });
    await tick(300);
    fireEvent.change(input, { target: { value: "VIBE-128" } });
    fireEvent.change(screen.getByLabelText("生成格式"), {
      target: { value: "code128" },
    });
    fireEvent.change(screen.getByLabelText("底部文字"), {
      target: { value: "custom" },
    });
    fireEvent.change(screen.getByLabelText("独立说明"), {
      target: { value: "标签" },
    });
    await tick(399);
    expect(actions.run).not.toHaveBeenCalled();
    await tick(1);
    expect(actions.run).toHaveBeenCalledExactlyOnceWith({
      type: "qrcode.generate",
      text: "VIBE-128",
      format: "code128",
      captionMode: "custom",
      captionText: "标签",
    });
    rerender(
      <QrCodePage
        viewState={{ ...viewState, revision: 2 }}
        actions={{ ...actions }}
      />,
    );
    await tick(500);
    expect(actions.run).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByLabelText("底部文字"), {
      target: { value: "payload" },
    });
    await tick(400);
    expect(actions.run).toHaveBeenCalledTimes(2);
  });

  it("waits for IME composition, and manual generation consumes the pending timer", async () => {
    const { actions, input } = setup();
    fireEvent.compositionStart(input);
    fireEvent.change(input, { target: { value: "中文" } });
    await tick(1000);
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.compositionEnd(input);
    await tick(399);
    expect(actions.run).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "生成图片" }));
    await tick(1000);
    expect(actions.run).toHaveBeenCalledTimes(1);
  });

  it("waits while busy and generates only the newest edits after completion", async () => {
    const { actions, input, rerender } = setup();
    fireEvent.change(input, { target: { value: "old" } });
    await tick(400);
    rerender(
      <QrCodePage
        viewState={{ ...viewState, features: { qrcode: { isBusy: true } } }}
        actions={actions}
      />,
    );
    fireEvent.change(input, { target: { value: "intermediate" } });
    await tick(500);
    fireEvent.change(input, { target: { value: "latest" } });
    await tick(500);
    expect(actions.run).toHaveBeenCalledTimes(1);
    rerender(<QrCodePage viewState={viewState} actions={actions} />);
    await tick(400);
    expect(actions.run).toHaveBeenLastCalledWith(
      expect.objectContaining({ text: "latest" }),
    );
    expect(actions.run).toHaveBeenCalledTimes(2);
  });

  it("cancels pending generation on blank input, decode tab, capability loss or unmount", async () => {
    const { actions, input, rerender, unmount } = setup();
    fireEvent.change(input, { target: { value: "draft" } });
    fireEvent.change(input, { target: { value: "  " } });
    await tick(500);
    fireEvent.change(input, { target: { value: "draft" } });
    fireEvent.click(screen.getByRole("tab", { name: "识别" }));
    await tick(500);
    fireEvent.click(screen.getByRole("tab", { name: "生成" }));
    rerender(
      <QrCodePage
        viewState={{ ...viewState, capabilities: [] }}
        actions={actions}
      />,
    );
    await tick(500);
    rerender(<QrCodePage viewState={viewState} actions={actions} />);
    unmount();
    await tick(500);
    expect(actions.run).not.toHaveBeenCalled();
  });
});
