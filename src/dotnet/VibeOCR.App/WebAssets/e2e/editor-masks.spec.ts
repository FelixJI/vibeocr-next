import { expect, test, type Page } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

/**
 * Goal #227 掩码能力（遮挡/笔刷效果/掩膜修补/重复匹配）的真实浏览器
 * 导出像素回归：全部走真实 UI 操作 → 真实编码 PNG → 浏览器解码逐像素
 * 断言，不依赖 UI 遮罩或 mock 像素。
 */

interface PixelRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface DecodedImage {
  width: number;
  height: number;
  pixels: Buffer;
}

async function decodeUpload(
  page: Page,
  body: Buffer,
  mediaType: string,
  region?: PixelRegion,
): Promise<DecodedImage> {
  const decoded = await page.evaluate(
    async ({ base64, mediaType, region }) => {
      const binary = atob(base64);
      const bytes = new Uint8Array(binary.length);
      for (let index = 0; index < binary.length; index += 1) {
        bytes[index] = binary.charCodeAt(index);
      }
      const bitmap = await createImageBitmap(
        new Blob([bytes], { type: mediaType }),
      );
      const canvas = document.createElement("canvas");
      canvas.width = region?.width ?? bitmap.width;
      canvas.height = region?.height ?? bitmap.height;
      const context = canvas.getContext("2d");
      if (!context) throw new Error("2d context unavailable");
      context.drawImage(
        bitmap,
        region?.x ?? 0,
        region?.y ?? 0,
        canvas.width,
        canvas.height,
        0,
        0,
        canvas.width,
        canvas.height,
      );
      bitmap.close();
      const data = context.getImageData(0, 0, canvas.width, canvas.height).data;
      // 用紧凑字节跨Playwright协议，避免为每个RGBA字节序列化一个JS数字。
      let binaryRgba = "";
      for (let offset = 0; offset < data.length; offset += 32768) {
        binaryRgba += String.fromCharCode(
          ...data.subarray(offset, offset + 32768),
        );
      }
      return {
        width: canvas.width,
        height: canvas.height,
        rgbaBase64: btoa(binaryRgba),
      };
    },
    { base64: body.toString("base64"), mediaType, region },
  );
  const pixels = Buffer.from(decoded.rgbaBase64, "base64");
  expect(pixels.length).toBe(decoded.width * decoded.height * 4);
  return { width: decoded.width, height: decoded.height, pixels };
}

function pixelAt(image: DecodedImage, x: number, y: number): number[] {
  const offset = (y * image.width + x) * 4;
  return Array.from(image.pixels.subarray(offset, offset + 4));
}

function expectSameAlpha(baseline: DecodedImage, actual: DecodedImage): void {
  const alpha = (image: DecodedImage) =>
    Buffer.from(image.pixels.filter((_, index) => index % 4 === 3));
  expect(Buffer.compare(alpha(baseline), alpha(actual))).toBe(0);
}

async function routeSource(page: Page, svgBody: string, path: string) {
  await page.route(`**/${path}`, (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: svgBody,
    }),
  );
}

async function mountWithUploadCapture(
  page: Page,
  uploads: { contentType: string; body: Buffer }[],
  sourcePath: string,
) {
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
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      imageEdit: {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: `/${sourcePath}`,
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
  });
}

/** 画布 900×600 属性空间拖拽；图像映射：display = (x*scale, offY+y*scale)。 */
function makeCanvasMapper(
  imageWidth: number,
  imageHeight: number,
): (at: { x: number; y: number }) => { x: number; y: number } {
  const scale = Math.min(900 / imageWidth, 600 / imageHeight);
  const offsetX = (900 - imageWidth * scale) / 2;
  const offsetY = (600 - imageHeight * scale) / 2;
  return (at) => ({
    x: offsetX + at.x * scale,
    y: offsetY + at.y * scale,
  });
}

async function pointerStroke(
  page: Page,
  points: readonly { x: number; y: number }[],
): Promise<void> {
  expect(points.length).toBeGreaterThanOrEqual(2);
  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  const box = await canvas.boundingBox();
  expect(box).toBeTruthy();
  const toClient = (at: { x: number; y: number }) => ({
    x: box!.x + (at.x / 900) * box!.width,
    y: box!.y + (at.y / 600) * box!.height,
  });
  const first = toClient(points[0]!);
  await page.mouse.move(first.x, first.y);
  await page.mouse.down();
  for (const at of points.slice(1)) {
    const client = toClient(at);
    await page.mouse.move(client.x, client.y, { steps: 4 });
  }
  await page.mouse.up();
}

async function clickCanvasAt(
  page: Page,
  at: { x: number; y: number },
): Promise<void> {
  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  const box = await canvas.boundingBox();
  expect(box).toBeTruthy();
  await page.mouse.click(
    box!.x + (at.x / 900) * box!.width,
    box!.y + (at.y / 600) * box!.height,
  );
}

async function saveAndDecode(
  page: Page,
  uploads: { contentType: string; body: Buffer }[],
  region?: PixelRegion,
): Promise<DecodedImage> {
  const index = uploads.length;
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(index + 1);
  await expect(
    page.getByRole("button", { name: "保存标注图", exact: true }),
  ).toBeEnabled();
  expect(uploads[index]!.contentType).toBe("image/png");
  return decodeUpload(page, uploads[index]!.body, "image/png", region);
}

