import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";

import { HotkeyRecorder, type HotkeyRecorderProps } from "./HotkeyRecorder";

afterEach(cleanup);

/**
 * 快捷键录入控件的最小语义键事件测试：修饰状态取自事件真实
 * ctrlKey/altKey/shiftKey/metaKey（含聚焦前/等待期按下）、完整组合在修饰键
 * 释放后保持、start 等待中的结束与迟到 ack 补偿、onEnd 失败可见；组合语法
 * 与保存事务由宿主注册器裁决，这里不做全组合穷举。
 */

function recorder(props: Partial<HotkeyRecorderProps> = {}) {
  const onChange = vi.fn();
  const onCancel = vi.fn();
  const onEnd = vi.fn();
  const view = render(
    <HotkeyRecorder
      label="快捷截图识别新快捷键"
      value="Ctrl+Alt+Q"
      onChange={onChange}
      onCancel={onCancel}
      onEnd={onEnd}
      {...props}
    />,
  );
  return {
    view,
    onChange,
    onCancel,
    onEnd,
    input: screen.getByLabelText("快捷截图识别新快捷键") as HTMLInputElement,
  };
}

const keyDown = (input: HTMLElement, key: string, init: object = {}) =>
  fireEvent.keyDown(input, { key, ...init });
const keyUp = (input: HTMLElement, key: string, init: object = {}) =>
  fireEvent.keyUp(input, { key, ...init });

function pendingStart(): {
  readonly onStart: (recordingId: string) => Promise<void>;
  resolve: () => void;
} {
  let resolveAck: (() => void) | undefined;
  const onStart = vi.fn(
    () =>
      new Promise<void>((resolve) => {
        resolveAck = resolve;
      }),
  );
  return { onStart, resolve: () => resolveAck?.() };
}

it("shows held modifiers immediately and keeps the last combo across releases", async () => {
  const user = userEvent.setup();
  const { input, onChange } = recorder();
  await user.click(input);

  keyDown(input, "Control", { ctrlKey: true });
  expect(input.value).toBe("Ctrl");
  keyDown(input, "Shift", { ctrlKey: true, shiftKey: true });
  expect(input.value).toBe("Ctrl+Shift");
  keyDown(input, "s", { ctrlKey: true, shiftKey: true });
  expect(input.value).toBe("Ctrl+Shift+S");
  expect(onChange).toHaveBeenLastCalledWith("Ctrl+Shift+S");

  // 回归：完整组合形成后任意修饰键释放都不降级为中间态。
  keyUp(input, "Shift", { ctrlKey: true });
  expect(input.value).toBe("Ctrl+Shift+S");
  keyUp(input, "Control", {});
  expect(input.value).toBe("Ctrl+Shift+S");
  expect(onChange).toHaveBeenLastCalledWith("Ctrl+Shift+S");

  // 按住产生的重复事件不产生重复键名或额外草稿。
  keyDown(input, "s", { repeat: true });
  expect(input.value).toBe("Ctrl+Shift+S");
  expect(onChange).toHaveBeenCalledTimes(3);

  // 下一次输入（新主键）才替换最近完整组合；主键支持 F1-F24 语法。
  keyDown(input, "F5", { altKey: true });
  expect(input.value).toBe("Alt+F5");
  expect(onChange).toHaveBeenLastCalledWith("Alt+F5");
});

it("derives modifiers from real key state including presses before capture", async () => {
  const user = userEvent.setup();
  const { onStart, resolve } = pendingStart();
  const { input, onChange, onEnd } = recorder({ onStart });
  await user.click(input);
  expect(screen.getByText("正在等待宿主释放旧快捷键…")).toBeInTheDocument();

  // 带修饰键的 Escape 不是裸 Esc（真实修饰标志 guard）：不取消等待。
  keyDown(input, "Escape", { ctrlKey: true });
  expect(screen.getByText("正在等待宿主释放旧快捷键…")).toBeInTheDocument();
  expect(onEnd).not.toHaveBeenCalled();

  // 等待宿主确认期间不接组合，避免旧 OS 注册吞键/误动作。
  keyDown(input, "Shift", { shiftKey: true });
  keyDown(input, "s", { shiftKey: true });
  expect(onChange).not.toHaveBeenCalled();
  expect(input.value).toBe("Ctrl+Alt+Q");

  await act(async () => {
    resolve();
  });
  expect(screen.getByText("请按下新的组合键，按 Esc 取消")).toBeInTheDocument();

  // 回归：Ctrl/Shift 在聚焦前或等待期按下、捕获期没有收到过其 keydown，
  // 主键事件的真实修饰状态仍组成正确组合。
  keyDown(input, "s", { ctrlKey: true, shiftKey: true });
  expect(input.value).toBe("Ctrl+Shift+S");
  expect(onChange).toHaveBeenCalledTimes(1);
  expect(onChange).toHaveBeenLastCalledWith("Ctrl+Shift+S");
});

