import { expect, test, type Page } from "@playwright/test";

import type { AppSnapshot } from "../src/bridge/client";
import { mountHost } from "./workbench-host";

/** 经真实 bridge 通道装载视图状态（生产构建同样可用）；
 *  runtimeLabel 随 diagnostics 投影（无诊断 → “应用启动中…”），
 *  快照锁定真实传输行为。 */
async function mount(
  page: Page,
  state: Omit<AppSnapshot, "sessionId">,
): Promise<void> {
  await mountHost(page, { sessionId: "visual-e2e", ...state });
}

test("1280x800 light recognition workspace", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mount(page, {
    revision: 1,
    route: "recognition",
    theme: "light",
    capabilities: [
      "recognition.file",
      "recognition.clipboard",
      "recognition.capture",
    ],
    features: {
      recognition: { isBusy: false, statusCode: "recognition.ready" },
    },
  });
  await expect(page).toHaveScreenshot("recognition-light-1280x800.png", {
    fullPage: true,
  });
});

test("1280x800 light annotation workspace with guidance", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mount(page, {
    revision: 2,
    route: "recognition",
    theme: "light",
    capabilities: [
      "recognition.file",
      "recognition.clipboard",
      "recognition.capture",
      "recognition.results",
      "recognition.annotation",
    ],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/vibeocr-64.png",
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
  });
  await expect(
    page.getByRole("toolbar", { name: "图片编辑工具" }),
  ).toBeVisible();
  await expect(
    page.getByText(/拖拽框选“屏蔽”区，识别时忽略其中内容/),
  ).toBeVisible();
  await expect(page).toHaveScreenshot("annotation-light-1280x800.png", {
    fullPage: true,
  });
});

test("annotation export keeps source pixels and excludes editor chrome", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const uploads: Buffer[] = [];
  let releaseFirstUpload!: () => void;
  const firstUploadGate = new Promise<void>((resolve) => {
    releaseFirstUpload = resolve;
  });
  await page.route("**/__annotation", async (route) => {
    const body = route.request().postDataBuffer();
    if (body) uploads.push(body);
    if (uploads.length === 1) await firstUploadGate;
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      }),
    });
  });
  await page.route("**/annotation-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect width="80" height="40" fill="#2f6fed"/><rect x="8" y="8" width="24" height="16" fill="#ffffff"/></svg>',
    }),
  );
  await mount(page, {
    revision: 3,
    route: "imageEdit",
    theme: "light",
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      imageEdit: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/annotation-source.svg",
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
  });

  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  await expect(canvas).toBeVisible();
  await canvas.evaluate((element) => {
    element.setPointerCapture = () => undefined;
  });
  const bounds = await canvas.boundingBox();
  expect(bounds).not.toBeNull();
  expect(bounds!.width / bounds!.height).toBeCloseTo(1.5, 2);
  await page
    .getByRole("button", { name: "矩形" })
    .evaluate((button: HTMLButtonElement) => button.click());
  await canvas.dispatchEvent("pointerdown", {
    pointerId: 1,
    clientX: bounds!.x + 320,
    clientY: bounds!.y + 230,
  });
  await canvas.dispatchEvent("pointerup", {
    pointerId: 1,
    clientX: bounds!.x + 500,
    clientY: bounds!.y + 350,
  });
  await page
    .getByRole("button", { name: "选择", exact: true })
    .evaluate((button: HTMLButtonElement) => button.click());
  await canvas.dispatchEvent("pointerdown", {
    pointerId: 2,
    clientX: bounds!.x + 400,
    clientY: bounds!.y + 290,
  });
  await canvas.dispatchEvent("pointerup", {
    pointerId: 2,
    clientX: bounds!.x + 400,
    clientY: bounds!.y + 290,
  });

  const saveAnnotated = page.getByRole("button", { name: "保存标注图" });
  await saveAnnotated.evaluate((button: HTMLButtonElement) => {
    button.click();
    button.click();
  });
  await expect.poll(() => uploads.length).toBe(1);
  await expect(saveAnnotated).toBeDisabled();
  releaseFirstUpload();
  await expect(saveAnnotated).toBeEnabled();
  await canvas.press("Escape");
  await saveAnnotated.evaluate((button: HTMLButtonElement) => button.click());
  await expect.poll(() => uploads.length).toBe(2);

  expect(uploads[0]).toEqual(uploads[1]);
  expect(uploads[0]?.readUInt32BE(16)).toBe(80);
  expect(uploads[0]?.readUInt32BE(20)).toBe(40);

  await page
    .getByRole("button", { name: "旋转 90°" })
    .evaluate((button: HTMLButtonElement) => button.click());
  await saveAnnotated.evaluate((button: HTMLButtonElement) => button.click());
  await expect.poll(() => uploads.length).toBe(3);
  expect(uploads[2]?.readUInt32BE(16)).toBe(40);
  expect(uploads[2]?.readUInt32BE(20)).toBe(80);
});

