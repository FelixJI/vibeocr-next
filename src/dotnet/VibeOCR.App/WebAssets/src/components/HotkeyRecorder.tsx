import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
} from "react";

/**
 * 快捷键录入控件：只做 WebView 内聚焦元素上的 keydown/keyup 局部捕获与
 * 本地草稿展示；不验证组合、不保存配置，合法性由宿主注册器在保存事务中
 * 裁决，仅修饰键草稿可否保存由宿主按钮判断。
 *
 * 挂起协议（纯函数 props，不依赖 bridge）：聚焦后等待 onStart 端到端确认
 * （宿主已释放本应用全局注册）才进入捕获，避免旧 OS 注册吞键/误动作；
 * 结束与迟到 ack 的补偿都经 onEnd（宿主侧幂等）。修饰状态取自事件真实
 * ctrlKey/altKey/shiftKey/metaKey，聚焦前或等待期按下的修饰键同样有效；
 * 完整组合在修饰键释放后保持，下一次输入才替换；Tab 不作候选组合——
 * 结束录入并放行浏览器焦点导航。全局 window/document 监听只在录入期间
 * 挂载。
 */

const MODIFIER_LABELS: Readonly<Record<string, string>> = {
  Control: "Ctrl",
  Alt: "Alt",
  Shift: "Shift",
  Meta: "Win",
};

const MODIFIER_ORDER: readonly string[] = ["Ctrl", "Alt", "Shift", "Win"];

export interface HotkeyRecorderProps {
  /** 可访问标签，如「快捷截图识别新快捷键」。 */
  readonly label: ReactNode;
  /** 宿主当前已配置组合；仅用于展示与取消恢复，组件不回写。 */
  readonly value: string;
  /** 本地草稿变化（含仅修饰键的临时草稿与恢复）；不持久化。 */
  readonly onChange: (hotkey: string) => void;
  /** 裸 Esc 取消草稿后回调；组件已先把草稿恢复为录入前内容。 */
  readonly onCancel?: () => void;
  readonly disabled?: boolean;
  /**
   * 聚焦后调用；resolve 即宿主端到端确认已释放本应用全局注册，此后才
   * 接完整组合；reject 或同步 throw 时显示错误且不接键。缺省时立即捕获。
   */
  readonly onStart?: () => Promise<void>;
  /** 录入结束（失焦/Esc/Tab/卸载/页面隐藏/迟到 ack 补偿）时尽力调用；宿主侧必须幂等，拒绝会在状态区呈现。 */
  readonly onEnd?: () => void | Promise<void>;
}

type RecorderPhase = "idle" | "starting" | "capturing";

function isImeEvent(event: ReactKeyboardEvent<HTMLInputElement>): boolean {
  return event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
}

function modifiersFromEvent(
  event: ReactKeyboardEvent<HTMLInputElement>,
): Set<string> {
  const held = new Set<string>();
  if (event.ctrlKey) {
    held.add("Ctrl");
  }
  if (event.altKey) {
    held.add("Alt");
  }
  if (event.shiftKey) {
    held.add("Shift");
  }
  if (event.metaKey) {
    held.add("Win");
  }
  return held;
}

function formatMainKey(key: string): string {
  if (key.length === 1) {
    return key.toUpperCase();
  }
  return /^f\d{1,2}$/i.test(key) ? key.toUpperCase() : key;
}

function joinModifiers(held: ReadonlySet<string>): string {
  return MODIFIER_ORDER.filter((label) => held.has(label)).join("+");
}

