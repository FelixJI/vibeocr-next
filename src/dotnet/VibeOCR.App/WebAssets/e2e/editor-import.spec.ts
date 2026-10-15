import { writeFile } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import type { HostCommand } from "../src/bridge/client";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

async function commands(page: Page): Promise<HostCommand[]> {
  return page.evaluate(
    () =>
      (window as Window & { __testHost: { commands: HostCommand[] } })
        .__testHost.commands,
  );
}
async function action(page: Page, name: string) {
  await page.getByRole("button", { name, exact: true }).click();
}
async function pngPixels(page: Page, png: Buffer, points: [number, number][]) {
  return page.evaluate(
    async ({ png, points }) => {
      const bitmap = await createImageBitmap(
        new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
          type: "image/png",
        }),
      );
      const canvas = document.createElement("canvas");
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      const context = canvas.getContext("2d")!;
      context.drawImage(bitmap, 0, 0);
      bitmap.close();
      return points.map(([x, y]) =>
        Array.from(context.getImageData(x, y, 1, 1).data),
      );
    },
    { png: png.toString("base64"), points },
  );
}

for (const scope of ["imageEdit", "recognition"] as const) {
  test(`${scope}: PNG alpha、图片文字独立变换、失败保画布、历史和资源回收`, async ({
    page,
  }, info) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const sessionId =
      scope === "imageEdit"
        ? "11111111111111111111111111111111"
        : "22222222222222222222222222222222";
    const baseUrl = "https://app.vibeocr/__resource/import-base";
    const imageUrl = "https://app.vibeocr/__resource/import-png";
    const textUrl = "https://app.vibeocr/__resource/import-text";
    const pngs = await page.evaluate(() => {
      const canvas = document.createElement("canvas");
      canvas.width = 900;
      canvas.height = 600;
      const base = canvas.toDataURL("image/png").split(",")[1]!;
      canvas.width = 160;
      canvas.height = 80;
      const context = canvas.getContext("2d")!;
      context.fillStyle = "rgba(255,0,0,0.5)";
      context.fillRect(0, 0, 80, 80);
      context.fillStyle = "#0000ff";
      context.fillRect(80, 0, 80, 80);
      return { base, image: canvas.toDataURL("image/png").split(",")[1]! };
    });
    // 以真实宿主源加载生产资产，资源 URI 与 CSP 的 self 保持同源。
    await page.route("https://app.vibeocr/**", async (route) => {
      const url = new URL(route.request().url());
      const response = await page.request.get(
        `http://127.0.0.1:4174${url.pathname}${url.search}`,
      );
      await route.fulfill({ response });
    });
    const original = Buffer.from(pngs.base, "base64");
    const importPng = Buffer.from(pngs.image, "base64");
    let imageReads = 0;
    let allowRead = true;
    await page.route(baseUrl, (route) =>
      route.fulfill({ contentType: "image/png", body: original }),
    );
    await page.route(imageUrl, (route) => {
      imageReads++;
      return allowRead
        ? route.fulfill({ contentType: "image/png", body: importPng })
        : route.abort();
    });
    await page.route(textUrl, (route) =>
      route.fulfill({ contentType: "text/plain", body: "Import text" }),
    );
    await page.route("**/__resource/bad-png", (route) =>
      route.fulfill({ contentType: "image/png", body: "not a PNG" }),
    );
    const uploads: Buffer[] = [];
    await page.route("**/__annotation", async (route) => {
      uploads.push(route.request().postDataBuffer()!);
      await route.fulfill({
        status: 201,
        contentType: "application/json",
        body: JSON.stringify({
          resourceUri:
            "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
        }),
      });
    });
    const state = {
      isBusy: false,
      statusCode: "recognition.completed",
      input: {
        url: baseUrl,
        mediaType: "image/png",
        byteLength: original.length,
      },
      screenshotSession: {
        sessionId,
        revision: 0,
        sceneEditing: scope === "recognition",
      },
    };
    await mountHost(
      page,
      {
        ...snapshot,
        route: "imageEdit",
        capabilities: ["recognition.results", "recognition.annotation"],
        features: { [scope]: state },
      },
      scope === "recognition"
        ? "https://app.vibeocr/#/imageEdit?scene=1"
        : "https://app.vibeocr/",
    );
    await expect(
      page.getByText("原图 900×600", { exact: false }),
    ).toBeVisible();
    await action(page, "粘贴系统内容");
    await expectCommand(page, {
      scope,
      action: "pasteSystemContent",
      arguments: {},
    });
    const descriptor = {
      requestId: "request-png-text",
      sessionId,
      revision: 0,
      items: [
        {
          kind: "image",
          assetId: "png-alpha",
          resourceUri: imageUrl,
          byteLength: importPng.length,
          pixelWidth: 160,
          pixelHeight: 80,
        },
        {
          kind: "text",
          assetId: "text",
          resourceUri: textUrl,
          byteLength: 11,
          textLength: 11,
          textLines: 1,
        },
      ],
    };
    await sendState(page, scope, { ...state, import: descriptor });
    await expectCommand(page, {
      scope,
      action: "ackImport",
      arguments: { sessionId, revision: 0, requestId: descriptor.requestId },
    });
    await expectCommand(page, {
      scope,
      action: "releaseAssets",
      arguments: { sessionId, resourceUris: [textUrl] },
    });
    const list = page.getByLabel("选择标注对象");
    await expect(list.locator("option")).toHaveCount(3);
    const save = async (name: string, width = 900, height = 600) => {
      const index = uploads.length;
      await action(page, "保存标注图");
      await expect.poll(() => uploads.length).toBe(index + 1);
      const png = uploads[index]!;
      expect(png.readUInt32BE(16)).toBe(width);
      expect(png.readUInt32BE(20)).toBe(height);
      await writeFile(info.outputPath(`${name}.png`), png);
      return png;
    };
    const points: [number, number][] = [
      [410, 280],
      [490, 280],
      [360, 250],
    ];
    const editorFrame = await page
      .getByLabel("图片检查画布")
      .evaluate((element) => {
        const canvas = element as HTMLCanvasElement;
        const context = canvas.getContext("2d")!;
        const scaleX = canvas.width / Number(canvas.dataset.coordinateWidth);
        const scaleY = canvas.height / Number(canvas.dataset.coordinateHeight);
        const pixels = context.getImageData(
          Math.floor(360 * scaleX),
          Math.floor(250 * scaleY),
          Math.ceil(180 * scaleX),
          Math.ceil(10 * scaleY),
        ).data;
        return Array.from(pixels).some(
          (alpha, index) => index % 4 === 3 && alpha > 0,
        );
      });
    expect(editorFrame).toBe(true);
    const imported = await save("imported-alpha");
    expect(await pngPixels(page, imported, [[365, 255]])).toEqual([
      [0, 0, 0, 0],
    ]);
    expect(await pngPixels(page, imported, points)).toEqual([
      [255, 0, 0, 128],
      [0, 0, 255, 255],
      [0, 0, 0, 0],
    ]);
    await list.selectOption("1");
    await page.getByLabel("图片检查画布").press("ArrowRight");
    const movedText = await save("text-moved-independently");
    expect(movedText).not.toEqual(imported);
    expect(await pngPixels(page, movedText, points)).toEqual(
      await pngPixels(page, imported, points),
    );
    await action(page, "撤销");
    expect(await save("undo-text-move")).toEqual(imported);
    allowRead = false; // 采纳后宿主 URL 即使不可再次读取，undo/redo/导出仍使用强引用位图。
    await list.selectOption("0");
    await action(page, "水平镜像");
    const mirrored = await save("mirrored-alpha");
    expect(await pngPixels(page, mirrored, points)).toEqual([
      [0, 0, 255, 255],
      [255, 0, 0, 128],
      [0, 0, 0, 0],
    ]);
    await action(page, "撤销");
    expect(await save("undo-mirror")).toEqual(imported);
    await action(page, "重做");
    expect(await save("redo-mirror")).toEqual(mirrored);
    await action(page, "撤销");
    await list.selectOption("0");
    await action(page, "对象旋转 90°");
    const rotated = await save("rotated-alpha");
    expect(
      await pngPixels(page, rotated, [
        [450, 260],
        [450, 340],
        [390, 300],
      ]),
    ).toEqual([
      [255, 0, 0, 128],
      [0, 0, 255, 255],
      [0, 0, 0, 0],
    ]);
    await action(page, "撤销");
    await action(page, "旋转 90°");
    const wholeRotated = await save("whole-image-rotated-alpha", 600, 900);
    expect(
      await pngPixels(page, wholeRotated, [
        [320, 410],
        [320, 490],
        [360, 450],
      ]),
    ).toEqual([
      [255, 0, 0, 128],
      [0, 0, 255, 255],
      [0, 0, 0, 0],
    ]);
    await action(page, "撤销");
    expect(await save("undo-whole-image-rotation")).toEqual(imported);
    await action(page, "撤销"); // 同批图片+文字一次撤销，redo仍保留图片资产。
    await expect(list).toHaveCount(0);
    expect(
      (await commands(page)).filter(
        (command) =>
          command.action === "releaseAssets" &&
          (command.arguments.resourceUris as string[]).some(
            (uri) => uri === imageUrl,
          ),
      ),
    ).toHaveLength(0);
    expect(await save("undo-import")).toEqual(original);
    await action(page, "重做");
    await expect(list.locator("option")).toHaveCount(3);
    expect(await save("redo-import")).toEqual(imported);
    const revision = (await commands(page))
      .filter((command) => command.action === "notifyScreenshotRevision")
      .at(-1)!.arguments.revision;
    await sendState(page, scope, {
      ...state,
      screenshotSession: { ...state.screenshotSession, revision },
      import: {
        ...descriptor,
        requestId: "broken-batch",
        revision,
        items: [
          descriptor.items[1],
          {
            ...descriptor.items[0],
            resourceUri: "https://app.vibeocr/__resource/bad-png",
            assetId: "bad",
          },
        ],
      },
    });
    await expectCommand(page, {
      scope,
      action: "rejectImport",
      arguments: { sessionId, revision, requestId: "broken-batch" },
    });
    await expect(list.locator("option")).toHaveCount(3);
    expect(await save("failed-batch-preserves-canvas")).toEqual(imported);
    await action(page, "撤销");
    // 新分支截断包含导入对象的redo历史后，宿主才收到该图片的精确释放。
    await action(page, "矩形");
    const canvas = page.getByLabel("图片检查画布");
    await canvas.evaluate((element) => {
      element.setPointerCapture = () => undefined;
    });
    const box = (await canvas.boundingBox())!;
    await canvas.dispatchEvent("pointerdown", {
      pointerId: 5,
      clientX: box.x + 15,
      clientY: box.y + 15,
    });
    await canvas.dispatchEvent("pointerup", {
      pointerId: 5,
      clientX: box.x + 85,
      clientY: box.y + 55,
    });
    await expectCommand(page, {
      scope,
      action: "releaseAssets",
      arguments: { sessionId, resourceUris: [imageUrl] },
    });
    expect(imageReads).toBe(1);
    // 原图只由测试路由提供；直接解析其原始透明像素作为不变基准。
    expect(await pngPixels(page, original, points)).toEqual([
      [0, 0, 0, 0],
      [0, 0, 0, 0],
      [0, 0, 0, 0],
    ]);
    expect(
      (await commands(page))
        .filter((command) =>
          ["ackImport", "rejectImport", "releaseAssets"].includes(
            command.action,
          ),
        )
        .every((command) => command.scope === scope),
    ).toBe(true);
  });
}

