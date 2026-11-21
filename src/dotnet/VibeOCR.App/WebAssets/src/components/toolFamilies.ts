/** 工具家族：仅视觉分组，族内子工具 id 与跨语言契约（mark 模型、快捷键、
 * 偏好 order/hidden/shortcuts）完全不变。主按钮展示族内"当前子工具"
 * （当前 tool 或族内最近使用，缺省为偏好顺序第一个可见子工具），旁挂
 * ▾ 按钮展开族内其余工具；不在任何家族的工具（选择/图片/橡皮擦等）
 * 仍为独立按钮。 */
export interface ToolFamily {
  readonly id: string;
  readonly label: string;
  readonly tools: readonly string[];
}

export const TOOL_FAMILIES: readonly ToolFamily[] = [
  {
    id: "shape",
    label: "形状",
    tools: ["rectangle", "ellipse", "arrow", "line", "polyline"],
  },
  { id: "brush", label: "画笔", tools: ["pen", "highlighter"] },
  { id: "text", label: "文字", tools: ["text", "callout", "numbering"] },
  {
    id: "mask",
    label: "遮挡",
    tools: ["mosaic", "blur", "solid", "exclude"],
  },
  { id: "emphasis", label: "强调", tools: ["spotlight", "magnifier"] },
  { id: "inpaint", label: "修补", tools: ["inpaint", "dupmatch"] },
];

/** 工具 → 所属家族（不在家族内返回 undefined）。 */
export function familyOfTool(tool: string): ToolFamily | undefined {
  return TOOL_FAMILIES.find((family) => family.tools.includes(tool));
}

/** 家族 ▾ 按钮的可访问名（测试与实现共用同一契约）。 */
export function familyMenuLabel(family: ToolFamily): string {
  return `${family.label}工具选项`;
}
