import { expect, test } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

/** 预览优先工作台的空间利用与收纳菜单键盘契约：
 *  - 超宽窗口不被全局 max-width 截窄，预览列吃到窗口面积；
 *  - 主窗编辑视图的画布起点不再被常驻静态说明推低；
 *  - 收纳后的导出菜单保持键盘可达（Enter/Arrow/Enter 提交同一命令）。 */

const WIDE = { width: 2560, height: 1440 } as const;
const LAPTOP = { width: 1280, height: 800 } as const;

test("wide recognition window uses the full width for preview and results", async ({
  page,
}) => {
  await page.setViewportSize(WIDE);
  await page.route("/wide-proof.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="500"><rect width="100%" height="100%" fill="#709acc"/></svg>',
    }),
  );
  await mountHost(page, {
    ...snapshot,
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/wide-proof.svg",
          mediaType: "image/svg+xml",
          byteLength: 200,
        },
      },
    },
  });
  const workspace = await page
    .locator(".workspace")
    .evaluate((element) => element.getBoundingClientRect().width);
  // 移除全局 1920px 阅读限宽后，预览型页面在 2560 宽窗口吃满内容区。
  expect(workspace).toBeGreaterThan(2200);
  const previewColumn = await page
    .locator("#recognition-preview")
    .evaluate((element) => element.getBoundingClientRect().width);
  const resultColumn = await page
    .locator(".inspection-grid > .work-panel:nth-child(2)")
    .evaluate((element) => element.getBoundingClientRect().width);
  expect(previewColumn).toBeGreaterThan(900);
  expect(resultColumn).toBeGreaterThan(700);
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth),
  ).toBeLessThanOrEqual(WIDE.width);
});

test("main-window edit view starts the canvas high at 1280x800", async ({
  page,
}) => {
  await page.setViewportSize(LAPTOP);
  await page.route("/high-proof.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="400"><rect width="100%" height="100%" fill="#709acc"/></svg>',
    }),
  );
  await mountHost(page, {
    ...snapshot,
    capabilities: [...snapshot.capabilities, "recognition.annotation"],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.session",
        screenshotSession: {
          sessionId: "0123456789abcdef0123456789abcdef",
          revision: 0,
        },
        input: {
          url: "/high-proof.svg",
          mediaType: "image/svg+xml",
          byteLength: 200,
        },
      },
    },
  });
  await page.getByRole("button", { name: "编辑图片", exact: true }).click();
  const stage = page.getByLabel("图片视口");
  await expect(stage).toBeVisible();
  const box = await stage.evaluate((element) => {
    const rect = element.getBoundingClientRect();
    return { top: rect.top, bottom: rect.bottom, height: rect.height };
  });
  // 静态用法/图像信息已收进折叠入口：画布起点明显上移（旧布局约 500px），
  // 且保持真实预览高度并完整落在窗口内。
  expect(box.top).toBeLessThan(430);
  expect(box.bottom).toBeLessThanOrEqual(LAPTOP.height);
  expect(box.height).toBeGreaterThanOrEqual(280);
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth),
  ).toBeLessThanOrEqual(LAPTOP.width);
  // 使用帮助入口保留且可键盘展开。
  await expect(page.getByText(/原图 800×400/)).toBeHidden();
  await page.getByText("使用帮助与图像信息").focus();
  await page.keyboard.press("Enter");
  await expect(page.getByText(/原图 800×400/)).toBeVisible();
  await expect(
    page.getByText(/拖拽框选“屏蔽”区，识别时忽略其中内容/),
  ).toBeVisible();
});

