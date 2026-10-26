import { expect, test, type Page } from "@playwright/test";
import {
  expectCommand,
  mountHost,
  rejectNextCommand,
  snapshot,
} from "./workbench-host";

const STRUCTURED_URL = "/structured.json";
const RESULT_URL = "/result.txt";
const INPUT_URL = "/input.png";
const RESULT_IMAGE_URL = "/result-image.png";
const DOC_IMAGE_URL = "/doc-image.png";

/** 记录默认行为是否被取消的页面内探针（见行编辑测试）。 */
type Pr225EventsWindow = Window & {
  __pr225Events?: Array<[string, boolean]>;
};

const structuredPayload = [
  { type: "text", text: "前置文本块" },
  { type: "formula", text: "a^2+b^2=c^2" },
  {
    type: "table",
    table: {
      schema_version: 1,
      row_count: 2,
      column_count: 2,
      cells: [
        {
          row: 0,
          column: 0,
          rowspan: 1,
          colspan: 1,
          text: "甲",
          is_header: true,
        },
        { row: 0, column: 1, rowspan: 1, colspan: 1, text: "乙" },
        { row: 1, column: 0, rowspan: 1, colspan: 1, text: "丙" },
        { row: 1, column: 1, rowspan: 1, colspan: 1, text: "丁" },
      ],
    },
  },
  {
    type: "lines",
    schema_version: 1,
    lines: [
      { text: "第一段行一", score: 0.93, bbox: [20, 20, 700, 120] },
      { text: "低置信行", score: 0.58, bbox: [20, 150, 700, 250] },
    ],
    modes: {
      raw: "第一段行一\n低置信行",
      line: "第一段行一\n低置信行",
      merged: "第一段行一低置信行",
      smart: "第一段行一\n\n低置信行",
    },
  },
  {
    // 稀疏 + 合并混合：验证列对齐与选择高亮交集。
    type: "table",
    table: {
      schema_version: 1,
      row_count: 3,
      column_count: 3,
      cells: [
        {
          row: 0,
          column: 0,
          rowspan: 2,
          colspan: 2,
          text: "合并块",
          is_header: true,
        },
        { row: 0, column: 2, rowspan: 1, colspan: 1, text: "右上" },
        { row: 2, column: 2, rowspan: 1, colspan: 1, text: "右下" },
      ],
    },
  },
  {
    // 携带识别文本的图片区块（MinerU 风格）：预览与文字并列呈现。
    type: "image",
    text: "插图说明文字",
    image: {
      resource: { url: DOC_IMAGE_URL, mediaType: "image/png" },
    },
  },
  {
    // 超限稀疏大表（101×101=10201 格）：降级为提示 + 导出替代，不提供
    // 剪贴板复制（宿主同样拒绝超限网格，旧实现会冻结 UI）。
    type: "table",
    table: {
      schema_version: 1,
      row_count: 101,
      column_count: 101,
      cells: [{ row: 0, column: 0, rowspan: 1, colspan: 1, text: "唯一值" }],
    },
  },
  { type: "chart", text: "无预览的图表内容", img_path: "chart.png" },
  { type: "seal", text: "无预览的印章文字" },
];

// 真实 1000×500 PNG（浏览器可解码）：校对视图行框可实际操作。
const PROOF_PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAA+gAAAH0CAYAAACuKActAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAAGdYAABnWARjRyu0AAAwRSURBVHhe7dcxAQAgDMCw+TcN/wRAj+Ssg84BAAAAvpsdAAAAgPcMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACDDoAAAAEGDQAQAAIMCgAwAAQIBBBwAAgACDDgAAAAEGHQAAAAIMOgAAAAQYdAAAAAgw6AAAABBg0AEAACDAoAMAAECAQQcAAIAAgw4AAAABBh0AAAACDDoAAAAEGHQAAAAIMOgAAAAQYNABAAAgwKADAABAgEEHAACAAIMOAAAAAQYdAAAAAgw6AAAABBh0AAAACLiYIcOKw6EvYQAAAABJRU5ErkJggg==",
  "base64",
);

