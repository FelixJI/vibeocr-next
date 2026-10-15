import type { ScreenshotTextLayerState } from "../components/ImageCanvasEditor";
import type { EditorImportDescriptor } from "../components/ImageCanvasEditor";

import { resource } from "./FeatureValues";

export interface ScreenshotSessionState {
  readonly sessionId: string;
  readonly revision: number;
  readonly textSelectionRequested: boolean;
  readonly sceneEditing: boolean;
  /** 冻结显示基准附带的 [0,1000] 归一化排除框：仅初始化屏蔽标记。 */
  readonly excludeBoxes?: readonly {
    readonly x: number;
    readonly y: number;
    readonly width: number;
    readonly height: number;
  }[];
}

// 宿主回显的活动纯截图会话：会话 id + 当前内容修订。
export function screenshotSession(
  value: unknown,
): ScreenshotSessionState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<ScreenshotSessionState>;
  return typeof candidate.sessionId === "string" &&
    candidate.sessionId.length > 0 &&
    typeof candidate.revision === "number" &&
    Number.isSafeInteger(candidate.revision) &&
    candidate.revision >= 0
    ? {
        sessionId: candidate.sessionId,
        revision: candidate.revision,
        textSelectionRequested: candidate.textSelectionRequested === true,
        sceneEditing: candidate.sceneEditing === true,
        excludeBoxes: Array.isArray(candidate.excludeBoxes)
          ? candidate.excludeBoxes.filter(
              (
                box,
              ): box is {
                x: number;
                y: number;
                width: number;
                height: number;
              } =>
                typeof box === "object" &&
                box !== null &&
                ["x", "y", "width", "height"].every(
                  (key) =>
                    typeof (box as Record<string, unknown>)[key] === "number" &&
                    Number.isFinite((box as Record<string, unknown>)[key]),
                ),
            )
          : undefined,
      }
    : undefined;
}

// 宿主回发的 #232 对象导入批次；字段不完整时按无导入处理。
export function imageEditImport(
  value: unknown,
): EditorImportDescriptor | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<EditorImportDescriptor>;
  if (
    typeof candidate.requestId !== "string" ||
    candidate.requestId.length === 0 ||
    typeof candidate.sessionId !== "string" ||
    typeof candidate.revision !== "number" ||
    !Array.isArray(candidate.items) ||
    candidate.items.length === 0 ||
    candidate.items.length > 16
  )
    return undefined;
  const items = candidate.items.filter(
    (
      item,
    ): item is {
      kind: "image" | "text";
      assetId: string;
      resourceUri: string;
      byteLength: number;
      pixelWidth?: number;
      pixelHeight?: number;
    } =>
      typeof item === "object" &&
      item !== null &&
      (item.kind === "image" || item.kind === "text") &&
      typeof item.assetId === "string" &&
      item.assetId.length > 0 &&
      typeof item.resourceUri === "string" &&
      item.resourceUri.startsWith("https://app.vibeocr/__resource/") &&
      typeof item.byteLength === "number" &&
      Number.isFinite(item.byteLength) &&
      item.byteLength >= 0,
  );
  if (items.length === 0) return undefined;
  return {
    requestId: candidate.requestId,
    sessionId: candidate.sessionId,
    revision: candidate.revision,
    items,
  };
}

export interface TextLayerLineState {
  readonly text: string;
  readonly x1: number;
  readonly y1: number;
  readonly x2: number;
  readonly y2: number;
  readonly order?: number | null;
  readonly score?: number | null;
}

export function textLayerLines(value: unknown): readonly TextLayerLineState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is TextLayerLineState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const line = entry as Partial<TextLayerLineState>;
    return (
      typeof line.text === "string" &&
      line.text.length > 0 &&
      [line.x1, line.y1, line.x2, line.y2].every(
        (coordinate) =>
          typeof coordinate === "number" && Number.isFinite(coordinate),
      )
    );
  });
}

// 宿主回发的原位文字层状态；字段不完整时按无层处理。
export function screenshotTextLayer(
  value: unknown,
): ScreenshotTextLayerState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<ScreenshotTextLayerState>;
  if (typeof candidate.status !== "string" || candidate.status.length === 0)
    return undefined;
  const binding = screenshotSession(candidate.binding);
  const image = resource(candidate.image);
  return {
    status: candidate.status,
    reason: typeof candidate.reason === "string" ? candidate.reason : undefined,
    binding,
    modeId: typeof candidate.modeId === "string" ? candidate.modeId : null,
    image,
    lines: candidate.lines === null ? null : textLayerLines(candidate.lines),
  };
}
