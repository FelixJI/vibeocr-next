import { expect, test } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

const sources = [
  {
    kind: "package_index",
    id: "tuna-pypi",
    displayName: "TUNA PyPI 镜像",
    endpoint: "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
    isDefault: true,
  },
  {
    kind: "package_index",
    id: "pypi",
    displayName: "PyPI 官方源",
    endpoint: "https://pypi.org/simple/",
  },
  {
    kind: "paddleocr_model_registry",
    id: "paddleocr-huggingface",
    provider: "huggingface",
    displayName: "Hugging Face",
    endpoint: "https://huggingface.co",
  },
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
    id: "paddleocr-bos",
    provider: "bos",
    displayName: "百度 BOS",
    endpoint: "https://paddle-model-ecology.bj.bcebos.com",
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-huggingface",
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
];

// 目录只提供 Paddle ONNX（CPU/DirectML/CUDA，同一套依赖覆盖文字/表格/
// 结构）与独立闭包的 MinerU（CPU/CUDA）。依赖来源只显示随包/在线，
// 不硬编码下载量估算。
const recipes = [
  {
    id: "paddleocr-ort-cpu",
    engine: "paddle",
    family: "paddle-onnx",
    familyDisplayName: "PaddleOCR ONNX",
    provider: "CPUExecutionProvider",
    defaultRemoteHost: true,
    displayName: "PaddleOCR ONNX · CPU",
    configuredRecognitionTypes: ["text", "table", "structure"],
    accelerator: "cpu",
    targetDevice: "cpu",
    dependencies: ["paddleocr==3.2.0", "onnxruntime==1.23.0"],
    dependencyOrigin: "bundled_pack",
  },
  {
    id: "paddleocr-ort-cuda",
    engine: "paddle",
    family: "paddle-onnx",
    familyDisplayName: "PaddleOCR ONNX",
    provider: "CUDAExecutionProvider",
    displayName: "PaddleOCR ONNX · NVIDIA CUDA",
    configuredRecognitionTypes: ["text", "table", "structure"],
    accelerator: "nvidia_cuda",
    targetDevice: "cuda",
    dependencies: ["paddleocr==3.2.0", "onnxruntime-gpu==1.23.0"],
    dependencyOrigin: "online_index",
  },
  {
    id: "paddleocr-ort-directml",
    engine: "paddle",
    family: "paddle-onnx",
    familyDisplayName: "PaddleOCR ONNX",
    provider: "DmlExecutionProvider",
    displayName: "PaddleOCR ONNX · Windows DirectML",
    configuredRecognitionTypes: ["text", "table", "structure"],
    accelerator: "cpu",
    targetDevice: "directml",
    dependencies: ["paddleocr==3.2.0", "onnxruntime-directml==1.23.0"],
    dependencyOrigin: "bundled_pack",
  },
  {
    id: "mineru-cpu",
    engine: "mineru",
    family: "mineru",
    familyDisplayName: "MinerU",
    provider: "CPUExecutionProvider",
    displayName: "MinerU · CPU",
    configuredRecognitionTypes: ["document"],
    accelerator: "cpu",
    targetDevice: "cpu",
    dependencies: ["mineru==4.0.10"],
    dependencyOrigin: "online_index",
  },
  {
    id: "mineru-cuda",
    engine: "mineru",
    family: "mineru",
    familyDisplayName: "MinerU",
    provider: "CUDAExecutionProvider",
    displayName: "MinerU · NVIDIA CUDA",
    configuredRecognitionTypes: ["document"],
    accelerator: "nvidia_cuda",
    targetDevice: "cuda",
    dependencies: ["mineru==4.0.10", "torch==2.6.0"],
    dependencyOrigin: "online_index",
  },
];

