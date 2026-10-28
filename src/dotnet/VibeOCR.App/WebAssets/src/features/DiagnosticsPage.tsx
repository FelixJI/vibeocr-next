import { Badge, Button, Select, Input } from "@fluentui/react-components";

import {
  Copy,
  Download,
  ExternalLink,
  RefreshCw,
  Settings,
  Square,
} from "lucide-react";

import { useState } from "react";

import { CapabilityGate } from "../components/CapabilityGate";

import { Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue, stringValues } from "./FeatureValues";

import { Panel } from "./FeaturePanels";

import { statusLabel } from "./FeatureValues";

interface UpdateSourceOptionView {
  readonly id: string;
  readonly kind: string;
  readonly displayName: string;
  readonly endpoint: string;
  readonly selected: boolean;
}

function sourceOptions(value: unknown): UpdateSourceOptionView[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (item): item is UpdateSourceOptionView =>
      typeof item === "object" &&
      item !== null &&
      typeof (item as UpdateSourceOptionView).id === "string" &&
      typeof (item as UpdateSourceOptionView).kind === "string" &&
      typeof (item as UpdateSourceOptionView).displayName === "string" &&
      typeof (item as UpdateSourceOptionView).endpoint === "string" &&
      typeof (item as UpdateSourceOptionView).selected === "boolean",
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
  const statusMessage =
    typeof update.statusMessage === "string" ? update.statusMessage : "";
  const detectionEndpoint =
    typeof update.detectionEndpoint === "string"
      ? update.detectionEndpoint
      : "";
  const sources = sourceOptions(update.sources);
  const updateBusy = booleanValue(update.isBusy);
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
        <Panel label="PRODUCT" title="VibeOCR Next">
          <p>
            本地优先的 Windows 文字识别工作台：截图/贴图/图片识别、PDF
            解析与文字层、表格与公式导出、二维码与批量处理。
          </p>
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
            {statusLabel(update.statusCode, "更新状态尚未同步。")}
            {typeof update.latestVersion === "string"
              ? ` · 最新版本 ${update.latestVersion}`
              : ""}
          </p>
          {statusMessage ? <p className="form-note">{statusMessage}</p> : null}
          <UpdateSourceControl
            connected={viewState.connected}
            capabilities={viewState.capabilities}
            sources={sources}
            detectionEndpoint={detectionEndpoint}
            busy={updateBusy}
            actions={actions}
          />
          <CapabilityGate
            capability="update.check"
            capabilities={viewState.capabilities}
            action={{ type: "update.check" }}
            actions={actions}
            disabled={updateBusy}
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
            disabled={!booleanValue(update.updateAvailable) || updateBusy}
            icon={<Download aria-hidden="true" size={16} />}
          >
            下载并安装
          </CapabilityGate>
          <CapabilityGate
            capability="update.install"
            capabilities={viewState.capabilities}
            action={{ type: "update.cancel" }}
            actions={actions}
            disabled={!updateBusy}
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

// 更新源分段控制：官方直连/自定义源；自定义支持镜像前缀与正向代理，
// 检查与下载始终绑定同一选定源；忙时禁止切换。
function UpdateSourceControl({
  connected,
  capabilities,
  sources,
  detectionEndpoint,
  busy,
  actions,
}: {
  readonly connected: boolean;
  readonly capabilities: readonly string[];
  readonly sources: readonly UpdateSourceOptionView[];
  readonly detectionEndpoint: string;
  readonly busy: boolean;
  readonly actions: FeatureProps["actions"];
}) {
  const canSelect = connected && capabilities.includes("update.selectSource");
  const configuredCustom = sources.find(
    (source) => source.id !== "direct" && source.kind !== "direct",
  );
  const selectedSource = sources.find((source) => source.selected);
  const customSelected = selectedSource?.id !== "direct" || false;
  const [editorOpen, setEditorOpen] = useState(false);
  const [customKind, setCustomKind] = useState<"url_prefix" | "forward_proxy">(
    configuredCustom?.kind === "forward_proxy" ? "forward_proxy" : "url_prefix",
  );
  const [customUrl, setCustomUrl] = useState("");
  // 分段只反映已保存选择；点击“自定义源”展开编辑器，保存成功后才切换。
  const openEditor = () => {
    if (configuredCustom?.kind === "forward_proxy")
      setCustomKind("forward_proxy");
    else if (configuredCustom?.kind === "url_prefix")
      setCustomKind("url_prefix");
    setCustomUrl("");
    setEditorOpen(true);
  };
  const selectDirect = () => {
    setEditorOpen(false);
    void actions.run({ type: "update.setSource", sourceId: "direct" });
  };
  const saveCustom = () => {
    void actions.run({
      type: "update.setSource",
      sourceId: "custom",
      kind: customKind,
      url: customUrl.trim(),
    });
  };
  const urlReady =
    /^https?:\/\/.+/i.test(customUrl.trim()) && customUrl.trim().length <= 2048;
  return (
    <>
      <div className="setting-row">
        <span id="update-source-label">检测与下载来源</span>
        <div
          className="update-source-segments"
          role="radiogroup"
          aria-labelledby="update-source-label"
        >
          <Button
            role="radio"
            aria-checked={!customSelected}
            appearance={!customSelected ? "primary" : "secondary"}
            disabled={!canSelect || busy}
            onClick={!customSelected ? undefined : selectDirect}
          >
            官方直连
          </Button>
          <Button
            role="radio"
            aria-checked={customSelected}
            appearance={customSelected ? "primary" : "secondary"}
            disabled={!canSelect || busy}
            onClick={openEditor}
          >
            自定义源
          </Button>
        </div>
      </div>
      {detectionEndpoint ? (
        <p className="form-note">检测地址：{detectionEndpoint}</p>
      ) : null}
      {selectedSource ? (
        <p className="form-note">
          下载来源：{selectedSource.displayName}
          {selectedSource.kind === "forward_proxy"
            ? `（经代理 ${selectedSource.endpoint} 下载）`
            : selectedSource.endpoint
              ? `（${selectedSource.endpoint}）`
              : ""}
          ；检查与下载始终使用同一来源。
        </p>
      ) : null}
      {editorOpen && !busy ? (
        <div className="update-source-editor">
          <div className="setting-row">
            <label htmlFor="update-source-kind">自定义源类型</label>
            <Select
              id="update-source-kind"
              value={customKind}
              onChange={(_, data) =>
                setCustomKind(
                  data.value === "forward_proxy"
                    ? "forward_proxy"
                    : "url_prefix",
                )
              }
            >
              <option value="url_prefix">
                镜像前缀（URL 前缀 + 官方 Release 路径）
              </option>
              <option value="forward_proxy">
                正向代理（HTTP/HTTPS CONNECT 代理）
              </option>
            </Select>
          </div>
          <div className="setting-row">
            <label htmlFor="update-source-url">
              {customKind === "forward_proxy" ? "代理地址" : "镜像前缀"}
            </label>
            <Input
              id="update-source-url"
              type="url"
              placeholder={
                customKind === "forward_proxy"
                  ? "http://127.0.0.1:7890"
                  : "https://mirror.example.com/vibeocr"
              }
              value={customUrl}
              onChange={(_, data) => setCustomUrl(data.value)}
            />
          </div>
          <p className="form-note">
            保存后检查与下载都使用该来源；切换来源后需重新检查更新。地址需为
            http/https，不能包含用户名、查询串或片段。
          </p>
          <div className="setting-row">
            <Button
              appearance="primary"
              disabled={!canSelect || !urlReady}
              onClick={saveCustom}
            >
              保存并使用
            </Button>
            <Button onClick={() => setEditorOpen(false)}>取消</Button>
          </div>
        </div>
      ) : null}
    </>
  );
}
