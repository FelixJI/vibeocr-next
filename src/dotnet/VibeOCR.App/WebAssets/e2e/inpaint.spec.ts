import { expect, test, type Page } from "@playwright/test";

import { expectCommand, mountHost, snapshot } from "./workbench-host";

interface DecodedImage {
  width: number;
  height: number;
  pixels: number[];
}

/** 与 annotation-format.spec 同源：真实浏览器解码上传的编码字节。 */
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

/** 选区外逐字节一致：包括透明合成、水印环与全部未选中像素。 */
function expectOutsideIdentical(
  baseline: DecodedImage,
  actual: DecodedImage,
  rect: { x: number; y: number; width: number; height: number },
): void {
  expect(actual.width).toBe(baseline.width);
  expect(actual.height).toBe(baseline.height);
  for (let y = 0; y < baseline.height; y += 1) {
    for (let x = 0; x < baseline.width; x += 1) {
      const inside =
        x >= rect.x &&
        x < rect.x + rect.width &&
        y >= rect.y &&
        y < rect.y + rect.height;
      if (inside) continue;
      const before = pixelAt(baseline, x, y);
      const after = pixelAt(actual, x, y);
      expect(after, `outside pixel (${x},${y})`).toEqual(before);
    }
  }
}

async function routeInpaintSource(page: Page, svgBody: string, path: string) {
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
    capabilities: ["recognition.results", "recognition.annotation"],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: `/${sourcePath}`,
          mediaType: "image/png",
          byteLength: 4096,
        },
      },
    },
  });
}

/** 画布坐标（900×600 属性空间）→ 页面客户区坐标后拖拽。 */
async function dragOnCanvas(
  page: Page,
  from: { x: number; y: number },
  to: { x: number; y: number },
): Promise<void> {
  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  const box = await canvas.boundingBox();
  expect(box).toBeTruthy();
  const toClient = (at: { x: number; y: number }) => ({
    x: box!.x + (at.x / 900) * box!.width,
    y: box!.y + (at.y / 600) * box!.height,
  });
  const start = toClient(from);
  const end = toClient(to);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move((start.x + end.x) / 2, (start.y + end.y) / 2);
  await page.mouse.move(end.x, end.y);
  await page.mouse.up();
}

test("inpaint preview applies real CPU worker pixels to export and keeps outside bytes", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  // 96×48：绿背景 + 中央洋红“水印”；选区内缩 2px，外圈水印保留用于
  // 选区外逐字节一致断言。
  await routeInpaintSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#0aa05a"/><rect x="32" y="12" width="32" height="16" fill="#e020f0"/></svg>',
    "inpaint-source.svg",
  );
  await mountWithUploadCapture(page, uploads, "inpaint-source.svg");
  const canvas = page.locator('canvas[aria-label="图片检查画布"]');
  await expect(canvas).toBeVisible();
  await expect(page.getByText(/原图 96×48/)).toBeVisible();

  // 基线：未修补导出的真实 PNG。
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect(uploads[0]!.contentType).toBe("image/png");
  const baseline = await decodeUpload(page, uploads[0]!.body, "image/png");
  expect(baseline.width).toBe(96);
  expect(baseline.height).toBe(48);

  // 去水印工具 + 明确框选：选区完全覆盖水印并压在绿背景上（自然约
  // (29,9)-(67,31)，外扩取整后 (29,9,38,22)），边界才能扩散出背景色。
  await page.getByRole("button", { name: "去水印" }).click();
  await expect(page.getByText(/复杂纹理或大面积覆盖效果有限/)).toBeVisible();
  await expect(page.getByRole("button", { name: "预览修补" })).toBeDisabled();
  await dragOnCanvas(page, { x: 281, y: 168 }, { x: 619, y: 357 });
  await expect(page.getByText(/已框选修补区域/)).toBeVisible();

  // 真实本地修补预览：完成后才出现“应用修补”。
  await page.getByRole("button", { name: "预览修补" }).click();
  await expect(page.getByText(/修补预览完成/)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("button", { name: "应用修补" })).toBeEnabled();

  // 前后对照切换存在且可来回切换。
  await page.getByLabel("修补对比").selectOption("before");
  await page.getByLabel("修补对比").selectOption("after");

  // 明确应用：恰好一次 commit；导出 PNG 解码验证修补像素与选区外一致性。
  await page.getByRole("button", { name: "应用修补" }).click();
  await expect(page.getByText(/已应用修补/)).toBeVisible();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  const applied = await decodeUpload(page, uploads[1]!.body, "image/png");
  // 选区中心被真实修补为背景绿，而不是原水印或纯遮罩。
  const center = pixelAt(applied, 48, 20);
  expect(Math.abs(center[0]! - 10)).toBeLessThanOrEqual(6);
  expect(Math.abs(center[1]! - 160)).toBeLessThanOrEqual(6);
  expect(Math.abs(center[2]! - 90)).toBeLessThanOrEqual(6);
  // 选区外（含全部未选中背景）逐字节一致。
  expectOutsideIdentical(baseline, applied, {
    x: 29,
    y: 9,
    width: 38,
    height: 22,
  });
  // 选区内不再残留水印色：所有选区像素都接近背景绿。
  for (const [x, y] of [
    [33, 13],
    [63, 27],
    [48, 20],
  ] as const) {
    const filled = pixelAt(applied, x, y);
    expect(Math.abs(filled[0]! - 10)).toBeLessThanOrEqual(6);
    expect(Math.abs(filled[1]! - 160)).toBeLessThanOrEqual(6);
    expect(Math.abs(filled[2]! - 90)).toBeLessThanOrEqual(6);
  }

  // 撤销恢复原图：再次导出与基线完全一致。
  await expect(page.getByRole("button", { name: "撤销" })).toBeEnabled();
  await page.getByRole("button", { name: "撤销" }).click();
  await expect(page.getByRole("button", { name: "撤销" })).toBeDisabled();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(3);
  const undone = await decodeUpload(page, uploads[2]!.body, "image/png");
  expect(undone.pixels).toEqual(baseline.pixels);
});

