import { expect, test } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  rejectNextCommand,
  sendState,
  snapshot,
} from "./workbench-host";

test("PDF HD inspection and correction remain reachable at 640px", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 640, height: 768 });
  await page.route("**/pdf-test-inspect.json", (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        page: 0,
        ocr_blocks: [
          {
            index: 2,
            text: "原文 OLD201",
            score: 0,
            score_unknown: true,
            is_manually_edited: false,
            bbox: [100, 200, 900, 300],
          },
        ],
      }),
    }),
  );
  await page.route("**/pdf-test-hd.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="1800" height="2400"><rect width="1800" height="2400" fill="white"/><text x="180" y="600" font-size="72">合成 PDF OLD201</text></svg>',
    }),
  );
  const pdfState = {
    sessionId: "pdf-real-session",
    pageCount: 2,
    selectedPage: 0,
    selectedPages: [0],
    revision: 9,
    isModified: true,
    pageInspectStatusCode: "pdf.inspect.ready",
    canInspectPage: true,
    canCorrectText: true,
    pagePreview: {
      url: "/pdf-test-hd.svg",
      mediaType: "image/png",
      byteLength: 100,
    },
    pageInspect: {
      url: "/pdf-test-inspect.json",
      mediaType: "application/json",
      byteLength: 300,
    },
    pages: [
      {
        index: 0,
        statusCode: "pdf.page.done",
        detected: true,
        hasTextLayer: true,
        addedThisSession: true,
      },
    ],
  };
  await mountHost(page, {
    ...snapshot,
    route: "pdf",
    features: { pdf: pdfState },
  });
  const region = page.getByRole("region", { name: "高清 PDF 页面" });
  await expect(page.getByAltText("当前第 1 页高清预览")).toBeVisible();
  await region.focus();
  await region.press("PageDown");
  await expectCommand(page, {
    scope: "pdf",
    action: "setCurrentPage",
    arguments: { page: 1 },
  });
  await page.getByRole("button", { name: "100%", exact: true }).click();
  await region.focus();
  await region.press("+");
  await expect(page.getByText("125%", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "适应页面" }).click();
  const block = page.getByRole("button", {
    name: /OCR · 置信度未知 · 原文 OLD201/,
  });
  await block.click();
  const input = page.getByLabel("校正文字");
  await input.fill("中文与 English 长句校正尾部 NEW201");
  await rejectNextCommand(page);
  await page.getByRole("button", { name: "提交校正", exact: true }).click();
  await expect(input).toHaveValue("中文与 English 长句校正尾部 NEW201");
  await expect(page.getByText(/校正未确认，请查看操作反馈/)).toBeVisible();
  await page.getByRole("button", { name: "提交校正", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expectCommand(page, {
    scope: "pdf",
    action: "updateBlockText",
    arguments: {
      sessionId: "pdf-real-session",
      revision: 9,
      page: 0,
      blockIndex: 2,
      expectedOldText: "原文 OLD201",
      newText: "中文与 English 长句校正尾部 NEW201",
    },
  });
  await block.click();
  await input.fill("未提交草稿");
  await input.press("Escape");
  await expect(input).toHaveCount(0);
  await block.click();
  await input.fill("换页时丢弃");
  await sendState(page, "pdf", {
    ...pdfState,
    selectedPage: 1,
    pagePreview: null,
    pageInspect: null,
    pageInspectStatusCode: "pdf.inspect.loading",
  });
  await expect(input).toHaveCount(0);
  await expect(block).toHaveCount(0);
  const save = page.getByRole("button", { name: "保存", exact: true });
  await save.focus();
  await save.press("Enter");
  await expectCommand(page, { scope: "pdf", action: "save", arguments: {} });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
  await sendState(page, "pdf", pdfState);
  await block.click();
  await page.screenshot({
    path: testInfo.outputPath("pdf-editing-640.png"),
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "提交校正", exact: true })
    .scrollIntoViewIfNeeded();
  await page.screenshot({
    path: testInfo.outputPath("pdf-editing-controls-640.png"),
    fullPage: true,
  });
});

test("same named PDF documents bind commands and restore only their own draft at 640px", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 640, height: 768 });
  const a = "11111111111111111111111111111111";
  const b = "22222222222222222222222222222222";
  await page.route("**/workspace-inspect.json", (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        page: 0,
        ocr_blocks: [
          { index: 2, text: "OWNED TEXT202", bbox: [100, 200, 900, 300] },
        ],
      }),
    }),
  );
  await page.route("**/workspace-hd.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="1800" height="2400"><rect width="1800" height="2400" fill="white"/></svg>',
    }),
  );
  const state = {
    documentId: a,
    sessionId: "worker-a",
    revision: 8,
    pageCount: 75,
    selectedPage: 0,
    selectedPages: [0],
    windowStart: 0,
    isModified: true,
    canCopyExport: true,
    canInspectPage: true,
    canCorrectText: true,
    pageInspectStatusCode: "pdf.inspect.ready",
    documents: [
      { documentId: a, name: "same.pdf", isModified: true },
      { documentId: b, name: "same.pdf", isModified: false },
    ],
    pagePreview: {
      url: "/workspace-hd.svg",
      mediaType: "image/svg+xml",
      byteLength: 130,
    },
    pageInspect: {
      url: "/workspace-inspect.json",
      mediaType: "application/json",
      byteLength: 120,
    },
    pages: Array.from({ length: 64 }, (_, index) => ({
      index,
      statusCode: "pdf.page.done",
      detected: true,
      hasTextLayer: index === 0,
      addedThisSession: index === 0,
    })),
  };
  await mountHost(page, {
    ...snapshot,
    route: "pdf",
    features: { ...snapshot.features, pdf: state },
  });
  await page.getByRole("button", { name: /OCR.*OWNED TEXT202/ }).click();
  await page.getByLabel("校正文字").fill("ONLY A DRAFT202");
  await page.locator(".pdf-documents button").nth(1).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "activateDocument",
    arguments: { documentId: b, documentRevision: 8 },
  });
  await sendState(page, "pdf", {
    ...state,
    documentId: b,
    sessionId: "worker-b",
    revision: 3,
    pageCount: 2,
    pages: state.pages.slice(0, 2),
    isModified: false,
  });
  await expect(page.getByLabel("校正文字")).toHaveCount(0);
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "save",
    arguments: { documentId: b, documentRevision: 3 },
  });
  await sendState(page, "pdf", {
    ...state,
    previewPosition: {
      revision: 8,
      page: 0,
      block: 2,
      draft: "ONLY A DRAFT202",
      zoom: 1.5,
    },
  });
  await expect(page.getByLabel("校正文字")).toHaveValue("ONLY A DRAFT202");
  await page.locator("details.pdf-export summary").click();
  await expect(
    page.getByText(/导出副本保留原文档修改状态和保存目标/),
  ).toBeVisible();
  await page.getByRole("button", { name: "选择目录并导出" }).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "exportDocuments",
    arguments: {
      documentId: a,
      documentRevision: 8,
      modifiedOnly: true,
      retry: false,
    },
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > innerWidth,
    ),
  ).toBe(false);
  await page.locator(".pdf-documents").scrollIntoViewIfNeeded();
  await page.screenshot({
    path: testInfo.outputPath("pdf-workspace-640.png"),
    fullPage: true,
  });
});
