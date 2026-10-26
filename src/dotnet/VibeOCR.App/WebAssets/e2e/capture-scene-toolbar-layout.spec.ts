import { expect, test, type Page } from "@playwright/test";
import { recordAnnotationUploads } from "./annotation-uploads";

import {
  expectCommand,
  mountHost,
  readyImageRecognitionModes,
  sendState,
} from "./workbench-host";

/** 工具/操作单排同列（与原生 ScreenRegionPicker 对齐），内容高度决定浮层位置；
 * 窄屏与高 DPI 靠容器内水平滚动保持动作可达。 */

test.beforeEach(async ({ page }) => {
  await page.route("**/__annotation/*", (route) =>
    route.fulfill({ status: 204 }),
  );
});

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
  ready = true,
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
          recognitionModes: ready ? readyImageRecognitionModes : [],
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
  if (geometry?.initialTool !== "select")
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
    // 截图现场：滚动区是主排内的工具+次要动作区；普通页是整条主排。
    const toolbar =
      document.querySelector<HTMLElement>(".editor-scene-scroll") ??
      document.querySelector<HTMLElement>(".editor-toolbar-main");
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

test("宽视口：工具与操作单排同列，属性下排位于背景内", async ({ page }) => {
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
  // 按钮尺寸与原生 ScreenRegionPicker token 对齐（高 32/字号 14/圆角 6）。
  const token = await main
    .getByRole("button", { name: "矩形", exact: true })
    .evaluate((element) => {
      const style = getComputedStyle(element);
      return {
        height: Math.round(element.getBoundingClientRect().height),
        fontSize: style.fontSize,
        radius: style.borderRadius,
      };
    });
  expect(token.height).toBeGreaterThanOrEqual(30);
  expect(token.height).toBeLessThanOrEqual(34);
  expect(token.fontSize).toBe("14px");
  // 核心动作可见性：primary 按钮不得白字配透明/白底（root 实测回归）。
  const blankCoreButtons = await page
    .locator(".editor-scene-core")
    .evaluate((core) =>
      Array.from(core.querySelectorAll("button"))
        .filter((button) => {
          const style = getComputedStyle(button);
          return (
            style.color === "rgb(255, 255, 255)" &&
            (style.backgroundColor === "rgba(0, 0, 0, 0)" ||
              style.backgroundColor === "rgb(255, 255, 255)")
          );
        })
        .map((button) => button.getAttribute("aria-label")),
    );
  expect(blankCoreButtons).toEqual([]);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  expect(await toolbarWithinViewport(page)).toBe(true);
  await expectContentsContained(page);
  await page.screenshot({
    path: test.info().outputPath("capture-toolbar-ready.png"),
  });
});

