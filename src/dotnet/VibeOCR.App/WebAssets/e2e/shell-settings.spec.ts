import { expect, test } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

test("update sources wrap without crowding the actions", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 640, height: 900 });
  await mountHost(page, {
    ...snapshot,
    route: "diagnostics",
    capabilities: ["update.check", "update.selectSource", "about.openProject"],
    features: {
      about: {
        version: "0.8.5",
        license: "MIT",
        projectUrl: "https://github.com/FelixJI/vibeocr-next",
      },
      update: {
        statusCode: "update.unchecked",
        detectionEndpoint:
          "https://github.com/FelixJI/vibeocr-next/releases/latest/download/releases.win.json",
        sources: [
          {
            id: "direct",
            kind: "direct",
            displayName: "GitHub",
            endpoint:
              "https://github.com/FelixJI/vibeocr-next/releases/latest/download",
            selected: true,
          },
        ],
      },
    },
  });
  await expect(
    page.getByRole("button", { name: "检查更新", exact: true }),
  ).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(
    640,
  );
  await page.screenshot({ path: testInfo.outputPath("about.png") });
});

for (const width of [640, 1280]) {
  test(`application switches remain accessible at ${width}px`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await mountHost(page, {
      ...snapshot,
      route: "settings",
      capabilities: ["settings.shell", "settings.tray"],
      features: { settings: { startupEnabled: true, minimizeToTray: false } },
    });
    await expect(
      page.getByRole("switch", { name: "开机自启动" }),
    ).toBeChecked();
    const tray = page.getByRole("switch", { name: "最小化到托盘" });
    await tray.focus();
    await page.keyboard.press("Space");
    await expectCommand(page, {
      scope: "settings",
      action: "setMinimizeToTray",
      arguments: { enabled: true },
    });
    await expect(page.locator(".workspace-header .eyebrow")).toHaveCount(0);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBe(width);
    await page.screenshot({ path: testInfo.outputPath("settings.png") });
  });
}
