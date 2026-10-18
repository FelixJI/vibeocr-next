import { expect, test, type Page } from "@playwright/test";
import {
  mountHost,
  snapshot,
  expectCommand,
  requestPinSnapshot,
  readPinReports,
  deferCommand,
  releaseCommand,
  rejectNextCommand,
} from "./workbench-host";

const sourceSession = "a".repeat(32);
const sourceUrl = "https://app.vibeocr/__resource/pin-image";
const png = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
  "base64",
);

async function pinEditor(page: Page) {
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/__resource/pin-image") {
      await route.fulfill({ contentType: "image/png", body: png });
      return;
    }
    if (url.pathname === "/__project") {
      await route.fulfill({
        status: 201,
        json: { contributeId: "b".repeat(32) },
      });
      return;
    }
    if (url.pathname === "/__annotation") {
      await route.fulfill({
        status: 201,
        json: {
          resourceUri: "https://app.vibeocr/__annotation/" + "c".repeat(32),
        },
      });
      return;
    }
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}`,
    );
    await route.fulfill({ response });
  });
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.annotation", "recognition.screenshotSession"],
      features: {
        recognition: { isBusy: false, statusCode: "recognition.ready" },
        imageEdit: {
          isBusy: false,
          statusCode: "recognition.session",
          input: null,
        },
        pinEdit: {
          isBusy: false,
          statusCode: "pinEdit.session",
          input: {
            url: sourceUrl,
            mediaType: "image/png",
            byteLength: png.length,
          },
          screenshotSession: { sessionId: sourceSession, revision: 0 },
        },
      },
    },
    "https://app.vibeocr/#/imageEdit?pin=1",
  );
  await expect(
    page.getByRole("button", { name: "完成贴图", exact: true }),
  ).toBeEnabled();
}

test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: "wait" });
});

test("pin editor saves one owned snapshot and acknowledges only after the command receipt", async ({
  page,
}, testInfo) => {
  await pinEditor(page);
  await expect(page.getByRole("navigation")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "取字", exact: true }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "贴图", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
  await deferCommand(page, "applySnapshot");
  await requestPinSnapshot(page, sourceSession);
  await expect
    .poll(async () =>
      (await page.evaluate(() => document.body.textContent))?.includes(
        "正在上传贴图内容",
      ),
    )
    .toBe(true);
  await expect.poll(async () => (await readPinReports(page)).length).toBe(0);
  await expectCommand(page, {
    scope: "pinEdit",
    action: "applySnapshot",
    arguments: {
      contributeId: "b".repeat(32),
      resourceUri: "https://app.vibeocr/__annotation/" + "c".repeat(32),
      sessionId: sourceSession,
      revision: 1,
    },
  });
  await releaseCommand(page);
  await expect
    .poll(async () =>
      (await readPinReports(page)).some((report) => report.payload.ok === true),
    )
    .toBe(true);
  await expectCommand(page, {
    scope: "pinEdit",
    action: "applySnapshot",
    arguments: {
      contributeId: "b".repeat(32),
      resourceUri: "https://app.vibeocr/__annotation/" + "c".repeat(32),
      sessionId: sourceSession,
      revision: 1,
    },
  });
  await page.screenshot({ path: testInfo.outputPath("pin-editor.png") });
});

test("failed save preserves the editor and a late request for another pin is ignored", async ({
  page,
}) => {
  await pinEditor(page);
  await requestPinSnapshot(page, "d".repeat(32));
  await expect(
    page.getByRole("button", { name: "完成贴图", exact: true }),
  ).toBeEnabled();
  await rejectNextCommand(page);
  await requestPinSnapshot(page, sourceSession);
  await expect
    .poll(async () =>
      (await readPinReports(page)).some(
        (report) => report.payload.ok === false,
      ),
    )
    .toBe(true);
  await expect(
    page.getByRole("button", { name: "完成贴图", exact: true }),
  ).toBeEnabled();
  await requestPinSnapshot(page, sourceSession);
  await expect
    .poll(async () =>
      (await readPinReports(page)).some((report) => report.payload.ok === true),
    )
    .toBe(true);
});

test("pin keyboard save uses the owned transaction and cancel keeps a dirty pin", async ({
  page,
}) => {
  await pinEditor(page);
  page.once("dialog", async (dialog) => {
    await dialog.dismiss();
  });
  await page.evaluate(() =>
    window.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "w",
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    ),
  );
  await expect(
    page.getByRole("button", { name: "完成贴图", exact: true }),
  ).toBeEnabled();
  await page.evaluate(() =>
    window.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "s",
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    ),
  );
  await expect
    .poll(async () =>
      (await readPinReports(page)).some((report) => report.payload.ok === true),
    )
    .toBe(true);
});

test("history is manageable without a session while recording is off", async ({
  page,
}) => {
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    features: {
      imageEdit: { isBusy: false },
      pinEdit: {
        isBusy: false,
        history: {
          enabled: false,
          count: 1,
          totalBytes: 68,
          maximumEntries: 200,
          maximumBytes: 268435456,
          error: "截图已成功，但历史保存失败",
          entries: [
            {
              entryId: "a".repeat(32),
              createdAt: "2026-10-19T00:00:00Z",
              width: 1,
              height: 1,
              bytes: 68,
            },
          ],
        },
      },
    },
  });
  await page.getByLabel("截图历史", { exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("历史保存失败");
  await page.getByRole("button", { name: "重开", exact: true }).click();
  await expectCommand(page, {
    scope: "pinEdit",
    action: "reopenHistoryEntry",
    arguments: { entryId: "a".repeat(32) },
  });
  await page.getByRole("button", { name: "贴图管理", exact: true }).click();
  await expectCommand(page, {
    scope: "pinEdit",
    action: "showManager",
    arguments: {},
  });
});

for (const [name, source, expected] of [
  ["valid formula", "\\frac{x^2}{2}", "formula.rendered"],
  ["invalid formula", "\\unknownCommand{", "formula.renderFailed"],
] as const) {
  test(name, async ({ page }) => {
    const encoded = Buffer.from(source).toString("base64url");
    await mountHost(
      page,
      snapshot,
      `/#/imageEdit?formula=1&render=${"e".repeat(32)}&src=${encoded}`,
      false,
    );
    await expect
      .poll(async () =>
        (await readPinReports(page)).some((report) => report.type === expected),
      )
      .toBe(true);
    if (expected === "formula.rendered") {
      await expect(page.locator(".katex")).toBeVisible();
      const report = (await readPinReports(page)).find(
        (item) => item.type === expected,
      )!;
      expect(report.payload.viewportWidth).toBeGreaterThan(0);
      expect(report.payload.devicePixelRatio).toBeGreaterThan(0);
    }
  });
}
