import {
  Button,
  Checkbox,
  Input,
  ProgressBar,
  Select,
  Toolbar,
  ToolbarDivider,
} from "@fluentui/react-components";

import {
  ArrowDown,
  ArrowUp,
  FolderOpen,
  RotateCcw,
  RotateCw,
  Save,
  ScanText,
  Trash2,
  X,
} from "lucide-react";

import { useCallback, useEffect, useRef, useState } from "react";

import type { AppAction, AppActions } from "../app/types";

import { shouldRunDocumentCommand } from "../app/keyboard";

import { CapabilityGate } from "../components/CapabilityGate";

import { PdfInspection } from "../components/PdfInspection";

import { StructuredResult } from "../components/StructuredResult";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { ResourceReference } from "./FeatureValues";

import { resource } from "./FeatureValues";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { stringValue } from "./FeatureValues";

import { numberValue } from "./FeatureValues";

import { useResourceText } from "./FeatureValues";

import { Panel } from "./FeaturePanels";

import { StatusLine } from "./FeaturePanels";

import { statusLabel } from "./FeatureValues";

import { recognitionEngines } from "./TaskEngineValues";

import { TaskEngineSelector } from "./TaskEngineSelection";

interface PdfPageState {
  readonly recognitionRevision?: number;
  readonly correctedAfterRecognition?: boolean;
  readonly detected?: boolean;
  readonly hasTextLayer?: boolean;
  readonly addedThisSession?: boolean;
  readonly rotation?: number;
  readonly width?: number;
  readonly height?: number;
  readonly index: number;
  readonly statusCode: string;
  readonly thumbnail?: ResourceReference | null;
  readonly structuredResult?: ResourceReference | null;
}

function pdfPages(value: unknown): readonly PdfPageState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is PdfPageState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const page = entry as Partial<PdfPageState>;
    return (
      typeof page.index === "number" &&
      typeof page.statusCode === "string" &&
      (page.structuredResult === undefined ||
        page.structuredResult === null ||
        resource(page.structuredResult) !== undefined)
    );
  });
}

function PdfParameters({
  state,
  actions,
  busy,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly actions: AppActions;
  readonly busy: boolean;
}) {
  const settings =
    state.processingSettings !== null &&
    typeof state.processingSettings === "object"
      ? (state.processingSettings as Record<string, unknown>)
      : {};
  const parameters = [
    ["renderDpi", "渲染 DPI", 300, 72, 600, 1],
    ["maxPixels", "单页像素预算", 16000000, 100000, 16000000, 1],
    ["fontSizeRatio", "字号比例", 0.8, 0.1, 2, 0.1],
    ["fontSizeRetryCount", "溢出重试次数", 5, 0, 10, 1],
    ["fontSizeShrinkFactor", "字号缩小比例", 0.75, 0.01, 0.99, 0.01],
    ["minFontSize", "最小字号（pt）", 4, 1, 72, 0.5],
  ] as const;
  return (
    <details>
      <summary>PDF 参数</summary>
      <form
        key={JSON.stringify(settings)}
        onSubmit={(event) => {
          event.preventDefault();
          const values = new FormData(event.currentTarget);
          const next = Object.fromEntries(
            parameters.map(([name]) => [name, Number(values.get(name))]),
          );
          actions.run({
            type: "pdf.setProcessingSettings",
            settings: {
              ...next,
              compressOnSave: values.has("compressOnSave"),
              cleanOnSave: values.has("cleanOnSave"),
            },
          });
        }}
      >
        <fieldset disabled={busy}>
          {parameters.map(([name, label, value, min, max, step]) => (
            <label key={name}>
              {label}
              <input
                name={name}
                type="number"
                min={min}
                max={max}
                step={step}
                required
                defaultValue={
                  typeof settings[name] === "number"
                    ? (settings[name] as number)
                    : value
                }
              />
            </label>
          ))}
          <label>
            <input
              name="compressOnSave"
              type="checkbox"
              defaultChecked={settings.compressOnSave !== false}
            />
            保存时压缩
          </label>
          <label>
            <input
              name="cleanOnSave"
              type="checkbox"
              defaultChecked={settings.cleanOnSave === true}
            />
            保存时深度清理内容流
          </label>
          <Button type="submit">保存 PDF 参数</Button>
        </fieldset>
      </form>
    </details>
  );
}

