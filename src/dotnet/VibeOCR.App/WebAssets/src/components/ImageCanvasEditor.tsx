import {
  Button,
  Input,
  Toolbar,
  ToolbarButton,
} from "@fluentui/react-components";
import { useEffect, useRef, useState } from "react";
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

type Tool = "select" | AnnotationTool | "crop";

const EMPTY: EditorState = { rotation: 0, marks: [] };

interface ImageCanvasEditorProps {
  readonly actions: AppActions;
  readonly canExport: boolean;
  readonly source: string;
}

export function ImageCanvasEditor({
  actions,
  canExport,
  source,
}: ImageCanvasEditorProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const imageRef = useRef<HTMLImageElement | undefined>(undefined);
  const dragStart = useRef<Point | undefined>(undefined);
  const selectedMarkRef = useRef<number | undefined>(undefined);
  const exportInProgressRef = useRef(false);
  const [tool, setTool] = useState<Tool>("select");
  const [annotationText, setAnnotationText] = useState("文本");
  const [selectedMark, setSelectedMark] = useState<number | undefined>();
  const [history, setHistory] = useState<readonly EditorState[]>([EMPTY]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [imageRevision, setImageRevision] = useState(0);
  const [isExporting, setIsExporting] = useState(false);
  const [operationMessage, setOperationMessage] = useState(
    "标注只影响复制或保存的图片副本，不会重新识别。",
  );
  const state = history[historyIndex] ?? EMPTY;

  useEffect(() => {
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
    draw(canvasRef.current, imageRef.current, state, selectedMark);
  }, [imageRevision, selectedMark, state]);

  function commit(next: EditorState) {
    setHistory((current) => [...current.slice(0, historyIndex + 1), next]);
    setHistoryIndex((current) => current + 1);
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
    setHistoryIndex((current) => Math.max(0, current - 1));
  }

  function redo() {
    setHistoryIndex((current) => Math.min(history.length - 1, current + 1));
  }

  async function copyAnnotatedImage() {
    if (!canExport || exportInProgressRef.current) return;
    exportInProgressRef.current = true;
    setIsExporting(true);
    try {
      setOperationMessage("正在生成并复制标注图片……");
      const blob = await exportCanvas(
        imageRef.current,
        canvasRef.current,
        state,
      );
      const resourceUri = await uploadAnnotatedImage(blob);
      const copied = await actions.run({
        type: "recognition.copyAnnotatedImage",
        resourceUri,
      });
      setOperationMessage(
        copied
          ? "已复制标注图片副本。识别结果保持不变。"
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
      const blob = await exportCanvas(
        imageRef.current,
        canvasRef.current,
        state,
      );
      const resourceUri = await uploadAnnotatedImage(blob);
      const saved = await actions.run({
        type: "recognition.saveAnnotatedImage",
        resourceUri,
      });
      setOperationMessage(
        saved
          ? "已保存标注图片副本。识别结果保持不变。"
          : "保存未完成，请查看页面提示后重试。",
      );
    } catch {
      setOperationMessage("无法生成或传递标注图片，请重试。");
    } finally {
      exportInProgressRef.current = false;
      setIsExporting(false);
    }
  }

  return (
    <div className="canvas-editor">
      <Toolbar
        aria-label="图片编辑工具"
        size="small"
        className="editor-toolbar"
      >
        {(
          [
            "select",
            "rectangle",
            "ellipse",
            "arrow",
            "text",
            "mosaic",
            "blur",
            "crop",
          ] as const
        ).map((value) => (
          <ToolbarButton
            appearance={tool === value ? "primary" : "subtle"}
            aria-pressed={tool === value}
            key={value}
            onClick={() => setTool(value)}
          >
            {
              {
                select: "选择",
                rectangle: "矩形",
                ellipse: "椭圆",
                arrow: "箭头",
                text: "文字",
                mosaic: "马赛克",
                blur: "模糊",
                crop: "裁剪",
              }[value]
            }
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
        <span aria-hidden="true" className="editor-toolbar-break" />
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
          onClick={() => setHistoryIndex((current) => Math.max(0, current - 1))}
        >
          撤销
        </ToolbarButton>
        <ToolbarButton
          aria-label="重做"
          disabled={historyIndex >= history.length - 1}
          onClick={() =>
            setHistoryIndex((current) =>
              Math.min(history.length - 1, current + 1),
            )
          }
        >
          重做
        </ToolbarButton>
      </Toolbar>
      <p className="editor-guidance">
        拖拽绘制或裁剪；选择标注后可拖动。马赛克与模糊会写入复制、保存的图片副本，当前识别结果不会改变。
      </p>
      <div className="canvas-stage">
        <canvas
          aria-label="图片检查画布"
          className={`inspection-canvas tool-${tool}`}
          height={600}
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
          onPointerDown={(event) => {
            const start = point(event);
            dragStart.current = start;
            if (tool === "select") {
              select(findMark(state.marks, start));
            }
            event.currentTarget.setPointerCapture(event.pointerId);
          }}
          onPointerUp={(event) => {
            if (!dragStart.current) return;
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
                  markIndex === index
                    ? {
                        ...mark,
                        start: {
                          x: mark.start.x + delta.x,
                          y: mark.start.y + delta.y,
                        },
                        end: {
                          x: mark.end.x + delta.x,
                          y: mark.end.y + delta.y,
                        },
                      }
                    : mark,
                ),
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
                      ...(tool === "text"
                        ? { text: annotationText.trim() || "文本" }
                        : {}),
                    },
                  ],
                });
            }
          }}
          ref={canvasRef}
          tabIndex={0}
          width={900}
        />
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

function draw(
  canvas: HTMLCanvasElement | null,
  image: HTMLImageElement | undefined,
  state: EditorState,
  selectedMark: number | undefined,
  showEditorChrome = true,
  markScale = 1,
) {
  const context = canvas?.getContext("2d");
  if (!canvas || !context) return;
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
  state.marks.forEach((mark, index) => {
    const width = mark.end.x - mark.start.x;
    const height = mark.end.y - mark.start.y;
    if (mark.tool === "rectangle") {
      context.setLineDash([]);
      context.strokeRect(mark.start.x, mark.start.y, width, height);
    } else if (mark.tool === "ellipse") {
      context.setLineDash([]);
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
      context.font = `600 ${24 * markScale}px system-ui, sans-serif`;
      context.strokeText(mark.text || "文本", mark.start.x, mark.end.y);
    } else if (mark.tool === "mosaic") {
      applyMosaic(context, canvas, mark, markScale);
    } else {
      applyBlur(context, canvas, mark, markScale);
    }
    if (showEditorChrome && selectedMark === index) {
      context.setLineDash([7, 5]);
      context.strokeRect(
        Math.min(mark.start.x, mark.end.x) - 5,
        Math.min(mark.start.y, mark.end.y) - 5,
        Math.abs(width) + 10,
        Math.abs(height) + 10,
      );
    }
  });
  if (showEditorChrome && state.crop) {
    context.setLineDash([8, 5]);
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
    })),
  };
  const rendered = document.createElement("canvas");
  rendered.width = naturalSize.width;
  rendered.height = naturalSize.height;
  const displayScale = imageTransform(image, state.rotation, displaySize).scale;
  draw(rendered, image, naturalState, undefined, false, 1 / displayScale);

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
