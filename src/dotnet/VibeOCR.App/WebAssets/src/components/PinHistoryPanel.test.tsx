import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import { PinHistoryPanel } from "./PinHistoryPanel";
import { parsePinHistory } from "./pinHistoryState";

import type { AppActions } from "../app/types";

afterEach(cleanup);

function actions(run = vi.fn().mockResolvedValue(true)): AppActions {
  return {
    run,
    navigate: () => {},
    setTheme: () => {},
  };
}

const validHistory = {
  enabled: true,
  count: 2,
  totalBytes: 2048,
  maximumEntries: 200,
  maximumBytes: 268435456,
  entries: [
    {
      entryId: "b".repeat(32),
      createdAt: "2026-01-02T12:00:00+08:00",
      width: 100,
      height: 50,
      bytes: 1024,
      recognizedText: "发票金额",
    },
    {
      entryId: "a".repeat(32),
      createdAt: "2026-01-01T12:00:00+08:00",
      width: 80,
      height: 60,
      bytes: 1024,
      recognizedText: null,
    },
  ],
};

describe("parsePinHistory", () => {
  it("accepts the host projection and rejects malformed shapes", () => {
    expect(parsePinHistory(validHistory)?.entries).toHaveLength(2);
    expect(parsePinHistory(undefined)).toBeUndefined();
    expect(
      parsePinHistory({ ...validHistory, enabled: "yes" }),
    ).toBeUndefined();
    expect(
      parsePinHistory({
        ...validHistory,
        entries: [{ ...validHistory.entries[0], entryId: "zz" }],
      }),
    ).toBeUndefined();
  });
});

describe("PinHistoryPanel", () => {
  it("dispatches enable toggle, reopen, delete and clear commands", async () => {
    const user = userEvent.setup();
    const run = vi.fn().mockResolvedValue(true);
    render(
      <PinHistoryPanel
        history={parsePinHistory(validHistory)}
        actions={actions(run)}
        connected={true}
      />,
    );

    await user.click(screen.getByRole("switch", { name: "启用截图历史" }));
    expect(run).toHaveBeenCalledWith({
      type: "pinEdit.setHistoryEnabled",
      enabled: false,
    });

    await user.click(screen.getAllByRole("button", { name: "重开" })[0]!);
    expect(run).toHaveBeenCalledWith({
      type: "pinEdit.reopenHistoryEntry",
      entryId: "b".repeat(32),
    });

    await user.click(screen.getAllByRole("button", { name: "删除" })[1]!);
    expect(run).toHaveBeenCalledWith({
      type: "pinEdit.deleteHistoryEntry",
      entryId: "a".repeat(32),
    });

    window.confirm = () => true;
    await user.click(screen.getByRole("button", { name: "清空" }));
    expect(run).toHaveBeenCalledWith({ type: "pinEdit.clearHistory" });
  });

  it("search submits the trimmed query via listHistory on enter", async () => {
    const user = userEvent.setup();
    const run = vi.fn().mockResolvedValue(true);
    render(
      <PinHistoryPanel
        history={parsePinHistory(validHistory)}
        actions={actions(run)}
        connected={true}
      />,
    );
    const search = screen.getByRole("searchbox", {
      name: "检索截图历史",
    });
    await user.type(search, "发票{Enter}");
    expect(run).toHaveBeenCalledWith({
      type: "pinEdit.listHistory",
      query: "发票",
    });
  });

  it("enables clear by total count even when the filtered list is empty", () => {
    const run = vi.fn().mockResolvedValue(true);
    render(
      <PinHistoryPanel
        history={parsePinHistory({ ...validHistory, entries: [] })}
        actions={actions(run)}
        connected={true}
      />,
    );
    // 检索无匹配时列表为空但宿主仍持有全量记录，清空按 count 启用。
    expect(screen.getByRole("button", { name: "清空" })).toBeEnabled();

    cleanup();
    render(
      <PinHistoryPanel
        history={parsePinHistory({ ...validHistory, count: 0, entries: [] })}
        actions={actions(run)}
        connected={true}
      />,
    );
    expect(screen.getByRole("button", { name: "清空" })).toBeDisabled();
  });

  it("renders nothing without a host projection", () => {
    const { container } = render(
      <PinHistoryPanel
        history={undefined}
        actions={actions()}
        connected={true}
      />,
    );
    expect(container).toBeEmptyDOMElement();
  });
});
