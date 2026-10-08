import { expect, test } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

test("narrow PDF page operations remain keyboard reachable and keep explicit scope", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 640, height: 768 });
  await mountHost(page, {
    ...snapshot,
    route: "pdf",
    features: {
      pdf: {
        pageCount: 129,
        selectedPages: [63, 128],
        selectedPage: 63,
        revision: 9,
        windowStart: 64,
        pages: [
          {
            index: 64,
            statusCode: "pdf.page.none",
            detected: true,
            width: 300,
            height: 500,
            rotation: 0,
          },
        ],
      },
    },
  });
  await page.getByLabel("页面处理范围").selectOption("all");
  const reverse = page.getByRole("button", { name: "逆时针 90°" });
  await reverse.focus();
  await reverse.press("Enter");
  await expectCommand(page, {
    scope: "pdf",
    action: "rotate",
    arguments: { degrees: -90, range: "all" },
  });
  const forward = page.getByRole("button", { name: "第 65 页向后移动" });
  await forward.focus();
  await forward.press("Enter");
  await expectCommand(page, {
    scope: "pdf",
    action: "movePage",
    arguments: { fromIndex: 64, toIndex: 65, revision: 9 },
  });
  await page.getByRole("button", { name: "全选页面" }).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "selectAll",
    arguments: { selected: true },
  });
  await expect(page.getByLabel("插入到第几页后（0 为开头）")).toBeVisible();
  await page.getByLabel("插入到第几页后（0 为开头）").fill("129");
  await page.getByLabel("空白页宽度 pt").fill("640");
  await page.getByLabel("空白页高度 pt").fill("480");
  const insert = page.getByRole("button", { name: "插入空白页" });
  await insert.focus();
  await insert.press("Enter");
  await expectCommand(page, {
    scope: "pdf",
    action: "insertBlank",
    arguments: { afterIndex: 128, width: 640, height: 480, revision: 9 },
  });
  const save = page.getByRole("button", { name: "保存", exact: true });
  await save.focus();
  await save.press("Enter");
  await expectCommand(page, { scope: "pdf", action: "save", arguments: {} });
  const horizontalOverflow = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth,
  );
  expect(horizontalOverflow).toBe(false);
  await page.screenshot({
    path: testInfo.outputPath("pdf-640-keyboard.png"),
    fullPage: true,
  });
  await sendState(page, "pdf", {
    pageCount: 129,
    isBusy: true,
    phase: "ocr",
    progressCurrent: 1,
    progressTotal: 129,
    summary: "已请求取消，等待后台实际收尾",
    pages: [{ index: 0, statusCode: "pdf.page.none" }],
    selectedPages: [0],
  });
  const cancel = page.getByRole("button", { name: "取消 PDF 操作" });
  await cancel.focus();
  await cancel.press("Enter");
  await expectCommand(page, { scope: "pdf", action: "cancel", arguments: {} });
});

test("public drag reorders a PDF page with its revision", async ({ page }) => {
  await mountHost(page, {
    ...snapshot,
    route: "pdf",
    features: {
      pdf: {
        pageCount: 2,
        selectedPages: [0],
        selectedPage: 0,
        revision: 5,
        pages: [
          { index: 0, statusCode: "pdf.page.none", detected: true },
          { index: 1, statusCode: "pdf.page.none", detected: true },
        ],
      },
    },
  });
  await page
    .locator('[data-page-index="0"]')
    .dragTo(page.locator('[data-page-index="1"]'));
  await expectCommand(page, {
    scope: "pdf",
    action: "movePage",
    arguments: { fromIndex: 0, toIndex: 1, revision: 5 },
  });
});
