import { expect, test } from "@playwright/test";

import { mountHost, snapshot } from "./workbench-host";

test("Windows socket workaround preserves the locked Playwright feature defaults", async ({
  playwright,
}, testInfo) => {
  const configuredArgs = testInfo.project.use.launchOptions?.args ?? [];
  if (process.platform !== "win32") {
    expect(configuredArgs).toEqual([]);
    return;
  }
  const disabledFeatures: string[][] = [];
  for (const args of [[], configuredArgs]) {
    const browser = await playwright.chromium.launch({
      args: ["--enable-automation", ...args],
    });
    try {
      const session = await browser.newBrowserCDPSession();
      const commandLine = await session.send("Browser.getBrowserCommandLine");
      const featureArg = commandLine.arguments
        .filter((arg) => arg.startsWith("--disable-features="))
        .at(-1);
      expect(featureArg).toBeDefined();
      disabledFeatures.push(featureArg!.split("=")[1]!.split(","));
    } finally {
      await browser.close();
    }
  }
  expect(new Set(disabledFeatures[1])).toEqual(
    new Set([...disabledFeatures[0]!, "TcpPortRandomizationWin"]),
  );
});

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

  const requestListeners = page.listenerCount("request");
  const errorListeners = page.listenerCount("pageerror");
  await mountHost(page, snapshot);
  expect(page.listenerCount("request")).toBe(requestListeners);
  expect(page.listenerCount("pageerror")).toBe(errorListeners);

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
  const reports: string[] = [];
  const reportError = console.error;
  const requestListeners = page.listenerCount("request");
  const errorListeners = page.listenerCount("pageerror");
  console.error = (...args: unknown[]) =>
    reports.push(args.map(String).join(" "));
  try {
    await expect(
      mountHost(page, { ...snapshot, route: "pdf" }, "/#/pdf"),
    ).rejects.toThrow("toBeVisible");
  } finally {
    console.error = reportError;
  }
  expect(reports).toHaveLength(1);
  expect(reports[0]).toContain("[mountHost]");
  expect(reports[0]).toContain("页面加载失败");
  expect(reports[0]).toContain("PdfPage-");
  expect(reports[0]).toContain("net::ERR_FAILED");
  expect(page.listenerCount("request")).toBe(requestListeners);
  expect(page.listenerCount("pageerror")).toBe(errorListeners);
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

test("startup diagnostics remain bounded when DOM evaluation never settles", async ({
  page,
}) => {
  test.setTimeout(5000);
  const failure = new Error("controlled navigation failure");
  const goto = page.goto;
  const evaluate = page.evaluate;
  const reportError = console.error;
  const reports: string[] = [];
  const requestListeners = page.listenerCount("request");
  const errorListeners = page.listenerCount("pageerror");
  page.goto = async (url, options) => {
    await goto.call(page, url, options);
    page.emit("pageerror", new Error("controlled pageerror"));
    throw failure;
  };
  page.evaluate = () => new Promise<never>(() => {});
  console.error = (...args: unknown[]) =>
    reports.push(args.map(String).join(" "));
  try {
    await expect(mountHost(page, snapshot)).rejects.toBe(failure);
  } finally {
    page.goto = goto;
    page.evaluate = evaluate;
    console.error = reportError;
  }
  expect(reports).toHaveLength(1);
  expect(reports[0]).toContain("DOM snapshot timed out after 1000ms");
  expect(reports[0]).toContain("controlled pageerror");
  expect(reports[0]).toContain("/assets/");
  expect(page.listenerCount("request")).toBe(requestListeners);
  expect(page.listenerCount("pageerror")).toBe(errorListeners);
});

test("main workbench warms tabs silently and switches without a loading placeholder", async ({
  page,
}) => {
  const loaded = new Set<string>();
  page.on("requestfinished", (request) => {
    const match =
      /\/assets\/(RecognitionPage|ImageEditPage|ImageConversionPage|BatchPage|PdfPage|QrCodePage|SettingsPage|DiagnosticsPage)-/.exec(
        request.url(),
      );
    if (match) loaded.add(match[1]!);
  });
  await mountHost(page, snapshot);
  await expect.poll(() => loaded.size).toBe(8);
  await page.evaluate(
    () => new Promise<void>((resolve) => requestIdleCallback(() => resolve())),
  );
  await expect(
    page.getByRole("heading", { level: 1, name: "PDF 工作台" }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("alert").filter({ hasText: "页面加载失败" }),
  ).toHaveCount(0);
  await page.evaluate(() => {
    document.documentElement.dataset.loadingFlash = "false";
    const observer = new MutationObserver((records) => {
      if (
        records.some((record) =>
          [...record.addedNodes].some(
            (node) =>
              node instanceof Element &&
              (node.matches(".page-loading") ||
                node.querySelector(".page-loading")),
          ),
        )
      )
        document.documentElement.dataset.loadingFlash = "true";
    });
    observer.observe(document.body, { childList: true, subtree: true });
  });
  await page.getByRole("link", { name: "PDF", exact: true }).click();
  await expect(
    page.getByRole("heading", { level: 1, name: "PDF 工作台" }),
  ).toBeVisible();
  expect(await page.locator("html").getAttribute("data-loading-flash")).toBe(
    "false",
  );
});

test("background preload errors stay silent until that tab is opened", async ({
  page,
}) => {
  let attempts = 0;
  await page.route("**/assets/PdfPage-*.js", (route) => {
    attempts++;
    return route.abort("failed");
  });
  await mountHost(page, snapshot);
  await expect.poll(() => attempts).toBe(1);
  await expect(
    page.getByRole("alert").filter({ hasText: "页面加载失败" }),
  ).toHaveCount(0);
  await page.getByRole("link", { name: "PDF", exact: true }).click();
  await expect(
    page.getByRole("alert").filter({ hasText: "页面加载失败" }),
  ).toBeVisible();
  expect(attempts).toBe(1);
});

test("cold tab shows a responsive skeleton instead of plain loading text", async ({
  page,
}, testInfo) => {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/assets/PdfPage-*.js", async (route) => {
    await gate;
    await route.continue();
  });
  try {
    await page.setViewportSize({ width: 640, height: 480 });
    await mountHost(page, { ...snapshot, route: "pdf" }, "/#/pdf", false);
    const skeleton = page.getByRole("status", { name: "页面加载中" });
    await expect(skeleton).toBeVisible();
    await expect(skeleton).toHaveAttribute("aria-busy", "true");
    await expect(page.getByText("正在加载页面…", { exact: true })).toHaveCount(
      0,
    );
    const box = await skeleton.boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(640);
    await page.screenshot({
      path: testInfo.outputPath("page-loading-skeleton.png"),
    });
  } finally {
    release();
  }
  await expect(
    page.getByRole("heading", { level: 1, name: "PDF 工作台" }),
  ).toBeVisible();
  await expect(page.getByRole("status", { name: "页面加载中" })).toHaveCount(0);
});
