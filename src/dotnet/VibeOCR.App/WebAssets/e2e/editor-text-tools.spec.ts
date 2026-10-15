import { writeFile } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

async function setupTextHost(
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

async function client(page: Page, x: number, y: number) {
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
  toX: number,
  toY: number,
) {
  const a = await client(page, x, y);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  const b = await client(page, toX, toY);
  await page.mouse.move(b.x, b.y);
  await page.mouse.up();
}
async function doubleClick(page: Page, x: number, y: number) {
  const at = await client(page, x, y);
  await page.mouse.dblclick(at.x, at.y);
}
async function scan(
  page: Page,
  region: { x: number; y: number; width: number; height: number },
  png?: Buffer,
) {
  return page.getByLabel("图片检查画布").evaluate(
    async (element: HTMLCanvasElement, args) => {
      let canvas = element;
      if (args.png) {
        const bytes = Uint8Array.from(atob(args.png), (v) => v.charCodeAt(0));
        const image = await createImageBitmap(
          new Blob([bytes], { type: "image/png" }),
        );
        canvas = document.createElement("canvas");
        canvas.width = image.width;
        canvas.height = image.height;
        canvas.getContext("2d")!.drawImage(image, 0, 0);
        image.close();
      }
      const sx = args.png ? 1 : canvas.width / 900,
        sy = args.png ? 1 : canvas.height / 600;
      const r = args.region,
        data = canvas
          .getContext("2d")!
          .getImageData(
            Math.round(r.x * sx),
            Math.round(r.y * sy),
            Math.max(1, Math.round(r.width * sx)),
            Math.max(1, Math.round(r.height * sy)),
          ).data;
      let blue = 0,
        white = 0,
        black = 0,
        green = 0;
      for (let i = 0; i < data.length; i += 4) {
        const red = data[i]!,
          g = data[i + 1]!,
          b = data[i + 2]!;
        if (
          Math.abs(red - 31) < 30 &&
          Math.abs(g - 111) < 30 &&
          Math.abs(b - 235) < 30
        )
          blue++;
        if (red > 240 && g > 240 && b > 240) white++;
        if (red < 65 && g < 65 && b < 65) black++;
        if (
          Math.abs(red - 18) < 15 &&
          Math.abs(g - 161) < 15 &&
          Math.abs(b - 80) < 15
        )
          green++;
      }
      return {
        blue,
        white,
        black,
        green,
        width: canvas.width,
        height: canvas.height,
      };
    },
    { region, png: png?.toString("base64") },
  );
}
async function save(page: Page, uploads: { body: Buffer }[]) {
  const count = uploads.length;
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(count + 1);
  return uploads[count]!.body;
}
async function deselect(page: Page) {
  await page.getByLabel("选择标注对象").selectOption("");
}

test("文字双击编辑、IME、字体回退和多行样式在预览与导出中一致", async ({
  page,
}, testInfo) => {
  const { uploads } = await setupTextHost(page);
  await page.getByRole("button", { name: "文字", exact: true }).click();
  await page.getByLabel("标注文字").fill("旧文字");
  await drag(page, 150, 180, 550, 180);
  await doubleClick(page, 190, 172);
  const input = page.getByLabel("标注文字");
  await input.fill("中文第一行\nNext line");
  await input.dispatchEvent("compositionstart", { data: "中文" });
  await input.press("Escape");
  await input.press("Control+Enter");
  await expect(
    page.getByRole("button", { name: "完成文字编辑", exact: true }),
  ).toBeVisible();
  await input.dispatchEvent("compositionend", { data: "中文" });
  const font = page.getByLabel("文字字体");
  await font.fill("Unavailable_Font_230");
  await font.press("Tab");
  await expect(
    page.getByText("字体“Unavailable_Font_230”不可用，已回退到系统字体。"),
  ).toBeVisible();
  await expect(font).toHaveValue("system-ui");
  await font.fill("monospace");
  await font.press("Tab");
  await page.getByLabel("文字字号").selectOption("32");
  for (const name of ["粗体", "斜体", "下划线", "文字背景", "文字描边"])
    await page.getByLabel(name, { exact: true }).check();
  await page.getByLabel("文字对齐").selectOption("center");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await expect(
    page.getByRole("button", { name: "保存标注图", exact: true }),
  ).toBeDisabled();
  await page.screenshot({
    path: testInfo.outputPath("text-edit-controls.png"),
  });
  await page.getByRole("button", { name: "完成文字编辑", exact: true }).click();
  await page.getByRole("button", { name: "编辑文字", exact: true }).click();
  await input.fill("取消这份草稿");
  await page.getByLabel("文字字号").selectOption("48");
  await input.press("Escape");
  await expect(input).toHaveValue("中文第一行\nNext line");
  await expect(page.getByLabel("文字字号")).toHaveValue("32");
  await deselect(page);
  const area = { x: 140, y: 140, width: 425, height: 100 };
  await expect
    .poll(async () => {
      const p = await scan(page, area);
      return p.blue > 100 && p.white > 500 && p.black > 20;
    })
    .toBe(true);
  const exported = await scan(page, area, await save(page, uploads));
  expect(exported.width).toBe(900);
  expect(exported.height).toBe(600);
  expect(exported.blue).toBeGreaterThan(100);
  expect(exported.white).toBeGreaterThan(500);
  expect(exported.black).toBeGreaterThan(20);
  expect(
    (await scan(page, { x: 145, y: 200, width: 10, height: 10 })).white,
  ).toBeGreaterThan(20);
  await page.screenshot({ path: testInfo.outputPath("text-properties.png") });
});

test("字母序号形状与重新编号、说明内容及指向可编辑并写入导出", async ({
  page,
}, testInfo) => {
  const { uploads } = await setupTextHost(page);
  await page.getByRole("button", { name: "序号", exact: true }).click();
  const start = page.getByLabel("序号起点");
  await start.fill("26");
  await start.press("Tab");
  await page.getByLabel("序号格式").selectOption("upper");
  await page.getByLabel("序号形状").selectOption("square");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 200, 180, 200, 180);
  await drag(page, 300, 180, 300, 180);
  await start.fill("5");
  await start.press("Tab");
  await page.getByRole("button", { name: "重新编号", exact: true }).click();
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("0");
  await expect(page.getByLabel("序号值")).toHaveValue("5");
  await page.getByLabel("序号值").fill("27");
  await page.getByLabel("序号值").press("Tab");
  await page.getByLabel("选择标注对象").selectOption("1");
  await expect(page.getByLabel("序号值")).toHaveValue("6");
  await page.getByRole("button", { name: "说明", exact: true }).click();
  await page.getByLabel("标注文字").fill("可编辑说明");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 150, 380, 400, 340);
  await page.getByLabel("说明样式").selectOption("arrow");
  await drag(page, 152, 381, 202, 401);
  await page.getByRole("button", { name: "编辑文字", exact: true }).click();
  await page.getByLabel("标注文字").fill("更新说明\n第二行");
  await page.getByRole("button", { name: "完成文字编辑", exact: true }).click();
  await deselect(page);
  await expect
    .poll(
      async () =>
        (await scan(page, { x: 195, y: 395, width: 12, height: 12 })).blue,
    )
    .toBeGreaterThan(0);
  const png = await save(page, uploads);
  expect(
    (await scan(page, { x: 195, y: 395, width: 12, height: 12 }, png)).blue,
  ).toBeGreaterThan(0);
  expect(
    (await scan(page, { x: 146, y: 376, width: 8, height: 8 }, png)).green,
  ).toBe(64);
  expect(
    (await scan(page, { x: 213, y: 192, width: 2, height: 2 }, png)).blue,
  ).toBeGreaterThan(0);
  expect(
    (await scan(page, { x: 395, y: 310, width: 160, height: 70 }, png)).blue,
  ).toBeGreaterThan(50);
  await page.screenshot({ path: testInfo.outputPath("numbering-callout.png") });
});

