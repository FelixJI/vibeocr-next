import { expect, test } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

const sources = [
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

// 目录只提供统一推理闭包 paddleocr-ort-directml（Paddle ONNX + MinerU
// 同一闭包，DirectML 安装档 + CPU 同闭包设备切换）。依赖全部随包离线
// 提供，可配置的下载来源只剩模型源。
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
  // #294：来源设置在常规页面直接可见，无需展开折叠区；依赖已全部随包
  // 离线安装，不再提供“依赖包来源”选择。
  const section = page.locator(".environment-source-settings");
  await expect(section).toBeVisible();
  await expect(page.getByLabel("依赖包来源")).toHaveCount(0);
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
  await expect(page.getByText("仅用于下载模型")).toBeVisible();

  await model.selectOption("huggingface");
  await page.getByRole("button", { name: "保存下载来源", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "setEnvironmentSources",
    arguments: {
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
        environmentSources: sources,
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
        environmentBusy: false,
      },
    },
  });
  // Paddle 分区：单一“推理后端”选择（统一闭包 = DirectML + CPU 切换）；
  // 不再有组件/能力套餐下拉。
  const backend = page.getByLabel("Paddle 推理后端", { exact: true });
  await expect(backend).toHaveValue("directml");
  await expect(backend.locator("option")).toHaveCount(2);
  await expect(page.getByLabel("识别组件")).toHaveCount(0);
  await expect(page.getByLabel("加速方式")).toHaveCount(0);
  await expect(page.locator("#managed-recipe-select")).toHaveCount(0);
  await expect(
    page.getByText(/文字、表格与版面结构识别共用同一套 PaddleOCR 依赖/),
  ).toBeVisible();
  await expect(
    page
      .locator(".engine-backend-section")
      .getByText(/依赖随包提供，可离线安装/),
  ).toBeVisible();
  // 快照已同步但未启动：如实展示默认配置“尚未启动”，不伪造就绪。
  await expect(
    page.getByText(
      "默认运行环境：PaddleOCR ONNX + MinerU · Windows DirectML（尚未启动）",
    ),
  ).toBeVisible();
  // 只有聚焦的 Paddle 分区自动查询；MinerU 分区提供显式入口。
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-directml" },
  });
  const mineruSection = page.locator(".engine-mineru-section");
  await expect(mineruSection).toBeVisible();
  await expect(
    mineruSection.getByRole("button", { name: "查询可复用环境" }),
  ).toBeVisible();
  // 同闭包设备切换是显式选择：失效旧计划，不静默安装或切换。
  await backend.selectOption("cpu");
  await expectCommand(page, {
    scope: "settings",
    action: "invalidateEnvironmentPlan",
    arguments: {},
  });
  await expect(
    page.locator(".engine-backend-section").getByText(/设备 cpu，Python 3\.13/),
  ).toBeVisible();
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
            name: "PaddleOCR ONNX + MinerU · Windows DirectML",
            revision: 2,
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
            configuredRecognitionTypes: [
              "text",
              "table",
              "structure",
              "document",
            ],
            targetDevice: "cpu",
          },
        ],
        activeEnvironmentId: "mineru-active",
        environmentRecipes: recipes,
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
  // 安装仍需显式查询 + 准备；设备切换失效旧计划后重新只读查询。
  await mineruSection.getByRole("button", { name: "查询可复用环境" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-directml" },
  });
  await mineruSection.getByLabel("MinerU 安装设备").selectOption("cpu");
  await expectCommand(page, {
    scope: "settings",
    action: "invalidateEnvironmentPlan",
    arguments: {},
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
    environmentCompatibility: {
      recipe: "paddleocr-ort-directml",
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
  await page
    .getByLabel("Paddle 推理后端", { exact: true })
    .selectOption("directml");
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
    arguments: { recipe: "paddleocr-ort-directml" },
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
      requestedRecipe: "paddleocr-ort-directml",
      recipe: "paddleocr-ort-directml",
      sourceIds: ["paddleocr-modelscope"],
      requestedSourceIds: null,
      dependencies: [
        "paddleocr==3.7.0",
        "onnxruntime-directml==1.24.4",
        "hidden-transitive==1.0",
      ],
    },
  };
  await sendState(page, "settings", prepared);
  const confirmation = page.locator(".runtime-install-plan");
  await expect(
    confirmation.getByText(
      /本次新增或替换组件：paddleocr==3\.7\.0、onnxruntime-directml==1\.24\.4；\s*共 3 项依赖/,
    ),
  ).toBeVisible();
  await expect(confirmation.getByText(/hidden-transitive/)).not.toBeVisible();
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toBeVisible();
  // 新选择（MinerU 设备切换）使旧计划失效：同 planId 的迟到重放不能确认。
  await page.getByLabel("MinerU 安装设备").selectOption("cpu");
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toHaveCount(0);
  await sendState(page, "settings", {
    ...prepared,
    environmentPlan: { ...prepared.environmentPlan, planId: "late-plan" },
  });
  // 换 planId 的新预览绑定当前选择（统一闭包同配方），恢复可确认。
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toBeVisible();
  await page.getByRole("link", { name: "单次识别", exact: true }).click();
  await sendState(page, "settings", prepared);
  await page.getByRole("link", { name: "设置", exact: true }).click();
  // 导航回来从宿主待确认计划恢复统一闭包选择。
  await expect(page.getByLabel("Paddle 推理后端", { exact: true })).toHaveValue(
    "directml",
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
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  const backend = page.getByLabel("Paddle 推理后端", { exact: true });
  const mineruDevice = page.getByLabel("MinerU 安装设备", { exact: true });
  await expect(backend.locator("option")).toHaveCount(2);
  await expect(mineruDevice.locator("option")).toHaveCount(2);
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-directml" },
  });
  await backend.selectOption("cpu");
  await expect(
    page
      .locator(".engine-backend-section")
      .getByText(/主要依赖：\s*paddleocr==3\.7\.0、onnxruntime-directml/),
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
    environmentCompatibility: {
      recipe: "paddleocr-ort-directml",
      selectedEnvironmentId: "shared",
    },
    environments: [
      {
        id: "shared",
        name: "Paddle",
        revision: 2,
        kind: "venv",
        status: "installed",
        recipe: "paddleocr-ort-directml",
        targetDevice: "cpu",
        pythonState: "ready",
        dependencyState: "installed",
        engineState: "ready",
        modelState: "ready",
        serviceState: "ready",
        configuredRecognitionTypes: ["text", "table", "structure", "document"],
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
  // 同闭包设备切换：切换命令发送用户选定设备，不触发新的安装计划。
  await section.getByRole("button", { name: "启动并验证当前环境" }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "switchEnvironment",
    arguments: { environmentId: "shared", device: "directml" },
  });
  // 同闭包重装仍可能产生明确移除的依赖（removed_dependencies 保留）：
  // 摘要如实列出，确认入口保持安装语义。
  await page.getByLabel("Paddle 推理后端", { exact: true }).selectOption("cpu");
  await sendState(page, "settings", {
    ...settings,
    environmentPlan: {
      planId: "b".repeat(32),
      environmentId: "shared",
      environmentRevision: 2,
      recipe: "paddleocr-ort-directml",
      requestedRecipe: "paddleocr-ort-directml",
      sourceIds: ["paddleocr-modelscope"],
      dependencies: [],
      removedDependencies: ["legacy-extra==1.0"],
      requiresServiceStop: true,
    },
  });
  await expect(
    page.getByText(/本次明确移除依赖：legacy-extra==1\.0/),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toBeVisible();
  await expect(page.getByText(/确认后暂停当前识别服务/)).toBeVisible();
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
    recipe: "paddleocr-ort-directml",
    targetDevice: "cpu",
    serviceState: "ready",
  };
  const settings = {
    environmentRecipes: recipes,
    environmentSources: sources,
    activeEnvironmentId: "shared",
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
    arguments: { recipe: "paddleocr-ort-directml" },
  });
  const compatibility = {
    recipe: "paddleocr-ort-directml",
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
    arguments: { environmentId: "shared", device: "directml" },
  });
  await sendState(page, "settings", { ...settings, environmentBusy: true });
  const complete = {
    ...settings,
    environments: [{ ...environment, targetDevice: "directml" }],
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
              c.arguments?.recipe === "paddleocr-ort-directml",
          ).length,
      ),
    )
    .toBe(2);
  await sendState(page, "settings", {
    ...complete,
    environmentCompatibility: compatibility,
  });
  await expect(
    page
      .locator(".engine-backend-section")
      .getByText("该兼容环境已是当前环境并通过启动验证，无需重装。", {
        exact: true,
      }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "重新查询兼容环境", exact: true }),
  ).toHaveCount(0);
});

