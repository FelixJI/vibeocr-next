import {
  Badge,
  Button,
  Checkbox,
  Input,
  ProgressBar,
  Select,
  Tab,
  TabList,
  Toolbar,
  ToolbarDivider,
} from "@fluentui/react-components";
import {
  ArrowDown,
  ArrowUp,
  Camera,
  ClipboardPaste,
  Copy,
  Download,
  ExternalLink,
  FileSpreadsheet,
  FilePlus2,
  FolderOpen,
  ImagePlus,
  Play,
  QrCode,
  RefreshCw,
  RotateCw,
  Save,
  ScanText,
  Sheet,
  Square,
  Trash2,
  X,
} from "lucide-react";
import { useEffect, useState } from "react";

import type { AppActions, AppViewState } from "../app/types";
import { CapabilityGate } from "../components/CapabilityGate";
import { ImageCanvasEditor } from "../components/ImageCanvasEditor";
import { EmptyStage, Workspace } from "../components/Workspace";

interface FeatureProps {
  readonly viewState: AppViewState;
  readonly actions: AppActions;
}

interface ResourceReference {
  readonly url: string;
  readonly mediaType: string;
  readonly byteLength: number;
}

interface BatchItemState {
  readonly id: string;
  readonly name: string;
  readonly statusCode: string;
  readonly resultSummary?: string | null;
}

interface PdfPageState {
  readonly index: number;
  readonly statusCode: string;
  readonly thumbnail?: ResourceReference | null;
}

interface QrResultState {
  readonly data: string;
  readonly format: string;
  readonly isUrl: boolean;
}

interface SourceOptionState {
  readonly kind: string;
  readonly id: string;
  readonly displayName: string;
  readonly selected: boolean;
}

interface FeatureOptionState {
  readonly featureId: string;
  readonly displayName: string;
  readonly accelerator: string;
  readonly selected: boolean;
}

interface InstallPlanComponentState {
  readonly componentId: string;
  readonly action: string;
  readonly dependencyState: string;
  readonly reasonCodes: readonly string[];
}

interface InstallPlanBlockerState {
  readonly code: string;
  readonly componentId?: string;
  readonly nextAction: string;
}

interface InstallPlanState {
  readonly planId: string;
  readonly expiresAt: string;
  readonly accelerator: string;
  readonly effectiveComponentIds: readonly string[];
  readonly effectiveDownloadSourceIds: readonly string[];
  readonly components: readonly InstallPlanComponentState[];
  readonly blockers: readonly InstallPlanBlockerState[];
  readonly cost: {
    readonly downloadBytes: number | null;
    readonly additionalDiskBytes: number | null;
    readonly unknownReasonCodes: readonly string[];
  };
}

interface MaintenanceState {
  readonly failureReason?: string;
  readonly failureCode?: string;
  readonly isRunning: boolean;
  readonly statusCode: string;
  readonly operationId?: string | null;
  readonly requestedComponentIds: readonly string[];
  readonly effectiveComponentIds: readonly string[];
  readonly requestedSourceIds: readonly string[];
  readonly effectiveSourceIds: readonly string[];
  readonly canCancel: boolean;
  readonly canRetry: boolean;
}

interface RecognitionEngineState {
  readonly engine: string;
  readonly displayName: string;
  readonly selected: boolean;
  readonly isTaskOverride: boolean;
  readonly availability: string;
  readonly requiresDownload: boolean;
  readonly lifecycleKind?: string;
  readonly supportsPreload?: boolean;
  readonly supportsTtl?: boolean;
  readonly supportsPinning?: boolean;
  readonly supportsRelease?: boolean;
}

function feature(
  viewState: AppViewState,
  scope: string,
): Readonly<Record<string, unknown>> {
  const value = viewState.features[scope];
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Readonly<Record<string, unknown>>)
    : {};
}

function resource(value: unknown): ResourceReference | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<ResourceReference>;
  return typeof candidate.url === "string" &&
    typeof candidate.mediaType === "string" &&
    typeof candidate.byteLength === "number"
    ? (candidate as ResourceReference)
    : undefined;
}

function numberValue(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function booleanValue(value: unknown): boolean {
  return value === true;
}

function stringValues(value: unknown): readonly string[] {
  return Array.isArray(value) &&
    value.every((entry) => typeof entry === "string")
    ? value
    : [];
}

function batchItems(value: unknown): readonly BatchItemState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is BatchItemState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const item = entry as Partial<BatchItemState>;
    return (
      typeof item.id === "string" &&
      typeof item.name === "string" &&
      typeof item.statusCode === "string" &&
      (item.resultSummary === undefined ||
        item.resultSummary === null ||
        typeof item.resultSummary === "string")
    );
  });
}

function batchItemLabel(statusCode: string): string {
  const labels: Readonly<Record<string, string>> = {
    "batch.item.pending": "等待",
    "batch.item.running": "处理中",
    "batch.item.completed": "完成",
    "batch.item.failed": "失败",
    "batch.item.cancelled": "已取消",
  };
  return labels[statusCode] ?? "未知";
}

function pdfPages(value: unknown): readonly PdfPageState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is PdfPageState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const page = entry as Partial<PdfPageState>;
    return (
      typeof page.index === "number" && typeof page.statusCode === "string"
    );
  });
}

function qrResults(value: unknown): readonly QrResultState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is QrResultState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const result = entry as Partial<QrResultState>;
    return (
      typeof result.data === "string" &&
      typeof result.format === "string" &&
      typeof result.isUrl === "boolean"
    );
  });
}

function sourceOptions(value: unknown): readonly SourceOptionState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is SourceOptionState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const option = entry as Partial<SourceOptionState>;
    return (
      typeof option.kind === "string" &&
      typeof option.id === "string" &&
      typeof option.displayName === "string" &&
      typeof option.selected === "boolean"
    );
  });
}

function featureOptions(value: unknown): readonly FeatureOptionState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is FeatureOptionState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const option = entry as Partial<FeatureOptionState>;
    return (
      typeof option.featureId === "string" &&
      typeof option.displayName === "string" &&
      typeof option.accelerator === "string" &&
      typeof option.selected === "boolean"
    );
  });
}

function recognitionEngines(value: unknown): readonly RecognitionEngineState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is RecognitionEngineState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const option = entry as Partial<RecognitionEngineState>;
    return (
      typeof option.engine === "string" &&
      typeof option.displayName === "string" &&
      typeof option.selected === "boolean" &&
      typeof option.isTaskOverride === "boolean" &&
      typeof option.availability === "string" &&
      typeof option.requiresDownload === "boolean"
    );
  });
}

function maintenanceState(value: unknown): MaintenanceState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const state = value as Partial<MaintenanceState>;
  if (
    typeof state.isRunning !== "boolean" ||
    typeof state.statusCode !== "string" ||
    !Array.isArray(state.requestedComponentIds) ||
    !Array.isArray(state.effectiveComponentIds) ||
    !Array.isArray(state.requestedSourceIds) ||
    !Array.isArray(state.effectiveSourceIds) ||
    typeof state.canCancel !== "boolean" ||
    typeof state.canRetry !== "boolean"
  )
    return undefined;
  return state as MaintenanceState;
}