test("裁剪旋转后的文字可命中续编，取消保持导出和历史", async ({ page }) => {
  const { uploads } = await setupTextHost(page);
  await page.getByRole("button", { name: "文字", exact: true }).click();
  await page.getByLabel("标注文字").fill("定位文字");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 200, 200, 400, 200);
  await page.getByRole("button", { name: "裁剪", exact: true }).click();
  await drag(page, 50, 50, 850, 550);
  await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await doubleClick(page, 540, 260);
  await expect(page.getByLabel("标注文字")).toHaveValue("定位文字");
  await page.getByLabel("标注文字").fill("放弃修改");
  await page.getByRole("button", { name: "取消文字编辑", exact: true }).click();
  await expect(page.getByLabel("标注文字")).toHaveValue("定位文字");
  await deselect(page);
  const pixels = await scan(
    page,
    { x: 330, y: 300, width: 160, height: 120 },
    await save(page, uploads),
  );
  expect(pixels.width).toBe(500);
  expect(pixels.height).toBe(800);
  expect(pixels.blue).toBeGreaterThan(50);
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("0");
  await expect(page.getByLabel("标注文字")).toHaveValue("定位文字");
});

test("非1:1原图的多行字号背景在导出中按自然比例缩放", async ({
  page,
}, testInfo) => {
  const { uploads } = await setupTextHost(page, { width: 300, height: 200 });
  await page.getByRole("button", { name: "文字", exact: true }).click();
  await page.getByLabel("标注文字").fill("Scale\nNext");
  await page.getByLabel("文字字号").selectOption("32");
  await page.getByLabel("文字字体").fill("monospace");
  await page.getByLabel("文字字体").press("Tab");
  await page.getByLabel("文字背景", { exact: true }).check();
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await drag(page, 150, 180, 550, 180);
  await deselect(page);
  await expect
    .poll(
      async () =>
        (await scan(page, { x: 140, y: 140, width: 425, height: 100 })).blue,
    )
    .toBeGreaterThan(100);
  const png = await save(page, uploads);
  await testInfo.attach("text-small-export", {
    body: png,
    contentType: "image/png",
  });
  const pixels = await scan(
    page,
    { x: 45, y: 45, width: 145, height: 40 },
    png,
  );
  expect(pixels.width).toBe(300);
  expect(pixels.height).toBe(200);
  expect(pixels.blue).toBeGreaterThan(30);
  expect(pixels.white).toBeGreaterThan(500);
  expect(
    (await scan(page, { x: 49, y: 58, width: 60, height: 4 }, png)).blue,
  ).toBeGreaterThan(5);
  expect(
    (await scan(page, { x: 49, y: 63, width: 60, height: 11 }, png)).blue,
  ).toBeGreaterThan(5);
});

