import { expect, test, type Page } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

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

/** 预览画布逻辑坐标采样（900×600 逻辑画布）。 */
async function previewPixels(page: Page, points: [number, number][]) {
  return page.evaluate((points) => {
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
    [940, 175],
  ]);
  // 正确：dest(800) → source 762.5+(800-700)/2=812.5；dest(940) → 882.5。
  expect(Math.abs(sampled[0]![0] - gray(812))).toBeLessThanOrEqual(6);
  expect(Math.abs(sampled[1]![0] - gray(882))).toBeLessThanOrEqual(6);
});

test("magnifier: moves with object and samples base+patches only, replacing lens area", async ({
  page,
}) => {
  const { uploads } = await setup(page);
  // 镜下先画一个不透明矩形标注（z 序在放大镜之下）：镜内不得透出它；
  // 镜的 source 左缘越出底图（<0）→ 该处为透明采样，替换后保持透明
  // （旧实现 source-over 会残留镜下矩形/底图）。
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
  // 输出像素 (275,200)：裁剪内自然坐标 = 100+275*2=650, 50+200*2=450；
  // 镜 dest(650,450) → source = 550+(650-500)/2=625, 150+(450-150)/2=300。
  const sampled = await pixels(page, png, [[275, 200]]);
  // 镜内像素经 2× 上采样 + 输出 2× 缩小双重平滑：容差 10 覆盖重采样噪声，
  // 仍远小于坐标/锚定错误造成的几十级漂移。
  expect(Math.abs(sampled[0]![0] - gray(625))).toBeLessThanOrEqual(10);
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
  // 底图保护：全部对象清除后导出与最初基准逐像素一致。
  await action(page, "选择");
  await page.getByLabel("选择标注对象").selectOption({ index: 1 });
  await action(page, "解锁");
  await action(page, "橡皮擦");
  await drag(page, 40, 130, 380, 130);
  const cleaned = await save(page, uploads);
  const diff = await page.evaluate(
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
          .getImageData(0, 0, canvas.width, canvas.height).data;
      };
      const left = await decode(a);
      const right = await decode(b);
      let changed = 0;
      for (let index = 0; index < left.length; index += 1) {
        if (left[index] !== right[index]) changed += 1;
      }
      return changed;
    },
    { a: pristine.toString("base64"), b: cleaned.toString("base64") },
  );
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
  const undoneDiff = await page.evaluate(
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
          .getImageData(0, 0, canvas.width, canvas.height).data;
      };
      const left = await decode(a);
      const right = await decode(b);
      let changed = 0;
      for (let index = 0; index < left.length; index += 1) {
        if (left[index] !== right[index]) changed += 1;
      }
      return changed;
    },
    { a: fullOpacity.toString("base64"), b: undone.toString("base64") },
  );
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
