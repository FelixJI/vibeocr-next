import { writeFile } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

async function setup(page: Page, natural = { width: 900, height: 600 }) {
  await page.setViewportSize({ width: 1280, height: 900 });
  const uploads: Buffer[] = [];
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
  await page.route("**/objects-source.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body:
        '<svg xmlns="http://www.w3.org/2000/svg" width="' +
        natural.width +
        '" height="' +
        natural.height +
        '"><rect width="100%" height="100%" fill="#12a150"/></svg>',
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
          url: "/objects-source.svg",
          mediaType: "image/png",
          byteLength: 2048,
        },
      },
    },
  });
  await expect(
    page.getByText(new RegExp("原图 " + natural.width + "×" + natural.height)),
  ).toBeVisible();
  return {
    uploads,
    canvas: page.getByLabel("图片检查画布"),
    list: page.getByLabel("选择标注对象"),
  };
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
async function click(
  page: Page,
  x: number,
  y: number,
  modifier?: "Shift" | "Control",
) {
  const target = await at(page, x, y);
  if (modifier) await page.keyboard.down(modifier);
  await page.mouse.click(target.x, target.y);
  if (modifier) await page.keyboard.up(modifier);
}
async function action(page: Page, name: string) {
  await page.getByRole("button", { name, exact: true }).click();
}
async function rectangle(page: Page, x: number, y: number, color = "#e02020") {
  await action(page, "矩形");
  await page.getByLabel("填充", { exact: true }).check();
  await page.getByLabel("填充颜色", { exact: true }).fill(color);
  await page.getByLabel("填充不透明度", { exact: true }).fill("100");
  await drag(page, x, y, x + 100, y + 60);
}
async function save(page: Page, uploads: Buffer[]) {
  const count = uploads.length;
  await action(page, "保存标注图");
  await expect.poll(() => uploads.length).toBe(count + 1);
  return uploads[count]!;
}
async function pixels(
  page: Page,
  png: Buffer,
  positions: { x: number; y: number }[],
) {
  return page.evaluate(
    async ({ png, positions }) => {
      const bitmap = await createImageBitmap(
        new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
          type: "image/png",
        }),
      );
      const canvas = document.createElement("canvas");
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      const ctx = canvas.getContext("2d")!;
      ctx.drawImage(bitmap, 0, 0);
      bitmap.close();
      return positions.map(({ x, y }) =>
        Array.from(ctx.getImageData(x, y, 1, 1).data).slice(0, 3),
      );
    },
    { png: png.toString("base64"), positions },
  );
}

test("PR249 对称大组两侧主对象分别回中心，保持组间距与一次撤销", async ({
  page,
}, info) => {
  const { uploads, list, canvas } = await setup(page);
  await rectangle(page, 100, 270);
  await rectangle(page, 700, 270, "#1f6feb");
  await action(page, "选择");
  await canvas.focus();
  await canvas.press("Control+a");
  await action(page, "组合");
  for (let i = 0; i < 3; i++) await action(page, "放大选区");
  for (const primary of ["0", "1"]) {
    await list.selectOption(primary);
    await expect(list.locator("option").nth(Number(primary) + 1)).toContainText(
      "画布外",
    );
    await action(page, "对象回中心");
    const png = await save(page, uploads);
    await writeFile(info.outputPath(`primary-${primary}-recovered.png`), png);
    expect(await pixels(page, png, [{ x: 450, y: 300 }])).toEqual([
      primary === "0" ? [224, 32, 32] : [31, 111, 235],
    ]);
    await expect(
      list.locator("option").nth(Number(primary) + 1),
    ).not.toContainText("画布外");
    await expect(
      list.locator("option").nth(Number(primary === "0" ? 1 : 0) + 1),
    ).toContainText("画布外");
    // 绕联合中心缩小3次还原600px成员间距；主对象的位置也验证整组平移。
    for (let i = 0; i < 3; i++) await action(page, "缩小选区");
    const compact = await save(page, uploads);
    await writeFile(info.outputPath(`primary-${primary}-compact.png`), compact);
    expect(
      await pixels(page, compact, [{ x: primary === "0" ? 736 : 164, y: 300 }]),
    ).toEqual([primary === "0" ? [224, 32, 32] : [31, 111, 235]]);
    for (let i = 0; i < 3; i++) await action(page, "撤销");
    await action(page, "撤销");
    await expect(list.locator("option").nth(1)).toContainText("画布外");
    await expect(list.locator("option").nth(2)).toContainText("画布外");
  }
  await list.selectOption("1");
  await action(page, "锁定");
  await expect(
    page.getByRole("button", { name: "对象回中心", exact: true }),
  ).toHaveCount(0);
});

test("PR249 缩放样式显示真实线宽圆角，选择档位只改当前对象且可逐次撤销", async ({
  page,
}) => {
  const { list } = await setup(page);
  await action(page, "矩形");
  await page.getByLabel("矩形圆角").selectOption("6");
  await rectangle(page, 100, 100);
  await rectangle(page, 500, 100);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "放大选区");
  const width = page.getByLabel("线条宽度");
  const radius = page.getByLabel("矩形圆角");
  await expect(width).toHaveValue("3.75");
  await expect(radius).toHaveValue("7.5");
  await width.click();
  await page.keyboard.press("Escape");
  await radius.click();
  await page.keyboard.press("Escape");
  await width.selectOption("5");
  await radius.selectOption("12");
  await list.selectOption("1");
  await expect(width).toHaveValue("3");
  await expect(radius).toHaveValue("6");
  await action(page, "撤销");
  await list.selectOption("0");
  await expect(width).toHaveValue("5");
  await expect(radius).toHaveValue("7.5");
  await action(page, "撤销");
  await list.selectOption("0");
  await expect(width).toHaveValue("3.75");
  await action(page, "撤销");
  await list.selectOption("0");
  await expect(width).toHaveValue("3");
  await expect(radius).toHaveValue("6");
});

