import { expect, test, type Page } from "@playwright/test";

import { expectCommand, mountHost, snapshot } from "./workbench-host";

interface DecodedImage {
  width: number;
  height: number;
  pixels: number[];
}

/** 在页面内用真实浏览器解码器展开上传的编码字节，供像素断言使用。 */
async function decodeUpload(
  page: Page,
  body: Buffer,
  mediaType: string,
): Promise<DecodedImage> {
  return page.evaluate(
    async ({ base64, mediaType }) => {
      const binary = atob(base64);
      const bytes = new Uint8Array(binary.length);
      for (let index = 0; index < binary.length; index += 1) {
        bytes[index] = binary.charCodeAt(index);
      }
      const bitmap = await createImageBitmap(
        new Blob([bytes], { type: mediaType }),
      );
      const canvas = document.createElement("canvas");
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      const context = canvas.getContext("2d");
      if (!context) throw new Error("2d context unavailable");
      context.drawImage(bitmap, 0, 0);
      const data = context.getImageData(0, 0, canvas.width, canvas.height).data;
      return {
        width: bitmap.width,
        height: bitmap.height,
        pixels: Array.from(data),
      };
    },
    { base64: body.toString("base64"), mediaType },
  );
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

interface AnnotationFormatHost {
  uploads: { contentType: string; body: Buffer }[];
}

/** 两个编码测试共享的前置：视口、真实上传收集与 80×40 半透明 SVG 源挂载，
 *  确认实时画布、原图信息行与按需检查入口可见（无第二幅常驻预览）。 */
async function setupAnnotationFormatHost(
  page: Page,
): Promise<AnnotationFormatHost> {
  await page.setViewportSize({ width: 1280, height: 800 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await page.route("**/__annotation", async (route) => {
    uploads.push({
      contentType: route.request().headers()["content-type"] ?? "",
      body: route.request().postDataBuffer() ?? Buffer.alloc(0),
    });
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      }),
    });
  });
  // 左半透明、右半纯色：用于验证 JPEG 白底合成与 PNG 原有深色合成。
  await page.route("**/annotation-format-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect x="40" width="40" height="40" fill="#e02020"/></svg>',
    }),
  );
  await mountHost(page, {
    ...snapshot,
    // 输出变换（格式/质量/尺寸/大小检查）属于完整编辑能力：挂在保留
    // 全量工具的图片编辑页验证。
    route: "imageEdit",
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      imageEdit: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/annotation-format-source.svg",
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
  });

  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  await expect(canvas).toBeVisible();
  await expect(page.getByText(/原图 80×40/)).toBeVisible();
  await expect(
    page.getByRole("button", { name: "检查文件大小" }),
  ).toBeVisible();
  await expect(page.locator('img[alt="最终输出预览"]')).toHaveCount(0);
  return { uploads };
}