async function mountRecognitionResults(page: Page): Promise<void> {
  await page.route(STRUCTURED_URL, (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify(structuredPayload),
    }),
  );
  await page.route(RESULT_URL, (route) =>
    route.fulfill({ contentType: "text/plain", body: "第一段行一\n低置信行" }),
  );
  await page.route(INPUT_URL, (route) =>
    route.fulfill({ contentType: "image/png", body: PROOF_PNG }),
  );
  await page.route(RESULT_IMAGE_URL, (route) =>
    route.fulfill({ contentType: "image/png", body: PROOF_PNG }),
  );
  await page.route(DOC_IMAGE_URL, (route) =>
    route.fulfill({ contentType: "image/png", body: PROOF_PNG }),
  );
  await mountHost(page, {
    ...snapshot,
    features: {
      recognition: {
        isBusy: false,
        statusCode: "recognition.completed",
        input: {
          url: INPUT_URL,
          mediaType: "image/png",
          byteLength: PROOF_PNG.byteLength,
        },
        result: {
          url: RESULT_URL,
          mediaType: "text/plain",
          byteLength: 8,
        },
        structuredResult: {
          url: STRUCTURED_URL,
          mediaType: "application/json",
          byteLength: 8,
        },
        resultImage: {
          url: RESULT_IMAGE_URL,
          mediaType: "image/png",
          byteLength: PROOF_PNG.byteLength,
        },
      },
    },
  });
}

test("reading order renders real content and block actions use original indexes", async ({
  page,
}, testInfo) => {
  await mountRecognitionResults(page);

  // 阅读顺序：文本段 → 公式（KaTeX）→ 表格 → 行列表，无英文 type 列表。
  // th 角色在 Playwright ARIA 引擎下不稳定，用文本定位器断言。
  await expect(page.getByText("前置文本块")).toBeVisible();
  await expect(page.locator(".formula-preview math")).toBeVisible();
  await expect(page.locator("th").filter({ hasText: "甲" })).toBeVisible();
  await expect(page.getByText(/共 2 行，低置信（<80%）1 个/)).toBeVisible();

  // 混排原始 blockIndex：table=2、formula=1（多表格时按区域作用域定位）。
  await page
    .getByRole("region", { name: "表格 1" })
    .getByRole("button", { name: "复制表格 HTML / TSV" })
    .click();
  await expectCommand(page, {
    scope: "recognition",
    action: "copyStructured",
    arguments: { resourceUri: STRUCTURED_URL, blockIndex: 2, format: "table" },
  });
  await page.getByRole("button", { name: "复制 LaTeX" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "copyStructured",
    arguments: { resourceUri: STRUCTURED_URL, blockIndex: 1, format: "latex" },
  });

  // 模式复制/导出按宿主权威 modes 提交。
  await page.getByRole("combobox", { name: "排版模式" }).selectOption("smart");
  await expect(
    page
      .getByRole("region", { name: "识别行文本" })
      .locator(".result-document"),
  ).toContainText("第一段行一\n\n低置信行");
  await page.getByRole("button", { name: "复制文本" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "copyLines",
    arguments: { resourceUri: STRUCTURED_URL, mode: "smart" },
  });
  const resultPanel = page.getByRole("region", {
    name: "识别结果",
    exact: true,
  });
  const panelBox = await resultPanel.boundingBox();
  for (const format of ["Markdown", "Word", "Excel"]) {
    const button = page.getByRole("button", {
      name: `导出 ${format}`,
      exact: true,
    });
    await expect(button).toBeVisible();
    const box = await button.boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(panelBox!.x);
    expect(box!.x + box!.width).toBeLessThanOrEqual(
      panelBox!.x + panelBox!.width,
    );
    expect(box!.height).toBeLessThanOrEqual(36);
  }
  await page.getByRole("button", { name: "导出 Word" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "export",
    arguments: { format: "docx", textMode: "smart" },
  });
  await page.screenshot({
    path: testInfo.outputPath("reading-order.png"),
  });
});

