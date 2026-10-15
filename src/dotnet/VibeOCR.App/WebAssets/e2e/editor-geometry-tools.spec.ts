import { expect, test, type Locator, type Page } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

/** 几何标注新能力的真实浏览器像素契约（预览画布与导出同源 draw）：
 *  1) 直线（虚线）与箭头（实心双向）按属性真实绘制且写入导出像素；
 *  2) 矩形圆角留空角落、Shift 正圆、Alt 中心绘制（填充对照）；
 *  3) 折线单击加点双击提交；0% 边框不透明对象经选择工具的对象命中入口
 *    恢复；一次绘制/拖动恰好一条历史，撤销后导出回到纯源图。
 *  源图 900×600 纯绿（与 900×600 显示坐标画布同长宽比，displayScale=1）：
 *  显示坐标即自然像素，8px 预览描边在导出中同为 8px，可做严格像素断言。
 *  坐标换算：每次绘制/点击前重新获取 canvas boundingBox（属性排展开与
 *  页面滚动都会改变屏幕位置，不得跨操作缓存）；draw 由 rAF 调度，像素
 *  断言一律用有界 expect.poll 等待，无固定 sleep。 */

const GREEN: readonly [number, number, number] = [18, 161, 80];
const STROKE_BLUE: readonly [number, number, number] = [31, 111, 235];
const FILL_RED: readonly [number, number, number] = [224, 32, 32];

function withinTolerance(
  actual: readonly number[],
  expected: readonly [number, number, number],
  tolerance: number,
): boolean {
  return [0, 1, 2].every(
    (channel) =>
      Math.abs((actual[channel] ?? -999) - expected[channel]!) <= tolerance,
  );
}

async function expectCanvasPixel(
  canvas: Locator,
  x: number,
  y: number,
  expected: readonly [number, number, number],
  tolerance: number,
) {
  await expect
    .poll(
      async () =>
        withinTolerance(
          await canvas.evaluate(
            (element: HTMLCanvasElement, coords) => {
              const context = element.getContext("2d")!;
              const pixel = context.getImageData(
                Math.round((coords.x / 900) * element.width),
                Math.round((coords.y / 600) * element.height),
                1,
                1,
              ).data;
              return [pixel[0], pixel[1], pixel[2]];
            },
            { x, y },
          ),
          expected,
          tolerance,
        ),
      { timeout: 5_000 },
    )
    .toBe(true);
}

/** 显示坐标 → 屏幕坐标：每次调用重新测量画布位置，不跨操作缓存。 */
async function canvasClient(page: Page, x: number, y: number) {
  const canvas = page.getByLabel("图片检查画布");
  await canvas.scrollIntoViewIfNeeded();
  const box = (await canvas.boundingBox())!;
  return {
    x: Math.round(box.x + (box.width * x) / 900),
    y: Math.round(box.y + (box.height * y) / 600),
  };
}

async function dragOnCanvas(
  page: Page,
  from: { x: number; y: number },
  to: { x: number; y: number },
  modifiers: { shift?: boolean; alt?: boolean } = {},
) {
  const start = await canvasClient(page, from.x, from.y);
  if (modifiers.shift) await page.keyboard.down("Shift");
  if (modifiers.alt) await page.keyboard.down("Alt");
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  // 按下后布局可能因状态行变化再次移动，落点同样重新测量。
  const end = await canvasClient(page, to.x, to.y);
  await page.mouse.move(end.x, end.y);
  await page.mouse.up();
  if (modifiers.shift) await page.keyboard.up("Shift");
  if (modifiers.alt) await page.keyboard.up("Alt");
}

/** 每个工具在本用例消费的属性全部显式设置：独立属性记忆不假定跨工具继承。 */
async function configureTool(
  page: Page,
  options: {
    color?: string;
    width?: string;
    lineStyle?: string;
    fill?: boolean;
    fillColor?: string;
  } = {},
) {
  if (options.color) await page.getByLabel("自定义颜色").fill(options.color);
  if (options.width)
    await page.getByLabel("线条宽度").selectOption(options.width);
  if (options.lineStyle)
    await page.getByLabel("线型").selectOption(options.lineStyle);
  if (options.fill) {
    await page.getByLabel("填充").check();
    if (options.fillColor)
      await page.getByLabel("填充颜色").fill(options.fillColor);
  }
}

