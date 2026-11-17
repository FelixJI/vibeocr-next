import {
  Button,
  Checkbox,
  Textarea,
  Tooltip,
} from "@fluentui/react-components";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import type { AppActions } from "../app/types";

import { progressivePreviewDecoder } from "./progressivePreviewDecoder";

import { isImeCompositionEvent } from "../app/keyboard";

interface Resource {
  readonly url: string;
}
interface Block {
  readonly index: number;
  readonly text: string;
  readonly bbox: readonly [number, number, number, number];
  readonly source: "ocr" | "native";
  readonly confidence?: number;
  readonly edited: boolean;
  readonly textTruncated: boolean;
}

function blocksFrom(value: unknown, page: number): readonly Block[] {
  if (
    !value ||
    typeof value !== "object" ||
    !("page" in value) ||
    value.page !== page
  )
    throw new Error("页面资源与当前页不一致");
  const payload = value as Record<string, unknown>;
  const result: Block[] = [];
  for (const source of ["ocr", "native"] as const) {
    const entries = payload[source === "ocr" ? "ocr_blocks" : "native_lines"];
    if (!Array.isArray(entries)) continue;
    entries
      .slice(0, 1000 - result.length)
      .forEach((entry: unknown, index: number) => {
        if (!entry || typeof entry !== "object") return;
        const item = entry as Record<string, unknown>;
        const bbox: unknown = item.bbox;
        const text = source === "ocr" ? item.text : item.text_preview;
        if (
          typeof text !== "string" ||
          !Array.isArray(bbox) ||
          bbox.length !== 4 ||
          !bbox.every(
            (n: unknown) => typeof n === "number" && Number.isFinite(n),
          ) ||
          bbox[2] <= bbox[0] ||
          bbox[3] <= bbox[1]
        )
          return;
        if (
          source === "ocr" &&
          (!Number.isInteger(item.index) || Number(item.index) < 0)
        )
          return;
        result.push({
          index: source === "ocr" ? Number(item.index) : index,
          text: text.slice(0, 2000),
          textTruncated: item.text_truncated === true || text.length > 2000,
          bbox: bbox as [number, number, number, number],
          source,
          edited: item.is_manually_edited === true,
          confidence:
            source === "ocr" &&
            item.score_unknown === false &&
            typeof item.score === "number" &&
            Number.isFinite(item.score) &&
            item.score >= 0 &&
            item.score <= 1
              ? item.score
              : undefined,
        });
      });
  }
  return result;
}

function blockLabel(block: Block): string {
  return `${block.source === "ocr" ? "OCR" : "已有 PDF 文字层 / 来源未知"} · ${
    block.confidence === undefined
      ? "置信度未知"
      : `置信度 ${(block.confidence * 100).toFixed(1)}%`
  }${block.edited ? " · 人工修改" : ""} · ${block.text}`;
}

// 预览位置（纯预览状态）：高频滚动/击键只保留单个在途命令与最新待发送值。
interface PreviewPositionUpdate {
  readonly zoom: number | null;
  readonly left: number;
  readonly top: number;
  readonly showBoxes: boolean;
  readonly block: number | null;
  readonly draft: string;
  readonly originalText: string | null;
  readonly revision: number;
  readonly page: number;
}

