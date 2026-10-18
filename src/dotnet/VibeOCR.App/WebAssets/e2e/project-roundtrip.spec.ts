import { expect, test, type Page, type Route } from "@playwright/test";

import {
  expectCommand,
  deferCommand,
  releaseCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

/** #233 工程 roundtrip：保存（采集→/__project 契约校验上传→窄命令）、
 * 打开（全资源 fetch/decode→ack 前可失败校验→ack 旧绑定→切换后原子安装
 * 已准备数据，不重取）、真实补丁保存、保存→打开→导出像素一致、失败/
 * 取消保画布、截图通道（recognition 作用域）保存与非默认逻辑帧。 */

// 等待导航触发的资源代理结束，避免 teardown 提前释放 APIResponse。
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: "wait" });
});

async function canvasClient(
  page: Page,
  x: number,
  y: number,
): Promise<{ x: number; y: number }> {
  const box = await page.getByLabel("图片检查画布").boundingBox();
  if (!box) throw new Error("canvas not visible");
  return {
    x: Math.round(box.x + (box.width * x) / 900),
    y: Math.round(box.y + (box.height * y) / 600),
  };
}

async function dragOnCanvas(
  page: Page,
  from: { x: number; y: number },
  to: { x: number; y: number },
) {
  const start = await canvasClient(page, from.x, from.y);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  const end = await canvasClient(page, to.x, to.y);
  await page.mouse.move(end.x, end.y);
  await page.mouse.up();
}

async function makePng(
  page: Page,
  width: number,
  height: number,
  fill: string,
): Promise<Buffer> {
  const base64 = await page.evaluate(
    ({ width, height, fill }) => {
      const canvas = document.createElement("canvas");
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext("2d")!;
      context.fillStyle = fill;
      context.fillRect(0, 0, width, height);
      return canvas.toDataURL("image/png").split(",")[1]!;
    },
    { width, height, fill },
  );
  return Buffer.from(base64, "base64");
}

async function action(page: Page, name: string) {
  await page.getByRole("button", { name, exact: true }).click();
}

async function proxyProductionAssets(page: Page) {
  await page.route("https://app.vibeocr/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await page.request.get(
      `http://127.0.0.1:4174${url.pathname}${url.search}`,
    );
    await route.fulfill({ response });
  });
}

interface ContributionPayload {
  canvas: { width: number; height: number };
  state: { rotation: number; marks: unknown[]; patches: unknown[] };
  patches: {
    id: number;
    x: number;
    y: number;
    width: number;
    height: number;
    png: string;
  }[];
}

/** /__project 契约 mock：镜像宿主 WorkbenchProjectStore 的关键拒绝条件，
 * 违规回 403（不再无条件 201 掩盖 C# 契约）。 */
function contractProjectRoute(uploads: ContributionPayload[]) {
  return async (route: Route) => {
    const raw = route.request().postDataBuffer()?.toString("utf8") ?? "";
    let payload: ContributionPayload;
    try {
      payload = JSON.parse(raw) as ContributionPayload;
    } catch {
      await route.fulfill({ status: 403, body: "invalid json" });
      return;
    }
    const reject = async (why: string) =>
      route.fulfill({ status: 403, body: why });
    if (
      !payload ||
      typeof payload !== "object" ||
      !Number.isInteger(payload.canvas?.width) ||
      !Number.isInteger(payload.canvas?.height) ||
      payload.canvas.width < 1 ||
      payload.canvas.height < 1
    ) {
      await reject("canvas");
      return;
    }
    if (!Array.isArray(payload.state?.marks)) {
      await reject("marks");
      return;
    }
    // 宿主契约：state.patches 必须为空数组（单一真源在 patches 数组）。
    if (
      !Array.isArray(payload.state?.patches) ||
      payload.state.patches.length
    ) {
      await reject("state.patches");
      return;
    }
    if (!Array.isArray(payload.patches) || payload.patches.length > 126) {
      await reject("patch count");
      return;
    }
    for (const patch of payload.patches) {
      if (
        !Number.isInteger(patch?.id) ||
        !Number.isInteger(patch?.x) ||
        !Number.isInteger(patch?.y) ||
        !Number.isInteger(patch?.width) ||
        !Number.isInteger(patch?.height) ||
        typeof patch?.png !== "string"
      ) {
        await reject("patch shape");
        return;
      }
      const bytes = Buffer.from(patch.png, "base64");
      // PNG 签名 + IHDR 维度必须等于声明矩形（镜像宿主校验）。
      if (
        bytes.length < 24 ||
        bytes[0] !== 0x89 ||
        bytes.readUInt32BE(12) !== 0x49484452 ||
        bytes.readUInt32BE(16) !== patch.width ||
        bytes.readUInt32BE(20) !== patch.height
      ) {
        await reject("patch png");
        return;
      }
    }
    uploads.push(payload);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ contributeId: "a".repeat(32) }),
    });
  };
}