test("table cell selection via drag, shift-click and keyboard", async ({
  page,
}, testInfo) => {
  await mountRecognitionResults(page);

  // 拖选：甲（th）→ 乙；th 角色在 Playwright ARIA 引擎下不稳定，用文本定位。
  const first = page.locator("th").filter({ hasText: "甲" });
  const second = page.getByRole("cell", { name: "乙" });
  await first.scrollIntoViewIfNeeded();
  const firstBox = await first.boundingBox();
  const secondBox = await second.boundingBox();
  expect(firstBox).not.toBeNull();
  expect(secondBox).not.toBeNull();
  await page.mouse.move(
    firstBox!.x + firstBox!.width / 2,
    firstBox!.y + firstBox!.height / 2,
  );
  await page.mouse.down();
  await page.mouse.move(
    secondBox!.x + secondBox!.width / 2,
    secondBox!.y + secondBox!.height / 2,
    { steps: 4 },
  );
  await page.mouse.up();
  await expect(first).toHaveClass(/cell-selected/);
  await expect(second).toHaveClass(/cell-selected/);
  await page.getByRole("button", { name: "复制所选单元格" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "copyStructuredCells",
    arguments: {
      resourceUri: STRUCTURED_URL,
      blockIndex: 2,
      rowStart: 0,
      rowEnd: 0,
      columnStart: 0,
      columnEnd: 1,
    },
  });

  // 键盘：聚焦甲后 Shift+ArrowDown 扩展到整列。
  await first.click();
  await expect(first).toBeFocused();
  await page.keyboard.press("Shift+ArrowDown");
  await expect(page.getByRole("cell", { name: "丙" })).toHaveClass(
    /cell-selected/,
  );
  await page.getByRole("button", { name: "复制所选单元格" }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "copyStructuredCells",
    arguments: {
      resourceUri: STRUCTURED_URL,
      blockIndex: 2,
      rowStart: 0,
      rowEnd: 1,
      columnStart: 0,
      columnEnd: 0,
    },
  });

  // 失败不静默：宿主拒绝时展示错误。
  await rejectNextCommand(page);
  await page.getByRole("button", { name: "复制所选单元格" }).click();
  await expect(page.getByText(/复制所选单元格失败/)).toBeVisible();

  // 稀疏 + 合并：第 2 行只有 col2 单元格，col0 为空占位；合并块跨入
  // 选择矩形时高亮（与宿主交集语义一致）。
  await expect(
    page.locator('[data-row="2"][data-column="0"].cell-empty'),
  ).toHaveCount(1);
  await expect(page.locator('[data-row="2"][data-column="2"]')).toHaveText(
    "右下",
  );
  const merged = page.locator("th").filter({ hasText: "合并块" });
  await expect(merged).toHaveAttribute("rowspan", "2");
  await merged.click();
  await expect(merged).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(
    page.getByRole("cell", { name: "右上", exact: true }),
  ).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await expect(
    page.getByRole("cell", { name: "右下", exact: true }),
  ).toBeFocused();
  await merged.click();
  await page
    .locator('[data-row="0"][data-column="2"]')
    .click({ modifiers: ["Shift"] });
  await expect(merged).toHaveClass(/cell-selected/);
  await page.screenshot({
    path: testInfo.outputPath("cell-selection.png"),
  });
});

test("oversized sparse tables degrade to export guidance without clipboard copy", async ({
  page,
}, testInfo) => {
  await mountRecognitionResults(page);

  // 降级契约与宿主面积上限一致：不物化网格、不提供整表/范围剪贴板
  // 复制，明确引导导出替代。
  const region = page.getByRole("region", { name: "表格 3" });
  await expect(region).toBeVisible();
  await expect(region.locator(".structured-table-scroll table")).toHaveCount(0);
  await expect(
    region.getByRole("button", { name: "复制表格 HTML / TSV" }),
  ).toHaveCount(0);
  await expect(
    region.getByRole("button", { name: "复制所选单元格" }),
  ).toHaveCount(0);
  await expect(region.getByText(/表格较大（超过 10000 格）/)).toBeVisible();
  await region.screenshot({
    path: testInfo.outputPath("large-table-export.png"),
  });
});

