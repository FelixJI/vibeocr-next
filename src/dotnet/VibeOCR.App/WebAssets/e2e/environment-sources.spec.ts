import { expect, test } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

test("device setting uses the committed host value and keeps model sources independent", async ({
  page,
}) => {
  await mountHost(page, {
    ...snapshot,
    route: "settings",
    capabilities: [
      "settings.selection",
      "settings.recognition-device",
      "runtime.refresh",
    ],
    features: {
      settings: {
        recognitionDevice: "cpu",
        sources: [
          {
            kind: "paddleocr_model_registry",
            id: "paddleocr-modelscope",
            displayName: "ModelScope",
            selected: true,
          },
          {
            kind: "paddleocr_model_registry",
            id: "paddleocr-huggingface",
            displayName: "Hugging Face",
            selected: false,
          },
          {
            kind: "mineru_model_registry",
            id: "mineru-modelscope",
            displayName: "ModelScope",
            selected: true,
          },
        ],
      },
    },
  });
  await expect(page.getByLabel("计算设备")).toHaveValue("cpu");
  await page.getByLabel("计算设备").selectOption("directml");
  await expectCommand(page, {
    scope: "settings",
    action: "setRecognitionDevice",
    arguments: { device: "directml" },
  });
  await page
    .getByLabel("PaddleOCR 模型来源")
    .selectOption("paddleocr-huggingface");
  await expectCommand(page, {
    scope: "settings",
    action: "setSource",
    arguments: {
      kind: "paddleocr_model_registry",
      sourceId: "paddleocr-huggingface",
    },
  });
  await expect(page.getByLabel("MinerU 模型来源")).toHaveValue(
    "mineru-modelscope",
  );
  await expect(page.getByRole("button", { name: "查看环境清单" })).toHaveCount(
    0,
  );
});
