import { Button, Toolbar } from "@fluentui/react-components";

import {
  ClipboardPaste,
  Copy,
  Download,
  FileSpreadsheet,
  ImagePlus,
  Sheet,
  Square,
} from "lucide-react";

import { useEffect, useState } from "react";

import {
  hasTextSelection,
  isEditableTarget,
  shouldRunDocumentCommand,
} from "../app/keyboard";

import { CaptureButton } from "../components/CaptureButton";

import { CapabilityGate } from "../components/CapabilityGate";

import { ImageCanvasEditor } from "../components/ImageCanvasEditor";

import { StructuredResult } from "../components/StructuredResult";
import type { IndexedResultLine } from "../components/ResultImageTextOverlay";
import {
  linesInfoFromSource,
  RESULT_TEXT_MODES,
  type ResultTextMode,
} from "../components/resultTextModes";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { resource } from "./FeatureValues";

import { useResourceText } from "./FeatureValues";

import { TaskEngineSelector } from "./TaskEngineSelection";

import { recognitionEngines } from "./TaskEngineValues";

import {
  screenshotSession,
  screenshotTextLayer,
  imageEditImport,
} from "./ScreenshotSession";

import { Panel, StatusLine } from "./FeaturePanels";

import { statusLabel } from "./FeatureValues";

import { stringValue } from "./FeatureValues";