async function setRangeValue(
  page: Page,
  label: string,
  value: string,
): Promise<void> {
  const slider = page.getByLabel(label);
  await expect(slider).toBeVisible();
  await slider.evaluate((element, next) => {
    const input = element as HTMLInputElement;
    const setter = Object.getOwnPropertyDescriptor(
      HTMLInputElement.prototype,
      "value",
    )?.set;
    setter?.call(input, next);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }, value);
}

/** 源外像素逐字节一致（RGBA）。 */
function expectOutsideRegionIdentical(
  baseline: DecodedImage,
  actual: DecodedImage,
  inside: (x: number, y: number) => boolean,
): void {
  expect(actual.width).toBe(baseline.width);
  expect(actual.height).toBe(baseline.height);
  for (let y = 0; y < baseline.height; y += 1) {
    for (let x = 0; x < baseline.width; x += 1) {
      if (inside(x, y)) continue;
      const offset = (y * baseline.width + x) * 4;
      for (let channel = 0; channel < 4; channel += 1) {
        if (
          actual.pixels[offset + channel] !== baseline.pixels[offset + channel]
        ) {
          expect(pixelAt(actual, x, y), `outside pixel (${x},${y})`).toEqual(
            pixelAt(baseline, x, y),
          );
        }
      }
    }
  }
}

test("brush mosaic/blur cover the trail only, keep transparency, support continuous intensity and transform", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  // 240×120：左半绿底（含洋红/黄色块）、右半全透明；笔刷马赛克/模糊
  // 轨迹跨越色块与透明边界（平坦纯色上马赛克/模糊不变色是预期行为）。
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="120"><rect width="120" height="120" fill="#0aa05a"/><rect x="40" y="20" width="24" height="16" fill="#e020f0"/><rect x="40" y="80" width="24" height="16" fill="#ffe000"/></svg>',
    "masks-brush-source.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-brush-source.svg");
  await expect(page.locator('canvas[aria-label="图片检查画布"]')).toBeVisible();
  await expect(page.getByText(/原图 240×120/)).toBeVisible();
  const map = makeCanvasMapper(240, 120);
  const toDisplay = (x: number, y: number) => map({ x, y });

  const baseline = await saveAndDecode(page, uploads);
  expect(baseline.width).toBe(240);
  expect(baseline.height).toBe(120);
  // 右半透明：基线 alpha 0。
  expect(pixelAt(baseline, 200, 60)[3]).toBe(0);

  // —— 马赛克笔刷：方式=笔刷、笔刷 64px，轨迹沿 y≈28 从 x=30 到
  // x=150，横穿洋红块 (40..64, 20..36) 与透明边界 x=120。
  await page.getByRole("button", { name: "马赛克" }).click();
  await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
  await page.getByLabel("马赛克与模糊笔刷大小").selectOption("64");
  await pointerStroke(page, [
    toDisplay(30, 28),
    toDisplay(80, 30),
    toDisplay(150, 28),
  ]);
  await expect(page.getByRole("button", { name: "撤销" })).toBeEnabled();

  const mosaicked = await saveAndDecode(page, uploads);
  // 马赛克单元小于色块时内部单元保持纯色是预期：断言取色块边界混合点
  // （左缘 x≈40、右缘 x≈64），远离轨迹完全一致。
  expect(pixelAt(mosaicked, 41, 28)).not.toEqual(pixelAt(baseline, 41, 28));
  expect(pixelAt(mosaicked, 63, 28)).not.toEqual(pixelAt(baseline, 63, 28));
  expectOutsideRegionIdentical(
    baseline,
    mosaicked,
    (x, y) => y >= 16 && y <= 44 && x >= 20 && x <= 160,
  );
  // 透明侧轨迹：RGB 可能被带入邻近颜色，但 alpha 保持 0（透明不丢）。
  expect(pixelAt(mosaicked, 140, 28)[3]).toBe(0);
  expectSameAlpha(baseline, mosaicked);

  // —— 模糊笔刷：下沿另一条短轨迹横穿黄色块 (40..64, 80..96)。
  await page.getByRole("button", { name: "模糊" }).click();
  await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
  await pointerStroke(page, [
    toDisplay(30, 88),
    toDisplay(80, 90),
    toDisplay(150, 86),
  ]);
  const blurred = await saveAndDecode(page, uploads);
  expectSameAlpha(baseline, blurred);
  expect(pixelAt(blurred, 41, 88)).not.toEqual(pixelAt(baseline, 41, 88));
  expect(pixelAt(blurred, 63, 88)).not.toEqual(pixelAt(baseline, 63, 88));
  expectOutsideRegionIdentical(
    mosaicked,
    blurred,
    (x, y) => y >= 74 && y <= 102 && x >= 20 && x <= 160,
  );

  // —— 连续强度：选择工具点击马赛克轨迹中点选中对象，强度 2.0→3.0。
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await expect(page.getByText(/2\.0 中/)).toBeHidden();
  await clickCanvasAt(page, toDisplay(80, 30));
  await expect(page.getByText(/2\.0 中/)).toBeVisible();
  await setRangeValue(page, "马赛克与模糊强度", "2.4");
  await expect(page.getByText("2.4", { exact: true })).toBeVisible();
  const intensified = await saveAndDecode(page, uploads);
  // 强度 3.0 块更大：洋红块左缘的混合比例改变，轨迹外不变。
  expect(pixelAt(intensified, 41, 28)).not.toEqual(pixelAt(blurred, 41, 28));
  expectOutsideRegionIdentical(
    blurred,
    intensified,
    (x, y) => y >= 12 && y <= 48 && x >= 18 && x <= 162,
  );

  // —— 整图旋转 90° 后导出：效果随真实像素变换，尺寸 120×240。
  await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
  const rotated = await saveAndDecode(page, uploads);
  expect(rotated.width).toBe(120);
  expect(rotated.height).toBe(240);
  // 原 (41,28)（洋红块左缘混合点）旋转后落在 (120-1-28, 41)=(91,41)：
  // 仍是混合后的马赛克像素，不再是纯洋红也不是纯背景。
  const rotatedPixel = pixelAt(rotated, 91, 41);
  expect(rotatedPixel).not.toEqual([224, 32, 240, 255]);
  expect(rotatedPixel).not.toEqual(pixelAt(baseline, 41, 28));

  // —— 撤销全部回到基线。
  await expect(page.getByRole("button", { name: "撤销" })).toBeEnabled();
  await page.getByRole("button", { name: "清除编辑" }).click();
  const cleared = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(Buffer.from(cleared.pixels), Buffer.from(baseline.pixels)),
  ).toBe(0);
});

