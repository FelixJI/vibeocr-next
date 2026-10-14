import { Button } from "@fluentui/react-components";
import katex from "katex";
import { useRef, useState, type MouseEvent } from "react";
import "katex/dist/katex.min.css";
import "./StructuredResult.css";
import { LineEditor } from "./LineEditor";
import { startEditOnEnterOrDoubleActivate } from "./lineEditorTrigger";
import {
  confidenceLabel,
  isLowConfidence,
  parseResultLines,
  parseResultModes,
  type ResultTextLine,
  type ResultTextMode,
  type ResultTextModes,
} from "./resultTextModes";

/** 宿主发布的结构化区块（形状宽松：type/text/table/lines/modes/image）。 */
interface Block {
  type?: unknown;
  text?: unknown;
  table?: {
    schema_version: number;
    row_count: number;
    column_count: number;
    cells: Cell[];
  };
  lines?: unknown;
  modes?: unknown;
  image?: {
    available?: boolean;
    reason?: string;
    resource?: { url: string; mediaType: string };
  };
}

export type StructuredCopyFormat = "table" | "latex";

/**
 * 结构化区块的原生剪贴板复制：由宿主重读权威资源构建 TSV/HTML/LaTeX
 * 后写入平台剪贴板。blockIndex 必须是宿主原始数组下标（混排不漂移）。
 */
export type StructuredCopyHandler = (
  blockIndex: number,
  format: StructuredCopyFormat,
) => Promise<boolean>;

/** 表格子范围（含合并单元格交集裁剪）的原生复制，粘贴 Word/Excel。 */
export type CellsCopyHandler = (
  blockIndex: number,
  rowStart: number,
  rowEnd: number,
  columnStart: number,
  columnEnd: number,
) => Promise<boolean>;

/**
 * 行内联编辑提交：宿主校验资源仍为当前结果且旧行文本一致后改写权威
 * 行数据并重建 raw/markdown。返回 false 表示结果过期或文本漂移。
 */
export type LineEditHandler = (
  lineIndex: number,
  expectedOldText: string,
  newText: string,
) => Promise<boolean>;

interface BlockEntry {
  readonly block: Block;
  /** 宿主原始数组下标：所有剪贴板/编辑命令的稳定标识。 */
  readonly index: number;
}

/**
 * 解析结构化结果并保留宿主原始下标：无效条目不渲染但仍占位，避免
 * table/formula/lines 的 blockIndex 相对宿主数组漂移。
 */
function blocksFrom(value: string): readonly BlockEntry[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) return [];
  const entries: BlockEntry[] = [];
  parsed.forEach((item, index) => {
    if (item !== null && typeof item === "object" && !Array.isArray(item)) {
      entries.push({ block: item as Block, index });
    }
  });
  return entries;
}

