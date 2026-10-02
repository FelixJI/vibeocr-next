import { expect, test, type Locator, type Page } from "@playwright/test";

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

/** blob URL 预览图不污染 canvas，可直接取样。 */
async function readPreviewPixels(
  element: Locator,
): Promise<DecodedImage & { src: string }> {
  return element.evaluate((node) => {
    const element = node as HTMLImageElement;
    const canvas = document.createElement("canvas");
    canvas.width = element.naturalWidth;
    canvas.height = element.naturalHeight;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("2d context unavailable");
    context.drawImage(element, 0, 0);
    const data = context.getImageData(0, 0, canvas.width, canvas.height).data;
    return {
      src: element.src,
      width: element.naturalWidth,
      height: element.naturalHeight,
      pixels: Array.from(data),
    };
  });
}

/** 把 160×80 原图坐标换算为页面 client 坐标（画布 900×600 内等比居中）。 */
async function naturalToClient(
  canvas: Locator,
  x: number,
  y: number,
): Promise<{ x: number; y: number }> {
  const box = await canvas.boundingBox();
  if (!box) throw new Error("canvas is not laid out");
  const scale = Math.min(900 / 160, 600 / 80);
  // 画布内部坐标（900×600 参考系）→ client 坐标按布局盒等比换算。
  const innerX = x * scale;
  const innerY = (600 - 80 * scale) / 2 + y * scale;
  return {
    x: box.x + (innerX / 900) * box.width,
    y: box.y + (innerY / 600) * box.height,
  };
}

const SOURCE_SVG = `<svg xmlns="http://www.w3.org/2000/svg" width="160" height="80">
  <rect width="160" height="80" fill="#ffffff"/>
  <rect x="0" y="0" width="80" height="80" fill="#101010"/>
  <rect x="100" y="20" width="40" height="40" fill="#e02020"/>
  <text x="12" y="46" font-family="Arial" font-size="14" fill="#eeeeee">机密123</text>
  <text x="104" y="14" font-family="Arial" font-size="10" fill="#202020">A1</text>
</svg>`;

