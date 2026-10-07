import { expect, test, type Page } from "@playwright/test";
import { expectCommand, mountHost } from "./workbench-host";

interface DecodedImage {
  width: number;
  height: number;
  pixels: number[];
}

/** 在页面内用真实浏览器解码器展开上传的编码字节，供像素断言使用。 */
async function decodeUpload(page: Page, body: Buffer): Promise<DecodedImage> {
  return page.evaluate(async (base64) => {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.charCodeAt(index);
    }
    const bitmap = await createImageBitmap(
      new Blob([bytes], { type: "image/png" }),
    );
    const canvas = document.createElement("canvas");
    canvas.width = bitmap.width;
    canvas.height = bitmap.height;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("2d context unavailable");
    context.drawImage(bitmap, 0, 0);
    bitmap.close();
    const data = context.getImageData(0, 0, canvas.width, canvas.height).data;
    return {
      width: canvas.width,
      height: canvas.height,
      pixels: Array.from(data),
    };
  }, body.toString("base64"));
}

function pixelAt(
  image: DecodedImage,
  x: number,
  y: number,
): [number, number, number] {
  const offset = (y * image.width + x) * 4;
  return [
    image.pixels[offset] ?? -1,
    image.pixels[offset + 1] ?? -1,
    image.pixels[offset + 2] ?? -1,
  ];
}