test("PR249 缩放文字显示30px字号，查看不新增历史", async ({ page }) => {
  const { list } = await setup(page);
  await action(page, "文字");
  await page.getByLabel("标注文字").fill("F2Ab");
  await drag(page, 100, 200, 300, 200);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "放大选区");
  await expect(page.getByLabel("文字字号")).toHaveValue("30");
  await list.selectOption("");
  await list.selectOption("0");
  await action(page, "撤销");
  await list.selectOption("0");
  await expect(page.getByLabel("文字字号")).toHaveValue("24");
});

test("框选、组合、复制与键盘批量编辑一次撤销，副本样式独立", async ({
  page,
}, info) => {
  const { uploads, canvas, list } = await setup(page);
  await rectangle(page, 100, 120);
  await rectangle(page, 300, 160);
  await action(page, "选择");
  await drag(page, 60, 80, 450, 260);
  await expect(page.getByText(/已选择 2 个对象/)).toBeVisible();
  await action(page, "组合");
  await list.selectOption("");
  await click(page, 150, 150);
  await expect(
    page.getByRole("button", { name: "解组", exact: true }),
  ).toBeVisible();
  await action(page, "重复对象");
  await expect(list.locator("option")).toHaveCount(5);
  await canvas.focus();
  await canvas.press("Delete");
  await expect(list.locator("option")).toHaveCount(3);
  await canvas.press("Control+z");
  await expect(list.locator("option")).toHaveCount(5);
  await list.selectOption("2");
  await page.getByLabel("填充颜色", { exact: true }).fill("#1f6feb");
  await list.selectOption("");
  const image = await save(page, uploads);
  expect(
    await pixels(page, image, [
      { x: 110, y: 140 },
      { x: 160, y: 150 },
    ]),
  ).toEqual([
    [224, 32, 32],
    [31, 111, 235],
  ]);
  await list.selectOption("2");
  await canvas.focus();
  await canvas.press("Control+c");
  await canvas.press("Control+v");
  await expect(list.locator("option")).toHaveCount(7);
  await canvas.press("Shift+ArrowRight");
  await canvas.press("Control+z");
  await expect(list.locator("option")).toHaveCount(7);
  await canvas.press("Control+z");
  await expect(list.locator("option")).toHaveCount(5);
  await canvas.press("Control+a");
  await page.screenshot({ path: info.outputPath("object-batch-actions.png") });
});

test("修饰键多选与层级、对齐、等距保持输出顺序", async ({ page }) => {
  const { uploads, list } = await setup(page);
  await rectangle(page, 100, 120);
  await rectangle(page, 140, 140, "#1f6feb");
  await action(page, "选择");
  await click(page, 170, 160);
  await action(page, "置底");
  await list.selectOption("");
  expect(
    (await pixels(page, await save(page, uploads), [{ x: 170, y: 160 }]))[0],
  ).toEqual([224, 32, 32]);
  await click(page, 120, 150);
  await click(page, 220, 160, "Shift");
  await action(page, "对齐与等距");
  await page.getByRole("menuitem", { name: "顶对齐", exact: true }).click();
  await list.selectOption("");
  expect(
    (await pixels(page, await save(page, uploads), [{ x: 220, y: 125 }]))[0],
  ).toEqual([31, 111, 235]);
  await action(page, "撤销");
  await rectangle(page, 650, 300, "#ffffff");
  await action(page, "选择");
  await action(page, "全选对象");
  await action(page, "对齐与等距");
  await page.getByRole("menuitem", { name: /水平等距/ }).click();
  await list.selectOption("");
  expect(
    (await pixels(page, await save(page, uploads), [{ x: 425, y: 160 }]))[0],
  ).toEqual([31, 111, 235]);
});

test("透明对象画布命中、锁定保护、列表解锁与离屏恢复可撤销", async ({
  page,
}, info) => {
  const { uploads, canvas, list } = await setup(page);
  await rectangle(page, 100, 120);
  await action(page, "选择");
  await click(page, 150, 150);
  await page.getByLabel("边框不透明度", { exact: true }).fill("0");
  await page.getByLabel("填充不透明度", { exact: true }).fill("0");
  await list.selectOption("");
  await click(page, 150, 150);
  await expect(list).toHaveValue("0");
  await action(page, "锁定");
  await expect(
    page.getByRole("button", { name: "删除对象", exact: true }),
  ).toBeDisabled();
  await page.getByLabel("填充颜色", { exact: true }).fill("#1f6feb");
  await expect(page.getByLabel("填充颜色", { exact: true })).toHaveValue(
    "#e02020",
  );
  await list.selectOption("");
  await click(page, 150, 150);
  await expect(list).toHaveValue("");
  await list.selectOption("0");
  await action(page, "解锁");
  await page.getByLabel("填充不透明度", { exact: true }).fill("100");
  await canvas.focus();
  await canvas.press("Control+a");
  for (let i = 0; i < 85; i++) await canvas.press("Shift+ArrowRight");
  await expect(list.locator("option").last()).toContainText("画布外");
  await action(page, "对象回中心");
  await expect(list.locator("option").last()).not.toContainText("画布外");
  await list.selectOption("");
  expect(
    (await pixels(page, await save(page, uploads), [{ x: 450, y: 300 }]))[0],
  ).toEqual([224, 32, 32]);
  await action(page, "撤销");
  await expect(list.locator("option").last()).toContainText("画布外");
  await page.screenshot({ path: info.outputPath("object-list-offscreen.png") });
});

