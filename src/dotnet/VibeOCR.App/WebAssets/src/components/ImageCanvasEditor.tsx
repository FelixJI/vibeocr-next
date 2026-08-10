import {
  Button,
  Input,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
} from "@fluentui/react-components";
import { useEffect, useRef, useState } from "react";

type AnnotationTool =
  "rectangle" | "ellipse" | "arrow" | "text" | "mosaic" | "blur";
type Tool = "select" | AnnotationTool | "crop";
interface Point {
  readonly x: number;
  readonly y: number;
}
interface Mark {
  readonly tool: AnnotationTool;
  readonly start: Point;
  readonly end: Point;
  readonly text?: string;
}
interface EditorState {
  readonly rotation: number;
  readonly marks: readonly Mark[];
  readonly crop?: { readonly start: Point; readonly end: Point };
}

const EMPTY: EditorState = { rotation: 0, marks: [] };

export function ImageCanvasEditor({ source }: { readonly source: string }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const imageRef = useRef<HTMLImageElement | undefined>(undefined);
  const dragStart = useRef<Point | undefined>(undefined);
  const selectedMarkRef = useRef<number | undefined>(undefined);
  const [tool, setTool] = useState<Tool>("select");
  const [annotationText, setAnnotationText] = useState("文本");
  const [selectedMark, setSelectedMark] = useState<number | undefined>();
  const [history, setHistory] = useState<readonly EditorState[]>([EMPTY]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [imageRevision, setImageRevision] = useState(0);
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
        <ToolbarDivider />
        <ToolbarButton
          aria-label="旋转 90°"
          onClick={() =>
            commit({ ...state, rotation: (state.rotation + 90) % 360 })
          }
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
      <div className="canvas-stage">
        <canvas
          aria-label="图片检查画布"
          className={`inspection-canvas tool-${tool}`}
          height={600}
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
          width={900}
        />
      </div>
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
    </div>
  );
}

function draw(
  canvas: HTMLCanvasElement | null,
  image: HTMLImageElement | undefined,
  state: EditorState,
  selectedMark: number | undefined,
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
  context.lineWidth = 3;
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
        mark.end.x - 16 * Math.cos(angle - 0.45),
        mark.end.y - 16 * Math.sin(angle - 0.45),
      );
      context.moveTo(mark.end.x, mark.end.y);
      context.lineTo(
        mark.end.x - 16 * Math.cos(angle + 0.45),
        mark.end.y - 16 * Math.sin(angle + 0.45),
      );
      context.stroke();
    } else if (mark.tool === "text") {
      context.font = "600 24px system-ui, sans-serif";
      context.strokeText(mark.text || "文本", mark.start.x, mark.end.y);
    } else {
      context.save();
      context.globalAlpha = mark.tool === "mosaic" ? 0.72 : 0.38;
      context.fillStyle = mark.tool === "mosaic" ? "#f38b35" : "#8a8a8a";
      context.fillRect(mark.start.x, mark.start.y, width, height);
      context.restore();
    }
    if (selectedMark === index) {
      context.setLineDash([7, 5]);
      context.strokeRect(
        Math.min(mark.start.x, mark.end.x) - 5,
        Math.min(mark.start.y, mark.end.y) - 5,
        Math.abs(width) + 10,
        Math.abs(height) + 10,
      );
    }
  });
  if (state.crop) {
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
