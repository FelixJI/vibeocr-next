export interface CanvasComposition {
  readonly margin: number;
  readonly background: "transparent" | "solid" | "gradient" | "image";
  readonly color: string;
  readonly endColor: string;
  readonly backgroundAssetId?: string;
  readonly backgroundStitchIndex?: number;
  readonly shadow: number;
  readonly radius: number;
  readonly ratio: "free" | "1:1" | "4:3" | "16:9" | "9:16";
  readonly stitch: "none" | "horizontal" | "vertical";
  readonly assetIds: readonly string[];
  readonly cut?: {
    readonly axis: "horizontal" | "vertical";
    readonly start: number;
    readonly end: number;
  };
}

export function pruneCompositionAssets(
  settings: CanvasComposition,
  alive: ReadonlySet<string>,
): CanvasComposition {
  const removedBeforeBackground =
    settings.backgroundStitchIndex === undefined
      ? 0
      : settings.assetIds
          .slice(0, settings.backgroundStitchIndex)
          .filter((id) => !alive.has(id)).length;
  const composition = {
    ...settings,
    assetIds: settings.assetIds.filter((id) => alive.has(id)),
    backgroundStitchIndex:
      settings.backgroundStitchIndex === undefined
        ? undefined
        : settings.backgroundStitchIndex - removedBeforeBackground,
  };
  if (
    composition.backgroundAssetId &&
    !alive.has(composition.backgroundAssetId)
  ) {
    if (composition.background === "image")
      composition.background = "transparent";
    composition.backgroundAssetId = undefined;
    composition.backgroundStitchIndex = undefined;
  }
  return composition;
}

export const DEFAULT_COMPOSITION: CanvasComposition = {
  margin: 0,
  background: "transparent",
  color: "#ffffff",
  endColor: "#8eb9f5",
  shadow: 0,
  radius: 0,
  ratio: "free",
  stitch: "none",
  assetIds: [],
};

export function parseComposition(
  value: unknown,
): CanvasComposition | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "object" || Array.isArray(value))
    throw new Error("工程排版数据无效");
  const input = value as Record<string, unknown>;
  const fields = [
    "margin",
    "background",
    "color",
    "endColor",
    "backgroundAssetId",
    "backgroundStitchIndex",
    "shadow",
    "radius",
    "ratio",
    "stitch",
    "assetIds",
    "cut",
  ];
  if (
    Object.keys(input).some((key) => !fields.includes(key)) ||
    ![input.margin, input.shadow, input.radius].every(
      (n) => typeof n === "number" && Number.isFinite(n) && n >= 0 && n <= 1000,
    ) ||
    !["transparent", "solid", "gradient", "image"].includes(
      String(input.background),
    ) ||
    ![input.color, input.endColor].every(
      (color) => typeof color === "string" && /^#[a-f0-9]{6}$/i.test(color),
    ) ||
    !["free", "1:1", "4:3", "16:9", "9:16"].includes(String(input.ratio)) ||
    !["none", "horizontal", "vertical"].includes(String(input.stitch)) ||
    !Array.isArray(input.assetIds) ||
    input.assetIds.length > 16 ||
    input.assetIds.some(
      (id) => typeof id !== "string" || !/^[a-f0-9]{32}$/.test(id),
    ) ||
    new Set(input.assetIds).size !== input.assetIds.length ||
    (input.backgroundAssetId !== undefined &&
      (typeof input.backgroundAssetId !== "string" ||
        !/^[a-f0-9]{32}$/.test(input.backgroundAssetId)))
  )
    throw new Error("工程排版数据无效");
  if (
    input.backgroundStitchIndex !== undefined &&
    (!Number.isInteger(input.backgroundStitchIndex) ||
      (input.backgroundStitchIndex as number) < 0 ||
      (input.backgroundStitchIndex as number) > 16 ||
      input.background !== "image" ||
      !input.backgroundAssetId ||
      input.assetIds.includes(input.backgroundAssetId))
  )
    throw new Error("背景拼接成员记录无效");
  if (input.background === "image" && !input.backgroundAssetId)
    throw new Error("背景图未选择");
  if (input.cut !== undefined) {
    if (!input.cut || typeof input.cut !== "object" || Array.isArray(input.cut))
      throw new Error("中段裁除数据无效");
    const cut = input.cut as Record<string, unknown>;
    if (
      Object.keys(cut).some((key) => !["axis", "start", "end"].includes(key)) ||
      !["horizontal", "vertical"].includes(String(cut.axis)) ||
      typeof cut.start !== "number" ||
      typeof cut.end !== "number" ||
      !Number.isFinite(cut.start) ||
      !Number.isFinite(cut.end) ||
      cut.start < 0 ||
      cut.end > 100 ||
      cut.start >= cut.end ||
      cut.end - cut.start >= 100
    )
      throw new Error("中段裁除范围须在 0–100% 内且保留图像");
  }
  return value as CanvasComposition;
}

