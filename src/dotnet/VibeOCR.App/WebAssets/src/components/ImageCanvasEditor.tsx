import {
  Square,
  Circle,
  ArrowUpRight,
  Pencil,
  Highlighter,
  Type,
  ListOrdered,
  Grid2X2,
  Droplets,
  ShieldOff,
  Crop,
  Eraser,
  TextCursor,
  MousePointer2,
  Hand,
  RotateCw,
  Undo2,
  Redo2,
  Copy,
  Save,
  Pin,
  Pipette,
  ScanText,
  X,
  Trash2,
  Settings2,
} from "lucide-react";
import {
  Button,
  Input,
  Select,
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
  type AnnotationTool,
  type CanvasSize,
  type EditorState,
  type Mark,
  type PixelPatch,
  type Point,
} from "./annotationGeometry";
import { uploadAnnotatedImage } from "./annotationHandoff";
import { captureSceneStyle, nativeCaptureScene } from "./captureSceneLayout";
import { ImageTextLayer } from "./ImageTextLayer";
import { toImageTextLines } from "./imageTextLayerGeometry";
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
      }[]
    | null;
}

type Tool =
  "select" | "hand" | "textSelect" | AnnotationTool | "crop" | "inpaint";

const EMPTY: EditorState = { rotation: 0, marks: [] };
const DEFAULT_COLOR = "#f38b35";

const TOOL_LABELS: Readonly<Record<Tool, string>> = {
  select: "选择",
  hand: "手形",
  textSelect: "取字",
  rectangle: "矩形",
  ellipse: "椭圆",
  arrow: "箭头",
  text: "文字",
  mosaic: "马赛克",
  blur: "模糊",
  pen: "画笔",
  highlighter: "荧光笔",
  numbering: "序号",
  exclude: "屏蔽",
  crop: "裁剪",
  inpaint: "去水印",
};

const TOOL_ICONS = {
  select: MousePointer2,
  hand: Hand,
  textSelect: TextCursor,
  rectangle: Square,
  ellipse: Circle,
  arrow: ArrowUpRight,
  text: Type,
  mosaic: Grid2X2,
  blur: Droplets,
  pen: Pencil,
  highlighter: Highlighter,
  numbering: ListOrdered,
  exclude: ShieldOff,
  crop: Crop,
  inpaint: Eraser,
};

