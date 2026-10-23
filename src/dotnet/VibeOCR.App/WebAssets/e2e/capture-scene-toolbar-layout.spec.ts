import { expect, test, type Page } from "@playwright/test";

import { expectCommand, mountHost } from "./workbench-host";

/** 截图现场两排工具栏的真实浏览器布局契约（最终方案）：
 *  1) 主排（图片编辑工具）承载全部绘图工具与操作（含取字/修补上下文
 *     操作、复制/保存/识别/撤销等），始终单行；过宽时容器内水平滚动
 *     保持可达，不把工具/操作挪去下排；
 *  2) 下排（当前工具设置）仅当前工具的颜色/线宽/字号/强度等参数与
 *     自动取字偏好，无属性时收起，且位于主排下方；
 *  3) 窄视口（360/640）：页面不横向溢出，主排可滚动到末尾按钮，
 *     下排可见且可交互（点色板、线宽选择器出现）。
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
      body: `<svg xmlns="http://www.w3.org/2000/svg" width="${geometry?.width ?? 900}" height="${geometry?.height ?? 400}"><rect width="100%" height="100%" fill="#d5e5f4"/></svg>`,
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
  // initialTool=rectangle：主排与属性下排（当前工具设置）同时就绪。
  await expect(
    page.getByRole("toolbar", { name: "当前工具设置" }),
  ).toBeVisible();
}

/** 主排内按钮的视觉行数（按按钮中心 y 去重；滚动不改变单行）。 */
async function mainRowCount(page: Page): Promise<number> {
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

/** 主排滚动到最右后，末尾按钮是否可达（仍在工具栏盒内可见）。 */
async function mainRowScrollableToEnd(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const toolbar = document.querySelector<HTMLElement>(".editor-toolbar-main");
    if (!toolbar) return false;
    toolbar.scrollLeft = toolbar.scrollWidth;
    const box = toolbar.getBoundingClientRect();
    const buttons = Array.from(toolbar.querySelectorAll("button"));
    const last = buttons[buttons.length - 1];
    if (!last) return false;
    const button = last.getBoundingClientRect();
    return button.right <= box.right + 0.5 && button.width > 0;
  });
}

/** 工具栏盒自身是否越出视口（溢出应被容器滚动承接，不撑破页面）。 */
async function toolbarWithinViewport(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const toolbars = Array.from(
      document.querySelectorAll<HTMLElement>(
        ".editor-toolbar-stack .editor-toolbar",
      ),
    );
    return toolbars.every((toolbar) => {
      const box = toolbar.getBoundingClientRect();
      return box.right <= window.innerWidth + 0.5 && box.left >= -0.5;
    });
  });
}

test("宽视口：主排（工具+操作）单行、位于属性下排上方，页面不横向溢出", async ({
  page,
}) => {
  await mountScene(page, 1280, 720);
  const main = page.getByRole("toolbar", { name: "图片编辑工具" });
  const settings = page.getByRole("toolbar", { name: "当前工具设置" });
  await expect(main).toBeVisible();
  await expect(settings).toBeVisible();
  const mainTop = await main.evaluate(
    (element: HTMLElement) => element.getBoundingClientRect().top,
  );
  const settingsTop = await settings.evaluate(
    (element: HTMLElement) => element.getBoundingClientRect().top,
  );
  // 主排在上、属性下排在下。
  expect(mainTop).toBeLessThan(settingsTop);
  // 主排全部按钮单行（工具+操作同排，不折行）。
  const mainWidth = await main.evaluate(
    (element: HTMLElement) => element.getBoundingClientRect().width,
  );
  test.info().annotations.push({
    type: "main-row-width",
    description: `${mainWidth.toFixed(1)}px`,
  });
  expect(await mainRowCount(page)).toBe(1);
  // 页面与工具栏盒均不横向溢出。
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  expect(await toolbarWithinViewport(page)).toBe(true);
});