function planComponents(value: unknown): readonly InstallPlanComponentState[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter(
      (entry): entry is Record<string, unknown> =>
        entry !== null && typeof entry === "object" && !Array.isArray(entry),
    )
    .map((entry) => ({
      componentId: stringValue(entry.componentId) ?? "",
      action: stringValue(entry.action) ?? "",
      dependencyState: stringValue(entry.dependencyState) ?? "",
      reasonCodes: stringValues(entry.reasonCodes),
    }))
    .filter((entry) => entry.componentId !== "");
}

function planBlockers(value: unknown): readonly InstallPlanBlockerState[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter(
      (entry): entry is Record<string, unknown> =>
        entry !== null && typeof entry === "object" && !Array.isArray(entry),
    )
    .map((entry) => ({
      code: stringValue(entry.code) ?? "",
      componentId: stringValue(entry.componentId ?? undefined),
      nextAction: stringValue(entry.nextAction) ?? "",
    }))
    .filter((entry) => entry.code !== "" && entry.nextAction !== "");
}

function planCost(value: unknown): InstallPlanState["cost"] {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return {
      downloadBytes: null,
      additionalDiskBytes: null,
      unknownReasonCodes: [],
    };
  const cost = value as Record<string, unknown>;
  return {
    downloadBytes:
      typeof cost.downloadBytes === "number" &&
      Number.isFinite(cost.downloadBytes)
        ? cost.downloadBytes
        : null,
    additionalDiskBytes:
      typeof cost.additionalDiskBytes === "number" &&
      Number.isFinite(cost.additionalDiskBytes)
        ? cost.additionalDiskBytes
        : null,
    unknownReasonCodes: stringValues(cost.unknownReasonCodes),
  };
}

function installPlan(value: unknown): InstallPlanState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const plan = value as Record<string, unknown>;
  const planId = stringValue(plan.planId);
  const expiresAt = stringValue(plan.expiresAt);
  const accelerator = stringValue(plan.accelerator);
  if (!planId || !expiresAt || !accelerator) return undefined;
  return {
    planId,
    expiresAt,
    accelerator,
    effectiveComponentIds: stringValues(plan.effectiveComponentIds),
    effectiveDownloadSourceIds: stringValues(plan.effectiveDownloadSourceIds),
    components: planComponents(plan.components),
    blockers: planBlockers(plan.blockers),
    cost: planCost(plan.cost),
  };
}

function isPlanFresh(expiresAt: string): boolean {
  const expiry = Date.parse(expiresAt);
  return Number.isFinite(expiry) && expiry > Date.now();
}

const installActionLabels: Readonly<Record<string, string>> = {
  retain: "保留",
  install: "安装",
  replace: "替换",
  remove: "移除",
};

const dependencyStateLabels: Readonly<Record<string, string>> = {
  satisfied: "依赖已满足",
  pending: "依赖待安装",
};

const installReasonLabels: Readonly<Record<string, string>> = {
  requested: "用户选择",
  required_dependency: "必要依赖",
  removed_by_selection: "按选择移除",
};

const unknownCostLabels: Readonly<Record<string, string>> = {
  artifact_resolution_required: "下载量待解析",
  candidate_disk_usage_unknown: "磁盘用量未知",
  native_model_preparation_not_estimated: "原生模型首次准备另计",
};

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "0 B";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  const exponent = Math.min(
    units.length - 1,
    Math.floor(Math.log(bytes) / Math.log(1024)),
  );
  const value = bytes / 1024 ** exponent;
  const text =
    value >= 100 || Number.isInteger(value)
      ? value.toFixed(0)
      : value.toFixed(1);
  return `${text} ${units[exponent]}`;
}

function knownLabels(
  codes: readonly string[],
  labels: Readonly<Record<string, string>>,
  unknownCodes: string[],
): readonly string[] {
  return codes.flatMap((code) => {
    const label = labels[code];
    if (label !== undefined) return [label];
    unknownCodes.push(code);
    return [];
  });
}

function maintenanceStatusLabel(statusCode: string): string {
  const labels: Readonly<Record<string, string>> = {
    idle: "尚未执行维护操作",
    running: "正在执行维护操作",
    succeeded: "维护操作已完成",
    failed: "维护操作失败",
    cancelled: "维护操作已取消",
    unavailable: "当前模式不可用",
  };
  return labels[statusCode] ?? statusCode;
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function availabilityLabel(availability: string): string {
  const labels: Readonly<Record<string, string>> = {
    ready: "可用",
    preparation_required: "需准备依赖",
    unavailable: "不可用",
  };
  return labels[availability] ?? availability;
}

function lifecycleHint(
  engine: RecognitionEngineState | undefined,
): string | null {
  if (!engine) return null;
  const controls = [
    engine.supportsPreload === true ? "预热" : null,
    engine.supportsTtl === true ? "TTL" : null,
    engine.supportsPinning === true ? "固定驻留" : null,
    engine.supportsRelease === true ? "释放" : null,
  ].filter((value): value is string => value !== null);
  if (engine.lifecycleKind === "unmanaged") {
    return "该模式不提供模型预热、TTL、固定驻留或释放控制。";
  }
  if (engine.lifecycleKind === "process_keep_alive") {
    return controls.length === 0
      ? "MinerU 使用进程保活；当前目录未声明可用控制。"
      : `MinerU 使用进程保活；仅支持：${controls.join("、")}。`;
  }
  if (engine.lifecycleKind === "model_residency") {
    return controls.length === 0
      ? "该 Paddle 模式使用模型驻留；当前目录未声明可用控制。"
      : `该 Paddle 模式使用模型驻留；支持：${controls.join("、")}。`;
  }
  return null;
}

function acceleratorLabel(accelerator: string): string {
  return accelerator === "nvidia_cuda" ? "CUDA GPU" : "CPU";
}

function statusLabel(value: unknown, fallback: string): string {
  const labels: Readonly<Record<string, string>> = {
    "recognition.running": "正在识别",
    "recognition.completed": "识别完成",
    "recognition.ready": "等待输入",
    "recognition.failed": "识别失败，请检查运行时状态后重试",
    "recognition.cancelled": "识别已取消，可重新选择输入",
    "pdf.open": "PDF 会话已建立",
    "pdf.empty": "尚未建立 PDF 会话",
    "qrcode.decoded": "识别完成",
    "qrcode.ready": "等待输入",
    "settings.ready": "运行环境设置已同步",
    "settings.restartRequired": "更改将在重启后生效",
    "update.available": "发现可用更新",
    "update.current": "当前已是最新版本",
  };
  return typeof value === "string" ? (labels[value] ?? fallback) : fallback;
}

function useResourceText(reference: ResourceReference | undefined): string {
  const [text, setText] = useState("");
  useEffect(() => {
    if (!reference) return undefined;
    const cancellation = new AbortController();
    void fetch(reference.url, {
      cache: "no-store",
      credentials: "omit",
      signal: cancellation.signal,
    })
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.text();
      })
      .then(setText)
      .catch((error: unknown) => {
        if (!(error instanceof DOMException && error.name === "AbortError"))
          setText("结果资源已失效，请重新识别。");
      });
    return () => cancellation.abort();
  }, [reference]);
  return reference ? text : "";
}