test("converged source setting keeps one simple global choice per kind", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: {
      settings: {
        environmentSources: sources,
        environmentDefaultSourceIds: [],
        environmentResolvedDefaultSources: [
          {
            kind: "package_index",
            id: "tuna-pypi",
            displayName: "TUNA PyPI 镜像",
            origin: "product_default",
          },
          {
            kind: "paddleocr_model_registry",
            id: "paddleocr-modelscope",
            provider: "modelscope",
            displayName: "ModelScope",
            origin: "product_default",
          },
          {
            kind: "mineru_model_registry",
            id: "mineru-modelscope",
            provider: "modelscope",
            displayName: "ModelScope",
            origin: "product_default",
          },
        ],
        environments: [],
        environmentRecipes: recipes,
        environmentBusy: false,
        environmentStatus: "来源设置已读取",
      },
    },
  });
  // 来源与清理收进高级折叠区：先展开再配置。
  await page.locator("details.environment-advanced-maintenance").click();
  const section = page.locator(".environment-source-settings");
  await expect(section).toBeVisible();
  // 依赖默认 TUNA，模型默认魔搭；选项不暴露“跟随/覆盖”层。
  const packages = page.getByLabel("依赖包来源", { exact: true });
  await expect(packages).toHaveValue("tuna-pypi");
  await expect(packages.locator("option[value='tuna-pypi']")).toHaveText(
    "TUNA PyPI 镜像（默认）",
  );
  const model = page.getByLabel("模型来源", { exact: true });
  await expect(model).toHaveValue("modelscope");
  await expect(model.locator("option")).toHaveCount(2);
  await expect(model.locator("option[value='modelscope']")).toHaveText(
    "ModelScope（魔搭）",
  );
  // 单一设置：不再渲染按环境覆盖与第二处全局默认入口。
  await expect(page.locator("details.managed-source-config")).toHaveCount(0);
  await expect(page.locator("details.managed-source-defaults")).toHaveCount(0);
  await expect(page.getByLabel("本环境依赖包来源")).toHaveCount(0);
  await expect(page.getByLabel("全局依赖包来源")).toHaveCount(0);
  await expect(page.getByText("仅用于安装依赖与下载模型")).toBeVisible();

  await packages.selectOption("pypi");
  await model.selectOption("huggingface");
  await page.getByRole("button", { name: "保存下载来源", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "setEnvironmentSources",
    arguments: {
      packageSourceId: "pypi",
      paddleocrModelSourceId: "paddleocr-huggingface",
      mineruModelSourceId: "mineru-huggingface",
    },
  });
});

test("paddle backend is a single explicit choice without capability packages", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: {
      settings: {
        environmentSources: sources.slice(0, 2),
        environments: [
          {
            id: "env-1",
            name: "空白",
            revision: 1,
            kind: "venv",
            status: "empty",
            pythonState: "ready",
            dependencyState: "empty",
            engineState: "unavailable",
            modelState: "not_applicable",
            serviceState: "not_started",
            configuredRecognitionTypes: [],
          },
        ],
        environmentRecipes: recipes,
        environmentHardware: { nvidiaDriverStatus: "ok" },
        environmentBusy: false,
      },
    },
  });
  // Paddle 分区：单一“推理后端”选择；不再有组件/能力套餐下拉。
  const backend = page.getByLabel("Paddle 推理后端", { exact: true });
  await expect(backend).toHaveValue("cpu");
  await expect(backend.locator("option")).toHaveCount(3);
  await expect(page.getByLabel("识别组件")).toHaveCount(0);
  await expect(page.getByLabel("加速方式")).toHaveCount(0);
  await expect(page.locator("#managed-recipe-select")).toHaveCount(0);
  await expect(
    page.getByText(/文字、表格与版面结构识别共用同一套 PaddleOCR 依赖/),
  ).toBeVisible();
  await expect(page.getByText(/依赖随包提供，可离线安装/)).toBeVisible();
  // 快照已同步但未启动：如实展示默认配置“尚未启动”，不伪造就绪。
  await expect(
    page.getByText("默认运行环境：PaddleOCR ONNX · CPU（尚未启动）"),
  ).toBeVisible();
  // 只有聚焦的 Paddle 分区自动查询；MinerU 分区提供显式入口。
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-cpu" },
  });
  const mineruSection = page.locator(".engine-mineru-section");
  await expect(mineruSection).toBeVisible();
  await expect(
    mineruSection.getByRole("button", { name: "查询可复用环境" }),
  ).toBeVisible();
  // 后端切换是显式选择：失效旧计划 + 只读查询新配方，不静默安装或切换。
  await backend.selectOption("directml");
  await expectCommand(page, {
    scope: "settings",
    action: "invalidateEnvironmentPlan",
    arguments: {},
  });
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-directml" },
  });
});

