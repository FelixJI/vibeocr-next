import { expect, test } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

test("MinerU parameters use the runtime catalog and show remote limitations", async ({
  page,
}) => {
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: ["settings.runtime"],
    features: {
      settings: {
        isBusy: false,
        theme: "system",
        startupEnabled: false,
        mineruConnection: {
          supported: true,
          mode: "remote",
          apiUrl: "https://example.com",
          hasApiKey: false,
        },
        mineruRecognition: {
          supported: true,
          stored: true,
          invalid: false,
          tier: "basic",
          defaultTier: "basic",
          ocrMode: "auto",
          pageRange: "all",
          language: "ch",
          tiers: [
            { id: "flash", availability: "ready" },
            { id: "basic", availability: "ready" },
            { id: "advanced", availability: "unavailable" },
          ],
          languages: ["ch", "korean"],
        },
      },
    },
  });
  await page.getByLabel("识别档位", { exact: true }).selectOption("flash");
  await page.getByLabel("OCR 模式", { exact: true }).selectOption("txt");
  await page.getByLabel("默认页码范围", { exact: true }).fill("2-r1");
  await expect(
    page.getByText("由服务端配置（客户端无法读取/覆盖）"),
  ).toBeVisible();
  await expect(
    page.getByText(
      "公式/表格识别：由档位与服务端决定；当前 MinerU 4 接口无独立开关。",
    ),
  ).toBeVisible();
  await expect(
    page.locator(".settings-mineru-recognition-panel"),
  ).toHaveScreenshot("mineru-recognition-remote.png");
  await page.getByRole("button", { name: "保存识别参数", exact: true }).click();
  await expectCommand(page, {
    scope: "settings",
    action: "setMineruRecognition",
    arguments: {
      tier: "flash",
      ocrMode: "txt",
      pageRange: "2-r1",
      language: "ch",
    },
  });
});