const SESSION = "33333333333333333333333333333333";

test("imageEdit: 保存工程经 /__project 契约上传完整贡献并发出窄命令", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  const base = await makePng(page, 900, 600, "#f0f0f0");
  const baseUrl = "/project-source.png";
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  const uploads: ContributionPayload[] = [];
  await page.route("**/__project", contractProjectRoute(uploads));
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    capabilities: ["recognition.annotation"],
    features: {
      imageEdit: {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: baseUrl,
          mediaType: "image/png",
          byteLength: base.length,
        },
        screenshotSession: { sessionId: SESSION, revision: 0 },
      },
    },
  });
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();

  // 画一个矩形对象：提交入历史即推进会话修订（权威信号）。
  await action(page, "矩形");
  await dragOnCanvas(page, { x: 150, y: 150 }, { x: 420, y: 320 });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: SESSION, revision: 1 },
  });

  await action(page, "保存工程");
  await expect.poll(() => uploads.length).toBe(1);
  const payload = uploads[0]!;
  expect(payload.canvas).toEqual({ width: 900, height: 600 });
  expect(payload.state.rotation).toBe(0);
  expect(payload.state.marks).toHaveLength(1);
  expect((payload.state.marks[0] as { tool: string }).tool).toBe("rectangle");
  expect(payload.state.patches).toEqual([]);
  expect(payload.patches).toEqual([]);
  await expectCommand(page, {
    scope: "imageEdit",
    action: "saveProject",
    arguments: {
      contributeId: "a".repeat(32),
      sessionId: SESSION,
      revision: 1,
    },
  });
  // 保存成功反馈与画布保留（对象仍在：修订不再推进）。
  await expect(page.getByText("已保存工程", { exact: false })).toBeVisible();
});

test("imageEdit: 已应用修补作为 PNG 补丁上传且 state.patches 保持空", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  const base = await makePng(page, 900, 600, "#40a060");
  const baseUrl = "/patch-source.png";
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  const uploads: ContributionPayload[] = [];
  await page.route("**/__project", contractProjectRoute(uploads));
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    capabilities: ["recognition.annotation"],
    features: {
      imageEdit: {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: baseUrl,
          mediaType: "image/png",
          byteLength: base.length,
        },
        screenshotSession: { sessionId: SESSION, revision: 0 },
      },
    },
  });
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  // 真实修补链路：框选→本地 worker 预览→应用（与 inpaint.spec 同流程）。
  await action(page, "去水印");
  await dragOnCanvas(page, { x: 300, y: 200 }, { x: 600, y: 380 });
  await expect(
    page.getByText("已框选修补区域", { exact: false }),
  ).toBeVisible();
  await action(page, "预览修补");
  await expect(page.getByText("修补预览完成", { exact: false })).toBeVisible({
    timeout: 30_000,
  });
  await action(page, "应用修补");
  await expect(page.getByText("已应用修补", { exact: false })).toBeVisible();

  await action(page, "保存工程");
  await expect.poll(() => uploads.length).toBe(1);
  const payload = uploads[0]!;
  // 契约：补丁在顶部数组携带完整 PNG+自然矩形，state.patches 为空。
  expect(payload.state.patches).toEqual([]);
  expect(payload.patches).toHaveLength(1);
  const patch = payload.patches[0]!;
  expect(Number.isInteger(patch.id)).toBe(true);
  expect(patch.width).toBeGreaterThan(0);
  expect(patch.height).toBeGreaterThan(0);
  const bytes = Buffer.from(patch.png, "base64");
  expect(bytes.readUInt32BE(16)).toBe(patch.width);
  expect(bytes.readUInt32BE(20)).toBe(patch.height);
});