test("1024x720 dark batch running workspace", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 720 });
  await mount(page, {
    revision: 4,
    route: "batch",
    theme: "dark",
    capabilities: ["batch.add", "batch.run", "batch.export"],
    features: {
      batch: {
        isBusy: true,
        isRunning: true,
        inputKindNotice:
          "Word、Excel、PowerPoint 文档使用 Flash 档整篇解析；PDF 和图片沿用当前档位。",
        itemCount: 3,
        completedCount: 1,
        failedCount: 0,
        windowStart: 0,
        items: [
          {
            id: "a",
            name: "发票-01.png",
            statusCode: "batch.item.completed",
            resultSummary: "含税合计 128.00",
          },
          { id: "b", name: "合同扫描件.pdf", statusCode: "batch.item.running" },
          {
            id: "c",
            name: "销售报表.xlsx",
            statusCode: "batch.item.pending",
            supportsPageRange: false,
          },
        ],
      },
    },
  });
  await expect(page.getByRole("button", { name: "添加文件" })).toBeVisible();
  await expect(page.getByText("销售报表.xlsx")).toBeVisible();
  await expect(
    page.getByRole("status").filter({ hasText: "Flash 档整篇解析" }),
  ).toBeVisible();
  await expect(page).toHaveScreenshot("batch-dark-1024x720.png", {
    fullPage: true,
  });
});

test("1280x800 light PDF review workspace", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  // 宿主真实契约：打开的文档必有 sessionId/documentId 与 documents 摘要
  // （含 dirty 状态）；canCopyExport 为 Runtime 协商真值（展示另存为与批量
  // 导出）。不伪造 HD 页检查或缩略图资源：canInspectPage=false 且无缩略图
  // 时回退到占位分支；截图前展开批量导出，呈现未确认导出状态。
  const documentId = "33333333333333333333333333333333";
  await mount(page, {
    revision: 7,
    route: "pdf",
    theme: "light",
    capabilities: ["pdf.open", "pdf.rotate", "pdf.edit", "pdf.save"],
    features: {
      pdf: {
        isBusy: false,
        statusCode: "pdf.open",
        sessionId: "0123456789abcdef0123456789abcdef",
        documentId,
        documents: [
          {
            documentId,
            name: "扫描合同-审阅.pdf",
            pageCount: 4,
            isModified: true,
            isBusy: false,
            phase: "idle",
            closeFailed: false,
          },
        ],
        pageCount: 4,
        selectedPage: 1,
        selectedPages: [1],
        windowStart: 0,
        revision: 3,
        detectedCount: 4,
        textLayerCount: 2,
        canAddTextLayer: true,
        isModified: true,
        canCopyExport: true,
        canInspectPage: false,
        canCorrectText: false,
        exporting: false,
        exportGeneration: 1,
        exportItems: [
          {
            documentId,
            name: "扫描合同-审阅.pdf",
            revision: 2,
            status: "unconfirmed",
            output: "扫描合同-审阅_1.pdf",
            error: "结果未确认，请检查输出 扫描合同-审阅_1.pdf；未自动重试。",
          },
        ],
        pages: [
          {
            index: 0,
            statusCode: "pdf.page.done",
            detected: true,
            hasTextLayer: true,
            addedThisSession: true,
          },
          {
            index: 1,
            statusCode: "pdf.page.done",
            detected: true,
            hasTextLayer: true,
          },
          {
            index: 2,
            statusCode: "pdf.page.none",
            detected: true,
            hasTextLayer: false,
          },
          {
            index: 3,
            statusCode: "pdf.page.none",
            detected: true,
            hasTextLayer: false,
          },
        ],
      },
    },
  });
  // 展开批量导出区后截图：未确认导出状态与重试入口对用户可见。
  await page.getByText("批量导出副本").click();
  await expect(page.getByText(/扫描合同-审阅\.pdf · 结果未确认/)).toBeVisible();
  await expect(page).toHaveScreenshot("pdf-light-1280x800.png", {
    fullPage: true,
  });
});

