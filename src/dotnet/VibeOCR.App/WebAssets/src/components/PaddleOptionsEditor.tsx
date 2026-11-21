import { Button, Select } from "@fluentui/react-components";
import { useState } from "react";
import type { AppActions } from "../app/types";

// #270：独立公式识别与 PaddleOCR-VL 模式已退役；本编辑器只保留仍可
// 选择模式的公开选项（历史记录中的退役字段由宿主 PaddleModeOptions
// 兼容读取，不在 UI 中暴露）。
const BOOLEAN_OPTIONS = {
  use_doc_orientation_classify: "文档方向分类",
  use_doc_unwarping: "文档去畸变",
  use_textline_orientation: "文本行方向分类",
  use_table_recognition: "识别表格",
  use_table_orientation_classify: "表格方向分类",
  use_ocr_results_with_table_cells: "结合单元格与 OCR 结果",
} as const;

type BooleanOption = keyof typeof BOOLEAN_OPTIONS;
type Options = Partial<Record<BooleanOption, boolean>> & {
  model_preset?: string;
  device?: string;
};

// 模型档位的唯一标签映射：任务级选择、跟随默认括号与保存回显共用，
// 不再向用户暴露裸英文枚举。
const MODEL_PRESET_LABELS: Record<string, string> = {
  tiny: "轻量",
  small: "均衡",
  medium: "高精度",
};

function modelPresetLabel(preset: string | null | undefined): string {
  if (!preset) return "均衡";
  return MODEL_PRESET_LABELS[preset] ?? preset;
}

function readOptions(value: unknown): Options {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  const source = value as Record<string, unknown>;
  const result: Options = {};
  for (const key of Object.keys(BOOLEAN_OPTIONS) as BooleanOption[]) {
    if (typeof source[key] === "boolean") result[key] = source[key];
  }
  if (typeof source.model_preset === "string")
    result.model_preset = source.model_preset;
  if (typeof source.device === "string") result.device = source.device;
  return result;
}

export function PaddleOptionsEditor({
  modeId,
  scope = "recognition",
  supportedOptions,
  values,
  // 任务级档位：null/undefined 表示“跟随默认”；选择立即经
  // {scope}.setTaskModelPreset 生效，不进入未保存编辑状态。
  taskModelPreset,
  defaultModelPreset,
  enabled = true,
  reasonCode,
  lifecycleNote,
  actions,
}: {
  readonly modeId: string;
  readonly scope?: "recognition" | "batch" | "pdf";
  readonly supportedOptions: readonly string[];
  readonly values: unknown;
  readonly taskModelPreset?: string | null;
  readonly defaultModelPreset?: string | null;
  readonly enabled?: boolean;
  readonly reasonCode?: string | null;
  readonly lifecycleNote?: string | null;
  readonly actions: AppActions;
}) {
  const [options, setOptions] = useState(() => readOptions(values));
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState("");
  // 对比前后都用 readOptions 规范化，键序差异不影响回显匹配。
  const localKey = JSON.stringify(readOptions(options));
  const incomingKey = JSON.stringify(readOptions(values));
  if (dirty && localKey === incomingKey) {
    setDirty(false);
  } else if (!dirty && localKey !== incomingKey) {
    setOptions(readOptions(values));
  }
  // 保存语义：当前任务档位（若已显式选择）固化为持久默认；跟随默认时
  // 沿用已保存默认，未保存过则省略该字段，不伪造引擎默认。
  const savedPreset = taskModelPreset ?? options.model_preset;
  const savePayload = readOptions(options);
  if (savedPreset !== undefined) {
    savePayload.model_preset = savedPreset;
  }
  return (
    <details className="recognition-options">
      <summary>识别参数</summary>
      <p className="form-note">
        档位与参数立即影响后续提交，运行中的任务保持原配置；保存后写入默认值。
        {modeId === "paddle_structure" &&
          "结构模式保留普通公式与图表区域，不提供公式、图表或印章专用识别。"}
      </p>
      {supportedOptions.includes("model_preset") && (
        <div className="setting-row">
          <label htmlFor={`${modeId}-model-preset`}>模型档位</label>
          <Select
            id={`${modeId}-model-preset`}
            value={taskModelPreset ?? ""}
            disabled={!enabled}
            onChange={(_, data) =>
              actions.run({
                type: `${scope}.setTaskModelPreset`,
                ...(data.value === "" ? {} : { preset: String(data.value) }),
              })
            }
          >
            <option value="">
              {`跟随默认（${modelPresetLabel(
                defaultModelPreset ?? options.model_preset,
              )}）`}
            </option>
            <option value="tiny">{modelPresetLabel("tiny")}</option>
            <option value="small">{modelPresetLabel("small")}</option>
            <option value="medium">{modelPresetLabel("medium")}</option>
          </Select>
          <p className="form-note">
            选择具体档位立即生效于后续提交；“保存参数”会把当前档位固化为默认。
          </p>
        </div>
      )}
      {supportedOptions.includes("device") && (
        <div className="setting-row">
          <label htmlFor={`${modeId}-provider`}>运行设备</label>
          <Select
            id={`${modeId}-provider`}
            value={options.device ?? "auto"}
            disabled={!enabled}
            onChange={(_, data) => {
              setDirty(true);
              setOptions((current) => ({ ...current, device: data.value }));
            }}
          >
            <option value="auto">跟随运行环境</option>
            <option value="cpu">CPU</option>
            <option value="directml">Windows DirectML</option>
          </Select>
          <p className="form-note">
            DirectML 需要对应的 ONNX 环境；不可用时会明确失败。
          </p>
        </div>
      )}
      {(Object.keys(BOOLEAN_OPTIONS) as BooleanOption[])
        .filter((key) => supportedOptions.includes(key))
        .map((key) => (
          <div className="setting-row" key={key}>
            <label htmlFor={`${modeId}-${key}`}>{BOOLEAN_OPTIONS[key]}</label>
            <Select
              id={`${modeId}-${key}`}
              value={
                options[key] === undefined ? "default" : String(options[key])
              }
              disabled={!enabled}
              onChange={(_, data) => {
                setDirty(true);
                setOptions((current) => {
                  const next = { ...current };
                  if (data.value === "default") delete next[key];
                  else next[key] = data.value === "true";
                  return next;
                });
              }}
            >
              <option value="default">使用引擎默认值</option>
              <option value="true">启用</option>
              <option value="false">关闭</option>
            </Select>
          </div>
        ))}
      <div className="button-row">
        <Button
          disabled={saving || !enabled}
          onClick={async () => {
            setSaving(true);
            try {
              const saved = await actions.run({
                type: "recognition.setOptions",
                modeId,
                options: savePayload,
              });
              setMessage(
                saved
                  ? "参数已保存（含当前档位），新任务起作为默认值。"
                  : "参数未保存，请检查当前环境和参数范围。",
              );
            } finally {
              setSaving(false);
            }
          }}
        >
          保存参数
        </Button>
        <Button
          appearance="secondary"
          disabled={!enabled}
          onClick={() => actions.navigate("settings")}
        >
          修改默认识别类型
        </Button>
      </div>
      <output aria-live="polite">{message}</output>
      {reasonCode ? <p className="form-note">当前环境：{reasonCode}</p> : null}
      {lifecycleNote ? <p className="form-note">{lifecycleNote}</p> : null}
    </details>
  );
}
