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
  Aperture,
  Camera,
  ClipboardPaste,
  Copy,
  Download,
  ExternalLink,
  Eye,
  EyeOff,
  FileSpreadsheet,
  FilePlus2,
  FolderOpen,
  ImagePlus,
  Play,
  QrCode,
  RefreshCw,
  RotateCcw,
  RotateCw,
  Save,
  ScanText,
  Sheet,
  Square,
  Trash2,
  X,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";

import type { AppActions, AppViewState } from "../app/types";
import { CapabilityGate } from "../components/CapabilityGate";
import { HotkeyRecorder } from "../components/HotkeyRecorder";
import { ImageCanvasEditor } from "../components/ImageCanvasEditor";
import { PaddleOptionsEditor } from "../components/PaddleOptionsEditor";
import { StructuredResult } from "../components/StructuredResult";
import type { ScreenshotTextLayerState } from "../components/ImageCanvasEditor";
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
  readonly structuredResult?: ResourceReference | null;
}

interface PdfPageState {
  readonly index: number;
  readonly statusCode: string;
  readonly thumbnail?: ResourceReference | null;
  readonly structuredResult?: ResourceReference | null;
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

interface ManagedEnvironmentState {
  readonly id: string;
  readonly name: string;
  readonly revision: number;
  readonly kind: string;
  readonly status: string;
  readonly pythonState: string;
  readonly dependencyState: string;
  readonly engineState: string;
  readonly modelState: string;
  readonly serviceState: string;
  readonly configuredRecognitionTypes: readonly string[];
  readonly targetDevice?: string | null;
  readonly actualDevice?: string | null;
  readonly reason?: string | null;
  readonly sourceIds?: readonly string[] | null;
  readonly overrideSourceIds?: readonly string[] | null;
  readonly unknownSourceIds?: readonly string[] | null;
  readonly resolvedSources?: readonly ManagedResolvedSourceState[] | null;
  readonly lastInstallFailure?: {
    readonly phase: string;
    readonly environmentRevision: number;
    readonly recipe: string;
    readonly reasonCode: string;
    readonly nextAction: string;
    readonly detail: string;
    readonly requestedSourceIds?: readonly string[] | null;
    readonly effectiveSourceIds?: readonly string[] | null;
  } | null;
  readonly pythonVersion?: string | null;
  readonly abi?: string | null;
  readonly python?: string | null;
  readonly path?: string | null;
  readonly diskBytes?: number;
}

interface ManagedSourceOptionState {
  readonly id: string;
  readonly kind: string;
  readonly displayName: string;
  readonly endpoint: string;
}

interface ManagedResolvedSourceState {
  readonly kind: string;
  readonly id: string | null;
  readonly displayName: string | null;
  readonly origin: string;
}

interface ManagedPlanSourceState {
  readonly id: string;
  readonly kind: string;
  readonly displayName: string;
  readonly endpoint: string;
  readonly requested: boolean;
  readonly inheritedFrom: string;
  readonly usage: string;
  readonly actualEndpoint?: string | null;
}

interface ManagedEnvironmentPlanState {
  readonly planId: string;
  readonly environmentId: string;
  readonly environmentRevision: number;
  readonly recipe: string;
  readonly requestedRecipe: string;
  readonly sourceIds: readonly string[];
  readonly dependencies: readonly string[];
  readonly requestedSourceIds?: readonly string[] | null;
  readonly sources?: readonly ManagedPlanSourceState[] | null;
  readonly dependencyOrigin?: string | null;
  readonly pythonOrigin?: string | null;
  readonly runtimeWheelOrigin?: string | null;
}

const sourceOriginLabels: Readonly<Record<string, string>> = {
  product_default: "产品默认",
  global_default: "全局默认",
  environment_override: "本环境覆盖",
};

const sourceUsageLabels: Readonly<Record<string, string>> = {
  online_index: "在线索引（哈希锁定）",
  bundled_pack: "随包离线包",
  model_preference: "模型偏好（实际下载端点未知）",
};

function sourceKindLabel(kind: string): string {
  return kind === "model_registry" ? "模型来源" : "依赖包来源";
}

const environmentRecipes = [
  ["rapidocr-cpu", "RapidOCR · CPU"],
  ["paddleocr-cpu", "PaddleOCR · CPU"],
  ["paddleocr-cuda", "PaddleOCR · NVIDIA CUDA"],
  ["mineru-cpu", "MinerU · CPU"],
  ["rapidocr+mineru-cpu", "RapidOCR + MinerU · CPU"],
  ["rapidocr+mineru-cuda", "RapidOCR + MinerU · NVIDIA CUDA"],
] as const;

const environmentRecoveryActions: Readonly<Record<string, string>> = {
  check_source_and_retry: "检查下载源与网络后重新预览安装",
  free_disk_space: "释放磁盘空间后重新预览安装",
  check_directory_permissions: "检查环境目录权限后重试",
  repair_environment: "检查便携版路径长度或重建该环境",
  preview_again: "重新预览依赖后重试",
  inspect_diagnostics: "查看诊断日志后重试",
  wait: "等待当前安装完成",
};

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
  readonly supportedOptions?: readonly string[];
  readonly options?: unknown;
  readonly reasonCode?: string | null;
}

interface ScreenshotSessionState {
  readonly sessionId: string;
  readonly revision: number;
  readonly textSelectionRequested: boolean;
}

// 宿主回显的活动纯截图会话：会话 id + 当前内容修订。
function screenshotSession(value: unknown): ScreenshotSessionState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<ScreenshotSessionState>;
  return typeof candidate.sessionId === "string" &&
    candidate.sessionId.length > 0 &&
    typeof candidate.revision === "number" &&
    Number.isSafeInteger(candidate.revision) &&
    candidate.revision >= 0
    ? {
        sessionId: candidate.sessionId,
        revision: candidate.revision,
        textSelectionRequested: candidate.textSelectionRequested === true,
      }
    : undefined;
}

interface TextLayerLineState {
  readonly text: string;
  readonly x1: number;
  readonly y1: number;
  readonly x2: number;
  readonly y2: number;
  readonly order?: number | null;
}

function textLayerLines(value: unknown): readonly TextLayerLineState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is TextLayerLineState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const line = entry as Partial<TextLayerLineState>;
    return (
      typeof line.text === "string" &&
      line.text.length > 0 &&
      [line.x1, line.y1, line.x2, line.y2].every(
        (coordinate) =>
          typeof coordinate === "number" && Number.isFinite(coordinate),
      )
    );
  });
}

// 宿主回发的原位文字层状态；字段不完整时按无层处理。
function screenshotTextLayer(
  value: unknown,
): ScreenshotTextLayerState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<ScreenshotTextLayerState>;
  if (typeof candidate.status !== "string" || candidate.status.length === 0)
    return undefined;
  const binding = screenshotSession(candidate.binding);
  const image = resource(candidate.image);
  return {
    status: candidate.status,
    reason: typeof candidate.reason === "string" ? candidate.reason : undefined,
    binding,
    modeId: typeof candidate.modeId === "string" ? candidate.modeId : null,
    image,
    lines: candidate.lines === null ? null : textLayerLines(candidate.lines),
  };
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
        typeof item.resultSummary === "string") &&
      (item.structuredResult === undefined ||
        item.structuredResult === null ||
        resource(item.structuredResult) !== undefined)
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
      typeof page.index === "number" &&
      typeof page.statusCode === "string" &&
      (page.structuredResult === undefined ||
        page.structuredResult === null ||
        resource(page.structuredResult) !== undefined)
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

