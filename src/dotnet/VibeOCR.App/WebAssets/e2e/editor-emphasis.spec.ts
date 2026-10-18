import { writeFile } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

/** #235 强调工具回归：聚光灯多区域并集/底图范围、放大镜真实倍率与
 * 边界/透明采样契约、对象橡皮擦锁定/Esc/底图保护、水印透明度像素证据。
 * 断言基于真实保存 PNG/预览画布像素，不重写快照。 */

/** 水平黑→白渐变：任意 x 唯一灰度，可精确反推采样来源（真实倍率/边界）。 */
const GRADIENT_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600">' +
  '<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="0">' +
  '<stop offset="0" stop-color="#000000"/>' +
  '<stop offset="1" stop-color="#ffffff"/></linearGradient></defs>' +
  '<rect width="900" height="600" fill="url(#g)"/></svg>';

/** 800×600 四色（红/绿/蓝/白 象限）：与 900×600 逻辑画布宽高比不同，
 * 预览存在左右 letterbox（透明），用于遮罩“仅底图范围”验收。 */
const QUADRANT_800 =
  '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="600">' +
  '<rect width="400" height="300" fill="#e02020"/>' +
  '<rect x="400" width="400" height="300" fill="#12a150"/>' +
  '<rect y="300" width="400" height="300" fill="#1f6feb"/>' +
  '<rect x="400" y="300" width="400" height="300" fill="#ffffff"/>' +
  "</svg>";

const SESSION = "33333333333333333333333333333333";

async function setup(
  page: Page,
  options: { svg?: string; natural?: { width: number; height: number } } = {},
) {
  const svg = options.svg ?? GRADIENT_SVG;
  const natural = options.natural ?? { width: 900, height: 600 };
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: Buffer[] = [];
  const projects: unknown[] = [];
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
  await page.route("**/__project", async (route) => {
    projects.push(route.request().postDataJSON());
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ contributeId: "a".repeat(32) }),
    });
  });
  await page.route("**/emphasis-source.svg", (route) =>
    route.fulfill({ contentType: "image/svg+xml", body: svg }),
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
          url: "/emphasis-source.svg",
          mediaType: "image/png",
          byteLength: 2048,
        },
        screenshotSession: { sessionId: SESSION, revision: 0 },
      },
    },
  });
  await expect(
    page.getByText(new RegExp(`原图 ${natural.width}×${natural.height}`)),
  ).toBeVisible();
  return { uploads, projects, canvas: page.getByLabel("图片检查画布") };
}

async function at(page: Page, x: number, y: number) {
  const canvas = page.getByLabel("图片检查画布");
  await canvas.scrollIntoViewIfNeeded();
  const box = (await canvas.boundingBox())!;
  return {
    x: box.x + (box.width * x) / 900,
    y: box.y + (box.height * y) / 600,
  };
}

async function drag(
  page: Page,
  x: number,
  y: number,
  endX: number,
  endY: number,
) {
  const from = await at(page, x, y);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  const to = await at(page, endX, endY);
  await page.mouse.move(to.x, to.y);
  await page.mouse.up();
}

async function action(page: Page, name: string) {
  await page.getByRole("button", { name, exact: true }).click();
}

async function save(page: Page, uploads: Buffer[]) {
  const count = uploads.length;
  await action(page, "保存标注图");
  await expect.poll(() => uploads.length).toBe(count + 1);
  return uploads[count]!;
}

/** 保存 PNG 上采样；points 为输出自然像素坐标。 */
async function pixels(page: Page, png: Buffer, points: [number, number][]) {
  return page.evaluate(
    async ({ png, points }) => {
      const bitmap = await createImageBitmap(
        new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
          type: "image/png",
        }),
      );
      const canvas = document.createElement("canvas");
      const width = bitmap.width;
      const height = bitmap.height;
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext("2d")!;
      context.drawImage(bitmap, 0, 0);
      // ImageBitmap.close() 后尺寸归零：先缓存再用，不能以 bitmap 尺寸 clamp。
      bitmap.close();
      return points.map(([x, y]) => {
        const px = Math.max(0, Math.min(width - 1, Math.round(x)));
        const py = Math.max(0, Math.min(height - 1, Math.round(y)));
        return Array.from(context.getImageData(px, py, 1, 1).data);
      });
    },
    { png: png.toString("base64"), points },
  );
}

/** 比较解码后的自然像素，避免 PNG 编码字节差异影响判断。 */
async function pixelDifference(page: Page, a: Buffer, b: Buffer) {
  return page.evaluate(
    async ({ a, b }) => {
      const decode = async (base64: string) => {
        const bitmap = await createImageBitmap(
          new Blob([Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))], {
            type: "image/png",
          }),
        );
        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        const context = canvas.getContext("2d")!;
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        return context.getImageData(0, 0, canvas.width, canvas.height);
      };
      const [left, right] = await Promise.all([decode(a), decode(b)]);
      if (left.width !== right.width || left.height !== right.height)
        throw new Error("输出尺寸不同");
      let changed = 0;
      for (let i = 0; i < left.data.length; i += 1)
        if (left.data[i] !== right.data[i]) changed += 1;
      return changed;
    },
    { a: a.toString("base64"), b: b.toString("base64") },
  );
}
/** 预览画布逻辑坐标采样（900×600 逻辑画布）。 */
async function previewPixels(page: Page, points: [number, number][]) {
  return page.evaluate(async (points) => {
    // 等待已排队的画布绘制完成，避免取到属性/裁剪提交前的旧帧。
    await new Promise<void>((resolve) =>
      requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
    );
    const canvas = document.querySelector<HTMLCanvasElement>(
      '[aria-label="图片检查画布"]',
    )!;
    const context = canvas.getContext("2d")!;
    const logicalWidth = Number(canvas.dataset.coordinateWidth ?? 900);
    const logicalHeight = Number(canvas.dataset.coordinateHeight ?? 600);
    return points.map(([x, y]) => {
      const px = Math.max(
        0,
        Math.min(
          canvas.width - 1,
          Math.round((x * canvas.width) / logicalWidth),
        ),
      );
      const py = Math.max(
        0,
        Math.min(
          canvas.height - 1,
          Math.round((y * canvas.height) / logicalHeight),
        ),
      );
      return Array.from(context.getImageData(px, py, 1, 1).data);
    });
  }, points);
}