/** 用浏览器 Canvas 对原始 PNG 建立正交变换参考，仅比较蓝色字形，排除画布背景。 */
async function inkOverlap(
  page: Page,
  original: Buffer,
  actual: Buffer,
  rotation: number,
  flipX: boolean,
  scale: number,
  flipY = false,
  center = { x: 100, y: 66 },
) {
  return page.evaluate(
    async (args) => {
      const decode = async (png: string) =>
        createImageBitmap(
          new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
            type: "image/png",
          }),
        );
      const source = await decode(args.original),
        target = await decode(args.actual);
      const canvas = document.createElement("canvas");
      canvas.width = 300;
      canvas.height = 200;
      const ctx = canvas.getContext("2d")!;
      ctx.translate(args.center.x, args.center.y);
      ctx.scale(
        args.flipX ? -args.scale : args.scale,
        args.flipY ? -args.scale : args.scale,
      );
      ctx.rotate((args.rotation * Math.PI) / 180);
      ctx.translate(-args.center.x, -args.center.y);
      // 正交参考保留原 PNG 像素，避免第二次平滑插值改变细线颜色；颜色阈值和双向断言不变。
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(source, 0, 0);
      const expected = ctx.getImageData(0, 0, 300, 200).data;
      ctx.resetTransform();
      ctx.clearRect(0, 0, 300, 200);
      ctx.drawImage(target, 0, 0);
      const actual = ctx.getImageData(0, 0, 300, 200).data;
      const blue = (data: Uint8ClampedArray, i: number) =>
        data[i]! < 90 &&
        data[i + 1]! > 60 &&
        data[i + 1]! < 150 &&
        data[i + 2]! > 180;
      // 向量字形旋转后会重新栅格化；允许 1px 抗锯齿差异，双向比对防漏字/多画。
      const compare = (from: Uint8ClampedArray, to: Uint8ClampedArray) => {
        let count = 0,
          matched = 0;
        for (let i = 0; i < from.length; i += 4) {
          if (!blue(from, i)) continue;
          count++;
          const x = (i / 4) % 300,
            y = Math.floor(i / 4 / 300);
          let hit = false;
          for (let dy = -1; dy <= 1; dy++)
            for (let dx = -1; dx <= 1; dx++) {
              if (
                x + dx >= 0 &&
                x + dx < 300 &&
                y + dy >= 0 &&
                y + dy < 200 &&
                blue(to, ((y + dy) * 300 + x + dx) * 4)
              )
                hit = true;
            }
          if (hit) matched++;
        }
        return { count, matched, ratio: matched / count };
      };
      const sourceSize = { width: source.width, height: source.height },
        targetSize = { width: target.width, height: target.height };
      const forward = compare(expected, actual),
        backward = compare(actual, expected);
      source.close();
      target.close();
      const bounds = (data: Uint8ClampedArray) => {
        let left = 300,
          top = 200,
          right = 0,
          bottom = 0,
          count = 0;
        for (let i = 0; i < data.length; i += 4)
          if (blue(data, i)) {
            const x = (i / 4) % 300,
              y = Math.floor(i / 4 / 300);
            left = Math.min(left, x);
            top = Math.min(top, y);
            right = Math.max(right, x);
            bottom = Math.max(bottom, y);
            count++;
          }
        return { left, top, right, bottom, count };
      };
      return {
        sourceSize,
        targetSize,
        forward,
        backward,
        overlap: Math.min(forward.ratio, backward.ratio),
        union: forward.count + backward.count,
        expected: bounds(expected),
        actual: bounds(actual),
      };
    },
    {
      original: original.toString("base64"),
      actual: actual.toString("base64"),
      rotation,
      flipX,
      scale,
      flipY,
      center,
    },
  );
}

