import { expect, test, type Locator, type Page } from "@playwright/test";
import { DEFAULT_EDITOR_PREFERENCES } from "../src/components/editorPreferences";
import {
  deferCommand,
  expectCommand,
  mountHost,
  releaseCommand,
  rejectNextCommand,
  sendState,
  snapshot,
  readyImageRecognitionModes,
} from "./workbench-host";

test.beforeEach(async ({ page }) => {
  await page.route("**/__annotation/*", (route) =>
    route.fulfill({ status: 204 }),
  );
});

const SESSION = "11111111111111111111111111111111";
const ASSET = "22222222222222222222222222222222";
const annotation =
  "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef";

async function png(page: Page, width: number, height: number, bands = false) {
  return Buffer.from(
    await page.evaluate(
      ({ width, height, bands }) => {
        const canvas = document.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext("2d")!;
        if (bands) {
          ctx.fillStyle = "#ff0000";
          ctx.fillRect(0, 0, width / 4, height);
          ctx.fillStyle = "#00ff00";
          ctx.fillRect(width / 4, 0, width / 2, height);
          ctx.fillStyle = "#0000ff";
          ctx.fillRect((width * 3) / 4, 0, width / 4, height);
        } else {
          ctx.fillStyle = "#00ff00";
          ctx.fillRect(width / 2, 0, width / 2, height);
        }
        return canvas.toDataURL("image/png").split(",")[1]!;
      },
      { width, height, bands },
    ),
    "base64",
  );
}
async function pixels(page: Page, url: string) {
  return page.evaluate(async (url) => {
    const blob = new Blob(
      [Uint8Array.from(atob(url.split(",")[1]!), (char) => char.charCodeAt(0))],
      { type: url.slice(5, url.indexOf(";")) },
    );
    const image = await createImageBitmap(blob);
    const canvas = document.createElement("canvas");
    canvas.width = image.width;
    canvas.height = image.height;
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(image, 0, 0);
    image.close();
    return {
      width: canvas.width,
      height: canvas.height,
      data: Array.from(
        ctx.getImageData(0, 0, canvas.width, canvas.height).data,
      ),
    };
  }, url);
}
// 预览采样直接持有当前 DOM 的 <img>：locator.evaluate 原子取元素并等
// decode 就绪，不经 blob URL 字符串往返，避免两次 evaluate 之间预览重渲
// 换 blob 后读到陈旧/已撤销引用。
async function previewPixels(preview: Locator) {
  return preview.evaluate(async (image: HTMLImageElement) => {
    await image.decode();
    const canvas = document.createElement("canvas");
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(image, 0, 0);
    return {
      width: canvas.width,
      height: canvas.height,
      data: Array.from(
        ctx.getImageData(0, 0, canvas.width, canvas.height).data,
      ),
    };
  });
}
function at(image: { width: number; data: number[] }, x: number, y: number) {
  return image.data.slice(
    (y * image.width + x) * 4,
    (y * image.width + x) * 4 + 4,
  );
}
async function commands(page: Page) {
  return page.evaluate(
    () =>
      (
        window as Window & {
          __testHost: {
            commands: {
              scope: string;
              action: string;
              arguments: Record<string, unknown>;
            }[];
          };
        }
      ).__testHost.commands,
  );
}
async function setup(
  page: Page,
  settings: Record<string, unknown> = {},
  size = { width: 80, height: 40 },
) {
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    // 后注册的代理优先匹配；租约释放交还给 beforeEach 的宿主路由。
    if (url.pathname.startsWith("/__annotation/")) return route.fallback();
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });

  const base = await png(page, size.width, size.height, true),
    imported = await png(page, 20, 10);
  const uploads: { contentType: string; bytes: Buffer }[] = [];
  const projects: Record<string, unknown>[] = [];
  await page.route("**/composition-source.png", (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  await page.route("**/composition-asset.png", (route) =>
    route.fulfill({ contentType: "image/png", body: imported }),
  );
  await page.route("**/__annotation", async (route) => {
    uploads.push({
      contentType: route.request().headers()["content-type"] ?? "",
      bytes: route.request().postDataBuffer()!,
    });
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: annotation }),
    });
  });
  await page.route("**/__project", async (route) => {
    projects.push(route.request().postDataJSON() as Record<string, unknown>);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ contributeId: ASSET }),
    });
  });
  const state = {
    isBusy: false,
    recognitionModes: readyImageRecognitionModes,
    statusCode: "recognition.session",
    input: {
      url: "https://app.vibeocr/__resource/composition-source.png",
      mediaType: "image/png",
      byteLength: base.length,
    },
    screenshotSession: { sessionId: SESSION, revision: 0 },
  };
  await page.setViewportSize({ width: 1280, height: 1000 });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.annotation", "recognition.screenshotSession"],
      features: { imageEdit: state, settings },
    },
    "https://app.vibeocr/",
  );
  await expect(
    page.getByText(new RegExp(`原图 ${size.width}×${size.height}`)),
  ).toBeVisible();
  return { uploads, projects, state, imported };
}

// 等待仍在转发的资源代理结束，避免 teardown 提前释放 APIResponse。
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: "wait" });
});

