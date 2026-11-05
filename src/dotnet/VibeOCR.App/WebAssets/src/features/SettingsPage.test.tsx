import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppActions, AppViewState } from "../app/types";

import { SettingsPage } from "./SettingsPage";

afterEach(cleanup);

// 与 e2e/environment-sources.spec.ts 同源的目录投影：统一推理闭包
// paddleocr-ort-directml 是唯一可安装配方（Paddle ONNX + MinerU 同一
// 闭包，DirectML 安装档 + CPU 同闭包设备切换）。
const recipes = [
  {
    id: "paddleocr-ort-directml",
    engine: "paddle",
    family: "paddle-onnx",
    familyDisplayName: "PaddleOCR ONNX + MinerU",
    provider: "DmlExecutionProvider",
    defaultRemoteHost: true,
    displayName: "PaddleOCR ONNX + MinerU · Windows DirectML",
    configuredRecognitionTypes: ["text", "table", "structure", "document"],
    accelerator: "cpu",
    targetDevice: "directml",
    supportedDevices: ["directml", "cpu"],
    pythonVersion: "3.13",
    dependencies: ["paddleocr==3.7.0", "onnxruntime-directml==1.24.4"],
    dependencyOrigin: "bundled_pack",
  },
];

const sources = [
  {
    kind: "paddleocr_model_registry",
    id: "paddleocr-modelscope",
    provider: "modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
    isDefault: true,
  },
  {
    kind: "paddleocr_model_registry",
    id: "paddleocr-huggingface",
    provider: "huggingface",
    displayName: "Hugging Face",
    endpoint: "https://huggingface.co",
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-modelscope",
    provider: "modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
    isDefault: true,
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-huggingface",
    provider: "huggingface",
    displayName: "Hugging Face",
    endpoint: "https://huggingface.co",
  },
];

const mineruEnvironments = [
  {
    id: "mineru-active",
    name: "PaddleOCR ONNX + MinerU · Windows DirectML",
    revision: 3,
    kind: "venv",
    status: "installed",
    pythonState: "ready",
    dependencyState: "installed",
    engineState: "ready",
    modelState: "ready",
    serviceState: "ready",
    configuredRecognitionTypes: ["text", "table", "structure", "document"],
    targetDevice: "directml",
  },
  {
    id: "mineru-old",
    name: "统一推理闭包（CPU 档）",
    revision: 1,
    kind: "venv",
    status: "installed",
    pythonState: "ready",
    dependencyState: "installed",
    engineState: "unverified",
    modelState: "not_checked",
    serviceState: "not_started",
    configuredRecognitionTypes: ["text", "table", "structure", "document"],
    targetDevice: "cpu",
  },
];