interface HotkeyActionOptionState {
  readonly actionId: string;
  readonly displayName: string;
  readonly configuredHotkey: string | null;
  readonly registeredHotkey: string | null;
  readonly error: string | null;
  readonly defaultHotkey: string | null;
}

// 宿主回显的动作键位：configured 是配置值，registered 是实际生效的
// 系统注册（为空表示被占用/未注册），二者可能不同。
function hotkeyActions(value: unknown): readonly HotkeyActionOptionState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is HotkeyActionOptionState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const option = entry as Partial<HotkeyActionOptionState>;
    return (
      typeof option.actionId === "string" &&
      typeof option.displayName === "string" &&
      (option.configuredHotkey === null ||
        typeof option.configuredHotkey === "string") &&
      (option.registeredHotkey === null ||
        typeof option.registeredHotkey === "string") &&
      (option.error === null || typeof option.error === "string") &&
      (option.defaultHotkey === null ||
        typeof option.defaultHotkey === "string")
    );
  });
}

interface FloatingToolbarOptionState {
  readonly enabled: boolean;
  readonly edge: string;
  readonly autoHide: boolean;
  readonly visibility: string;
  readonly error?: string | null;
}

function toolbarState(value: unknown): FloatingToolbarOptionState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const option = value as Partial<FloatingToolbarOptionState>;
  return typeof option.enabled === "boolean" &&
    typeof option.edge === "string" &&
    typeof option.autoHide === "boolean" &&
    typeof option.visibility === "string" &&
    (option.error === undefined ||
      option.error === null ||
      typeof option.error === "string")
    ? (option as FloatingToolbarOptionState)
    : undefined;
}

const TOOLBAR_EDGES = ["top", "bottom", "left", "right"] as const;

const TOOLBAR_EDGE_LABELS: Readonly<Record<string, string>> = {
  top: "顶部",
  bottom: "底部",
  left: "左侧",
  right: "右侧",
};