test("mineru engine install and removal stay explicit with active protection", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: {
      settings: {
        environmentSources: sources,
        environments: [
          {
            id: "mineru-active",
            name: "MinerU · CPU",
            revision: 2,
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
        ],
        activeEnvironmentId: "mineru-active",
        environmentRecipes: recipes,
        environmentHardware: { nvidiaDriverStatus: "ok" },
        environmentBusy: false,
      },
    },
  });
  const mineruSection = page.locator(".engine-mineru-section");
  await expect(
    mineruSection.getByText("MinerU 深度文档解析（本地引擎）"),
  ).toBeVisible();
  // MinerU 与 Paddle 独立、远程连接另列，不装本地 MinerU。
  await expect(mineruSection.getByText(/不安装本地引擎/)).toBeVisible();
  // 本地 MinerU 环境按分区列出：活动环境不提供移除按钮，只引导先切换。
  const activeRow = mineruSection.locator(
    "[data-environment-id='mineru-active']",
  );
  await expect(activeRow.getByText(/· 当前使用（修订/)).toBeVisible();
  await expect(activeRow.getByRole("button", { name: "移除环境" })).toHaveCount(
    0,
  );
  await expect(
    activeRow.getByText(/当前使用中：请先切换到其他环境后再移除/),
  ).toBeVisible();
  // 非活动环境走既有安全删除路径：两步确认后才发命令。
  const oldRow = mineruSection.locator("[data-environment-id='mineru-old']");
  await oldRow.getByRole("button", { name: "移除环境" }).click();
  await oldRow.getByRole("button", { name: "确认移除" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "deleteEnvironment",
    arguments: { environmentId: "mineru-old" },
  });
  // 移除语义：共享缓存与模型受保护保留。
  await expect(
    mineruSection.getByText(/共享模型缓存、下载缓存与基础/),
  ).toBeVisible();
  // 安装新闭包仍需显式查询 + 准备：MinerU CUDA 依赖在线安装。
  await mineruSection.getByRole("button", { name: "查询可复用环境" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "mineru-cpu" },
  });
  await mineruSection.getByLabel("MinerU 安装设备").selectOption("nvidia_cuda");
  await expectCommand(page, {
    scope: "settings",
    action: "invalidateEnvironmentPlan",
    arguments: {},
  });
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "mineru-cuda" },
  });
});

