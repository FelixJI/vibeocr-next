import { Badge, Button } from "@fluentui/react-components";

import {
  Copy,
  Download,
  ExternalLink,
  RefreshCw,
  Settings,
  Square,
} from "lucide-react";

import { CapabilityGate } from "../components/CapabilityGate";

import { Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { stringValues } from "./FeatureValues";

import { Panel } from "./FeatureValues";

import { statusLabel } from "./FeatureValues";

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
