import { Button } from "@fluentui/react-components";
import katex from "katex";
import { useState } from "react";
import "katex/dist/katex.min.css";

interface Cell {
  row: number;
  column: number;
  rowspan: number;
  colspan: number;
  text: string;
  is_header?: boolean;
}

interface Block {
  type: string;
  text?: string;
  table?: {
    schema_version: number;
    row_count: number;
    column_count: number;
    cells: Cell[];
  };
  image?: {
    available?: boolean;
    reason?: string;
    resource?: { url: string; mediaType: string };
  };
}

export type StructuredCopyFormat = "table" | "latex";

/**
 * 结构化区块的原生剪贴板复制：由宿主重读权威资源构建 TSV/HTML/LaTeX
 * 后经平台剪贴板写入（含 busy 重试与错误提示）；Web 侧不直接写
 * navigator.clipboard。返回 false 表示宿主拒绝或剪贴板失败。
 */
export type StructuredCopyHandler = (
  blockIndex: number,
  format: StructuredCopyFormat,
) => Promise<boolean>;

function blocksFrom(value: string): Block[] {
  try {
    const parsed: unknown = JSON.parse(value);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (item): item is Block =>
        item !== null &&
        typeof item === "object" &&
        typeof item.type === "string",
    );
  } catch {
    return [];
  }
}

function tableCells(block: Block): Cell[] {
  const table = block.table;
  if (
    !table ||
    table.schema_version !== 1 ||
    !Number.isSafeInteger(table.row_count) ||
    !Number.isSafeInteger(table.column_count) ||
    table.row_count < 1 ||
    table.column_count < 1 ||
    table.row_count > 1000 ||
    table.column_count > 1000 ||
    !Array.isArray(table.cells)
  )
    return [];
  return table.cells.filter(
    (cell) =>
      cell !== null &&
      typeof cell === "object" &&
      Number.isSafeInteger(cell.row) &&
      Number.isSafeInteger(cell.column) &&
      Number.isSafeInteger(cell.rowspan) &&
      Number.isSafeInteger(cell.colspan) &&
      cell.row >= 0 &&
      cell.column >= 0 &&
      cell.rowspan >= 1 &&
      cell.colspan >= 1 &&
      cell.row + cell.rowspan <= table.row_count &&
      cell.column + cell.colspan <= table.column_count &&
      typeof cell.text === "string",
  );
}

function Formula({
  text,
  onCopyLaTeX,
}: {
  readonly text: string;
  readonly onCopyLaTeX: () => void;
}) {
  let markup: string | undefined;
  try {
    markup = katex.renderToString(text, {
      displayMode: true,
      output: "mathml",
      throwOnError: true,
      trust: false,
      maxExpand: 100,
      macros: {},
    });
  } catch {
    /* Raw LaTeX remains visible below. */
  }
  return (
    <div className="structured-formula">
      {markup ? (
        <div
          className="formula-preview"
          dangerouslySetInnerHTML={{ __html: markup }}
        />
      ) : (
        <p className="form-note">公式无法渲染，原始 LaTeX 仍可复制。</p>
      )}
      <code>{text}</code>
      <Button size="small" onClick={onCopyLaTeX}>
        复制 LaTeX
      </Button>
    </div>
  );
}

export function StructuredResult({
  source,
  onCopy,
}: {
  readonly source: string;
  readonly onCopy: StructuredCopyHandler;
}) {
  // 调用方以资源 URL 作为 key：换结果即 remount，序号与复制状态天然
  // 重置；组件内部再对序号做派生 clamp，双保险避免越界。
  const blocks = blocksFrom(source);
  const tables = blocks
    .map((block, index) => ({ block, index }))
    .filter(({ block }) => tableCells(block).length > 0);
  const formulas = blocks
    .map((block, index) => ({ block, index }))
    .filter(
      ({ block }) => block.type === "formula" && typeof block.text === "string",
    );
  const [tableIndex, setTableIndex] = useState(0);
  const [formulaIndex, setFormulaIndex] = useState(0);
  const [copyFailed, setCopyFailed] = useState(false);
  const safeTableIndex = Math.min(tableIndex, tables.length - 1);
  const safeFormulaIndex = Math.min(formulaIndex, formulas.length - 1);
  const activeTable = tables[safeTableIndex];
  const activeFormula = formulas[safeFormulaIndex];
  const copy = async (blockIndex: number, format: StructuredCopyFormat) => {
    setCopyFailed(false);
    const copied = await onCopy(blockIndex, format);
    setCopyFailed(!copied);
  };
  return (
    <div className="structured-result">
      {activeTable && (
        <section>
          <label>
            表格 {safeTableIndex + 1}/{tables.length}{" "}
            <select
              value={safeTableIndex}
              onChange={(event) => setTableIndex(Number(event.target.value))}
            >
              {tables.map((_, index) => (
                <option key={index} value={index}>
                  表格 {index + 1}
                </option>
              ))}
            </select>
          </label>
          <Button
            size="small"
            onClick={() => void copy(activeTable.index, "table")}
          >
            复制表格 HTML / TSV
          </Button>
          <div className="structured-table-scroll">
            <table>
              <tbody>
                {Array.from(
                  { length: activeTable.block.table!.row_count },
                  (_, row) => (
                    <tr key={row}>
                      {tableCells(activeTable.block)
                        .filter((cell) => cell.row === row)
                        .sort((a, b) => a.column - b.column)
                        .map((cell, index) =>
                          cell.is_header ? (
                            <th
                              key={index}
                              rowSpan={cell.rowspan}
                              colSpan={cell.colspan}
                            >
                              {cell.text}
                            </th>
                          ) : (
                            <td
                              key={index}
                              rowSpan={cell.rowspan}
                              colSpan={cell.colspan}
                            >
                              {cell.text}
                            </td>
                          ),
                        )}
                    </tr>
                  ),
                )}
              </tbody>
            </table>
          </div>
        </section>
      )}
      {activeFormula?.block.text && (
        <section>
          <label>
            公式 {safeFormulaIndex + 1}/{formulas.length}{" "}
            <select
              value={safeFormulaIndex}
              onChange={(event) => setFormulaIndex(Number(event.target.value))}
            >
              {formulas.map((_, index) => (
                <option key={index} value={index}>
                  公式 {index + 1}
                </option>
              ))}
            </select>
          </label>
          <Formula
            text={activeFormula.block.text}
            onCopyLaTeX={() => void copy(activeFormula.index, "latex")}
          />
        </section>
      )}
      {blocks.length > 0 && (
        <section>
          <h3>阅读顺序与版面区块</h3>
          <ol className="structured-blocks">
            {blocks.map((block, index) => (
              <li key={index}>
                <strong>{block.type}</strong>
                {block.text && <p>{block.text}</p>}
                {(block.type === "image" || block.image) &&
                  (block.image?.resource ? (
                    <img src={block.image.resource.url} alt="文档图像区块" />
                  ) : (
                    <p className="form-note">
                      图片不可用：{block.image?.reason ?? "资源缺失"}
                    </p>
                  ))}
              </li>
            ))}
          </ol>
        </section>
      )}
      {copyFailed && (
        <p role="alert" className="form-note">
          复制失败：系统剪贴板暂不可用或结果已失效，请重试。
        </p>
      )}
    </div>
  );
}
