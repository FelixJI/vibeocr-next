import { Button, Input, Select } from "@fluentui/react-components";
import { useState } from "react";
import type { AppActions } from "../app/types";

const BOOLEAN_OPTIONS = {
  use_doc_orientation_classify: "文档方向分类",
  use_doc_unwarping: "文档去畸变",
  use_textline_orientation: "文本行方向分类",
  use_table_recognition: "识别表格",
  use_formula_recognition: "识别公式",
  use_seal_recognition: "识别印章",
  use_chart_recognition: "识别图表",
  vl_use_layout_detection: "VL 版面检测",
  vl_use_chart_recognition: "VL 图表识别",
  vl_use_seal_recognition: "VL 印章识别",
  use_ocr_for_image_block: "识别图像区块文字",
  use_table_orientation_classify: "表格方向分类",
  use_ocr_results_with_table_cells: "结合单元格与 OCR 结果",
} as const;

type BooleanOption = keyof typeof BOOLEAN_OPTIONS;
type Options = Partial<Record<BooleanOption, boolean>> & {
  formula_recognition_batch_size?: number;
  formula_recognition_model_name?: string;
};
const FORMULA_MODELS = [
  "LaTeX_OCR_rec",
  "PP-FormulaNet-L",
  "PP-FormulaNet-S",
  "PP-FormulaNet_plus-L",
  "PP-FormulaNet_plus-M",
  "PP-FormulaNet_plus-S",
  "UniMERNet",
];

function readOptions(value: unknown): Options {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  const source = value as Record<string, unknown>;
  const result: Options = {};
  for (const key of Object.keys(BOOLEAN_OPTIONS) as BooleanOption[]) {
    if (typeof source[key] === "boolean") result[key] = source[key];
  }
  if (typeof source.formula_recognition_batch_size === "number")
    result.formula_recognition_batch_size =
      source.formula_recognition_batch_size;
  if (typeof source.formula_recognition_model_name === "string")
    result.formula_recognition_model_name =
      source.formula_recognition_model_name;
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
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState("");
  const batchSize = options.formula_recognition_batch_size;
  const valid =
    batchSize === undefined ||
    (Number.isInteger(batchSize) && batchSize >= 1 && batchSize <= 64);
  return (
    <details className="recognition-options">
      <summary>识别参数</summary>
      <p className="form-note">
        默认值由当前环境提供；保存后用于新任务，运行中的任务保持原配置。
      </p>
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
              onChange={(_, data) =>
                setOptions((current) => {
                  const next = { ...current };
                  if (data.value === "default") delete next[key];
                  else next[key] = data.value === "true";
                  return next;
                })
              }
            >
              <option value="default">使用引擎默认值</option>
              <option value="true">启用</option>
              <option value="false">关闭</option>
            </Select>
          </div>
        ))}
      {supportedOptions.includes("formula_recognition_batch_size") && (
        <div className="setting-row">
          <label htmlFor={`${modeId}-formula-batch`}>公式模型批量大小</label>
          <Input
            id={`${modeId}-formula-batch`}
            type="number"
            min={1}
            max={64}
            step={1}
            value={batchSize === undefined ? "" : String(batchSize)}
            placeholder="使用引擎默认值"
            onChange={(_, data) =>
              setOptions((current) => {
                const next = { ...current };
                if (data.value === "")
                  delete next.formula_recognition_batch_size;
                else next.formula_recognition_batch_size = Number(data.value);
                return next;
              })
            }
          />
        </div>
      )}
      {supportedOptions.includes("formula_recognition_model_name") && (
        <div className="setting-row">
          <label htmlFor={`${modeId}-formula-model`}>公式模型</label>
          <Select
            id={`${modeId}-formula-model`}
            value={options.formula_recognition_model_name ?? ""}
            onChange={(_, data) =>
              setOptions((current) => {
                const next = { ...current };
                if (data.value === "")
                  delete next.formula_recognition_model_name;
                else next.formula_recognition_model_name = data.value;
                return next;
              })
            }
          >
            <option value="">使用引擎默认模型</option>
            {FORMULA_MODELS.map((model) => (
              <option key={model} value={model}>
                {model}
              </option>
            ))}
          </Select>
        </div>
      )}
      <Button
        disabled={saving || !valid}
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
      <output aria-live="polite">
        {valid ? message : "公式批量大小须为 1–64 的整数。"}
      </output>
    </details>
  );
}