test("named empty environment stays an explicit optional management entry", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  const settings = {
    environmentRecipes: recipes,
    environments: [],
    environmentBusy: false,
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  // #294：可选管理入口在常规页面直接可达；空名禁用，不插入普通准备路径。
  const create = page.getByRole("button", { name: "创建空环境" });
  await expect(create).toBeVisible();
  await expect(create).toBeDisabled();
  const name = page.getByLabel("环境名称");
  await name.fill("手动隔离");
  await expect(create).toBeEnabled();
  await create.click();
  await expectCommand(page, {
    scope: "settings",
    action: "createEnvironment",
    arguments: { name: "手动隔离" },
  });
  await sendState(page, "settings", { ...settings, environmentBusy: true });
  await expect(name).toBeDisabled();
  await expect(create).toBeDisabled();
});

test("install progress shows measured artifact bytes without a fake percentage", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  const base = {
    environmentRecipes: recipes,
    environments: [],
    environmentSupportsInstallProgress: true,
    maintenanceStatus: "旧运行时维护投影不得混入 managed 页面",
    maintenancePhase: "idle",
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings: base },
  });
  // managed 页面不渲染旧 RuntimeStatus 维护投影（#294）。
  await expect(page.getByText(/本次维护：旧运行时维护投影/)).toHaveCount(0);
  const progress = {
    attempt_id: "e2e-attempt",
    seq: 3,
    environment_id: "env",
    timestamp: "2026-10-08T00:00:00Z",
    phase: "download",
    state: "running",
    current: "下载依赖工件",
    dependency_total_known: true,
    download_files_total: 2,
    download_files_completed: 1,
    bytes_current: 2048,
    bytes_total: null,
    dependencies: [
      {
        name: "a",
        version: "1",
        download_state: "downloading",
        install_state: "pending",
      },
    ],
  };
  await sendState(page, "settings", {
    ...base,
    environmentInstallProgress: progress,
  });
  const panel = page.getByRole("region", { name: "依赖安装进度" });
  // 总量未知：显示已传字节与“总量未知”，不造百分比或总下载量。
  await expect(panel).toHaveAttribute("data-install-bytes-total", "unknown");
  await expect(panel).toContainText("当前依赖下载批次累计 2 KiB（总量未知）");
  // 字节跨待下载工件累计（received 汇总，total 为全部 HEAD 和）：切换到
  // 下一个工件时累计继续增长，不重置为单工件计数。
  await sendState(page, "settings", {
    ...base,
    environmentInstallProgress: {
      ...progress,
      seq: 4,
      bytes_current: 3072,
      bytes_total: 4096,
      dependencies: [
        {
          name: "a",
          version: "1",
          download_state: "downloaded",
          install_state: "pending",
        },
        {
          name: "b",
          version: "2",
          download_state: "downloading",
          install_state: "pending",
        },
      ],
    },
  });
  await expect(panel).toHaveAttribute("data-install-bytes-total", "4096");
  await expect(panel).toContainText("当前依赖下载批次累计 3 KiB / 4 KiB");
});

