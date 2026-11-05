import { EditorPreferencesPanel } from "../components/EditorPreferencesPanel";
import { editorPreferences } from "../components/editorPreferences";
import {
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
  RefreshCw,
  RotateCcw,
  Save,
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
  /** 配方可选设备全集（统一闭包含 CPU 切换档）；缺失回落 targetDevice。 */
  readonly supportedDevices?: readonly string[] | null;
  readonly pythonVersion?: string | null;
  readonly abi?: string | null;
  readonly platform?: string | null;
  readonly componentIds?: readonly string[] | null;
  readonly dependencies?: readonly string[] | null;
  readonly dependencyOrigin?: string | null;
  readonly runtimeWheelOrigin?: string | null;
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
  readonly modes: readonly string[];
  /** 组内可选择的设备全集（配方的 supported_devices 并集，安装档优先）。 */
  readonly devices: readonly string[];
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
    !["cpu", "directml"].includes(recipe.targetDevice ?? "")
  )
    return null;
  return {
    engine: recipe.engine,
    base: recipe.family,
    device: recipe.targetDevice!,
  };
}

// 依赖来源分类只消费目录 dependency_origin（依赖全部随包离线提供）；
// Runtime 尚未提供精确新增下载字节数，界面不硬编码任何体积估算。
function dependencyOriginNote(origin: string | null | undefined): string {
  if (origin === "bundled_pack") return "依赖随包提供，可离线安装";
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
      recipe.targetDevice !== device.device
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
      modes: [...(recipe.configuredRecognitionTypes ?? [])],
      // 设备全集来自目录 supported_devices（统一闭包 = 安装档 + CPU 切换），
      // 缺失时回落安装档；设备选择由此驱动，安装配方仍按 byDevice 绑定。
      devices: [
        ...new Set([
          ...(group?.devices ?? []),
          ...(recipe.supportedDevices?.length
            ? recipe.supportedDevices
            : [device.device]),
        ]),
      ],
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

// 设备 → 安装配方：按安装档绑定优先（byDevice）；统一闭包选 CPU 等非
// 安装档设备时回落组内唯一配方（同闭包设备切换，不另装配方）。
function recipeForDevice(
  group: RecipeComponentGroup | undefined,
  device: string,
): ManagedEnvironmentRecipeState | null {
  const bound = group?.byDevice.get(device);
  if (bound) return bound;
  const recipes = [...(group?.byDevice.values() ?? [])];
  return recipes.length === 1 ? (recipes[0] ?? null) : null;
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
): readonly string[] {
  const names = mainDependencyNames;
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
    parts.push(environment.targetDevice === "directml" ? "DirectML" : "CPU");
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

function acceleratorLabel(accelerator: string): string {
  return accelerator === "nvidia_cuda" ? "CUDA GPU" : "CPU";
}

function WorkerIdleEditor({
  paddleSeconds,
  mineruSeconds,
  remote,
  busy,
  workers,
  actions,
}: {
  readonly paddleSeconds: number;
  readonly mineruSeconds: number;
  readonly remote: boolean;
  readonly busy: boolean;
  readonly workers: unknown;
  readonly actions: AppActions;
}) {
  const [paddle, setPaddle] = useState(String(paddleSeconds));
  const [mineru, setMineru] = useState(String(mineruSeconds));
  const inFlight = useRef(false);
  const [pending, setPending] = useState(false);
  const disabled = busy || pending;
  const submit = async (restore: boolean) => {
    if (busy || inFlight.current) return;
    inFlight.current = true;
    setPending(true);
    try {
      const saved = await actions.run({
        type: "settings.setWorkerIdle",
        paddleSeconds: restore ? 600 : Number(paddle),
        mineruSeconds: remote ? mineruSeconds : restore ? 600 : Number(mineru),
      });
      if (saved && restore) {
        setPaddle("600");
        if (!remote) setMineru("600");
      }
    } finally {
      inFlight.current = false;
      setPending(false);
    }
  };
  const valid = (value: string) =>
    /^\d+$/.test(value) && Number(value) <= 2147483647;
  const states = Array.isArray(workers) ? workers : [];
  const status = (owner: string) => {
    const value = states.find(
      (entry: { worker?: { owner?: string } }) => entry.worker?.owner === owner,
    )?.worker;
    const labels: Record<string, string> = {
      not_started: "未启动",
      starting: "启动中",
      busy: "使用中",
      idle: "空闲保留",
      stopping: "正在退出",
      idle_stopped: "因空闲已退出",
      stop_failed: "退出失败",
      remote: "远程服务",
    };
    return labels[value?.state ?? "not_started"] ?? "未启动";
  };
  return (
    <>
      <p className="form-note">
        按进程管理资源；默认 600 秒（10 分钟）。0
        秒表示持久，不因空闲退出；下次识别会按需启动。
      </p>
      <label>
        PaddleOCR 空闲秒数
        <Input
          aria-label="PaddleOCR 空闲秒数"
          type="number"
          min={0}
          max={2147483647}
          step={1}
          value={paddle}
          onChange={(_, data) => setPaddle(data.value)}
          disabled={disabled}
        />
      </label>
      <p className="form-note">{status("PaddleOCR")}</p>
      {remote ? (
        <p className="form-note">MinerU：不管理远端进程。</p>
      ) : (
        <>
          <label>
            本地 MinerU 空闲秒数
            <Input
              aria-label="本地 MinerU 空闲秒数"
              type="number"
              min={0}
              max={2147483647}
              step={1}
              value={mineru}
              onChange={(_, data) => setMineru(data.value)}
              disabled={disabled}
            />
          </label>
          <p className="form-note">{status("MinerU")}</p>
        </>
      )}
      <div className="button-row">
        <Button
          disabled={disabled || !valid(paddle) || !valid(mineru)}
          onClick={() => submit(false)}
        >
          保存空闲设置
        </Button>
        <Button disabled={disabled} onClick={() => submit(true)}>
          恢复十分钟默认值
        </Button>
      </div>
      <p className="form-note">
        旧模型保留设置会迁为进程空闲退出；模型文件和运行环境继续保留。
      </p>
    </>
  );
}

export function SettingsPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "settings");
  // 预构建运行时树（PR2）随产品分发，运行时零安装：安装/准备/待确认计划/
  // 兼容查询入口全部退役。#294 的常规页面形态保留——环境清单、来源与
  // 清理直接呈现；分区只剩只读闭包状态与设备切换（switchEnvironment）。
  const managedEnvironmentsAvailable = viewState.capabilities.includes(
    "runtime.environments",
  );
  const environments = managedEnvironments(state.environments);
  const activeEnvironmentId = stringValue(state.activeEnvironmentId);
  const activeEnvironment = environments.find(
    (environment) => environment.id === activeEnvironmentId,
  );
  const recipes = managedEnvironmentRecipes(state.environmentRecipes);
  const catalog = recipeComponentGroups(recipes);
  const paddleGroup = catalog.groups.find((group) => group.engine === "paddle");
  // 统一推理闭包没有独立的 mineru 引擎配方：本地 MinerU 分区复用声明
  // document 能力的共享闭包分组（历史独立 mineru 目录形态仍按 engine 命中）。
  const mineruGroup = catalog.groups.find(
    (group) => group.engine === "mineru" || group.modes.includes("document"),
  );
  // 设备展示以宿主快照为准：当前设备取活动环境 targetDevice，不在目录
  // 设备集（或缺失）时回落分组首个设备，不维护本地选择状态避免漂移。
  const effectiveDeviceFor = (
    group: RecipeComponentGroup | undefined,
  ): string => {
    const devices = group?.devices ?? [];
    const current = activeEnvironment?.targetDevice ?? "";
    return current && devices.includes(current) ? current : (devices[0] ?? "");
  };
  const paddleDevice = effectiveDeviceFor(paddleGroup);
  const mineruDevice = effectiveDeviceFor(mineruGroup);
  // 设备切换直接发送 switchEnvironment（统一闭包 directml↔cpu 同环境
  // 切换，沿用现有命令签名），没有待确认计划或只读查询中间态。
  const switchEnvironmentDevice = (device: string) => {
    if (!device || !activeEnvironmentId) return;
    void actions.run({
      type: "settings.switchEnvironment",
      environmentId: activeEnvironmentId,
      device,
    });
  };
  // 快照已同步但仍无活动环境（初始化失败或默认环境被删）时，如实展示
  // 默认配置“尚未启动”；加载判定用宿主 typed 事实（快照是否已读到位），
  // 不依赖状态文案，空串/未投影不当作已加载。
  const managedRuntimeLoaded =
    state.environmentSnapshotLoaded === true ||
    recipes.length > 0 ||
    environments.length > 0;
  const defaultRuntimeRecipe =
    recipes.find((recipe) => recipe.defaultRemoteHost)?.displayName ??
    "PaddleOCR ONNX + MinerU · Windows DirectML";
  // 远程 MinerU 面板门禁：目录未加载时区分“正在启动”与“无已安装环境
  // 承载识别服务”。后者刷新永远无效，如实指路（随包离线基础配置即可），
  // 不暗示需要本地 MinerU 组件；旧宿主（无运行环境 capability）保留等待语义。
  const mineruServiceHostReady =
    !managedEnvironmentsAvailable || activeEnvironment?.status === "installed";
  // 首次进入 managed 设置页：宿主明确投影 typed 事实（快照未读到位且
  // 清单读取从未尝试，均严格 false）时自动发一次只读清单刷新（与
  // refreshRuntime 不同，不 ensure 服务、无安装/启动副作用）。旧宿主/字段
  // 缺失不自动，保留手动入口；启动初始化期间读取成功会得到中间快照，
  // 终态由宿主激活后补刷；失败后不再自动重试，错误留给显式重试；在途
  // busy/已有快照都不被触碰，legacy 主机不触发。
  const initialListRequested = useRef(false);
  useEffect(() => {
    if (
      !managedEnvironmentsAvailable ||
      initialListRequested.current ||
      state.environmentSnapshotLoaded !== false ||
      state.environmentListFetchAttempted !== false ||
      state.environmentBusy === true
    )
      return;
    initialListRequested.current = true;
    void actions.run({ type: "settings.refreshEnvironmentList" });
  }, [
    managedEnvironmentsAvailable,
    state.environmentSnapshotLoaded,
    state.environmentListFetchAttempted,
    state.environmentBusy,
    actions,
  ]);
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
              label="关闭时最小化到托盘"
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
        <Panel title="空闲后释放资源" className="settings-worker-panel">
          {stringValue(state.workerSettingsWarning) ? (
            <p role="alert">{stringValue(state.workerSettingsWarning)}</p>
          ) : null}
          <WorkerIdleEditor
            key={`${state.paddleIdleSeconds}|${state.mineruIdleSeconds}|${mineru?.mode}`}
            paddleSeconds={
              typeof state.paddleIdleSeconds === "number"
                ? state.paddleIdleSeconds
                : 600
            }
            mineruSeconds={
              typeof state.mineruIdleSeconds === "number"
                ? state.mineruIdleSeconds
                : 600
            }
            remote={mineru?.mode === "remote"}
            busy={booleanValue(state.isBusy)}
            workers={state.workers}
            actions={actions}
          />
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
                  本产品默认随附 PaddleOCR ONNX + MinerU 统一推理环境（DirectML
                  与 CPU 设备切换，含默认 small 文字模型，离线可用）；Paddle
                  推理后端与 MinerU 本地引擎在下方分别按需选择，当前运行任务
                  不受选择影响。
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
            {/* #294：旧 RuntimeStatus 维护投影只在 legacy 分支渲染，
                避免 legacy 健康/idle 被当成 managed 环境维护结果。 */}
            {!managedEnvironmentsAvailable ? (
              <>
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
              </>
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
                onDeviceChange={(device) => {
                  if (device === paddleDevice) return;
                  switchEnvironmentDevice(device);
                }}
              />
              <EngineEnvironmentSection
                family="mineru"
                state={state}
                actions={actions}
                group={mineruGroup}
                problems={catalog.problems}
                device={mineruDevice}
                onDeviceChange={(device) => {
                  if (device === mineruDevice) return;
                  switchEnvironmentDevice(device);
                }}
              />
              {/* #294：环境清单/来源/清理是常规维护能力，直接呈现；技术
                  详情只在各自区块内折叠，必需按钮不随之隐藏。 */}
              <InstalledEnvironmentList state={state} actions={actions} />
              <EnvironmentSourceSettings
                state={state}
                busy={state.environmentBusy === true}
                actions={actions}
              />
            </>
          ) : (
            // 预构建运行时树随产品分发：旧宿主（无 runtime.environments
            // capability）分支不再提供安装/维护入口，只保留上方既有状态行。
            <p className="form-note">当前版本运行环境随产品内置，无需维护。</p>
          )}
          <p className="form-note">
            {stringValue(state.statusMessage) ??
              statusLabel(state.statusCode, "运行环境状态已同步。")}
          </p>
        </Panel>
        <Panel title="MinerU 连接" className="settings-mineru-panel">
          {mineru?.mode === "unavailable" ? (
            <p role="alert">
              连接配置无法安全恢复，MinerU
              识别已禁用。请修复保留的设置文件后重启。
            </p>
          ) : null}
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

// 已安装环境列表（#195 / PR2 预构建运行时树）：常规页面只承载只读健康
// 展示、切换/启动验证、明确确认后的移除与空间清理；安装/准备/修复入口
// 已随“依赖闭包构建期随产品分发”退役。保护沿用 Runtime（活动/被引用/
// legacy/product 拒删，经 operation_blocks 投影），前端只如实呈现并引导
// 先切换。
function InstalledEnvironmentList({
  state,
  actions,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly actions: AppActions;
}) {
  const environments = managedEnvironments(state.environments);
  const activeId = stringValue(state.activeEnvironmentId);
  const busy = state.environmentBusy === true;
  const catalog = managedSourceOptions(state.environmentSources);
  const sourceName = (id: string | null | undefined): string =>
    id == null || id === ""
      ? "官方默认（端点未知）"
      : (catalog.find((source) => source.id === id)?.displayName ?? id);
  return (
    <section className="managed-environment-list" aria-label="已安装环境">
      <h3>已安装环境</h3>
      {environments.length === 0 ? (
        <p className="form-note">
          尚无环境记录；预构建运行环境由宿主装载，可点击“重新检查状态”。
        </p>
      ) : (
        environments.map((environment) => (
          <EnvironmentRow
            key={environment.id}
            environment={environment}
            activeId={activeId}
            busy={busy}
            sourceName={sourceName}
            actions={actions}
          />
        ))
      )}
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

// 来源收敛为单一简单设置：依赖已全部随包离线安装，可配置的只剩
// “下载模型”的来源；不暴露产品默认/全局默认/环境覆盖三层，也不提供
// 按次或按环境的来源覆盖。默认值跟随 Runtime 目录与全局默认解析结果。
function EnvironmentSourceSettings({
  state,
  busy,
  actions,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly busy: boolean;
  readonly actions: AppActions;
}) {
  const catalog = managedSourceOptions(state.environmentSources);
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
    modelProvider?: string;
  }>({});
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
    const ok = await actions.run(
      independent
        ? {
            type: "settings.setEnvironmentSources",
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
            modelSourceId: modelValue || null,
          },
    );
    if (ok) setEdit({});
  };
  const modelSourcesAvailable =
    (independent
      ? providerOptions.length > 0
      : sharedModelSources.length > 0) || modelMixed;
  return (
    <section className="environment-source-settings" aria-label="下载来源">
      <h3>下载来源（模型）</h3>
      <p className="form-note">
        仅用于下载模型：依赖已随包离线提供。保存只写配置，不影响已安装
        环境与当前运行的服务。
      </p>
      {!modelSourcesAvailable ? (
        <p className="form-note">
          下载源目录尚未同步；可点击“重新检查状态”后再配置。
        </p>
      ) : (
        <>
          {independent ? (
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
          ) : (
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
          )}
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
  return "CPU（通用）";
}

// 引擎分区状态卡（#283 / PR2 预构建运行时树）：Paddle 与 MinerU 共享
// 统一推理闭包，分区只呈现目录配方状态（display name / modes / devices）
// 与设备 Select；选择设备直接发送 switchEnvironment（同闭包 directml↔cpu
// 切换），没有只读兼容查询、待确认计划或准备入口。
function EngineEnvironmentSection({
  family,
  state,
  actions,
  group,
  problems,
  device,
  onDeviceChange,
}: {
  readonly family: EngineSelection;
  readonly state: Readonly<Record<string, unknown>>;
  readonly actions: AppActions;
  readonly group: RecipeComponentGroup | undefined;
  readonly problems: readonly string[];
  readonly device: string;
  readonly onDeviceChange: (device: string) => void;
}) {
  // 配方按安装档设备绑定；统一闭包选 CPU 等非安装档设备时回落组内
  // 唯一配方（同闭包设备切换，不另装配方）。
  const target = recipeForDevice(group, device) ?? undefined;
  const environments = managedEnvironments(state.environments);
  const activeId = stringValue(state.activeEnvironmentId);
  const busy = state.environmentBusy === true;
  const sources = managedSourceOptions(state.environmentSources);
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
          ? "文字、表格与版面结构识别共用同一套 PaddleOCR 依赖，差别只在模型与管线（按需下载，各后端共用）。运行环境随产品内置，选择设备即刻切换。"
          : "本地文档解析由统一推理闭包随包携带（与 PaddleOCR 同一环境），模型首次使用时下载。远程模式不安装本地引擎，连接在下方“MinerU 连接”设置。"}
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
              {family === "paddle" ? "Paddle 推理后端" : "MinerU 运行设备"}
            </label>
            <Select
              id={
                family === "paddle"
                  ? "paddle-backend-select"
                  : "mineru-backend-select"
              }
              value={device}
              disabled={busy || !activeId}
              onChange={(_, data) => onDeviceChange(String(data.value))}
            >
              {group.devices.map((key) => (
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
                ，设备 {device}，Python {target.pythonVersion ?? "未知"}）
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
          {activeId ? (
            <p className="form-note">
              切换设备会重新启动并验证识别服务；有运行中任务时切换会被拒绝。
            </p>
          ) : (
            <p className="form-note">
              当前没有活动环境；运行环境由宿主装载，可点击“重新检查状态”。
            </p>
          )}
          {family === "mineru" ? (
            <div
              className="mineru-environment-list"
              aria-label="本地 MinerU 环境与移除"
            >
              <h4>本地 MinerU 环境与移除</h4>
              {mineruEnvironments.length === 0 ? (
                <p className="form-note">
                  尚无本地 MinerU 环境记录；统一推理闭包启动后自动可用。
                </p>
              ) : (
                mineruEnvironments.map((environment) => (
                  <EnvironmentRow
                    key={environment.id}
                    environment={environment}
                    activeId={activeId}
                    busy={busy}
                    sourceName={mineruSourceName}
                    actions={actions}
                  />
                ))
              )}
              <p className="form-note">
                移除环境只删除该环境的专属依赖目录；共享模型缓存、下载缓存与基础
                Python
                受保护保留（可在下方使用空间清理）。当前活动环境需先切换到
                其他环境后才能移除。
              </p>
            </div>
          ) : null}
        </>
      )}
    </section>
  );
}

// 单个已安装环境行（PR2 预构建运行时树）：健康/状态只读展示、切换/启动
// 验证与明确确认后的移除；安装准备与空环境修复入口已退役。活动/legacy/
// product 拒删由 Runtime 经 operation_blocks 投影保护，前端只如实呈现并
// 引导先切换。
function EnvironmentRow({
  environment,
  activeId,
  busy,
  sourceName,
  actions,
}: {
  readonly environment: ManagedEnvironmentState;
  readonly activeId: string | undefined;
  readonly busy: boolean;
  readonly sourceName: (id: string | null | undefined) => string;
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
        本地模式下的预热与进程空闲退出只作用于自有本地资源，不控制远程服务。
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