interface Cell {
  row: number;
  column: number;
  rowspan: number;
  colspan: number;
  text: string;
  is_header?: boolean;
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

function isTextBlock(block: Block): block is Block & { text: string } {
  return (
    typeof block.type === "string" &&
    !["table", "formula", "lines", "image", "chart", "seal"].includes(
      block.type,
    ) &&
    typeof block.text === "string"
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

/** 表格单元格范围选择（网格锚点坐标；合并单元格以其锚点参与）。 */
interface CellSelection {
  readonly rowStart: number;
  readonly rowEnd: number;
  readonly columnStart: number;
  readonly columnEnd: number;
}

function selectionRect(
  anchor: { row: number; column: number },
  focus: { row: number; column: number },
): CellSelection {
  return {
    rowStart: Math.min(anchor.row, focus.row),
    rowEnd: Math.max(anchor.row, focus.row),
    columnStart: Math.min(anchor.column, focus.column),
    columnEnd: Math.max(anchor.column, focus.column),
  };
}

/** 语义与宿主范围复制一致：cell 矩形与选择矩形有交集即参与高亮。 */
function cellIntersectsSelection(
  cell: Cell,
  selection: CellSelection,
): boolean {
  return (
    cell.row <= selection.rowEnd &&
    cell.row + cell.rowspan - 1 >= selection.rowStart &&
    cell.column <= selection.columnEnd &&
    cell.column + cell.colspan - 1 >= selection.columnStart
  );
}

// shortcut: 超过宿主 MaxGridArea 的表格仅导出；有界预览/复制实现后再开放。
const MAX_TABLE_GRID_AREA = 10_000;

/** 表格网格放置：锚点单元格 + span 覆盖位 + 稀疏空位，保持列对齐。 */
function buildTableGrid(
  cells: readonly Cell[],
  rowCount: number,
  columnCount: number,
): (Cell | "covered" | null)[][] {
  const grid: (Cell | "covered" | null)[][] = Array.from(
    { length: rowCount },
    () => Array.from({ length: columnCount }, () => null),
  );
  for (const cell of cells) {
    for (let row = cell.row; row < cell.row + cell.rowspan; row += 1)
      for (
        let column = cell.column;
        column < cell.column + cell.colspan;
        column += 1
      )
        grid[row]![column] = "covered";
    grid[cell.row]![cell.column] = cell;
  }
  return grid;
}

function TableBlock({
  block,
  blockIndex,
  ordinal,
  onCopy,
  onCopyCells,
}: {
  readonly block: Block;
  readonly blockIndex: number;
  readonly ordinal: number;
  readonly onCopy: StructuredCopyHandler;
  readonly onCopyCells?: CellsCopyHandler;
}) {
  const [anchor, setAnchor] = useState<
    { row: number; column: number } | undefined
  >();
  const [focus, setFocus] = useState<
    { row: number; column: number } | undefined
  >();
  const [cellsError, setCellsError] = useState(false);
  const tableRef = useRef<HTMLTableElement>(null);
  const cells = tableCells(block);
  const table = block.table!;
  const gridTooLarge =
    table.row_count * table.column_count > MAX_TABLE_GRID_AREA;
  const selection = anchor && focus ? selectionRect(anchor, focus) : undefined;
  const selectCell = (cell: Cell, shiftKey: boolean) => {
    if (shiftKey && anchor) setFocus({ row: cell.row, column: cell.column });
    else {
      setAnchor({ row: cell.row, column: cell.column });
      setFocus({ row: cell.row, column: cell.column });
    }
  };
  // 拖选：按住左键划过单元格时以锚点矩形扩展（与 Shift+单击同语义）。
  const dragOver = (cell: Cell, buttons: number) => {
    if ((buttons & 1) === 0 || !anchor) return;
    setFocus({ row: cell.row, column: cell.column });
  };
  // 键盘选择：Enter/空格设锚点，方向键移动焦点，Shift+方向扩展选择。
  const moveFocus = (
    cell: Cell,
    rowDelta: number,
    columnDelta: number,
    shiftKey: boolean,
  ) => {
    let row = cell.row + rowDelta * (rowDelta > 0 ? cell.rowspan : 1);
    let column =
      cell.column + columnDelta * (columnDelta > 0 ? cell.colspan : 1);
    let target: Cell | undefined;
    while (
      row >= 0 &&
      row < block.table!.row_count &&
      column >= 0 &&
      column < block.table!.column_count
    ) {
      target = cells.find(
        (candidate) =>
          row >= candidate.row &&
          row < candidate.row + candidate.rowspan &&
          column >= candidate.column &&
          column < candidate.column + candidate.colspan,
      );
      if (target) break;
      row += rowDelta;
      column += columnDelta;
    }
    if (!target) return;
    if (shiftKey && anchor)
      setFocus({ row: target.row, column: target.column });
    else {
      setAnchor({ row: target.row, column: target.column });
      setFocus({ row: target.row, column: target.column });
    }
    tableRef.current
      ?.querySelector<HTMLTableCellElement>(
        `[data-row="${target.row}"][data-column="${target.column}"]`,
      )
      ?.focus();
  };
  const copyCells = async () => {
    if (!selection || !onCopyCells) return;
    setCellsError(false);
    const copied = await onCopyCells(
      blockIndex,
      selection.rowStart,
      selection.rowEnd,
      selection.columnStart,
      selection.columnEnd,
    );
    // 失败不静默：与整表复制同一可见错误边界。
    setCellsError(!copied);
  };
  return (
    <section className="structured-block" aria-label={`表格 ${ordinal}`}>
      <header className="structured-block-header">
        <strong>表格 {ordinal}</strong>
        {!gridTooLarge && (
          <Button size="small" onClick={() => void onCopy(blockIndex, "table")}>
            复制表格 HTML / TSV
          </Button>
        )}
        {onCopyCells && !gridTooLarge && selection && (
          <Button size="small" onClick={() => void copyCells()}>
            复制所选单元格
          </Button>
        )}
      </header>
      {gridTooLarge ? (
        <p className="form-note" role="note">
          {`表格较大（超过 ${MAX_TABLE_GRID_AREA} 格），暂不支持预览与剪贴板复制，请使用导出获取完整内容。`}
        </p>
      ) : (
        <div className="structured-table-scroll">
          <table ref={tableRef}>
            <tbody>
              {buildTableGrid(cells, table.row_count, table.column_count).map(
                (row, rowNumber) => (
                  <tr key={rowNumber}>
                    {row.map((placed, columnNumber) => {
                      if (placed === "covered") return null; // 由锚点 span 覆盖。
                      if (placed === null)
                        // 稀疏空位：非交互占位保持列对齐。
                        return (
                          <td
                            key={columnNumber}
                            className="cell-empty"
                            data-row={rowNumber}
                            data-column={columnNumber}
                          />
                        );
                      const cell = placed;
                      const selected =
                        selection && cellIntersectsSelection(cell, selection);
                      const props = {
                        key: columnNumber,
                        rowSpan: cell.rowspan,
                        colSpan: cell.colspan,
                        className: selected ? "cell-selected" : undefined,
                        "data-row": cell.row,
                        "data-column": cell.column,
                        tabIndex: 0,
                        onClick: (event: MouseEvent) =>
                          selectCell(cell, event.shiftKey),
                        onMouseDown: (
                          event: MouseEvent<HTMLTableCellElement>,
                        ) => {
                          // 阻止拖选时的原生文本选中，保持矩形选择语义。
                          if (event.button !== 0) return;
                          event.preventDefault();
                          event.currentTarget.focus();
                          selectCell(cell, event.shiftKey);
                        },
                        onMouseEnter: (event: MouseEvent) =>
                          dragOver(cell, event.buttons),
                        onKeyDown: (event: React.KeyboardEvent) => {
                          if (event.key === "Enter" || event.key === " ") {
                            event.preventDefault();
                            selectCell(cell, event.shiftKey);
                            return;
                          }
                          const deltas: Record<string, [number, number]> = {
                            ArrowUp: [-1, 0],
                            ArrowDown: [1, 0],
                            ArrowLeft: [0, -1],
                            ArrowRight: [0, 1],
                          };
                          const delta = deltas[event.key];
                          if (delta && onCopyCells) {
                            event.preventDefault();
                            moveFocus(cell, delta[0], delta[1], event.shiftKey);
                          }
                        },
                      };
                      return cell.is_header ? (
                        <th {...props}>{cell.text}</th>
                      ) : (
                        <td {...props}>{cell.text}</td>
                      );
                    })}
                  </tr>
                ),
              )}
            </tbody>
          </table>
        </div>
      )}
      {onCopyCells && !gridTooLarge && (
        <p className="form-note">
          单击、拖选或方向键选择范围；Shift 扩展选择。
        </p>
      )}
      {cellsError && (
        <p role="alert" className="form-note">
          复制所选单元格失败：系统剪贴板暂不可用或结果已失效，请重试。
        </p>
      )}
    </section>
  );
}

function LinesBlock({
  lines,
  modes,
  mode,
  onUpdateLine,
}: {
  readonly lines: readonly ResultTextLine[];
  readonly modes: ResultTextModes;
  readonly mode: ResultTextMode;
  readonly onUpdateLine?: LineEditHandler;
}) {
  const [editing, setEditing] = useState<number | undefined>(undefined);
  const knownScores = lines.filter((line) => line.score !== null);
  const lowCount = lines.filter((line) => isLowConfidence(line.score)).length;
  // 展示用宿主预计算的模式文本（拼接权威）；缺失时由页级选择器禁用，
  // 这里仅回退基础逐行内容。
  const modeText = modes[mode];
  return (
    <section className="structured-block" aria-label="识别行文本">
      <p className="result-lines-summary">
        共 {lines.length} 行
        {knownScores.length > 0
          ? `，低置信（<80%）${lowCount} 个`
          : "，本引擎未提供置信度"}
      </p>
      {mode === "line" || modeText === undefined ? (
        <ol className="result-lines-list">
          {lines.map((line, index) => {
            const editable = onUpdateLine !== undefined;
            // 正在编辑的行不装触发器：编辑框事件不经父级 li。
            const trigger =
              editable && editing !== index
                ? startEditOnEnterOrDoubleActivate(() => {
                    if (editing === undefined) setEditing(index);
                  })
                : undefined;
            return (
              <li
                key={index}
                className={[
                  "result-line",
                  isLowConfidence(line.score) ? "line-low-confidence" : "",
                  line.manuallyEdited ? "line-edited" : "",
                ]
                  .filter(Boolean)
                  .join(" ")}
                title={confidenceLabel(line.score)}
                {...trigger}
              >
                {editing === index && editable ? (
                  <LineEditor
                    label={`编辑第 ${index + 1} 行`}
                    initialText={line.text}
                    onSubmit={async (newText) => {
                      const applied = await onUpdateLine!(
                        index,
                        line.text,
                        newText,
                      );
                      if (applied) setEditing(undefined);
                      return applied;
                    }}
                    onCancel={() => setEditing(undefined)}
                  />
                ) : (
                  <>
                    {line.text}
                    {editable && (
                      <span className="result-line-hint">
                        双击或 Enter 编辑
                      </span>
                    )}
                  </>
                )}
              </li>
            );
          })}
        </ol>
      ) : (
        <pre className="result-document">{modeText}</pre>
      )}
    </section>
  );
}

/**
 * 阅读顺序统一渲染：按宿主原始 blocks 顺序展示实际文本/公式/表格/
 * 图片/行列表内容，专属剪贴板动作在块旁；全局复制/导出与排版模式
 * 选择在页级工具栏（RecognitionPage），不在此重复堆叠。
 */
export function StructuredResult({
  source,
  onCopy,
  onCopyCells,
  onUpdateLine,
  mode = "raw",
}: {
  readonly source: string;
  readonly onCopy: StructuredCopyHandler;
  readonly onCopyCells?: CellsCopyHandler;
  readonly onUpdateLine?: LineEditHandler;
  /** 页级排版模式（lines 块展示形态；非 lines 块不受影响）。 */
  readonly mode?: ResultTextMode;
}) {
  const entries = blocksFrom(source);
  const linesEntry = entries.find(
    (entry) => entry.block.type === "lines" && Array.isArray(entry.block.lines),
  );
  const lines = parseResultLines(linesEntry?.block.lines);
  const lineModes = parseResultModes(linesEntry?.block.modes);
  const [copyFailed, setCopyFailed] = useState(false);
  const copy = async (
    blockIndex: number,
    format: StructuredCopyFormat,
  ): Promise<boolean> => {
    setCopyFailed(false);
    const copied = await onCopy(blockIndex, format);
    setCopyFailed(!copied);
    return copied;
  };
  let tableOrdinal = 0;
  // 表格显示序号与宿主 blockIndex 分离：序号仅用于标签，命令一律用
  // 原始下标。预计算映射避免渲染循环内可变量。
  const tableOrdinals = new Map<number, number>();
  for (const { block, index } of entries) {
    if (block.type === "table" && tableCells(block).length > 0) {
      tableOrdinal += 1;
      tableOrdinals.set(index, tableOrdinal);
    }
  }
  return (
    <div className="structured-result">
      {entries.map(({ block, index }) => {
        if (block.type === "lines" && Array.isArray(block.lines)) {
          return lines.length > 0 ? (
            <LinesBlock
              key={index}
              lines={lines}
              modes={lineModes}
              mode={mode}
              onUpdateLine={onUpdateLine}
            />
          ) : null;
        }
        if (block.type === "table" && tableOrdinals.has(index)) {
          return (
            <TableBlock
              key={index}
              block={block}
              blockIndex={index}
              ordinal={tableOrdinals.get(index)!}
              onCopy={copy}
              onCopyCells={onCopyCells}
            />
          );
        }
        if (block.type === "formula" && typeof block.text === "string") {
          return (
            <section key={index} className="structured-block" aria-label="公式">
              <Formula
                text={block.text}
                onCopyLaTeX={() => void copy(index, "latex")}
              />
            </section>
          );
        }
        if (block.type === "image" || block.image) {
          // 图片预览旁保留非空识别文本（图表内容/图片说明/印章文字），资产失效也不丢。
          const imageText =
            typeof block.text === "string" && block.text.length > 0
              ? block.text
              : undefined;
          return (
            <section key={index} className="structured-block">
              {block.image?.resource ? (
                <img
                  className="structured-image"
                  src={block.image.resource.url}
                  alt="文档图像区块"
                />
              ) : (
                <p className="form-note">
                  图片不可用：{block.image?.reason ?? "资源缺失"}
                </p>
              )}
              {imageText !== undefined && (
                <p className="structured-text">{imageText}</p>
              )}
            </section>
          );
        }
        if (isTextBlock(block)) {
          return (
            <p key={index} className="structured-text">
              {block.text}
            </p>
          );
        }
        return null;
      })}
      {copyFailed && (
        <p role="alert" className="form-note">
          复制失败：系统剪贴板暂不可用或结果已失效，请重试。
        </p>
      )}
    </div>
  );
}