test("排版：中段裁除、背景、边距、圆角、比例、横纵拼接与工程可编辑状态一致", async ({
  page,
}, info) => {
  const { uploads, projects, state, imported } = await setup(page);
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  await page.getByLabel("排版背景", { exact: true }).selectOption("solid");
  await page.getByLabel("背景起色", { exact: true }).fill("#112233");
  await page.getByLabel("中段裁除方向").selectOption("vertical");
  await page.getByLabel("裁除起点", { exact: true }).fill("25");
  await page.getByLabel("裁除终点", { exact: true }).fill("75");
  const preview = page.getByAltText("排版输出预览");
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(60);
  const expected = await previewPixels(preview);
  expect([expected.width, expected.height]).toEqual([60, 60]);
  expect(at(expected, 1, 1)).toEqual([17, 34, 51, 255]);
  expect(at(expected, 15, 20)).toEqual([255, 0, 0, 255]);
  expect(at(expected, 45, 20)).toEqual([0, 0, 255, 255]);
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const exported = await pixels(
    page,
    `data:image/png;base64,${uploads[0]!.bytes.toString("base64")}`,
  );
  expect(exported).toEqual(expected);
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(72);
  await page.getByRole("button", { name: "重做", exact: true }).click();
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(60);
  await page.getByRole("button", { name: "保存工程", exact: true }).click();
  await expect.poll(() => projects.length).toBe(1);
  const saved = projects[0]!.state as {
    composition: { margin: number; cut: unknown };
    marks: unknown[];
  };
  expect(saved.composition.margin).toBe(10);
  expect(saved.composition.cut).toEqual({
    axis: "vertical",
    start: 25,
    end: 75,
  });
  expect(saved.marks).toEqual([]);
  await page.getByLabel("排版固定比例").selectOption("16:9");
  await page.getByLabel("画布圆角", { exact: true }).fill("10");
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(107);
  const rounded = await previewPixels(preview);
  expect(at(rounded, 0, 0)[3]).toBe(0);
  await page.getByLabel("排版阴影", { exact: true }).fill("5");
  await page.getByLabel("排版背景", { exact: true }).selectOption("gradient");
  await page.getByRole("button", { name: "重置排版", exact: true }).click();
  const revision = (await commands(page))
    .filter((command) => command.action === "notifyScreenshotRevision")
    .at(-1)!.arguments.revision;
  await sendState(page, "imageEdit", {
    ...state,
    screenshotSession: { ...state.screenshotSession, revision },
    import: {
      requestId: "composition-import",
      sessionId: SESSION,
      revision,
      items: [
        {
          kind: "image",
          assetId: ASSET,
          resourceUri: "https://app.vibeocr/__resource/composition-asset.png",
          byteLength: imported.length,
          pixelWidth: 20,
          pixelHeight: 10,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: {
      sessionId: SESSION,
      revision,
      requestId: "composition-import",
    },
  });
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  await page.getByLabel("多图拼接", { exact: true }).selectOption("horizontal");
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(120);
  await page.getByLabel("多图拼接", { exact: true }).selectOption("vertical");
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalHeight),
    )
    .toBe(70);
  await page.getByLabel("多图拼接", { exact: true }).selectOption("none");
  await page.getByLabel("排版背景", { exact: true }).selectOption("image");
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(100);
  await expect(page.getByText("背景图已失效，请重新导入")).toHaveCount(0);
  await page.screenshot({
    path: info.outputPath("composition.png"),
    fullPage: true,
  });
});

test("离开图像背景解除绑定：旧背景资产可拼接且删除对象不重置纯色背景", async ({
  page,
}) => {
  const { uploads, state, imported } = await setup(page);
  const preview = () => page.getByAltText("排版输出预览");
  const width = () =>
    preview().evaluate((image: HTMLImageElement) => image.naturalWidth);
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  await expect.poll(width).toBe(100);
  const revision = (await commands(page))
    .filter((command) => command.action === "notifyScreenshotRevision")
    .at(-1)!.arguments.revision;
  await sendState(page, "imageEdit", {
    ...state,
    screenshotSession: { ...state.screenshotSession, revision },
    import: {
      requestId: "background-unbind-import",
      sessionId: SESSION,
      revision,
      items: [
        {
          kind: "image",
          assetId: ASSET,
          resourceUri: "https://app.vibeocr/__resource/composition-asset.png",
          byteLength: imported.length,
          pixelWidth: 20,
          pixelHeight: 10,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: {
      sessionId: SESSION,
      revision,
      requestId: "background-unbind-import",
    },
  });
  // 背景选 image：绑定导入资产后切回 solid，绑定须解除。
  await page.getByLabel("排版背景", { exact: true }).selectOption("image");
  await expect(page.getByLabel("排版背景图", { exact: true })).toBeVisible();
  await expect.poll(width).toBe(100);
  await page.getByLabel("排版背景", { exact: true }).selectOption("solid");
  await page.getByLabel("背景起色", { exact: true }).fill("#112233");
  // 旧背景资产必须仍可拼接（残留绑定会把它从拼接列表排除）。
  await page.getByLabel("多图拼接", { exact: true }).selectOption("horizontal");
  await expect.poll(width).toBe(120);
  // 删除该图片对象：solid 背景不得被重置为 transparent，导出同源。
  // （画布排版是开关，面板在删除前后始终打开。）
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByRole("button", { name: "全选对象", exact: true }).click();
  await page.getByRole("button", { name: "删除对象", exact: true }).click();
  await expect.poll(width).toBe(100);
  const expected = await previewPixels(preview());
  expect(at(expected, 5, 5)).toEqual([17, 34, 51, 255]);
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const exported = await pixels(
    page,
    `data:image/png;base64,${uploads[0]!.bytes.toString("base64")}`,
  );
  expect(exported).toEqual(expected);
});

test("常用预设绑定语义快照，复制失败不关闭，成功 ack 后关闭；工具可发现且不抢输入", async ({
  page,
}, info) => {
  const preferences = {
    ...DEFAULT_EDITOR_PREFERENCES,
    hidden: ["pen"],
    shortcuts: { pen: "p" },
    output: {
      ...DEFAULT_EDITOR_PREFERENCES.output,
      format: "image/jpeg",
      width: 40,
      height: 20,
      closeAfterCopy: true,
    },
  };
  const { uploads } = await setup(page, {
    editorPreferences: preferences,
    imageOutputDirectory: "C:/authorized-output",
  });
  const toolbar = page.getByRole("toolbar", {
    name: "图片编辑工具",
    exact: true,
  });
  await expect(
    toolbar.getByRole("button", { name: "画笔", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "更多操作", exact: true }).click();
  await page.getByRole("menuitem", { name: "画笔", exact: true }).click();
  const canvas = page.getByLabel("图片检查画布", { exact: true });
  await canvas.focus();
  await page.keyboard.press("p");
  await expect(page.getByLabel("线条宽度", { exact: true })).toBeVisible();
  const presetSave = toolbar.getByRole("button", {
    name: "按常用预设保存",
    exact: true,
  });
  await expect(presetSave).toBeEnabled();
  await expect(
    page
      .getByRole("toolbar", { name: "输出设置", exact: true })
      .getByRole("button", { name: "按常用预设保存", exact: true }),
  ).toHaveCount(0);
  await expect(
    page
      .getByRole("toolbar", { name: "当前工具设置", exact: true })
      .getByRole("button", { name: "按常用预设保存", exact: true }),
  ).toHaveCount(0);
  await presetSave.scrollIntoViewIfNeeded();
  expect(
    await presetSave.evaluate((element) =>
      element
        .closest('[role="toolbar"]')
        ?.classList.contains("editor-toolbar-main"),
    ),
  ).toBe(true);
  await page.screenshot({
    path: info.outputPath("preset-primary-row.png"),
    fullPage: true,
  });
  await presetSave.click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/jpeg");
  const image = await pixels(
    page,
    `data:image/jpeg;base64,${uploads[0]!.bytes.toString("base64")}`,
  );
  expect([image.width, image.height]).toEqual([40, 20]);
  await expectCommand(page, {
    scope: "imageEdit",
    action: "savePresetImage",
    arguments: {
      resourceUri: annotation,
      presetId: "default",
      presetBinding: {
        preset: preferences.output,
        directory: "C:/authorized-output",
      },
      sessionId: SESSION,
      revision: 0,
    },
  });
  await rejectNextCommand(page);
  await page.getByRole("button", { name: "复制标注图", exact: true }).click();
  await expect(
    page.getByText("复制未完成，请查看页面提示后重试。", { exact: true }),
  ).toBeVisible();
  expect(
    (await commands(page)).some(
      (command) => command.action === "closeScreenshotSession",
    ),
  ).toBe(false);
  await deferCommand(page, "copyScreenshotImage");
  await page.getByRole("button", { name: "复制标注图", exact: true }).click();
  await expect
    .poll(
      async () =>
        (await commands(page)).filter(
          (command) => command.action === "copyScreenshotImage",
        ).length,
    )
    .toBe(2);
  expect(
    (await commands(page)).some(
      (command) => command.action === "closeScreenshotSession",
    ),
  ).toBe(false);
  await releaseCommand(page);
  await expectCommand(page, {
    scope: "imageEdit",
    action: "closeScreenshotSession",
    arguments: {},
  });
  await page.getByText("输出预设与工具栏定制", { exact: true }).click();
  await page.getByLabel("文字工具快捷键", { exact: true }).fill("p");
  await expect(
    page.getByRole("alert").filter({ hasText: "快捷键冲突" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "保存预设与工具栏", exact: true }),
  ).toBeDisabled();
  await page.getByLabel("文字工具快捷键", { exact: true }).fill("t");
  await page.getByLabel("预设文件名模板", { exact: true }).focus();
  await page.keyboard.press("p");
  await expect(
    toolbar.getByRole("button", { name: "文字", exact: true }),
  ).toHaveAttribute("aria-pressed", "false");
  await page
    .getByRole("button", { name: "重置预设与工具栏", exact: true })
    .click();
  await expectCommand(page, {
    scope: "settings",
    action: "setEditorPreferences",
    arguments: { preferences: DEFAULT_EDITOR_PREFERENCES },
  });
  await page.screenshot({
    path: info.outputPath("preferences.png"),
    fullPage: true,
  });
});

test("批量逐张复用预设，JPEG 白底，部分失败保留成功并保留文本导出", async ({
  page,
}) => {
  const source = await png(page, 80, 40);
  const uploads: Buffer[] = [];
  await page.route("**/batch-preset.png*", (route) =>
    route.fulfill({ contentType: "image/png", body: source }),
  );
  await page.route("**/__annotation", async (route) => {
    uploads.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: annotation }),
    });
  });
  const batch = {
    isRunning: false,
    itemCount: 3,
    completedCount: 0,
    failedCount: 0,
    items: [],
  };
  const output = {
    ...DEFAULT_EDITOR_PREFERENCES.output,
    format: "image/jpeg",
    width: 40,
    height: 20,
  };
  await mountHost(page, {
    ...snapshot,
    route: "batch",
    capabilities: ["batch.export", "batch.add", "batch.run"],
    features: {
      batch,
      settings: {
        editorPreferences: { ...DEFAULT_EDITOR_PREFERENCES, output },
        imageOutputDirectory: "C:/authorized-output",
      },
    },
  });
  await page
    .getByRole("button", { name: "按常用预设批量导出图片", exact: true })
    .click();
  for (const index of [0, 1, 2]) {
    await expectCommand(page, {
      scope: "batch",
      action: "prepareImageExport",
      arguments: { index },
    });
    if (index === 2) {
      await sendState(page, "batch", {
        ...batch,
        exportImageId: "33333333333333333333333333333333",
        exportImageError: "decode_unavailable",
      });
      continue;
    }
    if (index === 1) await rejectNextCommand(page);
    await sendState(page, "batch", {
      ...batch,
      exportImage: {
        url: `/batch-preset.png?item=${index}`,
        mediaType: "image/png",
        byteLength: source.length,
      },
      exportImageId: index === 0 ? SESSION : ASSET,
    });
    await expectCommand(page, {
      scope: "batch",
      action: "exportImage",
      arguments: {
        itemId: index === 0 ? SESSION : ASSET,
        resourceUri: annotation,
        presetId: "default",
        presetBinding: { preset: output, directory: "C:/authorized-output" },
      },
    });
  }
  await expect(
    page.getByText(/输出完成：成功 1，失败\/非图片 2/),
  ).toBeVisible();
  await expect(
    page.getByText(/其中 1 项格式解码不可用或图片内容损坏/),
  ).toBeVisible();
  await expect(
    page.getByText(/动画\/多帧仅取首帧，多页图片仅取第一页/),
  ).toBeVisible();
  expect(uploads.length).toBe(2);
  const image = await pixels(
    page,
    `data:image/jpeg;base64,${uploads[0]!.toString("base64")}`,
  );
  expect([image.width, image.height]).toEqual([40, 20]);
  expect(
    at(image, 2, 2)
      .slice(0, 3)
      .every((channel) => channel >= 250),
  ).toBe(true);
  await expect(
    page.getByText("已成功文件保留", { exact: false }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "导出全部 Markdown" }),
  ).toBeVisible();
});

