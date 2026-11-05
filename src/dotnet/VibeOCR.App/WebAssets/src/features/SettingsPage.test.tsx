import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions, AppViewState } from "../app/types";

import { SettingsPage } from "./SettingsPage";

afterEach(cleanup);

// 与 e2e/environment-sources.spec.ts 同源的目录投影：统一推理闭包
// paddleocr-ort-directml 是唯一可安装配方（Paddle ONNX + MinerU 同一
// 闭包，DirectML 安装档 + CPU 同闭包设备切换）。
function buildViewState(
  settings: Readonly<Record<string, unknown>>,
): AppViewState {
  return {
    connected: true,
    revision: 1,
    route: "settings",
    theme: "light",
    capabilities: [
      "settings.recognition-device",
      "settings.selection",
      "runtime.refresh",
    ],
    runtimeLabel: "运行环境",
    features: { settings },
  };
}

function buildActions(): AppActions & { run: ReturnType<typeof vi.fn> } {
  return {
    run: vi.fn().mockResolvedValue(true),
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
}

describe("SettingsPage 识别设置", () => {
  it("应用开关显示宿主状态并发送托盘设置，旧宿主禁用新能力", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const state = {
      ...buildViewState({ startupEnabled: true, minimizeToTray: false }),
      capabilities: ["settings.shell", "settings.tray"],
    };
    const { rerender } = render(
      <SettingsPage viewState={state} actions={actions} />,
    );
    expect(screen.getByRole("switch", { name: "开机自启动" })).toBeChecked();
    await user.click(
      screen.getByRole("switch", { name: "关闭时最小化到托盘" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setMinimizeToTray",
      enabled: true,
    });
    rerender(
      <SettingsPage
        viewState={{
          ...state,
          features: {
            settings: {
              startupEnabled: true,
              minimizeToTray: true,
              shellSettingsError: "保存失败",
            },
          },
        }}
        actions={actions}
      />,
    );
    expect(
      screen.getByRole("switch", { name: "关闭时最小化到托盘" }),
    ).toBeChecked();
    expect(screen.getByRole("alert")).toHaveTextContent("保存失败");
    rerender(
      <SettingsPage
        viewState={{ ...state, capabilities: ["settings.shell"] }}
        actions={actions}
      />,
    );
    expect(
      screen.getByRole("switch", { name: "关闭时最小化到托盘" }),
    ).toBeDisabled();
  });

  it("单一设备遵循宿主投影，显式选择才准备服务", async () => {
    const actions = buildActions();
    const viewState = buildViewState({ recognitionDevice: "cpu" });
    const { rerender } = render(
      <SettingsPage viewState={viewState} actions={actions} />,
    );
    const device = screen.getByLabelText("计算设备");
    expect(device).toHaveValue("cpu");
    expect(actions.run).not.toHaveBeenCalled();
    await userEvent.selectOptions(device, "directml");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setRecognitionDevice",
      device: "directml",
    });
    expect(device).toHaveValue("cpu");
    rerender(
      <SettingsPage
        viewState={buildViewState({ recognitionDevice: "directml" })}
        actions={actions}
      />,
    );
    expect(device).toHaveValue("directml");
    expect(
      screen.queryByRole("button", { name: "查看环境清单" }),
    ).not.toBeInTheDocument();
  });
});