test("文字自身旋转镜像与比例缩放在非1:1 PNG中只变换一次，命中同步", async ({
  page,
}, info) => {
  const { uploads, list } = await setup(page, { width: 300, height: 200 });
  await action(page, "文字");
  await page.getByLabel("标注文字").fill("F2Ab");
  // 自然字号16px、整数基点；非对称ASCII避免小字hinting及箭头fallback影响位图参考。
  await page.getByLabel("文字字号").selectOption("48");
  await page.getByLabel("文字字体").fill("Arial");
  await page.getByLabel("文字字体").press("Tab");
  await expect(
    page.getByText("已使用字体“Arial”。", { exact: true }),
  ).toBeVisible();
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 180, 216, 420, 216);
  await action(page, "选择");
  await list.selectOption("");
  const original = await save(page, uploads);
  await list.selectOption("0");
  await action(page, "对象旋转 90°");
  await list.selectOption("");
  const rotated = await save(page, uploads);
  const rotatedResult = await inkOverlap(page, original, rotated, 90, false, 1);
  await Promise.all([
    writeFile(info.outputPath("text-original.png"), original),
    writeFile(info.outputPath("text-rotated.png"), rotated),
    writeFile(
      info.outputPath("text-transform-pixels.json"),
      JSON.stringify({ rotated: rotatedResult }, null, 2),
    ),
  ]);
  expect(rotatedResult.overlap).toBeGreaterThan(0.95);
  await click(page, 300, 150);
  await expect(list).toHaveValue("0");
  await action(page, "水平镜像");
  await list.selectOption("");
  const mirrored = await save(page, uploads);
  const mirroredResult = await inkOverlap(
    page,
    original,
    mirrored,
    90,
    true,
    1,
  );
  await Promise.all([
    writeFile(info.outputPath("text-mirrored.png"), mirrored),
    writeFile(
      info.outputPath("text-transform-pixels.json"),
      JSON.stringify(
        { rotated: rotatedResult, mirrored: mirroredResult },
        null,
        2,
      ),
    ),
  ]);
  expect(mirroredResult.overlap).toBeGreaterThan(0.95);
  await list.selectOption("0");
  await action(page, "放大选区");
  await list.selectOption("");
  const scaled = await save(page, uploads);
  const result = await inkOverlap(page, original, scaled, 90, true, 1.25);
  await Promise.all([
    writeFile(info.outputPath("text-scaled.png"), scaled),
    writeFile(
      info.outputPath("text-transform-pixels.json"),
      JSON.stringify(
        { rotated: rotatedResult, mirrored: mirroredResult, scaled: result },
        null,
        2,
      ),
    ),
  ]);
  expect(result.union).toBeGreaterThan(60);
  expect(result.overlap).toBeGreaterThan(0.8);
  for (const comparison of [rotatedResult, mirroredResult, result]) {
    expect(comparison.sourceSize).toEqual({ width: 300, height: 200 });
    expect(comparison.targetSize).toEqual({ width: 300, height: 200 });
    for (const edge of ["left", "top", "right", "bottom"] as const)
      expect(
        Math.abs(comparison.actual[edge] - comparison.expected[edge]),
      ).toBeLessThanOrEqual(1);
  }
  await list.selectOption("0");
  await page.screenshot({ path: info.outputPath("text-object-transform.png") });
});

test("文字输入和IME保留原生快捷键，不触发对象命令", async ({ page }) => {
  const { canvas, list } = await setup(page);
  await action(page, "文字");
  await page.getByLabel("标注文字").fill("保留文字");
  await drag(page, 180, 210, 400, 210);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "编辑文字");
  const input = page.getByLabel("标注文字");
  await input.press("Control+a");
  await input.press("Delete");
  await input.fill("中文草稿");
  await input.dispatchEvent("compositionstart", { data: "中文" });
  await input.press("Control+d");
  await input.press("Escape");
  await expect(
    page.getByRole("button", { name: "完成文字编辑", exact: true }),
  ).toBeVisible();
  await input.dispatchEvent("compositionend", { data: "中文" });
  await action(page, "完成文字编辑");
  await expect(list.locator("option")).toHaveCount(2);
  await canvas.focus();
  await canvas.press("Control+d");
  await expect(list.locator("option")).toHaveCount(3);
});

test("模糊按层级采样前序画面，重排后撤销恢复原采样", async ({ page }) => {
  const { uploads, list } = await setup(page);
  await rectangle(page, 100, 120);
  await action(page, "模糊");
  await drag(page, 80, 100, 240, 210);
  await action(page, "选择");
  await list.selectOption("");
  const sample = [{ x: 195, y: 150 }];
  const before = (await pixels(page, await save(page, uploads), sample))[0]!;
  expect(before).not.toEqual([224, 32, 32]);
  await list.selectOption("0");
  await action(page, "置顶");
  await list.selectOption("");
  expect((await pixels(page, await save(page, uploads), sample))[0]).toEqual([
    224, 32, 32,
  ]);
  await action(page, "撤销");
  expect((await pixels(page, await save(page, uploads), sample))[0]).toEqual(
    before,
  );
});

test("说明自身旋转及垂直镜像的字形、背景和引线共同写入自然PNG", async ({
  page,
}, info) => {
  const { uploads, list } = await setup(page, { width: 300, height: 200 });
  await action(page, "说明");
  await page.getByLabel("标注文字").fill("F→AB");
  await page.getByLabel("文字字号").selectOption("32");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  const measured = await page.evaluate(() => {
    const context = document.createElement("canvas").getContext("2d")!;
    context.font = "400 32px system-ui, sans-serif";
    return context.measureText("F→AB").width;
  });
  // butt引线端点只沿法线外扩1.5；独立求tip使可见AABB中心仍为(240,162)。
  let tip = { x: 174 - measured, y: 100 };
  for (let i = 0; i < 8; i++) {
    const dx = tip.x - (300 + measured / 2),
      dy = tip.y - 198;
    const normalScale = 1.5 / Math.hypot(dx, dy);
    tip = {
      x: 172.5 - measured + Math.abs(dy) * normalScale,
      y: 98.5 + Math.abs(dx) * normalScale,
    };
  }
  await drag(page, tip.x, tip.y, 300, 210);
  await action(page, "选择");
  await list.selectOption("");
  const original = await save(page, uploads);
  const center = { x: 80, y: 54 };
  await list.selectOption("0");
  await action(page, "对象旋转 90°");
  await action(page, "垂直镜像");
  await list.selectOption("");
  const actual = await save(page, uploads);
  const result = await inkOverlap(
    page,
    original,
    actual,
    90,
    false,
    1,
    true,
    center,
  );
  await writeFile(info.outputPath("callout-original.png"), original);
  await writeFile(info.outputPath("callout-transformed.png"), actual);
  await writeFile(
    info.outputPath("callout-transform-pixels.json"),
    JSON.stringify(result, null, 2),
  );
  expect(result.overlap).toBeGreaterThan(0.93);
  expect(result.union).toBeGreaterThan(100);
});