test("remote MinerU can be enabled without an installed environment", async ({
  page,
}) => {
  const settings = {
    environments: [],
    activeEnvironmentId: null,
    environmentBusy: false,
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  const enable = page.getByRole("button", { name: "启用 MinerU 远程模式" });
  await expect(enable).toBeEnabled();
  await enable.click();
  await expectCommand(page, {
    scope: "settings",
    action: "prepareRemoteHost",
    arguments: {},
  });
  await sendState(page, "settings", { ...settings, environmentBusy: true });
  await expect(enable).toBeDisabled();
});

test("normal preparation keeps confirmation brief and rejects late plans after a new choice", async ({
  page,
}) => {
  const settings = {
    environmentRecipes: recipes,
    environmentSources: sources,
    environments: [],
    environmentHardware: { nvidiaDriverStatus: "ok" },
    environmentCompatibility: {
      recipe: "paddleocr-ort-cpu",
      selectedEnvironmentId: null,
    },
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  // 与原生 smoke 一样，向后端 Select 派发同值 change 不产生失效。
  await page.getByLabel("Paddle 推理后端", { exact: true }).selectOption("cpu");
  const invalidations = await page.evaluate(() => {
    const host = (
      window as Window & {
        __testHost: { commands: readonly { scope: string; action: string }[] };
      }
    ).__testHost;
    return host.commands.filter(
      (command) =>
        command.scope === "settings" &&
        command.action === "invalidateEnvironmentPlan",
    ).length;
  });
  expect(invalidations).toBe(0);
  await expect(
    page.getByRole("button", { name: "准备此后端", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "准备此后端", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "prepareEnvironment",
    arguments: { recipe: "paddleocr-ort-cpu" },
  });
  const prepared = {
    ...settings,
    environments: [
      {
        id: "prepared-id",
        name: "旧用户名称",
        revision: 1,
        kind: "venv",
        status: "empty",
        pythonState: "ready",
        dependencyState: "empty",
      },
    ],
    environmentPlan: {
      planId: "plan-one",
      environmentId: "prepared-id",
      environmentRevision: 1,
      requestedRecipe: "paddleocr-ort-cpu",
      recipe: "paddleocr-ort-cpu",
      sourceIds: ["tuna-pypi"],
      requestedSourceIds: null,
      dependencies: [
        "paddleocr==3.2.0",
        "onnxruntime==1.23.0",
        "hidden-transitive==1.0",
      ],
    },
  };
  await sendState(page, "settings", prepared);
  const confirmation = page.locator(".runtime-install-plan");
  await expect(
    confirmation.getByText(
      /本次新增或替换组件：paddleocr==3\.2\.0、onnxruntime==1\.23\.0；\s*共 3 项依赖/,
    ),
  ).toBeVisible();
  await expect(confirmation.getByText(/hidden-transitive/)).not.toBeVisible();
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toBeVisible();
  // 新选择（MinerU 设备）使旧计划失效：即使旧预览换 planId 迟到也不能确认。
  await page.getByLabel("MinerU 安装设备").selectOption("nvidia_cuda");
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toHaveCount(0);
  await sendState(page, "settings", {
    ...prepared,
    environmentPlan: { ...prepared.environmentPlan, planId: "late-plan" },
  });
  await expect(page.locator(".runtime-install-plan")).toHaveCount(0);
  await page.getByRole("link", { name: "单次识别", exact: true }).click();
  await sendState(page, "settings", prepared);
  await page.getByRole("link", { name: "设置", exact: true }).click();
  // 导航回来从宿主待确认计划恢复 Paddle CPU 选择。
  await expect(page.getByLabel("Paddle 推理后端", { exact: true })).toHaveValue(
    "cpu",
  );
  await page.getByRole("button", { name: "确认安装依赖", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "confirmEnvironmentInstall",
    arguments: { planId: "plan-one" },
  });
});

test("catalog rejects duplicate combinations without silent merging", async ({
  page,
}) => {
  const settings = {
    environmentRecipes: recipes,
    environments: [],
    environmentHardware: { nvidiaDriverStatus: "ok" },
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  const backend = page.getByLabel("Paddle 推理后端", { exact: true });
  const mineruDevice = page.getByLabel("MinerU 安装设备", { exact: true });
  await expect(backend.locator("option")).toHaveCount(3);
  await expect(mineruDevice.locator("option")).toHaveCount(2);
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-cpu" },
  });
  await backend.selectOption("directml");
  await expect(
    page.getByText(/主要依赖：\s*paddleocr==3\.2\.0、onnxruntime-directml/),
  ).toBeVisible();
  // 重复/冲突目录明确拒绝，两分区选择都不再渲染。
  await sendState(page, "settings", {
    ...settings,
    environmentRecipes: [recipes[0], recipes[0], recipes[0]],
  });
  await expect(page.getByRole("alert").first()).toHaveText(
    /重复或设备信息冲突/,
  );
  await expect(backend).toHaveCount(0);
  await expect(mineruDevice).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "准备此后端", exact: true }),
  ).toHaveCount(0);
});

