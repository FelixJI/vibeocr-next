import { expect, test, type Page } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

// AC5 的几何证据：动作区定宽后，同一设置页里“有恢复默认”与“无恢复默认”
// 的快捷操作行，应用/清空/禁用按钮列起点必须一致；窄窗口下动作簇换整行仍不
// 溢出。这里测的是 CSS 布局几何与真实键盘录入流程，不替代真实窗口的 DPI
// 验收。
const settingsSnapshot = {
  ...snapshot,
  route: "settings" as const,
  capabilities: ["settings.hotkeys"],
  features: {
    settings: {
      isBusy: false,
      statusCode: "settings.ready",
      backend: "cpu",
      pendingBackend: "cpu",
      sources: [],
      features: [],
      hotkeyActions: [
        {
          actionId: "screenshot_recognize",
          displayName: "截图识别",
          configuredHotkey: "Ctrl+Alt+Q",
          registeredHotkey: "Ctrl+Alt+Q",
          error: null,
          defaultHotkey: "Ctrl+Alt+Q",
        },
        {
          actionId: "clipboard_recognize",
          displayName: "剪贴板识别",
          configuredHotkey: null,
          registeredHotkey: null,
          error: null,
          defaultHotkey: null,
        },
      ],
    },
  },
};

interface RowGeometry {
  buttonLefts: number[];
  buttonTops: number[];
  clusterRightOverhang: number;
  rowScrollOverflow: number;
}

async function measureRows(page: Page): Promise<RowGeometry[]> {
  return page.evaluate(() => {
    const rows = [
      ...document.querySelectorAll<HTMLElement>(".hotkey-edit-row"),
    ];
    return rows.map((row) => {
      const buttons = [
        ...row.querySelectorAll<HTMLElement>(".setting-actions button"),
      ];
      const rowBox = row.getBoundingClientRect();
      const cluster = row.querySelector<HTMLElement>(".setting-actions")!;
      const clusterBox = cluster.getBoundingClientRect();
      return {
        buttonLefts: buttons.map(
          (button) => button.getBoundingClientRect().x - rowBox.x,
        ),
        buttonTops: buttons.map(
          (button) => button.getBoundingClientRect().y - rowBox.y,
        ),
        clusterRightOverhang: clusterBox.right - rowBox.right,
        rowScrollOverflow: row.scrollWidth - row.clientWidth,
      };
    });
  });
}

test("hotkey rows keep apply, clear and disable columns aligned across rows", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mountHost(page, settingsSnapshot);

  await expect(
    page.getByRole("button", { name: "应用 截图识别" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "清空 截图识别" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "恢复默认 剪贴板识别" }),
  ).toHaveCount(0);

  const rows = await measureRows(page);
  expect(rows).toHaveLength(2);
  const [withDefault, withoutDefault] = rows;
  expect(withDefault.buttonLefts).toHaveLength(4);
  expect(withoutDefault.buttonLefts).toHaveLength(3);
  // 两行的应用/清空/禁用按钮列起点一致（亚像素容差 1px）。
  for (const column of [0, 1, 2]) {
    expect(
      Math.abs(
        withDefault.buttonLefts[column]! - withoutDefault.buttonLefts[column]!,
      ),
    ).toBeLessThanOrEqual(1);
  }
  // 同一行内四个按钮同一基线：动作簇不换行、不叠加。
  expect(new Set(withDefault.buttonTops).size).toBe(1);
  expect(withDefault.buttonLefts[3]).toBeGreaterThan(
    withDefault.buttonLefts[2]!,
  );
  // 动作簇完整落在行内，行自身无横向滚动。
  for (const row of rows) {
    expect(row.clusterRightOverhang).toBeLessThanOrEqual(0.5);
    expect(row.rowScrollOverflow).toBeLessThanOrEqual(1);
  }

  const main = page.getByRole("main");
  expect(
    await main.evaluate((element) => element.scrollWidth - element.clientWidth),
  ).toBeLessThanOrEqual(1);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    ),
  ).toBeLessThanOrEqual(0);
});

