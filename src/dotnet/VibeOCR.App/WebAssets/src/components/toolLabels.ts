import type { Tool } from "./ImageCanvasEditor";

/** 工具 id → 中文短标签：工具栏按钮、族内菜单、对象列表与测试共用。
 * id 是跨语言契约（C# SelectionEditToolRow 同源），标签仅展示用。 */
export const TOOL_LABELS: Readonly<Record<Tool, string>> = {
  select: "选择",
  hand: "手形",
  textSelect: "取字",
  rectangle: "矩形",
  ellipse: "椭圆",
  arrow: "箭头",
  line: "直线",
  polyline: "折线",
  text: "文字",
  callout: "说明",
  mosaic: "马赛克",
  blur: "模糊",
  pen: "画笔",
  highlighter: "荧光笔",
  numbering: "序号",
  image: "图片",
  exclude: "屏蔽",
  solid: "遮挡",
  spotlight: "聚光灯",
  magnifier: "放大镜",
  objectEraser: "橡皮擦",
  crop: "裁剪",
  inpaint: "去水印",
  dupmatch: "重复匹配",
};
