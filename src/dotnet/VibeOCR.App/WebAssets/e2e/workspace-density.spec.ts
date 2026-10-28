import { expect, test } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

for (const height of [800, 1440]) {
  test(`workspace header stays compact at height ${height}`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1280, height });
    await mountHost(page, snapshot);
    const header = page.locator(".workspace-header");
    // 冗余说明与英文 eyebrow 已删：头部只剩标题与动作，不占预览高度。
    await expect(header.locator(".workspace-description")).toHaveCount(0);
    await expect(header.locator(".eyebrow")).toHaveCount(0);
    const headerBox = await header.boundingBox();
    expect(headerBox).not.toBeNull();
    expect(headerBox!.height).toBeLessThanOrEqual(64);
    await page.screenshot({
      path: testInfo.outputPath("compact-workspace.png"),
    });
  });
}

test("sidebar collapses, keeps navigation usable and expands on narrow screens", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mountHost(page, snapshot);
  const rail = page.getByRole("complementary", { name: "主导航" });
  const toggle = page.getByRole("button", { name: "收起侧边栏" });
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  await expect(rail).toHaveCSS("width", "184px");
  await toggle.click();
  const expand = page.getByRole("button", { name: "展开侧边栏" });
  await expect(expand).toHaveAttribute("aria-expanded", "false");
  await expect(rail).toHaveCSS("width", "52px");
  const pdf = page.getByRole("link", { name: "PDF", exact: true });
  await expect(pdf.locator("span")).toBeHidden();
  await expect(pdf).toHaveAttribute("title", "PDF");
  await pdf.click();
  await expect(
    page.getByRole("heading", { name: "PDF 工作台", exact: true }),
  ).toBeVisible();
  await expect(expand).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("collapsed-sidebar.png") });
  await expand.focus();
  await page.keyboard.press("Enter");
  await expect(toggle).toBeVisible();
  await expect(rail).toHaveCSS("width", "184px");
  await page.setViewportSize({ width: 640, height: 480 });
  await expect(expand).toBeVisible();
  await expand.click();
  await expect(toggle).toBeVisible();
  await expect(pdf.locator("span")).toBeVisible();
  await expect(rail).toHaveCSS("width", "184px");
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(
    640,
  );
});
