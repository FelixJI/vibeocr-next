import { useState } from "react";
import { Button, Input, Select } from "@fluentui/react-components";
import type { AppActions } from "../app/types";
import {
  DEFAULT_EDITOR_PREFERENCES,
  shortcutConflict,
  type EditorPreferences,
  type ImageOutputPreset,
} from "./editorPreferences";

const LABELS: Record<string, string> = {
  select: "选择",
  hand: "手形",
  textSelect: "取字",
  rectangle: "矩形",
  ellipse: "椭圆",
  arrow: "箭头",
  line: "直线",
  polyline: "折线",
  text: "文字",
  callout: "说明",
  mosaic: "马赛克",
  blur: "模糊",
  solid: "遮挡",
  spotlight: "聚光灯",
  magnifier: "放大镜",
  objectEraser: "橡皮擦",
  pen: "画笔",
  highlighter: "荧光笔",
  numbering: "序号",
  exclude: "屏蔽",
  crop: "裁剪",
  inpaint: "去水印",
  dupmatch: "重复匹配",
};

export function EditorPreferencesPanel({
  value,
  directory,
  error,
  actions,
}: {
  readonly value: EditorPreferences;
  readonly directory?: string;
  readonly error?: string;
  readonly actions: AppActions;
}) {
  const [draft, setDraft] = useState(value);
  const [message, setMessage] = useState("");
  const conflict = shortcutConflict(draft);
  const updateOutput = (patch: Partial<ImageOutputPreset>) =>
    setDraft({ ...draft, output: { ...draft.output, ...patch } });
  const templateValid =
    draft.output.nameTemplate.length > 0 &&
    !/[{}]/.test(
      draft.output.nameTemplate.replace(/\{(name|date|index)\}/g, ""),
    );
  const dimensionsValid =
    // 宿主 Width/Height 为 int：非整数（800.5/NaN/Infinity）在前端即拒绝，
    // 不发送必被宿主拒绝的设置；宽高均为 0 保持原尺寸的自动语义。
    Number.isInteger(draft.output.width) &&
    Number.isInteger(draft.output.height) &&
    ((draft.output.width === 0 && draft.output.height === 0) ||
      (draft.output.width > 0 &&
        draft.output.height > 0 &&
        draft.output.width <= 16384 &&
        draft.output.height <= 16384 &&
        draft.output.width * draft.output.height <= 64_000_000));
  return (
    <details className="recognition-options editor-preferences">
      <summary>输出预设与工具栏定制</summary>
      <p>常用输出预设：用于单图和批量图片；JPEG 透明区域明确合成为白底。</p>
      <label>
        文件名模板
        <Input
          aria-label="预设文件名模板"
          maxLength={100}
          value={draft.output.nameTemplate}
          onChange={(_, data) => updateOutput({ nameTemplate: data.value })}
        />
      </label>
      <p>
        可用占位符：{"{name}"} 原文件名、{"{date}"} 日期、{"{index}"}{" "}
        序号；同名自动添加后缀。
      </p>
      <p>
        输出目录：{directory || "尚未选择或已失效"}{" "}
        <Button
          onClick={() =>
            void actions.run({ type: "settings.pickImageOutputDirectory" })
          }
        >
          选择输出目录
        </Button>
      </p>
      <label>
        预设格式
        <Select
          aria-label="预设格式"
          value={draft.output.format}
          onChange={(_, data) =>
            updateOutput({
              format: data.value === "image/jpeg" ? "image/jpeg" : "image/png",
            })
          }
        >
          <option value="image/png">PNG</option>
          <option value="image/jpeg">JPEG（白底）</option>
        </Select>
      </label>
      <label>
        预设压缩品质
        <Input
          aria-label="预设压缩品质"
          type="number"
          min={10}
          max={100}
          value={String(Math.round(draft.output.quality * 100))}
          onChange={(_, data) =>
            updateOutput({ quality: Number(data.value) / 100 })
          }
        />
      </label>
      <label>
        预设宽度
        <Input
          aria-label="预设宽度"
          type="number"
          min={0}
          max={16384}
          value={String(draft.output.width)}
          onChange={(_, data) => updateOutput({ width: Number(data.value) })}
        />
      </label>
      <label>
        预设高度
        <Input
          aria-label="预设高度"
          type="number"
          min={0}
          max={16384}
          value={String(draft.output.height)}
          onChange={(_, data) => updateOutput({ height: Number(data.value) })}
        />
      </label>
      <p>宽高都为 0 时保留原尺寸；指定尺寸后按该尺寸输出，最大 64MP。</p>
      <label>
        <input
          type="checkbox"
          checked={draft.output.closeAfterCopy}
          onChange={(event) =>
            updateOutput({ closeAfterCopy: event.target.checked })
          }
        />
        复制成功后关闭编辑会话
      </label>
      <p>
        工具快捷键仅在编辑器内选择工具，支持单个英文字母，不影响全局快捷键；输入框内不触发。
      </p>
      {draft.order.map((tool, index) => (
        <div key={tool} className="editor-style-control">
          <label>
            <input
              type="checkbox"
              checked={!draft.hidden.includes(tool)}
              onChange={(event) =>
                setDraft({
                  ...draft,
                  hidden: event.target.checked
                    ? draft.hidden.filter((item) => item !== tool)
                    : [...draft.hidden, tool],
                })
              }
            />
            {LABELS[tool]}
          </label>
          <Button
            aria-label={`上移${LABELS[tool]}工具`}
            disabled={index === 0}
            onClick={() => {
              const order = [...draft.order];
              [order[index - 1], order[index]] = [
                order[index]!,
                order[index - 1]!,
              ];
              setDraft({ ...draft, order });
            }}
          >
            上移
          </Button>
          <Button
            aria-label={`下移${LABELS[tool]}工具`}
            disabled={index === draft.order.length - 1}
            onClick={() => {
              const order = [...draft.order];
              [order[index], order[index + 1]] = [
                order[index + 1]!,
                order[index]!,
              ];
              setDraft({ ...draft, order });
            }}
          >
            下移
          </Button>
          <Input
            aria-label={`${LABELS[tool]}工具快捷键`}
            maxLength={1}
            value={draft.shortcuts[tool] ?? ""}
            onChange={(_, data) => {
              const shortcuts = { ...draft.shortcuts };
              if (data.value) shortcuts[tool] = data.value.toLowerCase();
              else delete shortcuts[tool];
              setDraft({ ...draft, shortcuts });
            }}
          />
        </div>
      ))}
      {(conflict || !dimensionsValid) && (
        <p role="alert">
          {conflict ||
            "尺寸须同时为 0（保留原尺寸）或为不超过 16384 的整数（≤64MP）。"}
        </p>
      )}
      {!templateValid && (
        <p role="alert">
          文件名模板只能使用 {"{name}"}、{"{date}"}、{"{index}"}
          ，且花括号须配对。
        </p>
      )}
      {error && <p role="alert">{error}</p>}
      {message && <p role="status">{message}</p>}
      <Button
        disabled={
          !!conflict ||
          !dimensionsValid ||
          !templateValid ||
          !Number.isFinite(draft.output.quality) ||
          draft.output.quality < 0.1 ||
          draft.output.quality > 1
        }
        onClick={async () => {
          const saved = await actions.run({
            type: "settings.setEditorPreferences",
            preferences: draft,
          });
          setMessage(
            saved ? "已提交设置，请查看宿主回显。" : "设置未保存，原设置保留。",
          );
        }}
      >
        保存预设与工具栏
      </Button>
      <Button
        onClick={async () => {
          const saved = await actions.run({
            type: "settings.setEditorPreferences",
            preferences: DEFAULT_EDITOR_PREFERENCES,
          });
          if (saved) setDraft(DEFAULT_EDITOR_PREFERENCES);
        }}
      >
        重置预设与工具栏
      </Button>
    </details>
  );
}