test("锁定文字禁止直接输入与全局清除，解锁后可继续编辑", async ({ page }) => {
  const { list } = await setup(page);
  await action(page, "文字");
  await page.getByLabel("标注文字").fill("保留");
  await drag(page, 180, 210, 400, 210);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "锁定");
  await expect(page.getByLabel("标注文字")).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "清除编辑", exact: true }),
  ).toBeDisabled();
  await expect(list.locator("option")).toHaveCount(2);
  await action(page, "解锁");
  await page.getByLabel("标注文字").fill("继续");
  await action(page, "完成文字编辑");
  await expect(page.getByLabel("标注文字")).toHaveValue("继续");
});

test("OBJ-01 重复屏蔽达到64后按钮与Ctrl+D整次拒绝且不增加历史", async ({
  page,
}) => {
  const { canvas, list } = await setup(page);
  await action(page, "屏蔽");
  await drag(page, 100, 100, 160, 150);
  await action(page, "选择");
  for (let count = 2; count <= 64; count *= 2) {
    await action(page, "全选对象");
    await action(page, "重复对象");
    await expect(list.locator("option")).toHaveCount(count + 1);
  }
  await list.selectOption("0");
  await action(page, "重复对象");
  await expect(
    page.getByText("最多保留 64 个屏蔽区域；本次复制未修改对象。"),
  ).toBeVisible();
  await expect(list.locator("option")).toHaveCount(65);
  await canvas.focus();
  await canvas.press("Control+d");
  await expect(list.locator("option")).toHaveCount(65);
  await canvas.press("Control+z");
  await expect(list.locator("option")).toHaveCount(33);
});

for (const tool of ["画笔", "荧光笔"]) {
  test(
    "OBJ-02 " + tool + "轨迹中段可选而首尾空白弦线不命中",
    async ({ page }) => {
      const { list } = await setup(page);
      await action(page, tool);
      const from = await at(page, 100, 100);
      await page.mouse.move(from.x, from.y);
      await page.mouse.down();
      const middle = await at(page, 200, 250);
      await page.mouse.move(middle.x, middle.y);
      const end = await at(page, 300, 100);
      await page.mouse.move(end.x, end.y);
      await page.mouse.up();
      await action(page, "选择");
      await click(page, 200, 250);
      await expect(list).toHaveValue("0");
      await list.selectOption("");
      await click(page, 200, 100);
      await expect(list).toHaveValue("");
    },
  );
}

test("OBJ-04 镜像说明再整图旋转后PNG引线继续指向原图目标", async ({
  page,
}, info) => {
  const { uploads, list } = await setup(page, { width: 300, height: 200 });
  await action(page, "说明");
  await page.getByLabel("标注文字").fill("F→AB");
  await page.getByLabel("文字字号").selectOption("32");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 100, 100, 300, 210);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "水平镜像");
  const worldTargetX = await page.evaluate(() => {
    const context = document.createElement("canvas").getContext("2d")!;
    context.font = "400 32px system-ui, sans-serif";
    return 306 + context.measureText("F→AB").width;
  });
  await action(page, "旋转 90°");
  await list.selectOption("");
  const png = await save(page, uploads);
  const expected = { x: (600 - 100) / 3, y: worldTargetX / 3 };
  const positions = [];
  for (let dy = -2; dy <= 2; dy++)
    for (let dx = -2; dx <= 2; dx++)
      positions.push({
        x: Math.round(expected.x) + dx,
        y: Math.round(expected.y) + dy,
      });
  const colors = await pixels(page, png, positions);
  expect(
    colors.filter(
      ([red, green, blue]) =>
        red! < 90 && green! > 60 && green! < 150 && blue! > 180,
    ).length,
  ).toBeGreaterThan(1);
  await writeFile(info.outputPath("mirrored-callout-image-rotation.png"), png);
});

test("PR249 不透明度逐字输入及Enter仅一条历史，直接画布绘制使用失焦值", async ({
  page,
}) => {
  const { uploads, list } = await setup(page);
  await rectangle(page, 100, 120);
  await action(page, "选择");
  await list.selectOption("0");
  for (const label of ["边框不透明度", "填充不透明度"]) {
    const input = page.getByLabel(label, { exact: true });
    await input.fill("");
    await input.pressSequentially("37");
    await input.press("Enter");
    await action(page, "撤销");
    await list.selectOption("0");
    await expect(input).toHaveValue("100");
  }
  await action(page, "矩形");
  await page.getByLabel("填充不透明度", { exact: true }).fill("37");
  await drag(page, 500, 300, 600, 360);
  await action(page, "选择");
  await list.selectOption("");
  expect(
    (await pixels(page, await save(page, uploads), [{ x: 550, y: 330 }]))[0],
  ).toEqual([94, 113, 62]);
});