test("same Paddle environment switches selected device without another install", async ({
  page,
}) => {
  const settings = {
    environmentRecipes: recipes,
    environmentSources: sources,
    activeEnvironmentId: "shared",
    environmentHardware: { nvidiaDriverStatus: "ok" },
    environmentCompatibility: {
      recipe: "paddleocr-ort-cpu",
      selectedEnvironmentId: "shared",
    },
    environments: [
      {
        id: "shared",
        name: "Paddle",
        revision: 2,
        kind: "venv",
        status: "installed",
        recipe: "paddleocr-ort-cuda",
        targetDevice: "cuda",
        pythonState: "ready",
        dependencyState: "installed",
        engineState: "ready",
        modelState: "ready",
        serviceState: "ready",
        configuredRecognitionTypes: ["text", "table", "structure"],
      },
    ],
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  const section = page.locator(".engine-backend-section");
  await expect(
    section.getByText("该兼容环境已是当前环境并通过启动验证，无需重装。"),
  ).toHaveCount(0);
  await section.getByRole("button", { name: "启动并验证当前环境" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "switchEnvironment",
    arguments: { environmentId: "shared", device: "cpu" },
  });
  await page
    .locator("details.environment-advanced-maintenance > summary")
    .click();
  await page.getByRole("button", { name: "预览移除 CUDA（保留 CPU）" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "previewEnvironmentInstall",
    arguments: { environmentId: "shared", recipe: "paddleocr-ort-cpu" },
  });
  await sendState(page, "settings", {
    ...settings,
    environmentPlan: {
      planId: "a".repeat(32),
      environmentId: "shared",
      environmentRevision: 2,
      recipe: "paddleocr-ort-cpu",
      requestedRecipe: "paddleocr-ort-cpu",
      sourceIds: ["tuna-pypi"],
      dependencies: [],
      removedDependencies: ["nvidia-cudnn-cu13"],
      cudaPluginAction: "remove",
      requiresServiceStop: true,
    },
  });
  await expect(
    page.getByText(/同时移除 ONNX Runtime CUDA 13 执行插件/),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "确认移除 CUDA", exact: true }),
  ).toBeVisible();
  await expect(page.getByText(/确认后暂停当前识别服务/)).toBeVisible();
});

test("CUDA upgrade confirmation exposes all seven added NVIDIA pins", async ({
  page,
}) => {
  const dependencies = [
    "nvidia-cuda-runtime==13.4.92",
    "nvidia-cuda-nvrtc==13.4.92",
    "nvidia-cudnn-cu13==9.27.0.42",
    "nvidia-curand==10.4.4.72",
    "nvidia-cufft==12.4.0.43",
    "nvidia-cublas==13.8.0.4",
    "nvidia-nvjitlink==13.4.92",
  ];
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: {
      settings: {
        environmentRecipes: recipes,
        environmentSources: sources,
        environmentHardware: { nvidiaDriverStatus: "ok" },
        environments: [
          { id: "shared", name: "Paddle", revision: 2, status: "installed" },
        ],
        environmentPlan: {
          planId: "cuda-delta",
          environmentId: "shared",
          environmentRevision: 2,
          recipe: "paddleocr-ort-cuda",
          requestedRecipe: "paddleocr-ort-cuda",
          sourceIds: ["tuna-pypi"],
          dependencies,
          requiresServiceStop: true,
          cudaPluginAction: "prepare",
        },
      },
    },
  });
  const summary = page
    .locator(".runtime-install-plan > p")
    .filter({ hasText: "本次新增或替换组件" });
  await expect(summary).not.toContainText("无需新增组件");
  for (const pin of dependencies) await expect(summary).toContainText(pin);
  await expect(
    page.getByText(/同时准备锁定的 ONNX Runtime CUDA 13 执行插件/),
  ).toBeVisible();
  await page.getByRole("button", { name: "确认安装依赖", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "confirmEnvironmentInstall",
    arguments: { planId: "cuda-delta" },
  });
});

