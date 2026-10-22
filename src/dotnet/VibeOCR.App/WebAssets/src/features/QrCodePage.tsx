import { Button, Select, Tab, TabList } from "@fluentui/react-components";

import {
  ClipboardPaste,
  Copy,
  ExternalLink,
  QrCode,
  Save,
  ScanText,
  Square,
  Trash2,
} from "lucide-react";

import { useCallback, useEffect, useRef, useState } from "react";

import { CapabilityGate } from "../components/CapabilityGate";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { resource } from "./FeatureValues";

import { Panel } from "./FeaturePanels";

import { StatusLine } from "./FeaturePanels";

import { stringValue } from "./FeatureValues";

import { statusLabel } from "./FeatureValues";

interface QrResultState {
  readonly data: string;
  readonly format: string;
  readonly isUrl: boolean;
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

export function QrCodePage({ viewState, actions }: FeatureProps) {
  const [tab, setTab] = useState<"generate" | "decode">("generate");
  const [qrText, setQrText] = useState("");
  const [format, setFormat] = useState("qrcode");
  const [captionMode, setCaptionMode] = useState("off");
  const [captionText, setCaptionText] = useState("");
  const autoDecoded = useRef(0);
  const [composing, setComposing] = useState(false);
  const lastGenerated = useRef<string | undefined>(undefined);
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
  const canGenerate = viewState.capabilities.includes("qrcode.generate");
  const generationKey = JSON.stringify([
    qrText,
    format,
    captionMode,
    captionText,
  ]);
  const generate = useCallback(() => {
    lastGenerated.current = generationKey;
    return actions.run({
      type: "qrcode.generate",
      text: qrText,
      format,
      captionMode,
      captionText,
    });
  }, [actions, generationKey, qrText, format, captionMode, captionText]);
  useEffect(() => {
    if (
      tab !== "generate" ||
      !canGenerate ||
      isBusy ||
      composing ||
      !qrText.trim() ||
      lastGenerated.current === generationKey
    )
      return;
    const timer = window.setTimeout(() => {
      if (lastGenerated.current !== generationKey) void generate();
    }, 400);
    return () => window.clearTimeout(timer);
  }, [tab, canGenerate, isBusy, composing, qrText, generationKey, generate]);
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
                  ? "输入内容后自动生成二维码。"
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
                onCompositionStart={() => setComposing(true)}
                onCompositionEnd={() => setComposing(false)}
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
                    onCompositionStart={() => setComposing(true)}
                    onCompositionEnd={() => setComposing(false)}
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
                actions={{ ...actions, run: generate }}
                disabled={qrText.trim().length === 0 || isBusy || composing}
                icon={<QrCode aria-hidden="true" size={16} />}
              >
                生成图片
              </CapabilityGate>
              <p className="form-note">
                内容或设置修改后自动生成；连续输入停止 400
                毫秒后更新，无需启动识别运行环境。
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