async function setupGeometryHost(
  page: Page,
  sourceSize = { width: 900, height: 600 },
): Promise<{ uploads: { contentType: string; body: Buffer }[] }> {
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
  await page.route("**/geometry-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      // 源图与逻辑画布同比，尺寸可缩小以覆盖非 1:1 导出。
      body: `<svg xmlns="http://www.w3.org/2000/svg" width="${sourceSize.width}" height="${sourceSize.height}"><rect width="100%" height="100%" fill="#12a150"/></svg>`,
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
          url: "/geometry-source.svg",
          mediaType: "image/png",
          byteLength: 2048,
        },
      },
    },
  });
  const canvas = page.getByLabel("图片检查画布");
  await expect(canvas).toBeVisible();
  await expect(
    page.getByText(new RegExp(`原图 ${sourceSize.width}×${sourceSize.height}`)),
  ).toBeVisible();
  return { uploads };
}

/** 解码上传 PNG 并按自然像素坐标采样指定点列表。 */
async function decodeUploadPixels(
  page: Page,
  body: Buffer,
  points: readonly { x: number; y: number }[],
  expectedSize = { width: 900, height: 600 },
): Promise<number[][]> {
  return page
    .evaluate(
      async ({ base64, samples }) => {
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index += 1) {
          bytes[index] = binary.charCodeAt(index);
        }
        const bitmap = await createImageBitmap(
          new Blob([bytes], { type: "image/png" }),
        );
        const target = document.createElement("canvas");
        target.width = bitmap.width;
        target.height = bitmap.height;
        const context = target.getContext("2d")!;
        context.drawImage(bitmap, 0, 0);
        return {
          width: bitmap.width,
          height: bitmap.height,
          pixels: samples.map((at) => {
            const pixel = context.getImageData(
              Math.round(at.x),
              Math.round(at.y),
              1,
              1,
            ).data;
            return [pixel[0], pixel[1], pixel[2]];
          }),
        };
      },
      { base64: body.toString("base64"), samples: points },
    )
    .then((result) => {
      expect(result.width).toBe(expectedSize.width);
      expect(result.height).toBe(expectedSize.height);
      return result.pixels;
    });
}

/** 沿水平线采样并统计描边/底色覆盖（虚线节奏断言，容忍拖拽起点相位）。 */
function lineCoverage(pixels: readonly number[][]) {
  const blue = pixels.filter((pixel) =>
    withinTolerance(pixel, STROKE_BLUE, 50),
  ).length;
  const green = pixels.filter((pixel) =>
    withinTolerance(pixel, GREEN, 20),
  ).length;
  return { blue, green };
}

async function scanPreviewLine(
  canvas: Locator,
  y: number,
  from: number,
  count: number,
  step: number,
) {
  return canvas.evaluate(
    (element: HTMLCanvasElement, params) => {
      const context = element.getContext("2d")!;
      return Array.from({ length: params.count }, (_, index) => {
        const pixel = context.getImageData(
          Math.round(
            ((params.from + index * params.step) / 900) * element.width,
          ),
          Math.round((params.y / 600) * element.height),
          1,
          1,
        ).data;
        return [pixel[0], pixel[1], pixel[2]];
      });
    },
    { y, from, count, step },
  );
}

