/**
 * 纯文本行结果的排版模式（与 Classic keep/merge/smart 对齐）。各模式的
 * 拼接文本由宿主在发布 lines 区块时预计算（modes，拼接权威）：raw 为
 * Runtime raw_text 原样，其余由宿主 StructuredResultClipboard 计算。
 * Web 只按 modes 选择展示与提交复制/导出，不实现拼接算法，避免双实现
 * 漂移；宿主未发布某模式时该模式禁用并回退基础展示。
 */
export type ResultTextMode = "raw" | "line" | "merged" | "smart";

export const RESULT_TEXT_MODES: readonly {
  readonly value: ResultTextMode;
  readonly label: string;
}[] = [
  { value: "raw", label: "原始" },
  { value: "line", label: "每行" },
  { value: "merged", label: "一段" },
  { value: "smart", label: "智能分段" },
];

/** Classic 低置信度阈值（<80% 标记；score 缺失为 unknown，不标记）。 */
export const LOW_CONFIDENCE_THRESHOLD = 0.8;

export interface ResultTextLine {
  readonly text: string;
  readonly score: number | null;
  readonly bbox: readonly [number, number, number, number] | null;
  readonly manuallyEdited?: boolean;
}

/** 宿主预计算的各模式文本（展示/复制的拼接权威）。 */
export type ResultTextModes = Partial<Record<ResultTextMode, string>>;

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/** 解析宿主发布的 lines（宿主已按非空白过滤，TS 不重复过滤保行号一致）。 */
export function parseResultLines(value: unknown): readonly ResultTextLine[] {
  if (!Array.isArray(value)) return [];
  const lines: ResultTextLine[] = [];
  for (const entry of value) {
    if (entry === null || typeof entry !== "object") continue;
    const candidate = entry as {
      text?: unknown;
      score?: unknown;
      bbox?: unknown;
      is_manually_edited?: unknown;
    };
    if (typeof candidate.text !== "string" || candidate.text.length === 0)
      continue;
    const raw = candidate.bbox;
    const bbox =
      Array.isArray(raw) &&
      raw.length === 4 &&
      raw.every(isFiniteNumber) &&
      raw[0]! >= 0 &&
      raw[1]! >= 0 &&
      raw[2]! <= 1000 &&
      raw[3]! <= 1000 &&
      raw[0]! < raw[2]! &&
      raw[1]! < raw[3]!
        ? ([raw[0]!, raw[1]!, raw[2]!, raw[3]!] as const)
        : null;
    // 有效 score 需在 0..1；超范围视为 unknown，不伪装数值。
    const score =
      isFiniteNumber(candidate.score) &&
      candidate.score >= 0 &&
      candidate.score <= 1
        ? candidate.score
        : null;
    lines.push({
      text: candidate.text,
      score,
      bbox,
      manuallyEdited: candidate.is_manually_edited === true,
    });
  }
  return lines;
}

export function parseResultModes(value: unknown): ResultTextModes {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return {};
  const candidate = value as Record<string, unknown>;
  const modes: ResultTextModes = {};
  for (const mode of ["raw", "line", "merged", "smart"] as const) {
    // 与宿主契约：所有 string 都是合法值（raw 可为空串）；缺失只用
    // undefined 表达，禁用/回退判断不得用 falsy。
    if (typeof candidate[mode] === "string")
      modes[mode] = candidate[mode] as string;
  }
  return modes;
}

export function isLowConfidence(score: number | null): boolean {
  return score !== null && score < LOW_CONFIDENCE_THRESHOLD;
}

export function confidenceLabel(score: number | null): string {
  return score === null
    ? "置信度: 无置信度"
    : `置信度: ${(score * 100).toFixed(1)}%`;
}

/** lines 区块存在性、宿主可用模式与行数据的一次性解析。 */
export function linesInfoFromSource(source: string): {
  hasLines: boolean;
  modes: ResultTextModes;
  lines: readonly ResultTextLine[];
} {
  try {
    const parsed: unknown = JSON.parse(source);
    if (!Array.isArray(parsed))
      return { hasLines: false, modes: {}, lines: [] };
    for (const item of parsed) {
      if (
        item !== null &&
        typeof item === "object" &&
        (item as { type?: unknown }).type === "lines" &&
        Array.isArray((item as { lines?: unknown }).lines)
      ) {
        return {
          hasLines: true,
          modes: parseResultModes((item as { modes?: unknown }).modes),
          lines: parseResultLines((item as { lines?: unknown }).lines),
        };
      }
    }
    return { hasLines: false, modes: {}, lines: [] };
  } catch {
    return { hasLines: false, modes: {}, lines: [] };
  }
}