test("solid occlusion bakes opaque pixels into normal export while exclusion does not", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="120"><rect width="240" height="120" fill="#0aa05a" fill-opacity="0.4"/><rect x="40" y="20" width="24" height="16" fill="#e020f0"/></svg>',
    "masks-solid-source.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-solid-source.svg");
  await expect(page.getByText(/原图 240×120/)).toBeVisible();
  const map = makeCanvasMapper(240, 120);
  const toDisplay = (x: number, y: number) => map({ x, y });
  const baseline = await saveAndDecode(page, uploads);

  // 遮挡：纯色不透明（默认黑）写入普通导出。
  await page.getByRole("button", { name: "遮挡" }).click();
  await pointerStroke(page, [toDisplay(30, 40), toDisplay(110, 80)]);
  const solid = await saveAndDecode(page, uploads);
  expect(pixelAt(solid, 70, 60)).toEqual([0, 0, 0, 255]);

  await page.getByRole("button", { name: "遮挡" }).click();
  await pointerStroke(page, [toDisplay(120.3, 85.3), toDisplay(140.7, 100.7)]);
  const fractional = await saveAndDecode(page, uploads);
  expect(pixelAt(baseline, 120, 85)[3]).toBeLessThan(255);
  for (const [x, y] of [
    [120, 85],
    [140, 85],
    [120, 100],
    [140, 100],
  ] as const) {
    expect(pixelAt(fractional, x, y)).toEqual([0, 0, 0, 255]);
  }
  await page.getByRole("button", { name: "撤销", exact: true }).click();

  expectOutsideRegionIdentical(
    baseline,
    solid,
    (x, y) => x >= 27 && x <= 113 && y >= 37 && y <= 83,
  );

  // 屏蔽：普通导出完全不变（与基线逐字节一致），只进识别输入/屏蔽副本。
  await page.getByRole("button", { name: "屏蔽", exact: true }).click();
  await pointerStroke(page, [toDisplay(140, 30), toDisplay(220, 70)]);
  const normal = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(Buffer.from(normal.pixels), Buffer.from(solid.pixels)),
  ).toBe(0);

  // 屏蔽副本：排除区写入不透明白；遮挡区保持黑色真实像素。
  const index = uploads.length;
  await page.getByRole("button", { name: "保存屏蔽副本", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(index + 1);
  const masked = await decodeUpload(page, uploads[index]!.body, "image/png");
  expect(pixelAt(masked, 180, 50)).toEqual([255, 255, 255, 255]);
  expect(pixelAt(masked, 70, 60)).toEqual([0, 0, 0, 255]);
  expectOutsideRegionIdentical(
    solid,
    masked,
    (x, y) =>
      (x >= 137 && x <= 223 && y >= 27 && y <= 73) ||
      (x >= 27 && x <= 113 && y >= 37 && y <= 83),
  );
});

