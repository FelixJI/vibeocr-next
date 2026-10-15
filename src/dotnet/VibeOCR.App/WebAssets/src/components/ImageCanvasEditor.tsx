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
} from "@fluentui/react-components";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { createPortal } from "react-dom";
import type { AppActions } from "../app/types";

import {
  isImeCompositionEvent,
  shouldRunDocumentCommand,
} from "../app/keyboard";
import {
  annotationFill,
  annotationStrokeAlpha,
  constrainBoxDrag,
  constrainLineDrag,
  exclusionCoversOutput,
  exclusionDisplayPoints,
  exclusionNormalizedRects,
  finalOutputSize,
  imageTransform,
  isExclusionMark,
  outputSize,
  projectPoint,
  rectIntersectsBox,
  rotateEditorState,
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
  calloutLeaderSegment,
  nextNumberOrdinal,
  drawTextAnnotation,
  textAnnotationLocalBounds,
  transformTextPoint,
  isFontMark,
  isContentMark,
  resolveAnnotationFont,
} from "./annotationText";
import { uploadAnnotatedImage } from "./annotationHandoff";
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
import type { InpaintRect, InpaintResponse } from "./inpaint/inpaint";
import {
  describeInpaintFailure,
  naturalRectToDisplay,
  preflightSelection,
  selectionToNaturalRect,
} from "./inpaint/inpaintSelection";

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
  "select" | "hand" | "textSelect" | AnnotationTool | "crop" | "inpaint";

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
  exclude: "屏蔽",
  crop: "裁剪",
  inpaint: "去水印",
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
  "pen",
  "highlighter",
  "numbering",
  "exclude",
  "crop",
  "inpaint",
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
};

/** 马赛克/模糊强度档位：1=弱、2=中（缺省）、3=强。 */
const EFFECT_INTENSITIES = [1, 2, 3] as const;
const MOSAIC_CELLS: Readonly<Record<number, number>> = { 1: 8, 2: 14, 3: 26 };
const BLUR_RADII: Readonly<Record<number, number>> = { 1: 5, 2: 10, 3: 20 };

/** 标记携带的强度档位：非 1/3 的历史值一律回退到默认 2。 */
function markEffectIntensity(mark: Mark): number {
  const value = mark.style?.intensity;
  return value === 1 || value === 3 ? value : 2;
}

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
}

/** 编辑器发出的会话/导出命令 scope：同一命令形状在两个宿主 feature 上
 * 等价实现（非 scene 图片编辑页发 imageEdit.*，识别面与 scene 发
 * recognition.*，宿主命令约定同步演进）。 */
export type EditorCommandScope = "recognition" | "imageEdit";

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
  | "saveAnnotatedImage";

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
    },
  };