for (const width of [640, 360]) {
  test(`窄视口 ${width}px：主排单行可滚动到末尾，下排属性可见可交互`, async ({
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
    expect(await toolbarWithinViewport(page)).toBe(true);
    // 主排保持单行，且滚动到最右后末尾按钮（更多）可达。
    expect(await mainRowCount(page)).toBe(1);
    expect(await mainRowScrollableToEnd(page)).toBe(true);
    // 下排可见且可交互：点选常用色；线宽选择器在属性排且不出视口。
    const settings = page.getByRole("toolbar", { name: "当前工具设置" });
    await expect(
      settings.getByRole("group", { name: "标注颜色" }),
    ).toBeVisible();
    await page.getByRole("button", { name: "常用颜色 #1f6feb" }).click();
    const widthSelect = settings.getByLabel("线条宽度");
    await expect(widthSelect).toBeVisible();
    const selectBox = await widthSelect.boundingBox();
    expect(selectBox!.x + selectBox!.width).toBeLessThanOrEqual(width + 0.5);
  });
}

test("无属性工具收起下排；切换工具后属性排重新出现", async ({ page }) => {
  await mountScene(page, 1280, 720);
  // 选择工具无属性：下排收起，只剩主排。
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await expect(
    page.getByRole("toolbar", { name: "当前工具设置" }),
  ).toBeHidden();
  // 切回绘图工具：颜色/线宽属性排重新出现。
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  const settings = page.getByRole("toolbar", { name: "当前工具设置" });
  await expect(settings.getByRole("group", { name: "标注颜色" })).toBeVisible();
  await expect(settings.getByLabel("线条宽度")).toBeVisible();
});

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

test("选区覆盖工具栏时前景有实色背景，颜色面板在窄屏底部仍可操作", async ({
  page,
}) => {
  await mountScene(page, 360, 480, { x: 0, y: 0, width: 2560, height: 1440 });
  const stack = page.locator(".editor-toolbar-stack");
  await expect(stack).toHaveCSS("background-color", "rgb(255, 255, 255)");
  await page.getByRole("button", { name: "自定义颜色", exact: true }).click();
  const popup = page.getByRole("dialog", { name: "自定义颜色", exact: true });
  await expect(popup).toBeVisible();
  const box = (await popup.boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(360);
  expect(box.y + box.height).toBeLessThanOrEqual(480);
  await popup.getByLabel("十六进制颜色").fill("#123456");
  await expect(
    popup.getByRole("slider", { name: "红", exact: true }),
  ).toHaveValue("18");
  await page.screenshot({
    path: test.info().outputPath("capture-color-panel.png"),
  });
  await page.keyboard.press("Escape");
  await expect(popup).toBeHidden();
  await expect(
    page.getByRole("button", { name: "矩形", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
});

test("工具栏焦点的 Esc 先退工具，再退出现场", async ({ page }) => {
  await mountScene(page, 1280, 720);
  await page.getByLabel("线条宽度").focus();
  await page.keyboard.press("Escape");
  await expect(
    page.getByRole("button", { name: "矩形", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByLabel("图片检查画布")).toBeFocused();
  await page.getByRole("button", { name: "矩形", exact: true }).focus();
  await page.keyboard.press("Escape");
  await expect(
    page.getByRole("button", { name: "选择", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await page.keyboard.press("Escape");
  await expectCommand(page, {
    scope: "recognition",
    action: "closeScreenshotSession",
    arguments: {},
  });
});

test("空白区域右键先退工具再退出现场", async ({ page }) => {
  await mountScene(page, 1280, 720);
  await page.mouse.click(10, 10, { button: "right" });
  await expect(
    page.getByRole("button", { name: "选择", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await page.mouse.click(10, 10, { button: "right" });
  await expectCommand(page, {
    scope: "recognition",
    action: "closeScreenshotSession",
    arguments: {},
  });
});

for (const [action, format] of [
  ["复制标注图", "image/png"],
  ["保存标注图", "image/png"],
  ["保存标注图", "image/jpeg"],
] as const) {
  test(`圆角截图 ${action} ${format} 导出正确角落并支持撤销`, async ({
    page,
  }) => {
    await mountScene(page, 1280, 720);
    const uploads: Buffer[] = [];
    await page.route("**/__annotation", (route) => {
      const bytes = route.request().postDataBuffer();
      if (bytes) uploads.push(bytes);
      return route.fulfill({
        status: 201,
        contentType: "application/json",
        body: JSON.stringify({
          resourceUri:
            "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        }),
      });
    });
    await page
      .getByRole("combobox", { name: "截图圆角", exact: true })
      .selectOption("40");
    await expect(page.getByLabel("图片视口")).not.toHaveCSS(
      "border-top-left-radius",
      "0px",
    );
    await page.getByRole("button", { name: "撤销", exact: true }).click();
    await expect(
      page.getByRole("combobox", { name: "截图圆角", exact: true }),
    ).toHaveValue("0");
    await page.getByRole("button", { name: "重做", exact: true }).click();
    if (format === "image/jpeg") {
      await page.locator(".editor-output-settings > summary").click();
      await page.getByLabel("输出格式").selectOption(format);
      await page.locator(".editor-output-settings > summary").click();
    }
    await page.getByRole("button", { name: action, exact: true }).click();
    await expect.poll(() => uploads.length).toBe(1);
    const samples = await page.evaluate(async (base64) => {
      const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
      const image = await createImageBitmap(new Blob([bytes]));
      const canvas = document.createElement("canvas");
      canvas.width = image.width;
      canvas.height = image.height;
      const context = canvas.getContext("2d")!;
      context.drawImage(image, 0, 0);
      image.close();
      return {
        width: canvas.width,
        height: canvas.height,
        corner: Array.from(context.getImageData(0, 0, 1, 1).data),
        center: Array.from(
          context.getImageData(canvas.width / 2, canvas.height / 2, 1, 1).data,
        ),
      };
    }, uploads[0]!.toString("base64"));
    expect(samples.width).toBe(900);
    expect(samples.height).toBe(400);
    if (format === "image/png") expect(samples.corner[3]).toBe(0);
    else {
      expect(samples.corner[3]).toBe(255);
      for (const channel of samples.corner.slice(0, 3))
        expect(channel).toBeGreaterThanOrEqual(250);
    }
    for (const [index, value] of [213, 229, 244, 255].entries())
      expect(Math.abs(samples.center[index]! - value)).toBeLessThanOrEqual(3);
  });
}
