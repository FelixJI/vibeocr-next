export type AnnotationTool =
  | "rectangle"
  | "ellipse"
  | "arrow"
  | "text"
  | "mosaic"
  | "blur"
  | "pen"
  | "highlighter"
  | "numbering"
  /** 识别排除区：只影响识别输入与显式屏蔽副本，不写入普通导出。 */
  | "exclude";

export interface Point {
  readonly x: number;
  readonly y: number;
}

/** 逐标记样式；缺省回退到编辑器默认，历史条目保留当时样式。 */
export interface AnnotationStyle {
  readonly color?: string;
  readonly strokeWidth?: number;
  readonly fontSize?: number;
  /** 马赛克/模糊强度档位：1=弱、2=中（缺省）、3=强；决定块尺寸/滤波半径。 */
  readonly intensity?: number;
}

export interface Mark {
  readonly tool: AnnotationTool;
  readonly start: Point;
  readonly end: Point;
  readonly text?: string;
  readonly style?: AnnotationStyle;
  /** 画笔/荧光笔的采样点（含 start/end）；其他工具为空。 */
  readonly points?: readonly Point[];
  /** 序号标记的自动编号（绘制时重建）。 */
  readonly ordinal?: number;
}

/** 已应用的本地像素修补：原图（未旋转）像素空间的整数矩形。
 * 像素数据保存在编辑器侧补丁存储（按 id 索引），不进入历史序列化；
 * 矩形属于图像本体空间，旋转/裁剪/缩放等视图或输出变换不改变它。 */
