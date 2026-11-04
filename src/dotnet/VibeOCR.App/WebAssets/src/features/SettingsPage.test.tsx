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

  it("Paddle 分区只按推理后端选择且只有聚焦分区自动查询", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // Paddle 分区：单一后端选择（统一闭包 = DirectML 安装档 + CPU 切换），
    // 不再出现“识别组件/加速方式”双下拉。
    const backend = screen.getByLabelText("Paddle 推理后端");
    expect(backend).toHaveValue("directml");
    expect(within(backend).getAllByRole("option")).toHaveLength(2);
    expect(screen.queryByLabelText("识别组件")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("加速方式")).not.toBeInTheDocument();
    // 统一闭包同时命中两个分区：两分区都如实呈现随包依赖来源。
    expect(
      screen.getAllByText(/依赖随包提供，可离线安装/).length,
    ).toBeGreaterThan(0);
    expect(
      screen.getByText(/文字、表格与版面结构识别共用同一套 PaddleOCR 依赖/),
    ).toBeVisible();
    // 只有聚焦的 Paddle 分区自动查询；MinerU 分区不 mount 即发请求。
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-directml",
      }),
    );
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    expect(
      within(mineruSection).getByRole("button", { name: "查询可复用环境" }),
    ).toBeVisible();
    // 切换设备是显式选择：先失效旧计划，再对新设备档发起只读查询；
    // 同闭包切换不另装配方。
    await user.selectOptions(backend, "cpu");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    expect(screen.getByText(/设备 cpu，Python 3\.13/)).toBeVisible();
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-directml",
      }),
    );
  });

  it("统一推理闭包目录下 MinerU 分区复用共享闭包配方", async () => {
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
      environmentCompatibility: {
        recipe: "paddleocr-ort-directml",
        selectedEnvironmentId: "unified-installed",
      },
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
    // 兼容查询结果按配方绑定投影进 MinerU 分区（共享闭包同一配方命中）。
    expect(
      within(mineruSection).getByText(/可直接复用环境「统一推理闭包」/),
    ).toBeVisible();
    // 设备选择与安装配方分离：统一闭包同闭包支持 directml↔cpu 切换，
    // 设备下拉枚举 supported_devices，切换命令发送用户选定设备。
    const backend = screen.getByLabelText("Paddle 推理后端");
    expect(backend).toHaveValue("directml");
    expect(within(backend).getAllByRole("option")).toHaveLength(2);
    const paddleSection = screen.getByLabelText("PaddleOCR 推理后端");
    await user.click(
      within(paddleSection).getByRole("button", {
        name: "切换到此环境并启动验证",
      }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.switchEnvironment",
      environmentId: "unified-installed",
      device: "directml",
    });
    await user.selectOptions(backend, "cpu");
    expect(
      within(paddleSection).getByText(/设备 cpu，Python 3\.13/),
    ).toBeVisible();
    await user.click(
      within(paddleSection).getByRole("button", {
        name: "切换到此环境并启动验证",
      }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.switchEnvironment",
      environmentId: "unified-installed",
      device: "cpu",
    });
  });

  it("MinerU 分区显式查询后按 recipe 绑定展示结果与安装入口", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentBusy: false,
    };
    const view = render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    await user.click(
      within(mineruSection).getByRole("button", { name: "查询可复用环境" }),
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-directml",
      }),
    );
    // 迟到的他配方结果（退役配方）不得投影进当前选择：无命中结论不出现，
    // 只如实提示本次查询未收到结果，可重试。
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentCompatibility: {
            recipe: "paddleocr-ort-cpu",
            selectedEnvironmentId: null,
          },
        })}
        actions={actions}
      />,
    );
    const sectionAfterLateResult = screen.getByLabelText("MinerU 本地引擎");
    expect(
      within(sectionAfterLateResult).queryByText(/没有可直接复用的已安装环境/),
    ).not.toBeInTheDocument();
    expect(
      within(sectionAfterLateResult).getByRole("button", {
        name: "重新查询兼容环境",
      }),
    ).toBeVisible();
    // 匹配的结果才展示：无命中 → 显式安装入口（prepare 仍需用户确认计划）。
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentCompatibility: {
            recipe: "paddleocr-ort-directml",
            selectedEnvironmentId: null,
          },
        })}
        actions={actions}
      />,
    );
    expect(
      within(sectionAfterLateResult).getByText(/没有可直接复用的已安装环境/),
    ).toBeVisible();
    await user.click(
      screen.getByRole("button", { name: "安装 MinerU 本地引擎" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.prepareEnvironment",
      recipe: "paddleocr-ort-directml",
    });
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

  it("显式查询取得主导权：覆盖导航恢复的计划选择且不作废待确认计划", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    // 导航回来：待确认统一闭包计划驱动只读恢复（hasExplicitChoice=false）。
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [
        {
          id: "prepared-dml",
          name: "PaddleOCR ONNX + MinerU · Windows DirectML",
          revision: 1,
          kind: "venv",
          status: "empty",
          pythonState: "ready",
          dependencyState: "empty",
        },
      ],
      environmentPlan: {
        planId: "p-dml",
        environmentId: "prepared-dml",
        environmentRevision: 1,
        requestedRecipe: "paddleocr-ort-directml",
        recipe: "paddleocr-ort-directml",
        sourceIds: ["paddleocr-modelscope"],
        requestedSourceIds: null,
        dependencies: ["paddleocr==3.7.0"],
      },
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // 恢复选择：Paddle 分区回显 DirectML 后端与待确认计划；统一闭包下
    // 计划卡只渲染一次（归属 Paddle 分区），不出现重复确认入口。
    expect(screen.getByLabelText("Paddle 推理后端")).toHaveValue("directml");
    expect(
      screen.getAllByRole("button", { name: "确认安装依赖" }),
    ).toHaveLength(1);
    // 点击 MinerU 显式查询：焦点必须真正切换（不被恢复选择持续覆盖）。
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    await user.click(
      within(mineruSection).getByRole("button", { name: "查询可复用环境" }),
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-directml",
      }),
    );
    // 取得主导权不等于失效：仍绑定原配方的计划继续可确认。
    expect(
      screen.getAllByRole("button", { name: "确认安装依赖" }),
    ).toHaveLength(1);
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
  });

  it("晚到的旧计划不能绕过本地失效重新确认", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentBusy: false,
    };
    const plan = (planId: string) => ({
      planId,
      environmentId: "env-1",
      environmentRevision: 1,
      requestedRecipe: "paddleocr-ort-directml",
      recipe: "paddleocr-ort-directml",
      sourceIds: ["paddleocr-modelscope"],
      requestedSourceIds: null,
      dependencies: ["paddleocr==3.7.0"],
    });
    const view = render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // 显式设备选择绑定当前配方：随后到达的计划可确认。
    await user.selectOptions(screen.getByLabelText("MinerU 安装设备"), "cpu");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentPlan: plan("plan-a"),
        })}
        actions={actions}
      />,
    );
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
    // 再次显式设备切换：选择变化即失效旧计划。
    await user.selectOptions(
      screen.getByLabelText("MinerU 安装设备"),
      "directml",
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    // 失效后同 planId 的迟到重放不得再次进入确认。
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentPlan: plan("plan-a"),
        })}
        actions={actions}
      />,
    );
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    // 换 planId 的新计划（绑定当前选择）恢复可确认。
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentPlan: plan("plan-b"),
        })}
        actions={actions}
      />,
    );
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
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
    // 真实快照到达：不再重复刷新，目录可用后聚焦分区自动查询。
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
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-directml",
      }),
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

  it("legacy 主机与已有快照都不自动刷新且不清在途计划", () => {
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
          environmentStatus: "已预览锁定配方。",
          environmentSnapshotLoaded: true,
          environmentRecipes: recipes,
          environments: [
            { id: "env-1", name: "文档", revision: 1, status: "empty" },
          ],
          environmentPlan: {
            planId: "plan-a",
            environmentId: "env-1",
            environmentRevision: 1,
            requestedRecipe: "paddleocr-ort-directml",
            recipe: "paddleocr-ort-directml",
            sourceIds: [],
            dependencies: ["paddleocr==3.7.0"],
          },
        })}
        actions={actions}
      />,
    );
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.refreshEnvironmentList",
    });
    // 已有快照与待确认计划不被自动加载触碰。
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
  });
});