it("blur during pending start restores and a late ack re-ends the host", async () => {
  const user = userEvent.setup();
  const { onStart, resolve } = pendingStart();
  const { input, onChange, onEnd } = recorder({ onStart });
  await user.click(input);

  fireEvent.blur(input);
  expect(onEnd).toHaveBeenCalledOnce();
  expect(input.value).toBe("Ctrl+Alt+Q");

  // 回归：迟到的开始成功不能留下宿主 recording，必须再次 end（幂等补偿）。
  await act(async () => {
    resolve();
  });
  expect(onEnd).toHaveBeenCalledTimes(2);

  // 已不在录入态，键事件不接。
  keyDown(input, "s", { ctrlKey: true });
  expect(onChange).not.toHaveBeenCalled();
});

it("unmount during pending start also ends and compensates a late ack", async () => {
  const user = userEvent.setup();
  const { onStart, resolve } = pendingStart();
  const { view, onEnd } = recorder({ onStart });
  await user.click(screen.getByLabelText("快捷截图识别新快捷键"));

  view.unmount();
  expect(onEnd).toHaveBeenCalledOnce();

  await act(async () => {
    resolve();
  });
  expect(onEnd).toHaveBeenCalledTimes(2);
});

it("surfaces end failures instead of swallowing them", async () => {
  const user = userEvent.setup();
  const onEnd = vi.fn(() => Promise.reject(new Error("恢复失败")));
  const { input } = recorder({ onEnd });
  await user.click(input);

  keyDown(input, "Control", { ctrlKey: true });
  keyDown(input, "s", { ctrlKey: true });
  expect(input.value).toBe("Ctrl+S");
  fireEvent.blur(input);

  expect(
    await screen.findByText("恢复快捷键注册失败：恢复失败"),
  ).toBeInTheDocument();
});

it("surfaces host start failure without capturing and still ends recording", async () => {
  const user = userEvent.setup();
  const onStart = vi.fn(() => Promise.reject(new Error("宿主未就绪")));
  const { input, onChange, onEnd } = recorder({ onStart });
  await user.click(input);
  await act(async () => {});

  expect(screen.getByText("宿主未就绪")).toBeInTheDocument();
  expect(onEnd).toHaveBeenCalledOnce();

  keyDown(input, "Control", { ctrlKey: true });
  expect(onChange).not.toHaveBeenCalled();
  expect(input.value).toBe("Ctrl+Alt+Q");
});

it("lets Tab leave the field instead of recording a combo", async () => {
  const user = userEvent.setup();
  const { input, onChange, onEnd } = recorder();
  await user.click(input);

  keyDown(input, "Control", { ctrlKey: true });
  keyDown(input, "s", { ctrlKey: true });
  const callsAfterCapture = onChange.mock.calls.length;

  // Tab 结束录入且不 preventDefault，浏览器默认焦点导航放行。
  const tabAllowed = keyDown(input, "Tab");
  expect(tabAllowed).toBe(true);
  expect(onEnd).toHaveBeenCalledOnce();
  expect(onChange).toHaveBeenCalledTimes(callsAfterCapture);
  expect(input.value).toBe("Ctrl+S");
});

it("bare Escape restores the original draft and cancels recording", async () => {
  const user = userEvent.setup();
  const { input, onChange, onCancel, onEnd } = recorder();
  await user.click(input);

  keyDown(input, "Control", { ctrlKey: true });
  keyDown(input, "Alt", { ctrlKey: true, altKey: true });
  keyDown(input, "s", { ctrlKey: true, altKey: true });
  keyUp(input, "Alt", { ctrlKey: true });
  keyUp(input, "Control", {});
  expect(input.value).toBe("Ctrl+Alt+S");

  keyDown(input, "Escape", {});
  expect(input.value).toBe("Ctrl+Alt+Q");
  expect(onChange).toHaveBeenLastCalledWith("Ctrl+Alt+Q");
  expect(onCancel).toHaveBeenCalledOnce();
  expect(onEnd).toHaveBeenCalledOnce();
});