export function boundedCanvasSize(width: number, height: number) {
  width = Math.ceil(width);
  height = Math.ceil(height);
  if (
    !Number.isFinite(width) ||
    !Number.isFinite(height) ||
    width < 1 ||
    height < 1 ||
    width > 16384 ||
    height > 16384 ||
    width * height > 64_000_000
  )
    throw new Error("排版输出超出上限（单边≤16384px，总像素≤64MP）");
  return { width, height };
}

export function compositionSize(
  sources: readonly { width: number; height: number }[],
  settings: CanvasComposition,
) {
  const horizontal = settings.stitch === "horizontal";
  let width = horizontal
    ? sources.reduce((sum, item) => sum + item.width, 0)
    : Math.max(...sources.map((item) => item.width));
  let height = horizontal
    ? Math.max(...sources.map((item) => item.height))
    : sources.reduce((sum, item) => sum + item.height, 0);
  width += settings.margin * 2;
  height += settings.margin * 2;
  if (settings.ratio !== "free") {
    const [w, h] = settings.ratio.split(":").map(Number) as [number, number];
    if (width / height < w / h) width = (height * w) / h;
    else height = (width * h) / w;
  }
  return boundedCanvasSize(width, height);
}

function canvas(width: number, height: number) {
  const size = boundedCanvasSize(width, height);
  const result = document.createElement("canvas");
  result.width = size.width;
  result.height = size.height;
  return result;
}

export interface CompositionPlacement {
  readonly assetId?: string;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}
export interface CompositionLayout {
  readonly size: { width: number; height: number };
  readonly baseSize: { width: number; height: number };
  readonly cut?: { axis: "horizontal" | "vertical"; from: number; to: number };
  readonly placements: readonly CompositionPlacement[];
}

export function compositionLayout(
  source: { width: number; height: number },
  settings: CanvasComposition,
  assets?: ReadonlyMap<string, { naturalWidth: number; naturalHeight: number }>,
  imageMarks?: readonly { tool: string; assetId?: string }[],
): CompositionLayout {
  parseComposition(settings);
  const cut = settings.cut
    ? {
        axis: settings.cut.axis,
        from: Math.floor(
          ((settings.cut.axis === "vertical" ? source.width : source.height) *
            settings.cut.start) /
            100,
        ),
        to: Math.floor(
          ((settings.cut.axis === "vertical" ? source.width : source.height) *
            settings.cut.end) /
            100,
        ),
      }
    : undefined;
  if (
    cut &&
    (cut.to <= cut.from ||
      cut.to - cut.from >=
        (cut.axis === "vertical" ? source.width : source.height))
  )
    throw new Error("裁除范围小于一个像素或未保留图像");
  const baseSize = {
    width: source.width - (cut?.axis === "vertical" ? cut.to - cut.from : 0),
    height:
      source.height - (cut?.axis === "horizontal" ? cut.to - cut.from : 0),
  };
  const sources: { assetId?: string; width: number; height: number }[] = [
    baseSize,
  ];
  if (settings.stitch !== "none")
    for (const id of settings.assetIds) {
      const image = assets?.get(id);
      if (!image) throw new Error("拼接图片已失效，请重新导入");
      // 一个资源可被多个独立图片对象复用；每个存活对象保留一次拼接出现。
      const count =
        imageMarks?.filter(
          (mark) => mark.tool === "image" && mark.assetId === id,
        ).length ?? 1;
      for (let index = 0; index < count; index++)
        sources.push({
          assetId: id,
          width: image.naturalWidth,
          height: image.naturalHeight,
        });
    }
  const size = compositionSize(sources, settings);
  const horizontal = settings.stitch === "horizontal";
  const width = horizontal
    ? sources.reduce((sum, item) => sum + item.width, 0)
    : Math.max(...sources.map((item) => item.width));
  const height = horizontal
    ? Math.max(...sources.map((item) => item.height))
    : sources.reduce((sum, item) => sum + item.height, 0);
  let offset = 0;
  const placements = sources.map((item) => {
    const result = {
      ...item,
      x:
        (size.width - width) / 2 +
        (horizontal ? offset : (width - item.width) / 2),
      y:
        (size.height - height) / 2 +
        (horizontal ? (height - item.height) / 2 : offset),
    };
    offset += horizontal ? item.width : item.height;
    return result;
  });
  return { size, baseSize, cut, placements };
}