test("cancel and re-edit during processing terminate the worker so late results cannot commit", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  // 确定性延迟 Worker 消息：请求被扣住，测试控制何时（若 ever）送达。
  await page.addInitScript(() => {
    const RealWorker = window.Worker;
    const held: (() => void)[] = [];
    Object.assign(window, {
      __inpaintTest: {
        requests: 0,
        terminated: 0,
        release() {
          for (const deliver of held.splice(0)) deliver();
        },
      },
    });
    class DelayedWorker extends RealWorker {
      constructor(...args: ConstructorParameters<typeof Worker>) {
        super(...args);
        const originalTerminate = this.terminate.bind(this);
        this.terminate = () => {
          (
            window as unknown as {
              __inpaintTest: { terminated: number };
            }
          ).__inpaintTest.terminated += 1;
          return originalTerminate();
        };
      }

      postMessage(message: unknown, transfer?: Transferable[]): void {
        (
          window as unknown as { __inpaintTest: { requests: number } }
        ).__inpaintTest.requests += 1;
        held.push(() => super.postMessage(message, transfer));
      }
    }
    window.Worker = DelayedWorker;
  });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeInpaintSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#0aa05a"/><rect x="32" y="12" width="32" height="16" fill="#e020f0"/></svg>',
    "inpaint-late.svg",
  );
  await mountWithUploadCapture(page, uploads, "inpaint-late.svg");
  await expect(page.locator('canvas[aria-label="图片检查画布"]')).toBeVisible();

  await page.getByRole("button", { name: "去水印" }).click();
  await dragOnCanvas(page, { x: 319, y: 206 }, { x: 581, y: 319 });
  await page.getByRole("button", { name: "预览修补" }).click();
  await expect(page.getByText(/正在本地修补选中区域/)).toBeVisible();
  await expect(page.getByRole("button", { name: "取消修补" })).toBeEnabled();

  // 取消：terminate 立即生效；随后送达的迟到消息不能变成预览/提交。
  await page.getByRole("button", { name: "取消修补" }).click();
  await expect(page.getByText(/已取消修补；当前图片未被修改/)).toBeVisible();
  await page.evaluate(() =>
    (
      window as unknown as { __inpaintTest: { release(): void } }
    ).__inpaintTest.release(),
  );
  await page.waitForTimeout(500);
  expect(
    (
      await page.evaluate(
        () =>
          (
            window as unknown as {
              __inpaintTest: { requests: number; terminated: number };
            }
          ).__inpaintTest,
      )
    ).terminated,
  ).toBeGreaterThanOrEqual(1);
  await expect(page.getByText(/修补预览完成/)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "应用修补" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "撤销" })).toBeDisabled();

  // 重新编辑场景：处理期间旋转（内容修订推进）后，迟到结果同样不能提交。
  await dragOnCanvas(page, { x: 319, y: 206 }, { x: 581, y: 319 });
  await page.getByRole("button", { name: "预览修补" }).click();
  await page.getByRole("button", { name: "旋转 90°" }).click();
  await expect(page.getByText(/修补预览完成/)).toHaveCount(0);
  await page.evaluate(() =>
    (
      window as unknown as { __inpaintTest: { release(): void } }
    ).__inpaintTest.release(),
  );
  await page.waitForTimeout(500);
  await expect(page.getByText(/修补预览完成/)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "应用修补" })).toHaveCount(0);
  // 只有旋转进入了历史；图片内容未被修补污染。
  await expect(page.getByRole("button", { name: "撤销" })).toBeEnabled();
  await page.getByRole("button", { name: "撤销" }).click();
  await expect(page.getByRole("button", { name: "撤销" })).toBeDisabled();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const afterLate = await decodeUpload(page, uploads[0]!.body, "image/png");
  const watermark = pixelAt(afterLate, 48, 20);
  expect(watermark.slice(0, 3)).toEqual([224, 32, 240]);
});