test("image block keeps text within panel bounds; line editor keeps native Enter", async ({
  page,
}, testInfo) => {
  await mountRecognitionResults(page);

  // 高分辨率图片区块（1000×500）：预览不得按固有尺寸撞破结果面板。
  const image = page.getByRole("img", { name: "文档图像区块" });
  await expect(image).toBeVisible();
  await expect(page.getByText("插图说明文字")).toBeVisible();
  await expect(page.getByText("无预览的图表内容")).toBeVisible();
  await expect(page.getByText("无预览的印章文字")).toHaveCount(0);
  const resultPanel = page.getByRole("region", {
    name: "识别结果",
    exact: true,
  });
  const panelBox = await resultPanel.boundingBox();
  const imageBox = await image.boundingBox();
  expect(panelBox).not.toBeNull();
  expect(imageBox).not.toBeNull();
  expect(imageBox!.width).toBeLessThanOrEqual(panelBox!.width);
  expect(imageBox!.x).toBeGreaterThanOrEqual(panelBox!.x);
  expect(imageBox!.x + imageBox!.width).toBeLessThanOrEqual(
    panelBox!.x + panelBox!.width,
  );
  expect(imageBox!.height).toBeLessThanOrEqual(384); // max-height: 24rem。
  await page.screenshot({ path: testInfo.outputPath("image-bounds.png") });

  // 每行模式：真实键盘下编辑框内 Enter 换行、双击默认行为不被行
  // 触发器取消（修复前 Enter 被 li 冒泡层 preventDefault 吞掉）。
  await page.getByRole("combobox", { name: "排版模式" }).selectOption("line");
  const line = page
    .locator(".result-lines-list .result-line")
    .filter({ hasText: "第一段行一" });
  await line.dblclick();
  const editor = page.getByRole("textbox", { name: "编辑第 1 行" });
  await expect(editor).toBeFocused();
  await page.evaluate(() => {
    const scope = window as Pr225EventsWindow;
    scope.__pr225Events = [];
    for (const type of ["keydown", "dblclick"] as const)
      window.addEventListener(type, (event) =>
        scope.__pr225Events!.push([type, event.defaultPrevented]),
      );
  });
  await page.keyboard.press("Enter");
  await editor.dblclick();
  const events = await page.evaluate(
    () => (window as Pr225EventsWindow).__pr225Events,
  );
  expect(events).toEqual([
    ["keydown", false],
    ["dblclick", false],
  ]);
  // 编辑框初始全选：Enter 默认行为把选区替换为换行符。
  await expect(editor).toHaveValue("\n");
});

