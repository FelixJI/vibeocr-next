import { Button, Select } from "@fluentui/react-components";

import type { AppActions } from "../app/types";

import { PaddleOptionsEditor } from "../components/PaddleOptionsEditor";

import { availabilityLabel } from "./FeatureValues";

import type { RecognitionEngineState } from "./TaskEngineValues";

// 设备选项由 PaddleOptionsEditor 的 device 编辑器承载；Rapid 系模式的
// 独立设备持久化链已随 RapidOCR/RapidTable 退役移除。

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
          : `，${availabilityLabel(defaultMode.availability)}`
      }）`
    : "跟随默认（未读取）";
  const activeSupportsModelPreset =
    active?.supportedOptions?.includes("model_preset") === true;
  const activeIsPaddle =
    (active?.engine.startsWith("paddle_") ?? false) &&
    Array.isArray(active?.supportedOptions);
  // 本次模式是任务提交入口，常驻可见；档位/引擎参数/默认入口与生命周期
  // 技术提示属于低频诊断，收进折叠区，键盘可展开且语义不变。
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
                    {`${engine.displayName}（${availabilityLabel(engine.availability)}${engine.requiresDownload ? "，需下载" : ""}）`}
                  </option>
                ))}
              </optgroup>
            ) : null;
          })}
        </Select>
      </div>
      {/* 低频项保留就近短入口：引擎参数由 PaddleOptionsEditor 自带的
         “识别参数”折叠区承载，不再套第二层折叠。 */}
      {activeIsPaddle && active && (
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
      )}
      <details className="task-engine-advanced">
        <summary>高级选项</summary>
        <div className="task-engine-advanced-content">
          {activeSupportsModelPreset && active && (
            <div className="setting-row">
              <label htmlFor={`${scope}-task-model-preset`}>本次模型档位</label>
              <Select
                id={`${scope}-task-model-preset`}
                value={active.taskModelPreset ?? ""}
                disabled={!enabled}
                onChange={(_, data) =>
                  actions.run({
                    type: `${scope}.setTaskModelPreset`,
                    ...(data.value === ""
                      ? {}
                      : { preset: String(data.value) }),
                  })
                }
              >
                <option value="">{`跟随默认（${active.defaultModelPreset ?? "small"}）`}</option>
                <option value="tiny">tiny</option>
                <option value="small">small</option>
                <option value="medium">medium</option>
              </Select>
              <p className="form-note">
                仅影响后续提交，不改写已保存的默认档位。
              </p>
            </div>
          )}
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
          {lifecycleHint(active) ? (
            <p className="form-note">{lifecycleHint(active)}</p>
          ) : null}
        </div>
      </details>
    </div>
  );
}
