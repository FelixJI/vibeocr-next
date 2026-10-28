import { EditorPreferencesPanel } from "../components/EditorPreferencesPanel";
import { editorPreferences } from "../components/editorPreferences";
import {
  Badge,
  Button,
  Checkbox,
  Input,
  ProgressBar,
  Select,
  Switch,
} from "@fluentui/react-components";

import {
  Eye,
  EyeOff,
  Play,
  RefreshCw,
  RotateCcw,
  Save,
  Square,
  Trash2,
  X,
} from "lucide-react";

import { useEffect, useRef, useState } from "react";

import type { AppActions } from "../app/types";

import { CapabilityGate } from "../components/CapabilityGate";

import { HotkeyRecorder } from "../components/HotkeyRecorder";

import { Workspace } from "../components/Workspace";

import { stringValue } from "./FeatureValues";

import { stringValues } from "./FeatureValues";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { Panel } from "./FeaturePanels";

import { statusLabel } from "./FeatureValues";

import { availabilityLabel } from "./FeatureValues";

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
  readonly recipe?: string | null;
  readonly operationBlocks?: Readonly<Record<string, string | null>>;
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
  readonly provider?: string | null;
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
  readonly removedDependencies?: readonly string[];
  readonly requiresServiceStop?: boolean;
  readonly cudaPluginAction?: "prepare" | "remove" | null;
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
  readonly engine?: string | null;
  readonly family?: string | null;
  readonly familyDisplayName?: string | null;
  readonly provider?: string | null;
  readonly defaultRemoteHost?: boolean;
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

// 分组和设备只消费 Runtime 显式元数据；标识和显示名不参与行为解析。
interface RecipeComponentGroup {
  readonly key: string;
  readonly engine: "paddle" | "mineru";
  readonly title: string;
  readonly purposeLabel: string;
  readonly purposeKey: string;
  readonly byDevice: ReadonlyMap<string, ManagedEnvironmentRecipeState>;
}

interface RecipeComponentCatalog {
  readonly groups: readonly RecipeComponentGroup[];
  readonly problems: readonly string[];
}

function recipeChoice(
  recipe: ManagedEnvironmentRecipeState | undefined,
): { engine: "paddle" | "mineru"; device: string; base: string } | null {
  if (
    !recipe?.family ||
    !recipe.familyDisplayName ||
    !recipe.provider ||
    (recipe.engine !== "paddle" && recipe.engine !== "mineru") ||
    !["cpu", "cuda", "directml"].includes(recipe.targetDevice ?? "")
  )
    return null;
  return {
    engine: recipe.engine,
    base: recipe.family,
    device:
      recipe.targetDevice === "cuda" ? "nvidia_cuda" : recipe.targetDevice!,
  };
}

// 依赖来源分类只消费目录 dependency_origin（随包离线包 / 在线索引）；
// Runtime 尚未提供精确新增下载字节数，界面不硬编码任何体积估算。
function dependencyOriginNote(origin: string | null | undefined): string {
  if (origin === "bundled_pack") return "依赖随包提供，可离线安装";
  if (origin === "online_index")
    return "依赖需在线下载（当前无精确下载量预估）";
  return "依赖来源待目录同步";
}