test("图片编辑离开设置再返回释放满额资源并可再次导入", async ({
  page,
}, info) => {
  const sessionId = "33333333333333333333333333333333";
  const baseUrl = "https://app.vibeocr/__resource/route-base";
  const png = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 900;
    canvas.height = 600;
    return canvas.toDataURL("image/png").split(",")[1]!;
  });
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });
  await page.route(baseUrl, (route) =>
    route.fulfill({
      contentType: "image/png",
      body: Buffer.from(png, "base64"),
    }),
  );
  const importedPng = await page.request.get(
    "http://127.0.0.1:4174/vibeocr-64.png",
  );
  const bytes = await importedPng.body();
  const items = Array.from({ length: 16 }, (_, index) => ({
    kind: "image",
    assetId: `route-${index}`,
    resourceUri: `https://app.vibeocr/__resource/route-${index}`,
    byteLength: bytes.length,
    pixelWidth: 64,
    pixelHeight: 64,
  }));
  await page.route("https://app.vibeocr/__resource/route-*", (route) =>
    route.fulfill({ contentType: "image/png", body: bytes }),
  );
  // Playwright 的后注册路由优先：基准资源保持真实 900x600。
  await page.route(baseUrl, (route) =>
    route.fulfill({
      contentType: "image/png",
      body: Buffer.from(png, "base64"),
    }),
  );
  const state = {
    isBusy: false,
    statusCode: "recognition.completed",
    input: {
      url: baseUrl,
      mediaType: "image/png",
      byteLength: Buffer.from(png, "base64").length,
    },
    screenshotSession: { sessionId, revision: 0, sceneEditing: false },
  };
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.results", "recognition.annotation"],
      features: { imageEdit: state },
    },
    "https://app.vibeocr/",
  );
  await sendState(page, "imageEdit", {
    ...state,
    import: { requestId: "full-route-quota", sessionId, revision: 0, items },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: { sessionId, revision: 0, requestId: "full-route-quota" },
  });
  const list = page.getByLabel("选择标注对象");
  await expect(list.locator("option")).toHaveCount(17);
  // 真实宿主 ack 后会投影无 import 的当前会话状态。
  const revision = (await commands(page))
    .filter((command) => command.action === "notifyScreenshotRevision")
    .at(-1)!.arguments.revision;
  const resumed = {
    ...state,
    screenshotSession: { ...state.screenshotSession, revision },
  };
  await sendState(page, "imageEdit", resumed);
  await page.getByRole("link", { name: "设置", exact: true }).click();
  await expectCommand(page, {
    scope: "imageEdit",
    action: "releaseAssets",
    arguments: {
      sessionId,
      resourceUris: items.map((item) => item.resourceUri),
    },
  });
  await page.getByRole("link", { name: "图片编辑", exact: true }).click();
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  await expect(list).toHaveCount(0);
  await sendState(page, "imageEdit", {
    ...resumed,
    import: {
      requestId: "after-route-retirement",
      sessionId,
      revision,
      items: [items[0]],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: { sessionId, revision, requestId: "after-route-retirement" },
  });
  await expect(list.locator("option")).toHaveCount(2);
  await page.screenshot({
    path: info.outputPath("route-retirement-reimport.png"),
  });
});

