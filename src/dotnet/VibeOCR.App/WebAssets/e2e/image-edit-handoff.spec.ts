import { expect, test, type Page } from "@playwright/test";

import { mountHost, sendState, snapshot } from "./workbench-host";

/**
 * 显式识别交接（mock host 边界模拟，非真实 scene 窗口/真实 C# 宿主）：
 * 生产 React/router/bridge 全真实；仅原生边界按新合同回显冻结的
 * 未烘焙普通基准 + excludeBoxes。宿主侧行为由 App 单测覆盖。
 */

interface DecodedImage {
  width: number;
  height: number;
  pixels: number[];
}

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

function pixelAt(image: DecodedImage, x: number, y: number): number[] {
  const offset = (y * image.width + x) * 4;
  return [
    image.pixels[offset] ?? -1,
    image.pixels[offset + 1] ?? -1,
    image.pixels[offset + 2] ?? -1,
    image.pixels[offset + 3] ?? -1,
  ];
}

test("explicit recognition handoff keeps edited ordinary pixels, mask and route", async ({
  page,
}) => {
  test.setTimeout(120_000);
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
  // 80×40：左半透明、右半红；旋转 90° 后输出 40×80。
  await page.route("**/handoff-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect x="40" width="40" height="40" fill="#e02020"/></svg>',
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
        recognition: {
          isBusy: false,
          statusCode: "recognition.session",
          input: {
            url: "/handoff-source.svg",
            mediaType: "image/png",
            byteLength: 2048,
          },
          screenshotSession: {
            sessionId: "handoff-e2e",
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

  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  await expect(canvas).toBeVisible();
  // 旋转成竖图 + 真实标注 + 部分遮罩：全部由生产编辑器完成。
  await page.getByRole("button", { name: "旋转 90°" }).click();
  await expect(page.getByText(/输出 40×80/)).toBeVisible();
  await page.getByRole("button", { name: "矩形" }).click();
  await canvas.scrollIntoViewIfNeeded();
  const box = async () => (await canvas.boundingBox())!;
  // 旋转后图像显示区约在画布 attr x∈[300,600]/900：用画布包围盒相对坐标。
  const first = await box();
  await page.mouse.move(
    first.x + first.width * 0.55,
    first.y + first.height * 0.45,
  );
  await page.mouse.down();
  await page.mouse.move(
    first.x + first.width * 0.65,
    first.y + first.height * 0.55,
    { steps: 4 },
  );
  await page.mouse.up();
  await page
    .getByRole("toolbar", { name: "图片编辑工具" })
    .getByRole("button", { name: "屏蔽", exact: true })
    .click();
  const second = await box();
  await page.mouse.move(
    second.x + second.width * 0.4,
    second.y + second.height * 0.3,
  );
  await page.mouse.down();
  await page.mouse.move(
    second.x + second.width * 0.55,
    second.y + second.height * 0.6,
    { steps: 6 },
  );
  await page.mouse.up();
  await expect(page.getByRole("button", { name: "清空屏蔽" })).toBeEnabled();

  // 显式识别：上传未烘焙普通最终像素 + 归一化排除框。
  await page.getByRole("button", { name: "识别当前图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/png");
  const recognizeCommand = await page.evaluate(() =>
    (
      window as unknown as {
        __testHost: {
          commands: {
            scope: string;
            action: string;
            arguments: Record<string, unknown>;
          }[];
        };
      }
    ).__testHost.commands.find(
      (command) => command.action === "recognizeScreenshotImage",
    ),
  );
  expect(recognizeCommand?.arguments.excludeBoxes).toHaveLength(1);
  const frozenBytes = uploads[0]!.body;
  const frozen = await decodeUpload(page, frozenBytes, "image/png");
  expect(frozen.width).toBe(40);
  expect(frozen.height).toBe(80);

  // 模拟原生边界（mock host）：回显冻结的未烘焙普通基准 + excludeBoxes，
  // 并按 C# 交接语义把主窗口路由切到识别承载面。
  await page.route("**/handoff-frozen.png", (route) =>
    route.fulfill({ contentType: "image/png", body: frozenBytes }),
  );
  await sendState(page, "recognition", {
    isBusy: false,
    statusCode: "recognition.running",
    input: {
      url: "/handoff-frozen.png",
      mediaType: "image/png",
      byteLength: frozenBytes.length,
    },
    screenshotSession: {
      sessionId: "handoff-e2e",
      revision: 0,
      textSelectionRequested: false,
      sceneEditing: false,
      excludeBoxes: recognizeCommand.arguments.excludeBoxes,
    },
  });
  await sendState(page, "shell", { route: "recognition" });
  await expect(page.getByRole("heading", { name: "单次识别" })).toBeVisible();

  // 识别页：冻结基准保持已编辑普通像素（40×80，非原图 80×40），
  // 遮罩标记已重建且可清除；不再退回原图。
  await expect(page.getByText(/原图 40×80/)).toBeVisible();
  const clearMask = page.getByRole("button", { name: "清空屏蔽" });
  await expect(clearMask).toBeEnabled();
  await clearMask.click();
  await expect(page.getByText(/已清空全部屏蔽区/)).toBeVisible();

  // 导出普通图：内容/方向/尺寸与冻结基准一致（清空遮罩不改变像素）。
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  const exported = await decodeUpload(page, uploads[1]!.body, "image/png");
  expect(exported.width).toBe(40);
  expect(exported.height).toBe(80);
  expect(exported.pixels).toEqual(frozen.pixels);
  // 方向证据：旋转后红区位于下半，透明区位于上半（未退回原图 80×40）。
  const redBottom = pixelAt(exported, 20, 72);
  const transparentTop = pixelAt(exported, 20, 8);
  expect(redBottom[0]).toBeGreaterThan(200);
  expect(redBottom[3]).toBe(255);
  expect(transparentTop[3]).toBe(0);
});
