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