for (const label of ["文字", "说明"]) {
  test(
    label + "续编可使用画布取色，取消保留旧色、完成后导出新色",
    async ({ page }) => {
      await page.addInitScript(() =>
        Object.defineProperty(window, "EyeDropper", {
          value: undefined,
          configurable: true,
        }),
      );
      const { uploads } = await setupTextHost(page);
      await page.getByRole("button", { name: label, exact: true }).click();
      await page.getByLabel("标注文字").fill("取色测试");
      await page.getByLabel("自定义颜色").fill("#1f6feb");
      await page.getByLabel("文字背景", { exact: true }).check();
      await drag(page, 150, 180, 550, 180);
      const canvas = page.getByLabel("图片检查画布");
      const area = {
        x: label === "文字" ? 155 : 555,
        y: 160,
        width: 80,
        height: 18,
      };
      await page.getByRole("button", { name: "编辑文字", exact: true }).click();
      const pick = page.getByRole("button", { name: "屏幕取色", exact: true });
      await expect(pick).toHaveAttribute(
        "title",
        "取色：点击后在画布上取样颜色",
      );
      const sample = async () => {
        await pick.click();
        const at = await client(page, 800, 500);
        await page.mouse.click(at.x, at.y);
        await expect(page.getByLabel("自定义颜色")).toHaveValue("#12a150");
      };
      await sample();
      await expect.poll(async () => (await scan(page, area)).blue).toBe(0);
      await page
        .getByRole("button", { name: "取消文字编辑", exact: true })
        .click();
      await expect(page.getByLabel("自定义颜色")).toHaveValue("#1f6feb");
      await expect
        .poll(async () => (await scan(page, area)).blue)
        .toBeGreaterThan(20);
      expect(
        (await scan(page, area, await save(page, uploads))).blue,
      ).toBeGreaterThan(20);
      await page.getByRole("button", { name: "编辑文字", exact: true }).click();
      await pick.click();
      await page
        .getByRole("button", { name: "取消文字编辑", exact: true })
        .click();
      const afterCancel = await client(page, 800, 500);
      await page.mouse.click(afterCancel.x, afterCancel.y);
      await page.getByLabel("选择标注对象").selectOption("0");
      await expect(page.getByLabel("自定义颜色")).toHaveValue("#1f6feb");
      await page.getByRole("button", { name: "编辑文字", exact: true }).click();
      await pick.click();
      await canvas.focus();
      await page.keyboard.press("Escape");
      await expect(
        page.getByText("已取消取色；标注颜色保持不变。"),
      ).toBeVisible();
      await expect(
        page.getByRole("button", { name: "完成文字编辑", exact: true }),
      ).toBeVisible();
      await sample();
      await page
        .getByRole("button", { name: "完成文字编辑", exact: true })
        .click();
      await deselect(page);
      const png = await save(page, uploads);
      expect((await scan(page, area, png)).blue).toBe(0);
      expect((await scan(page, area, png)).green).toBeGreaterThan(20);
      await page.getByRole("button", { name: "撤销", exact: true }).click();
      await page.getByLabel("选择标注对象").selectOption("0");
      await expect(page.getByLabel("自定义颜色")).toHaveValue("#1f6feb");
      await expect(
        page.getByLabel("选择标注对象").locator("option"),
      ).toHaveCount(2);
    },
  );
}

