import { Button, Select } from "@fluentui/react-components";

import type { AppActions } from "../app/types";

import { PaddleOptionsEditor } from "../components/PaddleOptionsEditor";

import { availabilityLabel } from "./FeatureValues";

import type { RecognitionEngineState } from "./TaskEngineValues";

// 目录 supported_options 声明 device 的模式（rapid_table / rapid_text）持久
// 设备选项；缺省（无持久值）跟随 Runtime 默认 auto。
function modeDevice(value: unknown): string {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return "auto";
  const device = (value as { readonly device?: unknown }).device;
  return device === "cpu" || device === "cuda" ? device : "auto";
}

function lifecycleHint(
  engine: RecognitionEngineState | undefined,
): string | null {
  if (!engine) return null;
  const controls = [
    engine.supportsPreload === true ? "预热" : null,
    engine.supportsTtl === true ? "TTL" : null,
    engine.supportsPinning === true ? "固定驻留" : null,
    engine.supportsRelease === true ? "释放" : null,
  ].filter((value): value is string => value !== null);
  if (engine.lifecycleKind === "unmanaged") {
    return "该模式不提供模型预热、TTL、固定驻留或释放控制。";
  }
  if (engine.lifecycleKind === "process_keep_alive") {
    return controls.length === 0
      ? "本地 MinerU 使用进程保活；当前目录未声明可用控制。远程模型生命周期由服务端管理。"
      : `本地 MinerU 使用进程保活；仅支持：${controls.join("、")}。远程模型生命周期由服务端管理。`;
  }
  if (engine.lifecycleKind === "model_residency") {
    return controls.length === 0
      ? "该 Paddle 模式使用模型驻留；当前目录未声明可用控制。"
      : `该 Paddle 模式使用模型驻留；支持：${controls.join("、")}。`;
  }
  return null;
}

export function TaskEngineSelector({
  engines,
  taskEngine,
  enabled,
  actions,
  scope = "recognition",
}: {
  readonly engines: readonly RecognitionEngineState[];
  readonly taskEngine: string | undefined;
  readonly enabled: boolean;
  readonly actions: AppActions;
  readonly scope?: "recognition" | "batch" | "pdf";
}) {
  if (engines.length === 0) {
    return null;
  }
  const active =
    engines.find((engine) => engine.isTaskOverride) ??
    engines.find((engine) => engine.selected);
  // 默认标识独立于本次选择：override 时继承选项仍展示已提交默认及其
  // 可用性；旧宿主无 isDefault 时回退“选中且非覆盖”推断，未读取不伪造。
  const defaultMode =
    engines.find((engine) => engine.isDefault === true) ??
    engines.find((engine) => engine.selected && !engine.isTaskOverride);
  const inheritLabel = defaultMode
    ? `跟随默认（${defaultMode.displayName}${
        defaultMode.availability === "ready"
          ? ""
          : `，${availabilityLabel(defaultMode.availability)}`
      }）`
    : "跟随默认（未读取）";
  return (
    <div className="setting-row recognition-mode-settings">
      <label htmlFor={`${scope}-task-engine`}>本次识别模式</label>
      <Select
        id={`${scope}-task-engine`}
        value={taskEngine ?? ""}
        disabled={!enabled}
        onChange={(_, data) =>
          data.value === ""
            ? actions.run({ type: `${scope}.setTaskEngine` })
            : actions.run({
                type: `${scope}.setTaskEngine`,
                engine: String(data.value),
              })
        }
      >
        <option value="">{inheritLabel}</option>
        {(
          [
            ["文字", ["paddle_text"]],
            ["表格", ["rapid_table", "paddle_table"]],
            ["公式", ["paddle_formula"]],
            ["文档结构", ["paddle_structure", "paddle_document_vl"]],
            ["其他引擎", []],
          ] as const
        ).map(([label, ids]) => {
          const choices = engines.filter((engine) =>
            ids.length === 0
              ? ![
                  "paddle_text",
                  "rapid_table",
                  "paddle_table",
                  "paddle_formula",
                  "paddle_structure",
                  "paddle_document_vl",
                ].includes(engine.engine)
              : (ids as readonly string[]).includes(engine.engine),
          );
          return choices.length ? (
            <optgroup key={label} label={label}>
              {choices.map((engine) => (
                <option key={engine.engine} value={engine.engine}>
                  {`${engine.displayName}（${availabilityLabel(engine.availability)}${engine.requiresDownload ? "，需下载" : ""}）`}
                </option>
              ))}
            </optgroup>
          ) : null;
        })}
      </Select>
      {active?.reasonCode && (
        <p className="form-note">当前环境：{active.reasonCode}</p>
      )}
      {!active?.isTaskOverride ? (
        <p className="form-note">本次选择仅覆盖当前任务，不会改写默认。</p>
      ) : null}
      <div className="setting-row">
        <Button
          appearance="secondary"
          disabled={!enabled}
          onClick={() => actions.navigate("settings")}
        >
          修改默认识别类型
        </Button>
      </div>
      {active?.engine.startsWith("paddle_") &&
        Array.isArray(active.supportedOptions) && (
          // key 只绑定模式：保存成功后宿主会回显新的 active.options，若把它混入
          // key，保存瞬间会重挂编辑器并丢失保存提示与展开状态；宿主值同步由
          // 编辑器在无未保存编辑时自行完成。
          <PaddleOptionsEditor
            key={active.engine}
            modeId={active.engine}
            supportedOptions={active.supportedOptions}
            values={active.options}
            actions={actions}
          />
        )}{" "}
      {active?.supportedOptions?.includes("device") && (
        <div className="setting-row">
          <label htmlFor={`${scope}-mode-device`}>运行设备</label>
          <Select
            id={`${scope}-mode-device`}
            value={modeDevice(active.options)}
            disabled={!enabled}
            onChange={(_, data) =>
              actions.run({
                type: "recognition.setDeviceOption",
                modeId: active.engine,
                device: String(data.value),
              })
            }
          >
            <option value="auto">自动（可用时用 GPU）</option>
            <option value="cpu">CPU</option>
            <option value="cuda">GPU（CUDA）</option>
          </Select>
          <p className="form-note">
            GPU 需要 NVIDIA 显卡及 GPU 运行环境；不可用时会提示错误。
          </p>
        </div>
      )}
      {lifecycleHint(active) ? (
        <p className="form-note">{lifecycleHint(active)}</p>
      ) : null}
    </div>
  );
}
