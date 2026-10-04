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
