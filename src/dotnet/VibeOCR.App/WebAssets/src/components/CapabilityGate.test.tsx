import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { CapabilityGate } from "./CapabilityGate";

afterEach(cleanup);

it("explains a missing capability on keyboard focus without running its command", async () => {
  const user = userEvent.setup();
  const run = vi.fn();
  render(
    <CapabilityGate
      capability="imageConversion.convert"
      capabilities={[]}
      action={{ type: "imageConversion.addFiles" }}
      actions={{ run, navigate: vi.fn(), setTheme: vi.fn() }}
    >
      添加图片
    </CapabilityGate>,
  );
  const button = screen.getByRole("button", { name: "添加图片" });
  expect(button).toHaveAttribute("aria-disabled", "true");
  await user.tab();
  expect(button).toHaveFocus();
  expect(await screen.findByRole("tooltip")).toHaveTextContent(
    "此功能需要宿主能力：imageConversion.convert",
  );
  await user.keyboard("{Enter} ");
  await user.click(button);
  expect(run).not.toHaveBeenCalled();
});