interface ImageCanvasEditorProps {
  readonly actions: AppActions;
  readonly canExport: boolean;
  readonly canRecognize: boolean;
  readonly source: string;
  readonly captureScene?: CaptureScene;
  /** 原图体积（字节）；宿主可选提供，缺省时只显示尺寸不显示原图体积。 */
  readonly sourceByteLength?: number;
  readonly session?: ScreenshotSessionHandle;
  readonly textLayer?: ScreenshotTextLayerState;
  readonly autoText?: boolean;
  readonly showAutoTextPreference?: boolean;
  readonly onAutoTextChange?: (enabled: boolean) => void;
  /** 显式识别提交成功后的宿主交接（纯编辑页导航到识别承载面）。 */
  readonly onRecognitionSubmitted?: () => void;
  /** 工具集用途：默认 full；单次识别面传 recognition 精简为
   * 去水印/旋转/屏蔽三类处理工具。 */
  readonly toolset?: EditorToolset;
  /** 会话命令 scope：默认 recognition；非 scene 图片编辑页传 imageEdit。 */
  readonly commandScope?: EditorCommandScope;
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
  readonly onStyleChange: (change: StyleChange, targetId?: string) => void;
}) {
  // 数值输入草稿仅存在于此面板：焦点内自由编辑，blur/Enter 才提交。
  // targetId 随草稿冻结：即便提交前焦点被画布点击夺走，也写入当时
  // 正在编辑的对象，而不是重新解析的当前选区。
  const [opacityDraft, setOpacityDraft] = useState<
    { kind: "stroke" | "fill"; raw: string; targetId?: string } | undefined
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
                setOpacityDraft({
                  kind: "stroke",
                  raw: data.value,
                  targetId: styleTargetId,
                });
                commitOpacityInput(data.value, (value) =>
                  onStyleChange(
                    { kind: "strokeOpacity", value },
                    styleTargetId,
                  ),
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
                    setOpacityDraft({
                      kind: "fill",
                      raw: data.value,
                      targetId: styleTargetId,
                    });
                    commitOpacityInput(data.value, (value) =>
                      onStyleChange(
                        { kind: "fillOpacity", value },
                        styleTargetId,
                      ),
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
  source,
  sourceByteLength,
  captureScene,
  session,
  textLayer,
  autoText: autoTextProp,
  showAutoTextPreference = true,
  onAutoTextChange,
  onRecognitionSubmitted,
  toolset = "full",
  commandScope = "recognition",
  proofread,
  proofreadActive = false,
}: ImageCanvasEditorProps) {
  const commands = EDITOR_COMMANDS[commandScope];
  // 校对模式：同一图片区切换显示结果行框，编辑器其余 chrome 隐藏但状态保留。
  const proofreading = !!proofread && proofreadActive;
  const captureGeometry = captureScene;
  const contentRevisionRef = useRef(session?.revision ?? 0);
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
      : [];
  const [offscreenIds, setOffscreenIds] = useState<ReadonlySet<string>>(
    new Set(),
  );
  const exportInProgressRef = useRef(false);
  // 选定对象的几何编辑拖拽：origin 冻结拖前几何，objectDraft 驱动实时预览。
  // resize = 矩形类（矩形/椭圆/屏蔽）八点缩放；endpoint = 直线/箭头端点。
  const markDragRef = useRef<
    | {
        readonly kind: "resize";
        readonly id: string;
        readonly handle: ExclusionResizeHandle;
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
    return { ...DEFAULT_TOOL_ATTRIBUTES, ...toolAttributes[name] };
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
  const NO_SELECTION: ObjectSelectionState = {
    ids: new Set<string>(),
    primary: undefined,
  };
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
  }
  const resetResourcesKey = useRef<string | undefined>(undefined);
  useLayoutEffect(() => {
    if (resetResourcesKey.current === resetKey) return;
    resetResourcesKey.current = resetKey;
    draftMark.current = undefined;
    cropDraft.current = undefined;
    dragStart.current = undefined;
    textComposing.current = false;
    markDragRef.current = undefined;
    cropResizeRef.current = undefined;
    objectDraftRef.current = undefined;
    moveDragRef.current = undefined;
    marqueeRef.current = undefined;
    sessionIdRef.current = session?.sessionId;
    contentRevisionRef.current = session?.revision ?? 0;
    inpaintGenerationRef.current += 1;
    inpaintWorkerRef.current?.terminate();
    inpaintWorkerRef.current = undefined;
    inpaintPatchStore.current.clear();
    inpaintDraft.current = undefined;
  }, [resetKey, session?.revision, session?.sessionId]);

  // 冻结基准初始屏蔽：宿主随新基准回发归一化排除框时，映射回当前
  // 显示空间重建初始 exclude 标记（可编辑/可清除）；仅在基准变化时执行
  // 一次，不推进修订、不触发 OCR，后续修改沿用既有 revision 机制。
  const seededBaselineRef = useRef<string>("");
  useEffect(() => {
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

  // 画布取色（EyeDropper 不可用时的 fallback）：true 时下一次画布点击只取样颜色。
  const colorPickPendingRef = useRef(false);

  // 同会话内宿主回显修订时单调对齐本地计数，避免回退。
  useEffect(() => {
    if (session && sessionIdRef.current === session.sessionId) {
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
  }, [source, captureScene]);

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
    return {
      patches: appliedInpaintPatches(),
      preview: preview
        ? { rect: preview.naturalRect, canvas: preview.canvas }
        : undefined,
      selection,
    };
  }

  useLayoutEffect(
    () => () => {
      if (pendingDraw.current !== undefined)
        cancelAnimationFrame(pendingDraw.current);
      pendingDraw.current = undefined;
      previewCache.current = undefined;
      delete canvasRef.current?.dataset.frameReady;
    },
    [resetKey],
  );
  useLayoutEffect(() => {
    // 保留已排队的帧，只替换绘制内容，避免连续 pointermove 饿死绘制。
    latestDraw.current = () => {
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
      let markCount = state.marks.length;
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
            { patches: layer.patches, preview: layer.preview },
            undefined,
            false,
          );
          cached = {
            canvas: base,
            state,
            image,
            crop,
            preview: layer.preview,
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
        objectDraftRef.current
          ? state.marks.map(
              (mark) => objectDraftRef.current!.byId.get(mark.id) ?? mark,
            )
          : textEdit
            ? state.marks.map((mark) =>
                mark.id === textEdit.id ? textEdit.mark : mark,
              )
            : draftMark.current
              ? [...state.marks, draftMark.current]
              : state.marks,
        true,
        1,
        // 预览与导出一致：JPEG 显式白底合成；PNG 不填底色，透明由
        // 工作区 CSS 背景衬托，不把深色烧进图像内容。
        outputFormat === "image/jpeg" ? "#ffffff" : undefined,
        false,
        layer,
        cached,
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
      if (
        image &&
        image.naturalWidth > 0 &&
        image.naturalHeight > 0 &&
        canvas.getContext("2d")
      ) {
        canvas.dataset.frameReady = "true";
        presentation.current?.sourceDrawn();
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

  // 卸载：终止在途 Worker 并释放补丁像素；不触碰宿主状态。
  useEffect(() => {
    const store = inpaintPatchStore.current;
    const workerRef = inpaintWorkerRef;
    return () => {
      inpaintGenerationRef.current += 1;
      workerRef.current?.terminate();
      workerRef.current = undefined;
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

  function commit(next: EditorState) {
    setHistory((current) => [...current.slice(0, historyIndex + 1), next]);
    setHistoryIndex((current) => current + 1);
    // 提交后剔除已不存在对象的选择；主选失效时回退到剩余集合的顶层。
    pruneSelection(next.marks);
    notifyContentRevision();
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
    const currentSession = sessionIdRef.current;
    if (!currentSession) return;
    void actions.run({
      type: commands.notifyScreenshotRevision,
      sessionId: currentSession,
      revision,
    });
  }

  /** 作废未提交的去水印工作状态：终止 Worker、推进代数、清空预览/选区。 */
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
    if (message) setOperationMessage(message);
  }

  /** 用当前基线（原图 + 已应用补丁）发起 CPU Worker 修补预览。 */
  function startInpaintPreview() {
    if (inpaintBusy || inpaintPreview) return;
    const image = imageRef.current;
    const canvas = canvasRef.current;
    const selection = inpaintSelection;
    if (!image || !canvas || !image.naturalWidth || !image.naturalHeight) {
      setOperationMessage("图片尚未解码完成，无法修补；请稍后重试。");
      return;
    }
    if (!selection) {
      setOperationMessage("请先拖拽框选要去水印的区域。");
      return;
    }
    if (typeof Worker !== "function") {
      setOperationMessage("当前环境不支持本地修补，当前图片未被修改。");
      return;
    }
    const displaySize = canvasSize(canvas);
    const rect = selectionToNaturalRect(
      selection.start,
      selection.end,
      image,
      state.rotation,
      displaySize,
    );
    const preflight = preflightSelection(rect, image);
    if (!preflight.ok) {
      setOperationMessage(
        describeInpaintFailure(preflight.code, preflight.message),
      );
      return;
    }
    // 基线合成到原图尺寸离屏画布：透明/已修补像素一起作为修补输入。
    const base = document.createElement("canvas");
    base.width = image.naturalWidth;
    base.height = image.naturalHeight;
    const baseContext = base.getContext("2d", { willReadFrequently: true });
    if (!baseContext) {
      setOperationMessage("无法读取画布像素，修补未开始。");
      return;
    }
    baseContext.drawImage(image, 0, 0);
    for (const patch of state.patches ?? []) {
      const stored = inpaintPatchStore.current.get(patch.id);
      if (!stored) continue;
      // 替换式叠加：先清除该矩形再写入补丁像素，二次修补的输入不含
      // 旧像素残留，补丁自身的半透明 alpha 也原样保留。
      baseContext.clearRect(patch.x, patch.y, patch.width, patch.height);
      baseContext.drawImage(stored, patch.x, patch.y);
    }
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
    const frozen = {
      key: currentKeyRef.current,
      sessionId: sessionIdRef.current,
      contentVersion: contentRevisionRef.current,
      image,
      selection,
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
      if (frozen.selection !== inpaintSelectionRef.current) {
        setInpaintBusy(false);
        setOperationMessage("选区已变化，本次修补结果已丢弃；请重新预览。");
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
    worker.postMessage(
      {
        generation,
        rgba: buffer,
        width: base.width,
        height: base.height,
        rect,
      },
      [buffer],
    );
  }

  /** 明确应用预览：恰好一次 commit 追加补丁，随历史可撤销/重做。 */
  function applyInpaintPreview() {
    const preview = inpaintPreview;
    if (!preview) return;
    const id = inpaintPatchIdRef.current + 1;
    inpaintPatchIdRef.current = id;
    inpaintPatchStore.current.set(id, preview.canvas);
    commit({
      ...state,
      patches: [...(state.patches ?? []), { id, ...preview.naturalRect }],
    });
    setOperationMessage(
      "已应用修补；可用撤销恢复原图，复制/保存/识别使用修补后的画面。",
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
    ? (editingMark.style?.color ?? DEFAULT_COLOR)
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

  function applyStyleChange(change: StyleChange, targetId?: string): void {
    // 目标优先级：blur 提交携带的 targetId（防点入画布后选区已换）> 当前
    // 主选 > 工具属性记忆。选中标记时就地改样式入历史。
    const candidate =
      targetId !== undefined
        ? state.marks.find((mark) => mark.id === targetId)
        : editingMark;
    const mark =
      candidate && COLOR_TOOLS.includes(candidate.tool) ? candidate : undefined;
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
      const preservedFillColor = editingFill?.color ?? owningTool.fillColor;
      const preservedFillOpacity = editingFill
        ? Math.round(editingFill.alpha * 100)
        : owningTool.fillOpacity;
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
      else
        commit({
          ...state,
          marks: state.marks.map((entry) =>
            entry.id === mark.id ? { ...entry, style: next } : entry,
          ),
        });
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
      // 马赛克/模糊创建时冻结当前强度档位，撤销/重做回放当时效果。
      ...(tool === "mosaic" || tool === "blur"
        ? { intensity: attributes.intensity }
        : {}),
      // 描边类工具冻结边框不透明度；形状工具另冻结填充样式。
      ...(STROKE_TOOLS.includes(tool)
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
    commit({ ...state, resize: next });
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
      setInpaintDraft((current) =>
        current ? { ...current, end: at } : current,
      );
      return;
    }
    if (markDragRef.current) {
      const at = point(event);
      const start = dragStart.current;
      const drag = markDragRef.current;
      if (!start) return;
      const next =
        drag.kind === "resize"
          ? resizeBoxMark(
              drag.origin,
              drag.handle,
              { x: at.x - start.x, y: at.y - start.y },
              event.shiftKey,
            )
          : moveEndpointMark(
              drag.origin,
              drag.endpoint,
              { x: at.x - start.x, y: at.y - start.y },
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
      if (tool === "pen" || tool === "highlighter") {
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
    if (tool === "select") {
      // 主选（单选时）的几何手柄优先命中：矩形类八点缩放、直线/箭头端点；
      // 其次选区四边/四角裁剪手柄；命中对象则替换/增减选择并开始平移，
      // 空白处进入框选草稿（含锁定对象：命中跳过，列表可解锁）。
      const primaryId = selectionRef.current.primary;
      const single =
        selectionRef.current.ids.size === 1 && primaryId !== undefined
          ? state.marks.find((mark) => mark.id === primaryId)
          : undefined;
      const handle =
        single && !single.locked && isBoxResizableMark(single)
          ? rectResizeHandle(single, start)
          : undefined;
      const endpoint =
        !handle && single && !single.locked && isEndpointMark(single)
          ? endpointHandle(single, start)
          : undefined;
      if (single && handle) {
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
      // 重新框选即作废上一轮预览与在途请求；新选区在松开时确定。
      if (inpaintPreview || inpaintBusy) {
        cancelInpaintPending("已放弃上一次修补，请重新框选并预览。");
      } else {
        setInpaintDraft(undefined);
        setInpaintSelection(undefined);
      }
      setInpaintDraft({ start, end: start });
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
        ...(tool === "pen" || tool === "highlighter"
          ? { points: draftPoints.current }
          : {}),
        style: currentStyle(),
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
      const next =
        drag.kind === "resize"
          ? resizeBoxMark(
              drag.origin,
              drag.handle,
              { x: end.x - start.x, y: end.y - start.y },
              event.shiftKey,
            )
          : moveEndpointMark(
              drag.origin,
              drag.endpoint,
              { x: end.x - start.x, y: end.y - start.y },
              event.shiftKey,
            );
      // 几何未变（单击手柄）不入历史：拖拽才产生一条缩放/端点编辑记录。
      const moved =
        Math.hypot(
          next.start.x - drag.origin.start.x,
          next.start.y - drag.origin.start.y,
        ) >= 1 ||
        Math.hypot(
          next.end.x - drag.origin.end.x,
          next.end.y - drag.origin.end.y,
        ) >= 1;
      if (moved) {
        commit({
          ...state,
          marks: state.marks.map((mark) => (mark.id === drag.id ? next : mark)),
        });
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
        commit({
          ...state,
          crop: {
            start: { x: next.x, y: next.y },
            end: { x: next.x + next.width, y: next.y + next.height },
          },
        });
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
      commit({
        ...state,
        marks: moveMarkIds(state.marks, new Set(drag.ids), delta),
      });
      return;
    }
    if (tool === "polyline") {
      // 折线草稿保持开放：拖拽段（≥8px）在松开时补点，单击交给下一次
      // pointerDown 加点；提交只发生在双击/Enter（恰好一条历史）。
      if (draft?.tool === "polyline") {
        const points = [...(draft.points ?? [])];
        const last = points[points.length - 1];
        if (
          Math.hypot(end.x - start.x, end.y - start.y) >= 8 &&
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
    if (tool === "pen" || tool === "highlighter") {
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
      if (length >= 4) {
        commit({
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
        });
      }
      return;
    }
    if (tool === "numbering") {
      // 序号支持单击放置，不要求拖拽距离。
      commit({
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
              nextNumberOrdinal(state.marks, activeToolAttributes.numberStart),
            style: draft?.style ?? currentStyle(),
          },
        ],
      });
      return;
    }
    if (tool === "inpaint") {
      setInpaintDraft(undefined);
      if (Math.hypot(end.x - start.x, end.y - start.y) >= 8) {
        setInpaintSelection({
          start: {
            x: Math.min(start.x, end.x),
            y: Math.min(start.y, end.y),
          },
          end: {
            x: Math.max(start.x, end.x),
            y: Math.max(start.y, end.y),
          },
        });
        setInpaintCompare("after");
        setOperationMessage(
          "已框选修补区域；可重新框选，或点击“预览修补”查看效果。",
        );
      }
      return;
    }
    if (tool === "text" || Math.hypot(end.x - start.x, end.y - start.y) >= 8) {
      let createdId: string | undefined;
      if (tool === "crop") {
        commit({ ...state, crop: { start, end } });
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
        commit({
          ...state,
          marks: [
            ...state.marks,
            {
              id: createdId,
              tool,
              start: box ? box.start : start,
              end: box ? box.end : lineEnd,
              style: draft?.style ?? currentStyle(),
              ...(tool === "text"
                ? { textWidth: Math.abs(end.x - start.x) }
                : {}),
              ...(["text", "callout"].includes(tool)
                ? { text: draft?.text ?? (annotationText.trim() || "文本") }
                : {}),
            },
          ],
        });
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
      commit({
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
      });
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
  async function exportFinalImageIfUnchanged(bakeExclusions = false): Promise<
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
      format: outputFormat,
      quality: jpegQuality,
      inpaint: { patches: appliedInpaintPatches() },
      bakeExclusions,
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

  async function pinCurrentImage() {
    if (!session || !canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
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
        excludeBoxes: currentExclusionBoxes(),
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

  /** 当前排除区的 [0,1000] 归一化矩形（供宿主过滤贴图文字层）。 */
  function currentExclusionBoxes() {
    const image = imageRef.current;
    const canvas = canvasRef.current;
    if (!image || !canvas) return [];
    return exclusionNormalizedRects(image, state, canvasSize(canvas)).map(
      (rect) => ({
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
      }),
    );
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

  async function recognizeCurrentImage() {
    if (!session || !canRecognize || exportInProgressRef.current) return;
    if (refuseIfFullyMasked("recognition")) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage("正在提交显式识别……");
      // 上传未烘焙遮罩的普通最终像素 + 归一化排除框：宿主冻结普通显示
      // 基准并自行生成白色遮罩 OCR 输入；普通基准绝不采用遮罩像素。
      const exported = await exportFinalImageIfUnchanged(false);
      if (!exported) return;
      const started = await actions.run({
        type: commands.recognizeScreenshotImage,
        resourceUri: exported.resourceUri,
        sessionId: exported.sessionId,
        revision: exported.revision,
        excludeBoxes: currentExclusionBoxes(),
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
  function updateFontMark(patch: Partial<Mark>): void {
    if (
      !fontMark ||
      selectedMark === undefined ||
      !selectionModifiable(effectiveSelection())
    )
      return;
    const next = { ...fontMark, ...patch };
    if (textEdit?.id === selectedMark)
      setTextEdit({ id: selectedMark, mark: next });
    else
      commit({
        ...state,
        marks: state.marks.map((mark) =>
          mark.id === selectedMark ? next : mark,
        ),
      });
  }
  function applyTextStyle(patch: Partial<AnnotationTextStyle>): void {
    if (fontMark) updateFontMark({ style: { ...fontMark.style, ...patch } });
    else
      updateToolAttribute({
        textStyle: { ...activeToolAttributes.textStyle, ...patch },
      });
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
      commit({
        ...state,
        marks: state.marks.map((mark) =>
          mark.id === textEdit.id ? textEdit.mark : mark,
        ),
      });
    setTextEdit(undefined);
    setOperationMessage("已完成文字编辑。");
  }
  function resolveFont(requested: string): string {
    const resolved = resolveAnnotationFont(
      canvasRef.current?.getContext("2d") ?? null,
      requested || "system-ui",
    );
    if (resolved.family !== (activeTextStyle.fontFamily ?? "system-ui"))
      applyTextStyle({ fontFamily: resolved.family });
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
    commit({
      ...state,
      marks: state.marks.map((mark) =>
        mark.tool === "numbering" ? { ...mark, ordinal: next++ } : mark,
      ),
    });
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
    commit({ ...state, marks });
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
    commit({ ...state, marks: moveMarkIds(state.marks, ids, delta) });
  }

  function deleteSelection(): void {
    const ids = effectiveSelection();
    if (!ids.size || !selectionModifiable(ids)) return;
    cancelDrawing();
    commit({ ...state, marks: deleteMarkIds(state.marks, ids) });
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

  function pasteClipboard(): void {
    if (!clipboard.length) return;
    const clones = cloneMarks(
      clipboard,
      new Set(clipboard.map((mark) => mark.id)),
      { x: 16, y: 16 },
      nextMarkId,
      nextGroupId,
    );
    if (
      state.marks.filter(isExclusionMark).length +
        clones.filter(isExclusionMark).length >
      MAX_EXCLUSION_MARKS
    ) {
      setOperationMessage("最多保留 64 个屏蔽区域；本次复制未修改对象。");
      return;
    }
    cancelDrawing();
    commit({ ...state, marks: [...state.marks, ...clones] });
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
    cancelDrawing();
    commit({ ...state, marks: [...state.marks, ...clones] });
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
    if (!selectionModifiable(ids)) return;
    const bounds = selectionBounds(state.marks, ids, objectActionContext());
    if (!bounds) return;
    cancelDrawing();
    commit({
      ...state,
      marks: moveMarkIds(state.marks, ids, {
        x: size.width / 2 - bounds.x - bounds.width / 2,
        y: size.height / 2 - bounds.y - bounds.height / 2,
      }),
    });
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
        ? exclusionNormalizedRects(
            imageForLayer,
            state,
            canvasSize(displayCanvas),
          )
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

  // 容器外的工作台控件也可调用文档命令；ref 保留最新回调。
  const documentCommandRef = useRef({
    save: saveAnnotatedImage,
    close: () => actions.run({ type: commands.closeScreenshotSession }),
    canExport,
    isExporting,
    hasSession: !!session,
    suspended: proofreading,
  });
  useEffect(() => {
    documentCommandRef.current = {
      save: saveAnnotatedImage,
      close: () => actions.run({ type: commands.closeScreenshotSession }),
      canExport,
      isExporting,
      hasSession: !!session,
      suspended: proofreading,
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
  const selectionHasOffscreen =
    selectionCount > 0 &&
    state.marks.some(
      (mark) => selectedIds.has(mark.id) && markIsOffscreen(mark),
    );
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
        {selectionHasOffscreen && primaryMark && !primaryMark.locked && (
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
    (COLOR_TOOLS.includes(tool) || tool === "mosaic" || tool === "blur") && (
      <ToolbarButton
        aria-label="重置属性"
        icon={<ToolbarIcon name="refresh" />}
        title="重置当前工具属性为默认值"
        onClick={resetToolAttributes}
      >
        {captureStyle ? undefined : "重置属性"}
      </ToolbarButton>
    );
  const effectIntensityControl = toolset === "full" &&
    (tool === "mosaic" || tool === "blur") && (
      <label className="editor-style-control">
        <span aria-hidden="true">强度</span>
        <Select
          aria-label="马赛克与模糊强度"
          size="small"
          value={String(activeToolAttributes.intensity)}
          onChange={(_, data) =>
            updateToolAttribute({ intensity: Number(data.value) })
          }
        >
          {EFFECT_INTENSITIES.map((level) => (
            <option key={level} value={String(level)}>
              {level === 1 ? "弱" : level === 2 ? "中" : "强"}
            </option>
          ))}
        </Select>
      </label>
    );

  const fontSizeControl = fontTool && (
    <label className="editor-style-control">
      <span aria-hidden="true">字号</span>
      <Select
        aria-label="文字字号"
        size="small"
        value={String(activeTextStyle.fontSize ?? 24)}
        onChange={(_, data) =>
          fontMark
            ? updateFontMark({
                style: { ...fontMark.style, fontSize: Number(data.value) },
              })
            : updateToolAttribute({ fontSize: Number(data.value) })
        }
      >
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

  const inpaintOperations = tool === "inpaint" && (
    <>
      <ToolbarButton
        appearance="primary"
        aria-label="预览修补"
        disabled={!inpaintSelection || inpaintBusy || !!inpaintPreview}
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
      ) : inpaintSelection ? (
        <ToolbarButton
          aria-label="清除修补选区"
          icon={<ToolbarIcon name="exit" />}
          title="清除修补选区"
          onClick={() =>
            cancelInpaintPending("已清除修补选区；当前图片未被修改。")
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
            {selectionCount >= 2 && (
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
                      disabled={!selectionModifiableUi || selectionCount < 3}
                      onClick={() => distributeSelection("h")}
                    >
                      水平等距（≥3，首尾固定）
                    </MenuItem>
                    <MenuItem
                      disabled={!selectionModifiableUi || selectionCount < 3}
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
    select(undefined);
    commit({
      ...state,
      marks: state.marks.filter((mark) => !isExclusionMark(mark)),
    });
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
        commit(
          image && canvas
            ? rotateEditorState(state, image, canvasSize(canvas), nextRotation)
            : { ...state, rotation: nextRotation },
        );
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
    select(undefined);
    commit(EMPTY);
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
  const pinButton = session && (
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
  const recognizeButton = session && (
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
  );
  const closeSessionButton = session && (
    <Button
      title="结束会话"
      icon={<ToolbarIcon name="exit" />}
      aria-label="结束会话"
      appearance="transparent"
      size="small"
      disabled={isExporting}
      onClick={() =>
        void actions.run({ type: commands.closeScreenshotSession })
      }
    >
      {captureStyle ? undefined : "结束会话"}
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
    !!autoTextControl;

  return (
    <div
      className={`canvas-editor${
        captureStyle ? " capture-scene-editor" : ""
      }${proofreading ? " is-proofreading" : ""}`}
      style={captureStyle}
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
          void actions.run({ type: commands.closeScreenshotSession });
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
          {objectActionControls}
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
          {pinButton}
          {recognizeButton}
          {closeSessionButton}
          {captureStyle ? null : clearEditsButton}
          {moreActionsMenu}
        </Toolbar>
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
            {effectIntensityControl}
            {autoTextControl}
            {inpaintCompareControl}
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
            ? "拖拽绘制标注；拖动选区四边与四角可调整裁剪范围。矩形/椭圆选中后可拖八点手柄缩放，直线/箭头/说明可拖端点；文字可双击继续编辑，上排完成或取消。说明从指向位置拖到文字位置。按住 Shift 约束正方形/正圆/45°，按住 Alt 以起点为中心绘制；折线单击加点、双击或 Enter 完成，Esc 放弃草稿。手形或按住 Space 可平移；滚轮可调线宽。马赛克/模糊强度可在工具栏选择，写入复制、保存副本及显式识别输入；画面缩放不改变内容。"
            : "拖拽绘制或裁剪；选择标注后可拖动，矩形/椭圆可拖八点手柄缩放，直线/箭头/说明可拖端点；文字可双击继续编辑，上排完成或取消。说明从指向位置拖到文字位置；已提交裁剪框的四边/四角可继续拖拽调整。按住 Shift 约束正方形/正圆/45°，按住 Alt 以起点为中心绘制；折线单击加点、双击或 Enter 完成，Esc 放弃草稿。手形或按住 Space 可平移；滚轮可调线宽。选择工具下可 Ctrl+点击增减选择、空白拖动框选（完整包含）、Ctrl+A 全选，上排提供复制/粘贴/重复、层级、对齐等距、组合/锁定与旋转/镜像/缩放；方向键微移、Delete 删除。马赛克/模糊强度可在工具栏选择，写入复制、保存副本及显式识别输入；画面缩放不改变内容。"
          : "拖拽框选“屏蔽”区，识别时忽略其中内容；“去水印”先框选、预览修补再应用；旋转调整整图方向。按住 Space 可平移，画面缩放不改变内容。"}
      </p>
      {tool === "inpaint" && (
        <p className="editor-guidance">
          去水印（Beta）：拖拽框选要去水印的区域，先“预览修补”对比前后效果，确认后“应用修补”才写入编辑历史（可撤销，原图始终保留）；应用前可随时取消。
          本地修补基于周边颜色扩散，复杂纹理或大面积覆盖效果有限，不是无损还原。
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
          data-coordinate-width={captureStyle ? captureGeometry?.width : 900}
          data-coordinate-height={captureStyle ? captureGeometry?.height : 600}
          className={`inspection-canvas tool-${tool}`}
          height={captureStyle ? captureGeometry?.height : 600}
          style={{
            aspectRatio: captureStyle
              ? `${captureGeometry?.width} / ${captureGeometry?.height}`
              : "3 / 2",
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
              dragStart.current = undefined;
              setDraftMark(undefined);
              setCropDraft(undefined);
              setInpaintDraft(undefined);
              select(undefined);
              markDragRef.current = undefined;
              cropResizeRef.current = undefined;
              moveDragRef.current = undefined;
              marqueeRef.current = undefined;
              setObjectDraft(undefined);
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
          onPointerDown={pointerDown}
          onPointerMove={pointerMove}
          onPointerUp={pointerUp}
          ref={attachCanvas}
          tabIndex={0}
          width={captureStyle ? captureGeometry?.width : 900}
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
  return {
    ...mark,
    start: move(mark.start),
    end: move(mark.end),
    ...(mark.points ? { points: mark.points.map(move) } : {}),
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
 * 长度随 markScale 缩放，预览与导出同一节奏。 */
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
      context.restore();
    }
    context.restore();
  }
  context.lineWidth = 3 * markScale;
  context.strokeStyle = "#f38b35";
  marks.forEach((mark, index) => {
    if (index < (cached?.markCount ?? 0)) return;
    const width = mark.end.x - mark.start.x;
    const height = mark.end.y - mark.start.y;
    const color = mark.style?.color ?? DEFAULT_COLOR;
    const lineWidth = (mark.style?.strokeWidth ?? 3) * markScale;
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
        applyDashPattern(context, mark.style?.lineStyle, markScale, lineWidth);
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
        applyDashPattern(context, mark.style?.lineStyle, markScale, lineWidth);
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
      applyDashPattern(context, mark.style?.lineStyle, markScale, lineWidth);
      context.stroke();
      context.restore();
    } else if (mark.tool === "line" || mark.tool === "polyline") {
      // 直线/折线共用描边路径：折线用采样点，直线退化 start→end。折线
      // 草稿的活动 end 追加为最后一个顶点：拖拽/悬停中的新段实时可见；
      // 已提交标记 end == 最后顶点，不追加，渲染与旧像素不变。未提交
      // 草稿只进预览画布，导出/历史仍只包含已提交 points。
      const committedPoints = mark.points?.length
        ? mark.points
        : [mark.start, mark.end];
      const points = [...committedPoints];
      if (mark.tool === "polyline") {
        const last = points[points.length - 1];
        if (last && (last.x !== mark.end.x || last.y !== mark.end.y)) {
          points.push(mark.end);
        }
      }
      context.save();
      context.strokeStyle = color;
      context.lineWidth = lineWidth;
      context.globalAlpha = strokeAlpha;
      applyDashPattern(context, mark.style?.lineStyle, markScale, lineWidth);
      if (!mark.style?.lineStyle || mark.style.lineStyle === "solid") {
        context.lineCap = "round";
      }
      context.lineJoin = "round";
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
      const arrowStyle = mark.style?.arrowStyle ?? "open";
      const doubleHead = mark.style?.arrowDouble === true;
      const angle = Math.atan2(height, width);
      // 旧默认开放头：两条 16px 短线，保持旧对象渲染不变。
      const drawOpenHead = (tip: Point, dir: number): void => {
        context.setLineDash([]);
        context.beginPath();
        context.moveTo(tip.x, tip.y);
        context.lineTo(
          tip.x -
            16 * markScale * (mark.objectScale ?? 1) * Math.cos(dir - 0.45),
          tip.y -
            16 * markScale * (mark.objectScale ?? 1) * Math.sin(dir - 0.45),
        );
        context.moveTo(tip.x, tip.y);
        context.lineTo(
          tip.x -
            16 * markScale * (mark.objectScale ?? 1) * Math.cos(dir + 0.45),
          tip.y -
            16 * markScale * (mark.objectScale ?? 1) * Math.sin(dir + 0.45),
        );
        context.stroke();
      };
      // 短箭头的端头共同受总长度约束，防止轴杆反向穿过空心头。
      const headSize = Math.min(
        (10 * (mark.objectScale ?? 1) + (mark.style?.strokeWidth ?? 3) * 2) *
          markScale,
        Math.hypot(width, height) / (doubleHead ? 2 : 1),
      );
      const drawTriangleHead = (
        tip: Point,
        dir: number,
        filled: boolean,
      ): void => {
        const half = headSize / 2.5;
        const base = {
          x: tip.x - headSize * Math.cos(dir),
          y: tip.y - headSize * Math.sin(dir),
        };
        context.setLineDash([]);
        context.beginPath();
        context.moveTo(tip.x, tip.y);
        context.lineTo(
          base.x + half * Math.cos(dir - Math.PI / 2),
          base.y + half * Math.sin(dir - Math.PI / 2),
        );
        context.lineTo(
          base.x + half * Math.cos(dir + Math.PI / 2),
          base.y + half * Math.sin(dir + Math.PI / 2),
        );
        context.closePath();
        if (filled) {
          context.fillStyle = color;
          context.fill();
        }
        context.stroke();
      };
      if (arrowStyle === "open") {
        // 旧路径：完整轴杆 + 端头短线（无轴杆缩短），字节等价旧渲染。
        context.beginPath();
        context.moveTo(mark.start.x, mark.start.y);
        context.lineTo(mark.end.x, mark.end.y);
        applyDashPattern(context, mark.style?.lineStyle, markScale, lineWidth);
        context.stroke();
        drawOpenHead(mark.end, angle);
        if (doubleHead) drawOpenHead(mark.start, angle + Math.PI);
      } else {
        // 三角头：轴杆止于端头底边，空心内部不穿越轴杆。
        const axis = {
          x: Math.cos(angle),
          y: Math.sin(angle),
        };
        const shaftStart = doubleHead
          ? {
              x: mark.start.x + headSize * axis.x,
              y: mark.start.y + headSize * axis.y,
            }
          : mark.start;
        const shaftEnd = {
          x: mark.end.x - headSize * axis.x,
          y: mark.end.y - headSize * axis.y,
        };
        context.beginPath();
        context.moveTo(shaftStart.x, shaftStart.y);
        context.lineTo(shaftEnd.x, shaftEnd.y);
        applyDashPattern(context, mark.style?.lineStyle, markScale, lineWidth);
        context.stroke();
        drawTriangleHead(mark.end, angle, arrowStyle === "solid");
        if (doubleHead) {
          drawTriangleHead(mark.start, angle + Math.PI, arrowStyle === "solid");
        }
      }
      context.restore();
    } else if (isContentMark(mark)) {
      drawTextAnnotation(context, mark, markScale);
    } else if (mark.tool === "mosaic") {
      applyMosaic(context, canvas, mark, markScale);
    } else if (mark.tool === "blur") {
      applyBlur(context, canvas, mark, markScale);
    } else if (mark.tool === "pen" || mark.tool === "highlighter") {
      const points = mark.points ?? [mark.start, mark.end];
      context.setLineDash([]);
      context.save();
      context.strokeStyle = color;
      context.lineCap = "round";
      context.lineJoin = "round";
      if (mark.tool === "highlighter") {
        // 边框不透明度与荧光笔自身 35% 透明相乘：旧标记（缺省 100%）
        // 渲染保持不变，不覆盖原有透明语义。
        context.globalAlpha = 0.35 * strokeAlpha;
        context.lineWidth =
          Math.max((mark.style?.strokeWidth ?? 3) * 4, 12) * markScale;
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
      // 几何编辑手柄：矩形/椭圆八点缩放；直线/箭头两端端点（屏蔽标记
      // 自带八点手柄，不重复绘制）。
      if (
        selectedIds?.size === 1 &&
        !mark.locked &&
        (mark.tool === "rectangle" || mark.tool === "ellipse") &&
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

/** 矩形对角类标记（矩形/椭圆/屏蔽）支持八点缩放。 */
function isBoxResizableMark(mark: Mark): boolean {
  return (
    mark.tool === "rectangle" ||
    mark.tool === "ellipse" ||
    isExclusionMark(mark)
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

const effectSurfaces = new WeakMap<HTMLCanvasElement, HTMLCanvasElement>();
function effectSurface(
  canvas: HTMLCanvasElement,
  width: number,
  height: number,
) {
  let scratch = effectSurfaces.get(canvas);
  if (!scratch) {
    scratch = document.createElement("canvas");
    effectSurfaces.set(canvas, scratch);
  }
  const w = Math.max(1, Math.ceil(width)),
    h = Math.max(1, Math.ceil(height));
  if (scratch.width !== w) scratch.width = w;
  if (scratch.height !== h) scratch.height = h;
  scratch.getContext("2d")?.clearRect(0, 0, w, h);
  return scratch;
}

function applyMosaic(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
) {
  const size = canvasSize(canvas);
  const area = clampRect(normalizedRect(mark), size);
  if (area.width <= 0 || area.height <= 0) return;
  // 强度决定马赛克块尺寸：弱=8px、中=14px、强=26px，预览与导出同源。
  const cell = MOSAIC_CELLS[markEffectIntensity(mark)] ?? 14;
  const scratch = effectSurface(
    canvas,
    area.width / (cell * scale),
    area.height / (cell * scale),
  );
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
  // 强度决定滤波半径：弱=5px、中=10px、强=20px，预览与导出同源。
  const radius = BLUR_RADII[markEffectIntensity(mark)] ?? 10;
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

function clampRect(rect: ReturnType<typeof normalizedRect>, size: CanvasSize) {
  const x = Math.max(0, Math.min(size.width, rect.x));
  const y = Math.max(0, Math.min(size.height, rect.y));
  const right = Math.max(x, Math.min(size.width, rect.x + rect.width));
  const bottom = Math.max(y, Math.min(size.height, rect.y + rect.height));
  return { x, y, width: right - x, height: bottom - y };
}

interface ExportCanvasOptions {
  readonly format: OutputImageFormat;
  readonly quality: number;
  /** 导出只包含已应用补丁；未提交预览绝不进入导出/复制/识别像素。 */
  readonly inpaint?: InpaintDrawLayer;
  /** 识别/屏蔽副本输入：把排除区写入不透明白色像素；普通导出保持 false。 */
  readonly bakeExclusions?: boolean;
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
      const leader =
        mark.tool === "callout"
          ? calloutLeaderSegment(box, mark.start)
          : undefined;
      if (
        (at.x >= box.x - 6 &&
          at.x <= box.x + box.width + 6 &&
          at.y >= box.y - 6 &&
          at.y <= box.y + box.height + 6) ||
        (leader && distanceToSegment(at, leader.from, leader.to) <= 10)
      )
        return mark.id;
      continue;
    }
    // 直线/箭头/折线：按到线段的最短距离命中，避免包络盒内误选；
    // 其余工具沿用包络盒（±10px 容差）；透明对象按几何命中。
    if (
      mark.tool === "line" ||
      mark.tool === "arrow" ||
      mark.tool === "polyline" ||
      mark.tool === "pen" ||
      mark.tool === "highlighter"
    ) {
      const points =
        mark.tool === "polyline" && mark.points?.length
          ? mark.points
          : [mark.start, mark.end];
      let hit = false;
      for (let i = 1; i < points.length; i += 1) {
        if (distanceToSegment(point, points[i - 1]!, points[i]!) <= 10) {
          hit = true;
          break;
        }
      }
      if (hit) return mark.id;
      continue;
    }
    const left = Math.min(mark.start.x, mark.end.x) - 10;
    const right = Math.max(mark.start.x, mark.end.x) + 10;
    const top = Math.min(mark.start.y, mark.end.y) - 10;
    const bottom = Math.max(mark.start.y, mark.end.y) + 10;
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

/** 点到线段的最短距离：命中半径判定用，不引入额外几何库。 */
function distanceToSegment(at: Point, from: Point, to: Point): number {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  if (dx === 0 && dy === 0) return Math.hypot(at.x - from.x, at.y - from.y);
  const t = Math.max(
    0,
    Math.min(
      1,
      ((at.x - from.x) * dx + (at.y - from.y) * dy) / (dx * dx + dy * dy),
    ),
  );
  return Math.hypot(at.x - (from.x + t * dx), at.y - (from.y + t * dy));
}