test("bbox proofreading edits through the frozen result image", async ({
  page,
}, testInfo) => {
  await mountRecognitionResults(page);

  // 校对视图使用 resultImage（非会话基准图）。
  const image = page
    .getByRole("region", { name: "输入图像", exact: true })
    .getByRole("img", { name: "识别结果参考图" });
  await expect(image).toHaveAttribute("src", RESULT_IMAGE_URL);
  await expect(
    page
      .getByRole("region", { name: "识别结果", exact: true })
      .getByRole("img", { name: "识别结果参考图" }),
  ).toHaveCount(0);

  // 双击行框编辑；宿主拒绝时保留草稿，重试成功后收起。
  const line = page.getByLabel("第一段行一", { exact: true });
  await expect(line).toBeVisible();
  await expect(line).toHaveText("");
  await expect(line).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");

  // 双向悬停高亮：默认 raw 原文可精确映射行文本，图上/右侧任一侧
  // 悬停时两侧同步加亮，移开后同步熄灭。
  const rawSpan = page
    .locator(".result-document .result-raw-line", { hasText: "第一段行一" })
    .first();
  await expect(rawSpan).toBeVisible();
  await line.hover();
  await expect(rawSpan).toHaveClass(/line-hovered/);
  await page.screenshot({
    path: testInfo.outputPath("hover-bbox-to-result.png"),
  });
  await rawSpan.hover();
  await expect(line).toHaveClass(/hovered/);
  await page.screenshot({
    path: testInfo.outputPath("hover-result-to-bbox.png"),
  });
  await page.mouse.move(4, 4);
  await expect(rawSpan).not.toHaveClass(/line-hovered/);
  await expect(line).not.toHaveClass(/hovered/);
  await line.dblclick();
  const editor = page.getByRole("textbox", { name: "编辑第 1 行" });
  await editor.fill("改后的行文本");
  await rejectNextCommand(page);
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(page.getByText(/保存失败/)).toBeVisible();
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expectCommand(page, {
    scope: "recognition",
    action: "updateLine",
    arguments: {
      resourceUri: STRUCTURED_URL,
      lineIndex: 0,
      expectedOldText: "第一段行一",
      newText: "改后的行文本",
    },
  });
  await expect(page.getByRole("textbox", { name: "编辑第 1 行" })).toBeHidden();

  // 键盘路径：聚焦低置信行 + Enter 编辑，Escape 取消。
  const lowLine = page.getByLabel("低置信行", { exact: true });
  await expect(lowLine).toHaveClass(/low-confidence/);
  await lowLine.focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("textbox", { name: "编辑第 2 行" }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("textbox", { name: "编辑第 2 行" })).toBeHidden();

  // 窄屏 resize：行框随舞台缩放，不溢出视口。
  const before = await line.boundingBox();
  await page.screenshot({ path: testInfo.outputPath("bbox-left-preview.png") });
  await page.setViewportSize({ width: 480, height: 900 });
  await expect
    .poll(async () => (await line.boundingBox())?.width ?? Infinity)
    .toBeLessThan(before!.width);
  const after = await line.boundingBox();
  expect(before).not.toBeNull();
  expect(after).not.toBeNull();
  expect(after!.width).toBeLessThan(before!.width);
  expect(after!.x).toBeGreaterThanOrEqual(0);
  expect(after!.x + after!.width).toBeLessThanOrEqual(480);
  await page.screenshot({
    path: testInfo.outputPath("bbox-resize.png"),
  });
});

test("long-image proofreading editor stays inside the preview viewport", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 480 });
  await mountRecognitionResults(page);
  await page.route("/long-proof.svg", (route) =>
    route.fulfill({
      contentType: "image/svg+xml",
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="2400"><rect width="240" height="2400" fill="white"/></svg>',
    }),
  );
  await page
    .getByRole("img", { name: "识别结果参考图" })
    .evaluate((image: HTMLImageElement) => {
      image.src = "/long-proof.svg";
    });
  const line = page.getByLabel("第一段行一", { exact: true });
  await expect
    .poll(() =>
      page
        .getByRole("img", { name: "识别结果参考图" })
        .evaluate((image: HTMLImageElement) => image.naturalHeight),
    )
    .toBe(2400);
  await line.focus();
  await page.keyboard.press("Enter");
  const editor = page.getByRole("textbox", { name: "编辑第 1 行" });
  await editor.fill("长图校对");
  await expect
    .poll(() =>
      page.locator(".overlay-line-editor").evaluate((panel) => {
        const view = document
          .querySelector("#recognition-preview .canvas-stage")!
          .getBoundingClientRect();
        const rect = panel.getBoundingClientRect();
        return (
          rect.left >= view.left &&
          rect.right <= view.right &&
          rect.top >= view.top &&
          rect.bottom <= view.bottom
        );
      }),
    )
    .toBe(true);
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await expect(editor).toBeHidden();
});
