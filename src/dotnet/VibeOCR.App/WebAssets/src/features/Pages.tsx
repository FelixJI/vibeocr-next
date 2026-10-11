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
  Settings,
  Sheet,
  Square,
  Trash2,
  X,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";

import type { AppActions, AppViewState } from "../app/types";
import { CaptureButton } from "../components/CaptureButton";
import { CapabilityGate } from "../components/CapabilityGate";
import { HotkeyRecorder } from "../components/HotkeyRecorder";
import { ImageCanvasEditor } from "../components/ImageCanvasEditor";
import { PdfInspection } from "../components/PdfInspection";
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
  readonly pageRange?: string | null;
  readonly supportsPageRange?: boolean;
  readonly id: string;
  readonly name: string;
  readonly statusCode: string;
  readonly resultSummary?: string | null;
  readonly structuredResult?: ResourceReference | null;
}

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
  readonly isDefault?: boolean;
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

function sourceKindLabel(kind: string): string {
  return (
    (
      {
        paddleocr_model_registry: "PaddleOCR 模型来源",
        mineru_model_registry: "MinerU 模型来源",
        model_registry: "模型来源",
      } as Record<string, string>
    )[kind] ?? "依赖包来源"
  );
}

interface ManagedEnvironmentRecipeState {
  readonly id: string;
  readonly displayName: string;
  readonly configuredRecognitionTypes?: readonly string[] | null;
  readonly accelerator?: string | null;
  readonly targetDevice?: string | null;
  readonly pythonVersion?: string | null;
  readonly abi?: string | null;
  readonly platform?: string | null;
  readonly componentIds?: readonly string[] | null;
  readonly dependencies?: readonly string[] | null;
  readonly dependencyOrigin?: string | null;
  readonly runtimeWheelOrigin?: string | null;
}

interface ManagedEnvironmentHardwareState {
  /** ok/unsupported/unknown；缺失按 unknown（未探测）呈现，不臆造可用性。 */
  readonly nvidiaDriverStatus: string;
  readonly nvidiaDriverReason?: string | null;
  readonly nvidiaDriverVersion?: string | null;
}

interface ManagedEnvironmentCompatibilityState {
  readonly recipe: string;
  readonly selectedEnvironmentId?: string | null;
  readonly selectedEnvironmentRevision?: number | null;
  readonly selectionReason?: string | null;
}

function managedEnvironmentRecipes(
  value: unknown,
): readonly ManagedEnvironmentRecipeState[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (entry): entry is ManagedEnvironmentRecipeState =>
      entry !== null &&
      typeof entry === "object" &&
      !Array.isArray(entry) &&
      typeof (entry as Partial<ManagedEnvironmentRecipeState>).id ===
        "string" &&
      typeof (entry as Partial<ManagedEnvironmentRecipeState>).displayName ===
        "string",
  );
}

function managedEnvironmentHardware(
  value: unknown,
): ManagedEnvironmentHardwareState {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return { nvidiaDriverStatus: "unknown" };
  const candidate = value as Partial<ManagedEnvironmentHardwareState>;
  return typeof candidate.nvidiaDriverStatus === "string" &&
    candidate.nvidiaDriverStatus.length > 0
    ? (candidate as ManagedEnvironmentHardwareState)
    : { nvidiaDriverStatus: "unknown" };
}

function managedEnvironmentCompatibility(
  value: unknown,
): ManagedEnvironmentCompatibilityState | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const candidate = value as Partial<ManagedEnvironmentCompatibilityState>;
  return typeof candidate.recipe === "string" && candidate.recipe.length > 0
    ? (candidate as ManagedEnvironmentCompatibilityState)
    : undefined;
}

// 实际支持选项的展示标签：类型来自 Runtime 目录 configured_recognition_types。
const recognitionTypeLabels: Readonly<Record<string, string>> = {
  text: "文字识别",
  table: "表格识别",
  formula: "公式识别",
  structure: "版面结构",
  document: "文档解析",
  document_vl: "文档视觉大模型",
};

function recognitionPurposeLabel(types: readonly string[]): string {
  return types.map((type) => recognitionTypeLabels[type] ?? type).join(" + ");
}

// 用途/设备分组只消费 Runtime 目录字段（configured_recognition_types 与
// accelerator），不在前端重算依赖或配方组合。
function recipePurposeKey(recipe: ManagedEnvironmentRecipeState): string {
  return [...(recipe.configuredRecognitionTypes ?? [])].sort().join("|");
}

// 目录组件分组（#195）：以配方 id 的 -cpu/-cuda 后缀分组，并与 accelerator
// 字段双向校验；后缀缺失、加速器不一致、同组件同设备重复、成员用途或
// 展示名不一致都明确拒绝，不用集合首个匹配静默合并不同引擎。
interface RecipeComponentGroup {
  readonly key: string;
  readonly title: string;
  readonly purposeLabel: string;
  readonly purposeKey: string;
  readonly byDevice: ReadonlyMap<string, ManagedEnvironmentRecipeState>;
}

interface RecipeComponentCatalog {
  readonly groups: readonly RecipeComponentGroup[];
  readonly problems: readonly string[];
}

function recipeDeviceFromId(
  id: string,
): { base: string; device: string } | null {
  if (id.endsWith("-cpu"))
    return { base: id.slice(0, -"-cpu".length), device: "cpu" };
  if (id.endsWith("-cuda"))
    return { base: id.slice(0, -"-cuda".length), device: "nvidia_cuda" };
  return null;
}

// 目录展示名以“ · CPU”/“ · NVIDIA CUDA”结尾承载设备；组件标题仅做展示层去尾。
function recipeTitleWithoutDevice(displayName: string): string {
  if (displayName.endsWith(" · NVIDIA CUDA"))
    return displayName.slice(0, -" · NVIDIA CUDA".length);
  if (displayName.endsWith(" · CPU"))
    return displayName.slice(0, -" · CPU".length);
  return displayName;
}

function recipeComponentGroups(
  recipes: readonly ManagedEnvironmentRecipeState[],
): RecipeComponentCatalog {
  const problems: string[] = [];
  const candidates = new Map<string, ManagedEnvironmentRecipeState[]>();
  for (const recipe of recipes) {
    const device = recipeDeviceFromId(recipe.id);
    if (!device) {
      problems.push(`配方 ${recipe.id} 缺少可识别的设备后缀，已拒绝该组合。`);
      continue;
    }
    const key = `${device.base}|${device.device}`;
    candidates.set(key, [...(candidates.get(key) ?? []), recipe]);
  }
  const groups = new Map<string, RecipeComponentGroup>();
  const rejectedGroups = new Set<string>();
  for (const [key, entries] of candidates) {
    const recipe = entries[0];
    if (!recipe) continue;
    const device = recipeDeviceFromId(recipe.id);
    if (!device) continue;
    if (
      entries.length !== 1 ||
      recipe.accelerator !== device.device ||
      recipe.targetDevice !== (device.device === "cpu" ? "cpu" : "cuda")
    ) {
      problems.push(`配方 ${key} 重复或设备信息冲突，已拒绝该组合。`);
      continue;
    }
    const title = recipeTitleWithoutDevice(recipe.displayName);
    const purposeKey = recipePurposeKey(recipe);
    const group = groups.get(device.base);
    if (group && (group.title !== title || group.purposeKey !== purposeKey)) {
      rejectedGroups.add(device.base);
      problems.push(`组件 ${device.base} 的目录信息冲突，已拒绝该组件。`);
      continue;
    }
    groups.set(device.base, {
      key: device.base,
      title,
      purposeKey,
      purposeLabel: recognitionPurposeLabel(
        recipe.configuredRecognitionTypes ?? [],
      ),
      byDevice: new Map([...(group?.byDevice ?? []), [device.device, recipe]]),
    });
  }
  return {
    groups: [...groups.values()].filter(
      (group) => !rejectedGroups.has(group.key),
    ),
    problems,
  };
}

// 主要依赖摘要（#195）：仅从目录/计划的实际 dependencies 里筛白名单
// 组件并保留版本 pin，不建前端依赖矩阵；其余包只在只读完整清单里出现。
const mainDependencyNames: readonly string[] = [
  "rapidocr",
  "onnxruntime",
  "paddleocr",
  "paddlepaddle",
  "paddlepaddle-gpu",
  "mineru",
  "torch",
  "torchvision",
];

function normalizeDependencyName(name: string): string {
  return name.toLowerCase().replace(/[-_.]+/g, "-");
}

function dependencyName(pin: string): string {
  const match = /^[A-Za-z0-9._-]+/.exec(pin.trim());
  return match ? match[0] : pin.trim();
}

function mainDependencies(
  dependencies: readonly string[] | null | undefined,
): readonly string[] {
  const whitelist = new Set(mainDependencyNames.map(normalizeDependencyName));
  const byName = new Map(
    (dependencies ?? [])
      .map(
        (pin) =>
          [
            normalizeDependencyName(dependencyName(pin)),
            pin.includes("@") ? dependencyName(pin) : pin,
          ] as const,
      )
      .filter(([name]) => whitelist.has(name)),
  );
  // 按白名单顺序稳定展示，保留实际锁定版本串。
  return mainDependencyNames
    .map((name) => byName.get(normalizeDependencyName(name)))
    .filter((pin): pin is string => pin !== undefined);
}

function managedEnvironmentSummary(
  environment: ManagedEnvironmentState,
): string {
  const parts: string[] = [];
  if (environment.configuredRecognitionTypes?.length)
    parts.push(recognitionPurposeLabel(environment.configuredRecognitionTypes));
  if (environment.targetDevice)
    parts.push(environment.targetDevice === "cuda" ? "GPU" : "CPU");
  if (parts.length === 0)
    parts.push(environment.status === "empty" ? "空环境" : "已安装依赖");
  return parts.join(" · ");
}

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
  readonly isDefault?: boolean;
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
  readonly sceneEditing: boolean;
  /** 冻结显示基准附带的 [0,1000] 归一化排除框：仅初始化屏蔽标记。 */
  readonly excludeBoxes?: readonly {
    readonly x: number;
    readonly y: number;
    readonly width: number;
    readonly height: number;
  }[];
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
        sceneEditing: candidate.sceneEditing === true,
        excludeBoxes: Array.isArray(candidate.excludeBoxes)
          ? candidate.excludeBoxes.filter(
              (
                box,
              ): box is {
                x: number;
                y: number;
                width: number;
                height: number;
              } =>
                typeof box === "object" &&
                box !== null &&
                ["x", "y", "width", "height"].every(
                  (key) =>
                    typeof (box as Record<string, unknown>)[key] === "number" &&
                    Number.isFinite((box as Record<string, unknown>)[key]),
                ),
            )
          : undefined,
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
  readonly lingerMs: number;
  readonly peekPixels?: number;
  readonly theme: "system" | "light" | "dark";
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
  return Number.isInteger(option.lingerMs) &&
    option.lingerMs! >= 100 &&
    option.lingerMs! <= 5000 &&
    (option.theme === "system" ||
      option.theme === "light" ||
      option.theme === "dark") &&
    typeof option.enabled === "boolean" &&
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

interface MineruTierOptionState {
  readonly id: string;
  readonly availability: string;
  readonly reasonCode: string | null;
}

function mineruTierOptions(value: unknown): readonly MineruTierOptionState[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (item): item is MineruTierOptionState =>
      item !== null &&
      typeof item === "object" &&
      !Array.isArray(item) &&
      typeof (item as Partial<MineruTierOptionState>).id === "string" &&
      typeof (item as Partial<MineruTierOptionState>).availability === "string",
  );
}

interface MineruRecognitionState {
  readonly supported: boolean;
  readonly stored: boolean;
  readonly tier: string | null;
  readonly ocrMode: string | null;
  readonly pageRange: string | null;
  readonly language: string | null;
  readonly invalid: boolean;
  readonly invalidReason: string | null;
  readonly tiers: readonly MineruTierOptionState[];
  readonly languages: readonly string[];
  readonly defaultTier: string | null;
}