test("mask add/remove with a hole inpaints real worker pixels and preserves hole plus outside bytes", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  // 96×48：绿底 + 洋红水印 (32,12,32,16)。掩膜覆盖水印并挖中心洞。
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#0aa05a"/><rect x="32" y="12" width="32" height="16" fill="#e020f0"/><rect x="40" y="14" width="16" height="12" fill="#0aa05a"/><rect x="47" y="19" width="2" height="2" fill="#e020f0"/></svg>',
    "masks-hole-source.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-hole-source.svg");
  await expect(page.getByText(/原图 96×48/)).toBeVisible();
  const map = makeCanvasMapper(96, 48);
  const toDisplay = (x: number, y: number) => map({ x, y });
  const baseline = await saveAndDecode(page, uploads);
  const magenta = pixelAt(baseline, 48, 20);
  expect(magenta.slice(0, 3)).toEqual([224, 32, 240]);

  await page.getByRole("button", { name: "去水印" }).click();
  // 框选添加：覆盖整块水印（外扩后 (30,10)-(66,30)）。
  await pointerStroke(page, [toDisplay(30.5, 10.5), toDisplay(65.5, 29.5)]);
  await expect(page.getByText(/已框选修补区域/)).toBeVisible();
  // 框选移除：挖中心洞 (42,16)-(54,24)。
  await page.getByLabel("修补掩膜绘制方式").selectOption("rect-remove");
  await pointerStroke(page, [toDisplay(42.5, 16.5), toDisplay(53.5, 23.5)]);
  // 笔刷添加：右端水印边缘补一小段（x≈64 处覆盖列 62-66）。
  await page.getByLabel("修补掩膜绘制方式").selectOption("brush-add");
  await page.getByLabel("修补掩膜笔刷大小").selectOption("10");
  await pointerStroke(page, [toDisplay(63.5, 20.5), toDisplay(66.5, 22.5)]);

  await page.getByRole("button", { name: "预览修补" }).click();
  await expect(page.getByText(/修补预览完成/)).toBeVisible({
    timeout: 30_000,
  });
  const readPreviewPixel = () =>
    page.locator('canvas[aria-label="图片检查画布"]').evaluate((element) => {
      const canvas = element as HTMLCanvasElement;
      return Array.from(
        canvas
          .getContext("2d")!
          .getImageData(
            Math.floor((320 * canvas.width) / 900),
            Math.floor((260 * canvas.height) / 600),
            1,
            1,
          ).data,
      );
    });
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
  const visibleAfter = await readPreviewPixel();
  await page.getByLabel("修补对比").selectOption("before");
  await expect.poll(readPreviewPixel).toEqual([224, 32, 240, 255]);

  const previewOnly = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(
      Buffer.from(previewOnly.pixels),
      Buffer.from(baseline.pixels),
    ),
  ).toBe(0);
  await expect(page.getByRole("button", { name: "撤销" })).toBeDisabled();
  await page.getByLabel("修补对比").selectOption("after");
  await page.getByRole("button", { name: "应用修补" }).click();
  await expect(page.getByText(/已应用修补/)).toBeVisible();

  const applied = await saveAndDecode(page, uploads);
  await expect.poll(readPreviewPixel).toEqual(visibleAfter);
  // 洞内：水印洋红原样保留（含字节）。
  expect(pixelAt(applied, 48, 20)).toEqual(magenta);
  // 环形掩膜：恢复背景绿；采样点距洞边界≥6px，避免平滑渗色合入断言。
  for (const [x, y] of [
    [34, 20],
    [48, 12],
    [60, 22],
    [64, 21],
  ] as const) {
    const filled = pixelAt(applied, x, y);
    expect(Math.abs(filled[0]! - 10)).toBeLessThanOrEqual(12);
    expect(Math.abs(filled[1]! - 160)).toBeLessThanOrEqual(12);
    expect(Math.abs(filled[2]! - 90)).toBeLessThanOrEqual(12);
  }
  // 掩膜外逐字节一致（含洞外的未选水印边缘列 32 与背景）。
  expectOutsideRegionIdentical(
    baseline,
    applied,
    (x, y) =>
      x >= 30 &&
      x < 72 &&
      y >= 10 &&
      y < 30 &&
      !(x >= 42 && x < 54 && y >= 16 && y < 24),
  );

  // 撤销恰好恢复原始字节。
  await page.getByRole("button", { name: "撤销" }).click();
  const undone = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(Buffer.from(undone.pixels), Buffer.from(baseline.pixels)),
  ).toBe(0);
});

test("duplicate match previews candidates, applies once, follows rotation, and undoes", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  // 96×48：灰底 + 同一 10×8 图案三份（A 源、B、C 非网格偏移副本）；
  // 源选区与图案严格对齐，候选恰为副本位置且环境环为纯灰。
  const pattern = (ox: number, oy: number) =>
    `<rect x="${ox}" y="${oy}" width="5" height="4" fill="#ff00ff"/>` +
    `<rect x="${ox + 5}" y="${oy}" width="5" height="4" fill="#00c8ff"/>` +
    `<rect x="${ox}" y="${oy + 4}" width="3" height="4" fill="#ffe000"/>` +
    `<rect x="${ox + 3}" y="${oy + 4}" width="7" height="4" fill="#ff00ff"/>`;
  await routeSource(
    page,
    `<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#808080"/>${pattern(
      10,
      6,
    )}${pattern(33, 25)}${pattern(60, 30)}</svg>`,
    "masks-duplicate-source.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-duplicate-source.svg");
  await expect(page.getByText(/原图 96×48/)).toBeVisible();
  const map = makeCanvasMapper(96, 48);
  const toDisplay = (x: number, y: number) => map({ x, y });
  const baseline = await saveAndDecode(page, uploads);
  const patternAt = (x: number, y: number) => pixelAt(baseline, x, y);
  // 图案像素确证：A 内 (12,8) 为洋红象限。
  expect(patternAt(12, 8).slice(0, 3)).toEqual([255, 0, 255]);

  await page.evaluate(() => {
    const original = CanvasRenderingContext2D.prototype.strokeRect;
    CanvasRenderingContext2D.prototype.strokeRect = function (...args) {
      if (this.strokeStyle === "#12a150") {
        document.body.dataset.duplicateLineWidth = String(this.lineWidth);
        document.body.dataset.duplicateFont = this.font;
      }
      return original.apply(this, args);
    };
  });
  await page.getByRole("button", { name: "重复匹配" }).click();
  // 框选源 A：外扩取整后恰为 (10,6,10,8) 图案本身。
  await pointerStroke(page, [toDisplay(10.5, 6.5), toDisplay(19.5, 13.5)]);
  await expect(page.getByText(/已框选重复内容源区域/)).toBeVisible();
  await page.getByRole("button", { name: "查找重复" }).click();
  await expect(page.getByText(/找到 2 处重复内容/)).toBeVisible({
    timeout: 30_000,
  });

  await expect(page.locator("body")).toHaveAttribute(
    "data-duplicate-line-width",
    "2",
  );
  await expect(page.locator("body")).toHaveAttribute(
    "data-duplicate-font",
    "12px sans-serif",
  );

  // 确认前导出与基线逐字节一致（候选只是预览）。
  const beforeConfirm = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(
      Buffer.from(beforeConfirm.pixels),
      Buffer.from(baseline.pixels),
    ),
  ).toBe(0);

  // 逐项增删：取消候选 2（C 的副本）。
  await page.getByRole("checkbox", { name: "候选 2" }).uncheck();
  await page.getByRole("button", { name: "确认应用重复遮挡" }).click();
  await expect(page.getByText(/已应用 1 处重复内容遮挡/)).toBeVisible();
  // 恰好一次提交：撤销可用且一步回到基线。
  await expect(page.getByRole("button", { name: "撤销" })).toBeEnabled();

  const applied = await saveAndDecode(page, uploads);
  // B（候选 1，(33,25,10,8)）：被周边环境色（纯灰 128）真实遮挡。
  expect(pixelAt(applied, 37, 28)).toEqual([128, 128, 128, 255]);
  expectOutsideRegionIdentical(
    baseline,
    applied,
    (x, y) => x >= 33 && x < 43 && y >= 25 && y < 33,
  );
  // C：未勾选，图案原样保留。
  expect(pixelAt(applied, 62, 32)).toEqual(patternAt(62, 32));

  // 旋转 90° 后导出：遮挡随真实像素变换（48×96）；B 中心 (38,29) →
  // (48-1-29, 38)=(18,38)。
  await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
  const rotated = await saveAndDecode(page, uploads);
  expect(rotated.width).toBe(48);
  expect(rotated.height).toBe(96);
  expect(pixelAt(rotated, 18, 38)).toEqual([128, 128, 128, 255]);
  await page.getByRole("button", { name: "裁剪", exact: true }).click();
  const turnedMap = makeCanvasMapper(48, 96);
  await pointerStroke(page, [
    turnedMap({ x: 8, y: 16 }),
    turnedMap({ x: 40, y: 80 }),
  ]);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  const cropped = await saveAndDecode(page, uploads);
  expect(cropped.width).toBe(32);
  expect(cropped.height).toBe(64);
  expect(pixelAt(cropped, 10, 22)).toEqual([128, 128, 128, 255]);
  await page.getByRole("button", { name: "撤销" }).click();
  // 撤销两步（旋转、应用）回到基线。
  await page.getByRole("button", { name: "撤销" }).click();
  await page.getByRole("button", { name: "撤销" }).click();
  const undone = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(Buffer.from(undone.pixels), Buffer.from(baseline.pixels)),
  ).toBe(0);
});

