import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { StructuredResult } from "./StructuredResult";

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

it("copies a table through the host bridge handler, not navigator.clipboard", async () => {
  const onCopy = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([singleTable("数值")])}
      onCopy={onCopy}
    />,
  );

  await fireEvent.click(
    screen.getByRole("button", { name: "复制表格 HTML / TSV" }),
  );

  expect(onCopy).toHaveBeenCalledTimes(1);
  expect(onCopy).toHaveBeenCalledWith(0, "table");
});

it("copies the selected formula LaTeX through the host bridge handler", async () => {
  const onCopy = vi.fn(async () => true);
  render(
    <StructuredResult
      source={JSON.stringify([
        { type: "formula", text: "a^2+b^2=c^2" },
        { type: "formula", text: "\\frac{1}{2}" },
      ])}
      onCopy={onCopy}
    />,
  );

  await fireEvent.change(screen.getByRole("combobox"), {
    target: { value: "1" },
  });
  await fireEvent.click(screen.getByRole("button", { name: "复制 LaTeX" }));

  expect(onCopy).toHaveBeenCalledTimes(1);
  expect(onCopy).toHaveBeenCalledWith(1, "latex");
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

it("remounts per result so a stale table selection cannot leak across results", async () => {
  const twoTables = [singleTable("甲"), singleTable("乙")];
  const { rerender } = render(
    <StructuredResult
      key="first"
      source={JSON.stringify(twoTables)}
      onCopy={vi.fn(async () => true)}
    />,
  );
  const select = screen.getByRole("combobox");
  expect(screen.getByText(/表格 1\/2/)).toBeInTheDocument();

  await fireEvent.change(select, { target: { value: "1" } });

  // 换结果：调用方以资源 URL 为 key remount，序号回到首项。
  rerender(
    <StructuredResult
      key="second"
      source={JSON.stringify([singleTable("丙")])}
      onCopy={vi.fn(async () => true)}
    />,
  );
  const after = screen.getByRole("combobox");
  expect((after as HTMLSelectElement).value).toBe("0");
  expect(screen.getByText(/表格 1\/1/)).toBeInTheDocument();
});

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
