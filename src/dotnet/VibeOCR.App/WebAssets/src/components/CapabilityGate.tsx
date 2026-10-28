import { Button, Tooltip } from "@fluentui/react-components";
import { useId, type ReactElement, type ReactNode } from "react";

import type { AppAction, AppActions } from "../app/types";

interface CapabilityGateProps {
  readonly capability: string;
  readonly capabilities: readonly string[];
  readonly action: AppAction;
  readonly actions: AppActions;
  readonly appearance?: "primary" | "secondary" | "subtle" | "transparent";
  readonly icon?: ReactElement;
  readonly children: ReactNode;
  readonly disabled?: boolean;
}

export function CapabilityGate({
  capability,
  capabilities,
  action,
  actions,
  children,
  disabled,
  ...buttonProps
}: CapabilityGateProps) {
  // 原因按需展示并保留键盘访问；同一能力的多个入口使用独立描述 id。
  const noteId = useId();
  const available = capabilities.includes(capability);
  const reason = `此功能需要宿主能力：${capability}`;
  const button = (
    <Button
      {...buttonProps}
      aria-describedby={available ? undefined : noteId}
      disabled={disabled || !available}
      disabledFocusable={!available}
      onClick={() => actions.run(action)}
    >
      {children}
    </Button>
  );

  if (available) return button;
  return (
    <>
      <Tooltip content={reason} relationship="description">
        {button}
      </Tooltip>
      <span className="capability-note" id={noteId}>
        {reason}
      </span>
    </>
  );
}