const gray = (x: number) => Math.round((x / 900) * 255);

test("spotlight pixels: overlap equals single hole and letterbox stays transparent", async ({
  page,
}) => {
  const { uploads } = await setup(page, {
    svg: QUADRANT_800,
    natural: { width: 800, height: 600 },
  });
  await action(page, "聚光灯");
  await drag(page, 200, 100, 360, 220);
  await action(page, "选择");
  await drag(page, 280, 160, 281, 161);
  // “添加区域”是操作入口：必须位于上排工具栏（图片编辑工具），不在下排。
  const toolbarName = await page
    .getByRole("button", { name: "添加区域", exact: true })
    .evaluate((button) =>
      button.closest('[role="toolbar"]')?.getAttribute("aria-label"),
    );
  expect(toolbarName).toBe("图片编辑工具");
  await action(page, "添加区域");
  await drag(page, 320, 140, 480, 260);
  // 预览画布：遮罩只覆盖底图显示矩形（50..850），letterbox（<50）保持透明。
  const preview = await previewPixels(page, [
    [25, 300], // letterbox 左：透明，不被染黑
    [240, 140], // 仅区域 1：原始红色（孔洞）
    [350, 180], // 重叠：与单孔同色（不加深）
    [430, 110], // 两区域间隙但在联合包围盒内：必须是压暗（区分并集与 bbox 回退）
    [600, 150], // 底图内区域外：压暗的绿色
  ]);
  // letterbox 左侧完全透明：遮罩不把底图外区域染黑（alpha=0）。
  expect(preview[0]![3]).toBe(0);
  expect(preview[1]!.slice(0, 3)).toEqual([224, 32, 32]);
  expect(preview[2]!.slice(0, 3)).toEqual(preview[1]!.slice(0, 3));
  // 间隙在红色象限：压暗红（≈45% 亮度）而非原始红，也非绿色象限色。
  expect(preview[3]![0]).toBeLessThan(224);
  expect(preview[3]![0]).toBeGreaterThan(50);
  // 导出为自然 800×600（无 letterbox）：孔洞/压暗与预览同源。
  const png = await save(page, uploads);
  const exported = await pixels(page, png, [
    [190, 140],
    [300, 180],
    [500, 150],
    [790, 300],
  ]);
  expect(exported[0]!.slice(0, 3)).toEqual([224, 32, 32]);
  expect(exported[1]!.slice(0, 3)).toEqual([224, 32, 32]);
  expect(exported[2]![1]).toBeLessThan(161);
});

test("magnifier: real zoom anchored to unclamped geometry at canvas edge", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  // 镜 250×150，左上 (700,100)：右缘越出画布 50px。采样几何由未裁切标注
  // 矩形决定：中心 (825,175)，zoom=2 → source x 762.5..887.5；仅输出按
  // 画布裁切。若实现先 clamp 再推 source（重居中到 800），采样会整体左移
  // 约 62px 源像素并在此失败。
  await action(page, "放大镜");
  await drag(page, 700, 100, 950, 250);
  await expect(
    page.getByText("采样范围：底图与已应用的修补（不含标注）"),
  ).toBeVisible();
  const png = await save(page, uploads);
  const sampled = await pixels(page, png, [
    [800, 175],
    [880, 175],
  ]);
  // dest(800) → source812.5；dest(880) → source852.5，两个取点都在输出边界内。
  expect(Math.abs(sampled[0]![0] - gray(812))).toBeLessThanOrEqual(2);
  expect(Math.abs(sampled[1]![0] - gray(852))).toBeLessThanOrEqual(2);
});

test("magnifier: moves with object and excludes annotation pixels", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  // 镜下先画不透明矩形标注：镜内只显示底图，不得采到下层标注。
  await action(page, "矩形");
  await page.getByLabel("填充", { exact: true }).check();
  await drag(page, 380, 200, 620, 400);
  await action(page, "放大镜");
  await drag(page, 450, 150, 650, 300); // 200×150，中心 (550,225)，zoom=2 → source 500..600/150..225
  const png = await save(page, uploads);
  const sampled = await pixels(page, png, [
    [500, 175], // source (525,162)：底图渐变，绝不透出镜下矩形填充色
    [630, 280], // source (590,215)：仍为底图渐变
  ]);
  expect(Math.abs(sampled[0]![0] - gray(525))).toBeLessThanOrEqual(6);
  expect(Math.abs(sampled[1]![0] - gray(590))).toBeLessThanOrEqual(6);
  expect(sampled[0]![2]).toBeLessThanOrEqual(sampled[0]![0] + 8);
  // 移动放大镜（整体拖动）后，同一输出位置的采样随之改变（对象可移动）：
  // 右移 50 → 新镜 500..700，source 550..650；dest(560,175) → source 580。
  await action(page, "选择");
  await drag(page, 550, 225, 551, 226);
  await drag(page, 550, 225, 600, 225);
  const moved = await save(page, uploads);
  const movedSample = await pixels(page, moved, [[560, 175]]);
  expect(Math.abs(movedSample[0]![0] - gray(580))).toBeLessThanOrEqual(10);
});

test("magnifier: crop + non-1:1 export keeps sampling consistent", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  await action(page, "放大镜");
  await drag(page, 500, 150, 700, 300); // 200×150，中心 (600,225)，zoom=2 → source 550..650/150..225
  await action(page, "裁剪");
  await drag(page, 100, 50, 700, 550); // 输出 600×500
  await page.getByLabel("等比缩放").selectOption("50"); // 再缩到 300×250
  const png = await save(page, uploads);
  const info = await page.evaluate(async (base64) => {
    const bitmap = await createImageBitmap(
      new Blob([Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))], {
        type: "image/png",
      }),
    );
    const size = { width: bitmap.width, height: bitmap.height };
    bitmap.close();
    return size;
  }, png.toString("base64"));
  expect(info).toEqual({ width: 300, height: 250 });
  // 输出 (210,75) 对应自然 (520,200)，在镜内且偏离中心；source x=560，
  // 与未放大的原图 x520 相差约11级，2级容差可排除不放大/错误锚点。
  const sampled = await pixels(page, png, [[210, 75]]);
  // 2级仅容许8-bit渐变重采样量化，不能掩盖坐标偏移。
  expect(Math.abs(sampled[0]![0] - gray(560))).toBeLessThanOrEqual(2);
});