test("recognition export menu stays keyboard operable after consolidation", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await page.route("/ocr-result.txt", (route) =>
    route.fulfill({ contentType: "text/plain", body: "键盘导出结果" }),
  );
  await mountHost(page, snapshot);
  await sendState(page, "recognition", {
    isBusy: false,
    statusCode: "recognition.completed",
    result: {
      url: "/ocr-result.txt",
      mediaType: "text/plain",
      byteLength: 24,
    },
  });
  const trigger = page.getByRole("button", { name: "导出", exact: true });
  await trigger.focus();
  await expect(trigger).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("menuitem", { name: "Markdown", exact: true }),
  ).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Enter");
  await expectCommand(page, {
    scope: "recognition",
    action: "export",
    arguments: { format: "docx" },
  });
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
});

test("batch preview column stays in the window at 1024x720 and export menu is reachable", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1024, height: 720 });
  await page.route("/structured.json", (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify([{ type: "text", text: "批量结构化结果" }]),
    }),
  );
  await mountHost(
    page,
    {
      ...snapshot,
      route: "batch",
      capabilities: ["batch.add", "batch.run", "batch.export"],
      features: {
        batch: {
          isRunning: false,
          itemCount: 1,
          completedCount: 1,
          failedCount: 0,
          items: [
            {
              id: "done-1",
              name: "发票-01.png",
              statusCode: "batch.item.completed",
              resultSummary: "含税合计 128.00",
              structuredResult: {
                url: "/structured.json",
                mediaType: "application/json",
                byteLength: 64,
              },
            },
          ],
        },
      },
    },
    "/#/batch",
  );
  // 预览结果优先：选择队列项后，结果预览面板完整落在 720 高窗口内。
  await page
    .getByRole("button", { name: "查看 发票-01.png 的结构化结果" })
    .click();
  const previewPanel = page.getByRole("region", { name: "结果预览" });
  await expect(previewPanel).toBeVisible();
  await expect(page.getByText("批量结构化结果")).toBeVisible();
  const queuePanel = page.getByRole("region", { name: "文件队列" });
  const queueBox = await queuePanel.boundingBox();
  const panelBox = await previewPanel.boundingBox();
  // 中等窗口队列+结果同屏：两栏并列，面板不整列压到视口下方。
  expect(panelBox!.x).toBeGreaterThan(queueBox!.x + queueBox!.width);
  expect(panelBox!.x + panelBox!.width).toBeLessThanOrEqual(1024);
  expect(queueBox!.y + queueBox!.height).toBeLessThanOrEqual(720);
  expect(panelBox!.y + panelBox!.height).toBeLessThanOrEqual(720);
  const exportTrigger = page.getByRole("button", { name: "导出全部" });
  await expect(exportTrigger).toBeEnabled();
  await exportTrigger.click();
  await page.getByRole("menuitem", { name: "Excel", exact: true }).click();
  await expectCommand(page, {
    scope: "batch",
    action: "exportAll",
    arguments: { format: "xlsx" },
  });
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
});

test("qr narrow window keeps input and preview visible together", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "qrcode",
      capabilities: ["qrcode.generate"],
    },
    "/#/qrcode",
  );
  const content = page.getByLabel("输入内容", { exact: true });
  await expect(content).toBeVisible();
  const inputBox = await content.boundingBox();
  const previewPanel = page.getByRole("region", {
    name: "生成预览",
    exact: true,
  });
  const previewBox = await previewPanel.boundingBox();
  expect(inputBox!.y).toBeGreaterThanOrEqual(0);
  expect(inputBox!.y + inputBox!.height).toBeLessThanOrEqual(480);
  expect(previewBox!.y + previewBox!.height).toBeLessThanOrEqual(480);
  expect(previewBox!.height).toBeGreaterThan(200);
  await content.fill("窄窗输入");
  await expectCommand(page, {
    scope: "qrcode",
    action: "generate",
    arguments: {
      text: "窄窗输入",
      format: "qrcode",
      captionMode: "off",
      captionText: "",
    },
  });
  await page.screenshot({ path: testInfo.outputPath("qr-preview.png") });
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
});

