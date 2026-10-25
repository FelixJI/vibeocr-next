import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { TaskEngineSelector } from "../features/TaskEngineSelection";
import { PaddleOptionsEditor } from "./PaddleOptionsEditor";
import { RecognitionPage } from "../features/Pages";
import type { AppActions, AppViewState } from "../app/types";
afterEach(cleanup);

const actions = (): AppActions => ({
  run: vi.fn().mockResolvedValue(true),
  navigate: vi.fn(),
  setTheme: vi.fn(),
});

it("submits explicit typed overrides and omits default fields", async () => {
  const run = vi.fn().mockResolvedValue(true);
  const user = userEvent.setup();
  render(
    <PaddleOptionsEditor
      modeId="paddle_table"
      supportedOptions={[
        "use_doc_unwarping",
        "use_ocr_results_with_table_cells",
      ]}
      values={{ use_doc_unwarping: false }}
      actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
    />,
  );
  await user.click(screen.getByText("识别参数"));
  await user.selectOptions(screen.getByLabelText("文档去畸变"), "default");
  await user.selectOptions(
    screen.getByLabelText("结合单元格与 OCR 结果"),
    "false",
  );
  expect(screen.queryByLabelText("识别印章")).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "保存参数" }));
  expect(run).toHaveBeenCalledWith({
    type: "recognition.setOptions",
    modeId: "paddle_table",
    options: { use_ocr_results_with_table_cells: false },
  });
});

it("omits retired formula fields from the compatibility echo", async () => {
  // #270：退役字段（VL/公式专用）不再出现在编辑器；旧记录中残留的字段
  // 在回显投影中被忽略，不参与保存提交。
  const run = vi.fn().mockResolvedValue(true);
  const user = userEvent.setup();
  render(
    <PaddleOptionsEditor
      modeId="paddle_structure"
      supportedOptions={["use_doc_unwarping", "use_table_recognition"]}
      values={{
        use_doc_unwarping: true,
        formula_recognition_batch_size: 8,
        vl_use_layout_detection: true,
      }}
      actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
    />,
  );
  await user.click(screen.getByText("识别参数"));
  expect(screen.queryByLabelText("公式模型批量大小")).not.toBeInTheDocument();
  expect(screen.queryByLabelText("VL 版面检测")).not.toBeInTheDocument();
  expect(screen.getByLabelText("识别表格")).toHaveValue("default");
  await user.click(screen.getByRole("button", { name: "保存参数" }));
  // 同记录仍合法的字段保留（退役字段被剔除，不进入提交载荷）。
  expect(run).toHaveBeenCalledWith({
    type: "recognition.setOptions",
    modeId: "paddle_structure",
    options: { use_doc_unwarping: true },
  });
});

it("follows host-provided defaults when no local edits are pending", async () => {
  const user = userEvent.setup();
  const dispatch = actions();
  const props = {
    modeId: "paddle_text",
    supportedOptions: ["use_textline_orientation"],
    actions: dispatch,
  };
  const { rerender } = render(<PaddleOptionsEditor {...props} values={{}} />);
  await user.click(screen.getByText("识别参数"));
  expect(screen.getByLabelText("文本行方向分类")).toHaveValue("default");
  // 环境默认值/模式目录刷新到达时，无未保存编辑的编辑器重新初始化。
  rerender(
    <PaddleOptionsEditor
      {...props}
      values={{ use_textline_orientation: true }}
    />,
  );
  expect(screen.getByLabelText("文本行方向分类")).toHaveValue("true");
});

it("keeps unsaved edits over unrelated refreshes and resyncs after the save echo", async () => {
  const user = userEvent.setup();
  const dispatch = actions();
  const props = {
    modeId: "paddle_text",
    supportedOptions: ["use_textline_orientation", "use_doc_unwarping"],
    actions: dispatch,
  };
  const { rerender } = render(<PaddleOptionsEditor {...props} values={{}} />);
  await user.click(screen.getByText("识别参数"));
  await user.selectOptions(screen.getByLabelText("文本行方向分类"), "true");
  // 宿主推送其他选项的默认值时不得覆盖本地未保存编辑。
  rerender(
    <PaddleOptionsEditor {...props} values={{ use_doc_unwarping: false }} />,
  );
  expect(screen.getByLabelText("文本行方向分类")).toHaveValue("true");
  expect(screen.getByLabelText("文档去畸变")).toHaveValue("default");
  // 保存成功的回显追平本地值后解除未保存标记，编辑值保持不变。
  rerender(
    <PaddleOptionsEditor
      {...props}
      values={{ use_textline_orientation: true }}
    />,
  );
  expect(screen.getByLabelText("文本行方向分类")).toHaveValue("true");
  // 清洁状态下再次收到新的环境默认值时恢复跟随宿主。
  rerender(<PaddleOptionsEditor {...props} values={{}} />);
  expect(screen.getByLabelText("文本行方向分类")).toHaveValue("default");
});