function toolbarVisibilityLabel(visibility: string): string {
  const labels: Readonly<Record<string, string>> = {
    disabled: "已关闭",
    userHidden: "已主动隐藏（鼠标路过不恢复）",
    edgeHidden: "已靠边收起，悬停边缘可唤出",
    visible: "显示中",
    suspended: "截图避让中，结束后恢复原状",
  };
  return labels[visibility] ?? "等待宿主同步";
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

interface MineruConnectionState {
  readonly supported: boolean;
  readonly mode: string;
  readonly apiUrl: string;
  readonly hasApiKey: boolean;
}

// MinerU 连接投影：宿主只回传模式/根地址/是否已配置 Key，永远不回传明文。
function mineruConnection(value: unknown): MineruConnectionState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<MineruConnectionState>;
  return typeof candidate.supported === "boolean" &&
    typeof candidate.mode === "string" &&
    typeof candidate.apiUrl === "string" &&
    typeof candidate.hasApiKey === "boolean"
    ? {
        supported: candidate.supported,
        mode: candidate.mode,
        apiUrl: candidate.apiUrl,
        hasApiKey: candidate.hasApiKey,
      }
    : undefined;
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
      ? "本地 MinerU 使用进程保活；当前目录未声明可用控制。远程模型生命周期由服务端管理。"
      : `本地 MinerU 使用进程保活；仅支持：${controls.join("、")}。远程模型生命周期由服务端管理。`;
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
    "recognition.session": "已捕获截图；可编辑标注、复制保存或显式识别当前图",
    "recognition.expired": "内容已修改，旧识别结果已失效；请重新识别当前图",
    "recognition.failed": "识别失败，请检查运行时状态后重试",
    "recognition.cancelled": "识别已取消，可重新选择输入",
    "recognition.modeUnavailable":
      "所选识别模式在当前环境不可用；请检查模型、设备与运行环境",
    "recognition.exported": "结果已导出",
    "recognition.exportedIncomplete":
      "文件已保存，但部分图片缺失或该格式无法容纳图片；请检查原始结果",
    "pdf.open": "PDF 会话已建立",
    "pdf.empty": "尚未建立 PDF 会话",
    "pdf.failed": "PDF 操作失败，请检查文件或重试",
    "pdf.backendUnavailable": "识别服务暂不可用，请检查运行时状态后重试",
    "pdf.outOfMemory": "内存或显存不足，请减少页数或关闭其他任务后重试",
    "pdf.cancelled": "PDF 操作已取消，可重新操作",
    "qrcode.decoded": "识别完成",
    "qrcode.ready": "等待输入",
    "qrcode.running": "正在处理二维码…",
    "qrcode.failed": "图片识别失败，请检查输入图片和识别运行环境后重试",
    "qrcode.generateFailed": "二维码生成失败，请重试",
    "qrcode.invalidInput": "内容无法编码为二维码，请检查文本或缩短内容后重试。",
    "qrcode.decodeUnavailable": "图片识别需要识别运行环境，请启动或恢复后重试",
    "qrcode.cancelled": "二维码处理已取消",
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
  // The host owns each capture's intent; this user preference survives keyed
  // editor remounts and only applies to sessions explicitly captured for text.
  const [autoTextPreference, setAutoTextPreference] = useState(true);
  const state = feature(viewState, "recognition");
  const input = resource(state.input);
  const result = resource(state.result);
  const resultText = useResourceText(result);
  const structured = resource(state.structuredResult);
  const structuredText = useResourceText(structured);
  const session = screenshotSession(state.screenshotSession);
  const textLayer = screenshotTextLayer(state.textLayer);
  const sessionCapable = viewState.capabilities.includes(
    "recognition.screenshotSession",
  );
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
          {sessionCapable && (
            <CapabilityGate
              appearance="secondary"
              capability="recognition.screenshotSession"
              capabilities={viewState.capabilities}
              action={{ type: "recognition.captureScreenshotSession" }}
              actions={actions}
              icon={<Aperture aria-hidden="true" size={16} />}
            >
              纯截图
            </CapabilityGate>
          )}
          {viewState.capabilities.includes("recognition.scrollCapture") && (
            <CapabilityGate
              appearance="secondary"
              capability="recognition.scrollCapture"
              capabilities={viewState.capabilities}
              action={{ type: "recognition.captureScrollingScreenshot" }}
              actions={actions}
              icon={<ArrowDown aria-hidden="true" size={16} />}
            >
              长截图
            </CapabilityGate>
          )}
          {sessionCapable && (
            <CapabilityGate
              appearance="secondary"
              capability="recognition.screenshotSession"
              capabilities={viewState.capabilities}
              action={{ type: "recognition.captureScreenshotTextSession" }}
              actions={actions}
              icon={<ScanText aria-hidden="true" size={16} />}
            >
              截图取字
            </CapabilityGate>
          )}
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
              key={session ? `session-${session.sessionId}` : undefined}
              actions={actions}
              canExport={viewState.capabilities.includes(
                "recognition.annotation",
              )}
              canRecognize={sessionCapable}
              session={session}
              source={input.url}
              textLayer={textLayer}
              autoText={
                session?.textSelectionRequested === true && autoTextPreference
              }
              showAutoTextPreference={session?.textSelectionRequested === true}
              onAutoTextChange={setAutoTextPreference}
            />
          ) : (
            <EmptyStage
              title="等待图片"
              detail="选择、粘贴、截图或直接拖入图片"
            />
          )}
        </Panel>
        <Panel label="OUTPUT / 02" title="识别结果">
          {result || structured ? (
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
              {structured && (
                <StructuredResult
                  key={structured.url}
                  source={structuredText}
                  onCopy={(blockIndex, format) =>
                    actions.run({
                      type: "recognition.copyStructured",
                      resourceUri: structured.url,
                      blockIndex,
                      format,
                    })
                  }
                />
              )}
              {result && (
                <details open={!structured}>
                  <summary>原始文本</summary>
                  <pre className="result-document">
                    {resultText || "正在读取结果…"}
                  </pre>
                </details>
              )}
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
  const engines = recognitionEngines(state.engines);
  const exportIncomplete = booleanValue(state.exportIncomplete);
  // 单选待检查项：仅挂载所选结构化结果，避免窗口内几十项同时拉取。
  const [inspectId, setInspectId] = useState<string | null>(null);
  const inspected = items.find((item) => item.id === inspectId);
  const inspectedStructured = resource(inspected?.structuredResult);
  const inspectedText = useResourceText(inspectedStructured);
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
      <TaskEngineSelector
        engines={engines}
        taskEngine={stringValue(state.taskEngine)}
        enabled={
          viewState.capabilities.includes("recognition.engine") && !running
        }
        actions={actions}
        scope="batch"
      />
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
                      {resource(item.structuredResult) && (
                        <Button
                          appearance="subtle"
                          aria-label={`查看 ${item.name} 的结构化结果`}
                          onClick={() => setInspectId(item.id)}
                        >
                          <ScanText aria-hidden="true" size={15} />
                        </Button>
                      )}
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
          {inspected && inspectedStructured ? (
            <StructuredResult
              key={inspectedStructured.url}
              source={inspectedText}
              onCopy={(blockIndex, format) =>
                actions.run({
                  type: "recognition.copyStructured",
                  resourceUri: inspectedStructured.url,
                  blockIndex,
                  format,
                })
              }
            />
          ) : (
            <EmptyStage
              title="未选择文件"
              detail="从队列中选择带结构化结果的项目，检查表格、公式与版面区块。"
            />
          )}
        </Panel>
        <Panel label="RESULT" title="识别结果">
          {exportIncomplete && (
            <p role="alert" className="form-note">
              文件已保存，但部分图片缺失或目标格式无法容纳图片；请检查原始结果。
            </p>
          )}
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
  const engines = recognitionEngines(state.engines);
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
  const activeStructured = resource(activePage?.structuredResult);
  const activeStructuredText = useResourceText(activeStructured);
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
            {activeStructured && (
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
                二维码在本机直接生成，无需启动识别运行环境。
              </p>
            </div>
          ) : (
            <div className="form-stack">
              <p className="form-note">图片识别需要识别运行环境就绪。</p>
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
  // backend 是运行时 profile 声明的目标加速器，不是实测执行设备；
  // 未读取真实快照前不冒充任何设备。
  const backend = typeof state.backend === "string" ? state.backend : "";
  const backendLabel =
    backend === "cpu" || backend === "nvidia_cuda"
      ? acceleratorLabel(backend)
      : null;
  const maintenance = maintenanceState(state.maintenance);
  const busy = maintenance?.isRunning === true;
  const sources = sourceOptions(state.sources);
  const statusMessage =
    typeof state.statusMessage === "string" ? state.statusMessage : "";
  // 空下载源列表有两种含义：宿主目录尚未加载与 Backend 确未提供；
  // 初始/读取中的状态不得误报为 Backend 决策。
  const sourcesLoaded =
    sources.length > 0 ||
    (booleanValue(state.isBusy) !== true &&
      statusMessage !== "" &&
      statusMessage !== "正在读取设置" &&
      statusMessage !== "正在读取模型驻留状态");
  // 挂载后自动加载一次运行时快照：冷启动 bootstrap 只尽力加载目录，
  // 状态/来源以一次显式刷新为准（与手动“重新检查状态”同一命令）。
  // StrictMode 双挂载与宿主广播重放都不得重复派发。
  const autoRefreshed = useRef(false);
  useEffect(() => {
    if (autoRefreshed.current) return;
    if (!viewState.connected || busy) return;
    if (!viewState.capabilities.includes("runtime.refresh")) return;
    autoRefreshed.current = true;
    void actions.run({ type: "settings.refreshRuntime" });
  }, [actions, busy, viewState.capabilities, viewState.connected]);
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
  const mineru = mineruConnection(state.mineruConnection);
  return (
    <Workspace
      eyebrow="PREFERENCES / 05"
      title="设置"
      description="管理快捷操作、运行环境与下载来源。识别模式在对应任务中选择。"
    >
      <div className="settings-grid">
        <Panel label="APPLICATION" title="应用" className="settings-app-panel">
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
        </Panel>
        <Panel
          label="SHORTCUTS"
          title="快捷操作"
          className="settings-shortcuts-panel"
        >
          <p className="form-note">
            全局快捷键在系统任意位置可用，必须包含 Ctrl、Alt 或 Win
            修饰键，在输入框打字不会误触。修改会先注册新键、保存成功后才替换旧键；冲突或保存失败时原键继续生效。
          </p>
          <HotkeyActions
            actions={hotkeyActions(state.hotkeyActions)}
            enabled={viewState.capabilities.includes("settings.hotkeys")}
            dispatch={actions}
          />
        </Panel>
        <Panel
          label="TOOLBAR"
          title="悬浮工具栏"
          className="settings-toolbar-panel"
        >
          <FloatingToolbarPanel
            toolbar={toolbarState(state.floatingToolbar)}
            enabled={viewState.capabilities.includes(
              "settings.floatingToolbar",
            )}
            dispatch={actions}
          />
        </Panel>
        <Panel
          label="RUNTIME"
          title="运行环境"
          className="settings-runtime-panel"
        >
          <div className="runtime-summary">
            <strong>{`目标推理设备：${backendLabel ?? "尚未读取"}`}</strong>
            <p>
              实际执行设备：尚无实测值。Paddle 设备决策与 GPU 回退见诊断页。
            </p>
            <p>当前服务：{serviceText}</p>
            <p>本次维护：{maintenanceLine}</p>
            {/* 只有真实进行中的维护操作才渲染进度；设置刷新不是维护。 */}
            {state.progressActive === true ? (
              <ProgressBar
                value={
                  typeof state.progressPercent === "number" &&
                  Number.isFinite(state.progressPercent)
                    ? state.progressPercent / 100
                    : undefined
                }
                aria-label="运行环境维护进度"
              />
            ) : null}
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
          {viewState.capabilities.includes("runtime.environments") ? (
            <ManagedEnvironmentEditor state={state} actions={actions} />
          ) : (
            <>
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
            </>
          )}
          <p className="form-note">
            {stringValue(state.statusMessage) ??
              statusLabel(state.statusCode, "运行环境状态已同步。")}
          </p>
        </Panel>
        <Panel
          label="MINERU"
          title="MinerU 连接"
          className="settings-mineru-panel"
        >
          <MineruConnectionEditor
            key={`${mineru?.mode ?? ""}|${mineru?.apiUrl ?? ""}`}
            connection={mineru}
            locked={busy}
            hostBusy={booleanValue(state.isBusy)}
            actions={actions}
          />
        </Panel>
        <Panel
          label="DOWNLOADS"
          title="下载来源"
          className="settings-download-panel"
        >
          {viewState.capabilities.includes("runtime.environments") ? (
            <p className="form-note">
              下载来源已并入“环境与依赖”：全局默认、单环境覆盖与本次安装选择
              统一在那里配置。
            </p>
          ) : (
            <>
              <SourceSelector
                sources={sources}
                loaded={sourcesLoaded}
                enabled={viewState.capabilities.includes("settings.selection")}
                locked={busy}
                actions={actions}
              />
              <p className="form-note">
                依赖包来源用于安装运行环境；模型原生来源由引擎使用。更换来源不会安装组件或提前准备模型。
              </p>
            </>
          )}
        </Panel>
      </div>
    </Workspace>
  );
}

function managedEnvironments(
  value: unknown,
): readonly ManagedEnvironmentState[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (item): item is ManagedEnvironmentState =>
      typeof item === "object" &&
      item !== null &&
      typeof item.id === "string" &&
      typeof item.name === "string" &&
      typeof item.revision === "number" &&
      typeof item.status === "string",
  );
}

function managedEnvironmentPlan(
  value: unknown,
): ManagedEnvironmentPlanState | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const plan = value as Record<string, unknown>;
  if (
    typeof plan.planId !== "string" ||
    typeof plan.environmentId !== "string" ||
    typeof plan.environmentRevision !== "number" ||
    typeof plan.recipe !== "string" ||
    typeof plan.requestedRecipe !== "string" ||
    !Array.isArray(plan.sourceIds) ||
    !Array.isArray(plan.dependencies)
  )
    return undefined;
  return plan as unknown as ManagedEnvironmentPlanState;
}

function managedSourceOptions(
  value: unknown,
): readonly ManagedSourceOptionState[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (entry): entry is ManagedSourceOptionState =>
      entry !== null &&
      typeof entry === "object" &&
      !Array.isArray(entry) &&
      typeof (entry as Partial<ManagedSourceOptionState>).id === "string" &&
      typeof (entry as Partial<ManagedSourceOptionState>).kind === "string" &&
      typeof (entry as Partial<ManagedSourceOptionState>).displayName ===
        "string",
  );
}

