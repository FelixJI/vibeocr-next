import { Button, Tooltip } from "@fluentui/react-components";
import type { ReactElement, ReactNode } from "react";

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
  const available = capabilities.includes(capability);
  const blocked = disabled || !available;
  const reason = `此功能需要宿主能力：${capability}`;
  const button = (
    <Button
      {...buttonProps}
      aria-describedby={blocked ? `${capability}-capability` : undefined}
      disabled={blocked}
      onClick={() => actions.run(action)}
    >
      {children}
    </Button>
  );

  if (available) return button;
  return (
    <span className="capability-gate">
      <Tooltip content="宿主能力不可用" relationship="description">
        {button}
      </Tooltip>
      <span className="capability-note" id={`${capability}-capability`}>
        {reason}
      </span>
    </span>
  );
}
