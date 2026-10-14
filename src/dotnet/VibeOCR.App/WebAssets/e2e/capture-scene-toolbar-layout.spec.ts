import { expect, test, type Page } from "@playwright/test";

import { mountHost } from "./workbench-host";

/** 截图现场两排工具栏的真实浏览器布局契约：
 *  1) 宽视口：第一排主操作单行且不超过 880px 工具宽度上限，页面无横向溢出；
 *  2) 窄视口：两排各自换行收纳，覆盖层与所有按钮都不横向溢出视口；
 *     第二排可见且可交互（点色板、切工具后线宽出现）。
 *  只锁定几何契约，不做视觉基线（基线由主代理维护）。 */

const sceneGeometry = {
  surfaceId: "surface",
  captureSessionId: "toolbar-layout",
  inputUrl: "/capture-scene.svg",
  inputRevision: 0,
  documentGeneration: 1,
  bootstrapGeneration: 1,
  initialTool: "rectangle",
  x: 300,
  y: 200,
  width: 900,
  height: 400,
  desktopWidth: 2560,
  desktopHeight: 1440,
};

async function mountScene(
  page: Page,
  width: number,
  height: number,
  geometry?: Partial<typeof sceneGeometry>,
) {
  await page.setViewportSize({ width, height });
  await page.route("**/__capture_background", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="2560" height="1440"><rect width="2560" height="1440" fill="#e9eef4"/></svg>',
    }),
  );
  await page.route("**/capture-scene.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="400"><rect width="900" height="400" fill="white"/><rect x="40" y="140" width="500" height="180" fill="#d5e5f4"/></svg>',
    }),
  );
  await page.route("**/__annotation", (route) =>
    route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      }),
    }),
  );
  await mountHost(
    page,
    {
      sessionId: "toolbar-layout-e2e",
      captureScene: { ...sceneGeometry, ...geometry },
      revision: 1,
      route: "imageEdit",
      theme: "light",
      capabilities: ["recognition.annotation", "recognition.screenshotSession"],
      features: {
        recognition: {
          isBusy: false,
          statusCode: "recognition.session",
          input: {
            url: "/capture-scene.svg",
            mediaType: "image/png",
            byteLength: 4096,
          },
          screenshotSession: {
            sessionId: "toolbar-layout",
            revision: 0,
            sceneEditing: true,
          },
        },
      },
    },
    "/#/imageEdit?scene=1",
  );
  await expect(
    page.getByRole("toolbar", { name: "标注样式与辅助操作" }),
  ).toBeVisible();
}

/** 主操作排内按钮的视觉行数（按按钮中心 y 去重）。 */
async function primaryRowCount(page: Page): Promise<number> {
  return page
    .getByRole("toolbar", { name: "图片编辑工具" })
    .locator("button")
    .evaluateAll(
      (buttons) =>
        new Set(
          buttons.map((button) => {
            const box = button.getBoundingClientRect();
            return Math.round(box.top + box.height / 2);
          }),
        ).size,
    );
}

/** 两排工具栏按钮中横向超出视口的名字（应为空）。 */
async function horizontallyOffscreenButtons(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    Array.from(
      document.querySelectorAll<HTMLElement>(".editor-scene-toolstack button"),
    )
      .filter((button) => {
        const box = button.getBoundingClientRect();
        return (
          box.width > 0 &&
          (box.right > window.innerWidth + 0.5 || box.left < -0.5)
        );
      })
      .map((button) => button.getAttribute("aria-label") ?? button.textContent),
  );
}

test("宽视口：第一排主操作单行、宽度不超过 880px 且页面不横向溢出", async ({
  page,
}) => {
  await mountScene(page, 1280, 720);
  const primary = page.getByRole("toolbar", { name: "图片编辑工具" });
  await expect(primary).toBeVisible();
  expect(await primaryRowCount(page)).toBe(1);
  const primaryWidth = await primary.evaluate(
    (element: HTMLElement) => element.getBoundingClientRect().width,
  );
  test.info().annotations.push({
    type: "primary-row-width",
    description: `${primaryWidth.toFixed(1)}px`,
  });
  // 880px 是截图现场工具区的既有宽度上限（--capture-tools-left 公式）。
  expect(primaryWidth).toBeLessThanOrEqual(880);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  expect(await horizontallyOffscreenButtons(page)).toEqual([]);
});

