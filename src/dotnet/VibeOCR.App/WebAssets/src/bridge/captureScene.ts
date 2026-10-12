/** Host-owned physical geometry and the exact bootstrap input to present. */
export interface CaptureScene {
  readonly surfaceId: string;
  readonly captureSessionId: string;
  readonly inputUrl: string;
  readonly inputRevision: number;
  readonly documentGeneration: number;
  readonly bootstrapGeneration: number;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly desktopWidth: number;
  readonly desktopHeight: number;
  readonly initialTool: string;
}

export type CapturePixelFailure =
  "sourceDecode" | "backgroundDecode" | "draw" | "identity";

export function isCaptureScene(value: unknown): value is CaptureScene {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const scene = value as Record<string, unknown>;
  const fields = [
    "surfaceId",
    "captureSessionId",
    "inputUrl",
    "inputRevision",
    "documentGeneration",
    "bootstrapGeneration",
    "x",
    "y",
    "width",
    "height",
    "desktopWidth",
    "desktopHeight",
    "initialTool",
  ];
  if (
    Object.keys(scene).length !== fields.length ||
    fields.some((field) => !(field in scene))
  )
    return false;
  if (
    !["surfaceId", "captureSessionId", "inputUrl", "initialTool"].every(
      (field) =>
        typeof scene[field] === "string" &&
        scene[field].length > 0 &&
        scene[field].length <= 2048,
    )
  )
    return false;
  if (
    ![
      "inputRevision",
      "documentGeneration",
      "bootstrapGeneration",
      "x",
      "y",
      "width",
      "height",
      "desktopWidth",
      "desktopHeight",
    ].every(
      (field) =>
        Number.isSafeInteger(scene[field]) && (scene[field] as number) >= 0,
    )
  )
    return false;
  const typed = value as CaptureScene;
  return (
    typed.width > 0 &&
    typed.height > 0 &&
    typed.desktopWidth > 0 &&
    typed.desktopHeight > 0 &&
    typed.x + typed.width <= typed.desktopWidth &&
    typed.y + typed.height <= typed.desktopHeight
  );
}
