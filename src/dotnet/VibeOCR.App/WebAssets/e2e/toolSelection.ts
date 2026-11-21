import { type Page } from "@playwright/test";

/** 菜单项异步挂载等待：点开触发钮后菜单在下一拍渲染，直接 count 会
 * 误判为缺失；等待可见再判定。触发钮是开关——菜单已展开时直接点项，
 * 不再点触发钮把它关掉。禁用项（pointer-events:none）不可点也不该点：
 * 视为守卫生效（等价旧禁用按钮的 no-op），收起菜单返回成功。 */
async function clickMenuItemIn(
  page: Page,
  triggerName: string,
  itemName: string,
): Promise<boolean> {
  const item = page.getByRole("menuitem", { name: itemName, exact: true });
  const clickVisibleItem = async () => {
    await item.waitFor({ state: "visible", timeout: 1500 });
    if ((await item.getAttribute("aria-disabled")) === "true") {
      await page.keyboard.press("Escape");
      return true;
    }
    await item.click();
    return true;
  };
  if ((await item.count()) > 0) return clickVisibleItem();
  const trigger = page.getByRole("button", { name: triggerName, exact: true });
  if ((await trigger.count()) === 0) return false;
  await trigger.click();
  try {
    return await clickVisibleItem();
  } catch {
    await page.keyboard.press("Escape");
    return false;
  }
}

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
    const label = (await toggles.nth(index).getAttribute("aria-label")) ?? "";
    if (await clickMenuItemIn(page, label, name)) return;
  }
  throw new Error(`工具栏中未找到工具：${name}`);
}

/** 直达工具栏动作：直出按钮存在则点击；否则在 对象操作/输出操作/识别
 * 模式/更多操作 菜单中查找并点击同名菜单项（动作区重组后保存/对象/
 * 识别类动作收进菜单）。 */
export async function clickToolbarAction(
  page: Page,
  name: string,
): Promise<void> {
  const direct = page.getByRole("button", { name, exact: true });
  if ((await direct.count()) > 0) {
    await direct.click();
    return;
  }
  for (const menuName of ["对象操作", "输出操作", "识别模式", "更多操作"]) {
    if (await clickMenuItemIn(page, menuName, name)) return;
  }
  throw new Error(`工具栏中未找到动作：${name}`);
}

/** 通用入口（本地 action() 助手用）：不区分工具或动作——直出按钮直点，
 * 动作菜单未命中再试家族菜单。 */
export async function clickToolbarControl(
  page: Page,
  name: string,
): Promise<void> {
  const direct = page.getByRole("button", { name, exact: true });
  if ((await direct.count()) > 0) {
    await direct.click();
    return;
  }
  try {
    await clickToolbarAction(page, name);
    return;
  } catch {
    // 动作菜单未命中，继续按工具家族查找。
  }
  await activateToolbarTool(page, name);
}

/** 断言工具栏动作可用（直出按钮或菜单项未被禁用），断言后收起菜单。 */
export async function expectToolbarActionEnabled(
  page: import("@playwright/test").Page,
  name: string,
): Promise<void> {
  const { expect } = await import("@playwright/test");
  const direct = page.getByRole("button", { name, exact: true });
  if ((await direct.count()) > 0) {
    await expect(direct).toBeEnabled();
    return;
  }
  const item = page.getByRole("menuitem", { name, exact: true });
  if ((await item.count()) === 0) {
    await page.getByRole("button", { name: "输出操作", exact: true }).click();
    await item.waitFor({ state: "visible" });
  }
  await expect(item).not.toHaveAttribute("aria-disabled", "true");
  await page.keyboard.press("Escape");
}

/** 断言工具栏动作被禁用（导出在途等），断言后收起菜单。 */
export async function expectToolbarActionDisabled(
  page: import("@playwright/test").Page,
  name: string,
): Promise<void> {
  const { expect } = await import("@playwright/test");
  const direct = page.getByRole("button", { name, exact: true });
  if ((await direct.count()) > 0) {
    await expect(direct).toBeDisabled();
    return;
  }
  const item = page.getByRole("menuitem", { name, exact: true });
  if ((await item.count()) === 0) {
    await page.getByRole("button", { name: "输出操作", exact: true }).click();
    await item.waitFor({ state: "visible" });
  }
  await expect(item).toHaveAttribute("aria-disabled", "true");
  await page.keyboard.press("Escape");
}

/** 激活识别模式：文字模式直出主按钮，其余（表格识别等）经「识别模式」
 * 菜单；与动作/工具入口同一回退策略。 */
export async function activateRecognitionMode(
  page: import("@playwright/test").Page,
  label: string,
): Promise<void> {
  const direct = page.getByRole("button", { name: label, exact: true });
  if ((await direct.count()) > 0) {
    await direct.click();
    return;
  }
  if (await clickMenuItemIn(page, "识别模式", label)) return;
  throw new Error(`识别模式不可用：${label}`);
}