test("框选覆盖层在原位置编辑并保留保存和识别能力", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.addInitScript(() => {
    Object.assign(window, {
      vibeocrCaptureScene: {
        x: 300,
        y: 200,
        width: 900,
        height: 400,
        desktopWidth: 2560,
        desktopHeight: 1440,
        initialTool: "rectangle",
      },
    });
    document.addEventListener("DOMContentLoaded", () => {
      document.documentElement.dataset.captureScene = "true";
    });
  });
  const uploads: Buffer[] = [];
  await page.route("**/__capture_background", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="2560" height="1440"><rect width="2560" height="1440" fill="#e9eef4"/><text x="80" y="120" font-size="40" fill="#303a44">冻结桌面 · 编辑时保持可见</text></svg>',
    }),
  );
  await page.route("**/capture-scene.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="400"><rect width="900" height="400" fill="white"/><text x="40" y="90" font-family="Arial" font-size="42" fill="#25364b">VibeOCR 截图现场编辑</text><rect x="40" y="140" width="500" height="180" fill="#d5e5f4"/><path d="M50 290 L130 260 L210 270 L290 180 L370 230 L450 165 L530 200" stroke="#2d76aa" stroke-width="6" fill="none"/></svg>',
    }),
  );
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
  await mountHost(
    page,
    {
      sessionId: "capture-scene-e2e",
      revision: 1,
      route: "imageEdit",
      theme: "dark",
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
            sessionId: "capture-scene",
            revision: 0,
            sceneEditing: true,
          },
        },
      },
    },
    "/#/imageEdit?scene=1",
  );
  const canvas = page.getByLabel("图片检查画布");
  await expect(canvas).toBeVisible();
  await expect
    .poll(async () => {
      const box = await canvas.boundingBox();
      return (
        box && { x: box.x, y: box.y, width: box.width, height: box.height }
      );
    })
    .toEqual({ x: 150, y: 100, width: 450, height: 200 });
  await expect(page.getByRole("heading", { level: 1 })).not.toBeVisible();
  await expect(
    page.getByRole("button", { name: "矩形", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator(".capture-scene-backdrop img")).toBeVisible();
  await expect
    .poll(() =>
      page
        .locator(".capture-scene-backdrop img")
        .evaluate((image: HTMLImageElement) => image.naturalWidth),
    )
    .toBe(2560);
  const toolButtons = page
    .getByRole("toolbar", { name: "图片编辑工具" })
    .locator("button");
  const toolRows = await toolButtons.evaluateAll(
    (buttons) =>
      new Set(
        buttons.map((button) => {
          const box = button.getBoundingClientRect();
          return Math.round(box.top + box.height / 2);
        }),
      ).size,
  );
  expect(toolRows).toBe(1);
  await expect(
    page.getByRole("button", { name: "矩形", exact: true }),
  ).toHaveText("");
  // 截图现场不出现裁剪按钮：选区四边/四角拖拽即裁剪。
  await expect(
    page.getByRole("button", { name: "裁剪", exact: true }),
  ).toHaveCount(0);
  // 工具按钮：title 提示、可点击光标与清晰的选中态底色。
  const rectangle = page.getByRole("button", { name: "矩形", exact: true });
  await expect(rectangle).toHaveAttribute("title", "矩形");
  await expect(rectangle).toHaveCSS("cursor", "pointer");
  const pressedBackground = await rectangle.evaluate(
    (element) => getComputedStyle(element).backgroundColor,
  );
  const ellipse = page.getByRole("button", { name: "椭圆", exact: true });
  const idleBackground = await ellipse.evaluate(
    (element) => getComputedStyle(element).backgroundColor,
  );
  expect(pressedBackground).not.toBe(idleBackground);
  await ellipse.hover();
  await expect
    .poll(() =>
      ellipse.evaluate((element) => getComputedStyle(element).backgroundColor),
    )
    .not.toBe(idleBackground);
  await expect(page.locator(".capture-scene-mask")).toHaveCSS(
    "box-shadow",
    /rgba\(0, 0, 0, 0.35\)/,
  );
  await page.mouse.move(450, 150);
  await page.mouse.down();
  await page.mouse.move(560, 240);
  // 松开前就应绘出矩形，不能仅在提交历史时才更新像素。
  await expect
    .poll(() =>
      canvas.evaluate((element: HTMLCanvasElement) => {
        const pixel = element
          .getContext("2d")!
          .getImageData(600, 150, 1, 1).data;
        return Array.from(pixel);
      }),
    )
    .toEqual([243, 139, 53, 255]);
  await page.mouse.up();
  await expect
    .poll(() =>
      canvas.evaluate((element: HTMLCanvasElement) =>
        Array.from(element.getContext("2d")!.getImageData(100, 200, 1, 1).data),
      ),
    )
    .toEqual([213, 229, 244, 255]);
  const screenshot = await page.screenshot({
    path: testInfo.outputPath("scene-at-selection.png"),
  });
  const visiblePixels = await page.evaluate(async (bytes) => {
    const bitmap = await createImageBitmap(
      new Blob([new Uint8Array(bytes)], { type: "image/png" }),
    );
    const surface = document.createElement("canvas");
    surface.width = bitmap.width;
    surface.height = bitmap.height;
    const context = surface.getContext("2d")!;
    context.drawImage(bitmap, 0, 0);
    bitmap.close();
    return Array.from(context.getImageData(200, 200, 1, 1).data);
  }, Array.from(screenshot));
  expect(visiblePixels).toEqual([213, 229, 244, 255]);
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]?.readUInt32BE(16)).toBe(900);
  expect(uploads[0]?.readUInt32BE(20)).toBe(400);
  await page.getByRole("button", { name: "识别当前图" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "recognizeScreenshotImage",
    arguments: {
      resourceUri:
        "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      sessionId: "capture-scene",
      revision: 1,
      excludeBoxes: [],
    },
  });
  await canvas.press("Escape");
  await canvas.press("Escape");
  await expectCommand(page, {
    scope: "recognition",
    action: "closeScreenshotSession",
    arguments: {},
  });
});