test("imageEdit: 保存→打开→导出像素一致（非默认逻辑帧）", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  const base = await makePng(page, 900, 600, "#e8e8ff");
  const baseUrl = "/roundtrip-source.png";
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  const uploads: ContributionPayload[] = [];
  await page.route("**/__project", contractProjectRoute(uploads));
  const exports: Buffer[] = [];
  await page.route("**/__annotation", async (route) => {
    exports.push(route.request().postDataBuffer()!);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        resourceUri:
          "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
      }),
    });
  });
  const state = {
    isBusy: false,
    statusCode: "recognition.session",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId: SESSION, revision: 0 },
  };
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    capabilities: ["recognition.annotation"],
    features: { imageEdit: state },
  });
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();

  // 阶段一：恢复一个真实的非默认逻辑帧（1280×720）工程。mark 坐标在
  // 1280×720 空间内手工学定，与 canvas 声明一致（宿主组装总是同源）。
  const authoredMark = {
    id: "mark-1",
    tool: "rectangle",
    start: { x: 256, y: 192 },
    end: { x: 896, y: 512 },
    style: { color: "#dd2200", strokeWidth: 8 },
  };
  const manifestOne = {
    schemaVersion: 1,
    canvas: { width: 1280, height: 720 },
    source: {
      mediaType: "image/png",
      width: 900,
      height: 600,
      length: base.length,
    },
    assets: [],
    state: { rotation: 0, marks: [authoredMark], patches: [] },
  };
  await page.route("/roundtrip-manifest.json", (route) =>
    route.fulfill({
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify(manifestOne),
    }),
  );
  const firstSession = "66666666666666666666666666666666";
  await sendState(page, "imageEdit", {
    ...state,
    projectOpen: {
      pendingId: "e".repeat(32),
      sessionId: SESSION,
      revision: 0,
      generation: 7,
      manifest: {
        url: "/roundtrip-manifest.json",
        mediaType: "application/json; charset=utf-8",
        byteLength: JSON.stringify(manifestOne).length,
      },
      source: {
        url: baseUrl,
        mediaType: "image/png",
        byteLength: base.length,
      },
      assets: [],
      patches: [],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackProjectOpen",
    arguments: {
      pendingId: "e".repeat(32),
      sessionId: SESSION,
      revision: 0,
      generation: 7,
    },
  });
  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.session",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId: firstSession, revision: 0 },
  });
  await expect(page.getByText("工程已恢复", { exact: false })).toBeVisible();
  // 恢复采纳保存的逻辑 frame（非默认 900×600），仅 CSS 适配。
  await expect
    .poll(() =>
      page
        .getByLabel("图片检查画布")
        .evaluate((element) =>
          Number((element as HTMLCanvasElement).dataset.coordinateWidth),
        ),
    )
    .toBe(1280);

  // 第一次导出（保存前基准像素）。
  await action(page, "保存标注图");
  await expect.poll(() => exports.length).toBe(1);
  await action(page, "保存工程");
  await expect.poll(() => uploads.length).toBe(1);

  // 阶段二：宿主用捕获贡献同源组装 manifest#2（canvas/marks 与保存时
  // 一致），再次打开→安装→导出，必须与第一次逐像素一致。
  const payload = uploads[0]!;
  expect(payload.canvas).toEqual({ width: 1280, height: 720 });
  const manifest = {
    schemaVersion: 1,
    canvas: payload.canvas,
    source: {
      mediaType: "image/png",
      width: 900,
      height: 600,
      length: base.length,
    },
    assets: [],
    state: {
      ...payload.state,
      patches: payload.patches.map((patch) => ({
        id: patch.id,
        x: patch.x,
        y: patch.y,
        width: patch.width,
        height: patch.height,
      })),
    },
  };
  const manifestUrl = "/roundtrip-manifest-2.json";
  await page.route(manifestUrl, (route) =>
    route.fulfill({
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify(manifest),
    }),
  );
  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.session",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId: firstSession, revision: 0 },
    projectOpen: {
      pendingId: "5".repeat(32),
      sessionId: firstSession,
      revision: 0,
      generation: 8,
      manifest: {
        url: manifestUrl,
        mediaType: "application/json; charset=utf-8",
        byteLength: JSON.stringify(manifest).length,
      },
      source: {
        url: baseUrl,
        mediaType: "image/png",
        byteLength: base.length,
      },
      assets: [],
      patches: payload.patches.map((patch) => ({
        id: patch.id,
        resource: {
          url: `/roundtrip-patch-${patch.id}.png`,
          mediaType: "image/png",
          byteLength: 0,
        },
      })),
    },
  });
  for (const patch of payload.patches) {
    await page.route(`/roundtrip-patch-${patch.id}.png`, (route) =>
      route.fulfill({
        contentType: "image/png",
        body: Buffer.from(patch.png, "base64"),
      }),
    );
  }
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackProjectOpen",
    arguments: {
      pendingId: "5".repeat(32),
      sessionId: firstSession,
      revision: 0,
      generation: 8,
    },
  });
  const restoredSession = "55555555555555555555555555555555";
  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.session",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId: restoredSession, revision: 0 },
  });
  await expect(page.getByText("工程已恢复", { exact: false })).toBeVisible();
  // 恢复采纳保存的逻辑 frame（非默认 900×600），仅 CSS 适配。
  await expect
    .poll(() =>
      page
        .getByLabel("图片检查画布")
        .evaluate((element) =>
          Number((element as HTMLCanvasElement).dataset.coordinateWidth),
        ),
    )
    .toBe(1280);

  // 第二次导出：与第一次逐像素一致（对象/样式/坐标无重投影）。
  await action(page, "保存标注图");
  await expect.poll(() => exports.length).toBe(2);
  const identical = await page.evaluate(
    ({ first, second }) => {
      const decode = async (bytes: string) => {
        const bitmap = await createImageBitmap(
          new Blob([Uint8Array.from(atob(bytes), (c) => c.charCodeAt(0))], {
            type: "image/png",
          }),
        );
        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        const context = canvas.getContext("2d")!;
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        return {
          width: canvas.width,
          height: canvas.height,
          data: Array.from(
            context.getImageData(0, 0, canvas.width, canvas.height).data,
          ),
        };
      };
      return Promise.all([decode(first), decode(second)]).then(
        ([a, b]) =>
          a.width === b.width &&
          a.height === b.height &&
          a.data.length === b.data.length &&
          a.data.every((value, index) => value === b.data[index]),
      );
    },
    {
      first: exports[0]!.toString("base64"),
      second: exports[1]!.toString("base64"),
    },
  );
  expect(identical).toBe(true);
});

