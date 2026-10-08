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
    displayName: "Hugging Face",
    endpoint: "https://huggingface.co",
  },
  {
    kind: "paddleocr_model_registry",
    id: "paddleocr-modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
  },
  {
    kind: "paddleocr_model_registry",
    id: "paddleocr-bos",
    displayName: "百度 BOS",
    endpoint: "https://paddle-model-ecology.bj.bcebos.com",
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-huggingface",
    displayName: "Hugging Face",
    endpoint: "https://huggingface.co",
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
  },
];

const recipes = [
  {
    id: "rapidocr-cpu",
    displayName: "RapidOCR · CPU",
    configuredRecognitionTypes: ["text"],
    accelerator: "cpu",
    targetDevice: "cpu",
  },
  {
    id: "rapidocr+mineru-cpu",
    displayName: "RapidOCR + MinerU · CPU",
    configuredRecognitionTypes: ["text", "document"],
    accelerator: "cpu",
    targetDevice: "cpu",
  },
  {
    id: "rapidocr+mineru-cuda",
    displayName: "RapidOCR + MinerU · NVIDIA CUDA",
    configuredRecognitionTypes: ["text", "document"],
    accelerator: "nvidia_cuda",
    targetDevice: "cuda",
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
            displayName: "ModelScope",
            origin: "product_default",
          },
          {
            kind: "mineru_model_registry",
            id: "mineru-modelscope",
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

test("recipe choice binds component and accelerator without advanced controls", async ({
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
  // 默认选择 = 文字识别 · CPU（RapidOCR），没有第二套“锁定依赖配方”入口。
  await expect(page.getByLabel("识别组件", { exact: true })).toHaveValue(
    "rapidocr",
  );
  await expect(page.getByLabel("加速方式", { exact: true })).toHaveValue("cpu");
  await expect(page.locator("#managed-recipe-select")).toHaveCount(0);
  // 快照已同步但未启动：如实展示默认配置“尚未启动”，不伪造就绪。
  await expect(
    page.getByText("默认运行环境：RapidOCR · CPU（尚未启动）"),
  ).toBeVisible();
  await expect(
    page.getByText(/目标配置：RapidOCR · CPU（文字识别，设备 cpu/),
  ).toBeVisible();
  // 实际支持选项完整展示：文档解析用途来自 Runtime 目录分组。
  await page
    .getByLabel("识别组件", { exact: true })
    .selectOption("rapidocr+mineru");
  await expect(
    page.getByText(/目标配置：RapidOCR \+ MinerU · CPU/),
  ).toBeVisible();
  await expect(
    page.locator("details.managed-environment-advanced"),
  ).toHaveCount(0);
  await expect(page.getByLabel("新环境名称（留空自动命名）")).toHaveCount(0);
  await page.getByRole("button", { name: "继续准备依赖", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "previewEnvironmentInstall",
    arguments: {
      environmentId: "env-1",
      recipe: "rapidocr+mineru-cpu",
    },
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
      recipe: "rapidocr-cpu",
      selectedEnvironmentId: null,
    },
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  await page.getByRole("button", { name: "准备此配置", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "prepareEnvironment",
    arguments: { recipe: "rapidocr-cpu" },
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
      requestedRecipe: "rapidocr-cpu",
      recipe: "rapidocr-cpu",
      sourceIds: ["tuna-pypi"],
      requestedSourceIds: null,
      dependencies: [
        "rapidocr==3.9.2",
        "onnxruntime==1.24.4",
        "hidden-transitive==1.0",
      ],
    },
  };
  await sendState(page, "settings", prepared);
  const confirmation = page.locator(".runtime-install-plan");
  await expect(
    confirmation.getByText(
      /主要组件：rapidocr==3.9.2、onnxruntime==1.24.4；\s*共 3 项依赖/,
    ),
  ).toBeVisible();
  await expect(confirmation.getByText(/hidden-transitive/)).not.toBeVisible();
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("识别组件", { exact: true })
    .selectOption("rapidocr+mineru");
  await expect(
    page.getByRole("button", { name: "确认安装依赖", exact: true }),
  ).toHaveCount(0);
  // 即使旧预览换了 planId 迟到，也不能挪进环境行继续确认。
  await sendState(page, "settings", {
    ...prepared,
    environmentPlan: { ...prepared.environmentPlan, planId: "late-plan" },
  });
  await expect(page.getByLabel("识别组件", { exact: true })).toHaveValue(
    "rapidocr+mineru",
  );
  await expect(page.locator(".runtime-install-plan")).toHaveCount(0);
  await page.getByRole("link", { name: "单次识别", exact: true }).click();
  await sendState(page, "settings", prepared);
  await page.getByRole("link", { name: "设置", exact: true }).click();
  await expect(page.getByLabel("识别组件", { exact: true })).toHaveValue(
    "rapidocr",
  );
  await page.getByRole("button", { name: "确认安装依赖", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "confirmEnvironmentInstall",
    arguments: { planId: "plan-one" },
  });
});

test("catalog exposes all six exact recipes and permanently rejects duplicate combinations", async ({
  page,
}) => {
  const allRecipes = [
    ...recipes,
    {
      id: "mineru-cpu",
      displayName: "MinerU · CPU",
      configuredRecognitionTypes: ["document"],
      accelerator: "cpu",
      targetDevice: "cpu",
      dependencies: ["mineru==4.0.10"],
    },
    {
      id: "paddleocr-cpu",
      displayName: "PaddleOCR · CPU",
      configuredRecognitionTypes: ["text"],
      accelerator: "cpu",
      targetDevice: "cpu",
      dependencies: ["paddleocr==3.4.0", "paddlepaddle==3.2.2"],
    },
    {
      id: "paddleocr-cuda",
      displayName: "PaddleOCR · NVIDIA CUDA",
      configuredRecognitionTypes: ["text"],
      accelerator: "nvidia_cuda",
      targetDevice: "cuda",
      dependencies: ["paddleocr==3.4.0", "paddlepaddle-gpu==3.2.2"],
    },
  ];
  const settings = {
    environmentRecipes: allRecipes,
    environments: [],
    environmentHardware: { nvidiaDriverStatus: "ok" },
  };
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["runtime.environments"],
    features: { settings },
  });
  const component = page.getByLabel("识别组件", { exact: true });
  const accelerator = page.getByLabel("加速方式", { exact: true });
  await expect(component.locator("option")).toHaveCount(4);
  for (const recipe of allRecipes) {
    const base = recipe.id.replace(/-(cpu|cuda)$/, "");
    await component.selectOption(base);
    await accelerator.selectOption(recipe.accelerator);
    await expectCommand(page, {
      scope: "settings",
      action: "findCompatibleEnvironment",
      arguments: { recipe: recipe.id },
    });
    if (base === "rapidocr" || base === "mineru")
      await expect(accelerator.locator("option")).toHaveCount(1);
  }
  await component.selectOption("paddleocr");
  await accelerator.selectOption("nvidia_cuda");
  await expect(
    page.getByText(/主要依赖：paddleocr==3.4.0、paddlepaddle-gpu==3.2.2/),
  ).toBeVisible();
  await sendState(page, "settings", {
    ...settings,
    environmentRecipes: [recipes[0], recipes[0], recipes[0]],
  });
  await expect(page.getByRole("alert")).toHaveText(/重复或设备信息冲突/);
  await expect(component).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "准备此配置", exact: true }),
  ).toHaveCount(0);
});
