import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { StructuredResult } from "./StructuredResult";
import { linesInfoFromSource } from "./resultTextModes";

afterEach(cleanup);

const singleTable = (text: string) => ({
  type: "table",
  table: {
    schema_version: 1,
    row_count: 1,
    column_count: 1,
    cells: [{ row: 0, column: 0, rowspan: 1, colspan: 1, text }],
  },
});

it("renders canonical merged cells and preserves raw LaTeX on render failure", () => {
  render(
    <StructuredResult
      source={JSON.stringify([
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
                rowspan: 2,
                colspan: 1,
                text: "中文",
                is_header: true,
              },
              { row: 0, column: 1, rowspan: 1, colspan: 1, text: "=2+2" },
              { row: 1, column: 1, rowspan: 1, colspan: 1, text: "下行" },
            ],
          },
        },
        { type: "formula", text: "\\invalidcommand{x}" },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  expect(screen.getByRole("columnheader", { name: "中文" })).toHaveAttribute(
    "rowspan",
    "2",
  );
  expect(screen.getByRole("cell", { name: "=2+2" })).toHaveTextContent("=2+2");
  expect(screen.getAllByText("\\invalidcommand{x}").length).toBeGreaterThan(0);
  expect(
    screen.getByText("公式无法渲染，原始 LaTeX 仍可复制。"),
  ).toBeInTheDocument();
});

it("copies tables and formulas through host block indexes in mixed blocks", async () => {
  // 混排：text(0)、formula(1)、table(2)、image(3)。表格/公式动作必须
  // 提交宿主原始数组下标，不得用显示序号漂移。
  const onCopy = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([
        { type: "text", text: "前置文本块" },
        { type: "formula", text: "a^2+b^2=c^2" },
        singleTable("数值"),
        { type: "text", text: "后置文本块" },
      ])}
      onCopy={onCopy}
    />,
  );
  expect(screen.getByText("前置文本块")).toBeInTheDocument();
  expect(screen.getByText("后置文本块")).toBeInTheDocument();
  expect(screen.getByText("表格 1")).toBeInTheDocument();

  await fireEvent.click(
    screen.getByRole("button", { name: "复制表格 HTML / TSV" }),
  );
  expect(onCopy).toHaveBeenLastCalledWith(2, "table");

  await fireEvent.click(screen.getByRole("button", { name: "复制 LaTeX" }));
  expect(onCopy).toHaveBeenLastCalledWith(1, "latex");
});

it("keeps an error state when the native copy fails", async () => {
  render(
    <StructuredResult
      source={JSON.stringify([{ type: "formula", text: "x+y" }])}
      onCopy={vi.fn(async () => false)}
    />,
  );

  await fireEvent.click(screen.getByRole("button", { name: "复制 LaTeX" }));

  await waitFor(() => expect(screen.getByText(/复制失败/)).toBeInTheDocument());
});

it("remounts per result so a stale cell selection cannot leak across results", async () => {
  const { rerender } = render(
    <StructuredResult
      key="first"
      source={JSON.stringify([singleTable("甲")])}
      onCopy={vi.fn(async () => true)}
      onCopyCells={vi.fn(async () => true)}
    />,
  );
  await fireEvent.click(screen.getByRole("cell", { name: "甲" }));
  expect(screen.getByRole("cell", { name: "甲" })).toHaveClass("cell-selected");

  // 换结果：调用方以资源 URL 为 key remount，选区复位。
  rerender(
    <StructuredResult
      key="second"
      source={JSON.stringify([singleTable("乙")])}
      onCopy={vi.fn(async () => true)}
      onCopyCells={vi.fn(async () => true)}
    />,
  );
  expect(screen.getByRole("cell", { name: "乙" })).not.toHaveClass(
    "cell-selected",
  );
});