/** 共享的截图现场挂载：细纹理 SVG + 上传采集；返回画布与上传缓冲。 */
async function mountSceneForExport(page: Page, svg: string) {
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.addInitScript(() => {
    Object.assign(window, {
      vibeocrCaptureScene: {
        x: 300,
        y: 200,
        width: 900,
        height: 400,
        desktopWidth: 2560,
        desktopHeight: 1440,
      },
    });
    document.addEventListener("DOMContentLoaded", () => {
      document.documentElement.dataset.captureScene = "true";
    });
  });
  const uploads: Buffer[] = [];
  await page.route("**/__capture_background", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="2560" height="1440"><rect width="2560" height="1440" fill="#e9eef4"/></svg>',
    }),
  );
  await page.route("**/capture-scene.svg", (route) =>
    route.fulfill({ contentType: "image/svg+xml", body: svg }),
  );
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
  await mountHost(
    page,
    {
      sessionId: "capture-scene-e2e",
      revision: 1,
      route: "imageEdit",
      theme: "dark",
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
            sessionId: "capture-scene",
            revision: 0,
            sceneEditing: true,
          },
        },
      },
    },
    "/#/imageEdit?scene=1",
  );
  const canvas = page.getByLabel("图片检查画布");
  await expect
    .poll(async () => {
      const box = await canvas.boundingBox();
      return (
        box && { x: box.x, y: box.y, width: box.width, height: box.height }
      );
    })
    .toEqual({ x: 150, y: 100, width: 450, height: 200 });
  return { canvas, uploads };
}

test("拖动选区边角调整裁剪并写入真实导出", async ({ page }) => {
  const { canvas, uploads } = await mountSceneForExport(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="400"><rect width="900" height="400" fill="white"/><rect x="40" y="140" width="500" height="180" fill="#d5e5f4"/></svg>',
  );
  // 选区右边缘中点：坐标 (900,200) → 视口约 (597,200)；悬停提示 e-resize。
  await page.mouse.move(597, 200);
  await expect(canvas).toHaveCSS("cursor", "e-resize");
  await page.mouse.down();
  await page.mouse.move(547, 200);
  await page.mouse.up();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]?.readUInt32BE(16)).toBe(800);
  expect(uploads[0]?.readUInt32BE(20)).toBe(400);
});

test("马赛克强度与常用色影响真实导出", async ({ page }) => {
  const svg =
    '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="400"><rect width="900" height="400" fill="white"/><path d="M50 290 L130 260 L210 270 L290 180 L370 230 L450 165 L530 200" stroke="#2d76aa" stroke-width="6" fill="none"/><rect x="40" y="40" width="820" height="320" fill="none" stroke="#99aabb" stroke-width="2"/></svg>';
  const { uploads } = await mountSceneForExport(page, svg);
  const mosaicRegion = async () => {
    await page.mouse.move(250, 140);
    await page.mouse.down();
    await page.mouse.move(400, 220, { steps: 3 });
    await page.mouse.up();
  };

  // 基线：无效果导出。
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);

  // 马赛克弱/强：同一区域不同强度必须产生不同的真实导出字节。
  await page.getByRole("button", { name: "马赛克", exact: true }).click();
  await page.getByLabel("马赛克与模糊强度").selectOption("1");
  await mosaicRegion();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  await page.getByRole("button", { name: "撤销" }).click();
  await page.getByLabel("马赛克与模糊强度").selectOption("3");
  await mosaicRegion();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(3);

  expect(uploads[0]?.readUInt32BE(16)).toBe(900);
  expect(uploads[1]?.readUInt32BE(16)).toBe(900);
  expect(uploads[2]?.readUInt32BE(16)).toBe(900);
  expect(uploads[1]?.equals(uploads[0]!)).toBe(false);
  expect(uploads[2]?.equals(uploads[1]!)).toBe(false);
  expect(uploads[2]?.equals(uploads[0]!)).toBe(false);

  // 常用色色板：导出像素携带所选颜色（#1f6feb = rgb(31,111,235)）。
  await page.getByRole("button", { name: "撤销" }).click();
  await page.getByRole("button", { name: "常用颜色 #1f6feb" }).click();
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await page.mouse.move(450, 150);
  await page.mouse.down();
  await page.mouse.move(560, 240, { steps: 3 });
  await page.mouse.up();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(4);
  const decoded = await decodeUpload(page, uploads[3]!);
  expect(decoded.width).toBe(900);
  expect(pixelAt(decoded, 700, 100)).toEqual([31, 111, 235]);
  expect(pixelAt(decoded, 40, 60)).toEqual([153, 170, 187]);
});

