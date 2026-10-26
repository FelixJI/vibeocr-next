import {
  cleanup,
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

// 与 e2e/environment-sources.spec.ts 同源的目录投影：Paddle 三种 ONNX 后端
// 覆盖文字/表格/结构（仅模型与管线不同），MinerU 独立 CPU/CUDA 闭包。
const recipes = [
  {
    id: "paddleocr-ort-cpu",
    displayName: "PaddleOCR ONNX · CPU",
    configuredRecognitionTypes: ["text", "table", "structure"],
    accelerator: "cpu",
    targetDevice: "cpu",
    pythonVersion: "3.12",
    dependencies: ["paddleocr==3.2.0", "onnxruntime==1.23.0"],
    dependencyOrigin: "bundled_pack",
  },
  {
    id: "paddleocr-ort-directml",
    displayName: "PaddleOCR ONNX · Windows DirectML",
    configuredRecognitionTypes: ["text", "table", "structure"],
    accelerator: "cpu",
    targetDevice: "directml",
    pythonVersion: "3.12",
    dependencies: ["paddleocr==3.2.0", "onnxruntime-directml==1.23.0"],
    dependencyOrigin: "bundled_pack",
  },
  {
    id: "paddleocr-ort-cuda",
    displayName: "PaddleOCR ONNX · NVIDIA CUDA",
    configuredRecognitionTypes: ["text", "table", "structure"],
    accelerator: "nvidia_cuda",
    targetDevice: "cuda",
    pythonVersion: "3.12",
    dependencies: ["paddleocr==3.2.0", "onnxruntime-gpu==1.23.0"],
    dependencyOrigin: "online_index",
  },
  {
    id: "mineru-cpu",
    displayName: "MinerU · CPU",
    configuredRecognitionTypes: ["document"],
    accelerator: "cpu",
    targetDevice: "cpu",
    pythonVersion: "3.12",
    dependencies: ["mineru==4.0.10"],
    dependencyOrigin: "online_index",
  },
  {
    id: "mineru-cuda",
    displayName: "MinerU · NVIDIA CUDA",
    configuredRecognitionTypes: ["document"],
    accelerator: "nvidia_cuda",
    targetDevice: "cuda",
    pythonVersion: "3.12",
    dependencies: ["mineru==4.0.10", "torch==2.6.0"],
    dependencyOrigin: "online_index",
  },
];

const sources = [
  {
    kind: "package_index",
    id: "tuna-pypi",
    displayName: "TUNA PyPI 镜像",
    endpoint: "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
    isDefault: true,
  },
  {
    kind: "paddleocr_model_registry",
    id: "paddleocr-modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
    isDefault: true,
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
    isDefault: true,
  },
];

const mineruEnvironments = [
  {
    id: "mineru-active",
    name: "MinerU · CPU",
    revision: 3,
    kind: "venv",
    status: "installed",
    pythonState: "ready",
    dependencyState: "installed",
    engineState: "ready",
    modelState: "ready",
    serviceState: "ready",
    configuredRecognitionTypes: ["document"],
    targetDevice: "cpu",
  },
  {
    id: "mineru-old",
    name: "MinerU · NVIDIA CUDA",
    revision: 1,
    kind: "venv",
    status: "installed",
    pythonState: "ready",
    dependencyState: "installed",
    engineState: "unverified",
    modelState: "not_checked",
    serviceState: "not_started",
    configuredRecognitionTypes: ["document"],
    targetDevice: "cuda",
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
  it("Paddle 分区只按推理后端选择且只有聚焦分区自动查询", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentHardware: { nvidiaDriverStatus: "ok" },
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // Paddle 分区：单一后端选择，不再出现“识别组件/加速方式”双下拉。
    const backend = screen.getByLabelText("Paddle 推理后端");
    expect(backend).toHaveValue("cpu");
    expect(within(backend).getAllByRole("option")).toHaveLength(3);
    expect(screen.queryByLabelText("识别组件")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("加速方式")).not.toBeInTheDocument();
    expect(screen.getByText(/依赖随包提供，可离线安装/)).toBeVisible();
    expect(
      screen.getByText(/文字、表格与版面结构识别共用同一套 PaddleOCR 依赖/),
    ).toBeVisible();
    // 只有聚焦的 Paddle 分区自动查询；MinerU 分区不 mount 即发请求。
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-cpu",
      }),
    );
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.findCompatibleEnvironment",
      recipe: "mineru-cpu",
    });
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    expect(
      within(mineruSection).getByRole("button", { name: "查询可复用环境" }),
    ).toBeVisible();
    // 切换后端是显式选择：先失效旧计划，再对新配方发起只读查询。
    await user.selectOptions(backend, "directml");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "paddleocr-ort-directml",
      }),
    );
  });

  it("MinerU 分区显式查询后按 recipe 绑定展示结果与安装入口", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentHardware: { nvidiaDriverStatus: "ok" },
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
        recipe: "mineru-cpu",
      }),
    );
    // 迟到的他配方结果（Paddle）不得投影进 MinerU 分区：无命中结论不出现，
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
            recipe: "mineru-cpu",
            selectedEnvironmentId: null,
          },
        })}
        actions={actions}
      />,
    );
    expect(screen.getByText(/没有可直接复用的已安装环境/)).toBeVisible();
    await user.click(
      screen.getByRole("button", { name: "安装 MinerU 本地引擎" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.prepareEnvironment",
      recipe: "mineru-cpu",
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
      environmentHardware: { nvidiaDriverStatus: "ok" },
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    const activeRow = within(mineruSection)
      .getByText("MinerU · CPU")
      .closest("div") as HTMLElement;
    expect(activeRow).not.toBeNull();
    // 活动环境不提供移除按钮，只引导先切换到其他环境。
    expect(
      within(activeRow).queryByRole("button", { name: "移除环境" }),
    ).not.toBeInTheDocument();
    expect(
      within(activeRow).getByText(/当前使用中：请先切换到其他环境后再移除/),
    ).toBeVisible();
    // 非活动 MinerU 环境走既有安全删除路径：两步确认后才发命令。
    const oldRow = within(mineruSection)
      .getByText("MinerU · NVIDIA CUDA")
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
    // 导航回来：待确认 Paddle CUDA 计划驱动只读恢复（hasExplicitChoice=false）。
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [
        {
          id: "prepared-cuda",
          name: "PaddleOCR ONNX · NVIDIA CUDA",
          revision: 1,
          kind: "venv",
          status: "empty",
          pythonState: "ready",
          dependencyState: "empty",
        },
      ],
      environmentHardware: { nvidiaDriverStatus: "ok" },
      environmentPlan: {
        planId: "p-cuda",
        environmentId: "prepared-cuda",
        environmentRevision: 1,
        requestedRecipe: "paddleocr-ort-cuda",
        recipe: "paddleocr-ort-cuda",
        sourceIds: ["tuna-pypi"],
        requestedSourceIds: null,
        dependencies: ["paddleocr==3.2.0"],
      },
      environmentBusy: false,
    };
    render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // 恢复选择：Paddle 分区回显 CUDA 后端与待确认计划。
    expect(screen.getByLabelText("Paddle 推理后端")).toHaveValue("nvidia_cuda");
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
    // 点击 MinerU 显式查询：焦点必须真正切换（不被恢复选择持续覆盖）。
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    await user.click(
      within(mineruSection).getByRole("button", { name: "查询可复用环境" }),
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "mineru-cpu",
      }),
    );
    // 取得主导权不等于失效：仍绑定原配方的计划继续可确认。
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
  });

  it("单槽被占用后往返切回：显式查询推进查询代并重新发起", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentHardware: { nvidiaDriverStatus: "ok" },
      environmentBusy: false,
    };
    const view = render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    const paddleQuery = {
      type: "settings.findCompatibleEnvironment",
      recipe: "paddleocr-ort-cpu",
    } as const;
    // Paddle 自动查询得到结果。
    await waitFor(() => expect(actions.run).toHaveBeenCalledWith(paddleQuery));
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
    // MinerU 显式查询后宿主单槽被 MinerU 结果占用。
    const mineruSection = screen.getByLabelText("MinerU 本地引擎");
    await user.click(
      within(mineruSection).getByRole("button", { name: "查询可复用环境" }),
    );
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environmentCompatibility: {
            recipe: "mineru-cpu",
            selectedEnvironmentId: null,
          },
        })}
        actions={actions}
      />,
    );
    // Paddle 分区切回：同配方、同指纹但查询代已推进，去重不得阻拦重查。
    const paddleSection = screen.getByLabelText("PaddleOCR 推理后端");
    await user.click(
      within(paddleSection).getByRole("button", { name: "查询可复用环境" }),
    );
    const paddleDispatches = actions.run.mock.calls.filter(
      (call) => JSON.stringify(call[0]) === JSON.stringify({ ...paddleQuery }),
    );
    expect(paddleDispatches.length).toBeGreaterThanOrEqual(2);
  });

  it("晚到的他配方旧计划不能绕过失效跨分区确认", async () => {
    const user = userEvent.setup();
    const actions = buildActions();
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: [],
      environmentHardware: { nvidiaDriverStatus: "ok" },
      environmentBusy: false,
    };
    const view = render(
      <SettingsPage viewState={buildViewState(settings)} actions={actions} />,
    );
    // 显式安装选择切到 MinerU CUDA：选择变化即失效旧计划。
    await user.selectOptions(
      screen.getByLabelText("MinerU 安装设备"),
      "nvidia_cuda",
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    const environments = [
      {
        id: "env-1",
        name: "旧目标",
        revision: 1,
        kind: "venv",
        status: "empty",
        pythonState: "ready",
        dependencyState: "empty",
      },
    ];
    // 换 planId 迟到的旧 Paddle 计划：即使 Paddle 分区仍选 CPU 也不得确认。
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environments,
          environmentPlan: {
            planId: "late-paddle",
            environmentId: "env-1",
            environmentRevision: 1,
            requestedRecipe: "paddleocr-ort-cpu",
            recipe: "paddleocr-ort-cpu",
            sourceIds: ["tuna-pypi"],
            requestedSourceIds: null,
            dependencies: ["paddleocr==3.2.0"],
          },
        })}
        actions={actions}
      />,
    );
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    // 绑定当前安装选择（mineru-cuda）的计划恢复可确认。
    view.rerender(
      <SettingsPage
        viewState={buildViewState({
          ...settings,
          environments,
          environmentPlan: {
            planId: "mineru-plan",
            environmentId: "env-1",
            environmentRevision: 1,
            requestedRecipe: "mineru-cuda",
            recipe: "mineru-cuda",
            sourceIds: ["tuna-pypi"],
            requestedSourceIds: null,
            dependencies: ["mineru==4.0.10"],
          },
        })}
        actions={actions}
      />,
    );
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
  });

  it("技术环境清单、依赖版本与来源收进默认折叠的高级区", () => {
    const settings = {
      environmentRecipes: recipes,
      environmentSources: sources,
      environments: mineruEnvironments,
      activeEnvironmentId: "mineru-active",
      environmentHardware: { nvidiaDriverStatus: "ok" },
      environmentSupportsCleanup: false,
      environmentBusy: false,
    };
    render(
      <SettingsPage
        viewState={buildViewState(settings)}
        actions={buildActions()}
      />,
    );
    const advanced = document.querySelector(
      "details.environment-advanced-maintenance",
    );
    expect(advanced).not.toBeNull();
    expect(advanced).not.toHaveAttribute("open", "");
    expect(
      within(advanced as HTMLDetailsElement).getByText("已安装环境"),
    ).toBeInTheDocument();
    expect(
      within(advanced as HTMLDetailsElement).getByText(
        "下载来源（依赖与模型）",
      ),
    ).toBeInTheDocument();
    // 安装进度不属于高级设置：空闲时自身不渲染，不需要展开即可见。
    expect(
      within(advanced as HTMLDetailsElement).queryByLabelText("依赖安装进度"),
    ).not.toBeInTheDocument();
  });
});
