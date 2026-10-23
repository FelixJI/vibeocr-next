import { expect, test } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

for (const [width, height] of [
  [240, 1600],
  [1600, 240],
]) {
  test(`preview fits ${width}x${height}, remains horizontal and remembers collapse`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.route("/preview.svg", (route) =>
      route.fulfill({
        contentType: "image/svg+xml",
        body: `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}"><rect width="100%" height="100%" fill="#709acc"/></svg>`,
      }),
    );
    await mountHost(page, {
      ...snapshot,
      features: {
        recognition: {
          input: {
            url: "/preview.svg",
            mediaType: "image/png",
            byteLength: 200,
          },
        },
      },
    });
    const preview = page.locator("#recognition-preview");
    const image = preview.locator(".result-image-stage img");
    await expect(image).toBeVisible();
    await expect
      .poll(async () => {
        const size = await image.boundingBox();
        return size ? Math.abs(size.width / size.height - width / height) : 100;
      })
      .toBeLessThan(0.02);
    for (const viewport of [
      { width: 1280, height: 800 },
      { width: 800, height: 600 },
      { width: 640, height: 480 },
    ]) {
      await page.setViewportSize(viewport);
      await expect
        .poll(() =>
          preview
            .locator(".canvas-stage")
            .evaluate((el) => el.scrollHeight - el.clientHeight),
        )
        .toBeLessThanOrEqual(1);
      const bounds = await image.boundingBox();
      expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height);
      await expect
        .poll(() =>
          page.locator(".inspection-grid").evaluate((grid) => {
            const left = grid.children[0].getBoundingClientRect();
            const right = grid.children[1].getBoundingClientRect();
            return right.x - left.right;
          }),
        )
        .toBeGreaterThan(0);
    }
    await page.getByRole("button", { name: "收起图片预览" }).click();
    await expect(preview).toBeHidden();
    await page.reload();
    await expect(
      page.getByRole("button", { name: "展开图片预览" }),
    ).toBeVisible();
    await expect(preview).toBeHidden();
    await page.getByRole("button", { name: "展开图片预览" }).click();
    await expect(image).toBeVisible();
  });
}
