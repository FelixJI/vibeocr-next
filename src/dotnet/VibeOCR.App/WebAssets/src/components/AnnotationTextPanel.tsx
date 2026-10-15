import { useState } from "react";
import { Input, Select } from "@fluentui/react-components";
import type {
  AnnotationStyle,
  AnnotationTextStyle,
  Mark,
} from "./annotationGeometry";

export function AnnotationTextPanel({
  style,
  tool,
  onChange,
  onResolveFont,
}: {
  style: AnnotationStyle;
  tool: Mark["tool"];
  onChange: (patch: Partial<AnnotationTextStyle>) => void;
  onResolveFont: (requested: string) => string;
}) {
  const family = style.fontFamily ?? "system-ui";
  const [fontDraft, setFontDraft] = useState({ family, value: family });
  if (fontDraft.family !== family) setFontDraft({ family, value: family });
  return (
    <>
      <label className="editor-style-control">
        <span aria-hidden="true">字体</span>
        <Input
          aria-label="文字字体"
          size="small"
          value={fontDraft.value}
          onChange={(_, data) => setFontDraft({ family, value: data.value })}
          onBlur={() => {
            const resolved = onResolveFont(fontDraft.value);
            setFontDraft({ family: resolved, value: resolved });
          }}
        />
      </label>
      {tool !== "numbering" && (
        <>
          {(
            [
              ["fontBold", "粗体"],
              ["fontItalic", "斜体"],
              ["fontUnderline", "下划线"],
            ] as const
          ).map(([key, label]) => (
            <label className="editor-style-control" key={key}>
              <input
                type="checkbox"
                aria-label={label}
                checked={style[key] === true}
                onChange={(event) => onChange({ [key]: event.target.checked })}
              />
              {label}
            </label>
          ))}
          <label className="editor-style-control">
            <span aria-hidden="true">对齐</span>
            <Select
              aria-label="文字对齐"
              size="small"
              value={style.textAlign ?? "left"}
              onChange={(_, data) =>
                onChange({
                  textAlign: data.value as AnnotationTextStyle["textAlign"],
                })
              }
            >
              <option value="left">左对齐</option>
              <option value="center">居中</option>
              <option value="right">右对齐</option>
            </Select>
          </label>
          <label className="editor-style-control">
            <input
              type="checkbox"
              aria-label="文字背景"
              checked={
                style.textBackground
                  ? style.textBackground.enabled !== false
                  : tool === "callout"
              }
              onChange={(event) =>
                onChange({
                  textBackground: {
                    color: style.textBackground?.color ?? "#ffffff",
                    opacity: 100,
                    enabled: event.target.checked,
                  },
                })
              }
            />
            背景
          </label>
          {(style.textBackground || tool === "callout") &&
            style.textBackground?.enabled !== false && (
              <input
                aria-label="文字背景颜色"
                type="color"
                value={style.textBackground?.color ?? "#ffffff"}
                onChange={(event) =>
                  onChange({
                    textBackground: {
                      ...style.textBackground,
                      color: event.target.value,
                    },
                  })
                }
              />
            )}
          <label className="editor-style-control">
            <input
              type="checkbox"
              aria-label="文字描边"
              checked={(style.textOutline?.width ?? 0) > 0}
              onChange={(event) =>
                onChange({
                  textOutline: {
                    color: style.textOutline?.color ?? "#161616",
                    width: event.target.checked ? 2 : 0,
                  },
                })
              }
            />
            描边
          </label>
          {(style.textOutline?.width ?? 0) > 0 && (
            <input
              aria-label="文字描边颜色"
              type="color"
              value={style.textOutline?.color ?? "#161616"}
              onChange={(event) =>
                onChange({
                  textOutline: {
                    color: event.target.value,
                    width: style.textOutline?.width ?? 2,
                  },
                })
              }
            />
          )}
        </>
      )}
      {tool === "numbering" && (
        <>
          <label className="editor-style-control">
            <span aria-hidden="true">格式</span>
            <Select
              aria-label="序号格式"
              size="small"
              value={style.numberFormat ?? "number"}
              onChange={(_, data) =>
                onChange({
                  numberFormat:
                    data.value as AnnotationTextStyle["numberFormat"],
                })
              }
            >
              <option value="number">数字</option>
              <option value="upper">大写字母</option>
              <option value="lower">小写字母</option>
            </Select>
          </label>
          <label className="editor-style-control">
            <span aria-hidden="true">形状</span>
            <Select
              aria-label="序号形状"
              size="small"
              value={style.numberShape ?? "circle"}
              onChange={(_, data) =>
                onChange({
                  numberShape: data.value as AnnotationTextStyle["numberShape"],
                })
              }
            >
              <option value="circle">圆形</option>
              <option value="square">方形</option>
              <option value="diamond">菱形</option>
              <option value="none">无底形</option>
            </Select>
          </label>
        </>
      )}
      {tool === "callout" && (
        <label className="editor-style-control">
          <span aria-hidden="true">说明</span>
          <Select
            aria-label="说明样式"
            size="small"
            value={style.calloutStyle ?? "bubble"}
            onChange={(_, data) =>
              onChange({
                calloutStyle: data.value as AnnotationTextStyle["calloutStyle"],
              })
            }
          >
            <option value="bubble">气泡说明</option>
            <option value="arrow">箭头说明</option>
          </Select>
        </label>
      )}
    </>
  );
}

/** 输入草稿独立于已生效数值：合法数字即时同步，空/非法输入保留至失焦。 */
export function AnnotationNumberInput({
  value,
  label,
  onValueChange,
}: {
  value: number;
  label: "序号值" | "序号起点";
  onValueChange: (value: number) => void;
}) {
  const [draft, setDraft] = useState({ value, text: String(value) });
  if (draft.value !== value) setDraft({ value, text: String(value) });
  return (
    <label className="editor-style-control">
      <span aria-hidden="true">{label === "序号起点" ? "起点" : label}</span>
      <Input
        aria-label={label}
        type="number"
        size="small"
        min={1}
        max={9999}
        value={draft.text}
        onChange={(_, data) => {
          const next = Number(data.value);
          const valid =
            data.value.trim() !== "" &&
            Number.isInteger(next) &&
            next >= 1 &&
            next <= 9999;
          setDraft({ value: valid ? next : value, text: data.value });
          if (valid) onValueChange(next);
        }}
        onBlur={() => setDraft({ value, text: String(value) })}
      />
    </label>
  );
}
