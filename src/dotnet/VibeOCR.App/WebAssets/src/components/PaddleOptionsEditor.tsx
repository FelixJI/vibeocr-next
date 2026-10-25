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
  supportedOptions,
  values,
  actions,
}: {
  readonly modeId: string;
  readonly supportedOptions: readonly string[];
  readonly values: unknown;
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
  return (
    <details className="recognition-options">
      <summary>识别参数</summary>
      <p className="form-note">
        默认值由当前环境提供；保存后用于新任务，运行中的任务保持原配置。
        {modeId === "paddle_structure" &&
          "结构模式保留普通公式与图表区域，不提供公式、图表或印章专用识别。"}
      </p>
      {supportedOptions.includes("model_preset") && (
        <div className="setting-row">
          <label htmlFor={`${modeId}-model-preset`}>模型档位</label>
          <Select
            id={`${modeId}-model-preset`}
            value={options.model_preset ?? "small"}
            onChange={(_, data) => {
              setDirty(true);
              setOptions((current) => ({
                ...current,
                model_preset: data.value,
              }));
            }}
          >
            <option value="tiny">轻量</option>
            <option value="small">均衡（默认）</option>
            <option value="medium">高精度</option>
          </Select>
        </div>
      )}
      {supportedOptions.includes("device") && (
        <div className="setting-row">
          <label htmlFor={`${modeId}-provider`}>运行设备</label>
          <Select
            id={`${modeId}-provider`}
            value={options.device ?? "auto"}
            onChange={(_, data) => {
              setDirty(true);
              setOptions((current) => ({ ...current, device: data.value }));
            }}
          >
            <option value="auto">跟随运行环境</option>
            <option value="cpu">CPU</option>
            <option value="cuda">NVIDIA CUDA</option>
            <option value="directml">Windows DirectML</option>
          </Select>
          <p className="form-note">
            CUDA 和 DirectML 需要对应的 ONNX 环境；不可用时会明确失败。
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
      <Button
        disabled={saving}
        onClick={async () => {
          setSaving(true);
          try {
            const saved = await actions.run({
              type: "recognition.setOptions",
              modeId,
              options,
            });
            setMessage(
              saved
                ? "参数已保存，下一次识别生效。"
                : "参数未保存，请检查当前环境和参数范围。",
            );
          } finally {
            setSaving(false);
          }
        }}
      >
        保存参数
      </Button>
      <output aria-live="polite">{message}</output>
    </details>
  );
}