test("eraser: locked objects protected, Esc cancels gesture, base pixels intact", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  const pristine = await save(page, uploads);
  await action(page, "遮挡");
  await drag(page, 100, 100, 180, 160);
  await action(page, "遮挡");
  await drag(page, 300, 100, 380, 160);
  await action(page, "选择");
  await drag(page, 140, 130, 141, 131);
  await action(page, "锁定"); // 锁定第一个遮挡
  await action(page, "橡皮擦");
  // 一次拖动扫过两个对象（含稀疏 pointermove 的中间路径）。
  await drag(page, 40, 130, 380, 130);
  await action(page, "选择");
  const options = page.getByLabel("选择标注对象").locator("option");
  // 未锁定对象被整次擦除；锁定对象保留。
  await expect.poll(async () => options.count()).toBe(2); // 未选 + 锁定的遮挡
  await expect
    .poll(async () => options.last().textContent())
    .toContain("已锁定");
  await action(page, "撤销");
  await expect.poll(async () => options.count()).toBe(3); // 未锁定的遮挡恢复（一次撤销）
  // Esc 取消：拖拽中按 Escape，松开后对象保留且不产生历史。
  await action(page, "橡皮擦");
  const undoEnabledBefore = await page
    .getByRole("button", { name: "撤销" })
    .isEnabled();
  const from = await at(page, 120, 130);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x + 40, from.y);
  await page.keyboard.press("Escape");
  await page.mouse.up();
  await action(page, "选择");
  await expect.poll(async () => options.count()).toBe(3);
  const undoEnabledAfter = await page
    .getByRole("button", { name: "撤销" })
    .isEnabled();
  // 擦除前的“锁定”历史仍在（可撤销），但 Esc 的手势不得追加新历史。
  expect(undoEnabledAfter).toBe(undoEnabledBefore);
  await action(page, "撤销");
  await expect(options).toHaveCount(3);
  await expect(options.nth(1)).not.toContainText("已锁定");
  await action(page, "重做");
  await expect(options.nth(1)).toContainText("已锁定");
  // 底图保护：全部对象清除后导出与最初基准逐像素一致。
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ index: 1 });
  await action(page, "解锁");
  await action(page, "橡皮擦");
  await drag(page, 40, 130, 380, 130);
  const cleaned = await save(page, uploads);
  const diff = await pixelDifference(page, pristine, cleaned);
  expect(diff).toBe(0);
});

test("watermark: text and imported image opacity edit, undo, and project payload", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const baseUrl = "https://app.vibeocr/__resource/wm-base";
  const imageUrl = "https://app.vibeocr/__resource/wm-png";
  const pngs = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 900;
    canvas.height = 600;
    const context = canvas.getContext("2d")!;
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, 900, 600);
    const base = canvas.toDataURL("image/png").split(",")[1]!;
    canvas.width = 160;
    canvas.height = 80;
    const image = canvas.getContext("2d")!;
    image.fillStyle = "#0000ff";
    image.fillRect(0, 0, 160, 80);
    return { base, image: canvas.toDataURL("image/png").split(",")[1]! };
  });
  const original = Buffer.from(pngs.base, "base64");
  const importPng = Buffer.from(pngs.image, "base64");
  // 以真实宿主源（app.vibeocr）挂载应用：资源 URI 与 CSP self 同源
  // （与 editor-import 同一模式），本地静态资产经代理回源。
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: original }),
  );
  await page.route(imageUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: importPng }),
  );
  const uploads: Buffer[] = [];
  const projects: unknown[] = [];
  await page.route("**/__annotation", async (route) => {
    uploads.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: "https://app.vibeocr/a" }),
    });
  });
  await page.route("**/__project", async (route) => {
    projects.push(route.request().postDataJSON());
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ contributeId: "a".repeat(32) }),
    });
  });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.results", "recognition.annotation"],
      features: {
        imageEdit: {
          isBusy: false,
          statusCode: "recognition.completed",
          input: {
            url: baseUrl,
            mediaType: "image/png",
            byteLength: original.length,
          },
          screenshotSession: { sessionId: SESSION, revision: 0 },
        },
      },
    },
    "https://app.vibeocr/",
  );
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  // 授权导入图片对象（#232 宿主导入路径，无外部 URL/新权限）。
  await action(page, "粘贴系统内容");
  await page.evaluate(() => {
    (window as unknown as { __testHost: { commands: unknown[] } }).__testHost;
  });
  const descriptor = {
    requestId: "wm-image",
    sessionId: SESSION,
    revision: 0,
    items: [
      {
        kind: "image",
        assetId: "wm-png",
        resourceUri: imageUrl,
        byteLength: importPng.length,
        pixelWidth: 160,
        pixelHeight: 80,
      },
    ],
  };
  await page.evaluate((descriptor) => {
    const host = (
      window as unknown as {
        __testHost: { state: (scope: string, value: unknown) => void };
      }
    ).__testHost;
    host.state("imageEdit", {
      isBusy: false,
      statusCode: "recognition.completed",
      input: {
        url: "https://app.vibeocr/__resource/wm-base",
        mediaType: "image/png",
        byteLength: 2048,
      },
      screenshotSession: {
        sessionId: "33333333333333333333333333333333",
        revision: 0,
      },
      import: descriptor,
    });
  }, descriptor);
  await expect(page.getByLabel("选择标注对象")).toContainText("图片");
  // 文字水印：默认不透明度 100 保存基准，再改 30% 对比像素。
  await action(page, "文字");
  await drag(page, 100, 400, 420, 470);
  const fullOpacity = await save(page, uploads);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "2 · 文字" });
  const opacityInput = page.getByLabel("水印不透明度");
  await opacityInput.fill("30");
  await opacityInput.press("Enter");
  const lowOpacity = await save(page, uploads);
  const blend = await page.evaluate(
    async ({ a, b }) => {
      const decode = async (base64: string) => {
        const bitmap = await createImageBitmap(
          new Blob([Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))], {
            type: "image/png",
          }),
        );
        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        canvas.getContext("2d")!.drawImage(bitmap, 0, 0);
        bitmap.close();
        return canvas
          .getContext("2d")!
          .getImageData(0, 0, canvas.width, canvas.height);
      };
      const before = await decode(a);
      const after = await decode(b);
      let worst:
        | {
            x: number;
            y: number;
            before: number[];
            after: number[];
            expect: number[];
          }
        | undefined;
      for (let y = 300; y < 600 && !worst; y += 1) {
        for (let x = 0; x < 900 && !worst; x += 1) {
          const index = (y * 900 + x) * 4;
          const delta =
            Math.abs(before.data[index]! - after.data[index]!) +
            Math.abs(before.data[index + 1]! - after.data[index + 1]!) +
            Math.abs(before.data[index + 2]! - after.data[index + 2]!);
          if (delta < 60) continue;
          const expected = Array.from(
            [0, 1, 2].map(
              (channel) =>
                0.3 * before.data[index + channel]! +
                0.7 * after.data[index + channel]!,
            ),
          );
          // 30% 不透明度：after ≈ 0.3*glyph + 0.7*background；用 before
          // （100% 字形像素）与 after 反解 background 后验证混合比。
          worst = {
            x,
            y,
            before: [
              before.data[index]!,
              before.data[index + 1]!,
              before.data[index + 2]!,
            ],
            after: [
              after.data[index]!,
              after.data[index + 1]!,
              after.data[index + 2]!,
            ],
            expect: expected,
          };
        }
      }
      return worst;
    },
    { a: fullOpacity.toString("base64"), b: lowOpacity.toString("base64") },
  );
  expect(blend).toBeDefined();
  // after ≈ 0.3×before + 0.7×背景(255,255,255)：白底契约可精确预测。
  for (let channel = 0; channel < 3; channel += 1) {
    const expected = 0.3 * blend!.before[channel]! + 0.7 * 255;
    expect(Math.abs(blend!.after[channel]! - expected)).toBeLessThanOrEqual(12);
  }
  // 撤销恢复 100% 像素。
  await action(page, "撤销");
  const undone = await save(page, uploads);
  const undoneDiff = await pixelDifference(page, fullOpacity, undone);
  expect(undoneDiff).toBe(0);
  // 图片水印不透明度：就地编辑入历史，工程载荷携带 strokeOpacity。
  await page.getByLabel("选择标注对象").selectOption({ label: "1 · 图片" });
  await opacityInput.fill("50");
  await opacityInput.press("Enter");
  await action(page, "保存工程");
  await expect.poll(() => projects.length).toBe(1);
  const payload = projects[0] as {
    state: { marks: { tool: string; style?: { strokeOpacity?: number } }[] };
  };
  const imageMark = payload.state.marks.find((mark) => mark.tool === "image");
  expect(imageMark?.style?.strokeOpacity).toBe(50);
  const textMark = payload.state.marks.find((mark) => mark.tool === "text");
  expect(textMark?.style?.strokeOpacity).toBe(100);
});

