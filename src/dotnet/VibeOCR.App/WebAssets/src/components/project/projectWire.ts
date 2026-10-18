import { parseComposition, type CanvasComposition } from "../canvasComposition";
import type {
  AnnotationStyle,
  CanvasSize,
  EditorState,
  Mark,
  Point,
} from "../annotationGeometry";

/**
 * #233 .vibp v1 线形状（与宿主 VibpProjectManifest/WorkbenchProjectOpenState
 * 同一冻结 schema；本模块只做映射/预检，不复制第二真源语义）。
 * 坐标空间：canvas 为保存时的逻辑画布（普通编辑页固定 900×600，scene 为
 * 选区尺寸）；marks/crop/points 按该画布原样保存，恢复时经 natural 中转
 * 映射到当前画布（同上下文为恒等，scene→普通保持内容相对位置）。
 * patch 矩形为原图（未旋转）整数像素空间。
 */

export interface ProjectResourceRef {
  readonly url: string;
  readonly mediaType: string;
  readonly byteLength: number;
}

export interface WireCanvas {
  readonly width: number;
  readonly height: number;
}

export interface WirePoint {
  readonly x: number;
  readonly y: number;
}

export interface WireFill {
  readonly color?: string;
  readonly opacity?: number;
  readonly enabled?: boolean;
}

/** 与 AnnotationStyle 字段一一对应（含连续 intensity 与 brushSize）。 */
export interface WireStyle {
  readonly color?: string;
  readonly strokeWidth?: number;
  readonly fontSize?: number;
  readonly intensity?: number;
  readonly brushSize?: number;
  readonly strokeOpacity?: number;
  readonly fill?: WireFill;
  readonly lineStyle?: string;
  readonly cornerRadius?: number;
  readonly arrowStyle?: string;
  readonly arrowDouble?: boolean;
  readonly fontFamily?: string;
  readonly fontBold?: boolean;
  readonly fontItalic?: boolean;
  readonly fontUnderline?: boolean;
  readonly textAlign?: string;
  readonly textBackground?: WireFill;
  readonly textOutline?: { readonly color: string; readonly width: number };
  readonly numberFormat?: string;
  readonly numberShape?: string;
  readonly calloutStyle?: string;
}

export interface WireMark {
  readonly id: string;
  readonly tool: string;
  readonly start: WirePoint;
  readonly end: WirePoint;
  readonly text?: string;
  readonly assetId?: string;
  readonly textWidth?: number;
  readonly style?: WireStyle;
  readonly points?: readonly WirePoint[];
  /** #235 聚光灯孔洞区域：仅 tool=spotlight 携带，与宿主 VibpRegion 同形。 */
  readonly regions?: readonly WireRegion[];
  /** #235 放大镜倍率：仅 tool=magnifier 携带，界 [1.5,8]。 */
  readonly zoom?: number;
  readonly ordinal?: number;
  readonly groupId?: string;
  readonly locked?: boolean;
  readonly rotation?: number;
  readonly flipX?: boolean;
  readonly flipY?: boolean;
  readonly objectScale?: number;
}

