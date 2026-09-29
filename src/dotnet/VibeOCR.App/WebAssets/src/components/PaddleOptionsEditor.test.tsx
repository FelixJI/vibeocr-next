import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { PaddleOptionsEditor } from "./PaddleOptionsEditor";
afterEach(cleanup);

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