for (const width of [640, 360]) {
  test(`窄视口 ${width}px：两排换行收纳不横向溢出，第二排可见可达`, async ({
    page,
  }) => {
    await mountScene(page, width, 720);
    // 覆盖层自身不超出视口右缘。
    const overlay = page.locator(".capture-scene-editor");
    const box = await overlay.boundingBox();
    expect(box).not.toBeNull();
    test.info().annotations.push({
      type: `overlay-${width}`,
      description: `x=${box!.x.toFixed(1)} width=${box!.width.toFixed(1)}`,
    });
    expect(box!.x + box!.width).toBeLessThanOrEqual(width + 0.5);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    // 所有按钮不横向出视口（换行收纳而非裁剪）。
    expect(await horizontallyOffscreenButtons(page)).toEqual([]);
    // 第二排可见且可交互：点选常用色 + 工具切换后线宽选择器出现在第二排。
    // 与真实用户一致：覆盖层内部可滚动时先滚动到目标区再点击。
    const secondary = page.getByRole("toolbar", {
      name: "标注样式与辅助操作",
    });
    await expect(
      secondary.getByRole("group", { name: "标注颜色" }),
    ).toBeVisible();
    await page.getByRole("button", { name: "常用颜色 #1f6feb" }).click();
    await overlay.evaluate((element) => {
      element.scrollTop = 0;
    });
    await page.getByRole("button", { name: "矩形", exact: true }).click();
    const widthSelect = secondary.getByLabel("线条宽度");
    await expect(widthSelect).toBeVisible();
    const selectBox = await widthSelect.boundingBox();
    expect(selectBox!.x + selectBox!.width).toBeLessThanOrEqual(width + 0.5);
  });
}

test("输出设置可达：正常位置展开、操作格式/尺寸输入与色板、Tab 可达", async ({
  page,
}) => {
  await mountScene(page, 1280, 720);
  // 色板：真实可点击（不 force），选中后仍在视口内。
  const swatch = page.getByRole("button", { name: "常用颜色 #1f6feb" });
  await swatch.click();
  // Tab 可达：从首个工具按钮出发，焦点能推进到下一个工具按钮。
  await page.getByRole("button", { name: "选择", exact: true }).focus();
  await page.keyboard.press("Tab");
  const focused = await page.evaluate(() => ({
    label: document.activeElement?.getAttribute("aria-label"),
    tag: document.activeElement?.tagName,
  }));
  expect(focused.tag).toBe("BUTTON");
  expect(focused.label).not.toBe("选择");
  // 展开 settings：面板在视口内，格式切换、尺寸输入均可操作。
  await page.locator(".editor-output-settings > summary").click();
  const panel = page.getByRole("toolbar", { name: "输出设置" });
  await expect(panel).toBeVisible();
  const panelBox = await panel.boundingBox();
  expect(panelBox!.y).toBeGreaterThanOrEqual(0);
  expect(panelBox!.y + panelBox!.height).toBeLessThanOrEqual(720 + 0.5);
  await panel.getByLabel("输出格式").selectOption("image/jpeg");
  await expect(panel.getByLabel("JPEG 质量")).toBeVisible();
  await panel.getByLabel("JPEG 质量").selectOption("0.95");
  await panel.getByLabel("输出宽度").fill("640");
  await panel.getByRole("button", { name: "应用尺寸" }).click();
  await expect(
    panel.getByRole("button", { name: "检查文件大小" }),
  ).toBeEnabled();
});

test("大选区（y=0、高接近视口）时工具栏钉底，输出设置翻转上方仍完整可达", async ({
  page,
}) => {
  // 桌面 y=0/height=1360 → 视口 720 下选区高 680：下方放不下，工具栏
  // 回退钉底（720-96=624），设置面板翻转到覆盖层上方且在视口内。
  await mountScene(page, 1280, 720, { y: 0, height: 1360 });
  const overlay = page.locator(".capture-scene-editor");
  const overlayBox = await overlay.boundingBox();
  expect(Math.abs(overlayBox!.y - 624)).toBeLessThanOrEqual(1);
  await page.locator(".editor-output-settings > summary").click();
  const panel = page.getByRole("toolbar", { name: "输出设置" });
  await expect(panel).toBeVisible();
  const panelBox = await panel.boundingBox();
  expect(panelBox!.y).toBeGreaterThanOrEqual(0);
  expect(panelBox!.y + panelBox!.height).toBeLessThanOrEqual(720 + 0.5);
  // 实际操作：切换 JPEG 出现质量选择并可修改。
  await panel.getByLabel("输出格式").selectOption("image/jpeg");
  await panel.getByLabel("JPEG 质量").selectOption("0.85");
  await expect(panel.getByLabel("JPEG 质量")).toHaveValue("0.85");
});
