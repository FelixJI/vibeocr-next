import { expect, test } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

for (const height of [800, 1440]) {
  test(`workspace header stays compact at height ${height}`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1280, height });
    await mountHost(page, snapshot);
    const header = page.locator(".workspace-header");
    const description = header.locator(".workspace-description");
    const headerBox = await header.boundingBox();
    const descriptionBox = await description.boundingBox();
    expect(headerBox).not.toBeNull();
    expect(descriptionBox).not.toBeNull();
    expect(
      headerBox!.y +
        headerBox!.height -
        (descriptionBox!.y + descriptionBox!.height),
    ).toBeLessThanOrEqual(16);
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
  await expect(rail).toHaveCSS("width", "216px");
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
  await expect(rail).toHaveCSS("width", "216px");
  await page.setViewportSize({ width: 640, height: 480 });
  await expect(expand).toBeVisible();
  await expand.click();
  await expect(toggle).toBeVisible();
  await expect(pdf.locator("span")).toBeVisible();
  await expect(rail).toHaveCSS("width", "216px");
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(
    640,
  );
});