test("PR249 宽荧光笔及缩放后画笔的可见边缘可命中，外侧不误选", async ({
  page,
}) => {
  const { list } = await setup(page);
  for (const tool of ["荧光笔", "画笔"]) {
    await action(page, tool);
    await page.getByLabel("线条宽度").selectOption("8");
    await drag(page, 100, 200, 500, 200);
    await action(page, "选择");
    const index = "0";
    if (tool === "画笔") {
      await list.selectOption(index);
      for (let n = 0; n < 6; n++) await action(page, "放大选区");
      await list.selectOption("");
    }
    await click(page, 300, 214);
    await expect(list).toHaveValue(index);
    if (tool === "荧光笔") {
      await action(page, "放大选区");
      await list.selectOption("");
      await click(page, 300, 219);
      await expect(list).toHaveValue(index);
      await list.selectOption("");
      await click(page, 300, 222);
      await expect(list).toHaveValue("");
      await action(page, "全选对象");
      await action(page, "删除对象");
    }
  }
});

test("PR249 重复锁定不追加空历史，一次撤销恢复解锁", async ({ page }) => {
  const { list } = await setup(page);
  await rectangle(page, 100, 120);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "锁定");
  await action(page, "锁定");
  await action(page, "撤销");
  await expect(list.locator("option").last()).not.toContainText("已锁定");
});

test("PR249 第二轮菜单按组逻辑单元启用且锁定一致", async ({ page }) => {
  const { canvas, list } = await setup(page);
  await rectangle(page, 100, 120);
  await rectangle(page, 300, 120);
  await action(page, "选择");
  await canvas.focus();
  await canvas.press("Control+a");
  await action(page, "组合");
  await expect(page.getByRole("button", { name: "对齐与等距" })).toHaveCount(0);
  await rectangle(page, 500, 140);
  await action(page, "选择");
  await canvas.focus();
  await canvas.press("Control+a");
  await action(page, "对齐与等距");
  await expect(page.getByRole("menuitem", { name: /水平等距/ })).toBeDisabled();
  await page.keyboard.press("Escape");
  await rectangle(page, 700, 180);
  await action(page, "选择");
  await canvas.focus();
  await canvas.press("Control+a");
  await action(page, "对齐与等距");
  await expect(page.getByRole("menuitem", { name: /水平等距/ })).toBeEnabled();
  await page.keyboard.press("Escape");
  await action(page, "锁定");
  await expect(page.getByRole("button", { name: "对齐与等距" })).toBeDisabled();
  await list.selectOption("0");
  await action(page, "解锁");
  await expect(page.getByRole("button", { name: "对齐与等距" })).toHaveCount(0);
});

test("PR249 第二轮绘制工具对象快捷键不丢折线草稿，合法撤销重做仍可用", async ({
  page,
}) => {
  const { canvas, list } = await setup(page);
  await rectangle(page, 100, 120);
  await action(page, "选择");
  await list.selectOption("0");
  await canvas.focus();
  await canvas.press("Control+c");
  await action(page, "折线");
  await click(page, 500, 300);
  await click(page, 600, 350);
  for (const key of [
    "Control+d",
    "Control+v",
    "ArrowRight",
    "Delete",
    "Control+a",
    "Control+c",
  ])
    await canvas.press(key);
  await canvas.press("Enter");
  await action(page, "选择");
  await expect(list.locator("option")).toHaveCount(3);
  await canvas.focus();
  await canvas.press("Control+z");
  await expect(list.locator("option")).toHaveCount(2);
  await canvas.press("Control+y");
  await expect(list.locator("option")).toHaveCount(3);
});

test("PR249 第二轮普通文字对象旋转镜像后整图旋转保留世界锚点与真实PNG命中", async ({
  page,
}, info) => {
  const { uploads, list } = await setup(page, { width: 300, height: 200 });
  await action(page, "文字");
  await page.getByLabel("标注文字").fill("F2Ab");
  await page.getByLabel("文字字号").selectOption("48");
  await page.getByLabel("文字字体").fill("Arial");
  await page.getByLabel("文字字体").press("Tab");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 180, 216, 420, 216);
  await action(page, "选择");
  await list.selectOption("0");
  await action(page, "对象旋转 90°");
  await action(page, "水平镜像");
  await list.selectOption("");
  const before = await save(page, uploads);
  await action(page, "旋转 90°");
  const after = await save(page, uploads);
  const readBounds = (png: Buffer) =>
    page.evaluate(async (png) => {
      const image = await createImageBitmap(
        new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
          type: "image/png",
        }),
      );
      const canvas = document.createElement("canvas");
      canvas.width = image.width;
      canvas.height = image.height;
      const ctx = canvas.getContext("2d")!;
      ctx.drawImage(image, 0, 0);
      image.close();
      const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      let left = canvas.width,
        top = canvas.height,
        right = 0,
        bottom = 0,
        count = 0;
      for (let i = 0; i < data.length; i += 4)
        if (
          data[i]! < 90 &&
          data[i + 1]! > 60 &&
          data[i + 1]! < 150 &&
          data[i + 2]! > 180
        ) {
          const x = (i / 4) % canvas.width,
            y = Math.floor(i / 4 / canvas.width);
          left = Math.min(left, x);
          top = Math.min(top, y);
          right = Math.max(right, x);
          bottom = Math.max(bottom, y);
          count++;
        }
      return {
        width: canvas.width,
        height: canvas.height,
        left,
        top,
        right,
        bottom,
        count,
      };
    }, png.toString("base64"));
  const original = await readBounds(before),
    actual = await readBounds(after);
  // 对象变换后锚点(318,78)，自然(106,26)；整图90°映射到(174,106)。
  // 字形方向保持，PNG导出比例由1/3变1/2，局部距离只乘1.5。
  const expected = {
    left: 174 + (original.left - 106) * 1.5,
    right: 174 + (original.right - 106) * 1.5,
    top: 106 + (original.top - 26) * 1.5,
    bottom: 106 + (original.bottom - 26) * 1.5,
  };
  await Promise.all([
    writeFile(info.outputPath("text-world-before.png"), before),
    writeFile(info.outputPath("text-world-after.png"), after),
    writeFile(
      info.outputPath("text-world-bounds.json"),
      JSON.stringify({ original, actual, expected }, null, 2),
    ),
  ]);
  expect(actual.width).toBe(200);
  expect(actual.height).toBe(300);
  expect(original.count).toBeGreaterThan(50);
  expect(actual.count).toBeGreaterThan(50);
  for (const edge of ["left", "right", "top", "bottom"] as const)
    expect(Math.abs(actual[edge] - expected[edge])).toBeLessThanOrEqual(2);
  await click(
    page,
    450 + ((expected.left + expected.right) / 2 - 100) * 2,
    300 + ((expected.top + expected.bottom) / 2 - 150) * 2,
  );
  await expect(list).toHaveValue("0");
});

