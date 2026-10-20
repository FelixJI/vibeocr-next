import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions } from "../app/types";
import { EditorPreferencesPanel } from "./EditorPreferencesPanel";
import { DEFAULT_EDITOR_PREFERENCES } from "./editorPreferences";

// 宿主 Width/Height 为 int：非整数必须在前端拒绝，不发必被宿主拒绝的设置。
const actions: AppActions = {
  run: vi.fn().mockResolvedValue(true),
  navigate: vi.fn(),
  setTheme: vi.fn(),
};

async function openPanel() {
  const user = userEvent.setup();
  render(
    <EditorPreferencesPanel
      value={DEFAULT_EDITOR_PREFERENCES}
      actions={actions}
    />,
  );
  await user.click(screen.getByText("输出预设与工具栏定制"));
  const width = screen.getByLabelText("预设宽度");
  const height = screen.getByLabelText("预设高度");
  const save = screen.getByRole("button", { name: "保存预设与工具栏" });
  return { user, width, height, save };
}

describe("EditorPreferencesPanel 预设尺寸校验", () => {
  it.each(["{time}", "scan-{name", "scan-name}", "{{name}}"])(
    "拒绝非法模板 %s 并允许合法模板",
    async (template) => {
      const { user, save } = await openPanel();
      const input = screen.getByLabelText("预设文件名模板");
      await user.clear(input);
      await user.type(input, template.replaceAll("{", "{{"));
      expect(save).toBeDisabled();
      expect(screen.getByRole("alert")).toHaveTextContent("花括号须配对");
      await user.clear(input);
      await user.type(input, "{{name}-{{date}-{{index}");
      expect(save).toBeEnabled();
    },
  );
  afterEach(() => cleanup());
  it("非整数宽高拒绝保存并提示，整数与 0 自动语义可保存", async () => {
    const { user, width, height, save } = await openPanel();
    await user.type(width, "800");
    await user.type(height, "600");
    expect(screen.queryByRole("alert")).toBeNull();
    expect(save).toBeEnabled();

    await user.clear(width);
    await user.type(width, "800.5");
    expect(screen.getByRole("alert").textContent).toMatch(/整数/);
    expect(save).toBeDisabled();

    await user.clear(width);
    await user.type(width, "800");
    expect(save).toBeEnabled();

    await user.clear(height);
    await user.type(height, "600.5");
    expect(screen.getByRole("alert").textContent).toMatch(/整数/);
    expect(save).toBeDisabled();
    await user.clear(height);
    await user.type(height, "600");
    expect(save).toBeEnabled();
    // 0 自动语义：宽高同时为 0 合法，单侧为 0 拒绝。
    await user.clear(width);
    await user.type(width, "0");
    expect(save).toBeDisabled();
    await user.clear(height);
    await user.type(height, "0");
    expect(save).toBeEnabled();
  });

  it("超限尺寸拒绝保存，恢复支持范围后允许保存", async () => {
    const { user, width, height, save } = await openPanel();
    await user.type(height, "30");
    await user.type(width, "16385");
    expect(width).toHaveValue(16385);
    expect(save).toBeDisabled();
    await user.clear(width);
    await user.type(width, "40");
    expect(save).toBeEnabled();
  });
});
