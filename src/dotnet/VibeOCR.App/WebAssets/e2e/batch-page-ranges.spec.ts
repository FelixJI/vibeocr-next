import { expect, test } from "@playwright/test";
import { expectCommand, mountHost, snapshot } from "./workbench-host";

test("each file keeps an independent page range across recognition pipelines", async ({
  page,
}) => {
  await mountHost(page, {
    ...snapshot,
    route: "batch",
    capabilities: ["batch.add", "batch.run", "recognition.engine"],
    features: {
      batch: {
        isRunning: false,
        itemCount: 3,
        completedCount: 0,
        failedCount: 0,
        items: [
          {
            id: "first",
            name: "第一份.pdf",
            statusCode: "batch.item.pending",
            pageRange: "1-3",
          },
          {
            id: "second",
            name: "第二份.pdf",
            statusCode: "batch.item.pending",
            pageRange: "2",
          },
          {
            id: "office",
            name: "表格.xlsx",
            statusCode: "batch.item.pending",
            supportsPageRange: false,
          },
        ],
      },
    },
  });
  const first = page.getByRole("textbox", { name: "第一份.pdf 的页码范围" });
  await first.fill("2,r1");
  await first.press("Tab");
  await expectCommand(page, {
    scope: "batch",
    action: "setItemPageRange",
    arguments: {
      itemId: "first",
      pageRange: "2,r1",
    },
  });
  await expect(
    page.getByRole("textbox", { name: "第二份.pdf 的页码范围" }),
  ).toHaveValue("2");
  await expect(
    page.getByText("整篇解析；按页识别请先转换为 PDF"),
  ).toBeVisible();
  await expect(
    page.getByRole("textbox", { name: "表格.xlsx 的页码范围" }),
  ).toHaveCount(0);
});