for (const size of [
  { width: 901, height: 480 },
  { width: 1024, height: 600 },
  { width: 1280, height: 800 },
]) {
  test(`PDF inspection fits the remaining height at ${size.width}x${size.height}`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize(size);
    await page.route("/fit-page.svg", (route) =>
      route.fulfill({
        contentType: "image/svg+xml",
        body: '<svg xmlns="http://www.w3.org/2000/svg" width="600" height="800"><rect width="600" height="800" fill="white"/><g fill="#333" font-family="sans-serif"><text x="60" y="90" font-size="32">Contract / 合同</text><text x="60" y="150" font-size="18">VibeOCR Next · 页面预览测试</text><text x="60" y="230" font-size="18">完整页面应适应可用区域</text></g><path d="M60 280h480M60 340h480M60 400h480M60 460h480M60 520h480" stroke="#ddd"/></svg>',
      }),
    );
    await mountHost(page, {
      ...snapshot,
      route: "pdf",
      features: {
        pdf: {
          sessionId: "fit-document",
          revision: 1,
          pageCount: 2,
          selectedPage: 0,
          selectedPages: [0],
          canInspectPage: true,
          pagePreview: {
            url: "/fit-page.svg",
            mediaType: "image/svg+xml",
            byteLength: 300,
          },
          pages: [
            {
              index: 0,
              statusCode: "pdf.page.done",
              detected: true,
              hasTextLayer: true,
            },
          ],
        },
      },
    });
    await expect(page.getByAltText("当前第 1 页高清预览")).toBeVisible();
    const region = page.getByRole("region", { name: "高清 PDF 页面" });
    if (size.height < 560) await region.scrollIntoViewIfNeeded();
    const rect = (await region.boundingBox())!;
    expect(rect.height).toBeGreaterThanOrEqual(
      size.height < 560 ? 160 : size.height * 0.35,
    );
    // 原生滚动可能有 1px 边框舍入；图片本身仍须完整可见。
    expect(rect.y + rect.height).toBeLessThanOrEqual(size.height + 1);
    await expect(page.getByAltText("当前第 1 页高清预览")).toBeInViewport({
      ratio: 1,
    });
    if (size.height < 560)
      await expect(
        page.getByRole("button", { name: "保存", exact: true }),
      ).toBeInViewport();
    if (size.height >= 560) {
      expect(
        await page
          .getByRole("main")
          .evaluate((el) => el.scrollHeight - el.clientHeight),
      ).toBeLessThanOrEqual(1);
    }
    await page.screenshot({ path: testInfo.outputPath("pdf-fit.png") });
  });
}

for (const size of [
  { width: 600, height: 400 },
  { width: 640, height: 480 },
  { width: 1024, height: 600 },
]) {
  test(`long batch queue keeps results visible at ${size.width}x${size.height}`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize(size);
    await mountHost(page, {
      ...snapshot,
      route: "batch",
      capabilities: [
        ...snapshot.capabilities,
        "recognition.engine",
        "batch.add",
        "batch.run",
        "batch.export",
      ],
      features: {
        batch: {
          itemCount: 30,
          completedCount: 0,
          failedCount: 0,
          engines: [
            {
              engine: "paddle_text",
              displayName: "文字识别",
              availability: "ready",
              selected: true,
              supportedOptions: ["model_preset", "device"],
            },
          ],
          items: Array.from({ length: 30 }, (_, index) => ({
            id: String(index),
            name: `扫描文档-${index + 1}.pdf`,
            statusCode: "batch.item.pending",
          })),
        },
      },
    });
    const queue = page.locator(".batch-queue");
    await expect(queue).toBeVisible();
    expect(
      await queue.evaluate((el) => el.scrollHeight - el.clientHeight),
    ).toBeGreaterThan(0);
    const preview = page.getByRole("region", { name: "结果预览" });
    const rect = (await preview.boundingBox())!;
    expect(rect.y + rect.height).toBeLessThanOrEqual(size.height);
    expect(rect.height).toBeGreaterThan(180);
    expect(
      await page
        .getByRole("main")
        .evaluate((el) => el.scrollHeight - el.clientHeight),
    ).toBeLessThanOrEqual(1);
    await page.screenshot({ path: testInfo.outputPath("batch-preview.png") });
  });
}

