import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
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

it("rejects an invalid formula batch before submitting", async () => {
  const run = vi.fn();
  const user = userEvent.setup();
  render(
    <PaddleOptionsEditor
      modeId="paddle_formula"
      supportedOptions={["formula_recognition_batch_size"]}
      values={{}}
      actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
    />,
  );
  await user.click(screen.getByText("识别参数"));
  await user.type(screen.getByLabelText("公式模型批量大小"), "0");
  expect(screen.getByRole("button", { name: "保存参数" })).toBeDisabled();
  expect(run).not.toHaveBeenCalled();
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