test("measures real browser processing wall time and responsiveness on a 4MP image with ~1MP mask", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  await routeInpaintSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="2000" height="2000"><rect width="2000" height="2000" fill="#0aa05a"/><rect x="500" y="500" width="1000" height="1000" fill="#e020f0"/></svg>',
    "inpaint-perf.svg",
  );
  await mountWithUploadCapture(page, uploads, "inpaint-perf.svg");
  await expect(page.locator('canvas[aria-label="图片检查画布"]')).toBeVisible();
  await expect(page.getByText(/原图 2000×2000/)).toBeVisible();

  await page.getByRole("button", { name: "去水印" }).click();
  // 显示 (303,153)-(597,447) → 自然约 (510,510)-(1490,1490)：4MP 图 / 约 0.96MP 选区
  //（外扩取整后仍稳定低于 1MP 上限）。
  await dragOnCanvas(page, { x: 303, y: 153 }, { x: 597, y: 447 });
  await expect(page.getByText(/已框选修补区域/)).toBeVisible();

  const heapBefore = await page.evaluate(() => {
    const memory = (
      performance as Performance & {
        memory?: { usedJSHeapSize: number };
      }
    ).memory;
    return memory ? memory.usedJSHeapSize : null;
  });
  const startedAt = await page.evaluate(() => performance.now());
  await page.getByRole("button", { name: "预览修补" }).click();
  // 处理期间主线程仍可响应（rAF 回调在 1s 内完成），且可取消。
  await expect(page.getByText(/正在本地修补选中区域/)).toBeVisible();
  const rafLatency = await page.evaluate(
    () =>
      new Promise<number>((resolve) => {
        const startedAt = performance.now();
        requestAnimationFrame(() => resolve(performance.now() - startedAt));
      }),
  );
  // 性能证据在测试内量测，不固化进产品文案：端到端预览墙钟（含本地计算
  // 与消息往返），完成后按钮立即可用。
  await expect(page.getByText(/修补预览完成/)).toBeVisible({ timeout: 60_000 });
  const wallMs = (await page.evaluate(() => performance.now())) - startedAt;
  const heapAfter = await page.evaluate(() => {
    const memory = (
      performance as Performance & {
        memory?: { usedJSHeapSize: number };
      }
    ).memory;
    return memory ? memory.usedJSHeapSize : null;
  });
  // 可核实指标：真实墙钟耗时 > 0；像素传输为确定性算术（请求+响应各 4·W·H 字节）。
  const transferredBytes = 2 * 4 * 2000 * 2000;
  expect(wallMs).toBeGreaterThan(0);
  expect(transferredBytes).toBe(32_000_000);
  expect(rafLatency).toBeLessThan(1000);
  // 主线程 JSHeap 增量只是估算参考（不含 Worker 线程堆），不当作总内存。
  console.log(
    `[inpaint-perf] end-to-end preview wall=${wallMs.toFixed(0)}ms (real browser measurement) transfer=${(transferredBytes / 1048576).toFixed(1)}MB (computed 2×4·W·H) rAF-latency=${rafLatency.toFixed(1)}ms main-thread-jsheap-delta=${heapBefore && heapAfter ? `${((heapAfter - heapBefore) / 1048576).toFixed(1)}MB (estimated, excludes worker heap)` : "unavailable"}`,
  );

  // 放弃预览：不应用、不修改图片。
  await page.getByRole("button", { name: "放弃修补预览" }).click();
  await expect(
    page.getByText(/已放弃修补预览；当前图片未被修改/),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "撤销" })).toBeDisabled();
});