test("1024px screenshot session keeps editing and keyboard controls usable", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1024, height: 900 });
  await mount(page, {
    revision: 4,
    route: "recognition",
    theme: "light",
    capabilities: [
      "recognition.capture",
      "recognition.screenshotSession",
      "recognition.file",
      "recognition.clipboard",
      "recognition.annotation",
    ],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.session",
        screenshotSession: {
          sessionId: "0123456789abcdef0123456789abcdef",
          revision: 0,
        },
        input: {
          url: "/vibeocr-64.png",
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
  });
  await expect(
    page.getByRole("button", { name: "截图", exact: true }),
  ).toBeEnabled();
  // 识别面精简后无输出变换控件；键盘验证改用保留的显示缩放下拉。
  const zoom = page.getByRole("combobox", { name: "显示缩放" });

  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  const viewport = page.getByLabel("图片视口");
  const canvas = page.getByLabel("图片检查画布");
  // 在键盘改变缩放前记录初始 100% 布局，避免把旧 125% 布局当作基准。
  await expect(viewport).toHaveCSS("--editor-zoom", "1");
  // fit 布局就绪信号：画布宽度非零且不超过视口，避免读到 React 提交前的旧布局。
  let fitWidth = 0;
  await expect
    .poll(async () => {
      fitWidth = await canvas.evaluate(
        (element) => element.getBoundingClientRect().width,
      );
      return fitWidth;
    })
    .toBeGreaterThan(0);
  expect(fitWidth).toBeLessThanOrEqual(
    await viewport.evaluate((element) => element.clientWidth),
  );
  await zoom.focus();
  await expect(zoom).toBeFocused();
  await zoom.press("ArrowDown");
  await zoom.press("Tab");
  await expect(zoom).not.toBeFocused();
  await page.getByRole("combobox", { name: "显示缩放" }).selectOption("2");
  // 缩放应用与 React 提交存在异步窗口，轮询布局而非一次性读取。
  await expect
    .poll(() =>
      canvas.evaluate((element) => element.getBoundingClientRect().width),
    )
    .toBeGreaterThan(fitWidth * 1.9);
  await page.getByRole("combobox", { name: "显示缩放" }).selectOption("1");
  // 复原缩放同样存在异步提交窗口：慢速 runner 上必须等布局回到 fit 基准再截图。
  await expect
    .poll(async () => {
      const width = await canvas.evaluate(
        (element) => element.getBoundingClientRect().width,
      );
      return Math.abs(width - fitWidth);
    })
    .toBeLessThanOrEqual(1);
  // Tab/原生 select 的焦点滚动会移动嵌套 main；fullPage 只处理页面，
  // 不会复原该容器。键盘行为已验证，视觉基准统一回到内容顶部。
  const content = page.getByRole("main");
  await content.evaluate((element) =>
    element.scrollTo({ top: 0, left: 0, behavior: "instant" }),
  );
  await expect
    .poll(() => content.evaluate((element) => element.scrollTop))
    .toBe(0);
  await expect(page).toHaveScreenshot("screenshot-session-light-1024x900.png", {
    fullPage: true,
  });
});

test("recognition preview stage background follows the interface theme", async ({
  page,
}) => {
  // 用户项3：图片预览不需要黑边。透明 PNG 在主题背景上完整显示
  // （等比、不裁剪），画布视口背景不得是固定深色 #161616。
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.route("/transparent-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect x="20" y="10" width="40" height="20" fill="#2f6fed"/></svg>',
    }),
  );
  await mount(page, {
    revision: 9,
    route: "recognition",
    theme: "light",
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/transparent-source.svg",
          mediaType: "image/png",
          byteLength: 2048,
        },
      },
    },
  });
  const stage = page.getByLabel("图片视口");
  await expect(stage).toBeVisible();
  const backgroundColor = await stage.evaluate(
    (element) => getComputedStyle(element).backgroundColor,
  );
  expect(backgroundColor).not.toBe("rgb(22, 22, 22)");
  // 浅色主题下背景应是浅色（跟随 Fluent colorNeutralBackground3）。
  const channel = backgroundColor.match(/\d+/g)?.map(Number) ?? [];
  expect(channel.length).toBe(3);
  expect(channel[0]!).toBeGreaterThan(200);
  expect(channel[1]!).toBeGreaterThan(200);
  expect(channel[2]!).toBeGreaterThan(200);
});

test("mode selector stays above batch and PDF panel grids", async ({
  context,
}) => {
  for (const route of ["batch", "pdf"] as const) {
    const page = await context.newPage();
    await page.setViewportSize({ width: 1280, height: 800 });
    await mount(page, {
      revision: 1,
      route,
      theme: "light",
      capabilities: ["recognition.engine", "pdf.open", "batch.add"],
      features: {
        [route]: {
          engines: [
            {
              engine: "paddle_structure",
              displayName: "文档结构识别",
              availability: "ready",
              selected: false,
              requiresDownload: false,
              isTaskOverride: true,
              supportedOptions: ["use_seal_recognition"],
            },
          ],
          taskEngine: "paddle_structure",
          ...(route === "batch"
            ? {
                itemCount: 1,
                completedCount: 1,
                items: [
                  {
                    id: "table-1",
                    name: "table_merged_zh_en.png",
                    statusCode: "batch.item.completed",
                    structuredResult: {
                      url: "/structured-table.json",
                      mediaType: "application/json",
                      byteLength: 128,
                    },
                  },
                ],
              }
            : {}),
        },
      },
    });
    if (route === "batch") {
      const name = page.locator(".batch-item-copy");
      await expect(name).toBeVisible();
      expect((await name.boundingBox())!.width).toBeGreaterThan(100);
      const queue = page.locator(".batch-queue");
      expect(
        await queue.evaluate(
          (element) => element.scrollWidth <= element.clientWidth,
        ),
      ).toBe(true);
    }
    const selector = page.locator(".recognition-mode-settings");
    await expect(selector).toBeVisible();
    const grid = page.locator(
      route === "pdf" ? ".pdf-workspace" : ".collection-workspace",
    );
    const selectorBox = await selector.boundingBox();
    const gridBox = await grid.boundingBox();
    expect(selectorBox).not.toBeNull();
    expect(gridBox).not.toBeNull();
    expect(selectorBox!.y + selectorBox!.height).toBeLessThanOrEqual(
      gridBox!.y,
    );
    await page.close();
  }
});
