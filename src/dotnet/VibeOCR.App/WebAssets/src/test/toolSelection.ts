import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
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

/** 打开工具栏菜单（对象操作/输出操作/识别模式/更多操作等触发钮）并
 * 返回菜单元素。菜单通常同步打开；调用点处在外层 act(async) 内时渲染
 * 会被批处理挂起，用一次空 act 微冲刷兜底（不用 findByRole 轮询——
 * 轮询窗口会被菜单焦点管理关闭）。触发钮是开关：若上一次调用留下了
 * 打开的菜单，本次点击会先把它关掉，这里补一次点击重新打开。 */
export async function openToolbarMenu(name: string): Promise<HTMLElement> {
  const wasOpen = !!screen.queryByRole("menu");
  const trigger = screen.getByRole("button", { name });
  fireEvent.click(trigger);
  let menu = screen.queryByRole("menu");
  if (!menu) {
    await act(async () => {});
    menu = screen.queryByRole("menu");
    if (!menu && wasOpen) {
      fireEvent.click(trigger);
      await act(async () => {});
      menu = screen.getByRole("menu");
    }
  }
  if (menu) return menu;
  return screen.getByRole("menu");
}

/** 直达工具栏菜单项并点击（自动开菜单）。 */
export async function clickToolbarMenuItem(
  menuName: string,
  itemName: string,
): Promise<void> {
  const menu = await openToolbarMenu(menuName);
  fireEvent.click(within(menu).getByRole("menuitem", { name: itemName }));
}

/** 激活识别模式：文字模式直出主按钮，其余（表格识别等）经「识别模式」
 * 菜单；与工具家族激活同一回退策略。直出按钮在文字层准备期间可能短暂
 * 禁用，等待可用后再点击。 */
export async function activateRecognitionMode(label: string): Promise<void> {
  const direct = screen.queryByRole("button", { name: label });
  if (direct) {
    await waitFor(() => expect(direct).toBeEnabled());
    fireEvent.click(direct);
    return;
  }
  await clickToolbarMenuItem("识别模式", label);
}

/** 断言辅助：工具所属家族的 ▾ 按钮可访问名。 */
export function familyToggleName(family: ToolFamily): string {
  return familyMenuLabel(family);
}