test("批量同项重试立即分类解码不可用，无关 batch 快照不被消费", async ({
  page,
}) => {
  const source = await png(page, 80, 40);
  const uploads: Buffer[] = [];
  await page.route("**/batch-preset.png*", (route) =>
    route.fulfill({ contentType: "image/png", body: source }),
  );
  await page.route("**/__annotation", async (route) => {
    uploads.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: annotation }),
    });
  });
  const ITEM_A = SESSION,
    ITEM_B = ASSET,
    ITEM_C = "3".repeat(32);
  const batch = {
    isRunning: false,
    itemCount: 3,
    completedCount: 0,
    failedCount: 0,
    items: [],
  };
  const output = {
    ...DEFAULT_EDITOR_PREFERENCES.output,
    format: "image/png",
  };
  await mountHost(page, {
    ...snapshot,
    route: "batch",
    capabilities: ["batch.export", "batch.add", "batch.run"],
    features: {
      batch,
      settings: {
        editorPreferences: { ...DEFAULT_EDITOR_PREFERENCES, output },
        imageOutputDirectory: "C:/authorized-output",
      },
    },
  });
  const button = page.getByRole("button", {
    name: "按常用预设批量导出图片",
    exact: true,
  });
  // 第一轮：item0 成功；等待期间插入一份宿主真实的无关 batch 快照
  // （非 prepare 路径的 BatchState 不携带导出瞬态字段，仅有显式 null），
  // 它不得被消费，exportImage 仍须绑定本次 prepare 结果的 item。
  await button.click();
  await expectCommand(page, {
    scope: "batch",
    action: "prepareImageExport",
    arguments: { index: 0 },
  });
  await sendState(page, "batch", {
    ...batch,
    taskEngine: "rapid_text",
    exportImage: null,
    exportImageId: null,
    exportImageError: null,
  });
  await sendState(page, "batch", {
    ...batch,
    exportImage: {
      url: "/batch-preset.png?item=0",
      mediaType: "image/png",
      byteLength: source.length,
    },
    exportImageId: ITEM_A,
  });
  await expectCommand(page, {
    scope: "batch",
    action: "exportImage",
    arguments: {
      itemId: ITEM_A,
      resourceUri: annotation,
      presetId: "default",
      presetBinding: { preset: output, directory: "C:/authorized-output" },
    },
  });
  // 宿主 exportImage 回执后瞬态字段被清除：等待中的下一项不得消费它。
  await sendState(page, "batch", {
    ...batch,
    exportImage: null,
    exportImageId: null,
    exportImageError: null,
  });
  await expectCommand(page, {
    scope: "batch",
    action: "prepareImageExport",
    arguments: { index: 1 },
  });
  await sendState(page, "batch", {
    ...batch,
    exportImage: {
      url: "/batch-preset.png?item=1",
      mediaType: "image/png",
      byteLength: source.length,
    },
    exportImageId: ITEM_B,
  });
  await expectCommand(page, {
    scope: "batch",
    action: "exportImage",
    arguments: {
      itemId: ITEM_B,
      resourceUri: annotation,
      presetId: "default",
      presetBinding: { preset: output, directory: "C:/authorized-output" },
    },
  });
  await expectCommand(page, {
    scope: "batch",
    action: "prepareImageExport",
    arguments: { index: 2 },
  });
  // 轮末项 decode_unavailable：状态残留 exportImageId/error。
  await sendState(page, "batch", {
    ...batch,
    exportImageId: ITEM_C,
    exportImageError: "decode_unavailable",
  });
  await expect(
    page.getByText(/输出完成：成功 2，失败\/非图片 1/),
  ).toBeVisible();
  await expect(
    page.getByText(/其中 1 项格式解码不可用或图片内容损坏/),
  ).toBeVisible();
  expect(uploads.length).toBe(2);
  // 第二轮：首项为上轮末项（同 id 同 error，仅快照对象更新）——须立即
  // 分类为解码不可用，不得等 15s 超时记成通用失败。
  await button.click();
  await expect
    .poll(
      async () =>
        (await commands(page)).filter(
          (command) => command.action === "prepareImageExport",
        ).length,
    )
    .toBe(4);
  await sendState(page, "batch", {
    ...batch,
    exportImageId: ITEM_C,
    exportImageError: "decode_unavailable",
  });
  await expect(page.getByText(/正在输出图片 2\/3/)).toBeVisible();
  await expect
    .poll(
      async () =>
        (await commands(page)).filter(
          (command) => command.action === "prepareImageExport",
        ).length,
    )
    .toBe(5);
  await sendState(page, "batch", {
    ...batch,
    exportImage: {
      url: "/batch-preset.png?item=r2",
      mediaType: "image/png",
      byteLength: source.length,
    },
    exportImageId: ITEM_A,
  });
  await expect
    .poll(
      async () =>
        (await commands(page)).filter(
          (command) => command.action === "exportImage",
        ).length,
    )
    .toBe(3);
  await sendState(page, "batch", {
    ...batch,
    exportImage: null,
    exportImageId: null,
    exportImageError: null,
  });
  // 第二轮末项：另一项同样解码不可用，须与同项重试一样立即分类。
  await expect
    .poll(
      async () =>
        (await commands(page)).filter(
          (command) => command.action === "prepareImageExport",
        ).length,
    )
    .toBe(6);
  await sendState(page, "batch", {
    ...batch,
    exportImageId: "4".repeat(32),
    exportImageError: "decode_unavailable",
  });
  await expect(
    page.getByText(/输出完成：成功 1，失败\/非图片 2/),
  ).toBeVisible();
  await expect(
    page.getByText(/其中 2 项格式解码不可用或图片内容损坏/),
  ).toBeVisible();
  expect(uploads.length).toBe(3);
  const exports = (await commands(page)).filter(
    (command) => command.action === "exportImage",
  );
  expect(exports.map((command) => command.arguments.itemId)).toEqual([
    ITEM_A,
    ITEM_B,
    ITEM_A,
  ]);
});