test("initial empty projection auto-loads the managed catalog without manual actions", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  // 实机首启接线契约：宿主只给构造默认状态时页面自动补齐目录/快照；
  // 判定用 typed 事实字段，状态文案可为任意文本。
  const initial = {
    environmentStatus: "【任意宿主文案】不参与加载判定",
    environmentSnapshotLoaded: false,
    environmentListFetchAttempted: false,
    environments: [],
    environmentRecipes: [],
    environmentBusy: false,
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings: initial },
  });
  await expect(page.getByText("运行环境状态尚未读取")).toBeVisible();
  await expectCommand(page, {
    scope: "settings",
    action: "refreshEnvironmentList",
    arguments: {},
  });
  const refreshCount = () =>
    page.evaluate(
      () =>
        (
          window as Window & {
            __testHost: { commands: { scope: string; action: string }[] };
          }
        ).__testHost.commands.filter(
          (command) =>
            command.scope === "settings" &&
            command.action === "refreshEnvironmentList",
        ).length,
    );
  await expect.poll(refreshCount).toBe(1);
  // 真实快照到达：无需手动“查看环境清单”，聚焦分区自动只读查询。
  await sendState(page, "settings", {
    ...initial,
    environmentStatus: "运行环境状态已更新",
    environmentSnapshotLoaded: true,
    environmentSources: sources,
    environmentRecipes: recipes,
  });
  await expectCommand(page, {
    scope: "settings",
    action: "findCompatibleEnvironment",
    arguments: { recipe: "paddleocr-ort-directml" },
  });
  // 宿主只读查询回包（无兼容环境）后才出现准备入口，无需任何手动操作。
  await sendState(page, "settings", {
    ...initial,
    environmentStatus: "运行环境状态已更新",
    environmentSnapshotLoaded: true,
    environmentSources: sources,
    environmentRecipes: recipes,
    environmentCompatibility: {
      recipe: "paddleocr-ort-directml",
      selectedEnvironmentId: null,
    },
  });
  await expect(
    page.getByRole("button", { name: "准备此后端", exact: true }),
  ).toBeVisible();
  await expect.poll(refreshCount).toBe(1);
});
