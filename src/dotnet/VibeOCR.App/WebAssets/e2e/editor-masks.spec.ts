import { expect, test, type Page } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

/**
 * Goal #227 掩码能力（遮挡/笔刷效果/掩膜修补/重复匹配）的真实浏览器
 * 导出像素回归：全部走真实 UI 操作 → 真实编码 PNG → 浏览器解码逐像素
 * 断言，不依赖 UI 遮罩或 mock 像素。
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
  return image.pixels.slice(offset, offset + 4);
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
): Promise<DecodedImage> {
  const index = uploads.length;
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(index + 1);
  await expect(
    page.getByRole("button", { name: "保存标注图", exact: true }),
  ).toBeEnabled();
  expect(uploads[index]!.contentType).toBe("image/png");
  return decodeUpload(page, uploads[index]!.body, "image/png");
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
    '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="120"><rect width="240" height="120" fill="#0aa05a"/><rect x="40" y="20" width="24" height="16" fill="#e020f0"/></svg>',
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
  await page.getByLabel("修补对比").selectOption("before");
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

  await page.getByRole("button", { name: "重复匹配" }).click();
  // 框选源 A：外扩取整后恰为 (10,6,10,8) 图案本身。
  await pointerStroke(page, [toDisplay(10.5, 6.5), toDisplay(19.5, 13.5)]);
  await expect(page.getByText(/已框选重复内容源区域/)).toBeVisible();
  await page.getByRole("button", { name: "查找重复" }).click();
  await expect(page.getByText(/找到 2 处重复内容/)).toBeVisible({
    timeout: 30_000,
  });

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