export interface PixelPatch {
  readonly id: number;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export interface EditorState {
  readonly rotation: number;
  readonly marks: readonly Mark[];
  readonly crop?: { readonly start: Point; readonly end: Point };
  /** 最终输出像素尺寸；缺省为旋转/裁剪后的原始尺寸，随历史可撤销。 */
  readonly resize?: CanvasSize;
  /** 已应用（明确提交）的像素修补；每次应用恰好追加一项，随历史撤销/重做。 */
  readonly patches?: readonly PixelPatch[];
}

export interface CanvasSize {
  readonly width: number;
  readonly height: number;
}

/** 左上角 + 宽高的轴对齐矩形；屏蔽几何的公共交换形状。 */
export interface Rect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export function isExclusionMark(mark: Mark): boolean {
  return mark.tool === "exclude";
}

/** 任意角度旋转后内容的轴对齐包围盒；90° 倍数退化为精确宽高交换。
 * 向上取整保证旋转后的内容不会被包围盒裁掉；导出/显示同一规则。 */
export function rotatedBoundingBox(
  width: number,
  height: number,
  rotation: number,
): CanvasSize {
  const degrees = ((rotation % 360) + 360) % 360;
  // 90° 倍数走精确交换，避免三角函数 epsilon 导致尺寸 ±1。
  if (degrees % 90 === 0) {
    const quarterTurn = degrees % 180 !== 0;
    return {
      width: quarterTurn ? height : width,
      height: quarterTurn ? width : height,
    };
  }
  const angle = degrees * (Math.PI / 180);
  const cos = Math.abs(Math.cos(angle));
  const sin = Math.abs(Math.sin(angle));
  return {
    width: Math.ceil(width * cos + height * sin),
    height: Math.ceil(width * sin + height * cos),
  };
}

export function imageTransform(
  image: HTMLImageElement,
  rotation: number,
  size: CanvasSize,
) {
  // 适配任意角度：以旋转后的轴对齐包围盒为准，非 90° 倍数不再退化为未旋转尺寸。
  const bounds = rotatedBoundingBox(
    image.naturalWidth,
    image.naturalHeight,
    rotation,
  );
  return {
    centerX: size.width / 2,
    centerY: size.height / 2,
    scale: Math.min(size.width / bounds.width, size.height / bounds.height),
  };
}

export function projectPoint(
  point: Point,
  image: HTMLImageElement,
  fromRotation: number,
  fromSize: CanvasSize,
  toRotation: number,
  toSize: CanvasSize,
): Point {
  const from = imageTransform(image, fromRotation, fromSize);
  const fromAngle = (fromRotation * Math.PI) / 180;
  const screenX = (point.x - from.centerX) / from.scale;
  const screenY = (point.y - from.centerY) / from.scale;
  const imageX = screenX * Math.cos(fromAngle) + screenY * Math.sin(fromAngle);
  const imageY = -screenX * Math.sin(fromAngle) + screenY * Math.cos(fromAngle);

  const to = imageTransform(image, toRotation, toSize);
  const toAngle = (toRotation * Math.PI) / 180;
  return {
    x:
      to.centerX +
      to.scale * (imageX * Math.cos(toAngle) - imageY * Math.sin(toAngle)),
    y:
      to.centerY +
      to.scale * (imageX * Math.sin(toAngle) + imageY * Math.cos(toAngle)),
  };
}

export function rotateEditorState(
  state: EditorState,
  image: HTMLImageElement,
  size: CanvasSize,
  nextRotation: number,
): EditorState {
  const rotatePoint = (point: Point) =>
    projectPoint(point, image, state.rotation, size, nextRotation, size);
  return {
    rotation: nextRotation,
    marks: state.marks.map((mark) => ({
      ...mark,
      start: rotatePoint(mark.start),
      end: rotatePoint(mark.end),
      // 画笔/荧光笔轨迹点同映射，旋转后实际轨迹不错位。
      ...(mark.points ? { points: mark.points.map(rotatePoint) } : {}),
    })),
    ...(state.crop
      ? {
          crop: {
            start: rotatePoint(state.crop.start),
            end: rotatePoint(state.crop.end),
          },
        }
      : {}),
    // 指定输出尺寸是绝对目标：旋转内容不改变用户设置的输出宽高。
    ...(state.resize ? { resize: state.resize } : {}),
    // 像素修补固定在原图（未旋转）空间：旋转视图不需要重映射补丁坐标。
    ...(state.patches ? { patches: state.patches } : {}),
  };
}

export function outputSize(
  image: HTMLImageElement,
  rotation: number,
): CanvasSize {
  return rotatedBoundingBox(image.naturalWidth, image.naturalHeight, rotation);
}

/** 导出与尺寸信息共用的最终输出尺寸：旋转 → 裁剪 → 指定尺寸。 */
export function finalOutputSize(
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): CanvasSize {
  const naturalSize = outputSize(image, state.rotation);
  let width = naturalSize.width;
  let height = naturalSize.height;
  if (state.crop) {
    const mapPoint = (point: Point) =>
      projectPoint(
        point,
        image,
        state.rotation,
        displaySize,
        state.rotation,
        naturalSize,
      );
    const start = mapPoint(state.crop.start);
    const end = mapPoint(state.crop.end);
    const left = Math.max(
      0,
      Math.min(naturalSize.width, Math.min(start.x, end.x)),
    );
    const top = Math.max(
      0,
      Math.min(naturalSize.height, Math.min(start.y, end.y)),
    );
    const right = Math.max(
      left,
      Math.min(naturalSize.width, Math.max(start.x, end.x)),
    );
    const bottom = Math.max(
      top,
      Math.min(naturalSize.height, Math.max(start.y, end.y)),
    );
    width = right - left;
    height = bottom - top;
  }
  if (state.resize) {
    width = state.resize.width;
    height = state.resize.height;
  }
  return {
    width: Math.max(1, Math.round(width)),
    height: Math.max(1, Math.round(height)),
  };
}

/** 显示空间裁剪映射到自然输出空间并裁剪到画布内；与 finalOutputSize 同源。 */
export function mappedCropRect(
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): Rect {
  const naturalSize = outputSize(image, state.rotation);
  if (!state.crop) {
    return { x: 0, y: 0, width: naturalSize.width, height: naturalSize.height };
  }
  const mapPoint = (point: Point) =>
    projectPoint(
      point,
      image,
      state.rotation,
      displaySize,
      state.rotation,
      naturalSize,
    );
  const start = mapPoint(state.crop.start);
  const end = mapPoint(state.crop.end);
  const left = Math.max(
    0,
    Math.min(naturalSize.width, Math.min(start.x, end.x)),
  );
  const top = Math.max(
    0,
    Math.min(naturalSize.height, Math.min(start.y, end.y)),
  );
  const right = Math.max(
    left,
    Math.min(naturalSize.width, Math.max(start.x, end.x)),
  );
  const bottom = Math.max(
    top,
    Math.min(naturalSize.height, Math.max(start.y, end.y)),
  );
  return { x: left, y: top, width: right - left, height: bottom - top };
}

/** 任意浮点矩形外扩取整（floor/ceil）：白色填充不得在边缘残留原始文字像素。
 * 确定性代价是每个矩形最多向外扩张 1px。 */
function snapRectOutward(rect: Rect): Rect {
  const x = Math.floor(rect.x);
  const y = Math.floor(rect.y);
  return {
    x,
    y,
    width: Math.ceil(rect.x + rect.width) - x,
    height: Math.ceil(rect.y + rect.height) - y,
  };
}

/** 屏蔽矩形在自然（旋转后、裁剪前）输出空间的整数外扩包围盒。
 * 与导出时 draw() 的白色填充使用同一映射与取整，保证判定与实际像素一致。 */
export function exclusionNaturalRects(
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): readonly Rect[] {
  const naturalSize = outputSize(image, state.rotation);
  const mapPoint = (point: Point) =>
    projectPoint(
      point,
      image,
      state.rotation,
      displaySize,
      state.rotation,
      naturalSize,
    );
  const rects: Rect[] = [];
  for (const mark of state.marks) {
    if (!isExclusionMark(mark)) continue;
    const start = mapPoint(mark.start);
    const end = mapPoint(mark.end);
    const snapped = snapRectOutward({
      x: Math.min(start.x, end.x),
      y: Math.min(start.y, end.y),
      width: Math.abs(end.x - start.x),
      height: Math.abs(end.y - start.y),
    });
    // 越界草稿裁剪到画布；取整后为空（极小/贴边区域）的矩形不产生掩膜。
    const x = Math.max(0, snapped.x);
    const y = Math.max(0, snapped.y);
    const right = Math.min(naturalSize.width, snapped.x + snapped.width);
    const bottom = Math.min(naturalSize.height, snapped.y + snapped.height);
    if (right - x > 0 && bottom - y > 0) {
      rects.push({ x, y, width: right - x, height: bottom - y });
    }
  }
  return rects;
}

/** 屏蔽矩形在最终输出（裁剪 + 缩放后）空间的浮点投影；与文字层行框同坐标系。 */
export function exclusionOutputRects(
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): readonly Rect[] {
  const crop = mappedCropRect(image, state, displaySize);
  if (crop.width <= 0 || crop.height <= 0) return [];
  const output = finalOutputSize(image, state, displaySize);
  const scaleX = output.width / crop.width;
  const scaleY = output.height / crop.height;
  return exclusionNaturalRects(image, state, displaySize)
    .map((rect) => ({
      x: Math.max(rect.x, crop.x),
      y: Math.max(rect.y, crop.y),
      right: Math.min(rect.x + rect.width, crop.x + crop.width),
      bottom: Math.min(rect.y + rect.height, crop.y + crop.height),
    }))
    .filter((rect) => rect.right > rect.x && rect.bottom > rect.y)
    .map((rect) => ({
      x: (rect.x - crop.x) * scaleX,
      y: (rect.y - crop.y) * scaleY,
      width: (rect.right - rect.x) * scaleX,
      height: (rect.bottom - rect.y) * scaleY,
    }));
}

/** 屏蔽矩形投影到最终输出的 [0,1000] 归一化坐标，与文字层行框同参考系。 */
export function exclusionNormalizedRects(
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): readonly Rect[] {
  const output = finalOutputSize(image, state, displaySize);
  return exclusionOutputRects(image, state, displaySize).map((rect) => ({
    x: (rect.x / output.width) * 1000,
    y: (rect.y / output.height) * 1000,
    width: (rect.width / output.width) * 1000,
    height: (rect.height / output.height) * 1000,
  }));
}

/** 归一化 [0,1000] 排除框回投到当前显示空间：exclusionNormalizedRects
 * 的逆映射，供冻结基准初始屏蔽标记重建（坐标与手柄一致可编辑）。 */
export function exclusionDisplayPoints(
  box: { x: number; y: number; width: number; height: number },
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): { start: Point; end: Point } {
  const output = finalOutputSize(image, state, displaySize);
  const crop = mappedCropRect(image, state, displaySize);
  const naturalSize = outputSize(image, state.rotation);
  const toDisplay = (at: Point) =>
    projectPoint(
      at,
      image,
      state.rotation,
      naturalSize,
      state.rotation,
      displaySize,
    );
  const x0 =
    crop.x + (box.x / 1000) * output.width * (crop.width / output.width);
  const x1 =
    crop.x +
    ((box.x + box.width) / 1000) * output.width * (crop.width / output.width);
  const y0 =
    crop.y + (box.y / 1000) * output.height * (crop.height / output.height);
  const y1 =
    crop.y +
    ((box.y + box.height) / 1000) *
      output.height *
      (crop.height / output.height);
  return {
    start: toDisplay({ x: x0, y: y0 }),
    end: toDisplay({ x: x1, y: y1 }),
  };
}

/** 坐标压缩判定矩形并集是否完整覆盖目标区域（整数网格，无采样误差）。
 * 实现：x 扫线（相邻边组成板）+ 每板 y 区间合并；无额外几何框架，
 * 复杂度 O(n^2 log n)，n 为排除矩形数（用户拖拽量级，单次识别前调用一次）。 */
export function rectsCoverArea(rects: readonly Rect[], area: Rect): boolean {
  if (area.width <= 0 || area.height <= 0) return true;
  const covering = rects.filter((rect) => rect.width > 0 && rect.height > 0);
  if (covering.length === 0) return false;
  const xEdges = [
    ...new Set<number>([
      area.x,
      area.x + area.width,
      ...covering.flatMap((rect) => [rect.x, rect.x + rect.width]),
    ]),
  ].sort((a, b) => a - b);
  for (let i = 0; i < xEdges.length - 1; i += 1) {
    const left = xEdges[i]!;
    const right = xEdges[i + 1]!;
    // 只需覆盖落在目标区域内的 x 范围；区域外掩膜不影响判定。
    const overlapLeft = Math.max(left, area.x);
    const overlapRight = Math.min(right, area.x + area.width);
    if (overlapRight <= overlapLeft) continue;
    // 收集横跨本板且与目标 y 范围相交的 y 区间，排序后合并求覆盖长度。
    const spans: (readonly [number, number])[] = covering
      .filter(
        (rect) => rect.x <= overlapLeft && rect.x + rect.width >= overlapRight,
      )
      .map(
        (rect) =>
          [
            Math.max(rect.y, area.y),
            Math.min(rect.y + rect.height, area.y + area.height),
          ] as const,
      )
      .filter(([from, to]) => to > from)
      .sort((a, b) => a[0] - b[0]);
    let covered = 0;
    let reach = Number.NEGATIVE_INFINITY;
    for (const [from, to] of spans) {
      if (from > reach) {
        covered += to - from;
        reach = to;
      } else if (to > reach) {
        covered += to - reach;
        reach = to;
      }
    }
    if (covered < area.height) return false;
  }
  return true;
}

/** 整图屏蔽判定：外扩取整后的屏蔽并集覆盖外扩取整后的裁剪输出区域。 */
export function exclusionCoversOutput(
  image: HTMLImageElement,
  state: EditorState,
  displaySize: CanvasSize,
): boolean {
  const crop = mappedCropRect(image, state, displaySize);
  return rectsCoverArea(
    exclusionNaturalRects(image, state, displaySize),
    snapRectOutward(crop),
  );
}

/** 半开区间相交：行框与任一排除矩形有正面积重叠即命中；仅贴边不算。 */
export function rectIntersectsBox(
  rect: Rect,
  box: {
    readonly x1: number;
    readonly y1: number;
    readonly x2: number;
    readonly y2: number;
  },
): boolean {
  return (
    box.x2 > rect.x &&
    box.x1 < rect.x + rect.width &&
    box.y2 > rect.y &&
    box.y1 < rect.y + rect.height
  );
}