it("uses explicit metadata when recipe identifiers and display names change", async () => {
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
        environments: [],
      })}
      actions={actions}
    />,
  );
  const section = screen.getByLabelText("PaddleOCR 推理后端");
  await userEvent.selectOptions(within(section).getByRole("combobox"), "cpu");
  await userEvent.click(
    within(section).getByRole("button", { name: "重新查询兼容环境" }),
  );
  expect(actions.run).toHaveBeenCalledWith({
    type: "settings.findCompatibleEnvironment",
    recipe: "custom.engine_0",
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
  expect(actions.run).not.toHaveBeenCalledWith(
    expect.objectContaining({ type: "settings.prepareEnvironment" }),
  );
  view.unmount();
});

it.each(["device", "service", "active"] as const)(
  "兼容结果失效后随运行选择变化补发一次查询：%s",
  async (change) => {
    const actions = buildActions();
    const environment = {
      id: "shared",
      name: "Paddle",
      revision: 3,
      kind: "venv",
      status: "installed",
      recipe: "paddleocr-ort-directml",
      targetDevice: change === "device" ? "cpu" : "directml",
      serviceState: change === "service" ? "not_started" : "ready",
    };
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environmentBusy: false,
      activeEnvironmentId: change === "active" ? "other" : "shared",
      environments: [environment],
    };
    const query = {
      type: "settings.findCompatibleEnvironment",
      recipe: "paddleocr-ort-directml",
    };
    const view = render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    await waitFor(() => expect(actions.run).toHaveBeenCalledWith(query));
    const compatibility = {
      recipe: "paddleocr-ort-directml",
      selectedEnvironmentId: "shared",
    };
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentCompatibility: compatibility,
        })}
        actions={actions}
      />,
    );
    actions.run.mockClear();
    // A real same-id switch or background attach clears compatibility while
    // preserving the installed environment revision. Busy must defer dispatch.
    view.rerender(
      <SettingsPage
        viewState={buildViewState({ ...settings, environmentBusy: true })}
        actions={actions}
      />,
    );
    expect(actions.run).not.toHaveBeenCalledWith(query);
    const complete = {
      ...settings,
      activeEnvironmentId: "shared",
      environments: [
        { ...environment, targetDevice: "directml", serviceState: "ready" },
      ],
    };
    view.rerender(
      <SettingsPage viewState={buildViewState(complete)} actions={actions} />,
    );
    await waitFor(() => expect(actions.run).toHaveBeenCalledWith(query));
    expect(
      actions.run.mock.calls.filter(([command]) => command.type === query.type),
    ).toHaveLength(1);
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...complete,
          environmentCompatibility: compatibility,
        })}
        actions={actions}
      />,
    );
    expect(
      screen.getAllByText("该兼容环境已是当前环境并通过启动验证，无需重装。")
        .length,
    ).toBeGreaterThan(0);
    expect(
      actions.run.mock.calls.filter(([command]) => command.type === query.type),
    ).toHaveLength(1);
  },
);

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
