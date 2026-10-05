import { expect, test } from "@playwright/test";
import { expectCommand, mountHost } from "./workbench-host";

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
      },
    });
    document.addEventListener("DOMContentLoaded", () => {
      document.documentElement.dataset.captureScene = "true";
      // 浏览器中模拟原生冻结背景，测试不读取用户桌面。
      const background = document.createElement("div");
      background.style.cssText =
        "position:fixed;inset:0;z-index:-1;background:#344252";
      background.innerHTML =
        '<div style="margin:40px;background:#e9eef4;color:#303a44;padding:24px;font:18px Segoe UI;height:580px">示例桌面文档 · 截图冻结画面</div>';
      document.body.prepend(background);
    });
  });
  const uploads: Buffer[] = [];
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
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await page.mouse.move(450, 150);
  await page.mouse.down();
  await page.mouse.move(560, 240);
  await page.mouse.up();
  await page.screenshot({
    path: testInfo.outputPath("scene-at-selection.png"),
  });
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
