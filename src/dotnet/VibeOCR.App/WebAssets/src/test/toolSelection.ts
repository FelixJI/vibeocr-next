import { fireEvent, screen, within } from "@testing-library/react";
import { expect } from "vitest";

import { type Tool } from "../components/ImageCanvasEditor";
import { TOOL_LABELS } from "../components/toolLabels";
import {
  TOOL_FAMILIES,
  familyMenuLabel,
  type ToolFamily,
} from "../components/toolFamilies";

/** 按可访问名激活工具栏工具：直接按钮存在则点击；否则该工具是家族
 * 分组的非当前子工具，打开所属家族 ▾ 菜单选择。家族仅是视觉分组，
 * 激活语义与旧扁平工具条等价。 */
export async function activateToolbarTool(name: string): Promise<void> {
  const direct = screen.queryByRole("button", { name });
  if (direct) {
    fireEvent.click(direct);
    return;
  }
  const family = TOOL_FAMILIES.find((entry) =>
    entry.tools.some((tool) => TOOL_LABELS[tool as Tool] === name),
  );
  if (!family) throw new Error(`工具不在工具栏任何家族中：${name}`);
  fireEvent.click(
    screen.getByRole("button", { name: familyMenuLabel(family) }),
  );
  // Fluent 菜单同步打开；异步轮询窗口会被菜单自身的焦点管理关闭，
  // 必须同步查询（与既有「更多操作」菜单测试一致）。
  const menu = screen.getByRole("menu");
  fireEvent.click(within(menu).getByRole("menuitem", { name }));
}

/** 断言辅助：工具在工具栏可达（直接按钮或族内菜单项）且未禁用；
 * 不改变当前工具，断言后收起菜单。 */
export async function expectToolbarToolAvailable(name: string): Promise<void> {
  const direct = screen.queryByRole("button", {
    name,
  }) as HTMLButtonElement | null;
  if (direct) {
    expect(direct).toBeEnabled();
    return;
  }
  const family = TOOL_FAMILIES.find((entry) =>
    entry.tools.some((tool) => TOOL_LABELS[tool as Tool] === name),
  );
  if (!family) throw new Error(`工具不在工具栏任何家族中：${name}`);
  const toggle = screen.getByRole("button", { name: familyMenuLabel(family) });
  fireEvent.click(toggle);
  // Fluent 菜单同步打开；异步轮询窗口会被菜单自身的焦点管理关闭，
  // 必须同步查询（与既有「更多操作」菜单测试一致）。
  const menu = screen.getByRole("menu");
  expect(within(menu).getByRole("menuitem", { name })).not.toHaveAttribute(
    "aria-disabled",
    "true",
  );
  // 幂等收起：留开的菜单会让下一次族切换变成关闭动作。
  fireEvent.click(toggle);
}

/** 断言辅助：工具所属家族的 ▾ 按钮可访问名。 */
export function familyToggleName(family: ToolFamily): string {
  return familyMenuLabel(family);
}