function buildViewState(
  settings: Readonly<Record<string, unknown>>,
): AppViewState {
  return {
    connected: true,
    revision: 1,
    route: "settings",
    theme: "light",
    capabilities: ["runtime.environments"],
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

describe("SettingsPage 环境管理（#283 分区方向）", () => {
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

  it("设备选择随宿主快照回显，切换直接发送 switchEnvironment", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [
        {
          id: "shared",
          name: "PaddleOCR ONNX + MinerU · Windows DirectML",
          revision: 3,
          kind: "venv",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "ready",
          configuredRecognitionTypes: [
            "text",
            "table",
            "structure",
            "document",
          ],
          targetDevice: "directml",
        },
      ],
      activeEnvironmentId: "shared",
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // Paddle 分区：单一设备选择（统一闭包 = DirectML + CPU 切换），
    // 当前设备来自活动环境快照，不再有“识别组件/加速方式”双下拉。
    const backend = screen.getByLabelText("Paddle 推理后端");
    expect(backend).toHaveValue("directml");
    expect(within(backend).getAllByRole("option")).toHaveLength(2);
    expect(screen.queryByLabelText("识别组件")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("加速方式")).not.toBeInTheDocument();
    // 目录展示保留 display name / 用途 / 设备，依赖如实标注随包离线。
    expect(
      screen.getAllByText(/依赖随包提供，可离线安装/).length,
    ).toBeGreaterThan(0);
    expect(
      screen.getByText(/文字、表格与版面结构识别共用同一套 PaddleOCR 依赖/),
    ).toBeVisible();
    // 预构建运行时树：兼容查询/准备/待确认计划入口全部退役，挂载不发命令。
    expect(actions.run).not.toHaveBeenCalled();
    expect(
      screen.queryByRole("button", { name: "查询可复用环境" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "准备此后端" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    // 同闭包设备切换：switchEnvironment 携带用户选定设备。
    await user.selectOptions(backend, "cpu");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.switchEnvironment",
      environmentId: "shared",
      device: "cpu",
    });
  });

  it("统一推理闭包目录下 MinerU 分区复用共享闭包并按同一活动环境切换设备", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    // 统一闭包形态：目录只剩 paddleocr-ort-directml，document 能力随
    // 共享闭包声明；MinerU 分区必须识别该闭包而不是提示没有可用配方。
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [
        {
          id: "unified-installed",
          name: "统一推理闭包",
          revision: 1,
          kind: "venv",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "ready",
          configuredRecognitionTypes: [
            "text",
            "table",
            "structure",
            "document",
          ],
          targetDevice: "directml",
        },
      ],
      activeEnvironmentId: "unified-installed",
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    expect(
      within(mineruSection).queryByText(/目录尚未同步或没有可用配方/),
    ).not.toBeInTheDocument();
    expect(
      within(mineruSection).getByText(/本地文档解析由统一推理闭包随包携带/),
    ).toBeVisible();
    // 设备选择与安装配方分离：统一闭包同闭包支持 directml↔cpu 切换，
    // 设备下拉枚举 supported_devices，切换命令发送用户选定设备。
    const mineruDeviceSelect =
      within(mineruSection).getByLabelText("MinerU 运行设备");
    expect(mineruDeviceSelect).toHaveValue("directml");
    expect(within(mineruDeviceSelect).getAllByRole("option")).toHaveLength(2);
    expect(
      within(mineruSection).queryByRole("button", {
        name: "安装 MinerU 本地引擎",
      }),
    ).not.toBeInTheDocument();
    await user.selectOptions(mineruDeviceSelect, "cpu");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.switchEnvironment",
      environmentId: "unified-installed",
      device: "cpu",
    });
    expect(
      within(mineruSection).getByText(/切换设备会重新启动并验证识别服务/),
    ).toBeVisible();
  });

  it("MinerU 本地环境列出安装状态：活动环境引导先切换，非活动可确认移除", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: mineruEnvironments,
      activeEnvironmentId: "mineru-active",
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    const activeRow = within(mineruSection)
      .getByText("PaddleOCR ONNX + MinerU · Windows DirectML")
      .closest("div") as HTMLElement;
    expect(activeRow).not.toBeNull();
    // 活动环境不提供移除按钮，只引导先切换到其他环境。
    expect(
      within(activeRow).queryByRole("button", { name: "移除环境" }),
    ).not.toBeInTheDocument();
    expect(
      within(activeRow).getByText(/当前使用中：请先切换到其他环境后再移除/),
    ).toBeVisible();
    // 非活动环境走既有安全删除路径：两步确认后才发命令。
    const oldRow = within(mineruSection)
      .getByText("统一推理闭包（CPU 档）")
      .closest("div") as HTMLElement;
    await user.click(within(oldRow).getByRole("button", { name: "移除环境" }));
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.deleteEnvironment",
      environmentId: "mineru-old",
    });
    await user.click(within(oldRow).getByRole("button", { name: "确认移除" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.deleteEnvironment",
      environmentId: "mineru-old",
    });
    // 移除语义如实说明：共享缓存与模型受保护保留。
    expect(
      within(mineruSection).getByText(/共享模型缓存、下载缓存与基础/),
    ).toBeVisible();
  });

  it("环境清单与来源直接呈现在常规页面，不再有折叠高级区", () => {
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: mineruEnvironments,
      activeEnvironmentId: "mineru-active",
      environmentSupportsCleanup: false,
      environmentBusy: false,
      maintenanceStatus: "旧运行时维护投影不得混入 managed 页面",
    };
    render(
      <SettingsPage
        viewState={buildViewState(settings)}
        actions={buildActions()}
      />,
    );
    // #294：环境清单/来源/清理不再藏进折叠高级区，无需任何展开即可见。
    expect(
      document.querySelector("details.environment-advanced-maintenance"),
    ).toBeNull();
    expect(screen.getByLabelText("已安装环境")).toBeVisible();
    expect(screen.getByLabelText("下载来源")).toBeVisible();
    expect(
      screen.queryByText("高级：环境清单、依赖版本、下载来源与空间清理"),
    ).not.toBeInTheDocument();
    // managed 分支不渲染旧 RuntimeStatus 维护投影，避免旧健康/idle 被当成
    // 环境维护结果（legacy capability 分支保留）。
    expect(
      screen.queryByText("本次维护：旧运行时维护投影不得混入 managed 页面"),
    ).not.toBeInTheDocument();
    // 安装进度不属于设置项：空闲时自身不渲染。
    expect(screen.queryByLabelText("依赖安装进度")).not.toBeInTheDocument();
  });
  it("首次进入 managed 空投影自动只读加载清单且只发一次", async () => {
    const actions = buildActions();
    const initial = {
      environmentStatus: "【文案改版】宿主状态行可以是任何文本",
      environmentSnapshotLoaded: false,
      environmentListFetchAttempted: false,
      environments: [],
      environmentRecipes: [],
      environmentBusy: false,
    };
    const { rerender } = render(
      <SettingsPage viewState={buildViewState(initial)} actions={actions} />,
    );
    // 实机初始态：快照/目录未到，不误报“已同步但未启动”。
    expect(screen.getByText("运行环境状态尚未读取")).toBeInTheDocument();
    // 普通路径无需手动“查看环境清单”：自动发一次只读刷新。
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.refreshEnvironmentList",
      }),
    );
    expect(
      actions.run.mock.calls.filter(
        ([command]) =>
          (command as { type: string }).type ===
          "settings.refreshEnvironmentList",
      ),
    ).toHaveLength(1);
    // 真实快照到达：不再重复刷新；预构建运行时树下目录到达也不再触发
    // 兼容查询/准备等安装面命令。
    rerender(
      <SettingsPage
        viewState={buildViewState({
          ...initial,
          environmentStatus: "运行环境状态已更新",
          environmentSnapshotLoaded: true,
          environmentRecipes: recipes,
        })}
        actions={actions}
      />,
    );
    rerender(
      <SettingsPage
        viewState={buildViewState({
          ...initial,
          environmentStatus: "运行环境状态已更新",
          environmentSnapshotLoaded: true,
          environmentRecipes: recipes,
        })}
        actions={actions}
      />,
    );
    expect(
      actions.run.mock.calls.filter(
        ([command]) =>
          (command as { type: string }).type ===
          "settings.refreshEnvironmentList",
      ),
    ).toHaveLength(1);
    expect(
      actions.run.mock.calls.filter(
        ([command]) =>
          (command as { type: string }).type !==
          "settings.refreshEnvironmentList",
      ),
    ).toHaveLength(0);
  });

  it("初始 busy 等待解除后只发一次且失败不自动重试", async () => {
    const actions = buildActions();
    const initial = {
      environmentStatus: "启动初始化进行中（任意文案）",
      environmentSnapshotLoaded: false,
      environmentListFetchAttempted: false,
      environments: [],
      environmentRecipes: [],
      environmentBusy: true,
    };
    const { rerender } = render(
      <SettingsPage viewState={buildViewState(initial)} actions={actions} />,
    );
    expect(
      actions.run.mock.calls.filter(
        ([command]) =>
          (command as { type: string }).type ===
          "settings.refreshEnvironmentList",
      ),
    ).toHaveLength(0);
    rerender(
      <SettingsPage
        viewState={buildViewState({ ...initial, environmentBusy: false })}
        actions={actions}
      />,
    );
    await waitFor(() =>
      expect(
        actions.run.mock.calls.filter(
          ([command]) =>
            (command as { type: string }).type ===
            "settings.refreshEnvironmentList",
        ),
      ).toHaveLength(1),
    );
    // 失败终态：错误保留，不再自动重试刷掉它。
    rerender(
      <SettingsPage
        viewState={buildViewState({
          ...initial,
          environmentBusy: false,
          environmentListFetchAttempted: true,
          environmentStatus: "读取运行环境失败：连接超时",
        })}
        actions={actions}
      />,
    );
    expect(
      actions.run.mock.calls.filter(
        ([command]) =>
          (command as { type: string }).type ===
          "settings.refreshEnvironmentList",
      ),
    ).toHaveLength(1);
    expect(screen.getByText(/读取运行环境失败：连接超时/)).toBeInTheDocument();
  });

  it("legacy 主机与已有快照都不自动刷新", () => {
    const legacyActions = buildActions();
    render(
      <SettingsPage
        viewState={{
          ...buildViewState({
            environmentStatus: "启动初始化进行中（任意文案）",
            environmentSnapshotLoaded: false,
            environmentListFetchAttempted: false,
            environments: [],
          }),
          capabilities: ["settings.shell"],
        }}
        actions={legacyActions}
      />,
    );
    expect(legacyActions.run).not.toHaveBeenCalledWith({
      type: "settings.refreshEnvironmentList",
    });

    const actions = buildActions();
    render(
      <SettingsPage
        viewState={buildViewState({
          environmentStatus: "运行环境状态已同步。",
          environmentSnapshotLoaded: true,
          environmentRecipes: recipes,
          environments: [
            { id: "env-1", name: "文档", revision: 1, status: "empty" },
          ],
        })}
        actions={actions}
      />,
    );
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.refreshEnvironmentList",
    });
    // 已有快照不被自动加载触碰；页面静默等待用户操作。
  });

  it("product 环境按宿主 operation_blocks 投影禁用移除入口", () => {
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [
        {
          id: "product",
          name: "产品内置环境",
          revision: 1,
          kind: "venv",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "not_started",
          configuredRecognitionTypes: [
            "text",
            "table",
            "structure",
            "document",
          ],
          targetDevice: "directml",
          operationBlocks: { delete: "product_environment" },
        },
        {
          id: "legacy-env",
          name: "旧版环境",
          revision: 1,
          kind: "legacy",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "not_started",
          configuredRecognitionTypes: [],
        },
        {
          id: "user-env",
          name: "用户环境",
          revision: 2,
          kind: "venv",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "ready",
          configuredRecognitionTypes: ["text"],
          targetDevice: "cpu",
        },
      ],
      activeEnvironmentId: "user-env",
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    const list = screen.getByLabelText("已安装环境");
    // product 环境被宿主投影保护：移除按钮如实呈现但禁用。
    const productRow = within(list)
      .getByText("产品内置环境")
      .closest("div") as HTMLElement;
    expect(
      within(productRow).getByRole("button", { name: "移除环境" }),
    ).toBeDisabled();
    // legacy 环境按既有规则不提供移除入口，只读展示。
    const legacyRow = within(list)
      .getByText("旧版环境")
      .closest("div") as HTMLElement;
    expect(
      within(legacyRow).queryByRole("button", { name: "移除环境" }),
    ).not.toBeInTheDocument();
    expect(actions.run).not.toHaveBeenCalled();
  });
});

