import { expect, test, type Page } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

/** 形状半透明合成的真实像素契约（矩形/椭圆参数化同一测试）：
 *  1) 预览画布：填充 40% 与边框 70% 独立合成（红色填充叠绿底、蓝色描边
 *     分别叠在填充/源图上），数值输入任意整数 0-100 生效；
 *  2) 实际导出：保存上传的 PNG 解码后与预览同源（同一 draw 路径）；
 *  3) 撤销回到无标注源图像素。
 *  源图 80×40 纯绿 (#12a150)，画布显示坐标 900×600：图像满宽（scale
 *  11.25）、垂直居中（y∈[75,525]）。 */

const GREEN: readonly [number, number, number] = [18, 161, 80];
const BORDER_BLUE: readonly [number, number, number] = [31, 111, 235];

/** 前景 alpha 叠加到绿色底色的期望值。 */
function overGreen(
  foreground: readonly [number, number, number],
  alpha: number,
): readonly [number, number, number] {
  return foreground.map((channel, index) =>
    Math.round(alpha * channel + (1 - alpha) * GREEN[index]!),
  ) as readonly [number, number, number];
}

function expectPixel(
  actual: readonly number[],
  expected: readonly [number, number, number],
  tolerance: number,
) {
  for (let channel = 0; channel < 3; channel += 1) {
    expect(Math.abs(actual[channel]! - expected[channel]!)).toBeLessThanOrEqual(
      tolerance,
    );
  }
}

async function setupShapeHost(
  page: Page,
): Promise<{ uploads: { contentType: string; body: Buffer }[] }> {
  await page.setViewportSize({ width: 1280, height: 800 });
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
  await page.route("**/shape-fill-source.svg", (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect width="80" height="40" fill="#12a150"/></svg>',
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
          url: "/shape-fill-source.svg",
          mediaType: "image/png",
          byteLength: 2048,
        },
      },
    },
  });
  const canvas = page.getByLabel("图片检查画布");
  await expect(canvas).toBeVisible();
  await expect(page.getByText(/原图 80×40/)).toBeVisible();
  return { uploads };
}

async function decodeUpload(
  page: Page,
  body: Buffer,
): Promise<{
  width: number;
  height: number;
  at: (x: number, y: number) => number[];
}> {
  const decoded = await page.evaluate(async (base64) => {
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
    const data = context.getImageData(0, 0, bitmap.width, bitmap.height).data;
    return {
      width: bitmap.width,
      height: bitmap.height,
      pixels: Array.from(data),
    };
  }, body.toString("base64"));
  return {
    width: decoded.width,
    height: decoded.height,
    at: (x: number, y: number) => {
      const offset = (y * decoded.width + x) * 4;
      return [
        decoded.pixels[offset] ?? -1,
        decoded.pixels[offset + 1] ?? -1,
        decoded.pixels[offset + 2] ?? -1,
      ];
    },
  };
}

for (const shapeTool of ["矩形", "椭圆"] as const) {
  test(`${shapeTool} 半透明填充40%与独立边框70%写入预览与导出像素`, async ({
    page,
  }) => {
    const { uploads } = await setupShapeHost(page);
    const canvas = page.getByLabel("图片检查画布");

    await page.getByRole("button", { name: shapeTool, exact: true }).click();
    // 边框色设为蓝色 #1f6feb（默认橙色），线宽取最宽 8px：采样点远离
    // 填充边界与亚像素栅格化影响，验证准确 70% 合成。
    await page.getByLabel("自定义颜色").fill("#1f6feb");
    await page.getByLabel("线条宽度").selectOption("8");
    await page.getByLabel("填充").check();
    await page.getByLabel("填充颜色").fill("#ff0000");
    const fillOpacity = page.getByLabel("填充不透明度");
    await fillOpacity.fill("40");
    await fillOpacity.blur();
    const borderOpacity = page.getByLabel("边框不透明度");
    await borderOpacity.fill("70");
    await borderOpacity.blur();
    // 自定义颜色与任意整数不透明度（37）也必须是可输入的。
    await borderOpacity.fill("37");
    await borderOpacity.blur();
    await borderOpacity.fill("70");
    await borderOpacity.blur();

    await canvas.scrollIntoViewIfNeeded();
    const box = (await canvas.boundingBox())!;
    // 画布显示坐标 (180,240)-(450,360)：取整避免小数拖拽引入边缘抗锯齿。
    const toClient = (x: number, y: number) => ({
      x: Math.round(box.x + (box.width * x) / 900),
      y: Math.round(box.y + (box.height * y) / 600),
    });
    const start = toClient(180, 240);
    const end = toClient(450, 360);
    await page.mouse.move(start.x, start.y);
    await page.mouse.down();
    await page.mouse.move(end.x, end.y);
    await page.mouse.up();

    const expectedFill = overGreen([255, 0, 0], 0.4);
    const expectedBorderOverFill = [
      Math.round(0.7 * BORDER_BLUE[0] + 0.3 * expectedFill[0]),
      Math.round(0.7 * BORDER_BLUE[1] + 0.3 * expectedFill[1]),
      Math.round(0.7 * BORDER_BLUE[2] + 0.3 * expectedFill[2]),
    ] as const;
    const expectedBorderOverGreen = overGreen(BORDER_BLUE, 0.7);

    // 1) 预览像素：中心为填充 40% 叠绿底；描边带内外两侧分别按 70% 叠加
    //    在填充/源图上（颜色与不透明度独立合成）。
    const preview = await canvas.evaluate((element: HTMLCanvasElement) => {
      const context = element.getContext("2d")!;
      const at = (x: number, y: number) => {
        const pixel = context.getImageData(
          Math.round((x / 900) * element.width),
          Math.round((y / 600) * element.height),
          1,
          1,
        ).data;
        return [pixel[0], pixel[1], pixel[2]] as const;
      };
      return {
        center: at(315, 300),
        // 实测拖拽起点偏移≈-1.6px：描边带≈[174.4,182.4]、填充左边界≈178.4。
        // 180 深入描边且在填充内（距两边界 >1.5px）；176 深入描边且在源图侧。
        strokeOverFill: at(180, 300),
        strokeOverSource: at(176, 300),
        outside: at(150, 300),
      };
    });
    expectPixel(preview.center, expectedFill, 3);
    expectPixel(preview.strokeOverFill, expectedBorderOverFill, 3);
    expectPixel(preview.strokeOverSource, expectedBorderOverGreen, 3);
    expectPixel(preview.outside, GREEN, 3);

    // 2) 实际导出：保存后的 PNG 与预览同源；自然像素 80×40。
    await page.getByRole("button", { name: "保存标注图", exact: true }).click();
    await expect.poll(() => uploads.length).toBe(1);
    expect(uploads[0]!.contentType).toBe("image/png");
    const exported = await decodeUpload(page, uploads[0]!.body);
    expect(exported.width).toBe(80);
    expect(exported.height).toBe(40);
    // 填充区中心（自然 (28,20)）：40% 红叠绿；未覆盖区：源图绿原样。
    expectPixel(exported.at(28, 20), expectedFill, 4);
    expectPixel(exported.at(6, 20), GREEN, 4);

    // 3) 撤销：导出回到无标注的纯源图。
    await page.getByRole("button", { name: "撤销", exact: true }).click();
    await page.getByRole("button", { name: "保存标注图", exact: true }).click();
    await expect.poll(() => uploads.length).toBe(2);
    const undone = await decodeUpload(page, uploads[1]!.body);
    expectPixel(undone.at(28, 20), GREEN, 4);
  });
}