export function PdfInspection({
  position,
  page,
  count,
  revision,
  sessionId,
  preview,
  inspect,
  status,
  busy,
  canEdit,
  actions,
  registerPositionFlush,
  registerNativePositionFlush,
  documentId,
  canDemandPages,
  logicalSize,
  demandIdentity,
  reportPreviewDemand,
}: {
  readonly position?: Readonly<Record<string, unknown>>;
  readonly page: number;
  readonly count: number;
  readonly revision: number;
  readonly sessionId: string;
  readonly preview?: Resource;
  readonly inspect?: Resource;
  readonly status: string;
  readonly busy: boolean;
  readonly canEdit: boolean;
  readonly actions: AppActions;
  readonly documentId?: string;
  readonly registerNativePositionFlush?: AppActions["registerPdfPreviewFlush"];
  readonly registerPositionFlush?: (
    flush: (() => Promise<void>) | null,
  ) => void;
  /** 严格 true 时启用按需预览：逻辑尺寸来自页面 points，渐进资源经共享
   * decoder decode 完成后再替换旧图；缺字段走 legacy 自然像素语义。 */
  readonly canDemandPages?: boolean;
  /** 当前页 PDF points（width/height），仅 canDemandPages 时作为缩放权威。 */
  readonly logicalSize?: {
    readonly width: number;
    readonly height: number;
  };
  /** 当前检查身份（doc/session/pageKey/gen）：共享 decoder 的所有权 token。 */
  readonly demandIdentity?: string;
  /** 当前页按 CSS 显示几何×DPR 需要的最长像素边（正整数），携带 identity
   * 供父级配对，避免迟到旧页尺寸绑定到新选中页。 */
  readonly reportPreviewDemand?: (identity: string, maxEdge: number) => void;
}) {
  const viewport = useRef<HTMLDivElement>(null);
  const drag = useRef<{
    x: number;
    y: number;
    left: number;
    top: number;
  } | null>(null);
  const [blocks, setBlocks] = useState<readonly Block[]>([]);
  const [error, setError] = useState("");
  const [truncated, setTruncated] = useState(false);
  const [imageFailed, setImageFailed] = useState(false);
  const [imageDimensions, setImageDimensions] = useState({
    width: 612,
    height: 792,
  });
  const [space, setSpace] = useState({ width: 600, height: 480 });
  const [zoom, setZoom] = useState<number | null>(
    typeof position?.zoom === "number" ? position.zoom : null,
  );
  const [showBoxes, setShowBoxes] = useState(position?.showBoxes !== false);
  const [selection, setSelection] = useState<{
    source: Block["source"];
    index: number;
  } | null>(
    position?.revision === revision &&
      position?.page === page &&
      typeof position.block === "number"
      ? { source: "ocr", index: position.block }
      : null,
  );
  const selected =
    blocks.find(
      (block) =>
        block.source === selection?.source && block.index === selection.index,
    ) ?? null;
  const [draft, setDraft] = useState(
    position?.revision === revision &&
      position?.page === page &&
      typeof position.draft === "string"
      ? position.draft
      : "",
  );
  const [submitting, setSubmitting] = useState(false);
  const alive = useRef(true);
  const actionsRef = useRef(actions);
  useEffect(() => {
    actionsRef.current = actions;
  }, [actions]);
  const initialPosition = useRef(position);
  const [savedOriginalText] = useState(
    position?.revision === revision &&
      position?.page === page &&
      typeof position.originalText === "string"
      ? position.originalText
      : null,
  );
  const restoredPosition = useRef(
    !position || !(Number(position.left) || Number(position.top)),
  );
  const restoredImage = useRef(false);
  const originalText =
    selection?.source === "ocr"
      ? selected
        ? selected.textTruncated
          ? null
          : selected.text
        : savedOriginalText
      : null;
  const pendingPosition = useRef<PreviewPositionUpdate | null>(null);
  const drainingPosition = useRef<Promise<void> | null>(null);
  // 单个在途 + 最新 pending：同一时刻至多一个排空任务，重复调度直接复用同一
  // promise，不追加链；只有 run 确认成功才视为提交完成，未确认时保留待发送
  // 值并使排空失败，切换/关闭命令因此不发出。
  const drainPosition = useCallback((): Promise<void> => {
    const existing = drainingPosition.current;
    if (existing) return existing;
    // 无待发送值时直接完成：不能登记空任务，否则其同步结束时序会把已完
    // 成的 promise 留在唯一任务槽里，后续最新值永远不会被发送。
    if (!pendingPosition.current) return Promise.resolve();
    const task = (async () => {
      try {
        while (pendingPosition.current) {
          const current = pendingPosition.current;
          pendingPosition.current = null;
          let confirmed = false;
          try {
            confirmed = await actionsRef.current.run({
              type: "pdf.setPreviewPosition",
              position: current,
            });
          } catch {
            confirmed = false;
          }
          if (!confirmed) {
            // 未确认提交：保留待发送值（较新的值优先）供重试，排空以失败结束。
            pendingPosition.current ??= current;
            throw new Error("preview position update was not confirmed");
          }
        }
      } finally {
        drainingPosition.current = null;
      }
    })();
    drainingPosition.current = task;
    return task;
  }, []);
  const schedulePosition = useCallback(
    (update: PreviewPositionUpdate) => {
      pendingPosition.current = update;
      void drainPosition().catch(() => undefined);
    },
    [drainPosition],
  );
  // 切换/卸载前的排空：可等待，返回即表示最后位置/草稿已被宿主确认提交；
  // 关闭可能被用户取消或远端失败，同样只补发不丢弃。
  const flushPosition = drainPosition;
  useEffect(() => {
    registerPositionFlush?.(flushPosition);
    const unregisterNative = documentId
      ? registerNativePositionFlush?.(documentId, flushPosition)
      : undefined;
    return () => {
      unregisterNative?.();
      registerPositionFlush?.(null);
      // 非切换路径的卸载（翻页/修订/路由变化）也排空最后状态；后续任何
      // 激活/关闭命令都会先 await 同一排空任务。
      void flushPosition().catch(() => undefined);
    };
  }, [
    registerPositionFlush,
    registerNativePositionFlush,
    documentId,
    flushPosition,
  ]);
  const rememberPosition = () => {
    const node = viewport.current;
    schedulePosition({
      zoom,
      left: restoredPosition.current
        ? (node?.scrollLeft ?? 0)
        : Number(initialPosition.current?.left) || 0,
      top: restoredPosition.current
        ? (node?.scrollTop ?? 0)
        : Number(initialPosition.current?.top) || 0,
      showBoxes,
      block: selection?.index ?? null,
      draft,
      originalText,
      revision,
      page,
    });
  };
  useEffect(() => {
    schedulePosition({
      zoom,
      left: restoredPosition.current
        ? (viewport.current?.scrollLeft ?? 0)
        : Number(initialPosition.current?.left) || 0,
      top: restoredPosition.current
        ? (viewport.current?.scrollTop ?? 0)
        : Number(initialPosition.current?.top) || 0,
      showBoxes,
      block: selection?.index ?? null,
      draft,
      originalText,
      revision,
      page,
    });
  }, [
    schedulePosition,
    zoom,
    showBoxes,
    selection?.index,
    draft,
    originalText,
    revision,
    page,
  ]);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  // 按需预览（canDemandPages 严格 true）：缩放/坐标权威固定来自页面 points
  // （96/72 转 CSS 像素）；legacy 路径保持 onLoad naturalWidth 语义。
  const demandMode = canDemandPages === true;
  const previewUrl = preview?.url ?? "";
  const logicalDimensions =
    demandMode && logicalSize && logicalSize.width > 0 && logicalSize.height > 0
      ? {
          width: (logicalSize.width * 96) / 72,
          height: (logicalSize.height * 96) / 72,
        }
      : undefined;
  // 拿不到有效逻辑 geometry 时仅显示基础图，但不用自然像素当缩放权威：
  // demand 模式下 imageDimensions 永不更新，保持默认几何。
  const dimensions = demandMode
    ? (logicalDimensions ?? imageDimensions)
    : imageDimensions;
  const [devicePixelRatio, setDevicePixelRatio] = useState(
    () => window.devicePixelRatio || 1,
  );
  // 每次变化后重绑当前 resolution 查询：1→1.5 触发后再 1.5→2 也能触发。
  useEffect(() => {
    if (!demandMode) return;
    const media = window.matchMedia?.(`(resolution: ${devicePixelRatio}dppx)`);
    if (!media || typeof media.addEventListener !== "function") return;
    const onChange = () => setDevicePixelRatio(window.devicePixelRatio || 1);
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [demandMode, devicePixelRatio]);
  // 渐进预览：共享 decoder（跨页/文档/路由卸载唯一 issued decode）；首图与
  // 升级同一路径，空 committed 不回退最新 URL，展示只依赖已提交图。
  const progressive = useSyncExternalStore(
    progressivePreviewDecoder.subscribe,
    progressivePreviewDecoder.getSnapshot,
  );
  const [previewOwner] = useState(() => Symbol());
  useLayoutEffect(() => {
    if (!demandMode) return;
    return () => progressivePreviewDecoder.release(previewOwner);
  }, [demandMode, previewOwner]);
  useLayoutEffect(() => {
    if (demandMode) progressivePreviewDecoder.request(previewOwner, previewUrl);
  }, [demandMode, previewOwner, previewUrl]);
  const ownsPreview = demandMode && progressive.owner === previewOwner;
  const decodeFailed = ownsPreview && progressive.failed;
  const committedImage = ownsPreview ? progressive.committed : null;
  const previewAlt = `当前第 ${page + 1} 页高清预览`;
  const restoreImagePosition = useCallback(() => {
    if (restoredImage.current) return;
    restoredImage.current = true;
    requestAnimationFrame(() => {
      const node = viewport.current;
      const saved = initialPosition.current;
      if (alive.current && node && saved && !restoredPosition.current) {
        node.scrollLeft = typeof saved.left === "number" ? saved.left : 0;
        node.scrollTop = typeof saved.top === "number" ? saved.top : 0;
        restoredPosition.current = true;
      }
    });
  }, []);
  const mountDecodedImage = useCallback(
    (sheet: HTMLDivElement | null) => {
      if (!sheet || !committedImage) return;
      const detach = progressivePreviewDecoder.attach(
        previewOwner,
        sheet,
        previewAlt,
      );
      if (detach) restoreImagePosition();
      return detach;
    },
    [committedImage, previewOwner, previewAlt, restoreImagePosition],
  );
  useEffect(() => {
    const element = viewport.current;
    if (!element || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() =>
      setSpace({ width: element.clientWidth, height: element.clientHeight }),
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    if (!inspect) return;
    const cancellation = new AbortController();
    void fetch(inspect.url, {
      cache: "no-store",
      credentials: "omit",
      signal: cancellation.signal,
    })
      .then(async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const value: unknown = await response.json();
        if (!cancellation.signal.aborted)
          setTruncated(
            !!value &&
              typeof value === "object" &&
              "truncated" in value &&
              value.truncated === true,
          );
        return blocksFrom(value, page);
      })
      .then((value) => {
        if (!cancellation.signal.aborted) {
          setBlocks(value);
        }
      })
      .catch(() => {
        if (!cancellation.signal.aborted)
          setError("文字框读取失败，请重试当前页。");
      });
    return () => cancellation.abort();
  }, [inspect, page, revision]);
  const fit = Math.min(
    (space.width - 24) / dimensions.width,
    (space.height - 24) / dimensions.height,
  );
  const scale = zoom ?? Math.max(0.01, fit);
  // 缩放/fit/viewport resize/DPR 变化重新报告当前页需要的最长像素边；携带
  // 检查身份供父级配对；拿不到有效逻辑 geometry 时不报告虚构值。
  const logicalWidth = logicalDimensions?.width ?? 0;
  const logicalHeight = logicalDimensions?.height ?? 0;
  useEffect(() => {
    if (
      !demandMode ||
      !reportPreviewDemand ||
      logicalWidth <= 0 ||
      logicalHeight <= 0
    )
      return;
    const maxEdge = Math.ceil(
      Math.max(logicalWidth, logicalHeight) * scale * devicePixelRatio,
    );
    if (maxEdge > 0) reportPreviewDemand(demandIdentity ?? "", maxEdge);
  }, [
    demandMode,
    reportPreviewDemand,
    demandIdentity,
    logicalWidth,
    logicalHeight,
    scale,
    devicePixelRatio,
  ]);
  const changeZoom = (factor: number) =>
    setZoom(Math.max(0.05, Math.min(8, scale * factor)));
  const flip = (target: number) => {
    if (target >= 0 && target < count)
      void actions.run({ type: "pdf.setCurrentPage", page: target });
  };
  const cancel = () => {
    setSelection(null);
    setDraft("");
    setError("");
    if (submitting) void actions.run({ type: "pdf.cancel" });
  };
  const submit = async () => {
    if (
      !selected ||
      selected.source !== "ocr" ||
      !draft.trim() ||
      !sessionId ||
      submitting ||
      busy
    )
      return;
    setSubmitting(true);
    setError("");
    try {
      const ok = await actions.run({
        type: "pdf.updateBlockText",
        sessionId,
        revision,
        page,
        blockIndex: selected.index,
        expectedOldText: selected.text,
        newText: draft,
      });
      if (alive.current) {
        if (ok) {
          setSelection(null);
          setDraft("");
        } else
          setError("校正未确认，请查看操作反馈；原稿保留。失联时请重开复检。");
      }
    } catch {
      if (alive.current) setError("校正未确认，请重开文档复检。");
    } finally {
      if (alive.current) setSubmitting(false);
    }
  };
  return (
    <div className="pdf-inspection" aria-busy={busy || submitting}>
      <div className="pdf-inspection-tools" aria-label="高清页面操作">
        <Button disabled={page <= 0} onClick={() => flip(page - 1)}>
          上一页
        </Button>
        <span>
          第 {page + 1} / {count} 页
        </span>
        <Button disabled={page + 1 >= count} onClick={() => flip(page + 1)}>
          下一页
        </Button>
        <Button onClick={() => changeZoom(1 / 1.25)} aria-label="缩小页面">
          −
        </Button>
        <span>{Math.round(scale * 100)}%</span>
        <Button onClick={() => changeZoom(1.25)} aria-label="放大页面">
          +
        </Button>
        <Button onClick={() => setZoom(null)}>适应页面</Button>
        <Button onClick={() => setZoom(1)}>100%</Button>
        <Checkbox
          label="显示文字框"
          checked={showBoxes}
          onChange={(_, data) => setShowBoxes(data.checked === true)}
        />
        <Tooltip
          content="拖动空白区域平移；方向键平移，PageUp / PageDown 翻页，+ / − 缩放，0 适页，Esc 取消草稿。"
          relationship="description"
        >
          <Button appearance="subtle" aria-label="页面操作帮助">
            ?
          </Button>
        </Tooltip>
      </div>
      <p className="pdf-inspection-hint">切页或修订变化会丢弃未提交草稿。</p>
      {(status === "pdf.inspect.failed" ||
        error ||
        imageFailed ||
        decodeFailed) && (
        <div role="alert">
          <p>{error || "高清页面读取失败，请重试当前页。"}</p>
          <Button
            onClick={() => {
              setError("");
              setImageFailed(false);
              progressivePreviewDecoder.retry(previewOwner);
              void actions.run({ type: "pdf.retryPageInspect" });
            }}
          >
            重试当前页
          </Button>
        </div>
      )}
      <div
        ref={viewport}
        className="pdf-inspection-viewport"
        role="region"
        tabIndex={0}
        aria-label="高清 PDF 页面"
        onKeyDown={(event) => {
          if (event.target !== event.currentTarget) return;
          // IME 组合中的任何键（含 Esc）先交给输入法消费。
          if (isImeCompositionEvent(event.nativeEvent)) return;
          const node = viewport.current;
          if (event.key === "PageUp") flip(page - 1);
          else if (event.key === "PageDown") flip(page + 1);
          else if (event.key === "+" || event.key === "=") changeZoom(1.25);
          else if (event.key === "-") changeZoom(1 / 1.25);
          else if (event.key === "0") setZoom(null);
          else if (event.key === "Escape") cancel();
          else if (
            node &&
            ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(
              event.key,
            )
          ) {
            restoredPosition.current = true;
            node.scrollLeft +=
              event.key === "ArrowRight"
                ? 60
                : event.key === "ArrowLeft"
                  ? -60
                  : 0;
            node.scrollTop +=
              event.key === "ArrowDown"
                ? 60
                : event.key === "ArrowUp"
                  ? -60
                  : 0;
          } else return;
          event.preventDefault();
        }}
        onScroll={() => {
          if (restoredPosition.current) rememberPosition();
        }}
        onWheel={() => {
          restoredPosition.current = true;
        }}
        onPointerDown={(event) => {
          if (
            event.button !== 0 ||
            (event.target instanceof Element && event.target.closest("button"))
          )
            return;
          restoredPosition.current = true;
          const node = event.currentTarget;
          drag.current = {
            x: event.clientX,
            y: event.clientY,
            left: node.scrollLeft,
            top: node.scrollTop,
          };
          node.setPointerCapture(event.pointerId);
        }}
        onPointerMove={(event) => {
          if (!drag.current) return;
          event.currentTarget.scrollLeft =
            drag.current.left - (event.clientX - drag.current.x);
          event.currentTarget.scrollTop =
            drag.current.top - (event.clientY - drag.current.y);
        }}
        onPointerUp={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
      >
        {(demandMode ? committedImage : previewUrl) ? (
          <div
            className="pdf-inspection-sheet"
            ref={mountDecodedImage}
            style={{
              width: dimensions.width * scale,
              height: dimensions.height * scale,
            }}
          >
            {!demandMode && (
              <img
                src={previewUrl}
                alt={previewAlt}
                draggable={false}
                onError={() => setImageFailed(true)}
                onLoad={(event) => {
                  setImageDimensions({
                    width: event.currentTarget.naturalWidth || 612,
                    height: event.currentTarget.naturalHeight || 792,
                  });
                  restoreImagePosition();
                }}
              />
            )}
            {showBoxes &&
              blocks.map((block) => (
                <button
                  key={`${block.source}:${block.index}`}
                  type="button"
                  data-block-index={block.index}
                  className={`pdf-text-box ${block.source}${selected === block ? " selected" : ""}`}
                  style={{
                    left: `${block.bbox[0] / 10}%`,
                    top: `${block.bbox[1] / 10}%`,
                    width: `${(block.bbox[2] - block.bbox[0]) / 10}%`,
                    height: `${(block.bbox[3] - block.bbox[1]) / 10}%`,
                  }}
                  title={blockLabel(block)}
                  aria-label={blockLabel(block)}
                  onClick={() => {
                    if (!submitting) {
                      setSelection({
                        source: block.source,
                        index: block.index,
                      });
                      if (
                        selection?.source !== block.source ||
                        selection.index !== block.index
                      )
                        setDraft(block.text);
                      setError("");
                    }
                  }}
                />
              ))}
          </div>
        ) : !decodeFailed && status !== "pdf.inspect.failed" ? (
          <p aria-live="polite">
            {count > 0 ? "正在读取当前页高清预览…" : "打开文档后检查文字层。"}
          </p>
        ) : null}
      </div>
      {truncated && (
        <p role="status">
          当前页检查已截断（最多 1000 个框、每框 2000 字、全页 128000 字）。PDF
          内容完整保留；截断的文字块仅供检查。
        </p>
      )}
      {selected && (
        <div className="pdf-block-editor" aria-label="文字块检查">
          <p>{blockLabel(selected)}</p>
          {selected.source === "ocr" && canEdit && !selected.textTruncated ? (
            <>
              <Textarea
                aria-label="校正文字"
                value={draft}
                resize="vertical"
                disabled={busy || submitting}
                onChange={(_, data) => setDraft(data.value)}
                onKeyDown={(event) => {
                  // 组合中的 Esc 只取消输入法候选：校正框与草稿保持，
                  // 不与输入法一次按键关闭两层。
                  if (
                    event.key === "Escape" &&
                    !isImeCompositionEvent(event.nativeEvent)
                  )
                    cancel();
                }}
              />
              <div className="pdf-inspection-tools">
                <Button
                  appearance="primary"
                  disabled={
                    busy ||
                    submitting ||
                    !draft.trim() ||
                    draft === selected.text ||
                    !sessionId
                  }
                  onClick={() => void submit()}
                >
                  提交校正
                </Button>
                <Button onClick={cancel}>取消校正</Button>
              </div>
              <p>
                提交写入当前文档，点击保存后才落盘。取消不能撤销已经完成的写入。
              </p>
            </>
          ) : (
            <p>
              {selected.textTruncated
                ? "仅检查：文字预览已截断，不能用预览提交校正。"
                : selected.source === "ocr"
                  ? "仅检查：当前 Runtime 不支持原子文字校正。"
                  : "仅检查：已有 PDF 文字层没有可信 OCR 块身份。"}
            </p>
          )}
        </div>
      )}
    </div>
  );
}