test("R2 non-1:1 rectangle mosaic preserves the legacy 1/2/3 cell anchors in real PNG pixels", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  const stripes = Array.from(
    { length: 129 },
    (_, index) =>
      '<rect x="' +
      index * 14 +
      '" width="14" height="1200" fill="rgb(' +
      index +
      "," +
      ((index * 13) % 256) +
      "," +
      ((index * 29) % 256) +
      ')"/>',
  ).join("");
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="1800" height="1200">' +
      stripes +
      "</svg>",
    "masks-scale-anchors.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-scale-anchors.svg");
  await expect(page.getByText(/原图 1800×1200/)).toBeVisible();
  const map = makeCanvasMapper(1800, 1200);
  for (const [intensity, cell] of [
    [1, 16],
    [2, 28],
    [3, 52],
  ] as const) {
    await page.getByRole("button", { name: "马赛克", exact: true }).click();
    await setRangeValue(page, "马赛克与模糊强度", String(intensity));
    await pointerStroke(page, [
      map({ x: 0, y: 300 }),
      map({ x: 1800, y: 404 }),
    ]);
    const strip = await saveAndDecode(page, uploads, {
      x: 0,
      y: 350,
      width: 1800,
      height: 1,
    });
    const png = uploads.at(-1)!.body;
    expect(png.readUInt32BE(16)).toBe(1800);
    expect(png.readUInt32BE(20)).toBe(1200);
    const runs: number[] = [];
    let previous = "";
    for (let x = 0; x < strip.width; x++) {
      const color = pixelAt(strip, x, 0).join(",");
      if (color === previous) runs[runs.length - 1]! += 1;
      else {
        runs.push(1);
        previous = color;
      }
    }
    // 全宽由画布边界夹取，避免鼠标浮点投影在整除处多出一列采样。
    const samples = Math.ceil(1800 / cell);
    const expectedRuns: number[] = [];
    let previousSample = -1;
    for (let x = 0; x < 1800; x++) {
      const sample = Math.floor(((x + 0.5) * samples) / 1800);
      if (sample === previousSample)
        expectedRuns[expectedRuns.length - 1]! += 1;
      else {
        expectedRuns.push(1);
        previousSample = sample;
      }
    }
    expect(runs).toEqual(expectedRuns);
    await page.getByRole("button", { name: "清除编辑", exact: true }).click();
  }
});