function Panel({
  label,
  title,
  className,
  children,
}: {
  readonly label: string;
  readonly title: string;
  readonly className?: string;
  readonly children: React.ReactNode;
}) {
  return (
    <section
      className={`work-panel${className ? ` ${className}` : ""}`}
      aria-label={title}
    >
      <header className="panel-heading">
        <span>{label}</span>
        <h2>{title}</h2>
      </header>
      <div className="panel-content">{children}</div>
    </section>
  );
}

function StatusLine({ children }: { readonly children: React.ReactNode }) {
  return (
    <output className="status-line" aria-live="polite">
      {children}
    </output>
  );
}

export function RecognitionPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "recognition");
  const input = resource(state.input);
  const result = resource(state.result);
  const resultText = useResourceText(result);
  return (
    <Workspace
      eyebrow="WORKBENCH / 01"
      title="单次识别"
      description="截图、粘贴或拖入图片；在同一工作台检查与复制结果。"
      actions={
        <>
          <CapabilityGate
            appearance="secondary"
            capability="recognition.file"
            capabilities={viewState.capabilities}
            action={{ type: "recognition.selectImage" }}
            actions={actions}
            icon={<ImagePlus aria-hidden="true" size={16} />}
          >
            选择图片
          </CapabilityGate>
          <CapabilityGate
            appearance="secondary"
            capability="recognition.clipboard"
            capabilities={viewState.capabilities}
            action={{ type: "recognition.readClipboard" }}
            actions={actions}
            icon={<ClipboardPaste aria-hidden="true" size={16} />}
          >
            从剪贴板
          </CapabilityGate>
          <CapabilityGate
            appearance="primary"
            capability="recognition.capture"
            capabilities={viewState.capabilities}
            action={{ type: "recognition.captureScreen" }}
            actions={actions}
            icon={<Camera aria-hidden="true" size={16} />}
          >
            截图识别
          </CapabilityGate>
          <CapabilityGate
            capability="recognition.capture"
            capabilities={viewState.capabilities}
            action={{ type: "recognition.cancel" }}
            actions={actions}
            disabled={!booleanValue(state.isBusy)}
            icon={<Square aria-hidden="true" size={16} />}
          >
            取消
          </CapabilityGate>
        </>
      }
    >
      <StatusLine>
        {statusLabel(
          state.statusCode,
          viewState.connected
            ? "等待宿主输入。"
            : "等待宿主输入。演示状态不会读取剪贴板、文件或屏幕。",
        )}
      </StatusLine>
      <TaskEngineSelector
        engines={recognitionEngines(state.engines)}
        taskEngine={stringValue(state.taskEngine)}
        enabled={viewState.capabilities.includes("recognition.engine")}
        actions={actions}
      />
      <div className="inspection-grid">
        <Panel label="INPUT / 01" title="输入图像">
          {input ? (
            <ImageCanvasEditor
              actions={actions}
              canExport={viewState.capabilities.includes(
                "recognition.annotation",
              )}
              source={input.url}
            />
          ) : (
            <EmptyStage
              title="等待图片"
              detail="选择、粘贴、截图或直接拖入图片"
            />
          )}
        </Panel>
        <Panel label="OUTPUT / 02" title="识别结果">
          {result ? (
            <>
              <Toolbar aria-label="识别结果操作" size="small">
                <CapabilityGate
                  capability="recognition.results"
                  capabilities={viewState.capabilities}
                  action={{ type: "recognition.copy", format: "plain" }}
                  actions={actions}
                  icon={<Copy aria-hidden="true" size={16} />}
                >
                  复制文本
                </CapabilityGate>
                <CapabilityGate
                  capability="recognition.results"
                  capabilities={viewState.capabilities}
                  action={{ type: "recognition.export", format: "markdown" }}
                  actions={actions}
                  icon={<Download aria-hidden="true" size={16} />}
                >
                  导出 Markdown
                </CapabilityGate>
                <CapabilityGate
                  capability="recognition.results"
                  capabilities={viewState.capabilities}
                  action={{ type: "recognition.export", format: "docx" }}
                  actions={actions}
                  icon={<Sheet aria-hidden="true" size={16} />}
                >
                  导出 Word
                </CapabilityGate>
                <CapabilityGate
                  capability="recognition.results"
                  capabilities={viewState.capabilities}
                  action={{ type: "recognition.export", format: "xlsx" }}
                  actions={actions}
                  icon={<FileSpreadsheet aria-hidden="true" size={16} />}
                >
                  导出 Excel
                </CapabilityGate>
              </Toolbar>
              <pre className="result-document">
                {resultText || "正在读取结果…"}
              </pre>
            </>
          ) : (
            <EmptyStage
              title="尚无识别结果"
              detail="完成识别后，文本与结构化内容显示在这里。"
            />
          )}
        </Panel>
      </div>
    </Workspace>
  );
}