it("blur ends recording without submitting and keeps the last combo", async () => {
  const user = userEvent.setup();
  const { input, onChange, onCancel, onEnd } = recorder();
  await user.click(input);

  keyDown(input, "Control", { ctrlKey: true });
  keyDown(input, "Alt", { ctrlKey: true, altKey: true });
  keyDown(input, "s", { ctrlKey: true, altKey: true });
  const callsAfterCapture = onChange.mock.calls.length;

  fireEvent.blur(input);
  expect(onEnd).toHaveBeenCalledOnce();
  // 失焦不提交：草稿保持最近完整组合，不再发新的 onChange/onCancel。
  expect(onChange).toHaveBeenCalledTimes(callsAfterCapture);
  expect(onCancel).not.toHaveBeenCalled();
  expect(input.value).toBe("Ctrl+Alt+S");
});

it("ignores IME composition events", async () => {
  const user = userEvent.setup();
  const { input, onChange } = recorder();
  await user.click(input);

  // keyCode 229 是 IME 组合中的典型标记；手工构造事件保证 keyCode 可靠。
  const imeKeyDown = new KeyboardEvent("keydown", {
    key: "Process",
    ctrlKey: true,
    bubbles: true,
  });
  Object.defineProperty(imeKeyDown, "keyCode", { value: 229 });
  fireEvent(input, imeKeyDown);
  fireEvent.keyDown(input, { key: "中", isComposing: true, ctrlKey: true });
  expect(onChange).not.toHaveBeenCalled();
  expect(input.value).toBe("Ctrl+Alt+Q");

  // IME 事件被忽略后正常组合仍可录入。
  keyDown(input, "s", { ctrlKey: true });
  expect(input.value).toBe("Ctrl+S");
});

it("ends recording on window blur, pagehide and unmount best effort", async () => {
  const user = userEvent.setup();
  const { view, onEnd } = recorder();
  const input = screen.getByLabelText("快捷截图识别新快捷键");

  await user.click(input);
  fireEvent(window, new Event("blur"));
  expect(onEnd).toHaveBeenCalledOnce();

  // 失焦结束后点击可重新进入录入（焦点可能仍在输入框，点击路径可重试）。
  await user.click(input);
  fireEvent(window, new Event("pagehide"));
  expect(onEnd).toHaveBeenCalledTimes(2);

  await user.click(input);
  view.unmount();
  expect(onEnd).toHaveBeenCalledTimes(3);
});

it("does not record when disabled", async () => {
  const user = userEvent.setup();
  const { input, onChange, onEnd } = recorder({ disabled: true });

  expect(input).toBeDisabled();
  await user.click(input);
  fireEvent.focus(input);
  expect(onEnd).not.toHaveBeenCalled();

  keyDown(input, "Control", { ctrlKey: true });
  expect(onChange).not.toHaveBeenCalled();
  expect(input.value).toBe("Ctrl+Alt+Q");
});

it("ends a late acknowledgement with its original id after another recorder starts", async () => {
  const user = userEvent.setup();
  const { onStart, resolve } = pendingStart();
  const oldEnd = vi.fn();
  const newStart = vi.fn().mockResolvedValue(undefined);
  const old = recorder({ onStart, onEnd: oldEnd });
  await user.click(old.input);
  old.view.unmount();
  const originalId = vi.mocked(onStart).mock.calls[0]![0];

  const next = recorder({ onStart: newStart });
  await user.click(next.input);
  expect(newStart.mock.calls[0]![0]).not.toBe(originalId);
  await act(async () => resolve());
  expect(oldEnd).toHaveBeenLastCalledWith(originalId);
  keyDown(next.input, "s", { ctrlKey: true });
  expect(next.input).toHaveValue("Ctrl+S");
  expect(next.onEnd).not.toHaveBeenCalled();
});

it("uses the layout key and supports Win modifiers and function keys", async () => {
  const { input } = recorder();
  await userEvent.setup().click(input);
  fireEvent.keyDown(input, { key: "z", code: "KeyY", ctrlKey: true });
  expect(input).toHaveValue("Ctrl+Z");
  keyDown(input, "Meta", { metaKey: true });
  expect(input).toHaveValue("Win");
  keyDown(input, "F24", { altKey: true, metaKey: true });
  expect(input).toHaveValue("Alt+Win+F24");
});