test("直线虚线与箭头实心双向端头写入预览与导出像素", async ({ page }) => {
  const { uploads } = await setupGeometryHost(page);
  const canvas = page.getByLabel("图片检查画布");

  // 直线：蓝 8px 虚线 [12,8]，(150,200)→(750,200) 水平。
  await page.getByRole("button", { name: "直线", exact: true }).click();
  await configureTool(page, {
    color: "#1f6feb",
    width: "8",
    lineStyle: "dashed",
  });
  await dragOnCanvas(page, { x: 150, y: 200 }, { x: 750, y: 200 });
  await expect
    .poll(
      async () => {
        const coverage = lineCoverage(
          await scanPreviewLine(canvas, 200, 160, 140, 4),
        );
        return coverage.blue >= 15 && coverage.green >= 10;
      },
      { timeout: 5_000 },
    )
    .toBe(true);

  // 粗点线的圆头之间也必须留出底色，不能因线宽覆盖间隙退化为实线。
  await page.getByLabel("线型").selectOption("dotted");
  await dragOnCanvas(page, { x: 150, y: 270 }, { x: 750, y: 270 });
  await expect
    .poll(async () => {
      const coverage = lineCoverage(
        await scanPreviewLine(canvas, 270, 160, 140, 4),
      );
      return coverage.blue >= 15 && coverage.green >= 10;
    })
    .toBe(true);

  // 箭头：本工具重新显式设置属性（独立记忆不继承直线工具），实心双向。
  await page.getByRole("button", { name: "箭头", exact: true }).click();
  await configureTool(page, {
    color: "#1f6feb",
    width: "8",
    lineStyle: "solid",
  });
  await page.getByLabel("箭头样式").selectOption("solid");
  await page.getByLabel("双向箭头").check();
  await dragOnCanvas(page, { x: 200, y: 400 }, { x: 700, y: 440 });
  // 实心头中心（tip 内 13px，三角内部深处）：两端各一处纯描边色。
  await expectCanvasPixel(canvas, 687, 439, STROKE_BLUE, 30);
  await expectCanvasPixel(canvas, 213, 401, STROKE_BLUE, 30);

  // 导出与预览同源：自然 900×600，同坐标存在虚线与实心头像素。
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/png");
  const dashedRow = Array.from({ length: 140 }, (_, index) => ({
    x: 160 + index * 4,
    y: 200,
  }));
  const exported = await decodeUploadPixels(page, uploads[0]!.body, [
    ...dashedRow,
    ...dashedRow.map((point) => ({ ...point, y: 270 })),
    { x: 687, y: 439 },
    { x: 213, y: 401 },
    { x: 425, y: 300 },
  ]);
  const exportedCoverage = lineCoverage(exported.slice(0, 140));
  expect(exportedCoverage.blue).toBeGreaterThanOrEqual(15);
  expect(exportedCoverage.green).toBeGreaterThanOrEqual(10);
  const dottedCoverage = lineCoverage(exported.slice(140, 280));
  expect(dottedCoverage.blue).toBeGreaterThanOrEqual(15);
  expect(dottedCoverage.green).toBeGreaterThanOrEqual(10);
  expect(withinTolerance(exported[280]!, STROKE_BLUE, 30)).toBe(true);
  expect(withinTolerance(exported[281]!, STROKE_BLUE, 30)).toBe(true);
  // 无标注区域保持源图底色。
  expect(withinTolerance(exported[282]!, GREEN, 12)).toBe(true);
});

test("矩形圆角留空角落；Shift 正圆与 Alt 中心绘制写入预览像素", async ({
  page,
}) => {
  const { uploads } = await setupGeometryHost(page);
  const canvas = page.getByLabel("图片检查画布");

  // 圆角 12px：原直角描边覆盖的外角留空，左边描边带仍落笔。
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await configureTool(page, { color: "#1f6feb", width: "8" });
  await page.getByLabel("矩形圆角").selectOption("12");
  await dragOnCanvas(page, { x: 150, y: 120 }, { x: 450, y: 420 });
  await expectCanvasPixel(canvas, 148, 118, GREEN, 20);
  await expectCanvasPixel(canvas, 152, 300, STROKE_BLUE, 40);

  // Shift 椭圆：拖 (500,120)→(650,220)（dx150/dy100）约束为正圆
  // r=75、圆心 (575,195)：底点 (575,270) 落笔；未约束时的底点
  // (575,220) 位于正圆内部，保持底色。
  await page.getByRole("button", { name: "椭圆", exact: true }).click();
  await configureTool(page, { color: "#1f6feb", width: "8" });
  await dragOnCanvas(
    page,
    { x: 500, y: 120 },
    { x: 650, y: 220 },
    { shift: true },
  );
  await expectCanvasPixel(canvas, 575, 270, STROKE_BLUE, 60);
  await expectCanvasPixel(canvas, 575, 220, GREEN, 25);

  // Alt 矩形（本工具显式重设属性 + 红色填充做大面积对照）：从 (400,500)
  // 中心拖到 (500,560) → 矩形 (300,440)-(500,560)，中心 (400,500) 为填充
  // 色；非 Alt 时该点在矩形外，为源图底色。
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await configureTool(page, {
    color: "#1f6feb",
    width: "8",
    fill: true,
    fillColor: "#e02020",
  });
  const fillOpacity = page.getByLabel("填充不透明度");
  await fillOpacity.fill("100");
  await fillOpacity.blur();
  await dragOnCanvas(
    page,
    { x: 400, y: 500 },
    { x: 500, y: 560 },
    { alt: true },
  );
  await expectCanvasPixel(canvas, 400, 500, FILL_RED, 25);
  await expectCanvasPixel(canvas, 550, 500, GREEN, 20);
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const exported = await decodeUploadPixels(page, uploads[0]!.body, [
    { x: 148, y: 118 },
    { x: 152, y: 300 },
    { x: 575, y: 270 },
    { x: 575, y: 220 },
    { x: 400, y: 500 },
    { x: 550, y: 500 },
  ]);
  for (const [index, color] of [
    GREEN,
    STROKE_BLUE,
    STROKE_BLUE,
    GREEN,
    FILL_RED,
    GREEN,
  ].entries()) {
    expect(withinTolerance(exported[index]!, color, 20)).toBe(true);
  }
});