test("R7 a full-image mask with a known hole previews and applies real worker pixels", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="48"><rect width="64" height="48" fill="#e020f0"/><rect x="27" y="19" width="10" height="10" fill="#0aa05a"/></svg>',
    "masks-full-roi-hole.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-full-roi-hole.svg");
  await expect(page.getByText(/原图 64×48/)).toBeVisible();
  const baseline = await saveAndDecode(page, uploads);
  const map = makeCanvasMapper(64, 48);
  await page.getByRole("button", { name: "去水印", exact: true }).click();
  await pointerStroke(page, [
    map({ x: 0.01, y: 0.01 }),
    map({ x: 63.99, y: 47.99 }),
  ]);
  await page.getByLabel("修补掩膜绘制方式").selectOption("rect-remove");
  await pointerStroke(page, [
    map({ x: 27.5, y: 19.5 }),
    map({ x: 36.5, y: 28.5 }),
  ]);
  await page.getByRole("button", { name: "预览修补", exact: true }).click();
  await expect(page.getByText(/修补预览完成/)).toBeVisible();
  await page.getByRole("button", { name: "应用修补", exact: true }).click();
  const applied = await saveAndDecode(page, uploads);
  const green = [10, 160, 90, 255];
  expect(
    applied.pixels.every((value, index) => value === green[index % 4]),
  ).toBe(true);
  expectOutsideRegionIdentical(
    baseline,
    applied,
    (x, y) => !(x >= 27 && x < 37 && y >= 19 && y < 29),
  );
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  const undone = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(Buffer.from(undone.pixels), Buffer.from(baseline.pixels)),
  ).toBe(0);
});

test("R9 the first remove stroke keeps export bytes and history unchanged", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  const workers: string[] = [];
  page.on("worker", (worker) => workers.push(worker.url()));
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#0aa05a"/><rect x="32" y="12" width="32" height="16" fill="#e020f0"/></svg>',
    "masks-remove-first.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-remove-first.svg");
  await expect(page.getByText(/原图 96×48/)).toBeVisible();
  const baseline = await saveAndDecode(page, uploads);
  await page.getByRole("button", { name: "去水印", exact: true }).click();
  await page.getByLabel("修补掩膜绘制方式").selectOption("rect-remove");
  const map = makeCanvasMapper(96, 48);
  await pointerStroke(page, [
    map({ x: 30.5, y: 10.5 }),
    map({ x: 65.5, y: 29.5 }),
  ]);
  await expect(
    page.getByRole("button", { name: "预览修补", exact: true }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "应用修补", exact: true }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "撤销", exact: true }),
  ).toBeDisabled();
  const unchanged = await saveAndDecode(page, uploads);
  expect(
    Buffer.compare(Buffer.from(unchanged.pixels), Buffer.from(baseline.pixels)),
  ).toBe(0);
  expect(workers).toEqual([]);
});

test("tiled blur matches a whole-image filter across the 512px seam and click stamps persist", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="128"><rect width="512" height="128" fill="black"/><rect x="512" width="512" height="128" fill="white"/></svg>',
    "tile-seam.svg",
  );
  await mountWithUploadCapture(page, uploads, "tile-seam.svg");
  await expect(page.getByText(/原图 1024×128/)).toBeVisible();
  const baseline = await saveAndDecode(page, uploads);
  const map = makeCanvasMapper(1024, 128);
  await page.getByRole("button", { name: "模糊", exact: true }).click();
  await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
  await page.getByLabel("马赛克与模糊笔刷大小").selectOption("64");
  await pointerStroke(page, [map({ x: 50, y: 64 }), map({ x: 970, y: 64 })]);
  const actual = await saveAndDecode(page, uploads);
  const expected = await page.evaluate(
    async ({ pngBase64 }) => {
      const bitmap = await createImageBitmap(
        new Blob(
          [
            Uint8Array.from(atob(pngBase64), (character) =>
              character.charCodeAt(0),
            ),
          ],
          { type: "image/png" },
        ),
      );
      const source = document.createElement("canvas");
      source.width = 1024;
      source.height = 128;
      source.getContext("2d")!.drawImage(bitmap, 0, 0);
      bitmap.close();
      const result = document.createElement("canvas");
      result.width = 1024;
      result.height = 128;
      const context = result.getContext("2d")!;
      context.filter = "blur(" + (10 * 1024) / 900 + "px)";
      context.drawImage(source, 0, 0);
      return Array.from(context.getImageData(508, 64, 8, 1).data);
    },
    { pngBase64: uploads[0]!.body.toString("base64") },
  );
  for (let x = 508; x < 516; x++)
    expect(pixelAt(actual, x, 64)).toEqual(
      expected.slice((x - 508) * 4, (x - 507) * 4),
    );
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await clickCanvasAt(page, map({ x: 512, y: 64 }));
  const clicked = await saveAndDecode(page, uploads);
  expect(pixelAt(clicked, 511, 64)).not.toEqual(pixelAt(baseline, 511, 64));
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  const restored = await saveAndDecode(page, uploads);
  expect(Buffer.compare(restored.pixels, baseline.pixels)).toBe(0);
});

test("sparse diagonal effects bound scratch canvases in natural output", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="4096" height="4096"><rect width="4096" height="4096" fill="white"/><path d="M0 0L4096 4096" stroke="black" stroke-width="20"/></svg>',
    "tile-large.svg",
  );
  await mountWithUploadCapture(page, uploads, "tile-large.svg");
  await expect(page.getByText(/原图 4096×4096/)).toBeVisible();
  await page.evaluate(() => {
    const old = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (
      ...args: Parameters<typeof old>
    ) {
      if (
        this.width * this.height > 4_000_000 &&
        !(this.width === 4096 && this.height === 4096)
      )
        document.body.dataset.unboundedScratch =
          String(this.width) + "x" + this.height;
      return old.apply(this, args);
    };
  });
  await page.getByRole("button", { name: "模糊", exact: true }).click();
  await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
  const map = makeCanvasMapper(4096, 4096);
  await pointerStroke(page, [
    map({ x: 100, y: 100 }),
    map({ x: 3996, y: 3996 }),
  ]);
  const actual = await saveAndDecode(page, uploads, {
    x: 2046,
    y: 2046,
    width: 4,
    height: 4,
  });
  expect(actual.width).toBe(4);
  expect(pixelAt(actual, 1, 1)[3]).toBe(255);
  expect(pixelAt(actual, 1, 1)[0]).toBeGreaterThan(0);
  await expect(page.locator("body")).not.toHaveAttribute(
    "data-unbounded-scratch",
    /./,
  );
});