test("recognition: 截图会话通道保存工程可见文字并发 recognition.saveProject", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  const base = await makePng(page, 900, 600, "#eeeeee");
  const baseUrl = "https://app.vibeocr/__resource/capture-base";
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  const uploads: ContributionPayload[] = [];
  await page.route("**/__project", contractProjectRoute(uploads));
  const recognitionSession = "22222222222222222222222222222222";
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.annotation"],
      features: {
        recognition: {
          isBusy: false,
          statusCode: "recognition.session",
          input: {
            url: baseUrl,
            mediaType: "image/png",
            byteLength: base.length,
          },
          screenshotSession: {
            sessionId: recognitionSession,
            revision: 0,
          },
        },
      },
    },
    "https://app.vibeocr/#/imageEdit?scene=1",
  );
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  // 所有场景显示可见文字“保存工程”（scene 不再是空占位）。
  const button = page.getByRole("button", { name: "保存工程", exact: true });
  await expect(button).toBeVisible();
  await expect(button).toHaveText("保存工程");
  await button.click();
  await expect.poll(() => uploads.length).toBe(1);
  await expectCommand(page, {
    scope: "recognition",
    action: "saveProject",
    arguments: {
      contributeId: "a".repeat(32),
      sessionId: recognitionSession,
      revision: 0,
    },
  });
});

