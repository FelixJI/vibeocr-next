import { expect, test, type Page } from "@playwright/test";
import {
  mountHost,
  snapshot,
  sendState,
  expectCommand,
  requestPinSnapshot,
  readPinReports,
  deferCommand,
  releaseCommand,
  rejectNextCommand,
} from "./workbench-host";
import type { ProjectContributionBody } from "../src/components/project/projectWire";

const sourceSession = "a".repeat(32);
const sourceUrl = "https://app.vibeocr/__resource/pin-image";
const png = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
  "base64",
);

async function pinEditor(
  page: Page,
  image = png,
  excludeBoxes: { x: number; y: number; width: number; height: number }[] = [],
  imageReady?: Promise<void>,
  ordinaryEditor = false,
) {
  const projects: ProjectContributionBody[] = [];
  const uploads: Buffer[] = [];
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/__resource/pin-image") {
      await imageReady;
      await route.fulfill({ contentType: "image/png", body: image });
      return;
    }
    if (url.pathname === "/__project") {
      projects.push(route.request().postDataJSON() as ProjectContributionBody);
      await route.fulfill({
        status: 201,
        json: { contributeId: "b".repeat(32) },
      });
      return;
    }
    if (url.pathname === "/__annotation") {
      uploads.push(route.request().postDataBuffer()!);
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
        [ordinaryEditor ? "pinEdit" : "imageEdit"]: {
          isBusy: false,
          statusCode: "recognition.session",
          input: null,
        },
        [ordinaryEditor ? "imageEdit" : "pinEdit"]: {
          isBusy: false,
          statusCode: "pinEdit.session",
          input: {
            url: sourceUrl,
            mediaType: "image/png",
            byteLength: image.length,
          },
          screenshotSession: {
            sessionId: sourceSession,
            revision: 0,
            excludeBoxes,
          },
        },
      },
    },
    ordinaryEditor
      ? "https://app.vibeocr/#/imageEdit"
      : "https://app.vibeocr/#/imageEdit?pin=1",
  );
  await expect(
    page.getByRole("button", {
      name: ordinaryEditor ? "贴图" : "完成贴图",
      exact: true,
    }),
  ).toBeEnabled();
  return { projects, uploads };
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
      excludeBoxes: [],
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
      excludeBoxes: [],
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

