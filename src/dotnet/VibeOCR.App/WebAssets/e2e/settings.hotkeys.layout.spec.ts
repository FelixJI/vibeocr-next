import { expect, test, type Page } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

// AC5 的几何证据：动作区定宽后，同一设置页里“有恢复默认”与“无恢复默认”
// 的快捷操作行，应用/禁用按钮列起点必须一致；窄窗口下动作簇换整行仍不
// 溢出。这里测的是 CSS 布局几何，不替代真实窗口的 DPI 验收。
const settingsSnapshot = {
  ...snapshot,
  route: "settings" as const,
  capabilities: ["settings.hotkeys"],
  features: {
    settings: {
      isBusy: false,
      statusCode: "settings.ready",
      backend: "cpu",
      pendingBackend: "cpu",
      sources: [],
      features: [],
      hotkeyActions: [
        {
          actionId: "screenshot_recognize",
          displayName: "截图识别",
          configuredHotkey: "Ctrl+Alt+Q",
          registeredHotkey: "Ctrl+Alt+Q",
          error: null,
          defaultHotkey: "Ctrl+Alt+Q",
        },
        {
          actionId: "clipboard_recognize",
          displayName: "剪贴板识别",
          configuredHotkey: null,
          registeredHotkey: null,
          error: null,
          defaultHotkey: null,
        },
      ],
    },
  },
};

interface RowGeometry {
  buttonLefts: number[];
  buttonTops: number[];
  clusterRightOverhang: number;
  rowScrollOverflow: number;
}

async function measureRows(page: Page): Promise<RowGeometry[]> {
  return page.evaluate(() => {
    const rows = [
      ...document.querySelectorAll<HTMLElement>(".hotkey-edit-row"),
    ];
    return rows.map((row) => {
      const buttons = [
        ...row.querySelectorAll<HTMLElement>(".setting-actions button"),
      ];
      const rowBox = row.getBoundingClientRect();
      const cluster = row.querySelector<HTMLElement>(".setting-actions")!;
      const clusterBox = cluster.getBoundingClientRect();
      return {
        buttonLefts: buttons.map(
          (button) => button.getBoundingClientRect().x - rowBox.x,
        ),
        buttonTops: buttons.map(
          (button) => button.getBoundingClientRect().y - rowBox.y,
        ),
        clusterRightOverhang: clusterBox.right - rowBox.right,
        rowScrollOverflow: row.scrollWidth - row.clientWidth,
      };
    });
  });
}

test("hotkey rows keep apply and disable columns aligned across rows", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mountHost(page, settingsSnapshot);

  await expect(
    page.getByRole("button", { name: "应用 截图识别" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "恢复默认 剪贴板识别" }),
  ).toHaveCount(0);

  const rows = await measureRows(page);
  expect(rows).toHaveLength(2);
  const [withDefault, withoutDefault] = rows;
  // 两行的应用/禁用按钮列起点一致（亚像素容差 1px）。
  expect(
    Math.abs(withDefault.buttonLefts[0] - withoutDefault.buttonLefts[0]),
  ).toBeLessThanOrEqual(1);
  expect(
    Math.abs(withDefault.buttonLefts[1] - withoutDefault.buttonLefts[1]),
  ).toBeLessThanOrEqual(1);
  // 同一行内三个按钮同一基线：动作簇不换行、不叠加。
  expect(new Set(withDefault.buttonTops).size).toBe(1);
  expect(withDefault.buttonLefts[2]).toBeGreaterThan(
    withDefault.buttonLefts[1],
  );
  // 动作簇完整落在行内，行自身无横向滚动。
  for (const row of rows) {
    expect(row.clusterRightOverhang).toBeLessThanOrEqual(0.5);
    expect(row.rowScrollOverflow).toBeLessThanOrEqual(1);
  }

  const main = page.getByRole("main");
  expect(
    await main.evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    ),
  ).toBeLessThanOrEqual(0);
});

test("hotkey rows stay single-line and overflow-free at narrow widths", async ({
  page,
}) => {
  for (const size of [
    { width: 640, height: 480 },
    { width: 600, height: 400 },
  ]) {
    await page.setViewportSize(size);
    await mountHost(page, settingsSnapshot);
    await expect(
      page.getByRole("button", { name: "应用 截图识别" }),
    ).toBeVisible();

    const rows = await measureRows(page);
    expect(rows).toHaveLength(2);
    for (const row of rows) {
      // 窄窗口下动作簇整行呈现：按钮同一行、不越出行、不产生横向滚动。
      expect(new Set(row.buttonTops).size).toBe(1);
      expect(row.clusterRightOverhang).toBeLessThanOrEqual(0.5);
      expect(row.rowScrollOverflow).toBeLessThanOrEqual(1);
    }
    const main = page.getByRole("main");
    expect(
      await main.evaluate(
        (element) => element.scrollWidth - element.clientWidth,
      ),
      `${size.width}x${size.height}`,
    ).toBeLessThanOrEqual(1);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - window.innerWidth,
      ),
      `${size.width}x${size.height}`,
    ).toBeLessThanOrEqual(0);
  }
});