test("imageEdit: 打开工程先取全部资源再 ack，切换后安装且不重取", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  // 非 1:1 逻辑帧：自然图 640×480 恢复进 900×600 画布，rotation 90。
  const source = await makePng(page, 640, 480, "#101010");
  const patch = await makePng(page, 8, 8, "#00ff00");
  const sourceUrl = "https://app.vibeocr/__resource/open-source";
  const patchUrl = "https://app.vibeocr/__resource/open-patch";
  const manifestUrl = "https://app.vibeocr/__resource/open-manifest";
  let patchReads = 0;
  await page.route(sourceUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: source }),
  );
  await page.route(patchUrl, (route) => {
    patchReads++;
    return route.fulfill({ contentType: "image/png", body: patch });
  });
  const manifest = {
    schemaVersion: 1,
    canvas: { width: 900, height: 600 },
    source: {
      mediaType: "image/png",
      width: 640,
      height: 480,
      length: source.length,
    },
    assets: [],
    state: {
      rotation: 90,
      marks: [
        {
          id: "mark-1",
          tool: "text",
          start: { x: 120, y: 140 },
          end: { x: 360, y: 200 },
          text: "恢复对象",
          textWidth: 200,
        },
      ],
      patches: [{ id: 1, x: 10, y: 12, width: 8, height: 8 }],
    },
  };
  await page.route(manifestUrl, (route) =>
    route.fulfill({
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify(manifest),
    }),
  );

  // 空会话画布（无 input）：打开工程入口可用。
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.annotation"],
      features: {
        imageEdit: { isBusy: false, statusCode: "recognition.ready" },
      },
    },
    "https://app.vibeocr/",
  );
  await action(page, "打开工程");
  await expectCommand(page, {
    scope: "imageEdit",
    action: "openProject",
    arguments: {},
  });

  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.ready",
    projectOpen: {
      pendingId: "c".repeat(32),
      sessionId: "0".repeat(32),
      revision: 0,
      generation: 5,
      manifest: {
        url: manifestUrl,
        mediaType: "application/json; charset=utf-8",
        byteLength: JSON.stringify(manifest).length,
      },
      source: {
        url: sourceUrl,
        mediaType: "image/png",
        byteLength: source.length,
      },
      assets: [],
      patches: [
        {
          id: 1,
          resource: {
            url: patchUrl,
            mediaType: "image/png",
            byteLength: patch.length,
          },
        },
      ],
    },
  });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "ackProjectOpen",
    arguments: {
      pendingId: "c".repeat(32),
      sessionId: "0".repeat(32),
      revision: 0,
      generation: 5,
    },
  });
  await expect(page.getByText("工程已确认", { exact: false })).toBeVisible();

  const restoredSession = "44444444444444444444444444444444";
  await sendState(page, "imageEdit", {
    isBusy: false,
    statusCode: "recognition.session",
    input: {
      url: sourceUrl,
      mediaType: "image/png",
      byteLength: source.length,
    },
    screenshotSession: { sessionId: restoredSession, revision: 0 },
  });
  await expect(page.getByText("工程已恢复", { exact: false })).toBeVisible();
  // 安装只用已 decode 数据：补丁资源不发生第二次读取。
  await page.waitForTimeout(600);
  expect(patchReads).toBe(1);
});