test("hotkey rows stay single-line and overflow-free at narrow widths", async ({
  page,
}) => {
  for (const size of [
    { width: 640, height: 480 },
    { width: 600, height: 400 },
  ]) {
    await page.setViewportSize(size);
    await mountHost(page, settingsSnapshot);
    await expect(
      page.getByRole("button", { name: "应用 截图识别" }),
    ).toBeVisible();

    const rows = await measureRows(page);
    expect(rows).toHaveLength(2);
    for (const row of rows) {
      // 窄窗口下动作簇整行呈现：按钮同一行、不越出行、不产生横向滚动。
      expect(new Set(row.buttonTops).size).toBe(1);
      expect(row.clusterRightOverhang).toBeLessThanOrEqual(0.5);
      expect(row.rowScrollOverflow).toBeLessThanOrEqual(1);
    }
    const main = page.getByRole("main");
    expect(
      await main.evaluate(
        (element) => element.scrollWidth - element.clientWidth,
      ),
      `${size.width}x${size.height}`,
    ).toBeLessThanOrEqual(1);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - window.innerWidth,
      ),
      `${size.width}x${size.height}`,
    ).toBeLessThanOrEqual(0);
  }
});

// 录入框是只读回显，不能用 fill 写入：必须按真实控件流程用键盘录入，
// 并验证宿主录制会话开合（begin/end 同一 recordingId）与仅修饰键草稿
// 不能应用。
test("records a hotkey through the readonly input with real keyboard flow", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await mountHost(page, settingsSnapshot);

  const input = page.getByRole("textbox", { name: "剪贴板识别新快捷键" });
  expect(
    await input.evaluate((element) => (element as HTMLInputElement).readOnly),
  ).toBe(true);
  const apply = page.getByRole("button", { name: "应用 剪贴板识别" });
  await expect(apply).toBeDisabled();

  // 聚焦即开始宿主挂起会话；确认后进入捕获。
  await input.click();
  await expect(page.getByText("请按下新的组合键，按 Esc 取消")).toBeVisible();

  // 仅修饰键草稿保持“应用”禁用。
  await page.keyboard.down("Control");
  await expect(input).toHaveValue("Ctrl");
  await expect(apply).toBeDisabled();
  await page.keyboard.down("Alt");
  await expect(input).toHaveValue("Ctrl+Alt");
  await expect(apply).toBeDisabled();

  // 完整组合在修饰键释放后仍保持。
  await page.keyboard.press("c");
  await expect(input).toHaveValue("Ctrl+Alt+C");
  await page.keyboard.up("Control");
  await page.keyboard.up("Alt");
  await expect(input).toHaveValue("Ctrl+Alt+C");
  await expect(apply).toBeEnabled();

  // 移焦结束录制并应用：begin/end 使用同一 recordingId。
  await apply.click();
  await expectCommand(page, {
    scope: "settings",
    action: "setActionHotkey",
    arguments: { actionId: "clipboard_recognize", hotkey: "Ctrl+Alt+C" },
  });
  const recording = await page.evaluate(() => {
    const commands = (
      window as unknown as {
        __testHost: {
          commands: readonly {
            action: string;
            arguments: { recordingId?: string };
          }[];
        };
      }
    ).__testHost.commands;
    const begin = commands.find(
      (command) => command.action === "beginHotkeyRecording",
    );
    const end = commands.find(
      (command) => command.action === "endHotkeyRecording",
    );
    return {
      begin: begin?.arguments.recordingId,
      end: end?.arguments.recordingId,
    };
  });
  expect(recording.begin).toMatch(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  );
  expect(recording.end).toBe(recording.begin);

  // 清空回到空草稿，应用重新禁用；恢复默认仍走宿主重置命令。
  await page.getByRole("button", { name: "清空 剪贴板识别" }).click();
  await expect(input).toHaveValue("");
  await expect(apply).toBeDisabled();
  await page.getByRole("button", { name: "恢复默认 截图识别" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "resetActionHotkey",
    arguments: { actionId: "screenshot_recognize" },
  });
});