test("F1 spotlight rotation 90/270: mask limited to rotated base bounds", async ({
  page,
}, testInfo) => {
  const { uploads } = await setup(page, {
    svg:
      '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600">' +
      '<rect width="900" height="600" fill="#12a150"/></svg>',
  });
  await action(page, "聚光灯");
  await drag(page, 380, 260, 520, 340); // 孔洞以画布中心为对称
  for (const rotation of [90, 270]) {
    for (let turn = 0; turn < (rotation === 90 ? 1 : 2); turn += 1) {
      await action(page, "旋转 90°");
    }
    // 900×600 旋转 90° 后在 900×600 画布上底图实际占 x250..650，y0..600。
    // 旧实现用未旋转尺寸推包围盒（x150..750，y100..500）会在此失败。
    const preview = await previewPixels(page, [
      [200, 300], // 旋转边距：透明，不被染黑
      [450, 50], // 底图内、孔洞外：必须压暗
      [450, 300], // 孔洞（绕画布中心旋转后仍在中心）：原始绿色
    ]);
    expect(preview[0]![3]).toBe(0);
    expect(preview[1]![1]).toBeLessThan(161);
    expect(preview[2]!.slice(0, 3)).toEqual([18, 161, 80]);
    const exported = await pixels(page, await save(page, uploads), [
      [300, 75],
      [300, 450],
    ]);
    expect(exported[0]![1]).toBeLessThan(161);
    expect(exported[1]!.slice(0, 3)).toEqual([18, 161, 80]);
    await page.screenshot({
      path: testInfo.outputPath("spotlight-rotation-" + rotation + ".png"),
    });
  }
});

test("F2 magnifier crop: preview and export sample the same unclipped base", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  await action(page, "放大镜");
  await drag(page, 500, 150, 700, 300); // 中心 (600,225)，zoom=2 → source 550..650/150..225
  await action(page, "裁剪");
  await drag(page, 650, 50, 900, 550); // 裁剪 (650,50)-(900,550)
  // 预览 (675,225)：source x = 550+(675-500)/2 = 637.5（灰度约 180）。
  // 旧实现把 crop 传进采样基底：crop 外（<650）被清空 → 此处透明，失败。
  const preview = await previewPixels(page, [[675, 225]]);
  expect(Math.abs(preview[0]![0] - gray(637))).toBeLessThanOrEqual(6);
  expect(preview[0]![3]).toBe(255);
  const png = await save(page, uploads);
  const info = await page.evaluate(async (base64) => {
    const bitmap = await createImageBitmap(
      new Blob([Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))], {
        type: "image/png",
      }),
    );
    const size = { width: bitmap.width, height: bitmap.height };
    bitmap.close();
    return size;
  }, png.toString("base64"));
  expect(info).toEqual({ width: 250, height: 500 });
  // 导出 (25,175) = 自然 (675,225)：与预览同像素（不透明同一灰度）。
  const sampled = await pixels(page, png, [[25, 175]]);
  expect(Math.abs(sampled[0]![0] - gray(637))).toBeLessThanOrEqual(6);
  expect(sampled[0]![3]).toBe(255);
});

