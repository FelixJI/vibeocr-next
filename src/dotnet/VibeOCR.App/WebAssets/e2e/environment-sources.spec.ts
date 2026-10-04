import { expect, test } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

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
    isDefault: true,
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
    isDefault: true,
  },
  {
    kind: "mineru_model_registry",
    id: "mineru-modelscope",
    displayName: "ModelScope",
    endpoint: "https://www.modelscope.cn",
  },
];

test("global sources show a concrete default and independent engine choices", async ({
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
        environmentResolvedDefaultSources: sources
          .filter((source) => source.isDefault)
          .map((source) => ({ ...source, origin: "product_default" })),
        environments: [],
        environmentRecipes: [],
        environmentBusy: false,
        environmentStatus: "来源设置已读取",
      },
    },
  });
  await page.getByText("高级：环境与依赖管理", { exact: true }).click();
  await page.locator("details.managed-source-defaults > summary").click();
  const packages = page.getByLabel("全局依赖包来源", { exact: true });
  await expect(packages).toHaveValue("tuna-pypi");
  await expect(packages.locator("option[value='']")).toHaveCount(0);
  await expect(packages.locator("option[value='tuna-pypi']")).toHaveText(
    "TUNA PyPI 镜像（默认）",
  );
  const paddle = page.getByLabel("全局 PaddleOCR 模型来源", { exact: true });
  const mineru = page.getByLabel("全局 MinerU 模型来源", { exact: true });
  await paddle.selectOption("paddleocr-bos");
  await expect(mineru).toHaveValue("mineru-huggingface");
  await expect(mineru.locator("option")).toHaveCount(2);
  await mineru.selectOption("mineru-modelscope");
  await expect(paddle).toHaveValue("paddleocr-bos");
  await page
    .locator("details.managed-source-defaults")
    .screenshot({ path: "test-results/environment-sources.png" });
  await page
    .getByRole("button", { name: "保存全局默认来源", exact: true })
    .click();
  await expectCommand(page, {
    scope: "settings",
    action: "setEnvironmentSources",
    arguments: {
      packageSourceId: "tuna-pypi",
      paddleocrModelSourceId: "paddleocr-bos",
      mineruModelSourceId: "mineru-modelscope",
    },
  });
});
