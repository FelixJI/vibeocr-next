/**
 * 文档级键盘命令的共享守卫：与 HotkeyRecorder 的 isImeEvent 同一约定，
 * 供各功能面把 Ctrl+O/S/C 等接入现有 actions.run 业务入口时统一消费
 * 顺序（IME/文本编辑目标先消费，隐藏窗口不收命令）。
 */

/** IME 组合中的 keydown（isComposing 或 Windows/Chromium 的 keyCode 229）：
 * 输入法候选与组合先消费该键，文档命令、撤销/删除与 Esc 退出层级都让路。 */
export function isImeCompositionEvent(nativeEvent: KeyboardEvent): boolean {
  return nativeEvent.isComposing || nativeEvent.keyCode === 229;
}

/** 文档命令快捷键的公共前置：事件已被其他处理消费、页面隐藏（最小化/
 * 后台 WebView 窗口）、页面未持有焦点（隐藏但 Chromium 仍报 visible 或
 * 多 WebView 场景防误路由）或 IME 组合中时不触发任何业务命令。 */
export function shouldRunDocumentCommand(event: KeyboardEvent): boolean {
  return (
    !event.defaultPrevented &&
    !document.hidden &&
    document.hasFocus() &&
    !isImeCompositionEvent(event)
  );
}

/** 目标本身是文本输入控件：其编辑键（含 Ctrl+C 复制自身选区）优先于
 * 文档级命令，交回浏览器默认行为。 */
export function isEditableTarget(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLElement &&
    (target.isContentEditable ||
      ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName))
  );
}

/** 当前存在可见文本选择（页面选择或输入控件内选区）：Ctrl+C 交给原生
 * 复制，不降级为文档复制命令。 */
export function hasTextSelection(): boolean {
  const active = document.activeElement;
  if (
    active instanceof HTMLInputElement ||
    active instanceof HTMLTextAreaElement
  ) {
    const start = active.selectionStart;
    const end = active.selectionEnd;
    if (start !== null && end !== null && start !== end) return true;
  }
  const selection = document.getSelection();
  return !!selection && !selection.isCollapsed && selection.toString() !== "";
}