test("F3 text watermark: draft text/font kept when editing opacity in place", async ({
  page,
}) => {
  const { projects } = await setup(page);
  await action(page, "文字");
  await drag(page, 100, 100, 360, 170);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "1 · 文字" });
  await action(page, "编辑文字");
  const textarea = page.getByLabel("标注文字");
  await textarea.fill("B水印草稿");
  await page.getByLabel("粗体", { exact: true }).check();
  await page.getByLabel("文字背景").check();
  const opacityInput = page.getByLabel("水印不透明度");
  await opacityInput.fill("30");
  await opacityInput.press("Enter");
  await action(page, "完成文字编辑");
  await action(page, "保存工程");
  await expect.poll(() => projects.length).toBe(1);
  const payload = projects[0] as {
    state: {
      marks: {
        tool: string;
        text?: string;
        style?: {
          strokeOpacity?: number;
          textBackground?: unknown;
          fontBold?: boolean;
        };
      }[];
    };
  };
  const textMark = payload.state.marks.find((mark) => mark.tool === "text");
  // 旧实现用 stale state 快照重置 textEdit：草稿文字与背景样式全部丢失。
  expect(textMark?.text).toBe("B水印草稿");
  expect(textMark?.style?.strokeOpacity).toBe(30);
  expect(textMark?.style?.textBackground).toBeDefined();
  expect(textMark?.style?.fontBold).toBe(true);
});

test("F4 text watermark opacity 0 also removes its background", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  const pristine = await save(page, uploads);
  await action(page, "文字");
  await drag(page, 100, 400, 420, 470);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "1 · 文字" });
  await page.getByLabel("文字背景").check();
  const opacityInput = page.getByLabel("水印不透明度");
  await opacityInput.fill("0");
  await opacityInput.press("Enter");
  const zeroed = await save(page, uploads);
  // 整体不透明度 0：文字与其背景都必须消失，导出与无对象基准零差异。
  const diff = await pixelDifference(page, pristine, zeroed);
  expect(diff).toBe(0);
});

test("F5 magnifier samples applied patch and keeps transparent margin transparent", async ({
  page,
}, testInfo) => {
  test.setTimeout(90_000);
  // 真实本地修补：框选洋红块 → 预览 → 应用（写入历史，成为采样基底一部分）。
  const { uploads } = await setup(page, {
    svg:
      '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600">' +
      '<rect x="40" y="40" width="820" height="520" fill="#0aa05a"/>' +
      '<rect x="400" y="250" width="100" height="100" fill="#e020f0"/></svg>',
  });
  await testInfo.attach("F5-透明底图+待修补水印", {
    body: await page.getByLabel("图片检查画布").screenshot(),
    contentType: "image/png",
  });
  // 真实本地修补：框选洋红块 → 预览 → 应用（写入历史，成为采样基底一部分）。
  await action(page, "去水印");
  await drag(page, 396, 246, 504, 354);
  await action(page, "预览修补");
  await expect(page.getByText(/修补预览完成/)).toBeVisible({ timeout: 30_000 });
  await action(page, "应用修补");
  await expect(page.getByText(/已应用修补/)).toBeVisible();
  // 放大镜 2× 覆盖修补区中心：若采样原底图（而非底图+已应用修补）会显示洋红。
  await action(page, "放大镜");
  await drag(page, 430, 220, 530, 320);
  await testInfo.attach("F5-修补后放大镜", {
    body: await page.getByLabel("图片检查画布").screenshot(),
    contentType: "image/png",
  });
  // 先验证镜子确实采到已应用修补。
  const png = await save(page, uploads);
  const sampled = await pixels(page, png, [
    [460, 245], // 镜内：修补后的背景绿（洋红已被修补替换）
    [640, 300], // 底图内无修补：原始绿
  ]);
  expect(sampled[0]![1]).toBeGreaterThan(100);
  expect(Math.abs(sampled[0]![1]! - sampled[1]![1]!)).toBeLessThanOrEqual(30);
  expect(sampled[0]![2]).toBeLessThan(sampled[0]![1]!);
  // 镜子中心移到透明边距，dest x50 下方原本有绿色，但采样 source x35
  // 位于透明区；替换式绘制必须清除原绿色，不能 source-over 残留。
  await action(page, "放大镜");
  await drag(page, 0, 100, 200, 250);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "2 · 放大镜" });
  await drag(page, 100, 175, 20, 175);
  const transparentPreview = await previewPixels(page, [
    [50, 175],
    [100, 175],
  ]);
  const transparentPng = await save(page, uploads);
  const transparentExport = await pixels(page, transparentPng, [
    [50, 175],
    [100, 175],
  ]);
  expect(transparentPreview[0]![3]).toBe(0);
  expect(transparentExport[0]![3]).toBe(0);
  expect(transparentPreview[1]![3]).toBe(255);
  expect(transparentExport[1]!.slice(0, 3)).toEqual([10, 160, 90]);
});