export function RecognitionPage({ viewState, actions }: FeatureProps) {
  // The host owns each capture's intent; this user preference survives keyed
  // editor remounts and only applies to sessions explicitly captured for text.
  const [autoTextPreference, setAutoTextPreference] = useState(true);
  // 左侧单一图片区承载编辑与校对：有带 bbox 的结果时默认展示校对层
  // （宿主 state.resultImage 冻结参考图，不用会话基准图冒充）；切换到
  // 编辑仅隐藏画布，编辑器不卸载，未提交编辑状态不丢失。
  const [leftImageMode, setLeftImageMode] = useState<"proofread" | "edit">(
    "proofread",
  );
  const [proofreadBindingKey, setProofreadBindingKey] = useState<string>();
  // 排版模式为结果单一状态：唯一的一组复制/导出按钮按它提交，避免
  // 结果区再叠一套全局输出按钮。
  const [resultTextMode, setResultTextMode] = useState<ResultTextMode>("raw");
  const state = feature(viewState, "recognition");
  const input = resource(state.input);
  const result = resource(state.result);
  const resultText = useResourceText(result);
  const structured = resource(state.structuredResult);
  const structuredText = useResourceText(structured);
  // 识别时真实冻结的参考图（随结果原子替换/失效）：校对视图唯一图源；
  // 缺失时不渲染 bbox，不用会话基准图冒充。
  const resultImage = resource(state.resultImage);
  const linesInfo = structured
    ? linesInfoFromSource(structuredText ?? "")
    : null;
  // 宿主未发布某模式（undefined；解析已排除空串）时禁用该项。
  const effectiveMode =
    linesInfo?.hasLines && linesInfo.modes[resultTextMode] === undefined
      ? "raw"
      : resultTextMode;
  const session = screenshotSession(state.screenshotSession);
  const textLayer = screenshotTextLayer(state.textLayer);
  const sessionCapable = viewState.capabilities.includes(
    "recognition.screenshotSession",
  );
  // 左侧校对面：行框投影到识别时真实冻结的参考图（state.resultImage，
  // 随结果原子替换/失效），绝不拿会话输入基准图冒充，避免裁剪/旋转后错位。
  // 有带 bbox 的行时才提供；右侧不再重复渲染第二张校对图。
  const proofreadLines =
    structured && resultImage && linesInfo?.hasLines
      ? linesInfo.lines.flatMap((line, index) =>
          line.bbox ? [{ line, index } satisfies IndexedResultLine] : [],
        )
      : undefined;
  const proofread =
    resultImage && proofreadLines?.length
      ? {
          imageUrl: resultImage.url,
          // 绑定键：识别参考图+结构化资源任一变化即重挂校对层。
          bindingKey: `${resultImage.url}:${structured?.url ?? ""}`,
          lines: proofreadLines,
          onUpdateLine: (
            lineIndex: number,
            expectedOldText: string,
            newText: string,
          ) =>
            structured
              ? actions.run({
                  type: "recognition.updateLine",
                  resourceUri: structured.url,
                  lineIndex,
                  expectedOldText,
                  newText,
                })
              : Promise.resolve(false),
        }
      : undefined;
  // 仅结果身份变化时恢复默认校对；同一结果的宿主更新不打断编辑。
  if (proofreadBindingKey !== proofread?.bindingKey) {
    setProofreadBindingKey(proofread?.bindingKey);
    setLeftImageMode("proofread");
  }
  // Windows 常用命令：打开与工具栏按钮同一入口；Ctrl+C 仅在无文本选择、
  // 目标非输入控件且存在可复制结果时降级为复制识别结果（镜像页级唯一
  // 复制按钮：lines 结果按当前排版模式，其余 plain），不覆盖用户已有
  // 文本选区复制。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (
        !shouldRunDocumentCommand(event) ||
        event.repeat ||
        event.altKey ||
        !(event.ctrlKey || event.metaKey)
      )
        return;
      const key = event.key.toLowerCase();
      if (
        key === "o" &&
        !event.shiftKey &&
        viewState.capabilities.includes("recognition.file")
      ) {
        event.preventDefault();
        void actions.run({ type: "recognition.selectImage" });
      } else if (
        key === "c" &&
        !event.shiftKey &&
        !isEditableTarget(event.target) &&
        !hasTextSelection() &&
        (result !== undefined || structured !== undefined) &&
        viewState.capabilities.includes("recognition.results")
      ) {
        event.preventDefault();
        void actions.run(
          linesInfo?.hasLines && structured
            ? {
                type: "recognition.copyLines",
                resourceUri: structured.url,
                mode: effectiveMode,
              }
            : { type: "recognition.copy", format: "plain" },
        );
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [
    actions,
    viewState.capabilities,
    result,
    structured,
    linesInfo,
    effectiveMode,
  ]);
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
            <>
              {/* 单一左侧图片区的编辑/校对模式切换：明确当前视图；编辑器
               * 始终挂载，切回编辑时未提交标注/历史原样恢复。 */}
              {proofread ? (
                <div
                  aria-label="左侧视图模式"
                  className="left-view-switch"
                  role="group"
                >
                  <Button
                    appearance={
                      leftImageMode === "proofread" ? "primary" : "subtle"
                    }
                    aria-pressed={leftImageMode === "proofread"}
                    onClick={() => setLeftImageMode("proofread")}
                    size="small"
                  >
                    图像校对
                  </Button>
                  <Button
                    appearance={leftImageMode === "edit" ? "primary" : "subtle"}
                    aria-pressed={leftImageMode === "edit"}
                    onClick={() => setLeftImageMode("edit")}
                    size="small"
                  >
                    编辑图片
                  </Button>
                </div>
              ) : null}
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
                systemImport={imageEditImport(state.import)}
                autoText={
                  session?.textSelectionRequested === true && autoTextPreference
                }
                showAutoTextPreference={
                  session?.textSelectionRequested === true
                }
                onAutoTextChange={setAutoTextPreference}
                toolset="recognition"
                proofread={proofread}
                proofreadActive={leftImageMode === "proofread"}
              />
            </>
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
              <Toolbar
                aria-label="识别结果操作"
                size="small"
                className="recognition-result-toolbar"
              >
                <div className="result-action-row">
                  {linesInfo?.hasLines && structured ? (
                    <label className="result-mode-select">
                      排版模式{" "}
                      <select
                        aria-label="排版模式"
                        value={effectiveMode}
                        onChange={(event) =>
                          setResultTextMode(
                            event.target.value as ResultTextMode,
                          )
                        }
                      >
                        {RESULT_TEXT_MODES.map((entry) => (
                          <option
                            key={entry.value}
                            value={entry.value}
                            disabled={
                              linesInfo.modes[entry.value] === undefined
                            }
                          >
                            {entry.label}
                          </option>
                        ))}
                      </select>
                    </label>
                  ) : null}
                  <CapabilityGate
                    capability="recognition.results"
                    capabilities={viewState.capabilities}
                    action={
                      linesInfo?.hasLines && structured
                        ? {
                            type: "recognition.copyLines",
                            resourceUri: structured.url,
                            mode: effectiveMode,
                          }
                        : { type: "recognition.copy", format: "plain" }
                    }
                    actions={actions}
                    icon={<Copy aria-hidden="true" size={16} />}
                  >
                    复制文本
                  </CapabilityGate>
                </div>
                <div className="result-action-row">
                  {(["markdown", "docx", "xlsx"] as const).map((format) => (
                    <CapabilityGate
                      key={format}
                      capability="recognition.results"
                      capabilities={viewState.capabilities}
                      action={{
                        type: "recognition.export",
                        format,
                        ...(linesInfo?.hasLines && effectiveMode !== "raw"
                          ? { textMode: effectiveMode }
                          : {}),
                      }}
                      actions={actions}
                      icon={
                        format === "xlsx" ? (
                          <FileSpreadsheet aria-hidden="true" size={16} />
                        ) : format === "docx" ? (
                          <Sheet aria-hidden="true" size={16} />
                        ) : (
                          <Download aria-hidden="true" size={16} />
                        )
                      }
                    >
                      导出{" "}
                      {format === "markdown"
                        ? "Markdown"
                        : format === "docx"
                          ? "Word"
                          : "Excel"}
                    </CapabilityGate>
                  ))}
                </div>
              </Toolbar>
              {structured && (
                <StructuredResult
                  key={structured.url}
                  source={structuredText}
                  mode={effectiveMode}
                  onCopy={(blockIndex, format) =>
                    actions.run({
                      type: "recognition.copyStructured",
                      resourceUri: structured.url,
                      blockIndex,
                      format,
                    })
                  }
                  onCopyCells={(
                    blockIndex,
                    rowStart,
                    rowEnd,
                    columnStart,
                    columnEnd,
                  ) =>
                    actions.run({
                      type: "recognition.copyStructuredCells",
                      resourceUri: structured.url,
                      blockIndex,
                      rowStart,
                      rowEnd,
                      columnStart,
                      columnEnd,
                    })
                  }
                  onUpdateLine={(lineIndex, expectedOldText, newText) =>
                    actions.run({
                      type: "recognition.updateLine",
                      resourceUri: structured.url,
                      lineIndex,
                      expectedOldText,
                      newText,
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