export function BatchPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "batch");
  const itemCount = numberValue(state.itemCount);
  const completed = numberValue(state.completedCount);
  const failed = numberValue(state.failedCount);
  const running = booleanValue(state.isRunning);
  const items = batchItems(state.items);
  const windowStart = Math.max(0, numberValue(state.windowStart));
  return (
    <Workspace
      eyebrow="QUEUE / 02"
      title="批量识别"
      description="按队列处理图像，并集中检查单项结果。"
      actions={
        <>
          <CapabilityGate
            appearance="secondary"
            capability="batch.add"
            capabilities={viewState.capabilities}
            action={{ type: "batch.addFiles" }}
            actions={actions}
            icon={<FilePlus2 aria-hidden="true" size={16} />}
          >
            添加图片
          </CapabilityGate>
          <CapabilityGate
            appearance="primary"
            capability="batch.run"
            capabilities={viewState.capabilities}
            action={{ type: "batch.start" }}
            actions={actions}
            disabled={itemCount === 0 || running}
            icon={<Play aria-hidden="true" size={16} />}
          >
            开始识别
          </CapabilityGate>
          <CapabilityGate
            capability="batch.run"
            capabilities={viewState.capabilities}
            action={{ type: running ? "batch.cancel" : "batch.clear" }}
            actions={actions}
            disabled={itemCount === 0}
            icon={
              running ? (
                <Square aria-hidden="true" size={16} />
              ) : (
                <Trash2 aria-hidden="true" size={16} />
              )
            }
          >
            {running ? "取消全部" : "清空队列"}
          </CapabilityGate>
        </>
      }
    >
      <div className="collection-workspace">
        <Panel label="QUEUE" title="文件队列">
          <div className="queue-summary">
            <span>{itemCount} 个文件</span>
            <Badge appearance="tint">
              {running
                ? `正在处理 · ${completed}/${itemCount}`
                : failed > 0
                  ? `${failed} 项失败`
                  : itemCount > 0
                    ? "队列已就绪"
                    : "等待输入"}
            </Badge>
          </div>
          <p className="batch-scheduling-note">批次由识别服务自动调度</p>
          {itemCount === 0 ? (
            <EmptyStage
              title="队列为空"
              detail="添加图片后可调整顺序并开始识别。"
            />
          ) : (
            <>
              <ProgressBar
                aria-label="批量识别进度"
                value={itemCount > 0 ? completed / itemCount : 0}
              />
              <ol className="batch-queue" aria-label="批量文件队列">
                {items.map((item, index) => (
                  <li className="batch-item" key={item.id}>
                    <span className="batch-position">
                      {String(windowStart + index + 1).padStart(2, "0")}
                    </span>
                    <span className="batch-item-copy">
                      <strong>{item.name}</strong>
                      <small>{batchItemLabel(item.statusCode)}</small>
                      {item.resultSummary && <em>{item.resultSummary}</em>}
                    </span>
                    <span className="batch-item-actions">
                      <Button
                        appearance="subtle"
                        aria-label={`上移 ${item.name}`}
                        disabled={running || windowStart + index === 0}
                        onClick={() =>
                          actions.run({
                            type: "batch.moveItem",
                            itemId: item.id,
                            delta: -1,
                          })
                        }
                      >
                        <ArrowUp aria-hidden="true" size={15} />
                      </Button>
                      <Button
                        appearance="subtle"
                        aria-label={`下移 ${item.name}`}
                        disabled={
                          running || windowStart + index >= itemCount - 1
                        }
                        onClick={() =>
                          actions.run({
                            type: "batch.moveItem",
                            itemId: item.id,
                            delta: 1,
                          })
                        }
                      >
                        <ArrowDown aria-hidden="true" size={15} />
                      </Button>
                      <Button
                        appearance="subtle"
                        aria-label={`移除 ${item.name}`}
                        disabled={running}
                        onClick={() =>
                          actions.run({
                            type: "batch.removeItem",
                            itemId: item.id,
                          })
                        }
                      >
                        <Trash2 aria-hidden="true" size={15} />
                      </Button>
                    </span>
                  </li>
                ))}
              </ol>
              {itemCount > items.length && (
                <div
                  className="collection-pagination"
                  aria-label="批量队列分页"
                >
                  <Button
                    disabled={windowStart === 0}
                    onClick={() =>
                      actions.run({
                        type: "batch.setWindow",
                        start: Math.max(0, windowStart - 40),
                      })
                    }
                  >
                    上一组
                  </Button>
                  <span>
                    {windowStart + 1}–
                    {Math.min(itemCount, windowStart + items.length)} /{" "}
                    {itemCount}
                  </span>
                  <Button
                    disabled={windowStart + items.length >= itemCount}
                    onClick={() =>
                      actions.run({
                        type: "batch.setWindow",
                        start: windowStart + 40,
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
        <Panel label="INSPECT" title="文件预览">
          <EmptyStage
            title="未选择文件"
            detail="从队列中选择一个项目以检查输入。"
          />
        </Panel>
        <Panel label="RESULT" title="识别结果">
          <EmptyStage title="尚无结果" detail="批处理完成后在此检查文本。" />
          <CapabilityGate
            capability="batch.export"
            capabilities={viewState.capabilities}
            action={{ type: "batch.exportAll", format: "markdown" }}
            actions={actions}
            icon={<Download aria-hidden="true" size={16} />}
            disabled={completed === 0}
          >
            导出全部 Markdown
          </CapabilityGate>
          <CapabilityGate
            capability="batch.export"
            capabilities={viewState.capabilities}
            action={{ type: "batch.exportAll", format: "docx" }}
            actions={actions}
            icon={<Sheet aria-hidden="true" size={16} />}
            disabled={completed === 0}
          >
            导出全部 Word
          </CapabilityGate>
          <CapabilityGate
            capability="batch.export"
            capabilities={viewState.capabilities}
            action={{ type: "batch.exportAll", format: "xlsx" }}
            actions={actions}
            icon={<FileSpreadsheet aria-hidden="true" size={16} />}
            disabled={completed === 0}
          >
            导出全部 Excel
          </CapabilityGate>
        </Panel>
      </div>
    </Workspace>
  );
}

export function PdfPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "pdf");
  const pageCount = numberValue(state.pageCount);
  const selectedPage = numberValue(state.selectedPage);
  const pages = pdfPages(state.pages);
  const windowStart = Math.max(0, numberValue(state.windowStart));
  const selectedPages = Array.isArray(state.selectedPages)
    ? state.selectedPages.filter(
        (page): page is number => typeof page === "number" && page >= 0,
      )
    : [];
  const selected = new Set(selectedPages);
  const activePage = pages.find((page) => page.index === selectedPage);
  return (
    <Workspace
      eyebrow="DOCUMENT / 03"
      title="PDF 工作台"
      description="选择页面后完成旋转、页面 OCR 与保存操作。"
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
            打开 PDF
          </CapabilityGate>
          <CapabilityGate
            capability="pdf.open"
            capabilities={viewState.capabilities}
            action={{ type: "pdf.close" }}
            actions={actions}
            disabled={pageCount === 0}
            icon={<X aria-hidden="true" size={16} />}
          >
            关闭文档
          </CapabilityGate>
        </>
      }
    >
      <div className="pdf-workspace">
        <Panel label="PAGES" title="页面">
          {pages.length === 0 ? (
            <EmptyStage
              title="尚未打开文档"
              detail="打开 PDF 后显示页面状态与多选项。"
            />
          ) : (
            <>
              <ol className="pdf-page-list" aria-label="PDF 页面缩略图">
                {pages.map((page) => {
                  const thumbnail = resource(page.thumbnail);
                  return (
                    <li
                      className={selected.has(page.index) ? "is-selected" : ""}
                      key={page.index}
                    >
                      <Checkbox
                        aria-label={`选择第 ${page.index + 1} 页`}
                        checked={selected.has(page.index)}
                        onChange={(_, data) => {
                          const next = new Set(selected);
                          if (data.checked === true) next.add(page.index);
                          else next.delete(page.index);
                          actions.run({
                            type: "pdf.selectPages",
                            pages: [...next].sort(
                              (left, right) => left - right,
                            ),
                          });
                        }}
                      />
                      {thumbnail ? (
                        <img
                          alt={`第 ${page.index + 1} 页缩略图`}
                          src={thumbnail.url}
                        />
                      ) : (
                        <span className="pdf-thumbnail-placeholder">PDF</span>
                      )}
                      <span>第 {page.index + 1} 页</span>
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
          <Toolbar aria-label="PDF 页面命令">
            <CapabilityGate
              capability="pdf.rotate"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.rotate", degrees: 90 }}
              actions={actions}
              icon={<RotateCw aria-hidden="true" size={16} />}
              disabled={selectedPages.length === 0}
            >
              顺时针 90°
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.deletePages" }}
              actions={actions}
              disabled={selectedPages.length === 0}
              icon={<Trash2 aria-hidden="true" size={16} />}
            >
              删除选中页
            </CapabilityGate>
            <CapabilityGate
              capability="pdf.edit"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.ocrPages" }}
              actions={actions}
              disabled={selectedPages.length === 0}
              icon={<ScanText aria-hidden="true" size={16} />}
            >
              OCR 选中页
            </CapabilityGate>
            <ToolbarDivider />
            <CapabilityGate
              capability="pdf.save"
              capabilities={viewState.capabilities}
              action={{ type: "pdf.save" }}
              actions={actions}
              disabled={pageCount === 0}
              icon={<Save aria-hidden="true" size={16} />}
            >
              保存
            </CapabilityGate>
          </Toolbar>
          <Panel label="REVIEW" title="页面检查">
            {resource(activePage?.thumbnail) ? (
              <img
                className="pdf-review-image"
                src={resource(activePage?.thumbnail)?.url}
                alt={`当前第 ${selectedPage + 1} 页`}
              />
            ) : (
              <EmptyStage
                title={pageCount > 0 ? `${pageCount} 页文档` : "文档检查区"}
                detail={
                  pageCount > 0
                    ? `已选 ${selectedPages.length} 页`
                    : "选择页面后显示渲染预览与 OCR 状态。"
                }
              />
            )}
          </Panel>
          <StatusLine>
            {statusLabel(state.statusCode, "尚未建立 PDF 会话。")}
          </StatusLine>
        </div>
      </div>
    </Workspace>
  );
}

export function QrCodePage({ viewState, actions }: FeatureProps) {
  const [tab, setTab] = useState<"generate" | "decode">("generate");
  const [qrText, setQrText] = useState("");
  const state = feature(viewState, "qrcode");
  const generated = resource(state.generatedResource);
  const results = qrResults(state.items);
  const isBusy = booleanValue(state.isBusy);
  return (
    <Workspace
      eyebrow="CODE / 04"
      title="二维码工作台"
      description="生成二维码，或从图片中读取二维码与条形码。"
    >
      <div className="qr-workspace">
        <Panel label={tab === "generate" ? "GENERATE" : "DECODE"} title="预览">
          {generated ? (
            <img
              className="qr-resource-preview"
              src={generated.url}
              alt="生成的二维码"
            />
          ) : (
            <EmptyStage
              title="等待输入"
              detail={
                tab === "generate"
                  ? "输入内容并生成后显示二维码。"
                  : "粘贴或选择图片后显示定位预览。"
              }
            />
          )}
        </Panel>
        <section className="side-form">
          <TabList
            selectedValue={tab}
            onTabSelect={(_, data) =>
              setTab(data.value as "generate" | "decode")
            }
            aria-label="二维码模式"
          >
            <Tab value="generate">生成</Tab>
            <Tab value="decode">识别</Tab>
          </TabList>
          {tab === "generate" ? (
            <div className="form-stack">
              <label htmlFor="qr-content">输入内容</label>
              <Input
                id="qr-content"
                placeholder="输入要编码的内容"
                value={qrText}
                onChange={(_, data) => setQrText(data.value)}
              />
              <CapabilityGate
                appearance="primary"
                capability="qrcode.generate"
                capabilities={viewState.capabilities}
                action={{ type: "qrcode.generate", text: qrText }}
                actions={actions}
                disabled={qrText.trim().length === 0 || isBusy}
                icon={<QrCode aria-hidden="true" size={16} />}
              >
                生成二维码
              </CapabilityGate>
              <CapabilityGate
                capability="qrcode.save"
                capabilities={viewState.capabilities}
                action={{ type: "qrcode.save" }}
                actions={actions}
                disabled={!generated}
                icon={<Save aria-hidden="true" size={16} />}
              >
                保存二维码
              </CapabilityGate>
              <p className="form-note">
                当前生成接口支持内容与编码格式；颜色、Logo 与标签需要
                Backend/Protocol 生成选项。
              </p>
            </div>
          ) : (
            <div className="form-stack">
              <CapabilityGate
                appearance="primary"
                capability="qrcode.decode"
                capabilities={viewState.capabilities}
                action={{ type: "qrcode.decode" }}
                actions={actions}
                disabled={isBusy}
                icon={<ScanText aria-hidden="true" size={16} />}
              >
                选择图片识别
              </CapabilityGate>
              <CapabilityGate
                capability="qrcode.clipboard"
                capabilities={viewState.capabilities}
                action={{ type: "qrcode.decodeClipboard" }}
                actions={actions}
                disabled={isBusy}
                icon={<ClipboardPaste aria-hidden="true" size={16} />}
              >
                粘贴图片
              </CapabilityGate>
              <CapabilityGate
                capability="qrcode.decode"
                capabilities={viewState.capabilities}
                action={{ type: "qrcode.clear" }}
                actions={actions}
                disabled={results.length === 0 && !generated}
                icon={<Trash2 aria-hidden="true" size={16} />}
              >
                清空结果
              </CapabilityGate>
              {results.length > 0 ? (
                <ul className="decoded-results">
                  {results.map((result, index) => (
                    <li key={`${result.format}-${index}`}>
                      <span className="qr-result-format">{result.format}</span>
                      <span>{result.data}</span>
                      {result.isUrl && (
                        <CapabilityGate
                          capability="qrcode.openUrl"
                          capabilities={viewState.capabilities}
                          action={{ type: "qrcode.openUrl", url: result.data }}
                          actions={actions}
                          icon={<ExternalLink aria-hidden="true" size={16} />}
                        >
                          打开链接
                        </CapabilityGate>
                      )}
                    </li>
                  ))}
                </ul>
              ) : (
                <EmptyStage
                  title="暂无识别结果"
                  detail="识别后会显示编码格式、内容和可打开的 URL。"
                />
              )}
            </div>
          )}
          <Button
            appearance="secondary"
            disabled={!isBusy}
            onClick={() => actions.run({ type: "qrcode.cancel" })}
            icon={<Square aria-hidden="true" size={16} />}
          >
            取消任务
          </Button>
          <StatusLine>
            {statusLabel(
              state.statusCode,
              isBusy ? "正在处理二维码…" : "二维码工作台已就绪。",
            )}
          </StatusLine>
        </section>
      </div>
    </Workspace>
  );
}

export function SettingsPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "settings");
  const hostHotkey =
    typeof state.pendingHotkey === "string" && state.pendingHotkey
      ? state.pendingHotkey
      : typeof state.hotkey === "string"
        ? state.hotkey
        : "Ctrl+Alt+Q";
  const backend =
    typeof state.backend === "string" ? state.backend : "等待宿主同步";
  const backendLabel =
    backend === "cpu" || backend === "nvidia_cuda"
      ? acceleratorLabel(backend)
      : backend;
  const maintenance = maintenanceState(state.maintenance);
  const busy = maintenance?.isRunning === true;
  const sources = sourceOptions(state.sources);
  const serviceText = stringValue(state.serviceStatus) ?? "等待宿主同步";
  const maintenanceParts = [
    stringValue(state.maintenanceStatus),
    stringValue(state.maintenancePhase),
  ].filter((part): part is string => part !== undefined);
  const maintenanceLine =
    maintenanceParts.length > 0
      ? maintenanceParts.join(" · ")
      : "尚未执行维护操作";
  const progressText = stringValue(state.progressText);
  return (
    <Workspace
      eyebrow="PREFERENCES / 05"
      title="设置"
      description="管理快捷操作、运行环境与下载来源。识别模式在对应任务中选择。"
    >
      <div className="settings-grid">
        <Panel
          label="APPLICATION"
          title="应用与快捷键"
          className="settings-app-panel"
        >
          <div className="setting-row">
            <Checkbox
              label="开机自启动"
              checked={booleanValue(state.startupEnabled)}
              disabled={!viewState.capabilities.includes("settings.shell")}
              onChange={(_, data) =>
                actions.run({
                  type: "settings.setStartup",
                  enabled: data.checked === true,
                })
              }
            />
          </div>
          <HotkeyEditor
            key={hostHotkey}
            initialHotkey={hostHotkey}
            enabled={viewState.capabilities.includes("settings.shell")}
            actions={actions}
          />
          <p role="status">
            {typeof state.hotkeyStatus === "string" ? state.hotkeyStatus : ""}
          </p>
          <p>
            当前生效：
            {typeof state.hotkey === "string" && state.hotkey
              ? state.hotkey
              : "未注册"}
          </p>
        </Panel>
        <Panel
          label="RUNTIME"
          title="运行环境"
          className="settings-runtime-panel"
        >
          <div className="runtime-summary">
            <strong>{backendLabel}</strong>
            <p>当前服务：{serviceText}</p>
            <p>本次维护：{maintenanceLine}</p>
            <ProgressBar
              value={
                typeof state.progressPercent === "number" &&
                Number.isFinite(state.progressPercent)
                  ? state.progressPercent / 100
                  : undefined
              }
              aria-label="运行环境维护进度"
            />
            {progressText ? <p>{progressText}</p> : null}
            {stringValue(state.progressDetail) ? (
              <details>
                <summary>进度明细</summary>
                <p>{stringValue(state.progressDetail)}</p>
              </details>
            ) : null}
          </div>
          <BundledCapabilities capabilities={viewState.capabilities} />
          <CapabilityGate
            capability="runtime.refresh"
            capabilities={viewState.capabilities}
            action={{ type: "settings.refreshRuntime" }}
            actions={actions}
            icon={<RefreshCw aria-hidden="true" size={16} />}
          >
            重新检查状态
          </CapabilityGate>
          <AcceleratorFeatures
            pendingBackend={stringValue(state.pendingBackend) ?? "cpu"}
            features={featureOptions(state.features)}
            enabled={viewState.capabilities.includes("settings.selection")}
            locked={busy}
            actions={actions}
          />
          <MaintenanceActions
            maintenance={maintenance}
            plan={installPlan(state.installPlan)}
            canPreview={state.canPreviewInstall === true}
            enabled={viewState.capabilities.includes("runtime.maintenance")}
            sources={sources}
            features={featureOptions(state.features)}
            actions={actions}
          />
          <p className="form-note">
            {stringValue(state.statusMessage) ??
              statusLabel(state.statusCode, "运行环境状态已同步。")}
          </p>
        </Panel>
        <Panel
          label="DOWNLOADS"
          title="下载来源"
          className="settings-download-panel"
        >
          <SourceSelector
            sources={sources}
            enabled={viewState.capabilities.includes("settings.selection")}
            locked={busy}
            actions={actions}
          />
          <p className="form-note">
            依赖包来源用于安装运行环境；模型原生来源由引擎使用。更换来源不会安装组件或提前准备模型。
          </p>
        </Panel>
      </div>
    </Workspace>
  );
}

function SourceSelector({
  sources,
  enabled,
  locked,
  actions,
}: {
  readonly sources: readonly SourceOptionState[];
  readonly enabled: boolean;
  readonly locked: boolean;
  readonly actions: AppActions;
}) {
  const packageSources = sources.filter(
    (source) => source.kind === "package_index",
  );
  const modelSources = sources.filter(
    (source) => source.kind === "model_registry",
  );
  if (packageSources.length === 0 && modelSources.length === 0) {
    return <p className="form-note">当前 Backend 未提供下载源目录。</p>;
  }
  return (
    <>
      <SourceKindSelector
        kind="package_index"
        label="Python 包下载源"
        sources={packageSources}
        enabled={enabled}
        locked={locked}
        actions={actions}
      />
      <SourceKindSelector
        kind="model_registry"
        label="模型下载源"
        sources={modelSources}
        enabled={enabled}
        locked={locked}
        actions={actions}
      />
    </>
  );
}

function SourceKindSelector({
  kind,
  label,
  sources,
  enabled,
  locked,
  actions,
}: {
  readonly kind: string;
  readonly label: string;
  readonly sources: readonly SourceOptionState[];
  readonly enabled: boolean;
  readonly locked: boolean;
  readonly actions: AppActions;
}) {
  if (sources.length === 0) return null;
  const selected = sources.find((source) => source.selected);
  return (
    <div className="setting-row">
      <label htmlFor={`source-${kind}`}>{label}</label>
      <Select
        id={`source-${kind}`}
        value={selected?.id ?? ""}
        disabled={!enabled || locked}
        onChange={(_, data) =>
          data.value === ""
            ? actions.run({ type: "settings.setSource", kind })
            : actions.run({
                type: "settings.setSource",
                kind,
                sourceId: String(data.value),
              })
        }
      >
        <option value="">跟随 Backend 默认</option>
        {sources.map((source) => (
          <option key={source.id} value={source.id}>
            {source.displayName}
          </option>
        ))}
      </Select>
    </div>
  );
}

function AcceleratorFeatures({
  pendingBackend,
  features,
  enabled,
  locked,
  actions,
}: {
  readonly pendingBackend: string;
  readonly features: readonly FeatureOptionState[];
  readonly enabled: boolean;
  readonly locked: boolean;
  readonly actions: AppActions;
}) {
  return (
    <>
      <div className="setting-row">
        <label htmlFor="accelerator">推理设备</label>
        <Select
          id="accelerator"
          value={pendingBackend}
          disabled={!enabled || locked}
          onChange={(_, data) =>
            actions.run({
              type: "settings.setAccelerator",
              accelerator: String(data.value),
            })
          }
        >
          <option value="cpu">CPU</option>
          <option value="nvidia_cuda">CUDA GPU</option>
        </Select>
      </div>
      <p className="form-note">
        推理设备只决定运行环境的安装目标；选择 GPU
        不代表识别引擎已安装，各引擎可用性在识别任务中选择时显示。
      </p>
      {features.length > 0 ? (
        features.map((feature) => (
          <div className="setting-row" key={feature.featureId}>
            <Checkbox
              label={feature.displayName}
              checked={feature.selected}
              disabled={!enabled || locked}
              onChange={(_, data) =>
                actions.run({
                  type: "settings.setFeature",
                  featureId: feature.featureId,
                  enabled: data.checked === true,
                })
              }
            />
            <Badge appearance="outline">
              {acceleratorLabel(feature.accelerator)}
            </Badge>
          </div>
        ))
      ) : (
        <p className="form-note">
          当前推理设备没有需要额外勾选的组件；这不代表识别引擎已全部就绪。
        </p>
      )}
    </>
  );
}

function MaintenanceActions({
  maintenance,
  plan,
  canPreview,
  enabled,
  sources,
  features,
  actions,
}: {
  readonly maintenance: MaintenanceState | undefined;
  readonly plan: InstallPlanState | undefined;
  readonly canPreview: boolean;
  readonly enabled: boolean;
  readonly sources: readonly SourceOptionState[];
  readonly features: readonly FeatureOptionState[];
  readonly actions: AppActions;
}) {
  const busy = maintenance?.isRunning === true;
  return (
    <>
      <div className="setting-row">
        <Button
          disabled={!enabled || busy || !canPreview}
          onClick={() => actions.run({ type: "settings.installRuntime" })}
          icon={<Play aria-hidden="true" size={16} />}
        >
          预览安装范围
        </Button>
        {maintenance?.canCancel === true ? (
          <Button
            disabled={!enabled}
            onClick={() =>
              actions.run({ type: "settings.cancelRuntimeMaintenance" })
            }
            icon={<Square aria-hidden="true" size={16} />}
          >
            取消安装
          </Button>
        ) : null}
        {maintenance?.canRetry === true ? (
          <Button
            disabled={!enabled || busy}
            onClick={() =>
              actions.run({ type: "settings.retryRuntimeMaintenance" })
            }
            icon={<RefreshCw aria-hidden="true" size={16} />}
          >
            重新预览上次选择
          </Button>
        ) : null}
      </div>
      {busy ? (
        <p className="form-note">
          维护进行中：推理设备、组件与下载来源的修改和确认已暂停。
        </p>
      ) : null}
      {!canPreview ? (
        <p className="form-note">
          当前运行环境不支持安装预览，请先更新运行环境。
        </p>
      ) : null}
      {plan ? (
        <InstallPlanSection
          plan={plan}
          sources={sources}
          features={features}
          confirmDisabled={
            !enabled || busy || !canPreview || plan.blockers.length > 0
          }
          actions={actions}
        />
      ) : null}
      {maintenance ? (
        <>
          <p className="form-note">
            {maintenanceStatusLabel(maintenance.statusCode)}
          </p>
          {maintenance.failureReason ? (
            <p role="alert">{maintenance.failureReason}</p>
          ) : null}
          {maintenance.failureCode ? (
            <details>
              <summary>维护技术详情</summary>
              <p>{maintenance.failureCode}</p>
            </details>
          ) : null}
        </>
      ) : null}
    </>
  );
}

function displayNameFor(
  id: string,
  features: readonly FeatureOptionState[],
): string {
  return (
    features.find((feature) => feature.featureId === id)?.displayName ?? id
  );
}

function sourceNameFor(
  id: string,
  sources: readonly SourceOptionState[],
): string {
  return sources.find((source) => source.id === id)?.displayName ?? id;
}

function InstallPlanSection({
  plan,
  sources,
  features,
  confirmDisabled,
  actions,
}: {
  readonly plan: InstallPlanState;
  readonly sources: readonly SourceOptionState[];
  readonly features: readonly FeatureOptionState[];
  readonly confirmDisabled: boolean;
  readonly actions: AppActions;
}) {
  const [clock, setClock] = useState(Date.now);
  useEffect(() => {
    const expiry = Date.parse(plan.expiresAt);
    if (!Number.isFinite(expiry) || expiry <= Date.now()) return;
    const timer = window.setTimeout(
      () => setClock(Date.now()),
      Math.min(Math.max(0, expiry - Date.now() + 1), 2_147_483_647),
    );
    return () => window.clearTimeout(timer);
  }, [plan.expiresAt, clock]);
  const expired = !isPlanFresh(plan.expiresAt);
  const unknownCodes: string[] = [];
  const componentLines = plan.components.map((component) => {
    const reasons = knownLabels(
      component.reasonCodes,
      installReasonLabels,
      unknownCodes,
    );
    const parts = [
      installActionLabels[component.action] ?? component.action,
      dependencyStateLabels[component.dependencyState] ??
        component.dependencyState,
      ...reasons,
    ];
    return `${displayNameFor(component.componentId, features)}：${parts.join("；")}`;
  });
  const sourceNames = plan.effectiveDownloadSourceIds.map((id) =>
    sourceNameFor(id, sources),
  );
  const knownUnknownCosts = knownLabels(
    plan.cost.unknownReasonCodes,
    unknownCostLabels,
    unknownCodes,
  );
  const downloadText =
    plan.cost.downloadBytes === null
      ? "未知"
      : formatBytes(plan.cost.downloadBytes);
  const diskText =
    plan.cost.additionalDiskBytes === null
      ? "未知"
      : formatBytes(plan.cost.additionalDiskBytes);
  const blockerCodes = plan.blockers.map((blocker) => blocker.code);
  return (
    <section className="install-plan" aria-label="运行环境安装计划">
      <h3>核对安装范围</h3>
      <p className="form-note">
        推理设备：{plan.accelerator}。以下范围由 Backend
        预览，确认前不会修改本机组件或停止当前服务。
      </p>
      <ul className="plan-components">
        {componentLines.map((line, index) => (
          <li key={plan.components[index]?.componentId ?? index}>{line}</li>
        ))}
      </ul>
      <p className="form-note">
        下载来源：
        {sourceNames.length > 0 ? sourceNames.join("、") : "跟随 Backend 默认"}
      </p>
      <p className="form-note">
        预计下载 {downloadText}；新增磁盘占用 {diskText}。
      </p>
      {knownUnknownCosts.length > 0 ? (
        <ul className="plan-components">
          {knownUnknownCosts.map((reason) => (
            <li key={reason}>{reason}</li>
          ))}
        </ul>
      ) : null}
      {plan.blockers.map((blocker, index) => (
        <p
          key={`${blocker.code}-${blocker.componentId ?? index}`}
          role="alert"
          className="form-note"
        >
          无法安装
          {blocker.componentId
            ? ` ${displayNameFor(blocker.componentId, features)}`
            : ""}
          ：{blocker.nextAction}
        </p>
      ))}
      {expired ? (
        <p role="alert" className="form-note">
          安装计划已过期，请重新预览后确认。
        </p>
      ) : null}
      <Button
        disabled={confirmDisabled || expired}
        onClick={() =>
          actions.run({
            type: "settings.confirmRuntimeInstall",
            planId: plan.planId,
          })
        }
      >
        确认此计划并安装
      </Button>
      {unknownCodes.length > 0 || blockerCodes.length > 0 ? (
        <details className="plan-tech-details">
          <summary>技术详情</summary>
          <ul>
            {[...new Set([...unknownCodes, ...blockerCodes])].map((code) => (
              <li key={code}>{code}</li>
            ))}
          </ul>
        </details>
      ) : null}
    </section>
  );
}

function BundledCapabilities({
  capabilities,
}: {
  readonly capabilities: readonly string[];
}) {
  const qrReady =
    capabilities.includes("qrcode.generate") &&
    capabilities.includes("qrcode.decode");
  return (
    <div className="bundled-capabilities" aria-label="随包基础能力">
      <span className="bundled-capabilities-title">随包基础能力</span>
      <div className="bundled-capability-row">
        <div>
          <strong>二维码与条形码</strong>
          <p className="form-note">
            生成与识别独立运行，可从二维码工具直接使用。
          </p>
        </div>
        <Badge appearance="outline">
          {qrReady ? "随包可用" : "当前运行环境不可用"}
        </Badge>
      </div>
    </div>
  );
}

function TaskEngineSelector({
  engines,
  taskEngine,
  enabled,
  actions,
}: {
  readonly engines: readonly RecognitionEngineState[];
  readonly taskEngine: string | undefined;
  readonly enabled: boolean;
  readonly actions: AppActions;
}) {
  if (engines.length === 0) {
    return null;
  }
  const active =
    engines.find((engine) => engine.isTaskOverride) ??
    engines.find((engine) => engine.selected);
  return (
    <div className="setting-row">
      <label htmlFor="task-engine">本次识别模式</label>
      <Select
        id="task-engine"
        value={taskEngine ?? ""}
        disabled={!enabled}
        onChange={(_, data) =>
          data.value === ""
            ? actions.run({ type: "recognition.setTaskEngine" })
            : actions.run({
                type: "recognition.setTaskEngine",
                engine: String(data.value),
              })
        }
      >
        <option value="">使用 Runtime 默认模式</option>
        {engines.map((engine) => (
          <option key={engine.engine} value={engine.engine}>
            {`${engine.displayName}（${availabilityLabel(engine.availability)}${
              engine.requiresDownload ? "，需下载" : ""
            }）`}
          </option>
        ))}
      </Select>
      {lifecycleHint(active) ? (
        <p className="form-note">{lifecycleHint(active)}</p>
      ) : null}
    </div>
  );
}

function HotkeyEditor({
  initialHotkey,
  enabled,
  actions,
}: {
  readonly initialHotkey: string;
  readonly enabled: boolean;
  readonly actions: AppActions;
}) {
  const [hotkey, setHotkey] = useState(initialHotkey);
  return (
    <div className="setting-row">
      <label htmlFor="hotkey">截图快捷键</label>
      <Input
        id="hotkey"
        value={hotkey}
        onChange={(_, data) => setHotkey(data.value)}
      />
      <Button
        disabled={!enabled}
        onClick={() => actions.run({ type: "settings.setHotkey", hotkey })}
        icon={<Save aria-hidden="true" size={16} />}
      >
        应用
      </Button>
    </div>
  );
}

export function AboutPage({ viewState, actions }: FeatureProps) {
  const update = feature(viewState, "update");
  const about = feature(viewState, "about");
  const version =
    typeof about.version === "string" ? about.version : "等待宿主同步";
  const license =
    typeof about.license === "string" ? about.license : "等待宿主同步";
  const projectUrl =
    typeof about.projectUrl === "string" ? about.projectUrl : "";
  return (
    <Workspace
      eyebrow="ABOUT / 06"
      title="关于 VibeOCR"
      description="本地 OCR、PDF 处理与二维码工具。"
    >
      <div className="about-grid">
        <Panel label="PRODUCT" title="VibeOCR">
          <p>基于 PaddleOCR 的 Windows 本地处理工作台。</p>
          <dl className="detail-list">
            <dt>版本</dt>
            <dd>{version}</dd>
            <dt>许可</dt>
            <dd>{license}</dd>
            <dt>技术栈</dt>
            <dd>.NET · WinUI · WebView2 · React</dd>
            <dt>项目</dt>
            <dd>{projectUrl || "由宿主提供"}</dd>
          </dl>
          <CapabilityGate
            capability="about.openProject"
            capabilities={viewState.capabilities}
            action={{ type: "about.openProject" }}
            actions={actions}
            icon={<ExternalLink aria-hidden="true" size={16} />}
          >
            打开项目主页
          </CapabilityGate>
        </Panel>
        <Panel label="UPDATE" title="更新">
          <p className="form-note">
            {statusLabel(update.statusCode, "更新状态等待宿主连接。")}
            {typeof update.latestVersion === "string"
              ? ` · 最新版本 ${update.latestVersion}`
              : ""}
          </p>
          <CapabilityGate
            capability="update.check"
            capabilities={viewState.capabilities}
            action={{ type: "update.check" }}
            actions={actions}
            icon={<RefreshCw aria-hidden="true" size={16} />}
          >
            检查更新
          </CapabilityGate>
          <CapabilityGate
            appearance="primary"
            capability="update.install"
            capabilities={viewState.capabilities}
            action={{ type: "update.download" }}
            actions={actions}
            disabled={!booleanValue(update.updateAvailable)}
            icon={<Download aria-hidden="true" size={16} />}
          >
            下载并安装
          </CapabilityGate>
          <CapabilityGate
            capability="update.install"
            capabilities={viewState.capabilities}
            action={{ type: "update.cancel" }}
            actions={actions}
            disabled={!booleanValue(update.isBusy)}
            icon={<Square aria-hidden="true" size={16} />}
          >
            取消
          </CapabilityGate>
          {booleanValue(update.canCancelRuntimeMaintenance) ? (
            <CapabilityGate
              capability="update.install"
              capabilities={viewState.capabilities}
              action={{ type: "update.cancelRuntimeMaintenance" }}
              actions={actions}
              icon={<Square aria-hidden="true" size={16} />}
            >
              取消运行时维护后更新
            </CapabilityGate>
          ) : null}
        </Panel>
      </div>
    </Workspace>
  );
}

export function DiagnosticsPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "diagnostics");
  const milestones = stringValues(state.milestones);
  return (
    <Workspace
      eyebrow="SUPPORT / 07"
      title="诊断与修复"
      description="检查本机运行时、依赖与启动健康状态。"
      actions={
        <CapabilityGate
          capability="diagnostics.export"
          capabilities={viewState.capabilities}
          action={{ type: "diagnostics.export" }}
          actions={actions}
          icon={<Download aria-hidden="true" size={16} />}
        >
          导出脱敏诊断
        </CapabilityGate>
      }
    >
      <div className="diagnostics-grid">
        <Panel label="HEALTH" title="运行时">
          <div className="health-row">
            <span>Supervisor</span>
            <Badge appearance="tint">
              {typeof state.supervisorStatus === "string"
                ? state.supervisorStatus
                : "等待连接"}
            </Badge>
          </div>
          <div className="health-row">
            <span>Protocol</span>
            <Badge appearance="tint">
              {typeof state.protocolStatus === "string"
                ? state.protocolStatus
                : "等待连接"}
            </Badge>
          </div>
        </Panel>
        <Panel label="MILESTONES" title="启动里程碑">
          {milestones.length > 0 ? (
            <ol className="milestone-list">
              {milestones.map((milestone) => (
                <li key={milestone}>{milestone}</li>
              ))}
            </ol>
          ) : (
            <EmptyStage
              title="没有诊断快照"
              detail="宿主连接后显示 T0–T6 的启动耗时与修复入口。"
            />
          )}
        </Panel>
      </div>
    </Workspace>
  );
}