for (const width of [640, 360]) {
  test(`窄视口 ${width}px：工具和动作保持单排，识别与下排属性可见可交互`, async ({
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
    await expectContentsContained(page);
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
    await page.screenshot({
      path: test.info().outputPath(`capture-toolbar-${width}.png`),
    });
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

test("导出参数内联主排可达：格式/JPEG 质量可直接操作、Tab 可达", async ({
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
  // 无独立设置入口：格式/质量内联主排，切换 JPEG 后质量选择出现并可修改。
  await expect(page.locator(".editor-output-settings")).toHaveCount(0);
  await page.getByLabel("输出格式").selectOption("image/jpeg");
  await expect(page.getByLabel("JPEG 质量")).toBeVisible();
  await page.getByLabel("JPEG 质量").selectOption("0.95");
  await expect(page.getByLabel("JPEG 质量")).toHaveValue("0.95");
  await page.getByLabel("输出格式").selectOption("image/png");
  await expect(page.getByLabel("JPEG 质量")).toHaveCount(0);
});

test("大选区（y=0、高接近视口）时工具栏钉底，内联导出参数仍完整可达", async ({
  page,
}) => {
  // 桌面 y=0/height=1360 → 视口 720 下选区高 680：下方放不下单排工具栏，
  // 回退钉底（720-64=656），内联格式/质量仍可操作且在视口内。
  await mountScene(page, 1280, 720, { y: 0, height: 1360 });
  const overlay = page.locator(".capture-scene-editor");
  const overlayBox = await overlay.boundingBox();
  expect(overlayBox!.y).toBeGreaterThanOrEqual(0);
  expect(overlayBox!.y + overlayBox!.height).toBeLessThanOrEqual(720);
  const format = page.getByLabel("输出格式");
  const formatBox = await format.boundingBox();
  expect(formatBox!.x).toBeGreaterThanOrEqual(0);
  expect(formatBox!.x + formatBox!.width).toBeLessThanOrEqual(1280);
  // 实际操作：切换 JPEG 出现质量选择并可修改。
  await format.selectOption("image/jpeg");
  await page.getByLabel("JPEG 质量").selectOption("0.85");
  await expect(page.getByLabel("JPEG 质量")).toHaveValue("0.85");
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
    const uploads = await recordAnnotationUploads(page);
    await page.getByRole("button", { name: "截图直角，点击切换圆角" }).click();
    await expect(page.getByLabel("图片视口")).not.toHaveCSS(
      "border-top-left-radius",
      "0px",
    );
    await page.getByRole("button", { name: "撤销", exact: true }).click();
    await expect(
      page.getByRole("button", { name: "截图直角，点击切换圆角" }),
    ).toHaveAttribute("aria-pressed", "false");
    await page.getByRole("button", { name: "重做", exact: true }).click();
    if (format === "image/jpeg") {
      await page.getByLabel("输出格式").selectOption(format);
    }
    await page.getByRole("button", { name: action, exact: true }).click();
    const exported = await uploads.submitted(
      action === "复制标注图" ? "copyScreenshotImage" : "saveScreenshotImage",
    );
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
    }, exported.body.toString("base64"));
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

async function expectContentsContained(page: Page) {
  await expect(
    page.getByRole("button", { name: "识别文字", exact: true }),
  ).toBeInViewport();
  expect(
    await page.locator(".capture-scene-editor").evaluate((editor) => {
      const box = editor.getBoundingClientRect();
      return [
        ...editor.querySelectorAll<HTMLElement>(
          ".editor-toolbar-stack button, .editor-toolbar-settings input, .editor-toolbar-settings select",
        ),
      ].every((child) => {
        const rect = child.getBoundingClientRect();
        // 主排单行横向滚动：滚出部分被滚动容器裁剪，按裁剪后的可见部分
        // 判定；未滚出的控件仍须完整落在浮层盒内。
        const scroller = child.closest(
          ".editor-scene-scroll, .editor-toolbar-main",
        );
        const clip = scroller ? scroller.getBoundingClientRect() : null;
        const left = clip ? Math.max(rect.left, clip.left) : rect.left;
        const right = clip ? Math.min(rect.right, clip.right) : rect.right;
        const top = clip ? Math.max(rect.top, clip.top) : rect.top;
        const bottom = clip ? Math.min(rect.bottom, clip.bottom) : rect.bottom;
        if (right - left < 1 || bottom - top < 1) return true;
        return (
          top >= box.top &&
          bottom <= box.bottom + 1 &&
          left >= box.left &&
          right <= box.right + 1
        );
      });
    }),
  ).toBe(true);
}

test("未就绪时保留明确文字识别按钮与可展开原因", async ({ page }) => {
  await mountScene(page, 1280, 720, undefined, false);
  await expect(
    page.getByRole("button", { name: "识别文字", exact: true }),
  ).toBeDisabled();
  await expectContentsContained(page);
  await page.screenshot({
    path: test.info().outputPath("capture-toolbar-unavailable.png"),
  });
  await page.getByText("识别未就绪", { exact: true }).click();
  await expect(page.getByText(/识别目录未就绪/)).toBeVisible();
  await expectContentsContained(page);
});

test.describe("高 DPI", () => {
  test.use({ deviceScaleFactor: 2 });
  test("短屏工具栏随内容高度翻转且所有动作仍可达", async ({ page }) => {
    await mountScene(page, 640, 480, { y: 0, height: 1360 });
    await expectContentsContained(page);
    const bounds = (await page.locator(".capture-scene-editor").boundingBox())!;
    expect(bounds.y).toBeGreaterThanOrEqual(0);
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(480);
    await page.screenshot({
      path: test.info().outputPath("capture-toolbar-dpi2.png"),
    });
  });
});

test("默认文字可拖选，绘图和对象拖动优先，修订变化立即移除旧层", async ({
  page,
}) => {
  await mountScene(page, 1280, 720, { initialTool: "select" });
  const layerState = (revision: number) => ({
    isBusy: false,
    recognitionModes: readyImageRecognitionModes,
    input: {
      url: "/capture-scene.svg",
      mediaType: "image/png",
      byteLength: 4096,
    },
    screenshotSession: {
      sessionId: "toolbar-layout",
      revision,
      sceneEditing: true,
    },
    textLayer: {
      status: "textlayer.ready",
      binding: { sessionId: "toolbar-layout", revision },
      image: {
        url: "/capture-scene.svg",
        mediaType: "image/png",
        byteLength: 4096,
      },
      lines: [
        { text: "可选文字测试", x1: 100, y1: 100, x2: 500, y2: 200 },
        { text: "另一行文字", x1: 100, y1: 700, x2: 500, y2: 800 },
      ],
    },
  });
  await sendState(page, "recognition", layerState(0));
  const first = page
    .locator(".editor-selectable-text")
    .getByText("可选文字测试", { exact: true });
  await expect(first).toBeVisible();
  await expect.poll(async () => (await first.boundingBox())?.y).toBe(120);
  const line = (await first.boundingBox())!;
  await page.mouse.move(line.x + 1, line.y + line.height / 2);
  await page.mouse.down();
  await page.mouse.move(line.x + line.width - 1, line.y + line.height / 2, {
    steps: 8,
  });
  await page.mouse.up();
  await expect
    .poll(() => page.evaluate(() => document.getSelection()?.toString()))
    .not.toBe("");
  const canvas = page.getByLabel("图片检查画布");
  await expect(canvas).toBeVisible();
  const box = (await canvas.boundingBox())!;
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await expect(page.locator(".image-text-layer")).toHaveCount(0);
  await page.mouse.move(box.x + 40, box.y + 10);
  await page.mouse.down();
  await page.mouse.move(box.x + 260, box.y + 60, { steps: 4 });
  await page.mouse.up();
  await expectCommand(page, {
    scope: "recognition",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: "toolbar-layout", revision: 1 },
  });
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await sendState(page, "recognition", layerState(1));
  await expect(first).toHaveCount(0);
  await expect(page.getByText("另一行文字", { exact: true })).toBeVisible();
  await page.mouse.move(box.x + 80, box.y + 10);
  await page.mouse.down();
  await page.mouse.move(box.x + 100, box.y + 20, { steps: 4 });
  await page.mouse.up();
  await expectCommand(page, {
    scope: "recognition",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: "toolbar-layout", revision: 2 },
  });
  await expect(page.locator(".image-text-layer")).toHaveCount(0);
});

for (const inset of [10, 12]) {
  test(`就绪文字层避让初始裁剪顶边，${inset}px 命中处允许真实拖动`, async ({
    page,
  }) => {
    await mountScene(page, 1280, 720, { initialTool: "select", width: 800 });
    await sendState(page, "recognition", {
      isBusy: false,
      recognitionModes: readyImageRecognitionModes,
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
      textLayer: {
        status: "textlayer.ready",
        binding: { sessionId: "toolbar-layout", revision: 0 },
        image: {
          url: "/capture-scene.svg",
          mediaType: "image/png",
          byteLength: 4096,
        },
        lines: [
          { text: "顶边中央文字", x1: 450, y1: 20, x2: 550, y2: 70 },
          { text: "中部可选文字", x1: 100, y1: 300, x2: 400, y2: 400 },
        ],
      },
    });
    await expect(page.getByText("中部可选文字", { exact: true })).toBeVisible();
    await expect(page.getByText("顶边中央文字", { exact: true })).toHaveCount(
      0,
    );
    const canvas = page.getByLabel("图片检查画布");
    await expect
      .poll(() =>
        canvas.evaluate((element: HTMLCanvasElement) => {
          const data = element
            .getContext("2d")!
            .getImageData(element.width / 2 - 5, 0, 10, 6).data;
          let orange = 0;
          for (let i = 0; i < data.length; i += 4)
            if (
              data[i]! > 180 &&
              data[i + 1]! > 70 &&
              data[i + 1]! < 200 &&
              data[i + 2]! < 100
            )
              orange++;
          return orange;
        }),
      )
      .toBeGreaterThan(0);
    const box = (await canvas.boundingBox())!;
    const logicalHeight = await canvas.evaluate(
      (element: HTMLCanvasElement) => element.height,
    );
    const startY = box.y + (inset * box.height) / logicalHeight;
    await page.mouse.move(box.x + box.width / 2, startY);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2, startY + 30, { steps: 6 });
    await page.mouse.up();
    await expectCommand(page, {
      scope: "recognition",
      action: "notifyScreenshotRevision",
      arguments: { sessionId: "toolbar-layout", revision: 1 },
    });
    await expect(page.locator(".image-text-layer")).toHaveCount(0);
  });
}
