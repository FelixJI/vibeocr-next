import { type Page } from "@playwright/test";

/** 按可访问名激活工具栏工具：直接按钮存在则点击；否则遍历家族 ▾
 * 菜单找到该工具。家族仅是视觉分组，激活语义与旧扁平工具条等价。 */
export async function activateToolbarTool(
  page: Page,
  name: string,
): Promise<void> {
  const direct = page.getByRole("button", { name, exact: true });
  if ((await direct.count()) > 0) {
    await direct.click();
    return;
  }
  const toggles = page.getByRole("button", { name: /工具选项$/ });
  const count = await toggles.count();
  for (let index = 0; index < count; index++) {
    await toggles.nth(index).click();
    const item = page.getByRole("menuitem", { name, exact: true });
    if ((await item.count()) > 0) {
      await item.click();
      return;
    }
    await page.keyboard.press("Escape");
  }
  throw new Error(`工具栏中未找到工具：${name}`);
}