test("pdf narrow window shows page inspection before the thumbnail list", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "pdf",
      features: {
        pdf: {
          isBusy: false,
          statusCode: "pdf.open",
          pageCount: 2,
          selectedPage: 0,
          selectedPages: [0],
          pages: [
            { index: 0, statusCode: "pdf.page.done", detected: true },
            { index: 1, statusCode: "pdf.page.none" },
          ],
        },
      },
    },
    "/#/pdf",
  );
  const review = page.getByRole("region", { name: "页面检查" });
  await expect(review).toBeVisible();
  const reviewTop = (await review.boundingBox())!.y;
  const pagesTop = (await page
    .getByRole("region", { name: "页面", exact: true })
    .boundingBox())!.y;
  // 窄窗口主预览优先：页面检查在缩略图列表之前进入视口。
  expect(reviewTop).toBeLessThan(pagesTop);
  expect(reviewTop).toBeLessThan(480);
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
});

test("pdf main preview precedes advanced controls at 1280x800", async ({
  page,
}) => {
  await page.setViewportSize(LAPTOP);
  await mountHost(page, {
    ...snapshot,
    route: "pdf",
    features: {
      pdf: {
        isBusy: false,
        statusCode: "pdf.open",
        pageCount: 2,
        selectedPage: 0,
        selectedPages: [0],
        pages: [
          { index: 0, statusCode: "pdf.page.done", detected: true },
          { index: 1, statusCode: "pdf.page.none" },
        ],
      },
    },
  });
  const review = page.getByRole("region", { name: "页面检查" });
  await expect(review).toBeVisible();
  const reviewTop = (await review.boundingBox())!.y;
  // 主预览优先：页面检查面板位于插入/参数/导出折叠区之前。
  const advanced = page.getByText("高级与导出");
  const advancedTop = (await advanced.boundingBox())!.y;
  expect(reviewTop).toBeLessThan(advancedTop);
  expect(reviewTop).toBeLessThan(600);
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
});

test("recognition core inputs stay reachable at 600x400", async ({ page }) => {
  await page.setViewportSize({ width: 600, height: 400 });
  await mountHost(page, snapshot);
  const choose = page.getByRole("button", { name: "选择图片", exact: true });
  await expect(choose).toBeVisible();
  const chooseBox = await choose.boundingBox();
  expect(chooseBox!.y).toBeGreaterThanOrEqual(0);
  expect(chooseBox!.y).toBeLessThan(400);
  const status = page.locator(".status-line");
  const statusBox = await status.boundingBox();
  expect(statusBox).not.toBeNull();
  expect(statusBox!.y).toBeLessThan(400);
  await expect(
    page.getByRole("button", { name: "取消", exact: true }),
  ).toHaveCount(0);
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth),
  ).toBeLessThanOrEqual(600);
});

test("pdf empty state keeps only open entry and status at 640x480", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await mountHost(page, { ...snapshot, route: "pdf" }, "/#/pdf");
  const open = page.getByRole("button", { name: "打开 PDF", exact: true });
  const openBox = await open.boundingBox();
  expect(openBox!.y).toBeLessThan(480);
  // 空状态不再常驻整套禁用页面命令：高频命令只在打开文档后出现。
  await expect(
    page.getByRole("button", { name: "提取/解析选中页" }),
  ).toHaveCount(0);
  await expect(page.getByRole("button", { name: "删除选中页" })).toHaveCount(0);
  await expect(page.getByText("高级与导出")).toHaveCount(0);
  const status = page.locator(".status-line");
  const statusBox = await status.boundingBox();
  expect(statusBox).not.toBeNull();
  expect(statusBox!.y).toBeLessThan(480);
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
});
