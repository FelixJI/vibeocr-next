import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: true,
  retries: 0,
  workers: 2,
  reporter: "line",
  expect: {
    toHaveScreenshot: { animations: "disabled", maxDiffPixelRatio: 0.002 },
  },
  use: {
    baseURL: "http://127.0.0.1:4174",
    // 视觉快照必须可复现：系统 Edge 会在开发机与 runner 之间自动分叉
    // （基线由 Edge 155 生成、CI runner 是 Edge 154，文字光栅化差异导致
    // 快照 13% 假差异），因此使用随 npm 锁定版本的 bundled Chromium。
    colorScheme: "light",
    reducedMotion: "reduce",
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  webServer: {
    // e2e 运行预构建生产资产；构建在 npm script 层完成（fail-closed），
    // webServer 30s 预算只覆盖静态 preview 服务就绪，不掩盖构建/启动故障。
    command: "npm run preview -- --host 127.0.0.1 --port 4174 --strictPort",
    url: "http://127.0.0.1:4174",
    reuseExistingServer: false,
    timeout: 30_000,
  },
});