/** 修补后像素在 PNG 导出中的预期值：补丁保持原 alpha（半透明 128），
 * 替换式绘制下透出的是导出底色 #161616，而非被修补前的原图。 */
const TRANSPARENT_PATCHED = [16, 91, 56] as const;

function expectPixelNear(
  image: DecodedImage,
  x: number,
  y: number,
  expected: readonly number[],
  tolerance = 6,
): void {
  const actual = pixelAt(image, x, y);
  for (let channel = 0; channel < 3; channel += 1) {
    expect(
      Math.abs(actual[channel]! - (expected[channel] ?? 0)),
      `pixel (${x},${y}) channel ${channel}`,
    ).toBeLessThanOrEqual(tolerance);
  }
}

test("semi-transparent patches replace pixels (no ghost/alpha stacking) across overlay, rotation, crop and resize", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: { contentType: string; body: Buffer }[] = [];
  // 镂空绿框（不透明）包围半透明水印：选区内像素真正携带 alpha=128，
  // 且选区边界全部落在不透明绿带上，扩散填绿、alpha 原样保留；
  // 远离选区的四角保持全透明（PNG 导出为深底合成值）。
  await routeInpaintSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect x="24" y="4" width="48" height="8" fill="#0aa05a"/><rect x="24" y="28" width="48" height="16" fill="#0aa05a"/><rect x="24" y="12" width="8" height="16" fill="#0aa05a"/><rect x="64" y="12" width="8" height="16" fill="#0aa05a"/><rect x="32" y="12" width="32" height="16" fill="#e020f0" fill-opacity="0.5"/></svg>',
    "inpaint-alpha.svg",
  );
  await mountWithUploadCapture(page, uploads, "inpaint-alpha.svg");
  await expect(page.locator('canvas[aria-label="图片检查画布"]')).toBeVisible();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  const baseline = await decodeUpload(page, uploads[0]!.body, "image/png");
  // 基线：半透明洋红水印叠在绿背景上的 PNG 深底合成值。
  expectPixelNear(baseline, 48, 20, [123, 27, 131], 6);

  const applyInpaintOnce = async () => {
    await page.getByRole("button", { name: "去水印" }).click();
    await dragOnCanvas(page, { x: 281, y: 168 }, { x: 619, y: 357 });
    await page.getByRole("button", { name: "预览修补" }).click();
    await expect(page.getByText(/修补预览完成/)).toBeVisible({
      timeout: 30_000,
    });
    await page.getByRole("button", { name: "应用修补" }).click();
    await expect(page.getByText(/已应用修补/)).toBeVisible();
  };
  await applyInpaintOnce();

  // 第一次应用：导出像素 = 0.5×绿 + 0.5×深底（替换式），旧实现会保留
  // 原水印鬼影（≈ 0.5×绿 + 0.5×旧水印）而在此失败。
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(2);
  const firstApplied = await decodeUpload(page, uploads[1]!.body, "image/png");
  expectPixelNear(firstApplied, 48, 20, TRANSPARENT_PATCHED);
  expectOutsideIdentical(baseline, firstApplied, {
    x: 29,
    y: 9,
    width: 38,
    height: 22,
  });

  // 第二次在同一区域修补：基线合成也是替换式，结果不叠加、不漂移。
  await applyInpaintOnce();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(3);
  const secondApplied = await decodeUpload(page, uploads[2]!.body, "image/png");
  expectPixelNear(secondApplied, 48, 20, TRANSPARENT_PATCHED);
  expectOutsideIdentical(baseline, secondApplied, {
    x: 29,
    y: 9,
    width: 38,
    height: 22,
  });

  // 旋转 90°：输出 48×96，修补中心 (48,20) → 输出 (28,48)。
  await page.getByRole("button", { name: "旋转 90°" }).click();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(4);
  const rotated = await decodeUpload(page, uploads[3]!.body, "image/png");
  expect(rotated.width).toBe(48);
  expect(rotated.height).toBe(96);
  expectPixelNear(rotated, 28, 48, TRANSPARENT_PATCHED);
  await page.getByRole("button", { name: "撤销" }).click();

  // 裁剪自然 (16,0)-(80,48)：输出 64×48，修补中心 → 裁剪相对 (32,20)。
  await page.getByRole("button", { name: "裁剪" }).click();
  await dragOnCanvas(page, { x: 150, y: 75 }, { x: 750, y: 562 });
  await expect(page.getByRole("button", { name: "撤销" })).toBeEnabled();
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(5);
  const cropped = await decodeUpload(page, uploads[4]!.body, "image/png");
  expect(cropped.width).toBe(64);
  expect(cropped.height).toBe(48);
  expectPixelNear(cropped, 32, 20, TRANSPARENT_PATCHED);
  await page.getByRole("button", { name: "撤销" }).click();

  // 指定输出尺寸 48×24：修补区域等比缩小后仍是替换式像素。
  await page.getByLabel("输出宽度").fill("48");
  await page.getByLabel("输出高度").fill("24");
  await page.getByRole("button", { name: "应用尺寸" }).click();
  await expect(page.getByText(/输出 48×24/)).toBeVisible();
  await page.getByRole("button", { name: "保存标注图" }).click();
  await expect.poll(() => uploads.length).toBe(6);
  const resized = await decodeUpload(page, uploads[5]!.body, "image/png");
  expect(resized.width).toBe(48);
  expect(resized.height).toBe(24);
  expectPixelNear(resized, 24, 10, TRANSPARENT_PATCHED);
});

