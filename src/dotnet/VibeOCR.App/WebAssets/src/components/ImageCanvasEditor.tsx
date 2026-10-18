import {
  Button,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Textarea,
  Toolbar,
  ToolbarButton,
  useFocusableGroup,
} from "@fluentui/react-components";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { createPortal, flushSync } from "react-dom";
import type { AppActions } from "../app/types";
import type { ImageRecognitionMode } from "../features/ScreenshotSession";
import { availabilityLabel } from "../features/FeatureValues";

import {
  isImeCompositionEvent,
  shouldRunDocumentCommand,
} from "../app/keyboard";
import {
  annotationFill,
  annotationStrokeAlpha,
  annotationStrokeWidth,
  constrainBoxDrag,
  constrainLineDrag,
  exclusionCoversOutput,
  exclusionDisplayPoints,
  exclusionNaturalRects,
  exclusionNormalizedRects,
  displayRectsToNormalizedRects,
  finalOutputSize,
  imageTransform,
  isExclusionMark,
  magnifierZoomOf,
  outputSize,
  projectPoint,
  rectIntersectsBox,
  rotateEditorState,
  spotlightDarkness,
  spotlightRegionsOf,
  MAGNIFIER_ZOOMS,
  type AnnotationArrowStyle,
  type AnnotationFill,
  type AnnotationLineStyle,
  type AnnotationStyle,
  type AnnotationTextStyle,
  type AnnotationTool,
  type CanvasSize,
  type EditorState,
  type Mark,
  type PixelPatch,
  type Point,
  type Rect,
} from "./annotationGeometry";
import {
  alignMarkIds,
  cloneMarks,
  deleteMarkIds,
  distributeMarkIds,
  expandGroupSelection,
  groupMarkIds,
  markBoundsOf,
  markOffscreen,
  marksContainedInRect,
  mirrorMarkIds,
  moveMarkIds,
  reorderMarkIds,
  rotateMarkIds,
  scaleMarkIds,
  selectionBounds,
  selectionUnits,
  setMarkLocked,
  ungroupMarkIds,
  unlockedMarkIds,
  type ObjectAlign,
  type ObjectDistributeAxis,
  type ObjectZOrder,
} from "./annotationObjects";
import {
  AnnotationNumberInput,
  AnnotationTextPanel,
} from "./AnnotationTextPanel";
import {
  calloutLeaderPaths,
  nextNumberOrdinal,
  drawTextAnnotation,
  textAnnotationLocalBounds,
  transformTextPoint,
  isFontMark,
  isContentMark,
  resolveAnnotationFont,
} from "./annotationText";
import {
  annotationArrowGeometry,
  annotationPaths,
  hitStrokePaths,
} from "./annotationStrokeGeometry";
import { BRIDGE_VERSION, ChromeWebViewTransport } from "../bridge/client";
import { uploadAnnotatedImage } from "./annotationHandoff";
import {
  buildContributionState,
  contributionStateBytes,
  countReferencedAssets,
  PROJECT_CANVAS_TOTAL_PIXELS,
  patchEntryBudget,
  PROJECT_PATCH_MAX_PIXELS,
  PROJECT_PATCH_TOTAL_BYTES,
  PROJECT_PATCH_TOTAL_PIXELS,
  PROJECT_RESIZE_MAX_SIDE,
  PROJECT_RESIZE_TOTAL_PIXELS,
  PROJECT_STATE_BUDGET_BYTES,
  SPOTLIGHT_MAX_REGIONS,
  restoreEditorState,
  wireToMark,
  type WireEditorState,
  uploadProjectContribution,
} from "./project/projectWire";
import { captureSceneStyle } from "./captureSceneLayout";
import type { CaptureScene } from "../bridge/captureScene";
import { CapturePixelPresentation } from "./capturePixelPresentation";
import { ImageTextLayer } from "./ImageTextLayer";
import { toImageTextLines } from "./imageTextLayerGeometry";
import { ResultImageTextOverlay } from "./ResultImageTextOverlay";
import type {
  IndexedResultLine,
  OverlayLineEditHandler,
} from "./ResultImageTextOverlay";
import { ToolbarIcon } from "./ToolbarIcon";
import type { ToolbarIconName } from "./toolbarIconData";
import type {
  InpaintMaskRequest,
  InpaintRect,
  InpaintResponse,
} from "./inpaint/inpaint";
import { INPAINT_MAX_TOTAL_PATCH_PIXELS } from "./inpaint/inpaint";
import {
  INPAINT_MAX_MASK_PIXELS,
  INPAINT_MAX_ROI_PIXELS,
} from "./inpaint/inpaint";
import {
  describeInpaintFailure,
  naturalRectToDisplay,
  preflightSelection,
  selectionToNaturalRect,
} from "./inpaint/inpaintSelection";
import {
  applyMaskBrush,
  applyMaskRect,
  maskBounds,
  maskCount,
  totalDistinctPatchPixels,
  type InpaintMaskOpResult,
  type InpaintMaskState,
} from "./inpaint/inpaintMaskState";
import type {
  DuplicateCandidate,
  DuplicateResponse,
} from "./duplicate/duplicateMatch";
import { DUPLICATE_MAX_IMAGE_PIXELS } from "./duplicate/duplicateMatch";

/** 宿主回发的原位文字层状态（recognition.textLayer 投影）。 */
export interface ScreenshotTextLayerState {
  readonly status: string;
  readonly reason?: string | null;
  readonly binding?: {
    readonly sessionId: string;
    readonly revision: number;
  } | null;
  readonly modeId?: string | null;
  readonly image?: {
    readonly url: string;
    readonly mediaType: string;
    readonly byteLength: number;
  } | null;
  readonly lines?:
    | readonly {
        readonly text: string;
        readonly x1: number;
        readonly y1: number;
        readonly x2: number;
        readonly y2: number;
        readonly order?: number | null;
        readonly score?: number | null;
      }[]
    | null;
}

type Tool =
  | "select"
  | "hand"
  | "textSelect"
  | AnnotationTool
  | "crop"
  | "inpaint"
  | "dupmatch"
  /** #235 对象橡皮擦：一次拖动擦除命中的未锁定对象，整次手势一次撤销。 */
  | "objectEraser";

/** 属性排样式变更：判别联合保证每个 kind 携带对应类型，无需盲转换。 */
type StyleChange =
  | { readonly kind: "color"; readonly value: string }
  | { readonly kind: "strokeWidth"; readonly value: number }
  | { readonly kind: "strokeOpacity"; readonly value: number }
  | { readonly kind: "fillEnabled"; readonly value: boolean }
  | { readonly kind: "fillColor"; readonly value: string }
  | { readonly kind: "fillOpacity"; readonly value: number }
  | { readonly kind: "lineStyle"; readonly value: AnnotationLineStyle }
  | { readonly kind: "cornerRadius"; readonly value: number }
  | { readonly kind: "arrowStyle"; readonly value: AnnotationArrowStyle }
  | { readonly kind: "arrowDouble"; readonly value: boolean };

const EMPTY: EditorState = { rotation: 0, marks: [] };
const DEFAULT_COLOR = "#f38b35";

// 沿用标注手势的8px阈值；聚光灯孔洞的两个维度都须有效。
const MIN_DRAW_DISTANCE = 8;
const TOOL_LABELS: Readonly<Record<Tool, string>> = {
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

/** 图标语义需用户确认后才新增：直线/折线暂无矢量图标，用清晰文字按钮，
 * 不渲染空图标按钮；其余工具沿用既有 Tabler 图标。 */
const TOOL_ICONS: Readonly<Partial<Record<Tool, ToolbarIconName>>> = {
  select: "select",
  hand: "hand",
  textSelect: "textSelect",
  rectangle: "rectangle",
  ellipse: "ellipse",
  arrow: "arrow",
  text: "text",
  mosaic: "mosaic",
  blur: "blur",
  pen: "pen",
  highlighter: "highlighter",
  numbering: "numbering",
  exclude: "exclude",
  crop: "crop",
  inpaint: "inpaint",
};
// 遮挡/重复匹配为新增工具：未确认新图标，仅用清晰文字按钮。

/** 下排属性 gating：仅当前工具真正消费的设置才展示（选择/手形/裁剪等
 * 无关设置不出现）。 */
const STROKE_TOOLS: readonly Tool[] = [
  "rectangle",
  "ellipse",
  "arrow",
  "line",
  "polyline",
  "pen",
  "highlighter",
];
const COLOR_TOOLS: readonly Tool[] = [
  ...STROKE_TOOLS,
  "text",
  "numbering",
  "callout",
  "solid",
];
/** 线型（实/虚/点）属性适用的描边工具。 */
const LINE_STYLE_TOOLS: readonly Tool[] = [
  "rectangle",
  "ellipse",
  "line",
  "polyline",
  "arrow",
];
/** 圆角仅矩形提供；箭头端头样式仅箭头工具。 */
const CORNER_TOOLS: readonly Tool[] = ["rectangle"];
const ARROW_TOOLS: readonly Tool[] = ["arrow"];

const TOOL_ORDER: readonly Tool[] = [
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
];

/** 编辑器用途：full=完整图片编辑（原工具集全量保留）；recognition=单次
 * 识别精简，只保留去水印、旋转、屏蔽三类图片处理工具及必要交互入口
 * （选择用于调整屏蔽区，取字用于会话文字选择，均不产生图像处理）。 */
export type EditorToolset = "full" | "recognition";

const TOOLSETS: Readonly<Record<EditorToolset, readonly Tool[]>> = {
  full: TOOL_ORDER,
  recognition: ["select", "textSelect", "exclude", "inpaint"],
};

const STROKE_COLORS = [
  DEFAULT_COLOR,
  "#e02020",
  "#12a150",
  "#1f6feb",
  "#f2c94c",
  "#ffffff",
] as const;
const STROKE_WIDTHS = [2, 3, 5, 8] as const;
/** 矩形圆角档位（px）：0 与旧对象直角渲染一致。 */
const RECT_CORNER_RADII = [0, 6, 12, 18] as const;
const FONT_SIZES = [16, 24, 32, 48] as const;
/** 形状（矩形/椭圆）支持填充；其余描边工具仅复用边框不透明度。 */
const FILL_TOOLS: readonly Tool[] = ["rectangle", "ellipse"];
const DEFAULT_FILL_COLOR = "#ffffff";
const DEFAULT_FILL_OPACITY = 50;

/** 单个工具的属性记忆全集：只有该工具真正消费的字段才会展示/写入标记。 */
interface ToolAttributes {
  readonly color: string;
  readonly strokeWidth: number;
  readonly strokeOpacity: number;
  readonly lineStyle: AnnotationLineStyle;
  readonly cornerRadius: number;
  readonly fillEnabled: boolean;
  readonly fillColor: string;
  readonly fillOpacity: number;
  readonly arrowStyle: AnnotationArrowStyle;
  readonly arrowDouble: boolean;
  readonly fontSize: number;
  readonly textStyle: AnnotationTextStyle;
  readonly numberStart: number;
  readonly intensity: number;
  /** 马赛克/模糊绘制方式：矩形拖拽或自由笔刷。 */
  readonly effectMode: "rect" | "brush";
  /** 马赛克/模糊笔刷直径（px）；仅自由笔刷模式消费。 */
  readonly brushSize: number;
  /** 修补掩膜笔刷直径（px）；仅笔刷增/减模式消费。 */
  readonly inpaintBrushSize: number;
  /** 修补掩膜绘制方式：矩形/笔刷的增减。 */
  readonly maskMode: InpaintMaskMode;
  /** 聚光灯遮罩暗度（0-100）：写入对象 style.fill.opacity。 */
  readonly spotlightDarkness: number;
  /** 放大镜倍率：有界 [1.5,8]，写入对象 zoom。 */
  readonly magnifierZoom: number;
}

/** 工具属性默认值：与旧标记缺省渲染一致（实线/直角/开放单向箭头/空心）。 */
const DEFAULT_TOOL_ATTRIBUTES: ToolAttributes = {
  color: DEFAULT_COLOR,
  strokeWidth: 3,
  strokeOpacity: 100,
  lineStyle: "solid",
  cornerRadius: 0,
  fillEnabled: false,
  fillColor: DEFAULT_FILL_COLOR,
  fillOpacity: DEFAULT_FILL_OPACITY,
  arrowStyle: "open",
  arrowDouble: false,
  fontSize: 24,
  textStyle: { fontBold: false },
  numberStart: 1,
  intensity: 2,
  effectMode: "rect",
  brushSize: 24,
  inpaintBrushSize: 20,
  maskMode: "rect-add",
  spotlightDarkness: 55,
  magnifierZoom: 2,
};

/** 马赛克/模糊强度：连续值 [1,3]，旧档位 1=弱、2=中（缺省）、3=强为
 * 兼容锚点；非有限值回退默认 2。 */
const EFFECT_INTENSITY_MIN = 1;
const EFFECT_INTENSITY_MAX = 3;
const EFFECT_INTENSITY_STEP = 0.1;
/** 连续强度锚点间的分段线性插值：预览/导出/强度显示同一函数，
 * 保证显示与像素一致；旧 1/2/3 档位渲染不变。 */
function interpolateAnchor(
  value: number,
  stops: readonly [number, number, number],
): number {
  if (value <= 1) return stops[0]!;
  if (value >= 3) return stops[2]!;
  if (value <= 2) return stops[0]! + (value - 1) * (stops[1]! - stops[0]!);
  return stops[1]! + (value - 2) * (stops[2]! - stops[1]!);
}

/** 连续强度 → 马赛克块尺寸：1→8、2→14、3→26，中间分段线性。 */
function mosaicCellSize(intensity: number): number {
  return interpolateAnchor(intensity, [8, 14, 26]);
}

/** 连续强度 → 模糊半径：1→5、2→10、3→20，中间分段线性。 */
function blurRadius(intensity: number): number {
  return interpolateAnchor(intensity, [5, 10, 20]);
}

/** 标记携带的强度：夹到 [1,3] 并保留一位小数；非法值回退默认 2。 */
function markEffectIntensity(mark: Mark): number {
  const value = mark.style?.intensity;
  if (typeof value !== "number" || !Number.isFinite(value)) return 2;
  return Math.round(Math.min(3, Math.max(1, value)) * 10) / 10;
}

/** 强度显示标签：锚点用旧档位名，中间值显示数值，与像素同源。 */
function intensityLabel(value: number): string {
  if (value === 1) return "1.0 弱";
  if (value === 2) return "2.0 中";
  if (value === 3) return "3.0 强";
  return value.toFixed(1);
}

/** 笔刷直径档位（px）。 */
const EFFECT_BRUSH_SIZES = [12, 24, 40, 64] as const;
/** 修补掩膜笔刷直径档位（px）：与效果笔刷独立记忆。 */
const INPAINT_BRUSH_SIZES = [10, 20, 36, 60] as const;
const DEFAULT_SOLID_COLOR = "#000000";

/** 修补掩膜绘制方式：矩形/笔刷 × 添加/移除。 */
type InpaintMaskMode =
  "rect-add" | "rect-remove" | "brush-add" | "brush-remove";
const INPAINT_MASK_MODES: readonly {
  readonly value: InpaintMaskMode;
  readonly label: string;
}[] = [
  { value: "rect-add", label: "框选添加" },
  { value: "rect-remove", label: "框选移除" },
  { value: "brush-add", label: "笔刷添加" },
  { value: "brush-remove", label: "笔刷移除" },
];

/** 数值输入提交：仅 0-100 整数生效；空/越界/非整数丢弃，不意外写 0。 */
function commitOpacityInput(
  raw: string | undefined,
  apply: (value: number) => void,
): void {
  if (raw === undefined) return;
  const trimmed = raw.trim();
  if (!/^\d{1,3}$/.test(trimmed)) return;
  const value = Number(trimmed);
  if (value >= 0 && value <= 100) apply(value);
}

/** input[type=color] 仅接受 #rrggbb；非法值回退默认色。 */
function normalizeHexColor(value: string): string {
  return /^#[0-9a-fA-F]{6}$/.test(value) ? value : DEFAULT_COLOR;
}

/** 画布取样像素 → #rrggbb。 */
function rgbToHex(r: number, g: number, b: number): string {
  const part = (value: number) =>
    Math.max(0, Math.min(255, Math.round(value)))
      .toString(16)
      .padStart(2, "0");
  return `#${part(r)}${part(g)}${part(b)}`;
}

/** EyeDropper API（Chromium/WebView2）：不可用时隐藏取色入口。 */
interface EyeDropperResult {
  readonly sRGBHex: string;
}
interface EyeDropperSession {
  open(): Promise<EyeDropperResult>;
}
type EyeDropperConstructor = new () => EyeDropperSession;
type WindowWithEyeDropper = Window & {
  readonly EyeDropper?: EyeDropperConstructor;
};

/** 最终编码格式：blob.type 决定真实编码器，扩展名/MIME 与文件签名一致。 */
export type OutputImageFormat = "image/png" | "image/jpeg";
const JPEG_QUALITIES = [0.6, 0.75, 0.85, 0.9, 0.95] as const;
const SCALE_PERCENTS = [25, 50, 75, 100, 150, 200] as const;
/** 输出画布上限：单边与总像素均留在浏览器 canvas 面积上限内。 */
const MAX_OUTPUT_DIMENSION = 16384;
const MAX_OUTPUT_PIXELS = 64_000_000;

/** 活动纯截图会话句柄；宿主回显当前内容修订与冻结基准的初始排除框。 */
export interface ScreenshotSessionHandle {
  readonly sessionId: string;
  readonly revision: number;
  /** [0,1000] 归一化排除框：仅在基准变化时用于重建初始屏蔽标记。 */
  readonly excludeBoxes?: readonly {
    readonly x: number;
    readonly y: number;
    readonly width: number;
    readonly height: number;
  }[];
}

/** 去水印预览结果：仅存在于组件状态，绝不进入历史；明确应用才 commit。 */
interface InpaintPreviewResult {
  readonly generation: number;
  readonly naturalRect: InpaintRect;
  readonly canvas: HTMLCanvasElement;
}

/** 宿主 #232 导入项描述符：图片/文本体一律经 opaque 资源获取，不内联。 */
export interface EditorImportItem {
  readonly kind: "image" | "text";
  readonly assetId: string;
  readonly resourceUri: string;
  readonly byteLength: number;
  readonly pixelWidth?: number;
  readonly pixelHeight?: number;
}

/** 一次成功导入批次：绑定宿主会话/修订与请求 id，供幂等/迟到判定。 */
export interface EditorImportDescriptor {
  readonly requestId: string;
  readonly sessionId: string;
  readonly revision: number;
  readonly items: readonly EditorImportItem[];
}

/** #233 恢复安装包：宿主 ack 切换成功后由页面交付；全部为已 decode 强
 * 引用（位图/画布），安装后不重 fetch 任何已释放 URL。 */
export interface EditorProjectInstall {
  /** 幂等消费键（pendingId）。 */
  readonly token: string;
  readonly sourceUrl: string;
  /** 已 decode 的 source 强引用：安装直接使用，不经 URL 重载。 */
  readonly sourceImage: HTMLImageElement;
  readonly savedCanvas: { readonly width: number; readonly height: number };
  readonly state: WireEditorState;
  readonly assets: ReadonlyMap<
    string,
    { image: HTMLImageElement; url: string }
  >;
  readonly patches: ReadonlyMap<number, HTMLCanvasElement>;
}

/** draw()/exportCanvas 共用的补丁绘制层：矩形为原图（未旋转）像素空间。 */
export interface InpaintDrawLayer {
  readonly patches?: readonly {
    readonly rect: InpaintRect;
    readonly canvas: HTMLCanvasElement;
  }[];
  readonly preview?: {
    readonly rect: InpaintRect;
    readonly canvas: HTMLCanvasElement;
  };
  readonly selection?: { readonly start: Point; readonly end: Point };
  /** 未提交修补掩膜叠加层（原图空间，绿色提示）：仅编辑预览，不入导出。 */
  readonly maskOverlay?: {
    readonly rect: InpaintRect;
    readonly canvas: HTMLCanvasElement;
  };
  /** 重复匹配候选叠加层（原图空间）：仅编辑预览，不入导出。 */
  readonly duplicateOverlay?: readonly {
    readonly rect: InpaintRect;
    readonly accepted: boolean;
    readonly label: string;
  }[];
}

/** 编辑器发出的会话/导出命令 scope：同一命令形状在两个宿主 feature 上
 * 等价实现（非 scene 图片编辑页发 imageEdit.*，识别面与 scene 发
 * recognition.*，宿主命令约定同步演进）。 */
export type EditorCommandScope = "recognition" | "imageEdit" | "pinEdit";

/** 编辑器命令后缀：与 AppActionType 中两个 scope 的同名后缀一致。 */
type EditorCommandSuffix =
  | "notifyScreenshotRevision"
  | "closeScreenshotSession"
  | "copyScreenshotImage"
  | "saveScreenshotImage"
  | "pinScreenshotImage"
  | "recognizeScreenshotImage"
  | "prepareScreenshotTextLayer"
  | "cancelScreenshotTextLayer"
  | "copyScreenshotSelection"
  | "copyAnnotatedImage"
  | "saveAnnotatedImage"
  | "ackImport"
  | "rejectImport"
  | "releaseAssets";

type EditorCommandMap = Readonly<
  Record<EditorCommandSuffix, `${EditorCommandScope}.${EditorCommandSuffix}`>
>;

/** 静态全量映射：模板字面量类型保证每个命令名都是 AppActionType 的
 * 合法成员，不需要宽泛断言。 */
const EDITOR_COMMANDS: Readonly<Record<EditorCommandScope, EditorCommandMap>> =
  {
    recognition: {
      notifyScreenshotRevision: "recognition.notifyScreenshotRevision",
      closeScreenshotSession: "recognition.closeScreenshotSession",
      copyScreenshotImage: "recognition.copyScreenshotImage",
      saveScreenshotImage: "recognition.saveScreenshotImage",
      pinScreenshotImage: "recognition.pinScreenshotImage",
      recognizeScreenshotImage: "recognition.recognizeScreenshotImage",
      prepareScreenshotTextLayer: "recognition.prepareScreenshotTextLayer",
      cancelScreenshotTextLayer: "recognition.cancelScreenshotTextLayer",
      copyScreenshotSelection: "recognition.copyScreenshotSelection",
      copyAnnotatedImage: "recognition.copyAnnotatedImage",
      saveAnnotatedImage: "recognition.saveAnnotatedImage",
      ackImport: "recognition.ackImport",
      rejectImport: "recognition.rejectImport",
      releaseAssets: "recognition.releaseAssets",
    },
    imageEdit: {
      notifyScreenshotRevision: "imageEdit.notifyScreenshotRevision",
      closeScreenshotSession: "imageEdit.closeScreenshotSession",
      copyScreenshotImage: "imageEdit.copyScreenshotImage",
      saveScreenshotImage: "imageEdit.saveScreenshotImage",
      pinScreenshotImage: "imageEdit.pinScreenshotImage",
      recognizeScreenshotImage: "imageEdit.recognizeScreenshotImage",
      prepareScreenshotTextLayer: "imageEdit.prepareScreenshotTextLayer",
      cancelScreenshotTextLayer: "imageEdit.cancelScreenshotTextLayer",
      copyScreenshotSelection: "imageEdit.copyScreenshotSelection",
      copyAnnotatedImage: "imageEdit.copyAnnotatedImage",
      saveAnnotatedImage: "imageEdit.saveAnnotatedImage",
      ackImport: "imageEdit.ackImport",
      rejectImport: "imageEdit.rejectImport",
      releaseAssets: "imageEdit.releaseAssets",
    },
    // #238 贴图内编辑：宿主只实现真实消费者动作（C6）；映射保持全量
    // 类型形状，未消费动作在 pin 模式 UI 不可达且宿主 fail-closed 拒绝。
    pinEdit: {
      notifyScreenshotRevision: "pinEdit.notifyScreenshotRevision",
      closeScreenshotSession: "pinEdit.closeScreenshotSession",
      copyScreenshotImage: "pinEdit.copyScreenshotImage",
      saveScreenshotImage: "pinEdit.saveScreenshotImage",
      pinScreenshotImage: "pinEdit.pinScreenshotImage",
      recognizeScreenshotImage: "pinEdit.recognizeScreenshotImage",
      prepareScreenshotTextLayer: "pinEdit.prepareScreenshotTextLayer",
      cancelScreenshotTextLayer: "pinEdit.cancelScreenshotTextLayer",
      copyScreenshotSelection: "pinEdit.copyScreenshotSelection",
      copyAnnotatedImage: "pinEdit.copyAnnotatedImage",
      saveAnnotatedImage: "pinEdit.saveAnnotatedImage",
      ackImport: "pinEdit.ackImport",
      rejectImport: "pinEdit.rejectImport",
      releaseAssets: "pinEdit.releaseAssets",
    },
  };

/** #235 通用百分比输入：内部草稿，blur/Enter 提交 0-100 整数；空/越界/非整数不写回。 */
function PercentInputControl({
  label,
  ariaLabel,
  value,
  disabled,
  onCommit,
}: {
  readonly label: string;
  readonly ariaLabel: string;
  readonly value: number;
  readonly disabled?: boolean;
  readonly onCommit: (value: number) => void;
}) {
  const [draft, setDraft] = useState<string | undefined>();
  return (
    <label className="editor-style-control">
      <span aria-hidden="true">{label}</span>
      <Input
        aria-label={ariaLabel}
        disabled={disabled}
        inputMode="numeric"
        max={100}
        min={0}
        onBlur={() => {
          if (draft !== undefined) {
            commitOpacityInput(draft, onCommit);
            setDraft(undefined);
          }
        }}
        onChange={(_, data) => setDraft(data.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter") event.currentTarget.blur();
        }}
        size="small"
        style={{ width: 64 }}
        title="0-100 的整数百分比"
        type="number"
        value={draft ?? String(value)}
      />
      <span aria-hidden="true">%</span>
    </label>
  );
}

interface ImageCanvasEditorProps {
  readonly actions: AppActions;
  readonly canExport: boolean;
  readonly canRecognize: boolean;
  readonly recognitionModes?: readonly ImageRecognitionMode[];
  readonly source: string;
  readonly captureScene?: CaptureScene;
  /** 原图体积（字节）；宿主可选提供，缺省时只显示尺寸不显示原图体积。 */
  readonly sourceByteLength?: number;
  readonly session?: ScreenshotSessionHandle;
  readonly textLayer?: ScreenshotTextLayerState;
  /** #232 宿主系统内容导入描述符：全部解码成功后一次 commit 追加对象。 */
  readonly systemImport?: EditorImportDescriptor;
  /** #233 工程恢复安装包：宿主 ack 成功切换后安装已准备数据。 */
  readonly projectInstall?: EditorProjectInstall;
  readonly installProject?: (token: string, install: () => void) => boolean;
  readonly interactionLocked?: boolean;
  readonly autoText?: boolean;
  readonly showAutoTextPreference?: boolean;
  readonly onAutoTextChange?: (enabled: boolean) => void;
  /** 显式识别提交成功后的宿主交接（纯编辑页导航到识别承载面）。 */
  readonly onRecognitionSubmitted?: () => void;
  /** 工具集用途：默认 full；单次识别面传 recognition 精简为
   * 去水印/旋转/屏蔽三类处理工具。 */
  readonly toolset?: EditorToolset;
  /** 会话命令 scope：默认 recognition；非 scene 图片编辑页传 imageEdit；
   * 贴图内编辑传 pinEdit。 */
  readonly commandScope?: EditorCommandScope;
  /** #238 贴图内编辑模式：主排以“完成贴图”取代“保存工程”，隐藏
   * 钉图/识别入口；会话命令走 pinEdit.*。 */
  readonly pinSnapshotApply?: boolean;
  /** 左侧校对面数据：冻结参考图（宿主 state.resultImage，绝不用会话基准
   * 图冒充）+ 结果行框与行编辑闭环。提供且 proofreadActive 时，同一图片
   * 区显示校对层；编辑画布仅隐藏（display:none），编辑历史与未提交状态
   * 保留，文档级 Ctrl+S/Ctrl+W 在校对模式挂起。 */
  readonly proofread?: {
    readonly imageUrl: string;
    /** 绑定键：参考图或结构化资源变化时校对层整体重挂（清空编辑草稿）。 */
    readonly bindingKey: string;
    readonly lines: readonly IndexedResultLine[];
    readonly onUpdateLine?: OverlayLineEditHandler;
  };
  /** 校对层是否激活：切换由宿主 UI 控制；无 proofread 数据时忽略。 */
  readonly proofreadActive?: boolean;
}

/** 属性下排样式面板：颜色/线宽/边框不透明度/填充开关/颜色/不透明度；
 * 不透明度为 0-100 任意整数，blur/Enter 提交，空/越界/非整数不写回。
 * 所有变更经统一 onStyleChange 上交，荧光笔边框不透明度与自身 35% 透明
 * 相乘的语义由父级 draw 决定。 */
function AnnotationStylePanel({
  showColorControl,
  showStrokeControls,
  showLineStyleControl,
  showCornerControl,
  showArrowControls,
  showFillControl,
  activeStyleColor,
  activeStrokeWidth,
  activeStrokeOpacity,
  activeLineStyle,
  activeCornerRadius,
  activeArrowStyle,
  activeArrowDouble,
  activeFillEnabled,
  activeFillColor,
  activeFillOpacity,
  eyedropperAvailable,
  eyedropperBusy,
  styleTargetId,
  onPickColor,
  onStyleChange,
}: {
  readonly showColorControl: boolean;
  readonly showStrokeControls: boolean;
  readonly showLineStyleControl: boolean;
  readonly showCornerControl: boolean;
  readonly showArrowControls: boolean;
  readonly showFillControl: boolean;
  readonly activeStyleColor: string;
  readonly activeStrokeWidth: number;
  readonly activeStrokeOpacity: number;
  readonly activeLineStyle: AnnotationLineStyle;
  readonly activeCornerRadius: number;
  readonly activeArrowStyle: AnnotationArrowStyle;
  readonly activeArrowDouble: boolean;
  readonly activeFillEnabled: boolean;
  readonly activeFillColor: string;
  readonly activeFillOpacity: number;
  readonly eyedropperAvailable: boolean;
  readonly eyedropperBusy: boolean;
  /** 就地编辑目标对象 id：草稿开始时冻结，blur 提交带回，防止画布
   * pointerdown 已切换选区后把草稿写进新对象。 */
  readonly styleTargetId: string | undefined;
  readonly onPickColor: () => void;
  readonly onStyleChange: (
    change: StyleChange,
    targetId?: string | null,
  ) => void;
}) {
  // 数值输入草稿仅存在于此面板：焦点内自由编辑，blur/Enter 才提交。
  // targetId 随草稿冻结：即便提交前焦点被画布点击夺走，也写入当时
  // 正在编辑的对象，而不是重新解析的当前选区。
  const [opacityDraft, setOpacityDraft] = useState<
    | { kind: "stroke" | "fill"; raw: string; targetId: string | null }
    | undefined
  >();
  if (
    !showColorControl &&
    !showStrokeControls &&
    !showLineStyleControl &&
    !showCornerControl &&
    !showArrowControls &&
    !showFillControl
  )
    return null;
  return (
    <>
      {showColorControl && (
        <div
          aria-label="标注颜色"
          className="editor-color-control"
          role="group"
        >
          {STROKE_COLORS.map((color) => (
            <button
              aria-label={`常用颜色 ${color}`}
              aria-pressed={
                normalizeHexColor(activeStyleColor).toLowerCase() === color
              }
              className="editor-color-swatch"
              key={color}
              onClick={() => onStyleChange({ kind: "color", value: color })}
              title={color}
              type="button"
              style={{ background: color }}
            />
          ))}
          <input
            aria-label="自定义颜色"
            onChange={(event) =>
              onStyleChange({ kind: "color", value: event.target.value })
            }
            title="自定义颜色"
            type="color"
            value={normalizeHexColor(activeStyleColor)}
          />
          <Button
            appearance="subtle"
            aria-label="屏幕取色"
            disabled={eyedropperBusy}
            icon={<ToolbarIcon name="pipette" />}
            onClick={onPickColor}
            size="small"
            title={
              eyedropperAvailable ? "屏幕取色" : "取色：点击后在画布上取样颜色"
            }
          />
        </div>
      )}
      {showStrokeControls && (
        <>
          <label className="editor-style-control">
            <span aria-hidden="true">线宽</span>
            <Select
              aria-label="线条宽度"
              size="small"
              value={String(activeStrokeWidth)}
              onChange={(_, data) =>
                onStyleChange({
                  kind: "strokeWidth",
                  value: Number(data.value),
                })
              }
            >
              {Number.isFinite(activeStrokeWidth) &&
                activeStrokeWidth > 0 &&
                !STROKE_WIDTHS.some((width) => width === activeStrokeWidth) && (
                  <option value={String(activeStrokeWidth)}>
                    {activeStrokeWidth}px
                  </option>
                )}
              {STROKE_WIDTHS.map((width) => (
                <option key={width} value={String(width)}>
                  {width}px
                </option>
              ))}
            </Select>
          </label>
          <label className="editor-style-control">
            <span aria-hidden="true">边框不透明度</span>
            <Input
              aria-label="边框不透明度"
              inputMode="numeric"
              max={100}
              min={0}
              onBlur={() => {
                if (opacityDraft?.kind === "stroke")
                  commitOpacityInput(opacityDraft.raw, (value) =>
                    onStyleChange(
                      { kind: "strokeOpacity", value },
                      opacityDraft.targetId,
                    ),
                  );
                setOpacityDraft(undefined);
              }}
              onChange={(_, data) => {
                setOpacityDraft((current) =>
                  current?.kind === "stroke"
                    ? { ...current, raw: data.value }
                    : {
                        kind: "stroke",
                        raw: data.value,
                        targetId: styleTargetId ?? null,
                      },
                );
              }}
              onKeyDown={(event) => {
                if (event.key === "Enter") event.currentTarget.blur();
              }}
              size="small"
              style={{ width: 72 }}
              title="0-100 的整数百分比"
              type="number"
              value={
                opacityDraft?.kind === "stroke"
                  ? opacityDraft.raw
                  : String(activeStrokeOpacity)
              }
            />
            <span aria-hidden="true">%</span>
          </label>
        </>
      )}
      {showLineStyleControl && (
        <label className="editor-style-control">
          <span aria-hidden="true">线型</span>
          <Select
            aria-label="线型"
            size="small"
            value={activeLineStyle}
            onChange={(_, data) =>
              onStyleChange({
                kind: "lineStyle",
                value:
                  data.value === "dashed"
                    ? "dashed"
                    : data.value === "dotted"
                      ? "dotted"
                      : "solid",
              })
            }
          >
            <option value="solid">实线</option>
            <option value="dashed">虚线</option>
            <option value="dotted">点线</option>
          </Select>
        </label>
      )}
      {showCornerControl && (
        <label className="editor-style-control">
          <span aria-hidden="true">圆角</span>
          <Select
            aria-label="矩形圆角"
            size="small"
            value={String(activeCornerRadius)}
            onChange={(_, data) =>
              onStyleChange({ kind: "cornerRadius", value: Number(data.value) })
            }
          >
            {Number.isFinite(activeCornerRadius) &&
              activeCornerRadius >= 0 &&
              !RECT_CORNER_RADII.some(
                (radius) => radius === activeCornerRadius,
              ) && (
                <option value={String(activeCornerRadius)}>
                  {activeCornerRadius}px
                </option>
              )}
            {RECT_CORNER_RADII.map((radius) => (
              <option key={radius} value={String(radius)}>
                {radius === 0 ? "直角" : `${radius}px`}
              </option>
            ))}
          </Select>
        </label>
      )}
      {showArrowControls && (
        <>
          <label className="editor-style-control">
            <span aria-hidden="true">箭头样式</span>
            <Select
              aria-label="箭头样式"
              size="small"
              value={activeArrowStyle}
              onChange={(_, data) =>
                onStyleChange({
                  kind: "arrowStyle",
                  value:
                    data.value === "solid"
                      ? "solid"
                      : data.value === "hollow"
                        ? "hollow"
                        : "open",
                })
              }
            >
              <option value="open">开放</option>
              <option value="solid">实心</option>
              <option value="hollow">空心</option>
            </Select>
          </label>
          <label className="editor-style-control">
            <input
              aria-label="双向箭头"
              checked={activeArrowDouble}
              onChange={(event) =>
                onStyleChange({
                  kind: "arrowDouble",
                  value: event.target.checked,
                })
              }
              type="checkbox"
            />
            <span aria-hidden="true">双向</span>
          </label>
        </>
      )}
      {showFillControl && (
        <>
          <label className="editor-style-control">
            <input
              aria-label="填充"
              checked={activeFillEnabled}
              onChange={(event) =>
                onStyleChange({
                  kind: "fillEnabled",
                  value: event.target.checked,
                })
              }
              type="checkbox"
            />
            <span aria-hidden="true">填充</span>
          </label>
          {activeFillEnabled && (
            <>
              <input
                aria-label="填充颜色"
                className="editor-fill-color"
                onChange={(event) =>
                  onStyleChange({
                    kind: "fillColor",
                    value: event.target.value,
                  })
                }
                title="填充颜色"
                type="color"
                value={normalizeHexColor(activeFillColor)}
              />
              <label className="editor-style-control">
                <span aria-hidden="true">填充不透明度</span>
                <Input
                  aria-label="填充不透明度"
                  inputMode="numeric"
                  max={100}
                  min={0}
                  onBlur={() => {
                    if (opacityDraft?.kind === "fill")
                      commitOpacityInput(opacityDraft.raw, (value) =>
                        onStyleChange(
                          { kind: "fillOpacity", value },
                          opacityDraft.targetId,
                        ),
                      );
                    setOpacityDraft(undefined);
                  }}
                  onChange={(_, data) => {
                    setOpacityDraft((current) =>
                      current?.kind === "fill"
                        ? { ...current, raw: data.value }
                        : {
                            kind: "fill",
                            raw: data.value,
                            targetId: styleTargetId ?? null,
                          },
                    );
                  }}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") event.currentTarget.blur();
                  }}
                  size="small"
                  style={{ width: 72 }}
                  title="0-100 的整数百分比"
                  type="number"
                  value={
                    opacityDraft?.kind === "fill"
                      ? opacityDraft.raw
                      : String(activeFillOpacity)
                  }
                />
                <span aria-hidden="true">%</span>
              </label>
            </>
          )}
        </>
      )}
    </>
  );
}

export function ImageCanvasEditor({
  actions,
  canExport,
  canRecognize,
  recognitionModes,
  source,
  sourceByteLength,
  captureScene,
  session,
  textLayer,
  systemImport,
  projectInstall,
  installProject,
  interactionLocked = false,
  autoText: autoTextProp,
  showAutoTextPreference = true,
  onAutoTextChange,
  onRecognitionSubmitted,
  toolset = "full",
  commandScope = "recognition",
  pinSnapshotApply = false,
  proofread,
  proofreadActive = false,
}: ImageCanvasEditorProps) {
  const commands = EDITOR_COMMANDS[commandScope];
  // #233 工程保存命令：scene/识别面发 recognition.*，普通编辑页发 imageEdit.*。
  const projectSaveCommand =
    commandScope === "imageEdit"
      ? ("imageEdit.saveProject" as const)
      : ("recognition.saveProject" as const);
  // 校对模式：同一图片区切换显示结果行框，编辑器其余 chrome 隐藏但状态保留。
  const proofreading = !!proofread && proofreadActive;
  const captureGeometry = captureScene;
  const contentRevisionRef = useRef(session?.revision ?? 0);
  const importGenerationRef = useRef(0);
  const importMountedRef = useRef(true);
  const presentation = useRef<CapturePixelPresentation | undefined>(undefined);
  const backdropRef = useRef<HTMLImageElement>(null);
  useEffect(() => {
    if (!captureScene) return;
    const attempt = new CapturePixelPresentation((failure) =>
      actions.reportCapturePixels?.(captureScene, failure),
    );
    presentation.current = attempt;
    if (
      source !== captureScene.inputUrl ||
      session?.sessionId !== captureScene.captureSessionId ||
      contentRevisionRef.current !== captureScene.inputRevision
    )
      attempt.fail("identity");
    else if (backdropRef.current?.complete)
      void attempt.backgroundLoaded(backdropRef.current);
    return () => {
      attempt.dispose();
      if (presentation.current === attempt) presentation.current = undefined;
    };
    // The attempt belongs to this bootstrap/input, not later annotation revisions.
  }, [captureScene, source, session?.sessionId, actions]);
  const [sceneViewport, setSceneViewport] = useState(() => ({
    width: window.innerWidth,
    height: window.innerHeight,
  }));
  useEffect(() => {
    if (!captureGeometry) return undefined;
    const resize = () =>
      setSceneViewport({
        width: window.innerWidth,
        height: window.innerHeight,
      });
    window.addEventListener("resize", resize);
    return () => window.removeEventListener("resize", resize);
  }, [captureGeometry]);
  const captureStyle = captureSceneStyle(
    captureGeometry,
    sceneViewport.width,
    sceneViewport.height,
  );
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [displayCanvas, setDisplayCanvas] = useState<HTMLCanvasElement | null>(
    null,
  );
  const attachCanvas = useCallback((canvas: HTMLCanvasElement | null) => {
    canvasRef.current = canvas;
    setDisplayCanvas(canvas);
  }, []);
  const viewportRef = useRef<HTMLDivElement>(null);
  const imageRef = useRef<HTMLImageElement | undefined>(undefined);
  const dragStart = useRef<Point | undefined>(undefined);
  const selectionDragBounds = useRef<DOMRect | undefined>(undefined);
  // 稳定对象 id：单调计数，不随撤销/重置回退，历史内 id 永不复用。
  const nextMarkIdRef = useRef(0);
  const nextGroupIdRef = useRef(0);
  function nextMarkId(): string {
    nextMarkIdRef.current += 1;
    return `mark-${nextMarkIdRef.current}`;
  }
  function nextGroupId(): string {
    nextGroupIdRef.current += 1;
    return `group-${nextGroupIdRef.current}`;
  }
  // 编辑器内对象剪贴板：仅存对象快照，不接系统剪贴板/桥（232 另行导入）。
  const [clipboardSnapshot, setClipboardSnapshot] = useState<{
    source: string;
    sessionId: string;
    marks: readonly Mark[];
  }>();
  const clipboard =
    clipboardSnapshot?.source === source &&
    clipboardSnapshot.sessionId === (session?.sessionId ?? "")
      ? clipboardSnapshot.marks
      : EMPTY.marks;
  const [offscreenIds, setOffscreenIds] = useState<ReadonlySet<string>>(
    new Set(),
  );
  const exportInProgressRef = useRef(false);
  // 选定对象的几何编辑拖拽：origin 冻结拖前几何，objectDraft 驱动实时预览。
  // resize = 矩形类（矩形/椭圆/屏蔽）八点缩放；endpoint = 直线/箭头端点；
  // region-resize = 聚光灯单孔洞区域八点缩放（其余区域不动）。
  const markDragRef = useRef<
    | {
        readonly kind: "resize";
        readonly id: string;
        readonly handle: ExclusionResizeHandle;
        readonly origin: Mark;
        readonly revision: number;
      }
    | {
        readonly kind: "region-resize";
        readonly id: string;
        readonly handle: ExclusionResizeHandle;
        readonly regionIndex: number;
        readonly origin: Mark;
        readonly revision: number;
      }
    | {
        readonly kind: "endpoint";
        readonly id: string;
        readonly endpoint: "start" | "end";
        readonly origin: Mark;
        readonly revision: number;
      }
    | undefined
  >(undefined);
  // 整选区平移拖拽：冻结拖前对象集合与开始修订，提交前校验。
  const moveDragRef = useRef<
    | {
        readonly ids: readonly string[];
        readonly origins: readonly Mark[];
        readonly hitId: string;
        readonly revision: number;
      }
    | undefined
  >(undefined);
  // 空白处框选草稿：松开时完整包含的未锁定对象入选择。
  const marqueeRef = useRef<{ start: Point; end: Point } | undefined>(
    undefined,
  );
  // 选区（裁剪框）四边/四角拖拽：origin 冻结拖前矩形，cropDraft 驱动实时预览。
  const cropResizeRef = useRef<
    | {
        readonly handle: ExclusionResizeHandle;
        readonly origin: { readonly start: Point; readonly end: Point };
      }
    | undefined
  >(undefined);
  const sessionIdRef = useRef(session?.sessionId);
  const [tool, setTool] = useState<Tool>(() => {
    const scene = captureScene;
    const initial = scene?.initialTool;
    // 截图现场没有裁剪工具按钮：初始工具不得落在 crop 上。
    if (scene && initial === "crop") return "select";
    return TOOL_ORDER.find((value) => value === initial) ?? "select";
  });
  const [zoom, setZoom] = useState(1);
  const [spaceHeld, setSpaceHeld] = useState(false);
  const panStart = useRef<
    | {
        x: number;
        y: number;
        left: number;
        top: number;
      }
    | undefined
  >(undefined);
  const [annotationText, setAnnotationText] = useState("文本");
  const [textEdit, setTextEdit] = useState<{ id: string; mark: Mark }>();
  const textInputRef = useRef<HTMLTextAreaElement>(null);
  const textComposing = useRef(false);
  const textEditingRef = useRef(false);
  useLayoutEffect(() => {
    textEditingRef.current = !!textEdit;
  }, [textEdit]);
  const textEditingId = textEdit?.id;
  useEffect(() => {
    if (textEditingId !== undefined) textInputRef.current?.focus();
  }, [textEditingId]);
  // 各工具属性记忆：每个工具独立记录自己消费的属性，切换工具互不覆盖；
  // 未记录时回退默认值；“重置属性”删除当前工具条目回到默认。
  const [toolAttributes, setToolAttributes] = useState<
    Partial<Record<Tool, Partial<ToolAttributes>>>
  >({});
  /** 任意工具的属性记忆（含默认回退）：选中标记按其所属工具恢复，
   * 不受当前选择工具影响。 */
  function attributesOfTool(name: Tool): ToolAttributes {
    return {
      ...DEFAULT_TOOL_ATTRIBUTES,
      // 遮挡默认黑色（不透明遮挡敏感内容），其余工具保持旧默认色。
      ...(name === "solid" && !toolAttributes[name]?.color
        ? { color: DEFAULT_SOLID_COLOR }
        : {}),
      ...toolAttributes[name],
    };
  }
  const activeToolAttributes = attributesOfTool(tool);
  const toolAttributesRef = useRef(toolAttributes);
  useEffect(() => {
    toolAttributesRef.current = toolAttributes;
  });
  // 支持 EyeDropper 时提供屏幕取色；构造失败/取消保持当前颜色。
  const [eyedropperAvailable] = useState(
    () => typeof (window as WindowWithEyeDropper).EyeDropper === "function",
  );
  const [eyedropperBusy, setEyedropperBusy] = useState(false);
  // 多选状态：稳定 id 集合 + 主选（最后点入者，承载手柄/就地属性编辑）。
  interface ObjectSelectionState {
    readonly ids: ReadonlySet<string>;
    readonly primary: string | undefined;
  }
  const NO_SELECTION = useMemo<ObjectSelectionState>(
    () => ({ ids: new Set<string>(), primary: undefined }),
    [],
  );
  const [objectSelection, setObjectSelection] =
    useState<ObjectSelectionState>(NO_SELECTION);
  const selectionRef = useRef(objectSelection);
  useLayoutEffect(() => {
    selectionRef.current = objectSelection;
  }, [objectSelection]);
  const selectedMark: string | undefined = objectSelection.primary;
  const selectedIds: ReadonlySet<string> = objectSelection.ids;
  const [history, setHistory] = useState<readonly EditorState[]>([EMPTY]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [imageRevision, setImageRevision] = useState(0);
  // 源图尺寸按 source 键控：换图后旧尺寸立即失效，不在 effect 内同步重置。
  const [decodedSourceSize, setDecodedSourceSize] = useState<{
    source: string;
    size: CanvasSize;
    image: HTMLImageElement;
  }>();
  const sourceSize =
    decodedSourceSize?.source === source ? decodedSourceSize.size : undefined;
  const [isExporting, setIsExporting] = useState(false);
  // 按需“导出效果”检查：只在用户明确点击时真实编码一次，不自动跟随
  // 编辑重编码，也不渲染常驻第二幅预览；与复制/保存按钮可用性解耦。
  const [isCheckingExport, setIsCheckingExport] = useState(false);
  const pendingDraw = useRef<number | undefined>(undefined);
  const latestDraw = useRef<() => void>(() => {});
  const previewCache = useRef<
    | {
        canvas: HTMLCanvasElement;
        state: EditorState;
        image: HTMLImageElement | undefined;
        crop: EditorState["crop"];
        preview: InpaintDrawLayer["preview"];
        maskOverlay: InpaintDrawLayer["maskOverlay"];
        duplicateOverlay: InpaintDrawLayer["duplicateOverlay"];
        format: OutputImageFormat;
        markCount: number;
      }
    | undefined
  >(undefined);
  const draftMark = useRef<Mark | undefined>(undefined);
  const draftPoints = useRef<Point[]>([]);
  function scheduleDraw() {
    if (pendingDraw.current !== undefined) return;
    pendingDraw.current = requestAnimationFrame(() => {
      pendingDraw.current = undefined;
      try {
        latestDraw.current();
      } catch (error) {
        if (presentation.current) presentation.current.fail("draw");
        else throw error;
      }
    });
  }

  function setDraftMark(
    value: Mark | undefined | ((current: Mark | undefined) => Mark | undefined),
  ) {
    draftMark.current =
      typeof value === "function" ? value(draftMark.current) : value;
    scheduleDraw();
  }
  // 裁剪草稿：拖拽期间即以实时裁剪画面 + 虚线框反映输出有效内容。
  const cropDraft = useRef<{ start: Point; end: Point } | undefined>(undefined);
  function setCropDraft(
    value:
      | { start: Point; end: Point }
      | undefined
      | ((
          current: { start: Point; end: Point } | undefined,
        ) => { start: Point; end: Point } | undefined),
  ) {
    cropDraft.current =
      typeof value === "function" ? value(cropDraft.current) : value;
    scheduleDraw();
  }
  // 去水印（Beta）：拖拽草稿/确定选区均为显示空间坐标；预览结果仅在
  // 明确应用时进入历史，取消/迟到结果不触碰当前图片。
  const inpaintDraft = useRef<{ start: Point; end: Point } | undefined>(
    undefined,
  );
  function setInpaintDraft(
    value:
      | { start: Point; end: Point }
      | undefined
      | ((
          current: { start: Point; end: Point } | undefined,
        ) => { start: Point; end: Point } | undefined),
  ) {
    inpaintDraft.current =
      typeof value === "function" ? value(inpaintDraft.current) : value;
    scheduleDraw();
  }
  const [inpaintSelection, setInpaintSelection] = useState<
    { start: Point; end: Point } | undefined
  >();
  const inpaintSelectionRef = useRef<{ start: Point; end: Point } | undefined>(
    undefined,
  );
  const [inpaintPreview, setInpaintPreview] = useState<InpaintPreviewResult>();
  const [inpaintBusy, setInpaintBusy] = useState(false);
  const [inpaintCompare, setInpaintCompare] = useState<"after" | "before">(
    "after",
  );
  // Worker/补丁存储全部走 ref：像素不进 React 状态，避免多余拷贝。
  const inpaintWorkerRef = useRef<Worker | undefined>(undefined);
  const inpaintGenerationRef = useRef(0);
  const inpaintPatchStore = useRef(new Map<number, HTMLCanvasElement>());
  const inpaintPatchIdRef = useRef(0);
  // 修补二值掩膜（原图空间）：矩形/笔刷增减共同维护；掩膜像素量
  // ≤1MP，仅“预览修补→应用”链路消费，不入历史。
  const [inpaintMask, setInpaintMask] = useState<
    InpaintMaskState | undefined
  >();
  // 同步基线供同一事件中的取消、批量涂抹和预览消费。
  const inpaintMaskRef = useRef<InpaintMaskState | undefined>(undefined);
  const pendingMaskFrame = useRef<number | undefined>(undefined);
  // 掩膜叠加层（绿色提示画布，尺寸 = ROI）：随掩膜变化重建。
  const inpaintMaskOverlay = useMemo(() => {
    if (!inpaintMask) return undefined;
    const canvas = document.createElement("canvas");
    canvas.width = inpaintMask.rect.width;
    canvas.height = inpaintMask.rect.height;
    const context = canvas.getContext("2d");
    if (!context) return undefined;
    // 环境（如测试 stub）可能不提供 ImageData：叠加层只是预览提示，
    // 拿不到像素缓冲就跳过，不影响掩膜/修补未提交语义。
    const image = context.createImageData(canvas.width, canvas.height);
    if (!image || !image.data) return undefined;
    for (let i = 0; i < inpaintMask.data.length; i += 1) {
      if ((inpaintMask.data[i] ?? 0) === 0) continue;
      image.data[i * 4] = 18;
      image.data[i * 4 + 1] = 161;
      image.data[i * 4 + 2] = 80;
      image.data[i * 4 + 3] = 96;
    }
    context.putImageData(image, 0, 0);
    return { rect: inpaintMask.rect, canvas };
  }, [inpaintMask]);
  // 掩膜笔刷拖拽中的采样点（显示空间→操作时映射原图）：不入历史。
  const inpaintBrushPoints = useRef<Point[]>([]);
  // 重复内容匹配（AC3）：候选仅存在于组件状态，确认前不修改内容。
  const dupmatchDraft = useRef<{ start: Point; end: Point } | undefined>(
    undefined,
  );
  // #235 对象橡皮擦：一次拖动累积命中的未锁定对象 id；松开时整次手势
  // 仅一次历史提交，拖拽中仅预览（不动历史、不动底图/修补）。last 记录
  // 上一次采样点，供扫过命中按线段插值。
  const objectEraserHits = useRef<Set<string> | undefined>(undefined);
  const objectEraserLast = useRef<Point | undefined>(undefined);
  // #235 聚光灯“添加区域”：目标对象 id；激活后下一次拖拽把新孔洞区域
  // 提交进该对象（一次历史），Esc/切换工具/取消作废。
  const [spotlightRegionTarget, setSpotlightRegionTarget] = useState<
    string | undefined
  >();
  const [spotlightRegionDraft, setSpotlightRegionDraft] = useState<
    { start: Point; end: Point } | undefined
  >();
  function updateSpotlightRegionDraft(
    value:
      | { start: Point; end: Point }
      | undefined
      | ((
          current: { start: Point; end: Point } | undefined,
        ) => { start: Point; end: Point } | undefined),
  ) {
    setSpotlightRegionDraft((current) =>
      typeof value === "function" ? value(current) : value,
    );
    scheduleDraw();
  }
  const [dupmatchSelection, setDupmatchSelection] = useState<
    { start: Point; end: Point } | undefined
  >();
  // 与掩膜相同：异步导入消费同步选区，不能读解码开始时的闭包。
  const dupmatchSelectionRef = useRef<typeof dupmatchSelection>(undefined);
  const [dupmatchBusy, setDupmatchBusy] = useState(false);
  const [dupmatchCandidates, setDupmatchCandidates] = useState<
    readonly DuplicateCandidate[] | undefined
  >();
  const [dupmatchAccepted, setDupmatchAccepted] = useState<
    readonly boolean[] | undefined
  >(undefined);
  const dupmatchWorkerRef = useRef<Worker | undefined>(undefined);
  const dupmatchGenerationRef = useRef(0);
  const dupmatchFrozenRef = useRef<
    | {
        readonly key: string;
        readonly sessionId: string | undefined;
        readonly contentVersion: number;
        readonly image: HTMLImageElement;
        readonly selection?: { readonly start: Point; readonly end: Point };
        readonly generation?: number;
      }
    | undefined
  >(undefined);
  // 对象拖拽预览覆盖：按 id 覆盖拖动中的对象（缩放/端点/整选区平移共用），
  // 记录开始修订，提交前校验防止串入过期状态。
  const objectDraftRef = useRef<
    | { readonly byId: ReadonlyMap<string, Mark>; readonly revision: number }
    | undefined
  >(undefined);
  function setObjectDraft(
    value:
      | { readonly byId: ReadonlyMap<string, Mark>; readonly revision: number }
      | undefined,
  ) {
    objectDraftRef.current = value;
    scheduleDraw();
  }
  // 本地修订：编辑提交即时推进，不等宿主回显；文字层绑定据此立即失效。
  const [localRevision, setLocalRevision] = useState(session?.revision ?? 0);
  const [localAutoText, setLocalAutoText] = useState(false);
  // 输出设置属于会话内容的一部分：改动推进修订、使旧文字层/识别结果失效，
  // 但不入编辑历史（撤销只回退几何编辑，含尺寸变更）。
  const [outputFormat, setOutputFormat] =
    useState<OutputImageFormat>("image/png");
  const [jpegQuality, setJpegQuality] = useState<number>(0.9);
  // 权威最新值：导出 await 归来后的比较读 ref，不依赖旧闭包。
  const outputFormatRef = useRef<OutputImageFormat>(outputFormat);
  const jpegQualityRef = useRef<number>(jpegQuality);
  const [sizeDraft, setSizeDraft] = useState<{
    width: string;
    height: string;
  }>({ width: "", height: "" });
  const autoText = autoTextProp ?? localAutoText;
  const [prepareNonce, setPrepareNonce] = useState(0);
  const [operationMessage, setOperationMessage] = useState(
    captureStyle
      ? ""
      : session
        ? "纯截图会话：标注后可复制、保存或显式识别当前图；不会自动提交 OCR。"
        : "标注只影响复制或保存的图片副本，不会重新识别。",
  );
  const state = history[historyIndex] ?? EMPTY;
  const hasExclusions = state.marks.some(isExclusionMark);

  // 取字模式：仅当宿主层就绪且绑定与当前本地修订一致时展示；
  // 显示的图像就是识别输入的同一最终 PNG，不拿旧层映射。
  const layerImageRef = textLayer?.image;
  const layerImageUrl = layerImageRef?.url;
  const layerReady =
    tool === "textSelect" &&
    textLayer?.status === "textlayer.ready" &&
    !!textLayer.binding &&
    textLayer.binding.sessionId === session?.sessionId &&
    textLayer.binding.revision === localRevision &&
    !!layerImageUrl;

  // 换图（新截图、新输入文件或迟到源替换）或会话切换时重置编辑历史：
  // 旧图标注/裁剪不得导出到新图。按 React 推荐在渲染期随 props 调整状态，
  // 而不是在 effect 中级联 setState。
  const sessionKey = session?.sessionId ?? "";
  const resetKey = `${source}\n${sessionKey}`;
  const [appliedResetKey, setAppliedResetKey] = useState(resetKey);
  if (appliedResetKey !== resetKey) {
    setAppliedResetKey(resetKey);
    setHistory([EMPTY]);
    setHistoryIndex(0);
    setTextEdit(undefined);
    setObjectSelection(NO_SELECTION);
    setLocalRevision(session?.revision ?? 0);
    // 换图/换会话：作废在途修补请求与迟到响应，丢弃全部补丁像素。
    setInpaintSelection(undefined);
    setInpaintPreview(undefined);
    setInpaintBusy(false);
    inpaintMaskRef.current = undefined;
    setInpaintMask(undefined);
    inpaintBrushPoints.current = [];
    if (pendingMaskFrame.current !== undefined)
      cancelAnimationFrame(pendingMaskFrame.current);
    pendingMaskFrame.current = undefined;
    // 重复匹配状态同样绑定源与会话：一并作废。
    dupmatchGenerationRef.current += 1;
    dupmatchWorkerRef.current?.terminate();
    dupmatchWorkerRef.current = undefined;
    dupmatchFrozenRef.current = undefined;
    dupmatchDraft.current = undefined;
    dupmatchSelectionRef.current = undefined;
    setDupmatchSelection(undefined);
    setDupmatchCandidates(undefined);
    setDupmatchAccepted(undefined);
    setDupmatchBusy(false);
    // 本 PR 新增手势草稿同样绑定源与会话：加区 target/拖拽草稿与橡皮擦
    // 手势随重置作废，不吞新画布首个手势、不产生空历史提交（J2）。
    setSpotlightRegionTarget(undefined);
    setSpotlightRegionDraft(undefined);
    objectEraserHits.current = undefined;
    objectEraserLast.current = undefined;
  }
  const resetResourcesKey = useRef<string | undefined>(undefined);
  useLayoutEffect(() => {
    if (resetResourcesKey.current === resetKey) return;
    resetResourcesKey.current = resetKey;
    retireImportsRef.current();
    draftMark.current = undefined;
    cropDraft.current = undefined;
    dragStart.current = undefined;
    textComposing.current = false;
    markDragRef.current = undefined;
    cropResizeRef.current = undefined;
    objectDraftRef.current = undefined;
    moveDragRef.current = undefined;
    marqueeRef.current = undefined;
    objectEraserHits.current = undefined;
    objectEraserLast.current = undefined;
    sessionIdRef.current = session?.sessionId;
    contentRevisionRef.current = session?.revision ?? 0;
    inpaintGenerationRef.current += 1;
    inpaintWorkerRef.current?.terminate();
    inpaintWorkerRef.current = undefined;
    inpaintPatchStore.current.clear();
    inpaintDraft.current = undefined;
    // 换图/换会话：恢复逻辑 frame 一并回到默认（下次安装重新采纳）。
    setProjectFrame(undefined);
    // 换图/换会话：丢弃导入资产强引用与消费记录；宿主在会话替换时整体回收。
    importAssetsRef.current.clear();
    importAssetUrlsRef.current.clear();
    consumedImportRequestsRef.current = new Set();
    importGenerationRef.current += 1;
    importInFlightRef.current = false;
  }, [resetKey, session?.revision, session?.sessionId]);

  // 冻结基准初始屏蔽：宿主随新基准回发归一化排除框时，映射回当前
  // 显示空间重建初始 exclude 标记（可编辑/可清除）；仅在基准变化时执行
  // 一次，不推进修订、不触发 OCR，后续修改沿用既有 revision 机制。
  const seededBaselineRef = useRef<string>("");
  useLayoutEffect(() => {
    const seedKey = session ? `${session.sessionId}:${source}` : "";
    if (!seedKey || seededBaselineRef.current === seedKey) return;
    const boxes = session?.excludeBoxes;
    if (!boxes?.length) {
      seededBaselineRef.current = seedKey;
      return;
    }
    const image = imageRef.current;
    const canvas = canvasRef.current;
    // 图像尚未解码或解码结果不属于当前 source 时不播种也不锁 seedKey：
    // 换图瞬间 imageRef 可能仍是旧图，必须以 decodedSourceSize 键控为准。
    if (
      !image ||
      !canvas ||
      !image.naturalWidth ||
      !image.naturalHeight ||
      decodedSourceSize?.source !== source
    ) {
      return;
    }
    if (history.length !== 1 || historyIndex !== 0 || state !== EMPTY) {
      seededBaselineRef.current = seedKey;
      return;
    }
    const fresh: EditorState = { rotation: 0, marks: [] };
    const marks = boxes.map((box) => ({
      id: nextMarkId(),
      tool: "exclude" as const,
      ...exclusionDisplayPoints(box, image, fresh, canvasSize(canvas)),
    }));
    setHistory([
      {
        rotation: 0,
        marks,
      },
    ]);
    seededBaselineRef.current = seedKey;
  }, [
    session,
    source,
    imageRevision,
    decodedSourceSize,
    history,
    historyIndex,
    state,
  ]);

  function initialExclusionsReady(): boolean {
    return (
      !session?.excludeBoxes?.length ||
      seededBaselineRef.current === `${session.sessionId}:${source}`
    );
  }

  // 画布取色（EyeDropper 不可用时的 fallback）：true 时下一次画布点击只取样颜色。
  const colorPickPendingRef = useRef(false);

  // 同会话内宿主回显修订时单调对齐本地计数，避免回退。
  useEffect(() => {
    if (
      session &&
      sessionIdRef.current === session.sessionId &&
      typeof session.revision === "number"
    ) {
      contentRevisionRef.current = Math.max(
        contentRevisionRef.current,
        session.revision,
      );
    }
  }, [session]);

  // 导出后校验用的最新 key（source+session）；await 之后闭包已过期，
  // 只能从 ref 读取当前值。
  const currentKeyRef = useRef<string>(resetKey);
  useEffect(() => {
    currentKeyRef.current = resetKey;
  });

  useEffect(() => {
    // 恢复安装已提供 decode 强引用的 source：不重 fetch 该 URL。
    if (
      projectInstall?.sourceUrl === source ||
      preparedSourceUrlsRef.current.has(source)
    )
      return;
    // 换图后立即失效旧解码结果：新图 decode 完成前不得导出旧 image。
    imageRef.current = undefined;
    const image = new Image();
    image.decoding = "async";
    let active = true;
    image.onload = async () => {
      try {
        if (captureScene && typeof image.decode === "function")
          await image.decode();
      } catch {
        if (active) presentation.current?.fail("sourceDecode");
        return;
      }
      if (!active) return;
      if (
        captureScene &&
        (image.naturalWidth !== captureScene.width ||
          image.naturalHeight !== captureScene.height)
      ) {
        presentation.current?.fail("identity");
        return;
      }
      imageRef.current = image;
      setDecodedSourceSize({
        image,
        source,
        size: { width: image.naturalWidth, height: image.naturalHeight },
      });
      setImageRevision((current) => current + 1);
    };
    image.onerror = () => {
      if (active) presentation.current?.fail("sourceDecode");
    };
    image.src = source;
    return () => {
      active = false;
      image.onload = null;
      image.onerror = null;
      if (imageRef.current === image) imageRef.current = undefined;
    };
  }, [source, captureScene, projectInstall?.sourceUrl]);

  // 去水印绘制层：应用补丁从存储解析；预览仅在对比为“修补后”时叠加；
  // 选区高亮按映射后的有效范围回投显示，用户看到的就是实际修改范围。
  const imageForLayer =
    decodedSourceSize?.source === source ? decodedSourceSize.image : undefined;
  function appliedInpaintPatches(): readonly {
    rect: PixelPatch;
    canvas: HTMLCanvasElement;
  }[] {
    return (state.patches ?? []).flatMap((rect) => {
      const canvas = inpaintPatchStore.current.get(rect.id);
      return canvas ? [{ rect, canvas }] : [];
    });
  }

  function currentInpaintLayer(): InpaintDrawLayer {
    const preview =
      inpaintPreview && inpaintCompare === "after" ? inpaintPreview : undefined;
    let selection: { start: Point; end: Point } | undefined;
    const drag = inpaintDraft.current ?? inpaintSelection;
    if (
      tool === "inpaint" &&
      drag &&
      imageForLayer &&
      displayCanvas &&
      imageForLayer.naturalWidth > 0 &&
      imageForLayer.naturalHeight > 0
    ) {
      const displaySize = canvasSize(displayCanvas);
      const rect = selectionToNaturalRect(
        drag.start,
        drag.end,
        imageForLayer,
        state.rotation,
        displaySize,
      );
      if (rect.width > 0 && rect.height > 0) {
        selection = naturalRectToDisplay(
          rect,
          imageForLayer,
          state.rotation,
          displaySize,
        );
      }
    }
    // 重复匹配叠加层：拖拽/源选区 + 候选框（原图空间，随旋转变换）。
    let duplicateOverlay: InpaintDrawLayer["duplicateOverlay"];
    if (
      tool === "dupmatch" &&
      imageForLayer &&
      displayCanvas &&
      imageForLayer.naturalWidth > 0 &&
      imageForLayer.naturalHeight > 0
    ) {
      const displaySize = canvasSize(displayCanvas);
      const entries: {
        rect: InpaintRect;
        accepted: boolean;
        label: string;
      }[] = [];
      const pending = dupmatchDraft.current ?? dupmatchSelection;
      if (pending) {
        const rect = selectionToNaturalRect(
          pending.start,
          pending.end,
          imageForLayer,
          state.rotation,
          displaySize,
        );
        if (rect.width > 0 && rect.height > 0) {
          entries.push({
            rect,
            accepted: true,
            label: dupmatchCandidates ? "源" : "",
          });
        }
      }
      dupmatchCandidates?.forEach((candidate, index) => {
        entries.push({
          rect: candidate,
          accepted: dupmatchAccepted?.[index] ?? true,
          label: `候选${index + 1}`,
        });
      });
      duplicateOverlay = entries.length > 0 ? entries : undefined;
    }
    return {
      patches: appliedInpaintPatches(),
      preview: preview
        ? { rect: preview.naturalRect, canvas: preview.canvas }
        : undefined,
      selection,
      maskOverlay: inpaintPreview ? undefined : inpaintMaskOverlay,
      duplicateOverlay,
    };
  }

  useLayoutEffect(
    () => () => {
      if (pendingDraw.current !== undefined)
        cancelAnimationFrame(pendingDraw.current);
      pendingDraw.current = undefined;
      if (pendingMaskFrame.current !== undefined)
        cancelAnimationFrame(pendingMaskFrame.current);
      pendingMaskFrame.current = undefined;
      inpaintBrushPoints.current = [];
      previewCache.current = undefined;
      delete canvasRef.current?.dataset.frameReady;
    },
    [resetKey],
  );
  useLayoutEffect(() => {
    // 保留已排队的帧，只替换绘制内容，避免连续 pointermove 饿死绘制。
    latestDraw.current = () => {
      try {
        validateBrushBudget(
          imageRef.current,
          canvasRef.current,
          draftMark.current
            ? { ...state, marks: [...state.marks, draftMark.current] }
            : state,
        );

        const canvas = canvasRef.current;
        if (!canvas || layerReady) return;
        if (captureScene && !canvas.getContext("2d")) {
          presentation.current?.fail("draw");
          return;
        }
        const image = imageRef.current;
        const offscreen = new Set(
          state.marks
            .filter((mark) =>
              markOffscreen(
                mark,
                canvasSize(canvas),
                canvas.getContext("2d") ?? undefined,
              ),
            )
            .map((mark) => mark.id),
        );
        setOffscreenIds((current) =>
          current.size === offscreen.size &&
          [...current].every((id) => offscreen.has(id))
            ? current
            : offscreen,
        );
        const layer = currentInpaintLayer();
        const crop = cropDraft.current ? undefined : state.crop;
        const renderedState = crop === state.crop ? state : { ...state, crop };
        // Only the unchanged prefix is cached: moving a mark must also recompute
        // all later mosaic/blur marks that sample its pixels. Never cache exports.
        const draftIds = objectDraftRef.current?.byId.keys() ?? [];
        // 擦除草稿会改变数组索引，不能复用包含待删除对象的缓存前缀。
        let markCount = objectEraserHits.current?.size ? 0 : state.marks.length;
        for (const id of [...selectedIds, ...draftIds]) {
          const index = state.marks.findIndex((mark) => mark.id === id);
          if (index >= 0 && index < markCount) markCount = index;
        }
        let cached = previewCache.current;
        if (canvas.width * canvas.height <= 16_000_000) {
          if (
            !cached ||
            cached.state !== state ||
            cached.image !== image ||
            cached.crop !== crop ||
            cached.preview?.canvas !== layer.preview?.canvas ||
            cached.maskOverlay?.canvas !== layer.maskOverlay?.canvas ||
            cached.duplicateOverlay !== layer.duplicateOverlay ||
            cached.format !== outputFormat ||
            cached.markCount !== markCount ||
            cached.canvas.width !== canvas.width ||
            cached.canvas.height !== canvas.height
          ) {
            const base = cached?.canvas ?? document.createElement("canvas");
            base.width = canvas.width;
            base.height = canvas.height;
            Object.assign(base.dataset, canvas.dataset);
            draw(
              base,
              image,
              renderedState,
              undefined,
              state.marks.slice(0, markCount),
              true,
              1,
              outputFormat === "image/jpeg" ? "#ffffff" : undefined,
              false,
              {
                patches: layer.patches,
                preview: layer.preview,
                maskOverlay: layer.maskOverlay,
                duplicateOverlay: layer.duplicateOverlay,
              },
              undefined,
              false,
              importAssetsRef.current,
            );
            cached = {
              canvas: base,
              state,
              image,
              crop,
              preview: layer.preview,
              maskOverlay: layer.maskOverlay,
              duplicateOverlay: layer.duplicateOverlay,
              format: outputFormat,
              markCount,
            };
            previewCache.current = cached;
          }
        } else {
          cached = undefined;
          previewCache.current = undefined;
        }
        draw(
          canvasRef.current,
          imageRef.current,
          // 拖动裁剪时保留完整画面，只更新遮罩；松开后提交实际裁剪。
          renderedState,
          selectedIds,
          (() => {
            // 对象橡皮擦拖拽中：命中对象仅从预览移除（不入历史）；其余
            // 覆盖链（对象拖拽/文字编辑/草稿）沿用既有顺序。
            const base = objectEraserHits.current?.size
              ? state.marks.filter(
                  (mark) => !objectEraserHits.current!.has(mark.id),
                )
              : state.marks;
            if (objectDraftRef.current)
              return base.map(
                (mark) => objectDraftRef.current!.byId.get(mark.id) ?? mark,
              );
            if (textEdit)
              return base.map((mark) =>
                mark.id === textEdit.id ? textEdit.mark : mark,
              );
            if (draftMark.current) return [...base, draftMark.current];
            return base;
          })(),
          true,
          1,
          // 预览与导出一致：JPEG 显式白底合成；PNG 不填底色，透明由
          // 工作区 CSS 背景衬托，不把深色烧进图像内容。
          outputFormat === "image/jpeg" ? "#ffffff" : undefined,
          false,
          layer,
          cached,
          true,
          importAssetsRef.current,
        );
        if (cropDraft.current) {
          drawCropPreview(canvas, cropDraft.current);
        } else if (captureGeometry && !layerReady) {
          // 截图现场：选区即当前裁剪范围；无已提交裁剪时在四边/四角绘制闲置手柄。
          drawSceneCropHandles(canvas, state.crop);
        }
        if (marqueeRef.current) {
          // 空白框选草稿：蓝色虚线实时反馈包含范围。
          const marqueeContext = canvas.getContext("2d");
          const area = clampRect(
            normalizedRect(marqueeRef.current),
            canvasSize(canvas),
          );
          if (marqueeContext && (area.width > 0 || area.height > 0)) {
            marqueeContext.save();
            marqueeContext.setLineDash([6, 4]);
            marqueeContext.lineWidth = 1.5;
            marqueeContext.strokeStyle = "#1f6feb";
            marqueeContext.strokeRect(area.x, area.y, area.width, area.height);
            marqueeContext.restore();
          }
        }
        if (spotlightRegionDraft) {
          // 聚光灯“添加区域”拖拽草稿：橙色虚线预览新孔洞。
          const draftContext = canvas.getContext("2d");
          const area = clampRect(
            normalizedRect(spotlightRegionDraft),
            canvasSize(canvas),
          );
          if (draftContext && (area.width > 0 || area.height > 0)) {
            draftContext.save();
            draftContext.setLineDash([6, 4]);
            draftContext.lineWidth = 1.5;
            draftContext.strokeStyle = "#f38b35";
            draftContext.strokeRect(area.x, area.y, area.width, area.height);
            draftContext.restore();
          }
        }
        if (
          image &&
          image.naturalWidth > 0 &&
          image.naturalHeight > 0 &&
          canvas.getContext("2d")
        ) {
          canvas.dataset.frameReady = "true";
          presentation.current?.sourceDrawn();
        }
      } catch (error) {
        previewCache.current = undefined;
        if (presentation.current) presentation.current.fail("draw");
        else
          setOperationMessage(
            error instanceof Error
              ? error.message
              : "图片预览失败，请减小笔刷范围后重试。",
          );
      }
    };
    if (!layerReady) scheduleDraw();
  });

  useLayoutEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || captureGeometry || typeof ResizeObserver === "undefined")
      return;
    const resize = () => {
      const bounds = canvas.getBoundingClientRect();
      if (bounds.width <= 0 || bounds.height <= 0) return;
      const pixels = window.devicePixelRatio || 1;
      // Bound preview memory to 16 MP; full-resolution export retains its own limit.
      const scale = Math.min(
        pixels,
        Math.sqrt(16_000_000 / (bounds.width * bounds.height)),
      );
      const width = Math.max(1, Math.round(bounds.width * scale));
      const height = Math.max(1, Math.round(bounds.height * scale));
      if (canvas.width === width && canvas.height === height) return;
      canvas.width = width;
      canvas.height = height;
      scheduleDraw();
    };
    const observer = new ResizeObserver(resize);
    observer.observe(canvas);
    window.addEventListener("resize", resize);
    resize();
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", resize);
    };
  }, [resetKey, captureGeometry]);

  // 选区镜像：Worker 响应到达时用 ref 比对最新选区，不依赖旧闭包。
  useEffect(() => {
    inpaintSelectionRef.current = inpaintSelection;
  }, [inpaintSelection]);

  // 补丁像素仅保留仍被历史（含撤销/重做分支）引用的条目。
  useEffect(() => {
    const alive = new Set<number>();
    for (const entry of history) {
      for (const patch of entry.patches ?? []) alive.add(patch.id);
    }
    for (const id of [...inpaintPatchStore.current.keys()]) {
      if (!alive.has(id)) inpaintPatchStore.current.delete(id);
    }
  }, [history]);

  // #232 导入图片资产 store：按 assetId 强引用已解码位图，不进历史/桥；
  // 撤销/重做/导出始终用内存位图，不依赖资源 TTL 后重新 GET。
  const importAssetsRef = useRef(new Map<string, HTMLImageElement>());
  const importAssetUrlsRef = useRef(new Map<string, string>());
  const importInFlightRef = useRef(false);
  const consumedImportRequestsRef = useRef<ReadonlySet<string>>(new Set());
  const pendingImportRef = useRef<EditorImportDescriptor | undefined>(
    undefined,
  );
  const retireImportsRef = useRef(retireImports);
  // latest-ref：commit/cancelDrawing 是渲染期函数，直接列入导入 effect 依赖
  // 会每帧重跑；经 ref 取最新渲染实现，async 回调始终拿到当前 historyIndex，
  // 且导入前有修订不变校验，不会用旧闭包覆盖新内容。
  const commitRef = useRef(commit);
  const cancelDrawingRef = useRef(cancelDrawing);
  useLayoutEffect(() => {
    commitRef.current = commit;
    cancelDrawingRef.current = cancelDrawing;
    retireImportsRef.current = retireImports;
  });

  function retireImports(): void {
    const pending = pendingImportRef.current;
    pendingImportRef.current = undefined;
    if (pending)
      void actions.run({
        type: commands.rejectImport,
        sessionId: pending.sessionId,
        revision: pending.revision,
        requestId: pending.requestId,
      });
    const resourceUris = [...importAssetUrlsRef.current.values()];
    importAssetUrlsRef.current.clear();
    importAssetsRef.current.clear();
    if (!sessionIdRef.current) return;
    for (let offset = 0; offset < resourceUris.length; offset += 16)
      void actions.run({
        type: commands.releaseAssets,
        sessionId: sessionIdRef.current,
        resourceUris: resourceUris.slice(offset, offset + 16),
      });
  }
  // 资产 GC：保留历史（含重做）与编辑器剪贴板引用；导入批次仅在 commit 时登记。释放
  // 经宿主 releaseAssets 精确回收 owned 资源（幂等，分批 ≤16）。
  useEffect(() => {
    if (!session) return;
    const alive = new Set<string>();
    for (const marks of [...history.map((entry) => entry.marks), clipboard]) {
      for (const mark of marks) {
        if (mark.tool === "image" && mark.assetId) alive.add(mark.assetId);
      }
    }
    const released: string[] = [];
    for (const assetId of [...importAssetsRef.current.keys()]) {
      if (alive.has(assetId)) continue;
      importAssetsRef.current.delete(assetId);
      const url = importAssetUrlsRef.current.get(assetId);
      importAssetUrlsRef.current.delete(assetId);
      if (url) released.push(url);
    }
    for (let index = 0; index < released.length; index += 16) {
      void actions.run({
        type: commands.releaseAssets,
        sessionId: session.sessionId,
        resourceUris: released.slice(index, index + 16),
      });
    }
  }, [history, clipboard, session, actions, commands.releaseAssets]);

  // 宿主导入批次：会话/修订绑定校验 → 全部真实解码 → 一次 commit 追加
  // 对象并选中；迟到/取消/解码失败/存在未完成草稿时拒收，旧画布不变。
  useEffect(() => {
    const descriptor = systemImport;
    if (!descriptor || descriptor.items.length === 0) return;
    // 重放幂等：同一请求只消费一次（撤销后也不重复追加）。
    if (consumedImportRequestsRef.current.has(descriptor.requestId)) return;
    consumedImportRequestsRef.current = new Set([
      ...consumedImportRequestsRef.current,
      descriptor.requestId,
    ]);
    const acknowledge = () =>
      actions.run({
        type: commands.ackImport,
        sessionId: descriptor.sessionId,
        revision: descriptor.revision,
        requestId: descriptor.requestId,
      });
    const rejectImport = () =>
      void actions.run({
        type: commands.rejectImport,
        sessionId: descriptor.sessionId,
        revision: descriptor.revision,
        requestId: descriptor.requestId,
      });
    if (
      !session ||
      session.sessionId !== descriptor.sessionId ||
      contentRevisionRef.current !== descriptor.revision ||
      importInFlightRef.current
    ) {
      rejectImport();
      return;
    }
    importInFlightRef.current = true;
    pendingImportRef.current = descriptor;
    const generation = importGenerationRef.current;
    void (async () => {
      try {
        const decodedImages: { assetId: string; image: HTMLImageElement }[] =
          [];
        for (const item of descriptor.items) {
          if (item.kind !== "image") continue;
          const image = await new Promise<HTMLImageElement>((resolve, fail) => {
            const element = new Image();
            element.decoding = "async";
            element.onload = () => resolve(element);
            element.onerror = () =>
              fail(new Error("import image decode failed"));
            element.src = item.resourceUri;
          });
          decodedImages.push({ assetId: item.assetId, image });
        }
        const decodedTexts: { assetId: string; text: string }[] = [];
        for (const item of descriptor.items) {
          if (item.kind !== "text") continue;
          const response = await fetch(item.resourceUri, {
            cache: "no-store",
            credentials: "omit",
          });
          if (!response.ok) throw new Error("import text fetch failed");
          decodedTexts.push({
            assetId: item.assetId,
            text: await response.text(),
          });
        }
        // 全部 decode 成功后一次 commit；期间任何修订/草稿变化都拒收，
        // 不自动丢弃用户未完成的文字/折线/修补草稿。
        if (
          !importMountedRef.current ||
          generation !== importGenerationRef.current ||
          !session ||
          session.sessionId !== descriptor.sessionId ||
          contentRevisionRef.current !== descriptor.revision ||
          textEditingRef.current ||
          draftMark.current ||
          cropDraft.current ||
          inpaintDraft.current ||
          inpaintMaskRef.current ||
          inpaintBrushPoints.current.length > 0 ||
          dupmatchDraft.current ||
          dupmatchSelectionRef.current ||
          dupmatchFrozenRef.current ||
          moveDragRef.current ||
          markDragRef.current ||
          objectDraftRef.current ||
          cropResizeRef.current ||
          marqueeRef.current
        ) {
          rejectImport();
          if (
            importMountedRef.current &&
            generation === importGenerationRef.current
          )
            setOperationMessage(
              "导入期间画布已变化或存在未完成草稿；本批未导入，画布保持原状。",
            );
          return;
        }
        const canvas = canvasRef.current;
        const size = canvas ? canvasSize(canvas) : { width: 900, height: 600 };
        const imported: Mark[] = [];
        let offset = 0;
        for (const decoded of decodedImages) {
          const naturalWidth = decoded.image.naturalWidth || 1;
          const naturalHeight = decoded.image.naturalHeight || 1;
          const fit = Math.min(
            1,
            (Math.min(size.width, size.height) * 0.5) /
              Math.max(naturalWidth, naturalHeight),
          );
          const width = Math.max(1, naturalWidth * fit);
          const height = Math.max(1, naturalHeight * fit);
          const centerX = size.width / 2 + offset;
          const centerY = size.height / 2 + offset;
          imported.push({
            id: nextMarkId(),
            tool: "image",
            assetId: decoded.assetId,
            start: {
              x: centerX - width / 2,
              y: centerY - height / 2,
            },
            end: {
              x: centerX + width / 2,
              y: centerY + height / 2,
            },
          });
          offset += 24;
        }
        for (const entry of decodedTexts) {
          imported.push({
            id: nextMarkId(),
            tool: "text",
            text: entry.text,
            // 0 = 自然字形宽（#230 契约）：多行按 lineHeight 展开，端点仅作锚点。
            textWidth: 0,
            start: {
              x: size.width / 2 + offset,
              y: size.height / 2 + offset,
            },
            end: {
              x: size.width / 2 + offset,
              y: size.height / 2 + offset,
            },
          });
          offset += 24;
        }
        // 资产强引用先登记：commit 后历史立即引用，GC 不会误回收。
        for (const decoded of decodedImages) {
          importAssetsRef.current.set(decoded.assetId, decoded.image);
          const descriptor2 = descriptor.items.find(
            (item) => item.assetId === decoded.assetId,
          );
          if (descriptor2)
            importAssetUrlsRef.current.set(
              decoded.assetId,
              descriptor2.resourceUri,
            );
        }
        if (
          !commitRef.current({
            ...state,
            marks: [...state.marks, ...imported],
          })
        ) {
          // 未写入历史的资产仍归暂存批次：撤销本地登记，由 rejectImport
          // 精确回收宿主资源；不能 ack 或用成功反馈覆盖预算错误。
          for (const decoded of decodedImages) {
            importAssetsRef.current.delete(decoded.assetId);
            importAssetUrlsRef.current.delete(decoded.assetId);
          }
          rejectImport();
          return;
        }
        cancelDrawingRef.current();
        setObjectSelection({
          ids: new Set(imported.map((mark) => mark.id)),
          primary: imported[imported.length - 1]?.id,
        });
        if (await acknowledge()) {
          // 文字已复制进对象与历史，不再需要宿主资源；图片由历史/剪贴板 GC 回收。
          const resourceUris = descriptor.items
            .filter(
              (item) =>
                item.kind === "text" ||
                !importMountedRef.current ||
                generation !== importGenerationRef.current,
            )
            .map((item) => item.resourceUri);
          if (resourceUris.length)
            await actions.run({
              type: commands.releaseAssets,
              sessionId: descriptor.sessionId,
              resourceUris,
            });
        }
        if (
          importMountedRef.current &&
          generation === importGenerationRef.current
        )
          setOperationMessage(
            `已导入 ${imported.length} 个对象；可撤销，其余画布内容不变。`,
          );
      } catch {
        rejectImport();
        if (
          importMountedRef.current &&
          generation === importGenerationRef.current
        )
          setOperationMessage("导入内容解码失败；本批未导入，画布保持原状。");
      } finally {
        if (pendingImportRef.current?.requestId === descriptor.requestId)
          pendingImportRef.current = undefined;
        if (generation === importGenerationRef.current)
          importInFlightRef.current = false;
      }
    })();
  }, [systemImport, session, actions, commands, state]);

  // #233 工程恢复安装：宿主 ack 切换（新会话到位）后原子安装已 decode 的
  // marks/补丁/资产/source。合同“恢复使用保存的逻辑 frame，不做重投影”：
  // 采纳 saved canvas 为逻辑 frame（仅 CSS 适配 viewport）；全部可失败
  // 校验已在 ack 前由页面 validateProjectRestore 完成，此处无失败路径；
  // 恢复为初始历史基线（不追加到空白 history），token 消费后不再重 fetch
  // 任何 URL（source 安装强引用，不触发 URL 重载）。
  const installedProjectTokensRef = useRef<ReadonlySet<string>>(new Set());
  const preparedSourceUrlsRef = useRef<ReadonlySet<string>>(new Set());
  // 恢复工程的逻辑画布：undefined = 默认 900×600；install 采纳 saved canvas。
  const [projectFrame, setProjectFrame] = useState<CanvasSize | undefined>();
  useEffect(() => {
    const install = projectInstall;
    if (!install || installedProjectTokensRef.current.has(install.token)) {
      return;
    }
    const image = install.sourceImage;
    if (!image.naturalWidth || !image.naturalHeight) return;
    const apply = () => {
      installedProjectTokensRef.current = new Set([
        ...installedProjectTokensRef.current,
        install.token,
      ]);
      preparedSourceUrlsRef.current = new Set([
        ...preparedSourceUrlsRef.current,
        install.sourceUrl,
      ]);
      const restored = restoreEditorState(install.state, {
        naturalSize: {
          width: image.naturalWidth,
          height: image.naturalHeight,
        },
        savedCanvas: install.savedCanvas,
        displayCanvas: install.savedCanvas,
        rotation: install.state.rotation,
      });
      // 强引用先登记，再替换历史基线。
      for (const [id, patchCanvas] of install.patches) {
        inpaintPatchStore.current.set(id, patchCanvas);
      }
      for (const [assetId, entry] of install.assets) {
        importAssetsRef.current.set(assetId, entry.image);
        importAssetUrlsRef.current.set(assetId, entry.url);
      }
      advanceProjectIdCounters(restored);
      imageRef.current = image;
      setDecodedSourceSize({
        image,
        source: install.sourceUrl,
        size: { width: image.naturalWidth, height: image.naturalHeight },
      });
      setImageRevision((current) => current + 1);
      setProjectFrame({
        width: install.savedCanvas.width,
        height: install.savedCanvas.height,
      });
      setHistory([restored]);
      setHistoryIndex(0);
      setObjectSelection(NO_SELECTION);
      setOperationMessage(
        "工程已恢复：对象/样式/修补/裁剪/旋转全部就位，可继续编辑。",
      );
    };
    if (installProject) installProject(install.token, apply);
    else apply();
  }, [projectInstall, installProject, NO_SELECTION]);

  /** 恢复后的稳定 id 计数推进：新对象不与恢复对象撞 id（含组/补丁）。 */
  function advanceProjectIdCounters(restored: EditorState) {
    for (const mark of restored.marks) {
      const markNumber = Number(mark.id.replace(/^mark-/, ""));
      if (Number.isInteger(markNumber) && markNumber > nextMarkIdRef.current) {
        nextMarkIdRef.current = markNumber;
      }
      const groupNumber = mark.groupId
        ? Number(mark.groupId.replace(/^group-/, ""))
        : 0;
      if (
        Number.isInteger(groupNumber) &&
        groupNumber > nextGroupIdRef.current
      ) {
        nextGroupIdRef.current = groupNumber;
      }
    }
    for (const patch of restored.patches ?? []) {
      if (patch.id > inpaintPatchIdRef.current) {
        inpaintPatchIdRef.current = patch.id;
      }
    }
  }

  // 卸载：终止 Worker、释放补丁像素，并退役本编辑器持有的导入资源。
  useEffect(() => {
    importMountedRef.current = true;
    const store = inpaintPatchStore.current;
    const workerRef = inpaintWorkerRef;
    const dupWorkerRef = dupmatchWorkerRef;
    return () => {
      importMountedRef.current = false;
      // StrictMode 的探测卸载会立即重新挂载；仅真实退役才释放宿主资源。
      queueMicrotask(() => {
        if (!importMountedRef.current) retireImportsRef.current();
      });
      inpaintGenerationRef.current += 1;
      workerRef.current?.terminate();
      workerRef.current = undefined;
      dupmatchGenerationRef.current += 1;
      dupWorkerRef.current?.terminate();
      dupWorkerRef.current = undefined;
      store.clear();
    };
  }, []);

  // 导出/信息行共用的当前输出尺寸；值不变时保持引用稳定，避免无关编辑
  // 重置用户正在输入的宽高草稿。
  const currentOutputSize = useMemo<CanvasSize | undefined>(() => {
    if (!imageForLayer) return undefined;
    return finalOutputSize(
      imageForLayer,
      state,
      displayCanvas ? canvasSize(displayCanvas) : { width: 900, height: 600 },
    );
  }, [state, imageForLayer, displayCanvas]);

  // 尺寸输入草稿只在真实输出尺寸变化时刷新（渲染期随值调整，不打断输入）。
  const [appliedDraftSize, setAppliedDraftSize] = useState<
    CanvasSize | undefined
  >();
  if (
    currentOutputSize &&
    (appliedDraftSize?.width !== currentOutputSize.width ||
      appliedDraftSize?.height !== currentOutputSize.height)
  ) {
    setAppliedDraftSize(currentOutputSize);
    setSizeDraft({
      width: String(currentOutputSize.width),
      height: String(currentOutputSize.height),
    });
  }

  // 按需文件大小检查：用户明确点击才真实编码一次；编码结束逐项校验
  // 冻结上下文，旧版本结果不得覆盖新内容后的提示。
  async function checkFileSize() {
    if (isCheckingExport) return;
    setIsCheckingExport(true);
    const frozen = freezeExportContext();
    setOperationMessage("正在按当前输出设置检查文件大小……");
    try {
      const blob = await exportCanvas(frozen.image, canvasRef.current, state, {
        format: outputFormat,
        quality: jpegQuality,
        inpaint: { patches: appliedInpaintPatches() },
        imageAssets: importAssetsRef.current,
      });
      if (textEditingRef.current || !exportContextUnchanged(frozen)) {
        setOperationMessage("检查期间内容或设置已变化，请重新检查。");
        return;
      }
      setOperationMessage(
        `文件大小：${blob.type} · 实际 ${formatBytes(blob.size)}。`,
      );
    } catch {
      setOperationMessage("文件大小检查失败；可稍后重试。");
    } finally {
      setIsCheckingExport(false);
    }
  }

  // 原位取字：会话/修订/工具变化时导出当前最终 PNG 并请求宿主准备文字层。
  const sessionKeyForLayer = session?.sessionId ?? "";
  const layerBindingKey = textLayer?.binding
    ? `${textLayer.binding.sessionId}:${textLayer.binding.revision}`
    : "";
  const currentLayerKey = session
    ? `${sessionKeyForLayer}:${localRevision}`
    : "";
  const lastAttempt = useRef<{ key: string; nonce: number }>({
    key: "",
    nonce: -1,
  });
  const prepareInFlight = useRef(false);

  async function prepareTextLayer() {
    if (!session || prepareInFlight.current || exportInProgressRef.current) {
      return;
    }
    if (refuseIfFullyMasked("textLayer")) return;
    prepareInFlight.current = true;
    try {
      // 文字层与识别同源：屏蔽区写入白色像素，被屏蔽文字不可进入可选层。
      const exported = await exportFinalImageIfUnchanged(true);
      if (!exported) return;
      await actions.run({
        type: commands.prepareScreenshotTextLayer,
        sessionId: exported.sessionId,
        revision: exported.revision,
        resourceUri: exported.resourceUri,
      });
    } catch {
      setOperationMessage("无法导出或提交文字层准备，请重试。");
    } finally {
      prepareInFlight.current = false;
    }
  }

  useEffect(() => {
    if (
      !session ||
      !canRecognize ||
      isExporting ||
      (!autoText && tool !== "textSelect")
    ) {
      return;
    }
    // 同一绑定已有任何宿主状态（含 preparing/ready/unavailable/cancelled）
    // 不再自动重复提交；重试只能通过工具重入/手动按钮推进 nonce。
    if (layerBindingKey === currentLayerKey) {
      return;
    }
    const attemptKey = `${currentLayerKey}:${imageRevision}`;
    if (
      lastAttempt.current.key === attemptKey &&
      lastAttempt.current.nonce === prepareNonce
    ) {
      return;
    }
    const timer = setTimeout(() => {
      lastAttempt.current = { key: attemptKey, nonce: prepareNonce };
      void prepareTextLayer();
    }, 400);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    sessionKeyForLayer,
    localRevision,
    prepareNonce,
    autoText,
    tool,
    canRecognize,
    isExporting,
    imageRevision,
    layerBindingKey,
    currentLayerKey,
  ]);

  function commit(next: EditorState): boolean {
    if (!initialExclusionsReady()) {
      setOperationMessage("正在恢复原有屏蔽区，请稍后重试编辑。");
      return false;
    }
    try {
      validateBrushBudget(imageRef.current, canvasRef.current, next);
    } catch (error) {
      setOperationMessage(
        error instanceof Error ? error.message : "笔刷效果预算检查失败",
      );
      return false;
    }
    setHistory((current) => [...current.slice(0, historyIndex + 1), next]);
    setHistoryIndex((current) => current + 1);
    // 提交后剔除已不存在对象的选择；主选失效时回退到剩余集合的顶层。
    pruneSelection(next.marks);
    notifyContentRevision();
    return true;
  }

  /** 按存活 id 裁剪选择：撤销/删除/清除后的悬空 id 不再命中任何对象。 */
  function pruneSelection(marks: readonly Mark[]): void {
    const current = selectionRef.current;
    if (current.ids.size === 0) return;
    const alive = new Set(marks.map((mark) => mark.id));
    const kept = [...current.ids].filter((id) => alive.has(id));
    if (kept.length === current.ids.size) return;
    const keptSet = new Set(kept);
    const primary =
      current.primary && keptSet.has(current.primary)
        ? current.primary
        : kept[kept.length - 1];
    setObjectSelection(
      keptSet.size > 0 ? { ids: keptSet, primary } : NO_SELECTION,
    );
  }

  function notifyContentRevision() {
    // 本地内容版本始终推进：文件会话没有宿主修订回显，也要能拒绝迟到导出。
    contentRevisionRef.current += 1;
    const revision = contentRevisionRef.current;
    setLocalRevision(revision);
    // 任何内容变化（含应用修补本身）都作废未提交的去水印预览/选区与
    // 在途请求：迟到结果绝不追认提交。
    cancelInpaintPending();
    cancelDupmatchPending();
    const currentSession = sessionIdRef.current;
    if (!currentSession) return;
    void actions.run({
      type: commands.notifyScreenshotRevision,
      sessionId: currentSession,
      revision,
    });
  }

  /** 作废未提交的去水印工作状态：终止 Worker、推进代数、清空预览/选区/掩膜。 */
  function cancelInpaintPending(message?: string) {
    inpaintGenerationRef.current += 1;
    if (inpaintWorkerRef.current) {
      inpaintWorkerRef.current.terminate();
      inpaintWorkerRef.current = undefined;
    }
    setInpaintDraft(undefined);
    setInpaintSelection(undefined);
    setInpaintPreview(undefined);
    setInpaintBusy(false);
    inpaintMaskRef.current = undefined;
    setInpaintMask(undefined);
    inpaintBrushPoints.current = [];
    if (pendingMaskFrame.current !== undefined)
      cancelAnimationFrame(pendingMaskFrame.current);
    pendingMaskFrame.current = undefined;
    if (message) setOperationMessage(message);
  }

  /** 基线合成（原图 + 已应用补丁）到原图尺寸离屏画布：修补/重复匹配
   * 共用同一输入；透明/已修补像素一起作为基线。 */
  function composeBaseline(
    image: HTMLImageElement,
  ):
    | { canvas: HTMLCanvasElement; context: CanvasRenderingContext2D }
    | undefined {
    const base = document.createElement("canvas");
    base.width = image.naturalWidth;
    base.height = image.naturalHeight;
    const context = base.getContext("2d", { willReadFrequently: true });
    if (!context) return undefined;
    context.drawImage(image, 0, 0);
    for (const patch of state.patches ?? []) {
      const stored = inpaintPatchStore.current.get(patch.id);
      if (!stored) continue;
      // 替换式叠加：先清除该矩形再写入补丁像素，二次修补的输入不含
      // 旧像素残留，补丁自身的半透明 alpha 也原样保留。
      context.clearRect(patch.x, patch.y, patch.width, patch.height);
      context.drawImage(stored, patch.x, patch.y);
    }
    return { canvas: base, context };
  }

  /** 掩膜增/减统一入口：越预算时显式提示并保留原掩膜。 */
  function runMaskOperation(
    result: InpaintMaskOpResult,
    successMessage?: string,
  ): void {
    if (!result.ok) {
      const message =
        result.reason === "roi-budget"
          ? `修补掩膜工作区超过 ${(INPAINT_MAX_ROI_PIXELS / 1_000_000).toFixed(0)}MP 上限，请分多次修补较小区域。`
          : result.reason === "mask-budget"
            ? `修补掩膜有效像素超过 ${(INPAINT_MAX_MASK_PIXELS / 1_000_000).toFixed(0)}MP 上限，请分多次修补较小区域。`
            : "笔刷轨迹过长，超出修补掩膜工作量预算；请分段涂抹。";
      setOperationMessage(message);
      return;
    }
    inpaintMaskRef.current = result.state;
    setInpaintMask(result.state);
    if (successMessage) setOperationMessage(successMessage);
  }

  /** 显示空间拖拽/选区 → 原图空间矩形掩膜操作。 */
  function applySelectionToMask(
    selection: { start: Point; end: Point },
    add: boolean,
  ): void {
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      setOperationMessage("图片尚未解码完成，无法编辑修补掩膜。");
      return;
    }
    const rect = selectionToNaturalRect(
      selection.start,
      selection.end,
      image,
      state.rotation,
      canvasSize(canvas),
    );
    runMaskOperation(
      applyMaskRect(
        inpaintMaskRef.current,
        rect,
        { width: image.naturalWidth, height: image.naturalHeight },
        add,
      ),
      add
        ? "已框选修补区域；可继续增减掩膜，或点击“预览修补”查看效果。"
        : "已从修补掩膜移除框选区域。",
    );
  }

  /** 掩膜笔刷拖拽：显示空间点列按当前旋转变换到原图空间后 stamp。 */
  function paintMaskBrush(points: readonly Point[], add: boolean): void {
    if (points.length === 0) return;
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      setOperationMessage("图片尚未解码完成，无法编辑修补掩膜。");
      return;
    }
    const displaySize = canvasSize(canvas);
    const naturalPoints = points.map((at) =>
      projectPoint(at, image, state.rotation, displaySize, 0, {
        width: image.naturalWidth,
        height: image.naturalHeight,
      }),
    );
    runMaskOperation(
      applyMaskBrush(
        inpaintMaskRef.current,
        naturalPoints,
        activeToolAttributes.inpaintBrushSize /
          2 /
          imageTransform(image, state.rotation, displaySize).scale,
        {
          width: image.naturalWidth,
          height: image.naturalHeight,
        },
        add,
      ),
    );
  }

  /** 帧内保留全部轨迹点，仅在帧边界复制 ROI、计数及重建叠加层。 */
  function flushMaskBrush(): void {
    if (pendingMaskFrame.current !== undefined)
      cancelAnimationFrame(pendingMaskFrame.current);
    pendingMaskFrame.current = undefined;
    const points = inpaintBrushPoints.current;
    if (!points.length) return;
    inpaintBrushPoints.current = [points[points.length - 1]!];
    paintMaskBrush(points, activeToolAttributes.maskMode === "brush-add");
  }

  function queueMaskBrush(at: Point): void {
    inpaintBrushPoints.current.push(at);
    if (pendingMaskFrame.current !== undefined) return;
    pendingMaskFrame.current = requestAnimationFrame(() => {
      pendingMaskFrame.current = undefined;
      flushMaskBrush();
    });
  }

  /** 用当前基线（原图 + 已应用补丁）与二值掩膜发起 CPU Worker 修补预览。 */
  function startInpaintPreview() {
    if (inpaintBusy || inpaintPreview) return;
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      setOperationMessage("图片尚未解码完成，无法修补；请稍后重试。");
      return;
    }
    // 只有成功添加的有效掩膜可预览；拖拽移除区不是待修补区域。
    flushMaskBrush();
    const maskState = inpaintMaskRef.current;
    if (!maskState) {
      setOperationMessage("修补掩膜为空，请先添加要修补的区域。");
      return;
    }
    const preflight = preflightSelection(
      maskState.rect,
      image,
      maskCount(maskState),
    );
    if (!preflight.ok) {
      setOperationMessage(
        describeInpaintFailure(preflight.code, preflight.message),
      );
      return;
    }
    const rect = maskBounds(maskState);
    if (!rect) {
      setOperationMessage("修补掩膜为空，请先框选或涂抹要修补的区域。");
      return;
    }
    if (typeof Worker !== "function") {
      setOperationMessage("当前环境不支持本地修补，当前图片未被修改。");
      return;
    }
    // 基线合成到原图尺寸离屏画布：透明/已修补像素一起作为修补输入。
    const composed = composeBaseline(image);
    if (!composed) {
      setOperationMessage("无法读取画布像素，修补未开始。");
      return;
    }
    const { canvas: base, context: baseContext } = composed;
    const pixels = baseContext.getImageData(0, 0, base.width, base.height);
    const generation = inpaintGenerationRef.current + 1;
    inpaintGenerationRef.current = generation;
    inpaintWorkerRef.current?.terminate();
    let worker: Worker;
    try {
      worker = new Worker(
        new URL("./inpaint/inpaint.worker.ts", import.meta.url),
        { type: "module" },
      );
    } catch {
      // 同步构造失败（如环境限制）：保留原图，允许重试，不留半途状态。
      inpaintWorkerRef.current = undefined;
      setOperationMessage("无法启动本地修补，当前图片未被修改；请重试。");
      return;
    }
    inpaintWorkerRef.current = worker;
    // 掩膜收敛到紧致包围盒：请求 ROI 即补丁矩形，掩膜数据重建为紧致
    // 拷贝后 transfer；本地掩膜保留供继续增减，迟到响应按代数丢弃。
    const roiMask = new Uint8Array(rect.width * rect.height);
    for (let y = 0; y < rect.height; y += 1) {
      for (let x = 0; x < rect.width; x += 1) {
        const sx = rect.x - maskState.rect.x + x;
        const sy = rect.y - maskState.rect.y + y;
        if (
          sx < 0 ||
          sy < 0 ||
          sx >= maskState.rect.width ||
          sy >= maskState.rect.height
        )
          continue;
        roiMask[y * rect.width + x] =
          maskState.data[sy * maskState.rect.width + sx] ?? 0;
      }
    }
    const frozen = {
      key: currentKeyRef.current,
      sessionId: sessionIdRef.current,
      contentVersion: contentRevisionRef.current,
      image,
      mask: maskState,
      generation,
    };
    worker.onmessage = (event: MessageEvent<InpaintResponse>) => {
      const response = event.data;
      if (response.generation !== inpaintGenerationRef.current) return;
      if (
        frozen.key !== currentKeyRef.current ||
        frozen.sessionId !== sessionIdRef.current ||
        frozen.contentVersion !== contentRevisionRef.current ||
        frozen.image !== imageRef.current
      ) {
        setInpaintBusy(false);
        setOperationMessage(
          "内容或会话已变化，本次修补结果已丢弃；请重新预览。",
        );
        return;
      }
      if (frozen.mask !== inpaintMaskRef.current) {
        setInpaintBusy(false);
        setOperationMessage("掩膜已变化，本次修补结果已丢弃；请重新预览。");
        return;
      }
      if (!response.ok) {
        setInpaintBusy(false);
        setOperationMessage(
          describeInpaintFailure(response.error.code, response.error.message),
        );
        return;
      }
      const full = document.createElement("canvas");
      full.width = base.width;
      full.height = base.height;
      const fullContext = full.getContext("2d");
      const patch = document.createElement("canvas");
      patch.width = rect.width;
      patch.height = rect.height;
      const patchContext = patch.getContext("2d");
      if (!fullContext || !patchContext) {
        setInpaintBusy(false);
        setOperationMessage("无法生成修补预览，当前图片未被修改。");
        return;
      }
      fullContext.putImageData(
        new ImageData(
          new Uint8ClampedArray(response.rgba),
          base.width,
          base.height,
        ),
        0,
        0,
      );
      patchContext.drawImage(
        full,
        rect.x,
        rect.y,
        rect.width,
        rect.height,
        0,
        0,
        rect.width,
        rect.height,
      );
      setInpaintPreview({
        generation,
        naturalRect: rect,
        canvas: patch,
      });
      setInpaintBusy(false);
      setInpaintCompare("after");
      setOperationMessage("修补预览完成。请对比前后效果，确认后再“应用修补”。");
    };
    worker.onerror = () => {
      if (generation !== inpaintGenerationRef.current) return;
      setInpaintBusy(false);
      setOperationMessage("本地修补意外失败，当前图片未被修改；请重试。");
    };
    setInpaintBusy(true);
    setInpaintPreview(undefined);
    setOperationMessage(
      "正在本地修补选中区域……期间可取消，当前图片不会被修改。",
    );
    const buffer = pixels.data.buffer;
    const maskCopy = new Uint8Array(roiMask);
    const request: InpaintMaskRequest = {
      generation,
      rgba: buffer,
      width: base.width,
      height: base.height,
      rect,
      mask: maskCopy.buffer,
    };
    worker.postMessage(request, [buffer, maskCopy.buffer]);
  }

  /** 新提交后仍保留的唯一补丁像素数；被截断的重做分支不计入。 */
  function totalPatchPixels(): number {
    return totalDistinctPatchPixels(history, historyIndex);
  }

  /** 明确应用预览：恰好一次 commit 追加补丁，随历史可撤销/重做；历史
   * 补丁聚合预算超限时显式拒绝，绝不静默丢弃撤销条目。 */
  function applyInpaintPreview() {
    const preview = inpaintPreview;
    if (!preview) return;
    const addition = preview.naturalRect.width * preview.naturalRect.height;
    if (totalPatchPixels() + addition > INPAINT_MAX_TOTAL_PATCH_PIXELS) {
      setOperationMessage(
        `历史修补总量将超出 ${INPAINT_MAX_TOTAL_PATCH_PIXELS / 1_000_000}MP 预算；请先撤销部分修补后再应用。`,
      );
      return;
    }
    const id = inpaintPatchIdRef.current + 1;
    inpaintPatchIdRef.current = id;
    inpaintPatchStore.current.set(id, preview.canvas);
    if (
      !commit({
        ...state,
        patches: [...(state.patches ?? []), { id, ...preview.naturalRect }],
      })
    ) {
      inpaintPatchStore.current.delete(id);
      return;
    }
    setOperationMessage(
      "已应用修补；可用撤销恢复原图，复制/保存/识别使用修补后的画面。",
    );
  }

  /** 作废重复匹配在途/候选状态：候选绑定源与修订，过期即丢弃。 */
  function cancelDupmatchPending(message?: string) {
    dupmatchGenerationRef.current += 1;
    if (dupmatchWorkerRef.current) {
      dupmatchWorkerRef.current.terminate();
      dupmatchWorkerRef.current = undefined;
    }
    dupmatchFrozenRef.current = undefined;
    dupmatchDraft.current = undefined;
    dupmatchSelectionRef.current = undefined;
    setDupmatchSelection(undefined);
    setDupmatchCandidates(undefined);
    setDupmatchAccepted(undefined);
    setDupmatchBusy(false);
    if (message) setOperationMessage(message);
  }

  /** 发起重复内容匹配：同尺度平移、有界搜索；候选仅预览不入历史。 */
  function startDuplicateSearch() {
    if (dupmatchBusy) return;
    const image = imageRef.current;
    const canvas = canvasRef.current;
    const selection = dupmatchSelection;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      setOperationMessage("图片尚未解码完成，无法匹配；请稍后重试。");
      return;
    }
    if (!selection) {
      setOperationMessage("请先拖拽框选要匹配的重复内容（如一处水印）。");
      return;
    }
    if (typeof Worker !== "function") {
      setOperationMessage("当前环境不支持本地匹配，当前图片未被修改。");
      return;
    }
    const rect = selectionToNaturalRect(
      selection.start,
      selection.end,
      image,
      state.rotation,
      canvasSize(canvas),
    );
    if (rect.width <= 0 || rect.height <= 0) {
      setOperationMessage("选区不在图片内，请重新框选要匹配的内容。");
      return;
    }
    if (image.naturalWidth * image.naturalHeight > DUPLICATE_MAX_IMAGE_PIXELS) {
      setOperationMessage(
        `图片 ${(image.naturalWidth * image.naturalHeight) / 1_000_000}MP 超过重复匹配上限 8MP，请先缩小图片。`,
      );
      return;
    }
    const composed = composeBaseline(image);
    if (!composed) {
      setOperationMessage("无法读取画布像素，匹配未开始。");
      return;
    }
    const pixels = composed.context.getImageData(
      0,
      0,
      composed.canvas.width,
      composed.canvas.height,
    );
    const generation = dupmatchGenerationRef.current + 1;
    dupmatchGenerationRef.current = generation;
    dupmatchWorkerRef.current?.terminate();
    let worker: Worker;
    try {
      worker = new Worker(
        new URL("./duplicate/duplicateMatch.worker.ts", import.meta.url),
        { type: "module" },
      );
    } catch {
      dupmatchWorkerRef.current = undefined;
      setOperationMessage("无法启动本地匹配，当前图片未被修改；请重试。");
      return;
    }
    dupmatchWorkerRef.current = worker;
    const frozen = {
      key: currentKeyRef.current,
      sessionId: sessionIdRef.current,
      contentVersion: contentRevisionRef.current,
      image,
      selection,
      generation,
    };
    dupmatchFrozenRef.current = frozen;
    worker.onmessage = (event: MessageEvent<DuplicateResponse>) => {
      const response = event.data;
      if (response.generation !== dupmatchGenerationRef.current) return;
      if (
        frozen.key !== currentKeyRef.current ||
        frozen.sessionId !== sessionIdRef.current ||
        frozen.contentVersion !== contentRevisionRef.current ||
        frozen.image !== imageRef.current ||
        frozen.selection !== dupmatchSelection
      ) {
        setDupmatchBusy(false);
        cancelDupmatchPending(
          "内容或选区已变化，本次匹配结果已丢弃；请重新搜索。",
        );
        return;
      }
      setDupmatchBusy(false);
      if (!response.ok) {
        setOperationMessage(response.error.message);
        return;
      }
      setDupmatchCandidates(response.candidates);
      setDupmatchAccepted(response.candidates.map(() => true));
      setOperationMessage(
        `找到 ${response.candidates.length} 处重复内容；可逐项增删候选，确认后才写入图片。`,
      );
    };
    worker.onerror = () => {
      if (generation !== dupmatchGenerationRef.current) return;
      setDupmatchBusy(false);
      setOperationMessage("本地匹配意外失败，当前图片未被修改；请重试。");
    };
    setDupmatchBusy(true);
    setDupmatchCandidates(undefined);
    setDupmatchAccepted(undefined);
    setOperationMessage(
      "正在本地搜索重复内容……期间可取消，当前图片不会被修改。",
    );
    const buffer = pixels.data.buffer;
    worker.postMessage(
      {
        generation,
        rgba: buffer,
        width: composed.canvas.width,
        height: composed.canvas.height,
        rect,
      },
      [buffer],
    );
  }

  /** 确认应用候选：每个接受候选写入不透明纯色补丁（取自周边环境色），
   * 恰好一次 commit；源/修订已变化时拒绝迟到确认。 */
  function applyDuplicateCandidates() {
    const candidates = dupmatchCandidates;
    const accepted = dupmatchAccepted;
    const frozen = dupmatchFrozenRef.current;
    if (!candidates || !accepted || !frozen) return;
    const image = imageRef.current;
    if (
      !image ||
      frozen.key !== currentKeyRef.current ||
      frozen.sessionId !== sessionIdRef.current ||
      frozen.contentVersion !== contentRevisionRef.current ||
      frozen.image !== image
    ) {
      cancelDupmatchPending("内容或会话已变化，候选已失效；请重新框选并搜索。");
      return;
    }
    const chosen = candidates.filter((_, index) => accepted[index] === true);
    if (chosen.length === 0) {
      setOperationMessage("未勾选任何候选；请至少保留一个候选或放弃匹配。");
      return;
    }
    const addition = chosen.reduce(
      (total, candidate) => total + candidate.width * candidate.height,
      0,
    );
    if (totalPatchPixels() + addition > INPAINT_MAX_TOTAL_PATCH_PIXELS) {
      setOperationMessage(
        `候选遮挡总量将超出 ${INPAINT_MAX_TOTAL_PATCH_PIXELS / 1_000_000}MP 补丁预算；请减少候选或先撤销部分修补。`,
      );
      return;
    }
    const composed = composeBaseline(image);
    if (!composed) {
      setOperationMessage("无法读取画布像素，本次应用已取消。");
      return;
    }
    const baseline = composed.context.getImageData(
      0,
      0,
      composed.canvas.width,
      composed.canvas.height,
    ).data;
    const patches = [...(state.patches ?? [])];
    // 先完整暂存本批补丁：任一候选构造失败则丢弃整批（未写入 store/
    // 历史，画布引用即刻释放），不遗留已建补丁的孤儿像素。
    const staged: {
      readonly id: number;
      readonly rect: DuplicateCandidate;
      readonly canvas: HTMLCanvasElement;
    }[] = [];
    for (const candidate of chosen) {
      // 候选周边 2px 环境平均色：纯色遮挡与背景衔接；无有效采样时黑色。
      let r = 0;
      let g = 0;
      let b = 0;
      let n = 0;
      for (
        let y = candidate.y - 2;
        y <= candidate.y + candidate.height + 1;
        y += 1
      ) {
        for (
          let x = candidate.x - 2;
          x <= candidate.x + candidate.width + 1;
          x += 1
        ) {
          const inside =
            x >= candidate.x &&
            x < candidate.x + candidate.width &&
            y >= candidate.y &&
            y < candidate.y + candidate.height;
          if (inside) continue;
          if (
            x < 0 ||
            y < 0 ||
            x >= image.naturalWidth ||
            y >= image.naturalHeight
          )
            continue;
          const p = (y * image.naturalWidth + x) * 4;
          if ((baseline[p + 3] ?? 0) === 0) continue;
          r += baseline[p] ?? 0;
          g += baseline[p + 1] ?? 0;
          b += baseline[p + 2] ?? 0;
          n += 1;
        }
      }
      const color =
        n > 0
          ? `rgb(${Math.round(r / n)},${Math.round(g / n)},${Math.round(b / n)})`
          : "#000000";
      const patch = document.createElement("canvas");
      patch.width = candidate.width;
      patch.height = candidate.height;
      const context = patch.getContext("2d");
      if (!context) {
        setOperationMessage(
          "无法生成遮挡补丁，本次应用已整体取消；图片未被修改。",
        );
        return;
      }
      context.fillStyle = color;
      context.fillRect(0, 0, patch.width, patch.height);
      const id = inpaintPatchIdRef.current + staged.length + 1;
      staged.push({ id, rect: candidate, canvas: patch });
    }
    // 全部成功后一次性移交：store 写入与历史提交要么整体发生要么不发生。
    inpaintPatchIdRef.current += staged.length;
    for (const entry of staged) {
      inpaintPatchStore.current.set(entry.id, entry.canvas);
      patches.push({ id: entry.id, ...entry.rect });
    }
    if (!commit({ ...state, patches })) {
      for (const entry of staged) inpaintPatchStore.current.delete(entry.id);
      return;
    }
    cancelDupmatchPending();
    setOperationMessage(
      `已应用 ${chosen.length} 处重复内容遮挡；可用撤销恢复，复制/保存/识别使用真实像素。`,
    );
  }

  /** 单选替换：点击/列表选择入口；组成员自动展开为整组（组整体选择）。 */
  function select(id: string | undefined): void {
    if (!id) {
      setObjectSelection(NO_SELECTION);
      return;
    }
    const ids = expandGroupSelection(state.marks, new Set([id]));
    setObjectSelection({ ids, primary: id });
  }

  /** 修饰键增减：已在选区内则移除，否则加入；组按整组切换。 */
  function toggleSelection(id: string): void {
    const groupIds = expandGroupSelection(state.marks, new Set([id]));
    const current = selectionRef.current;
    const adding = ![...groupIds].some((member) => current.ids.has(member));
    if (adding) {
      const ids = new Set([...current.ids, ...groupIds]);
      setObjectSelection({ ids, primary: id });
      return;
    }
    const ids = new Set(
      [...current.ids].filter((entry) => !groupIds.has(entry)),
    );
    const primary =
      current.primary && ids.has(current.primary)
        ? current.primary
        : [...ids][ids.size - 1];
    setObjectSelection(ids.size > 0 ? { ids, primary } : NO_SELECTION);
  }

  /** 选区含锁定对象时整体不可修改：避免对锁组/锁对象的部分修改。 */
  function selectionModifiable(ids: ReadonlySet<string> | undefined): boolean {
    const effective = ids ?? selectionRef.current.ids;
    if (effective.size === 0) return false;
    if (state.marks.some((mark) => effective.has(mark.id) && mark.locked)) {
      setOperationMessage("选区包含已锁定对象；请先在对象列表解锁后再修改。");
      return false;
    }
    return true;
  }

  // 样式编辑目标：选择工具下选中描边类（矩形/椭圆/箭头/画笔/荧光笔）
  // 已有标记时，属性排就地编辑该标记样式（每次修改入历史可撤销）；
  // 未选中时同一控件编辑新标记的绘制默认值。旧标记缺省：边框 100%、
  // 无填充，渲染与旧版完全一致。
  const editingMarkCandidate =
    tool === "select" && selectedMark !== undefined
      ? textEdit?.id === selectedMark
        ? textEdit.mark
        : state.marks.find((mark) => mark.id === selectedMark)
      : undefined;
  const editingMark =
    editingMarkCandidate && COLOR_TOOLS.includes(editingMarkCandidate.tool)
      ? editingMarkCandidate
      : undefined;
  const styleEditing = !!editingMark;
  const activeStyleColor = editingMark
    ? (editingMark.style?.color ??
      (editingMark.tool === "solid" ? DEFAULT_SOLID_COLOR : DEFAULT_COLOR))
    : activeToolAttributes.color;
  const activeStrokeWidth = editingMark
    ? (editingMark.style?.strokeWidth ?? 3)
    : activeToolAttributes.strokeWidth;
  const activeStrokeOpacity = editingMark
    ? Math.round(annotationStrokeAlpha(editingMark) * 100)
    : activeToolAttributes.strokeOpacity;
  const activeLineStyle = editingMark
    ? (editingMark.style?.lineStyle ?? "solid")
    : activeToolAttributes.lineStyle;
  const activeCornerRadius = editingMark
    ? (editingMark.style?.cornerRadius ?? 0)
    : activeToolAttributes.cornerRadius;
  const activeArrowStyle = editingMark
    ? (editingMark.style?.arrowStyle ?? "open")
    : activeToolAttributes.arrowStyle;
  const activeArrowDouble = editingMark
    ? editingMark.style?.arrowDouble === true
    : activeToolAttributes.arrowDouble;
  const editingFill = editingMark ? annotationFill(editingMark) : undefined;
  const activeFillEnabled = editingMark
    ? !!editingFill
    : activeToolAttributes.fillEnabled;
  const activeFillColor = editingFill?.color ?? activeToolAttributes.fillColor;
  const activeFillOpacity = editingFill
    ? Math.round(editingFill.alpha * 100)
    : activeToolAttributes.fillOpacity;

  /** 属性排样式统一入口：选中标记时就地改样式入历史，否则更新当前工具的属性记忆；集中回退避免散布条件 setter 分派。 */
  /** 重新开启填充：对象自身携带的关闭前值（fill.enabled=false 保留
   * color/opacity）优先，其次所属工具的属性记忆；结果写入对象自身样式，
   * 随历史/撤销重做与视图投影自然流转，无索引侧表。 */
  function reenabledFill(
    previous: AnnotationFill | undefined,
    owningTool: ToolAttributes,
  ): AnnotationFill {
    if (previous) return { color: previous.color, opacity: previous.opacity };
    return {
      color: owningTool.fillColor,
      opacity: owningTool.fillOpacity,
    };
  }

  function applyStyleChange(
    change: StyleChange,
    targetId?: string | null,
  ): void {
    // 目标优先级：blur 提交携带的 targetId（防点入画布后选区已换）> 当前
    // 主选 > 工具属性记忆。选中标记时就地改样式入历史。
    const candidate =
      targetId === null
        ? undefined
        : targetId !== undefined
          ? state.marks.find((mark) => mark.id === targetId)
          : editingMark;
    const mark =
      candidate && COLOR_TOOLS.includes(candidate.tool) ? candidate : undefined;
    if (typeof targetId === "string" && !mark) return;
    if (mark) {
      if (
        !selectionModifiable(
          expandGroupSelection(state.marks, new Set([mark.id])),
        )
      )
        return;
      const style = mark.style ?? {};
      // 选中标记已带填充时，改色保留其原不透明度、改不透明度保留其原色；
      // 回退值取所属工具记忆而非当前选择工具。
      const owningTool = attributesOfTool(mark.tool);
      const targetFill = annotationFill(mark);
      const preservedFillColor =
        targetFill?.color ?? style.fill?.color ?? owningTool.fillColor;
      const preservedFillOpacity = targetFill
        ? Math.round(targetFill.alpha * 100)
        : (style.fill?.opacity ?? owningTool.fillOpacity);
      const next: Mark["style"] =
        change.kind === "color"
          ? { ...style, color: change.value }
          : change.kind === "strokeWidth"
            ? { ...style, strokeWidth: change.value }
            : change.kind === "strokeOpacity"
              ? { ...style, strokeOpacity: change.value }
              : change.kind === "lineStyle"
                ? { ...style, lineStyle: change.value }
                : change.kind === "cornerRadius"
                  ? { ...style, cornerRadius: change.value }
                  : change.kind === "arrowStyle"
                    ? { ...style, arrowStyle: change.value }
                    : change.kind === "arrowDouble"
                      ? { ...style, arrowDouble: change.value }
                      : change.kind === "fillEnabled"
                        ? {
                            ...style,
                            // 关闭保留值（enabled=false 渲染为空心），重开
                            // 恢复对象自身值或所属工具记忆。
                            fill: change.value
                              ? reenabledFill(style.fill, owningTool)
                              : style.fill
                                ? { ...style.fill, enabled: false }
                                : undefined,
                          }
                        : change.kind === "fillColor"
                          ? {
                              ...style,
                              fill: {
                                color: change.value,
                                opacity: preservedFillOpacity,
                              },
                            }
                          : {
                              ...style,
                              fill: {
                                color: preservedFillColor,
                                opacity: change.value,
                              },
                            };
      if (JSON.stringify(style) === JSON.stringify(next)) return;
      if (textEdit?.id === mark.id)
        setTextEdit({ id: mark.id, mark: { ...mark, style: next } });
      else if (
        !commit({
          ...state,
          marks: state.marks.map((entry) =>
            entry.id === mark.id ? { ...entry, style: next } : entry,
          ),
        })
      )
        return;
      return;
    }
    // 未选中标记：更新当前工具的属性记忆（不影响其他工具，可重置）。
    updateToolAttribute(
      change.kind === "color"
        ? { color: change.value }
        : change.kind === "strokeWidth"
          ? { strokeWidth: change.value }
          : change.kind === "strokeOpacity"
            ? { strokeOpacity: change.value }
            : change.kind === "lineStyle"
              ? { lineStyle: change.value }
              : change.kind === "cornerRadius"
                ? { cornerRadius: change.value }
                : change.kind === "arrowStyle"
                  ? { arrowStyle: change.value }
                  : change.kind === "arrowDouble"
                    ? { arrowDouble: change.value }
                    : change.kind === "fillEnabled"
                      ? { fillEnabled: change.value }
                      : change.kind === "fillColor"
                        ? { fillColor: change.value }
                        : { fillOpacity: change.value },
    );
  }

  /** 更新当前工具的属性记忆；各工具互不影响，重置时删除条目回默认。 */
  function updateToolAttribute(change: Partial<ToolAttributes>): void {
    setToolAttributes((current) => ({
      ...current,
      [tool]: { ...current[tool], ...change },
    }));
  }

  /** 重置当前工具属性记忆：回到默认值，不触碰已有标记与历史。 */
  function resetToolAttributes(): void {
    setToolAttributes((current) => {
      const next = { ...current };
      delete next[tool];
      return next;
    });
    setOperationMessage(`已重置“${TOOL_LABELS[tool]}”工具属性为默认值。`);
  }

  function point(event: React.MouseEvent<HTMLCanvasElement>): Point {
    // 选中对象会展开属性栏；同一手势沿按下时边界换算，避免布局移动变成对象位移。
    const bounds =
      tool === "select" && dragStart.current && selectionDragBounds.current
        ? selectionDragBounds.current
        : event.currentTarget.getBoundingClientRect();
    return {
      x:
        ((event.clientX - bounds.left) / Math.max(bounds.width, 1)) *
        canvasSize(event.currentTarget).width,
      y:
        ((event.clientY - bounds.top) / Math.max(bounds.height, 1)) *
        canvasSize(event.currentTarget).height,
    };
  }

  function undo() {
    if (historyIndex === 0) return;
    cancelDrawing();
    select(undefined);
    setHistoryIndex(historyIndex - 1);
    notifyContentRevision();
  }

  function redo() {
    if (historyIndex >= history.length - 1) return;
    cancelDrawing();
    select(undefined);
    setHistoryIndex(historyIndex + 1);
    notifyContentRevision();
  }

  function currentStyle(): Mark["style"] {
    const attributes = activeToolAttributes;
    return {
      color: attributes.color,
      strokeWidth: attributes.strokeWidth,
      fontSize: attributes.fontSize,
      ...(["text", "numbering", "callout"].includes(tool)
        ? attributes.textStyle
        : {}),
      // 马赛克/模糊创建时冻结当前强度与笔刷尺寸，撤销/重做回放当时效果。
      ...(tool === "mosaic" || tool === "blur"
        ? {
            intensity: attributes.intensity,
            brushSize: attributes.brushSize,
          }
        : {}),
      // 描边类工具冻结边框不透明度；形状工具另冻结填充样式；文字水印
      // 复用同一 strokeOpacity 字段（缺省 100，旧标记渲染不变）。
      ...(STROKE_TOOLS.includes(tool) || tool === "text"
        ? { strokeOpacity: attributes.strokeOpacity }
        : {}),
      ...(FILL_TOOLS.includes(tool) && attributes.fillEnabled
        ? {
            fill: {
              color: attributes.fillColor,
              opacity: attributes.fillOpacity,
            },
          }
        : {}),
      // 聚光灯暗度冻结进 fill.opacity（通用字段，缺省 55）。
      // 线型/圆角/箭头端头：与默认一致时不写入，旧字段兼容且载荷精简。
      ...(LINE_STYLE_TOOLS.includes(tool) && attributes.lineStyle !== "solid"
        ? { lineStyle: attributes.lineStyle }
        : {}),
      ...(CORNER_TOOLS.includes(tool) && attributes.cornerRadius > 0
        ? { cornerRadius: attributes.cornerRadius }
        : {}),
      ...(tool === "arrow"
        ? {
            ...(attributes.arrowStyle !== "open"
              ? { arrowStyle: attributes.arrowStyle }
              : {}),
            ...(attributes.arrowDouble ? { arrowDouble: true } : {}),
          }
        : {}),
      ...(tool === "spotlight"
        ? {
            fill: {
              color: "#000000",
              opacity: attributes.spotlightDarkness,
            },
          }
        : {}),
    };
  }

  /** 取色结果统一走单一分发调用点（屏幕取色/画布取样共用）。 */
  function applyPickedColor(hex: string): void {
    applyStyleChange({ kind: "color", value: hex });
  }

  /** EyeDropper 屏幕取色：结果色仅更新当前标注色，失败/取消不改动。 */
  async function pickScreenColor() {
    const Picker = (window as WindowWithEyeDropper).EyeDropper;
    if (!Picker || eyedropperBusy) return;
    setEyedropperBusy(true);
    try {
      const result = await new Picker().open();
      const hex = result.sRGBHex?.toLowerCase();
      if (hex && /^#[0-9a-f]{6}$/.test(hex)) {
        applyPickedColor(hex);
        setOperationMessage(`已从屏幕取色 ${hex}。`);
      }
    } catch {
      // 用户取消或环境拒绝：保持当前颜色，不打断编辑。
      setOperationMessage("已取消屏幕取色；标注颜色保持不变。");
    } finally {
      setEyedropperBusy(false);
    }
  }

  /** 进入一次性画布取色：下一次画布点击读取当前内容像素，Esc 取消。 */
  function enterCanvasPick() {
    colorPickPendingRef.current = true;
    setOperationMessage("取色模式：在画布上点击要取样的位置；按 Esc 取消。");
  }

  /** 画布取样：在无编辑 chrome 的离屏重绘上读 1px，避免遮罩/手柄/虚线框
   * 污染采样；透明像素不产生有效颜色。 */
  function sampleCanvasColor(at: Point, canvas: HTMLCanvasElement) {
    colorPickPendingRef.current = false;
    const size = canvasSize(canvas);
    const clean = document.createElement("canvas");
    clean.width = Math.max(1, Math.round(size.width));
    clean.height = Math.max(1, Math.round(size.height));
    clean.dataset.coordinateWidth = String(size.width);
    clean.dataset.coordinateHeight = String(size.height);
    draw(
      clean,
      imageRef.current,
      textEdit
        ? {
            ...state,
            marks: state.marks.map((mark) =>
              mark.id === textEdit.id ? textEdit.mark : mark,
            ),
          }
        : state,
      undefined,
      undefined,
      false,
      1,
      outputFormat === "image/jpeg" ? "#ffffff" : undefined,
      false,
      { patches: currentInpaintLayer().patches },
      undefined,
      true,
      importAssetsRef.current,
    );
    const context = clean.getContext("2d", { willReadFrequently: true });
    const pixel = context?.getImageData(
      Math.max(0, Math.min(size.width - 1, Math.floor(at.x))),
      Math.max(0, Math.min(size.height - 1, Math.floor(at.y))),
      1,
      1,
    );
    if (!pixel || pixel.data.length < 4 || pixel.data[3] === 0) {
      setOperationMessage("该位置没有可取的颜色；标注颜色保持不变。");
      return;
    }
    const hex = rgbToHex(pixel.data[0]!, pixel.data[1]!, pixel.data[2]!);
    applyPickedColor(hex);
    setOperationMessage(`已从画布取色 ${hex}。`);
  }

  function isValidOutputSize(size: { width: number; height: number }): boolean {
    return (
      Number.isInteger(size.width) &&
      Number.isInteger(size.height) &&
      size.width >= 1 &&
      size.height >= 1 &&
      size.width <= MAX_OUTPUT_DIMENSION &&
      size.height <= MAX_OUTPUT_DIMENSION &&
      size.width * size.height <= MAX_OUTPUT_PIXELS
    );
  }

  /** 尺寸变更入历史、推进修订；无效目标保持原图可恢复状态。 */
  function applyResize(
    next: { width: number; height: number } | undefined,
    label: string,
  ) {
    const image = imageRef.current;
    if (!image) {
      setOperationMessage("图片尚未解码完成，无法调整输出尺寸。");
      return;
    }
    if (next && !isValidOutputSize(next)) {
      setOperationMessage(
        `输出尺寸超出上限（单边≤${MAX_OUTPUT_DIMENSION}px，总像素≤${MAX_OUTPUT_PIXELS / 1_000_000}MP），已保持当前尺寸。`,
      );
      return;
    }
    if (!commit({ ...state, resize: next })) return;
    setOperationMessage(
      next
        ? `已设置输出尺寸 ${next.width}×${next.height}（${label}）；可用撤销恢复。`
        : "已恢复原始输出尺寸；可用撤销恢复。",
    );
  }

  function applyScalePercent(percent: number) {
    const base = currentOutputSize;
    if (!base) {
      setOperationMessage("图片尚未解码完成，无法调整输出尺寸。");
      return;
    }
    if (percent === 100) {
      applyResize(undefined, "原始尺寸");
      return;
    }
    applyResize(
      {
        width: Math.max(1, Math.round((base.width * percent) / 100)),
        height: Math.max(1, Math.round((base.height * percent) / 100)),
      },
      `等比 ${percent}%`,
    );
  }

  function applySpecifiedSize() {
    const width = Number(sizeDraft.width);
    const height = Number(sizeDraft.height);
    if (
      sizeDraft.width.trim() === "" ||
      sizeDraft.height.trim() === "" ||
      !Number.isInteger(width) ||
      !Number.isInteger(height)
    ) {
      setOperationMessage("请输入不小于 1 的整数宽高后再应用。");
      return;
    }
    applyResize({ width, height }, "指定尺寸");
  }

  function changeOutputFormat(format: OutputImageFormat) {
    if (format === outputFormat) return;
    outputFormatRef.current = format;
    setOutputFormat(format);
    setOperationMessage(
      format === "image/jpeg"
        ? "输出格式已切换为 JPEG：透明区域将以白色背景合成，可在“JPEG 质量”中调整压缩。"
        : "输出格式已切换为 PNG。",
    );
    // 格式改变最终像素：推进修订使旧文字层/识别结果失效，在途导出被拒绝。
    notifyContentRevision();
  }

  function changeJpegQuality(quality: number) {
    if (quality === jpegQuality) return;
    jpegQualityRef.current = quality;
    setJpegQuality(quality);
    notifyContentRevision();
  }

  /** #235 对象橡皮擦累积：按手势扫过的线段采样命中（约 6px 步长，覆盖
   * pointermove 事件稀疏时路径中间的小对象）；命中未锁定对象加入本次
   * 手势集合（仅预览）；锁定对象由 findMark 跳过，底图/修补永不触及。
   * 已命中对象从命中候选中排除：同一手势内在重叠栈上继续命中被显露的
   * 下层对象（H4），锁定对象仍不可擦除。 */
  function eraseObjectAt(at: Point, from: Point | undefined): void {
    const context = canvasRef.current?.getContext("2d") ?? undefined;
    const probe = (point: Point): void => {
      const hits = objectEraserHits.current;
      if (!hits) return;
      const candidates = hits.size
        ? state.marks.filter((mark) => !hits.has(mark.id))
        : state.marks;
      const hitId = findMark(candidates, point, context);
      if (hitId === undefined) return;
      hits.add(hitId);
    };
    if (!from || (from.x === at.x && from.y === at.y)) {
      probe(at);
    } else {
      const distance = Math.hypot(at.x - from.x, at.y - from.y);
      const steps = Math.max(1, Math.ceil(distance / 6));
      for (let step = 0; step <= steps; step += 1) {
        probe({
          x: from.x + ((at.x - from.x) * step) / steps,
          y: from.y + ((at.y - from.y) * step) / steps,
        });
      }
    }
    scheduleDraw();
  }

  function cancelDrawing() {
    setTextEdit(undefined);
    textComposing.current = false;
    dragStart.current = undefined;
    markDragRef.current = undefined;
    cropResizeRef.current = undefined;
    moveDragRef.current = undefined;
    marqueeRef.current = undefined;
    colorPickPendingRef.current = false;
    setObjectDraft(undefined);
    setCropDraft(undefined);
    setDraftMark(undefined);
    setInpaintDraft(undefined);
    dupmatchDraft.current = undefined;
    inpaintBrushPoints.current = [];
    // 对象橡皮擦手势作废：不提交任何删除（不影响历史与底图）。
    objectEraserHits.current = undefined;
    objectEraserLast.current = undefined;
    setSpotlightRegionDraft(undefined);
    setSpotlightRegionTarget(undefined);
    if (pendingMaskFrame.current !== undefined)
      cancelAnimationFrame(pendingMaskFrame.current);
    pendingMaskFrame.current = undefined;
  }

  // 滚轮调线宽：仅描边类工具且未在就地编辑选中标记时生效；监听只挂画布
  // （非被动，阻止页面滚动），输入框/下拉自身的键盘与滚动不受影响。
  useLayoutEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return undefined;
    const onWheel = (event: WheelEvent) => {
      if (!STROKE_TOOLS.includes(tool) || styleEditing) return;
      event.preventDefault();
      const previous: ToolAttributes = {
        ...DEFAULT_TOOL_ATTRIBUTES,
        ...toolAttributesRef.current[tool],
      };
      const nearest = STROKE_WIDTHS.reduce(
        (best, width, index) =>
          Math.abs(width - previous.strokeWidth) <
          Math.abs(STROKE_WIDTHS[best]! - previous.strokeWidth)
            ? index
            : best,
        0,
      );
      const next = Math.max(
        0,
        Math.min(
          STROKE_WIDTHS.length - 1,
          nearest + (event.deltaY > 0 ? -1 : 1),
        ),
      );
      const strokeWidth = STROKE_WIDTHS[next]!;
      setToolAttributes((current) => ({
        ...current,
        [tool]: { ...current[tool], strokeWidth },
      }));
      setOperationMessage(
        draftMark.current
          ? `下一笔线条宽度：${strokeWidth}px。`
          : `线条宽度：${strokeWidth}px。`,
      );
    };
    canvas.addEventListener("wheel", onWheel, { passive: false });
    return () => canvas.removeEventListener("wheel", onWheel);
  }, [tool, styleEditing]);

  function pointerMove(event: React.PointerEvent<HTMLCanvasElement>) {
    if (colorPickPendingRef.current) {
      event.currentTarget.style.cursor = "crosshair";
      return;
    }
    if (!dragStart.current) {
      // 折线草稿悬停橡皮筋：无按键拖拽时也把活动端跟随游标，下一段实时
      // 预览可见；仅更新草稿（不入历史、不入导出），点击才真正加点。
      if (tool === "polyline" && draftMark.current?.tool === "polyline") {
        setDraftMark((current) =>
          current && current.tool === "polyline"
            ? { ...current, end: point(event) }
            : current,
        );
        return;
      }
      if (tool === "select") {
        const at = point(event);
        const primaryId = selectionRef.current.primary;
        const selected =
          primaryId === undefined
            ? undefined
            : state.marks.find((mark) => mark.id === primaryId);
        // 选定标记的几何手柄优先：矩形类八点缩放、直线/箭头端点。
        const handle =
          selected &&
          selectionRef.current.ids.size === 1 &&
          isBoxResizableMark(selected)
            ? rectResizeHandle(selected, at)
            : undefined;
        const endpoint =
          !handle &&
          selected &&
          selectionRef.current.ids.size === 1 &&
          isEndpointMark(selected)
            ? endpointHandle(selected, at)
            : undefined;
        // 选区四边/四角：悬停时提示对应的方向光标。
        const cropBase =
          handle || endpoint
            ? undefined
            : cropAdjustBase(
                state,
                canvasSize(event.currentTarget),
                !!captureGeometry,
              );
        const cropHandle = cropBase
          ? rectResizeHandle(cropBase, at)
          : undefined;
        event.currentTarget.style.cursor = handle
          ? `${handle}-resize`
          : endpoint
            ? "move"
            : cropHandle
              ? `${cropHandle}-resize`
              : findMark(
                    state.marks,
                    at,
                    canvasRef.current?.getContext("2d") ?? undefined,
                  ) === undefined
                ? "default"
                : "move";
      }
      return;
    }
    if (tool === "inpaint") {
      const at = point(event);
      const mode = activeToolAttributes.maskMode;
      if (mode === "brush-add" || mode === "brush-remove") {
        queueMaskBrush(at);
        return;
      }
      setInpaintDraft((current) =>
        current ? { ...current, end: at } : current,
      );
      return;
    }
    if (tool === "dupmatch") {
      const at = point(event);
      if (dupmatchDraft.current) {
        dupmatchDraft.current = { ...dupmatchDraft.current, end: at };
        scheduleDraw();
      }
      return;
    }
    if (tool === "objectEraser") {
      // 拖拽中持续扫过累积命中（仅预览），松开统一一次提交。
      const at = point(event);
      eraseObjectAt(at, objectEraserLast.current ?? at);
      objectEraserLast.current = at;
      return;
    }
    if (spotlightRegionDraft) {
      const at = point(event);
      updateSpotlightRegionDraft((current) =>
        current ? { ...current, end: at } : current,
      );
      return;
    }
    if (markDragRef.current) {
      const at = point(event);
      const start = dragStart.current;
      const drag = markDragRef.current;
      if (!start) return;
      const delta = { x: at.x - start.x, y: at.y - start.y };
      const next =
        drag.kind === "resize"
          ? resizeBoxMark(drag.origin, drag.handle, delta, event.shiftKey)
          : drag.kind === "region-resize"
            ? resizeSpotlightRegionMark(drag, delta, event.shiftKey)
            : moveEndpointMark(
                drag.origin,
                drag.endpoint,
                delta,
                event.shiftKey,
              );
      setObjectDraft({
        byId: new Map([[drag.id, next]]),
        revision: drag.revision,
      });
      return;
    }
    if (cropResizeRef.current) {
      const at = point(event);
      const start = dragStart.current;
      setCropDraft(
        resizeRectPoints(
          cropResizeRef.current.origin.start,
          cropResizeRef.current.origin.end,
          cropResizeRef.current.handle,
          { x: at.x - start.x, y: at.y - start.y },
        ),
      );
      return;
    }
    if (tool === "crop") {
      const at = point(event);
      setCropDraft((current) => (current ? { ...current, end: at } : current));
      return;
    }
    const at = point(event);
    if (tool === "select") {
      if (marqueeRef.current) {
        marqueeRef.current.end = at;
        scheduleDraw();
        return;
      }
      // 整选区平移：所有选中对象从冻结原像同步位移。
      const drag = moveDragRef.current;
      if (drag) {
        const delta = {
          x: at.x - dragStart.current.x,
          y: at.y - dragStart.current.y,
        };
        const byId = new Map<string, Mark>();
        for (const origin of drag.origins) {
          byId.set(origin.id, moveMark(origin, delta));
        }
        setObjectDraft({ byId, revision: drag.revision });
      }
      return;
    }
    setDraftMark((current) => {
      if (!current) return current;
      if (
        tool === "pen" ||
        tool === "highlighter" ||
        ((tool === "mosaic" || tool === "blur") &&
          activeToolAttributes.effectMode === "brush")
      ) {
        const points = draftPoints.current;
        const last = points[points.length - 1];
        if (last && Math.hypot(at.x - last.x, at.y - last.y) < 2) {
          return current;
        }
        points.push(at);
        return { ...current, points, end: at };
      }
      const anchor = dragStart.current ?? current.start;
      // Shift/Alt 拖拽约束：矩形/椭圆正方/正圆与中心绘制；直线/箭头 45°。
      if (tool === "rectangle" || tool === "ellipse") {
        const box = constrainBoxDrag(anchor, at, {
          shift: event.shiftKey,
          fromCenter: event.altKey,
        });
        return { ...current, start: box.start, end: box.end };
      }
      if (tool === "line" || tool === "arrow") {
        return {
          ...current,
          end: constrainLineDrag(anchor, at, event.shiftKey),
        };
      }
      return { ...current, end: at };
    });
  }

  function pointerDown(event: React.PointerEvent<HTMLCanvasElement>) {
    if (event.button !== 0 || dragStart.current) return;
    // 一次性画布取色：只采样像素，不开始任何绘制/选中/裁剪。
    if (colorPickPendingRef.current) {
      sampleCanvasColor(point(event), event.currentTarget);
      event.currentTarget.style.cursor = "";
      return;
    }
    if (textEdit) return;
    if (tool === "textSelect" || tool === "hand" || spaceHeld) return;
    const start = point(event);
    selectionDragBounds.current = event.currentTarget.getBoundingClientRect();
    dragStart.current = start;
    if (tool === "objectEraser") {
      // 一次拖动 = 一次手势：从空集合开始累积命中，松开才一次性提交。
      objectEraserHits.current = new Set();
      objectEraserLast.current = start;
      eraseObjectAt(start, undefined);
    } else if (spotlightRegionTarget !== undefined) {
      const target = state.marks.find(
        (mark) => mark.id === spotlightRegionTarget,
      );
      if (!target || target.tool !== "spotlight" || target.locked) {
        setSpotlightRegionTarget(undefined);
        setOperationMessage("目标聚光灯对象已不可用，“添加区域”已取消。");
        dragStart.current = undefined;
        return;
      }
      updateSpotlightRegionDraft({ start, end: start });
    } else if (tool === "select") {
      // 主选（单选时）的几何手柄优先命中：矩形类八点缩放、直线/箭头端点；
      // 其次选区四边/四角裁剪手柄；命中对象则替换/增减选择并开始平移，
      // 空白处进入框选草稿（含锁定对象：命中跳过，列表可解锁）。
      const primaryId = selectionRef.current.primary;
      const single =
        selectionRef.current.ids.size === 1 && primaryId !== undefined
          ? state.marks.find((mark) => mark.id === primaryId)
          : undefined;
      const region =
        single &&
        !single.locked &&
        single.tool === "spotlight" &&
        spotlightRegionTarget === undefined
          ? spotlightRegionHandle(single, start)
          : undefined;
      const handle =
        !region && single && !single.locked && isBoxResizableMark(single)
          ? rectResizeHandle(single, start)
          : undefined;
      const endpoint =
        !handle && single && !single.locked && isEndpointMark(single)
          ? endpointHandle(single, start)
          : undefined;
      if (single && region) {
        markDragRef.current = {
          kind: "region-resize",
          id: single.id,
          handle: region.handle,
          regionIndex: region.index,
          origin: single,
          revision: contentRevisionRef.current,
        };
      } else if (single && handle) {
        markDragRef.current = {
          kind: "resize",
          id: single.id,
          handle,
          origin: single,
          revision: contentRevisionRef.current,
        };
      } else if (single && endpoint) {
        markDragRef.current = {
          kind: "endpoint",
          id: single.id,
          endpoint,
          origin: single,
          revision: contentRevisionRef.current,
        };
      } else {
        const cropBase = cropAdjustBase(
          state,
          canvasSize(event.currentTarget),
          !!captureGeometry,
        );
        const cropHandle = cropBase && rectResizeHandle(cropBase, start);
        if (cropBase && cropHandle) {
          cropResizeRef.current = { handle: cropHandle, origin: cropBase };
        } else {
          const hitId = findMark(
            state.marks,
            start,
            canvasRef.current?.getContext("2d") ?? undefined,
          );
          // #235 聚光灯 Alt+点击区域：删除该孔洞（最后一个区域随对象一起删），
          // 一次历史可撤销。
          if (hitId !== undefined && event.altKey) {
            const hit = state.marks.find((mark) => mark.id === hitId);
            if (hit?.tool === "spotlight" && !hit.locked) {
              const index = spotlightRegionAt(hit, start);
              if (index >= 0) {
                const regions = spotlightRegionsOf(hit);
                const next = withSpotlightRegions(
                  hit,
                  regions.filter((_, at) => at !== index),
                );
                dragStart.current = undefined;
                if (!next) {
                  if (
                    !commit({
                      ...state,
                      marks: deleteMarkIds(state.marks, new Set([hit.id])),
                    })
                  )
                    return;
                  setOperationMessage(
                    "已删除最后一个区域并移除聚光灯对象；可撤销。",
                  );
                } else {
                  if (
                    !commit({
                      ...state,
                      marks: state.marks.map((mark) =>
                        mark.id === hit.id ? next : mark,
                      ),
                    })
                  )
                    return;
                  setOperationMessage(
                    `已删除区域（剩 ${next.regions!.length} 个）；可撤销。`,
                  );
                }
                return;
              }
            }
          }
          if (hitId === undefined) {
            // 空白按下：进入框选；未拖动松开时清空选择（与旧单击空白一致）。
            marqueeRef.current = { start, end: start };
          } else if (event.shiftKey || event.ctrlKey || event.metaKey) {
            toggleSelection(hitId);
          } else {
            const alreadySelected = selectionRef.current.ids.has(hitId);
            if (!alreadySelected) select(hitId);
            // 选区就绪后尝试开始整选区平移（含锁定时拒绝并提示）。
            const ids = alreadySelected
              ? selectionRef.current.ids
              : expandGroupSelection(state.marks, new Set([hitId]));
            const origins = state.marks.filter((mark) => ids.has(mark.id));
            if (origins.some((mark) => mark.locked)) {
              setOperationMessage(
                "选区包含已锁定对象，不能拖动；可在对象列表解锁。",
              );
            } else {
              moveDragRef.current = {
                ids: [...ids],
                origins,
                hitId,
                revision: contentRevisionRef.current,
              };
            }
          }
        }
      }
    } else if (tool === "inpaint") {
      const mode = activeToolAttributes.maskMode;
      // 重新绘制即作废上一轮预览与在途请求；新掩膜在松开/涂抹时确定。
      if (inpaintPreview || inpaintBusy) {
        cancelInpaintPending("已放弃上一次修补，请重新编辑掩膜并预览。");
      } else if (mode === "rect-add" || mode === "rect-remove") {
        setInpaintDraft(undefined);
        setInpaintSelection(undefined);
      }
      if (mode === "brush-add" || mode === "brush-remove") {
        inpaintBrushPoints.current = [];
        queueMaskBrush(start);
      } else {
        setInpaintDraft({ start, end: start });
      }
    } else if (tool === "dupmatch") {
      if (dupmatchCandidates || dupmatchBusy) {
        cancelDupmatchPending("已放弃上一次重复匹配，请重新框选。");
      } else {
        dupmatchDraft.current = undefined;
        dupmatchSelectionRef.current = undefined;
        setDupmatchSelection(undefined);
      }
      dupmatchDraft.current = { start, end: start };
      scheduleDraw();
    } else if (tool === "crop") {
      setCropDraft({ start, end: start });
    } else if (tool === "polyline") {
      // 折线：单击加点，草稿保持开放；拖拽段在松开时补点；双击/Enter
      // 提交，Esc 或切换工具放弃（cancelDrawing）。
      const existing = draftMark.current;
      const points =
        existing?.tool === "polyline" && existing.points
          ? [...existing.points]
          : [start];
      const last = points[points.length - 1]!;
      if (Math.hypot(start.x - last.x, start.y - last.y) >= 3) {
        points.push(start);
      }
      setDraftMark({
        id: existing?.id ?? nextMarkId(),
        tool,
        start: points[0]!,
        end: start,
        points,
        style: existing?.style ?? currentStyle(),
      });
    } else {
      draftPoints.current = [start];
      setDraftMark({
        id: nextMarkId(),
        tool,
        start,
        end: start,
        ...(tool === "pen" ||
        tool === "highlighter" ||
        ((tool === "mosaic" || tool === "blur") &&
          activeToolAttributes.effectMode === "brush")
          ? { points: draftPoints.current }
          : {}),
        style: currentStyle(),
        ...(tool === "magnifier"
          ? { zoom: activeToolAttributes.magnifierZoom }
          : {}),
        ...(["text", "callout"].includes(tool)
          ? { text: annotationText.trim() || "文本" }
          : {}),
        ...(tool === "numbering"
          ? {
              ordinal: nextNumberOrdinal(
                state.marks,
                activeToolAttributes.numberStart,
              ),
            }
          : {}),
      });
    }
    if (typeof event.currentTarget.setPointerCapture === "function") {
      event.currentTarget.setPointerCapture(event.pointerId);
    }
  }

  function pointerUp(event: React.PointerEvent<HTMLCanvasElement>) {
    if (!dragStart.current) return;
    if (tool === "textSelect" || tool === "hand" || spaceHeld) return;
    const end = point(event);
    const start = dragStart.current;
    const draft = draftMark.current;
    dragStart.current = undefined;
    setObjectDraft(undefined);
    setDraftMark(undefined);
    // 裁剪草稿无论是否提交都结束：短拖/单击不留 0 尺寸草稿裁空画布。
    if (tool === "crop") setCropDraft(undefined);
    if (tool === "objectEraser") {
      // 整次手势一次撤销：所有命中对象在同一条历史中删除；底图/修补/
      // 裁剪不受影响。
      const hits = objectEraserHits.current;
      objectEraserHits.current = undefined;
      objectEraserLast.current = undefined;
      if (hits?.size) {
        if (!commit({ ...state, marks: deleteMarkIds(state.marks, hits) }))
          return;
        setOperationMessage(
          `已擦除 ${hits.size} 个对象；整次拖动一次撤销，底图与修补不受影响。`,
        );
      }
      return;
    }
    if (spotlightRegionDraft) {
      const regionDraft = spotlightRegionDraft;
      updateSpotlightRegionDraft(undefined);
      const targetId = spotlightRegionTarget;
      setSpotlightRegionTarget(undefined);
      const region = normalizedRect({ start: regionDraft.start, end });
      const target = state.marks.find((mark) => mark.id === targetId);
      if (
        target &&
        target.tool === "spotlight" &&
        !target.locked &&
        region.width >= MIN_DRAW_DISTANCE &&
        region.height >= MIN_DRAW_DISTANCE
      ) {
        const regions = spotlightRegionsOf(target);
        if (regions.length >= SPOTLIGHT_MAX_REGIONS) {
          setOperationMessage(
            `聚光灯区域已达上限 ${SPOTLIGHT_MAX_REGIONS} 个，未添加。`,
          );
          return;
        }
        const next = withSpotlightRegions(target, [...regions, region]);
        if (
          next &&
          commit({
            ...state,
            marks: state.marks.map((mark) =>
              mark.id === target.id ? next : mark,
            ),
          })
        ) {
          setOperationMessage(
            `已添加区域（共 ${next.regions!.length} 个）；可用撤销恢复。`,
          );
        }
      }
      return;
    }
    // 空白框选：完整包含的未锁定对象入选择；未拖动（单击空白）清空选择。
    if (tool === "select" && marqueeRef.current) {
      const marquee = marqueeRef.current;
      marqueeRef.current = undefined;
      const distance = Math.hypot(
        end.x - marquee.start.x,
        end.y - marquee.start.y,
      );
      if (distance >= 4) {
        const rect = normalizedRect({ start: marquee.start, end: end });
        const ids = expandGroupSelection(
          state.marks,
          marksContainedInRect(
            state.marks,
            rect,
            canvasRef.current?.getContext("2d") ?? undefined,
          ),
        );
        if (ids.size > 0) {
          const topmost = state.marks.findLast((mark) => ids.has(mark.id))?.id;
          setObjectSelection({ ids, primary: topmost });
          setOperationMessage(
            `已选择 ${ids.size} 个对象（框选完整包含）；可拖动、对齐或成组。`,
          );
        } else {
          setObjectSelection(NO_SELECTION);
        }
      } else {
        setObjectSelection(NO_SELECTION);
      }
      return;
    }
    if (markDragRef.current) {
      const drag = markDragRef.current;
      markDragRef.current = undefined;
      setObjectDraft(undefined);
      if (drag.revision !== contentRevisionRef.current) {
        setOperationMessage("内容已变化，本次手柄拖动已放弃；请重试。");
        return;
      }
      const delta = { x: end.x - start.x, y: end.y - start.y };
      const next =
        drag.kind === "resize"
          ? resizeBoxMark(drag.origin, drag.handle, delta, event.shiftKey)
          : drag.kind === "region-resize"
            ? resizeSpotlightRegionMark(drag, delta, event.shiftKey)
            : moveEndpointMark(
                drag.origin,
                drag.endpoint,
                delta,
                event.shiftKey,
              );
      // 几何未变（单击手柄）不入历史：拖拽才产生一条缩放/端点编辑记录；
      // 聚光灯区域缩放可能不改变联合包围盒，按区域列表比较。
      const moved =
        drag.kind === "region-resize"
          ? JSON.stringify(next.regions) !==
            JSON.stringify(
              drag.origin.regions ?? spotlightRegionsOf(drag.origin),
            )
          : Math.hypot(
              next.start.x - drag.origin.start.x,
              next.start.y - drag.origin.start.y,
            ) >= 1 ||
            Math.hypot(
              next.end.x - drag.origin.end.x,
              next.end.y - drag.origin.end.y,
            ) >= 1;
      if (moved) {
        if (
          !commit({
            ...state,
            marks: state.marks.map((mark) =>
              mark.id === drag.id ? next : mark,
            ),
          })
        )
          return;
      }
      return;
    }
    if (cropResizeRef.current) {
      const { handle, origin } = cropResizeRef.current;
      cropResizeRef.current = undefined;
      setCropDraft(undefined);
      const canvas = canvasRef.current;
      const size = canvas
        ? canvasSize(canvas)
        : { width: Number.POSITIVE_INFINITY, height: Number.POSITIVE_INFINITY };
      const next = clampRect(
        normalizedRect(
          resizeRectPoints(origin.start, origin.end, handle, {
            x: end.x - start.x,
            y: end.y - start.y,
          }),
        ),
        size,
      );
      const before = clampRect(normalizedRect(origin), size);
      // 尺寸真正变化才入历史：点击手柄不产生空裁剪步骤。
      if (
        Math.abs(next.width - before.width) >= 1 ||
        Math.abs(next.height - before.height) >= 1
      ) {
        if (
          !commit({
            ...state,
            crop: {
              start: { x: next.x, y: next.y },
              end: { x: next.x + next.width, y: next.y + next.height },
            },
          })
        )
          return;
      }
      return;
    }
    if (tool === "select") {
      const drag = moveDragRef.current;
      moveDragRef.current = undefined;
      if (!drag) return;
      const delta = { x: end.x - start.x, y: end.y - start.y };
      if (Math.hypot(delta.x, delta.y) < 2) {
        select(drag.hitId);
        return;
      }
      if (drag.revision !== contentRevisionRef.current) {
        setOperationMessage("内容已变化，本次拖动已放弃；请重试。");
        return;
      }
      if (
        !commit({
          ...state,
          marks: moveMarkIds(state.marks, new Set(drag.ids), delta),
        })
      )
        return;
      return;
    }
    if (tool === "polyline") {
      // 折线草稿保持开放：拖拽段（≥8px）在松开时补点，单击交给下一次
      // pointerDown 加点；提交只发生在双击/Enter（恰好一条历史）。
      if (draft?.tool === "polyline") {
        const points = [...(draft.points ?? [])];
        const last = points[points.length - 1];
        if (
          Math.hypot(end.x - start.x, end.y - start.y) >= MIN_DRAW_DISTANCE &&
          (!last || Math.hypot(end.x - last.x, end.y - last.y) >= 3)
        ) {
          points.push(end);
          setDraftMark({ ...draft, points, end });
        } else {
          setDraftMark(draft);
        }
      }
      return;
    }
    if (
      tool === "pen" ||
      tool === "highlighter" ||
      ((tool === "mosaic" || tool === "blur") &&
        activeToolAttributes.effectMode === "brush")
    ) {
      setDraftMark(undefined);
      const sampled = draft?.points ?? [start];
      const last = sampled[sampled.length - 1];
      const points =
        last?.x === end.x && last.y === end.y
          ? [...sampled]
          : [...sampled, end];
      const length = points.reduce((total, at, index) => {
        const previous = points[index - 1];
        return previous
          ? total + Math.hypot(at.x - previous.x, at.y - previous.y)
          : total;
      }, 0);
      if (length >= 4 || tool === "mosaic" || tool === "blur") {
        if (
          !commit({
            ...state,
            marks: [
              ...state.marks,
              {
                id: draft?.id ?? nextMarkId(),
                tool,
                start: points[0] ?? start,
                end: points[points.length - 1] ?? end,
                points,
                style: draft?.style ?? currentStyle(),
              },
            ],
          })
        )
          return;
      }
      return;
    }
    if (tool === "numbering") {
      // 序号支持单击放置，不要求拖拽距离。
      if (
        !commit({
          ...state,
          marks: [
            ...state.marks,
            {
              id: draft?.id ?? nextMarkId(),
              tool,
              start,
              end,
              ordinal:
                draft?.ordinal ??
                nextNumberOrdinal(
                  state.marks,
                  activeToolAttributes.numberStart,
                ),
              style: draft?.style ?? currentStyle(),
            },
          ],
        })
      )
        return;
      return;
    }
    if (tool === "inpaint") {
      setInpaintDraft(undefined);
      const mode = activeToolAttributes.maskMode;
      if (mode === "brush-add" || mode === "brush-remove") {
        queueMaskBrush(end);
        flushMaskBrush();
        inpaintBrushPoints.current = [];
        if (pendingMaskFrame.current !== undefined)
          cancelAnimationFrame(pendingMaskFrame.current);
        pendingMaskFrame.current = undefined;
        return;
      }
      if (Math.hypot(end.x - start.x, end.y - start.y) >= MIN_DRAW_DISTANCE) {
        const selection = {
          start: {
            x: Math.min(start.x, end.x),
            y: Math.min(start.y, end.y),
          },
          end: {
            x: Math.max(start.x, end.x),
            y: Math.max(start.y, end.y),
          },
        };
        setInpaintSelection(selection);
        setInpaintCompare("after");
        applySelectionToMask(selection, mode !== "rect-remove");
      }
      return;
    }
    if (tool === "dupmatch") {
      const draft = dupmatchDraft.current;
      dupmatchDraft.current = undefined;
      scheduleDraw();
      if (
        draft &&
        Math.hypot(end.x - start.x, end.y - start.y) >= MIN_DRAW_DISTANCE
      ) {
        const selection = {
          start: {
            x: Math.min(start.x, end.x),
            y: Math.min(start.y, end.y),
          },
          end: {
            x: Math.max(start.x, end.x),
            y: Math.max(start.y, end.y),
          },
        };
        dupmatchSelectionRef.current = selection;
        setDupmatchSelection(selection);
        setOperationMessage(
          "已框选重复内容源区域；点击“查找重复”搜索同尺度平移副本。",
        );
      }
      return;
    }
    if (
      tool === "text" ||
      Math.hypot(end.x - start.x, end.y - start.y) >= MIN_DRAW_DISTANCE
    ) {
      const spotlightBox =
        tool === "spotlight" ? normalizedRect({ start, end }) : undefined;
      if (
        spotlightBox &&
        (spotlightBox.width < MIN_DRAW_DISTANCE ||
          spotlightBox.height < MIN_DRAW_DISTANCE)
      )
        return;
      // 放大镜同理：纯水平/垂直拖拽（欧氏达标但一维为 0）不产生不可见
      // 对象与空历史（H2）。草稿与提交同一门槛。
      if (tool === "magnifier") {
        const lens = normalizedRect({ start, end });
        if (lens.width < MIN_DRAW_DISTANCE || lens.height < MIN_DRAW_DISTANCE)
          return;
      }
      let createdId: string | undefined;
      if (tool === "crop") {
        if (!commit({ ...state, crop: { start, end } })) return;
      } else if (
        tool === "exclude" &&
        state.marks.filter(isExclusionMark).length >= MAX_EXCLUSION_MARKS
      ) {
        // 与桥上限一致：达到上限提示并拒绝新增，保留既有标记与修订。
        setOperationMessage(
          `屏蔽区已达上限 ${MAX_EXCLUSION_MARKS} 个；请先删除或清空后再新增。`,
        );
      } else {
        // 提交与预览同源约束：Shift 正方/正圆/45°，Alt 中心绘制。
        const box =
          tool === "rectangle" || tool === "ellipse"
            ? constrainBoxDrag(start, end, {
                shift: event.shiftKey,
                fromCenter: event.altKey,
              })
            : undefined;
        const lineEnd =
          tool === "line" || tool === "arrow"
            ? constrainLineDrag(start, end, event.shiftKey)
            : end;
        createdId = draft?.id ?? nextMarkId();
        // 聚光灯首个区域 = 有效二维矩形；放大镜沿用手势冻结的倍率。
        if (
          !commit({
            ...state,
            marks: [
              ...state.marks,
              {
                id: createdId,
                tool,
                start: spotlightBox
                  ? { x: spotlightBox.x, y: spotlightBox.y }
                  : box
                    ? box.start
                    : start,
                end: spotlightBox
                  ? {
                      x: spotlightBox.x + spotlightBox.width,
                      y: spotlightBox.y + spotlightBox.height,
                    }
                  : box
                    ? box.end
                    : lineEnd,
                style: draft?.style ?? currentStyle(),
                ...(spotlightBox ? { regions: [spotlightBox] } : {}),
                ...(tool === "magnifier"
                  ? { zoom: draft?.zoom ?? activeToolAttributes.magnifierZoom }
                  : {}),
                ...(tool === "text"
                  ? { textWidth: Math.abs(end.x - start.x) }
                  : {}),
                ...(["text", "callout"].includes(tool)
                  ? { text: draft?.text ?? (annotationText.trim() || "文本") }
                  : {}),
              },
            ],
          })
        )
          return;
      }
      if (tool === "text" || tool === "callout") {
        setTool("select");
        if (createdId) select(createdId);
      }
    }
  }

  /** 提交折线草稿：去重相邻点、要求有效长度，恰好一条历史；不足两点
   * 视为误触放弃，不产生空历史。双击与 Enter 共用本入口。 */
  function commitPolylineDraft() {
    const draft = draftMark.current;
    if (draft?.tool !== "polyline") return;
    const deduped: Point[] = [];
    for (const at of draft.points ?? []) {
      const last = deduped[deduped.length - 1];
      if (!last || Math.hypot(at.x - last.x, at.y - last.y) >= 3) {
        deduped.push(at);
      }
    }
    const length = deduped.reduce((total, at, index) => {
      const previous = deduped[index - 1];
      return previous
        ? total + Math.hypot(at.x - previous.x, at.y - previous.y)
        : total;
    }, 0);
    if (deduped.length >= 2 && length >= 4) {
      if (
        !commit({
          ...state,
          marks: [
            ...state.marks,
            {
              id: draft.id,
              tool: "polyline",
              start: deduped[0]!,
              end: deduped[deduped.length - 1]!,
              points: deduped,
              style: draft.style,
            },
          ],
        })
      )
        return;
    } else {
      setOperationMessage("折线至少需要两个不同位置的点；已放弃本次草稿。");
    }
    setDraftMark(undefined);
  }

  /** 操作开始时冻结的导出上下文；await 归来后必须逐项一致才允许发送。 */
  interface FrozenExportContext {
    readonly key: string;
    readonly sessionId: string | undefined;
    readonly contentVersion: number;
    readonly image: HTMLImageElement | undefined;
    readonly output: {
      readonly format: OutputImageFormat;
      readonly quality: number;
    };
  }

  function freezeExportContext(): FrozenExportContext {
    return {
      key: currentKeyRef.current,
      sessionId: sessionIdRef.current,
      contentVersion: contentRevisionRef.current,
      image: imageRef.current,
      output: {
        format: outputFormatRef.current,
        quality: jpegQualityRef.current,
      },
    };
  }

  function exportContextUnchanged(frozen: FrozenExportContext): boolean {
    // 全部读 ref：上传期间用户切换格式/质量或几何编辑（含无会话的文件
    // 模式）都推进内容版本，旧像素不得贴上新设置发出。
    return (
      frozen.key === currentKeyRef.current &&
      frozen.sessionId === sessionIdRef.current &&
      frozen.image === imageRef.current &&
      frozen.contentVersion === contentRevisionRef.current &&
      frozen.output.format === outputFormatRef.current &&
      frozen.output.quality === jpegQualityRef.current
    );
  }

  /** 多出口共用的安全导出：上传归来后校验未变；变化则不发送。
   * bake=true 时把排除区写入不透明白色像素（识别输入与显式屏蔽副本）。 */
  async function exportFinalImageIfUnchanged(
    bakeExclusions = false,
    forcePng = false,
  ): Promise<
    | {
        resourceUri: string;
        sessionId?: string;
        revision?: number;
        mediaType: OutputImageFormat;
        byteLength: number;
      }
    | undefined
  > {
    if (textEdit) {
      setOperationMessage("请先完成或取消文字编辑。");
      return undefined;
    }
    const frozen = freezeExportContext();
    const blob = await exportCanvas(frozen.image, canvasRef.current, state, {
      format: forcePng ? "image/png" : outputFormat,
      quality: jpegQuality,
      inpaint: { patches: appliedInpaintPatches() },
      bakeExclusions,
      imageAssets: importAssetsRef.current,
    });
    const resourceUri = await uploadAnnotatedImage(blob);
    if (textEditingRef.current || !exportContextUnchanged(frozen)) {
      setOperationMessage(
        "导出期间内容或会话已变化，本次未发送；请在新画面重试。",
      );
      return undefined;
    }
    return {
      resourceUri,
      sessionId: frozen.sessionId,
      revision: frozen.sessionId ? frozen.contentVersion : undefined,
      mediaType: blob.type === "image/jpeg" ? "image/jpeg" : "image/png",
      byteLength: blob.size,
    };
  }

  async function copyAnnotatedImage(masked = false) {
    if (!canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage(
        masked ? "正在生成并复制屏蔽副本……" : "正在生成并复制标注图片……",
      );
      const exported = await exportFinalImageIfUnchanged(masked);
      if (!exported) return;
      const copied = exported.sessionId
        ? await actions.run({
            type: commands.copyScreenshotImage,
            resourceUri: exported.resourceUri,
            sessionId: exported.sessionId,
            revision: exported.revision,
          })
        : await actions.run({
            type: commands.copyAnnotatedImage,
            resourceUri: exported.resourceUri,
          });
      setOperationMessage(
        copied
          ? exported.sessionId
            ? masked
              ? `已复制屏蔽副本（${formatLabel(exported)}）：屏蔽区为真实白色像素；原图与普通副本不受影响。`
              : `已复制截图副本（${formatLabel(exported)}）；屏蔽区不会写入本副本，仅识别输入与显式屏蔽副本使用白色覆盖。`
            : masked
              ? `已复制屏蔽副本（${formatLabel(exported)}）：屏蔽区为真实白色像素；原图与普通副本不受影响。`
              : `已复制标注图片副本（${formatLabel(exported)}）。识别结果保持不变。`
          : "复制未完成，请查看页面提示后重试。",
      );
    } catch {
      setOperationMessage("无法生成或传递标注图片，请重试。");
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  async function saveAnnotatedImage(masked = false) {
    if (!canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage(
        masked
          ? "正在生成屏蔽副本并打开系统保存窗口……"
          : "正在生成标注图片并打开系统保存窗口……",
      );
      const exported = await exportFinalImageIfUnchanged(masked);
      if (!exported) return;
      const saved = exported.sessionId
        ? await actions.run({
            type: commands.saveScreenshotImage,
            resourceUri: exported.resourceUri,
            sessionId: exported.sessionId,
            revision: exported.revision,
          })
        : await actions.run({
            type: commands.saveAnnotatedImage,
            resourceUri: exported.resourceUri,
          });
      setOperationMessage(
        saved
          ? exported.sessionId
            ? masked
              ? `已保存屏蔽副本（${formatLabel(exported)}）：屏蔽区为真实白色像素；原图与普通副本不受影响。`
              : `已保存截图副本（${formatLabel(exported)}）；屏蔽区不会写入本副本，仅识别输入与显式屏蔽副本使用白色覆盖。`
            : masked
              ? `已保存屏蔽副本（${formatLabel(exported)}）：屏蔽区为真实白色像素；原图与普通副本不受影响。`
              : `已保存标注图片副本（${formatLabel(exported)}）。识别结果保持不变。`
          : "保存未完成，请查看页面提示后重试。",
      );
    } catch {
      setOperationMessage("无法生成或传递标注图片，请重试。");
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  /** #233/#238 共享：已应用补丁的预算前置 + 逐块 PNG 编码。失败以既有
   * 文案抛出（保存工程与完成贴图同一套边界）。 */
  async function encodeAppliedProjectPatches(): Promise<
    {
      id: number;
      x: number;
      y: number;
      width: number;
      height: number;
      png: string;
    }[]
  > {
    const applied = appliedInpaintPatches();
    // entry 总预算前置（encode/upload/picker 前）：manifest+source+唯一
    // 引用资产+补丁 ≤128；共享 asset 引用只计一次（与宿主同界）。
    const referencedAssetCount = countReferencedAssets(state.marks);
    const patchBudget = patchEntryBudget(referencedAssetCount);
    if (applied.length > patchBudget) {
      throw new Error(
        `工程包含 ${referencedAssetCount} 张嵌入图片和 ${applied.length} 块修补，合计最多 ${patchBudget} 块修补，请减少图片对象或修补后再保存。`,
      );
    }
    const encoded: {
      id: number;
      x: number;
      y: number;
      width: number;
      height: number;
      png: string;
    }[] = [];
    let totalPixels = 0;
    let totalBytes = 0;
    for (const { rect, canvas: patchCanvas } of applied) {
      if (encoded.length >= patchBudget) {
        throw new Error(
          `工程包含 ${referencedAssetCount} 张嵌入图片，修补最多 ${patchBudget} 块，请减少图片对象或修补后再保存。`,
        );
      }
      const pixels = rect.width * rect.height;
      if (pixels > PROJECT_PATCH_MAX_PIXELS) {
        throw new Error("单块修补超过 1MP 像素上限，无法保存为工程。");
      }
      totalPixels += pixels;
      if (totalPixels > PROJECT_PATCH_TOTAL_PIXELS) {
        throw new Error("修补总像素超过 8MP 上限，无法保存为工程。");
      }
      const blob = await new Promise<Blob | null>((resolve) =>
        patchCanvas.toBlob(resolve, "image/png"),
      );
      if (!blob) throw new Error("工程补丁编码失败，请重试。");
      totalBytes += blob.size;
      if (totalBytes > PROJECT_PATCH_TOTAL_BYTES) {
        throw new Error("修补编码总量超过 64MiB 上限，无法保存为工程。");
      }
      encoded.push({
        id: rect.id,
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
        png: arrayBufferToBase64(await blob.arrayBuffer()),
      });
    }
    return encoded;
  }

  /** #233/#238 共享：组装 /__project 贡献（canvas+state+补丁）并校验状态预算。 */
  async function buildProjectContributionPayload(): Promise<
    ReturnType<typeof buildContributionState> & {
      patches: {
        id: number;
        x: number;
        y: number;
        width: number;
        height: number;
        png: string;
      }[];
    }
  > {
    const canvas = canvasRef.current;
    if (!canvas) throw new Error("画布尚未就绪，请重试。");
    const payload = {
      ...buildContributionState(canvasSize(canvas), state),
      patches: await encodeAppliedProjectPatches(),
    };
    const stateBytes = contributionStateBytes(payload);
    if (stateBytes > PROJECT_STATE_BUDGET_BYTES) {
      throw new Error(
        "工程状态超出 256KiB 上限（对象过多），无法保存；可拆分后重试。",
      );
    }
    return payload;
  }

  /** #233 保存工程：采集逻辑 canvas+可编辑 state+已应用补丁 PNG，经
   * /__project 窄上传贡献后发送保存命令；宿主 picker 授权并原子写，
   * 失败/取消保留旧文件与当前画布。 */
  async function saveCurrentProject() {
    if (!session || exportInProgressRef.current || textEdit) return;
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      return;
    }
    exportInProgressRef.current = true;
    setIsExporting(true);
    const frozenSession = session.sessionId;
    const frozenRevision = contentRevisionRef.current;
    try {
      setOperationMessage("正在采集工程内容……");
      const payload = await buildProjectContributionPayload();
      setOperationMessage("正在上传工程贡献……");
      const contributeId = await uploadProjectContribution(payload);
      if (
        sessionIdRef.current !== frozenSession ||
        contentRevisionRef.current !== frozenRevision
      ) {
        setOperationMessage("上传期间会话已变化，本次未保存；请在新画面重试。");
        return;
      }
      const saved = await actions.run({
        type: projectSaveCommand,
        contributeId,
        sessionId: frozenSession,
        revision: frozenRevision,
      });
      setOperationMessage(
        saved
          ? "已保存工程（.vibp）：包含原图、对象/样式与已应用修补；与另存图片互不影响。"
          : "工程未保存：内容已变化、会话失效或格式/预算不符，请查看页面提示后重试。",
      );
    } catch (error) {
      setOperationMessage(
        error instanceof Error ? error.message : "工程保存失败，请重试。",
      );
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  /** #238 完成贴图编辑：/__project 贡献（工程可恢复）+ /__annotation 最终
   * 合成 PNG（换装显示）一次上传，宿主原子写入 owned 快照后才回 ack；
   * 会话已变化/超限时保留编辑现场可重试。 */
  const pinSavePromiseRef = useRef<Promise<boolean> | undefined>(undefined);
  const pinSaveRef = useRef<() => Promise<boolean>>(undefined);
  pinSaveRef.current = applyPinSnapshot;
  useEffect(() => {
    if (!pinSnapshotApply) return;
    const transport = ChromeWebViewTransport.fromWindow(window);
    return transport?.subscribe((value) => {
      if (typeof value !== "object" || value === null) return;
      const request = value as {
        version?: unknown;
        kind?: unknown;
        type?: unknown;
        payload?: { sessionId?: unknown };
      };
      if (
        request.version === BRIDGE_VERSION &&
        request.kind === "request" &&
        request.type === "pinEdit.requestSnapshot" &&
        request.payload?.sessionId === sessionIdRef.current
      )
        void pinSaveRef.current?.();
    });
  }, [pinSnapshotApply]);

  function applyPinSnapshot(): Promise<boolean> {
    if (pinSavePromiseRef.current) return pinSavePromiseRef.current;
    const sourceSession = sessionIdRef.current;
    const promise = applyPinSnapshotCore()
      .then((ok) => {
        ChromeWebViewTransport.fromWindow(window)?.postMessage({
          version: BRIDGE_VERSION,
          kind: "event",
          id: crypto.randomUUID(),
          type: "pinEdit.snapshotResult",
          payload: { sessionId: sourceSession, ok },
        });
        return ok;
      })
      .finally(() => {
        pinSavePromiseRef.current = undefined;
      });
    pinSavePromiseRef.current = promise;
    return promise;
  }

  async function applyPinSnapshotCore(): Promise<boolean> {
    if (
      !session ||
      !pinSnapshotApply ||
      exportInProgressRef.current ||
      textEdit
    )
      return false;
    if (!initialExclusionsReady()) {
      setOperationMessage("正在恢复原有屏蔽区，请稍后重试完成贴图。");
      return false;
    }
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      return false;
    }
    exportInProgressRef.current = true;
    setIsExporting(true);
    const frozenSession = session.sessionId;
    const frozenRevision = contentRevisionRef.current;
    try {
      const boxes = hostExclusionBoxes();
      if (!boxes) {
        setOperationMessage(
          "排除框数量超过宿主上限（64）：请减少屏蔽区或放大镜后重试完成贴图。",
        );
        return false;
      }
      setOperationMessage("正在采集贴图内容……");
      const payload = await buildProjectContributionPayload();
      setOperationMessage("正在上传贴图内容……");
      const contributeId = await uploadProjectContribution(payload);
      // 最终合成 PNG：与贡献同一次冻结上下文，导出后逐项复核。
      const exported = await exportFinalImageIfUnchanged(false, true);
      if (
        !exported?.sessionId ||
        sessionIdRef.current !== frozenSession ||
        contentRevisionRef.current !== frozenRevision
      ) {
        setOperationMessage("上传期间内容或会话已变化，本次未完成；请重试。");
        return false;
      }
      const applied = await actions.run({
        type: "pinEdit.applySnapshot",
        excludeBoxes: boxes,
        contributeId,
        resourceUri: exported.resourceUri,
        sessionId: frozenSession,
        revision: frozenRevision,
      });
      setOperationMessage(
        applied
          ? "已完成贴图编辑；贴图已更新。"
          : "完成未生效：会话已失效或格式/预算不符，请查看提示后重试。",
      );
      return applied;
    } catch (error) {
      setOperationMessage(
        error instanceof Error ? error.message : "完成贴图失败，请重试。",
      );
      return false;
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  /** 识别/贴图上传的宿主排除框：基础屏蔽 + 放大镜采样泄露区（同一
   * 归一化映射，复用既有 excludeBoxes 契约，无新 IPC）。基础屏蔽数+泄露
   * 区数超过桥上限（与 MAX_EXCLUSION_MARKS/codec 同界）时返回 undefined，
   * 调用方必须明确拒绝该操作：不截断、不静默丢屏蔽、不放宽预算。 */
  function hostExclusionBoxes():
    { x: number; y: number; width: number; height: number }[] | undefined {
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas) return [];
    const size = canvasSize(canvas);
    const base = exclusionNormalizedRects(image, state, size);
    const leak = displayRectsToNormalizedRects(
      magnifierExclusionLeakRects(state, image, size),
      image,
      state,
      size,
    );
    if (base.length + leak.length > MAX_EXCLUSION_MARKS) return undefined;
    return [...base, ...leak].map((rect) => ({
      x: rect.x,
      y: rect.y,
      width: rect.width,
      height: rect.height,
    }));
  }

  async function pinCurrentImage() {
    if (!session || !canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      // 预算检查在上传前：超限时明确拒绝且不消耗上传租约/配额；排除框
      // 与随后导出使用同一冻结会话/修订，一次计算，await 后不换源重算。
      const boxes = hostExclusionBoxes();
      if (!boxes) {
        setOperationMessage(
          "排除框数量超过宿主上限（64）：请减少屏蔽区或放大镜后重试贴图。",
        );
        return;
      }
      // 贴图显示/复制/保存使用未烘焙遮罩的原图像素（AC5）；宿主在取字时
      // 按冻结的 excludeBoxes 生成临时遮罩输入，并沿用行过滤拒绝被屏文字。
      const exported = await exportFinalImageIfUnchanged(false);
      if (!exported?.sessionId) return;
      const pinned = await actions.run({
        type: commands.pinScreenshotImage,
        resourceUri: exported.resourceUri,
        sessionId: exported.sessionId,
        revision: exported.revision,
        // 归一化排除矩形：宿主对贴图文字层执行同一正面积相交丢弃策略，
        // 保持与原位行过滠除跨边界行以外的一致。
        excludeBoxes: boxes,
      });
      setOperationMessage(
        pinned ? "已创建桌面贴图。" : "贴图未创建，请查看页面提示。",
      );
    } catch {
      setOperationMessage("无法创建贴图，请重试。");
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  /** 整图屏蔽拒绝：没有可识别内容时不提交，原图与屏蔽草稿保持不变。 */
  function refuseIfFullyMasked(kind: "recognition" | "textLayer"): boolean {
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas) return false;
    if (!exclusionCoversOutput(image, state, canvasSize(canvas))) {
      return false;
    }
    setOperationMessage(
      kind === "recognition"
        ? "整张图都在屏蔽区内：没有可识别内容，已取消本次识别；原图与屏蔽区保持不变，可撤销后重试。"
        : "整张图都在屏蔽区内：没有可取文字，已取消文字层准备；可撤销后重试。",
    );
    return true;
  }

  async function recognizeCurrentImage(modeId?: string) {
    if (!session || !canRecognize || exportInProgressRef.current) return;
    if (refuseIfFullyMasked("recognition")) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage("正在提交显式识别……");
      // 预算检查在上传前：超限时明确拒绝且不消耗上传租约/配额；排除框
      // 与随后导出使用同一冻结会话/修订，一次计算，await 后不换源重算。
      const boxes = hostExclusionBoxes();
      if (!boxes) {
        setOperationMessage(
          "排除框数量超过宿主上限（64）：请减少屏蔽区或放大镜后重试识别。",
        );
        return;
      }
      // 上传未烘焙遮罩的普通最终像素 + 归一化排除框：宿主冻结普通显示
      // 基准并自行生成白色遮罩 OCR 输入；普通基准绝不采用遮罩像素。
      const exported = await exportFinalImageIfUnchanged(false);
      if (!exported) return;
      const started = await actions.run({
        type: commands.recognizeScreenshotImage,
        ...(modeId === undefined ? {} : { modeId }),
        resourceUri: exported.resourceUri,
        sessionId: exported.sessionId,
        revision: exported.revision,
        excludeBoxes: boxes,
      });
      if (started) {
        // 宿主完成显式识别交接（scene 会话转移/主窗口导航）；结果由
        // 识别承载面展示，本编辑器不再常驻第二幅结果画面。
        onRecognitionSubmitted?.();
      }
      setOperationMessage(
        started
          ? `已提交识别当前图（${formatLabel(exported)}）；结果将在识别页面显示。`
          : "识别未开始：内容已更新或会话已失效，请重试。",
      );
    } catch {
      setOperationMessage("无法生成或传递识别图片，请重试。");
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  const fontMark =
    editingMarkCandidate && isFontMark(editingMarkCandidate)
      ? editingMarkCandidate
      : undefined;
  const activeFontTool =
    fontMark?.tool ??
    (tool === "text" || tool === "numbering" || tool === "callout"
      ? tool
      : undefined);
  const fontTool = activeFontTool !== undefined;
  const activeTextStyle: AnnotationStyle = fontMark?.style ?? {
    ...activeToolAttributes.textStyle,
    fontSize: activeToolAttributes.fontSize,
  };
  const contentMark =
    fontMark && isContentMark(fontMark) ? fontMark : undefined;
  function updateFontMark(patch: Partial<Mark>): boolean {
    if (
      !fontMark ||
      selectedMark === undefined ||
      !selectionModifiable(effectiveSelection())
    )
      return false;
    const next = { ...fontMark, ...patch };
    if (textEdit?.id === selectedMark)
      setTextEdit({ id: selectedMark, mark: next });
    else if (
      !commit({
        ...state,
        marks: state.marks.map((mark) =>
          mark.id === selectedMark ? next : mark,
        ),
      })
    )
      return false;
    return true;
  }
  function applyTextStyle(patch: Partial<AnnotationTextStyle>): boolean {
    if (fontMark)
      return updateFontMark({ style: { ...fontMark.style, ...patch } });
    else
      updateToolAttribute({
        textStyle: { ...activeToolAttributes.textStyle, ...patch },
      });
    return true;
  }
  function beginTextEdit(id: string): void {
    const mark = state.marks.find((entry) => entry.id === id);
    if (
      !mark ||
      !isContentMark(mark) ||
      !selectionModifiable(expandGroupSelection(state.marks, new Set([id])))
    )
      return;
    cancelDrawing();
    select(id);
    setTool("select");
    setTextEdit({ id, mark });
    setOperationMessage("正在编辑文字草稿；完成后才能复制或保存，Esc 可取消。");
  }
  function finishTextEdit(): void {
    if (!textEdit || textComposing.current) return;
    if (textEdit.mark.text?.trim() === "") {
      setOperationMessage("文字不能为空；可取消以保留原文字。");
      return;
    }
    if (
      JSON.stringify(textEdit.mark) !==
      JSON.stringify(state.marks.find((mark) => mark.id === textEdit.id))
    )
      if (
        !commit({
          ...state,
          marks: state.marks.map((mark) =>
            mark.id === textEdit.id ? textEdit.mark : mark,
          ),
        })
      )
        return;
    setTextEdit(undefined);
    setOperationMessage("已完成文字编辑。");
  }
  function resolveFont(requested: string): string {
    const resolved = resolveAnnotationFont(
      canvasRef.current?.getContext("2d") ?? null,
      requested || "system-ui",
    );
    if (
      resolved.family !== (activeTextStyle.fontFamily ?? "system-ui") &&
      !applyTextStyle({ fontFamily: resolved.family })
    )
      return activeTextStyle.fontFamily ?? "system-ui";
    if (!resolved.available)
      setOperationMessage("字体“" + requested + "”不可用，已回退到系统字体。");
    else setOperationMessage("已使用字体“" + resolved.family + "”。");
    return resolved.family;
  }
  function renumber(): void {
    if (state.marks.some((mark) => mark.tool === "numbering" && mark.locked))
      return;
    const start = attributesOfTool("numbering").numberStart;
    let next = start;
    if (
      !commit({
        ...state,
        marks: state.marks.map((mark) =>
          mark.tool === "numbering" ? { ...mark, ordinal: next++ } : mark,
        ),
      })
    )
      return;
    setOperationMessage("已从 " + start + " 重新编号；可撤销。");
  }
  // —— 对象批量操作：每次恰好一条历史，按稳定 id 定位；含锁选区禁改 ——
  function objectActionContext(): CanvasRenderingContext2D | undefined {
    return canvasRef.current?.getContext("2d") ?? undefined;
  }

  function effectiveSelection(): ReadonlySet<string> {
    return expandGroupSelection(state.marks, selectionRef.current.ids);
  }

  /** 变换基准：选区联合边界（含字形旋转包围盒）中心。 */
  function selectionCenterOf(ids: ReadonlySet<string>): Point | undefined {
    const bounds = selectionBounds(state.marks, ids, objectActionContext());
    if (!bounds) return undefined;
    return { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };
  }

  function commitTransform(
    ids: ReadonlySet<string>,
    marks: readonly Mark[],
    message: string,
  ): void {
    if (
      marks.length === state.marks.length &&
      marks.every((mark, index) => mark === state.marks[index])
    )
      return;
    cancelDrawing();
    if (!commit({ ...state, marks })) return;
    const primary = selectionRef.current.primary;
    setObjectSelection({
      ids,
      primary: primary && ids.has(primary) ? primary : [...ids][ids.size - 1],
    });
    setOperationMessage(message);
  }

  function nudgeSelection(delta: Point): void {
    cancelDrawing();
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    if (!commit({ ...state, marks: moveMarkIds(state.marks, ids, delta) }))
      return;
  }

  function deleteSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    cancelDrawing();
    if (!commit({ ...state, marks: deleteMarkIds(state.marks, ids) })) return;
    setObjectSelection(NO_SELECTION);
    setOperationMessage(`已删除 ${ids.size} 个对象；可用撤销恢复。`);
  }

  function rotateSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    const center = selectionCenterOf(ids);
    if (!center) return;
    commitTransform(
      ids,
      rotateMarkIds(state.marks, ids, center),
      "已将选区旋转 90°；命中与导出同步更新，可撤销。",
    );
  }

  function mirrorSelection(axis: "h" | "v"): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    const center = selectionCenterOf(ids);
    if (!center) return;
    commitTransform(
      ids,
      mirrorMarkIds(state.marks, ids, center, axis),
      axis === "h" ? "已水平镜像选区；可撤销。" : "已垂直镜像选区；可撤销。",
    );
  }

  function scaleSelection(factor: number): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    const center = selectionCenterOf(ids);
    if (!center) return;
    commitTransform(
      ids,
      scaleMarkIds(state.marks, ids, factor, center),
      factor > 1
        ? "已等比放大选区（几何与线宽/字号同步缩放一次）。"
        : "已等比缩小选区（几何与线宽/字号同步缩放一次）。",
    );
  }

  function reorderSelection(mode: ObjectZOrder): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    commitTransform(
      ids,
      reorderMarkIds(state.marks, ids, mode),
      "已调整层级；相对顺序保持不变。",
    );
  }

  function alignSelection(align: ObjectAlign): void {
    const ids = effectiveSelection();
    if (ids.size < 2 || !selectionModifiable(ids)) return;
    commitTransform(
      ids,
      alignMarkIds(state.marks, ids, align, objectActionContext()),
      "已按选区边界对齐；可撤销。",
    );
  }

  function distributeSelection(axis: ObjectDistributeAxis): void {
    const ids = effectiveSelection();
    if (ids.size < 3 || !selectionModifiable(ids)) return;
    commitTransform(
      ids,
      distributeMarkIds(state.marks, ids, axis, objectActionContext()),
      "已等距分布（首尾固定）；可撤销。",
    );
  }

  function groupSelection(): void {
    const ids = effectiveSelection();
    if (ids.size < 2 || !selectionModifiable(ids)) return;
    commitTransform(
      ids,
      groupMarkIds(state.marks, ids, nextGroupId()),
      `已组合 ${ids.size} 个对象；点击任一成员将整组选择。`,
    );
  }

  function ungroupSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    if (!state.marks.some((mark) => ids.has(mark.id) && mark.groupId)) return;
    commitTransform(
      ids,
      ungroupMarkIds(state.marks, ids),
      "已解组；成员层级与样式保持不变。",
    );
  }

  function lockSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size) return;
    commitTransform(
      ids,
      setMarkLocked(state.marks, ids, true),
      "已锁定选区；画布不再命中，可在对象列表选择后解锁。",
    );
  }

  function unlockSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size) return;
    commitTransform(
      ids,
      setMarkLocked(state.marks, ids, false),
      "已解锁选区；画布命中恢复。",
    );
  }

  function copySelection(): void {
    const ids = effectiveSelection();
    if (!ids.size) return;
    setClipboardSnapshot({
      source,
      sessionId: session?.sessionId ?? "",
      marks: state.marks.filter((mark) => ids.has(mark.id)),
    });
    setOperationMessage(
      `已复制 ${ids.size} 个对象到编辑器剪贴板（不写入系统剪贴板）。`,
    );
  }

  function canAppendObjects(clones: readonly Mark[]): boolean {
    if (
      state.marks.filter(isExclusionMark).length +
        clones.filter(isExclusionMark).length >
      MAX_EXCLUSION_MARKS
    ) {
      setOperationMessage("最多保留 64 个屏蔽区域；本次复制未修改对象。");
      return false;
    }
    return true;
  }

  function pasteClipboard(): void {
    if (!clipboard.length) return;
    const clones = cloneMarks(
      clipboard,
      new Set(clipboard.map((mark) => mark.id)),
      { x: 16, y: 16 },
      nextMarkId,
      nextGroupId,
    );
    if (!canAppendObjects(clones)) return;
    cancelDrawing();
    if (!commit({ ...state, marks: [...state.marks, ...clones] })) return;
    setObjectSelection({
      ids: new Set(clones.map((mark) => mark.id)),
      primary: clones[clones.length - 1]?.id,
    });
    setOperationMessage(
      `已粘贴 ${clones.length} 个对象副本；副本样式独立，可撤销。`,
    );
  }

  function duplicateSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    const clones = cloneMarks(
      state.marks,
      ids,
      { x: 16, y: 16 },
      nextMarkId,
      nextGroupId,
    );
    if (!canAppendObjects(clones)) return;
    cancelDrawing();
    if (!commit({ ...state, marks: [...state.marks, ...clones] })) return;
    setObjectSelection({
      ids: new Set(clones.map((mark) => mark.id)),
      primary: clones[clones.length - 1]?.id,
    });
    setOperationMessage("已创建选区副本；副本与原件样式独立，可撤销。");
  }

  function selectAllObjects(): void {
    const ids = unlockedMarkIds(state.marks);
    if (!ids.size) return;
    cancelDrawing();
    setObjectSelection({
      ids,
      primary: state.marks.findLast((mark) => ids.has(mark.id))?.id,
    });
    setOperationMessage(`已全选 ${ids.size} 个未锁定对象。`);
  }

  /** 离屏对象显式回中心：一次平移入历史，可撤销；不自动裁切/丢弃。 */
  function restoreObjectToCenter(id: string): void {
    const mark = state.marks.find((entry) => entry.id === id);
    const canvas = canvasRef.current;
    if (!mark || mark.locked || !canvas) return;
    const size = canvasSize(canvas);
    const ids = expandGroupSelection(state.marks, new Set([id]));
    if (!markOffscreen(mark, size, objectActionContext())) return;
    if (!selectionModifiable(ids)) return;
    const bounds = markBoundsOf(mark, objectActionContext());
    cancelDrawing();
    if (
      !commit({
        ...state,
        marks: moveMarkIds(state.marks, ids, {
          x: size.width / 2 - bounds.x - bounds.width / 2,
          y: size.height / 2 - bounds.y - bounds.height / 2,
        }),
      })
    )
      return;
    select(id);
    setOperationMessage("已将离屏对象移回画布中心；可撤销。");
  }

  const panning = tool === "hand" || spaceHeld;

  const [decodedLayer, setDecodedLayer] = useState<
    { url: string; image: HTMLImageElement } | undefined
  >();
  const layerImage =
    decodedLayer && decodedLayer.url === layerImageUrl
      ? decodedLayer.image
      : undefined;
  useEffect(() => {
    if (!layerReady || !layerImageUrl) return undefined;
    const image = new Image();
    image.decoding = "async";
    image.onload = () => setDecodedLayer({ url: layerImageUrl, image });
    image.src = layerImageUrl;
    return () => {
      image.onload = null;
    };
  }, [layerReady, layerImageUrl]);

  const stageRef = useRef<HTMLDivElement>(null);
  const [measuredSize, setMeasuredSize] = useState<
    { image: HTMLImageElement; size: CanvasSize } | undefined
  >();
  const stageSize = useMemo(
    () =>
      measuredSize && measuredSize.image === layerImage && layerImage
        ? measuredSize.size
        : layerImage
          ? { width: layerImage.naturalWidth, height: layerImage.naturalHeight }
          : undefined,
    [measuredSize, layerImage],
  );
  useEffect(() => {
    if (!layerReady || !layerImage) return undefined;
    const element = stageRef.current;
    if (!element || typeof ResizeObserver === "undefined") return undefined;
    // 布局尺寸优先；无布局环境回退为最终 PNG 自然尺寸（1:1 显示）。
    const observer = new ResizeObserver(() => {
      const width = element.clientWidth;
      const height = element.clientHeight;
      if (width > 0 && height > 0) {
        setMeasuredSize((current) =>
          current?.image === layerImage &&
          current.size.width === width &&
          current.size.height === height
            ? current
            : { image: layerImage, size: { width, height } },
        );
      }
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [layerReady, layerImage]);

  const layerLines = textLayer?.lines;
  // 边界相交策略：行框与任一排除矩形有正面积重叠即整体丢弃（仅贴边不算），
  // 不臆造引擎缺失坐标；被屏蔽文字因输入像素已覆盖本就不可进入层。
  // 行框是 [0,1000] 归一化坐标（参考最终输出 PNG），排除矩形同步归一化。
  const layerExclusionRects = useMemo(
    () =>
      layerReady && imageForLayer && displayCanvas
        ? [
            ...exclusionNormalizedRects(
              imageForLayer,
              state,
              canvasSize(displayCanvas),
            ),
            // 放大镜泄露区与屏蔽块同规则参与行过滤：与贴图宿主侧过滤一致
            // （H3；输入像素本身已烘焙，此处是同一信任边界的行级兑现）。
            ...displayRectsToNormalizedRects(
              magnifierExclusionLeakRects(
                state,
                imageForLayer,
                canvasSize(displayCanvas),
              ),
              imageForLayer,
              state,
              canvasSize(displayCanvas),
            ),
          ]
        : undefined,
    [layerReady, state, imageForLayer, displayCanvas],
  );
  const visibleLayerLines = useMemo(
    () =>
      toImageTextLines(
        (layerLines ?? [])
          .filter(
            (line) =>
              !layerExclusionRects?.some((rect) =>
                rectIntersectsBox(rect, {
                  x1: Math.min(line.x1, line.x2),
                  y1: Math.min(line.y1, line.y2),
                  x2: Math.max(line.x1, line.x2),
                  y2: Math.max(line.y1, line.y2),
                }),
              ),
          )
          .map((line) => ({
            text: line.text,
            bbox: [line.x1, line.y1, line.x2, line.y2] as const,
            order: line.order ?? undefined,
            score: line.score ?? undefined,
          })),
      ),
    [layerLines, layerExclusionRects],
  );
  const [selection, setSelection] = useState<
    { key: string; text: string } | undefined
  >();
  const layerSelectionKey =
    layerReady && layerImageUrl
      ? `${layerImageUrl}:${sessionKeyForLayer}:${localRevision}`
      : "";
  const selectionText =
    selection?.key === layerSelectionKey && layerSelectionKey
      ? selection.text
      : "";
  const [copyMenu, setCopyMenu] = useState<{
    x: number;
    y: number;
    text: string;
    key: string;
  }>();
  const copyButtonSelection = useRef<{ text: string; key: string } | undefined>(
    undefined,
  );
  const readLayerSelection = useCallback(() => {
    const root = stageRef.current?.querySelector(".image-text-layer");
    const current = document.getSelection();
    const range = current?.rangeCount ? current.getRangeAt(0) : undefined;
    return layerSelectionKey &&
      root &&
      range &&
      current?.anchorNode &&
      current.focusNode &&
      root.contains(current.anchorNode) &&
      root.contains(current.focusNode) &&
      root.contains(range.commonAncestorContainer)
      ? range.toString()
      : "";
  }, [layerSelectionKey]);
  async function copySelectedText(frozen?: { text: string; key: string }) {
    const text = frozen
      ? frozen.key === layerSelectionKey
        ? frozen.text
        : ""
      : readLayerSelection();
    if (!session || !text || !layerSelectionKey) return;
    setCopyMenu(undefined);
    // 浮层按钮即将卸载：把焦点还给始终可见且可聚焦的图片视口容器
    // （layerReady 时画布 display:none 不可聚焦），避免焦点落到 body 后
    // Tab 从头开始。
    viewportRef.current?.focus({ preventScroll: true });
    try {
      const copied = await actions.run({
        type: commands.copyScreenshotSelection,
        sessionId: session.sessionId,
        revision: localRevision,
        text,
      });
      setOperationMessage(
        copied
          ? "已复制所选文字。"
          : "复制所选文字失败；请查看操作错误并重试。",
      );
    } catch {
      setOperationMessage("复制所选文字失败；请查看操作错误并重试。");
    }
  }
  useEffect(() => {
    if (!layerSelectionKey) return undefined;
    const readSelection = () => {
      const text = readLayerSelection();
      setSelection((current) =>
        current?.key === layerSelectionKey && current.text === text
          ? current
          : { key: layerSelectionKey, text },
      );
    };
    document.addEventListener("selectionchange", readSelection);
    return () => document.removeEventListener("selectionchange", readSelection);
  }, [layerSelectionKey, layerLines, stageSize, readLayerSelection]);

  const textLayerHint = textLayerStatusLabel(textLayer);

  function closeSession(): Promise<boolean> {
    if (pinSnapshotApply && !window.confirm("放弃当前更改并恢复上次贴图？"))
      return Promise.resolve(false);
    return actions.run({ type: commands.closeScreenshotSession });
  }

  // 容器外的工作台控件也可调用文档命令；ref 保留最新回调。
  const documentCommandRef = useRef({
    save: pinSnapshotApply ? applyPinSnapshot : saveAnnotatedImage,
    close: closeSession,
    canExport,
    isExporting,
    hasSession: !!session,
    suspended: proofreading,
  });
  useEffect(() => {
    documentCommandRef.current = {
      save: pinSnapshotApply ? applyPinSnapshot : saveAnnotatedImage,
      close: closeSession,
      canExport,
      isExporting,
      hasSession: !!session,
      suspended: proofreading || interactionLocked,
    };
  });
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      // 校对模式挂起文档级命令：隐藏的编辑器不得继续消费 Ctrl+S/Ctrl+W。
      if (documentCommandRef.current.suspended || textComposing.current) return;
      if (
        !shouldRunDocumentCommand(event) ||
        event.repeat ||
        event.altKey ||
        event.shiftKey ||
        !(event.ctrlKey || event.metaKey)
      )
        return;
      const key = event.key.toLowerCase();
      const state = documentCommandRef.current;
      if (key === "s" && state.canExport && !state.isExporting) {
        event.preventDefault();
        void state.save();
        return;
      }
      if (
        key === "w" &&
        state.hasSession &&
        !state.isExporting &&
        !["INPUT", "TEXTAREA", "SELECT"].includes(
          (event.target as HTMLElement | null)?.tagName ?? "",
        )
      ) {
        event.preventDefault();
        void state.close();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  // —— 工具栏组合片段 ——
  // 现场两排（与原生框选悬浮栏同构）：上排承载全部工具与操作，不换行，
  // 过宽靠容器内水平滚动保持单行可达；下排仅当前工具属性，可换行收纳。
  // 普通页仍是单条工具栏+底部 footer，两种布局共用片段，可访问名称与
  // 命令不随布局变化。
  const toolButtons = TOOLSETS[toolset]
    .filter((candidate) => !pinSnapshotApply || candidate !== "textSelect")
    // 截图现场无裁剪按钮：选区四边/四角拖拽直接调整裁剪。
    .filter((value) => !(captureStyle && value === "crop"))
    .map((value) => {
      // 新增工具（直线/折线）无已确认图标：始终显示清晰文字，不放空按钮。
      const icon = TOOL_ICONS[value];
      return (
        <ToolbarButton
          appearance={tool === value ? "primary" : "subtle"}
          aria-label={TOOL_LABELS[value]}
          title={TOOL_LABELS[value]}
          icon={icon ? <ToolbarIcon name={icon} /> : undefined}
          aria-pressed={tool === value}
          key={value}
          onClick={() => {
            cancelDrawing();
            setTool(value);
          }}
        >
          {/* 现场以图标为主；马赛克/模糊为用户确认的效果预览入口，带短标签。 */}
          {captureStyle
            ? icon
              ? value === "mosaic" || value === "blur"
                ? TOOL_LABELS[value]
                : undefined
              : TOOL_LABELS[value]
            : TOOL_LABELS[value]}
        </ToolbarButton>
      );
    });

  // 选区派生态（工具栏/列表共用）：含锁判定驱动修改类动作禁用。
  const selectionCount = selectedIds.size;
  const selectionUnitCount = selectionUnits(
    state.marks,
    selectedIds,
    undefined,
  ).length;
  const selectionTouchLocked =
    selectionCount > 0 &&
    state.marks.some((mark) => selectedIds.has(mark.id) && mark.locked);
  const selectionModifiableUi = selectionCount > 0 && !selectionTouchLocked;
  const selectionTouchGroup =
    selectionCount > 0 &&
    state.marks.some((mark) => selectedIds.has(mark.id) && !!mark.groupId);
  const hasUnlockedMarks = state.marks.some((mark) => !mark.locked);
  // 对象列表（下排属性区）：按 z 序列出全部对象；锁定/离屏对象在画布
  // 不可选/不可见，仍可在此选择并解锁/移回中心（不静默裁切）。
  const markIsOffscreen = (mark: Mark): boolean => offscreenIds.has(mark.id);
  const primaryMark =
    selectedMark !== undefined
      ? state.marks.find((mark) => mark.id === selectedMark)
      : undefined;
  const primaryIndex = primaryMark ? state.marks.indexOf(primaryMark) : -1;
  const objectSelectionControl = tool === "select" &&
    state.marks.length > 0 && (
      <>
        <label className="editor-style-control">
          <span aria-hidden="true">标注</span>
          <Select
            aria-label="选择标注对象"
            size="small"
            value={primaryIndex >= 0 ? String(primaryIndex) : ""}
            onChange={(_, data) => {
              cancelDrawing();
              if (data.value === "") select(undefined);
              else {
                const index = Number(data.value);
                const mark = state.marks[index];
                if (mark) select(mark.id);
              }
            }}
          >
            <option value="">
              {selectionCount > 1
                ? `未选择（已选 ${selectionCount} 个）`
                : "未选择"}
            </option>
            {state.marks.map((mark, index) => (
              <option key={mark.id} value={String(index)}>
                {index + 1} · {TOOL_LABELS[mark.tool]}
                {mark.locked ? "（已锁定）" : ""}
                {markIsOffscreen(mark) ? "（画布外）" : ""}
              </option>
            ))}
          </Select>
        </label>
        {primaryMark && markIsOffscreen(primaryMark) && !primaryMark.locked && (
          <ToolbarButton
            aria-label="对象回中心"
            onClick={() => restoreObjectToCenter(primaryMark.id)}
            title="把离屏对象移回画布中心（可撤销）"
          >
            回中心
          </ToolbarButton>
        )}
      </>
    );

  const annotationTextInput = (tool === "text" ||
    tool === "callout" ||
    contentMark) && (
    <Textarea
      aria-label="标注文字"
      className="editor-annotation-text"
      size="small"
      resize="vertical"
      rows={2}
      maxLength={4000}
      ref={textInputRef}
      disabled={!!contentMark?.locked}
      value={contentMark ? (contentMark.text ?? "") : annotationText}
      onChange={(_, data) => {
        if (contentMark && selectedMark !== undefined) {
          if (!selectionModifiable(effectiveSelection())) return;
          setTextEdit({
            id: selectedMark,
            mark: { ...contentMark, text: data.value },
          });
        } else setAnnotationText(data.value);
      }}
      onCompositionStart={() => {
        textComposing.current = true;
      }}
      onCompositionEnd={() => {
        textComposing.current = false;
      }}
      onKeyDown={(event) => {
        event.stopPropagation();
        if (textComposing.current || isImeCompositionEvent(event.nativeEvent))
          return;
        if (event.key === "Escape" && textEdit) {
          event.preventDefault();
          cancelDrawing();
          setOperationMessage("已取消文字编辑，原文字保留。");
        } else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
          event.preventDefault();
          finishTextEdit();
        } else if (
          event.key.toLowerCase() === "s" &&
          (event.ctrlKey || event.metaKey)
        )
          event.preventDefault();
      }}
    />
  );

  // 可见性：绘制时按当前工具；选择工具下选中描边类标记时按标记工具，
  // 其余工具（选择未选中/手形/裁剪等）不出现无关设置。
  const showStrokeColorControl =
    toolset === "full" && (COLOR_TOOLS.includes(tool) || styleEditing);
  const showStrokeWidthControl =
    toolset === "full" &&
    (STROKE_TOOLS.includes(tool) ||
      (!!editingMark && STROKE_TOOLS.includes(editingMark.tool)));
  const showLineStyleControl =
    toolset === "full" &&
    (LINE_STYLE_TOOLS.includes(tool) ||
      (editingMark ? LINE_STYLE_TOOLS.includes(editingMark.tool) : false));
  const showCornerControl =
    toolset === "full" &&
    (CORNER_TOOLS.includes(tool) || editingMark?.tool === "rectangle");
  const showArrowControls =
    toolset === "full" &&
    (ARROW_TOOLS.includes(tool) || editingMark?.tool === "arrow");
  const showFillControl =
    toolset === "full" &&
    (FILL_TOOLS.includes(tool) ||
      (editingMark ? FILL_TOOLS.includes(editingMark.tool) : false));

  // 属性下排样式控件收敛为独立面板：控件与事件接线在子组件内完成，
  // 父级仅以单一 onStyleChange 入口承接（选中标记就地改样式或更新默认值）。
  const stylePanel = (
    <AnnotationStylePanel
      activeArrowDouble={activeArrowDouble}
      activeArrowStyle={activeArrowStyle}
      activeCornerRadius={activeCornerRadius}
      activeFillColor={activeFillColor}
      activeFillEnabled={activeFillEnabled}
      activeFillOpacity={activeFillOpacity}
      activeLineStyle={activeLineStyle}
      activeStrokeOpacity={activeStrokeOpacity}
      activeStrokeWidth={activeStrokeWidth}
      activeStyleColor={activeStyleColor}
      eyedropperAvailable={eyedropperAvailable}
      eyedropperBusy={eyedropperBusy}
      styleTargetId={editingMark?.id}
      onPickColor={
        eyedropperAvailable ? () => void pickScreenColor() : enterCanvasPick
      }
      onStyleChange={applyStyleChange}
      showArrowControls={showArrowControls}
      showColorControl={showStrokeColorControl}
      showCornerControl={showCornerControl}
      showFillControl={showFillControl}
      showLineStyleControl={showLineStyleControl}
      showStrokeControls={showStrokeWidthControl}
    />
  );
  // 重置当前工具属性：仅在存在属性记忆的工具出现；就地编辑选中标记时
  // 不提供，避免把“标记样式”与“工具记忆”混为一谈。
  const resetAttributesControl = !styleEditing &&
    (COLOR_TOOLS.includes(tool) ||
      tool === "mosaic" ||
      tool === "blur" ||
      tool === "spotlight" ||
      tool === "magnifier") && (
      <ToolbarButton
        aria-label="重置属性"
        icon={<ToolbarIcon name="refresh" />}
        title="重置当前工具属性为默认值"
        onClick={resetToolAttributes}
      >
        {captureStyle ? undefined : "重置属性"}
      </ToolbarButton>
    );
  // 已绘马赛克/模糊区域的强度就地编辑：选择工具选中该类标记时，
  // 属性排直接调其强度并逐次入历史（可撤销）；显示与像素同一函数。
  const effectMarkCandidate =
    tool === "select" && selectedMark !== undefined
      ? textEdit?.id === selectedMark
        ? textEdit.mark
        : state.marks.find((mark) => mark.id === selectedMark)
      : undefined;
  const editingEffectMark =
    effectMarkCandidate &&
    (effectMarkCandidate.tool === "mosaic" ||
      effectMarkCandidate.tool === "blur")
      ? effectMarkCandidate
      : undefined;
  const effectIntensityTarget = editingEffectMark
    ? markEffectIntensity(editingEffectMark)
    : tool === "mosaic" || tool === "blur"
      ? Math.round(
          Math.min(
            EFFECT_INTENSITY_MAX,
            Math.max(EFFECT_INTENSITY_MIN, activeToolAttributes.intensity),
          ) * 10,
        ) / 10
      : undefined;
  const effectIntensityLocked =
    !!editingEffectMark &&
    state.marks.some(
      (mark) =>
        mark.locked &&
        expandGroupSelection(state.marks, new Set([editingEffectMark.id])).has(
          mark.id,
        ),
    );
  function commitEffectIntensity(value: number): void {
    if (editingEffectMark && selectedMark !== undefined) {
      if (
        !selectionModifiable(
          expandGroupSelection(state.marks, new Set([editingEffectMark.id])),
        )
      )
        return;
      if (
        !commit({
          ...state,
          marks: state.marks.map((entry) =>
            entry.id === selectedMark
              ? { ...entry, style: { ...entry.style, intensity: value } }
              : entry,
          ),
        })
      )
        return;
      setOperationMessage("已调整该区域强度；可用撤销恢复。");
      return;
    }
    updateToolAttribute({ intensity: value });
  }
  const effectIntensityFocus = useFocusableGroup({
    ignoreDefaultKeydown: {
      ArrowLeft: true,
      ArrowRight: true,
      ArrowUp: true,
      ArrowDown: true,
      Home: true,
      End: true,
    },
  });
  const effectIntensityControl = toolset === "full" &&
    effectIntensityTarget !== undefined && (
      <label className="editor-style-control">
        <span aria-hidden="true">强度</span>
        <input
          {...effectIntensityFocus}
          aria-label="马赛克与模糊强度"
          disabled={effectIntensityLocked}
          max={EFFECT_INTENSITY_MAX}
          min={EFFECT_INTENSITY_MIN}
          step={EFFECT_INTENSITY_STEP}
          type="range"
          value={effectIntensityTarget}
          onChange={(event) => {
            const value =
              Math.round(Number(event.currentTarget.value) * 10) / 10;
            if (Number.isFinite(value)) commitEffectIntensity(value);
          }}
        />
        <span aria-hidden="true">{intensityLabel(effectIntensityTarget)}</span>
      </label>
    );
  // 马赛克/模糊绘制方式：矩形拖拽或自由笔刷；笔刷大小同排可选。
  const effectModeControl = toolset === "full" &&
    (tool === "mosaic" || tool === "blur") && (
      <label className="editor-style-control">
        <span aria-hidden="true">方式</span>
        <Select
          aria-label="马赛克与模糊绘制方式"
          size="small"
          value={activeToolAttributes.effectMode}
          onChange={(_, data) =>
            updateToolAttribute({
              effectMode: data.value === "brush" ? "brush" : "rect",
            })
          }
        >
          <option value="rect">矩形</option>
          <option value="brush">笔刷</option>
        </Select>
      </label>
    );
  // #235 选择工具下可就地编辑的新对象：聚光灯/放大镜/文字与图片（水印）。
  const emphasisMarkCandidate = editingMarkCandidate;
  const editingSpotlightMark =
    emphasisMarkCandidate?.tool === "spotlight"
      ? emphasisMarkCandidate
      : undefined;
  const editingMagnifierMark =
    emphasisMarkCandidate?.tool === "magnifier"
      ? emphasisMarkCandidate
      : undefined;
  const watermarkOpacityMark =
    emphasisMarkCandidate &&
    (emphasisMarkCandidate.tool === "text" ||
      emphasisMarkCandidate.tool === "image")
      ? emphasisMarkCandidate
      : undefined;

  /** 就地样式提交：每次修改入历史可撤销；文字编辑中同步草稿。 */
  function commitMarkStyle(
    mark: Mark,
    style: Mark["style"],
    message: string,
  ): void {
    if (
      !selectionModifiable(
        expandGroupSelection(state.marks, new Set([mark.id])),
      )
    )
      return;
    if (JSON.stringify(mark.style) === JSON.stringify(style)) return;
    if (textEdit?.id === mark.id) {
      setTextEdit({ id: mark.id, mark: { ...mark, style } });
      setOperationMessage(message);
      return;
    }
    if (
      !commit({
        ...state,
        marks: state.marks.map((entry) =>
          entry.id === mark.id ? { ...entry, style } : entry,
        ),
      })
    )
      return;
    setOperationMessage(message);
  }

  // 聚光灯暗度：工具激活时调默认暗度；选中对象时就地调（一次历史）。
  const spotlightDarknessControl = toolset === "full" &&
    (tool === "spotlight" || editingSpotlightMark) && (
      <label className="editor-style-control">
        <span aria-hidden="true">暗度</span>
        <Select
          aria-label="聚光灯暗度"
          size="small"
          disabled={!!editingSpotlightMark?.locked}
          value={String(
            editingSpotlightMark
              ? spotlightDarkness(editingSpotlightMark)
              : activeToolAttributes.spotlightDarkness,
          )}
          onChange={(_, data) => {
            const value = Number(data.value);
            if (!Number.isFinite(value)) return;
            if (editingSpotlightMark) {
              commitMarkStyle(
                editingSpotlightMark,
                {
                  ...editingSpotlightMark.style,
                  fill: {
                    color: editingSpotlightMark.style?.fill?.color ?? "#000000",
                    opacity: value,
                  },
                },
                `已调整聚光灯暗度为 ${value}%；可撤销。`,
              );
            } else updateToolAttribute({ spotlightDarkness: value });
          }}
        >
          {[25, 40, 55, 70, 85].map((percent) => (
            <option key={percent} value={String(percent)}>
              {percent}%
            </option>
          ))}
        </Select>
        <span aria-hidden="true">遮罩限定底图范围，按对象层级叠加</span>
      </label>
    );
  // 聚光灯添加区域：下一次拖拽把新孔洞提交进选中对象（一次历史）。
  const spotlightAddRegionControl = editingSpotlightMark &&
    !editingSpotlightMark.locked && (
      <ToolbarButton
        aria-label="添加区域"
        title="为选中聚光灯拖拽添加一个强调区域"
        onClick={() => {
          setSpotlightRegionTarget(editingSpotlightMark.id);
          setOperationMessage(
            "添加区域：在画布上拖拽绘制新的强调孔洞；Esc 取消。",
          );
        }}
      >
        添加区域
      </ToolbarButton>
    );
  // 放大镜倍率 + 采样范围说明：采样仅底图与已应用的修补，不递归标注。
  const magnifierZoomControl = toolset === "full" &&
    (tool === "magnifier" || editingMagnifierMark) && (
      <>
        <label className="editor-style-control">
          <span aria-hidden="true">倍率</span>
          <Select
            aria-label="放大镜倍率"
            size="small"
            disabled={!!editingMagnifierMark?.locked}
            value={String(
              editingMagnifierMark
                ? magnifierZoomOf(editingMagnifierMark)
                : activeToolAttributes.magnifierZoom,
            )}
            onChange={(_, data) => {
              const value = Number(data.value);
              if (!Number.isFinite(value)) return;
              if (editingMagnifierMark) {
                if (
                  !selectionModifiable(
                    expandGroupSelection(
                      state.marks,
                      new Set([editingMagnifierMark.id]),
                    ),
                  )
                )
                  return;
                if (
                  !commit({
                    ...state,
                    marks: state.marks.map((entry) =>
                      entry.id === editingMagnifierMark.id
                        ? { ...entry, zoom: value }
                        : entry,
                    ),
                  })
                )
                  return;
                setOperationMessage(`已调整放大镜倍率为 ${value}×；可撤销。`);
              } else updateToolAttribute({ magnifierZoom: value });
            }}
          >
            {MAGNIFIER_ZOOMS.map((zoom) => (
              <option key={zoom} value={String(zoom)}>
                {zoom}×
              </option>
            ))}
          </Select>
        </label>
        <span aria-hidden="true">采样范围：底图与已应用的修补（不含标注）</span>
      </>
    );
  // 文字/图片水印不透明度：复用通用 strokeOpacity；选中对象就地编辑入历史。
  const watermarkOpacityControl = toolset === "full" &&
    (tool === "text" || watermarkOpacityMark) && (
      <PercentInputControl
        ariaLabel="水印不透明度"
        disabled={!!watermarkOpacityMark?.locked}
        label="不透明度"
        value={
          watermarkOpacityMark
            ? Math.round(annotationStrokeAlpha(watermarkOpacityMark) * 100)
            : activeToolAttributes.strokeOpacity
        }
        onCommit={(value) => {
          if (watermarkOpacityMark) {
            commitMarkStyle(
              watermarkOpacityMark,
              {
                ...watermarkOpacityMark.style,
                strokeOpacity: value,
              },
              `已调整水印不透明度为 ${value}%；可撤销。`,
            );
          } else updateToolAttribute({ strokeOpacity: value });
        }}
      />
    );
  // 对象橡皮擦提示：下排仅属性/提示，无额外控件。
  const objectEraserHint = tool === "objectEraser" && (
    <span aria-hidden="true">
      拖动擦除命中的未锁定对象；整次拖动一次撤销，不影响底图与修补。
    </span>
  );
  const effectBrushSizeControl = toolset === "full" &&
    (tool === "mosaic" || tool === "blur") &&
    activeToolAttributes.effectMode === "brush" && (
      <label className="editor-style-control">
        <span aria-hidden="true">笔刷</span>
        <Select
          aria-label="马赛克与模糊笔刷大小"
          size="small"
          value={String(activeToolAttributes.brushSize)}
          onChange={(_, data) =>
            updateToolAttribute({ brushSize: Number(data.value) })
          }
        >
          {EFFECT_BRUSH_SIZES.map((size) => (
            <option key={size} value={String(size)}>
              {size}px
            </option>
          ))}
        </Select>
      </label>
    );
  // 修补掩膜绘制方式：矩形/笔刷 × 添加/移除；笔刷大小同排可选。
  const inpaintMaskModeControl = tool === "inpaint" && (
    <label className="editor-style-control">
      <span aria-hidden="true">掩膜</span>
      <Select
        aria-label="修补掩膜绘制方式"
        size="small"
        value={activeToolAttributes.maskMode}
        onChange={(_, data) => {
          const value = data.value as ToolAttributes["maskMode"];
          if (INPAINT_MASK_MODES.some((mode) => mode.value === value)) {
            updateToolAttribute({ maskMode: value });
          }
        }}
      >
        {INPAINT_MASK_MODES.map((mode) => (
          <option key={mode.value} value={mode.value}>
            {mode.label}
          </option>
        ))}
      </Select>
    </label>
  );
  const inpaintBrushSizeControl = tool === "inpaint" &&
    (activeToolAttributes.maskMode === "brush-add" ||
      activeToolAttributes.maskMode === "brush-remove") && (
      <label className="editor-style-control">
        <span aria-hidden="true">笔刷</span>
        <Select
          aria-label="修补掩膜笔刷大小"
          size="small"
          value={String(activeToolAttributes.inpaintBrushSize)}
          onChange={(_, data) =>
            updateToolAttribute({ inpaintBrushSize: Number(data.value) })
          }
        >
          {INPAINT_BRUSH_SIZES.map((size) => (
            <option key={size} value={String(size)}>
              {size}px
            </option>
          ))}
        </Select>
      </label>
    );
  // 重复匹配候选逐项增删：勾选状态仅存在于此，确认前不修改内容。
  const dupmatchCandidateControls =
    tool === "dupmatch" && dupmatchCandidates ? (
      <div
        aria-label="重复匹配候选"
        className="editor-style-control"
        role="group"
      >
        {dupmatchCandidates.map((candidate, index) => (
          <label key={index}>
            <input
              aria-label={`候选 ${index + 1}`}
              checked={dupmatchAccepted?.[index] ?? true}
              type="checkbox"
              onChange={(event) =>
                setDupmatchAccepted((current) =>
                  (current ?? dupmatchCandidates.map(() => true)).map(
                    (value, entryIndex) =>
                      entryIndex === index ? event.target.checked : value,
                  ),
                )
              }
            />
            <span aria-hidden="true">
              候选{index + 1}（{Math.round(candidate.x)},
              {Math.round(candidate.y)}，误差
              {candidate.error.toFixed(1)}）
            </span>
          </label>
        ))}
      </div>
    ) : undefined;

  const activeFontSize = activeTextStyle.fontSize ?? 24;
  const fontSizeControl = fontTool && (
    <label className="editor-style-control">
      <span aria-hidden="true">字号</span>
      <Select
        aria-label="文字字号"
        size="small"
        value={String(activeFontSize)}
        onChange={(_, data) =>
          fontMark
            ? updateFontMark({
                style: { ...fontMark.style, fontSize: Number(data.value) },
              })
            : updateToolAttribute({ fontSize: Number(data.value) })
        }
      >
        {Number.isFinite(activeFontSize) &&
          activeFontSize > 0 &&
          !FONT_SIZES.some((size) => size === activeFontSize) && (
            <option value={String(activeFontSize)}>{activeFontSize}px</option>
          )}
        {FONT_SIZES.map((size) => (
          <option key={size} value={String(size)}>
            {size}px
          </option>
        ))}
      </Select>
    </label>
  );

  // 自动取字偏好：仅随取字工具在属性排显示；偏好状态本身（autoText/
  // onAutoTextChange）不受工具切换影响，切回取字时恢复勾选。
  const autoTextControl = session &&
    showAutoTextPreference &&
    tool === "textSelect" && (
      <label className="editor-style-control">
        <input
          aria-label="自动取字"
          checked={autoText}
          onChange={(event) => {
            setLocalAutoText(event.target.checked);
            onAutoTextChange?.(event.target.checked);
          }}
          type="checkbox"
        />
        <span aria-hidden="true">自动取字</span>
      </label>
    );

  // 取字上下文操作（按钮，非属性）：主排按工具显示。
  const textLayerControls = (
    <>
      {tool === "textSelect" &&
        textLayer?.status === "textlayer.preparing" &&
        session && (
          <ToolbarButton
            aria-label="取消准备文字层"
            icon={<ToolbarIcon name="exit" />}
            title="取消准备文字层"
            onClick={() =>
              void actions.run({
                type: commands.cancelScreenshotTextLayer,
                sessionId: session.sessionId,
                revision: localRevision,
              })
            }
          >
            {captureStyle ? undefined : "取消准备"}
          </ToolbarButton>
        )}
      {tool === "textSelect" &&
        (textLayer?.status === "textlayer.unavailable" ||
          textLayer?.status === "textlayer.failed" ||
          textLayer?.status === "textlayer.cancelled") && (
          <ToolbarButton
            aria-label="重新准备文字层"
            disabled={!canRecognize}
            icon={<ToolbarIcon name="refresh" />}
            title="重新准备文字层"
            onClick={() => setPrepareNonce((nonce) => nonce + 1)}
          >
            {captureStyle ? undefined : "重新准备"}
          </ToolbarButton>
        )}
      {tool === "textSelect" && selectionText.length > 0 && session && (
        <Button
          appearance="primary"
          icon={<ToolbarIcon name="copy" />}
          size="small"
          onPointerDown={() => {
            const text = readLayerSelection();
            copyButtonSelection.current = text
              ? { text, key: layerSelectionKey }
              : undefined;
          }}
          onClick={() => {
            const frozen = copyButtonSelection.current;
            copyButtonSelection.current = undefined;
            void copySelectedText(frozen);
          }}
        >
          {captureStyle ? undefined : "复制所选"}
        </Button>
      )}
    </>
  );

  // 掩膜有效像素计数（仅随掩膜变化重算）：预览按钮与清除入口的可用性。
  const inpaintMaskPixelCount = useMemo(
    () => maskCount(inpaintMask),
    [inpaintMask],
  );
  const inpaintOperations = tool === "inpaint" && (
    <>
      <ToolbarButton
        appearance="primary"
        aria-label="预览修补"
        disabled={
          inpaintMaskPixelCount === 0 || inpaintBusy || !!inpaintPreview
        }
        icon={<ToolbarIcon name="preview" />}
        title="预览修补"
        onClick={startInpaintPreview}
      >
        {captureStyle ? undefined : "预览修补"}
      </ToolbarButton>
      {inpaintBusy ? (
        <ToolbarButton
          aria-label="取消修补"
          icon={<ToolbarIcon name="exit" />}
          title="取消修补"
          onClick={() => cancelInpaintPending("已取消修补；当前图片未被修改。")}
        >
          {captureStyle ? undefined : "取消修补"}
        </ToolbarButton>
      ) : inpaintPreview ? (
        <>
          <ToolbarButton
            appearance="primary"
            aria-label="应用修补"
            icon={<ToolbarIcon name="confirm" />}
            title="应用修补"
            onClick={applyInpaintPreview}
          >
            {captureStyle ? undefined : "应用修补"}
          </ToolbarButton>
          <ToolbarButton
            aria-label="放弃修补预览"
            icon={<ToolbarIcon name="exit" />}
            title="放弃修补预览"
            onClick={() =>
              cancelInpaintPending("已放弃修补预览；当前图片未被修改。")
            }
          >
            {captureStyle ? undefined : "放弃预览"}
          </ToolbarButton>
        </>
      ) : inpaintSelection || inpaintMaskPixelCount > 0 ? (
        <ToolbarButton
          aria-label="清除修补选区"
          icon={<ToolbarIcon name="exit" />}
          title="清除修补选区"
          onClick={() =>
            cancelInpaintPending("已清除修补掩膜与选区；当前图片未被修改。")
          }
        >
          {captureStyle ? undefined : "清除选区"}
        </ToolbarButton>
      ) : null}
    </>
  );

  // 修补参数（对比下拉）：属性下排，仅在存在预览时展示。
  const inpaintCompareControl = tool === "inpaint" && inpaintPreview && (
    <label className="editor-style-control">
      <span aria-hidden="true">对比</span>
      <Select
        aria-label="修补对比"
        size="small"
        value={inpaintCompare}
        onChange={(_, data) =>
          setInpaintCompare(data.value === "before" ? "before" : "after")
        }
      >
        <option value="after">修补后</option>
        <option value="before">本次修补前</option>
      </Select>
    </label>
  );

  // 重复匹配主排操作（新动作用文字按钮，不新增图标）。
  const dupmatchOperations = tool === "dupmatch" && (
    <>
      {dupmatchCandidates ? (
        <>
          <ToolbarButton
            appearance="primary"
            aria-label="确认应用重复遮挡"
            title="确认应用重复遮挡"
            onClick={applyDuplicateCandidates}
          >
            确认应用
          </ToolbarButton>
          <ToolbarButton
            aria-label="放弃重复匹配"
            title="放弃重复匹配"
            onClick={() =>
              cancelDupmatchPending("已放弃重复匹配；当前图片未被修改。")
            }
          >
            放弃匹配
          </ToolbarButton>
        </>
      ) : (
        <ToolbarButton
          appearance="primary"
          aria-label="查找重复"
          title="查找重复"
          disabled={!dupmatchSelection || dupmatchBusy}
          onClick={startDuplicateSearch}
        >
          {dupmatchBusy ? "匹配中…" : "查找重复"}
        </ToolbarButton>
      )}
      {dupmatchBusy && (
        <ToolbarButton
          aria-label="取消重复匹配"
          title="取消重复匹配"
          onClick={() =>
            cancelDupmatchPending("已取消重复匹配；当前图片未被修改。")
          }
        >
          取消匹配
        </ToolbarButton>
      )}
    </>
  );
  // 对象动作（上排）：选择工具下可见；含锁选区时修改类禁用，避免部分
  // 修改锁定对象/锁组。纯文字按钮，不新增图标语义。
  const objectActionControls = toolset === "full" &&
    tool === "select" &&
    !textEdit && (
      <>
        <ToolbarButton
          aria-label="全选对象"
          disabled={!hasUnlockedMarks}
          onClick={selectAllObjects}
          title="全选未锁定对象（Ctrl+A）"
        >
          全选
        </ToolbarButton>
        <ToolbarButton
          aria-label="粘贴对象"
          disabled={clipboard.length === 0}
          onClick={pasteClipboard}
          title="粘贴编辑器剪贴板对象（Ctrl+V）"
        >
          粘贴
        </ToolbarButton>
        {selectionCount > 0 && (
          <>
            <ToolbarButton
              aria-label="复制对象"
              onClick={copySelection}
              title="复制到编辑器剪贴板（Ctrl+C）"
            >
              复制
            </ToolbarButton>
            <ToolbarButton
              aria-label="重复对象"
              disabled={!selectionModifiableUi}
              onClick={duplicateSelection}
              title="原位创建副本（Ctrl+D）"
            >
              重复
            </ToolbarButton>
            <ToolbarButton
              aria-label="删除对象"
              disabled={!selectionModifiableUi}
              onClick={deleteSelection}
              title="删除所选对象（Delete）"
            >
              删除
            </ToolbarButton>
            <ToolbarButton
              aria-label="对象旋转 90°"
              disabled={!selectionModifiableUi}
              onClick={rotateSelection}
              title="所选对象绕选区中心旋转 90°（命中/导出同步）"
            >
              旋转90°
            </ToolbarButton>
            <ToolbarButton
              aria-label="水平镜像"
              disabled={!selectionModifiableUi}
              onClick={() => mirrorSelection("h")}
              title="绕选区中心水平镜像"
            >
              镜像H
            </ToolbarButton>
            <ToolbarButton
              aria-label="垂直镜像"
              disabled={!selectionModifiableUi}
              onClick={() => mirrorSelection("v")}
              title="绕选区中心垂直镜像"
            >
              镜像V
            </ToolbarButton>
            <ToolbarButton
              aria-label="放大选区"
              disabled={!selectionModifiableUi}
              onClick={() => scaleSelection(1.25)}
              title="等比放大（几何与线宽/字号同步缩放一次）"
            >
              放大
            </ToolbarButton>
            <ToolbarButton
              aria-label="缩小选区"
              disabled={!selectionModifiableUi}
              onClick={() => scaleSelection(0.8)}
              title="等比缩小（几何与线宽/字号同步缩放一次）"
            >
              缩小
            </ToolbarButton>
            <ToolbarButton
              aria-label="上移一层"
              disabled={!selectionModifiableUi}
              onClick={() => reorderSelection("up")}
              title="上移一层（保持相对顺序）"
            >
              上移一层
            </ToolbarButton>
            <ToolbarButton
              aria-label="下移一层"
              disabled={!selectionModifiableUi}
              onClick={() => reorderSelection("down")}
              title="下移一层（保持相对顺序）"
            >
              下移一层
            </ToolbarButton>
            <ToolbarButton
              aria-label="置顶"
              disabled={!selectionModifiableUi}
              onClick={() => reorderSelection("front")}
              title="置顶（保持相对顺序）"
            >
              置顶
            </ToolbarButton>
            <ToolbarButton
              aria-label="置底"
              disabled={!selectionModifiableUi}
              onClick={() => reorderSelection("back")}
              title="置底（保持相对顺序）"
            >
              置底
            </ToolbarButton>
            {selectionUnitCount >= 2 && (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <ToolbarButton
                    aria-label="对齐与等距"
                    disabled={!selectionModifiableUi}
                    title="按变换边界对齐/等距分布"
                  >
                    对齐
                  </ToolbarButton>
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {(
                      [
                        ["left", "左对齐"],
                        ["hcenter", "水平居中"],
                        ["right", "右对齐"],
                        ["top", "顶对齐"],
                        ["vcenter", "垂直居中"],
                        ["bottom", "底对齐"],
                      ] as const
                    ).map(([align, label]) => (
                      <MenuItem
                        key={align}
                        disabled={!selectionModifiableUi}
                        onClick={() => alignSelection(align)}
                      >
                        {label}
                      </MenuItem>
                    ))}
                    <MenuItem
                      disabled={
                        !selectionModifiableUi || selectionUnitCount < 3
                      }
                      onClick={() => distributeSelection("h")}
                    >
                      水平等距（≥3，首尾固定）
                    </MenuItem>
                    <MenuItem
                      disabled={
                        !selectionModifiableUi || selectionUnitCount < 3
                      }
                      onClick={() => distributeSelection("v")}
                    >
                      垂直等距（≥3，首尾固定）
                    </MenuItem>
                  </MenuList>
                </MenuPopover>
              </Menu>
            )}
            {selectionCount >= 2 && (
              <ToolbarButton
                aria-label="组合"
                disabled={!selectionModifiableUi}
                onClick={groupSelection}
                title="组合所选对象（不重排层级）"
              >
                组合
              </ToolbarButton>
            )}
            {selectionTouchGroup && (
              <ToolbarButton
                aria-label="解组"
                disabled={!selectionModifiableUi}
                onClick={ungroupSelection}
                title="解散所选组（成员保留层级与样式）"
              >
                解组
              </ToolbarButton>
            )}
            <ToolbarButton
              aria-label="锁定"
              onClick={lockSelection}
              title="锁定所选对象：画布不再命中，可经对象列表选择后解锁"
            >
              锁定
            </ToolbarButton>
            {selectionTouchLocked && (
              <ToolbarButton
                aria-label="解锁"
                onClick={unlockSelection}
                title="解锁所选对象"
              >
                解锁
              </ToolbarButton>
            )}
          </>
        )}
      </>
    );

  /** 清空屏蔽：只移除排除区，普通标注保留，几何变更入历史可撤销。 */
  function clearExclusions() {
    if (state.marks.some((mark) => isExclusionMark(mark) && mark.locked)) {
      setOperationMessage("屏蔽区包含锁定对象，请先解锁。");
      return;
    }
    cancelDrawing();
    if (
      !commit({
        ...state,
        marks: state.marks.filter((mark) => !isExclusionMark(mark)),
      })
    )
      return;
    select(undefined);
    setOperationMessage("已清空全部屏蔽区；可用撤销恢复。");
  }

  // 历史操作：旋转/撤销/重做属操作，现场排上排，普通页在工具栏末尾；
  // 清空屏蔽在现场收入“更多”菜单，普通页仍为按钮。
  const rotateControl = (
    <ToolbarButton
      title="旋转 90°"
      icon={<ToolbarIcon name="rotate" />}
      aria-label="旋转 90°"
      onClick={() => {
        // 整图旋转：进行中的折线/裁剪/修补草稿坐标随旋转失效，统一放弃，
        // 不留在旧坐标叠加渲染或被 Enter/双击复活。
        cancelDrawing();
        const image = imageRef.current;
        const canvas = canvasRef.current;
        const nextRotation = (state.rotation + 90) % 360;
        if (
          !commit(
            image && canvas
              ? rotateEditorState(
                  state,
                  image,
                  canvasSize(canvas),
                  nextRotation,
                )
              : { ...state, rotation: nextRotation },
          )
        )
          return;
      }}
    >
      {captureStyle ? undefined : "旋转 90°"}
    </ToolbarButton>
  );
  const undoButton = (
    <ToolbarButton
      title="撤销"
      icon={<ToolbarIcon name="undo" />}
      aria-label="撤销"
      disabled={historyIndex === 0 || !!textEdit}
      onClick={undo}
    >
      {captureStyle ? undefined : "撤销"}
    </ToolbarButton>
  );
  const redoButton = (
    <ToolbarButton
      title="重做"
      icon={<ToolbarIcon name="redo" />}
      aria-label="重做"
      disabled={historyIndex >= history.length - 1 || !!textEdit}
      onClick={redo}
    >
      {captureStyle ? undefined : "重做"}
    </ToolbarButton>
  );
  const clearExclusionsButton = (
    <ToolbarButton
      title="清空屏蔽"
      icon={<ToolbarIcon name="clearExclusions" />}
      aria-label="清空屏蔽"
      disabled={
        !hasExclusions ||
        state.marks.some((mark) => isExclusionMark(mark) && mark.locked)
      }
      onClick={clearExclusions}
    >
      {captureStyle ? undefined : "清空屏蔽"}
    </ToolbarButton>
  );

  // 导出/会话主操作：全部并入主排；footer 只承载状态提示（现场为固定
  // 位置浮层）。
  const hasEdits =
    state.marks.length > 0 ||
    state.rotation !== 0 ||
    !!state.crop ||
    !!state.resize ||
    !!state.patches?.length;

  /** 清除编辑：整体回到空白基准，入历史可撤销；进行中的草稿（折线等）
   * 属于被清除的编辑内容，一并放弃，不得在 EMPTY 后仍渲染或复活。 */
  function clearEdits() {
    if (state.marks.some((mark) => mark.locked)) {
      setOperationMessage("存在锁定对象，请先解锁再清除编辑。");
      return;
    }
    cancelDrawing();
    if (!commit(EMPTY)) return;
    select(undefined);
  }

  const copyAnnotatedButton = (
    <Button
      title="复制标注图"
      icon={<ToolbarIcon name="copy" />}
      aria-label="复制标注图"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void copyAnnotatedImage()}
    >
      {captureStyle ? undefined : "复制标注图"}
    </Button>
  );
  const copyExcludedButton = hasExclusions && (
    <Button
      title="复制屏蔽副本"
      icon={<ToolbarIcon name="copyExcluded" />}
      aria-label="复制屏蔽副本"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void copyAnnotatedImage(true)}
    >
      {captureStyle ? undefined : "复制屏蔽副本"}
    </Button>
  );
  const saveAnnotatedButton = (
    <Button
      title="保存标注图"
      icon={<ToolbarIcon name="save" />}
      aria-label="保存标注图"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void saveAnnotatedImage()}
    >
      {captureStyle ? undefined : "保存标注图"}
    </Button>
  );
  const saveExcludedButton = hasExclusions && (
    <Button
      title="保存屏蔽副本"
      icon={<ToolbarIcon name="saveExcluded" />}
      aria-label="保存屏蔽副本"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void saveAnnotatedImage(true)}
    >
      {captureStyle ? undefined : "保存屏蔽副本"}
    </Button>
  );
  const saveProjectButton = session && (
    <Button
      title="保存工程"
      aria-label="保存工程"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void saveCurrentProject()}
    >
      保存工程
    </Button>
  );
  const applyPinSnapshotButton = session && pinSnapshotApply && (
    <Button
      title="完成贴图"
      aria-label="完成贴图"
      appearance="primary"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void applyPinSnapshot()}
    >
      完成贴图
    </Button>
  );
  const pinButton = session && !pinSnapshotApply && (
    <Button
      title="贴图"
      icon={<ToolbarIcon name="pin" />}
      aria-label="贴图"
      size="small"
      disabled={!canExport || isExporting || !!textEdit}
      onClick={() => void pinCurrentImage()}
    >
      {captureStyle ? undefined : "贴图"}
    </Button>
  );
  const imageModes: readonly ImageRecognitionMode[] = recognitionModes?.length
    ? recognitionModes
    : ["文字识别", "表格识别", "公式识别"].map((displayName) => ({
        modeId: "",
        displayName,
        availability: "catalog_unavailable",
      }));
  const unavailableModeHints =
    recognitionModes === undefined
      ? []
      : imageModes
          .filter((mode) => mode.availability !== "ready" || !mode.modeId)
          .map(
            (mode) =>
              `${mode.displayName}：${
                mode.availability === "catalog_unavailable"
                  ? "识别目录未就绪"
                  : mode.availability === "default_missing" ||
                      mode.availability === "default_invalid"
                    ? "默认文字模式未配置或无效"
                    : availabilityLabel(mode.availability)
              }${mode.reasonCode ? `（${mode.reasonCode}）` : ""}`,
          );
  const recognizeButton =
    session &&
    (recognitionModes !== undefined ? (
      imageModes.map((mode) => (
        <Button
          key={mode.displayName}
          title={mode.hint ?? mode.displayName}
          aria-label={mode.displayName}
          appearance="primary"
          size="small"
          disabled={
            !canRecognize ||
            isExporting ||
            !!textEdit ||
            mode.availability !== "ready" ||
            !mode.modeId
          }
          onClick={() => void recognizeCurrentImage(mode.modeId)}
        >
          {mode.displayName}
        </Button>
      ))
    ) : (
      <Button
        title="识别当前图"
        icon={<ToolbarIcon name="textRecognition" />}
        aria-label="识别当前图"
        appearance="primary"
        size="small"
        disabled={!canRecognize || isExporting || !!textEdit}
        onClick={() => void recognizeCurrentImage()}
      >
        {captureStyle ? undefined : "识别当前图"}
      </Button>
    ));
  const closeSessionButton = session && (
    <Button
      title={pinSnapshotApply ? "放弃更改" : "结束会话"}
      icon={<ToolbarIcon name="exit" />}
      aria-label={pinSnapshotApply ? "放弃更改" : "结束会话"}
      appearance="transparent"
      size="small"
      disabled={isExporting}
      onClick={() => void closeSession()}
    >
      {captureStyle ? undefined : pinSnapshotApply ? "放弃更改" : "结束会话"}
    </Button>
  );
  const clearEditsButton = (
    <Button
      title="清除编辑"
      icon={<ToolbarIcon name="clear" />}
      aria-label="清除编辑"
      appearance="transparent"
      size="small"
      disabled={!hasEdits || state.marks.some((mark) => mark.locked)}
      onClick={clearEdits}
    >
      {captureStyle ? undefined : "清除编辑"}
    </Button>
  );

  // “更多”菜单收纳现场低频操作（按当前可用状态）；菜单项保留原按钮
  // 的权限/忙态守卫，普通页直出按钮不收菜单。
  const moreActionsMenu = captureStyle && (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <ToolbarButton
          aria-label="更多操作"
          icon={<ToolbarIcon name="more" />}
          title="更多操作"
        />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItem
            disabled={!hasExclusions}
            icon={<ToolbarIcon name="clearExclusions" />}
            onClick={clearExclusions}
          >
            清空屏蔽
          </MenuItem>
          {hasExclusions && (
            <>
              <MenuItem
                disabled={!canExport || isExporting || !!textEdit}
                icon={<ToolbarIcon name="copyExcluded" />}
                onClick={() => void copyAnnotatedImage(true)}
              >
                复制屏蔽副本
              </MenuItem>
              <MenuItem
                disabled={!canExport || isExporting || !!textEdit}
                icon={<ToolbarIcon name="saveExcluded" />}
                onClick={() => void saveAnnotatedImage(true)}
              >
                保存屏蔽副本
              </MenuItem>
            </>
          )}
          <MenuItem
            disabled={!hasEdits}
            icon={<ToolbarIcon name="clear" />}
            onClick={clearEdits}
          >
            清除编辑
          </MenuItem>
        </MenuList>
      </MenuPopover>
    </Menu>
  );

  // 下排收起：仅当前工具参数（颜色/线宽/边框不透明度/填充/字号/强度、
  // 修补对比）或选中描边类标记的就地样式、会话取字偏好存在时渲染；
  // 上下文操作已归主排。
  const settingsRowActive =
    !!objectSelectionControl ||
    styleEditing ||
    fontTool ||
    (toolset === "full" &&
      (COLOR_TOOLS.includes(tool) || tool === "mosaic" || tool === "blur")) ||
    !!inpaintCompareControl ||
    !!effectModeControl ||
    !!effectIntensityControl ||
    !!spotlightDarknessControl ||
    !!magnifierZoomControl ||
    !!watermarkOpacityControl ||
    !!objectEraserHint ||
    !!inpaintMaskModeControl ||
    !!dupmatchCandidateControls ||
    !!autoTextControl;

  return (
    <div
      className={`canvas-editor${
        captureStyle ? " capture-scene-editor" : ""
      }${proofreading ? " is-proofreading" : ""}`}
      style={captureStyle}
      inert={interactionLocked}
      data-screenshot-session={session?.sessionId ?? ""}
      onKeyDown={(event) => {
        if (textComposing.current || isImeCompositionEvent(event.nativeEvent))
          return;
        if (textEdit) return;
        // 校对模式挂起本地编辑手势（Space 平移/Esc 关闭），不抢占校对交互。
        if (proofreading) return;
        const target = event.target as HTMLElement;
        // 文档命令由 window 消费，画布手势仍在本地处理。
        if (
          captureStyle &&
          session &&
          event.key === "Escape" &&
          tool === "select" &&
          selectedMark === undefined &&
          !isExporting &&
          !["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName)
        ) {
          event.preventDefault();
          void closeSession();
          return;
        }
        if (
          event.code === "Space" &&
          !["INPUT", "TEXTAREA", "SELECT", "BUTTON"].includes(target.tagName)
        ) {
          event.preventDefault();
          setSpaceHeld(true);
        }
      }}
      onKeyUp={(event) => {
        if (event.code === "Space") setSpaceHeld(false);
      }}
    >
      {captureStyle &&
        createPortal(
          <div
            className="capture-scene-backdrop"
            style={captureStyle}
            aria-hidden="true"
          >
            <img
              ref={backdropRef}
              src="/__capture_background"
              alt=""
              draggable={false}
              onLoad={(event) => {
                void presentation.current?.backgroundLoaded(
                  event.currentTarget,
                );
              }}
              onError={() => presentation.current?.fail("backgroundDecode")}
            />
            <div className="capture-scene-mask" />
          </div>,
          document.body,
        )}
      {/* 主排：全部工具与操作同排（过宽时容器内水平滚动保持单行可达，
          不把工具挪离主排）；下排仅当前工具属性，无属性时收起。 */}
      <div className="editor-toolbar-stack">
        <Toolbar
          aria-label="图片编辑工具"
          size="small"
          className={`editor-toolbar editor-toolbar-main${
            captureStyle ? " editor-toolbar-scene-primary" : ""
          }`}
        >
          {toolButtons}
          {textLayerControls}
          {inpaintOperations}
          {dupmatchOperations}
          {objectActionControls}
          {spotlightAddRegionControl}
          {rotateControl}
          {resetAttributesControl}
          {contentMark && !textEdit && (
            <ToolbarButton
              aria-label="编辑文字"
              onClick={() => beginTextEdit(selectedMark!)}
            >
              编辑文字
            </ToolbarButton>
          )}
          {textEdit && (
            <>
              <ToolbarButton aria-label="完成文字编辑" onClick={finishTextEdit}>
                完成文字
              </ToolbarButton>
              <ToolbarButton
                aria-label="取消文字编辑"
                onClick={() => {
                  cancelDrawing();
                  setOperationMessage("已取消文字编辑，原文字保留。");
                }}
              >
                取消文字
              </ToolbarButton>
            </>
          )}
          {activeFontTool === "numbering" &&
            state.marks.some((mark) => mark.tool === "numbering") && (
              <ToolbarButton
                aria-label="重新编号"
                disabled={state.marks.some(
                  (mark) => mark.tool === "numbering" && mark.locked,
                )}
                onClick={renumber}
              >
                重新编号
              </ToolbarButton>
            )}
          {captureStyle ? null : clearExclusionsButton}
          {undoButton}
          {redoButton}
          {copyAnnotatedButton}
          {captureStyle ? null : copyExcludedButton}
          {saveAnnotatedButton}
          {captureStyle ? null : saveExcludedButton}
          {pinSnapshotApply ? null : saveProjectButton}
          {applyPinSnapshotButton}
          {pinButton}
          {pinSnapshotApply ? null : recognizeButton}
          {closeSessionButton}
          {captureStyle ? null : clearEditsButton}
          {moreActionsMenu}
        </Toolbar>
        {session && !canRecognize && !pinSnapshotApply && (
          <p className="form-note">
            当前宿主未提供图片识别能力；请检查运行环境。
          </p>
        )}
        {session && unavailableModeHints.length > 0 && (
          <p className="form-note">
            {unavailableModeHints.join("；")}
            。请在设置中检查运行环境与默认识别类型。
          </p>
        )}
        {settingsRowActive ? (
          <Toolbar
            aria-label="当前工具设置"
            size="small"
            className="editor-toolbar editor-toolbar-settings"
          >
            {objectSelectionControl}
            {annotationTextInput}
            {stylePanel}
            {fontSizeControl}
            {activeFontTool && (
              <AnnotationTextPanel
                style={activeTextStyle}
                tool={activeFontTool}
                onChange={applyTextStyle}
                onResolveFont={resolveFont}
              />
            )}
            {activeFontTool === "numbering" && (
              <AnnotationNumberInput
                key={fontMark ? "mark-" + selectedMark : "number-start"}
                label={fontMark ? "序号值" : "序号起点"}
                value={fontMark?.ordinal ?? activeToolAttributes.numberStart}
                onValueChange={(value) => {
                  if (fontMark) {
                    if (value !== fontMark.ordinal)
                      updateFontMark({ ordinal: value });
                  } else updateToolAttribute({ numberStart: value });
                }}
              />
            )}
            {effectModeControl}
            {effectBrushSizeControl}
            {effectIntensityControl}
            {spotlightDarknessControl}
            {magnifierZoomControl}
            {watermarkOpacityControl}
            {objectEraserHint}
            {autoTextControl}
            {inpaintMaskModeControl}
            {inpaintBrushSizeControl}
            {inpaintCompareControl}
            {dupmatchCandidateControls}
          </Toolbar>
        ) : null}
      </div>
      {toolset === "full" && (
        <details
          className="editor-output-settings"
          open={captureStyle ? undefined : true}
        >
          <summary aria-label="输出设置" title="输出设置">
            <ToolbarIcon name="settings" />
          </summary>
          <Toolbar
            aria-label="输出设置"
            size="small"
            className="editor-toolbar"
          >
            <label className="editor-style-control">
              <span aria-hidden="true">输出格式</span>
              <Select
                aria-label="输出格式"
                size="small"
                value={outputFormat}
                onChange={(_, data) =>
                  changeOutputFormat(
                    data.value === "image/jpeg" ? "image/jpeg" : "image/png",
                  )
                }
              >
                <option value="image/png">PNG（无损）</option>
                <option value="image/jpeg">JPEG（白底）</option>
              </Select>
            </label>
            {outputFormat === "image/jpeg" && (
              <label className="editor-style-control">
                <span aria-hidden="true">JPEG 质量</span>
                <Select
                  aria-label="JPEG 质量"
                  size="small"
                  value={String(jpegQuality)}
                  onChange={(_, data) => changeJpegQuality(Number(data.value))}
                >
                  {JPEG_QUALITIES.map((quality) => (
                    <option key={quality} value={String(quality)}>
                      {Math.round(quality * 100)}%
                    </option>
                  ))}
                </Select>
              </label>
            )}
            <label className="editor-style-control">
              <span aria-hidden="true">等比缩放</span>
              <Select
                aria-label="等比缩放"
                size="small"
                value=""
                onChange={(_, data) => {
                  if (data.value) applyScalePercent(Number(data.value));
                }}
              >
                <option value="">选择比例</option>
                {SCALE_PERCENTS.map((percent) => (
                  <option key={percent} value={String(percent)}>
                    {percent === 100 ? "100%（原始）" : `${percent}%`}
                  </option>
                ))}
              </Select>
            </label>
            <label className="editor-style-control">
              <span aria-hidden="true">输出尺寸</span>
              <Input
                aria-label="输出宽度"
                min={1}
                size="small"
                style={{ width: 76 }}
                type="number"
                value={sizeDraft.width}
                onChange={(_, data) =>
                  setSizeDraft((current) => ({ ...current, width: data.value }))
                }
              />
              <span aria-hidden="true">×</span>
              <Input
                aria-label="输出高度"
                min={1}
                size="small"
                style={{ width: 76 }}
                type="number"
                value={sizeDraft.height}
                onChange={(_, data) =>
                  setSizeDraft((current) => ({
                    ...current,
                    height: data.value,
                  }))
                }
              />
              <ToolbarButton aria-label="应用尺寸" onClick={applySpecifiedSize}>
                应用
              </ToolbarButton>
            </label>
            <ToolbarButton
              aria-label="检查文件大小"
              disabled={isCheckingExport || !!textEdit}
              onClick={() => void checkFileSize()}
            >
              检查文件大小
            </ToolbarButton>
          </Toolbar>
        </details>
      )}
      <p className="editor-guidance">
        {toolset === "full"
          ? captureStyle
            ? "拖拽绘制标注；拖动选区四边与四角可调整裁剪范围。矩形/椭圆选中后可拖八点手柄缩放，直线/箭头/说明可拖端点；文字可双击继续编辑，上排完成或取消。说明从指向位置拖到文字位置。按住 Shift 约束正方形/正圆/45°，按住 Alt 以起点为中心绘制；折线单击加点、双击或 Enter 完成，Esc 放弃草稿。手形或按住 Space 可平移；滚轮可调线宽。马赛克/模糊支持矩形与笔刷，强度连续可调（兼容旧弱/中/强档位），选中已绘区域可再调强度；遮挡为纯色不透明像素，与屏蔽区（仅识别输入）分开；聚光灯在底图范围内按对象层级叠加，一对象多区域强调（选中后可添加区域、拖手柄编辑、Alt+点击删区域），放大镜采样底图与已应用的修补；效果写入复制、保存副本及显式识别输入；画面缩放不改变内容。"
            : "拖拽绘制或裁剪；选择标注后可拖动，矩形/椭圆可拖八点手柄缩放，直线/箭头/说明可拖端点；文字可双击继续编辑，上排完成或取消。说明从指向位置拖到文字位置；已提交裁剪框的四边/四角可继续拖拽调整。按住 Shift 约束正方/正圆/45°，按住 Alt 以起点为中心绘制；折线单击加点、双击或 Enter 完成，Esc 放弃草稿。手形或按住 Space 可平移；滚轮可调线宽。选择工具下可 Ctrl+点击增减选择、空白拖动框选（完整包含）、Ctrl+A 全选，上排提供复制/粘贴/重复、层级、对齐等距、组合/锁定与旋转/镜像/缩放；方向键微移、Delete 删除。马赛克/模糊支持矩形与笔刷，强度连续可调（兼容旧弱/中/强档位），选中已绘区域可再调强度；遮挡为纯色不透明像素，与屏蔽区（仅识别输入）分开；聚光灯在底图范围内按对象层级叠加，拖拽创建多区域强调（选中后“添加区域”新增孔洞、拖八点手柄编辑单区域、Alt+点击删除区域，整对象可移动/撤销），放大镜为可移动导出对象（采样底图与已应用的修补，不采样标注），橡皮擦一次拖动擦除命中未锁定对象（整次一次撤销）；文字/图片对象可调不透明度作水印。效果写入复制、保存副本及显式识别输入；画面缩放不改变内容。"
          : "拖拽框选“屏蔽”区，识别时忽略其中内容；“去水印”先框选、预览修补再应用；旋转调整整图方向。按住 Space 可平移，画面缩放不改变内容。"}
      </p>
      {tool === "inpaint" && (
        <p className="editor-guidance">
          去水印（Beta）：拖拽框选要去水印的区域，先“预览修补”对比前后效果，确认后“应用修补”才写入编辑历史（可撤销，原图始终保留）；应用前可随时取消。
          掩膜支持矩形/笔刷的添加与移除：掩膜内的洞与掩膜外每字节（含透明度）原样保留；整图≤8MP、单次掩膜≤1MP，历史修补总量另有预算，超限会明确提示而不是丢弃撤销。
          本地修补基于周边颜色扩散，复杂纹理或大面积覆盖效果有限，不是无损还原。
        </p>
      )}
      {tool === "dupmatch" && (
        <p className="editor-guidance">
          重复匹配：拖拽框选一处重复内容（如水印），点击“查找重复”在同尺度平移下搜索其他副本（低纹理或超大图会明确拒绝）；候选可逐项勾选增删，
          “确认应用”才一次性写入遮挡补丁（取候选周边环境色、不透明真实像素，可撤销）；确认前不修改内容，换图/编辑/选区变化会作废候选。
        </p>
      )}
      <p className="editor-guidance">
        屏蔽区只用于识别输入及显式屏蔽副本；普通复制/保存与贴图画面保留原图内容，贴图取字仍会忽略被屏蔽文字。整图被屏蔽时无法识别。
      </p>
      <p className="editor-guidance">
        {toolset === "full"
          ? sourceSize && currentOutputSize
            ? `原图 ${sourceSize.width}×${sourceSize.height}${
                typeof sourceByteLength === "number"
                  ? ` · ${formatBytes(sourceByteLength)}`
                  : ""
              } → 输出 ${currentOutputSize.width}×${
                currentOutputSize.height
              } · ${
                outputFormat === "image/jpeg"
                  ? `JPEG 质量 ${Math.round(jpegQuality * 100)}%`
                  : "PNG 无损"
              }。${
                outputFormat === "image/jpeg"
                  ? "JPEG 不保留透明：透明区域将合成白色背景。"
                  : "PNG 保留透明度；文件大小可用“检查文件大小”按需查看。"
              }`
            : "图片解码完成后显示原图与输出尺寸。"
          : sourceSize
            ? `原图 ${sourceSize.width}×${sourceSize.height}${
                typeof sourceByteLength === "number"
                  ? ` · ${formatBytes(sourceByteLength)}`
                  : ""
              } · 识别、复制与保存使用原始像素的 PNG。`
            : "图片解码完成后显示原图尺寸。"}
      </p>
      {tool === "textSelect" && textLayerHint && (
        <p className="editor-guidance">{textLayerHint}</p>
      )}
      <label className="editor-style-control">
        <span>显示缩放</span>
        <Select
          aria-label="显示缩放"
          size="small"
          value={String(zoom)}
          onChange={(_, data) => setZoom(Number(data.value))}
        >
          {[0.5, 0.75, 1, 1.25, 1.5, 2].map((value) => (
            <option key={value} value={String(value)}>
              {Math.round(value * 100)}%
            </option>
          ))}
        </Select>
      </label>
      <div
        aria-label="图片视口"
        className={`canvas-stage${panning ? " is-panning" : ""}`}
        ref={viewportRef}
        tabIndex={0}
        style={{
          ["--editor-zoom" as string]: zoom,
          overflow: "auto",
          maxHeight: "70vh",
          position: "relative",
          cursor: panning ? "grab" : undefined,
        }}
        onContextMenu={(event) => {
          // 校对模式：右键/选区交互归校对层，编辑器不拦截。
          if (proofreading) return;
          const text = readLayerSelection();
          if (tool !== "textSelect" || !text) {
            setCopyMenu(undefined);
            return;
          }
          event.preventDefault();
          const bounds = event.currentTarget.getBoundingClientRect();
          setCopyMenu({
            x: event.clientX - bounds.left + event.currentTarget.scrollLeft,
            y: event.clientY - bounds.top + event.currentTarget.scrollTop,
            text,
            key: layerSelectionKey,
          });
        }}
        onPointerDown={(event) => {
          // 校对模式：不进入平移捕获，保留容器原生滚动。
          if (proofreading) return;
          if (event.button === 0) setCopyMenu(undefined);
          if (!panning) return;
          panStart.current = {
            x: event.clientX,
            y: event.clientY,
            left: event.currentTarget.scrollLeft,
            top: event.currentTarget.scrollTop,
          };
          event.currentTarget.setPointerCapture?.(event.pointerId);
          event.preventDefault();
        }}
        onPointerMove={(event) => {
          const start = panStart.current;
          if (!start) return;
          event.currentTarget.scrollLeft =
            start.left - (event.clientX - start.x);
          event.currentTarget.scrollTop = start.top - (event.clientY - start.y);
        }}
        onPointerUp={(event) => {
          panStart.current = undefined;
          if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
            event.currentTarget.releasePointerCapture(event.pointerId);
          }
        }}
        onPointerCancel={() => {
          panStart.current = undefined;
        }}
      >
        {proofreading && proofread ? (
          // 左侧校对面：冻结参考图 + 结果行框投影；编辑画布仅隐藏，切换回
          // 编辑模式时未提交编辑与历史原样恢复。
          <ResultImageTextOverlay
            key={proofread.bindingKey}
            imageUrl={proofread.imageUrl}
            lines={proofread.lines}
            onUpdateLine={proofread.onUpdateLine}
          />
        ) : layerReady ? (
          <div
            className="image-text-frame"
            ref={stageRef}
            style={{
              margin: "0 auto",
              position: "relative",
            }}
          >
            <img
              alt="截图最终画面"
              className="inspection-canvas"
              src={layerImageUrl}
              style={{ width: "100%" }}
            />
            {layerImage && stageSize && textLayer?.binding && session ? (
              <ImageTextLayer
                activeSession={{
                  sessionId: session.sessionId,
                  revision: localRevision,
                }}
                binding={textLayer.binding}
                lines={visibleLayerLines}
                viewport={{ image: layerImage, size: stageSize }}
              />
            ) : null}
          </div>
        ) : null}
        <canvas
          aria-label="图片检查画布"
          data-coordinate-width={
            captureStyle ? captureGeometry?.width : (projectFrame?.width ?? 900)
          }
          data-coordinate-height={
            captureStyle
              ? captureGeometry?.height
              : (projectFrame?.height ?? 600)
          }
          className={`inspection-canvas tool-${tool}`}
          height={
            captureStyle
              ? captureGeometry?.height
              : (projectFrame?.height ?? 600)
          }
          style={{
            aspectRatio: captureStyle
              ? `${captureGeometry?.width} / ${captureGeometry?.height}`
              : `${projectFrame?.width ?? 900} / ${projectFrame?.height ?? 600}`,
            display: layerReady || proofreading ? "none" : "block",
            cursor: panning
              ? "grab"
              : tool === "select"
                ? "default"
                : tool === "text" || tool === "textSelect"
                  ? "text"
                  : "crosshair",
            opacity: captureStyle && !sourceSize ? 0 : undefined,
          }}
          onKeyDown={(event) => {
            // IME 组合（含标注文字输入结束前迟到的 229 键）不触发撤销/
            // 删除/手势取消；非组合行为保持不变。
            if (
              textComposing.current ||
              isImeCompositionEvent(event.nativeEvent)
            )
              return;
            if (event.key === "Escape" && colorPickPendingRef.current) {
              // 取消画布取色：不触发选区清除/工具切换/会话关闭。
              event.preventDefault();
              event.stopPropagation();
              colorPickPendingRef.current = false;
              setOperationMessage("已取消取色；标注颜色保持不变。");
              return;
            }
            if (textEdit) {
              if (event.key === "Escape") {
                event.preventDefault();
                event.stopPropagation();
                cancelDrawing();
                setOperationMessage("已取消文字编辑，原文字保留。");
              }
              return;
            }
            // Enter 完成折线草稿：监听只挂画布，焦点在输入框时不触发。
            if (
              event.key === "Enter" &&
              draftMark.current?.tool === "polyline"
            ) {
              event.preventDefault();
              commitPolylineDraft();
              return;
            }
            const modifier = event.ctrlKey || event.metaKey;
            const key = event.key.toLowerCase();
            if (modifier && key === "z") {
              event.preventDefault();
              if (event.shiftKey) redo();
              else undo();
            } else if (modifier && key === "y") {
              event.preventDefault();
              redo();
            } else if (tool !== "select" && event.key !== "Escape") {
              return;
            } else if (modifier && key === "a") {
              // 全选未锁定对象（含锁定对象跳过，可在列表选择/解锁）。
              event.preventDefault();
              const ids = unlockedMarkIds(state.marks);
              if (ids.size > 0) {
                const topmost = state.marks.findLast((mark) =>
                  ids.has(mark.id),
                )?.id;
                setObjectSelection({ ids, primary: topmost });
              }
            } else if (modifier && key === "c") {
              event.preventDefault();
              copySelection();
            } else if (modifier && key === "v") {
              event.preventDefault();
              pasteClipboard();
            } else if (modifier && key === "d") {
              event.preventDefault();
              duplicateSelection();
            } else if (
              (event.key === "ArrowLeft" ||
                event.key === "ArrowRight" ||
                event.key === "ArrowUp" ||
                event.key === "ArrowDown") &&
              selectionRef.current.ids.size > 0
            ) {
              // 微移仅画布上下文：焦点在输入框时不进入本处理器。
              event.preventDefault();
              const step = event.shiftKey ? 10 : 1;
              const delta =
                event.key === "ArrowLeft"
                  ? { x: -step, y: 0 }
                  : event.key === "ArrowRight"
                    ? { x: step, y: 0 }
                    : event.key === "ArrowUp"
                      ? { x: 0, y: -step }
                      : { x: 0, y: step };
              nudgeSelection(delta);
            } else if (event.key === "Delete") {
              event.preventDefault();
              deleteSelection();
            } else if (event.key === "Escape") {
              cancelDrawing();
              select(undefined);
              setTool("select");
            }
          }}
          onDoubleClick={(event) => {
            // 双击完成折线草稿：恰好一条历史；其他工具无草稿时忽略。
            if (draftMark.current?.tool === "polyline") {
              commitPolylineDraft();
            } else {
              const id = findMark(
                state.marks,
                point(event),
                canvasRef.current?.getContext("2d") ?? undefined,
              );
              if (id !== undefined) beginTextEdit(id);
            }
          }}
          onPointerCancel={cancelDrawing}
          onPointerDownCapture={(event) => {
            const input = document.activeElement;
            if (
              input instanceof HTMLInputElement &&
              input.closest(".canvas-editor") ===
                event.currentTarget.closest(".canvas-editor") &&
              ["边框不透明度", "填充不透明度", "水印不透明度"].includes(
                input.getAttribute("aria-label") ?? "",
              )
            ) {
              // 失焦提交先于本次绘制冻结样式，避免 pointerdown 默认聚焦晚于取样。
              flushSync(() => input.blur());
            }
          }}
          onPointerDown={pointerDown}
          onPointerMove={pointerMove}
          onPointerUp={pointerUp}
          ref={attachCanvas}
          tabIndex={0}
          width={
            captureStyle ? captureGeometry?.width : (projectFrame?.width ?? 900)
          }
        />
        {copyMenu?.key === layerSelectionKey && copyMenu.text && (
          <Button
            appearance="primary"
            size="small"
            onPointerDown={(event) => event.stopPropagation()}
            style={{
              position: "absolute",
              left: copyMenu.x,
              top: copyMenu.y,
              zIndex: 2,
            }}
            onClick={() => copySelectedText(copyMenu)}
          >
            复制所选
          </Button>
        )}
      </div>
      <div className="editor-footer">
        <output aria-live="polite" className="editor-operation-status">
          {canExport
            ? operationMessage
            : "当前宿主不支持标注图片复制或保存；编辑预览不会改变识别结果。"}
        </output>
      </div>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

/** 导出完成后的真实体积描述；体积来自实际编码后的 blob。 */
function formatLabel(exported: {
  mediaType: string;
  byteLength: number;
}): string {
  return `${exported.mediaType} · ${formatBytes(exported.byteLength)}`;
}

function textLayerStatusLabel(
  layer: ScreenshotTextLayerState | undefined,
): string | undefined {
  if (!layer) {
    return "取字模式：进入或新截图后自动用本地轻量文字引擎准备可选文字层。";
  }
  switch (layer.status) {
    case "textlayer.preparing":
      return "正在准备原位文字层……可取消；复制/保存图片不受影响。";
    case "textlayer.ready":
      return "在图片上拖动选择文字：支持行内中英文子串与跨行；Ctrl+C 或“复制所选”仅复制所选内容。";
    case "textlayer.unavailable":
      return layer.reason === "textlayer.serviceUnavailable"
        ? "本地识别服务尚未就绪；截图和贴图仍可用，连接恢复后可重新准备文字层。"
        : layer.reason === "textlayer.modeNotReady"
          ? "本地轻量文字引擎未就绪；自动取字不会安装依赖，可先在设置中完成准备。"
          : "无可用本地轻量文字引擎；自动取字不会安装依赖或使用远程。";
    case "textlayer.cancelled":
      return "已取消准备文字层；可点“重新准备”重试。";
    case "textlayer.empty":
      return "图片中没有可选择的文字。";
    case "textlayer.failed":
      return layer.reason === "textlayer.tooLarge"
        ? "识别文本过大，无法安全显示为原位文字层。"
        : "文字层准备失败；可点“重新准备”重试。";
    case "textlayer.expired":
      return "内容已更新，旧文字层已失效；等待或重新准备后可继续取字。";
    default:
      return undefined;
  }
}

/** 旧 markBounds 局部实现由 annotationObjects.markBoundsOf 取代（旋转感知）。 */

function moveMark(mark: Mark, delta: Point): Mark {
  const move = (at: Point) => ({ x: at.x + delta.x, y: at.y + delta.y });
  // 聚光灯区域随同一增量映射：拖拽预览与 moveMarkIds 提交同几何，
  // 松开不跳变（H1）。
  return {
    ...mark,
    start: move(mark.start),
    end: move(mark.end),
    ...(mark.points ? { points: mark.points.map(move) } : {}),
    ...(mark.regions
      ? {
          regions: mark.regions.map((region) => {
            const cornerA = move({ x: region.x, y: region.y });
            const cornerB = move({
              x: region.x + region.width,
              y: region.y + region.height,
            });
            return {
              x: Math.min(cornerA.x, cornerB.x),
              y: Math.min(cornerA.y, cornerB.y),
              width: Math.abs(cornerB.x - cornerA.x),
              height: Math.abs(cornerB.y - cornerA.y),
            };
          }),
        }
      : {}),
  };
}

function drawCropPreview(
  canvas: HTMLCanvasElement,
  crop: NonNullable<EditorState["crop"]>,
) {
  const context = canvas.getContext("2d");
  if (!context) return;
  const size = canvasSize(canvas);
  const area = clampRect(normalizedRect(crop), size);
  const right = area.x + area.width,
    bottom = area.y + area.height;
  context.save();
  context.fillStyle = "rgba(0, 0, 0, 0.35)";
  context.fillRect(0, 0, size.width, area.y);
  context.fillRect(0, area.y, area.x, area.height);
  context.fillRect(right, area.y, size.width - right, area.height);
  context.fillRect(0, bottom, size.width, size.height - bottom);
  context.strokeStyle = "#f38b35";
  context.lineWidth = 1;
  context.setLineDash([8, 5]);
  context.strokeRect(area.x, area.y, area.width, area.height);
  context.restore();
  drawHandleSquares(context, area, "#f38b35");
}

/** 截图现场闲置选区手柄：已提交裁剪在其框上，否则覆盖整幅画布四边/四角。 */
function drawSceneCropHandles(
  canvas: HTMLCanvasElement,
  crop: EditorState["crop"],
) {
  const context = canvas.getContext("2d");
  if (!context) return;
  const area = crop
    ? clampRect(normalizedRect(crop), canvasSize(canvas))
    : { x: 0, y: 0, ...canvasSize(canvas) };
  if (area.width <= 0 || area.height <= 0) return;
  drawHandleSquares(context, area, "#f38b35");
}

/** 八点手柄：白底方块 + 主题色描边，可拖拽的视觉惯示。 */
function drawHandleSquares(
  context: CanvasRenderingContext2D,
  area: { x: number; y: number; width: number; height: number },
  stroke: string,
) {
  context.save();
  context.setLineDash([]);
  context.fillStyle = "#ffffff";
  context.strokeStyle = stroke;
  for (const handle of EXCLUSION_RESIZE_HANDLES) {
    const at = exclusionHandlePoint(area, handle);
    context.fillRect(at.x - 4, at.y - 4, 8, 8);
    context.strokeRect(at.x - 4, at.y - 4, 8, 8);
  }
  context.restore();
}

/** 端点手柄（直线/箭头）：与八点手柄同样式的白底方块。 */
function drawPointHandleSquares(
  context: CanvasRenderingContext2D,
  points: readonly Point[],
  stroke: string,
) {
  context.save();
  context.setLineDash([]);
  context.fillStyle = "#ffffff";
  context.strokeStyle = stroke;
  for (const at of points) {
    context.fillRect(at.x - 4, at.y - 4, 8, 8);
    context.strokeRect(at.x - 4, at.y - 4, 8, 8);
  }
  context.restore();
}

/** 虚线 [12,8]；点线圆头间距至少两倍线宽，避免粗线端帽重叠。
 * 固定长度随自然导出比例与对象比例缩放，线宽已烘焙对象比例。 */
function applyDashPattern(
  context: CanvasRenderingContext2D,
  style: AnnotationLineStyle | undefined,
  markScale: number,
  lineWidth: number,
): void {
  if (style === "dashed") {
    context.setLineDash([12 * markScale, 8 * markScale]);
    context.lineCap = "butt";
  } else if (style === "dotted") {
    context.setLineDash([
      2 * markScale,
      Math.max(6 * markScale, lineWidth * 2),
    ]);
    context.lineCap = "round";
  } else {
    context.setLineDash([]);
    context.lineCap = "butt";
  }
}

function canvasSize(canvas: HTMLCanvasElement): CanvasSize {
  return {
    width: Number(canvas.dataset.coordinateWidth) || canvas.width,
    height: Number(canvas.dataset.coordinateHeight) || canvas.height,
  };
}

/** 分块 base64：避免大补丁一次展开超过调用栈。 */
function arrayBufferToBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  const chunk = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
  }
  return btoa(binary);
}

function draw(
  canvas: HTMLCanvasElement | null,
  image: HTMLImageElement | undefined,
  state: EditorState,
  selectedIds: ReadonlySet<string> | undefined,
  marksOverride?: readonly Mark[],
  showEditorChrome = true,
  markScale = 1,
  /** 仅 JPEG 显式白底时传色；缺省完全不填底色，PNG 透明 alpha 原样保留。 */
  background?: string,
  bakeExclusions = false,
  inpaintLayer?: InpaintDrawLayer,
  cached?: { canvas: HTMLCanvasElement; markCount: number },
  overlays = true,
  /** #232 导入图片资产（assetId → 已解码位图）；缺失的资产不绘制。 */
  imageAssets?: ReadonlyMap<string, CanvasImageSource>,
) {
  const context = canvas?.getContext("2d");
  if (!canvas || !context) return;
  const size = canvasSize(canvas);
  context.setTransform(
    canvas.width / size.width,
    0,
    0,
    canvas.height / size.height,
    0,
    0,
  );
  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = "high";
  const marks = marksOverride ?? state.marks;
  context.clearRect(0, 0, size.width, size.height);
  if (cached) {
    context.drawImage(cached.canvas, 0, 0, size.width, size.height);
  } else {
    // 底色只用于显式合成（JPEG 白底）；无底色时保持透明，不烧任何深色。
    if (background) {
      context.fillStyle = background;
      context.fillRect(0, 0, size.width, size.height);
    }
    context.save();
    if (state.crop) {
      context.beginPath();
      context.rect(
        Math.min(state.crop.start.x, state.crop.end.x),
        Math.min(state.crop.start.y, state.crop.end.y),
        Math.abs(state.crop.end.x - state.crop.start.x),
        Math.abs(state.crop.end.y - state.crop.start.y),
      );
      context.clip();
    }
    if (image) {
      const quarterTurn = state.rotation % 180 !== 0;
      const sourceWidth = quarterTurn
        ? image.naturalHeight
        : image.naturalWidth;
      const sourceHeight = quarterTurn
        ? image.naturalWidth
        : image.naturalHeight;
      const scale = Math.min(
        size.width / sourceWidth,
        size.height / sourceHeight,
      );
      context.save();
      context.translate(size.width / 2, size.height / 2);
      context.rotate((state.rotation * Math.PI) / 180);
      context.drawImage(
        image,
        (-image.naturalWidth * scale) / 2,
        (-image.naturalHeight * scale) / 2,
        image.naturalWidth * scale,
        image.naturalHeight * scale,
      );
      // 已应用补丁与未提交预览在同一图像本地坐标系内叠加：矩形为
      // 原图（未旋转）像素空间，缩放/旋转与上图一致，导出时 1:1 无重采样。
      const drawPatch = (
        source: HTMLCanvasElement,
        rect: InpaintRect,
      ): void => {
        const dx = (-image.naturalWidth * scale) / 2 + rect.x * scale;
        const dy = (-image.naturalHeight * scale) / 2 + rect.y * scale;
        const dw = rect.width * scale;
        const dh = rect.height * scale;
        // 替换式绘制：先在补丁矩形内回填底色（PNG 为清除，保持透明），
        // 再叠加补丁像素；半透明补丁透出底色/透明而非被修补前的原图。
        context.save();
        context.beginPath();
        context.rect(dx, dy, dw, dh);
        context.clip();
        if (background) {
          context.fillStyle = background;
          context.fillRect(dx, dy, dw, dh);
        } else {
          context.clearRect(dx, dy, dw, dh);
        }
        context.drawImage(source, dx, dy, dw, dh);
        context.restore();
      };
      for (const entry of inpaintLayer?.patches ?? []) {
        drawPatch(entry.canvas, entry.rect);
      }
      if (inpaintLayer?.preview) {
        drawPatch(inpaintLayer.preview.canvas, inpaintLayer.preview.rect);
      }
      // 未提交掩膜/重复候选叠加层与补丁同坐标系（原图空间随旋转变换）；
      // 仅编辑预览，exportCanvas 只传 patches，绝不进入导出像素。
      if (inpaintLayer?.maskOverlay) {
        const overlay = inpaintLayer.maskOverlay;
        context.drawImage(
          overlay.canvas,
          (-image.naturalWidth * scale) / 2 + overlay.rect.x * scale,
          (-image.naturalHeight * scale) / 2 + overlay.rect.y * scale,
          overlay.rect.width * scale,
          overlay.rect.height * scale,
        );
      }
      if (inpaintLayer?.duplicateOverlay?.length) {
        context.save();
        context.lineWidth = 2;
        context.font = "12px sans-serif";
        for (const entry of inpaintLayer.duplicateOverlay) {
          const dx = (-image.naturalWidth * scale) / 2 + entry.rect.x * scale;
          const dy = (-image.naturalHeight * scale) / 2 + entry.rect.y * scale;
          context.setLineDash(entry.accepted ? [] : [5, 4]);
          context.strokeStyle = entry.accepted ? "#12a150" : "#8a8886";
          context.strokeRect(
            dx,
            dy,
            entry.rect.width * scale,
            entry.rect.height * scale,
          );
          if (entry.label) {
            context.setLineDash([]);
            context.fillStyle = entry.accepted ? "#12a150" : "#8a8886";
            context.fillText(entry.label, dx + 4, dy + 14);
          }
        }
        context.restore();
      }
      context.restore();
    }
    context.restore();
  }
  context.lineWidth = 3 * markScale;
  context.strokeStyle = "#f38b35";
  // #235 放大镜：需绘制时先重绘一份“底图与已应用的修补”的干净基底供采样。
  let magnifierBase: HTMLCanvasElement | undefined;
  for (let index = cached?.markCount ?? 0; index < marks.length; index += 1) {
    if (marks[index]!.tool === "magnifier") {
      magnifierBase = renderMagnifierBase(
        canvas,
        image,
        state,
        markScale,
        background,
        inpaintLayer,
        imageAssets,
        bakeExclusions,
      );
      break;
    }
  }
  marks.forEach((mark, index) => {
    if (index < (cached?.markCount ?? 0)) return;
    if (mark.tool === "image") {
      const source = mark.assetId ? imageAssets?.get(mark.assetId) : undefined;
      const x = Math.min(mark.start.x, mark.end.x);
      const y = Math.min(mark.start.y, mark.end.y);
      const width = Math.abs(mark.end.x - mark.start.x);
      const height = Math.abs(mark.end.y - mark.start.y);
      if (source && width > 0 && height > 0) {
        context.save();
        context.setLineDash([]);
        // #235 图片水印透明度：复用通用 strokeOpacity（缺省 100 不变）。
        context.globalAlpha = annotationStrokeAlpha(mark);
        if (mark.rotation || mark.flipX || mark.flipY) {
          const centerX = x + width / 2;
          const centerY = y + height / 2;
          context.translate(centerX, centerY);
          context.rotate(((mark.rotation ?? 0) * Math.PI) / 180);
          context.scale(mark.flipX ? -1 : 1, mark.flipY ? -1 : 1);
          context.translate(-centerX, -centerY);
        }
        context.drawImage(source, x, y, width, height);
        context.restore();
      }
    }
    const width = mark.end.x - mark.start.x;
    const height = mark.end.y - mark.start.y;
    const color = mark.style?.color ?? DEFAULT_COLOR;
    const lineWidth = annotationStrokeWidth(mark) * markScale;
    const strokeAlpha = annotationStrokeAlpha(mark);
    if (mark.tool === "rectangle") {
      context.setLineDash([]);
      context.lineWidth = lineWidth;
      context.strokeStyle = color;
      const fill = annotationFill(mark);
      const radius = Math.min(
        (mark.style?.cornerRadius ?? 0) * markScale,
        Math.min(Math.abs(width), Math.abs(height)) / 2,
      );
      if (radius > 0 && typeof context.roundRect === "function") {
        // 圆角路径：填充先铺底、边框后描，两者颜色/不透明度独立。
        const area = normalizedRect(mark);
        context.beginPath();
        context.roundRect(area.x, area.y, area.width, area.height, radius);
        if (fill) {
          context.save();
          context.globalAlpha = fill.alpha;
          context.fillStyle = fill.color;
          context.fill();
          context.restore();
        }
        context.save();
        context.globalAlpha = strokeAlpha;
        applyDashPattern(
          context,
          mark.style?.lineStyle,
          markScale * (mark.objectScale ?? 1),
          lineWidth,
        );
        context.stroke();
        context.restore();
      } else {
        // 直角/旧路径：fillRect+strokeRect 保持旧对象像素不变。
        if (fill) {
          context.save();
          context.globalAlpha = fill.alpha;
          context.fillStyle = fill.color;
          context.fillRect(mark.start.x, mark.start.y, width, height);
          context.restore();
        }
        context.save();
        context.globalAlpha = strokeAlpha;
        applyDashPattern(
          context,
          mark.style?.lineStyle,
          markScale * (mark.objectScale ?? 1),
          lineWidth,
        );
        context.strokeRect(mark.start.x, mark.start.y, width, height);
        context.restore();
      }
    } else if (mark.tool === "ellipse") {
      context.setLineDash([]);
      context.lineWidth = lineWidth;
      context.strokeStyle = color;
      context.beginPath();
      context.ellipse(
        mark.start.x + width / 2,
        mark.start.y + height / 2,
        Math.abs(width) / 2,
        Math.abs(height) / 2,
        0,
        0,
        Math.PI * 2,
      );
      const fill = annotationFill(mark);
      if (fill) {
        context.save();
        context.globalAlpha = fill.alpha;
        context.fillStyle = fill.color;
        // fill() 不清除当前路径：先按填充不透明度填充，再沿同一路径描边。
        context.fill();
        context.restore();
      }
      context.save();
      context.globalAlpha = strokeAlpha;
      applyDashPattern(
        context,
        mark.style?.lineStyle,
        markScale * (mark.objectScale ?? 1),
        lineWidth,
      );
      context.stroke();
      context.restore();
    } else if (mark.tool === "line" || mark.tool === "polyline") {
      // 直线/折线共用描边路径：折线用采样点，直线退化 start→end。折线
      // 草稿的活动 end 追加为最后一个顶点：拖拽/悬停中的新段实时可见；
      // 已提交标记 end == 最后顶点，不追加，渲染与旧像素不变。未提交
      // 草稿只进预览画布，导出/历史仍只包含已提交 points。
      const path = annotationPaths(mark)[0]!;
      const points = path.points;
      context.save();
      context.strokeStyle = color;
      context.lineWidth = lineWidth;
      context.globalAlpha = strokeAlpha;
      applyDashPattern(
        context,
        mark.style?.lineStyle,
        markScale * (mark.objectScale ?? 1),
        lineWidth,
      );
      context.lineCap = path.cap;
      context.lineJoin = path.join ?? "miter";
      context.beginPath();
      points.forEach((at, pointIndex) => {
        if (pointIndex === 0) context.moveTo(at.x, at.y);
        else context.lineTo(at.x, at.y);
      });
      if (points.length === 1) {
        const only = points[0]!;
        context.lineTo(only.x + 0.1, only.y);
      }
      context.stroke();
      context.restore();
    } else if (mark.tool === "arrow") {
      context.setLineDash([]);
      context.lineWidth = lineWidth;
      context.strokeStyle = color;
      context.save();
      context.globalAlpha = strokeAlpha;
      const geometry = annotationArrowGeometry(mark, markScale);
      const groups = [[geometry.shaft], ...geometry.heads];
      groups.forEach((paths, index) => {
        context.setLineDash([]);
        context.beginPath();
        for (const path of paths) {
          path.points.forEach((at, pointIndex) => {
            if (pointIndex === 0) context.moveTo(at.x, at.y);
            else context.lineTo(at.x, at.y);
          });
          if (path.closed) context.closePath();
        }
        if (paths.some((path) => path.filled)) {
          context.fillStyle = color;
          context.fill();
        }
        if (index === 0)
          applyDashPattern(
            context,
            mark.style?.lineStyle,
            markScale * (mark.objectScale ?? 1),
            lineWidth,
          );
        context.lineCap = paths[0]!.cap;
        context.lineJoin = paths[0]!.join ?? "miter";
        context.stroke();
      });
      context.restore();
    } else if (isContentMark(mark)) {
      context.save();
      // #235 文字水印透明度：复用通用 strokeOpacity（缺省 100 不变）。
      context.globalAlpha = annotationStrokeAlpha(mark);
      drawTextAnnotation(context, mark, markScale);
      context.restore();
    } else if (mark.tool === "mosaic") {
      applyMosaic(context, canvas, mark, markScale);
    } else if (mark.tool === "blur") {
      applyBlur(context, canvas, mark, markScale);
    } else if (mark.tool === "pen" || mark.tool === "highlighter") {
      const path = annotationPaths(mark)[0]!;
      const points = path.points;
      context.setLineDash([]);
      context.save();
      context.strokeStyle = color;
      context.lineCap = path.cap;
      context.lineJoin = path.join ?? "miter";
      if (mark.tool === "highlighter") {
        // 边框不透明度与荧光笔自身 35% 透明相乘：旧标记（缺省 100%）
        // 渲染保持不变，不覆盖原有透明语义。
        context.globalAlpha = 0.35 * strokeAlpha;
        context.lineWidth = lineWidth;
      } else {
        context.globalAlpha = strokeAlpha;
        context.lineWidth = lineWidth;
      }
      context.beginPath();
      points.forEach((at, pointIndex) => {
        if (pointIndex === 0) context.moveTo(at.x, at.y);
        else context.lineTo(at.x, at.y);
      });
      if (points.length === 1) {
        const only = points[0]!;
        context.lineTo(only.x + 0.1, only.y);
      }
      context.stroke();
      context.restore();
    } else if (mark.tool === "exclude") {
      if (showEditorChrome) {
        // 预览专用外观：红色虚线 + 斜纹，明确区别于普通标注；不入导出。
        drawExclusionChrome(
          context,
          mark,
          selectedIds?.has(mark.id) === true &&
            selectedIds.size === 1 &&
            !mark.locked,
        );
      } else if (bakeExclusions) {
        // 识别/屏蔽副本输入：外扩取整后的不透明白色硬覆盖，
        // 严格不保留原始文字像素（与 exclusionNaturalRects 同一取整）。
        const area = normalizedRect(mark);
        const x = Math.floor(area.x);
        const y = Math.floor(area.y);
        context.setLineDash([]);
        context.fillStyle = "#ffffff";
        context.fillRect(
          x,
          y,
          Math.ceil(area.x + area.width) - x,
          Math.ceil(area.y + area.height) - y,
        );
      }
      // 普通导出（bake=false）完全跳过：屏蔽不改变复制/保存/贴图像素。
    } else if (mark.tool === "solid") {
      // 纯色不透明遮挡：预览与导出同像素，写入普通导出与识别输入；
      // 与 exclude（仅识别语义、不改导出像素）明确分离。
      const area = normalizedRect(mark);
      if (area.width > 0 && area.height > 0) {
        context.setLineDash([]);
        context.fillStyle = normalizeHexColor(
          mark.style?.color ?? DEFAULT_SOLID_COLOR,
        );
        context.fillRect(
          Math.floor(area.x),
          Math.floor(area.y),
          Math.ceil(area.x + area.width) - Math.floor(area.x),
          Math.ceil(area.y + area.height) - Math.floor(area.y),
        );
      }
    } else if (mark.tool === "spotlight") {
      drawSpotlight(
        context,
        canvas,
        mark,
        showEditorChrome,
        selectedIds?.has(mark.id) === true &&
          selectedIds.size === 1 &&
          !mark.locked,
        image,
        state.rotation,
      );
    } else if (mark.tool === "magnifier") {
      drawMagnifier(
        context,
        canvas,
        mark,
        magnifierBase,
        markScale,
        background,
      );
    } else if (mark.tool === "numbering") {
      drawTextAnnotation(context, mark, markScale);
    }
    if (showEditorChrome && selectedIds?.has(mark.id) === true) {
      const bounds = markBoundsOf(mark, context);
      context.setLineDash([7, 5]);
      context.lineWidth = 1 * markScale;
      context.strokeStyle = "#f38b35";
      context.strokeRect(
        bounds.x - 5,
        bounds.y - 5,
        bounds.width + 10,
        bounds.height + 10,
      );
      // 几何编辑手柄：矩形/椭圆/遮挡/放大镜共用 isBoxResizableMark
      // （命中与渲染同一白名单，H5）；屏蔽标记自带八点手柄，不重复绘制。
      if (
        selectedIds?.size === 1 &&
        !mark.locked &&
        isBoxResizableMark(mark) &&
        !isExclusionMark(mark)
      ) {
        drawHandleSquares(context, normalizedRect(mark), "#f38b35");
      } else if (
        selectedIds?.size === 1 &&
        !mark.locked &&
        isEndpointMark(mark)
      ) {
        drawPointHandleSquares(
          context,
          isFontMark(mark)
            ? [
                transformTextPoint(mark, mark.start),
                transformTextPoint(mark, mark.end),
              ]
            : [mark.start, mark.end],
          "#f38b35",
        );
      }
    }
  });
  if (bakeExclusions) {
    // 识别/屏蔽副本的最终硬覆盖：任何在屏蔽块之后绘制的对象（放大镜的
    // 替换式重绘、聚光灯、任何效果）都不得把被屏蔽内容带回屏蔽区（H3）。
    // 普通导出（bake=false）不进入此分支，保留正常像素。
    context.save();
    context.setLineDash([]);
    context.fillStyle = "#ffffff";
    for (const mark of marks) {
      if (!isExclusionMark(mark)) continue;
      const area = normalizedRect(mark);
      const x = Math.floor(area.x);
      const y = Math.floor(area.y);
      context.fillRect(
        x,
        y,
        Math.ceil(area.x + area.width) - x,
        Math.ceil(area.y + area.height) - y,
      );
    }
    context.restore();
  }
  if (showEditorChrome && overlays && state.crop) {
    context.setLineDash([8, 5]);
    context.lineWidth = 1 * markScale;
    context.strokeStyle = "#f38b35";
    context.strokeRect(
      state.crop.start.x,
      state.crop.start.y,
      state.crop.end.x - state.crop.start.x,
      state.crop.end.y - state.crop.start.y,
    );
    // 已提交裁剪框叠加八点手柄：选择工具可直接拖边/角继续调整。
    drawHandleSquares(context, normalizedRect(state.crop), "#f38b35");
  }
  if (showEditorChrome && overlays && inpaintLayer?.selection) {
    // 选区高亮按映射后的有效修补范围回投显示（含外扩取整部分）。
    const selection = inpaintLayer.selection;
    context.setLineDash([6, 4]);
    context.lineWidth = 1.5 * markScale;
    context.strokeStyle = "#12a150";
    context.strokeRect(
      Math.min(selection.start.x, selection.end.x),
      Math.min(selection.start.y, selection.end.y),
      Math.abs(selection.end.x - selection.start.x),
      Math.abs(selection.end.y - selection.start.y),
    );
  }
  context.setLineDash([]);
}

/** 屏蔽区预览外观：半透明红底 + 斜纹 + 红色虚线框；选中时叠加八点缩放手柄。 */
function drawExclusionChrome(
  context: CanvasRenderingContext2D,
  mark: Mark,
  selected: boolean,
) {
  const area = normalizedRect(mark);
  if (area.width <= 0 || area.height <= 0) {
    if (selected) drawHandleSquares(context, area, "#e02020");
    return;
  }
  context.save();
  context.fillStyle = "rgba(224, 32, 32, 0.12)";
  context.fillRect(area.x, area.y, area.width, area.height);
  context.strokeStyle = "rgba(224, 32, 32, 0.7)";
  context.lineWidth = 1;
  context.setLineDash([]);
  context.beginPath();
  const step = 12;
  for (let x = area.x - area.height; x < area.x + area.width; x += step) {
    context.moveTo(Math.max(x, area.x), area.y);
    context.lineTo(
      Math.min(x + area.height, area.x + area.width),
      area.y + area.height,
    );
  }
  context.stroke();
  context.setLineDash([6, 4]);
  context.strokeStyle = "#e02020";
  context.strokeRect(area.x, area.y, area.width, area.height);
  if (selected) {
    drawHandleSquares(context, area, "#e02020");
  }
  context.restore();
}

const EXCLUSION_RESIZE_HANDLES = [
  "nw",
  "n",
  "ne",
  "e",
  "se",
  "s",
  "sw",
  "w",
] as const;

type ExclusionResizeHandle = (typeof EXCLUSION_RESIZE_HANDLES)[number];

/** 手柄在未归一化 start/end 矩形上的命中半径（画布内都坐标）。 */
const EXCLUSION_HANDLE_RADIUS = 12;

function exclusionHandlePoint(
  area: ReturnType<typeof normalizedRect>,
  handle: ExclusionResizeHandle,
): Point {
  return {
    x: handle.includes("w")
      ? area.x
      : handle.includes("e")
        ? area.x + area.width
        : area.x + area.width / 2,
    y: handle.includes("n")
      ? area.y
      : handle.includes("s")
        ? area.y + area.height
        : area.y + area.height / 2,
  };
}

/** 任意 start/end 矩形的八点手柄命中测试（含屏蔽标记与选区裁剪框）：
 * 小矩形各手柄命中圈重叠时取距离最近者（相等时稳定取枚举顺序靠前），
 * 不再按 nw→w 固定顺序首命中。 */
function rectResizeHandle(
  rect: Pick<Mark, "start" | "end">,
  at: Point,
): ExclusionResizeHandle | undefined {
  const area = normalizedRect(rect);
  // 零尺寸手柄重叠时仍按最近距离命中，允许把退化对象重新拉开。
  let best: ExclusionResizeHandle | undefined;
  let bestDistance = Number.POSITIVE_INFINITY;
  for (const handle of EXCLUSION_RESIZE_HANDLES) {
    const point = exclusionHandlePoint(area, handle);
    const distance = Math.hypot(at.x - point.x, at.y - point.y);
    // 严格小于：等距 tie 保留先遍历到的手柄，行为稳定可预测。
    if (distance <= EXCLUSION_HANDLE_RADIUS && distance < bestDistance) {
      best = handle;
      bestDistance = distance;
    }
  }
  return best;
}

/** 可拖拽调整裁剪的基准矩形：已提交裁剪优先；截图现场无裁剪时为整幅选区。 */
function cropAdjustBase(
  state: EditorState,
  size: CanvasSize,
  scene: boolean,
): { readonly start: Point; readonly end: Point } | undefined {
  if (state.crop) return state.crop;
  if (scene) {
    return {
      start: { x: 0, y: 0 },
      end: { x: size.width, y: size.height },
    };
  }
  return undefined;
}

/** 拖动 start/end 矩形的边/角：反向拖拽自动翻转，不产生负尺寸。 */
function resizeRectPoints(
  start: Point,
  end: Point,
  handle: ExclusionResizeHandle,
  delta: Point,
): { start: Point; end: Point } {
  const left = Math.min(start.x, end.x);
  const right = Math.max(start.x, end.x);
  const top = Math.min(start.y, end.y);
  const bottom = Math.max(start.y, end.y);
  let west = left;
  let east = right;
  let north = top;
  let south = bottom;
  if (handle.includes("w")) west += delta.x;
  if (handle.includes("e")) east += delta.x;
  if (handle.includes("n")) north += delta.y;
  if (handle.includes("s")) south += delta.y;
  return {
    start: { x: Math.min(west, east), y: Math.min(north, south) },
    end: { x: Math.max(west, east), y: Math.max(north, south) },
  };
}

/** 矩形对角类标记（矩形/椭圆/遮挡/屏蔽/放大镜）支持八点缩放；聚光灯
 * 为多区域对象，手柄按区域单独命中，不在此列。 */
function isBoxResizableMark(mark: Mark): boolean {
  return (
    mark.tool === "rectangle" ||
    mark.tool === "ellipse" ||
    mark.tool === "solid" ||
    mark.tool === "magnifier" ||
    isExclusionMark(mark)
  );
}

/** #235 聚光灯区域辅助：命中/重设 start=end 同步包围盒，空区域集删除对象。 */
function spotlightRegionsBBox(
  regions: readonly Rect[],
): { start: Point; end: Point } | undefined {
  if (!regions.length) return undefined;
  const left = Math.min(...regions.map((region) => region.x));
  const top = Math.min(...regions.map((region) => region.y));
  const right = Math.max(...regions.map((region) => region.x + region.width));
  const bottom = Math.max(...regions.map((region) => region.y + region.height));
  return { start: { x: left, y: top }, end: { x: right, y: bottom } };
}

function withSpotlightRegions(
  mark: Mark,
  regions: readonly Rect[],
): Mark | undefined {
  const kept = regions.filter(
    (region) => region.width > 0 && region.height > 0,
  );
  const box = spotlightRegionsBBox(kept);
  if (!box) return undefined;
  return { ...mark, regions: kept, start: box.start, end: box.end };
}

/** 聚光灯单区域八点手柄命中：全部区域取最近命中（距离相等时稳定取靠前）。 */
function spotlightRegionHandle(
  mark: Mark,
  at: Point,
): { index: number; handle: ExclusionResizeHandle } | undefined {
  const regions = spotlightRegionsOf(mark);
  let best: { index: number; handle: ExclusionResizeHandle } | undefined;
  let bestDistance = Number.POSITIVE_INFINITY;
  regions.forEach((region, index) => {
    const handle = rectResizeHandle(
      {
        start: { x: region.x, y: region.y },
        end: { x: region.x + region.width, y: region.y + region.height },
      },
      at,
    );
    if (!handle) return;
    const point = exclusionHandlePoint(region, handle);
    const distance = Math.hypot(at.x - point.x, at.y - point.y);
    if (distance < bestDistance) {
      best = { index, handle };
      bestDistance = distance;
    }
  });
  return best;
}

/** 命中点所在的聚光灯区域下标（±pad 容差）；未命中返回 -1。 */
function spotlightRegionAt(mark: Mark, at: Point, pad = 6): number {
  const regions = spotlightRegionsOf(mark);
  return regions.findIndex(
    (region) =>
      at.x >= region.x - pad &&
      at.x <= region.x + region.width + pad &&
      at.y >= region.y - pad &&
      at.y <= region.y + region.height + pad,
  );
}

/** 拖动聚光灯单区域八点手柄：仅该区域变化，其余区域与暗度保持不动；
 * start/end 同步重算区域联合包围盒。 */
function resizeSpotlightRegionMark(
  drag: {
    readonly handle: ExclusionResizeHandle;
    readonly regionIndex: number;
    readonly origin: Mark;
  },
  delta: Point,
  shift: boolean,
): Mark {
  const regions = spotlightRegionsOf(drag.origin);
  const region = regions[drag.regionIndex];
  if (!region) return drag.origin;
  const resized = resizeBoxMark(
    {
      ...drag.origin,
      start: { x: region.x, y: region.y },
      end: { x: region.x + region.width, y: region.y + region.height },
    },
    drag.handle,
    delta,
    shift,
  );
  const rect = normalizedRect(resized);
  return (
    withSpotlightRegions(
      drag.origin,
      regions.map((at, index) => (index === drag.regionIndex ? rect : at)),
    ) ?? drag.origin
  );
}

/** 直线/箭头支持端点拖拽。 */
function isEndpointMark(mark: Mark): boolean {
  return (
    mark.tool === "line" || mark.tool === "arrow" || mark.tool === "callout"
  );
}

/** 直线/箭头端点命中：两端都在半径内时取距离更近的一端（相等时稳定取
 * start），短线段（长度 < 2×半径）的 end 手柄因此仍然可命中可拖拽。 */
function endpointHandle(mark: Mark, at: Point): "start" | "end" | undefined {
  if (isFontMark(mark)) at = transformTextPoint(mark, at, true);
  const toStart = Math.hypot(at.x - mark.start.x, at.y - mark.start.y);
  const toEnd = Math.hypot(at.x - mark.end.x, at.y - mark.end.y);
  if (toStart > EXCLUSION_HANDLE_RADIUS && toEnd > EXCLUSION_HANDLE_RADIUS)
    return undefined;
  return toStart <= toEnd ? "start" : "end";
}

/** 拖动八点手柄缩放矩形类标记（屏蔽/矩形/椭圆共用）：反向拖拽自动翻转；
 * Shift + 角手柄对角锚定，保持拖前宽高比（正方/正圆约束的缩放版）。 */
function resizeBoxMark(
  mark: Mark,
  handle: ExclusionResizeHandle,
  delta: Point,
  shift: boolean,
): Mark {
  if (delta.x === 0 && delta.y === 0) return mark;
  const left = Math.min(mark.start.x, mark.end.x);
  const right = Math.max(mark.start.x, mark.end.x);
  const top = Math.min(mark.start.y, mark.end.y);
  const bottom = Math.max(mark.start.y, mark.end.y);
  const width = right - left;
  const height = bottom - top;
  if (shift && handle.length === 2) {
    const anchorX = handle.includes("w") ? right : left;
    const anchorY = handle.includes("n") ? bottom : top;
    const cornerX = (handle.includes("w") ? left : right) + delta.x;
    const cornerY = (handle.includes("n") ? top : bottom) + delta.y;
    const dx = cornerX - anchorX;
    const dy = cornerY - anchorY;
    // 零宽/零高退化时退回正方约束，避免除零。
    const ratio = width > 0 && height > 0 ? width / height : 1;
    // 保持比例时把两个轴的拖拽位移都换算到宽轴取较大者：宽高比 2:1 的
    // 矩形 dy=80 意味着宽应到 160（高 80），而不是 max(|dx|,|dy|)=80
    // 直接当宽（旧实现会把 2:1 缩成 80×40）。
    const nextWidth = Math.max(Math.abs(dx), Math.abs(dy) * ratio);
    const nextHeight = nextWidth / ratio;
    const x2 = anchorX + (dx < 0 ? -1 : 1) * nextWidth;
    const y2 = anchorY + (dy < 0 ? -1 : 1) * nextHeight;
    return {
      ...mark,
      start: {
        x: Math.min(anchorX, x2),
        y: Math.min(anchorY, y2),
      },
      end: {
        x: Math.max(anchorX, x2),
        y: Math.max(anchorY, y2),
      },
    };
  }
  return {
    ...mark,
    ...resizeRectPoints(mark.start, mark.end, handle, delta),
  };
}

/** 拖动直线/箭头端点：另一端固定；Shift 吸附 45° 倍数方向。 */
function moveEndpointMark(
  mark: Mark,
  endpoint: "start" | "end",
  delta: Point,
  shift: boolean,
): Mark {
  if (delta.x === 0 && delta.y === 0) return mark;
  if (isFontMark(mark)) {
    const worldStart = transformTextPoint(mark, mark.start);
    if (endpoint === "end") {
      const next = {
        ...mark,
        end: { x: mark.end.x + delta.x, y: mark.end.y + delta.y },
      };
      return { ...next, start: transformTextPoint(next, worldStart, true) };
    }
    return {
      ...mark,
      start: transformTextPoint(
        mark,
        { x: worldStart.x + delta.x, y: worldStart.y + delta.y },
        true,
      ),
    };
  }
  const at = { x: mark[endpoint].x + delta.x, y: mark[endpoint].y + delta.y };
  return endpoint === "end"
    ? { ...mark, end: constrainLineDrag(mark.start, at, shift) }
    : { ...mark, start: constrainLineDrag(mark.end, at, shift) };
}

function normalizedRect(mark: Pick<Mark, "start" | "end">) {
  return {
    x: Math.min(mark.start.x, mark.end.x),
    y: Math.min(mark.start.y, mark.end.y),
    width: Math.abs(mark.end.x - mark.start.x),
    height: Math.abs(mark.end.y - mark.start.y),
  };
}

const effectSurfaces = new WeakMap<
  HTMLCanvasElement,
  Map<string, HTMLCanvasElement>
>();
function effectSurface(
  canvas: HTMLCanvasElement,
  width: number,
  height: number,
  key = "default",
) {
  let surfaces = effectSurfaces.get(canvas);
  if (!surfaces) {
    surfaces = new Map();
    effectSurfaces.set(canvas, surfaces);
  }
  let scratch = surfaces.get(key);
  if (!scratch) {
    scratch = document.createElement("canvas");
    surfaces.set(key, scratch);
  }
  const w = Math.max(1, Math.ceil(width)),
    h = Math.max(1, Math.ceil(height));
  if (scratch.width !== w) scratch.width = w;
  if (scratch.height !== h) scratch.height = h;
  const context = scratch.getContext("2d");
  context?.setTransform(1, 0, 0, 1, 0, 0);
  context?.clearRect(0, 0, w, h);
  return scratch;
}

/** 笔刷轨迹（含笔宽）在画布内的包围盒，用于笔刷马赛克/模糊的采样范围。 */
function strokeArea(
  points: readonly Point[],
  pad: number,
  size: CanvasSize,
): ReturnType<typeof normalizedRect> {
  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;
  for (const at of points) {
    minX = Math.min(minX, at.x);
    minY = Math.min(minY, at.y);
    maxX = Math.max(maxX, at.x);
    maxY = Math.max(maxY, at.y);
  }
  if (!Number.isFinite(minX)) return { x: 0, y: 0, width: 0, height: 0 };
  return clampRect(
    {
      x: minX - pad,
      y: minY - pad,
      width: maxX - minX + pad * 2,
      height: maxY - minY + pad * 2,
    },
    size,
  );
}

/** 笔刷轨迹 → 单色掩膜画布（尺寸 = area）：供 destination-in 裁剪效果。 */
function strokeMaskSurface(
  canvas: HTMLCanvasElement,
  area: ReturnType<typeof normalizedRect>,
  points: readonly Point[],
  brush: number,
): HTMLCanvasElement | undefined {
  const mask = effectSurface(canvas, area.width, area.height, "brush-mask");
  const context = mask.getContext("2d");
  if (!context) throw new Error("笔刷掩膜画布分配失败");
  context.lineWidth = brush;
  context.lineCap = "round";
  context.lineJoin = "round";
  context.strokeStyle = "#000000";
  context.setLineDash([]);
  context.beginPath();
  points.forEach((at, index) => {
    const x = at.x - area.x;
    const y = at.y - area.y;
    if (index === 0) context.moveTo(x, y);
    else context.lineTo(x, y);
  });
  if (points.length === 1) {
    const only = points[0]!;
    context.lineTo(only.x - area.x + 0.1, only.y - area.y);
  }
  context.stroke();
  return mask;
}

/** 掩膜裁剪后把效果像素写回主画布：仅笔刷覆盖处被替换。 */
function compositeThroughMask(
  context: CanvasRenderingContext2D,
  effect: HTMLCanvasElement,
  mask: HTMLCanvasElement,
  area: ReturnType<typeof normalizedRect>,
): void {
  const composited = effectSurface(
    effect,
    area.width,
    area.height,
    "brush-composited",
  );
  const compositor = composited.getContext("2d");
  if (!compositor) throw new Error("笔刷合成画布分配失败");
  compositor.drawImage(effect, 0, 0);
  compositor.globalCompositeOperation = "destination-in";
  compositor.drawImage(mask, 0, 0);
  compositor.globalCompositeOperation = "source-over";
  context.save();
  context.imageSmoothingEnabled = false;
  // 效果仅重绘已有像素的颜色，透明度沿用原图。
  context.globalCompositeOperation = "source-atop";
  context.drawImage(composited, area.x, area.y, area.width, area.height);
  context.restore();
}

function applyMosaic(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
) {
  if (mark.points?.length) {
    applyBrushTiles(context, canvas, mark, scale);
    return;
  }
  const size = canvasSize(canvas);
  const area = clampRect(normalizedRect(mark), size);
  if (area.width <= 0 || area.height <= 0) return;
  // 强度决定马赛克块尺寸：1→8、2→14、3→26，中间连续插值，预览与导出同源。
  const cell = mosaicCellSize(markEffectIntensity(mark)) * scale;
  const scratch = effectSurface(canvas, area.width / cell, area.height / cell);
  const scratchContext = scratch.getContext("2d");
  if (!scratchContext) return;
  scratchContext.filter = "none";
  scratchContext.imageSmoothingEnabled = false;
  const sx = canvas.width / size.width,
    sy = canvas.height / size.height;
  scratchContext.drawImage(
    canvas,
    area.x * sx,
    area.y * sy,
    area.width * sx,
    area.height * sy,
    0,
    0,
    scratch.width,
    scratch.height,
  );
  context.save();
  context.imageSmoothingEnabled = false;
  context.clearRect(area.x, area.y, area.width, area.height);
  context.drawImage(scratch, area.x, area.y, area.width, area.height);
  context.restore();
}

function applyBlur(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
) {
  if (mark.points?.length) {
    applyBrushTiles(context, canvas, mark, scale);
    return;
  }
  const size = canvasSize(canvas);
  const area = clampRect(normalizedRect(mark), size);
  if (area.width <= 0 || area.height <= 0) return;
  // Sample neighbouring pixels, then replace only the selected region. This
  // avoids unblurred edges and alpha accumulating over the original pixels.
  const padding = 30 * scale;
  const sample = clampRect(
    {
      x: area.x - padding,
      y: area.y - padding,
      width: area.width + padding * 2,
      height: area.height + padding * 2,
    },
    size,
  );
  const sx = canvas.width / size.width,
    sy = canvas.height / size.height;
  // 强度决定滤波半径：1→5、2→10、3→20，中间连续插值，预览与导出同源。
  const radius = blurRadius(markEffectIntensity(mark));
  const scratch = effectSurface(canvas, sample.width * sx, sample.height * sy);
  const scratchContext = scratch.getContext("2d");
  if (!scratchContext) return;
  scratchContext.imageSmoothingEnabled = true;
  scratchContext.filter = `blur(${radius * scale * sx}px)`;
  scratchContext.drawImage(
    canvas,
    sample.x * sx,
    sample.y * sy,
    sample.width * sx,
    sample.height * sy,
    0,
    0,
    scratch.width,
    scratch.height,
  );
  context.save();
  context.beginPath();
  context.rect(area.x, area.y, area.width, area.height);
  context.clip();
  context.clearRect(area.x, area.y, area.width, area.height);
  context.drawImage(scratch, sample.x, sample.y, sample.width, sample.height);
  context.restore();
}

/** #233 ack 前可失败校验：效果笔刷预算与逻辑 frame 合法性。全部通过才
 * 允许 ack——ack 后安装无失败路径，旧宿主会话/旧画布只在此时前受保护。 */
// eslint-disable-next-line react-refresh/only-export-components -- 纯函数与组件同文件共享 brushTiles 预算其源
export function validateProjectRestore(
  sourceImage: HTMLImageElement,
  savedCanvas: { readonly width: number; readonly height: number },
  state: WireEditorState,
): void {
  if (
    !sourceImage.naturalWidth ||
    !sourceImage.naturalHeight ||
    !Number.isFinite(savedCanvas.width) ||
    !Number.isFinite(savedCanvas.height) ||
    savedCanvas.width < 1 ||
    savedCanvas.height < 1
  ) {
    throw new Error("工程逻辑画布或原图尺寸无效");
  }
  // 逻辑 canvas 总像素预算（与宿主/编辑器 64MP 一致）：在任何真实画布
  // 分配前拒绝，不依赖后续安装路径兜底。
  if (savedCanvas.width * savedCanvas.height > PROJECT_CANVAS_TOTAL_PIXELS) {
    throw new Error(
      `工程逻辑画布超出 64MP 总像素上限（${savedCanvas.width}×${savedCanvas.height}）；未确认切换，当前画布保持不变。`,
    );
  }
  // 输出 resize 上限（与编辑器 UI 一致）：恢复后导出分配前拒绝。
  if (
    state.resize &&
    (!Number.isInteger(state.resize.width) ||
      !Number.isInteger(state.resize.height) ||
      state.resize.width < 1 ||
      state.resize.height < 1 ||
      state.resize.width > PROJECT_RESIZE_MAX_SIDE ||
      state.resize.height > PROJECT_RESIZE_MAX_SIDE ||
      state.resize.width * state.resize.height > PROJECT_RESIZE_TOTAL_PIXELS)
  ) {
    throw new Error(
      "工程输出尺寸超出上限（单边≤16384px，总像素≤64MP）；未确认切换，当前画布保持不变。",
    );
  }
  const canvas = document.createElement("canvas");
  canvas.dataset.coordinateWidth = String(savedCanvas.width);
  canvas.dataset.coordinateHeight = String(savedCanvas.height);
  const restored: EditorState = {
    rotation: state.rotation,
    marks: state.marks.map((mark) => wireToMark(mark, (point) => point, 1)),
    crop: state.crop
      ? { start: { ...state.crop.start }, end: { ...state.crop.end } }
      : undefined,
    resize: state.resize ? { ...state.resize } : undefined,
    patches: state.patches.map((patch) => ({ ...patch })),
  };
  try {
    validateBrushBudget(sourceImage, canvas, restored);
  } catch {
    throw new Error("工程效果笔刷超出当前预算；未确认切换，当前画布保持不变。");
  }
}

function validateBrushBudget(
  image: HTMLImageElement | undefined,
  canvas: HTMLCanvasElement | null,
  next: EditorState,
) {
  if (!image || !canvas) return;
  const size = outputSize(image, next.rotation),
    display = canvasSize(canvas);
  const scale = 1 / imageTransform(image, next.rotation, display).scale;
  for (const mark of next.marks) {
    if (
      (mark.tool !== "mosaic" && mark.tool !== "blur") ||
      !mark.points?.length
    )
      continue;
    brushTiles(
      {
        ...mark,
        points: mark.points.map((at) =>
          projectPoint(at, image, next.rotation, display, next.rotation, size),
        ),
      },
      scale,
      size,
    );
  }
}

const BRUSH_TILE_PIXELS = 512;
const BRUSH_STAGED_PIXELS = 8_000_000;
const BRUSH_SAMPLE_PIXELS = 4_000_000;

function brushTiles(
  mark: Mark,
  scale: number,
  size: CanvasSize,
  pixelScale = 1,
) {
  const points = mark.points ?? [mark.start, mark.end];
  const radius = Math.max(2, (mark.style?.brushSize ?? 24) * scale) / 2 + 1;
  const side = BRUSH_TILE_PIXELS / Math.max(1, pixelScale);
  const area = strokeArea(points, radius, size);
  const tiles: ReturnType<typeof normalizedRect>[] = [];
  let pixels = 0;
  const halo =
    mark.tool === "blur"
      ? blurRadius(markEffectIntensity(mark)) * 3 * scale
      : 0;
  for (
    let y = Math.floor(area.y / side) * side;
    y < area.y + area.height;
    y += side
  ) {
    for (
      let x = Math.floor(area.x / side) * side;
      x < area.x + area.width;
      x += side
    ) {
      const outer = clampRect({ x, y, width: side, height: side }, size);
      const tx = Math.max(outer.x, area.x),
        ty = Math.max(outer.y, area.y);
      const tile = {
        x: tx,
        y: ty,
        width: Math.min(outer.x + outer.width, area.x + area.width) - tx,
        height: Math.min(outer.y + outer.height, area.y + area.height) - ty,
      };
      const cx = tile.x + tile.width / 2,
        cy = tile.y + tile.height / 2;
      const reach = radius + Math.hypot(tile.width, tile.height) / 2;
      const touches = points.some((a, i) => {
        const b = points[i + 1] ?? a,
          dx = b.x - a.x,
          dy = b.y - a.y;
        const t =
          dx || dy
            ? Math.max(
                0,
                Math.min(
                  1,
                  ((cx - a.x) * dx + (cy - a.y) * dy) / (dx * dx + dy * dy),
                ),
              )
            : 0;
        return Math.hypot(cx - a.x - t * dx, cy - a.y - t * dy) <= reach;
      });
      if (!touches) continue;
      pixels +=
        Math.ceil(tile.width * pixelScale) *
        Math.ceil(tile.height * pixelScale);
      if (
        pixels > BRUSH_STAGED_PIXELS ||
        (Math.ceil((tile.width + halo * 2) * pixelScale) + 2) *
          (Math.ceil((tile.height + halo * 2) * pixelScale) + 2) >
          BRUSH_SAMPLE_PIXELS
      )
        throw new Error(
          "笔刷效果超出8MP暂存或4MP单块采样预算，请缩短轨迹或减小笔刷/强度。",
        );
      tiles.push(tile);
    }
  }
  return tiles;
}

function applyBrushTiles(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale: number,
) {
  const size = canvasSize(canvas),
    sx = canvas.width / size.width,
    sy = canvas.height / size.height;
  const tiles = brushTiles(mark, scale, size, Math.max(sx, sy));
  const staged: {
    area: ReturnType<typeof normalizedRect>;
    canvas: HTMLCanvasElement;
    x: number;
    y: number;
  }[] = [];
  // 全部读取原始画布后才提交，halo 不会读取已模糊的相邻块。
  try {
    for (const planned of tiles) {
      const x = Math.round(planned.x * sx),
        y = Math.round(planned.y * sy);
      const right = Math.round((planned.x + planned.width) * sx),
        bottom = Math.round((planned.y + planned.height) * sy);
      if (right <= x || bottom <= y) continue;
      const area = {
        x: x / sx,
        y: y / sy,
        width: (right - x) / sx,
        height: (bottom - y) / sy,
      };
      const tile = document.createElement("canvas");
      tile.width = right - x;
      tile.height = bottom - y;
      const target = tile.getContext("2d");
      if (!target) throw new Error("笔刷效果画布分配失败");
      target.drawImage(
        canvas,
        x,
        y,
        tile.width,
        tile.height,
        0,
        0,
        tile.width,
        tile.height,
      );
      target.setTransform(sx, 0, 0, sy, -x, -y);
      staged.push({ area, canvas: tile, x, y });
      if (mark.tool === "mosaic")
        applyMosaicBrush(target, canvas, mark, scale, area);
      else applyBlurBrush(target, canvas, mark, scale, area);
    }
    context.save();
    context.setTransform(1, 0, 0, 1, 0, 0);
    for (const tile of staged) {
      context.clearRect(tile.x, tile.y, tile.canvas.width, tile.canvas.height);
      context.drawImage(tile.canvas, tile.x, tile.y);
    }
    context.restore();
  } finally {
    for (const tile of staged) {
      tile.canvas.width = 1;
      tile.canvas.height = 1;
    }
  }
}

/** 自由笔刷马赛克：轨迹包围盒内按块尺寸降采样，再经笔刷掩膜裁剪写回。 */
function applyMosaicBrush(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
  tileArea?: ReturnType<typeof normalizedRect>,
) {
  const size = canvasSize(canvas);
  const points = mark.points ?? [mark.start, mark.end];
  const brush = Math.max(2, (mark.style?.brushSize ?? 24) * scale);
  const fullArea = strokeArea(points, brush / 2 + 1, size);
  const area = tileArea ?? fullArea;
  if (area.width <= 2 || area.height <= 2) return;
  const cell = Math.max(2, mosaicCellSize(markEffectIntensity(mark)) * scale);
  const mask = strokeMaskSurface(canvas, area, points, brush);
  if (!mask) return;
  const cellWidth = fullArea.width / Math.ceil(fullArea.width / cell),
    cellHeight = fullArea.height / Math.ceil(fullArea.height / cell);
  const gx = Math.floor((area.x - fullArea.x) / cellWidth),
    gy = Math.floor((area.y - fullArea.y) / cellHeight);
  const right = Math.ceil((area.x + area.width - fullArea.x) / cellWidth),
    bottom = Math.ceil((area.y + area.height - fullArea.y) / cellHeight);
  // 保留裁剪前的格子映射；drawImage 会同步裁剪源与目标，不能拉伸剩余源区域。
  const sample = {
    x: fullArea.x + gx * cellWidth,
    y: fullArea.y + gy * cellHeight,
    width: (right - gx) * cellWidth,
    height: (bottom - gy) * cellHeight,
  };
  const down = effectSurface(
    canvas,
    right - gx,
    bottom - gy,
    "brush-mosaic-down",
  );
  const downContext = down.getContext("2d");
  if (!downContext) throw new Error("笔刷效果画布分配失败");
  downContext.imageSmoothingEnabled = true;
  downContext.imageSmoothingQuality = "high";
  const sx = canvas.width / size.width,
    sy = canvas.height / size.height;
  downContext.drawImage(
    canvas,
    sample.x * sx,
    sample.y * sy,
    sample.width * sx,
    sample.height * sy,
    0,
    0,
    down.width,
    down.height,
  );
  const up = effectSurface(canvas, area.width, area.height, "brush-mosaic-up");
  const upContext = up.getContext("2d");
  if (!upContext) throw new Error("笔刷效果画布分配失败");
  upContext.imageSmoothingEnabled = false;
  upContext.drawImage(
    down,
    (area.x - sample.x) / cellWidth,
    (area.y - sample.y) / cellHeight,
    area.width / cellWidth,
    area.height / cellHeight,
    0,
    0,
    up.width,
    up.height,
  );
  compositeThroughMask(context, up, mask, area);
}

/** 自由笔刷模糊：带边距采样模糊后经笔刷掩膜裁剪写回，只替换覆盖像素。 */
function applyBlurBrush(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
  tileArea?: ReturnType<typeof normalizedRect>,
) {
  const size = canvasSize(canvas);
  const points = mark.points ?? [mark.start, mark.end];
  const brush = Math.max(2, (mark.style?.brushSize ?? 24) * scale);
  const fullArea = strokeArea(points, brush / 2 + 1, size);
  const area = tileArea ?? fullArea;
  if (area.width <= 0 || area.height <= 0) return;
  const radius = blurRadius(markEffectIntensity(mark));
  const mask = strokeMaskSurface(canvas, area, points, brush);
  if (!mask) return;
  let sample = clampRect(
    {
      x: area.x - radius * 3 * scale,
      y: area.y - radius * 3 * scale,
      width: area.width + radius * 6 * scale,
      height: area.height + radius * 6 * scale,
    },
    size,
  );
  const sx = canvas.width / size.width,
    sy = canvas.height / size.height;
  const px = Math.floor(sample.x * sx),
    py = Math.floor(sample.y * sy);
  sample = clampRect(
    {
      x: px / sx,
      y: py / sy,
      width: (Math.ceil((sample.x + sample.width) * sx) - px) / sx,
      height: (Math.ceil((sample.y + sample.height) * sy) - py) / sy,
    },
    size,
  );
  const blurred = effectSurface(
    canvas,
    sample.width * sx,
    sample.height * sy,
    "brush-blur-sample",
  );
  const blurContext = blurred.getContext("2d");
  if (!blurContext) throw new Error("笔刷效果画布分配失败");
  blurContext.imageSmoothingEnabled = true;
  blurContext.filter = `blur(${radius * scale * sx}px)`;
  blurContext.drawImage(
    canvas,
    sample.x * sx,
    sample.y * sy,
    sample.width * sx,
    sample.height * sy,
    0,
    0,
    blurred.width,
    blurred.height,
  );
  // 裁出 area 尺寸的效果层，再经掩膜写回。
  const cut = effectSurface(canvas, area.width, area.height, "brush-blur-cut");
  const cutContext = cut.getContext("2d");
  if (!cutContext) throw new Error("笔刷效果画布分配失败");
  cutContext.drawImage(
    blurred,
    (area.x - sample.x) * sx,
    (area.y - sample.y) * sy,
    area.width * sx,
    area.height * sy,
    0,
    0,
    cut.width,
    cut.height,
  );
  compositeThroughMask(context, cut, mask, area);
}

/** #235 H3：放大镜把屏蔽源区域放大显示到原屏蔽框之外时，被屏蔽内容
 * 会随普通像素上传并绕过宿主遮罩。权威屏蔽源与烘焙/识别输入同一外扩
 * 取整自然矩形（exclusionNaturalRects），回投显示空间后做源→镜内投影
 * （裁剪前推导，仅输出时裁剪），返回每个（镜×屏蔽块）在镜内的泄露
 * 显示矩形——raw mark 几何不直接参与（取整边界 texel 不得遗漏）。 */
function magnifierExclusionLeakRects(
  state: EditorState,
  image: HTMLImageElement,
  displaySize: CanvasSize,
): Rect[] {
  const rects: Rect[] = [];
  const naturalSize = outputSize(image, state.rotation);
  const toDisplay = (at: Point) =>
    projectPoint(
      at,
      image,
      state.rotation,
      naturalSize,
      state.rotation,
      displaySize,
    );
  const authoritativeSources = exclusionNaturalRects(
    image,
    state,
    displaySize,
  ).map((rect) => {
    const start = toDisplay({ x: rect.x, y: rect.y });
    const end = toDisplay({
      x: rect.x + rect.width,
      y: rect.y + rect.height,
    });
    return {
      x: Math.min(start.x, end.x),
      y: Math.min(start.y, end.y),
      width: Math.abs(end.x - start.x),
      height: Math.abs(end.y - start.y),
    };
  });
  for (const mark of state.marks) {
    if (mark.tool !== "magnifier") continue;
    const dest = normalizedRect(mark);
    if (dest.width < 1 || dest.height < 1) continue;
    const zoom = magnifierZoomOf(mark);
    const source = {
      x: dest.x + dest.width / 2 - dest.width / 2 / zoom,
      y: dest.y + dest.height / 2 - dest.height / 2 / zoom,
      width: dest.width / zoom,
      height: dest.height / zoom,
    };
    for (const area of authoritativeSources) {
      const left = Math.max(source.x, area.x);
      const top = Math.max(source.y, area.y);
      const right = Math.min(source.x + source.width, area.x + area.width);
      const bottom = Math.min(source.y + source.height, area.y + area.height);
      if (right <= left || bottom <= top) continue;
      const destLeft = Math.max(dest.x, dest.x + (left - source.x) * zoom);
      const destTop = Math.max(dest.y, dest.y + (top - source.y) * zoom);
      const destRight = Math.min(
        dest.x + dest.width,
        dest.x + (right - source.x) * zoom,
      );
      const destBottom = Math.min(
        dest.y + dest.height,
        dest.y + (bottom - source.y) * zoom,
      );
      if (destRight > destLeft && destBottom > destTop) {
        rects.push({
          x: destLeft,
          y: destTop,
          width: destRight - destLeft,
          height: destBottom - destTop,
        });
      }
    }
  }
  return rects;
}
function clampRect(rect: ReturnType<typeof normalizedRect>, size: CanvasSize) {
  const x = Math.max(0, Math.min(size.width, rect.x));
  const y = Math.max(0, Math.min(size.height, rect.y));
  const right = Math.max(x, Math.min(size.width, rect.x + rect.width));
  const bottom = Math.max(y, Math.min(size.height, rect.y + rect.height));
  return { x, y, width: right - x, height: bottom - y };
}

/** #235 聚光灯：暗色单次填充 + 区域并集 destination-out 一次裁切——
 * 重叠孔洞只裁一次，暗部不加深；遮罩仅覆盖底图显示区域（letterbox/
 * 透明画布外部不染色）。预览与导出同一绘制路径。 */
function drawSpotlight(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  chrome: boolean,
  selected: boolean,
  image: HTMLImageElement | undefined,
  rotation: number,
) {
  const size = canvasSize(canvas);
  const regions = spotlightRegionsOf(mark).map((region) =>
    clampRect(region, size),
  );
  const mask = effectSurface(canvas, size.width, size.height, "spotlight-mask");
  const maskContext = mask.getContext("2d");
  if (!maskContext) return;
  // 遮罩范围 = 底图显示矩形（与 draw() 图像分支同一适配计算）：
  // 宽高比不同的底图不会把 letterbox 染黑；无图（测试 stub）退化为整幅。
  let area = { x: 0, y: 0, width: size.width, height: size.height };
  if (image && image.naturalWidth > 0 && image.naturalHeight > 0) {
    const quarterTurn = rotation % 180 !== 0;
    const sourceWidth = quarterTurn ? image.naturalHeight : image.naturalWidth;
    const sourceHeight = quarterTurn ? image.naturalWidth : image.naturalHeight;
    const scale = Math.min(
      size.width / sourceWidth,
      size.height / sourceHeight,
    );
    area = clampRect(
      {
        x: (size.width - sourceWidth * scale) / 2,
        y: (size.height - sourceHeight * scale) / 2,
        width: sourceWidth * scale,
        height: sourceHeight * scale,
      },
      size,
    );
  }
  const fill = annotationFill(mark);
  maskContext.setTransform(1, 0, 0, 1, 0, 0);
  maskContext.globalCompositeOperation = "source-over";
  maskContext.globalAlpha = 1;
  maskContext.clearRect(0, 0, mask.width, mask.height);
  maskContext.fillStyle = fill?.color ?? "#000000";
  maskContext.globalAlpha = spotlightDarkness(mark) / 100;
  maskContext.fillRect(area.x, area.y, area.width, area.height);
  maskContext.globalAlpha = 1;
  maskContext.globalCompositeOperation = "destination-out";
  for (const region of regions) {
    maskContext.fillRect(region.x, region.y, region.width, region.height);
  }
  maskContext.globalCompositeOperation = "source-over";
  context.drawImage(mask, 0, 0, size.width, size.height);
  if (chrome) {
    for (const region of regions) {
      if (region.width <= 0 && region.height <= 0) continue;
      context.save();
      context.setLineDash([]);
      context.lineWidth = 1;
      context.strokeStyle = "rgba(0,0,0,0.7)";
      context.strokeRect(region.x, region.y, region.width, region.height);
      context.setLineDash([6, 4]);
      context.strokeStyle = "rgba(255,255,255,0.9)";
      context.strokeRect(region.x, region.y, region.width, region.height);
      context.restore();
    }
    if (selected) {
      for (const region of regions) {
        drawHandleSquares(context, region, "#f38b35");
      }
    }
  }
}

/** #235 放大镜：采样几何由未裁切的标注矩形（中心/尺寸）决定，仅输出
 * 时按画布裁切——镜子移出画布或裁剪后不会重新居中缩放。采样源是
 * “底图与已应用的修补”的干净重绘，不递归采样自己或其它标注；镜内为
 * 替换式绘制（与已应用补丁同契约）：PNG 先清除再写采样像素，越界采样
 * 处保持透明、不残留镜下原图/其它标注；JPEG 在清除处回填显式白底。 */
function drawMagnifier(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  base: HTMLCanvasElement | undefined,
  markScale: number,
  background: string | undefined,
) {
  const size = canvasSize(canvas);
  // 未裁切的标注几何：采样矩形完全由此推导，不受画布边界影响。
  const dest = normalizedRect(mark);
  if (dest.width < 1 || dest.height < 1) return;
  const zoom = magnifierZoomOf(mark);
  const centerX = dest.x + dest.width / 2;
  const centerY = dest.y + dest.height / 2;
  const source = {
    x: centerX - dest.width / 2 / zoom,
    y: centerY - dest.height / 2 / zoom,
    width: dest.width / zoom,
    height: dest.height / zoom,
  };
  // 仅输出范围按画布裁切：可见部分 = 标注矩形 ∩ 画布。
  const visible = clampRect(dest, size);
  context.save();
  if (visible.width > 0 && visible.height > 0) {
    context.beginPath();
    context.rect(visible.x, visible.y, visible.width, visible.height);
    context.clip();
    // 替换式绘制：PNG 清除保持透明；JPEG 回填显式白底。
    if (background) {
      context.fillStyle = background;
      context.fillRect(visible.x, visible.y, visible.width, visible.height);
    } else {
      context.clearRect(visible.x, visible.y, visible.width, visible.height);
    }
    if (base && base.width > 0 && base.height > 0) {
      const sx = base.width / size.width;
      const sy = base.height / size.height;
      // 源矩形越界按底图边界裁切：只绘制与底图相交的部分。
      const left = Math.max(0, source.x);
      const top = Math.max(0, source.y);
      const right = Math.min(size.width, source.x + source.width);
      const bottom = Math.min(size.height, source.y + source.height);
      if (right > left && bottom > top) {
        context.imageSmoothingEnabled = true;
        context.imageSmoothingQuality = "high";
        context.drawImage(
          base,
          left * sx,
          top * sy,
          (right - left) * sx,
          (bottom - top) * sy,
          dest.x + (left - source.x) * zoom,
          dest.y + (top - source.y) * zoom,
          (right - left) * zoom,
          (bottom - top) * zoom,
        );
      }
    }
  }
  context.restore();
  context.save();
  context.setLineDash([]);
  context.lineWidth = 2 * markScale;
  context.strokeStyle = "rgba(0,0,0,0.85)";
  context.strokeRect(dest.x, dest.y, dest.width, dest.height);
  context.lineWidth = 1 * markScale;
  context.strokeStyle = "rgba(255,255,255,0.9)";
  context.strokeRect(
    dest.x + 2 * markScale,
    dest.y + 2 * markScale,
    Math.max(0, dest.width - 4 * markScale),
    Math.max(0, dest.height - 4 * markScale),
  );
  context.restore();
}

/** 放大镜采样基底：底图与已应用的修补的干净重绘（无标注、无编辑 chrome、
 * 无未提交预览），与外层 draw 同一状态/背景/缩放，保证镜内像素在预览
 * 与导出中同源。marksOverride 为空数组，递归不会再进入放大镜分支；
 * bake 路径例外：识别/屏蔽副本输入中，基底必须烘焙屏蔽白块——镜不得
 * 从无标注底图读回被屏蔽内容（H3）。 */
function renderMagnifierBase(
  canvas: HTMLCanvasElement,
  image: HTMLImageElement | undefined,
  state: EditorState,
  markScale: number,
  background: string | undefined,
  inpaintLayer: InpaintDrawLayer | undefined,
  imageAssets: ReadonlyMap<string, CanvasImageSource> | undefined,
  bakeExclusions = false,
): HTMLCanvasElement | undefined {
  const size = canvasSize(canvas);
  const base = effectSurface(canvas, size.width, size.height, "magnifier-base");
  base.dataset.coordinateWidth = String(size.width);
  base.dataset.coordinateHeight = String(size.height);
  draw(
    base,
    image,
    // 裁剪只限制最终输出，不能清空镜子的底图采样源。
    { ...state, crop: undefined },
    undefined,
    bakeExclusions ? state.marks.filter(isExclusionMark) : [],
    false,
    markScale,
    background,
    bakeExclusions,
    { patches: inpaintLayer?.patches },
    undefined,
    false,
    imageAssets,
  );
  return base;
}

interface ExportCanvasOptions {
  readonly format: OutputImageFormat;
  readonly quality: number;
  /** 导出只包含已应用补丁；未提交预览绝不进入导出/复制/识别像素。 */
  readonly inpaint?: InpaintDrawLayer;
  /** 识别/屏蔽副本输入：把排除区写入不透明白色像素；普通导出保持 false。 */
  readonly bakeExclusions?: boolean;
  /** #232 导入图片资产：与显示同一分派绘制，导出像素含导入对象。 */
  readonly imageAssets?: ReadonlyMap<string, CanvasImageSource>;
}

/** 编码器必须真实产出目标格式：类型与文件签名不一致时拒绝，不得只改后缀。 */
async function verifyEncodedBlob(
  blob: Blob,
  format: OutputImageFormat,
): Promise<void> {
  if (blob.type !== format) {
    throw new Error(`canvas encoder did not produce ${format}`);
  }
  const signature = new Uint8Array(await blob.slice(0, 4).arrayBuffer());
  const isPng =
    signature[0] === 0x89 &&
    signature[1] === 0x50 &&
    signature[2] === 0x4e &&
    signature[3] === 0x47;
  const isJpeg =
    signature[0] === 0xff && signature[1] === 0xd8 && signature[2] === 0xff;
  if (format === "image/png" ? !isPng : !isJpeg) {
    throw new Error(`encoded bytes do not match ${format}`);
  }
}

async function exportCanvas(
  image: HTMLImageElement | undefined,
  displayCanvas: HTMLCanvasElement | null,
  state: EditorState,
  options: ExportCanvasOptions,
): Promise<Blob> {
  if (!image || !displayCanvas || !image.naturalWidth || !image.naturalHeight) {
    throw new Error("source image is unavailable");
  }
  const displaySize = canvasSize(displayCanvas);
  const naturalSize = outputSize(image, state.rotation);
  const mapPoint = (point: Point) =>
    projectPoint(
      point,
      image,
      state.rotation,
      displaySize,
      state.rotation,
      naturalSize,
    );
  // textWidth 与字号保持逻辑单位；draw 的 markScale 统一缩放一次。
  const naturalState: EditorState = {
    rotation: state.rotation,
    marks: state.marks.map((mark) => ({
      ...mark,
      start: mapPoint(mark.start),
      end: mapPoint(mark.end),
      ...(mark.points ? { points: mark.points.map(mapPoint) } : {}),
      // 聚光灯区域与端点同一投影：裁剪/缩放/旋转后孔洞位置不漂移。
      ...(mark.regions
        ? {
            regions: mark.regions.map((region) => {
              const cornerA = mapPoint({ x: region.x, y: region.y });
              const cornerB = mapPoint({
                x: region.x + region.width,
                y: region.y + region.height,
              });
              return {
                x: Math.min(cornerA.x, cornerB.x),
                y: Math.min(cornerA.y, cornerB.y),
                width: Math.abs(cornerB.x - cornerA.x),
                height: Math.abs(cornerB.y - cornerA.y),
              };
            }),
          }
        : {}),
    })),
  };
  const rendered = document.createElement("canvas");
  rendered.width = naturalSize.width;
  rendered.height = naturalSize.height;
  const displayScale = imageTransform(image, state.rotation, displaySize).scale;
  draw(
    rendered,
    image,
    naturalState,
    undefined,
    undefined,
    false,
    1 / displayScale,
    // JPEG 无透明：导出时显式合成白色背景；PNG 不烧底色，保留 alpha。
    options.format === "image/jpeg" ? "#ffffff" : undefined,
    options.bakeExclusions === true,
    { patches: options.inpaint?.patches },
    undefined,
    true,
    options.imageAssets,
  );

  const mappedCrop = state.crop
    ? clampRect(
        normalizedRect({
          start: mapPoint(state.crop.start),
          end: mapPoint(state.crop.end),
        }),
        naturalSize,
      )
    : { x: 0, y: 0, ...naturalSize };
  if (mappedCrop.width < 1 || mappedCrop.height < 1) {
    throw new Error("crop area is empty");
  }
  // 裁剪与指定输出尺寸同一次 drawImage 完成；无 resize 时 1:1 拷贝。
  const target = finalOutputSize(image, state, displaySize);
  const output = document.createElement("canvas");
  output.width = target.width;
  output.height = target.height;
  const context = output.getContext("2d");
  if (!context) {
    throw new Error("canvas export is unavailable");
  }
  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = "high";
  context.drawImage(
    rendered,
    mappedCrop.x,
    mappedCrop.y,
    mappedCrop.width,
    mappedCrop.height,
    0,
    0,
    output.width,
    output.height,
  );
  const blob = await new Promise<Blob | null>((resolve) => {
    output.toBlob(
      (value) => resolve(value),
      options.format,
      options.format === "image/jpeg" ? options.quality : undefined,
    );
  });
  if (!blob) {
    throw new Error(
      options.format === "image/jpeg"
        ? "JPEG export failed"
        : "PNG export failed",
    );
  }
  await verifyEncodedBlob(blob, options.format);
  return blob;
}

/** 排除矩形数量上限：与桥 WorkbenchBridgeCodec.MaximumExclusionBoxes 一致；
 * 达到上限时提示并拒绝新增，不丢弃既有标记，也不静默截断 payload。 */
const MAX_EXCLUSION_MARKS = 64;

function findMark(
  marks: readonly Mark[],
  point: Point,
  context?: CanvasRenderingContext2D,
): string | undefined {
  for (let index = marks.length - 1; index >= 0; index -= 1) {
    const mark = marks[index];
    // 锁定对象画布命中跳过：透明对象按几何命中，锁定对象仅列表可选。
    if (!mark || mark.locked) continue;
    if (context && isFontMark(mark)) {
      // 字形旋转：指针逆旋转回局部未旋转空间后按同一盒/引线判定。
      const at = transformTextPoint(mark, point, true);
      const box = textAnnotationLocalBounds(context, mark);
      const hitPadding = Math.max(
        6,
        (mark.style?.textOutline?.width ?? 0) / 2,
        mark.tool === "callout" ? annotationStrokeWidth(mark) / 2 : 0,
      );
      const leaderPaths =
        mark.tool === "callout" ? calloutLeaderPaths(box, mark) : [];
      if (
        (at.x >= box.x - hitPadding &&
          at.x <= box.x + box.width + hitPadding &&
          at.y >= box.y - hitPadding &&
          at.y <= box.y + box.height + hitPadding) ||
        hitStrokePaths(leaderPaths, at, annotationStrokeWidth(mark))
      )
        return mark.id;
      continue;
    }
    // 直线/箭头/折线/画笔：按到线段的最短距离命中，避免包络盒内误选；
    // 笔刷马赛克/模糊同样按轨迹+笔刷半径精确命中；其余工具沿用包络盒
    // （±10px 容差）；透明对象按几何命中。
    if (
      mark.tool === "line" ||
      mark.tool === "arrow" ||
      mark.tool === "polyline" ||
      mark.tool === "pen" ||
      mark.tool === "highlighter" ||
      ((mark.tool === "mosaic" || mark.tool === "blur") && mark.points?.length)
    ) {
      if (
        hitStrokePaths(
          annotationPaths(mark),
          point,
          mark.tool === "mosaic" || mark.tool === "blur"
            ? Math.max(2, mark.style?.brushSize ?? 24)
            : annotationStrokeWidth(mark),
        )
      )
        return mark.id;
      continue;
    }
    const bounds = markBoundsOf(mark, context);
    const left = bounds.x - 10;
    const right = bounds.x + bounds.width + 10;
    const top = bounds.y - 10;
    const bottom = bounds.y + bounds.height + 10;
    if (
      point.x >= left &&
      point.x <= right &&
      point.y >= top &&
      point.y <= bottom
    )
      return mark.id;
  }
  return undefined;
}