for (const dpr of [1, 1.25, 1.5, 2]) {
  test(`冻结画面在 ${dpr * 100}% DPI 保留逐像素清晰度`, async ({ browser }) => {
    const context = await browser.newContext({
      viewport: { width: 1200, height: 800 },
      deviceScaleFactor: dpr,
    });
    const page = await context.newPage();
    try {
      const geometry = {
        x: 301,
        y: 201,
        width: 601,
        height: 301,
        desktopWidth: 1200 * dpr,
        desktopHeight: 800 * dpr,
      };
      await page.addInitScript((geometry) => {
        Object.assign(window, { vibeocrCaptureScene: geometry });
        document.addEventListener("DOMContentLoaded", () => {
          document.documentElement.dataset.captureScene = "true";
        });
      }, geometry);
      // 黑白单像素棋盘；任何降采样或插值都会产生灰色像素。
      const images = await page.evaluate((geometry) => {
        const c = document.createElement("canvas");
        c.width = geometry.desktopWidth;
        c.height = geometry.desktopHeight;
        const ctx = c.getContext("2d")!;
        const data = ctx.createImageData(c.width, c.height);
        for (let y = 0; y < c.height; y++)
          for (let x = 0; x < c.width; x++) {
            const i = (y * c.width + x) * 4;
            const v = ((x + y) % 2) * 255;
            data.data.set([v, v, v, 255], i);
          }
        ctx.putImageData(data, 0, 0);
        const background = c.toDataURL();
        const crop = document.createElement("canvas");
        crop.width = geometry.width;
        crop.height = geometry.height;
        crop
          .getContext("2d")!
          .drawImage(
            c,
            geometry.x,
            geometry.y,
            crop.width,
            crop.height,
            0,
            0,
            crop.width,
            crop.height,
          );
        return { background, crop: crop.toDataURL() };
      }, geometry);
      await page.route("**/pixel-grid.png", (route) =>
        route.fulfill({
          contentType: "image/png",
          body: Buffer.from(images.crop.split(",")[1]!, "base64"),
        }),
      );
      await page.route("**/__capture_background", (route) =>
        route.fulfill({
          contentType: "image/png",
          body: Buffer.from(images.background.split(",")[1]!, "base64"),
        }),
      );
      await mountHost(
        page,
        {
          sessionId: "dpi",
          revision: 1,
          route: "imageEdit",
          theme: "light",
          capabilities: [
            "recognition.annotation",
            "recognition.screenshotSession",
          ],
          features: {
            recognition: {
              isBusy: false,
              statusCode: "recognition.session",
              input: {
                url: "/pixel-grid.png",
                mediaType: "image/png",
                byteLength: 100,
              },
              screenshotSession: {
                sessionId: "dpi",
                revision: 0,
                sceneEditing: true,
              },
            },
          },
        },
        "/#/imageEdit?scene=1",
      );
      const canvas = page.getByLabel("图片检查画布");
      await expect
        .poll(() =>
          canvas.evaluate((c: HTMLCanvasElement) =>
            Array.from(c.getContext("2d")!.getImageData(20, 20, 1, 1).data),
          ),
        )
        .toEqual([0, 0, 0, 255]);
      const bytes = await page.screenshot();
      const pixels = await page.evaluate(async (bytes) => {
        const bitmap = await createImageBitmap(
          new Blob([new Uint8Array(bytes)], { type: "image/png" }),
        );
        const c = document.createElement("canvas");
        c.width = bitmap.width;
        c.height = bitmap.height;
        const ctx = c.getContext("2d")!;
        ctx.drawImage(bitmap, 0, 0);
        bitmap.close();
        return {
          selection: Array.from(ctx.getImageData(321, 221, 8, 1).data),
          outside: Array.from(ctx.getImageData(20, 20, 8, 1).data),
        };
      }, Array.from(bytes));
      expect(pixels.selection).toEqual(
        Array.from({ length: 8 }, (_, i) => [
          (i % 2) * 255,
          (i % 2) * 255,
          (i % 2) * 255,
          255,
        ]).flat(),
      );
      expect(pixels.outside).toEqual(
        Array.from({ length: 8 }, (_, i) => [
          (i % 2) * 166,
          (i % 2) * 166,
          (i % 2) * 166,
          255,
        ]).flat(),
      );
    } finally {
      await context.close();
    }
  });
}
