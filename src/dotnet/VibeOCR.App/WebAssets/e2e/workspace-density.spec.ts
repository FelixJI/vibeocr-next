import { expect, test } from "@playwright/test";
import { mountHost, snapshot } from "./workbench-host";

for (const height of [800, 1440]) {
  test(`workspace header stays compact at height ${height}`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1280, height });
    await mountHost(page, snapshot);
    const header = page.locator(".workspace-header");
    const description = header.locator(".workspace-description");
    const headerBox = await header.boundingBox();
    const descriptionBox = await description.boundingBox();
    expect(headerBox).not.toBeNull();
    expect(descriptionBox).not.toBeNull();
    expect(
      headerBox!.y +
        headerBox!.height -
        (descriptionBox!.y + descriptionBox!.height),
    ).toBeLessThanOrEqual(16);
    await page.screenshot({
      path: testInfo.outputPath("compact-workspace.png"),
    });
  });
}