test("PR249 第二轮重复组合不增加撤销步骤，重选后检查原组已解体", async ({
  page,
}) => {
  const { canvas, list } = await setup(page);
  await rectangle(page, 100, 120);
  await rectangle(page, 300, 120);
  await action(page, "选择");
  await canvas.focus();
  await canvas.press("Control+a");
  await action(page, "组合");
  await action(page, "组合");
  await action(page, "撤销");
  await list.selectOption("0");
  await expect(
    page.getByRole("button", { name: "解组", exact: true }),
  ).toHaveCount(0);
});

for (const arrowStyle of ["open", "hollow", "solid"]) {
  test(
    "PR249 第三轮 " + arrowStyle + "箭头缩放四向端头真实PNG可见像素可点击",
    async ({ page }, info) => {
      const { uploads, list } = await setup(page);
      await action(page, "箭头");
      await page.getByLabel("箭头样式").selectOption(arrowStyle);
      await page.getByLabel("双向箭头").check();
      await page.getByLabel("自定义颜色").fill("#1f6feb");
      await drag(page, 400, 300, 440, 300);
      await action(page, "选择");
      await list.selectOption("0");
      for (let step = 0; step < 6; step++) await action(page, "放大选区");
      for (let turn = 0; turn < 4; turn++) {
        await list.selectOption("");
        const png = await save(page, uploads);
        const wing = await page.evaluate(
          async ({ png, vertical }) => {
            const image = await createImageBitmap(
              new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
                type: "image/png",
              }),
            );
            const canvas = document.createElement("canvas");
            canvas.width = image.width;
            canvas.height = image.height;
            const ctx = canvas.getContext("2d")!;
            ctx.drawImage(image, 0, 0);
            image.close();
            const data = ctx.getImageData(
              0,
              0,
              canvas.width,
              canvas.height,
            ).data;
            let result = { x: 0, y: 0, distance: 0 };
            let tip = { x: 0, y: 0, distance: 0 };
            for (let i = 0; i < data.length; i += 4)
              if (
                data[i]! < 90 &&
                data[i + 1]! > 60 &&
                data[i + 1]! < 150 &&
                data[i + 2]! > 180
              ) {
                const x = (i / 4) % canvas.width,
                  y = Math.floor(i / 4 / canvas.width),
                  distance = Math.abs(vertical ? x - 420 : y - 300);
                if (distance > result.distance) result = { x, y, distance };
                const along = Math.abs(vertical ? y - 300 : x - 420);
                if (along > tip.distance) tip = { x, y, distance: along };
              }
            return { ...result, tip };
          },
          { png: png.toString("base64"), vertical: turn % 2 === 1 },
        );
        await Promise.all([
          writeFile(info.outputPath("arrow-" + turn + ".png"), png),
          writeFile(
            info.outputPath("wing-" + turn + ".json"),
            JSON.stringify(wing),
          ),
        ]);
        expect(wing.distance).toBeGreaterThan(20);
        await click(page, wing.x + 0.5, wing.y + 0.5);
        await expect(list).toHaveValue("0");
        if (arrowStyle !== "open") {
          // 三角miter尖角也有真实像素超出端点10px，不能只计算半线宽外扩。
          expect(wing.tip.distance - 20 * 1.25 ** 6).toBeGreaterThan(10);
          await list.selectOption("");
          await click(page, wing.tip.x + 0.5, wing.tip.y + 0.5);
          await expect(list).toHaveValue("0");
        }
        await action(page, "对象旋转 90°");
      }
    },
  );
}

test("PR249 第三轮粗荧光笔仅露出4px仍可见且真实PNG可命中", async ({
  page,
}, info) => {
  const { uploads, list, canvas } = await setup(page);
  await action(page, "荧光笔");
  await page.getByLabel("线条宽度").selectOption("8");
  await drag(page, 100, 48, 300, 48);
  await action(page, "选择");
  await list.selectOption("0");
  await canvas.focus();
  for (let i = 0; i < 6; i++) await canvas.press("Shift+ArrowUp");
  await list.selectOption("");
  const png = await save(page, uploads);
  await writeFile(info.outputPath("partial-highlighter.png"), png);
  const sample = await pixels(page, png, [
    { x: 200, y: 1 },
    { x: 200, y: 10 },
  ]);
  expect(sample[0]).not.toEqual([18, 161, 80]);
  expect(sample[1]).toEqual([18, 161, 80]);
  await expect(list.locator("option").last()).not.toContainText("画布外");
  await click(page, 200, 1);
  await expect(list).toHaveValue("0");
});