it("目录标识变更时按显式元数据展示并沿活动环境切换设备", async () => {
  const actions = buildActions();
  const renamed = recipes.map((recipe) => ({
    ...recipe,
    id: "custom.engine_0",
    displayName: "名称 0",
  }));
  render(
    <SettingsPage
      viewState={buildViewState({
        environmentRecipes: renamed,
        environments: [
          {
            id: "env-1",
            name: "统一闭包",
            revision: 2,
            kind: "venv",
            status: "installed",
            targetDevice: "directml",
            configuredRecognitionTypes: ["text", "document"],
          },
        ],
        activeEnvironmentId: "env-1",
      })}
      actions={actions}
    />,
  );
  const section = screen.getByLabelText("PaddleOCR 推理后端");
  // 目录展示只用显式 display name / 用途元数据，不解析 id。
  expect(within(section).getByText(/目标配置：名称 0/)).toBeVisible();
  await userEvent.selectOptions(within(section).getByRole("combobox"), "cpu");
  expect(actions.run).toHaveBeenCalledWith({
    type: "settings.switchEnvironment",
    environmentId: "env-1",
    device: "cpu",
  });
});

it("saves model source identifiers from provider metadata", async () => {
  const actions = buildActions();
  const renamed = sources.map((source, index) => ({
    ...source,
    id: `source-${index}`,
  }));
  const view = render(
    <SettingsPage
      viewState={buildViewState({
        environmentRecipes: recipes,
        environmentSources: renamed,
        environments: [],
      })}
      actions={actions}
    />,
  );
  // #294：来源设置在常规页面直接可见，无需先展开折叠区；依赖已全部
  // 随包离线安装，不再有“依赖包来源”下拉。
  expect(screen.getByLabelText("模型来源")).toHaveValue("modelscope");
  expect(screen.queryByLabelText("依赖包来源")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "保存下载来源" }));
  expect(actions.run).toHaveBeenCalledWith({
    type: "settings.setEnvironmentSources",
    paddleocrModelSourceId: "source-0",
    mineruModelSourceId: "source-2",
  });
  // 选择另一提供方后保存：两类引擎统一为该提供方的来源 id。
  await userEvent.selectOptions(
    screen.getByLabelText("模型来源"),
    "huggingface",
  );
  await userEvent.click(screen.getByRole("button", { name: "保存下载来源" }));
  expect(actions.run).toHaveBeenCalledWith({
    type: "settings.setEnvironmentSources",
    paddleocrModelSourceId: "source-1",
    mineruModelSourceId: "source-3",
  });
  // 保存只写来源配置，不触发任何安装面命令。
  expect(
    actions.run.mock.calls.filter(
      ([command]) => command.type !== "settings.setEnvironmentSources",
    ),
  ).toHaveLength(0);
  view.unmount();
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