function recipeComponentGroups(
  recipes: readonly ManagedEnvironmentRecipeState[],
): RecipeComponentCatalog {
  const problems: string[] = [];
  const candidates = new Map<string, ManagedEnvironmentRecipeState[]>();
  for (const recipe of recipes) {
    const device = recipeChoice(recipe);
    if (!device) {
      problems.push(`配方 ${recipe.id} 缺少引擎或设备元数据，已拒绝该组合。`);
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
    const device = recipeChoice(recipe);
    if (!device) continue;
    if (
      entries.length !== 1 ||
      recipe.accelerator !==
        (device.device === "directml" ? "cpu" : device.device) ||
      recipe.targetDevice !==
        (device.device === "nvidia_cuda" ? "cuda" : device.device)
    ) {
      problems.push(`配方 ${key} 重复或设备信息冲突，已拒绝该组合。`);
      continue;
    }
    const title = recipe.familyDisplayName!;
    const purposeKey = recipePurposeKey(recipe);
    const group = groups.get(device.base);
    if (
      group &&
      (group.engine !== device.engine || group.purposeKey !== purposeKey)
    ) {
      rejectedGroups.add(device.base);
      problems.push(`组件 ${device.base} 的目录信息冲突，已拒绝该组件。`);
      continue;
    }
    groups.set(device.base, {
      key: device.base,
      engine: device.engine,
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
  "paddleocr",
  "paddlepaddle",
  "paddlepaddle-gpu",
  "onnxruntime",
  "onnxruntime-directml",
  "onnxruntime-gpu",
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
  includeCuda = false,
): readonly string[] {
  const names = includeCuda
    ? [
        ...mainDependencyNames,
        "nvidia-cuda-runtime",
        "nvidia-cuda-nvrtc",
        "nvidia-cudnn-cu13",
        "nvidia-curand",
        "nvidia-cufft",
        "nvidia-cublas",
        "nvidia-nvjitlink",
      ]
    : mainDependencyNames;
  const whitelist = new Set(names.map(normalizeDependencyName));
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
  return names
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
    parts.push(
      environment.targetDevice === "directml"
        ? "DirectML"
        : environment.targetDevice === "cuda"
          ? "GPU"
          : "CPU",
    );
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
  readonly reasonCode?: string | null;
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
        reasonCode:
          typeof candidate.reasonCode === "string"
            ? candidate.reasonCode
            : null,
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

function acceleratorLabel(accelerator: string): string {
  return accelerator === "nvidia_cuda" ? "CUDA GPU" : "CPU";
}

export function SettingsPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "settings");
  // #283：按引擎分区选择（Paddle 推理后端 / MinerU 本地引擎）→准备→确认；
  // 技术环境清单、来源与清理收进折叠高级区。选择以目录配方 id 绑定，
  // 导航回来时从宿主待确认计划恢复。
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
  // 选择按引擎分区：Paddle 只记住推理后端，MinerU 单独记住安装设备；
  // 引擎焦点决定哪个分区驱动兼容性查询（宿主查询结果一次只绑定一个
  // 配方，两分区不能同时自动轮询互相覆盖）。
  const [engineDevices, setEngineDevices] = useState({
    paddle: "cpu",
    mineru: "cpu",
  });
  const [engineFocus, setEngineFocus] = useState<EngineSelection>("paddle");
  // 本地失效抑制：选择变化→宿主 invalidate 回传之间旧计划不得继续确认。
  const [invalidatedPlanId, setInvalidatedPlanId] = useState<string | null>(
    null,
  );
  const rawPlan = managedEnvironmentPlan(state.environmentPlan);
  const catalog = recipeComponentGroups(recipes);
  const paddleGroup = catalog.groups.find((group) => group.engine === "paddle");
  const mineruGroup = catalog.groups.find((group) => group.engine === "mineru");
  const restoredChoice =
    !hasExplicitChoice && rawPlan?.requestedRecipe
      ? recipeChoice(
          recipes.find((recipe) => recipe.id === rawPlan.requestedRecipe),
        )
      : null;
  const restoredRecipeId =
    !hasExplicitChoice && rawPlan?.requestedRecipe
      ? rawPlan.requestedRecipe
      : null;
  const focusedEngine = restoredChoice?.engine ?? engineFocus;
  const effectiveDeviceFor = (
    group: RecipeComponentGroup | undefined,
    preferred: string,
  ): string => {
    const keys = [...(group?.byDevice.keys() ?? [])];
    return keys.includes(preferred)
      ? preferred
      : keys.includes("cpu")
        ? "cpu"
        : (keys[0] ?? "");
  };
  const paddleDevice = effectiveDeviceFor(
    paddleGroup,
    restoredChoice?.engine === "paddle"
      ? restoredChoice.device
      : engineDevices.paddle,
  );
  const mineruDevice = effectiveDeviceFor(
    mineruGroup,
    restoredChoice?.engine === "mineru"
      ? restoredChoice.device
      : engineDevices.mineru,
  );
  const paddleRecipe = paddleGroup?.byDevice.get(paddleDevice);
  const mineruRecipe = mineruGroup?.byDevice.get(mineruDevice);
  // 安装选择绑定（#283）：只有显式选择（或导航恢复的计划）的配方可确认；
  // 只读查询/焦点不改变绑定，另一分区的旧配方计划（含换 planId 迟到）
  // 一律拒绝，不得绕过失效跨分区确认。
  const [explicitlySelectedRecipeId, setExplicitlySelectedRecipeId] = useState<
    string | null
  >(null);
  const selectedRecipeId = explicitlySelectedRecipeId ?? restoredRecipeId;
  const recipeForSelection = (
    engine: "paddle" | "mineru",
    device: string,
  ): string | null =>
    (engine === "paddle"
      ? paddleGroup?.byDevice.get(device)?.id
      : mineruGroup?.byDevice.get(device)?.id) ?? null;
  // 快照已同步但仍无活动环境（初始化失败或默认环境被删）时，如实展示
  // 默认配置“尚未启动”；仅目录/快照未到时才显示“尚未读取”。
  const environmentStatusLine = stringValue(state.environmentStatus);
  const managedRuntimeLoaded =
    recipes.length > 0 ||
    environments.length > 0 ||
    (environmentStatusLine ?? "") !== "";
  const defaultRuntimeRecipe =
    recipes.find((recipe) => recipe.defaultRemoteHost)?.displayName ??
    "PaddleOCR ONNX · CPU";
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
  const onEngineDeviceChange = (
    engine: "paddle" | "mineru",
    device: string,
  ) => {
    setHasExplicitChoice(true);
    invalidateSelectionPlan();
    setRequestedEnvironmentId(null);
    setEngineFocus(engine);
    // 从导航恢复的计划出发改另一区时，先固化两区当前有效设备（可能
    // 来自恢复选择），未修改区不得回落默认 CPU。
    setEngineDevices({
      paddle: engine === "paddle" ? device : paddleDevice,
      mineru: engine === "mineru" ? device : mineruDevice,
    });
    setExplicitlySelectedRecipeId(recipeForSelection(engine, device));
  };
  // 显式查询/选择取得主导权：覆盖导航恢复的只读选择，并推进查询代，
  // 让聚焦分区在宿主单槽被他配方结果占用后能重新发起只读查询；
  // 不作废仍绑定原配方的待确认计划（失效仍只由选择变化触发）。
  const takeEngineFocus = (engine: "paddle" | "mineru") => {
    // 取得主导权前先固化当前有效设备（可能来自恢复计划，如 CUDA/DML）：
    // 否则清掉 restoredChoice 后会回落默认 CPU，绑定旧计划的待确认卡失配。
    setEngineDevices({ paddle: paddleDevice, mineru: mineruDevice });
    setExplicitlySelectedRecipeId((current) => current ?? restoredRecipeId);
    setHasExplicitChoice(true);
    setEngineFocus(engine);
    setQueryGeneration((current) => current + 1);
  };
  // 确认卡只属于当前分区的选择，晚到的旧目标不能进入环境行继续确认。
  const planFor = (
    recipeId: string | undefined,
  ): ManagedEnvironmentPlanState | undefined =>
    rawPlan &&
    rawPlan.planId !== invalidatedPlanId &&
    rawPlan.requestedRecipe === recipeId &&
    (!requestedEnvironmentId ||
      rawPlan.environmentId === requestedEnvironmentId)
      ? rawPlan
      : undefined;
  const paddlePlan =
    paddleRecipe?.id === selectedRecipeId && paddleRecipe
      ? planFor(paddleRecipe.id)
      : undefined;
  const mineruPlan =
    mineruRecipe?.id === selectedRecipeId && mineruRecipe
      ? planFor(mineruRecipe.id)
      : undefined;
  const previewEnvironment = (environmentId: string, recipe: string) => {
    const family = recipeChoice(recipes.find((entry) => entry.id === recipe));
    if (family) onEngineDeviceChange(family.engine, family.device);
    setRequestedEnvironmentId(environmentId);
    void actions.run({
      type: "settings.previewEnvironmentInstall",
      environmentId,
      recipe,
    });
  };
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
  // 通用设置只显示本地状态；“重新检查状态”是显式计算准备入口。
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
    <Workspace title="设置" className="reading-limited">
      <div className="settings-grid">
        <Panel title="应用" className="settings-app-panel">
          <div className="setting-row">
            <Switch
              className="application-switch"
              labelPosition="before"
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
          <div className="setting-row">
            <Switch
              className="application-switch"
              labelPosition="before"
              label="最小化到托盘"
              checked={booleanValue(state.minimizeToTray)}
              disabled={!viewState.capabilities.includes("settings.tray")}
              onChange={(_, data) =>
                actions.run({
                  type: "settings.setMinimizeToTray",
                  enabled: data.checked,
                })
              }
            />
          </div>
          {stringValue(state.shellSettingsError) ? (
            <p role="alert" className="form-note">
              {stringValue(state.shellSettingsError)}
            </p>
          ) : null}
        </Panel>
        <Panel title="识别默认模式" className="settings-default-mode-panel">
          <DefaultRecognitionModeEditor
            key={defaultMode?.modeId ?? ""}
            mode={defaultMode}
            modes={recognitionModeOptions(state.recognitionModes)}
            locked={busy}
            hostBusy={booleanValue(state.isBusy)}
            actions={actions}
          />
        </Panel>
        <Panel title="快捷操作" className="settings-shortcuts-panel">
          <p className="form-note">快捷键需包含 Ctrl、Alt 或 Win。</p>
          <HotkeyActions
            actions={hotkeyActions(state.hotkeyActions)}
            enabled={viewState.capabilities.includes("settings.hotkeys")}
            dispatch={actions}
          />
        </Panel>
        <Panel title="悬浮工具栏" className="settings-toolbar-panel">
          <FloatingToolbarPanel
            toolbar={toolbarState(state.floatingToolbar)}
            enabled={viewState.capabilities.includes(
              "settings.floatingToolbar",
            )}
            dispatch={actions}
          />
        </Panel>
        <Panel title="运行环境" className="settings-runtime-panel">
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
                  本产品默认随附 PaddleOCR ONNX · CPU 文字识别环境（含默认 small
                  文字模型，离线可用）；Paddle 推理后端与 MinerU 本地引擎在下方
                  分别按需选择，当前运行任务不受选择影响。
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
              <CapabilityGate
                capability="runtime.environments"
                capabilities={viewState.capabilities}
                action={{ type: "settings.refreshEnvironmentList" }}
                actions={actions}
                icon={<RefreshCw aria-hidden="true" size={16} />}
              >
                查看环境清单
              </CapabilityGate>
              <p className="form-note">
                只读取已有环境，不启动或初始化计算服务。
              </p>
              <EngineEnvironmentSection
                family="paddle"
                state={state}
                actions={actions}
                group={paddleGroup}
                problems={catalog.problems}
                device={paddleDevice}
                focused={focusedEngine === "paddle"}
                onTakeFocus={() => takeEngineFocus("paddle")}
                onDeviceChange={(device) => {
                  if (device === paddleDevice) return;
                  onEngineDeviceChange("paddle", device);
                }}
                queryGeneration={queryGeneration}
                onCancelPlan={invalidateSelectionPlan}
                onPrepare={() => onEngineDeviceChange("paddle", paddleDevice)}
                plan={paddlePlan}
              />
              <EngineEnvironmentSection
                family="mineru"
                state={state}
                actions={actions}
                group={mineruGroup}
                problems={catalog.problems}
                device={mineruDevice}
                focused={focusedEngine === "mineru"}
                onTakeFocus={() => takeEngineFocus("mineru")}
                onDeviceChange={(device) => {
                  if (device === mineruDevice) return;
                  onEngineDeviceChange("mineru", device);
                }}
                queryGeneration={queryGeneration}
                onCancelPlan={invalidateSelectionPlan}
                onPrepare={() => onEngineDeviceChange("mineru", mineruDevice)}
                plan={mineruPlan}
                onPreview={previewEnvironment}
              />
              {/* 安装进度是操作状态而非设置项，保留在折叠区外可见；空闲时
                  自身不渲染。 */}
              <EnvironmentInstallDetails state={state} />
              <details className="environment-advanced-maintenance">
                <summary>高级：环境清单、依赖版本、下载来源与空间清理</summary>
                <p className="form-note">
                  技术环境清单与来源维护默认折叠；只读取状态与保存来源配置，
                  删除类操作仍需逐项确认。
                </p>
                <InstalledEnvironmentList
                  state={state}
                  actions={actions}
                  selectedRecipeId={
                    focusedEngine === "mineru"
                      ? (mineruRecipe?.id ?? "")
                      : (paddleRecipe?.id ?? "")
                  }
                  plan={paddlePlan ?? mineruPlan}
                  onPreview={previewEnvironment}
                />
                <EnvironmentSourceSettings
                  state={state}
                  busy={state.environmentBusy === true}
                  actions={actions}
                  onInvalidate={invalidateSelectionPlan}
                />
              </details>
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
        <Panel title="MinerU 连接" className="settings-mineru-panel">
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
          <Panel title="下载来源" className="settings-download-panel">
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
      <EditorPreferencesPanel
        key={JSON.stringify(state.editorPreferences)}
        value={editorPreferences(state.editorPreferences)}
        actions={actions}
        directory={
          typeof state.imageOutputDirectory === "string"
            ? state.imageOutputDirectory
            : undefined
        }
        error={
          typeof state.editorPreferencesError === "string"
            ? state.editorPreferencesError
            : undefined
        }
      />
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
        本次新增或替换组件：
        {mainDependencies(plan.dependencies, true).join("、") ||
          (plan.dependencies.length === 0
            ? "无需新增组件"
            : "详见完整依赖明细")}
        ； 共 {plan.dependencies.length} 项依赖。
        {plan.dependencyOrigin === "bundled_pack"
          ? "依赖来自随包离线包，无需下载。"
          : "依赖按所选来源安装。"}
      </p>
      <p className="form-note">
        {plan.requiresServiceStop
          ? "确认后暂停当前识别服务，完整候选验证后换入；失败恢复原环境。运行中任务需先结束。"
          : "依赖安装只写入此目标环境。"}
        {plan.cudaPluginAction === "prepare"
          ? "同时准备锁定的 ONNX Runtime CUDA 13 执行插件。"
          : plan.cudaPluginAction === "remove"
            ? "同时移除 ONNX Runtime CUDA 13 执行插件。"
            : ""}
        模型在需要时从所选来源下载，已缓存模型直接复用。
        {(plan.removedDependencies?.length ?? 0) > 0
          ? `本次明确移除 CUDA 附加依赖：${plan.removedDependencies?.join("、")}。`
          : ""}
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
          {(plan.removedDependencies?.length ?? 0) > 0
            ? "确认移除 CUDA"
            : "确认安装依赖"}
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
        <summary>本次依赖与来源明细（只读）</summary>
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
  return (
    <section className="managed-environment-list" aria-label="已安装环境">
      <h3>已安装环境</h3>
      {environments.length === 0 ? (
        <p className="form-note">尚无环境；可在上方分区中准备第一个配置。</p>
      ) : (
        environments.map((environment) => (
          <EnvironmentRow
            key={environment.id}
            environment={environment}
            activeId={activeId}
            busy={busy}
            sourceName={sourceName}
            continueRecipe={
              environment.status === "empty"
                ? selectedRecipeId
                : environment.lastInstallFailure?.phase === "failed"
                  ? environment.lastInstallFailure.recipe
                  : ""
            }
            onPreview={onPreview}
            actions={actions}
          />
        ))
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
  const modelProviderOf = (id: string | null | undefined): string => {
    const source = catalog.find((entry) => entry.id === id);
    return (
      source?.provider ?? (source?.kind === "model_registry" ? source.id : "")
    );
  };
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
    ? paddleSources.filter(
        (source) =>
          source.provider &&
          mineruSources.some(
            (mineru) =>
              modelProviderOf(mineru.id) === modelProviderOf(source.id),
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
            paddleocrModelSourceId:
              paddleSources.find((source) => source.provider === modelValue)
                ?.id ??
              (modelValue === ""
                ? resolvedId("paddleocr_model_registry") || null
                : null),
            mineruModelSourceId:
              mineruSources.find((source) => source.provider === modelValue)
                ?.id ??
              (modelValue === ""
                ? resolvedId("mineru_model_registry") || null
                : null),
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

type EngineSelection = "paddle" | "mineru";

function engineDeviceOptionLabel(device: string): string {
  if (device === "directml") return "Windows GPU（DirectML）";
  if (device === "nvidia_cuda") return "NVIDIA GPU（CUDA）";
  return "CPU（通用）";
}

// 引擎分区选择卡（#283 用户确认方向）：Paddle 只按推理后端选择（文字/表格/结构同依赖，仅模型与管线不同），MinerU 单独安装/查看状态/移除。
// 宿主 compatibility 与 install plan 都是单槽状态：同一时刻只有聚焦分区自动发查询，未聚焦分区只提供显式入口；结果按 recipe 绑定才投影，
// 迟到/他配方结果不会误显示，两卡不会互相覆盖或永久 pending。
function EngineEnvironmentSection({
  family,
  state,
  actions,
  group,
  problems,
  device,
  focused,
  onTakeFocus,
  onDeviceChange,
  queryGeneration,
  onCancelPlan,
  onPrepare,
  plan,
  onPreview,
}: {
  readonly family: EngineSelection;
  readonly state: Readonly<Record<string, unknown>>;
  readonly actions: AppActions;
  readonly group: RecipeComponentGroup | undefined;
  readonly problems: readonly string[];
  readonly device: string;
  readonly focused: boolean;
  readonly onTakeFocus: () => void;
  readonly onDeviceChange: (device: string) => void;
  readonly queryGeneration: number;
  readonly onCancelPlan: () => void;
  readonly onPrepare: () => void;
  readonly plan: ManagedEnvironmentPlanState | undefined;
  readonly onPreview?: (environmentId: string, recipe: string) => void;
}) {
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
            ? "不支持：当前 NVIDIA 驱动不满足安装要求"
            : "不支持：未检测到可用的 NVIDIA 驱动"
          : "暂不可选：NVIDIA 驱动状态未探测（可稍后重新检查状态）";
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
    compatibleEnvironment.serviceState === "ready" &&
    compatibleEnvironment.targetDevice === target?.targetDevice;
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
  // Installed revisions stay unchanged on device switches and service attach.
  // Include the running selection used by activeReady so clearing the host's
  // compatibility result schedules one fresh query through the existing gate.
  const environmentsFingerprint = `${activeId ?? ""}|${environments
    .map(
      (environment) =>
        `${environment.id}:${environment.revision}:${environment.status}:${environment.targetDevice ?? ""}:${environment.serviceState ?? ""}`,
    )
    .join("|")}`;
  const [dispatchedQuery, setDispatchedQuery] = useState<{
    recipe: string;
    fingerprint: string;
    generation: number;
  } | null>(null);
  // 在途闸门只供 effect/事件处理器读写，不在渲染中读取（react-hooks/refs）。
  const inFlightQuery = useRef<string | null>(null);
  const queryKey = `${targetId}|${environmentsFingerprint}|${queryGeneration}`;
  useEffect(() => {
    // 单槽闸门：只有聚焦分区自动查询；未聚焦分区不在 mount 时发请求，避免两卡互相覆盖宿主唯一的 compatibility 结果。
    if (!focused || !targetId || busy || blockedReason) return;
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
    focused,
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
    focused &&
    !busy &&
    !blockedReason &&
    compatibilityRecipe !== targetId &&
    (dispatchedQuery?.recipe !== targetId ||
      dispatchedQuery.fingerprint !== environmentsFingerprint ||
      dispatchedQuery.generation !== queryGeneration);
  // 未聚焦分区不自动查询：显式入口把焦点接到本分区后由同一 effect 单次补发，不产生第二条并发查询路径。
  const manualQueryVisible =
    !!targetId && !focused && !busy && !blockedReason && !queryResult;
  const mineruEnvironments =
    family === "mineru"
      ? environments.filter((environment) =>
          (environment.configuredRecognitionTypes ?? []).includes("document"),
        )
      : [];
  const mineruSourceName = (id: string | null | undefined): string =>
    id == null || id === ""
      ? "官方默认（端点未知）"
      : (sources.find((source) => source.id === id)?.displayName ?? id);
  return (
    <section
      className={
        family === "paddle" ? "engine-backend-section" : "engine-mineru-section"
      }
      aria-label={
        family === "paddle" ? "PaddleOCR 推理后端" : "MinerU 本地引擎"
      }
    >
      <h3>
        {family === "paddle"
          ? "PaddleOCR 推理后端"
          : "MinerU 深度文档解析（本地引擎）"}
      </h3>
      <p className="form-note">
        {family === "paddle"
          ? "文字、表格与版面结构识别共用同一套 PaddleOCR 依赖，差别只在模型与管线（按需下载，各后端共用）。选择后端只做只读查询，确认后才安装或切换。"
          : "MinerU 是独立于 Paddle 的本地文档解析引擎，CPU 与 CUDA 依赖闭包不同，模型首次使用时下载。远程模式不安装本地引擎，连接在下方“MinerU 连接”设置。"}
      </p>
      {problems.map((problem) => (
        <p role="alert" key={problem}>
          {problem}
        </p>
      ))}
      {!group ? (
        <p className="form-note">
          运行环境目录尚未同步或没有可用配方；可点击“重新检查状态”后再试。
        </p>
      ) : (
        <>
          <div className="setting-row">
            <label
              htmlFor={
                family === "paddle"
                  ? "paddle-backend-select"
                  : "mineru-backend-select"
              }
            >
              {family === "paddle" ? "Paddle 推理后端" : "MinerU 安装设备"}
            </label>
            <Select
              id={
                family === "paddle"
                  ? "paddle-backend-select"
                  : "mineru-backend-select"
              }
              value={device}
              disabled={busy}
              onChange={(_, data) => onDeviceChange(String(data.value))}
            >
              {[...group.byDevice.keys()].map((key) => (
                <option key={key} value={key}>
                  {engineDeviceOptionLabel(key)}
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
                依赖来源：{dependencyOriginNote(target.dependencyOrigin)}
                {family === "paddle" &&
                target.dependencyOrigin === "bundled_pack"
                  ? "；默认 small 文字模型随包，表格/结构等模型按需下载"
                  : ""}
                ；主要依赖：
                {mainDependencies(target.dependencies).join("、") ||
                  "目录未提供主要依赖摘要"}
                ；
                {target.dependencies
                  ? `${target.dependencies.length} 项锁定依赖。`
                  : "依赖总数待目录同步。"}
                模型按需准备，实际执行设备尚未验证。
              </p>
            </>
          ) : null}
          <p className="environment-hardware-note">
            NVIDIA 驱动（Runtime 探测，仅 CUDA 需要，不代表满足所选后端）：
            {hardware.nvidiaDriverStatus === "ok"
              ? `已检测到（${hardware.nvidiaDriverVersion ?? "版本未知"}）`
              : hardware.nvidiaDriverStatus === "unsupported"
                ? "不满足要求"
                : "未探测"}
          </p>
          {blockedReason ? (
            <p role="alert">{blockedReason}；该配置当前不可选。</p>
          ) : target ? (
            <>
              {queryResult ? (
                compatibleEnvironment ? (
                  <p>
                    可直接复用环境「{compatibleEnvironment.name}」
                    {queryResult.selectionReason === "active_environment"
                      ? "（当前活动环境）"
                      : ""}
                    ；切换前不安装任何内容。
                  </p>
                ) : (
                  <p>没有可直接复用的已安装环境（均为空、不匹配或未验证）。</p>
                )
              ) : manualQueryVisible ? (
                <div className="setting-row">
                  <span className="form-note">
                    此配置尚未查询；查询是只读操作。
                  </span>
                  <Button onClick={onTakeFocus}>查询可复用环境</Button>
                </div>
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
                    disabled={
                      busy ||
                      Boolean(compatibleEnvironment.operationBlocks?.switch)
                    }
                    onClick={() =>
                      void actions.run({
                        type: "settings.switchEnvironment",
                        environmentId: compatibleEnvironment.id,
                        device: target?.targetDevice,
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
                    {family === "mineru"
                      ? "安装 MinerU 本地引擎"
                      : "准备此后端"}
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
          {family === "mineru" ? (
            <div
              className="mineru-environment-list"
              aria-label="本地 MinerU 环境与移除"
            >
              <h4>本地 MinerU 环境与移除</h4>
              {mineruEnvironments.length === 0 ? (
                <p className="form-note">
                  尚无本地 MinerU 环境；上方选择设备后点“安装 MinerU 本地引擎”。
                </p>
              ) : (
                mineruEnvironments.map((environment) => (
                  <EnvironmentRow
                    key={environment.id}
                    environment={environment}
                    activeId={activeId}
                    busy={busy}
                    sourceName={mineruSourceName}
                    continueRecipe={
                      environment.status === "empty"
                        ? targetId
                        : environment.lastInstallFailure?.phase === "failed"
                          ? environment.lastInstallFailure.recipe
                          : ""
                    }
                    onPreview={onPreview}
                    actions={actions}
                  />
                ))
              )}
              <p className="form-note">
                移除环境只删除该环境的专属依赖目录；共享模型缓存、下载缓存与基础
                Python
                受保护保留（空间清理在下方高级区）。当前活动环境需先切换到
                其他环境后才能移除。
              </p>
            </div>
          ) : null}
        </>
      )}
    </section>
  );
}

// 单个已安装环境行：切换/启动验证、失败修复、空环境继续准备与明确确认后的移除；活动/legacy 拒删由 Runtime 保护，前端只如实呈现并引导先切换。
function EnvironmentRow({
  environment,
  activeId,
  busy,
  sourceName,
  continueRecipe,
  onPreview,
  actions,
}: {
  readonly environment: ManagedEnvironmentState;
  readonly activeId: string | undefined;
  readonly busy: boolean;
  readonly sourceName: (id: string | null | undefined) => string;
  readonly continueRecipe: string;
  readonly onPreview?: (environmentId: string, recipe: string) => void;
  readonly actions: AppActions;
}) {
  const [confirmingRemove, setConfirmingRemove] = useState(false);
  const failure = environment.lastInstallFailure;
  const removeAllowed =
    environment.kind !== "legacy" && environment.id !== activeId;
  return (
    <div
      className="managed-environment-item"
      data-environment-id={environment.id}
    >
      <p>
        <strong>{environment.name}</strong>
        {environment.id === activeId ? " · 当前使用" : ""}（修订{" "}
        {environment.revision}，{managedEnvironmentSummary(environment)}）
      </p>
      <p>
        状态：{environment.status === "empty" ? "空环境" : "已安装依赖"} ·
        Python {environment.pythonState} · 依赖 {environment.dependencyState}
      </p>
      <p>
        引擎 {environment.engineState} · 模型 {environment.modelState} · 服务{" "}
        {environment.serviceState}
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
          {environment.abi || "未验证"}；占用：
          {typeof environment.diskBytes === "number"
            ? `${(environment.diskBytes / 1024 / 1024).toFixed(1)} MiB`
            : "未检查"}
          ；Python：{environment.python || "未定位"}；环境目录：
          {environment.path || "未定位"}
        </p>
      </details>
      {environment.reason ? <p role="alert">{environment.reason}</p> : null}
      {failure ? (
        <p role={failure.phase === "failed" ? "alert" : "status"}>
          {failure.phase === "failed" ? "上次依赖安装未完成" : "依赖安装进行中"}
          （环境修订 {failure.environmentRevision}，{failure.recipe}）：
          {failure.detail}（原因：{failure.reasonCode}；建议：
          {environmentRecoveryActions[failure.nextAction] ?? failure.nextAction}
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
      {Object.values(environment.operationBlocks ?? {}).includes(
        "environment_has_active_jobs",
      ) ? (
        <p className="form-note">
          该环境有运行中或排队任务，维护操作需等待任务结束。
        </p>
      ) : null}
      <div className="setting-row">
        {environment.id !== activeId ||
        (environment.status === "installed" &&
          environment.serviceState !== "ready") ? (
          <Button
            disabled={busy || Boolean(environment.operationBlocks?.switch)}
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
        {environment.recipe === "paddleocr-ort-cuda" && onPreview ? (
          <Button
            disabled={
              busy || Boolean(environment.operationBlocks?.preview_install)
            }
            onClick={() => onPreview(environment.id, "paddleocr-ort-cpu")}
          >
            预览移除 CUDA（保留 CPU）
          </Button>
        ) : null}
        {continueRecipe && onPreview ? (
          <Button
            disabled={
              busy || Boolean(environment.operationBlocks?.preview_install)
            }
            onClick={() => onPreview(environment.id, continueRecipe)}
          >
            {environment.status === "empty" ? "继续准备依赖" : "重新准备依赖"}
          </Button>
        ) : null}
        {environment.status === "empty" &&
        environment.pythonState !== "ready" ? (
          <Button
            disabled={
              busy || Boolean(environment.operationBlocks?.repair_empty)
            }
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
          confirmingRemove ? (
            <>
              <Button
                disabled={busy || Boolean(environment.operationBlocks?.delete)}
                onClick={() => {
                  setConfirmingRemove(false);
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
                onClick={() => setConfirmingRemove(false)}
              >
                取消移除
              </Button>
            </>
          ) : (
            <Button
              icon={<Trash2 aria-hidden="true" size={16} />}
              disabled={busy || Boolean(environment.operationBlocks?.delete)}
              onClick={() => setConfirmingRemove(true)}
            >
              移除环境
            </Button>
          )
        ) : environment.id === activeId ? (
          <span className="form-note">
            当前使用中：请先切换到其他环境后再移除。
          </span>
        ) : null}
      </div>
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
  const retired = mode?.reasonCode === "recognition_mode_retired";
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
          识别类型；兼容行为：任务未显式选择时按通用 OCR（PaddleOCR）执行。更新
          识别服务后可在此设置。
        </p>
      </>
    );
  }
  if (modes.length === 0) {
    return (
      <>
        {retired && (
          <p className="form-note" role="alert">
            当前默认识别模式已退役，请重新选择可用模式并保存。旧配置保留到显式保存。
          </p>
        )}
        <p className="form-note">
          当前运行环境未提供识别模式目录，暂无法配置默认识别模式；请重新检查状态或切换运行环境。
        </p>
      </>
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
              {retired
                ? "请重新选择（当前模式已退役）"
                : currentId !== null
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
      {retired ? (
        <p className="form-note" role="alert">
          当前默认识别模式已退役，请重新选择可用模式并保存。旧配置保留到显式保存。
        </p>
      ) : null}
      {currentId !== null && !inCatalog && !retired ? (
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
        截图工具栏的“文字识别”使用此处保存的文字或文档模式；若选择表格模式，文字识别按钮会提示重新设置文字引擎。
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
