import { test } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

test("QR content and caption settings generate through the host without clicking generate", async ({
  page,
}) => {
  await mountHost(
    page,
    { ...snapshot, route: "qrcode", capabilities: ["qrcode.generate"] },
    "/#/qrcode",
  );
  await page.getByLabel("输入内容", { exact: true }).fill("自动生成二维码");
  await expectCommand(page, {
    scope: "qrcode",
    action: "generate",
    arguments: {
      text: "自动生成二维码",
      format: "qrcode",
      captionMode: "off",
      captionText: "",
    },
  });
  await page.getByText("底部文字", { exact: true }).click();
  await page.getByLabel("底部文字", { exact: true }).selectOption("custom");
  await page.getByLabel("独立说明", { exact: true }).fill("最新说明");
  await expectCommand(page, {
    scope: "qrcode",
    action: "generate",
    arguments: {
      text: "自动生成二维码",
      format: "qrcode",
      captionMode: "custom",
      captionText: "最新说明",
    },
  });
});