// 全局 MinerU 识别偏好投影：tier/language 可用性来自目录；远程模式下
// language 仅是本地偏好，不代表服务端当前值。
function mineruRecognition(value: unknown): MineruRecognitionState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<MineruRecognitionState>;
  if (
    typeof candidate.supported !== "boolean" ||
    typeof candidate.stored !== "boolean" ||
    typeof candidate.invalid !== "boolean"
  )
    return undefined;
  return {
    supported: candidate.supported,
    stored: candidate.stored,
    tier: candidate.tier ?? null,
    ocrMode: candidate.ocrMode ?? null,
    pageRange: candidate.pageRange ?? null,
    language: candidate.language ?? null,
    invalid: candidate.invalid === true,
    invalidReason: candidate.invalidReason ?? null,
    tiers: mineruTierOptions(candidate.tiers),
    languages: Array.isArray(candidate.languages)
      ? candidate.languages.filter(
          (item): item is string => typeof item === "string",
        )
      : [],
    defaultTier: candidate.defaultTier ?? null,
  };
}

interface DefaultRecognitionModeState {
  readonly supported: boolean;
  readonly modeId: string | null;
  readonly stored: boolean;
}

// 默认识别模式投影：modeId=null 表示能力未声明或首读未完成。
function defaultRecognitionMode(
  value: unknown,
): DefaultRecognitionModeState | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<DefaultRecognitionModeState>;
  return typeof candidate.supported === "boolean" &&
    typeof candidate.stored === "boolean" &&
    (candidate.modeId === null || typeof candidate.modeId === "string")
    ? {
        supported: candidate.supported,
        modeId: candidate.modeId ?? null,
        stored: candidate.stored,
      }
    : undefined;
}

interface RecognitionModeOptionState {
  readonly id: string;
  readonly displayName: string;
  readonly availability: string;
  readonly reasonCode?: string | null;
}

// 设置页可选识别模式目录（来自 ocr.recognition-modes.v1 目录投影）。
function recognitionModeOptions(
  value: unknown,
): readonly RecognitionModeOptionState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is RecognitionModeOptionState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const option = entry as Partial<RecognitionModeOptionState>;
    return (
      typeof option.id === "string" &&
      typeof option.displayName === "string" &&
      typeof option.availability === "string"
    );
  });
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
    "pdf.page.none": "未处理",
    "pdf.page.processing": "处理中",
    "pdf.page.done": "已完成",
    "pdf.page.failed": "失败",
    "pdf.empty": "尚未建立 PDF 会话",
    "pdf.failed": "PDF 操作失败，请检查文件或重试",
    "pdf.backendUnavailable": "识别服务暂不可用，请检查运行时状态后重试",
    "pdf.outOfMemory": "内存或显存不足，请减少页数或关闭其他任务后重试",
    "pdf.cancelled": "PDF 操作已取消",
    "qrcode.decoded": "识别完成",
    "qrcode.ready": "等待输入",
    "qrcode.running": "正在处理二维码…",
    "qrcode.failed": "图片识别失败，请检查输入图片后重试",
    "qrcode.generateFailed": "二维码生成失败，请重试",
    "qrcode.invalidInput": "内容不符合所选编码格式，请检查下方提示。",
    "qrcode.copied": "已复制当前预览图片",
    "qrcode.noCodes": "当前预览中未识别到支持的二维码或条码",
    "qrcode.decodeUnavailable": "图片识别暂不可用，请稍后重试",
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