test("JPEG and PNG outputs encode real decodable pixels with declared sizes", async ({
  page,
}) => {
  const { uploads } = await setupAnnotationFormatHost(page);

  // 指定尺寸 + JPEG：真实编码（类型、签名）、白底透明合成与声明尺寸一致。
  await page.getByLabel("输出格式").selectOption("image/jpeg");
  await expect(page.getByText(/透明区域将合成白色背景/)).toBeVisible();
  await page.getByLabel("JPEG 质量").selectOption("0.85");
  await page.getByLabel("输出宽度").fill("40");
  await page.getByLabel("输出高度").fill("20");
  await page.getByRole("button", { name: "应用尺寸" }).click();
  await expect(page.getByText(/输出 40×20/)).toBeVisible();

  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/jpeg");
  const jpegBytes = uploads[0]!.body;
  expect(jpegBytes[0]).toBe(0xff);
  expect(jpegBytes[1]).toBe(0xd8);
  expect(jpegBytes[2]).toBe(0xff);
  const decoded = await decodeUpload(page, jpegBytes, "image/jpeg");
  expect(decoded.width).toBe(40);
  expect(decoded.height).toBe(20);
  const transparent = pixelAt(decoded, 8, 10);
  expect(transparent[0]).toBeGreaterThan(235);
  expect(transparent[1]).toBeGreaterThan(235);
  expect(transparent[2]).toBeGreaterThan(235);
  const red = pixelAt(decoded, 32, 10);
  expect(Math.abs(red[0] - 224)).toBeLessThanOrEqual(20);
  expect(Math.abs(red[1] - 32)).toBeLessThanOrEqual(25);
  expect(Math.abs(red[2] - 32)).toBeLessThanOrEqual(25);
  await expectCommand(page, {
    scope: "imageEdit",
    action: "saveAnnotatedImage",
    arguments: {
      resourceUri:
        "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
    },
  });

  // 按需文件大小检查：明确点击才真实编码；不自动跟随编辑生成第二预览。
  await expect
    .poll(async () => {
      const button = page.getByRole("button", { name: "检查文件大小" });
      await button.click();
      return page.locator(".editor-operation-status").textContent();
    })
    .toContain("文件大小：image/jpeg · 实际");

  // 切回 PNG：保留透明 alpha（不再烧深色底），尺寸仍为指定值。
  await page.getByLabel("输出格式").selectOption("image/png");
  await expect(page.getByText(/PNG 保留透明度/)).toBeVisible();
  await expect
    .poll(async () => {
      const button = page.getByRole("button", { name: "检查文件大小" });
      await button.click();
      return page.locator(".editor-operation-status").textContent();
    })
    .toContain("文件大小：image/png · 实际");
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  expect(uploads[1]!.contentType).toBe("image/png");
  const pngBytes = uploads[1]!.body;
  expect(pngBytes.readUInt32BE(16)).toBe(40);
  expect(pngBytes.readUInt32BE(20)).toBe(20);
  const decodedPng = await decodeUpload(page, pngBytes, "image/png");
  // 透明区不烧入任何底色：RGB 全 0 且 alpha 为 0。
  const transparentOffset = (10 * 40 + 8) * 4;
  expect(decodedPng.pixels[transparentOffset]).toBe(0);
  expect(decodedPng.pixels[transparentOffset + 1]).toBe(0);
  expect(decodedPng.pixels[transparentOffset + 2]).toBe(0);
  expect(decodedPng.pixels[transparentOffset + 3]).toBe(0);
  expect(pixelAt(decodedPng, 32, 10)).toEqual([224, 32, 32]);
});

test("undo restores original output size with matching PNG headers", async ({
  page,
}) => {
  const { uploads } = await setupAnnotationFormatHost(page);

  // 默认 PNG：应用指定尺寸并保存，前态为真实 40×20 PNG。
  await page.getByLabel("输出宽度").fill("40");
  await page.getByLabel("输出高度").fill("20");
  await page.getByRole("button", { name: "应用尺寸" }).click();
  await expect(page.getByText(/输出 40×20/)).toBeVisible();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/png");
  expect(uploads[0]!.body.readUInt32BE(16)).toBe(40);
  expect(uploads[0]!.body.readUInt32BE(20)).toBe(20);

  // 撤销尺寸：回到原始输出尺寸，PNG 头部与声明一致。
  await page.getByRole("button", { name: "撤销" }).click();
  await expect(page.getByText(/输出 80×40/)).toBeVisible();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  expect(uploads[1]!.body.readUInt32BE(16)).toBe(80);
  expect(uploads[1]!.body.readUInt32BE(20)).toBe(40);
});

test("failed output preview preserves editing and reports failure without staying busy", async ({
  page,
}) => {
  await page.addInitScript(() => {
    HTMLCanvasElement.prototype.toBlob = (callback) => callback(null);
  });
  await page.route("**/preview-failure.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect width="80" height="40" fill="red"/></svg>',
    }),
  );
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      imageEdit: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: "/preview-failure.svg",
          mediaType: "image/png",
          byteLength: 100,
        },
      },
    },
  });
  await expect(
    page.getByRole("button", { name: "检查文件大小" }),
  ).toBeEnabled();
  await page.getByRole("button", { name: "检查文件大小" }).click();
  await expect(page.getByText("文件大小检查失败；可稍后重试。")).toBeVisible();
  await expect(page.getByRole("button", { name: "保存标注图" })).toBeEnabled();
  await expect(page.getByLabel("图片检查画布")).toBeVisible();
  // 失败可恢复：不再点击也保持可编辑，不遗留忙碌状态。
  await expect(
    page.getByRole("button", { name: "检查文件大小" }),
  ).toBeEnabled();
});