test("停止图片导出后上传完成也不写入", async ({ page }) => {
  const source = await png(page, 80, 40);
  await page.route("**/batch-preset.png*", (route) =>
    route.fulfill({ contentType: "image/png", body: source }),
  );
  let releaseUpload!: () => void;
  let uploadStarted = false;
  const gate = new Promise<void>((resolve) => (releaseUpload = resolve));
  await page.route("**/__annotation", async (route) => {
    uploadStarted = true;
    await gate;
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: annotation }),
    });
  });
  const batch = {
    isRunning: false,
    itemCount: 1,
    completedCount: 0,
    failedCount: 0,
    items: [],
  };
  const output = {
    ...DEFAULT_EDITOR_PREFERENCES.output,
    format: "image/png",
  };
  await mountHost(page, {
    ...snapshot,
    route: "batch",
    capabilities: ["batch.export", "batch.add", "batch.run"],
    features: {
      batch,
      settings: {
        editorPreferences: { ...DEFAULT_EDITOR_PREFERENCES, output },
        imageOutputDirectory: "C:/authorized-output",
      },
    },
  });
  await page
    .getByRole("button", { name: "按常用预设批量导出图片", exact: true })
    .click();
  await expectCommand(page, {
    scope: "batch",
    action: "prepareImageExport",
    arguments: { index: 0 },
  });
  await sendState(page, "batch", {
    ...batch,
    exportImage: {
      url: "/batch-preset.png?item=0",
      mediaType: "image/png",
      byteLength: source.length,
    },
    exportImageId: SESSION,
  });
  // 上传已发出且被门挡住：此刻停止，再放行；停止后不得再发 batch.exportImage。
  await expect.poll(() => uploadStarted).toBe(true);
  await page.getByRole("button", { name: "停止图片导出", exact: true }).click();
  const discarded = page.waitForRequest(
    (request) =>
      request.method() === "DELETE" &&
      new URL(request.url()).pathname === new URL(annotation).pathname,
  );
  releaseUpload();
  await discarded;
  await expect(page.getByText(/已停止：成功 0，失败\/非图片 0/)).toBeVisible();
  expect(
    (await commands(page)).filter(
      (command) => command.action === "exportImage",
    ),
  ).toHaveLength(0);
});