it("keeps recognized text beside image/chart/seal previews including failed assets", () => {
  // 生产管线对 image/chart/seal 同时产出 text 与 image（图表内容、图片
  // 说明、印章文字）：预览资产失效时识别文本也不得丢失。
  render(
    <StructuredResult
      source={JSON.stringify([
        {
          type: "image",
          text: "图片说明文字",
          image: {
            resource: {
              url: "https://resource.vibeocr/image",
              mediaType: "image/png",
            },
          },
        },
        {
          type: "chart",
          text: "图表内容摘要",
          image: {
            resource: {
              url: "https://resource.vibeocr/chart",
              mediaType: "image/png",
            },
          },
        },
        {
          type: "seal",
          text: "印章文字",
          image: { available: false, reason: "资产已过期" },
        },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  expect(screen.getByText("图片说明文字")).toBeInTheDocument();
  expect(screen.getByText("图表内容摘要")).toBeInTheDocument();
  expect(screen.getByText("印章文字")).toBeInTheDocument();
  expect(screen.getByText("图片不可用：资产已过期")).toBeInTheDocument();
});

it.each(["image", "chart", "seal"])(
  "keeps %s text when no image descriptor is provided",
  (type) => {
    render(
      <StructuredResult
        source={JSON.stringify([
          { type, text: "无预览的识别文字", img_path: "figure.png" },
        ])}
        onCopy={vi.fn(async () => true)}
      />,
    );
    expect(screen.getByText("无预览的识别文字")).toBeInTheDocument();
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
  },
);

it("previews image assets on chart and seal blocks", () => {
  render(
    <StructuredResult
      source={JSON.stringify([
        {
          type: "chart",
          image: {
            resource: {
              url: "https://resource.vibeocr/chart",
              mediaType: "image/png",
            },
          },
        },
        { type: "seal", image: { available: false, reason: "资产已过期" } },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  expect(screen.getByRole("img", { name: "文档图像区块" })).toHaveAttribute(
    "src",
    "https://resource.vibeocr/chart",
  );
  expect(screen.getByText("图片不可用：资产已过期")).toBeInTheDocument();
});

it("bounds sparse oversized tables to a note without clipboard copy", () => {
  // 101×101=10201 格仅 1 个单元格：刚超过 10000 格上限的合法稀疏输入。
  // 宿主对超限网格拒绝剪贴板构建（旧实现会物化整表矩阵冻结 UI），前端
  // 必须同步降级：不物化网格、不提供整表/范围复制，明确引导导出替代。
  const onCopy = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([
        { type: "text", text: "前置文本块" },
        {
          type: "table",
          table: {
            schema_version: 1,
            row_count: 101,
            column_count: 101,
            cells: [
              {
                row: 0,
                column: 0,
                rowspan: 1,
                colspan: 1,
                text: "唯一值",
                is_header: true,
              },
            ],
          },
        },
      ])}
      onCopy={onCopy}
      onCopyCells={vi.fn(async () => true)}
    />,
  );
  expect(screen.getByRole("region", { name: "表格 1" })).toBeInTheDocument();
  expect(document.querySelector(".structured-table-scroll table")).toBeNull();
  expect(
    screen.getByText(
      "表格较大（超过 10000 格），暂不支持预览与剪贴板复制，请使用导出获取完整内容。",
    ),
  ).toBeInTheDocument();
  // 降级态不展示范围选择提示与不可用交互，也不再引导整表复制。
  expect(screen.queryByText(/拖选或方向键/)).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "复制所选单元格" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "复制表格 HTML / TSV" }),
  ).not.toBeInTheDocument();
  expect(onCopy).not.toHaveBeenCalled();
});

it("renders tables within the grid area limit", () => {
  // 边界内（100×100=10000 格）仍完整物化网格，不提前降级。
  const cells = Array.from({ length: 100 }, (_, row) =>
    Array.from({ length: 100 }, (_, column) => ({
      row,
      column,
      rowspan: 1,
      colspan: 1,
      text: `${row}-${column}`,
    })),
  ).flat();
  render(
    <StructuredResult
      source={JSON.stringify([
        {
          type: "table",
          table: {
            schema_version: 1,
            row_count: 100,
            column_count: 100,
            cells,
          },
        },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  expect(document.querySelectorAll(".structured-table-scroll td").length).toBe(
    10000,
  );
  expect(screen.queryByText(/表格较大/)).not.toBeInTheDocument();
  // 恰在上限内的表格仍提供整表剪贴板复制。
  expect(
    screen.getByRole("button", { name: "复制表格 HTML / TSV" }),
  ).toBeInTheDocument();
});

it("skips million-cell grids for 1000×1000 sparse tables", () => {
  // 1000×1000 少 cells：协议允许的极端稀疏表也不得物化百万格 DOM，
  // 也不得引导宿主物化超限剪贴板负载。
  render(
    <StructuredResult
      source={JSON.stringify([
        {
          type: "table",
          table: {
            schema_version: 1,
            row_count: 1000,
            column_count: 1000,
            cells: [
              { row: 0, column: 0, rowspan: 1, colspan: 1, text: "唯一值" },
            ],
          },
        },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  expect(document.querySelector(".structured-table-scroll table")).toBeNull();
  expect(document.querySelectorAll("tr").length).toBe(0);
  expect(screen.getByText(/表格较大/)).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "复制表格 HTML / TSV" }),
  ).not.toBeInTheDocument();
});

it("keeps native Enter newline and double-click selection inside the line editor", async () => {
  // 每行模式下 LineEditor 嵌套在带触发器的 <li> 内：编辑框内的默认
  // 行为（Enter 换行、双击选词）不得被父级行触发器 preventDefault 取消。
  const onUpdateLine = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([
        linesBlock([{ text: "普通行文本", score: 0.9, bbox: null }]),
      ])}
      mode="line"
      onCopy={vi.fn(async () => true)}
      onUpdateLine={onUpdateLine}
    />,
  );
  const line = document.querySelector(".result-lines-list .result-line");
  if (!line) throw new Error("result line is missing");
  await fireEvent.dblClick(line);
  const editor = screen.getByRole("textbox", { name: "编辑第 1 行" });
  const defaults: Array<[string, boolean]> = [];
  const record = (event: Event) =>
    defaults.push([event.type, event.defaultPrevented]);
  document.addEventListener("keydown", record);
  document.addEventListener("dblclick", record);
  editor.focus();
  await fireEvent.keyDown(editor, { key: "Enter" });
  await fireEvent.dblClick(editor);
  document.removeEventListener("keydown", record);
  document.removeEventListener("dblclick", record);
  expect(defaults).toEqual([
    ["keydown", false],
    ["dblclick", false],
  ]);
  // Enter 只进入草稿：编辑器保持打开且未触发行激活或提交。
  expect(
    screen.getByRole("textbox", { name: "编辑第 1 行" }),
  ).toBeInTheDocument();
  expect(onUpdateLine).not.toHaveBeenCalled();
});

it("orders table cells by column and ignores malformed cell entries", () => {
  render(
    <StructuredResult
      source={JSON.stringify([
        {
          type: "table",
          table: {
            schema_version: 1,
            row_count: 1,
            column_count: 2,
            cells: [
              null,
              { row: 0, column: 1, rowspan: 1, colspan: 1, text: "right" },
              { row: 0, column: 0, rowspan: 1, colspan: 1, text: "left" },
            ],
          },
        },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  expect(screen.getAllByRole("cell").map((cell) => cell.textContent)).toEqual([
    "left",
    "right",
  ]);
});

const linesBlock = (lines: unknown[], modes?: Record<string, string>) => ({
  type: "lines",
  schema_version: 1,
  lines,
  ...(modes ? { modes } : {}),
});

const fixtureModes: Record<string, string> = {
  raw: "第一段第一行\n第一段第二行\n第二段首行",
  line: "第一段第一行\n第一段第二行\n第二段首行",
  merged: "第一段第一行第一段第二行第二段首行",
  smart: "第一段第一行第一段第二行\n\n第二段首行",
};

const fixtureLines = [
  { text: "第一段第一行", score: 0.95, bbox: [10, 10, 200, 30] },
  { text: "第一段第二行", score: 0.62, bbox: [10, 34, 200, 54] },
  { text: "第二段首行", score: null, bbox: [10, 140, 200, 160] },
];

it("displays host-authored mode text and low-confidence marks", () => {
  const source = JSON.stringify([linesBlock(fixtureLines, fixtureModes)]);
  // 页级模式信息与可用性由同一解析派生（工具栏据此禁用缺失模式）。
  const info = linesInfoFromSource(source);
  expect(info.hasLines).toBe(true);
  expect(info.modes).toEqual(fixtureModes);

  const { rerender } = render(
    <StructuredResult
      source={source}
      mode="smart"
      onCopy={vi.fn(async () => true)}
    />,
  );
  // 智能分段：直接展示宿主权威 modes.smart，Web 不重复拼接。
  expect(document.querySelector(".result-document")?.textContent).toBe(
    fixtureModes.smart,
  );
  expect(screen.getByText(/共 3 行，低置信（<80%）1 个/)).toBeInTheDocument();

  // 每行模式：逐行展示低置信标记（score<0.8）与 unknown（不标记）。
  rerender(
    <StructuredResult
      source={source}
      mode="line"
      onCopy={vi.fn(async () => true)}
    />,
  );
  const items = document.querySelectorAll(".result-lines-list .result-line");
  expect(items.length).toBe(3);
  expect(items[1]!).toHaveClass("line-low-confidence");
  expect(items[1]!).toHaveAttribute("title", "置信度: 62.0%");
  expect(items[2]!).not.toHaveClass("line-low-confidence");
  expect(items[2]!).toHaveAttribute("title", "置信度: 无置信度");
});

it("falls back to basic per-line rendering when host modes are missing", () => {
  // 宿主未发布 modes：缺失模式被页级禁用；组件只保留基础逐行内容，
  // 不实现拼接算法。
  render(
    <StructuredResult
      source={JSON.stringify([linesBlock(fixtureLines)])}
      mode="smart"
      onCopy={vi.fn(async () => true)}
    />,
  );
  const items = document.querySelectorAll(".result-lines-list .result-line");
  expect(items.length).toBe(3);
  expect(items[0]!).toHaveTextContent("第一段第一行");
});

it("edits a line in place via double-click or Enter through the host handler", async () => {
  const onUpdateLine = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([
        linesBlock([{ text: "旧行文本", score: 0.9, bbox: null }]),
      ])}
      mode="line"
      onCopy={vi.fn(async () => true)}
      onUpdateLine={onUpdateLine}
    />,
  );
  const line = document.querySelector(".result-lines-list .result-line");
  if (!line) throw new Error("result line is missing");
  expect(screen.getByText("双击或 Enter 编辑")).toBeInTheDocument();

  await fireEvent.dblClick(line);
  const editor = screen.getByRole("textbox", { name: "编辑第 1 行" });
  await fireEvent.change(editor, { target: { value: "新行文本" } });
  await fireEvent.click(screen.getByRole("button", { name: "保存" }));
  expect(onUpdateLine).toHaveBeenCalledWith(0, "旧行文本", "新行文本");
  await waitFor(() =>
    expect(
      screen.queryByRole("textbox", { name: "编辑第 1 行" }),
    ).not.toBeInTheDocument(),
  );

  // 键盘路径：聚焦后 Enter 开始编辑，Escape 取消。
  await fireEvent.keyDown(line, { key: "Enter" });
  const reopened = screen.getByRole("textbox", { name: "编辑第 1 行" });
  await fireEvent.keyDown(reopened, {
    key: "Escape",
  });
  expect(
    screen.queryByRole("textbox", { name: "编辑第 1 行" }),
  ).not.toBeInTheDocument();
});

it("copies a selected cell range with merged cells by anchor through the host handler", async () => {
  const onCopyCells = vi.fn(async () => true);
  const onCopy = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([
        { type: "text", text: "前置文本块" },
        {
          type: "table",
          table: {
            schema_version: 1,
            row_count: 2,
            column_count: 2,
            cells: [
              { row: 0, column: 0, rowspan: 1, colspan: 1, text: "甲" },
              { row: 0, column: 1, rowspan: 1, colspan: 1, text: "乙" },
              { row: 1, column: 0, rowspan: 1, colspan: 1, text: "丙" },
              { row: 1, column: 1, rowspan: 1, colspan: 1, text: "丁" },
            ],
          },
        },
      ])}
      onCopy={onCopy}
      onCopyCells={onCopyCells}
    />,
  );

  await fireEvent.click(screen.getByRole("cell", { name: "甲" }));
  await fireEvent.click(screen.getByRole("cell", { name: "丁" }), {
    shiftKey: true,
  });

  // 锚点矩形内高亮；复制提交宿主原始 blockIndex（前置 text 块占位 0）。
  expect(screen.getByRole("cell", { name: "甲" })).toHaveClass("cell-selected");
  expect(screen.getByRole("cell", { name: "乙" })).toHaveClass("cell-selected");
  await fireEvent.click(screen.getByRole("button", { name: "复制所选单元格" }));
  expect(onCopyCells).toHaveBeenCalledWith(1, 0, 1, 0, 1);
});

it("reports cell copy failures instead of failing silently", async () => {
  render(
    <StructuredResult
      source={JSON.stringify([singleTable("值")])}
      onCopy={vi.fn(async () => true)}
      onCopyCells={vi.fn(async () => false)}
    />,
  );
  await fireEvent.click(screen.getByRole("cell", { name: "值" }));
  await fireEvent.click(screen.getByRole("button", { name: "复制所选单元格" }));
  await waitFor(() =>
    expect(screen.getByText(/复制所选单元格失败/)).toBeInTheDocument(),
  );
});

it("keeps sparse columns aligned with empty placeholders", () => {
  render(
    <StructuredResult
      source={JSON.stringify([
        {
          type: "table",
          table: {
            schema_version: 1,
            row_count: 2,
            column_count: 2,
            cells: [
              { row: 0, column: 0, rowspan: 1, colspan: 1, text: "左上" },
              { row: 0, column: 1, rowspan: 1, colspan: 1, text: "右上" },
              { row: 1, column: 1, rowspan: 1, colspan: 1, text: "右下" },
            ],
          },
        },
      ])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  // 稀疏行：第 2 行缺 col0，先渲染非交互空占位再渲染 col1，列不移位。
  const rows = document.querySelectorAll(".structured-table-scroll tr");
  expect(rows.length).toBe(2);
  const secondRow = rows[1]!;
  const placeholder = secondRow.children[0]!;
  expect(placeholder).toHaveAttribute("data-column", "0");
  expect(placeholder).toHaveClass("cell-empty");
  expect(secondRow.children[1]!).toHaveAttribute("data-column", "1");
  expect(secondRow.children[1]!).toHaveTextContent("右下");
});

it("highlights merged cells intersecting the selection rectangle", async () => {
  render(
    <StructuredResult
      source={JSON.stringify([
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
                rowspan: 2,
                colspan: 1,
                text: "合并列",
                is_header: true,
              },
              { row: 0, column: 1, rowspan: 1, colspan: 1, text: "乙" },
              { row: 1, column: 1, rowspan: 1, colspan: 1, text: "丁" },
            ],
          },
        },
      ])}
      onCopy={vi.fn(async () => true)}
      onCopyCells={vi.fn(async () => true)}
    />,
  );
  // 仅选右列：合并列（col0，跨两行）不与选择矩形相交，不高亮。
  await fireEvent.click(screen.getByRole("cell", { name: "乙" }));
  await fireEvent.click(screen.getByRole("cell", { name: "丁" }), {
    shiftKey: true,
  });
  expect(screen.getByRole("columnheader", { name: "合并列" })).not.toHaveClass(
    "cell-selected",
  );
  // 选到左列锚点：合并列与选择矩形相交，高亮且保留跨行 span。
  await fireEvent.click(screen.getByRole("columnheader", { name: "合并列" }));
  await fireEvent.click(screen.getByRole("cell", { name: "丁" }), {
    shiftKey: true,
  });
  expect(screen.getByRole("columnheader", { name: "合并列" })).toHaveClass(
    "cell-selected",
  );
  expect(screen.getByRole("columnheader", { name: "合并列" })).toHaveAttribute(
    "rowspan",
    "2",
  );
});

it("scopes keyboard cell selection to the table containing focus", async () => {
  const twoCells = (name: string) => ({
    type: "table",
    table: {
      schema_version: 1,
      row_count: 2,
      column_count: 1,
      cells: [
        { row: 0, column: 0, rowspan: 1, colspan: 1, text: `${name}上` },
        { row: 1, column: 0, rowspan: 1, colspan: 1, text: `${name}下` },
      ],
    },
  });
  render(
    <StructuredResult
      source={JSON.stringify([twoCells("甲"), twoCells("乙")])}
      onCopy={vi.fn(async () => true)}
      onCopyCells={vi.fn(async () => true)}
    />,
  );
  // 聚焦第二个表格首行，Shift+ArrowDown 扩展：焦点只落在第二个表格。
  const secondTop = screen.getByRole("cell", { name: "乙上" });
  secondTop.focus();
  await fireEvent.keyDown(secondTop, {
    key: "ArrowDown",
    shiftKey: true,
  });
  expect(screen.getByRole("cell", { name: "乙下" })).toHaveClass(
    "cell-selected",
  );
  expect(document.activeElement).toBe(
    screen.getByRole("cell", { name: "乙下" }),
  );
  // 第一个表格不受跨表选择影响。
  expect(screen.getByRole("cell", { name: "甲上" })).not.toHaveClass(
    "cell-selected",
  );
});