test("F5b image watermark: real semi-transparent pixels blend with opacity", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const baseUrl = "https://app.vibeocr/__resource/wm2-base";
  const imageUrl = "https://app.vibeocr/__resource/wm2-png";
  const pngs = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 900;
    canvas.height = 600;
    const context = canvas.getContext("2d")!;
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, 900, 600);
    const base = canvas.toDataURL("image/png").split(",")[1]!;
    canvas.width = 160;
    canvas.height = 80;
    const image = canvas.getContext("2d")!;
    image.fillStyle = "rgba(255,0,0,0.5)";
    image.fillRect(0, 0, 80, 80);
    image.fillStyle = "#0000ff";
    image.fillRect(80, 0, 80, 80);
    return { base, image: canvas.toDataURL("image/png").split(",")[1]! };
  });
  const original = Buffer.from(pngs.base, "base64");
  const importPng = Buffer.from(pngs.image, "base64");
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: original }),
  );
  await page.route(imageUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: importPng }),
  );
  const uploads: Buffer[] = [];
  await page.route("**/__annotation", async (route) => {
    uploads.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: "https://app.vibeocr/a" }),
    });
  });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.results", "recognition.annotation"],
      features: {
        imageEdit: {
          isBusy: false,
          statusCode: "recognition.completed",
          input: {
            url: baseUrl,
            mediaType: "image/png",
            byteLength: original.length,
          },
          screenshotSession: { sessionId: SESSION, revision: 0 },
        },
      },
    },
    "https://app.vibeocr/",
  );
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  await action(page, "粘贴系统内容");
  const descriptor = {
    requestId: "wm2-image",
    sessionId: SESSION,
    revision: 0,
    items: [
      {
        kind: "image",
        assetId: "wm2-png",
        resourceUri: imageUrl,
        byteLength: importPng.length,
        pixelWidth: 160,
        pixelHeight: 80,
      },
    ],
  };
  await page.evaluate((descriptor) => {
    const host = (
      window as unknown as {
        __testHost: { state: (scope: string, value: unknown) => void };
      }
    ).__testHost;
    host.state("imageEdit", {
      isBusy: false,
      statusCode: "recognition.completed",
      input: {
        url: "https://app.vibeocr/__resource/wm2-base",
        mediaType: "image/png",
        byteLength: 2048,
      },
      screenshotSession: {
        sessionId: "33333333333333333333333333333333",
        revision: 0,
      },
      import: descriptor,
    });
  }, descriptor);
  await expect(page.getByLabel("选择标注对象")).toContainText("图片");
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "1 · 图片" });
  const opacityInput = page.getByLabel("水印不透明度");
  await opacityInput.fill("50");
  await opacityInput.press("Enter");
  const count = uploads.length;
  await action(page, "保存标注图");
  await expect.poll(() => uploads.length).toBe(count + 1);
  const png = uploads[count]!;
  // 半透明红半 × 整体 50%：0.5×(0.5红+0.5白)+0.5白 = (255,191,191)；
  // 不透明蓝半 × 50% = (128,128,255)。旧实现若忽略 globalAlpha 会得到
  // (255,128,128)/(0,0,255)，若双重叠加会得到其它值。
  const sampled = await pixels(page, png, [
    [400, 280],
    [500, 280],
  ]);
  expect(sampled[0]!.slice(0, 3)).toEqual([255, 191, 191]);
  // Canvas 的 8-bit alpha 乘法与颜色量化允许最多 2 级舍入误差。
  for (const channel of [0, 1]) {
    expect(Math.abs(sampled[1]![channel]! - 128)).toBeLessThanOrEqual(2);
  }
  expect(sampled[1]![2]).toBe(255);
  expect(sampled[0]![3]).toBe(255);
});

test("F5c emphasis save-restore keeps spotlight/magnifier/watermark pixels", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const baseUrl = "https://app.vibeocr/__resource/rt-base";
  const imageUrl = "https://app.vibeocr/__resource/rt-asset";
  const pngs = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 900;
    canvas.height = 600;
    const context = canvas.getContext("2d")!;
    context.fillStyle = "#f0f0f0";
    context.fillRect(0, 0, 900, 600);
    context.fillStyle = "#1f6feb";
    context.fillRect(360, 80, 180, 120);
    const base = canvas.toDataURL("image/png").split(",")[1]!;
    canvas.width = 90;
    canvas.height = 60;
    const image = canvas.getContext("2d")!;
    image.fillStyle = "#e02020";
    image.fillRect(0, 0, 90, 60);
    return { base, image: canvas.toDataURL("image/png").split(",")[1]! };
  });
  const original = Buffer.from(pngs.base, "base64");
  const importPng = Buffer.from(pngs.image, "base64");
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: original }),
  );
  await page.route(imageUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: importPng }),
  );
  const uploads: Buffer[] = [];
  const projects: unknown[] = [];
  await page.route("**/__annotation", async (route) => {
    uploads.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri: "https://app.vibeocr/a" }),
    });
  });
  await page.route("**/__project", async (route) => {
    projects.push(route.request().postDataJSON());
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ contributeId: "a".repeat(32) }),
    });
  });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.results", "recognition.annotation"],
      features: {
        imageEdit: {
          isBusy: false,
          statusCode: "recognition.completed",
          input: {
            url: baseUrl,
            mediaType: "image/png",
            byteLength: original.length,
          },
          screenshotSession: { sessionId: SESSION, revision: 0 },
        },
      },
    },
    "https://app.vibeocr/",
  );
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  // 授权导入图片资产。
  await action(page, "粘贴系统内容");
  const descriptor = {
    requestId: "rt-image",
    sessionId: SESSION,
    revision: 0,
    items: [
      {
        kind: "image",
        assetId: "rt-asset",
        resourceUri: imageUrl,
        byteLength: importPng.length,
        pixelWidth: 90,
        pixelHeight: 60,
      },
    ],
  };
  await page.evaluate((descriptor) => {
    const host = (
      window as unknown as {
        __testHost: { state: (scope: string, value: unknown) => void };
      }
    ).__testHost;
    host.state("imageEdit", {
      isBusy: false,
      statusCode: "recognition.completed",
      input: {
        url: "https://app.vibeocr/__resource/rt-base",
        mediaType: "image/png",
        byteLength: 2048,
      },
      screenshotSession: {
        sessionId: "33333333333333333333333333333333",
        revision: 0,
      },
      import: descriptor,
    });
  }, descriptor);
  await expect(page.getByLabel("选择标注对象")).toContainText("图片");
  // 文字水印 30%。
  await action(page, "文字");
  await drag(page, 100, 400, 420, 470);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "2 · 文字" });
  const opacityInput = page.getByLabel("水印不透明度");
  await opacityInput.fill("30");
  await opacityInput.press("Enter");
  // 聚光灯 + 放大镜（对象级操作经选择工具选中后不动）。
  await action(page, "聚光灯");
  await drag(page, 200, 100, 360, 220);
  await action(page, "放大镜");
  await drag(page, 500, 150, 700, 300);
  // 保存前导出基准。
  const countBefore = uploads.length;
  await action(page, "保存标注图");
  await expect.poll(() => uploads.length).toBe(countBefore + 1);
  const before = uploads[countBefore]!;
  await action(page, "保存工程");
  await expect.poll(() => projects.length).toBe(1);
  const payload = projects[0] as {
    canvas: { width: number; height: number };
    state: {
      marks: { id: string; tool: string; assetId?: string }[];
    };
  };
  // 以捕获载荷组装真实 manifest 并走生产恢复路径（解析→安装→导出）。
  const manifest = {
    schemaVersion: 1,
    canvas: payload.canvas,
    source: {
      mediaType: "image/png",
      width: 900,
      height: 600,
      length: original.length,
    },
    assets: [
      { id: "rt-asset", width: 90, height: 60, length: importPng.length },
    ],
    state: {
      ...payload.state,
      patches: [],
    },
  };
  const manifestUrl = "https://app.vibeocr/__resource/rt-manifest";
  await page.route(manifestUrl, (route) =>
    route.fulfill({
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify(manifest),
    }),
  );
  const secondSession = "44444444444444444444444444444444";
  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.completed",
    input: {
      url: baseUrl,
      mediaType: "image/png",
      byteLength: original.length,
    },
    screenshotSession: { sessionId: secondSession, revision: 0 },
    projectOpen: {
      pendingId: "b".repeat(32),
      sessionId: secondSession,
      revision: 0,
      generation: 3,
      manifest: {
        url: manifestUrl,
        mediaType: "application/json; charset=utf-8",
        byteLength: JSON.stringify(manifest).length,
      },
      source: {
        url: baseUrl,
        mediaType: "image/png",
        byteLength: original.length,
      },
      assets: [
        {
          id: "rt-asset",
          resource: {
            url: imageUrl,
            mediaType: "image/png",
            byteLength: importPng.length,
          },
        },
      ],
      patches: [],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackProjectOpen",
    arguments: {
      pendingId: "b".repeat(32),
      sessionId: secondSession,
      revision: 0,
      generation: 3,
    },
  });
  // ack 后宿主切换到新会话才安装已准备的工程，与真实宿主握手一致。
  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.completed",
    input: {
      url: baseUrl,
      mediaType: "image/png",
      byteLength: original.length,
    },
    screenshotSession: {
      sessionId: "55555555555555555555555555555555",
      revision: 0,
    },
  });
  await expect(page.getByText("工程已恢复", { exact: false })).toBeVisible();
  // 恢复后导出必须与保存前逐像素一致（含聚光灯遮罩/孔洞、放大镜采样、
  // 图片资产与水印透明度）。
  const countAfter = uploads.length;
  await action(page, "保存标注图");
  await expect.poll(() => uploads.length).toBe(countAfter + 1);
  const after = uploads[countAfter]!;
  const diff = await pixelDifference(page, before, after);
  expect(diff).toBe(0);
  const beforePath = testInfo.outputPath("emphasis-before-restore.png");
  const afterPath = testInfo.outputPath("emphasis-after-restore.png");
  await writeFile(beforePath, before);
  await writeFile(afterPath, after);
  await testInfo.attach("emphasis-before-restore", {
    path: beforePath,
    contentType: "image/png",
  });
  await testInfo.attach("emphasis-after-restore", {
    path: afterPath,
    contentType: "image/png",
  });
});