async function exactFractionalStroke(
  page: Page,
  points: readonly { x: number; y: number }[],
) {
  await page.mouse.move(5, 5);
  await page
    .locator('canvas[aria-label="图片检查画布"]')
    .evaluate((element, points) => {
      const box = element.getBoundingClientRect();
      const emit = (
        type: string,
        at: { x: number; y: number },
        buttons: number,
      ) =>
        element.dispatchEvent(
          new PointerEvent(type, {
            bubbles: true,
            pointerId: 1,
            pointerType: "mouse",
            isPrimary: true,
            button: 0,
            buttons,
            clientX: box.x + (at.x / 900) * box.width,
            clientY: box.y + (at.y / 600) * box.height,
          }),
        );
      emit("pointerdown", points[0]!, 1);
      for (const at of points.slice(1)) emit("pointermove", at, 1);
      emit("pointerup", points.at(-1)!, 0);
    }, points);
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
}

for (const tool of ["马赛克", "模糊"]) {
  for (const [edge, points] of [
    [
      "left",
      [
        { x: 524.8, y: 300 },
        { x: 870.3, y: 300 },
      ],
    ],
    [
      "right",
      [
        { x: 100.3, y: 300 },
        { x: 499.2, y: 300 },
      ],
    ],
    [
      "top",
      [
        { x: 300, y: 524.8 },
        { x: 300, y: 570.3 },
      ],
    ],
    [
      "bottom",
      [
        { x: 300, y: 100.3 },
        { x: 300, y: 499.2 },
      ],
    ],
  ] as const) {
    test(
      tool +
        " fractional " +
        edge +
        " tile sliver exports without zero-sized canvas",
      async ({ page }) => {
        await page.setViewportSize({ width: 1280, height: 900 });
        const uploads: { contentType: string; body: Buffer }[] = [];
        await routeSource(
          page,
          '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600"><defs><pattern id="p" width="16" height="16" patternUnits="userSpaceOnUse"><rect width="16" height="16" fill="white"/><rect width="8" height="16" fill="black"/></pattern></defs><rect width="900" height="600" fill="url(#p)"/></svg>',
          "fractional-sliver.svg",
        );
        await mountWithUploadCapture(page, uploads, "fractional-sliver.svg");
        await expect(page.getByText(/原图 900×600/)).toBeVisible();
        const baseline = await saveAndDecode(page, uploads);
        await page.getByRole("button", { name: tool, exact: true }).click();
        await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
        await exactFractionalStroke(page, points);
        await expect(page.getByText(/width or height of 0/)).toHaveCount(0);
        const actual = await saveAndDecode(page, uploads);
        expectSameAlpha(baseline, actual);
        const middle = {
          x: Math.round((points[0].x + points[1].x) / 2),
          y: Math.round((points[0].y + points[1].y) / 2),
        };
        expect(pixelAt(actual, middle.x, middle.y)).not.toEqual(
          pixelAt(baseline, middle.x, middle.y),
        );
        await page.getByRole("button", { name: "撤销", exact: true }).click();
        const restored = await saveAndDecode(page, uploads);
        expect(
          Buffer.compare(
            Buffer.from(restored.pixels),
            Buffer.from(baseline.pixels),
          ),
        ).toBe(0);
      },
    );
  }
}

test("fractional mosaic uses one unfragmented grid across the 512px black-white seam", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600"><rect width="512" height="600" fill="black"/><rect x="512" width="388" height="600" fill="white"/></svg>',
    "fractional-mosaic-grid.svg",
  );
  await mountWithUploadCapture(page, uploads, "fractional-mosaic-grid.svg");
  await expect(page.getByText(/原图 900×600/)).toBeVisible();
  const baseline = await saveAndDecode(page, uploads);
  await page.getByRole("button", { name: "马赛克", exact: true }).click();
  await page.getByLabel("马赛克与模糊绘制方式").selectOption("brush");
  await exactFractionalStroke(page, [
    { x: 13.3, y: 300 },
    { x: 870.3, y: 300 },
  ]);
  const actual = await saveAndDecode(page, uploads);
  const reference = await page.evaluate(
    async ({ pngBase64 }) => {
      const bitmap = await createImageBitmap(
        new Blob(
          [
            Uint8Array.from(atob(pngBase64), (character) =>
              character.charCodeAt(0),
            ),
          ],
          { type: "image/png" },
        ),
      );
      const source = document.createElement("canvas");
      source.width = 900;
      source.height = 600;
      source.getContext("2d")!.drawImage(bitmap, 0, 0);
      bitmap.close();
      const area = { x: 0.3, y: 287, width: 883, height: 26 };
      const down = document.createElement("canvas");
      down.width = Math.ceil(area.width / 14);
      down.height = Math.ceil(area.height / 14);
      const small = down.getContext("2d")!;
      small.imageSmoothingEnabled = true;
      small.imageSmoothingQuality = "high";
      small.drawImage(
        source,
        area.x,
        area.y,
        area.width,
        area.height,
        0,
        0,
        down.width,
        down.height,
      );
      const result = document.createElement("canvas");
      result.width = 900;
      result.height = 600;
      const context = result.getContext("2d")!;
      context.imageSmoothingEnabled = false;
      context.drawImage(down, area.x, area.y, area.width, area.height);
      return Array.from(context.getImageData(510, 300, 14, 1).data);
    },
    { pngBase64: uploads[0]!.body.toString("base64") },
  );
  for (let x = 510; x < 524; x++)
    expect(pixelAt(actual, x, 300)).toEqual(
      reference.slice((x - 510) * 4, (x - 509) * 4),
    );
  expect(pixelAt(actual, 511, 300)).toEqual(pixelAt(actual, 512, 300));
});