function managedResolvedSources(
  value: unknown,
): readonly ManagedResolvedSourceState[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (entry): entry is ManagedResolvedSourceState =>
      entry !== null &&
      typeof entry === "object" &&
      !Array.isArray(entry) &&
      typeof (entry as Partial<ManagedResolvedSourceState>).kind === "string" &&
      (entry as Partial<ManagedResolvedSourceState>).id !== undefined,
  );
}

function ManagedEnvironmentEditor({
  state,
  actions,
}: {
  readonly state: Record<string, unknown>;
  readonly actions: AppActions;
}) {
  const environments = managedEnvironments(state.environments);
  const activeId = stringValue(state.activeEnvironmentId);
  const [selectedId, setSelectedId] = useState("");
  const [name, setName] = useState("");
  const [recipe, setRecipe] = useState<string>(environmentRecipes[0][0]);
  // 目录优先；旧宿主无目录时回退到 package id 列表（显示名即 id）。
  const catalog = managedSourceOptions(state.environmentSources);
  const packageSources = catalog.length
    ? catalog.filter((source) => source.kind === "package_index")
    : stringValues(state.environmentPackageSourceIds).map((id) => ({
        id,
        kind: "package_index",
        displayName: id,
        endpoint: "",
      }));
  const modelSources = catalog.filter(
    (source) => source.kind === "model_registry",
  );
  const sourceName = (id: string | null | undefined): string =>
    id == null || id === ""
      ? "官方默认（端点未知）"
      : (catalog.find((source) => source.id === id)?.displayName ?? id);
  const packageIds = packageSources.map((source) => source.id);
  const [sourceId, setSourceId] = useState("");
  // "" = 本次跟随环境配置（环境 override → 全局默认 → 产品默认）。
  const selectedSourceId =
    sourceId === "" || packageIds.includes(sourceId) ? sourceId : "";
  const selected =
    environments.find((environment) => environment.id === selectedId) ??
    environments.find((environment) => environment.id === activeId) ??
    environments[0];
  const plan = managedEnvironmentPlan(state.environmentPlan);
  const [invalidatedPlanId, setInvalidatedPlanId] = useState<string | null>(
    null,
  );
  const invalidatePreview = () => {
    setInvalidatedPlanId(plan?.planId ?? null);
    void actions.run({ type: "settings.invalidateEnvironmentPlan" });
  };
  const requestMatches =
    plan?.requestedSourceIds == null
      ? selectedSourceId === ""
      : plan.requestedSourceIds.length === 1 &&
        plan.requestedSourceIds[0] === selectedSourceId;
  const pendingPlan =
    plan &&
    plan.planId !== invalidatedPlanId &&
    plan.environmentId === selected?.id &&
    plan.requestedRecipe === recipe &&
    requestMatches
      ? plan
      : undefined;
  const busy = state.environmentBusy === true;
  // 全局默认与单环境 override 的选择：未编辑时跟随持久化值；保存成功后
  // 清空本地编辑，回显宿主回传的真值。
  const defaultIds = stringValues(state.environmentDefaultSourceIds);
  const globalPackageId =
    defaultIds.find((id) => packageIds.includes(id)) ?? "";
  const modelIds = modelSources.map((source) => source.id);
  const globalModelId = defaultIds.find((id) => modelIds.includes(id)) ?? "";
  const [globalEdit, setGlobalEdit] = useState<{
    packageId?: string;
    modelId?: string;
  }>({});
  const globalPackageValue = globalEdit.packageId ?? globalPackageId;
  const globalModelValue = globalEdit.modelId ?? globalModelId;
  const [envEdit, setEnvEdit] = useState<{
    envId: string;
    packageId?: string;
    modelId?: string;
  }>({ envId: "" });
  const overrideIds = stringValues(selected?.overrideSourceIds);
  const envOverridePackageRecord =
    overrideIds.find((id) => packageIds.includes(id)) ?? "";
  const envOverrideModelRecord =
    overrideIds.find((id) => modelIds.includes(id)) ?? "";
  const envOverridePackage =
    (envEdit.envId === selected?.id ? envEdit.packageId : undefined) ??
    envOverridePackageRecord;
  const envOverrideModel =
    (envEdit.envId === selected?.id ? envEdit.modelId : undefined) ??
    envOverrideModelRecord;
  return (
    <div className="managed-environments">
      <p className="form-note">
        每个环境拥有独立的 Python
        与依赖；新建环境可以保持空白，安装依赖和切换服务是分别确认的操作。
      </p>
      <div className="setting-row">
        <Input
          aria-label="新环境名称"
          placeholder="新环境名称"
          value={name}
          disabled={busy}
          onChange={(_, data) => setName(data.value)}
        />
        <Button
          disabled={busy || !name.trim()}
          onClick={() => {
            void actions.run({
              type: "settings.createEnvironment",
              name: name.trim(),
            });
            setName("");
          }}
        >
          创建空环境
        </Button>
      </div>
      {environments.length === 0 ? (
        <p>尚无环境。可以先创建空环境，稍后安装识别依赖。</p>
      ) : (
        <>
          <label htmlFor="managed-environment-select">目标环境</label>
          <Select
            id="managed-environment-select"
            value={selected?.id ?? ""}
            disabled={busy}
            onChange={(event) => {
              invalidatePreview();
              setSelectedId(event.target.value);
            }}
          >
            {environments.map((environment) => (
              <option key={environment.id} value={environment.id}>
                {environment.name}
                {environment.id === activeId ? " · 当前使用" : ""}
              </option>
            ))}
          </Select>
        </>
      )}
      {selected ? (
        <>
          <p>
            状态：{selected.status === "empty" ? "空环境" : "已安装依赖"} ·
            Python {selected.pythonState} · 依赖 {selected.dependencyState}
          </p>
          <p>
            引擎 {selected.engineState} · 模型 {selected.modelState} · 服务{" "}
            {selected.serviceState}
          </p>
          <p>
            已配置识别：
            {selected.configuredRecognitionTypes?.join("、") || "无"}
            ；目标设备：{selected.targetDevice || "未设置"}；实际设备：
            {selected.actualDevice || "未报告实际执行设备"}
          </p>
          <p>
            解释器：{selected.pythonVersion || "未验证"} · ABI{" "}
            {selected.abi || "未验证"}
            ；占用：
            {typeof selected.diskBytes === "number"
              ? `${(selected.diskBytes / 1024 / 1024).toFixed(1)} MiB`
              : "未检查"}
          </p>
          <p className="form-note">
            Python：{selected.python || "未定位"}；环境目录：
            {selected.path || "未定位"}
          </p>
          {selected.reason ? <p role="alert">{selected.reason}</p> : null}
          {selected.lastInstallFailure ? (
            <p
              role={
                selected.lastInstallFailure.phase === "failed"
                  ? "alert"
                  : "status"
              }
            >
              {selected.lastInstallFailure.phase === "failed"
                ? "上次依赖安装未完成"
                : "依赖安装进行中"}
              （环境修订 {selected.lastInstallFailure.environmentRevision}，
              {selected.lastInstallFailure.recipe}）：
              {selected.lastInstallFailure.detail}（原因：
              {selected.lastInstallFailure.reasonCode}；建议：
              {environmentRecoveryActions[
                selected.lastInstallFailure.nextAction
              ] ?? selected.lastInstallFailure.nextAction}
              ）
              {selected.lastInstallFailure.effectiveSourceIds?.length
                ? `；请求源：${
                    selected.lastInstallFailure.requestedSourceIds == null
                      ? "跟随配置"
                      : selected.lastInstallFailure.requestedSourceIds
                          .map((id) => sourceName(id))
                          .join("、")
                  }；生效源：${selected.lastInstallFailure.effectiveSourceIds
                    .map((id) => sourceName(id))
                    .join("、")}`
                : ""}
            </p>
          ) : null}
          <details className="managed-source-config">
            <summary>来源配置（默认、覆盖与已生效值）</summary>
            {managedResolvedSources(selected.resolvedSources).map((entry) => (
              <p key={entry.kind}>
                {sourceKindLabel(entry.kind)}：
                {entry.displayName ?? "官方默认（端点未知）"}（
                {sourceOriginLabels[entry.origin] ?? entry.origin}）
              </p>
            ))}
            {selected.sourceIds?.length ? (
              <p className="form-note">
                已安装依赖来源：
                {selected.sourceIds.map((id) => sourceName(id)).join("、")}
                （该环境安装时锁定）
              </p>
            ) : null}
            {selected.unknownSourceIds?.length ? (
              <p role="note">
                未知来源：{selected.unknownSourceIds.join("、")}
                （当前版本目录不含，解析已回退继承）
              </p>
            ) : null}
            {packageSources.length + modelSources.length === 0 ? (
              <p className="form-note">
                下载源目录尚未同步；可点击“重新检查状态”后再配置。
              </p>
            ) : (
              <>
                {catalog.length > 0 && modelSources.length === 0 ? (
                  <p className="form-note">
                    当前目录没有可选的模型来源；模型偏好保持官方默认（端点未知）。
                  </p>
                ) : null}
                <div className="setting-row">
                  <label htmlFor="managed-env-package-source">
                    本环境依赖包来源
                  </label>
                  <Select
                    id="managed-env-package-source"
                    value={envOverridePackage}
                    disabled={busy}
                    onChange={(_, data) =>
                      setEnvEdit((current) => ({
                        envId: selected.id,
                        packageId: String(data.value),
                        modelId:
                          current.envId === selected.id
                            ? current.modelId
                            : undefined,
                      }))
                    }
                  >
                    <option value="">
                      跟随全局默认（当前 {sourceName(globalPackageId)}）
                    </option>
                    {packageSources.map((source) => (
                      <option key={source.id} value={source.id}>
                        {source.displayName}
                      </option>
                    ))}
                  </Select>
                </div>
              </>
            )}
            {modelSources.length > 0 ? (
              <div className="setting-row">
                <label htmlFor="managed-env-model-source">本环境模型来源</label>
                <Select
                  id="managed-env-model-source"
                  value={envOverrideModel}
                  disabled={busy}
                  onChange={(_, data) =>
                    setEnvEdit((current) => ({
                      envId: selected.id,
                      modelId: String(data.value),
                      packageId:
                        current.envId === selected.id
                          ? current.packageId
                          : undefined,
                    }))
                  }
                >
                  <option value="">
                    跟随全局默认（当前 {sourceName(globalModelId)}）
                  </option>
                  {modelSources.map((source) => (
                    <option key={source.id} value={source.id}>
                      {source.displayName}
                    </option>
                  ))}
                </Select>
              </div>
            ) : null}
            <div className="setting-row">
              <Button
                disabled={
                  busy || packageSources.length + modelSources.length === 0
                }
                onClick={async () => {
                  const ok = await actions.run({
                    type: "settings.setEnvironmentSources",
                    environmentId: selected.id,
                    packageSourceId: envOverridePackage || null,
                    modelSourceId: envOverrideModel || null,
                  });
                  if (ok) setEnvEdit({ envId: "" });
                }}
              >
                保存本环境来源
              </Button>
              {selected.id === activeId && selected.status === "installed" ? (
                <span className="form-note">
                  该环境正在使用；模型来源修改将在下次启动时生效。
                </span>
              ) : null}
            </div>
            <p className="form-note">
              保存只写配置：不下载、不安装、不重启；只影响后续安装与继承，
              保存环境 A 不会修改环境 B。
            </p>
          </details>
          {selected.kind !== "legacy" &&
          (selected.id !== activeId || selected.status === "empty") ? (
            <>
              <label htmlFor="managed-recipe-select">锁定依赖配方</label>
              <Select
                id="managed-recipe-select"
                value={recipe}
                disabled={busy}
                onChange={(event) => {
                  invalidatePreview();
                  setRecipe(event.target.value);
                }}
              >
                {environmentRecipes.map(([id, label]) => (
                  <option key={id} value={id}>
                    {label}
                  </option>
                ))}
              </Select>
              <label htmlFor="managed-package-source-select">
                本次依赖下载源
              </label>
              <Select
                id="managed-package-source-select"
                value={selectedSourceId}
                disabled={busy}
                onChange={(event) => {
                  invalidatePreview();
                  setSourceId(event.target.value);
                }}
              >
                <option value="">
                  跟随环境配置（当前解析为{" "}
                  {managedResolvedSources(selected.resolvedSources).find(
                    (entry) => entry.kind === "package_index",
                  )?.displayName ?? "TUNA PyPI 镜像（产品默认）"}
                  ）
                </option>
                {packageSources.map((source) => (
                  <option key={source.id} value={source.id}>
                    {source.displayName}
                  </option>
                ))}
              </Select>
              <div className="setting-row">
                <Button
                  disabled={busy}
                  onClick={() =>
                    void actions.run({
                      type: "settings.previewEnvironmentInstall",
                      environmentId: selected.id,
                      recipe,
                      ...(selectedSourceId
                        ? { sourceId: selectedSourceId }
                        : {}),
                    })
                  }
                >
                  预览依赖
                </Button>
                {selected.status === "empty" &&
                selected.pythonState !== "ready" ? (
                  <Button
                    disabled={busy}
                    onClick={() =>
                      void actions.run({
                        type: "settings.repairEmptyEnvironment",
                        environmentId: selected.id,
                      })
                    }
                  >
                    修复空环境 Python
                  </Button>
                ) : null}
                {selected.id !== activeId ? (
                  <Button
                    disabled={busy}
                    onClick={() =>
                      void actions.run({
                        type: "settings.deleteEnvironment",
                        environmentId: selected.id,
                      })
                    }
                  >
                    删除环境
                  </Button>
                ) : null}
              </div>
            </>
          ) : null}
          {pendingPlan ? (
            <div className="runtime-install-plan">
              <p>
                计划：{pendingPlan.recipe} · 目标
                {environments.find(
                  (environment) => environment.id === pendingPlan.environmentId,
                )?.name ?? pendingPlan.environmentId}
                （环境修订 {pendingPlan.environmentRevision}）· 请求源：
                {pendingPlan.requestedSourceIds == null
                  ? "跟随配置"
                  : pendingPlan.requestedSourceIds
                      .map((id) => sourceName(id))
                      .join("、")}
                ；生效源：
                {pendingPlan.sourceIds.map((id) => sourceName(id)).join("、")}
              </p>
              <details>
                <summary>
                  锁定依赖（{pendingPlan.dependencies.length} 项）
                </summary>
                <p>{pendingPlan.dependencies.join("、")}</p>
              </details>
              <details>
                <summary>来源与资产明细</summary>
                {(pendingPlan.sources ?? []).map((source) => (
                  <p key={source.id}>
                    {source.displayName}（{sourceKindLabel(source.kind)}，
                    {source.requested
                      ? "本次指定"
                      : `继承（${
                          sourceOriginLabels[source.inheritedFrom] ??
                          source.inheritedFrom
                        }）`}
                    ；{sourceUsageLabels[source.usage] ?? source.usage}
                    {source.endpoint ? `；端点 ${source.endpoint}` : ""}
                    ；实际下载端点
                    {source.actualEndpoint ?? "未知"}）
                  </p>
                ))}
                <p className="form-note">
                  依赖来源：
                  {pendingPlan.dependencyOrigin === "bundled_pack"
                    ? "随包离线包"
                    : "锁定在线索引（哈希锁定）"}
                  ；解释器固定归档与内部 Runtime wheel 随产品分发并经清单校验。
                </p>
                <p className="form-note">
                  模型源仅为偏好投影，实际下载端点与成本未知；缓存命中
                  不会计入新下载，实际下载情况安装后才可知。
                </p>
              </details>
              <Button
                disabled={busy}
                onClick={() =>
                  void actions.run({
                    type: "settings.confirmEnvironmentInstall",
                    planId: pendingPlan.planId,
                    ...(selectedSourceId ? { sourceId: selectedSourceId } : {}),
                  })
                }
              >
                确认安装依赖
              </Button>
            </div>
          ) : null}
          {selected.id !== activeId ||
          (selected.status === "installed" &&
            selected.serviceState !== "ready") ? (
            <Button
              disabled={busy}
              onClick={() =>
                void actions.run({
                  type: "settings.switchEnvironment",
                  environmentId: selected.id,
                })
              }
            >
              {selected.id === activeId ? "启动并验证当前环境" : "切换到此环境"}
            </Button>
          ) : null}
        </>
      ) : null}
      <details className="managed-source-defaults">
        <summary>全局默认来源（所有环境继承）</summary>
        {stringValues(state.environmentUnknownDefaultSourceIds).length ? (
          <p role="note">
            全局配置含未知来源：
            {stringValues(state.environmentUnknownDefaultSourceIds).join("、")}
            （当前版本目录不含，已回退产品默认）
          </p>
        ) : null}
        {packageSources.length + modelSources.length === 0 ? (
          <p className="form-note">
            下载源目录尚未同步；可点击“重新检查状态”后再配置。
          </p>
        ) : (
          <>
            {catalog.length > 0 && modelSources.length === 0 ? (
              <p className="form-note">
                当前目录没有可选的模型来源；模型偏好保持官方默认（端点未知）。
              </p>
            ) : null}
            <div className="setting-row">
              <label htmlFor="managed-global-package-source">
                全局依赖包来源
              </label>
              <Select
                id="managed-global-package-source"
                value={globalPackageValue}
                disabled={busy}
                onChange={(_, data) =>
                  setGlobalEdit((current) => ({
                    ...current,
                    packageId: String(data.value),
                  }))
                }
              >
                <option value="">产品默认（TUNA PyPI 镜像）</option>
                {packageSources.map((source) => (
                  <option key={source.id} value={source.id}>
                    {source.displayName}
                  </option>
                ))}
              </Select>
            </div>
          </>
        )}
        {modelSources.length > 0 ? (
          <div className="setting-row">
            <label htmlFor="managed-global-model-source">全局模型来源</label>
            <Select
              id="managed-global-model-source"
              value={globalModelValue}
              disabled={busy}
              onChange={(_, data) =>
                setGlobalEdit((current) => ({
                  ...current,
                  modelId: String(data.value),
                }))
              }
            >
              <option value="">产品默认（官方原生默认，端点未知）</option>
              {modelSources.map((source) => (
                <option key={source.id} value={source.id}>
                  {source.displayName}
                </option>
              ))}
            </Select>
          </div>
        ) : null}
        <div className="setting-row">
          <Button
            disabled={busy || packageSources.length + modelSources.length === 0}
            onClick={async () => {
              const ok = await actions.run({
                type: "settings.setEnvironmentSources",
                packageSourceId: globalPackageValue || null,
                modelSourceId: globalModelValue || null,
              });
              if (ok) setGlobalEdit({});
            }}
          >
            保存全局默认来源
          </Button>
          <span className="form-note">
            只影响后续继承与旧计划有效性，不会篡改在途安装操作。
          </span>
        </div>
      </details>
      <p role="status">{stringValue(state.environmentStatus) ?? ""}</p>
      {state.environmentCanCancelInstall === true ? (
        <Button
          onClick={() =>
            void actions.run({ type: "settings.cancelEnvironmentInstall" })
          }
        >
          取消安装
        </Button>
      ) : null}
    </div>
  );
}

