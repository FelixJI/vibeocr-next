import { Button, Select } from "@fluentui/react-components";

import type { AppActions } from "../app/types";

import { PaddleOptionsEditor } from "../components/PaddleOptionsEditor";

import { modeAvailabilityLabel } from "./FeatureValues";

import type { RecognitionEngineState } from "./TaskEngineValues";

// 设备选项由 PaddleOptionsEditor 的 device 编辑器承载；Rapid 系模式的
// 独立设备持久化链已随 RapidOCR/RapidTable 退役移除。
// 模型档位整合在“识别参数”内：任务级选择（跟随默认/轻量/均衡/高精度）
// 与“保存参数”固化默认共用同一入口，不再保留并列的“高级选项”折叠区。

function lifecycleHint(
  engine: RecognitionEngineState | undefined,
): string | null {
  if (!engine) return null;
  if (engine.lifecycleKind === "unmanaged") return "该模式由操作系统管理。";
  if (engine.lifecycleKind === "process_keep_alive")
    return "空闲后释放进程资源，可在设置中调整或设为持久；远程 MinerU 由服务端管理。";
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
          : `，${modeAvailabilityLabel(defaultMode.availability, defaultMode.reasonCode)}`
      }）`
    : "跟随默认（未读取）";
  const activeIsPaddle =
    (active?.engine.startsWith("paddle_") ?? false) &&
    Array.isArray(active?.supportedOptions);
  // 本次模式是任务提交入口，常驻可见；引擎参数/档位/默认入口与环境、
  // 生命周期技术提示收进“识别参数”折叠区，键盘可展开且语义不变。
  return (
    <div className="recognition-mode-settings">
      <div className="setting-row recognition-mode-row">
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
              ["表格", ["paddle_table"]],
              ["文档结构", ["paddle_structure"]],
              ["其他引擎", []],
            ] as const
          ).map(([label, ids]) => {
            const choices = engines.filter((engine) =>
              ids.length === 0
                ? !["paddle_text", "paddle_table", "paddle_structure"].includes(
                    engine.engine,
                  )
                : (ids as readonly string[]).includes(engine.engine),
            );
            return choices.length ? (
              <optgroup key={label} label={label}>
                {choices.map((engine) => (
                  <option key={engine.engine} value={engine.engine}>
                    {`${engine.displayName}（${modeAvailabilityLabel(engine.availability, engine.reasonCode)}${engine.requiresDownload ? "，需下载" : ""}）`}
                  </option>
                ))}
              </optgroup>
            ) : null;
          })}
        </Select>
      </div>
      {!active?.isTaskOverride ? (
        <p className="form-note">本次选择仅覆盖当前任务，不会改写默认。</p>
      ) : null}
      {activeIsPaddle && active && (
        // key 只绑定模式：保存成功后宿主会回显新的 active.options，若把它混入
        // key，保存瞬间会重挂编辑器并丢失保存提示与展开状态；宿主值同步由
        // 编辑器在无未保存编辑时自行完成。
        <PaddleOptionsEditor
          key={active.engine}
          modeId={active.engine}
          scope={scope}
          supportedOptions={active.supportedOptions}
          values={active.options}
          taskModelPreset={active.taskModelPreset}
          defaultModelPreset={active.defaultModelPreset}
          enabled={enabled}
          reasonCode={active.reasonCode}
          lifecycleNote={lifecycleHint(active)}
          actions={actions}
        />
      )}
      {!activeIsPaddle && active && (
        // 非 Paddle 模式的参数由运行环境或服务端管理：同一“识别参数”折叠
        // 区只承载环境、生命周期提示与默认模式入口，保持单一折叠语义。
        <details className="recognition-options">
          <summary>识别参数</summary>
          <p className="form-note">该模式的参数由运行环境或服务端管理。</p>
          {active.reasonCode ? (
            <p className="form-note">当前环境：{active.reasonCode}</p>
          ) : null}
          {lifecycleHint(active) ? (
            <p className="form-note">{lifecycleHint(active)}</p>
          ) : null}
          <div className="button-row">
            <Button
              appearance="secondary"
              disabled={!enabled}
              onClick={() => actions.navigate("settings")}
            >
              修改默认识别类型
            </Button>
          </div>
        </details>
      )}
    </div>
  );
}