/** 聚光灯孔洞区域（显示空间矩形）：与宿主 VibpRegion 同形。 */
export interface WireRegion {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export interface WirePatch {
  readonly id: number;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export interface WireEditorState {
  readonly composition?: CanvasComposition;
  readonly rotation: number;
  readonly marks: readonly WireMark[];
  readonly crop?: { readonly start: WirePoint; readonly end: WirePoint };
  readonly resize?: { readonly width: number; readonly height: number };
  readonly patches: readonly WirePatch[];
}

export interface WireManifest {
  readonly schemaVersion: number;
  readonly canvas: WireCanvas;
  readonly source: {
    readonly mediaType: string;
    readonly width: number;
    readonly height: number;
    readonly length: number;
  };
  readonly assets: readonly {
    readonly id: string;
    readonly width: number;
    readonly height: number;
    readonly length: number;
  }[];
  readonly state: WireEditorState;
}

/** /__project 上传体（canvas+state+base64 补丁）。 */
export interface ProjectContributionBody {
  readonly canvas: WireCanvas;
  readonly state: WireEditorState;
  readonly patches: readonly {
    readonly id: number;
    readonly x: number;
    readonly y: number;
    readonly width: number;
    readonly height: number;
    readonly png: string;
  }[];
}

/** 宿主 imageEdit 状态帧的 projectOpen 投影（仅引用+绑定）。 */
export interface ProjectOpenWire {
  readonly pendingId: string;
  readonly sessionId: string;
  readonly revision: number;
  readonly generation: number;
  readonly manifest: ProjectResourceRef;
  readonly source: ProjectResourceRef;
  readonly assets: readonly {
    readonly id: string;
    readonly resource: ProjectResourceRef;
  }[];
  readonly patches: readonly {
    readonly id: number;
    readonly resource: ProjectResourceRef;
  }[];
}

export const PROJECT_STATE_BUDGET_BYTES = 256 * 1024;
export const PROJECT_UPLOAD_BUDGET_BYTES = 64 * 1024 * 1024;
/** 逻辑 canvas 总像素上限：与编辑器输出上限及宿主 VibpProjectLimits 一致；
 * 在任何真实画布分配前拒绝（parse/ack 前）。 */
export const PROJECT_CANVAS_TOTAL_PIXELS = 64_000_000;
/** 输出 resize 上限：与编辑器 UI 输出限额一致（单边 16384/总 64MP），
 * 区别于逻辑 canvas 的 32768 单边；恢复/导出前拒绝手造巨大输出。 */
export const PROJECT_RESIZE_MAX_SIDE = 16_384;
export const PROJECT_RESIZE_TOTAL_PIXELS = 64_000_000;
export const PROJECT_PATCH_MAX_PIXELS = 1_000_000;
export const PROJECT_PATCH_TOTAL_PIXELS = 8_000_000;
export const PROJECT_PATCH_MAX_COUNT = 126;
/** ZIP entry 总上限（含 manifest+source）：与宿主 VibpProjectLimits 一致。 */
export const PROJECT_ENTRY_TOTAL = 128;

/** 当前 marks 唯一引用的图片资产数：共享 asset 引用只计一次。 */
export function countReferencedAssets(
  marks: readonly { readonly assetId?: string }[],
): number {
  return new Set(
    marks
      .map((mark) => mark.assetId)
      .filter((assetId): assetId is string => assetId !== undefined),
  ).size;
}

/** 补丁 entry 预算：128 - manifest+source - 唯一资产；与宿主同界。 */
export function patchEntryBudget(referencedAssetCount: number): number {
  return PROJECT_ENTRY_TOTAL - 2 - referencedAssetCount;
}
export const PROJECT_PATCH_TOTAL_BYTES = 64 * 1024 * 1024;
export const PROJECT_OPEN_DEADLINE_MS = 30_000;
/** #235 新增对象边界：与宿主 VibpProjectRules 同界，不弱化既有预算。 */
export const SPOTLIGHT_MAX_REGIONS = 16;
export const WIRE_MAGNIFIER_ZOOM_MIN = 1.5;
export const WIRE_MAGNIFIER_ZOOM_MAX = 8;

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function finiteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function optionalString(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function optionalFinite(value: unknown): number | undefined {
  return finiteNumber(value) ? value : undefined;
}

function optionalBoolean(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

function parseResourceRef(value: unknown): ProjectResourceRef | undefined {
  if (
    !isRecord(value) ||
    typeof value.url !== "string" ||
    typeof value.mediaType !== "string" ||
    !finiteNumber(value.byteLength)
  ) {
    return undefined;
  }
  return {
    url: value.url,
    mediaType: value.mediaType,
    byteLength: value.byteLength,
  };
}

/** 状态帧 projectOpen 投影解析：字段缺失/形状不符返回 undefined。 */
export function parseProjectOpen(value: unknown): ProjectOpenWire | undefined {
  if (!isRecord(value)) return undefined;
  const manifest = parseResourceRef(value.manifest);
  const source = parseResourceRef(value.source);
  if (
    typeof value.pendingId !== "string" ||
    typeof value.sessionId !== "string" ||
    !finiteNumber(value.revision) ||
    !finiteNumber(value.generation) ||
    !manifest ||
    !source ||
    !Array.isArray(value.assets) ||
    !Array.isArray(value.patches)
  ) {
    return undefined;
  }
  const assets: ProjectOpenWire["assets"][number][] = [];
  for (const entry of value.assets) {
    if (!isRecord(entry)) return undefined;
    const resource = parseResourceRef(entry.resource);
    if (typeof entry.id !== "string" || !resource) return undefined;
    assets.push({ id: entry.id, resource });
  }
  const patches: ProjectOpenWire["patches"][number][] = [];
  for (const entry of value.patches) {
    if (!isRecord(entry)) return undefined;
    const resource = parseResourceRef(entry.resource);
    if (!finiteNumber(entry.id) || !resource) return undefined;
    patches.push({ id: entry.id, resource });
  }
  return {
    pendingId: value.pendingId,
    sessionId: value.sessionId,
    revision: value.revision,
    generation: value.generation,
    manifest,
    source,
    assets,
    patches,
  };
}

function parsePoint(value: unknown): Point | undefined {
  if (!isRecord(value) || !finiteNumber(value.x) || !finiteNumber(value.y)) {
    return undefined;
  }
  return { x: value.x, y: value.y };
}

function parseStyle(value: unknown): WireStyle | undefined {
  if (!isRecord(value)) return undefined;
  const parseFill = (fill: unknown): WireFill | undefined => {
    if (!isRecord(fill)) return undefined;
    return {
      color: optionalString(fill.color),
      opacity: optionalFinite(fill.opacity),
      enabled: optionalBoolean(fill.enabled),
    };
  };
  const outline = isRecord(value.textOutline)
    ? {
        color:
          typeof value.textOutline.color === "string"
            ? value.textOutline.color
            : "",
        width:
          typeof value.textOutline.width === "number" &&
          Number.isFinite(value.textOutline.width)
            ? value.textOutline.width
            : Number.NaN,
      }
    : undefined;
  return {
    color: optionalString(value.color),
    strokeWidth: optionalFinite(value.strokeWidth),
    fontSize: optionalFinite(value.fontSize),
    intensity: optionalFinite(value.intensity),
    brushSize: optionalFinite(value.brushSize),
    strokeOpacity: optionalFinite(value.strokeOpacity),
    fill: value.fill === undefined ? undefined : parseFill(value.fill),
    lineStyle: optionalString(value.lineStyle),
    cornerRadius: optionalFinite(value.cornerRadius),
    arrowStyle: optionalString(value.arrowStyle),
    arrowDouble: optionalBoolean(value.arrowDouble),
    fontFamily: optionalString(value.fontFamily),
    fontBold: optionalBoolean(value.fontBold),
    fontItalic: optionalBoolean(value.fontItalic),
    fontUnderline: optionalBoolean(value.fontUnderline),
    textAlign: optionalString(value.textAlign),
    textBackground:
      value.textBackground === undefined
        ? undefined
        : parseFill(value.textBackground),
    textOutline: outline,
    numberFormat: optionalString(value.numberFormat),
    numberShape: optionalString(value.numberShape),
    calloutStyle: optionalString(value.calloutStyle),
  };
}

function parseWireMark(value: unknown): WireMark | undefined {
  if (!isRecord(value)) return undefined;
  const start = parsePoint(value.start);
  const end = parsePoint(value.end);
  if (
    typeof value.id !== "string" ||
    typeof value.tool !== "string" ||
    !start ||
    !end
  ) {
    return undefined;
  }
  const points =
    Array.isArray(value.points) &&
    value.points.every((point) => parsePoint(point))
      ? (value.points as unknown[]).map((point) => parsePoint(point)!)
      : undefined;
  // 聚光灯区域：fail closed——非 spotlight 工具携带即拒绝；区域数与
  // 尺寸越界拒绝，不静默截断。
  let regions: readonly WireRegion[] | undefined;
  if (value.regions !== undefined) {
    if (value.tool !== "spotlight" || !Array.isArray(value.regions))
      return undefined;
    if (
      value.regions.length < 1 ||
      value.regions.length > SPOTLIGHT_MAX_REGIONS
    )
      return undefined;
    const parsed: WireRegion[] = [];
    for (const region of value.regions) {
      if (
        !isRecord(region) ||
        !finiteNumber(region.x) ||
        !finiteNumber(region.y) ||
        !finiteNumber(region.width) ||
        !finiteNumber(region.height) ||
        // 零尺寸区域加载后会压暗全图且无孔洞：严格 > 0（合法小正数可读）。
        region.width <= 0 ||
        region.height <= 0
      )
        return undefined;
      parsed.push({
        x: region.x,
        y: region.y,
        width: region.width,
        height: region.height,
      });
    }
    regions = parsed;
  }
  // 放大镜倍率：fail closed——非 magnifier 携带或越界即拒绝。
  let zoom: number | undefined;
  if (value.zoom !== undefined) {
    if (
      !finiteNumber(value.zoom) ||
      value.tool !== "magnifier" ||
      value.zoom < WIRE_MAGNIFIER_ZOOM_MIN ||
      value.zoom > WIRE_MAGNIFIER_ZOOM_MAX
    )
      return undefined;
    zoom = value.zoom;
  }
  return {
    id: value.id,
    tool: value.tool,
    start,
    end,
    text: optionalString(value.text),
    assetId: optionalString(value.assetId),
    textWidth: optionalFinite(value.textWidth),
    style: value.style === undefined ? undefined : parseStyle(value.style),
    points,
    regions,
    zoom,
    ordinal:
      typeof value.ordinal === "number" && Number.isInteger(value.ordinal)
        ? value.ordinal
        : undefined,
    groupId: optionalString(value.groupId),
    locked: optionalBoolean(value.locked),
    rotation: optionalFinite(value.rotation),
    flipX: optionalBoolean(value.flipX),
    flipY: optionalBoolean(value.flipY),
    objectScale: optionalFinite(value.objectScale),
  };
}

/** manifest 受控 GET 后的解析：schemaVersion≠1/结构不符/坐标非有限一律拒绝。 */
export function parseProjectManifest(value: unknown): WireManifest {
  if (!isRecord(value) || value.schemaVersion !== 1) {
    throw new Error("不支持的工程格式版本");
  }
  if (
    !isRecord(value.canvas) ||
    !finiteNumber(value.canvas.width) ||
    !finiteNumber(value.canvas.height) ||
    value.canvas.width * value.canvas.height > PROJECT_CANVAS_TOTAL_PIXELS ||
    !isRecord(value.source) ||
    typeof value.source.mediaType !== "string" ||
    !finiteNumber(value.source.width) ||
    !finiteNumber(value.source.height) ||
    !Array.isArray(value.assets) ||
    !isRecord(value.state) ||
    !finiteNumber(value.state.rotation) ||
    !Array.isArray(value.state.marks) ||
    !Array.isArray(value.state.patches)
  ) {
    throw new Error("工程数据结构不完整");
  }
  const marks: WireMark[] = [];
  for (const mark of value.state.marks) {
    const parsed = parseWireMark(mark);
    if (!parsed) throw new Error("工程对象数据不完整");
    marks.push(parsed);
  }
  const patches: WirePatch[] = [];
  const intField = (input: unknown): number | undefined =>
    typeof input === "number" && Number.isInteger(input) ? input : undefined;
  for (const patch of value.state.patches) {
    if (!isRecord(patch)) throw new Error("工程修补数据不完整");
    const id = intField(patch.id);
    const x = intField(patch.x);
    const y = intField(patch.y);
    const width = intField(patch.width);
    const height = intField(patch.height);
    if (
      id === undefined ||
      x === undefined ||
      y === undefined ||
      width === undefined ||
      height === undefined
    ) {
      throw new Error("工程修补数据不完整");
    }
    patches.push({ id, x, y, width, height });
  }
  const cropWire = isRecord(value.state.crop) ? value.state.crop : undefined;
  const cropStart = parsePoint(cropWire?.start);
  const cropEnd = parsePoint(cropWire?.end);
  if (cropWire && (!cropStart || !cropEnd)) {
    throw new Error("工程裁剪数据不完整");
  }
  const assets: WireManifest["assets"][number][] = [];
  for (const asset of value.assets) {
    if (
      !isRecord(asset) ||
      typeof asset.id !== "string" ||
      !finiteNumber(asset.width) ||
      !finiteNumber(asset.height)
    ) {
      throw new Error("工程图片资产数据不完整");
    }
    assets.push({
      id: asset.id,
      width: asset.width,
      height: asset.height,
      length: finiteNumber(asset.length) ? asset.length : 0,
    });
  }
  const resizeWire = isRecord(value.state.resize)
    ? value.state.resize
    : undefined;
  const resize =
    resizeWire &&
    finiteNumber(resizeWire.width) &&
    finiteNumber(resizeWire.height)
      ? { width: resizeWire.width, height: resizeWire.height }
      : undefined;
  if (resizeWire && !resize) {
    throw new Error("工程输出尺寸数据不完整");
  }
  if (
    resize &&
    (!Number.isInteger(resize.width) ||
      !Number.isInteger(resize.height) ||
      resize.width < 1 ||
      resize.height < 1 ||
      resize.width > PROJECT_RESIZE_MAX_SIDE ||
      resize.height > PROJECT_RESIZE_MAX_SIDE ||
      resize.width * resize.height > PROJECT_RESIZE_TOTAL_PIXELS)
  ) {
    throw new Error("工程输出尺寸超出上限（单边≤16384px，总像素≤64MP）");
  }
  const sourceLength = finiteNumber(value.source.length)
    ? value.source.length
    : 0;
  return {
    schemaVersion: 1,
    canvas: {
      width: value.canvas.width,
      height: value.canvas.height,
    },
    source: {
      mediaType: value.source.mediaType,
      width: value.source.width,
      height: value.source.height,
      length: sourceLength,
    },
    assets,
    state: {
      composition: parseComposition(value.state.composition),
      rotation: value.state.rotation,
      marks,
      crop:
        cropStart && cropEnd ? { start: cropStart, end: cropEnd } : undefined,
      resize,
      patches,
    },
  };
}

function styleToWire(style: AnnotationStyle): WireStyle {
  const fillToWire = (fill: AnnotationStyle["fill"]): WireFill | undefined =>
    fill
      ? {
          color: fill.color,
          opacity: fill.opacity,
          enabled: fill.enabled,
        }
      : undefined;
  return {
    color: style.color,
    strokeWidth: style.strokeWidth,
    fontSize: style.fontSize,
    intensity: style.intensity,
    brushSize: style.brushSize,
    strokeOpacity: style.strokeOpacity,
    fill: fillToWire(style.fill),
    lineStyle: style.lineStyle,
    cornerRadius: style.cornerRadius,
    arrowStyle: style.arrowStyle,
    arrowDouble: style.arrowDouble,
    fontFamily: style.fontFamily,
    fontBold: style.fontBold,
    fontItalic: style.fontItalic,
    fontUnderline: style.fontUnderline,
    textAlign: style.textAlign,
    textBackground: fillToWire(style.textBackground),
    textOutline: style.textOutline,
    numberFormat: style.numberFormat,
    numberShape: style.numberShape,
    calloutStyle: style.calloutStyle,
  };
}

function wireToStyle(style: WireStyle): AnnotationStyle {
  const wireToFill = (fill: WireFill | undefined): AnnotationStyle["fill"] =>
    fill ? { ...fill } : undefined;
  return {
    color: style.color,
    strokeWidth: style.strokeWidth,
    fontSize: style.fontSize,
    intensity: style.intensity,
    brushSize: style.brushSize,
    strokeOpacity: style.strokeOpacity,
    fill: wireToFill(style.fill),
    lineStyle: style.lineStyle as AnnotationStyle["lineStyle"],
    cornerRadius: style.cornerRadius,
    arrowStyle: style.arrowStyle as AnnotationStyle["arrowStyle"],
    arrowDouble: style.arrowDouble,
    fontFamily: style.fontFamily,
    fontBold: style.fontBold,
    fontItalic: style.fontItalic,
    fontUnderline: style.fontUnderline,
    textAlign: style.textAlign as AnnotationStyle["textAlign"],
    textBackground: wireToFill(style.textBackground),
    textOutline: style.textOutline,
    numberFormat: style.numberFormat as AnnotationStyle["numberFormat"],
    numberShape: style.numberShape as AnnotationStyle["numberShape"],
    calloutStyle: style.calloutStyle as AnnotationStyle["calloutStyle"],
  };
}

function markToWire(mark: Mark): WireMark {
  return {
    id: mark.id,
    tool: mark.tool,
    start: { ...mark.start },
    end: { ...mark.end },
    text: mark.text,
    assetId: mark.assetId,
    textWidth: mark.textWidth,
    style: mark.style ? styleToWire(mark.style) : undefined,
    points: mark.points
      ? mark.points.map((point) => ({ ...point }))
      : undefined,
    regions: mark.regions
      ? mark.regions.map((region) => ({ ...region }))
      : undefined,
    zoom: mark.zoom,
    ordinal: mark.ordinal,
    groupId: mark.groupId,
    locked: mark.locked,
    rotation: mark.rotation,
    flipX: mark.flipX,
    flipY: mark.flipY,
    objectScale: mark.objectScale,
  };
}

/**
 * 保存采集：逻辑 canvas + 当前 EditorState 原样序列化（补丁描述符来自
 * state.patches；PNG 字节由调用方另行编码）。不做坐标重投影。
 */
export function buildContributionState(
  canvas: CanvasSize,
  state: EditorState,
): ProjectContributionBody {
  return {
    canvas: { width: canvas.width, height: canvas.height },
    state: {
      composition: state.composition,
      rotation: state.rotation,
      marks: state.marks.map(markToWire),
      crop: state.crop
        ? {
            start: { ...state.crop.start },
            end: { ...state.crop.end },
          }
        : undefined,
      resize: state.resize ? { ...state.resize } : undefined,
      patches: [],
    },
    patches: [],
  };
}

export function wireToMark(
  mark: WireMark,
  map: (point: Point) => Point,
  scale: number,
): Mark {
  const scaled = (value: number | undefined): number | undefined =>
    value === undefined ? undefined : value * scale;
  return {
    id: mark.id,
    tool: mark.tool as Mark["tool"],
    start: map(mark.start),
    end: map(mark.end),
    text: mark.text,
    assetId: mark.assetId,
    textWidth: scaled(mark.textWidth),
    style: mark.style
      ? {
          ...wireToStyle(mark.style),
          strokeWidth: scaled(mark.style.strokeWidth),
          fontSize: scaled(mark.style.fontSize),
          brushSize: scaled(mark.style.brushSize),
          cornerRadius: scaled(mark.style.cornerRadius),
          textOutline: mark.style.textOutline
            ? {
                color: mark.style.textOutline.color,
                width: mark.style.textOutline.width * scale,
              }
            : undefined,
        }
      : undefined,
    points: mark.points ? mark.points.map(map) : undefined,
    // 区域是几何：与 points 同一映射；倍率是比例，不随缩放变化。
    regions: mark.regions
      ? mark.regions.map((region) => {
          const cornerA = map({ x: region.x, y: region.y });
          const cornerB = map({
            x: region.x + region.width,
            y: region.y + region.height,
          });
          return {
            x: Math.min(cornerA.x, cornerB.x),
            y: Math.min(cornerA.y, cornerB.y),
            width: Math.abs(cornerB.x - cornerA.x),
            height: Math.abs(cornerB.y - cornerA.y),
          };
        })
      : undefined,
    zoom: mark.zoom,
    ordinal: mark.ordinal,
    groupId: mark.groupId,
    locked: mark.locked,
    rotation: mark.rotation as Mark["rotation"],
    flipX: mark.flipX,
    flipY: mark.flipY,
    objectScale: mark.objectScale,
  };
}
/**
 * 恢复映射：合同“恢复使用保存的逻辑 frame，不做有损坐标重投影”——恒等
 * 逐值拷贝（marks/crop/resize/rotation/style 原样）；逻辑 frame 由编辑器
 * 采纳 saved canvas（仅 CSS 适配 viewport），source/canvas/frame 与
 * capture 身份分离。自然像素补丁矩形同空间直接传递。
 */
export function restoreEditorState(
  wire: WireEditorState,
  options: {
    readonly naturalSize: CanvasSize;
    readonly savedCanvas: CanvasSize;
    readonly displayCanvas: CanvasSize;
    readonly rotation: number;
  },
): EditorState {
  void options;
  return {
    composition: wire.composition,
    rotation: wire.rotation,
    marks: wire.marks.map((mark) => wireToMark(mark, (point) => point, 1)),
    crop: wire.crop
      ? { start: { ...wire.crop.start }, end: { ...wire.crop.end } }
      : undefined,
    resize: wire.resize ? { ...wire.resize } : undefined,
    patches: wire.patches.map((patch) => ({ ...patch })),
  };
}

/** TS 早期预检：state 序列化预算（与宿主 256KiB 同界）。 */
export function contributionStateBytes(body: ProjectContributionBody): number {
  return new TextEncoder().encode(JSON.stringify(body.state)).byteLength;
}

/** /__project 窄上传：返回 contributeId；非 2xx/形状不符抛错。 */
export async function uploadProjectContribution(
  body: ProjectContributionBody,
): Promise<string> {
  const response = await fetch("/__project", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(
      response.status === 403
        ? "工程贡献上传被拒绝：内容超限或格式不符"
        : "工程贡献上传失败",
    );
  }
  const payload: unknown = await response.json();
  if (
    !isRecord(payload) ||
    typeof payload.contributeId !== "string" ||
    !/^[0-9a-f]{32}$/.test(payload.contributeId)
  ) {
    throw new Error("工程贡献上传响应无效");
  }
  return payload.contributeId;
}

/** 同源受控 GET（/__resource/）：失败抛错，不静默降级；可携 AbortSignal。 */
export async function fetchProjectResource(
  url: string,
  signal?: AbortSignal,
): Promise<Response> {
  const response = await fetch(url, {
    cache: "no-store",
    credentials: "omit",
    signal,
  });
  if (!response.ok) throw new Error(`工程资源读取失败（${response.status}）`);
  return response;
}

/** 解码为强引用 HTMLImageElement（ack 后不再依赖 URL）；可携 AbortSignal。 */
export function decodeProjectImage(
  url: string,
  signal?: AbortSignal,
): Promise<HTMLImageElement> {
  return new Promise((resolve, fail) => {
    const element = new Image();
    element.decoding = "async";
    const onAbort = () => fail(new DOMException("aborted", "AbortError"));
    signal?.addEventListener("abort", onAbort, { once: true });
    element.onload = () => {
      signal?.removeEventListener("abort", onAbort);
      resolve(element);
    };
    element.onerror = () => {
      signal?.removeEventListener("abort", onAbort);
      fail(new Error("工程图片解码失败"));
    };
    element.src = url;
  });
}

/** 解码补丁 PNG 为等尺寸画布（保持 alpha；后续不重 fetch）。 */
export async function decodeProjectPatch(
  url: string,
  rect: WirePatch,
  signal?: AbortSignal,
): Promise<HTMLCanvasElement> {
  const response = await fetchProjectResource(url, signal);
  const blob = await response.blob();
  if (!blob.type.startsWith("image/png")) {
    throw new Error("工程补丁不是 PNG");
  }
  const objectUrl = URL.createObjectURL(blob);
  let image: HTMLImageElement;
  try {
    image = await new Promise<HTMLImageElement>((resolve, fail) => {
      const element = new Image();
      element.onload = () => resolve(element);
      element.onerror = () => fail(new Error("工程补丁解码失败"));
      element.src = objectUrl;
    });
  } catch (error) {
    URL.revokeObjectURL(objectUrl);
    throw error;
  }
  URL.revokeObjectURL(objectUrl);
  if (
    image.naturalWidth !== rect.width ||
    image.naturalHeight !== rect.height
  ) {
    throw new Error("工程补丁尺寸与声明不符");
  }
  const canvas = document.createElement("canvas");
  canvas.width = rect.width;
  canvas.height = rect.height;
  const context = canvas.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("工程补丁画布创建失败");
  context.drawImage(image, 0, 0);
  return canvas;
}
