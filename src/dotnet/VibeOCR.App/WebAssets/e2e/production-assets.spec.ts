import { expect, test } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

/**
 * 生产资产契约：workbench e2e 必须运行在预构建产物上，不得依赖 dev server
 * 的按需源码 transform（冷启动时该成本会占用用例导航预算，慢 runner 上导致
 * 首批用例超时）。断言：无 /src/**、/@vite/client、.vite/deps 请求，且模块
 * 入口均为 assets/*.js bundle。
 */
test("workbench e2e loads prebuilt production assets without on-demand source transforms", async ({
  page,
}) => {
  const baseURL = test.info().project.use.baseURL ?? "http://127.0.0.1:4174";
  const origin = new URL(baseURL).origin;
  const sameOriginPaths: string[] = [];
  page.on("request", (request) => {
    const url = new URL(request.url());
    if (url.origin === origin) sameOriginPaths.push(url.pathname);
  });

  await mountHost(page, snapshot);

  const transformed = sameOriginPaths.filter(
    (path) =>
      path.startsWith("/src/") ||
      path.startsWith("/@vite/") ||
      path.startsWith("/node_modules/.vite/deps/"),
  );
  expect(transformed, "on-demand source transform requests").toEqual([]);

  const moduleScriptPaths = await page.evaluate(() =>
    Array.from(
      document.querySelectorAll<HTMLScriptElement>('script[type="module"]'),
    ).map((script) => new URL(script.src, location.href).pathname),
  );
  expect(moduleScriptPaths.length).toBeGreaterThan(0);
  for (const path of moduleScriptPaths) {
    expect(path).toMatch(/^\/assets\/[^/]+\.js$/);
  }
});

test("image edit scene only requests its production feature closure", async ({
  page,
}) => {
  const scripts = new Map<string, number>();
  const bodies: Promise<void>[] = [];
  page.on("response", (response) => {
    const path = new URL(response.url()).pathname;
    if (/^\/assets\/[^/]+\.js$/.test(path))
      bodies.push(
        response.body().then((body) => {
          scripts.set(path, body.length);
        }),
      );
  });
  await page.route("**/production-edit.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="white"/></svg>',
    }),
  );
  await mountHost(
    page,
    {
      ...snapshot,
      route: "imageEdit",
      features: {
        recognition: {
          isBusy: false,
          statusCode: "recognition.session",
          input: {
            url: "/production-edit.svg",
            mediaType: "image/svg+xml",
            byteLength: 140,
          },
        },
      },
    },
    "/#/imageEdit?scene=1",
  );
  await Promise.all(bodies);
  expect(
    [...scripts.keys()].filter((path) =>
      /PdfPage|SettingsPage|BatchPage|TaskEngineSelection|result-math/.test(
        path,
      ),
    ),
  ).toEqual([]);
  expect(
    [...scripts.keys()].some((path) => path.includes("ImageEditPage")),
  ).toBe(true);
  await test.info().attach("image-edit-production-requests.json", {
    body: JSON.stringify({
      scripts: Object.fromEntries(scripts),
      decodedBytes: [...scripts.values()].reduce(
        (sum, bytes) => sum + bytes,
        0,
      ),
    }),
    contentType: "application/json",
  });
});

test("failed production feature chunk recovers only after explicit document reload", async ({
  page,
}) => {
  let attempts = 0;
  let blocked = true;
  await page.route("**/assets/PdfPage-*.js", (route) => {
    attempts++;
    return blocked ? route.abort("failed") : route.continue();
  });
  let documentRequests = 0;
  page.on("request", (request) => {
    if (request.isNavigationRequest()) documentRequests++;
  });
  await mountHost(page, { ...snapshot, route: "pdf" }, "/#/pdf", false);
  await expect(
    page.getByRole("alert").filter({ hasText: "页面加载失败" }),
  ).toBeVisible();
  expect(attempts).toBe(1);
  blocked = false;
  await page.getByRole("button", { name: "保留草稿并重新加载页面" }).click();
  await test.info().attach("chunk-retry.json", {
    body: JSON.stringify({
      initialRequests: 1,
      requestsAfterRetry: attempts,
    }),
    contentType: "application/json",
  });
  await expect.poll(() => attempts).toBe(2);
  await expect(
    page.getByRole("heading", { level: 1, name: "PDF 工作台" }),
  ).toBeVisible();
  expect(attempts).toBe(2);
  expect(documentRequests).toBe(2);
  expect(
    await page.evaluate(
      () =>
        (window as Window & { __testHost: { commands: unknown[] } }).__testHost
          .commands,
    ),
  ).toEqual([]);
  expect(await page.evaluate(() => location.hash)).toContain("/pdf");
});
