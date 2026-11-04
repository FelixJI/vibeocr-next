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

test("paddle device switch reuses the shipped closure without install flows", async ({
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
        activeEnvironmentId: "env-1",
        environmentRecipes: recipes,
        environmentBusy: false,
      },
    },
  });
  // Paddle 分区：单一设备选择（统一闭包 = DirectML + CPU 切换）；不再有
  // 组件/能力套餐下拉，也没有兼容查询/准备入口。
  const backend = page.getByLabel("Paddle 推理后端", { exact: true });
  await expect(backend).toHaveValue("directml");
  await expect(backend.locator("option")).toHaveCount(2);
  await expect(page.getByLabel("识别组件")).toHaveCount(0);
  await expect(page.getByLabel("加速方式")).toHaveCount(0);
  await expect(page.locator("#managed-recipe-select")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "准备此后端" })).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "查询可复用环境" }),
  ).toHaveCount(0);
  await expect(
    page.getByText(/文字、表格与版面结构识别共用同一套 PaddleOCR 依赖/),
  ).toBeVisible();
  await expect(
    page
      .locator(".engine-backend-section")
      .getByText(/依赖随包提供，可离线安装/),
  ).toBeVisible();
  await expect(page.getByText(/当前运行环境：统一推理闭包/)).toBeVisible();
  // 设备切换即 switchEnvironment：携带用户选定设备，同闭包不触发安装。
  await backend.selectOption("cpu");
  await expectCommand(page, {
    scope: "settings",
    action: "switchEnvironment",
    arguments: { environmentId: "env-1", device: "cpu" },
  });
});

test("mineru environments stay read-only with active removal protection", async ({
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
  // 预构建运行时树：MinerU 分区只读展示共享闭包状态与设备切换，
  // 不再有查询/安装入口；设备下拉随活动环境快照回显。
  await expect(
    mineruSection.getByRole("button", { name: "查询可复用环境" }),
  ).toHaveCount(0);
  const mineruDevice = mineruSection.getByLabel("MinerU 运行设备");
  await expect(mineruDevice).toHaveValue("directml");
  await expect(mineruDevice.locator("option")).toHaveCount(2);
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
  const mineruDevice = page.getByLabel("MinerU 运行设备", { exact: true });
  await expect(backend.locator("option")).toHaveCount(2);
  await expect(mineruDevice.locator("option")).toHaveCount(2);
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
  // 真实快照与目录到达：无需手动“查看环境清单”；预构建运行时树下
  // 目录到达不再触发兼容查询/准备等安装面命令。
  await sendState(page, "settings", {
    ...initial,
    environmentStatus: "运行环境状态已更新",
    environmentSnapshotLoaded: true,
    environmentSources: sources,
    environmentRecipes: recipes,
  });
  const installCommands = () =>
    page.evaluate(
      () =>
        (
          window as Window & {
            __testHost: { commands: { scope: string; action: string }[] };
          }
        ).__testHost.commands.filter(
          (command) =>
            command.scope === "settings" &&
            command.action !== "refreshEnvironmentList",
        ).length,
    );
  await expect.poll(installCommands).toBe(0);
  await expect(
    page.getByRole("button", { name: "准备此后端", exact: true }),
  ).toHaveCount(0);
  await expect.poll(refreshCount).toBe(1);
});
