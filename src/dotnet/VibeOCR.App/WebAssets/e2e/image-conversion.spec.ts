import { expect, test } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  sendState,
  snapshot,
} from "./workbench-host";

test("image conversion is a separate workspace without OCR or toolbar settings", async ({
  page,
}, testInfo) => {
  await mountHost(
    page,
    {
      ...snapshot,
      route: "batch",
      capabilities: [...snapshot.capabilities, "imageConversion.convert"],
      features: {
        batch: {
          itemCount: 1,
          items: [
            {
              id: "ocr-item",
              name: "识别文档.pdf",
              statusCode: "batch.item.pending",
            },
          ],
        },
        imageConversion: {
          itemCount: 1,
          items: [{ id: "convert-item", name: "转换图片.png" }],
          windowStart: 0,
        },
      },
    },
    "/#/batch",
  );
  await expect(
    page.getByRole("region", { name: "批量图片预设输出" }),
  ).toHaveCount(0);
  await expect(page.getByText("识别文档.pdf", { exact: true })).toBeVisible();
  await page.getByRole("link", { name: "图像转换", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "图像转换", exact: true }),
  ).toBeVisible();
  await expect(page.getByText("转换图片.png", { exact: true })).toBeVisible();
  await expect(page.getByText("识别文档.pdf", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "开始识别" })).toHaveCount(0);
  await page.getByText("输出预设", { exact: true }).click();
  await expect(page.getByLabel("预设格式", { exact: true })).toBeVisible();
  await expect(
    page.getByText("复制成功后关闭编辑会话", { exact: true }),
  ).toHaveCount(0);
  await page.screenshot({
    path: testInfo.outputPath("image-conversion-workspace.png"),
  });
  await page.getByRole("button", { name: "清空队列", exact: true }).click();
  await expectCommand(page, {
    scope: "imageConversion",
    action: "clear",
    arguments: {},
  });
  await sendState(page, "imageConversion", { itemCount: 0, items: [] });
  await page.getByRole("link", { name: "批量识别", exact: true }).click();
  await expect(page.getByText("识别文档.pdf", { exact: true })).toBeVisible();
});