test("上传中离开再启动批量导出，旧上传返回不写入也不干扰新运行", async ({
  page,
}) => {
  const source = await png(page, 80, 40);
  await page.route("**/batch-preset.png*", (route) =>
    route.fulfill({ contentType: "image/png", body: source }),
  );
  const releases: (() => void)[] = [];
  const newAnnotation =
    "https://app.vibeocr/__annotation/abcdef0123456789abcdef0123456789";
  await page.route("**/__annotation", async (route) => {
    const index = releases.length;
    await new Promise<void>((resolve) => releases.push(resolve));
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        resourceUri: index === 0 ? annotation : newAnnotation,
      }),
    });
  });
  const batch = {
    isRunning: false,
    itemCount: 1,
    completedCount: 0,
    failedCount: 0,
    items: [],
  };
  await mountHost(page, {
    ...snapshot,
    route: "batch",
    capabilities: ["batch.export", "batch.add", "batch.run"],
    features: {
      batch,
      settings: {
        editorPreferences: DEFAULT_EDITOR_PREFERENCES,
        imageOutputDirectory: "C:/authorized-output",
      },
    },
  });
  const start = page.getByRole("button", {
    name: "按常用预设批量导出图片",
    exact: true,
  });
  const ready = {
    ...batch,
    exportImage: {
      url: "/batch-preset.png",
      mediaType: "image/png",
      byteLength: source.length,
    },
    exportImageId: SESSION,
  };
  await start.click();
  await expectCommand(page, {
    scope: "batch",
    action: "prepareImageExport",
    arguments: { index: 0 },
  });
  await sendState(page, "batch", ready);
  await expect.poll(() => releases.length).toBe(1);
  await page.getByRole("button", { name: "停止图片导出", exact: true }).click();
  await expect(start).toBeDisabled();
  await sendState(page, "shell", { route: "settings" });
  await expect(
    page.getByRole("region", { name: "批量图片预设输出" }),
  ).toHaveCount(0);
  await sendState(page, "shell", { route: "batch" });
  await expect(start).toBeEnabled();
  await start.click();
  await expect
    .poll(
      async () =>
        (await commands(page)).filter(
          (command) => command.action === "prepareImageExport",
        ).length,
    )
    .toBe(2);
  await sendState(page, "batch", ready);
  await expect.poll(() => releases.length).toBe(2);
  const oldResponse = page.waitForResponse("**/__annotation");
  const discarded = page.waitForRequest(
    (request) =>
      request.method() === "DELETE" &&
      new URL(request.url()).pathname === new URL(annotation).pathname,
  );
  releases[0]!();
  await discarded;
  await (await oldResponse).finished();
  releases[1]!();
  await expect(
    page.getByText(/输出完成：成功 1，失败\/非图片 0/),
  ).toBeVisible();
  expect(
    (await commands(page)).filter(
      (command) => command.action === "exportImage",
    ),
  ).toEqual([
    {
      scope: "batch",
      action: "exportImage",
      arguments: {
        itemId: SESSION,
        resourceUri: newAnnotation,
        presetId: "default",
        presetBinding: {
          preset: DEFAULT_EDITOR_PREFERENCES.output,
          directory: "C:/authorized-output",
        },
      },
    },
  ]);
});
test("先拼接后图像背景同步拼接列表，换背景资产互换且离开时插回", async ({
  page,
}) => {
  const { state, imported } = await setup(page);
  const assetB = await png(page, 30, 10);
  const ITEM_B = "5".repeat(32);
  await page.route("**/composition-asset2.png", (route) =>
    route.fulfill({ contentType: "image/png", body: assetB }),
  );
  const preview = page.getByAltText("排版输出预览");
  const width = () =>
    preview.evaluate((image: HTMLImageElement) => image.naturalWidth);
  await sendState(page, "imageEdit", {
    ...state,
    import: {
      requestId: "bg-stitch-import",
      sessionId: SESSION,
      revision: 0,
      items: [
        {
          kind: "image",
          assetId: ASSET,
          resourceUri: "https://app.vibeocr/__resource/composition-asset.png",
          byteLength: imported.length,
          pixelWidth: 20,
          pixelHeight: 10,
        },
        {
          kind: "image",
          assetId: ITEM_B,
          resourceUri: "https://app.vibeocr/__resource/composition-asset2.png",
          byteLength: assetB.length,
          pixelWidth: 30,
          pixelHeight: 10,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: {
      sessionId: SESSION,
      revision: 0,
      requestId: "bg-stitch-import",
    },
  });
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  // 先拼接：两项全部进入（80+20+30+边距20=150）。
  await page.getByLabel("多图拼接", { exact: true }).selectOption("horizontal");
  await expect.poll(width).toBe(150);
  // 后选图像背景：绑定 assets[0]，它从拼接列表移出（130），不再重复渲染。
  await page.getByLabel("排版背景", { exact: true }).selectOption("image");
  await expect(page.getByLabel("排版背景图", { exact: true })).toHaveValue(
    ASSET,
  );
  await expect.poll(width).toBe(130);
  // 换背景为另一资产：旧背景按对象顺序插回、新背景移出（120）。
  await page.getByLabel("排版背景图", { exact: true }).selectOption(ITEM_B);
  await expect.poll(width).toBe(120);
  // 离开图像背景：绑定解除且背景资产插回拼接，恢复全量拼接（150）。
  await page
    .getByLabel("排版背景", { exact: true })
    .selectOption("transparent");
  await expect.poll(width).toBe(150);
});

test("排版最终像素在复制、贴图、OCR与取字一致，复用图片屏蔽传播且文字层过滤同坐标", async ({
  page,
}) => {
  const { uploads, state, imported } = await setup(page);
  await sendState(page, "imageEdit", {
    ...state,
    import: {
      requestId: "mask-import",
      sessionId: SESSION,
      revision: 0,
      items: [
        {
          kind: "image",
          assetId: ASSET,
          resourceUri: "https://app.vibeocr/__resource/composition-asset.png",
          byteLength: imported.length,
          pixelWidth: 20,
          pixelHeight: 10,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: { sessionId: SESSION, revision: 0, requestId: "mask-import" },
  });
  await page.getByRole("button", { name: "屏蔽", exact: true }).click();
  const canvas = page.getByLabel("图片检查画布", { exact: true });
  await canvas.scrollIntoViewIfNeeded();
  const box = await canvas.boundingBox();
  if (!box) throw new Error("missing canvas");
  const point = (x: number, y: number) => ({
    x: box.x + ((x * 11.25) / 900) * box.width,
    y: box.y + ((y * 11.25 + 75) / 600) * box.height,
  });
  const from = point(10, 10),
    to = point(70, 30);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y);
  await page.mouse.up();
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  await page.getByLabel("中段裁除方向").selectOption("vertical");
  await page.getByLabel("裁除起点", { exact: true }).fill("25");
  await page.getByLabel("裁除终点", { exact: true }).fill("75");
  await page.getByLabel("多图拼接", { exact: true }).selectOption("horizontal");
  await expect
    .poll(() =>
      page
        .getByAltText("排版输出预览")
        .evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(80);
  await page.getByRole("button", { name: "复制标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const normal = await pixels(
    page,
    `data:image/png;base64,${uploads[0]!.bytes.toString("base64")}`,
  );
  expect([normal.width, normal.height]).toEqual([80, 60]);
  expect(at(normal, 25, 30)).toEqual([255, 0, 0, 255]);
  expect(at(normal, 65, 30)).toEqual([0, 255, 0, 255]);
  await page.getByRole("button", { name: "贴图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(2);
  expect(uploads[1]!.bytes).toEqual(uploads[0]!.bytes);
  const pin = (await commands(page)).find(
    (command) => command.action === "pinScreenshotImage",
  )!;
  const boxes = pin.arguments.excludeBoxes as {
    x: number;
    y: number;
    width: number;
    height: number;
  }[];
  expect(boxes.length).toBe(2);
  expect(
    boxes.some(
      (rect) => Math.abs(rect.x - 250) < 15 && Math.abs(rect.width - 250) < 30,
    ),
  ).toBe(true);
  const reusedBox = boxes.find((rect) => rect.x === 625)!;
  expect(reusedBox.width).toBe(250);
  expect(reusedBox.y).toBeCloseTo((25 / 60) * 1000, 10);
  expect(reusedBox.height).toBeCloseTo((10 / 60) * 1000, 10);
  for (const rect of boxes) {
    expect(rect.x + rect.width).toBeLessThanOrEqual(1000);
    expect(rect.y + rect.height).toBeLessThanOrEqual(1000);
  }
  await page.getByRole("button", { name: "取字", exact: true }).click();
  await expect
    .poll(async () =>
      (await commands(page)).some(
        (command) => command.action === "prepareScreenshotTextLayer",
      ),
    )
    .toBe(true);
  await expect.poll(() => uploads.length).toBe(3);
  const masked = await pixels(
    page,
    `data:image/png;base64,${uploads[2]!.bytes.toString("base64")}`,
  );
  expect(at(masked, 25, 30)).toEqual([255, 255, 255, 255]);
  expect(at(masked, 65, 30)).toEqual([255, 255, 255, 255]);
  expect(at(masked, 15, 30)).toEqual(at(normal, 15, 30));
  await page.route("**/composition-layer.png", (route) =>
    route.fulfill({ contentType: "image/png", body: uploads[2]!.bytes }),
  );
  const revision = (await commands(page))
    .filter((command) => command.action === "notifyScreenshotRevision")
    .at(-1)!.arguments.revision;
  await sendState(page, "imageEdit", {
    ...state,
    screenshotSession: { sessionId: SESSION, revision },
    textLayer: {
      status: "textlayer.ready",
      binding: { sessionId: SESSION, revision },
      image: {
        url: "https://app.vibeocr/composition-layer.png",
        mediaType: "image/png",
        byteLength: uploads[2]!.bytes.length,
      },
      lines: [
        { text: "kept", x1: 120, y1: 350, x2: 200, y2: 500 },
        { text: "masked base", x1: 300, y1: 350, x2: 450, y2: 500 },
        { text: "masked reused", x1: 650, y1: 430, x2: 850, y2: 550 },
      ],
    },
  });
  await expect(page.getByText("kept", { exact: true })).toBeVisible();
  await expect(page.getByText("masked base", { exact: true })).toHaveCount(0);
  await expect(page.getByText("masked reused", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("排版背景", { exact: true }).selectOption("image");
  const before = uploads.length;
  await page.getByRole("button", { name: "文字识别", exact: true }).click();
  await expect(
    page.getByText(/整张图都在屏蔽区内：没有可识别内容/),
  ).toBeVisible();
  expect(uploads.length).toBe(before);
  await page
    .getByLabel("排版背景", { exact: true })
    .selectOption("transparent");
  await page.getByRole("button", { name: "文字识别", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(before + 1);
  expect(uploads.at(-1)!.bytes).toEqual(uploads[0]!.bytes);
  await expect(
    page.getByRole("heading", { name: "单次识别", exact: true }),
  ).toBeVisible();
  const recognition = (await commands(page)).find(
    (command) => command.action === "recognizeScreenshotImage",
  )!;
  expect(recognition.arguments.excludeBoxes).toEqual(boxes);
});

test("235×239：被中段裁除的屏蔽源仍屏蔽放大镜泄露，全部普通输出与文字层共用最终坐标", async ({
  page,
}, info) => {
  const { uploads, state } = await setup(page);
  const canvas = page.getByLabel("图片检查画布", { exact: true });
  const drag = async (
    tool: string,
    x: number,
    y: number,
    right: number,
    bottom: number,
  ) => {
    await page.getByRole("button", { name: tool, exact: true }).click();
    await canvas.scrollIntoViewIfNeeded();
    const box = await canvas.boundingBox();
    if (!box) throw new Error("missing canvas");
    const point = (x: number, y: number) => ({
      x: box.x + ((x * 11.25) / 900) * box.width,
      y: box.y + ((y * 11.25 + 75) / 600) * box.height,
    });
    const from = point(x, y),
      to = point(right, bottom);
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(to.x, to.y);
    await page.mouse.up();
  };
  await drag("屏蔽", 27, 10, 30, 30);
  await drag("放大镜", 10, 5, 70, 35);
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  await page.getByLabel("中段裁除方向").selectOption("vertical");
  await page.getByLabel("裁除起点", { exact: true }).fill("25");
  await page.getByLabel("裁除终点", { exact: true }).fill("75");
  await expect
    .poll(() =>
      page
        .getByAltText("排版输出预览")
        .evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(60);
  for (const [index, name] of ["复制标注图", "保存标注图", "贴图"].entries()) {
    await page.getByRole("button", { name, exact: true }).click();
    await expect.poll(() => uploads.length).toBe(index + 1);
    if (index > 0) expect(uploads[index]!.bytes).toEqual(uploads[0]!.bytes);
  }
  const normal = await pixels(
    page,
    `data:image/png;base64,${uploads[0]!.bytes.toString("base64")}`,
  );
  expect([normal.width, normal.height]).toEqual([60, 60]);
  expect(at(normal, 27, 30)).toEqual([0, 255, 0, 255]);
  const pin = (await commands(page)).find(
    (command) => command.action === "pinScreenshotImage",
  )!;
  const boxes = pin.arguments.excludeBoxes as {
    x: number;
    y: number;
    width: number;
    height: number;
  }[];
  expect(boxes.length).toBeGreaterThan(0);
  expect(
    boxes.some(
      (rect) =>
        rect.x <= 450 &&
        rect.x + rect.width >= 450 &&
        rect.y <= 500 &&
        rect.y + rect.height >= 500,
    ),
  ).toBe(true);
  for (const rect of boxes) {
    expect(rect.x + rect.width).toBeLessThanOrEqual(1000);
    expect(rect.y + rect.height).toBeLessThanOrEqual(1000);
  }
  await page.getByRole("button", { name: "取字", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(4);
  const masked = await pixels(
    page,
    `data:image/png;base64,${uploads[3]!.bytes.toString("base64")}`,
  );
  expect(at(masked, 27, 30)).toEqual([255, 255, 255, 255]);
  expect(at(masked, 15, 30)).toEqual(at(normal, 15, 30));
  await page.route("**/lens-composition-layer.png", (route) =>
    route.fulfill({ contentType: "image/png", body: uploads[3]!.bytes }),
  );
  const revision = (await commands(page))
    .filter((command) => command.action === "notifyScreenshotRevision")
    .at(-1)!.arguments.revision;
  await sendState(page, "imageEdit", {
    ...state,
    screenshotSession: { sessionId: SESSION, revision },
    textLayer: {
      status: "textlayer.ready",
      binding: { sessionId: SESSION, revision },
      image: {
        url: "https://app.vibeocr/lens-composition-layer.png",
        mediaType: "image/png",
        byteLength: uploads[3]!.bytes.length,
      },
      lines: [
        { text: "lens-kept", x1: 200, y1: 400, x2: 250, y2: 600 },
        { text: "lens-masked", x1: 440, y1: 400, x2: 470, y2: 600 },
      ],
    },
  });
  await expect(page.getByText("lens-kept", { exact: true })).toBeVisible();
  await expect(page.getByText("lens-masked", { exact: true })).toHaveCount(0);
  await page.screenshot({
    path: info.outputPath("composition-magnifier-mask.png"),
    fullPage: true,
  });
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByRole("button", { name: "文字识别", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(5);
  expect(uploads[4]!.bytes).toEqual(uploads[0]!.bytes);
  // 收到上传不等于识别交接完成；等待导航落地后再清理异步资源路由。
  await expect(
    page.getByRole("heading", { name: "单次识别", exact: true }),
  ).toBeVisible();
  const recognition = (await commands(page)).find(
    (command) => command.action === "recognizeScreenshotImage",
  )!;
  expect(recognition.arguments.excludeBoxes).toEqual(boxes);
});

test("F239-01：无排版20000px图片保留普通输出，真实排版仍拒绝超限", async ({
  page,
}) => {
  const { uploads } = await setup(page, {}, { width: 20000, height: 100 });
  for (const [index, name] of ["复制标注图", "保存标注图", "贴图"].entries()) {
    await page.getByRole("button", { name, exact: true }).click();
    await expect.poll(() => uploads.length).toBe(index + 1);
    if (index > 0) expect(uploads[index]!.bytes).toEqual(uploads[0]!.bytes);
  }
  const dimensions = await page.evaluate(async (base64) => {
    const bitmap = await createImageBitmap(
      new Blob([Uint8Array.from(atob(base64), (char) => char.charCodeAt(0))], {
        type: "image/png",
      }),
    );
    const size = [bitmap.width, bitmap.height];
    bitmap.close();
    return size;
  }, uploads[0]!.bytes.toString("base64"));
  expect(dimensions).toEqual([20000, 100]);
  await page.getByRole("button", { name: "取字", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(4);
  expect(uploads[3]!.bytes).toEqual(uploads[0]!.bytes);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("排版边距", { exact: true }).fill("1");
  await page.getByRole("button", { name: "文字识别", exact: true }).click();
  await expect(page.getByText(/排版输出超出上限/).first()).toBeVisible();
  expect(uploads.length).toBe(4);
  await page
    .getByRole("toolbar", { name: "图片编辑工具", exact: true })
    .getByRole("button", { name: "重置排版", exact: true })
    .click();
  await page.getByRole("button", { name: "文字识别", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(5);
  expect(uploads[4]!.bytes).toEqual(uploads[0]!.bytes);
  // 收到上传不等于识别交接完成；等待导航落地后再清理异步资源路由。
  await expect(
    page.getByRole("heading", { name: "单次识别", exact: true }),
  ).toBeVisible();
});

test("F239-03：仅排版编辑可从上排清除或重置，撤销恢复真实预览", async ({
  page,
}, info) => {
  await setup(page);
  const main = page.getByRole("toolbar", { name: "图片编辑工具", exact: true });
  await expect(
    main.getByRole("button", { name: "清除编辑", exact: true }),
  ).toBeDisabled();
  await main.getByRole("button", { name: "画布排版", exact: true }).click();
  const panel = page.getByRole("region", { name: "画布排版属性", exact: true });
  await expect(
    panel.getByRole("button", { name: "重置排版", exact: true }),
  ).toHaveCount(0);
  const reset = main.getByRole("button", { name: "重置排版", exact: true });
  await expect(reset).toBeDisabled();
  await page.getByLabel("排版边距", { exact: true }).fill("10");
  const width = () =>
    page
      .getByAltText("排版输出预览")
      .evaluate((img: HTMLImageElement) => img.naturalWidth);
  await expect.poll(width).toBe(100);
  await expect(
    main.getByRole("button", { name: "清除编辑", exact: true }),
  ).toBeEnabled();
  await main.getByRole("button", { name: "清除编辑", exact: true }).click();
  await expect.poll(width).toBe(80);
  await main.getByRole("button", { name: "撤销", exact: true }).click();
  await expect.poll(width).toBe(100);
  await reset.click();
  await expect.poll(width).toBe(80);
  await main.getByRole("button", { name: "撤销", exact: true }).click();
  await expect.poll(width).toBe(100);
  await reset.scrollIntoViewIfNeeded();
  await page.screenshot({
    path: info.outputPath("composition-reset-primary-row.png"),
    fullPage: true,
  });
});

test("复制的图片对象逐份拼接，撤销与删除一份保持出现次数", async ({ page }) => {
  const { state, imported, uploads, projects } = await setup(page);
  await sendState(page, "imageEdit", {
    ...state,
    import: {
      requestId: "copy-stitch",
      sessionId: SESSION,
      revision: 0,
      items: [
        {
          kind: "image",
          assetId: ASSET,
          resourceUri: "https://app.vibeocr/__resource/composition-asset.png",
          byteLength: imported.length,
          pixelWidth: 20,
          pixelHeight: 10,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: { sessionId: SESSION, revision: 0, requestId: "copy-stitch" },
  });
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("多图拼接", { exact: true }).selectOption("horizontal");
  const preview = page.getByAltText("排版输出预览");
  const width = () =>
    preview.evaluate((image: HTMLImageElement) => image.naturalWidth);
  await expect.poll(width).toBe(100);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByRole("button", { name: "全选对象", exact: true }).click();
  await page.getByRole("button", { name: "复制对象", exact: true }).click();
  await page.getByRole("button", { name: "粘贴对象", exact: true }).click();
  await expect.poll(width).toBe(120);
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await expect.poll(width).toBe(100);
  await page.getByRole("button", { name: "重做", exact: true }).click();
  await expect.poll(width).toBe(120);
  const objects = page.getByLabel("选择标注对象", { exact: true });
  const ids = await objects
    .locator("option")
    .evaluateAll((options) =>
      options
        .map((option) => (option as HTMLOptionElement).value)
        .filter(Boolean),
    );
  await objects.selectOption(ids.at(-1)!);
  await page.getByRole("button", { name: "删除对象", exact: true }).click();
  await expect.poll(width).toBe(100);
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await expect.poll(width).toBe(120);
  await page.getByRole("button", { name: "保存工程", exact: true }).click();
  await expect.poll(() => projects.length).toBe(1);
  const saved = projects[0] as {
    state: { marks: { tool: string; assetId?: string }[] };
  };
  expect(
    saved.state.marks.filter(
      (mark) => mark.tool === "image" && mark.assetId === ASSET,
    ),
  ).toHaveLength(2);
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(
    (
      await pixels(
        page,
        "data:image/png;base64," + uploads[0]!.bytes.toString("base64"),
      )
    ).width,
  ).toBe(120);
});

test("预设上传期间锁住设置和目录，并发送开始时的语义快照", async ({ page }) => {
  const preferences = DEFAULT_EDITOR_PREFERENCES;
  await setup(page, {
    editorPreferences: preferences,
    imageOutputDirectory: "C:/authorized-output",
  });
  let release!: () => void;
  let started = false;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/__annotation", async (route) => {
    started = true;
    await gate;
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: annotation }),
    });
  });
  await page
    .getByRole("button", { name: "按常用预设保存", exact: true })
    .click();
  await expect.poll(() => started).toBe(true);
  await page.getByText("输出预设与工具栏定制", { exact: true }).click();
  await expect(
    page.getByLabel("预设文件名模板", { exact: true }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "选择输出目录", exact: true }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "保存预设与工具栏", exact: true }),
  ).toBeDisabled();
  await sendState(page, "settings", {
    editorPreferences: {
      ...preferences,
      output: {
        ...preferences.output,
        quality: 0.1,
        nameTemplate: "changed-{name}",
      },
    },
    imageOutputDirectory: "C:/other-output",
  });
  await expect(
    page.getByLabel("预设文件名模板", { exact: true }),
  ).toBeDisabled();
  release();
  await expectCommand(page, {
    scope: "imageEdit",
    action: "savePresetImage",
    arguments: {
      resourceUri: annotation,
      presetId: "default",
      presetBinding: {
        preset: preferences.output,
        directory: "C:/authorized-output",
      },
      sessionId: SESSION,
      revision: 0,
    },
  });
});

test("删除背景之前的拼接成员会同步索引，撤销与保存保留顺序", async ({
  page,
}) => {
  const { state, imported, projects } = await setup(page);
  const b = "5".repeat(32),
    c = "6".repeat(32);
  const second = await png(page, 30, 10, true),
    third = await png(page, 40, 10);
  await page.route("**/background-b.png", (route) =>
    route.fulfill({ contentType: "image/png", body: second }),
  );
  await page.route("**/background-c.png", (route) =>
    route.fulfill({ contentType: "image/png", body: third }),
  );
  await sendState(page, "imageEdit", {
    ...state,
    import: {
      requestId: "background-order",
      sessionId: SESSION,
      revision: 0,
      items: [
        {
          kind: "image",
          assetId: ASSET,
          resourceUri: "https://app.vibeocr/__resource/composition-asset.png",
          byteLength: imported.length,
          pixelWidth: 20,
          pixelHeight: 10,
        },
        {
          kind: "image",
          assetId: b,
          resourceUri: "https://app.vibeocr/__resource/background-b.png",
          byteLength: second.length,
          pixelWidth: 30,
          pixelHeight: 10,
        },
        {
          kind: "image",
          assetId: c,
          resourceUri: "https://app.vibeocr/__resource/background-c.png",
          byteLength: third.length,
          pixelWidth: 40,
          pixelHeight: 10,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: {
      sessionId: SESSION,
      revision: 0,
      requestId: "background-order",
    },
  });
  await page.getByRole("button", { name: "画布排版", exact: true }).click();
  await page.getByLabel("多图拼接", { exact: true }).selectOption("horizontal");
  await page.getByLabel("排版背景", { exact: true }).selectOption("image");
  await page.getByLabel("排版背景图", { exact: true }).selectOption(b);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象", { exact: true }).selectOption("0");
  await page.getByRole("button", { name: "删除对象", exact: true }).click();
  await page.getByRole("button", { name: "保存工程", exact: true }).click();
  await expect.poll(() => projects.length).toBe(1);
  expect(
    (projects[0] as { state: { composition: unknown } }).state.composition,
  ).toMatchObject({
    backgroundAssetId: b,
    assetIds: [c],
    backgroundStitchIndex: 0,
  });
  await page
    .getByLabel("排版背景", { exact: true })
    .selectOption("transparent");
  const preview = page.getByAltText("排版输出预览");
  await expect
    .poll(async () => at(await previewPixels(preview), 82, 20))
    .toEqual([255, 0, 0, 255]);
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await expect
    .poll(() =>
      preview.evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(140);
  await page.getByRole("button", { name: "重做", exact: true }).click();
  await page
    .getByLabel("排版背景", { exact: true })
    .selectOption("transparent");
  await expect
    .poll(async () => at(await previewPixels(preview), 82, 20))
    .toEqual([255, 0, 0, 255]);
});

test("预设明确失败回执逐次释放租约，成功回执不抢先丢弃", async ({ page }) => {
  await setup(page, {
    editorPreferences: DEFAULT_EDITOR_PREFERENCES,
    imageOutputDirectory: "C:/authorized-output",
  });
  const active = new Set<string>();
  const discarded: string[] = [];
  let uploads = 0;
  await page.route("**/__annotation", async (route) => {
    if (active.size >= 8) {
      await route.fulfill({ status: 429 });
      return;
    }
    const uri =
      "https://app.vibeocr/__annotation/" +
      (++uploads).toString(16).padStart(32, "0");
    active.add(new URL(uri).pathname);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: uri }),
    });
  });
  await page.route("**/__annotation/*", async (route) => {
    const path = new URL(route.request().url()).pathname;
    expect(route.request().method()).toBe("DELETE");
    expect(active.delete(path)).toBe(true);
    discarded.push(path);
    await route.fulfill({ status: 204 });
  });
  const save = page.getByRole("button", {
    name: "按常用预设保存",
    exact: true,
  });
  for (let index = 0; index < 10; index++) {
    await rejectNextCommand(page);
    await save.click();
    await expect.poll(() => discarded.length).toBe(index + 1);
    await expect(save).toBeEnabled();
    expect(active.size).toBe(0);
  }
  await save.click();
  await expect(
    page.getByText("已按常用预设保存副本；已有文件不会被覆盖。", {
      exact: true,
    }),
  ).toBeVisible();
  expect(uploads).toBe(11);
  expect(discarded).toHaveLength(10);
});

test("宿主图片租约释放不被静态资源代理覆盖", async ({ page }) => {
  await setup(page);
  const status = await page.evaluate(async (url) => {
    const response = await fetch(url, { method: "DELETE" });
    return response.status;
  }, annotation);
  expect(status).toBe(204);
});
