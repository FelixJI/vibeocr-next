import { readyImageRecognitionModes } from "./workbench-host";
import { expect, test } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

/** 普通编辑页主排（图片编辑工具）在窄面板下的真实布局契约：
 *  1) 按钮不收缩：中文标签单行显示（不逐字竖排），高度不超过单行按钮；
 *  2) 图标实际渲染 24×24，不被压扁；
 *  3) 主排单行且真实水平滚动：滚到末尾后最后按钮可达、仍在容器内；
 *     键盘聚焦末尾按钮同样可达。 */

test("窄面板：主排按钮单行文字、图标24px、单行滚动到末尾可达", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1024, height: 800 });
  await page.route("**/main-row-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60"><rect width="120" height="60" fill="#d5e5f4"/></svg>',
    }),
  );
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: [
        "recognition.annotation",
        "recognition.screenshotSession",
        "recognition.results",
      ],
      features: {
        imageEdit: {
          recognitionModes: readyImageRecognitionModes,
          isBusy: false,
          statusCode: "recognition.session",
          input: {
            url: "/main-row-source.svg",
            mediaType: "image/png",
            byteLength: 2048,
          },
          screenshotSession: {
            sessionId: "main-row-e2e",
            revision: 0,
            textSelectionRequested: false,
            sceneEditing: false,
            excludeBoxes: [],
          },
        },
      },
    },
    "/",
  );

  const main = page.getByRole("toolbar", { name: "图片编辑工具" });
  await expect(main).toBeVisible();

  // 1) 单行按钮契约：任何按钮不因逐字折行膨胀（高度 ≤ 44px），宽度 ≥ 图标宽。
  const squashed = await main.locator("button").evaluateAll((buttons) =>
    buttons
      .filter((button) => button.clientWidth > 0)
      .filter(
        (button) =>
          button.getBoundingClientRect().height > 44 ||
          button.getBoundingClientRect().width < 24,
      )
      .map((button) => ({
        label: button.getAttribute("aria-label") ?? button.textContent,
        height: button.getBoundingClientRect().height,
      })),
  );
  expect(squashed).toEqual([]);

  // 中文标签按钮（识别当前图）内容单行：按钮高度与短标签按钮一致量级。
  const recognize = main.getByRole("button", { name: "识别文字" });
  const recognizeHeight = await recognize.evaluate(
    (element) => element.getBoundingClientRect().height,
  );
  expect(recognizeHeight).toBeLessThanOrEqual(44);

  // 2) 图标真实渲染 24×24（宽高都不被压缩）。
  const iconSizes = await main.locator("button svg").evaluateAll((svgs) =>
    svgs.map((svg) => {
      const box = svg.getBoundingClientRect();
      return {
        width: Math.round(box.width * 10) / 10,
        height: Math.round(box.height * 10) / 10,
      };
    }),
  );
  expect(iconSizes.length).toBeGreaterThan(10);
  for (const size of iconSizes) {
    expect(size).toEqual({ width: 24, height: 24 });
  }

  // 3) 主排单行（按钮中心 y 唯一）且真实水平滚动。
  const rows = await main.locator("button").evaluateAll(
    (buttons) =>
      new Set(
        buttons.map((button) => {
          const box = button.getBoundingClientRect();
          return Math.round(box.top + box.height / 2);
        }),
      ).size,
  );
  expect(rows).toBe(1);
  const scrollable = await main.evaluate((element: HTMLElement) => ({
    scrollWidth: element.scrollWidth,
    clientWidth: element.clientWidth,
  }));
  expect(scrollable.scrollWidth).toBeGreaterThan(scrollable.clientWidth);

  // 滚到末尾：最后按钮（清除编辑）可达且仍在容器盒内。
  const lastReachable = await main.evaluate((element: HTMLElement) => {
    element.scrollLeft = element.scrollWidth;
    const box = element.getBoundingClientRect();
    const buttons = Array.from(element.querySelectorAll("button"));
    const last = buttons[buttons.length - 1]!;
    const button = last.getBoundingClientRect();
    return {
      label: last.getAttribute("aria-label"),
      right: button.right,
      containerRight: box.right,
      width: button.width,
    };
  });
  expect(lastReachable.label).toBe("清除编辑");
  expect(lastReachable.width).toBeGreaterThan(0);
  expect(lastReachable.right).toBeLessThanOrEqual(
    lastReachable.containerRight + 0.5,
  );

  // 键盘可达：聚焦末尾按钮时容器自动滚动，按钮保持在容器内。
  await recognize.focus();
  await page.keyboard.press("Tab");
  const focusVisible = await page.evaluate(() => {
    const element = document.activeElement as HTMLElement | null;
    const container = document.querySelector<HTMLElement>(
      ".editor-toolbar-main",
    );
    if (!element || !container) return { ok: false };
    const box = element.getBoundingClientRect();
    const c = container.getBoundingClientRect();
    return { ok: box.right <= c.right + 0.5 && box.width > 0 };
  });
  expect(focusVisible.ok).toBe(true);
});