test("折线双击提交；0% 不透明对象经对象选择入口恢复，撤销后导出回纯源图", async ({
  page,
}, testInfo) => {
  const { uploads } = await setupGeometryHost(page);
  const canvas = page.getByLabel("图片检查画布");

  // 折线：本工具显式设置属性；三个单击点 + 双击提交（恰好一条历史）。
  await page.getByRole("button", { name: "折线", exact: true }).click();
  await configureTool(page, {
    color: "#1f6feb",
    width: "8",
    lineStyle: "solid",
  });
  for (const point of [
    { x: 200, y: 300 },
    { x: 400, y: 300 },
    { x: 400, y: 450 },
  ]) {
    const client = await canvasClient(page, point.x, point.y);
    await page.mouse.click(client.x, client.y);
  }
  const last = await canvasClient(page, 400, 450);
  await page.mouse.dblclick(last.x, last.y);
  // 两段中点都真实落笔。
  await expectCanvasPixel(canvas, 300, 300, STROKE_BLUE, 40);
  await expectCanvasPixel(canvas, 400, 375, STROKE_BLUE, 40);

  // 0% 边框不透明矩形：输出像素完全不可见。
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await configureTool(page, { color: "#1f6feb", width: "8" });
  const opacity = page.getByLabel("边框不透明度");
  await opacity.fill("0");
  await opacity.blur();
  await dragOnCanvas(page, { x: 500, y: 100 }, { x: 800, y: 300 });
  await expectCanvasPixel(canvas, 650, 100, GREEN, 12);

  // 透明对象可从可见下拉入口按序号选择，不依赖记住画布坐标。
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption({ label: "2 · 矩形" });
  await expect(
    page.getByRole("toolbar", { name: "当前工具设置" }),
  ).toBeVisible();
  const markOpacity = page.getByLabel("边框不透明度");
  await expect(markOpacity).toHaveValue("0");
  await markOpacity.fill("100");
  await markOpacity.blur();
  await page.screenshot({
    path: testInfo.outputPath("geometry-object-selection.png"),
  });
  // 先点空白取消选择，避免八点手柄覆盖边框中点的像素。
  const blank = await canvasClient(page, 100, 80);
  await page.mouse.click(blank.x, blank.y);
  await expectCanvasPixel(canvas, 650, 100, STROKE_BLUE, 45);

  // 一次绘制/拖动恰好一条历史：折线(1) + 透明矩形(2) + 样式恢复(3)；
  // 撤销 3 次回到纯源图，导出像素无任何标注。
  for (let index = 0; index < 3; index += 1) {
    await page.getByRole("button", { name: "撤销", exact: true }).click();
  }
  await expectCanvasPixel(canvas, 300, 300, GREEN, 12);
  await expectCanvasPixel(canvas, 650, 100, GREEN, 12);
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const undone = await decodeUploadPixels(page, uploads[0]!.body, [
    { x: 300, y: 300 },
    { x: 400, y: 375 },
    { x: 650, y: 100 },
    { x: 90, y: 60 },
  ]);
  for (const pixel of undone) {
    expect(withinTolerance(pixel, GREEN, 12)).toBe(true);
  }
});

