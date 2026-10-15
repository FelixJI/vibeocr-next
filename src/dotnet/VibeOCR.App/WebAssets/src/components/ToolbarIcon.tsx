import {
  toolbarIconData,
  toolbarIconEffects,
  toolbarIconSize,
  toolbarIconStrokeWidth,
  type ToolbarIconName,
} from "./toolbarIconData";

export function ToolbarIcon({
  name,
  size = toolbarIconSize,
}: {
  readonly name: ToolbarIconName;
  readonly size?: number;
}) {
  const icon = toolbarIconData[name];
  return (
    <svg
      aria-hidden="true"
      focusable="false"
      width={size}
      height={size}
      viewBox={`0 0 ${toolbarIconSize} ${toolbarIconSize}`}
      fill="none"
      stroke="currentColor"
      strokeWidth={toolbarIconStrokeWidth}
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {toolbarIconEffects[name]?.map((layer, index) => (
        <path
          key={index}
          d={layer.path}
          fill={layer.stroke ? "none" : "currentColor"}
          stroke={layer.stroke ? "currentColor" : "none"}
          strokeDasharray={layer.dashed ? "0.5 2.5" : undefined}
          opacity={layer.opacity}
        />
      )) ?? <path d={icon.path} />}
    </svg>
  );
}
