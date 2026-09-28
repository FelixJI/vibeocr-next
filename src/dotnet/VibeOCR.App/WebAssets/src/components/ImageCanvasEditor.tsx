import {
  Button,
  Input,
  Select,
  Toolbar,
  ToolbarButton,
} from "@fluentui/react-components";
import { useEffect, useMemo, useRef, useState } from "react";
import type { AppActions } from "../app/types";
import {
  imageTransform,
  outputSize,
  projectPoint,
  rotateEditorState,
  type AnnotationTool,
  type CanvasSize,
  type EditorState,
  type Mark,
  type Point,
} from "./annotationGeometry";
import { uploadAnnotatedImage } from "./annotationHandoff";
import { ImageTextLayer } from "./ImageTextLayer";
import { toImageTextLines } from "./imageTextLayerGeometry";

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

type Tool = "select" | "hand" | "textSelect" | AnnotationTool | "crop";

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
  crop: "裁剪",
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
  "crop",
];

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

/** 活动纯截图会话句柄；宿主回显当前内容修订。 */
export interface ScreenshotSessionHandle {
  readonly sessionId: string;
  readonly revision: number;
  readonly textSelectionRequested?: boolean;
}

interface ImageCanvasEditorProps {
  readonly actions: AppActions;
  readonly canExport: boolean;
  readonly canRecognize: boolean;
  readonly source: string;
  readonly session?: ScreenshotSessionHandle;
  readonly textLayer?: ScreenshotTextLayerState;
  readonly autoText?: boolean;
  readonly showAutoTextPreference?: boolean;
  readonly onAutoTextChange?: (enabled: boolean) => void;
}

