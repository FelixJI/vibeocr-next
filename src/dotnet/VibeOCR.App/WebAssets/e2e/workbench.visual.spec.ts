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