test("F6 eraser: hit objects vanish from drag preview immediately", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  const pristine = await save(page, uploads);
  await action(page, "遮挡");
  await drag(page, 100, 100, 180, 160);
  await action(page, "遮挡");
  await drag(page, 300, 100, 380, 160);
  await action(page, "橡皮擦");
  const from = await at(page, 140, 130);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x + 20, from.y);
  await page.waitForTimeout(200);
  // 拖拽中：第一个遮挡已从预览消失（露出底图渐变），第二个仍可见。
  const midDrag = await previewPixels(page, [
    [140, 130],
    [340, 130],
  ]);
  // 底图渐变：gray(140)=40（无遮挡黑块残留）。
  expect(Math.abs(midDrag[0]![0] - gray(140))).toBeLessThanOrEqual(6);
  expect(midDrag[1]![0]).toBeLessThanOrEqual(10);
  await page.mouse.up();
  await action(page, "选择");
  await expect(page.getByLabel("选择标注对象").locator("option")).toHaveCount(
    2,
  );
  // 同一手势只擦掉第一个对象：第二个仍保留；一次撤销恢复整个手势。
  await action(page, "撤销");
  await expect(page.getByLabel("选择标注对象").locator("option")).toHaveCount(
    3,
  );
  await action(page, "橡皮擦");
  await drag(page, 40, 130, 380, 130);
  // 最终底图像素不变。
  const cleaned = await save(page, uploads);
  expect(await pixelDifference(page, cleaned, pristine)).toBe(0);
  await action(page, "撤销");
  await action(page, "选择");
  await expect(page.getByLabel("选择标注对象").locator("option")).toHaveCount(
    3,
  );
});

test("G1 eraser Escape restores pixels before pointerup without switching tools", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  await action(page, "遮挡");
  await drag(page, 100, 100, 180, 160);
  await drag(page, 300, 100, 380, 160);
  const before = await save(page, uploads);
  await action(page, "橡皮擦");
  const from = await at(page, 140, 130);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  const erased = await previewPixels(page, [[140, 130]]);
  expect(erased[0]![0]).toBeGreaterThan(30);
  await page.keyboard.press("Escape");
  // 不切工具，不触发工具按钮里的 cancelDrawing。
  const cancelled = await previewPixels(page, [[140, 130]]);
  expect(cancelled[0]!.slice(0, 3)).toEqual([0, 0, 0]);
  await page.mouse.up();
  expect(await pixelDifference(page, before, await save(page, uploads))).toBe(
    0,
  );
  await action(page, "撤销");
  await expect(page.getByLabel("选择标注对象").locator("option")).toHaveCount(
    2,
  );
});

