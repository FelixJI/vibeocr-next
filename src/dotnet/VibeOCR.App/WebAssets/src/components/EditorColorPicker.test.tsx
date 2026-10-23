import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { EditorColorPicker } from "./EditorColorPicker";

it("颜色输入只提交完整十六进制值，RGB 滑块保留其他通道", () => {
  const change = vi.fn();
  render(
    <EditorColorPicker label="自定义颜色" value="#123456" onChange={change} />,
  );
  fireEvent.click(screen.getByRole("button", { name: "自定义颜色" }));
  const input = screen.getByRole("textbox", { name: "十六进制颜色" });
  fireEvent.change(input, { target: { value: "#zzzzzz" } });
  expect(change).not.toHaveBeenCalled();
  fireEvent.change(input, { target: { value: "#ABCDEF" } });
  expect(change).toHaveBeenLastCalledWith("#abcdef");
  fireEvent.change(screen.getByRole("slider", { name: "红" }), {
    target: { value: "255" },
  });
  expect(change).toHaveBeenLastCalledWith("#ff3456");
});
