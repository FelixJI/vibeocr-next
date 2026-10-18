import { expect, test, type Page } from "@playwright/test";
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
    const existing = url.startsWith("blob:")
      ? [...document.images].find((image) => image.src === url)
      : undefined;
    let image: HTMLImageElement | ImageBitmap;
    if (existing) {
      await existing.decode();
      image = existing;
    } else {
      const blob = new Blob(
        [
          Uint8Array.from(atob(url.split(",")[1]!), (char) =>
            char.charCodeAt(0),
          ),
        ],
        { type: url.slice(5, url.indexOf(";")) },
      );
      image = await createImageBitmap(blob);
    }
    const canvas = document.createElement("canvas");
    canvas.width =
      image instanceof HTMLImageElement ? image.naturalWidth : image.width;
    canvas.height =
      image instanceof HTMLImageElement ? image.naturalHeight : image.height;
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(image, 0, 0);
    return {
      width: canvas.width,
      height: canvas.height,
      data: Array.from(
        ctx.getImageData(0, 0, canvas.width, canvas.height).data,
      ),
    };
  }, url);
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
async function setup(page: Page, settings: Record<string, unknown> = {}) {
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });
  const base = await png(page, 80, 40, true),
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
  await expect(page.getByText(/原图 80×40/)).toBeVisible();
  return { uploads, projects, state, imported };
}

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
  const expected = await pixels(
    page,
    (await preview.getAttribute("src")) ?? "",
  );
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
  const rounded = await pixels(page, (await preview.getAttribute("src")) ?? "");
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

test("常用预设只发送 ID，复制失败不关闭，成功 ack 后关闭；工具可发现且不抢输入", async ({
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
    itemCount: 2,
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
  for (const index of [0, 1]) {
    await expectCommand(page, {
      scope: "batch",
      action: "prepareImageExport",
      arguments: { index },
    });
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
      },
    });
  }
  await expect(
    page.getByText(/输出完成：成功 1，失败\/非图片 1/),
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
  const recognition = (await commands(page)).find(
    (command) => command.action === "recognizeScreenshotImage",
  )!;
  expect(recognition.arguments.excludeBoxes).toEqual(boxes);
});
