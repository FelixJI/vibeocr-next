import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { SelectionToolbarPanel } from "./SelectionToolbarPanel";

afterEach(cleanup);

it("保留失败草稿，同一配置可重试直到宿主回显", async () => {
  const user = userEvent.setup();
  const run = vi.fn().mockResolvedValueOnce(false).mockResolvedValue(true);
  const actions = { run, navigate: vi.fn(), setTheme: vi.fn() };
  const value = ["rectangle"];
  const view = render(
    <SelectionToolbarPanel value={value} actions={actions} />,
  );
  await user.click(screen.getByText("框选动作栏工具"));
  await user.click(screen.getByLabelText("显示画笔"));
  const save = screen.getByRole("button", { name: "保存框选动作栏工具" });
  await user.click(save);
  expect(save).toBeEnabled();
  expect(screen.getByRole("alert")).toHaveTextContent("未保存");
  await user.click(save);
  // 命令成功送达时，宿主仍可能返回写盘错误和原值。
  view.rerender(
    <SelectionToolbarPanel value={value} actions={actions} error="写盘失败" />,
  );
  expect(screen.getByRole("alert")).toHaveTextContent("写盘失败");
  expect(screen.getByLabelText("显示画笔")).toBeChecked();
  expect(screen.queryByText(/已保存/)).toBeNull();
  await user.click(save);
  expect(run.mock.calls).toEqual(
    Array(3).fill([
      { type: "settings.setSelectionToolbar", tools: ["rectangle", "pen"] },
    ]),
  );
  view.rerender(
    <SelectionToolbarPanel value={["rectangle", "pen"]} actions={actions} />,
  );
  expect(save).toBeDisabled();
  expect(screen.queryByRole("alert")).toBeNull();
});
