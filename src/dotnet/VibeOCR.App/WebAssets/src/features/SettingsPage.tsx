import { EditorPreferencesPanel } from "../components/EditorPreferencesPanel";
import { editorPreferences } from "../components/editorPreferences";
import {
  Button,
  Checkbox,
  Input,
  Select,
  Switch,
} from "@fluentui/react-components";

import { Eye, EyeOff, RefreshCw, RotateCcw, Save, X } from "lucide-react";

import { useRef, useState } from "react";

import type { AppActions } from "../app/types";

import { CapabilityGate } from "../components/CapabilityGate";

import { HotkeyRecorder } from "../components/HotkeyRecorder";

import { Workspace } from "../components/Workspace";

import { stringValue } from "./FeatureValues";

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

function WorkerIdleEditor({
  paddleSeconds,
  mineruSeconds,
  remote,
  busy,
  workers,
  actions,
}: {
  readonly paddleSeconds: number | null;
  readonly mineruSeconds: number | null;
  readonly remote: boolean;
  readonly busy: boolean;
  readonly workers: unknown;
  readonly actions: AppActions;
}) {
  // 真实策略尚未读取时不得用占位值编辑/保存：呈现明确的未加载状态与
  // 显式加载入口（复用“检查识别服务”同一刷新命令），加载成功后才编辑。
  const loaded = paddleSeconds !== null && mineruSeconds !== null;
  const [paddle, setPaddle] = useState(loaded ? String(paddleSeconds) : "");
  const [mineru, setMineru] = useState(loaded ? String(mineruSeconds) : "");
  const inFlight = useRef(false);
  const [pending, setPending] = useState(false);
  const disabled = busy || pending;
  const load = async () => {
    if (busy || inFlight.current) return;
    inFlight.current = true;
    setPending(true);
    try {
      await actions.run({ type: "settings.refreshRuntime" });
    } finally {
      inFlight.current = false;
      setPending(false);
    }
  };
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
  if (!loaded) {
    return (
      <>
        <p className="form-note">加载已保存的空闲策略后即可编辑。</p>
        <div className="button-row">
          <Button disabled={disabled} onClick={() => void load()}>
            加载空闲策略
          </Button>
        </div>
        <p className="form-note">加载时会按需启动识别服务。</p>
      </>
    );
  }
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
                : null
            }
            mineruSeconds={
              typeof state.mineruIdleSeconds === "number"
                ? state.mineruIdleSeconds
                : null
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
            locked={false}
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
        <Panel title="识别服务" className="settings-runtime-panel">
          <p>当前服务：{serviceText}</p>
          <CapabilityGate
            capability="runtime.refresh"
            capabilities={viewState.capabilities}
            action={{ type: "settings.refreshRuntime" }}
            actions={actions}
            icon={<RefreshCw aria-hidden="true" size={16} />}
          >
            检查识别服务
          </CapabilityGate>
          <div className="setting-row">
            <label htmlFor="recognition-device">计算设备</label>
            <Select
              id="recognition-device"
              value={stringValue(state.recognitionDevice) ?? ""}
              disabled={
                !viewState.capabilities.includes(
                  "settings.recognition-device",
                ) || booleanValue(state.isBusy)
              }
              onChange={(_, data) =>
                actions.run({
                  type: "settings.setRecognitionDevice",
                  device: data.value,
                })
              }
            >
              <option value="" disabled>
                尚未读取
              </option>
              <option value="auto">自动</option>
              <option value="cpu">CPU</option>
              <option value="directml">DirectML</option>
            </Select>
          </div>
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
            locked={booleanValue(state.isBusy)}
            hostBusy={booleanValue(state.isBusy)}
            serviceHostReady={mineru !== undefined}
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
            locked={booleanValue(state.isBusy)}
            actions={actions}
          />
        </Panel>
        <Panel title="模型来源" className="settings-download-panel">
          <SourceSelector
            sources={sources}
            loaded={sourcesLoaded}
            enabled={viewState.capabilities.includes("settings.selection")}
            locked={booleanValue(state.isBusy)}
            actions={actions}
          />
        </Panel>
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
  if (modelSources.length === 0) {
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
            启动识别服务后即可配置远程 MinerU 连接。
          </p>
          <div className="setting-row">
            <Button
              disabled={locked}
              onClick={() =>
                void actions.run({ type: "settings.refreshRuntime" })
              }
              icon={<RefreshCw aria-hidden="true" size={16} />}
            >
              启动识别服务
            </Button>
          </div>
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
          <option value="local">本地（随产品提供）</option>
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
          识别服务尚未提供模式目录。请重新检查服务状态后设置默认识别模式。
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
          已保存的默认模式 “{currentId}”
          不在当前目录中。请重新选择并保存，或在任务中显式选择可用模式。
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
          回退其他识别。请检查识别服务或选择其他可用模式。
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
        仅“就绪”模式可设为默认。
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
