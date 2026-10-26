import { createPortal } from "react-dom";
import { useEffect, useRef, useState } from "react";
import "./ResultImageTextOverlay.css";
import { LineEditor } from "./LineEditor";
import { startEditOnEnterOrDoubleActivate } from "./lineEditorTrigger";
import { layoutTextBox } from "./imageTextLayerGeometry";
import {
  confidenceLabel,
  isLowConfidence,
  type ResultTextLine,
} from "./resultTextModes";

/** 带原始行下标的结果行：命令必须用宿主权威索引，不用显示序号。 */
export interface IndexedResultLine {
  readonly line: ResultTextLine;
  readonly index: number;
}

export type OverlayLineEditHandler = (
  lineIndex: number,
  expectedOldText: string,
  newText: string,
) => Promise<boolean>;

interface Stage {
  image: HTMLImageElement;
  width: number;
  height: number;
}

/**
 * 结果绑定的图像校对视图：把当前识别结果 lines 的 [0,1000] bbox 投影到
 * 识别时真实冻结的参考图（宿主 state.resultImage，随结果原子替换/失效；
 * 调用方不得用会话基准图冒充）。低置信行提示；双击或聚焦后 Enter 开始
 * 行内联编辑，经宿主 recognition.updateLine 闭环，与结果区行编辑同一
 * 命令同一权威数据；Escape 取消、失败保留草稿可重试。绑定键（参考图
 * URL+结构化资源 URL）由调用方作 key，任一变化即整体重挂。
 * 布局尺寸经 ResizeObserver 跟踪（含 cleanup），面板缩放/折叠区展开后
 * 行框即时重投影；无 ResizeObserver 环境回退 load 时 1:1 测量。
 */
export function ResultImageTextOverlay({
  imageUrl,
  fit = false,
  lines,
  onUpdateLine,
  highlightIndex,
  onHoverLine,
}: {
  readonly imageUrl: string;
  readonly fit?: boolean;
  readonly lines: readonly IndexedResultLine[];
  readonly onUpdateLine?: OverlayLineEditHandler;
  /** 双向高亮：右侧结果行悬停时对应行框加亮（同一宿主行下标）。 */
  readonly highlightIndex?: number;
  readonly onHoverLine?: (index: number | undefined) => void;
}) {
  const [aspectRatio, setAspectRatio] = useState(1);
  const [figure, setFigure] = useState<HTMLElement | null>(null);
  const imageRef = useRef<HTMLImageElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const [stage, setStage] = useState<Stage | undefined>();
  const [editing, setEditing] = useState<number | undefined>(undefined);

  const applyStage = (
    image: HTMLImageElement,
    width: number,
    height: number,
  ) => {
    if (
      !image.naturalWidth ||
      !image.naturalHeight ||
      width <= 0 ||
      height <= 0
    )
      return;
    setStage((current) =>
      current &&
      current.image === image &&
      current.width === width &&
      current.height === height
        ? current
        : { image, width, height },
    );
  };

  // ResizeObserver 跟踪布局尺寸（含 cleanup）：面板缩放/折叠区展开后
  // 行框即时重投影；无该 API 的环境回退 load 时 1:1 测量。
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage || typeof ResizeObserver === "undefined") return undefined;
    const remeasure = () => {
      if (!imageRef.current) return;
      applyStage(
        imageRef.current,
        stage.clientWidth || imageRef.current.naturalWidth,
        stage.clientHeight || imageRef.current.naturalHeight,
      );
    };
    const observer = new ResizeObserver(remeasure);
    observer.observe(stage);
    remeasure();
    return () => observer.disconnect();
  }, []);

  const submit = async (
    entry: IndexedResultLine,
    newText: string,
  ): Promise<boolean> =>
    onUpdateLine ? onUpdateLine(entry.index, entry.line.text, newText) : false;

  const decoded = stage;
  return (
    <figure
      className={`result-image-overlay${fit ? " fit-preview" : ""}`}
      ref={setFigure}
      aria-label="识别结果图像校对"
    >
      <div
        className="result-image-stage"
        ref={stageRef}
        style={
          fit
            ? { width: `min(100cqw, calc((100cqh - 48px) * ${aspectRatio}))` }
            : undefined
        }
      >
        <img
          ref={imageRef}
          src={imageUrl}
          alt={lines.length ? "识别结果参考图" : "图片预览"}
          onLoad={(event) => {
            setAspectRatio(
              event.currentTarget.naturalWidth /
                event.currentTarget.naturalHeight,
            );
            applyStage(
              event.currentTarget,
              event.currentTarget.parentElement?.clientWidth ||
                event.currentTarget.naturalWidth,
              event.currentTarget.parentElement?.clientHeight ||
                event.currentTarget.naturalHeight,
            );
          }}
          style={{ width: "100%" }}
        />
        {decoded &&
          lines.map(({ line, index }) => {
            if (!line.bbox) return null;
            const box = layoutTextBox(line.bbox, decoded.image, {
              width: decoded.width,
              height: decoded.height,
            });
            if (!box) return null;
            const low = isLowConfidence(line.score);
            const editable = onUpdateLine !== undefined;
            const trigger = editable
              ? startEditOnEnterOrDoubleActivate(() => {
                  if (editing !== undefined) return;
                  setEditing(index);
                })
              : undefined;
            const editingThis = editing === index;
            // 编辑面板锚定行框并钳位到舞台内，避免越过右/下边缘。
            const panelLeft = Math.max(
              0,
              Math.min(box.left, Math.max(0, decoded.width - 320)),
            );
            const panelTop = Math.max(
              0,
              Math.min(box.top, Math.max(0, decoded.height - 140)),
            );
            const editor =
              editingThis && editable ? (
                <div
                  className="overlay-line-editor"
                  style={
                    fit
                      ? undefined
                      : { left: `${panelLeft}px`, top: `${panelTop}px` }
                  }
                >
                  <LineEditor
                    label={`编辑第 ${index + 1} 行`}
                    initialText={line.text}
                    onSubmit={async (newText) => {
                      const applied = await submit({ line, index }, newText);
                      if (applied) setEditing(undefined);
                      return applied;
                    }}
                    onCancel={() => setEditing(undefined)}
                  />
                </div>
              ) : null;
            return (
              <div key={index}>
                <div
                  className={`overlay-line${
                    low ? " low-confidence" : ""
                  }${highlightIndex === index ? " hovered" : ""}`}
                  title={
                    line.score === null
                      ? undefined
                      : confidenceLabel(line.score)
                  }
                  style={{
                    left: `${box.left}px`,
                    top: `${box.top}px`,
                    width: `${box.width}px`,
                    height: `${box.height}px`,
                  }}
                  aria-label={line.text}
                  onMouseEnter={() => onHoverLine?.(index)}
                  onMouseLeave={() => onHoverLine?.(undefined)}
                  {...trigger}
                />
                {editor &&
                  (fit && figure ? createPortal(editor, figure) : editor)}
              </div>
            );
          })}
      </div>
      {onUpdateLine && (
        <figcaption className="form-note">
          双击或聚焦后按 Enter
          在图上校对并修改文本；低置信（&lt;80%）行以红色标记。
        </figcaption>
      )}
    </figure>
  );
}