test("merge integration imported pixels survive cached preview, brush sampling and canvas color picking", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.addInitScript(() =>
    Object.defineProperty(window, "EyeDropper", {
      value: undefined,
      configurable: true,
    }),
  );
  const sessionId = "33333333333333333333333333333333";
  const baseUrl = "https://app.vibeocr/__resource/merge-base";
  const imageUrl = "https://app.vibeocr/__resource/merge-alpha";
  const pngs = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 900;
    canvas.height = 600;
    const base = canvas.toDataURL("image/png").split(",")[1]!;
    canvas.width = 160;
    canvas.height = 80;
    const context = canvas.getContext("2d")!;
    context.fillStyle = "rgba(255,0,0,0.5)";
    context.fillRect(0, 0, 160, 80);
    return { base, image: canvas.toDataURL("image/png").split(",")[1]! };
  });
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      "http://127.0.0.1:4174" + url.pathname + url.search,
    );
    await route.fulfill({ response });
  });
  const base = Buffer.from(pngs.base, "base64"),
    image = Buffer.from(pngs.image, "base64");
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  await page.route(imageUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: image }),
  );
  const uploads: Buffer[] = [];
  await page.route("**/__annotation", async (route) => {
    uploads.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      }),
    });
  });
  const state = {
    isBusy: false,
    statusCode: "recognition.completed",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId, revision: 0, sceneEditing: false },
  };
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.results", "recognition.annotation"],
      features: { imageEdit: state },
    },
    "https://app.vibeocr/",
  );
  await expect(page.getByText(/原图 900×600/)).toBeVisible();
  await sendState(page, "imageEdit", {
    ...state,
    import: {
      requestId: "merge-pixels",
      sessionId,
      revision: 0,
      items: [
        {
          kind: "image",
          assetId: "merge-alpha",
          resourceUri: imageUrl,
          byteLength: image.length,
          pixelWidth: 160,
          pixelHeight: 80,
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackImport",
    arguments: { sessionId, revision: 0, requestId: "merge-pixels" },
  });
  const canvas = page.getByLabel("图片检查画布");
  const clickAt = async (x: number, y: number) => {
    const box = (await canvas.boundingBox())!;
    await canvas.click({
      position: { x: (x * box.width) / 900, y: (y * box.height) / 600 },
    });
  };
  await action(page, "选择");
  await clickAt(10, 10);
  await expect
    .poll(() =>
      canvas.evaluate((element) => {
        const canvas = element as HTMLCanvasElement;
        return Array.from(
          canvas
            .getContext("2d")!
            .getImageData(
              Math.floor((450 * canvas.width) / 900),
              Math.floor((300 * canvas.height) / 600),
              1,
              1,
            ).data,
        );
      }),
    )
    .toEqual([255, 0, 0, 128]);
  const save = async () => {
    const index = uploads.length;
    await action(page, "保存标注图");
    await expect.poll(() => uploads.length).toBe(index + 1);
    return uploads[index]!;
  };
  const baseline = await save();
  await action(page, "矩形");
  await action(page, "屏幕取色");
  await clickAt(450, 300);
  await expect(
    page.getByText("已从画布取色 #ff0000。", { exact: true }),
  ).toBeVisible();
  for (const tool of ["马赛克", "模糊"]) {
    await action(page, tool);
    await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
    const box = (await canvas.boundingBox())!;
    await page.mouse.move(
      box.x + (420 * box.width) / 900,
      box.y + (300 * box.height) / 600,
    );
    await page.mouse.down();
    await page.mouse.move(
      box.x + (480 * box.width) / 900,
      box.y + (300 * box.height) / 600,
    );
    await page.mouse.up();
    const effected = await save();
    expect(
      await pngPixels(page, effected, [
        [450, 300],
        [390, 280],
      ]),
    ).toEqual([
      [255, 0, 0, 128],
      [255, 0, 0, 128],
    ]);
    await action(page, "撤销");
    expect(await save()).toEqual(baseline);
  }
});
