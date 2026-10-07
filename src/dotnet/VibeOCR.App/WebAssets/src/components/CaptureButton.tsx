import { Button, Tooltip } from "@fluentui/react-components";
import { Camera } from "lucide-react";
import type { AppActions } from "../app/types";

/// 右上角统一截图入口：单击直接开始普通截图；长截图、取字等
/// 后续动作在框选后的动作栏中选择，不再弹出菜单。
export function CaptureButton({
  capabilities,
  actions,
}: {
  readonly capabilities: readonly string[];
  readonly actions: AppActions;
}) {
  return (
    <Tooltip
      content="开始截图；框选后可用长截图、取字、识别等动作"
      relationship="label"
    >
      <Button
        appearance="primary"
        aria-label="截图"
        disabled={!capabilities.includes("recognition.capture")}
        icon={<Camera aria-hidden="true" size={16} />}
        onClick={() => actions.run({ type: "recognition.captureScreen" })}
        title="开始截图；框选后可用长截图、取字、识别等动作"
      >
        截图
      </Button>
    </Tooltip>
  );
}