for (const { stateFirst, leaveDuringAck } of [
  { stateFirst: false, leaveDuringAck: false },
  { stateFirst: true, leaveDuringAck: false },
  { stateFirst: true, leaveDuringAck: true },
]) {
  test(
    "imageEdit: " +
      (leaveDuringAck
        ? "ack在途宿主路由卸载再返回"
        : stateFirst
          ? "新状态先到且继续编辑后回执迟到"
          : "回执先于新状态") +
      "仍恢复对象/图片资产/修补并继续保存",
    async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await proxyProductionAssets(page);
      const source = await makePng(page, 900, 600, "#101010");
      const patch = await makePng(page, 8, 8, "#00ff00");
      const asset = await makePng(page, 16, 12, "#ff0000");
      const assetId = "0123456789abcdef0123456789abcdef";
      const sourceUrl = "https://app.vibeocr/__resource/open-source";
      const patchUrl = "https://app.vibeocr/__resource/open-patch";
      const assetUrl = "https://app.vibeocr/__resource/open-asset";
      const manifestUrl = "https://app.vibeocr/__resource/open-manifest";
      const reads = { patch: 0, asset: 0 };
      await page.route(sourceUrl, (route) =>
        route.fulfill({ contentType: "image/png", body: source }),
      );
      await page.route(patchUrl, (route) => {
        reads.patch++;
        return route.fulfill({ contentType: "image/png", body: patch });
      });
      await page.route(assetUrl, (route) => {
        reads.asset++;
        return route.fulfill({ contentType: "image/png", body: asset });
      });
      const manifest = {
        schemaVersion: 1,
        canvas: { width: 900, height: 600 },
        source: {
          mediaType: "image/png",
          width: 900,
          height: 600,
          length: source.length,
        },
        assets: [{ id: assetId, width: 16, height: 12, length: asset.length }],
        state: {
          rotation: 0,
          marks: [
            {
              id: "mark-1",
              tool: "text",
              start: { x: 120, y: 140 },
              end: { x: 360, y: 200 },
              text: "恢复对象",
              textWidth: 200,
            },
            {
              id: "mark-2",
              tool: "image",
              assetId,
              start: { x: 400, y: 240 },
              end: { x: 560, y: 360 },
            },
          ],
          patches: [{ id: 1, x: 10, y: 12, width: 8, height: 8 }],
        },
      };
      await page.route(manifestUrl, (route) =>
        route.fulfill({
          contentType: "application/json; charset=utf-8",
          body: JSON.stringify(manifest),
        }),
      );
      const uploads: ContributionPayload[] = [];
      await page.route("**/__project", contractProjectRoute(uploads));
      const exports: Buffer[] = [];
      await page.route("**/__annotation", async (route) => {
        exports.push(route.request().postDataBuffer()!);
        await route.fulfill({
          status: 201,
          contentType: "application/json",
          body: JSON.stringify({
            resourceUri:
              "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef",
          }),
        });
      });
      await mountHost(
        page,
        {
          ...snapshot,
          route: "imageEdit",
          capabilities: ["recognition.annotation"],
          features: {
            imageEdit: { isBusy: false, statusCode: "recognition.ready" },
          },
        },
        "https://app.vibeocr/",
      );
      await action(page, "打开工程");
      if (stateFirst) await deferCommand(page, "ackProjectOpen");
      await sendState(page, "imageEdit", {
        isBusy: false,
        statusCode: "recognition.ready",
        projectOpen: {
          pendingId: "c".repeat(32),
          sessionId: "0".repeat(32),
          revision: 0,
          generation: 5,
          manifest: {
            url: manifestUrl,
            mediaType: "application/json; charset=utf-8",
            byteLength: JSON.stringify(manifest).length,
          },
          source: {
            url: sourceUrl,
            mediaType: "image/png",
            byteLength: source.length,
          },
          assets: [
            {
              id: assetId,
              resource: {
                url: assetUrl,
                mediaType: "image/png",
                byteLength: asset.length,
              },
            },
          ],
          patches: [
            {
              id: 1,
              resource: {
                url: patchUrl,
                mediaType: "image/png",
                byteLength: patch.length,
              },
            },
          ],
        },
      });
      await expectCommand(page, {
        scope: "imageEdit",
        action: "ackProjectOpen",
        arguments: {
          pendingId: "c".repeat(32),
          sessionId: "0".repeat(32),
          revision: 0,
          generation: 5,
        },
      });
      if (!stateFirst)
        await expect(
          page.getByText("工程已确认", { exact: false }),
        ).toBeVisible();
      if (leaveDuringAck) {
        await sendState(page, "shell", { route: "recognition" });
        await expect(
          page.getByRole("heading", { name: "单次识别", exact: true }),
        ).toBeVisible();
        await expect(
          page.getByRole("heading", { name: "图片编辑", exact: true }),
        ).toHaveCount(0);
      }
      const restoredSession = "44444444444444444444444444444444";
      await sendState(page, "imageEdit", {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: sourceUrl,
          mediaType: "image/png",
          byteLength: source.length,
        },
        screenshotSession: { sessionId: restoredSession, revision: 0 },
      });
      if (leaveDuringAck) {
        await releaseCommand(page);
        await sendState(page, "shell", { route: "imageEdit" });
      }
      // state-first 不先释放回执：权威提交即可恢复并开始真实编辑。
      await expect(
        page.getByText("工程已恢复", { exact: false }),
      ).toBeVisible();
      await page.screenshot({
        path: test.info().outputPath("project-restored.png"),
      });
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(1);
      expect(uploads[0]!.state.marks).toEqual(manifest.state.marks);
      expect(uploads[0]!.patches).toHaveLength(1);
      await action(page, "保存标注图");
      await expect.poll(() => exports.length).toBe(1);
      const samples = await page.evaluate(async (png) => {
        const bitmap = await createImageBitmap(
          new Blob([Uint8Array.from(atob(png), (char) => char.charCodeAt(0))], {
            type: "image/png",
          }),
        );
        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        const context = canvas.getContext("2d")!;
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        return [
          Array.from(context.getImageData(12, 14, 1, 1).data),
          Array.from(context.getImageData(480, 300, 1, 1).data),
        ];
      }, exports[0]!.toString("base64"));
      expect(samples).toEqual([
        [0, 255, 0, 255],
        [255, 0, 0, 255],
      ]);
      await action(page, "矩形");
      await dragOnCanvas(page, { x: 600, y: 400 }, { x: 800, y: 500 });
      await expectCommand(page, {
        scope: "imageEdit",
        action: "notifyScreenshotRevision",
        arguments: { sessionId: restoredSession, revision: 1 },
      });
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(2);
      expect(uploads[1]!.state.marks).toHaveLength(3);
      expect(uploads[1]!.state.marks.slice(0, 2)).toEqual(manifest.state.marks);
      expect(uploads[1]!.patches).toEqual(uploads[0]!.patches);
      if (stateFirst && !leaveDuringAck) await releaseCommand(page);
      await page.getByRole("button", { name: "撤销", exact: true }).click();
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(3);
      expect(uploads[2]!.state.marks).toEqual(manifest.state.marks);
      expect(uploads[2]!.patches).toEqual(uploads[0]!.patches);
      await page.getByRole("button", { name: "重做", exact: true }).click();
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(4);
      expect(uploads[3]!.state.marks).toEqual(uploads[1]!.state.marks);
      expect(uploads[3]!.patches).toEqual(uploads[0]!.patches);
      expect(reads).toEqual({ patch: 1, asset: 1 });
      // 页面/编辑器卸载通过原有 releaseAssets 归还恢复资产。
      await page.getByRole("link", { name: "单次识别" }).click();
      await expectCommand(page, {
        scope: "imageEdit",
        action: "releaseAssets",
        arguments: { sessionId: restoredSession, resourceUris: [assetUrl] },
      });
      // releaseAssets 早于懒加载页面就绪，先完成导航再回收资源代理。
      await expect(
        page.getByRole("button", { name: "选择图片", exact: true }),
      ).toBeVisible();
    },
  );
}
test("imageEdit: 准备中离开页面不得 ack 切换宿主", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  const base = await makePng(page, 900, 600, "#dddddd");
  const baseUrl = "/leave-base.png";
  const manifestUrl = "/leave-manifest.json";
  const sourceUrl = "https://app.vibeocr/__resource/leave-source";
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  await page.route(sourceUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  // manifest 延迟响应：准备期间用户离开（route 切换卸载页面）。
  await page.route(manifestUrl, async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    await route.fulfill({
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({
        schemaVersion: 1,
        canvas: { width: 900, height: 600 },
      }),
    });
  });
  const state = {
    isBusy: false,
    statusCode: "recognition.session",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId: SESSION, revision: 0 },
  };
  await mountHost(page, {
    ...snapshot,
    route: "imageEdit",
    capabilities: ["recognition.annotation"],
    features: { imageEdit: state },
  });
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  await sendState(page, "imageEdit", {
    ...state,
    projectOpen: {
      pendingId: "f".repeat(32),
      sessionId: SESSION,
      revision: 0,
      generation: 2,
      manifest: {
        url: manifestUrl,
        mediaType: "application/json; charset=utf-8",
        byteLength: 64,
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
  // 准备期间离开到识别页：卸载清理必须中止在途准备，禁止迟到 ack。
  await page.getByRole("link", { name: "单次识别" }).click();
  await page.waitForTimeout(2600);
  const commands = await page.evaluate(
    () =>
      (
        window as unknown as {
          __testHost: { commands: { scope: string; action: string }[] };
        }
      ).__testHost.commands,
  );
  expect(
    commands.some(
      (command) =>
        command.scope === "imageEdit" && command.action === "ackProjectOpen",
    ),
  ).toBe(false);
});

test("imageEdit: 不支持的工程版本被拒收且旧画布保持", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await proxyProductionAssets(page);
  const base = await makePng(page, 900, 600, "#dddddd");
  const baseUrl = "https://app.vibeocr/__resource/keep-base";
  await page.route(baseUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  const badManifestUrl = "https://app.vibeocr/__resource/bad-manifest";
  const sourceUrl = "https://app.vibeocr/__resource/bad-source";
  await page.route(badManifestUrl, (route) =>
    route.fulfill({
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({ schemaVersion: 2 }),
    }),
  );
  await page.route(sourceUrl, (route) =>
    route.fulfill({ contentType: "image/png", body: base }),
  );
  const state = {
    isBusy: false,
    statusCode: "recognition.session",
    input: { url: baseUrl, mediaType: "image/png", byteLength: base.length },
    screenshotSession: { sessionId: SESSION, revision: 0 },
  };
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      capabilities: ["recognition.annotation"],
      features: { imageEdit: state },
    },
    "https://app.vibeocr/",
  );
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
  await action(page, "矩形");
  await dragOnCanvas(page, { x: 100, y: 100 }, { x: 300, y: 260 });
  await expectCommand(page, {
    scope: "imageEdit",
    action: "notifyScreenshotRevision",
    arguments: { sessionId: SESSION, revision: 1 },
  });

  await sendState(page, "imageEdit", {
    ...state,
    projectOpen: {
      pendingId: "d".repeat(32),
      sessionId: SESSION,
      revision: 1,
      generation: 2,
      manifest: {
        url: badManifestUrl,
        mediaType: "application/json; charset=utf-8",
        byteLength: 18,
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
  await expectCommand(page, {
    scope: "imageEdit",
    action: "rejectProjectOpen",
    arguments: {
      pendingId: "d".repeat(32),
      sessionId: SESSION,
      revision: 1,
    },
  });
  await expect(
    page.getByText("不支持的工程格式版本", { exact: false }),
  ).toBeVisible();
  // 旧画布保持：原会话/对象仍在（无新修订、无安装）。
  await expect(page.getByText("原图 900×600", { exact: false })).toBeVisible();
});

for (const timeout of [false, true]) {
  test(
    "imageEdit: pending " +
      (timeout ? "超时" : "撤销") +
      "明确结束读取且旧对象与撤销历史保留",
    async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await proxyProductionAssets(page);
      const source = await makePng(page, 900, 600, "#dddddd");
      const sourceUrl = "https://app.vibeocr/__resource/timeout-source";
      await page.route(sourceUrl, (route) =>
        route.fulfill({ contentType: "image/png", body: source }),
      );
      const uploads: ContributionPayload[] = [];
      await page.route("**/__project", contractProjectRoute(uploads));
      const state = {
        isBusy: false,
        statusCode: "recognition.session",
        input: {
          url: sourceUrl,
          mediaType: "image/png",
          byteLength: source.length,
        },
        screenshotSession: { sessionId: SESSION, revision: 0 },
      };
      await mountHost(
        page,
        {
          ...snapshot,
          route: "imageEdit",
          capabilities: ["recognition.annotation"],
          features: { imageEdit: state },
        },
        "https://app.vibeocr/",
      );
      await expect(
        page.getByText("原图 900×600", { exact: false }),
      ).toBeVisible();
      await action(page, "矩形");
      await dragOnCanvas(page, { x: 150, y: 150 }, { x: 420, y: 320 });
      await expectCommand(page, {
        scope: "imageEdit",
        action: "notifyScreenshotRevision",
        arguments: { sessionId: SESSION, revision: 1 },
      });
      let releaseManifest!: () => void;
      const manifestGate = new Promise<void>((resolve) => {
        releaseManifest = resolve;
      });
      let manifestStarted = false;
      await page.route(
        "https://app.vibeocr/__resource/timeout-manifest",
        async (route) => {
          manifestStarted = true;
          await manifestGate;
          await route.fulfill({ contentType: "application/json", body: "{}" });
        },
      );
      await sendState(page, "imageEdit", {
        ...state,
        screenshotSession: { sessionId: SESSION, revision: 1 },
        projectOpen: {
          pendingId: "9".repeat(32),
          sessionId: SESSION,
          revision: 1,
          generation: 2,
          manifest: {
            url: "https://app.vibeocr/__resource/timeout-manifest",
            mediaType: "application/json",
            byteLength: 2,
          },
          source: {
            url: sourceUrl,
            mediaType: "image/png",
            byteLength: source.length,
          },
          assets: [],
          patches: [],
        },
      });
      await expect.poll(() => manifestStarted).toBe(true);
      await expect(
        page.getByText("正在读取工程清单与资源", { exact: false }),
      ).toBeVisible();
      // 宿主是30秒计时权威；这里驱动实际到期/取消终态，测试浏览器在途撤销。
      await sendState(page, "imageEdit", {
        ...state,
        screenshotSession: { sessionId: SESSION, revision: 1 },
        statusCode: timeout
          ? "imageEdit.projectOpenTimeout"
          : "imageEdit.projectOpenCancelled",
      });
      await expect(
        page.getByText(timeout ? "工程打开超时；" : "工程打开已取消；", {
          exact: false,
        }),
      ).toBeVisible();
      await expect(
        page.getByText("正在读取工程清单与资源", { exact: false }),
      ).toHaveCount(0);
      releaseManifest();
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(1);
      expect(uploads[0]!.state.marks).toHaveLength(1);
      await page.getByLabel("图片检查画布").focus();
      await page.keyboard.press("Control+z");
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(2);
      expect(uploads[1]!.state.marks).toEqual([]);
      await page.getByLabel("图片检查画布").focus();
      await page.keyboard.press("Control+y");
      await action(page, "保存工程");
      await expect.poll(() => uploads.length).toBe(3);
      expect(uploads[2]!.state.marks).toEqual(uploads[0]!.state.marks);
      const commands = await page.evaluate(
        () =>
          (
            window as unknown as {
              __testHost: { commands: { action: string }[] };
            }
          ).__testHost.commands,
      );
      expect(
        commands.some((command) => command.action === "ackProjectOpen"),
      ).toBe(false);
    },
  );
}
