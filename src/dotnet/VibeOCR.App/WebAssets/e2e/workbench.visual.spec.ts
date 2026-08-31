import { expect, test, type Page } from "@playwright/test";

interface VisualState {
  readonly connected: boolean;
  readonly revision: number;
  readonly route: string;
  readonly theme: "system" | "light" | "dark";
  readonly capabilities: readonly string[];
  readonly features: Readonly<Record<string, unknown>>;
  readonly runtimeLabel: string;
}

async function mount(page: Page, state: VisualState): Promise<void> {
  await page.addInitScript((value) => {
    window.__VIBEOCR_VISUAL_STATE__ = value;
  }, state);
  await page.goto(`/#/${state.route}`);
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
}

test("1280x800 light recognition workspace", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mount(page, {
    connected: true,
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
    runtimeLabel: "Runtime 就绪 · GPU",
  });
  await expect(page).toHaveScreenshot("recognition-light-1280x800.png", {
    fullPage: true,
  });
});

test("1280x800 light annotation workspace with guidance", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mount(page, {
    connected: true,
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
    runtimeLabel: "Runtime 就绪 · GPU",
  });
  await expect(
    page.getByRole("toolbar", { name: "图片编辑工具" }),
  ).toBeVisible();
  await expect(
    page.getByText(/马赛克与模糊会写入复制、保存的图片副本/),
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
    connected: true,
    revision: 3,
    route: "recognition",
    theme: "light",
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/annotation-source.svg",
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
    runtimeLabel: "Runtime 就绪 · GPU",
  });

  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  await expect(canvas).toBeVisible();
  await canvas.evaluate((element) => {
    element.setPointerCapture = () => undefined;
  });
  const bounds = await canvas.boundingBox();
  expect(bounds).not.toBeNull();
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
    connected: true,
    revision: 4,
    route: "batch",
    theme: "dark",
    capabilities: ["batch.add", "batch.run", "batch.export"],
    features: {
      batch: {
        isBusy: true,
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
          { id: "c", name: "表格照片.jpg", statusCode: "batch.item.pending" },
        ],
      },
    },
    runtimeLabel: "正在处理 · 2 / 3",
  });
  await expect(page).toHaveScreenshot("batch-dark-1024x720.png", {
    fullPage: true,
  });
});

test("1280x800 light PDF review workspace", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mount(page, {
    connected: true,
    revision: 7,
    route: "pdf",
    theme: "light",
    capabilities: ["pdf.open", "pdf.rotate", "pdf.edit", "pdf.save"],
    features: {
      pdf: {
        isBusy: false,
        statusCode: "pdf.open",
        pageCount: 4,
        selectedPage: 1,
        selectedPages: [1],
        windowStart: 0,
        pages: [
          { index: 0, statusCode: "pdf.page.done" },
          { index: 1, statusCode: "pdf.page.done" },
          { index: 2, statusCode: "pdf.page.none" },
          { index: 3, statusCode: "pdf.page.none" },
        ],
      },
    },
    runtimeLabel: "Runtime 就绪 · CPU",
  });
  await expect(page).toHaveScreenshot("pdf-light-1280x800.png", {
    fullPage: true,
  });
});
