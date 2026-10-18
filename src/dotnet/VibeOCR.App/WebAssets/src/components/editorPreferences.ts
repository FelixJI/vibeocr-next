export const EDITOR_TOOLS = [
  "select",
  "hand",
  "textSelect",
  "rectangle",
  "ellipse",
  "arrow",
  "line",
  "polyline",
  "text",
  "callout",
  "mosaic",
  "blur",
  "solid",
  "spotlight",
  "magnifier",
  "pen",
  "highlighter",
  "numbering",
  "objectEraser",
  "exclude",
  "crop",
  "inpaint",
  "dupmatch",
] as const;
export type ConfigurableEditorTool = (typeof EDITOR_TOOLS)[number];
export interface ImageOutputPreset {
  readonly id: "default";
  readonly nameTemplate: string;
  readonly format: "image/png" | "image/jpeg";
  readonly quality: number;
  readonly width: number;
  readonly height: number;
  readonly closeAfterCopy: boolean;
}
export interface EditorPreferences {
  readonly output: ImageOutputPreset;
  readonly order: readonly ConfigurableEditorTool[];
  readonly hidden: readonly ConfigurableEditorTool[];
  readonly shortcuts: Readonly<Partial<Record<ConfigurableEditorTool, string>>>;
}
export const DEFAULT_EDITOR_PREFERENCES: EditorPreferences = {
  output: {
    id: "default",
    nameTemplate: "{name}-{date}",
    format: "image/png",
    quality: 0.9,
    width: 0,
    height: 0,
    closeAfterCopy: false,
  },
  order: EDITOR_TOOLS,
  hidden: [],
  shortcuts: {},
};
export function editorPreferences(value: unknown): EditorPreferences {
  if (
    value &&
    typeof value === "object" &&
    "output" in value &&
    "order" in value &&
    "hidden" in value &&
    "shortcuts" in value
  )
    return value as EditorPreferences;
  return DEFAULT_EDITOR_PREFERENCES;
}
export function shortcutConflict(
  preferences: EditorPreferences,
): string | undefined {
  const shortcuts = Object.values(preferences.shortcuts).filter(Boolean);
  if (shortcuts.some((key) => !/^[a-z]$/.test(key)))
    return "工具快捷键仅支持单个英文字母；Ctrl+S/Z 等系统编辑键保留。";
  if (new Set(shortcuts).size !== shortcuts.length)
    return "快捷键冲突：同一按键只能分配给一个工具。";
  return undefined;
}
export function shortcutTool(
  event: {
    key: string;
    ctrlKey: boolean;
    metaKey: boolean;
    altKey: boolean;
    shiftKey: boolean;
  },
  preferences: EditorPreferences,
): ConfigurableEditorTool | undefined {
  if (
    event.ctrlKey ||
    event.metaKey ||
    event.altKey ||
    event.shiftKey ||
    shortcutConflict(preferences)
  )
    return undefined;
  return preferences.order.find(
    (tool) => preferences.shortcuts[tool] === event.key.toLowerCase(),
  );
}