export function ImageCanvasEditor({
  actions,
  canExport,
  canRecognize,
  source,
  session,
  textLayer,
  autoText: autoTextProp,
  showAutoTextPreference = true,
  onAutoTextChange,
}: ImageCanvasEditorProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const imageRef = useRef<HTMLImageElement | undefined>(undefined);
  const dragStart = useRef<Point | undefined>(undefined);
  const selectedMarkRef = useRef<number | undefined>(undefined);
  const exportInProgressRef = useRef(false);
  const contentRevisionRef = useRef(session?.revision ?? 0);
  const sessionIdRef = useRef(session?.sessionId);
  const [tool, setTool] = useState<Tool>("select");
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
  const [selectedMark, setSelectedMark] = useState<number | undefined>();
  const [history, setHistory] = useState<readonly EditorState[]>([EMPTY]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [imageRevision, setImageRevision] = useState(0);
  const [isExporting, setIsExporting] = useState(false);
  const [draftMark, setDraftMark] = useState<Mark | undefined>();
  // 本地修订：编辑提交即时推进，不等宿主回显；文字层绑定据此立即失效。
  const [localRevision, setLocalRevision] = useState(session?.revision ?? 0);
  const [localAutoText, setLocalAutoText] = useState(false);
  const autoText = autoTextProp ?? localAutoText;
  const [prepareNonce, setPrepareNonce] = useState(0);
  const [operationMessage, setOperationMessage] = useState(
    session
      ? "纯截图会话：标注后可复制、保存或显式识别当前图；不会自动提交 OCR。"
      : "标注只影响复制或保存的图片副本，不会重新识别。",
  );
  const state = history[historyIndex] ?? EMPTY;

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
    selectedMarkRef.current = undefined;
    setSelectedMark(undefined);
    sessionIdRef.current = session?.sessionId;
    contentRevisionRef.current = session?.revision ?? 0;
    setLocalRevision(session?.revision ?? 0);
  }

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
      setImageRevision((current) => current + 1);
    };
    image.src = source;
    return () => {
      image.onload = null;
      if (imageRef.current === image) imageRef.current = undefined;
    };
  }, [source]);

  useEffect(() => {
    draw(
      canvasRef.current,
      imageRef.current,
      state,
      selectedMark,
      draftMark ? [...state.marks, draftMark] : state.marks,
    );
  }, [imageRevision, selectedMark, state, draftMark]);

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
    prepareInFlight.current = true;
    try {
      const exported = await exportFinalPngIfUnchanged();
      if (!exported) return;
      await actions.run({
        type: "recognition.prepareScreenshotTextLayer",
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
    const currentSession = sessionIdRef.current;
    if (!currentSession) return;
    contentRevisionRef.current += 1;
    const revision = contentRevisionRef.current;
    setLocalRevision(revision);
    void actions.run({
      type: "recognition.notifyScreenshotRevision",
      sessionId: currentSession,
      revision,
    });
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
        event.currentTarget.width,
      y:
        ((event.clientY - bounds.top) / Math.max(bounds.height, 1)) *
        event.currentTarget.height,
    };
  }

  function undo() {
    if (historyIndex === 0) return;
    setHistoryIndex(historyIndex - 1);
    notifyContentRevision();
  }

  function redo() {
    if (historyIndex >= history.length - 1) return;
    setHistoryIndex(historyIndex + 1);
    notifyContentRevision();
  }

  function currentStyle(): Mark["style"] {
    return { color: strokeColor, strokeWidth, fontSize };
  }

  function pointerMove(event: React.PointerEvent<HTMLCanvasElement>) {
    if (!dragStart.current) return;
    if (tool !== "pen" && tool !== "highlighter") return;
    const at = point(event);
    setDraftMark((current) => {
      if (!current) return current;
      const points = current.points ?? [];
      const last = points[points.length - 1];
      if (last && Math.hypot(at.x - last.x, at.y - last.y) < 2) {
        return current;
      }
      return { ...current, points: [...points, at], end: at };
    });
  }

  function pointerDown(event: React.PointerEvent<HTMLCanvasElement>) {
    if (tool === "textSelect" || tool === "hand" || spaceHeld) return;
    const start = point(event);
    dragStart.current = start;
    if (tool === "select") {
      select(findMark(state.marks, start));
    } else if (tool === "pen" || tool === "highlighter") {
      setDraftMark({
        tool,
        start,
        end: start,
        points: [start],
        style: currentStyle(),
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
    dragStart.current = undefined;
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
      const draft = draftMark;
      setDraftMark(undefined);
      const points = draft?.points ?? [start, end];
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
    if (Math.hypot(end.x - start.x, end.y - start.y) >= 8) {
      if (tool === "crop") commit({ ...state, crop: { start, end } });
      else
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
    readonly revision: number | undefined;
    readonly image: HTMLImageElement | undefined;
  }

  function freezeExportContext(): FrozenExportContext {
    const sessionId = sessionIdRef.current;
    return {
      key: currentKeyRef.current,
      sessionId,
      revision: sessionId ? contentRevisionRef.current : undefined,
      image: imageRef.current,
    };
  }

  function exportContextUnchanged(frozen: FrozenExportContext): boolean {
    if (
      frozen.key !== currentKeyRef.current ||
      frozen.sessionId !== sessionIdRef.current ||
      frozen.image !== imageRef.current
    ) {
      return false;
    }
    // 上传期间发生编辑（修订前进）同样禁止把旧像素贴上新修订。
    return (
      frozen.sessionId === undefined ||
      frozen.revision === contentRevisionRef.current
    );
  }

  /** 三出口共用的安全导出：上传归来后校验未变；变化则不发送。 */
  async function exportFinalPngIfUnchanged(): Promise<
    { resourceUri: string; sessionId?: string; revision?: number } | undefined
  > {
    const frozen = freezeExportContext();
    const blob = await exportCanvas(frozen.image, canvasRef.current, state);
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
      revision: frozen.revision,
    };
  }

  async function copyAnnotatedImage() {
    if (!canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage("正在生成并复制标注图片……");
      const exported = await exportFinalPngIfUnchanged();
      if (!exported) return;
      const copied = exported.sessionId
        ? await actions.run({
            type: "recognition.copyScreenshotImage",
            resourceUri: exported.resourceUri,
            sessionId: exported.sessionId,
            revision: exported.revision,
          })
        : await actions.run({
            type: "recognition.copyAnnotatedImage",
            resourceUri: exported.resourceUri,
          });
      setOperationMessage(
        copied
          ? exported.sessionId
            ? "已复制截图副本；显式识别只会使用当前最终画面。"
            : "已复制标注图片副本。识别结果保持不变。"
          : "复制未完成，请查看页面提示后重试。",
      );
    } catch {
      setOperationMessage("无法生成或传递标注图片，请重试。");
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  async function saveAnnotatedImage() {
    if (!canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage("正在生成标注图片并打开系统保存窗口……");
      const exported = await exportFinalPngIfUnchanged();
      if (!exported) return;
      const saved = exported.sessionId
        ? await actions.run({
            type: "recognition.saveScreenshotImage",
            resourceUri: exported.resourceUri,
            sessionId: exported.sessionId,
            revision: exported.revision,
          })
        : await actions.run({
            type: "recognition.saveAnnotatedImage",
            resourceUri: exported.resourceUri,
          });
      setOperationMessage(
        saved
          ? exported.sessionId
            ? "已保存截图副本；显式识别只会使用当前最终画面。"
            : "已保存标注图片副本。识别结果保持不变。"
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
      const exported = await exportFinalPngIfUnchanged();
      if (!exported?.sessionId) return;
      const pinned = await actions.run({
        type: "recognition.pinScreenshotImage",
        resourceUri: exported.resourceUri,
        sessionId: exported.sessionId,
        revision: exported.revision,
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

  async function recognizeCurrentImage() {
    if (!session || !canRecognize || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage("正在导出当前最终画面并提交显式识别……");
      const exported = await exportFinalPngIfUnchanged();
      if (!exported) return;
      const started = await actions.run({
        type: "recognition.recognizeScreenshotImage",
        resourceUri: exported.resourceUri,
        sessionId: exported.sessionId,
        revision: exported.revision,
      });
      setOperationMessage(
        started
          ? "已提交识别当前图；完成后结果显示在右侧。"
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
    tool !== "numbering";
  const fontTool = tool === "text" || tool === "numbering";
  const panning = tool === "hand" || spaceHeld;

  // 取字模式：仅当宿主层就绪且绑定与当前本地修订一致时展示；
  // 显示的图像就是识别输入的同一最终 PNG，不田旧层映射。
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
  function copySelectedText(text = selectionText) {
    if (!session || !text || !layerSelectionKey) return;
    setCopyMenu(undefined);
    void actions.run({
      type: "recognition.copyScreenshotSelection",
      sessionId: session.sessionId,
      revision: localRevision,
      text,
    });
  }
  useEffect(() => {
    if (!layerSelectionKey) return undefined;
    const readSelection = () => {
      const root = stageRef.current;
      const documentSelection = document.getSelection();
      const anchor = documentSelection?.anchorNode;
      const text =
        root &&
        anchor &&
        root.contains(anchor) &&
        documentSelection &&
        documentSelection.rangeCount > 0
          ? documentSelection.getRangeAt(0).toString()
          : "";
      setSelection((current) =>
        current?.key === layerSelectionKey && current.text === text
          ? current
          : { key: layerSelectionKey, text },
      );
    };
    document.addEventListener("selectionchange", readSelection);
    return () => document.removeEventListener("selectionchange", readSelection);
  }, [layerSelectionKey, layerLines, stageSize]);

  const textLayerHint = textLayerStatusLabel(textLayer);

  return (
    <div
      className="canvas-editor"
      onKeyDown={(event) => {
        const target = event.target as HTMLElement;
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
      <Toolbar
        aria-label="图片编辑工具"
        size="small"
        className="editor-toolbar"
      >
        {TOOL_ORDER.map((value) => (
          <ToolbarButton
            appearance={tool === value ? "primary" : "subtle"}
            aria-pressed={tool === value}
            key={value}
            onClick={() => setTool(value)}
          >
            {TOOL_LABELS[value]}
          </ToolbarButton>
        ))}
        {tool === "text" && (
          <Input
            aria-label="标注文字"
            size="small"
            value={annotationText}
            onChange={(_, data) => setAnnotationText(data.value)}
          />
        )}
        <label className="editor-style-control">
          <span aria-hidden="true">颜色</span>
          <Select
            aria-label="标注颜色"
            size="small"
            value={strokeColor}
            onChange={(_, data) => setStrokeColor(data.value)}
          >
            {STROKE_COLORS.map((color) => (
              <option key={color} value={color}>
                {color}
              </option>
            ))}
          </Select>
        </label>
        {shapeTool && (
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
                  type: "recognition.cancelScreenshotTextLayer",
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
            onClick={() => copySelectedText()}
          >
            复制所选
          </Button>
        )}
        <ToolbarButton
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
                    { width: canvas.width, height: canvas.height },
                    nextRotation,
                  )
                : { ...state, rotation: nextRotation },
            );
          }}
        >
          旋转 90°
        </ToolbarButton>
        <ToolbarButton
          aria-label="撤销"
          disabled={historyIndex === 0}
          onClick={undo}
        >
          撤销
        </ToolbarButton>
        <ToolbarButton
          aria-label="重做"
          disabled={historyIndex >= history.length - 1}
          onClick={redo}
        >
          重做
        </ToolbarButton>
      </Toolbar>
      <p className="editor-guidance">
        拖拽绘制或裁剪；选择标注后可拖动。手形或按住 Space
        可平移。画笔与荧光笔沿拖拽轨迹绘制，序号单击放置。马赛克、模糊与打码会写入复制、保存副本及显式识别输入；画面缩放不改变内容。
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
          overflow: "auto",
          maxHeight: "70vh",
          position: "relative",
          cursor: panning ? "grab" : undefined,
        }}
        onContextMenu={(event) => {
          if (tool !== "textSelect" || !selectionText) return;
          event.preventDefault();
          const bounds = event.currentTarget.getBoundingClientRect();
          setCopyMenu({
            x: event.clientX - bounds.left + event.currentTarget.scrollLeft,
            y: event.clientY - bounds.top + event.currentTarget.scrollTop,
            text: selectionText,
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
            ref={stageRef}
            style={{
              margin: "0 auto",
              position: "relative",
              width: "fit-content",
            }}
          >
            <img
              alt="截图最终画面"
              className="inspection-canvas"
              src={layerImageUrl}
              style={{ width: `${780 * zoom}px`, maxWidth: "none" }}
            />
            {layerImage && stageSize && textLayer?.binding && session ? (
              <ImageTextLayer
                activeSession={{
                  sessionId: session.sessionId,
                  revision: localRevision,
                }}
                binding={textLayer.binding}
                lines={toImageTextLines(
                  (layerLines ?? []).map((line) => ({
                    text: line.text,
                    bbox: [line.x1, line.y1, line.x2, line.y2] as const,
                    order: line.order ?? undefined,
                  })),
                )}
                viewport={{ image: layerImage, size: stageSize }}
              />
            ) : null}
          </div>
        ) : null}
        <canvas
          aria-label="图片检查画布"
          className={`inspection-canvas tool-${tool}`}
          height={600}
          style={{
            display: layerReady ? "none" : "block",
            width: `${780 * zoom}px`,
            maxWidth: "none",
          }}
          onKeyDown={(event) => {
            const modifier = event.ctrlKey || event.metaKey;
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
              select(undefined);
              setTool("select");
            }
          }}
          onPointerDown={pointerDown}
          onPointerMove={pointerMove}
          onPointerUp={pointerUp}
          ref={canvasRef}
          tabIndex={0}
          width={900}
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
            onClick={() => copySelectedText(copyMenu.text)}
          >
            复制所选
          </Button>
        )}
      </div>
      <div className="editor-footer">
        <Button
          size="small"
          disabled={!canExport || isExporting}
          onClick={() => void copyAnnotatedImage()}
        >
          复制标注图
        </Button>
        <Button
          size="small"
          disabled={!canExport || isExporting}
          onClick={() => void saveAnnotatedImage()}
        >
          保存标注图
        </Button>
        {session && (
          <>
            <Button
              size="small"
              disabled={!canExport || isExporting}
              onClick={() => void pinCurrentImage()}
            >
              贴图
            </Button>
            <Button
              appearance="primary"
              size="small"
              disabled={!canRecognize || isExporting}
              onClick={() => void recognizeCurrentImage()}
            >
              识别当前图
            </Button>
            <Button
              appearance="transparent"
              size="small"
              disabled={isExporting}
              onClick={() =>
                void actions.run({
                  type: "recognition.closeScreenshotSession",
                })
              }
            >
              结束会话
            </Button>
          </>
        )}
        <Button
          appearance="transparent"
          size="small"
          disabled={
            state.marks.length === 0 && state.rotation === 0 && !state.crop
          }
          onClick={() => {
            select(undefined);
            commit(EMPTY);
          }}
        >
          清除编辑
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
  const points = mark.points ?? [mark.start, mark.end];
  const xs = points.map((at) => at.x);
  const ys = points.map((at) => at.y);
  return {
    left: Math.min(...xs, mark.start.x, mark.end.x),
    right: Math.max(...xs, mark.start.x, mark.end.x),
    top: Math.min(...ys, mark.start.y, mark.end.y),
    bottom: Math.max(...ys, mark.start.y, mark.end.y),
  };
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

function draw(
  canvas: HTMLCanvasElement | null,
  image: HTMLImageElement | undefined,
  state: EditorState,
  selectedMark: number | undefined,
  marksOverride?: readonly Mark[],
  showEditorChrome = true,
  markScale = 1,
) {
  const context = canvas?.getContext("2d");
  if (!canvas || !context) return;
  const marks = marksOverride ?? state.marks;
  context.clearRect(0, 0, canvas.width, canvas.height);
  context.fillStyle = "#161616";
  context.fillRect(0, 0, canvas.width, canvas.height);
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
    const sourceWidth = quarterTurn ? image.naturalHeight : image.naturalWidth;
    const sourceHeight = quarterTurn ? image.naturalWidth : image.naturalHeight;
    const scale = Math.min(
      canvas.width / sourceWidth,
      canvas.height / sourceHeight,
    );
    context.save();
    context.translate(canvas.width / 2, canvas.height / 2);
    context.rotate((state.rotation * Math.PI) / 180);
    context.drawImage(
      image,
      (-image.naturalWidth * scale) / 2,
      (-image.naturalHeight * scale) / 2,
      image.naturalWidth * scale,
      image.naturalHeight * scale,
    );
    context.restore();
  }
  context.restore();
  context.lineWidth = 3 * markScale;
  context.strokeStyle = "#f38b35";
  marks.forEach((mark, index) => {
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
  if (showEditorChrome && state.crop) {
    context.setLineDash([8, 5]);
    context.lineWidth = 1 * markScale;
    context.strokeStyle = "#f38b35";
    context.strokeRect(
      state.crop.start.x,
      state.crop.start.y,
      state.crop.end.x - state.crop.start.x,
      state.crop.end.y - state.crop.start.y,
    );
  }
  context.setLineDash([]);
}

function normalizedRect(mark: Pick<Mark, "start" | "end">) {
  return {
    x: Math.min(mark.start.x, mark.end.x),
    y: Math.min(mark.start.y, mark.end.y),
    width: Math.abs(mark.end.x - mark.start.x),
    height: Math.abs(mark.end.y - mark.start.y),
  };
}

function applyMosaic(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
) {
  const area = normalizedRect(mark);
  const scratch = document.createElement("canvas");
  scratch.width = Math.max(1, Math.ceil(area.width / (14 * scale)));
  scratch.height = Math.max(1, Math.ceil(area.height / (14 * scale)));
  const scratchContext = scratch.getContext("2d");
  if (!scratchContext) return;
  scratchContext.imageSmoothingEnabled = false;
  scratchContext.drawImage(
    canvas,
    area.x,
    area.y,
    area.width,
    area.height,
    0,
    0,
    scratch.width,
    scratch.height,
  );
  context.save();
  context.imageSmoothingEnabled = false;
  context.drawImage(
    scratch,
    0,
    0,
    scratch.width,
    scratch.height,
    area.x,
    area.y,
    area.width,
    area.height,
  );
  context.restore();
}

function applyBlur(
  context: CanvasRenderingContext2D,
  canvas: HTMLCanvasElement,
  mark: Mark,
  scale = 1,
) {
  const area = normalizedRect(mark);
  const scratch = document.createElement("canvas");
  scratch.width = Math.max(1, Math.ceil(area.width));
  scratch.height = Math.max(1, Math.ceil(area.height));
  const scratchContext = scratch.getContext("2d");
  if (!scratchContext) return;
  scratchContext.filter = `blur(${10 * scale}px)`;
  scratchContext.drawImage(
    canvas,
    area.x,
    area.y,
    area.width,
    area.height,
    0,
    0,
    area.width,
    area.height,
  );
  context.drawImage(scratch, area.x, area.y);
}

function clampRect(rect: ReturnType<typeof normalizedRect>, size: CanvasSize) {
  const x = Math.max(0, Math.min(size.width, rect.x));
  const y = Math.max(0, Math.min(size.height, rect.y));
  const right = Math.max(x, Math.min(size.width, rect.x + rect.width));
  const bottom = Math.max(y, Math.min(size.height, rect.y + rect.height));
  return { x, y, width: right - x, height: bottom - y };
}

function exportCanvas(
  image: HTMLImageElement | undefined,
  displayCanvas: HTMLCanvasElement | null,
  state: EditorState,
): Promise<Blob> {
  if (!image || !displayCanvas || !image.naturalWidth || !image.naturalHeight) {
    return Promise.reject(new Error("source image is unavailable"));
  }
  const displaySize = {
    width: displayCanvas.width,
    height: displayCanvas.height,
  };
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
    return Promise.reject(new Error("crop area is empty"));
  }
  const output = document.createElement("canvas");
  output.width = Math.max(1, Math.round(mappedCrop.width));
  output.height = Math.max(1, Math.round(mappedCrop.height));
  const context = output.getContext("2d");
  if (!context) {
    return Promise.reject(new Error("canvas export is unavailable"));
  }
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
  return new Promise((resolve, reject) => {
    output.toBlob((blob) => {
      if (blob) resolve(blob);
      else reject(new Error("PNG export failed"));
    }, "image/png");
  });
}

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