export function HotkeyRecorder({
  label,
  value,
  onChange,
  onCancel,
  disabled = false,
  onStart,
  onEnd,
}: HotkeyRecorderProps) {
  const [phase, setPhase] = useState<RecorderPhase>("idle");
  const [draft, setDraft] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const inputId = useId();
  const statusId = `${inputId}-status`;

  const phaseRef = useRef<RecorderPhase>("idle");
  const draftRef = useRef<string | null>(null);
  const originalRef = useRef("");
  const lastComboRef = useRef<string | null>(null);
  const sessionRef = useRef(0);
  const valueRef = useRef(value);
  const disabledRef = useRef(disabled);
  const callbacksRef = useRef({ onChange, onCancel, onStart, onEnd });

  useEffect(() => {
    callbacksRef.current = { onChange, onCancel, onStart, onEnd };
  });

  const applyPhase = useCallback((next: RecorderPhase) => {
    phaseRef.current = next;
    setPhase(next);
  }, []);

  const applyDraft = useCallback((next: string | null) => {
    if (draftRef.current === next) {
      return;
    }
    draftRef.current = next;
    setDraft(next);
    if (next !== null) {
      callbacksRef.current.onChange(next);
    }
  }, []);

  const notifyEnd = useCallback(() => {
    const end = callbacksRef.current.onEnd;
    if (!end) {
      return;
    }
    const reportFailure = (error: unknown) => {
      // onEnd 失败必须在状态区可见，不能吞掉造成假恢复。
      setErrorMessage(
        error instanceof Error
          ? `恢复快捷键注册失败：${error.message}`
          : "恢复快捷键注册失败，请重试。",
      );
    };
    try {
      void Promise.resolve(end()).catch(reportFailure);
    } catch (error) {
      reportFailure(error);
    }
  }, []);

  const endRecording = useCallback(() => {
    if (phaseRef.current === "idle") {
      return;
    }
    sessionRef.current += 1;
    applyPhase("idle");
    // 丢弃仅修饰键的临时草稿；本会话未发布过草稿时不发多余的 onChange。
    if (draftRef.current !== null) {
      applyDraft(lastComboRef.current ?? originalRef.current);
    }
    notifyEnd();
  }, [applyDraft, applyPhase, notifyEnd]);

  const startRecording = useCallback(() => {
    if (disabledRef.current || phaseRef.current !== "idle") {
      return;
    }
    const session = (sessionRef.current += 1);
    originalRef.current = draftRef.current ?? valueRef.current;
    lastComboRef.current = null;
    setErrorMessage(null);
    applyPhase("starting");
    const failStart = (error: unknown) => {
      if (sessionRef.current !== session) {
        return;
      }
      setErrorMessage(
        error instanceof Error
          ? error.message
          : "宿主未能开始快捷键录入，请重试。",
      );
      endRecording();
    };
    const start = callbacksRef.current.onStart;
    if (!start) {
      applyPhase("capturing");
      return;
    }
    // 同步 throw 与拒绝走同一失败路径，避免崩溃 React 事件处理。
    let ack: Promise<void>;
    try {
      ack = start();
    } catch (error) {
      failStart(error);
      return;
    }
    ack
      .then(() => {
        if (sessionRef.current === session && phaseRef.current === "starting") {
          applyPhase("capturing");
          return;
        }
        if (sessionRef.current !== session && phaseRef.current === "idle") {
          // 迟到的 ack：宿主可能在本会话结束之后才真正开始挂起，
          // 再次 onEnd 补偿；更新的会话由其自身生命周期负责结束。
          notifyEnd();
        }
      })
      .catch(failStart);
  }, [applyPhase, endRecording, notifyEnd]);

  // 宿主配置变化时跟随新值；录入中保留本地草稿。
  useEffect(() => {
    if (valueRef.current === value) {
      return;
    }
    valueRef.current = value;
    if (phaseRef.current === "idle") {
      applyDraft(null);
    }
  }, [value, applyDraft]);

  // 录入中被禁用：立即结束，不提交。
  useEffect(() => {
    if (disabled && phaseRef.current !== "idle") {
      endRecording();
    }
  }, [disabled, endRecording]);

  useEffect(() => {
    disabledRef.current = disabled;
  }, [disabled]);

  // DOM 级监听只在录入期间挂载。
  const recording = phase !== "idle";
  useEffect(() => {
    if (!recording) {
      return;
    }
    const onWindowBlur = () => endRecording();
    const onPageHide = () => endRecording();
    const onVisibilityChange = () => {
      if (document.visibilityState === "hidden") {
        endRecording();
      }
    };
    window.addEventListener("blur", onWindowBlur);
    window.addEventListener("pagehide", onPageHide);
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      window.removeEventListener("blur", onWindowBlur);
      window.removeEventListener("pagehide", onPageHide);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [recording, endRecording]);

  // 卸载时尽力结束录入：不提交，仅通知宿主恢复注册。
  useEffect(() => {
    return () => {
      if (phaseRef.current !== "idle") {
        endRecording();
      }
    };
  }, [endRecording]);

  const handleFocus = () => startRecording();
  const handleClick = () => startRecording();
  const handleBlur = () => endRecording();

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLInputElement>) => {
    if (phaseRef.current !== "capturing") {
      // 等待确认期间只有裸 Esc（真实修饰标志为零）才放弃等待。
      if (
        phaseRef.current === "starting" &&
        event.key === "Escape" &&
        modifiersFromEvent(event).size === 0
      ) {
        event.preventDefault();
        endRecording();
      }
      return;
    }
    if (event.key === "Tab") {
      // Tab 是焦点导航而非候选组合：结束录入并放行浏览器默认行为。
      endRecording();
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (isImeEvent(event) || event.repeat) {
      return;
    }
    const held = modifiersFromEvent(event);
    const modifier = MODIFIER_LABELS[event.key];
    if (modifier !== undefined) {
      applyDraft(joinModifiers(held));
      return;
    }
    if (event.key === "Escape" && held.size === 0) {
      lastComboRef.current = null;
      endRecording();
      callbacksRef.current.onCancel?.();
      return;
    }
    const modifiers = joinModifiers(held);
    const mainKey = formatMainKey(event.key);
    const combo = modifiers ? `${modifiers}+${mainKey}` : mainKey;
    lastComboRef.current = combo;
    applyDraft(combo);
  };

  const handleKeyUp = (event: ReactKeyboardEvent<HTMLInputElement>) => {
    if (phaseRef.current !== "capturing") {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (isImeEvent(event)) {
      return;
    }
    const modifier = MODIFIER_LABELS[event.key];
    if (modifier === undefined) {
      return;
    }
    if (lastComboRef.current !== null) {
      // 完整组合形成后修饰键释放不丢最近完整组合，下一次输入才替换。
      applyDraft(lastComboRef.current);
      return;
    }
    const held = modifiersFromEvent(event);
    if (held.size > 0) {
      applyDraft(joinModifiers(held));
    } else {
      applyDraft(originalRef.current);
    }
  };

  const display = draft ?? value;
  const statusText =
    errorMessage ??
    (phase === "starting"
      ? "正在等待宿主释放旧快捷键…"
      : phase === "capturing"
        ? "请按下新的组合键，按 Esc 取消"
        : "");

  return (
    <div className="hotkey-recorder">
      <label htmlFor={inputId}>{label}</label>
      <input
        id={inputId}
        type="text"
        readOnly
        value={display}
        placeholder="点击后按下新组合"
        disabled={disabled}
        onFocus={handleFocus}
        onClick={handleClick}
        onBlur={handleBlur}
        onKeyDown={handleKeyDown}
        onKeyUp={handleKeyUp}
        aria-describedby={statusId}
        aria-invalid={errorMessage === null ? undefined : true}
      />
      <span id={statusId} role="status" aria-live="polite">
        {statusText}
      </span>
    </div>
  );
}
