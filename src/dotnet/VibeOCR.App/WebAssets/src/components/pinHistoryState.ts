/** #238 宿主 pinEdit.history 投影的严格形状解析（camelCase）；
 * 畸形输入整体忽略（fail closed，不渲染半份列表）。 */

export interface PinHistoryEntry {
  readonly entryId: string;
  readonly createdAt: string;
  readonly width: number;
  readonly height: number;
  readonly bytes: number;
  readonly recognizedText?: string | null;
}

export interface PinHistory {
  readonly enabled: boolean;
  readonly error?: string | null;
  readonly count: number;
  readonly totalBytes: number;
  readonly maximumEntries: number;
  readonly maximumBytes: number;
  readonly entries: readonly PinHistoryEntry[];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function parsePinHistory(value: unknown): PinHistory | undefined {
  if (!isRecord(value)) return undefined;
  const { enabled, count, totalBytes, maximumEntries, maximumBytes, entries } =
    value;
  if (
    typeof enabled !== "boolean" ||
    typeof count !== "number" ||
    !Number.isSafeInteger(count) ||
    typeof totalBytes !== "number" ||
    !Number.isSafeInteger(totalBytes) ||
    typeof maximumEntries !== "number" ||
    !Number.isSafeInteger(maximumEntries) ||
    typeof maximumBytes !== "number" ||
    !Number.isSafeInteger(maximumBytes) ||
    !Array.isArray(entries)
  )
    return undefined;
  const parsed: PinHistoryEntry[] = [];
  for (const entry of entries) {
    if (!isRecord(entry)) return undefined;
    const { entryId, createdAt, width, height, bytes, recognizedText } = entry;
    if (
      typeof entryId !== "string" ||
      !/^[0-9a-f]{32}$/.test(entryId) ||
      typeof createdAt !== "string" ||
      typeof width !== "number" ||
      !Number.isSafeInteger(width) ||
      typeof height !== "number" ||
      !Number.isSafeInteger(height) ||
      typeof bytes !== "number" ||
      !Number.isSafeInteger(bytes) ||
      (recognizedText !== null &&
        recognizedText !== undefined &&
        typeof recognizedText !== "string")
    )
      return undefined;
    parsed.push({
      entryId,
      createdAt,
      width,
      height,
      bytes,
      recognizedText: recognizedText ?? null,
    });
  }
  return {
    enabled,
    error: typeof value.error === "string" ? value.error : null,
    count,
    totalBytes,
    maximumEntries,
    maximumBytes,
    entries: parsed,
  };
}