test("G1 spotlight Escape clears pending region and adds no history", async ({
  page,
}) => {
  const { uploads, projects } = await setup(page);
  await action(page, "聚光灯");
  await drag(page, 100, 100, 200, 200);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "1 · 聚光灯" });
  const before = await previewPixels(page, [
    [500, 250],
    [600, 300],
  ]);
  await action(page, "添加区域");
  const from = await at(page, 500, 250);
  const to = await at(page, 600, 350);
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y);
  await page.keyboard.press("Escape");
  expect(
    await previewPixels(page, [
      [500, 250],
      [600, 300],
    ]),
  ).toEqual(before);
  await page.mouse.up();
  // Esc 后继续在空白框选，旧“添加区域”目标也不能复活。
  await drag(page, 500, 250, 600, 350);
  await action(page, "保存工程");
  await expect.poll(() => projects.length).toBe(1);
  const body = projects[0] as { state: { marks: { regions?: unknown[] }[] } };
  expect(body.state.marks[0]!.regions).toHaveLength(1);
  await action(page, "撤销");
  await action(page, "保存工程");
  await expect.poll(() => projects.length).toBe(2);
  expect(
    (projects[1] as { state: { marks: unknown[] } }).state.marks,
  ).toHaveLength(0);
  const png = await save(page, uploads);
  expect((await pixels(page, png, [[450, 300]]))[0]![0]).toBeGreaterThan(120);
});

test("G2 spotlight mask is bounded spatially and follows object z-order", async ({
  page,
}, info) => {
  const { uploads } = await setup(page);
  await action(page, "矩形");
  await page.getByLabel("填充", { exact: true }).check();
  await page.getByLabel("填充不透明度", { exact: true }).fill("100");
  await page.getByLabel("填充不透明度", { exact: true }).press("Enter");
  await drag(page, 100, 100, 250, 200);
  const annotation = await pixels(page, await save(page, uploads), [
    [150, 150],
  ]);
  await action(page, "聚光灯");
  await drag(page, 600, 400, 700, 500);
  await expect(
    page.getByText("遮罩限定底图范围，按对象层级叠加", { exact: true }),
  ).toBeVisible();
  const over = await pixels(page, await save(page, uploads), [[150, 150]]);
  for (let channel = 0; channel < 3; channel += 1)
    expect(
      Math.abs(over[0]![channel]! - annotation[0]![channel]! * 0.45),
    ).toBeLessThanOrEqual(1);
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ label: "2 · 聚光灯" });
  await action(page, "下移一层");
  const below = await save(page, uploads);
  expect((await pixels(page, below, [[150, 150]]))[0]).toEqual(annotation[0]);
  await action(page, "撤销");
  expect(
    (await pixels(page, await save(page, uploads), [[150, 150]]))[0],
  ).toEqual(over[0]);
  await page.screenshot({ path: info.outputPath("spotlight-z-order.png") });
});

test("G3 watermark opacity typed then drawn directly controls the first text pixels", async ({
  page,
}) => {
  const { uploads, projects } = await setup(page, {
    svg: '<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600"><rect width="900" height="600" fill="#ffffff"/></svg>',
  });
  await action(page, "文字");
  await page.getByLabel("水印不透明度").fill("37");
  // 直接按画布开始首个文字；不手动 Enter、Tab 或 blur。
  await drag(page, 100, 400, 420, 470);
  const png = await save(page, uploads);
  const probes: [number, number][] = [];
  for (let y = 400; y < 500; y += 1)
    for (let x = 100; x < 180; x += 1) probes.push([x, y]);
  const glyph = await pixels(page, png, probes);
  expect(
    Math.abs(Math.min(...glyph.map((pixel) => pixel[1]!)) - 212),
  ).toBeLessThanOrEqual(2);
  expect(
    Math.abs(Math.min(...glyph.map((pixel) => pixel[2]!)) - 180),
  ).toBeLessThanOrEqual(2);
  await action(page, "保存工程");
  await expect.poll(() => projects.length).toBe(1);
  expect(
    (
      projects[0] as {
        state: { marks: { style: { strokeOpacity: number } }[] };
      }
    ).state.marks[0]!.style.strokeOpacity,
  ).toBe(37);
});

for (const axis of ["horizontal", "vertical"] as const) {
  const to = axis === "horizontal" ? [350, 300] : [300, 350];
  test(`G4 ${axis} spotlight creation is ignored without history`, async ({
    page,
  }) => {
    const { uploads, projects } = await setup(page);
    const pristine = await save(page, uploads);
    await action(page, "遮挡");
    await drag(page, 100, 100, 180, 160);
    await action(page, "聚光灯");
    await drag(page, 300, 300, to[0]!, to[1]!);
    await action(page, "保存工程");
    await expect.poll(() => projects.length).toBe(1);
    expect(
      (projects[0] as { state: { marks: unknown[] } }).state.marks,
    ).toHaveLength(1);
    await action(page, "撤销");
    expect(
      await pixelDifference(page, pristine, await save(page, uploads)),
    ).toBe(0);
  });
  test(`G4 ${axis} spotlight region adds no unchanged history`, async ({
    page,
  }) => {
    const { projects } = await setup(page);
    await action(page, "聚光灯");
    await drag(page, 100, 100, 200, 200);
    await action(page, "选择");
    await page.getByLabel("选择标注对象").selectOption({ label: "1 · 聚光灯" });
    await action(page, "添加区域");
    await drag(page, 300, 300, to[0]!, to[1]!);
    await action(page, "撤销");
    await action(page, "保存工程");
    await expect.poll(() => projects.length).toBe(1);
    expect(
      (projects[0] as { state: { marks: unknown[] } }).state.marks,
    ).toHaveLength(0);
  });
}

for (const zoom of [4, 8]) {
  test(`F7 magnifier ${zoom}x draft and final sample identical off-center pixels`, async ({
    page,
  }) => {
    const { uploads } = await setup(page);
    await action(page, "放大镜");
    await page.getByLabel("放大镜倍率").selectOption(String(zoom));
    const from = await at(page, 300, 150);
    const to = await at(page, 700, 350);
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(to.x, to.y);
    const points: [number, number][] = [
      [400, 250],
      [650, 250],
    ];
    const draft = await previewPixels(page, points);
    expect(Math.abs(draft[0]![0] - gray(500 - 100 / zoom))).toBeLessThanOrEqual(
      2,
    );
    expect(Math.abs(draft[1]![0] - gray(500 + 150 / zoom))).toBeLessThanOrEqual(
      2,
    );
    await page.mouse.up();
    const exported = await pixels(page, await save(page, uploads), points);
    expect(exported).toEqual(draft);
  });
}