export function ImageEditPage({ viewState, actions }: FeatureProps) {
  const [autoTextPreference, setAutoTextPreference] = useState(true);
  const scene = window.location.hash.includes("?scene=1");
  // 宿主命令约定：scene（#/imageEdit?scene=1）仍由 recognition feature 承载
  // 并发 recognition.*；非 scene 独立编辑页读 imageEdit feature，编辑器
  // 会话命令全部改发 imageEdit.*（与 recognition 同形命令）。
  const state = feature(viewState, scene ? "recognition" : "imageEdit");
  const input = resource(state.input);
  const session = screenshotSession(state.screenshotSession);
  const busy = booleanValue(state.isBusy);
  return (
    <Workspace
      eyebrow={scene ? "CAPTURE" : "IMAGE"}
      title={scene ? "截图现场编辑" : "图片编辑"}
      description="本地编辑、缩放与真实格式转换；识别仅在明确点击时执行。"
      actions={
        <>
          {!scene && (
            <>
              <Button
                disabled={!viewState.connected || busy}
                onClick={() => actions.run({ type: "imageEdit.selectImage" })}
                icon={<ImagePlus aria-hidden="true" size={16} />}
              >
                选择图片
              </Button>
              <Button
                disabled={!viewState.connected || busy}
                onClick={() => actions.run({ type: "imageEdit.readClipboard" })}
                icon={<ClipboardPaste aria-hidden="true" size={16} />}
              >
                粘贴图片
              </Button>
            </>
          )}
        </>
      }
    >
      <StatusLine>
        {busy
          ? "正在读取图片…"
          : state.statusCode === "recognition.inputFailed"
            ? "无法读取图片；当前编辑已保留，请检查格式、尺寸和访问权限。"
            : input
              ? "原图保留；默认另存副本。"
              : "选择、粘贴或拖入图片。"}
      </StatusLine>
      {session?.sceneEditing && !scene ? (
        <EmptyStage
          title="正在截图现场编辑"
          detail="请在截图现场窗口继续编辑当前图片。"
        />
      ) : input ? (
        <ImageCanvasEditor
          key={session ? `session-${session.sessionId}` : input.url}
          actions={actions}
          canExport={viewState.capabilities.includes("recognition.annotation")}
          canRecognize={viewState.capabilities.includes(
            "recognition.screenshotSession",
          )}
          source={input.url}
          sourceByteLength={input.byteLength}
          session={session}
          textLayer={screenshotTextLayer(state.textLayer)}
          autoText={
            session?.textSelectionRequested === true && autoTextPreference
          }
          showAutoTextPreference={session?.textSelectionRequested === true}
          onAutoTextChange={setAutoTextPreference}
          commandScope={scene ? "recognition" : "imageEdit"}
          // 纯编辑不展示识别结果；显式识别提交成功后导航到识别承载面。
          // scene 窗口固定路由，由宿主完成会话交接与主窗口导航。
          onRecognitionSubmitted={
            scene ? undefined : () => actions.navigate("recognition")
          }
        />
      ) : (
        <EmptyStage title="等待图片" detail="选择、粘贴或直接拖入图片" />
      )}
    </Workspace>
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
          <CaptureButton
            capabilities={viewState.capabilities}
            actions={actions}
          />
          {/* 先编辑后识别：宿主在 recognition scope 创建同一截图会话与修订，
           * 载入后在共享画布中标注/屏蔽，再用“识别当前图”显式提交掩膜输入。
           * 编辑入口不再借用 imageEdit scope 命令（命令约定已分离）。 */}
          <CapabilityGate
            appearance="secondary"
            capability="recognition.file"
            capabilities={viewState.capabilities}
            action={{ type: "recognition.openImageForEdit" }}
            actions={actions}
            icon={<ImagePlus aria-hidden="true" size={16} />}
          >
            选图编辑
          </CapabilityGate>
          <CapabilityGate
            appearance="secondary"
            capability="recognition.clipboard"
            capabilities={viewState.capabilities}
            action={{ type: "recognition.pasteImageForEdit" }}
            actions={actions}
            icon={<ClipboardPaste aria-hidden="true" size={16} />}
          >
            粘贴编辑
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
          {session?.sceneEditing ? (
            <EmptyStage
              title="正在截图现场编辑"
              detail="请在截图现场窗口继续编辑当前图片。"
            />
          ) : input ? (
            <ImageCanvasEditor
              key={session ? `session-${session.sessionId}` : undefined}
              actions={actions}
              canExport={viewState.capabilities.includes(
                "recognition.annotation",
              )}
              canRecognize={sessionCapable}
              session={session}
              source={input.url}
              sourceByteLength={input.byteLength}
              textLayer={textLayer}
              autoText={
                session?.textSelectionRequested === true && autoTextPreference
              }
              showAutoTextPreference={session?.textSelectionRequested === true}
              onAutoTextChange={setAutoTextPreference}
              toolset="recognition"
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
  const inputKindNotice =
    typeof state.inputKindNotice === "string" && state.inputKindNotice
      ? state.inputKindNotice
      : null;
  // 单选待检查项：仅挂载所选结构化结果，避免窗口内几十项同时拉取。
  const [inspectId, setInspectId] = useState<string | null>(null);
  const inspected = items.find((item) => item.id === inspectId);
  const inspectedStructured = resource(inspected?.structuredResult);
  const inspectedText = useResourceText(inspectedStructured);
  return (
    <Workspace
      eyebrow="QUEUE / 02"
      title="批量识别"
      description="按队列处理图片与文档，并集中检查单项结果。"
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
            添加文件
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
          <p className="batch-scheduling-note">
            页码从 1 开始；留空沿用 MinerU
            默认范围，其他管道默认全部。图片按单页处理。
          </p>
          {inputKindNotice && (
            <p role="status" className="batch-scheduling-note">
              {inputKindNotice}
            </p>
          )}
          {itemCount === 0 ? (
            <EmptyStage
              title="队列为空"
              detail="添加图片或文档后可调整顺序并开始识别。"
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
                      {item.supportsPageRange === false ? (
                        <small>整篇解析；按页识别请先转换为 PDF</small>
                      ) : (
                        <Input
                          key={`${item.id}-${item.pageRange ?? ""}`}
                          size="small"
                          aria-label={`${item.name} 的页码范围`}
                          placeholder="默认页码（all 或 1,3-5）"
                          defaultValue={item.pageRange ?? ""}
                          disabled={running}
                          onBlur={(event) => {
                            const pageRange = event.currentTarget.value.trim();
                            if (pageRange !== (item.pageRange ?? "")) {
                              void actions.run({
                                type: "batch.setItemPageRange",
                                itemId: item.id,
                                pageRange,
                              });
                            }
                          }}
                        />
                      )}
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

export function QrCodePage({ viewState, actions }: FeatureProps) {
  const [tab, setTab] = useState<"generate" | "decode">("generate");
  const [qrText, setQrText] = useState("");
  const [format, setFormat] = useState("qrcode");
  const [captionMode, setCaptionMode] = useState("off");
  const [captionText, setCaptionText] = useState("");
  const autoDecoded = useRef(0);
  const state = feature(viewState, "qrcode");
  const generated = resource(state.generatedResource);
  const previewRevision =
    typeof state.previewRevision === "number" ? state.previewRevision : 0;
  useEffect(() => {
    if (
      tab === "decode" &&
      generated &&
      state.needsPreviewDecode === true &&
      !state.isBusy &&
      previewRevision > autoDecoded.current
    ) {
      autoDecoded.current = previewRevision;
      void actions.run({ type: "qrcode.decodeCurrent", force: false });
    }
  }, [
    tab,
    generated,
    state.needsPreviewDecode,
    state.isBusy,
    previewRevision,
    actions,
  ]);
  const results = qrResults(state.items);
  const isBusy = booleanValue(state.isBusy);
  return (
    <Workspace
      eyebrow="CODE / 04"
      title="二维码与条码"
      description="本地生成二维码与条码，或识别当前预览中的编码。"
    >
      <div className="qr-workspace">
        <Panel label={tab === "generate" ? "GENERATE" : "DECODE"} title="预览">
          {generated ? (
            <img
              className="qr-resource-preview"
              src={generated.url}
              alt="当前二维码与条码预览"
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
            aria-label="二维码与条码模式"
          >
            <Tab value="generate">生成</Tab>
            <Tab value="decode">识别</Tab>
          </TabList>
          {tab === "generate" ? (
            <div className="form-stack">
              <label htmlFor="qr-format">生成格式</label>
              <Select
                id="qr-format"
                value={format}
                onChange={(_, data) => setFormat(data.value)}
              >
                <option value="qrcode">QR Code</option>
                <option value="code128">Code 128</option>
                <option value="ean13">EAN-13</option>
              </Select>
              <p className="form-note">
                {format === "qrcode"
                  ? "支持中文、Unicode 和换行，例如：你好 🌏。"
                  : format === "code128"
                    ? "仅接受 ASCII 字符（0–127），例如：VIBE-128。"
                    : "输入 12 位 ASCII 数字自动补校验位，或 13 位含正确校验位，例如：590123412345。"}
              </p>
              <label htmlFor="qr-content">输入内容</label>
              <textarea
                id="qr-content"
                placeholder="输入要编码的内容"
                value={qrText}
                onChange={(event) => setQrText(event.target.value)}
              />
              <label htmlFor="qr-caption-mode">底部文字</label>
              <Select
                id="qr-caption-mode"
                value={captionMode}
                onChange={(_, data) => setCaptionMode(data.value)}
              >
                <option value="off">关闭</option>
                <option value="payload">显示编码内容</option>
                <option value="custom">独立说明</option>
              </Select>
              {captionMode === "custom" && (
                <>
                  <label htmlFor="qr-caption">独立说明</label>
                  <textarea
                    id="qr-caption"
                    value={captionText}
                    onChange={(event) => setCaptionText(event.target.value)}
                  />
                </>
              )}
              <CapabilityGate
                appearance="primary"
                capability="qrcode.generate"
                capabilities={viewState.capabilities}
                action={{
                  type: "qrcode.generate",
                  text: qrText,
                  format,
                  captionMode,
                  captionText,
                }}
                actions={actions}
                disabled={qrText.trim().length === 0 || isBusy}
                icon={<QrCode aria-hidden="true" size={16} />}
              >
                生成图片
              </CapabilityGate>
              <p className="form-note">
                二维码在本机直接生成，无需启动识别运行环境。
              </p>
            </div>
          ) : (
            <div className="form-stack">
              <p className="form-note">
                识别在本机直接完成，无需启动识别运行环境；
                解码能力已随产品预装：支持 QR Code、Code 128、Code 39、Code
                93、EAN-13/8、UPC-A/E、Codabar、交插 2/5、DataBar
                等常见格式；识别效果取决于图片清晰度与对比度。
              </p>
              <CapabilityGate
                capability="qrcode.decode"
                capabilities={viewState.capabilities}
                action={{ type: "qrcode.decodeCurrent", force: true }}
                actions={actions}
                disabled={!generated || isBusy}
              >
                识别当前预览 / 重新识别
              </CapabilityGate>
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
          <CapabilityGate
            capability="qrcode.copyImage"
            capabilities={viewState.capabilities}
            action={{ type: "qrcode.copyImage" }}
            actions={actions}
            disabled={!generated || isBusy}
            icon={<Copy aria-hidden="true" size={16} />}
          >
            复制图片
          </CapabilityGate>
          <CapabilityGate
            capability="qrcode.save"
            capabilities={viewState.capabilities}
            action={{ type: "qrcode.save" }}
            actions={actions}
            disabled={!generated || isBusy}
            icon={<Save aria-hidden="true" size={16} />}
          >
            保存图片
          </CapabilityGate>
          {state.statusCode === "qrcode.invalidInput" && (
            <p role="alert">{stringValue(state.statusMessage)}</p>
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
              isBusy ? "正在处理二维码与条码…" : "二维码与条码已就绪。",
            )}
          </StatusLine>
        </section>
      </div>
    </Workspace>
  );
}

export function SettingsPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "settings");
  // #195：常规页面直接承载选择→准备→确认；高级区与手动命名/创建空
  // 环境入口移除，必要管理在“已安装环境”列表完成。选择以目录配方 id
  // 唯一绑定（识别组件 + 加速方式），导航回来时从宿主待确认计划恢复。
  const managedEnvironmentsAvailable = viewState.capabilities.includes(
    "runtime.environments",
  );
  const environments = managedEnvironments(state.environments);
  const activeEnvironmentId = stringValue(state.activeEnvironmentId);
  const activeEnvironment = environments.find(
    (environment) => environment.id === activeEnvironmentId,
  );
  const recipes = managedEnvironmentRecipes(state.environmentRecipes);
  const [hasExplicitChoice, setHasExplicitChoice] = useState(false);
  const [queryGeneration, setQueryGeneration] = useState(0);
  const [requestedEnvironmentId, setRequestedEnvironmentId] = useState<
    string | null
  >(null);
  const [catalogChoice, setCatalogChoice] = useState({
    component: "",
    device: "cpu",
  });
  // 本地失效抑制：选择变化→宿主 invalidate 回传之间旧计划不得继续确认。
  const [invalidatedPlanId, setInvalidatedPlanId] = useState<string | null>(
    null,
  );
  const rawPlan = managedEnvironmentPlan(state.environmentPlan);
  const restoredChoice =
    !hasExplicitChoice && rawPlan?.requestedRecipe
      ? recipeDeviceFromId(rawPlan.requestedRecipe)
      : null;
  const choice = restoredChoice
    ? { component: restoredChoice.base, device: restoredChoice.device }
    : catalogChoice;
  const groups = recipeComponentGroups(recipes).groups;
  const effectiveComponent = groups.some(
    (group) => group.key === choice.component,
  )
    ? choice.component
    : (groups[0]?.key ?? "");
  const effectiveGroup = groups.find(
    (group) => group.key === effectiveComponent,
  );
  const devices = [...(effectiveGroup?.byDevice.keys() ?? [])];
  const effectiveDevice = devices.includes(choice.device)
    ? choice.device
    : devices.includes("cpu")
      ? "cpu"
      : (devices[0] ?? "");
  const targetRecipe = effectiveGroup?.byDevice.get(effectiveDevice);
  // 快照已同步但仍无活动环境（初始化失败或默认环境被删）时，如实展示
  // 默认配置“尚未启动”；仅目录/快照未到时才显示“尚未读取”。
  const environmentStatusLine = stringValue(state.environmentStatus);
  const managedRuntimeLoaded =
    recipes.length > 0 ||
    environments.length > 0 ||
    (environmentStatusLine ?? "") !== "";
  const defaultRuntimeRecipe =
    recipes.find((recipe) => recipe.id === "rapidocr-cpu")?.displayName ??
    "RapidOCR · CPU";
  // 远程 MinerU 面板门禁：目录未加载时区分“正在启动”与“无已安装环境
  // 承载识别服务”。后者刷新永远无效，如实指路（随包离线基础配置即可），
  // 不暗示需要本地 MinerU 组件；旧宿主（无运行环境 capability）保留等待语义。
  const mineruServiceHostReady =
    !managedEnvironmentsAvailable || activeEnvironment?.status === "installed";
  const invalidateSelectionPlan = () => {
    setInvalidatedPlanId(rawPlan?.planId ?? null);
    setQueryGeneration((current) => current + 1);
    void actions.run({ type: "settings.invalidateEnvironmentPlan" });
  };
  const onCatalogChoiceChange = (component: string, device: string) => {
    setHasExplicitChoice(true);
    invalidateSelectionPlan();
    setRequestedEnvironmentId(null);
    setCatalogChoice({ component, device });
  };
  // 确认卡只属于当前选择，晚到的旧目标不能进入环境行继续确认。
  const pendingPlan =
    rawPlan &&
    rawPlan.planId !== invalidatedPlanId &&
    rawPlan.requestedRecipe === targetRecipe?.id &&
    (!requestedEnvironmentId ||
      rawPlan.environmentId === requestedEnvironmentId)
      ? rawPlan
      : undefined;
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
  const recognition = mineruRecognition(state.mineruRecognition);
  const defaultMode = defaultRecognitionMode(state.defaultRecognitionMode);
  return (
    <Workspace
      eyebrow="PREFERENCES / 05"
      title="设置"
      description="管理快捷操作、运行环境与下载来源。默认识别类型在此设置，单次/批量/PDF 中的选择仅覆盖当次任务。"
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
          label="RECOGNITION"
          title="识别默认模式"
          className="settings-default-mode-panel"
        >
          <DefaultRecognitionModeEditor
            key={defaultMode?.modeId ?? ""}
            mode={defaultMode}
            modes={recognitionModeOptions(state.recognitionModes)}
            locked={busy}
            hostBusy={booleanValue(state.isBusy)}
            actions={actions}
          />
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
            {managedEnvironmentsAvailable ? (
              <>
                {/* 启动状态未知不伪造就绪也不写“暂不可用”：有活动环境展示
                    实际状态；已同步但未启动展示默认配置；仅加载中才“尚未读取”。 */}
                <strong>
                  {activeEnvironment
                    ? `当前运行环境：${activeEnvironment.name}（${managedEnvironmentSummary(activeEnvironment)}）`
                    : managedRuntimeLoaded
                      ? `默认运行环境：${defaultRuntimeRecipe}（尚未启动）`
                      : "运行环境状态尚未读取"}
                </strong>
                <p>
                  本产品默认随附 RapidOCR · CPU
                  文字识别环境；其他组件与加速方式在下方“识别组件”中按需选择，
                  当前运行任务不受选择影响。
                </p>
              </>
            ) : (
              <>
                <strong>{`目标推理设备：${backendLabel ?? "尚未读取"}`}</strong>
                <p>
                  实际执行设备：尚无实测值。Paddle 设备决策与 GPU
                  回退见“关于与诊断”页。
                </p>
              </>
            )}
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
          <CapabilityGate
            capability="runtime.refresh"
            capabilities={viewState.capabilities}
            action={{ type: "settings.refreshRuntime" }}
            actions={actions}
            icon={<RefreshCw aria-hidden="true" size={16} />}
          >
            重新检查状态
          </CapabilityGate>
          {managedEnvironmentsAvailable ? (
            <>
              <EnvironmentRecommendation
                state={state}
                actions={actions}
                component={effectiveComponent}
                device={effectiveDevice}
                onChoiceChange={(component, device) => {
                  if (
                    component === effectiveComponent &&
                    device === effectiveDevice
                  )
                    return;
                  onCatalogChoiceChange(component, device);
                }}
                queryGeneration={queryGeneration}
                onCancelPlan={invalidateSelectionPlan}
                onPrepare={() =>
                  onCatalogChoiceChange(effectiveComponent, effectiveDevice)
                }
                plan={pendingPlan}
              />
              <InstalledEnvironmentList
                state={state}
                actions={actions}
                selectedRecipeId={targetRecipe?.id ?? ""}
                plan={pendingPlan}
                onPreview={(environmentId, recipe) => {
                  const selected = recipeDeviceFromId(recipe);
                  if (!selected) return;
                  onCatalogChoiceChange(selected.base, selected.device);
                  setRequestedEnvironmentId(environmentId);
                  void actions.run({
                    type: "settings.previewEnvironmentInstall",
                    environmentId,
                    recipe,
                  });
                }}
              />
              <EnvironmentSourceSettings
                state={state}
                busy={state.environmentBusy === true}
                actions={actions}
                onInvalidate={invalidateSelectionPlan}
              />
            </>
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
            locked={
              busy ||
              state.environmentBusy === true ||
              booleanValue(state.isBusy)
            }
            hostBusy={booleanValue(state.isBusy)}
            serviceHostReady={mineruServiceHostReady}
            actions={actions}
          />
        </Panel>
        <Panel
          label="MINERU"
          title="MinerU 识别参数"
          className="settings-mineru-recognition-panel"
        >
          <MineruRecognitionEditor
            key={[
              recognition?.stored ? 1 : 0,
              recognition?.tier ?? "",
              recognition?.ocrMode ?? "",
              recognition?.pageRange ?? "",
              recognition?.language ?? "",
              recognition?.tiers.map((tier) => tier.id).join(","),
              recognition?.languages.join(","),
            ].join("|")}
            recognition={recognition}
            connection={mineru}
            locked={
              busy ||
              state.environmentBusy === true ||
              booleanValue(state.isBusy)
            }
            actions={actions}
          />
        </Panel>
        {/* #136：管理模式下 DOWNLOADS 卡片已迁移删除（来源全部在“环境与依赖”
            内配置）；无该 capability 的旧宿主路径完整保留 SourceSelector。 */}
        {viewState.capabilities.includes("runtime.environments") ? null : (
          <Panel
            label="DOWNLOADS"
            title="下载来源"
            className="settings-download-panel"
          >
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
          </Panel>
        )}
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

// 简要安装确认卡（#195）：只呈现用户决策需要的概要——准备哪组功能、
// 主要组件、目标环境/加速、依赖总数、有效下载来源与影响；完整锁清单、
// 来源端点与目标目录收进只读 details，不成为另一个高级设置区。
// 确认绑定 planId/目标环境/修订，取消（作废本次计划）与安装中取消均可达。
function EnvironmentPlanPanel({
  plan,
  environments,
  sourceName,
  busy,
  canCancelInstall,
  onCancel,
  actions,
}: {
  readonly plan: ManagedEnvironmentPlanState;
  readonly environments: readonly ManagedEnvironmentState[];
  readonly sourceName: (id: string | null | undefined) => string;
  readonly busy: boolean;
  readonly canCancelInstall: boolean;
  readonly onCancel: () => void;
  readonly actions: AppActions;
}) {
  const target = environments.find(
    (environment) => environment.id === plan.environmentId,
  );
  const targetState =
    target?.lastInstallFailure?.phase === "failed"
      ? "失败后重新准备"
      : target?.status === "empty"
        ? "空环境准备"
        : target?.status === "installed"
          ? "已安装环境准备"
          : "目标状态尚未确认";
  return (
    <div className="runtime-install-plan">
      <p>
        计划：{plan.recipe} · 目标
        {target?.name ?? plan.environmentId}（{targetState}）· 下载来源：
        {plan.sourceIds.map((id) => sourceName(id)).join("、") || "产品默认"}
      </p>
      <p className="form-note">
        主要组件：
        {mainDependencies(plan.dependencies).join("、") || "见完整依赖清单"}；
        共 {plan.dependencies.length} 项依赖。
        {plan.dependencyOrigin === "bundled_pack"
          ? "依赖来自随包离线包，无需下载。"
          : "依赖按所选来源安装。"}
      </p>
      <p className="form-note">
        依赖安装只写入此目标环境，不会停止当前识别服务；模型在需要时从所选
        来源下载，已缓存模型直接复用。
      </p>
      <div className="setting-row">
        <Button
          disabled={busy}
          onClick={() =>
            void actions.run({
              type: "settings.confirmEnvironmentInstall",
              planId: plan.planId,
            })
          }
        >
          确认安装依赖
        </Button>
        <Button disabled={busy} onClick={onCancel}>
          取消
        </Button>
        {canCancelInstall ? (
          <Button
            onClick={() =>
              void actions.run({ type: "settings.cancelEnvironmentInstall" })
            }
          >
            取消安装
          </Button>
        ) : null}
      </div>
      <details>
        <summary>完整依赖与来源明细（只读）</summary>
        <p>环境修订：{plan.environmentRevision}</p>
        <p>锁定依赖（{plan.dependencies.length} 项）</p>
        <p>{plan.dependencies.join("、")}</p>
        {(plan.sources ?? []).map((source) => (
          <p key={source.id}>
            {source.displayName}（{sourceKindLabel(source.kind)}
            {source.endpoint ? `；端点 ${source.endpoint}` : ""}）
          </p>
        ))}
        {target ? (
          <p className="form-note">
            Python：{target.python || "未定位"}；环境目录：
            {target.path || "未定位"}
          </p>
        ) : null}
      </details>
    </div>
  );
}

// 已安装环境列表（#195）：常规页面承载必要管理——切换/启动验证、失败
// 修复、旧空环境继续准备与明确确认后的移除；保护沿用 Runtime（活动/被
// 引用/legacy 拒删），失败保留旧健康环境，不宣称清理未证实资源。
function InstalledEnvironmentList({
  state,
  actions,
  selectedRecipeId,
  plan,
  onPreview,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly actions: AppActions;
  readonly selectedRecipeId: string;
  readonly plan: ManagedEnvironmentPlanState | undefined;
  readonly onPreview: (environmentId: string, recipe: string) => void;
}) {
  const environments = managedEnvironments(state.environments);
  const activeId = stringValue(state.activeEnvironmentId);
  const busy = state.environmentBusy === true;
  const canCancelInstall = state.environmentCanCancelInstall === true;
  const catalog = managedSourceOptions(state.environmentSources);
  const sourceName = (id: string | null | undefined): string =>
    id == null || id === ""
      ? "官方默认（端点未知）"
      : (catalog.find((source) => source.id === id)?.displayName ?? id);
  const [confirmingRemoveId, setConfirmingRemoveId] = useState<string | null>(
    null,
  );
  return (
    <section className="managed-environment-list" aria-label="已安装环境">
      <h3>已安装环境</h3>
      {environments.length === 0 ? (
        <p className="form-note">
          尚无环境；可在上方“识别组件”中准备第一个配置。
        </p>
      ) : (
        environments.map((environment) => {
          const failure = environment.lastInstallFailure;
          const continueRecipe =
            environment.status === "empty"
              ? selectedRecipeId
              : failure?.phase === "failed"
                ? failure.recipe
                : "";
          const removeAllowed =
            environment.kind !== "legacy" && environment.id !== activeId;
          return (
            <div
              key={environment.id}
              className="managed-environment-item"
              data-environment-id={environment.id}
            >
              <p>
                <strong>{environment.name}</strong>
                {environment.id === activeId ? " · 当前使用" : ""}（修订{" "}
                {environment.revision}，{managedEnvironmentSummary(environment)}
                ）
              </p>
              <p>
                状态：{environment.status === "empty" ? "空环境" : "已安装依赖"}{" "}
                · Python {environment.pythonState} · 依赖{" "}
                {environment.dependencyState}
              </p>
              <p>
                引擎 {environment.engineState} · 模型 {environment.modelState} ·
                服务 {environment.serviceState}
              </p>
              <p>
                已配置识别：
                {recognitionPurposeLabel(
                  environment.configuredRecognitionTypes ?? [],
                ) || "无"}
                ；目标设备：{environment.targetDevice || "未设置"}；实际设备：
                {environment.actualDevice || "未报告实际执行设备"}
              </p>
              <details>
                <summary>环境明细（只读）</summary>
                <p className="form-note">
                  解释器：{environment.pythonVersion || "未验证"} · ABI{" "}
                  {environment.abi || "未验证"}
                  ；占用：
                  {typeof environment.diskBytes === "number"
                    ? `${(environment.diskBytes / 1024 / 1024).toFixed(1)} MiB`
                    : "未检查"}
                  ；Python：{environment.python || "未定位"}；环境目录：
                  {environment.path || "未定位"}
                </p>
              </details>
              {environment.reason ? (
                <p role="alert">{environment.reason}</p>
              ) : null}
              {failure ? (
                <p role={failure.phase === "failed" ? "alert" : "status"}>
                  {failure.phase === "failed"
                    ? "上次依赖安装未完成"
                    : "依赖安装进行中"}
                  （环境修订 {failure.environmentRevision}，{failure.recipe}）：
                  {failure.detail}（原因：{failure.reasonCode}；建议：
                  {environmentRecoveryActions[failure.nextAction] ??
                    failure.nextAction}
                  ）
                  {failure.effectiveSourceIds?.length
                    ? `；请求源：${
                        failure.requestedSourceIds == null
                          ? "跟随配置"
                          : failure.requestedSourceIds
                              .map((id) => sourceName(id))
                              .join("、")
                      }；生效源：${failure.effectiveSourceIds
                        .map((id) => sourceName(id))
                        .join("、")}`
                    : ""}
                </p>
              ) : null}
              <div className="setting-row">
                {environment.id !== activeId ||
                (environment.status === "installed" &&
                  environment.serviceState !== "ready") ? (
                  <Button
                    disabled={busy}
                    onClick={() =>
                      void actions.run({
                        type: "settings.switchEnvironment",
                        environmentId: environment.id,
                      })
                    }
                  >
                    {environment.id === activeId
                      ? "启动并验证当前环境"
                      : "切换到此环境"}
                  </Button>
                ) : null}
                {continueRecipe ? (
                  <Button
                    disabled={busy}
                    onClick={() => onPreview(environment.id, continueRecipe)}
                  >
                    {environment.status === "empty"
                      ? "继续准备依赖"
                      : "重新准备依赖"}
                  </Button>
                ) : null}
                {environment.status === "empty" &&
                environment.pythonState !== "ready" ? (
                  <Button
                    disabled={busy}
                    onClick={() =>
                      void actions.run({
                        type: "settings.repairEmptyEnvironment",
                        environmentId: environment.id,
                      })
                    }
                  >
                    修复空环境 Python
                  </Button>
                ) : null}
                {removeAllowed ? (
                  confirmingRemoveId === environment.id ? (
                    <>
                      <Button
                        disabled={busy}
                        onClick={() => {
                          setConfirmingRemoveId(null);
                          void actions.run({
                            type: "settings.deleteEnvironment",
                            environmentId: environment.id,
                          });
                        }}
                      >
                        确认移除
                      </Button>
                      <Button
                        disabled={busy}
                        onClick={() => setConfirmingRemoveId(null)}
                      >
                        取消移除
                      </Button>
                    </>
                  ) : (
                    <Button
                      icon={<Trash2 aria-hidden="true" size={16} />}
                      disabled={busy}
                      onClick={() => setConfirmingRemoveId(environment.id)}
                    >
                      移除环境
                    </Button>
                  )
                ) : null}
              </div>
            </div>
          );
        })
      )}
      {canCancelInstall && !plan ? (
        <Button
          onClick={() =>
            void actions.run({ type: "settings.cancelEnvironmentInstall" })
          }
        >
          取消安装
        </Button>
      ) : null}
      <EnvironmentCleanup state={state} busy={busy} actions={actions} />
      <EnvironmentInstallDetails state={state} />
      <p role="status">{stringValue(state.environmentStatus) ?? ""}</p>
    </section>
  );
}

interface CleanupItem {
  readonly id: string;
  readonly category: string;
  readonly label: string;
  readonly environment_id: string | null;
  readonly logical_bytes: number | null;
  readonly can_clean: boolean;
  readonly reason: string;
  readonly paths: readonly string[];
  readonly last_error?: string;
  readonly path_count?: number;
}
interface CleanupPlan {
  readonly plan_id: string;
  readonly items: readonly CleanupItem[];
  readonly warning: string;
}
interface CleanupResult {
  readonly items: readonly {
    readonly id: string;
    readonly state: string;
    readonly detail: string;
    readonly removed_logical_bytes: number;
  }[];
}

function EnvironmentCleanup({
  state,
  busy,
  actions,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly busy: boolean;
  readonly actions: AppActions;
}) {
  if (state.environmentSupportsCleanup !== true) return null;
  const plan = state.environmentCleanupPlan as CleanupPlan | null | undefined;
  const result = state.environmentCleanupResult as
    CleanupResult | null | undefined;
  const page =
    typeof state.environmentCleanupPage === "number"
      ? state.environmentCleanupPage
      : 0;
  const pageCount =
    typeof state.environmentCleanupPageCount === "number"
      ? state.environmentCleanupPageCount
      : 1;
  const outcomes: Readonly<Record<string, string>> = {
    deleted: "已移除",
    failed: "失败，可重查续清",
    cancelled: "已取消，可重查续清",
  };
  return (
    <section aria-label="空间清理">
      <h4>空间清理</h4>
      <p className="form-note">
        非当前环境可能仍有用途，请自行选择需要保留的配置。仅处理有明确归属的环境、准备残留和无保留引用的依赖下载工件；模型、基础
        Python 和未知目录受保护。
      </p>
      <Button
        disabled={busy}
        onClick={() =>
          void actions.run({ type: "settings.previewEnvironmentCleanup" })
        }
      >
        检查可清理项
      </Button>
      {pageCount > 1 ? (
        <div aria-label="清理分页">
          <Button
            disabled={busy || page === 0}
            onClick={() =>
              void actions.run({
                type: "settings.setEnvironmentCleanupPage",
                page: page - 1,
              })
            }
          >
            上一页清理项目
          </Button>
          <span>
            第 {page + 1} / {pageCount} 页；仅清理本页明确选择的项目
          </span>
          <Button
            disabled={busy || page + 1 >= pageCount}
            onClick={() =>
              void actions.run({
                type: "settings.setEnvironmentCleanupPage",
                page: page + 1,
              })
            }
          >
            下一页清理项目
          </Button>
        </div>
      ) : null}
      {plan && Array.isArray(plan.items) ? (
        <CleanupSelection
          key={`${plan.plan_id}:${page}`}
          plan={plan}
          busy={busy}
          actions={actions}
        />
      ) : null}
      {state.environmentCanCancelCleanup === true ? (
        <Button
          onClick={() =>
            void actions.run({ type: "settings.cancelEnvironmentCleanup" })
          }
        >
          取消清理
        </Button>
      ) : null}
      {result && Array.isArray(result.items) ? (
        <ul aria-label="清理结果">
          {result.items.map((item) => (
            <li
              key={item.id}
              role={item.state === "failed" ? "alert" : "status"}
            >
              {outcomes[item.state] ?? item.state}：{item.detail}
              ；已移除逻辑字节{" "}
              {(item.removed_logical_bytes / 1024 / 1024).toFixed(1)} MiB
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}

function CleanupSelection({
  plan,
  busy,
  actions,
}: {
  readonly plan: CleanupPlan;
  readonly busy: boolean;
  readonly actions: AppActions;
}) {
  const [selected, setSelected] = useState<readonly string[]>([]);
  const [confirming, setConfirming] = useState(false);
  const chosen = plan.items.filter(
    (item) => selected.includes(item.id) && item.can_clean,
  );
  const categories: Readonly<Record<string, string>> = {
    environment: "环境及专属依赖",
    residual: "已记录残留",
    dependency_cache: "共享依赖下载缓存",
    models: "模型",
    python_base: "基础 Python",
    unknown: "未知目录",
    cache: "其他缓存",
    legacy: "原有环境",
  };
  return (
    <div>
      <ul aria-label="可清理项">
        {plan.items.map((item) => (
          <li key={item.id}>
            <Checkbox
              disabled={busy || !item.can_clean}
              checked={selected.includes(item.id)}
              label={`${item.label} · ${categories[item.category] ?? item.category}`}
              onChange={(_, data) => {
                setConfirming(false);
                setSelected(
                  data.checked === true
                    ? [...selected, item.id]
                    : selected.filter((id) => id !== item.id),
                );
              }}
            />
            <p>
              {item.can_clean ? "可选择" : "受保护"}：{item.reason}
              ；占用（逻辑字节）
              {typeof item.logical_bytes === "number"
                ? `${(item.logical_bytes / 1024 / 1024).toFixed(1)} MiB`
                : "未知"}
            </p>
            {item.last_error ? (
              <p role="alert">上次未完成：{item.last_error}</p>
            ) : null}
            <details>
              <summary>
                路径摘要（只读，显示首项，长文本省略，共{" "}
                {item.path_count ?? item.paths.length} 项）
              </summary>
              {item.paths.map((path) => (
                <p key={path}>{path}</p>
              ))}
            </details>
          </li>
        ))}
      </ul>
      <p>{plan.warning}</p>
      {confirming ? (
        <div role="alert">
          <p>
            确认移除所选 {chosen.length} 项：
            {chosen.map((item) => item.label).join("、")}
            。环境及专属依赖删除后不可撤销，缓存以后可能需要重新下载；统计为逻辑字节，不保证物理磁盘释放。
          </p>
          <Button
            disabled={busy || chosen.length === 0}
            onClick={() => {
              setConfirming(false);
              void actions.run({
                type: "settings.runEnvironmentCleanup",
                planId: plan.plan_id,
                itemIds: chosen.map((item) => item.id),
              });
            }}
          >
            确认清理所选项目
          </Button>
          <Button disabled={busy} onClick={() => setConfirming(false)}>
            返回选择
          </Button>
        </div>
      ) : (
        <Button
          disabled={busy || chosen.length === 0}
          onClick={() => setConfirming(true)}
        >
          预览所选清理影响
        </Button>
      )}
    </div>
  );
}

// 来源收敛为单一简单设置：依赖包与模型各选一个来源，语义仅“安装依赖”
// 与“下载模型”；不暴露产品默认/全局默认/环境覆盖三层，也不提供按次
// 或按环境的来源覆盖。默认值跟随 Runtime 目录与全局默认解析结果。
interface EnvironmentInstallProgress {
  readonly attempt_id: string;
  readonly seq: number;
  readonly environment_id: string;
  readonly timestamp: string;
  readonly phase: string;
  readonly state: string;
  readonly current: string;
  readonly dependency_total_known: boolean;
  readonly dependencies: readonly {
    name: string;
    version: string | null;
    download_state: string;
    install_state: string;
  }[];
  readonly download_files_total: number | null;
  readonly download_files_completed: number;
}

function EnvironmentInstallDetails({
  state,
}: {
  readonly state: Readonly<Record<string, unknown>>;
}) {
  const value = state.environmentInstallProgress;
  if (!value || typeof value !== "object") {
    return state.environmentCanCancelInstall === true &&
      state.environmentSupportsInstallProgress !== true ? (
      <p role="status">
        当前运行组件不支持实时明细；安装结果仍以环境记录为准。
      </p>
    ) : null;
  }
  const progress = value as EnvironmentInstallProgress;
  if (!Array.isArray(progress.dependencies)) return null;
  const phases: Readonly<Record<string, string>> = {
    prepare: "准备",
    resolve: "解析依赖",
    unpack: "解包",
    download: "下载",
    install: "安装依赖批次",
    runtime_wheel: "安装内部 Runtime wheel",
    verify: "验证",
    complete: "完成",
  };
  const states: Readonly<Record<string, string>> = {
    running: "进行中",
    succeeded: "已完成",
    failed: "失败",
    cancelled: "取消",
  };
  const origins: Readonly<Record<string, string>> = {
    pending: "待处理",
    bundled: "随包可用",
    cached: "缓存复用",
    downloading: "正在下载",
    downloaded: "已新增下载",
  };
  const reused = progress.dependencies.filter((item) =>
    ["bundled", "cached"].includes(item.download_state),
  ).length;
  const installed = progress.dependencies.filter(
    (item) => item.install_state === "installed",
  ).length;
  const logs = Array.isArray(state.environmentInstallLog)
    ? state.environmentInstallLog.filter(
        (line): line is string => typeof line === "string",
      )
    : [];
  return (
    <section
      className="environment-install-progress"
      aria-label="依赖安装进度"
      data-install-state={progress.state}
      data-install-phase={progress.phase}
      data-install-attempt={progress.attempt_id}
      data-install-seq={progress.seq}
      data-install-environment={progress.environment_id}
      data-install-installed={installed}
      data-install-total={
        progress.dependency_total_known
          ? progress.dependencies.length
          : "unknown"
      }
    >
      <h4>
        {phases[progress.phase] ?? progress.phase} ·{" "}
        {states[progress.state] ?? progress.state}
      </h4>
      <p role="status">
        共{" "}
        {progress.dependency_total_known
          ? progress.dependencies.length
          : "正在解析 / 未知"}{" "}
        项依赖｜已复用 {reused} 项｜新增下载文件{" "}
        {progress.download_files_completed}/
        {progress.download_files_total ?? "未知"} 个｜批次已安装 {installed}/
        {progress.dependency_total_known
          ? progress.dependencies.length
          : "未知"}{" "}
        项
      </p>
      <p>
        当前：{progress.current || "等待安装事件"}；最后事件：
        {progress.timestamp}
      </p>
      <p className="form-note">
        包数与下载文件数分别统计；Python 和内部 Runtime wheel
        随产品提供，模型尚未下载。安装批次成功后才确认依赖，整体完成须通过验证与提交。
        源分发包的隔离构建临时依赖按所选来源获取，单独于目标包与目标工件下载数量。
      </p>
      <details>
        <summary>依赖状态（只读）</summary>
        <ul>
          {progress.dependencies.map((item) => (
            <li key={`${item.name}==${item.version ?? "unknown"}`}>
              {item.name} {item.version ?? "版本未知"}：
              {origins[item.download_state] ?? item.download_state}；
              {item.install_state === "installed" ? "已安装" : "等待批次成功"}
            </li>
          ))}
        </ul>
      </details>
      <details>
        <summary>实时命令输出（只读，最多保留 200 行，超限截断）</summary>
        <pre
          tabIndex={0}
          style={{
            maxHeight: "20rem",
            overflow: "auto",
            whiteSpace: "pre-wrap",
            overflowWrap: "anywhere",
          }}
        >
          {logs.join("\n") || "等待命令输出…"}
        </pre>
      </details>
    </section>
  );
}

function EnvironmentSourceSettings({
  state,
  busy,
  actions,
  onInvalidate,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly busy: boolean;
  readonly actions: AppActions;
  readonly onInvalidate: () => void;
}) {
  const catalog = managedSourceOptions(state.environmentSources);
  const packageSources = catalog.length
    ? catalog.filter((source) => source.kind === "package_index")
    : stringValues(state.environmentPackageSourceIds).map((id) => ({
        id,
        kind: "package_index",
        displayName: id,
        endpoint: "",
        isDefault: false,
      }));
  const paddleSources = catalog.filter(
    (source) => source.kind === "paddleocr_model_registry",
  );
  const mineruSources = catalog.filter(
    (source) => source.kind === "mineru_model_registry",
  );
  const sharedModelSources = catalog.filter(
    (source) => source.kind === "model_registry",
  );
  const independent = paddleSources.length > 0 || mineruSources.length > 0;
  const modelProviderOf = (id: string | null | undefined): string =>
    id == null
      ? ""
      : id.startsWith("paddleocr-")
        ? id.slice("paddleocr-".length)
        : id.startsWith("mineru-")
          ? id.slice("mineru-".length)
          : id;
  const defaultIds = stringValues(state.environmentDefaultSourceIds);
  const resolvedDefaults = managedResolvedSources(
    state.environmentResolvedDefaultSources,
  );
  const resolvedId = (kind: string): string =>
    defaultIds.find((id) =>
      catalog.some((source) => source.id === id && source.kind === kind),
    ) ??
    resolvedDefaults.find((source) => source.kind === kind)?.id ??
    catalog.find((source) => source.kind === kind && source.isDefault)?.id ??
    "";
  const [edit, setEdit] = useState<{
    packageId?: string;
    modelProvider?: string;
  }>({});
  const packageValue = edit.packageId ?? resolvedId("package_index");
  // 模型统一选择“提供方”（魔搭/Hugging Face）：两类引擎的解析默认一致时
  // 直接回显；不一致时如实标注，保存后统一为所选提供方。
  const resolvedProviders = [
    modelProviderOf(resolvedId("paddleocr_model_registry")),
    modelProviderOf(resolvedId("mineru_model_registry")),
  ].filter((provider) => provider !== "");
  const unifiedModelProvider =
    resolvedProviders.length === 0
      ? ""
      : resolvedProviders.every((provider) => provider === resolvedProviders[0])
        ? resolvedProviders[0]
        : "";
  const modelMixed =
    independent &&
    edit.modelProvider === undefined &&
    unifiedModelProvider === "" &&
    resolvedProviders.length > 1;
  const modelValue =
    edit.modelProvider ??
    (independent ? unifiedModelProvider : resolvedId("model_registry"));
  // 统一选项 = 两类引擎都声明的提供方；旧宿主只有共享 model_registry 时
  // 直接列目录 id。
  const providerOptions = independent
    ? paddleSources.filter((source) =>
        mineruSources.some(
          (mineru) => modelProviderOf(mineru.id) === modelProviderOf(source.id),
        ),
      )
    : sharedModelSources;
  const providerLabel = (id: string, displayName: string): string =>
    modelProviderOf(id) === "modelscope" && displayName === "ModelScope"
      ? "ModelScope（魔搭）"
      : displayName;
  const save = async () => {
    onInvalidate();
    const ok = await actions.run(
      independent
        ? {
            type: "settings.setEnvironmentSources",
            packageSourceId: packageValue || null,
            paddleocrModelSourceId: paddleSources.some(
              (source) => source.id === `paddleocr-${modelValue}`,
            )
              ? `paddleocr-${modelValue}`
              : null,
            mineruModelSourceId: mineruSources.some(
              (source) => source.id === `mineru-${modelValue}`,
            )
              ? `mineru-${modelValue}`
              : null,
          }
        : {
            type: "settings.setEnvironmentSources",
            packageSourceId: packageValue || null,
            modelSourceId: modelValue || null,
          },
    );
    if (ok) setEdit({});
  };
  return (
    <section className="environment-source-settings" aria-label="下载来源">
      <h3>下载来源（依赖与模型）</h3>
      <p className="form-note">
        仅用于安装依赖与下载模型：保存只写配置，不影响已安装环境与
        当前运行的服务。
      </p>
      {packageSources.length === 0 ? (
        <p className="form-note">
          下载源目录尚未同步；可点击“重新检查状态”后再配置。
        </p>
      ) : (
        <>
          <div className="setting-row">
            <label htmlFor="settings-package-source">依赖包来源</label>
            <Select
              id="settings-package-source"
              value={packageValue}
              disabled={busy}
              onChange={(_, data) =>
                setEdit((current) => ({
                  ...current,
                  packageId: String(data.value),
                }))
              }
            >
              {packageSources.map((source) => (
                <option key={source.id} value={source.id}>
                  {source.displayName}
                  {source.isDefault ? "（默认）" : ""}
                </option>
              ))}
            </Select>
          </div>
          {independent ? (
            providerOptions.length > 0 && (
              <div className="setting-row">
                <label htmlFor="settings-model-source">模型来源</label>
                <Select
                  id="settings-model-source"
                  value={modelValue}
                  disabled={busy}
                  onChange={(_, data) =>
                    setEdit((current) => ({
                      ...current,
                      modelProvider: String(data.value),
                    }))
                  }
                >
                  {modelMixed ? (
                    <option value="">当前不一致（保存后统一）</option>
                  ) : null}
                  {providerOptions.map((source) => (
                    <option
                      key={modelProviderOf(source.id)}
                      value={modelProviderOf(source.id)}
                    >
                      {providerLabel(source.id, source.displayName)}
                    </option>
                  ))}
                </Select>
              </div>
            )
          ) : sharedModelSources.length > 0 ? (
            <div className="setting-row">
              <label htmlFor="settings-model-source">模型来源</label>
              <Select
                id="settings-model-source"
                value={modelValue}
                disabled={busy}
                onChange={(_, data) =>
                  setEdit((current) => ({
                    ...current,
                    modelProvider: String(data.value),
                  }))
                }
              >
                {sharedModelSources.map((source) => (
                  <option key={source.id} value={source.id}>
                    {providerLabel(source.id, source.displayName)}
                  </option>
                ))}
              </Select>
            </div>
          ) : null}
          {modelMixed ? (
            <p className="form-note" role="note">
              当前 PaddleOCR 与 MinerU
              的模型来源不一致；选择后保存将统一为同一提供方。
            </p>
          ) : null}
          <div className="setting-row">
            <Button disabled={busy} onClick={() => void save()}>
              保存下载来源
            </Button>
          </div>
        </>
      )}
    </section>
  );
}

function EnvironmentRecommendation({
  state,
  actions,
  component,
  device,
  onChoiceChange,
  queryGeneration,
  onCancelPlan,
  onPrepare,
  plan,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly actions: AppActions;
  readonly component: string;
  readonly device: string;
  readonly onChoiceChange: (component: string, device: string) => void;
  readonly queryGeneration: number;
  readonly onCancelPlan: () => void;
  readonly onPrepare: () => void;
  readonly plan: ManagedEnvironmentPlanState | undefined;
}) {
  const recipes = managedEnvironmentRecipes(state.environmentRecipes);
  const catalog = recipeComponentGroups(recipes);
  const group = catalog.groups.find((item) => item.key === component);
  const target = group?.byDevice.get(device);
  const hardware = managedEnvironmentHardware(state.environmentHardware);
  const environments = managedEnvironments(state.environments);
  const activeId = stringValue(state.activeEnvironmentId);
  const busy = state.environmentBusy === true;
  const sources = managedSourceOptions(state.environmentSources);
  const compatibility = managedEnvironmentCompatibility(
    state.environmentCompatibility,
  );
  const blockedReason =
    target?.accelerator !== "nvidia_cuda"
      ? null
      : hardware.nvidiaDriverStatus === "ok"
        ? null
        : hardware.nvidiaDriverStatus === "unsupported"
          ? hardware.nvidiaDriverReason === "nvidia_driver_incompatible"
            ? `不支持：NVIDIA 驱动 ${hardware.nvidiaDriverVersion ?? ""} 低于 CUDA 12.x 下限（需 ≥ 528.33）`
            : "不支持：未检测到可用的 NVIDIA 驱动"
          : "暂不可选：GPU 状态未探测（可稍后重新检查状态）";
  const targetId = target?.id ?? "";
  const compatibilityRecipe = compatibility?.recipe ?? "";
  const queryResult =
    compatibility?.recipe === targetId ? compatibility : undefined;
  const compatibleEnvironment = queryResult?.selectedEnvironmentId
    ? environments.find((item) => item.id === queryResult.selectedEnvironmentId)
    : undefined;
  const activeReady =
    compatibleEnvironment?.id === activeId &&
    compatibleEnvironment?.status === "installed" &&
    compatibleEnvironment.serviceState === "ready";
  const pendingPlan = plan?.requestedRecipe === targetId ? plan : undefined;
  const preparing = useRef(false);
  const prepare = async () => {
    if (!targetId || preparing.current) return;
    preparing.current = true;
    onPrepare();
    try {
      await actions.run({
        type: "settings.prepareEnvironment",
        recipe: targetId,
      });
    } finally {
      preparing.current = false;
    }
  };
  const environmentsFingerprint = environments
    .map(
      (environment) =>
        `${environment.id}:${environment.revision}:${environment.status}`,
    )
    .join("|");
  const [dispatchedQuery, setDispatchedQuery] = useState<{
    recipe: string;
    fingerprint: string;
    generation: number;
  } | null>(null);
  // 在途闸门只供 effect/事件处理器读写，不在渲染中读取（react-hooks/refs）。
  const inFlightQuery = useRef<string | null>(null);
  const queryKey = `${targetId}|${environmentsFingerprint}|${queryGeneration}`;
  useEffect(() => {
    if (!targetId || busy || blockedReason) return;
    if (compatibilityRecipe === targetId) return;
    if (
      dispatchedQuery?.recipe === targetId &&
      dispatchedQuery.fingerprint === environmentsFingerprint &&
      dispatchedQuery.generation === queryGeneration
    )
      return;
    if (inFlightQuery.current === queryKey) return;
    setDispatchedQuery({
      recipe: targetId,
      fingerprint: environmentsFingerprint,
      generation: queryGeneration,
    });
    inFlightQuery.current = queryKey;
    const settle = () => {
      if (inFlightQuery.current === queryKey) inFlightQuery.current = null;
    };
    void Promise.resolve(
      actions.run({
        type: "settings.findCompatibleEnvironment",
        recipe: targetId,
      }),
    ).then(settle, settle);
  }, [
    targetId,
    busy,
    blockedReason,
    compatibilityRecipe,
    environmentsFingerprint,
    queryGeneration,
    queryKey,
    dispatchedQuery,
    actions,
  ]);
  const retryQuery = () => {
    if (!targetId || inFlightQuery.current === queryKey) return;
    setDispatchedQuery({
      recipe: targetId,
      fingerprint: environmentsFingerprint,
      generation: queryGeneration,
    });
    inFlightQuery.current = queryKey;
    const settle = () => {
      if (inFlightQuery.current === queryKey) inFlightQuery.current = null;
    };
    void Promise.resolve(
      actions.run({
        type: "settings.findCompatibleEnvironment",
        recipe: targetId,
      }),
    ).then(settle, settle);
  };
  const queryPending =
    !!targetId &&
    dispatchedQuery?.recipe === targetId &&
    compatibilityRecipe !== targetId;
  // 宿主清空结果且环境状态已变（马上会自动补发一次新查询）时，如实显示
  // “正在查询”，不闪现误导性的“查询未完成”重试入口。
  const queryDispatchExpected =
    !!targetId &&
    !busy &&
    !blockedReason &&
    compatibilityRecipe !== targetId &&
    (dispatchedQuery?.recipe !== targetId ||
      dispatchedQuery.fingerprint !== environmentsFingerprint ||
      dispatchedQuery.generation !== queryGeneration);
  return (
    <section className="environment-recommendation" aria-label="环境配置">
      <h3>识别组件</h3>
      <p className="form-note">
        选择待准备或切换的环境配置，不改变识别默认模式或正在执行的任务。选择只查询兼容环境，明确确认后才安装。
      </p>
      {catalog.problems.map((problem) => (
        <p role="alert" key={problem}>
          {problem}
        </p>
      ))}
      {catalog.groups.length === 0 ? (
        <p className="form-note">
          运行环境目录尚未同步或没有可用配方；可点击“重新检查状态”后再试。
        </p>
      ) : (
        <>
          <div className="setting-row">
            <label htmlFor="environment-component-select">识别组件</label>
            <Select
              id="environment-component-select"
              value={component}
              disabled={busy}
              onChange={(_, data) => onChoiceChange(String(data.value), device)}
            >
              {catalog.groups.map((item) => (
                <option key={item.key} value={item.key}>
                  {item.title} · {item.purposeLabel || "用途未声明"}
                </option>
              ))}
            </Select>
          </div>
          <div className="setting-row">
            <label htmlFor="environment-device-select">加速方式</label>
            <Select
              id="environment-device-select"
              value={device}
              disabled={busy || !group}
              onChange={(_, data) =>
                onChoiceChange(component, String(data.value))
              }
            >
              {[...(group?.byDevice.keys() ?? [])].map((key) => (
                <option key={key} value={key}>
                  {key === "nvidia_cuda" ? "NVIDIA GPU（CUDA）" : "CPU（通用）"}
                </option>
              ))}
            </Select>
          </div>
          {target ? (
            <>
              <p>
                目标配置：{target.displayName}（
                {recognitionPurposeLabel(
                  target.configuredRecognitionTypes ?? [],
                ) || "用途未知"}
                ，设备 {target.targetDevice ?? "未知"}，Python{" "}
                {target.pythonVersion ?? "未知"}）
              </p>
              <p className="form-note">
                主要依赖：
                {mainDependencies(target.dependencies).join("、") ||
                  "目录未提供主要依赖摘要"}
                ；
                {target.dependencies
                  ? `${target.dependencies.length} 项锁定依赖。`
                  : "依赖总数待目录同步。"}
                模型按需准备，实际执行设备尚未验证。
              </p>
              <p className="form-note">
                CPU 适用于通用识别。GPU 仅作用于配方支持的组件；组合配方不代表
                RapidOCR 使用 GPU，MinerU 的默认路径也不因包含 torch 就保证使用
                GPU。
              </p>
            </>
          ) : null}
          <p className="environment-hardware-note">
            GPU 状态（Runtime 探测）：
            {hardware.nvidiaDriverStatus === "ok"
              ? `可用（驱动 ${hardware.nvidiaDriverVersion ?? "未知版本"}）`
              : hardware.nvidiaDriverStatus === "unsupported"
                ? "不支持"
                : "未探测"}
          </p>
          <p className="form-note">
            远程 MinerU 使用远端依赖，无需安装本地 MinerU 或 GPU
            配方；连接在下方设置。
          </p>
          {blockedReason ? (
            <p role="alert">{blockedReason}；该配置当前不可选。</p>
          ) : target ? (
            <>
              {queryResult ? (
                compatibleEnvironment ? (
                  <p>
                    可直接复用环境「{compatibleEnvironment.name}」（修订{" "}
                    {compatibleEnvironment.revision}，
                    {queryResult.selectionReason === "active_environment"
                      ? "当前活动环境"
                      : "按稳定顺序选中"}
                    ）；切换前不安装任何内容。
                  </p>
                ) : (
                  <p>没有可直接复用的已安装环境（均为空、不匹配或未验证）。</p>
                )
              ) : queryPending && !busy && !queryDispatchExpected ? (
                <div className="setting-row">
                  <span className="form-note">
                    兼容性查询未完成（未收到结果）；可重试。
                  </span>
                  <Button onClick={retryQuery}>重新查询兼容环境</Button>
                </div>
              ) : (
                <p className="form-note">正在查询可复用环境…</p>
              )}
              {activeReady ? (
                <p role="status">
                  该兼容环境已是当前环境并通过启动验证，无需重装。
                </p>
              ) : compatibleEnvironment ? (
                <>
                  <Button
                    disabled={busy}
                    onClick={() =>
                      void actions.run({
                        type: "settings.switchEnvironment",
                        environmentId: compatibleEnvironment.id,
                      })
                    }
                  >
                    {compatibleEnvironment.id === activeId
                      ? "启动并验证当前环境"
                      : "切换到此环境并启动验证"}
                  </Button>
                  <p className="form-note">
                    切换会重新启动并验证识别服务；有运行中任务时切换会被拒绝。
                  </p>
                </>
              ) : queryResult && !pendingPlan ? (
                <div className="setting-row">
                  <Button disabled={busy} onClick={() => void prepare()}>
                    准备此配置
                  </Button>
                  <span className="form-note">
                    自动准备目标环境并预览依赖；确认后才会安装，重试复用同一准备环境。
                  </span>
                </div>
              ) : null}
            </>
          ) : null}
          {pendingPlan ? (
            <EnvironmentPlanPanel
              plan={pendingPlan}
              environments={environments}
              sourceName={(id) =>
                sources.find((source) => source.id === id)?.displayName ??
                id ??
                "产品默认"
              }
              busy={busy}
              canCancelInstall={state.environmentCanCancelInstall === true}
              onCancel={onCancelPlan}
              actions={actions}
            />
          ) : null}
        </>
      )}
    </section>
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
  const modelKinds = [
    { kind: "paddleocr_model_registry", label: "PaddleOCR 模型来源" },
    { kind: "mineru_model_registry", label: "MinerU 模型来源" },
    ...(!sources.some(
      (source) =>
        source.kind === "paddleocr_model_registry" ||
        source.kind === "mineru_model_registry",
    )
      ? [{ kind: "model_registry", label: "模型下载源" }]
      : []),
  ];
  const modelSources = sources.filter((source) =>
    modelKinds.some(({ kind }) => source.kind === kind),
  );
  if (packageSources.length === 0 && modelSources.length === 0) {
    return (
      <p className="form-note">
        {loaded
          ? "当前识别服务未提供下载源目录。"
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
      {modelKinds.map(({ kind, label }) => (
        <SourceKindSelector
          key={kind}
          kind={kind}
          label={label}
          sources={modelSources.filter((source) => source.kind === kind)}
          enabled={enabled}
          locked={locked}
          actions={actions}
        />
      ))}
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
        <option value="">跟随识别服务默认</option>
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

function MineruRecognitionEditor({
  recognition,
  connection,
  locked,
  actions,
}: {
  readonly recognition: MineruRecognitionState | undefined;
  readonly connection: MineruConnectionState | undefined;
  readonly locked: boolean;
  readonly actions: AppActions;
}) {
  const loaded = recognition !== undefined;
  const supported = recognition?.supported === true;
  const tiers = recognition?.tiers ?? [];
  const languages = recognition?.languages ?? [];
  const remote = connection?.mode === "remote";
  const [tier, setTier] = useState(
    recognition?.tier ?? recognition?.defaultTier ?? "",
  );
  const [ocrMode, setOcrMode] = useState(recognition?.ocrMode ?? "auto");
  const [pageRange, setPageRange] = useState(recognition?.pageRange ?? "all");
  const [language, setLanguage] = useState(
    recognition?.language ??
      (languages.includes("ch") ? "ch" : (languages[0] ?? "")),
  );
  const tierUsable = (id: string) =>
    tiers.some(
      (option) =>
        option.id === id &&
        (option.availability === "ready" ||
          option.availability === "preparation_required"),
    );
  const save = async () => {
    await actions.run({
      type: "settings.setMineruRecognition",
      tier,
      ocrMode,
      pageRange,
      language,
    });
  };
  if (!loaded) {
    return (
      <p className="form-note">
        MinerU 识别参数等待运行时目录同步；可点击“重新检查状态”后配置。
      </p>
    );
  }
  if (!supported) {
    return (
      <p className="form-note" role="note">
        当前识别服务未声明 ocr.mineru-config.v1，无法配置识别参数；请更新
        识别服务。
      </p>
    );
  }
  return (
    <>
      {recognition?.invalid === true ? (
        <p className="form-note" role="alert">
          已保存的 MinerU 识别参数无法解析（{recognition.invalidReason}），
          深度文档解析提交会被拒绝；请重新选择并保存以修复。
        </p>
      ) : null}
      <div className="setting-row">
        <label htmlFor="mineru-tier">识别档位</label>
        <Select
          id="mineru-tier"
          value={tier}
          disabled={locked || tiers.length === 0}
          onChange={(_, data) => setTier(data.value)}
        >
          {tiers.map((option) => (
            <option
              key={option.id}
              value={option.id}
              disabled={
                option.availability !== "ready" &&
                option.availability !== "preparation_required"
              }
            >
              {mineruTierLabel(option.id)}
              {option.availability === "preparation_required"
                ? "（需准备）"
                : option.availability !== "ready"
                  ? "（不可用）"
                  : ""}
            </option>
          ))}
        </Select>
      </div>
      <div className="setting-row">
        <label htmlFor="mineru-ocr-mode">OCR 模式</label>
        <Select
          id="mineru-ocr-mode"
          value={ocrMode}
          disabled={locked}
          onChange={(_, data) => setOcrMode(data.value)}
        >
          <option value="auto">自动（推荐）</option>
          <option value="txt">优先文本层</option>
          <option value="ocr">强制 OCR</option>
        </Select>
      </div>
      <div className="setting-row">
        <label htmlFor="mineru-page-range">默认页码范围</label>
        <Input
          id="mineru-page-range"
          placeholder="all"
          value={pageRange}
          disabled={locked}
          onChange={(_, data) => setPageRange(data.value)}
        />
      </div>
      <p className="form-note">
        页码范围仅对 PDF 输入生效（all 或 1,3-5；r1
        表示最后一页）；单张图片/截图始终解析全部内容。
      </p>
      <div className="setting-row">
        <label htmlFor={remote ? undefined : "mineru-language"}>识别语言</label>
        {remote ? (
          <p className="form-note" role="note">
            由服务端配置（客户端无法读取/覆盖）
          </p>
        ) : (
          <Select
            id="mineru-language"
            value={language}
            disabled={locked || languages.length === 0}
            onChange={(_, data) => setLanguage(data.value)}
          >
            {languages.map((item) => (
              <option key={item} value={item}>
                {mineruLanguageLabel(item)}
              </option>
            ))}
          </Select>
        )}
      </div>
      {remote ? (
        <p className="form-note">
          远程模式语言由自部署服务启动参数决定；本地偏好仅在本机模式生效。
        </p>
      ) : (
        <p className="form-note">
          语言作为本地解析服务的启动参数；变更会在下次解析时重启服务生效。
        </p>
      )}
      <p className="form-note">
        公式/表格识别：由档位与服务端决定；当前 MinerU 4 接口无独立开关。
      </p>
      <div className="setting-row">
        <Button
          disabled={
            locked ||
            tier === "" ||
            (!remote && language === "") ||
            !tierUsable(tier)
          }
          onClick={() => void save()}
        >
          保存识别参数
        </Button>
      </div>
    </>
  );
}

function mineruTierLabel(id: string): string {
  switch (id) {
    case "flash":
      return "闪电（flash）";
    case "basic":
      return "基础（basic）";
    case "standard":
      return "标准（standard）";
    case "advanced":
      return "进阶（advanced）";
    default:
      return id;
  }
}

function mineruLanguageLabel(id: string): string {
  switch (id) {
    case "ch":
      return "中文+英文";
    case "ch_server":
      return "中文+英文（服务版模型）";
    case "korean":
      return "韩文";
    case "ta":
      return "泰米尔文";
    case "te":
      return "泰卢固文";
    case "ka":
      return "格鲁吉亚文";
    case "th":
      return "泰文";
    case "el":
      return "希腊文";
    case "arabic":
      return "阿拉伯文";
    case "east_slavic":
      return "东斯拉夫文";
    case "cyrillic":
      return "西里尔文";
    case "devanagari":
      return "天城文";
    default:
      return id;
  }
}

function MineruConnectionEditor({
  connection,
  locked,
  hostBusy,
  serviceHostReady,
  actions,
}: {
  readonly connection: MineruConnectionState | undefined;
  readonly locked: boolean;
  readonly hostBusy: boolean;
  readonly serviceHostReady?: boolean;
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
      {loaded ? null : serviceHostReady === false ? (
        <>
          <p className="form-note" role="note">
            当前没有已启动的运行环境承载识别服务。远程 MinerU 不需要本地
            MinerU/PaddleOCR 依赖，但连接配置由识别服务保存。
          </p>
          <div className="setting-row">
            <Button
              disabled={locked}
              onClick={() =>
                void actions.run({ type: "settings.prepareRemoteHost" })
              }
              icon={<RefreshCw aria-hidden="true" size={16} />}
            >
              启用 MinerU 远程模式
            </Button>
          </div>
          <p className="form-note">
            首次使用会从安装包准备基础服务，无需下载本地 MinerU/PaddleOCR
            引擎或模型。
          </p>
        </>
      ) : (
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
          当前识别服务未声明 ocr.mineru-remote-api.v1，远程模式不可用；请更新
          识别服务后再配置远程连接。
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
          <p className="form-note">
            填自部署 MinerU 4 解析服务的根地址（如
            https://mineru4.example.com）： 不要带 /file_parse
            等具体路径，也不要填 OpenAI/VLM 兼容地址。
          </p>
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
              ? "识别服务已保存 API Key；留空保存会保留它，勾选上方清除项后保存可移除。"
              : "识别服务未保存 API Key。"}
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
        远程模式由识别服务将解析请求转发到自部署的 MinerU 4 服务，不要求本地
        MinerU 组件、模型或
        GPU；保存只写入配置，不验证服务连通性，实际效果以识别任务结果为准。
      </p>
      <p className="form-note">
        “验证并准备远程服务”由识别服务
        首次调用远程服务完成准备并刷新可用性；不宣称未验证的连接可解析。
      </p>
      <p className="form-note">
        本地模式下的模型预热、驻留 TTL 与释放只作用于本地模型，不控制远程服务。
      </p>
    </>
  );
}

// 默认识别模式编辑器。能力未声明时只读展示准确兼容说明，绝不向旧
// Backend 写未知键；未知/不可用持久值明确提示并引导重选，不伪造回退。
function DefaultRecognitionModeEditor({
  mode,
  modes,
  locked,
  hostBusy,
  actions,
}: {
  readonly mode: DefaultRecognitionModeState | undefined;
  readonly modes: readonly RecognitionModeOptionState[];
  readonly locked: boolean;
  readonly hostBusy: boolean;
  readonly actions: AppActions;
}) {
  const loaded = mode !== undefined;
  const supported = mode?.supported === true;
  const currentId = mode?.modeId ?? null;
  // 本地选择直接控制显示：旧值合法时初始化为它，否则从空白开始；
  // 修复期间的新选择立即反映在 selector 上，不被旧值强制回空白。
  const inCatalog = modes.some((option) => option.id === currentId);
  const [selected, setSelected] = useState<string>(
    inCatalog && currentId !== null ? currentId : "",
  );
  const current = modes.find((option) => option.id === currentId);
  const selectedOption = modes.find((option) => option.id === selected);
  const ready = selectedOption?.availability === "ready";
  const save = async () => {
    if (!ready) return;
    await actions.run({
      type: "settings.setDefaultRecognitionMode",
      mode: selected,
    });
  };
  if (!loaded) {
    return (
      <p className="form-note">
        默认识别模式等待运行环境目录同步；可点击“重新检查状态”后配置。
      </p>
    );
  }
  if (!supported) {
    return (
      <>
        <p className="form-note" role="note">
          当前识别服务未声明 ocr.default-recognition-mode.v1，暂不支持配置默认
          识别类型；兼容行为：任务未显式选择时按快速 OCR（RapidOCR）执行。更新
          识别服务后可在此设置。
        </p>
      </>
    );
  }
  if (modes.length === 0) {
    return (
      <p className="form-note">
        当前运行环境未提供识别模式目录，暂无法配置默认识别模式；请重新检查
        状态或切换运行环境。
      </p>
    );
  }
  return (
    <>
      <div className="setting-row">
        <label htmlFor="default-recognition-mode">默认识别类型</label>
        <Select
          id="default-recognition-mode"
          value={selected}
          disabled={locked || hostBusy}
          onChange={(_, data) => setSelected(String(data.value))}
        >
          {!inCatalog ? (
            <option value="">
              {currentId !== null
                ? "请重新选择（当前值不在目录）"
                : mode.stored
                  ? "请重新选择（当前值无法解析）"
                  : "请选择"}
            </option>
          ) : null}
          {modes.map((option) => (
            <option
              key={option.id}
              value={option.id}
              disabled={option.availability !== "ready"}
            >
              {`${option.displayName}（${availabilityLabel(option.availability)}）`}
            </option>
          ))}
        </Select>
      </div>
      {currentId !== null && !inCatalog ? (
        <p className="form-note" role="alert">
          已回显的默认 “{currentId}” 不在当前运行环境目录中（环境可能已切换或
          组件被移除），保存新默认前未显式选择的任务会拒绝提交；请重新选择
          并保存。
        </p>
      ) : null}
      {currentId === null && mode.stored ? (
        <p className="form-note" role="alert">
          当前运行环境保存的默认值无法解析，未显式选择的任务会拒绝提交；
          请重新选择并保存以修复。
        </p>
      ) : null}
      {current?.reasonCode && current.availability !== "ready" ? (
        <p className="form-note" role="alert">
          当前默认 {current.displayName}（
          {availabilityLabel(current.availability)}：{current.reasonCode}
          ）：未显式选择模式的任务提交会被拒绝，不会静默
          回退其他识别；请先准备组件或改选其他默认。
        </p>
      ) : null}
      <div className="setting-row">
        <Button
          disabled={locked || hostBusy || !ready || selected === currentId}
          onClick={() => void save()}
          icon={<Save aria-hidden="true" size={16} />}
        >
          保存默认识别模式
        </Button>
      </div>
      <p className="form-note">
        保存的是 Runtime 持久默认：单次、批量、PDF 与截图任务未显式选择时继承
        它；已在运行/排队的任务参数不受影响，任务中的显式选择始终优先。
        仅“就绪”模式可设为默认，需要下载/准备的请先在运行环境页准备。
        截图工具栏的“文字识别”使用此处保存的文字或文档模式；若选择表格、公式模式，文字识别按钮会提示重新设置文字引擎。
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
        <span className="setting-actions">
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
        </span>
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
        推理设备：{plan.accelerator}。以下范围由识别服务
        预览，确认前不会修改本机组件或停止当前服务。
      </p>
      <ul className="plan-components">
        {componentLines.map((line, index) => (
          <li key={plan.components[index]?.componentId ?? index}>{line}</li>
        ))}
      </ul>
      <p className="form-note">
        下载来源：
        {sourceNames.length > 0 ? sourceNames.join("、") : "跟随识别服务默认"}
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
  // 默认标识独立于本次选择：override 时继承选项仍展示已提交默认及其
  // 可用性；旧宿主无 isDefault 时回退“选中且非覆盖”推断，未读取不伪造。
  const defaultMode =
    engines.find((engine) => engine.isDefault === true) ??
    engines.find((engine) => engine.selected && !engine.isTaskOverride);
  const inheritLabel = defaultMode
    ? `跟随默认（${defaultMode.displayName}${
        defaultMode.availability === "ready"
          ? ""
          : `，${availabilityLabel(defaultMode.availability)}`
      }）`
    : "跟随默认（未读取）";
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
        <option value="">{inheritLabel}</option>
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
      {!active?.isTaskOverride ? (
        <p className="form-note">本次选择仅覆盖当前任务，不会改写默认。</p>
      ) : null}
      <div className="setting-row">
        <Button
          appearance="secondary"
          disabled={!enabled}
          onClick={() => actions.navigate("settings")}
        >
          修改默认识别类型
        </Button>
      </div>
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
      <div className="setting-row hotkey-edit-row">
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
        {/* 应用/清空/禁用/恢复默认同一操作簇：各行按钮列对齐，不随状态行
           或错误换行漂移。应用在仅修饰键草稿时禁用。 */}
        <span className="setting-actions">
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
        </span>
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
  const [draft, setDraft] = useState({
    value: String(toolbar?.lingerMs ?? ""),
    savedDelay: toolbar?.lingerMs,
    savedError: toolbar?.error,
  });
  const delayDraft =
    draft.savedDelay === toolbar?.lingerMs &&
    draft.savedError === toolbar?.error
      ? draft.value
      : String(toolbar?.lingerMs ?? "");
  const delay = Number(delayDraft);
  const delayValid =
    delayDraft.trim() !== "" &&
    Number.isInteger(delay) &&
    delay >= 100 &&
    delay <= 5000;
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
        <span className="setting-actions">
          <Button
            disabled={!enabled || !toolbar.enabled}
            onClick={() =>
              dispatch.run({ type: "settings.showFloatingToolbar" })
            }
            icon={<Eye aria-hidden="true" size={16} />}
          >
            显示
          </Button>
          <Button
            appearance="secondary"
            disabled={!enabled || !toolbar.enabled}
            onClick={() =>
              dispatch.run({ type: "settings.hideFloatingToolbar" })
            }
            icon={<EyeOff aria-hidden="true" size={16} />}
          >
            隐藏
          </Button>
        </span>
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
      <div className="setting-row">
        <label htmlFor="toolbar-delay">收起时间（毫秒）</label>
        <Input
          id="toolbar-delay"
          type="number"
          min={100}
          max={5000}
          step={1}
          value={delayDraft}
          disabled={!enabled || !toolbar.autoHide}
          aria-invalid={!delayValid}
          aria-describedby="toolbar-delay-note"
          onChange={(_, data) =>
            setDraft({
              value: data.value,
              savedDelay: toolbar.lingerMs,
              savedError: toolbar.error,
            })
          }
        />
        <Button
          disabled={!enabled || !toolbar.autoHide || !delayValid}
          onClick={() =>
            dispatch.run({
              type: "settings.setFloatingToolbarPreferences",
              lingerMs: delay,
            })
          }
        >
          保存时间
        </Button>
      </div>
      <p className="form-note" id="toolbar-delay-note">
        {delayValid
          ? "100–5000 毫秒，仅在鼠标离开后自动收起时生效。"
          : "请输入 100–5000 范围内的整数毫秒；尚未保存。"}
      </p>
      <div className="setting-row">
        <label htmlFor="toolbar-peek">收起后露出像素</label>
        <Select
          id="toolbar-peek"
          value={String(toolbar.peekPixels ?? 2)}
          disabled={!enabled || !toolbar.autoHide}
          onChange={(_, data) =>
            dispatch.run({
              type: "settings.setFloatingToolbarPreferences",
              peekPixels: Number(data.value),
            })
          }
        >
          {Array.from({ length: 20 }, (_, index) => index + 1).map((pixels) => (
            <option key={pixels} value={pixels}>
              {pixels} 像素
            </option>
          ))}
        </Select>
      </div>
      <p className="form-note">
        仅在工具栏所在位置露出提示条，鼠标移到提示条上展开。
      </p>
      <div className="setting-row">
        <label htmlFor="toolbar-theme">工具栏主题</label>
        <Select
          id="toolbar-theme"
          value={toolbar.theme}
          disabled={!enabled}
          onChange={(_, data) =>
            dispatch.run({
              type: "settings.setFloatingToolbarPreferences",
              theme: String(data.value),
            })
          }
        >
          <option value="system">跟随系统</option>
          <option value="light">亮色</option>
          <option value="dark">深色</option>
        </Select>
      </div>
    </>
  );
}

export function DiagnosticsPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "diagnostics");
  const about = feature(viewState, "about");
  const update = feature(viewState, "update");
  const milestones = stringValues(state.milestones);
  const deviceEvidence = stringValues(state.deviceEvidence);
  const version =
    typeof about.version === "string" ? about.version : "等待宿主同步";
  const license =
    typeof about.license === "string" ? about.license : "等待宿主同步";
  const projectUrl =
    typeof about.projectUrl === "string" ? about.projectUrl : "";
  // 修复入口不另造能力：运行环境重试与空环境修复是设置页的既有命令，
  // 这里只导航过去；演示模式（无宿主桥）用本地哈希跳转保持入口可用。
  const openSettings = () => {
    if (viewState.connected) {
      actions.navigate("settings");
      return;
    }
    window.location.hash = "#/settings";
  };
  return (
    <Workspace
      eyebrow="SUPPORT / 06"
      title="关于与诊断"
      description="产品信息、更新与服务健康检查集中在一个入口；内部协议与排错证据折叠在技术详情里，可复制或导出脱敏内容。"
      actions={
        <>
          <CapabilityGate
            capability="diagnostics.export"
            capabilities={viewState.capabilities}
            action={{ type: "diagnostics.export" }}
            actions={actions}
            icon={<Download aria-hidden="true" size={16} />}
          >
            导出脱敏诊断
          </CapabilityGate>
          <CapabilityGate
            appearance="secondary"
            capability="diagnostics.copy"
            capabilities={viewState.capabilities}
            action={{ type: "diagnostics.copy" }}
            actions={actions}
            icon={<Copy aria-hidden="true" size={16} />}
          >
            复制诊断详情
          </CapabilityGate>
        </>
      }
    >
      <div className="diagnostics-grid">
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
        <Panel
          label="HEALTH"
          title="服务健康"
          className="diagnostics-health-panel"
        >
          <div className="health-row">
            <span>识别服务</span>
            <Badge appearance="tint">
              {typeof state.supervisorStatus === "string"
                ? state.supervisorStatus
                : "等待连接"}
            </Badge>
          </div>
          <p className="form-note">
            识别服务未就绪时，可打开设置的运行环境分区重新检查状态、重试上次维护或修复空环境。
          </p>
          <div className="setting-row">
            <Button
              onClick={openSettings}
              icon={<Settings aria-hidden="true" size={16} />}
            >
              打开设置的修复入口
            </Button>
          </div>
          {/* 技术详情默认折叠：原生 details 可键盘展开；内部协议与实例证据
              只服务排错，不在普通界面与产品信息并列。 */}
          <details className="diagnostics-tech-details">
            <summary>技术详情（内部协议与排错证据）</summary>
            <div className="health-row">
              <span>内部协议</span>
              <span>
                {typeof state.protocolStatus === "string"
                  ? state.protocolStatus
                  : "等待连接"}
              </span>
            </div>
            <div>
              <p className="form-note">
                设备决策与回退日志；不表示识别作业已成功执行。
              </p>
              {deviceEvidence.length > 0 ? (
                <pre>{deviceEvidence.join("\n")}</pre>
              ) : (
                <p className="form-note">尚无设备证据。</p>
              )}
            </div>
            <div>
              <h3>启动里程碑</h3>
              {milestones.length > 0 ? (
                <ol className="milestone-list">
                  {milestones.map((milestone) => (
                    <li key={milestone}>{milestone}</li>
                  ))}
                </ol>
              ) : (
                <p className="form-note">尚无启动里程碑快照。</p>
              )}
            </div>
          </details>
        </Panel>
      </div>
    </Workspace>
  );
}
