import { expect, test } from "@playwright/test";
import type { HostCommand } from "../src/bridge/client";
import {
  expectCommand,
  rejectNextCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

for (const size of [
  { width: 600, height: 400 },
  { width: 640, height: 480 },
  { width: 1024, height: 600 },
  { width: 2560, height: 1440 },
]) {
  test(`all routes stay reachable at ${size.width}x${size.height}`, async ({
    page,
  }) => {
    await page.setViewportSize(size);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.addInitScript(() => {
      const events: { type: string; label: string | null }[] = [];
      Object.assign(window, { __navigationEvents: events });
      for (const type of ["pointerdown", "pointerup", "click"]) {
        document.addEventListener(
          type,
          (event) => {
            const link = (event.target as Element | null)?.closest(
              ".navigation-rail a",
            );
            if (!link) return;
            events.push({ type, label: link.getAttribute("aria-label") });
            if (events.length > 8) events.shift();
          },
          true,
        );
      }
    });
    await mountHost(page, {
      ...snapshot,
      capabilities: [...snapshot.capabilities, "settings.floatingToolbar"],
      features: {
        ...snapshot.features,
        settings: {
          floatingToolbar: {
            enabled: true,
            edge: "top",
            autoHide: true,
            visibility: "visible",
            lingerMs: 300,
            theme: "system",
          },
        },
      },
    });
    for (const [route, name] of [
      ["recognition", "单次识别"],
      ["batch", "批量识别"],
      ["qrcode", "二维码与条码"],
      ["pdf", "PDF"],
      ["settings", "设置"],
      ["diagnostics", "关于与诊断"],
    ] as const) {
      try {
        await page.getByRole("link", { name, exact: true }).click();
        await expect(
          page.getByRole("heading", { level: 1, name, exact: false }),
        ).toBeVisible();
      } catch (error) {
        try {
          const evidence = await page.evaluate((expectedRoute) => {
            const host = window as Window & {
              __testHost: { commands: readonly HostCommand[] };
              __navigationEvents: readonly {
                type: string;
                label: string | null;
              }[];
            };
            return {
              hash: location.hash,
              expectedRoute,
              headings: Array.from(document.querySelectorAll("h1"))
                .slice(0, 8)
                .map((heading) => heading.textContent?.trim()),
              currentLinks: Array.from(
                document.querySelectorAll(".navigation-rail a[aria-current]"),
              )
                .slice(0, 8)
                .map((link) => ({
                  label: link.getAttribute("aria-label"),
                  current: link.getAttribute("aria-current"),
                })),
              commands: host.__testHost.commands
                .filter(
                  (command) =>
                    command.scope === "shell" && command.action === "navigate",
                )
                .slice(-8),
              pointerEvents: host.__navigationEvents.slice(-8),
            };
          }, route);
          console.error("[navigation-failure]", JSON.stringify(evidence));
        } catch (diagnosticError) {
          console.error(
            "[navigation-failure] evidence unavailable:",
            String(diagnosticError),
          );
        }
        throw error;
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth),
      ).toBeLessThanOrEqual(size.width);
      const main = page.getByRole("main");
      expect(
        await main.evaluate(
          (element) => element.scrollWidth - element.clientWidth,
        ),
        name,
      ).toBeLessThanOrEqual(1);
      expect(
        await page
          .locator(".workspace")
          .evaluate((element) => element.getBoundingClientRect().width),
      ).toBeLessThanOrEqual(1920);
      if (name === "设置") {
        for (const label of ["收起时间（毫秒）", "工具栏主题"]) {
          const control = page.getByLabel(label, { exact: true });
          await control.scrollIntoViewIfNeeded();
          await expect(control).toBeInViewport();
          await expect(control).toBeEnabled();
        }
        await page.screenshot({
          path: `test-results/toolbar-settings-${size.width}x${size.height}.png`,
          fullPage: true,
        });
      }
      const lastControl = main
        .locator("button:visible, input:visible, a:visible")
        .last();
      if (await lastControl.count()) {
        await lastControl.scrollIntoViewIfNeeded();
        await expect(lastControl).toBeInViewport();
      }
    }
    expect(errors).toEqual([]);
  });
}

