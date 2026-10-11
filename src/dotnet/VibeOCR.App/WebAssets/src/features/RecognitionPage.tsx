import { Toolbar } from "@fluentui/react-components";

import {
  ClipboardPaste,
  Copy,
  Download,
  FileSpreadsheet,
  ImagePlus,
  Sheet,
  Square,
} from "lucide-react";

import { useState } from "react";

import { CaptureButton } from "../components/CaptureButton";

import { CapabilityGate } from "../components/CapabilityGate";

import { ImageCanvasEditor } from "../components/ImageCanvasEditor";

import { StructuredResult } from "../components/StructuredResult";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { resource } from "./FeatureValues";

import {
  useResourceText,
  TaskEngineSelector,
  recognitionEngines,
} from "./TaskEngineSelection";

import { screenshotSession, screenshotTextLayer } from "./ScreenshotSession";

import { StatusLine } from "./FeatureValues";

import { statusLabel } from "./FeatureValues";

import { stringValue } from "./FeatureValues";

import { Panel } from "./FeatureValues";

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
