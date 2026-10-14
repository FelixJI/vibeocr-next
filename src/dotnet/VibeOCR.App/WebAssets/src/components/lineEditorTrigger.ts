/** 可聚焦行触发器属性：Enter 开始编辑，双击同效；Escape 由编辑器处理。 */
export interface LineTriggerProps {
  tabIndex: number;
  onDoubleClick: (event: { preventDefault(): void }) => void;
  onKeyDown: (event: { key: string; preventDefault(): void }) => void;
}

export function startEditOnEnterOrDoubleActivate(
  onActivate: () => void,
): LineTriggerProps {
  return {
    tabIndex: 0,
    onDoubleClick: (event) => {
      event.preventDefault();
      onActivate();
    },
    onKeyDown: (event) => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      onActivate();
    },
  };
}
