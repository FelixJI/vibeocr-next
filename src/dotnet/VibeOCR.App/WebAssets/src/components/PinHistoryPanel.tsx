import { Button, Switch } from "@fluentui/react-components";
import { useState } from "react";

import type { AppActions } from "../app/types";

import type { PinHistory } from "./pinHistoryState";

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
}

function entryName(createdAt: string): string {
  const date = new Date(createdAt);
  return Number.isNaN(date.getTime())
    ? createdAt
    : date.toLocaleString("zh-CN", { hour12: false });
}

/** #238 截图历史管理面板：开关/检索/重开/删除/清空（仅 pinEdit.* 命令；
 * 关闭默认值、容量双上限与最旧淘汰由宿主持有）。 */
export function PinHistoryPanel({
  history,
  actions,
  connected,
}: {
  readonly history: PinHistory | undefined;
  readonly actions: AppActions;
  readonly connected: boolean;
}) {
  const [query, setQuery] = useState("");
  if (history === undefined) return null;
  return (
    <div className="pin-history">
      <Button
        disabled={!connected}
        onClick={() => void actions.run({ type: "pinEdit.showManager" })}
      >
        贴图管理
      </Button>
      <details>
        <summary aria-label="截图历史" title="截图历史">
          截图历史（{history.count}/{history.maximumEntries} 条 ·{" "}
          {formatBytes(history.totalBytes)}/{formatBytes(history.maximumBytes)}
          ）
        </summary>
        <div className="pin-history-panel">
          <div className="pin-history-toolbar">
            <Switch
              aria-label="启用截图历史"
              checked={history.enabled}
              disabled={!connected}
              onChange={(_, data) =>
                void actions.run({
                  type: "pinEdit.setHistoryEnabled",
                  enabled: data.checked,
                })
              }
              label={
                history.enabled
                  ? "已启用：记录成功截图（含未钉图），最旧先淘汰"
                  : "已停用：不再记录新截图；既有条目保留"
              }
            />
            <input
              className="pin-history-search"
              type="search"
              placeholder="按日期或已识别文本检索"
              aria-label="检索截图历史"
              value={query}
              disabled={!connected}
              onChange={(event) => setQuery(event.target.value)}
              onKeyDown={(event) => {
                if (event.key !== "Enter") return;
                event.preventDefault();
                void actions.run({
                  type: "pinEdit.listHistory",
                  query: query.trim() === "" ? null : query.trim(),
                });
              }}
            />
            <Button
              size="small"
              disabled={!connected}
              onClick={() =>
                void actions.run({
                  type: "pinEdit.listHistory",
                  query: query.trim() === "" ? null : query.trim(),
                })
              }
            >
              检索
            </Button>
            <Button
              size="small"
              disabled={!connected || history.count === 0}
              onClick={() => {
                if (
                  window.confirm(
                    `将删除全部 ${history.count} 条截图历史（仅本应用拥有的记录），不可恢复。`,
                  )
                )
                  void actions.run({ type: "pinEdit.clearHistory" });
              }}
            >
              清空
            </Button>
          </div>
          {history.error && <p role="alert">{history.error}</p>}
          {history.entries.length === 0 ? (
            <p className="form-note">
              {history.enabled
                ? "暂无匹配的截图记录。"
                : "截图历史默认关闭；启用后记录成功截图会话（含未钉图）。"}
            </p>
          ) : (
            <ul className="pin-history-list">
              {history.entries.map((entry) => (
                <li key={entry.entryId} className="pin-history-entry">
                  <span className="pin-history-name">
                    {entryName(entry.createdAt)}
                    {entry.width > 0 && entry.height > 0
                      ? ` · ${entry.width}×${entry.height}`
                      : ""}{" "}
                    · {formatBytes(entry.bytes)}
                  </span>
                  {entry.recognizedText ? (
                    <span
                      className="pin-history-text"
                      title={entry.recognizedText}
                    >
                      {entry.recognizedText.slice(0, 80)}
                    </span>
                  ) : null}
                  <span className="pin-history-actions">
                    <Button
                      size="small"
                      disabled={!connected}
                      onClick={() =>
                        void actions.run({
                          type: "pinEdit.reopenHistoryEntry",
                          entryId: entry.entryId,
                        })
                      }
                    >
                      重开
                    </Button>
                    <Button
                      size="small"
                      disabled={!connected}
                      onClick={() =>
                        void actions.run({
                          type: "pinEdit.deleteHistoryEntry",
                          entryId: entry.entryId,
                        })
                      }
                    >
                      删除
                    </Button>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </details>
    </div>
  );
}