it("keeps the save confirmation visible when the host echoes saved options", async () => {
  const dispatch = actions();
  const engines = (options: unknown) => [
    {
      engine: "paddle_text",
      displayName: "通用 OCR（PaddleOCR）",
      selected: true,
      isTaskOverride: true,
      availability: "ready",
      requiresDownload: false,
      supportedOptions: ["use_textline_orientation"],
      options,
    },
  ];
  const viewState = (revision: number, options: unknown): AppViewState => ({
    connected: true,
    revision,
    route: "recognition",
    theme: "light",
    capabilities: ["recognition.engine"],
    features: {
      recognition: { taskEngine: "paddle_text", engines: engines(options) },
    },
    runtimeLabel: "原生宿主已连接",
  });
  const user = userEvent.setup();
  const { rerender } = render(
    <RecognitionPage actions={dispatch} viewState={viewState(1, {})} />,
  );
  await user.click(screen.getByText("识别参数"));
  const details = screen.getByText("识别参数").closest("details");
  await user.selectOptions(screen.getByLabelText("文本行方向分类"), "true");
  await user.click(screen.getByRole("button", { name: "保存参数" }));
  expect(dispatch.run).toHaveBeenCalledWith({
    type: "recognition.setOptions",
    modeId: "paddle_text",
    options: { use_textline_orientation: true },
  });
  expect(await screen.findByText("参数已保存，下一次识别生效。")).toBeVisible();
  // 保存成功后宿主回显新的模式选项（revision 递增的状态推送）；
  // 编辑器不得因此重挂而丢失保存提示、已展开面板与已选值。
  rerender(
    <RecognitionPage
      actions={dispatch}
      viewState={viewState(2, { use_textline_orientation: true })}
    />,
  );
  expect(screen.getByText("参数已保存，下一次识别生效。")).toBeVisible();
  expect(screen.getByLabelText("文本行方向分类")).toHaveValue("true");
  expect(screen.getByText("识别参数").closest("details")).toBe(details);
});

it("defaults to balanced and saves an actual Paddle model/provider choice", async () => {
  const run = vi.fn().mockResolvedValue(true);
  const user = userEvent.setup();
  render(
    <PaddleOptionsEditor
      modeId="paddle_text"
      supportedOptions={["model_preset", "device"]}
      values={{}}
      actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
    />,
  );
  await user.click(screen.getByText("识别参数"));
  expect(screen.getByLabelText("模型档位")).toHaveValue("small");
  await user.selectOptions(screen.getByLabelText("模型档位"), "medium");
  await user.selectOptions(screen.getByLabelText("运行设备"), "directml");
  await user.click(screen.getByRole("button", { name: "保存参数" }));
  expect(run).toHaveBeenCalledWith({
    type: "recognition.setOptions",
    modeId: "paddle_text",
    options: { model_preset: "medium", device: "directml" },
  });
});

it.each(["recognition", "batch", "pdf"] as const)(
  "sends a transient model preset for %s without saving defaults",
  async (scope) => {
    const run = vi.fn().mockResolvedValue(true);
    const user = userEvent.setup();
    render(
      <TaskEngineSelector
        scope={scope}
        enabled
        taskEngine={undefined}
        engines={[
          {
            engine: "paddle_text",
            displayName: "Paddle OCR",
            selected: true,
            isTaskOverride: false,
            availability: "ready",
            requiresDownload: false,
            supportedOptions: ["model_preset"],
            defaultModelPreset: "medium",
            taskModelPreset: null,
            options: { model_preset: "medium" },
          },
        ]}
        actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
      />,
    );
    expect(
      screen.getByRole("option", { name: "跟随默认（medium）" }),
    ).toBeVisible();
    await user.selectOptions(screen.getByLabelText("本次模型档位"), "tiny");
    expect(run).toHaveBeenCalledExactlyOnceWith({
      type: `${scope}.setTaskModelPreset`,
      preset: "tiny",
    });
    await user.selectOptions(screen.getByLabelText("本次模型档位"), "");
    expect(run).toHaveBeenLastCalledWith({
      type: `${scope}.setTaskModelPreset`,
    });
  },
);
