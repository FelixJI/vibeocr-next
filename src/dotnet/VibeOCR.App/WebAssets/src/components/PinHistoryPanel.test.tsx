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

  it("resets the host filter when a new panel mounts after history changes off-page", async () => {
    const user = userEvent.setup();
    let hostQuery: string | null = null;
    const entries = [...validHistory.entries];
    const run = vi.fn(async (command: Parameters<AppActions["run"]>[0]) => {
      if (
        command.type === "pinEdit.listHistory" &&
        (command.query === null || typeof command.query === "string")
      )
        hostQuery = command.query;
      return true;
    });
    const projection = () =>
      parsePinHistory({
        ...validHistory,
        count: entries.length,
        entries: entries.filter(
          (entry) =>
            hostQuery === null || entry.recognizedText?.includes(hostQuery),
        ),
      });
    const panel = () => (
      <PinHistoryPanel
        history={projection()}
        actions={actions(run)}
        connected={true}
      />
    );
    const first = render(panel());
    await user.type(
      screen.getByRole("searchbox", { name: "检索截图历史" }),
      "发票{Enter}",
    );
    first.rerender(panel());
    expect(screen.getAllByRole("button", { name: "重开" })).toHaveLength(1);
    first.unmount();
    // 路由离开后新截图发布历史，宿主仍持有上次已提交的筛选。
    entries.push({ ...validHistory.entries[1]!, entryId: "c".repeat(32) });
    const returned = render(panel());
    returned.rerender(panel());
    expect(screen.getByRole("searchbox", { name: "检索截图历史" })).toHaveValue(
      "",
    );
    expect(screen.getAllByRole("button", { name: "重开" })).toHaveLength(3);
    expect(run).toHaveBeenLastCalledWith({
      type: "pinEdit.listHistory",
      query: null,
    });
  });

  it("syncs only when history becomes available and preserves the submitted query on reconnect", async () => {
    const user = userEvent.setup();
    const run = vi.fn().mockResolvedValue(true);
    const panel = (
      history: ReturnType<typeof parsePinHistory>,
      connected: boolean,
    ) => (
      <PinHistoryPanel
        history={history}
        actions={actions(run)}
        connected={connected}
      />
    );
    const view = render(panel(undefined, true));
    expect(run).not.toHaveBeenCalled();
    view.rerender(panel(parsePinHistory(validHistory), false));
    expect(run).not.toHaveBeenCalled();
    view.rerender(panel(parsePinHistory(validHistory), true));
    expect(run).toHaveBeenLastCalledWith({
      type: "pinEdit.listHistory",
      query: null,
    });
    const search = screen.getByRole("searchbox", { name: "检索截图历史" });
    await user.type(search, "发票{Enter}未提交");
    run.mockClear();
    view.rerender(panel(parsePinHistory(validHistory), true));
    expect(run).not.toHaveBeenCalled();
    view.rerender(panel(parsePinHistory(validHistory), false));
    view.rerender(panel(parsePinHistory(validHistory), true));
    expect(run).toHaveBeenCalledExactlyOnceWith({
      type: "pinEdit.listHistory",
      query: "发票",
    });
    expect(search).toHaveValue("发票未提交");
    run.mockClear();
    view.rerender(panel(undefined, true));
    expect(run).not.toHaveBeenCalled();
    view.rerender(panel(parsePinHistory(validHistory), true));
    expect(run).toHaveBeenCalledExactlyOnceWith({
      type: "pinEdit.listHistory",
      query: "发票",
    });
  });

  it("keeps the submitted filter through reopen and delete projections", async () => {
    const user = userEvent.setup();
    const run = vi.fn().mockResolvedValue(true);
    const panel = (entries = validHistory.entries) => (
      <PinHistoryPanel
        history={parsePinHistory({ ...validHistory, entries })}
        actions={actions(run)}
        connected={true}
      />
    );
    const view = render(panel());
    await user.type(
      screen.getByRole("searchbox", { name: "检索截图历史" }),
      "发票{Enter}",
    );
    run.mockClear();
    view.rerender(panel([validHistory.entries[0]!]));
    await user.click(screen.getByRole("button", { name: "重开" }));
    view.rerender(panel([validHistory.entries[0]!]));
    await user.click(screen.getByRole("button", { name: "删除" }));
    view.rerender(panel([]));
    expect(screen.getByRole("searchbox", { name: "检索截图历史" })).toHaveValue(
      "发票",
    );
    expect(run.mock.calls.map(([command]) => command.type)).toEqual([
      "pinEdit.reopenHistoryEntry",
      "pinEdit.deleteHistoryEntry",
    ]);
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

  it("bounds the search query to the host 128-character budget", async () => {
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
    // 粘贴超过宿主协议预算的检索词后提交：不发送会被拒收且无回执的超长查询。
    await user.type(search, "x".repeat(140) + "{Enter}");
    const call = run.mock.calls.findLast(
      ([command]) => command.type === "pinEdit.listHistory",
    );
    expect(call).toBeDefined();
    expect((call![0] as { query: string }).query.length).toBeLessThanOrEqual(
      128,
    );
  });

  it("shows entry dates in the host-searchable yyyy-MM-dd HH:mm:ss format", () => {
    render(
      <PinHistoryPanel
        history={parsePinHistory(validHistory)}
        actions={actions()}
        connected={true}
      />,
    );
    // 显示与宿主检索同一固定格式：按界面看到的日期输入检索必须命中。
    const names = screen.getAllByText(/\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}/);
    expect(names.length).toBeGreaterThan(0);
    expect(names.every((node) => !node.textContent!.includes("/"))).toBe(true);
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