export function renderComposition(
  source: HTMLCanvasElement,
  settings: CanvasComposition | undefined,
  assets?: ReadonlyMap<string, HTMLImageElement>,
  imageMarks?: readonly { tool: string; assetId?: string }[],
): HTMLCanvasElement {
  if (!settings) return source;
  const layout = compositionLayout(source, settings, assets, imageMarks);
  let base = source;
  if (layout.cut) {
    const { axis, from, to } = layout.cut;
    const vertical = axis === "vertical",
      length = vertical ? source.width : source.height;
    base = canvas(layout.baseSize.width, layout.baseSize.height);
    const ctx = base.getContext("2d");
    if (!ctx) throw new Error("排版画布不可用");
    if (vertical) {
      if (from > 0)
        ctx.drawImage(
          source,
          0,
          0,
          from,
          source.height,
          0,
          0,
          from,
          source.height,
        );
      if (to < length)
        ctx.drawImage(
          source,
          to,
          0,
          length - to,
          source.height,
          from,
          0,
          length - to,
          source.height,
        );
    } else {
      if (from > 0)
        ctx.drawImage(
          source,
          0,
          0,
          source.width,
          from,
          0,
          0,
          source.width,
          from,
        );
      if (to < length)
        ctx.drawImage(
          source,
          0,
          to,
          source.width,
          length - to,
          0,
          from,
          source.width,
          length - to,
        );
    }
  }
  const output = canvas(layout.size.width, layout.size.height),
    ctx = output.getContext("2d");
  if (!ctx) throw new Error("排版画布不可用");
  ctx.beginPath();
  ctx.roundRect(0, 0, output.width, output.height, settings.radius);
  ctx.clip();
  if (settings.background === "solid" || settings.background === "gradient") {
    const gradient = ctx.createLinearGradient(
      0,
      0,
      output.width,
      output.height,
    );
    gradient.addColorStop(0, settings.color);
    gradient.addColorStop(1, settings.endColor);
    ctx.fillStyle = settings.background === "solid" ? settings.color : gradient;
    ctx.fillRect(0, 0, output.width, output.height);
  } else if (settings.background === "image") {
    const image = assets?.get(settings.backgroundAssetId!);
    if (!image) throw new Error("背景图已失效，请重新导入");
    const scale = Math.max(
      output.width / image.naturalWidth,
      output.height / image.naturalHeight,
    );
    ctx.drawImage(
      image,
      (output.width - image.naturalWidth * scale) / 2,
      (output.height - image.naturalHeight * scale) / 2,
      image.naturalWidth * scale,
      image.naturalHeight * scale,
    );
  }
  for (const item of layout.placements) {
    const image = item.assetId ? assets?.get(item.assetId) : base;
    if (!image) throw new Error("拼接图片已失效，请重新导入");
    ctx.save();
    ctx.shadowColor = "#00000080";
    ctx.shadowBlur = settings.shadow;
    ctx.shadowOffsetY = settings.shadow / 3;
    ctx.drawImage(image, item.x, item.y);
    ctx.restore();
  }
  return output;
}