test("R15 restarting preview with a dense brush keeps every point and discards the old mask", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#0aa05a"/><rect x="10" y="12" width="6" height="6" fill="#e020f0"/><path d="M60 30 L66 20 L72 30 L78 20" stroke="#e020f0" stroke-width="2" fill="none"/></svg>',
    "masks-r15-restart.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-r15-restart.svg");
  await expect(page.getByText(/原图 96×48/)).toBeVisible();
  const baseline = await saveAndDecode(page, uploads);
  const map = makeCanvasMapper(96, 48);
  await page.getByRole("button", { name: "去水印" }).click();
  await pointerStroke(page, [map({ x: 9, y: 11 }), map({ x: 17, y: 19 })]);
  await page.getByRole("button", { name: "预览修补" }).click();
  await expect(page.getByText(/修补预览完成/)).toBeVisible();
  await page.getByLabel("修补掩膜绘制方式").selectOption("brush-add");
  await page.getByLabel("修补掩膜笔刷大小").selectOption("10");
  // One browser task: all bends arrive before rAF, including the pointerup-only endpoint.
  const points = [
    map({ x: 60, y: 30 }),
    map({ x: 66, y: 20 }),
    map({ x: 72, y: 30 }),
    map({ x: 78, y: 20 }),
  ];
  await page
    .locator('canvas[aria-label="图片检查画布"]')
    .evaluate((canvas, points) => {
      const box = canvas.getBoundingClientRect();
      points.forEach((at, i) =>
        canvas.dispatchEvent(
          new PointerEvent(
            i === 0
              ? "pointerdown"
              : i === points.length - 1
                ? "pointerup"
                : "pointermove",
            {
              bubbles: true,
              pointerId: 1,
              button: 0,
              buttons: i === points.length - 1 ? 0 : 1,
              clientX: box.x + (at.x * box.width) / 900,
              clientY: box.y + (at.y * box.height) / 600,
            },
          ),
        ),
      );
    }, points);
  await page.getByRole("button", { name: "预览修补" }).click();
  await expect(page.getByText(/修补预览完成/)).toBeVisible();
  await page.getByRole("button", { name: "应用修补" }).click();
  const applied = await saveAndDecode(page, uploads);
  expect(pixelAt(applied, 12, 14)).toEqual(pixelAt(baseline, 12, 14));
  for (const [x, y] of [
    [60, 30],
    [66, 20],
    [72, 30],
    [78, 20],
  ]) {
    const repaired = pixelAt(applied, x!, y!);
    expect(repaired).toEqual([10, 160, 90, 255]);
  }
  expectSameAlpha(baseline, applied);
  expectOutsideRegionIdentical(
    baseline,
    applied,
    (x, y) => x >= 55 && x < 84 && y >= 15 && y < 36,
  );
  await page.getByRole("button", { name: "撤销" }).click();
  const undone = await saveAndDecode(page, uploads);
  expect(Buffer.compare(undone.pixels, baseline.pixels)).toBe(0);
  await expect(page.getByRole("button", { name: "撤销" })).toBeDisabled();
});

test("R15 alpha-only duplicate texture finds the real RGBA copy", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><defs><pattern id="p" width="2" height="2" patternUnits="userSpaceOnUse"><rect width="2" height="2" fill="#646464" opacity=".25"/><path d="M0 0h1v1H0z M1 1h1v1H1z" fill="#646464"/></pattern></defs><rect x="10" y="6" width="10" height="8" fill="url(#p)"/><rect x="60" y="30" width="10" height="8" fill="url(#p)"/></svg>',
    "masks-r15-alpha.svg",
  );
  await mountWithUploadCapture(page, uploads, "masks-r15-alpha.svg");
  await expect(page.getByText(/原图 96×48/)).toBeVisible();
  const baseline = await saveAndDecode(page, uploads);
  expect(pixelAt(baseline, 10, 6)[3]).toBe(255);
  expect(pixelAt(baseline, 11, 6)[3]).toBeLessThan(255);
  const map = makeCanvasMapper(96, 48);
  await page.getByRole("button", { name: "重复匹配" }).click();
  await pointerStroke(page, [
    map({ x: 10.5, y: 6.5 }),
    map({ x: 19.5, y: 13.5 }),
  ]);
  await page.getByRole("button", { name: "查找重复" }).click();
  await expect(page.getByText(/找到 1 处重复内容/)).toBeVisible();
  const previewOnly = await saveAndDecode(page, uploads);
  expect(Buffer.compare(previewOnly.pixels, baseline.pixels)).toBe(0);
  await page.getByRole("button", { name: "确认应用重复遮挡" }).click();
  const applied = await saveAndDecode(page, uploads);
  expectOutsideRegionIdentical(
    baseline,
    applied,
    (x, y) => x >= 60 && x < 70 && y >= 30 && y < 38,
  );
  expect(pixelAt(applied, 61, 30)[3]).toBe(255);
  await page.getByRole("button", { name: "撤销" }).click();
  const undone = await saveAndDecode(page, uploads);
  expect(Buffer.compare(undone.pixels, baseline.pixels)).toBe(0);
});
