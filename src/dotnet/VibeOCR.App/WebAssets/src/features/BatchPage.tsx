import { Badge, Button, Input, ProgressBar } from "@fluentui/react-components";

import {
  ArrowDown,
  ArrowUp,
  Download,
  FileSpreadsheet,
  FilePlus2,
  Play,
  ScanText,
  Sheet,
  Square,
  Trash2,
} from "lucide-react";

import { useState } from "react";

import { CapabilityGate } from "../components/CapabilityGate";

import { StructuredResult } from "../components/StructuredResult";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { ResourceReference } from "./FeatureValues";

import { resource } from "./FeatureValues";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue, numberValue } from "./FeatureValues";

import { useResourceText } from "./FeatureValues";

import { stringValue } from "./FeatureValues";

import { Panel } from "./FeaturePanels";

import { recognitionEngines } from "./TaskEngineValues";

import { TaskEngineSelector } from "./TaskEngineSelection";

interface BatchItemState {
  readonly pageRange?: string | null;
  readonly supportsPageRange?: boolean;
  readonly id: string;
  readonly name: string;
  readonly statusCode: string;
  readonly resultSummary?: string | null;
  readonly structuredResult?: ResourceReference | null;
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