export function PdfPage(props: FeatureProps) {
  return (
    <PdfDocumentPage
      key={stringValue(feature(props.viewState, "pdf").documentId)}
      {...props}
    />
  );
}

function PdfDocumentPage({ viewState, actions: parentActions }: FeatureProps) {
  const [operationRange, setOperationRange] = useState("selected");
  const [insertAfter, setInsertAfter] = useState(1);
  const [pageWidth, setPageWidth] = useState(612);
  const [pageHeight, setPageHeight] = useState(792);
  const draggingPage = useRef<number | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<readonly number[] | null>(
    null,
  );
  const [confirmRevision, setConfirmRevision] = useState(0);
  const [replaceLayers, setReplaceLayers] = useState(false);
  const state = feature(viewState, "pdf");
  const documentId =
    typeof state.documentId === "string" ? state.documentId : undefined;
  // 当前检查组件的预览状态排空接缝：切换/关闭前 await 最后位置/草稿在宿主
  // 完成提交，再发送后续命令（关闭可能取消/失败，文档保留时草稿不丢）。
  const positionFlush = useRef<(() => Promise<void>) | null>(null);
  const registerPositionFlush = useCallback(
    (flush: (() => Promise<void>) | null) => {
      positionFlush.current = flush;
    },
    [],
  );
  const registerNativePreviewFlush = parentActions.registerPdfPreviewFlush;
  const actions: AppActions = {
    ...parentActions,
    run: (action) => {
      const bound =
        documentId !== undefined &&
        action.type.startsWith("pdf.") &&
        action.type !== "pdf.open"
          ? {
              documentId,
              documentRevision: numberValue(state.revision),
              ...action,
            }
          : action;
      if (
        action.type !== "pdf.activateDocument" &&
        action.type !== "pdf.close" &&
        action.type !== "pdf.open"
      )
        return parentActions.run(bound);
      // 切换/关闭前等待最后位置/草稿在宿主确认提交；未确认时不发出该命令，
      // 待发送值保留在检查组件中供重试。
      const flush = positionFlush.current;
      if (!flush) return parentActions.run(bound);
      return flush().then(
        () => parentActions.run(bound),
        () => false,
      );
    },
  };
  const documents = Array.isArray(state.documents)
    ? state.documents.filter(
        (value): value is Record<string, unknown> =>
          !!value && typeof value === "object",
      )
    : [];
  const exports = Array.isArray(state.exportItems)
    ? state.exportItems.filter(
        (value): value is Record<string, unknown> =>
          !!value && typeof value === "object",
      )
    : [];
  // 关闭入口对准当前活动文档：远端会话未释放（关闭失败可重试）的条目即使
  // 页数为 0 也保持可用，避免只能退出应用才能释放会话。
  const activeDocument = documents.find(
    (document) => stringValue(document.documentId) === documentId,
  );
  const [modifiedOnly, setModifiedOnly] = useState(true);
  const busy = booleanValue(state.isBusy);
  const detected = numberValue(state.detectedCount);
  const layers = numberValue(state.textLayerCount);
  const canAdd =
    state.canAddTextLayer === true && detected === numberValue(state.pageCount);
  const engines = recognitionEngines(state.engines);
  const pageCount = numberValue(state.pageCount);
  const selectedPage = numberValue(state.selectedPage);
  const hasCurrentPage =
    stringValue(state.sessionId) !== undefined &&
    selectedPage >= 0 &&
    selectedPage < pageCount;
  // 包装 run 每次渲染都新建：用 ref 持有最新包装（自动绑定当前
  // documentId/revision），监听器不随其重建。
  const wrappedActionsRef = useRef(actions);
  useEffect(() => {
    wrappedActionsRef.current = actions;
  });
  // Windows 文档命令快捷键：与工具栏按钮同一包装 run 入口（自动绑定当前
  // documentId/revision，并在关闭/打开前等待检查草稿排空），可用条件逐条
  // 镜像对应按钮；IME 组合、隐藏/失焦页面与未支持场景不触发任何命令。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (
        !shouldRunDocumentCommand(event) ||
        event.repeat ||
        event.altKey ||
        !(event.ctrlKey || event.metaKey)
      )
        return;
      const key = event.key.toLowerCase();
      const closeEnabled =
        pageCount > 0 || busy || activeDocument?.closeFailed === true;
      const command: AppAction | null =
        key === "o" &&
        !event.shiftKey &&
        viewState.capabilities.includes("pdf.open")
          ? { type: "pdf.open" }
          : key === "s" &&
              !event.shiftKey &&
              viewState.capabilities.includes("pdf.save") &&
              pageCount > 0 &&
              !busy
            ? { type: "pdf.save" }
            : key === "s" &&
                event.shiftKey &&
                state.canCopyExport === true &&
                pageCount > 0 &&
                !busy
              ? { type: "pdf.saveAs" }
              : key === "w" &&
                  !event.shiftKey &&
                  viewState.capabilities.includes("pdf.open") &&
                  closeEnabled
                ? { type: "pdf.close" }
                : null;
      if (!command) return;
      event.preventDefault();
      void wrappedActionsRef.current.run(command);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [
    viewState.capabilities,
    pageCount,
    busy,
    state.canCopyExport,
    activeDocument?.closeFailed,
  ]);
  useEffect(() => {
    if (!documentId || (hasCurrentPage && state.canInspectPage === true))
      return;
    return registerNativePreviewFlush?.(documentId, () => Promise.resolve());
  }, [
    documentId,
    hasCurrentPage,
    state.canInspectPage,
    registerNativePreviewFlush,
  ]);
  const pages = pdfPages(state.pages);
  const windowStart = Math.max(0, numberValue(state.windowStart));
  const selectedPages = Array.isArray(state.selectedPages)
    ? state.selectedPages.filter(
        (page): page is number => typeof page === "number" && page >= 0,
      )
    : [];
  const selected = new Set(selectedPages);
  const operationDisabled =
    busy ||
    pageCount === 0 ||
    (operationRange === "selected" && selectedPages.length === 0);
  const insertionDisabled =
    busy ||
    pageCount === 0 ||
    !Number.isInteger(insertAfter) ||
    insertAfter < 0 ||
    insertAfter > pageCount ||
    !Number.isFinite(pageWidth) ||
    !Number.isFinite(pageHeight) ||
    pageWidth <= 0 ||
    pageHeight <= 0;
  const movePage = (fromIndex: number, toIndex: number) =>
    actions.run({
      type: "pdf.movePage",
      fromIndex,
      toIndex,
      revision: numberValue(state.revision),
    });
  const activePage = pages.find((page) => page.index === selectedPage);
  const activeStructured = resource(activePage?.structuredResult);
  const activeStructuredText = useResourceText(activeStructured);
  return (
    <Workspace
      eyebrow="DOCUMENT / 03"
      title="PDF 工作台"
      description="提取/解析结果，或为扫描页添加可搜索文字层后保存。"
      actions={
        <>
          <CapabilityGate
            appearance="primary"
            capability="pdf.open"
            capabilities={viewState.capabilities}
            action={{ type: "pdf.open" }}
            actions={actions}
            icon={<FolderOpen aria-hidden="true" size={16} />}
          >
            {documents.length > 0 ? "添加 PDF" : "打开 PDF"}
          </CapabilityGate>
          <CapabilityGate
            capability="pdf.open"
            capabilities={viewState.capabilities}
            action={{ type: "pdf.close" }}
            actions={actions}
            disabled={
              pageCount === 0 && !busy && activeDocument?.closeFailed !== true
            }
            icon={<X aria-hidden="true" size={16} />}
          >
            关闭文档
          </CapabilityGate>
        </>
      }
    >
      <div className="pdf-documents" aria-label="PDF 文档列表">
        {documents.map((document) => (
          <Button
            key={stringValue(document.documentId)}
            appearance={
              document.documentId === documentId ? "primary" : "secondary"
            }
            aria-pressed={document.documentId === documentId}
            onClick={() =>
              void actions.run({
                type: "pdf.activateDocument",
                documentId: document.documentId,
              })
            }
          >
            {stringValue(document.name) ?? "PDF"}
            {document.isModified === true ? " · 未保存" : ""}
            {document.isBusy === true ? " · 处理中" : ""}
            {document.closeFailed === true ? " · 关闭失败，可重试" : ""}
          </Button>
        ))}
      </div>
      {documents.length > 0 && (
        <details className="pdf-export">
          <summary>批量导出副本</summary>
          <p>
            导出副本保留原文档修改状态和保存目标。默认避让同名文件；目标竞争导致的失败可重试。
          </p>
          <label>
            <input
              type="checkbox"
              checked={modifiedOnly}
              onChange={(event) => setModifiedOnly(event.target.checked)}
            />
            仅已修改文档
          </label>
          <Button
            disabled={state.canCopyExport !== true || state.exporting === true}
            onClick={() =>
              void actions.run({
                type: "pdf.exportDocuments",
                modifiedOnly,
                retry: false,
              })
            }
          >
            选择目录并导出
          </Button>
          <Button
            disabled={state.exporting !== true}
            onClick={() => void actions.run({ type: "pdf.cancelExport" })}
          >
            停止后续导出
          </Button>
          <Button
            disabled={
              state.exporting === true ||
              // 与宿主 CreateExportPlan(retry) 一致：仅真正失败/取消/未开始项
              // 可重试；saved 与 unconfirmed（结果未确认，不自动重试）不重发。
              !exports.some(
                (item) =>
                  item.status === "failed" ||
                  item.status === "cancelled" ||
                  item.status === "not_started",
              )
            }
            onClick={() =>
              void actions.run({
                type: "pdf.exportDocuments",
                modifiedOnly,
                retry: true,
              })
            }
          >
            重试未完成项
          </Button>
          <ul>
            {exports.map((item) => (
              <li key={stringValue(item.documentId)}>
                {stringValue(item.name)} ·{" "}
                {item.status === "saved"
                  ? "成功"
                  : item.status === "saving"
                    ? "提交中"
                    : item.status === "unconfirmed"
                      ? "结果未确认"
                      : item.status === "failed"
                        ? "失败"
                        : item.status === "cancelled"
                          ? "取消"
                          : "未开始"}
                {stringValue(item.output)
                  ? ` · ${stringValue(item.output)}`
                  : ""}
                {stringValue(item.error) ? ` · ${stringValue(item.error)}` : ""}
              </li>
            ))}
          </ul>
        </details>
      )}
      <TaskEngineSelector
        engines={engines}
        taskEngine={stringValue(state.taskEngine)}
        enabled={
          viewState.capabilities.includes("recognition.engine") &&
          !booleanValue(state.isBusy)
        }
        actions={actions}
        scope="pdf"
      />
      <div className="pdf-workspace">
        <Panel label="PAGES" title="页面">
          {pages.length === 0 ? (
            <EmptyStage
              title="尚未打开文档"
              detail="打开 PDF 后显示页面状态与多选项。"
            />
          ) : (
            <>
              <div className="pdf-selection-actions">
                <Button
                  disabled={busy}
                  onClick={() =>
                    actions.run({
                      type: "pdf.selectAll",
                      selected: true,
                    })
                  }
                >
                  全选页面
                </Button>
                <Button
                  disabled={busy || selectedPages.length === 0}
                  onClick={() =>
                    actions.run({ type: "pdf.selectAll", selected: false })
                  }
                >
                  取消选择
                </Button>
                <span>
                  已选 {selectedPages.length} / {pageCount} 页
                </span>
              </div>
              <ol className="pdf-page-list" aria-label="PDF 页面缩略图">
                {pages.map((page) => {
                  const thumbnail = resource(page.thumbnail);
                  return (
                    <li
                      className={selected.has(page.index) ? "is-selected" : ""}
                      key={page.index}
                      data-page-index={page.index}
                      draggable={!busy}
                      onDragStart={() => {
                        draggingPage.current = page.index;
                      }}
                      onDragEnd={() => {
                        draggingPage.current = null;
                      }}
                      onDragOver={(event) => {
                        if (!busy) event.preventDefault();
                      }}
                      onDrop={(event) => {
                        event.preventDefault();
                        if (
                          !busy &&
                          draggingPage.current !== null &&
                          draggingPage.current !== page.index
                        )
                          void movePage(draggingPage.current, page.index);
                        draggingPage.current = null;
                      }}
                    >
                      <Checkbox
                        aria-label={`选择第 ${page.index + 1} 页`}
                        checked={selected.has(page.index)}
                        onChange={(_, data) => {
                          actions.run({
                            type: "pdf.selectPage",
                            page: page.index,
                            selected: data.checked === true,
                          });
                        }}
                      />
                      {thumbnail ? (
                        <button
                          type="button"
                          className="pdf-thumbnail-button"
                          aria-label={`检查第 ${page.index + 1} 页`}
                          onClick={() =>
                            actions.run({
                              type: "pdf.setCurrentPage",
                              page: page.index,
                            })
                          }
                        >
                          <img
                            alt={`第 ${page.index + 1} 页缩略图`}
                            src={thumbnail.url}
                          />
                        </button>
                      ) : (
                        <span className="pdf-thumbnail-placeholder">PDF</span>
                      )}
                      <span>第 {page.index + 1} 页</span>
                      <div className="pdf-page-move">
                        <Button
                          size="small"
                          aria-label={`第 ${page.index + 1} 页向前移动`}
                          disabled={busy || page.index === 0}
                          onClick={() => movePage(page.index, page.index - 1)}
                          icon={<ArrowUp size={14} />}
                        />
                        <Button
                          size="small"
                          aria-label={`第 ${page.index + 1} 页向后移动`}
                          disabled={busy || page.index === pageCount - 1}
                          onClick={() => movePage(page.index, page.index + 1)}
                          icon={<ArrowDown size={14} />}
                        />
                      </div>
                      <span>
                        {page.detected
                          ? page.hasTextLayer
                            ? page.addedThisSession
                              ? "本次 OCR 文字层"
                              : "已有文字层 / 来源未知"
                            : "无文字层"
                          : "检测中"}
                      </span>
                      <span>{statusLabel(page.statusCode, "待处理")}</span>
                      {(page.width ?? 0) > 0 && (
                        <span>
                          {Math.round(page.width ?? 0)} ×{" "}
                          {Math.round(page.height ?? 0)} pt · {page.rotation}°
                        </span>
                      )}
                    </li>
                  );
                })}
              </ol>
              {pageCount > pages.length && (
                <div
                  className="collection-pagination"
                  aria-label="PDF 页面分页"
                >
                  <Button
                    disabled={windowStart === 0}
                    onClick={() =>
                      actions.run({
                        type: "pdf.setWindow",
                        start: Math.max(0, windowStart - 64),
                      })
                    }
                  >
                    上一组
                  </Button>
                  <span>
                    {windowStart + 1}–
                    {Math.min(pageCount, windowStart + pages.length)} /{" "}
                    {pageCount}
                  </span>
                  <Button
                    disabled={windowStart + pages.length >= pageCount}
                    onClick={() =>
                      actions.run({
                        type: "pdf.setWindow",
                        start: windowStart + 64,
                      })
                    }
                  >
                    下一组
                  </Button>
                </div>
              )}
            </>
          )}
        </Panel>
        <div className="pdf-main">
          <div className="pdf-selection-actions">
            <label htmlFor="pdf-operation-range">页面处理范围</label>
            <Select
              id="pdf-operation-range"
              value={operationRange}
              disabled={busy}
              onChange={(_, data) => setOperationRange(data.value)}
            >
              <option value="selected">选中页</option>
              <option value="all">全文件</option>
            </Select>
            <span>旋转、横纵放置和文字朝向仅处理此范围</span>
          </div>
          <Toolbar aria-label="PDF 页面命令">
            <CapabilityGate
              capability="pdf.rotate"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.rotate",
                degrees: 90,
                range: operationRange,
              }}
              actions={actions}
              icon={<RotateCw aria-hidden="true" size={16} />}
              disabled={operationDisabled}
            >
              顺时针 90°
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.rotate"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.rotate",
                degrees: -90,
                range: operationRange,
              }}
              actions={actions}
              disabled={operationDisabled}
              icon={<RotateCcw size={16} aria-hidden="true" />}
            >
              逆时针 90°
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.rotate"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.orient",
                landscape: true,
                range: operationRange,
              }}
              actions={actions}
              disabled={operationDisabled}
            >
              横放
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.rotate"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.orient",
                landscape: false,
                range: operationRange,
              }}
              actions={actions}
              disabled={operationDisabled}
            >
              纵放
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.rotate"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.correctOrientation", range: operationRange }}
              actions={actions}
              disabled={operationDisabled}
            >
              自动文字朝向
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.deletePages" }}
              actions={actions}
              disabled={selectedPages.length === 0 || busy}
              icon={<Trash2 aria-hidden="true" size={16} />}
            >
              删除选中页
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.ocrPages" }}
              actions={actions}
              disabled={selectedPages.length === 0 || busy}
              icon={<ScanText aria-hidden="true" size={16} />}
            >
              提取/解析选中页
            </CapabilityGate>
            <ToolbarDivider />
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.addTextLayers",
                range: "selected",
                overwrite: replaceLayers,
              }}
              actions={actions}
              disabled={busy || !canAdd || selectedPages.length === 0}
            >
              添加选中页文字层
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.addTextLayers",
                range: "unlayered",
                overwrite: false,
              }}
              actions={actions}
              disabled={
                busy || !canAdd || layers === pageCount || pageCount === 0
              }
            >
              为全部无层页添加
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{
                type: "pdf.addTextLayers",
                range: "all",
                overwrite: replaceLayers,
              }}
              actions={actions}
              disabled={busy || !canAdd || pageCount === 0}
            >
              添加整本文字层
            </CapabilityGate>
            <Button
              disabled={
                busy || selectedPages.length === 0 || detected !== pageCount
              }
              onClick={() => {
                setConfirmDelete([...selectedPages]);
                setConfirmRevision(numberValue(state.revision));
              }}
            >
              删除选中文字层
            </Button>
            <ToolbarDivider />
            <CapabilityGate
              capability="pdf.save"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.save" }}
              actions={actions}
              disabled={pageCount === 0 || busy}
              icon={<Save aria-hidden="true" size={16} />}
            >
              保存
            </CapabilityGate>
            {state.canCopyExport === true && (
              <Button
                disabled={busy || pageCount === 0}
                onClick={() => void actions.run({ type: "pdf.saveAs" })}
              >
                另存为并切换保存目标
              </Button>
            )}
          </Toolbar>
          <div className="pdf-insertion" aria-label="插入页面">
            <label htmlFor="pdf-insert-after">插入到第几页后（0 为开头）</label>
            <Input
              id="pdf-insert-after"
              type="number"
              min={0}
              max={pageCount}
              value={String(insertAfter)}
              disabled={busy}
              onChange={(_, data) => setInsertAfter(Number(data.value))}
            />
            <label htmlFor="pdf-blank-width">空白页宽度 pt</label>
            <Input
              id="pdf-blank-width"
              type="number"
              min={1}
              value={String(pageWidth)}
              disabled={busy}
              onChange={(_, data) => setPageWidth(Number(data.value))}
            />
            <label htmlFor="pdf-blank-height">空白页高度 pt</label>
            <Input
              id="pdf-blank-height"
              type="number"
              min={1}
              value={String(pageHeight)}
              disabled={busy}
              onChange={(_, data) => setPageHeight(Number(data.value))}
            />
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              actions={actions}
              action={{
                type: "pdf.insertBlank",
                afterIndex: insertAfter - 1,
                width: pageWidth,
                height: pageHeight,
                revision: numberValue(state.revision),
              }}
              disabled={insertionDisabled}
            >
              插入空白页
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              actions={actions}
              action={{
                type: "pdf.insertFrom",
                afterIndex: insertAfter - 1,
                revision: numberValue(state.revision),
              }}
              disabled={
                busy ||
                pageCount === 0 ||
                !Number.isInteger(insertAfter) ||
                insertAfter < 0 ||
                insertAfter > pageCount
              }
            >
              插入其他 PDF
            </CapabilityGate>
          </div>
          <p>
            自动文字朝向需要已就绪的 Paddle 通用文字
            OCR；缺少能力或模型时请在组件设置中检查。
          </p>
          <Button onClick={() => actions.navigate("settings")}>
            打开组件设置
          </Button>
          <p aria-live="polite">
            已检测 {detected}/{pageCount} 页 · 有文字层 {layers} 页 · 无文字层{" "}
            {Math.max(0, detected - layers)} 页 ·{" "}
            {state.isModified === true ? "当前修订尚未保存" : "当前修订已保存"}
          </p>
          <Checkbox
            checked={replaceLayers}
            disabled={busy}
            label="显式覆盖已有文字层（默认跳过）"
            onChange={(_, data) => setReplaceLayers(data.checked === true)}
          />
          {!canAdd && pageCount > 0 && (
            <p>
              添加文字层需要全部页检测完成，并使用具有可逆页坐标的文字 OCR
              模式，关闭文档去扭曲。提取/解析仍可使用。
            </p>
          )}
          <PdfParameters state={state} actions={actions} busy={busy} />
          {confirmDelete && (
            <div role="alertdialog" aria-label="确认删除文字层">
              <p>
                将删除第 {confirmDelete.map((index) => index + 1).join("、")}{" "}
                页的全部文字，包括原有可见文字，页面外观可能变化；扫描图像和其它图元保留。这不是安全脱敏工具。
              </p>
              <Button
                disabled={busy}
                onClick={() => {
                  actions.run({
                    type: "pdf.deleteTextLayers",
                    pages: confirmDelete,
                    revision: confirmRevision,
                    confirmed: true,
                  });
                  setConfirmDelete(null);
                }}
              >
                确认删除文字层
              </Button>
              <Button onClick={() => setConfirmDelete(null)}>返回</Button>
            </div>
          )}
          {busy && (
            <div aria-live="polite">
              <p>
                {(
                  {
                    load: "检测文字层",
                    render: "渲染",
                    ocr: "识别",
                    rotate: "旋转",
                    write: "写入文字层",
                    delete: "删除文字层",
                    save: "保存",
                    idle: "等待后台收尾",
                  } as Record<string, string>
                )[stringValue(state.phase) ?? "idle"] ?? "处理中"}{" "}
                · {numberValue(state.progressCurrent)}/
                {numberValue(state.progressTotal)}
              </p>
              <ProgressBar
                value={
                  numberValue(state.progressTotal) > 0 && state.phase !== "save"
                    ? numberValue(state.progressCurrent) /
                      numberValue(state.progressTotal)
                    : undefined
                }
              />
              <Button onClick={() => actions.run({ type: "pdf.cancel" })}>
                取消 PDF 操作
              </Button>
              <p>取消不会撤销已完成写入；后台实际收尾后才可继续操作。</p>
            </div>
          )}
          <Panel label="REVIEW" title="页面检查">
            {hasCurrentPage && state.canInspectPage === true ? (
              <PdfInspection
                key={`${documentId}:${stringValue(state.sessionId)}:${numberValue(state.revision)}:${selectedPage}:${resource(state.pagePreview)?.url ?? ""}:${resource(state.pageInspect)?.url ?? ""}`}
                position={
                  state.previewPosition &&
                  typeof state.previewPosition === "object"
                    ? (state.previewPosition as Record<string, unknown>)
                    : undefined
                }
                page={selectedPage}
                count={pageCount}
                revision={numberValue(state.revision)}
                sessionId={stringValue(state.sessionId) ?? ""}
                preview={resource(state.pagePreview)}
                inspect={resource(state.pageInspect)}
                status={stringValue(state.pageInspectStatusCode) ?? ""}
                busy={busy}
                canEdit={state.canCorrectText === true}
                actions={actions}
                registerPositionFlush={registerPositionFlush}
                documentId={documentId}
                registerNativePositionFlush={registerNativePreviewFlush}
              />
            ) : hasCurrentPage && activePage?.thumbnail ? (
              <img
                className="pdf-resource-preview"
                src={activePage.thumbnail.url}
                alt={`第 ${selectedPage + 1} 页预览`}
              />
            ) : (
              <p>选择页面后查看预览。</p>
            )}
            {activeStructured && (
              <>
                <p>
                  原始识别结果
                  {typeof activePage?.recognitionRevision === "number"
                    ? `（修订 ${activePage.recognitionRevision}）`
                    : ""}
                  {activePage?.correctedAfterRecognition === true
                    ? " · 校正前，复制内容保留原始识别文本"
                    : " · 复制内容保留原始识别文本"}
                </p>
                <StructuredResult
                  key={activeStructured.url}
                  source={activeStructuredText}
                  onCopy={(blockIndex, format) =>
                    actions.run({
                      type: "recognition.copyStructured",
                      resourceUri: activeStructured.url,
                      blockIndex,
                      format,
                    })
                  }
                />
              </>
            )}
          </Panel>
          <StatusLine>
            {statusLabel(state.statusCode, "尚未建立 PDF 会话。")}{" "}
            {stringValue(state.summary)}
          </StatusLine>
        </div>
      </div>
    </Workspace>
  );
}