describe("Worker 空闲资源设置", () => {
  it.each([
    [false, false],
    [false, true],
    [true, false],
    [true, true],
  ])(
    "等待空闲设置回执时禁止编辑与重复提交（restore=%s, saved=%s）",
    async (restore, saved) => {
      const user = userEvent.setup();
      const actions = buildActions();
      let complete!: (saved: boolean) => void;
      const receipt = new Promise<boolean>((resolve) => {
        complete = resolve;
      });
      actions.run.mockImplementation((command) =>
        command.type === "settings.setWorkerIdle"
          ? receipt
          : Promise.resolve(true),
      );
      render(
        <SettingsPage
          viewState={buildViewState({
            paddleIdleSeconds: 600,
            mineruIdleSeconds: 600,
          })}
          actions={actions}
        />,
      );
      const paddle = screen.getByRole("spinbutton", {
        name: "PaddleOCR 空闲秒数",
      });
      const mineru = screen.getByRole("spinbutton", {
        name: "本地 MinerU 空闲秒数",
      });
      const save = screen.getByRole("button", { name: "保存空闲设置" });
      const reset = screen.getByRole("button", { name: "恢复十分钟默认值" });
      await user.clear(paddle);
      await user.type(paddle, "0");
      await user.clear(mineru);
      await user.type(mineru, "123");
      act(() => {
        fireEvent.click(restore ? reset : save);
        fireEvent.click(restore ? save : reset);
      });
      expect(
        actions.run.mock.calls.filter(
          ([command]) => command.type === "settings.setWorkerIdle",
        ),
      ).toHaveLength(1);
      for (const control of [paddle, mineru, save, reset])
        expect(control).toBeDisabled();
      await user.type(paddle, "42");
      await user.type(mineru, "84");
      expect(paddle).toHaveValue(0);
      expect(mineru).toHaveValue(123);
      await act(async () => {
        complete(saved);
        await receipt;
      });
      for (const control of [paddle, mineru, save, reset])
        expect(control).toBeEnabled();
      expect(paddle).toHaveValue(restore && saved ? 600 : 0);
      expect(mineru).toHaveValue(restore && saved ? 600 : 123);
    },
  );

  it("同值恢复默认后重置两项草稿，再保存仍为十分钟", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    render(
      <SettingsPage
        viewState={buildViewState({
          paddleIdleSeconds: 600,
          mineruIdleSeconds: 600,
        })}
        actions={actions}
      />,
    );
    const paddle = screen.getByRole("spinbutton", {
      name: "PaddleOCR 空闲秒数",
    });
    const mineru = screen.getByRole("spinbutton", {
      name: "本地 MinerU 空闲秒数",
    });
    for (const input of [paddle, mineru]) {
      await user.clear(input);
      await user.type(input, "0");
    }
    await user.click(screen.getByRole("button", { name: "恢复十分钟默认值" }));
    await waitFor(() => {
      expect(paddle).toHaveValue(600);
      expect(mineru).toHaveValue(600);
    });
    actions.run.mockClear();
    await user.click(screen.getByRole("button", { name: "保存空闲设置" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "settings.setWorkerIdle",
      paddleSeconds: 600,
      mineruSeconds: 600,
    });
  });

  it("恢复默认失败时保留两项未保存草稿", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    actions.run.mockResolvedValue(false);
    render(
      <SettingsPage
        viewState={buildViewState({
          paddleIdleSeconds: 600,
          mineruIdleSeconds: 600,
        })}
        actions={actions}
      />,
    );
    const paddle = screen.getByRole("spinbutton", {
      name: "PaddleOCR 空闲秒数",
    });
    const mineru = screen.getByRole("spinbutton", {
      name: "本地 MinerU 空闲秒数",
    });
    await user.clear(paddle);
    await user.type(paddle, "0");
    await user.clear(mineru);
    await user.type(mineru, "123");
    await user.click(screen.getByRole("button", { name: "恢复十分钟默认值" }));
    expect(paddle).toHaveValue(0);
    expect(mineru).toHaveValue(123);
    await user.click(screen.getByRole("button", { name: "保存空闲设置" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "settings.setWorkerIdle",
      paddleSeconds: 0,
      mineruSeconds: 123,
    });
  });

  it("远程模式恢复仅重置 Paddle 草稿并保留本地 MinerU 策略", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    render(
      <SettingsPage
        viewState={buildViewState({
          paddleIdleSeconds: 600,
          mineruIdleSeconds: 0,
          mineruConnection: {
            supported: true,
            mode: "remote",
            apiUrl: "https://example.test",
            hasApiKey: false,
          },
        })}
        actions={actions}
      />,
    );
    const paddle = screen.getByRole("spinbutton", {
      name: "PaddleOCR 空闲秒数",
    });
    await user.clear(paddle);
    await user.type(paddle, "0");
    await user.click(screen.getByRole("button", { name: "恢复十分钟默认值" }));
    await waitFor(() => expect(paddle).toHaveValue(600));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "settings.setWorkerIdle",
      paddleSeconds: 600,
      mineruSeconds: 0,
    });
    actions.run.mockClear();
    await user.click(screen.getByRole("button", { name: "保存空闲设置" }));
    expect(actions.run).toHaveBeenLastCalledWith({
      type: "settings.setWorkerIdle",
      paddleSeconds: 600,
      mineruSeconds: 0,
    });
  });
  it("提交整数秒、持久与恢复十分钟默认值", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    render(
      <SettingsPage
        viewState={buildViewState({
          paddleIdleSeconds: 600,
          mineruIdleSeconds: 600,
        })}
        actions={actions}
      />,
    );
    const paddle = screen.getByRole("spinbutton", {
      name: "PaddleOCR 空闲秒数",
    });
    await user.clear(paddle);
    await user.type(paddle, "0");
    await user.click(screen.getByRole("button", { name: "保存空闲设置" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setWorkerIdle",
      paddleSeconds: 0,
      mineruSeconds: 600,
    });
    await user.click(screen.getByRole("button", { name: "恢复十分钟默认值" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setWorkerIdle",
      paddleSeconds: 600,
      mineruSeconds: 600,
    });
  });
  it("拒绝负数和小数，远程 MinerU 不展示本地超时控件", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    render(
      <SettingsPage
        viewState={buildViewState({
          paddleIdleSeconds: 120,
          mineruIdleSeconds: 0,
          mineruConnection: {
            supported: true,
            mode: "remote",
            apiUrl: "https://example.test",
            hasApiKey: false,
          },
        })}
        actions={actions}
      />,
    );
    expect(
      screen.queryByRole("spinbutton", { name: "本地 MinerU 空闲秒数" }),
    ).not.toBeInTheDocument();
    const paddle = screen.getByRole("spinbutton", {
      name: "PaddleOCR 空闲秒数",
    });
    await user.clear(paddle);
    await user.type(paddle, "-1");
    expect(screen.getByRole("button", { name: "保存空闲设置" })).toBeDisabled();
    await user.clear(paddle);
    await user.type(paddle, "1.5");
    expect(screen.getByRole("button", { name: "保存空闲设置" })).toBeDisabled();
  });
});