test("清空前置屏蔽取消文字草稿并保留原文字、矩形和可撤销历史", async ({
  page,
}) => {
  const { uploads } = await setupTextHost(page);
  await page.getByRole("button", { name: "屏蔽", exact: true }).click();
  await drag(page, 80, 80, 150, 130);
  await page.getByRole("button", { name: "文字", exact: true }).click();
  await page.getByLabel("标注文字").fill("A原文字");
  await drag(page, 200, 220, 360, 220);
  await page.getByRole("button", { name: "矩形", exact: true }).click();
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await page.getByLabel("线条宽度").selectOption("8");
  await drag(page, 500, 300, 700, 450);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("1");
  await page.getByRole("button", { name: "编辑文字", exact: true }).click();
  await page.getByLabel("标注文字").fill("A*草稿");
  await page.getByRole("button", { name: "清空屏蔽", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "完成文字编辑", exact: true }),
  ).toHaveCount(0);
  const objects = page.getByLabel("选择标注对象");
  await expect(objects.locator("option")).toHaveText([
    "未选择",
    "1 · 文字",
    "2 · 矩形",
  ]);
  await objects.selectOption("0");
  await expect(page.getByLabel("标注文字")).toHaveValue("A原文字");
  await deselect(page);
  const png = await save(page, uploads);
  expect(
    (await scan(page, { x: 550, y: 298, width: 100, height: 4 }, png)).blue,
  ).toBeGreaterThan(300);
  await page.getByRole("button", { name: "撤销", exact: true }).click();
  await expect(objects.locator("option")).toHaveText([
    "未选择",
    "1 · 屏蔽",
    "2 · 文字",
    "3 · 矩形",
  ]);
  await objects.selectOption("1");
  await expect(page.getByLabel("标注文字")).toHaveValue("A原文字");
});