test("非1:1尺寸下圆角先缩放再夹取，预览与小图导出保留相同圆角", async ({
  page,
}) => {
  const { uploads } = await setupGeometryHost(page, {
    width: 300,
    height: 200,
  });
  const canvas = page.getByLabel("图片检查画布");
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await configureTool(page, { fill: true, fillColor: "#e02020" });
  await page.getByLabel("填充不透明度").fill("100");
  await page.getByLabel("填充不透明度").blur();
  await page.getByLabel("矩形圆角").selectOption("18");
  await page.getByLabel("边框不透明度").fill("0");
  await page.getByLabel("边框不透明度").blur();
  await dragOnCanvas(page, { x: 150, y: 120 }, { x: 210, y: 180 });
  await expectCanvasPixel(canvas, 153, 123, GREEN, 20);
  await expectCanvasPixel(canvas, 180, 150, FILL_RED, 20);
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  // 逻辑 60×60/r18 导出为自然 20×20/r6；左上 6×6 至少三点完全留绿。
  // 错误的先夹取再缩放会变成 r3.33，只有角点留绿，其余被红色填充。
  const corner = Array.from({ length: 36 }, (_, index) => ({
    x: 50 + (index % 6),
    y: 40 + Math.floor(index / 6),
  }));
  const pixels = await decodeUploadPixels(
    page,
    uploads[0]!.body,
    [...corner, { x: 60, y: 50 }],
    { width: 300, height: 200 },
  );
  expect(
    pixels.slice(0, 36).filter((pixel) => withinTolerance(pixel, GREEN, 20))
      .length,
  ).toBeGreaterThanOrEqual(3);
  expect(withinTolerance(pixels[36]!, FILL_RED, 20)).toBe(true);
});

test.describe("缩放后的标注控制点", () => {
  test.use({ deviceScaleFactor: 2 });
  test("150% 显示缩放后拖动矩形角手柄，预览和导出更新几何且一次撤销恢复", async ({
    page,
  }) => {
    const { uploads } = await setupGeometryHost(page);
    const canvas = page.getByLabel("图片检查画布");
    await page.getByRole("button", { name: "矩形", exact: true }).click();
    await configureTool(page, { color: "#1f6feb", width: "8" });
    await dragOnCanvas(page, { x: 150, y: 150 }, { x: 450, y: 350 });
    await page.getByRole("button", { name: "选择", exact: true }).click();
    await page.getByLabel("选择标注对象").selectOption("0");
    await page.getByLabel("显示缩放", { exact: true }).selectOption("1.5");
    await expect
      .poll(() =>
        canvas.evaluate((element: HTMLCanvasElement) =>
          Math.abs(
            element.width -
              element.getBoundingClientRect().width * devicePixelRatio,
          ),
        ),
      )
      .toBeLessThanOrEqual(1);
    await dragOnCanvas(page, { x: 450, y: 350 }, { x: 550, y: 420 });
    await page.getByLabel("选择标注对象").selectOption("");
    await expectCanvasPixel(canvas, 550, 400, STROKE_BLUE, 40);
    await expectCanvasPixel(canvas, 450, 350, GREEN, 20);
    await page.getByRole("button", { name: "保存标注图", exact: true }).click();
    await expect.poll(() => uploads.length).toBe(1);
    const pixels = await decodeUploadPixels(page, uploads[0]!.body, [
      { x: 550, y: 400 },
      { x: 450, y: 350 },
    ]);
    expect(withinTolerance(pixels[0]!, STROKE_BLUE, 30)).toBe(true);
    expect(withinTolerance(pixels[1]!, GREEN, 20)).toBe(true);
    await page.getByRole("button", { name: "撤销", exact: true }).click();
    await expectCanvasPixel(canvas, 450, 250, STROKE_BLUE, 40);
    await expectCanvasPixel(canvas, 550, 400, GREEN, 20);
  });
});