test("PR249 第三轮混合多选主对象在画内不显示回中心，离屏组恢复保留间距可撤销", async ({
  page,
}, info) => {
  const { uploads, list, canvas } = await setup(page);
  await rectangle(page, 100, 120);
  await action(page, "选择");
  await list.selectOption("0");
  await canvas.focus();
  for (let i = 0; i < 30; i++) await canvas.press("Shift+ArrowLeft");
  await rectangle(page, 500, 300, "#1f6feb");
  await action(page, "选择");
  await canvas.focus();
  await canvas.press("Control+a");
  await expect(list).toHaveValue("1");
  await expect(
    page.getByRole("button", { name: "对象回中心", exact: true }),
  ).toHaveCount(0);
  // 列表明确选择实际离屏对象后才提供恢复；锁定仍阻止动作。
  await list.selectOption("0");
  await action(page, "锁定");
  await expect(
    page.getByRole("button", { name: "对象回中心", exact: true }),
  ).toHaveCount(0);
  await action(page, "解锁");
  await action(page, "对象回中心");
  await canvas.focus();
  await canvas.press("Control+a");
  await action(page, "组合");
  await canvas.focus();
  for (let i = 0; i < 65; i++) await canvas.press("Shift+ArrowLeft");
  await expect(list.locator("option").nth(1)).toContainText("画布外");
  await action(page, "对象回中心");
  const png = await save(page, uploads);
  await writeFile(info.outputPath("recovered-group.png"), png);
  // 组原间距100x30，蓝色主对象恢复到450,300；两成员的相对位置不变。
  expect(
    await pixels(page, png, [
      { x: 350, y: 270 },
      { x: 450, y: 300 },
    ]),
  ).toEqual([
    [224, 32, 32],
    [31, 111, 235],
  ]);
  await action(page, "撤销");
  await expect(list.locator("option").nth(1)).toContainText("画布外");
});

test("GEO-01 平端帽开放箭头完全不可见后仍可从列表回中心", async ({
  page,
}, info) => {
  const { uploads, list, canvas } = await setup(page);
  await action(page, "箭头");
  await page.getByLabel("线条宽度").selectOption("8");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 400, 300, 440, 300);
  await action(page, "选择");
  await list.selectOption("");
  await writeFile(
    info.outputPath("butt-arrow-original.png"),
    await save(page, uploads),
  );
  await list.selectOption("0");
  for (let i = 0; i < 6; i++) await action(page, "放大选区");
  await canvas.focus();
  for (let i = 0; i < 51; i++) await canvas.press("Shift+ArrowLeft");
  await list.selectOption("");
  const png = await save(page, uploads);
  await writeFile(info.outputPath("butt-arrow-offscreen.png"), png);
  const painted = await page.evaluate(async (png) => {
    const image = await createImageBitmap(
      new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
        type: "image/png",
      }),
    );
    const canvas = document.createElement("canvas");
    canvas.width = image.width;
    canvas.height = image.height;
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(image, 0, 0);
    image.close();
    const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    let count = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] !== 18 || data[i + 1] !== 161 || data[i + 2] !== 80) count++;
    return count;
  }, png.toString("base64"));
  expect(painted).toBe(0);
  await expect(list.locator("option").last()).toContainText("画布外");
  await list.selectOption("0");
  await action(page, "对象回中心");
  await expect(list.locator("option").last()).not.toContainText("画布外");
  await action(page, "撤销");
  await expect(list.locator("option").last()).toContainText("画布外");
});

test("GEO-02 虚线折线短末段完全离屏后可恢复且一次撤销", async ({
  page,
}, info) => {
  const { uploads, list, canvas } = await setup(page);
  await action(page, "折线");
  await page.getByLabel("线条宽度").selectOption("8");
  await page.getByLabel("线型").selectOption("dashed");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await click(page, 400, 300);
  await click(page, 440, 300);
  await click(page, 443.25, 300);
  await canvas.press("Enter");
  await action(page, "选择");
  await list.selectOption("");
  await writeFile(
    info.outputPath("round-join-original.png"),
    await save(page, uploads),
  );
  await list.selectOption("0");
  for (let i = 0; i < 6; i++) await action(page, "放大选区");
  await canvas.focus();
  for (let i = 0; i < 50; i++) await canvas.press("Shift+ArrowLeft");
  for (let i = 0; i < 5; i++) await canvas.press("ArrowLeft");
  await list.selectOption("");
  const png = await save(page, uploads);
  await writeFile(info.outputPath("round-join-offscreen.png"), png);
  const painted = await page.evaluate(async (png) => {
    const image = await createImageBitmap(
      new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], {
        type: "image/png",
      }),
    );
    const canvas = document.createElement("canvas");
    canvas.width = image.width;
    canvas.height = image.height;
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(image, 0, 0);
    image.close();
    const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    let count = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] !== 18 || data[i + 1] !== 161 || data[i + 2] !== 80) count++;
    return count;
  }, png.toString("base64"));
  expect(painted).toBe(0);
  await expect(list.locator("option").last()).toContainText("画布外");
  await list.selectOption("0");
  await action(page, "对象回中心");
  await expect(list.locator("option").last()).not.toContainText("画布外");
  await action(page, "撤销");
  await expect(list.locator("option").last()).toContainText("画布外");
});