const TOOL_ORDER: readonly Tool[] = [
  "select",
  "hand",
  "textSelect",
  "rectangle",
  "ellipse",
  "arrow",
  "text",
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
const FONT_SIZES = [16, 24, 32, 48] as const;

/** 马赛克/模糊强度档位：1=弱、2=中（缺省）、3=强。 */
const EFFECT_INTENSITIES = [1, 2, 3] as const;
const MOSAIC_CELLS: Readonly<Record<number, number>> = { 1: 8, 2: 14, 3: 26 };
const BLUR_RADII: Readonly<Record<number, number>> = { 1: 5, 2: 10, 3: 20 };

/** 标记携带的强度档位：非 1/3 的历史值一律回退到默认 2。 */
function markEffectIntensity(mark: Mark): number {
  const value = mark.style?.intensity;
  return value === 1 || value === 3 ? value : 2;
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
}

export function ImageCanvasEditor({
  actions,
  canExport,
  canRecognize,
  source,
  sourceByteLength,
  session,
  textLayer,
  autoText: autoTextProp,
  showAutoTextPreference = true,
  onAutoTextChange,
  onRecognitionSubmitted,
  toolset = "full",
  commandScope = "recognition",
}: ImageCanvasEditorProps) {
  const commands = EDITOR_COMMANDS[commandScope];
  const captureGeometry = nativeCaptureScene();
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
  const imageRef = useRef<HTMLImageElement | undefined>(undefined);
  const dragStart = useRef<Point | undefined>(undefined);
  const selectedMarkRef = useRef<number | undefined>(undefined);
  const exportInProgressRef = useRef(false);
  // 选定屏蔽矩形的八点缩放拖拽：origin 冻结拖前几何，resizeDraft 驱动实时预览。
  const resizeRef = useRef<
    | {
        readonly index: number;
        readonly handle: ExclusionResizeHandle;
        readonly origin: Mark;
      }
    | undefined
  >(undefined);
  // 选区（裁剪框）四边/四角拖拽：origin 冻结拖前矩形，cropDraft 驱动实时预览。
  const cropResizeRef = useRef<
    | {
        readonly handle: ExclusionResizeHandle;
        readonly origin: { readonly start: Point; readonly end: Point };
      }
    | undefined
  >(undefined);
  const contentRevisionRef = useRef(session?.revision ?? 0);
  const sessionIdRef = useRef(session?.sessionId);
  const [tool, setTool] = useState<Tool>(() => {
    const scene = nativeCaptureScene();
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
  const [strokeColor, setStrokeColor] = useState<string>(DEFAULT_COLOR);
  const [strokeWidth, setStrokeWidth] = useState<number>(3);
  const [fontSize, setFontSize] = useState<number>(24);
  // 马赛克/模糊强度：创建标记时写入 style.intensity，历史条目保留当时档位。
  const [effectIntensity, setEffectIntensity] = useState<number>(2);
  // 支持 EyeDropper 时提供屏幕取色；构造失败/取消保持当前颜色。
  const [eyedropperAvailable] = useState(
    () => typeof (window as WindowWithEyeDropper).EyeDropper === "function",
  );
  const [eyedropperBusy, setEyedropperBusy] = useState(false);
  const [selectedMark, setSelectedMark] = useState<number | undefined>();
  const [history, setHistory] = useState<readonly EditorState[]>([EMPTY]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [imageRevision, setImageRevision] = useState(0);
  // 源图尺寸按 source 键控：换图后旧尺寸立即失效，不在 effect 内同步重置。
  const [decodedSourceSize, setDecodedSourceSize] = useState<{
    source: string;
    size: CanvasSize;
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
  const resizeDraft = useRef<
    { readonly index: number; readonly mark: Mark } | undefined
  >(undefined);
  function setResizeDraft(
    value:
      | { readonly index: number; readonly mark: Mark }
      | undefined
      | ((
          current: { readonly index: number; readonly mark: Mark } | undefined,
        ) => { readonly index: number; readonly mark: Mark } | undefined),
  ) {
    resizeDraft.current =
      typeof value === "function" ? value(resizeDraft.current) : value;
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
    setDraftMark(undefined);
    setCropDraft(undefined);
    dragStart.current = undefined;
    resizeRef.current = undefined;
    cropResizeRef.current = undefined;
    setResizeDraft(undefined);
    selectedMarkRef.current = undefined;
    setSelectedMark(undefined);
    sessionIdRef.current = session?.sessionId;
    contentRevisionRef.current = session?.revision ?? 0;
    setLocalRevision(session?.revision ?? 0);
    // 换图/换会话：作废在途修补请求与迟到响应，丢弃全部补丁像素。
    inpaintGenerationRef.current += 1;
    inpaintWorkerRef.current?.terminate();
    inpaintWorkerRef.current = undefined;
    inpaintPatchStore.current.clear();
    setInpaintDraft(undefined);
    setInpaintSelection(undefined);
    setInpaintPreview(undefined);
    setInpaintBusy(false);
  }

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
    image.onload = () => {
      imageRef.current = image;
      setDecodedSourceSize({
        source,
        size: { width: image.naturalWidth, height: image.naturalHeight },
      });
      setImageRevision((current) => current + 1);
    };
    image.src = source;
    return () => {
      image.onload = null;
      if (imageRef.current === image) imageRef.current = undefined;
    };
  }, [source]);

  // 去水印绘制层：应用补丁从存储解析；预览仅在对比为“修补后”时叠加；
  // 选区高亮按映射后的有效范围回投显示，用户看到的就是实际修改范围。
  const displayCanvasRef = canvasRef.current;
  const imageForLayer = imageRef.current;
  // 已应用补丁只随编辑历史变化；导出/预览编码共用同一份解析结果。
  const appliedInpaintPatches = useMemo<
    readonly { rect: PixelPatch; canvas: HTMLCanvasElement }[]
  >(
    () =>
      (state.patches ?? [])
        .map((patch) => ({
          rect: patch,
          canvas: inpaintPatchStore.current.get(patch.id),
        }))
        .filter(
          (
            entry,
          ): entry is {
            rect: PixelPatch;
            canvas: HTMLCanvasElement;
          } => !!entry.canvas,
        ),
    [state],
  );
  function currentInpaintLayer(): InpaintDrawLayer {
    const preview =
      inpaintPreview && inpaintCompare === "after" ? inpaintPreview : undefined;
    let selection: { start: Point; end: Point } | undefined;
    const drag = inpaintDraft.current ?? inpaintSelection;
    if (
      tool === "inpaint" &&
      drag &&
      imageForLayer &&
      displayCanvasRef &&
      imageForLayer.naturalWidth > 0 &&
      imageForLayer.naturalHeight > 0
    ) {
      const displaySize = canvasSize(displayCanvasRef);
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
      patches: appliedInpaintPatches,
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
      const image = imageRef.current;
      const layer = currentInpaintLayer();
      const crop = cropDraft.current ? undefined : state.crop;
      const renderedState = crop === state.crop ? state : { ...state, crop };
      // Only the unchanged prefix is cached: moving a mark must also recompute
      // all later mosaic/blur marks that sample its pixels. Never cache exports.
      const markCount = Math.min(
        state.marks.length,
        resizeDraft.current?.index ?? selectedMark ?? state.marks.length,
      );
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
        selectedMark,
        resizeDraft.current
          ? state.marks.map((mark, index) =>
              index === resizeDraft.current!.index
                ? resizeDraft.current!.mark
                : mark,
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
      // 宿主揭示门控：首个真实图像帧上屏后才标记；换图/会话由 resetKey 清理。
      if (image && image.naturalWidth > 0 && image.naturalHeight > 0) {
        canvas.dataset.frameReady = "true";
      }
      if (cropDraft.current) {
        drawCropPreview(canvas, cropDraft.current);
      } else if (captureGeometry && !layerReady) {
        // 截图现场：选区即当前裁剪范围；无已提交裁剪时在四边/四角绘制闲置手柄。
        drawSceneCropHandles(canvas, state.crop);
      }
    };
    if (!layerReady) scheduleDraw();
  });
  function scheduleDraw() {
    if (pendingDraw.current !== undefined) return;
    pendingDraw.current = requestAnimationFrame(() => {
      pendingDraw.current = undefined;
      latestDraw.current();
    });
  }

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
  const lastOutputSize = useRef<CanvasSize | undefined>(undefined);
  const currentOutputSize = useMemo<CanvasSize | undefined>(() => {
    const decoded = decodedSourceSize;
    const image = imageRef.current;
    if (
      !decoded ||
      decoded.source !== source ||
      !image ||
      !image.naturalWidth ||
      !image.naturalHeight
    ) {
      return undefined;
    }
    const canvas = canvasRef.current;
    const size = finalOutputSize(
      image,
      state,
      canvas ? canvasSize(canvas) : { width: 900, height: 600 },
    );
    const previous = lastOutputSize.current;
    if (
      previous &&
      previous.width === size.width &&
      previous.height === size.height
    ) {
      return previous;
    }
    lastOutputSize.current = size;
    return size;
  }, [state, decodedSourceSize, source]);

  // 尺寸输入草稿只在真实输出尺寸变化时刷新（渲染期随值调整，不打断输入）。
  const [appliedDraftSize, setAppliedDraftSize] = useState<
    CanvasSize | undefined
  >();
  if (currentOutputSize && appliedDraftSize !== currentOutputSize) {
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
        inpaint: { patches: appliedInpaintPatches },
      });
      if (!exportContextUnchanged(frozen)) {
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

  function commit(next: EditorState) {
    setHistory((current) => [...current.slice(0, historyIndex + 1), next]);
    setHistoryIndex((current) => current + 1);
    notifyContentRevision();
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

  function select(index: number | undefined) {
    selectedMarkRef.current = index;
    setSelectedMark(index);
  }

  function point(event: React.PointerEvent<HTMLCanvasElement>): Point {
    const bounds = event.currentTarget.getBoundingClientRect();
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
    return {
      color: strokeColor,
      strokeWidth,
      fontSize,
      // 马赛克/模糊创建时冻结当前强度档位，撤销/重做回放当时效果。
      ...(tool === "mosaic" || tool === "blur"
        ? { intensity: effectIntensity }
        : {}),
    };
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
        setStrokeColor(hex);
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
      state,
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
    setStrokeColor(hex);
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
    dragStart.current = undefined;
    resizeRef.current = undefined;
    cropResizeRef.current = undefined;
    colorPickPendingRef.current = false;
    setResizeDraft(undefined);
    setCropDraft(undefined);
    setDraftMark(undefined);
    setInpaintDraft(undefined);
  }

  function pointerMove(event: React.PointerEvent<HTMLCanvasElement>) {
    if (colorPickPendingRef.current) {
      event.currentTarget.style.cursor = "crosshair";
      return;
    }
    if (!dragStart.current) {
      if (tool === "select") {
        const at = point(event);
        const selected =
          selectedMarkRef.current === undefined
            ? undefined
            : state.marks[selectedMarkRef.current];
        const handle =
          selected && isExclusionMark(selected)
            ? rectResizeHandle(selected, at)
            : undefined;
        // 选区四边/四角：悬停时提示对应的方向光标。
        const cropBase = handle
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
          : cropHandle
            ? `${cropHandle}-resize`
            : findMark(state.marks, at) === undefined
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
    if (resizeRef.current) {
      const at = point(event);
      const start = dragStart.current;
      setResizeDraft({
        index: resizeRef.current.index,
        mark: resizeExclusionMark(
          resizeRef.current.origin,
          resizeRef.current.handle,
          { x: at.x - start.x, y: at.y - start.y },
        ),
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
      const index = selectedMarkRef.current;
      const mark = index === undefined ? undefined : state.marks[index];
      if (index !== undefined && mark)
        setResizeDraft({
          index,
          mark: moveMark(mark, {
            x: at.x - dragStart.current.x,
            y: at.y - dragStart.current.y,
          }),
        });
      return;
    }
    setDraftMark((current) => {
      if (!current) return current;
      if (tool !== "pen" && tool !== "highlighter")
        return { ...current, end: at };
      const points = draftPoints.current;
      const last = points[points.length - 1];
      if (last && Math.hypot(at.x - last.x, at.y - last.y) < 2) {
        return current;
      }
      points.push(at);
      return { ...current, points, end: at };
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
    if (tool === "textSelect" || tool === "hand" || spaceHeld) return;
    const start = point(event);
    dragStart.current = start;
    if (tool === "select") {
      // 选定屏蔽矩形优先命中八点缩放手柄；其次选区四边/四角裁剪手柄。
      const index = selectedMarkRef.current;
      const selected = index !== undefined ? state.marks[index] : undefined;
      const handle =
        index !== undefined && selected && isExclusionMark(selected)
          ? rectResizeHandle(selected, start)
          : undefined;
      if (index !== undefined && selected && handle) {
        resizeRef.current = { index, handle, origin: selected };
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
          select(findMark(state.marks, start));
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
    } else {
      draftPoints.current = [start];
      setDraftMark({
        tool,
        start,
        end: start,
        ...(tool === "pen" || tool === "highlighter"
          ? { points: draftPoints.current }
          : {}),
        style: currentStyle(),
        ...(tool === "text" ? { text: annotationText.trim() || "文本" } : {}),
        ...(tool === "numbering"
          ? {
              ordinal:
                state.marks.filter((mark) => mark.tool === "numbering").length +
                1,
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
    setResizeDraft(undefined);
    setDraftMark(undefined);
    // 裁剪草稿无论是否提交都结束：短拖/单击不留 0 尺寸草稿裁空画布。
    if (tool === "crop") setCropDraft(undefined);
    if (resizeRef.current) {
      const { index, handle, origin } = resizeRef.current;
      resizeRef.current = undefined;
      setResizeDraft(undefined);
      commit({
        ...state,
        marks: state.marks.map((mark, markIndex) =>
          markIndex === index
            ? resizeExclusionMark(origin, handle, {
                x: end.x - start.x,
                y: end.y - start.y,
              })
            : mark,
        ),
      });
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
      const index = selectedMarkRef.current;
      if (index === undefined) return;
      const delta = { x: end.x - start.x, y: end.y - start.y };
      if (Math.hypot(delta.x, delta.y) < 2) return;
      commit({
        ...state,
        marks: state.marks.map((mark, markIndex) =>
          markIndex === index ? moveMark(mark, delta) : mark,
        ),
      });
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
            tool,
            start,
            end,
            ordinal:
              state.marks.filter((mark) => mark.tool === "numbering").length +
              1,
            style: currentStyle(),
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
    if (Math.hypot(end.x - start.x, end.y - start.y) >= 8) {
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
      } else
        commit({
          ...state,
          marks: [
            ...state.marks,
            {
              tool,
              start,
              end,
              style: currentStyle(),
              ...(tool === "text"
                ? { text: annotationText.trim() || "文本" }
                : {}),
            },
          ],
        });
    }
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
    const frozen = freezeExportContext();
    const blob = await exportCanvas(frozen.image, canvasRef.current, state, {
      format: outputFormat,
      quality: jpegQuality,
      inpaint: { patches: appliedInpaintPatches },
      bakeExclusions,
    });
    const resourceUri = await uploadAnnotatedImage(blob);
    if (!exportContextUnchanged(frozen)) {
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

  const shapeTool =
    tool !== "select" &&
    tool !== "hand" &&
    tool !== "textSelect" &&
    tool !== "crop" &&
    tool !== "text" &&
    tool !== "numbering" &&
    tool !== "exclude";
  const fontTool = tool === "text" || tool === "numbering";
  const panning = tool === "hand" || spaceHeld;

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
      layerReady && imageForLayer && canvasRef.current
        ? exclusionNormalizedRects(
            imageForLayer,
            state,
            canvasSize(canvasRef.current),
          )
        : undefined,
    [layerReady, state, imageForLayer],
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

  return (
    <div
      className={`canvas-editor${captureStyle ? " capture-scene-editor" : ""}`}
      style={captureStyle}
      data-screenshot-session={session?.sessionId ?? ""}
      onKeyDown={(event) => {
        const target = event.target as HTMLElement;
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
            <img src="/__capture_background" alt="" draggable={false} />
            <div className="capture-scene-mask" />
          </div>,
          document.body,
        )}
      <Toolbar
        aria-label="图片编辑工具"
        size="small"
        className="editor-toolbar"
      >
        {TOOLSETS[toolset]
          // 截图现场无裁剪按钮：选区四边/四角拖拽直接调整裁剪。
          .filter((value) => !(captureStyle && value === "crop"))
          .map((value) => {
            const Icon = TOOL_ICONS[value];
            return (
              <ToolbarButton
                appearance={tool === value ? "primary" : "subtle"}
                aria-label={TOOL_LABELS[value]}
                title={TOOL_LABELS[value]}
                icon={captureStyle ? <Icon size={18} /> : undefined}
                aria-pressed={tool === value}
                key={value}
                onClick={() => {
                  cancelDrawing();
                  setTool(value);
                }}
              >
                {captureStyle ? undefined : TOOL_LABELS[value]}
              </ToolbarButton>
            );
          })}
        {tool === "text" && (
          <Input
            aria-label="标注文字"
            size="small"
            value={annotationText}
            onChange={(_, data) => setAnnotationText(data.value)}
          />
        )}
        {toolset === "full" && (
          <div
            aria-label="标注颜色"
            className="editor-color-control"
            role="group"
          >
            {STROKE_COLORS.map((color) => (
              <button
                aria-label={`常用颜色 ${color}`}
                aria-pressed={
                  normalizeHexColor(strokeColor).toLowerCase() === color
                }
                className="editor-color-swatch"
                key={color}
                onClick={() => setStrokeColor(color)}
                title={color}
                type="button"
                style={{ background: color }}
              />
            ))}
            <input
              aria-label="自定义颜色"
              onChange={(event) => setStrokeColor(event.target.value)}
              title="自定义颜色"
              type="color"
              value={normalizeHexColor(strokeColor)}
            />
            <Button
              appearance="subtle"
              aria-label="屏幕取色"
              disabled={eyedropperBusy}
              icon={<Pipette size={16} />}
              onClick={() =>
                eyedropperAvailable ? void pickScreenColor() : enterCanvasPick()
              }
              size="small"
              title={
                eyedropperAvailable
                  ? "屏幕取色"
                  : "取色：点击后在画布上取样颜色"
              }
            />
          </div>
        )}
        {toolset === "full" && (tool === "mosaic" || tool === "blur") && (
          <label className="editor-style-control">
            <span aria-hidden="true">强度</span>
            <Select
              aria-label="马赛克与模糊强度"
              size="small"
              value={String(effectIntensity)}
              onChange={(_, data) => setEffectIntensity(Number(data.value))}
            >
              {EFFECT_INTENSITIES.map((level) => (
                <option key={level} value={String(level)}>
                  {level === 1 ? "弱" : level === 2 ? "中" : "强"}
                </option>
              ))}
            </Select>
          </label>
        )}
        {toolset === "full" && shapeTool && (
          <label className="editor-style-control">
            <span aria-hidden="true">线宽</span>
            <Select
              aria-label="线条宽度"
              size="small"
              value={String(strokeWidth)}
              onChange={(_, data) => setStrokeWidth(Number(data.value))}
            >
              {STROKE_WIDTHS.map((width) => (
                <option key={width} value={String(width)}>
                  {width}px
                </option>
              ))}
            </Select>
          </label>
        )}
        {fontTool && (
          <label className="editor-style-control">
            <span aria-hidden="true">字号</span>
            <Select
              aria-label="文字字号"
              size="small"
              value={String(fontSize)}
              onChange={(_, data) => setFontSize(Number(data.value))}
            >
              {FONT_SIZES.map((size) => (
                <option key={size} value={String(size)}>
                  {size}px
                </option>
              ))}
            </Select>
          </label>
        )}
        <span aria-hidden="true" className="editor-toolbar-break" />
        {session && showAutoTextPreference && (
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
        )}
        {tool === "textSelect" &&
          textLayer?.status === "textlayer.preparing" &&
          session && (
            <ToolbarButton
              aria-label="取消准备文字层"
              onClick={() =>
                void actions.run({
                  type: commands.cancelScreenshotTextLayer,
                  sessionId: session.sessionId,
                  revision: localRevision,
                })
              }
            >
              取消准备
            </ToolbarButton>
          )}
        {tool === "textSelect" &&
          (textLayer?.status === "textlayer.unavailable" ||
            textLayer?.status === "textlayer.failed" ||
            textLayer?.status === "textlayer.cancelled") && (
            <ToolbarButton
              aria-label="重新准备文字层"
              disabled={!canRecognize}
              onClick={() => setPrepareNonce((nonce) => nonce + 1)}
            >
              重新准备
            </ToolbarButton>
          )}
        {tool === "textSelect" && selectionText.length > 0 && session && (
          <Button
            appearance="primary"
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
            复制所选
          </Button>
        )}
        {tool === "inpaint" && (
          <>
            <ToolbarButton
              appearance="primary"
              aria-label="预览修补"
              disabled={!inpaintSelection || inpaintBusy || !!inpaintPreview}
              onClick={startInpaintPreview}
            >
              预览修补
            </ToolbarButton>
            {inpaintBusy ? (
              <ToolbarButton
                aria-label="取消修补"
                onClick={() =>
                  cancelInpaintPending("已取消修补；当前图片未被修改。")
                }
              >
                取消修补
              </ToolbarButton>
            ) : inpaintPreview ? (
              <>
                <ToolbarButton
                  appearance="primary"
                  aria-label="应用修补"
                  onClick={applyInpaintPreview}
                >
                  应用修补
                </ToolbarButton>
                <ToolbarButton
                  aria-label="放弃修补预览"
                  onClick={() =>
                    cancelInpaintPending("已放弃修补预览；当前图片未被修改。")
                  }
                >
                  放弃预览
                </ToolbarButton>
                <label className="editor-style-control">
                  <span aria-hidden="true">对比</span>
                  <Select
                    aria-label="修补对比"
                    size="small"
                    value={inpaintCompare}
                    onChange={(_, data) =>
                      setInpaintCompare(
                        data.value === "before" ? "before" : "after",
                      )
                    }
                  >
                    <option value="after">修补后</option>
                    <option value="before">本次修补前</option>
                  </Select>
                </label>
              </>
            ) : inpaintSelection ? (
              <ToolbarButton
                aria-label="清除修补选区"
                onClick={() =>
                  cancelInpaintPending("已清除修补选区；当前图片未被修改。")
                }
              >
                清除选区
              </ToolbarButton>
            ) : null}
          </>
        )}
        <ToolbarButton
          title="旋转 90°"
          icon={captureStyle ? <RotateCw size={18} /> : undefined}
          aria-label="旋转 90°"
          onClick={() => {
            const image = imageRef.current;
            const canvas = canvasRef.current;
            const nextRotation = (state.rotation + 90) % 360;
            commit(
              image && canvas
                ? rotateEditorState(
                    state,
                    image,
                    canvasSize(canvas),
                    nextRotation,
                  )
                : { ...state, rotation: nextRotation },
            );
          }}
        >
          {captureStyle ? undefined : "旋转 90°"}
        </ToolbarButton>
        <ToolbarButton
          title="清空屏蔽"
          icon={captureStyle ? <ShieldOff size={18} /> : undefined}
          aria-label="清空屏蔽"
          disabled={!hasExclusions}
          onClick={() => {
            // 只移除排除区：普通标注保留，几何变更入历史可撤销。
            select(undefined);
            setResizeDraft(undefined);
            resizeRef.current = undefined;
            commit({
              ...state,
              marks: state.marks.filter((mark) => !isExclusionMark(mark)),
            });
            setOperationMessage("已清空全部屏蔽区；可用撤销恢复。");
          }}
        >
          {captureStyle ? undefined : "清空屏蔽"}
        </ToolbarButton>
        <ToolbarButton
          title="撤销"
          icon={captureStyle ? <Undo2 size={18} /> : undefined}
          aria-label="撤销"
          disabled={historyIndex === 0}
          onClick={undo}
        >
          {captureStyle ? undefined : "撤销"}
        </ToolbarButton>
        <ToolbarButton
          title="重做"
          icon={captureStyle ? <Redo2 size={18} /> : undefined}
          aria-label="重做"
          disabled={historyIndex >= history.length - 1}
          onClick={redo}
        >
          {captureStyle ? undefined : "重做"}
        </ToolbarButton>
      </Toolbar>
      {toolset === "full" && (
        <details
          className="editor-output-settings"
          open={captureStyle ? undefined : true}
        >
          <summary aria-label="输出设置" title="输出设置">
            <Settings2 size={18} />
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
              disabled={isCheckingExport}
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
            ? "拖拽绘制标注；拖动选区四边与四角可调整裁剪范围。手形或按住 Space 可平移。画笔与荧光笔沿拖拽轨迹绘制，序号单击放置。马赛克/模糊强度可在工具栏选择，与打码一样写入复制、保存副本及显式识别输入；画面缩放不改变内容。"
            : "拖拽绘制或裁剪；选择标注后可拖动，已提交裁剪框的四边/四角可继续拖拽调整。手形或按住 Space 可平移。画笔与荧光笔沿拖拽轨迹绘制，序号单击放置。马赛克/模糊强度可在工具栏选择，写入复制、保存副本及显式识别输入；画面缩放不改变内容。"
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
        tabIndex={0}
        style={{
          ["--editor-zoom" as string]: zoom,
          overflow: "auto",
          maxHeight: "70vh",
          position: "relative",
          cursor: panning ? "grab" : undefined,
        }}
        onContextMenu={(event) => {
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
        {layerReady ? (
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
            display: layerReady ? "none" : "block",
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
            const modifier = event.ctrlKey || event.metaKey;
            if (event.key === "Escape" && colorPickPendingRef.current) {
              // 取消画布取色：不触发选区清除/工具切换/会话关闭。
              event.preventDefault();
              event.stopPropagation();
              colorPickPendingRef.current = false;
              setOperationMessage("已取消取色；标注颜色保持不变。");
              return;
            }
            if (modifier && event.key.toLowerCase() === "z") {
              event.preventDefault();
              if (event.shiftKey) redo();
              else undo();
            } else if (modifier && event.key.toLowerCase() === "y") {
              event.preventDefault();
              redo();
            } else if (event.key === "Delete" && selectedMark !== undefined) {
              event.preventDefault();
              commit({
                ...state,
                marks: state.marks.filter((_, index) => index !== selectedMark),
              });
              select(undefined);
            } else if (event.key === "Escape") {
              dragStart.current = undefined;
              setDraftMark(undefined);
              setCropDraft(undefined);
              setInpaintDraft(undefined);
              select(undefined);
              resizeRef.current = undefined;
              cropResizeRef.current = undefined;
              setResizeDraft(undefined);
              setTool("select");
            }
          }}
          onPointerCancel={cancelDrawing}
          onPointerDown={pointerDown}
          onPointerMove={pointerMove}
          onPointerUp={pointerUp}
          ref={canvasRef}
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
        <Button
          title="复制标注图"
          icon={captureStyle ? <Copy size={18} /> : undefined}
          aria-label="复制标注图"
          size="small"
          disabled={!canExport || isExporting}
          onClick={() => void copyAnnotatedImage()}
        >
          {captureStyle ? undefined : "复制标注图"}
        </Button>
        {hasExclusions && (
          <Button
            size="small"
            disabled={!canExport || isExporting}
            onClick={() => void copyAnnotatedImage(true)}
          >
            复制屏蔽副本
          </Button>
        )}
        <Button
          title="保存标注图"
          icon={captureStyle ? <Save size={18} /> : undefined}
          aria-label="保存标注图"
          size="small"
          disabled={!canExport || isExporting}
          onClick={() => void saveAnnotatedImage()}
        >
          {captureStyle ? undefined : "保存标注图"}
        </Button>
        {hasExclusions && (
          <Button
            title="保存屏蔽副本"
            icon={captureStyle ? <ShieldOff size={18} /> : undefined}
            aria-label="保存屏蔽副本"
            size="small"
            disabled={!canExport || isExporting}
            onClick={() => void saveAnnotatedImage(true)}
          >
            {captureStyle ? undefined : "保存屏蔽副本"}
          </Button>
        )}
        {session && (
          <>
            <Button
              title="贴图"
              icon={captureStyle ? <Pin size={18} /> : undefined}
              aria-label="贴图"
              size="small"
              disabled={!canExport || isExporting}
              onClick={() => void pinCurrentImage()}
            >
              {captureStyle ? undefined : "贴图"}
            </Button>
            <Button
              title="识别当前图"
              icon={captureStyle ? <ScanText size={18} /> : undefined}
              aria-label="识别当前图"
              appearance="primary"
              size="small"
              disabled={!canRecognize || isExporting}
              onClick={() => void recognizeCurrentImage()}
            >
              {captureStyle ? undefined : "识别当前图"}
            </Button>
            <Button
              title="结束会话"
              icon={captureStyle ? <X size={18} /> : undefined}
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
          </>
        )}
        <Button
          title="清除编辑"
          icon={captureStyle ? <Trash2 size={18} /> : undefined}
          aria-label="清除编辑"
          appearance="transparent"
          size="small"
          disabled={
            state.marks.length === 0 &&
            state.rotation === 0 &&
            !state.crop &&
            !state.resize &&
            !state.patches?.length
          }
          onClick={() => {
            select(undefined);
            commit(EMPTY);
          }}
        >
          {captureStyle ? undefined : "清除编辑"}
        </Button>
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

function markBounds(mark: Mark) {
  let left = mark.start.x,
    right = mark.start.x,
    top = mark.start.y,
    bottom = mark.start.y;
  for (const point of mark.points ?? [mark.start, mark.end]) {
    left = Math.min(left, point.x);
    right = Math.max(right, point.x);
    top = Math.min(top, point.y);
    bottom = Math.max(bottom, point.y);
  }
  return { left, right, top, bottom };
}

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
  selectedMark: number | undefined,
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
    if (mark.tool === "rectangle") {
      context.setLineDash([]);
      context.lineWidth = lineWidth;
      context.strokeStyle = color;
      context.strokeRect(mark.start.x, mark.start.y, width, height);
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
      context.stroke();
    } else if (mark.tool === "arrow") {
      context.setLineDash([]);
      context.lineWidth = lineWidth;
      context.strokeStyle = color;
      context.beginPath();
      context.moveTo(mark.start.x, mark.start.y);
      context.lineTo(mark.end.x, mark.end.y);
      context.stroke();
      const angle = Math.atan2(height, width);
      context.beginPath();
      context.moveTo(mark.end.x, mark.end.y);
      context.lineTo(
        mark.end.x - 16 * markScale * Math.cos(angle - 0.45),
        mark.end.y - 16 * markScale * Math.sin(angle - 0.45),
      );
      context.moveTo(mark.end.x, mark.end.y);
      context.lineTo(
        mark.end.x - 16 * markScale * Math.cos(angle + 0.45),
        mark.end.y - 16 * markScale * Math.sin(angle + 0.45),
      );
      context.stroke();
    } else if (mark.tool === "text") {
      context.setLineDash([]);
      context.fillStyle = color;
      context.font = `600 ${(mark.style?.fontSize ?? 24) * markScale}px system-ui, sans-serif`;
      context.fillText(mark.text || "文本", mark.start.x, mark.end.y);
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
        context.globalAlpha = 0.35;
        context.lineWidth =
          Math.max((mark.style?.strokeWidth ?? 3) * 4, 12) * markScale;
      } else {
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
        drawExclusionChrome(context, mark, selectedMark === index);
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
      const size = (mark.style?.fontSize ?? 24) * markScale;
      const radius = Math.max(size * 0.68, 8 * markScale);
      context.setLineDash([]);
      context.save();
      context.fillStyle = color;
      context.beginPath();
      context.arc(mark.start.x, mark.start.y, radius, 0, Math.PI * 2);
      context.fill();
      context.fillStyle = "#161616";
      context.font = `700 ${size}px system-ui, sans-serif`;
      context.textAlign = "center";
      context.textBaseline = "middle";
      context.fillText(String(mark.ordinal ?? 1), mark.start.x, mark.start.y);
      context.restore();
    }
    if (showEditorChrome && selectedMark === index) {
      const bounds = markBounds(mark);
      context.setLineDash([7, 5]);
      context.lineWidth = 1 * markScale;
      context.strokeStyle = "#f38b35";
      context.strokeRect(
        bounds.left - 5,
        bounds.top - 5,
        bounds.right - bounds.left + 10,
        bounds.bottom - bounds.top + 10,
      );
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
  if (area.width <= 0 || area.height <= 0) return;
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

/** 任意 start/end 矩形的八点手柄命中测试（含屏蔽标记与选区裁剪框）。 */
function rectResizeHandle(
  rect: Pick<Mark, "start" | "end">,
  at: Point,
): ExclusionResizeHandle | undefined {
  const area = normalizedRect(rect);
  if (area.width <= 0 || area.height <= 0) return undefined;
  for (const handle of EXCLUSION_RESIZE_HANDLES) {
    const point = exclusionHandlePoint(area, handle);
    if (Math.hypot(at.x - point.x, at.y - point.y) <= EXCLUSION_HANDLE_RADIUS) {
      return handle;
    }
  }
  return undefined;
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

/** 拖动手柄调整屏蔽矩形：反向拖拽自动翻转，不产生负尺寸。 */
function resizeExclusionMark(
  mark: Mark,
  handle: ExclusionResizeHandle,
  delta: Point,
): Mark {
  return {
    ...mark,
    ...resizeRectPoints(mark.start, mark.end, handle, delta),
  };
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

function findMark(marks: readonly Mark[], point: Point): number | undefined {
  for (let index = marks.length - 1; index >= 0; index -= 1) {
    const mark = marks[index];
    if (!mark) continue;
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
      return index;
  }
  return undefined;
}
