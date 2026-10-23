export interface RecognitionEngineState {
  readonly engine: string;
  readonly displayName: string;
  readonly selected: boolean;
  readonly isTaskOverride: boolean;
  readonly availability: string;
  readonly requiresDownload: boolean;
  readonly isDefault?: boolean;
  readonly taskModelPreset?: string | null;
  readonly defaultModelPreset?: string | null;
  readonly lifecycleKind?: string;
  readonly supportsPreload?: boolean;
  readonly supportsTtl?: boolean;
  readonly supportsPinning?: boolean;
  readonly supportsRelease?: boolean;
  readonly supportedOptions?: readonly string[];
  readonly options?: unknown;
  readonly reasonCode?: string | null;
}

export function recognitionEngines(
  value: unknown,
): readonly RecognitionEngineState[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is RecognitionEngineState => {
    if (entry === null || typeof entry !== "object" || Array.isArray(entry))
      return false;
    const option = entry as Partial<RecognitionEngineState>;
    return (
      typeof option.engine === "string" &&
      typeof option.displayName === "string" &&
      typeof option.selected === "boolean" &&
      typeof option.isTaskOverride === "boolean" &&
      typeof option.availability === "string" &&
      typeof option.requiresDownload === "boolean"
    );
  });
}