test("recognition survives navigation and resize, then exposes result actions", async ({
  page,
}) => {
  await page.setViewportSize({ width: 800, height: 600 });
  await page.route("**/ocr-result.txt", (route) =>
    route.fulfill({ contentType: "text/plain", body: "端到端识别结果" }),
  );
  await mountHost(page, snapshot);
  await page.getByRole("button", { name: "选择图片", exact: true }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "selectImage",
    arguments: {},
  });
  await sendState(page, "recognition", {
    isBusy: true,
    statusCode: "recognition.running",
  });
  await expect(
    page.getByRole("button", { name: "取消", exact: true }),
  ).toBeEnabled();
  await page.getByRole("link", { name: "设置", exact: true }).click();
  await page.setViewportSize({ width: 1920, height: 1080 });
  await sendState(page, "recognition", {
    isBusy: false,
    statusCode: "recognition.completed",
    result: { url: "/ocr-result.txt", mediaType: "text/plain", byteLength: 24 },
  });
  await page.getByRole("link", { name: "单次识别", exact: true }).click();
  await expect(page.getByText("端到端识别结果", { exact: true })).toBeVisible();
  await page.setViewportSize({ width: 640, height: 480 });
  await page.getByRole("button", { name: "复制文本", exact: true }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "copy",
    arguments: { format: "plain" },
  });
  await page.getByRole("button", { name: "导出 Word", exact: true }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "export",
    arguments: { format: "docx" },
  });
  await page.screenshot({
    path: "test-results/recognition-compact.png",
    fullPage: true,
  });
});

test("failed command can be retried and running recognition can be cancelled", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await mountHost(page, snapshot);
  await rejectNextCommand(page);
  await page.getByRole("button", { name: "选择图片", exact: true }).click();
  await expect(page.getByRole("alert")).toBeVisible();
  await page.getByRole("button", { name: "选择图片", exact: true }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await sendState(page, "recognition", {
    isBusy: true,
    statusCode: "recognition.running",
  });
  await page.getByRole("button", { name: "取消", exact: true }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "cancel",
    arguments: {},
  });
  await sendState(page, "recognition", {
    isBusy: false,
    statusCode: "recognition.ready",
  });
  await expect(
    page.getByRole("button", { name: "取消", exact: true }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "选择图片", exact: true }),
  ).toBeEnabled();
});

test("PDF selection, delete and save remain clickable in a compact workspace", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await mountHost(page, snapshot);
  await page.getByRole("link", { name: "PDF", exact: true }).click();
  const pdf = {
    isBusy: false,
    statusCode: "pdf.open",
    pageCount: 2,
    selectedPage: 0,
    selectedPages: [0],
    windowStart: 0,
    pages: [
      { index: 0, statusCode: "pdf.page.done" },
      { index: 1, statusCode: "pdf.page.none" },
    ],
  };
  await sendState(page, "pdf", pdf);
  await page.getByRole("checkbox", { name: "选择第 2 页" }).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "selectPage",
    arguments: { page: 1, selected: true },
  });
  await sendState(page, "pdf", { ...pdf, selectedPages: [0, 1] });
  await expect(
    page.getByRole("checkbox", { name: "选择第 2 页" }),
  ).toBeChecked();
  await page.getByRole("checkbox", { name: "选择第 1 页" }).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "selectPage",
    arguments: { page: 0, selected: false },
  });
  await sendState(page, "pdf", { ...pdf, selectedPage: 1, selectedPages: [1] });
  await page.getByRole("button", { name: "删除选中页" }).click();
  await expectCommand(page, {
    scope: "pdf",
    action: "deletePages",
    arguments: {},
  });
  await sendState(page, "pdf", {
    ...pdf,
    pageCount: 1,
    selectedPages: [0],
    pages: [pdf.pages[0]],
  });
  await expect(page.getByRole("checkbox", { name: "选择第 2 页" })).toHaveCount(
    0,
  );
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expectCommand(page, { scope: "pdf", action: "save", arguments: {} });
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
  await sendState(page, "pdf", {
    ...pdf,
    pageCount: 1,
    selectedPage: 0,
    selectedPages: [0],
    pages: [pdf.pages[0]],
    isBusy: true,
    phase: "write",
    progressCurrent: 1,
    progressTotal: 4,
  });
  await page
    .getByRole("button", { name: "取消 PDF 操作", exact: true })
    .click();
  await expectCommand(page, { scope: "pdf", action: "cancel", arguments: {} });
  expect(
    await page
      .getByRole("main")
      .evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
  await page.screenshot({
    path: "test-results/pdf-compact.png",
    fullPage: true,
  });
});
