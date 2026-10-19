import { Button, Switch } from "@fluentui/react-components";
import { useEffect, useEffectEvent, useRef, useState } from "react";

import type { AppActions } from "../app/types";

import type { PinHistory } from "./pinHistoryState";

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
}

/** 宿主 WorkbenchBridgeCodec.MaxHistoryQueryLength 同源预算：超长查询会被
 * 协议拒收且无回执，输入与提交两路统一限制。 */
const maximumHistoryQueryLength = 128;

/** 与宿主 EntryName/检索同一固定本地格式（yyyy-MM-dd HH:mm:ss），
 * 用户按界面看到的日期输入检索才能命中。 */
function formatHistoryTimestamp(createdAt: string): string {
  const date = new Date(createdAt);
  if (Number.isNaN(date.getTime())) return createdAt;
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function entryName(createdAt: string): string {
  return formatHistoryTimestamp(createdAt);
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
  const submittedQuery = useRef<string | null>(null);
  const ready = connected && history !== undefined;
  const refreshHistory = useEffectEvent(() => {
    void actions.run({
      type: "pinEdit.listHistory",
      query: submittedQuery.current,
    });
  });
  useEffect(() => {
    if (ready) refreshHistory();
  }, [ready]);
  const submitSearch = () => {
    submittedQuery.current =
      query.trim().slice(0, maximumHistoryQueryLength) || null;
    void actions.run({
      type: "pinEdit.listHistory",
      query: submittedQuery.current,
    });
  };
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
                  ? "已启用：接收的成功截图（含未钉图）将记录，最旧先淘汰"
                  : "已停用：新截图不记录；此前已接收截图按当时开关录入，既有条目保留"
              }
            />
            <input
              className="pin-history-search"
              type="search"
              placeholder="按日期或已识别文本检索"
              aria-label="检索截图历史"
              value={query}
              maxLength={maximumHistoryQueryLength}
              disabled={!connected}
              onChange={(event) => setQuery(event.target.value)}
              onKeyDown={(event) => {
                if (event.key !== "Enter") return;
                event.preventDefault();
                submitSearch();
              }}
            />
            <Button size="small" disabled={!connected} onClick={submitSearch}>
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
