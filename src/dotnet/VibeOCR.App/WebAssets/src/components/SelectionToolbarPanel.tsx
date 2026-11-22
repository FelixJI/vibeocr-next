import { useState } from "react";

import type { AppActions } from "../app/types";

/** 框选动作栏编辑工具：词汇表与标签以 C# SelectionEditToolRow 为跨语言
 * 契约镜像（id 一致，标签仅展示用）。 */
const SELECTION_TOOLS: readonly (readonly [id: string, label: string])[] = [
  ["rectangle", "矩形"],
  ["ellipse", "椭圆"],
  ["arrow", "箭头"],
  ["pen", "画笔"],
  ["highlighter", "荧光笔"],
  ["text", "文字"],
  ["numbering", "序号"],
  ["mosaic", "马赛克"],
  ["blur", "模糊"],
  ["exclude", "屏蔽"],
  ["inpaint", "去水印"],
];

const DEFAULT_SELECTION_TOOLS: readonly string[] = [
  "rectangle",
  "ellipse",
  "highlighter",
  "arrow",
  "text",
  "mosaic",
  "numbering",
];

interface SelectionToolbarPanelProps {
  readonly value: unknown;
  readonly actions: AppActions;
  readonly error?: string;
}

function parseTools(value: unknown): string[] {
  if (Array.isArray(value)) {
    const known = SELECTION_TOOLS.map(([id]) => id);
    const tools = value.filter(
      (entry): entry is string =>
        typeof entry === "string" && known.includes(entry),
    );
    if (tools.length > 0) return tools;
  }
  return [...DEFAULT_SELECTION_TOOLS];
}

/** 框选动作栏工具配置：勾选显隐 + 上移/下移排序（与编辑器偏好面板同一
 * 交互模式）；保存经 settings.setSelectionToolbar 命令，宿主钳制到词汇
 * 表有序子集。 */
export function SelectionToolbarPanel({
  value,
  actions,
  error,
}: SelectionToolbarPanelProps) {
  // 状态变化由 SettingsPage 的 key={JSON.stringify(value)} 重挂载承接，
  // 不在 effect 里同步草稿（避免级联渲染）。
  const [draft, setDraft] = useState<string[]>(() => parseTools(value));
  const [saved, setSaved] = useState<string[] | null>(null);
  const dirty = JSON.stringify(draft) !== JSON.stringify(parseTools(value));
  const move = (index: number, offset: -1 | 1) => {
    const next = [...draft];
    const target = index + offset;
    if (target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target]!, next[index]!];
    setDraft(next);
  };
  const available = SELECTION_TOOLS.filter(
    ([id]) => !draft.includes(id),
  ) as readonly (readonly [string, string])[];
  return (
    <details className="editor-preferences">
      <summary
        aria-label="框选动作栏工具"
        title="配置截图框选动作栏显示的编辑工具"
      >
        框选动作栏工具
      </summary>
      <table>
        <caption>
          动作栏按此顺序展示勾选的工具；未勾选的工具仍可在编辑现场的族菜单中使用
        </caption>
        <thead>
          <tr>
            <th scope="col">显示</th>
            <th scope="col">工具</th>
            <th scope="col">排序</th>
          </tr>
        </thead>
        <tbody>
          {draft.map((tool, index) => {
            const label =
              SELECTION_TOOLS.find(([id]) => id === tool)?.[1] ?? tool;
            return (
              <tr key={tool}>
                <td>
                  <input
                    aria-label={`显示${label}`}
                    checked
                    type="checkbox"
                    onChange={() => {
                      setDraft(draft.filter((entry) => entry !== tool));
                    }}
                  />
                </td>
                <td>{label}</td>
                <td>
                  <button
                    disabled={index === 0}
                    onClick={() => move(index, -1)}
                    type="button"
                  >
                    上移
                  </button>
                  <button
                    disabled={index === draft.length - 1}
                    onClick={() => move(index, 1)}
                    type="button"
                  >
                    下移
                  </button>
                </td>
              </tr>
            );
          })}
          {available.map(([tool, label]) => (
            <tr key={tool}>
              <td>
                <input
                  aria-label={`显示${label}`}
                  type="checkbox"
                  onChange={() => {
                    setDraft([...draft, tool]);
                  }}
                />
              </td>
              <td>{label}</td>
              <td />
            </tr>
          ))}
        </tbody>
      </table>
      <button
        disabled={!dirty || saved !== null}
        onClick={() => {
          void actions
            .run({
              type: "settings.setSelectionToolbar",
              tools: draft.length > 0 ? draft : [...DEFAULT_SELECTION_TOOLS],
            })
            .then(() => setSaved(draft));
        }}
        type="button"
      >
        保存框选动作栏工具
      </button>
      {error ? <p className="form-note">{error}</p> : null}
      {saved ? (
        <p className="form-note">已保存；下次截图动作栏即按新序列展示。</p>
      ) : null}
    </details>
  );
}