test("healthy CPU upgrades through the public CUDA prepare action", async ({
  page,
}) => {
  const settings = {
    environmentRecipes: recipes,
    environmentSources: sources,
    environmentHardware: { nvidiaDriverStatus: "ok" },
    activeEnvironmentId: "shared-cpu",
    environmentCompatibility: {
      recipe: "paddleocr-ort-cuda",
      selectedEnvironmentId: null,
    },
    environments: [
      {
        id: "shared-cpu",
        name: "existing CPU",
        revision: 3,
        kind: "venv",
        status: "installed",
        recipe: "paddleocr-ort-cpu",
        targetDevice: "cpu",
        pythonState: "ready",
        dependencyState: "installed",
        engineState: "ready",
        serviceState: "ready",
      },
    ],
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  await page
    .getByLabel("Paddle 推理后端", { exact: true })
    .selectOption("nvidia_cuda");
  await sendState(page, "settings", settings);
  await page
    .locator("details.environment-advanced-maintenance > summary")
    .click();
  const row = page.locator(
    '.managed-environment-item[data-environment-id="shared-cpu"]',
  );
  await expect(row).toBeVisible();
  // Reproduces why the previous native smoke could never click its repair path.
  await expect(
    row.getByRole("button", { name: "重新准备依赖", exact: true }),
  ).toHaveCount(0);
  const prepare = page
    .locator(".engine-backend-section")
    .getByRole("button", { name: "准备此后端", exact: true });
  await expect(prepare).toBeEnabled();
  await prepare.click();
  await expectCommand(page, {
    scope: "settings",
    action: "prepareEnvironment",
    arguments: { recipe: "paddleocr-ort-cuda" },
  });
  await sendState(page, "settings", {
    ...settings,
    environmentPlan: {
      planId: "upgrade-existing",
      environmentId: "shared-cpu",
      environmentRevision: 3,
      recipe: "paddleocr-ort-cuda",
      requestedRecipe: "paddleocr-ort-cuda",
      sourceIds: ["tuna-pypi"],
      dependencies: ["nvidia-cudnn-cu13==9.27.0.42"],
      requiresServiceStop: true,
      cudaPluginAction: "prepare",
    },
  });
  await expect(page.locator(".runtime-install-plan")).toContainText(
    "existing CPU",
  );
  await page.getByRole("button", { name: "确认安装依赖", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "confirmEnvironmentInstall",
    arguments: { planId: "upgrade-existing" },
  });
});

test("same-id device commit automatically refreshes compatibility without manual retry", async ({
  page,
}) => {
  const environment = {
    id: "shared",
    name: "Paddle",
    revision: 3,
    kind: "venv",
    status: "installed",
    recipe: "paddleocr-ort-cuda",
    targetDevice: "cuda",
    serviceState: "ready",
  };
  const settings = {
    environmentRecipes: recipes,
    environmentSources: sources,
    activeEnvironmentId: "shared",
    environmentHardware: { nvidiaDriverStatus: "ok" },
    environmentBusy: false,
    environments: [environment],
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-cpu" },
  });
  const compatibility = {
    recipe: "paddleocr-ort-cpu",
    selectedEnvironmentId: "shared",
  };
  await sendState(page, "settings", {
    ...settings,
    environmentCompatibility: compatibility,
  });
  await page
    .locator(".engine-backend-section")
    .getByRole("button", { name: "启动并验证当前环境", exact: true })
    .click();
  await expectCommand(page, {
    scope: "settings",
    action: "switchEnvironment",
    arguments: { environmentId: "shared", device: "cpu" },
  });
  await sendState(page, "settings", { ...settings, environmentBusy: true });
  const complete = {
    ...settings,
    environments: [{ ...environment, targetDevice: "cpu" }],
  };
  await sendState(page, "settings", complete);
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (
            window as Window & {
              __testHost: {
                commands: {
                  scope: string;
                  action: string;
                  arguments?: { recipe?: string };
                }[];
              };
            }
          ).__testHost.commands.filter(
            (c) =>
              c.scope === "settings" &&
              c.action === "findCompatibleEnvironment" &&
              c.arguments?.recipe === "paddleocr-ort-cpu",
          ).length,
      ),
    )
    .toBe(2);
  await sendState(page, "settings", {
    ...complete,
    environmentCompatibility: compatibility,
  });
  await expect(
    page.getByText("该兼容环境已是当前环境并通过启动验证，无需重装。", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "重新查询兼容环境", exact: true }),
  ).toHaveCount(0);
});