test("序号输入不先失焦即放置，删除和手改后按最大值自动续号", async ({
  page,
}) => {
  await setupTextHost(page);
  await page.getByRole("button", { name: "序号", exact: true }).click();
  await page.getByLabel("序号格式").selectOption("upper");
  await page.getByLabel("序号起点").fill("25");
  await drag(page, 200, 200, 200, 200);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("0");
  await expect(page.getByLabel("序号值")).toHaveValue("25");
  await page.getByRole("button", { name: "序号", exact: true }).click();
  await drag(page, 300, 200, 300, 200);
  await drag(page, 400, 200, 400, 200);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("1");
  await page.getByLabel("图片检查画布").focus();
  await page.keyboard.press("Delete");
  await page.getByRole("button", { name: "序号", exact: true }).click();
  await drag(page, 500, 200, 500, 200);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("2");
  await expect(page.getByLabel("序号值")).toHaveValue("28");
  await page.getByLabel("选择标注对象").selectOption("0");
  await page.getByLabel("序号值").fill("51");
  await expect(page.getByLabel("序号值")).toBeFocused();
  await page.getByRole("button", { name: "序号", exact: true }).click();
  await drag(page, 600, 200, 600, 200);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await page.getByLabel("选择标注对象").selectOption("3");
  await expect(page.getByLabel("序号值")).toHaveValue("52");
});
test("关闭背景的右上目标说明不从框内穿线，命中和PNG导出一致", async ({
  page,
}) => {
  const { uploads } = await setupTextHost(page);
  await page.getByRole("button", { name: "说明", exact: true }).click();
  await page
    .getByLabel("标注文字")
    .fill("X         \nX         \nX         \nX");
  await page.getByLabel("文字字体").fill("monospace");
  await page.getByLabel("文字字体").press("Tab");
  await page.getByLabel("自定义颜色").fill("#1f6feb");
  await page.getByLabel("说明样式").selectOption("arrow");
  await page.getByLabel("文字背景", { exact: true }).uncheck();
  await drag(page, 600, 100, 300, 220);
  await deselect(page);
  await expect
    .poll(
      async () =>
        (await scan(page, { x: 330, y: 220, width: 70, height: 30 })).blue,
    )
    .toBe(0);
  const at = await client(page, 500, 168);
  await page.mouse.click(at.x, at.y);
  await expect(page.getByLabel("选择标注对象")).toHaveValue("0");
  await deselect(page);
  const png = await save(page, uploads);
  expect(
    (await scan(page, { x: 330, y: 220, width: 70, height: 30 }, png)).blue,
  ).toBe(0);
  expect(
    (await scan(page, { x: 490, y: 155, width: 30, height: 30 }, png)).blue,
  ).toBeGreaterThan(20);
});
test("深色序号底形使用白字，预览和PNG导出均有实际可读字形", async ({
  page,
}) => {
  const { uploads } = await setupTextHost(page);
  await page.getByRole("button", { name: "序号", exact: true }).click();
  await page.getByLabel("序号形状").selectOption("square");
  await page.getByLabel("文字字号").selectOption("48");
  await page.getByLabel("自定义颜色").fill("#000000");
  await drag(page, 300, 250, 300, 250);
  await page.getByRole("button", { name: "选择", exact: true }).click();
  await deselect(page);
  const area = { x: 280, y: 230, width: 40, height: 40 };
  await expect
    .poll(async () => (await scan(page, area)).white)
    .toBeGreaterThan(20);
  const pixels = await scan(page, area, await save(page, uploads));
  expect(pixels.white).toBeGreaterThan(50);
  expect(pixels.black).toBeGreaterThan(100);
});

for (const tool of ["文字", "序号", "说明"] as const) {
  test(`${tool}连续输入真实字体后直接画布绘制，字形写入PNG`, async ({
    page,
  }, testInfo) => {
    const { uploads } = await setupTextHost(page);
    await page.getByRole("button", { name: tool, exact: true }).click();
    if (tool !== "序号") await page.getByLabel("标注文字").fill("iiiiiiiiiiii");
    else await page.getByLabel("序号形状").selectOption("none");
    await page.getByLabel("文字字号").selectOption("48");
    await page.getByLabel("自定义颜色").fill("#1f6feb");
    const font = page.getByLabel("文字字体");
    await font.fill("");
    await font.pressSequentially("Courier New");
    await expect(font).toHaveValue("Courier New");
    await expect(font).toBeFocused();
    if (tool === "说明") await drag(page, 100, 400, 200, 220);
    else if (tool === "序号") await drag(page, 300, 220, 300, 220);
    else await drag(page, 120, 220, 500, 220);
    await expect(page.getByLabel("文字字体")).toHaveValue("Courier New");
    await page.getByRole("button", { name: "选择", exact: true }).click();
    await deselect(page);
    const area =
      tool === "文字"
        ? { x: 360, y: 180, width: 100, height: 60 }
        : tool === "说明"
          ? { x: 440, y: 180, width: 90, height: 60 }
          : { x: 280, y: 180, width: 40, height: 80 };
    await expect
      .poll(async () => (await scan(page, area)).blue)
      .toBeGreaterThan(20);
    const png = await save(page, uploads);
    const exportPath = testInfo.outputPath("font-export.png");
    await writeFile(exportPath, png);
    await testInfo.attach("font-export.png", {
      path: exportPath,
      contentType: "image/png",
    });
    expect((await scan(page, area, png)).blue).toBeGreaterThan(20);
    await page.screenshot({
      path: testInfo.outputPath("direct-font-draw.png"),
    });
  });
}