test("exclusion regions bake opaque white pixels into the recognition upload only", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
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
  await page.route("**/recognition-exclusion-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: SOURCE_SVG,
    }),
  );
  await mountHost(page, {
    ...snapshot,
    capabilities: [
      "recognition.file",
      "recognition.clipboard",
      "recognition.results",
      "recognition.annotation",
      "recognition.screenshotSession",
    ],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: "/recognition-exclusion-source.svg",
          mediaType: "image/png",
          byteLength: 4096,
        },
        screenshotSession: {
          sessionId: "exclusion-e2e",
          revision: 0,
          textSelectionRequested: false,
          sceneEditing: false,
        },
      },
    },
  });

  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  await expect(canvas).toBeVisible();
  await expect(page.getByText(/原图 160×80/)).toBeVisible();

  // 先编辑后识别入口：复用既有 imageEdit 命令（宿主创建同一会话/修订），
  // 不新增协议；直接识别入口保持不变。
  await page.getByRole("button", { name: "选图编辑" }).click();
  await expectCommand(page, {
    scope: "imageEdit",
    action: "selectImage",
    arguments: {},
  });
  await page.getByRole("button", { name: "粘贴编辑" }).click();
  await expectCommand(page, {
    scope: "imageEdit",
    action: "readClipboard",
    arguments: {},
  });

  // 框选左半（敏感文字区）为排除区。
  await page.getByRole("button", { name: "屏蔽", exact: true }).click();
  await canvas.scrollIntoViewIfNeeded();
  const start = await naturalToClient(canvas, 0, 0);
  const end = await naturalToClient(canvas, 80, 80);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(end.x, end.y, { steps: 6 });
  await page.mouse.up();
  await expectCommand(page, {
    scope: "recognition",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: "exclusion-e2e", revision: 1 },
  });

  // 屏蔽副本入口出现；屏蔽导出预览为真实白色像素。
  await expect(
    page.getByRole("button", { name: "保存屏蔽副本" }),
  ).toBeVisible();
  const maskedPreview = page.locator('img[alt="屏蔽导出预览"]');
  await expect(maskedPreview).toBeVisible();
  await expect
    .poll(() => maskedPreview.evaluate((el) => el.naturalWidth))
    .toBe(160);
  const maskedPreviewPixels = await readPreviewPixels(maskedPreview);
  expect(pixelAt(maskedPreviewPixels, 40, 40)).toEqual([255, 255, 255]);
  expect(pixelAt(maskedPreviewPixels, 120, 40)).toEqual([224, 32, 32]);

  // 普通最终输出预览不包含掩膜：左半保持原黑底。
  const finalPreview = page.locator('img[alt="最终输出预览"]');
  await expect(finalPreview).toBeVisible();
  await expect
    .poll(() => finalPreview.evaluate((el) => el.naturalWidth))
    .toBe(160);
  const finalPreviewPixels = await readPreviewPixels(finalPreview);
  expect(pixelAt(finalPreviewPixels, 40, 40)).toEqual([16, 16, 16]);

  // 识别当前图：上传副本中屏蔽区为纯白，未屏蔽像素保持原样。
  await page.getByRole("button", { name: "识别当前图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/png");
  const recognitionInput = await decodeUpload(
    page,
    uploads[0]!.body,
    "image/png",
  );
  expect(recognitionInput.width).toBe(160);
  expect(recognitionInput.height).toBe(80);
  // 屏蔽区中心与边缘均不含原始文字像素。
  expect(pixelAt(recognitionInput, 40, 40)).toEqual([255, 255, 255]);
  expect(pixelAt(recognitionInput, 79, 79)).toEqual([255, 255, 255]);
  // 屏蔽区之外不发生非预期变更：红块与右半白底保持。
  expect(pixelAt(recognitionInput, 120, 40)).toEqual([224, 32, 32]);
  expect(pixelAt(recognitionInput, 150, 70)).toEqual([255, 255, 255]);
  await expectCommand(page, {
    scope: "recognition",
    action: "recognizeScreenshotImage",
    arguments: {
      resourceUri:
        "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      sessionId: "exclusion-e2e",
      revision: 1,
    },
  });
  await expect(page.getByText(/已提交识别当前图（image\/png/)).toBeVisible();

  // 普通保存：原图像素保留，屏蔽区不写入。
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  const normalSave = await decodeUpload(page, uploads[1]!.body, "image/png");
  expect(pixelAt(normalSave, 40, 40)).toEqual([16, 16, 16]);
  expect(pixelAt(normalSave, 120, 40)).toEqual([224, 32, 32]);

  // 显式屏蔽副本导出：真实白色像素，与识别输入一致。
  await page.getByRole("button", { name: "保存屏蔽副本" }).click();
  await expect.poll(() => uploads.length).toBe(3);
  const maskedSave = await decodeUpload(page, uploads[2]!.body, "image/png");
  expect(pixelAt(maskedSave, 40, 40)).toEqual([255, 255, 255]);
  expect(pixelAt(maskedSave, 120, 40)).toEqual([224, 32, 32]);

  // 补第二个排除区覆盖剩余区域：整图被屏蔽时拒绝识别且不产生新上传。
  await canvas.scrollIntoViewIfNeeded();
  const rest = await naturalToClient(canvas, 80, 0);
  const restEnd = await naturalToClient(canvas, 160, 80);
  await page.mouse.move(rest.x, rest.y);
  await page.mouse.down();
  await page.mouse.move(restEnd.x, restEnd.y, { steps: 6 });
  await page.mouse.up();
  await expectCommand(page, {
    scope: "recognition",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: "exclusion-e2e", revision: 2 },
  });
  await page.getByRole("button", { name: "识别当前图" }).click();
  await expect(
    page.getByText(
      "整张图都在屏蔽区内：没有可识别内容，已取消本次识别；原图与屏蔽区保持不变，可撤销后重试。",
    ),
  ).toBeVisible();
  await page.waitForTimeout(300);
  expect(uploads.length).toBe(3);

  // 撤销第二个排除区后可再次识别（新修订）。
  await page.getByRole("button", { name: "撤销" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: "exclusion-e2e", revision: 3 },
  });
  await page.getByRole("button", { name: "识别当前图" }).click();
  await expect.poll(() => uploads.length).toBe(4);
  const retryInput = await decodeUpload(page, uploads[3]!.body, "image/png");
  expect(pixelAt(retryInput, 120, 40)).toEqual([224, 32, 32]);
});