function SourceSelector({
  sources,
  loaded,
  enabled,
  locked,
  actions,
}: {
  readonly sources: readonly SourceOptionState[];
  readonly loaded: boolean;
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
    return (
      <p className="form-note">
        {loaded
          ? "当前 Backend 未提供下载源目录。"
          : "运行环境目录尚未加载，正在等待宿主同步。"}
      </p>
    );
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

function MineruConnectionEditor({
  connection,
  locked,
  hostBusy,
  actions,
}: {
  readonly connection: MineruConnectionState | undefined;
  readonly locked: boolean;
  readonly hostBusy: boolean;
  readonly actions: AppActions;
}) {
  const remoteSupported = connection?.supported === true;
  const loaded = connection !== undefined;
  const [mode, setMode] = useState<"local" | "remote">(
    connection?.mode === "remote" ? "remote" : "local",
  );
  const [apiUrl, setApiUrl] = useState(connection?.apiUrl ?? "");
  const [apiKey, setApiKey] = useState("");
  const [clearKey, setClearKey] = useState(false);
  const remote = mode === "remote";
  const hasStoredKey = connection?.hasApiKey === true;
  const save = async () => {
    if (!remote) {
      await actions.run({ type: "settings.setMineruConnection", mode });
      return;
    }
    // 三态：未编辑不携带 apiKey（保留），勾选清除发送空串，输入新值替换。
    const keyArgument = clearKey ? "" : apiKey === "" ? null : apiKey;
    const saved = await actions.run(
      keyArgument === null
        ? { type: "settings.setMineruConnection", mode, apiUrl }
        : {
            type: "settings.setMineruConnection",
            mode,
            apiUrl,
            apiKey: keyArgument,
          },
    );
    if (saved) {
      setApiKey("");
      setClearKey(false);
    }
  };
  return (
    <>
      {loaded ? null : (
        <p className="form-note">
          MinerU 连接设置等待运行环境目录同步；可点击“重新检查状态”后配置。
        </p>
      )}
      <div className="setting-row">
        <label htmlFor="mineru-mode">MinerU 运行模式</label>
        <Select
          id="mineru-mode"
          value={mode}
          disabled={locked || !loaded}
          onChange={(_, data) =>
            setMode(data.value === "remote" ? "remote" : "local")
          }
        >
          <option value="local">本地（随运行环境安装）</option>
          <option value="remote" disabled={!remoteSupported}>
            远程（自部署服务）
          </option>
        </Select>
      </div>
      {loaded && !remoteSupported ? (
        <p className="form-note" role="note">
          当前 Backend 未声明 ocr.mineru-remote-api.v1，远程模式不可用；请更新
          Backend 后再配置远程连接。
        </p>
      ) : null}
      {remote ? (
        <>
          <div className="setting-row">
            <label htmlFor="mineru-api-url">服务根地址</label>
            <Input
              id="mineru-api-url"
              placeholder="https://mineru4.example.com"
              value={apiUrl}
              onChange={(_, data) => setApiUrl(data.value)}
            />
          </div>
          <div className="setting-row">
            <label htmlFor="mineru-api-key">API Key（可选）</label>
            <Input
              id="mineru-api-key"
              type="password"
              autoComplete="off"
              placeholder={
                hasStoredKey
                  ? "输入新 Key 替换；留空保存保留已配置的 Key"
                  : "输入后随保存配置；可留空"
              }
              disabled={clearKey}
              value={apiKey}
              onChange={(_, data) => setApiKey(data.value)}
            />
          </div>
          {hasStoredKey ? (
            <div className="setting-row">
              <Checkbox
                label="保存时清除已保存的 API Key"
                checked={clearKey}
                disabled={locked || !loaded}
                onChange={(_, data) => setClearKey(data.checked === true)}
              />
            </div>
          ) : null}
          <p className="form-note">
            {hasStoredKey
              ? "Backend 已保存 API Key；留空保存会保留它，勾选上方清除项后保存可移除。"
              : "Backend 未保存 API Key。"}
          </p>
        </>
      ) : null}
      <div className="setting-row">
        <Button
          disabled={
            locked ||
            !loaded ||
            (remote && (!remoteSupported || apiUrl.trim().length === 0))
          }
          onClick={() => void save()}
          icon={<Save aria-hidden="true" size={16} />}
        >
          保存 MinerU 配置
        </Button>
      </div>
      {remote ? (
        <div className="setting-row">
          <Button
            appearance="secondary"
            disabled={
              locked ||
              hostBusy ||
              !remoteSupported ||
              apiUrl.trim().length === 0
            }
            onClick={() =>
              void actions.run({ type: "settings.prepareMineruConnection" })
            }
            icon={<RefreshCw aria-hidden="true" size={16} />}
          >
            验证并准备远程服务
          </Button>
        </div>
      ) : null}
      <p className="form-note">
        远程模式由 Backend 将解析请求转发到自部署的 MinerU 4 服务，不要求本地
        MinerU 组件、模型或
        GPU；保存只写入配置，不验证服务连通性，实际效果以识别任务结果为准。
      </p>
      <p className="form-note">
        “验证并准备远程服务”由 Backend
        首次调用远程服务完成准备并刷新可用性；不宣称未验证的连接可解析。
      </p>
      <p className="form-note">
        本地模式下的模型预热、驻留 TTL 与释放只作用于本地模型，不控制远程服务。
      </p>
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
  scope = "recognition",
}: {
  readonly engines: readonly RecognitionEngineState[];
  readonly taskEngine: string | undefined;
  readonly enabled: boolean;
  readonly actions: AppActions;
  readonly scope?: "recognition" | "batch" | "pdf";
}) {
  if (engines.length === 0) {
    return null;
  }
  const active =
    engines.find((engine) => engine.isTaskOverride) ??
    engines.find((engine) => engine.selected);
  return (
    <div className="setting-row recognition-mode-settings">
      <label htmlFor={`${scope}-task-engine`}>本次识别模式</label>
      <Select
        id={`${scope}-task-engine`}
        value={taskEngine ?? ""}
        disabled={!enabled}
        onChange={(_, data) =>
          data.value === ""
            ? actions.run({ type: `${scope}.setTaskEngine` })
            : actions.run({
                type: `${scope}.setTaskEngine`,
                engine: String(data.value),
              })
        }
      >
        <option value="">使用 Runtime 默认模式</option>
        {(
          [
            ["文字", ["paddle_text"]],
            ["表格", ["paddle_table"]],
            ["公式", ["paddle_formula"]],
            ["文档结构", ["paddle_structure", "paddle_document_vl"]],
            ["其他引擎", []],
          ] as const
        ).map(([label, ids]) => {
          const choices = engines.filter((engine) =>
            ids.length === 0
              ? ![
                  "paddle_text",
                  "paddle_table",
                  "paddle_formula",
                  "paddle_structure",
                  "paddle_document_vl",
                ].includes(engine.engine)
              : (ids as readonly string[]).includes(engine.engine),
          );
          return choices.length ? (
            <optgroup key={label} label={label}>
              {choices.map((engine) => (
                <option key={engine.engine} value={engine.engine}>
                  {`${engine.displayName}（${availabilityLabel(engine.availability)}${engine.requiresDownload ? "，需下载" : ""}）`}
                </option>
              ))}
            </optgroup>
          ) : null;
        })}
      </Select>
      {active?.reasonCode && (
        <p className="form-note">当前环境：{active.reasonCode}</p>
      )}
      {active?.engine.startsWith("paddle_") &&
        Array.isArray(active.supportedOptions) && (
          // key 只绑定模式：保存成功后宿主会回显新的 active.options，若把它混入
          // key，保存瞬间会重挂编辑器并丢失保存提示与展开状态；宿主值同步由
          // 编辑器在无未保存编辑时自行完成。
          <PaddleOptionsEditor
            key={active.engine}
            modeId={active.engine}
            supportedOptions={active.supportedOptions}
            values={active.options}
            actions={actions}
          />
        )}{" "}
      {lifecycleHint(active) ? (
        <p className="form-note">{lifecycleHint(active)}</p>
      ) : null}
    </div>
  );
}

function HotkeyActions({
  actions: hotkeyOptions,
  enabled,
  dispatch,
}: {
  readonly actions: readonly HotkeyActionOptionState[];
  readonly enabled: boolean;
  readonly dispatch: AppActions;
}) {
  if (hotkeyOptions.length === 0) {
    return (
      <p className="form-note">
        {enabled
          ? "动作目录等待宿主同步。"
          : "此功能需要宿主能力：settings.hotkeys"}
      </p>
    );
  }

  return (
    <div className="hotkey-action-list">
      {hotkeyOptions.map((option) => (
        <HotkeyActionRow
          key={`${option.actionId}:${option.configuredHotkey ?? ""}`}
          option={option}
          enabled={enabled}
          dispatch={dispatch}
        />
      ))}
    </div>
  );
}

function HotkeyActionRow({
  option,
  enabled,
  dispatch,
}: {
  readonly option: HotkeyActionOptionState;
  readonly enabled: boolean;
  readonly dispatch: AppActions;
}) {
  const [hotkey, setHotkey] = useState(option.configuredHotkey ?? "");
  // 配置已存但未注册生效（如键位被其他应用占用）必须如实区分。
  const status = option.registeredHotkey
    ? `当前生效：${option.registeredHotkey}`
    : option.configuredHotkey
      ? `已保存 ${option.configuredHotkey}，但当前未注册生效`
      : "未绑定";
  return (
    <div className="hotkey-action-row">
      <div className="hotkey-action-head">
        <strong>{option.displayName}</strong>
        <span>{status}</span>
      </div>
      <div className="setting-row">
        <HotkeyRecorder
          label={option.displayName + "新快捷键"}
          value={hotkey}
          disabled={!enabled}
          onChange={setHotkey}
          onStart={async (recordingId) => {
            if (
              !(await dispatch.run({
                type: "settings.beginHotkeyRecording",
                recordingId,
              }))
            )
              throw new Error("宿主未能开始快捷键录入，请重试。");
          }}
          onEnd={async (recordingId) => {
            if (
              !(await dispatch.run({
                type: "settings.endHotkeyRecording",
                recordingId,
              }))
            )
              throw new Error("宿主未能恢复快捷键注册，请重试。");
          }}
        />
        <Button
          disabled={
            !enabled ||
            hotkey.trim() === "" ||
            hotkey
              .split("+")
              .every((key) => ["Ctrl", "Alt", "Shift", "Win"].includes(key))
          }
          aria-label={`应用 ${option.displayName}`}
          onClick={() =>
            dispatch.run({
              type: "settings.setActionHotkey",
              actionId: option.actionId,
              hotkey: hotkey.trim(),
            })
          }
          icon={<Save aria-hidden="true" size={16} />}
        >
          应用
        </Button>
        <Button
          appearance="secondary"
          disabled={!enabled || hotkey === ""}
          aria-label={"清空 " + option.displayName}
          onClick={() => setHotkey("")}
        >
          清空
        </Button>
        <Button
          appearance="secondary"
          disabled={!enabled || !option.configuredHotkey}
          aria-label={`禁用 ${option.displayName}`}
          onClick={() =>
            dispatch.run({
              type: "settings.setActionHotkey",
              actionId: option.actionId,
            })
          }
          icon={<X aria-hidden="true" size={16} />}
        >
          禁用
        </Button>
        {option.defaultHotkey ? (
          <Button
            appearance="secondary"
            disabled={!enabled}
            aria-label={`恢复默认 ${option.displayName}`}
            onClick={() =>
              dispatch.run({
                type: "settings.resetActionHotkey",
                actionId: option.actionId,
              })
            }
            icon={<RotateCcw aria-hidden="true" size={16} />}
          >
            恢复默认
          </Button>
        ) : null}
      </div>
      {option.error ? (
        <p className="form-note" role="alert">
          {option.error}
        </p>
      ) : null}
    </div>
  );
}

function FloatingToolbarPanel({
  toolbar,
  enabled,
  dispatch,
}: {
  readonly toolbar: FloatingToolbarOptionState | undefined;
  readonly enabled: boolean;
  readonly dispatch: AppActions;
}) {
  if (!toolbar) {
    return (
      <p className="form-note">
        {enabled
          ? "悬浮工具栏状态等待宿主同步。"
          : "此功能需要宿主能力：settings.floatingToolbar"}
      </p>
    );
  }

  return (
    <>
      <div className="setting-row">
        <Checkbox
          label="启用悬浮工具栏"
          checked={toolbar.enabled}
          disabled={!enabled}
          onChange={(_, data) =>
            dispatch.run({
              type: "settings.setFloatingToolbarEnabled",
              enabled: data.checked === true,
            })
          }
        />
      </div>
      <p className="form-note">
        当前状态：{toolbarVisibilityLabel(toolbar.visibility)}
        。主动隐藏不会被鼠标路过唤回，可从本页、托盘菜单或“悬浮栏显示/隐藏”快捷键找回；截图时工具栏与感应条会自动让位，结束后恢复原状。
      </p>
      {toolbar.error ? (
        <p className="form-note" role="alert">
          {toolbar.error}
        </p>
      ) : null}
      <div className="setting-row">
        <Button
          disabled={!enabled || !toolbar.enabled}
          onClick={() => dispatch.run({ type: "settings.showFloatingToolbar" })}
          icon={<Eye aria-hidden="true" size={16} />}
        >
          显示
        </Button>
        <Button
          appearance="secondary"
          disabled={!enabled || !toolbar.enabled}
          onClick={() => dispatch.run({ type: "settings.hideFloatingToolbar" })}
          icon={<EyeOff aria-hidden="true" size={16} />}
        >
          隐藏
        </Button>
      </div>
      <div className="setting-row">
        <label htmlFor="toolbar-edge">靠边位置</label>
        <Select
          id="toolbar-edge"
          value={toolbar.edge}
          disabled={!enabled}
          onChange={(_, data) =>
            dispatch.run({
              type: "settings.setFloatingToolbarLayout",
              edge: String(data.value),
              autoHide: toolbar.autoHide,
            })
          }
        >
          {TOOLBAR_EDGES.map((edge) => (
            <option key={edge} value={edge}>
              {TOOLBAR_EDGE_LABELS[edge]}
            </option>
          ))}
        </Select>
        <Checkbox
          label="鼠标离开后自动收起"
          checked={toolbar.autoHide}
          disabled={!enabled}
          onChange={(_, data) =>
            dispatch.run({
              type: "settings.setFloatingToolbarLayout",
              edge: toolbar.edge,
              autoHide: data.checked === true,
            })
          }
        />
      </div>
    </>
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
  const deviceEvidence = stringValues(state.deviceEvidence);
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
        <Panel label="DEVICE" title="当前实例的 Paddle 设备日志">
          <p>设备决策与回退日志；不表示识别作业已成功执行。</p>
          {deviceEvidence.length > 0 ? (
            <pre>{deviceEvidence.join("\n")}</pre>
          ) : (
            <p>尚无设备证据。</p>
          )}
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