test("pin cannot edit or commit before its inherited exclusions are ready", async ({
  page,
}) => {
  let releaseImage!: () => void;
  const ready = new Promise<void>((resolve) => {
    releaseImage = resolve;
  });
  const { projects, uploads } = await pinEditor(
    page,
    png,
    [{ x: 0, y: 0, width: 250, height: 1000 }],
    ready,
  );
  try {
    await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
    await requestPinSnapshot(page, sourceSession);
    await expect
      .poll(async () =>
        (await readPinReports(page)).some(
          (report) => report.payload.ok === false,
        ),
      )
      .toBe(true);
    expect(projects).toHaveLength(0);
    expect(uploads).toHaveLength(0);
  } finally {
    releaseImage();
  }
  await expect(page.getByText("原图 1×1", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
  await requestPinSnapshot(page, sourceSession);
  await expect.poll(() => projects.length).toBe(1);
  expect(projects[0]!.state.rotation).toBe(90);
  expect(projects[0]!.state.marks.map((mark) => mark.tool)).toEqual([
    "exclude",
  ]);
});

test("JPEG output remains available while owned snapshot uses a real PNG", async ({
  page,
}) => {
  const { uploads, projects } = await pinEditor(page);
  await page.getByLabel("输出格式", { exact: true }).selectOption("image/jpeg");
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect([...uploads[0]!.subarray(0, 2)]).toEqual([255, 216]);
  await requestPinSnapshot(page, sourceSession);
  await expect.poll(() => uploads.length).toBe(2);
  await expect.poll(() => projects.length).toBe(1);
  expect([...uploads[1]!.subarray(0, 8)]).toEqual([
    137, 80, 78, 71, 13, 10, 26, 10,
  ]);
  await expect
    .poll(async () =>
      (await readPinReports(page)).some((report) => report.payload.ok === true),
    )
    .toBe(true);
  await expect(page.getByLabel("输出格式", { exact: true })).toHaveValue(
    "image/jpeg",
  );
});

test("pin owned save and restore retain spotlight and magnifier geometry and pixels", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const base = Buffer.from(
    await page.evaluate(() => {
      const canvas = document.createElement("canvas");
      canvas.width = 900;
      canvas.height = 600;
      const context = canvas.getContext("2d")!;
      context.fillStyle = "#e02020";
      context.fillRect(0, 0, 450, 600);
      context.fillStyle = "#12a150";
      context.fillRect(450, 0, 450, 600);
      return canvas.toDataURL("image/png").split(",")[1]!;
    }),
    "base64",
  );
  const { projects, uploads } = await pinEditor(page, base, [
    { x: 0, y: 0, width: 250, height: 1000 },
  ]);
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  async function draw(
    tool: string,
    x: number,
    y: number,
    endX: number,
    endY: number,
  ) {
    await page.getByRole("button", { name: tool, exact: true }).click();
    const canvas = page.getByLabel("图片检查画布");
    await canvas.scrollIntoViewIfNeeded();
    const bounds = (await canvas.boundingBox())!;
    await page.mouse.move(
      bounds.x + (bounds.width * x) / 900,
      bounds.y + (bounds.height * y) / 600,
    );
    await page.mouse.down();
    await page.mouse.move(
      bounds.x + (bounds.width * endX) / 900,
      bounds.y + (bounds.height * endY) / 600,
    );
    await page.mouse.up();
  }
  await draw("聚光灯", 100, 100, 300, 280);
  await draw("放大镜", 50, 100, 350, 280);
  await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
  await requestPinSnapshot(page, sourceSession);
  await expect.poll(() => projects.length).toBe(1);
  await expect.poll(() => uploads.length).toBe(1);
  await expect
    .poll(async () =>
      (await readPinReports(page)).some((report) => report.payload.ok === true),
    )
    .toBe(true);
  expect(projects[0]!.state.marks.map((mark) => mark.tool)).toEqual([
    "exclude",
    "spotlight",
    "magnifier",
  ]);
  expect(projects[0]!.state.marks[1]!.regions).toHaveLength(1);
  expect(projects[0]!.state.marks[2]!.zoom).toBe(2);
  const initialBoxes = await page.evaluate(() => {
    const commands = (
      window as Window & {
        __testHost: {
          commands: {
            action: string;
            arguments: {
              excludeBoxes?: {
                x: number;
                y: number;
                width: number;
                height: number;
              }[];
            };
          }[];
        };
      }
    ).__testHost.commands;
    return commands.find((command) => command.action === "applySnapshot")!
      .arguments.excludeBoxes!;
  });
  expect(initialBoxes.length).toBeGreaterThanOrEqual(2); // Base exclusion and magnifier sampling propagation.
  expect(initialBoxes[0]!.x).toBe(0);
  expect(initialBoxes[0]!.y).toBe(0);
  expect(initialBoxes[0]!.width).toBe(1000);
  // Rotation maps the left 225px to the top; the existing natural-pixel contract snaps outward.
  expect(initialBoxes[0]!.height).toBeGreaterThanOrEqual(250);
  expect(initialBoxes[0]!.height).toBeLessThanOrEqual((226 / 900) * 1000);
  const manifest = {
    schemaVersion: 1,
    canvas: projects[0]!.canvas,
    source: {
      mediaType: "image/png",
      width: 900,
      height: 600,
      length: base.length,
    },
    assets: [],
    state: projects[0]!.state,
  };
  const manifestUrl = "https://app.vibeocr/__resource/pin-restored-manifest";
  await page.route(manifestUrl, (route) =>
    route.fulfill({
      contentType: "application/json; charset=utf-8",
      json: manifest,
    }),
  );
  const restoredSession = "d".repeat(32);
  await sendState(page, "pinEdit", {
    isBusy: false,
    statusCode: "pinEdit.session",
    input: { url: sourceUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: {
      sessionId: restoredSession,
      revision: 0,
      excludeBoxes: initialBoxes,
    },
    projectRestore: {
      pendingId: "f".repeat(32),
      sessionId: restoredSession,
      revision: 0,
      generation: 3,
      manifest: {
        url: manifestUrl,
        mediaType: "application/json; charset=utf-8",
        byteLength: JSON.stringify(manifest).length,
      },
      source: {
        url: sourceUrl,
        mediaType: "image/png",
        byteLength: base.length,
      },
      assets: [],
      patches: [],
    },
  });
  await expect(page.getByText("工程已恢复", { exact: false })).toBeVisible();
  await requestPinSnapshot(page, restoredSession);
  await expect.poll(() => projects.length).toBe(2);
  await expect.poll(() => uploads.length).toBe(2);
  expect(projects[1]!.state).toEqual(projects[0]!.state);
  const allBoxes = await page.evaluate(() =>
    (
      window as Window & {
        __testHost: {
          commands: { action: string; arguments: { excludeBoxes?: unknown } }[];
        };
      }
    ).__testHost.commands
      .filter((command) => command.action === "applySnapshot")
      .map((command) => command.arguments.excludeBoxes),
  );
  expect(allBoxes).toEqual([initialBoxes, initialBoxes]);
  const differences = await page.evaluate(
    async (encoded) => {
      const frames = await Promise.all(
        encoded.map(async (value) => {
          const bitmap = await createImageBitmap(
            new Blob(
              [Uint8Array.from(atob(value), (char) => char.charCodeAt(0))],
              { type: "image/png" },
            ),
          );
          const canvas = document.createElement("canvas");
          canvas.width = bitmap.width;
          canvas.height = bitmap.height;
          const context = canvas.getContext("2d")!;
          context.drawImage(bitmap, 0, 0);
          bitmap.close();
          return context.getImageData(0, 0, canvas.width, canvas.height).data;
        }),
      );
      if (frames[0]!.length !== frames[1]!.length)
        throw new Error("Restored dimensions changed");
      return frames[0]!.reduce(
        (count, value, index) => count + (value === frames[1]![index] ? 0 : 1),
        0,
      );
    },
    uploads.map((image) => image.toString("base64")),
  );
  expect(differences).toBe(0);
  const scopes = await page.evaluate(() =>
    (
      window as Window & { __testHost: { commands: { scope: string }[] } }
    ).__testHost.commands.map((command) => command.scope),
  );
  expect(scopes.every((scope) => scope === "pinEdit")).toBe(true);
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

test("first pin is PNG while ordinary save and copy retain JPEG selection", async ({
  page,
}) => {
  const { uploads } = await pinEditor(page, png, [], undefined, true);
  await page.getByLabel("输出格式", { exact: true }).selectOption("image/jpeg");
  await page.getByRole("button", { name: "保存标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(1);
  expect([...uploads[0]!.subarray(0, 3)]).toEqual([0xff, 0xd8, 0xff]);
  await page.getByRole("button", { name: "复制标注图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(2);
  expect([...uploads[1]!.subarray(0, 3)]).toEqual([0xff, 0xd8, 0xff]);
  await page.getByRole("button", { name: "贴图", exact: true }).click();
  await expect.poll(() => uploads.length).toBe(3);
  expect([...uploads[2]!.subarray(0, 8)]).toEqual([
    137, 80, 78, 71, 13, 10, 26, 10,
  ]);
  await expectCommand(page, {
    scope: "imageEdit",
    action: "pinScreenshotImage",
    arguments: {
      resourceUri: "https://app.vibeocr/__annotation/" + "c".repeat(32),
      sessionId: sourceSession,
      revision: 1,
      excludeBoxes: [],
    },
  });
  await expect(page.getByLabel("输出格式", { exact: true })).toHaveValue(
    "image/jpeg",
  );
});

function restoredPinState(
  sessionId: string,
  pendingId: string,
  manifestUrl?: string,
) {
  const input = {
    url: sourceUrl,
    mediaType: "image/png",
    byteLength: png.length,
  };
  return {
    isBusy: false,
    statusCode: "pinEdit.session",
    input,
    screenshotSession: { sessionId, revision: 0, excludeBoxes: [] },
    ...(manifestUrl
      ? {
          projectRestore: {
            pendingId,
            sessionId,
            revision: 0,
            generation: 3,
            manifest: {
              url: manifestUrl,
              mediaType: "application/json",
              byteLength: 128,
            },
            source: input,
            assets: [],
            patches: [],
          },
        }
      : {}),
  };
}
const restoreManifest = (width = 1) => ({
  schemaVersion: 1,
  canvas: { width, height: 1 },
  source: { mediaType: "image/png", width: 1, height: 1, length: png.length },
  assets: [],
  state: { rotation: 0, marks: [], patches: [] },
});

for (const failure of ["fetch", "validate"] as const) {
  test(`pin project ${failure} failure returns to confirmed flat PNG and can save`, async ({
    page,
  }) => {
    const { projects } = await pinEditor(page);
    const manifestUrl = `https://app.vibeocr/__resource/failed-${failure}`;
    await page.route(manifestUrl, (route) =>
      failure === "fetch"
        ? route.fulfill({ status: 500, body: "unavailable" })
        : route.fulfill({ json: restoreManifest(0) }),
    );
    const session = "d".repeat(32),
      pending = "f".repeat(32);
    await sendState(
      page,
      "pinEdit",
      restoredPinState(session, pending, manifestUrl),
    );
    await expectCommand(page, {
      scope: "pinEdit",
      action: "rejectProjectRestore",
      arguments: { pendingId: pending, sessionId: session, revision: 0 },
    });
    await expect(
      page.getByRole("button", { name: "完成贴图", exact: true }),
    ).toBeDisabled();
    await sendState(page, "pinEdit", restoredPinState(session, pending));
    await expect(
      page.getByRole("button", { name: "完成贴图", exact: true }),
    ).toBeEnabled();
    await expect(
      page.getByText("工程恢复失败", { exact: false }),
    ).toBeVisible();
    await page.getByRole("button", { name: "旋转 90°", exact: true }).click();
    await requestPinSnapshot(page, session);
    await expect.poll(() => projects.length).toBe(1);
    expect(projects[0]!.state.rotation).toBe(90);
    expect(projects[0]!.state.marks).toEqual([]);
    await expect
      .poll(async () =>
        (await readPinReports(page)).some(
          (report) => report.payload.ok === true,
        ),
      )
      .toBe(true);
  });
}

for (const late of ["fetch", "receipt"] as const) {
  test(`late old pin restore ${late} cannot unlock or install into a new session`, async ({
    page,
  }) => {
    await pinEditor(page);
    const urlA = "https://app.vibeocr/__resource/old-restore";
    const urlB = "https://app.vibeocr/__resource/new-restore";
    let releaseA!: () => void, releaseB!: () => void;
    const a = new Promise<void>((resolve) => {
      releaseA = resolve;
    });
    const b = new Promise<void>((resolve) => {
      releaseB = resolve;
    });
    let startedA = false,
      startedB = false;
    await page.route(urlA, async (route) => {
      startedA = true;
      if (late === "fetch") await a;
      await route.fulfill({ status: 500 });
    });
    await page.route(urlB, async (route) => {
      startedB = true;
      await b;
      await route.fulfill({ json: restoreManifest() });
    });
    const sessionA = "d".repeat(32),
      sessionB = "e".repeat(32);
    if (late === "receipt") await deferCommand(page, "rejectProjectRestore");
    await sendState(
      page,
      "pinEdit",
      restoredPinState(sessionA, "a".repeat(32), urlA),
    );
    await expect.poll(() => startedA).toBe(true);
    if (late === "receipt")
      await expectCommand(page, {
        scope: "pinEdit",
        action: "rejectProjectRestore",
        arguments: {
          pendingId: "a".repeat(32),
          sessionId: sessionA,
          revision: 0,
        },
      });
    await sendState(
      page,
      "pinEdit",
      restoredPinState(sessionB, "b".repeat(32), urlB),
    );
    await expect.poll(() => startedB).toBe(true);
    releaseA();
    if (late === "receipt") await releaseCommand(page);
    await expect(
      page.getByRole("button", { name: "完成贴图", exact: true }),
    ).toBeDisabled();
    releaseB();
    await expect(page.getByText("工程已恢复", { exact: false })).toBeVisible();
    await expect(
      page.getByRole("button", { name: "完成贴图", exact: true }),
    ).toBeEnabled();
    await requestPinSnapshot(page, sessionB);
    await expect
      .poll(async () =>
        (await readPinReports(page)).some(
          (report) =>
            report.payload.sessionId === sessionB && report.payload.ok === true,
        ),
      )
      .toBe(true);
  });
}

test("pin restore rejection failure remains locked with an explicit discard exit", async ({
  page,
}) => {
  await pinEditor(page);
  const url = "https://app.vibeocr/__resource/reject-failure";
  await page.route(url, (route) => route.fulfill({ status: 500 }));
  await rejectNextCommand(page);
  await sendState(
    page,
    "pinEdit",
    restoredPinState("d".repeat(32), "e".repeat(32), url),
  );
  await expect(page.getByText("回退未确认", { exact: false })).toBeVisible();
  await expect(
    page.getByRole("button", { name: "完成贴图", exact: true }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "放弃更改", exact: true }),
  ).toBeEnabled();
});