test("screenshot session mode exports applied inpaint pixels for explicit recognition", async ({
  page,
}) => {
  test.setTimeout(90_000);
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
  await routeInpaintSource(
    page,
    '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="48"><rect width="96" height="48" fill="#0aa05a"/><rect x="32" y="12" width="32" height="16" fill="#e020f0"/></svg>',
    "inpaint-session.svg",
  );
  await mountHost(page, {
    ...snapshot,
    capabilities: [
      "recognition.results",
      "recognition.annotation",
      "recognition.screenshotSession",
    ],
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: "/inpaint-session.svg",
          mediaType: "image/png",
          byteLength: 4096,
        },
        screenshotSession: {
          sessionId: "inpaint-session-e2e",
          revision: 0,
          textSelectionRequested: false,
          sceneEditing: false,
        },
      },
    },
  });
  await expect(page.locator('canvas[aria-label="图片检查画布"]')).toBeVisible();

  // 会话模式同一“去水印”入口：框选 → 预览 → 应用，推进会话修订。
  await page.getByRole("button", { name: "去水印" }).click();
  await dragOnCanvas(page, { x: 281, y: 168 }, { x: 619, y: 357 });
  await page.getByRole("button", { name: "预览修补" }).click();
  await expect(page.getByText(/修补预览完成/)).toBeVisible({
    timeout: 30_000,
  });
  await page.getByRole("button", { name: "应用修补" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: "inpaint-session-e2e", revision: 1 },
  });

  // 显式识别走会话导出路径：上传的识别输入包含已应用修补像素。
  await page.getByRole("button", { name: "识别当前图" }).click();
  await expect.poll(() => uploads.length).toBe(1);
  await expectCommand(page, {
    scope: "recognition",
    action: "recognizeScreenshotImage",
    arguments: {
      resourceUri:
        "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      sessionId: "inpaint-session-e2e",
      revision: 1,
    },
  });
  const recognitionInput = await decodeUpload(
    page,
    uploads[0]!.body,
    "image/png",
  );
  expectPixelNear(recognitionInput, 48, 20, [10, 160, 90]);
});
