import {
  annotationStrokeWidth,
  mirrorPointAround,
  rotatePointQuarter,
  scalePointAround,
  rotatedBoundingBox,
  type CanvasSize,
  type Mark,
  type Point,
  type Rect,
} from "./annotationGeometry";
import {
  calloutLeaderPaths,
  isFontMark,
  textAnnotationBounds,
  textAnnotationLocalBounds,
  textTransformAnchor,
  transformTextPoint,
} from "./annotationText";
import {
  annotationPathBounds,
  strokePathsBounds,
} from "./annotationStrokeGeometry";

/** 对象批量操作的公共上下文：字形类边界测量需要 2D 上下文（measureText）。 */
export type BoundsContext = CanvasRenderingContext2D | undefined;

/** 字形类与导入图片共享单对象局部变换（rotation/flip）：旋转/镜像只移动
 * 锚点并翻转标志，几何不烘入坐标，位图/字形随局部变换绘制。 */
export function isTransformableMark(mark: Mark): boolean {
  return isFontMark(mark) || mark.tool === "image";
}

/** 导入图片对象的局部矩形（未旋转）。 */
function imageMarkRect(mark: Mark): Rect {
  const left = Math.min(mark.start.x, mark.end.x);
  const top = Math.min(mark.start.y, mark.end.y);
  return {
    x: left,
    y: top,
    width: Math.abs(mark.end.x - mark.start.x),
    height: Math.abs(mark.end.y - mark.start.y),
  };
}

/** 对齐目标：按变换边界（含字形旋转后的轴对齐包围盒）计算。 */
export type ObjectAlign =
  "left" | "hcenter" | "right" | "top" | "vcenter" | "bottom";

/** 等距分布轴：至少 3 个对象，首尾固定，中间对象按中心等距。 */
export type ObjectDistributeAxis = "h" | "v";

/** 层级顺序：数组尾 = 顶层；上/下/顶/底都保持被选对象间的相对顺序。 */
export type ObjectZOrder = "up" | "down" | "front" | "back";

/** 单对象边界（变换后轴对齐包围盒）：字形类按文本边界并包含锚点（引线端），
 * 路径含实际描边与端头，其余形状含描边外沿。与选中虚线框同一规则。 */
export function markBoundsOf(mark: Mark, context: BoundsContext): Rect {
  if (context && isFontMark(mark)) {
    let box = textAnnotationBounds(context, mark);
    const padding =
      mark.tool === "numbering"
        ? 0
        : Math.max(
            (mark.style?.textOutline?.width ?? 0) / 2,
            mark.tool === "callout" ? annotationStrokeWidth(mark) / 2 : 0,
          );
    box = {
      x: box.x - padding,
      y: box.y - padding,
      width: box.width + 2 * padding,
      height: box.height + 2 * padding,
    };
    if (mark.tool === "callout") {
      const paths = calloutLeaderPaths(
        textAnnotationLocalBounds(context, mark),
        mark,
      );
      if (paths.length) {
        const local = strokePathsBounds(paths, annotationStrokeWidth(mark));
        const corners = [
          { x: local.x, y: local.y },
          { x: local.x + local.width, y: local.y },
          { x: local.x, y: local.y + local.height },
          { x: local.x + local.width, y: local.y + local.height },
        ].map((at) => transformTextPoint(mark, at));
        const x = Math.min(box.x, ...corners.map((at) => at.x)),
          y = Math.min(box.y, ...corners.map((at) => at.y));
        box = {
          x,
          y,
          width: Math.max(box.x + box.width, ...corners.map((at) => at.x)) - x,
          height:
            Math.max(box.y + box.height, ...corners.map((at) => at.y)) - y,
        };
      }
    }
    const start = transformTextPoint(mark, mark.start);
    const left = Math.min(box.x, start.x);
    const top = Math.min(box.y, start.y);
    const right = Math.max(box.x + box.width, start.x);
    const bottom = Math.max(box.y + box.height, start.y);
    return { x: left, y: top, width: right - left, height: bottom - top };
  }
  if (["line", "polyline", "pen", "highlighter", "arrow"].includes(mark.tool))
    return annotationPathBounds(mark);
  if (mark.tool === "image") {
    // 位图矩形 + 局部 90° 旋转/镜像后的轴对齐包围盒；无变换时退化为原矩形。
    const rect = imageMarkRect(mark);
    const rotated = rotatedBoundingBox(
      rect.width,
      rect.height,
      mark.rotation ?? 0,
    );
    return {
      x: rect.x + (rect.width - rotated.width) / 2,
      y: rect.y + (rect.height - rotated.height) / 2,
      width: rotated.width,
      height: rotated.height,
    };
  }
  const padding =
    mark.tool === "rectangle" || mark.tool === "ellipse"
      ? annotationStrokeWidth(mark) / 2
      : 0;
  let left = mark.start.x,
    right = mark.start.x,
    top = mark.start.y,
    bottom = mark.start.y;
  for (const at of mark.points ?? [mark.start, mark.end]) {
    left = Math.min(left, at.x);
    right = Math.max(right, at.x);
    top = Math.min(top, at.y);
    bottom = Math.max(bottom, at.y);
  }
  return {
    x: left - padding,
    y: top - padding,
    width: right - left + 2 * padding,
    height: bottom - top + 2 * padding,
  };
}

/** 选区联合边界；空选区返回 undefined。 */
export function selectionBounds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  context: BoundsContext,
): Rect | undefined {
  const selected = marks.filter((mark) => ids.has(mark.id));
  if (selected.length === 0) return undefined;
  let left = Number.POSITIVE_INFINITY,
    right = Number.NEGATIVE_INFINITY,
    top = Number.POSITIVE_INFINITY,
    bottom = Number.NEGATIVE_INFINITY;
  for (const mark of selected) {
    const box = markBoundsOf(mark, context);
    left = Math.min(left, box.x);
    right = Math.max(right, box.x + box.width);
    top = Math.min(top, box.y);
    bottom = Math.max(bottom, box.y + box.height);
  }
  return { x: left, y: top, width: right - left, height: bottom - top };
}

/** 组整体选择：选区含任一组成员时并入该组全部成员。 */
export function expandGroupSelection(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
): ReadonlySet<string> {
  const groupIds = new Set(
    marks
      .filter((mark) => mark.groupId && ids.has(mark.id))
      .map((mark) => mark.groupId!),
  );
  if (groupIds.size === 0) return ids;
  const expanded = new Set(ids);
  for (const mark of marks) {
    if (mark.groupId && groupIds.has(mark.groupId)) expanded.add(mark.id);
  }
  return expanded;
}

function mapMarkPoints(mark: Mark, move: (at: Point) => Point): Mark {
  return {
    ...mark,
    start: move(mark.start),
    end: move(mark.end),
    ...(mark.points ? { points: mark.points.map(move) } : {}),
  };
}

/** 平移：保持 id/样式/层级不变。 */
export function moveMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  delta: Point,
): readonly Mark[] {
  if (!delta.x && !delta.y) return marks;
  return marks.map((mark) =>
    ids.has(mark.id)
      ? mapMarkPoints(mark, (at) => ({ x: at.x + delta.x, y: at.y + delta.y }))
      : mark,
  );
}

/** 标量随等比缩放同步更新且只更新一次：线宽/圆角/字号/布局宽/描边宽度。 */
function scaleMarkScalars(mark: Mark, factor: number): Mark {
  if (factor === 1) return mark;
  const style = mark.style ?? {};
  const nextStyle = style
    ? {
        ...style,
        strokeWidth: (style.strokeWidth ?? 3) * factor,
        ...(style.cornerRadius
          ? { cornerRadius: Math.max(0, style.cornerRadius * factor) }
          : {}),
        ...(isFontMark(mark)
          ? { fontSize: (style.fontSize ?? 24) * factor }
          : {}),
        ...(style.textOutline
          ? {
              textOutline: {
                color: style.textOutline.color,
                width: style.textOutline.width * factor,
              },
            }
          : {}),
      }
    : style;
  return {
    ...mark,
    style: nextStyle,
    objectScale: (mark.objectScale ?? 1) * factor,
    // textWidth 是布局横宽：与几何同一比例烘焙一次；0（纯垂直拖动）保持 0。
    ...(mark.textWidth !== undefined
      ? { textWidth: mark.textWidth * factor }
      : {}),
  };
}

/** 等比缩放：绕 center 缩放几何，标量同步乘一次；factor<=0 拒绝原样返回。 */
export function scaleMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  factor: number,
  center: Point,
): readonly Mark[] {
  if (!Number.isFinite(factor) || !(factor > 0) || factor === 1) return marks;
  return marks.map((mark) =>
    ids.has(mark.id)
      ? scaleMarkScalars(
          mapMarkPoints(mark, (at) => scalePointAround(at, center, factor)),
          factor,
        )
      : mark,
  );
}

/** 字形只移动局部锚点，方向由共享字形变换表达，避免布局横宽二次旋转。 */
function moveTransformAnchor(mark: Mark, map: (at: Point) => Point): Mark {
  const anchor =
    mark.tool === "image"
      ? {
          x: (mark.start.x + mark.end.x) / 2,
          y: (mark.start.y + mark.end.y) / 2,
        }
      : textTransformAnchor(mark);
  const next = map(anchor);
  return mapMarkPoints(mark, (at) => ({
    x: at.x + next.x - anchor.x,
    y: at.y + next.y - anchor.y,
  }));
}

export function rotateMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  center: Point,
): readonly Mark[] {
  return marks.map((mark) => {
    if (!ids.has(mark.id)) return mark;
    const rotate = (at: Point) => rotatePointQuarter(at, center, 1);
    return isTransformableMark(mark)
      ? {
          ...moveTransformAnchor(mark, rotate),
          rotation: (((mark.rotation ?? 0) + 90) % 360) as Mark["rotation"],
        }
      : mapMarkPoints(mark, rotate);
  });
}

export function mirrorMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  center: Point,
  axis: "h" | "v",
): readonly Mark[] {
  return marks.map((mark) => {
    if (!ids.has(mark.id)) return mark;
    const mirror = (at: Point) => mirrorPointAround(at, center, axis);
    return isTransformableMark(mark)
      ? {
          ...moveTransformAnchor(mark, mirror),
          rotation: ((360 - (mark.rotation ?? 0)) % 360) as Mark["rotation"],
          ...(axis === "h" ? { flipX: !mark.flipX } : { flipY: !mark.flipY }),
        }
      : mapMarkPoints(mark, mirror);
  });
}

/** 层级调整：被选对象整体上/下移一层或置顶/置底，相对顺序不变。 */
export function reorderMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  mode: ObjectZOrder,
): readonly Mark[] {
  const next = [...marks];
  if (mode === "front") {
    const moving = next.filter((mark) => ids.has(mark.id));
    return [...next.filter((mark) => !ids.has(mark.id)), ...moving];
  }
  if (mode === "back") {
    const moving = next.filter((mark) => ids.has(mark.id));
    return [...moving, ...next.filter((mark) => !ids.has(mark.id))];
  }
  if (mode === "up") {
    for (let index = next.length - 2; index >= 0; index -= 1) {
      if (ids.has(next[index]!.id) && !ids.has(next[index + 1]!.id)) {
        [next[index], next[index + 1]] = [next[index + 1]!, next[index]!];
      }
    }
  } else {
    for (let index = 0; index < next.length - 1; index += 1) {
      if (!ids.has(next[index]!.id) && ids.has(next[index + 1]!.id)) {
        [next[index], next[index + 1]] = [next[index + 1]!, next[index]!];
      }
    }
  }
  return next;
}

/** 组合是对齐与等距的一个单元，成员保持相对位置。 */
export function selectionUnits(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  context: BoundsContext,
) {
  const units = new Map<string, Set<string>>();
  for (const mark of marks) {
    if (!ids.has(mark.id)) continue;
    const key = mark.groupId ? "group:" + mark.groupId : "mark:" + mark.id;
    if (!units.has(key)) units.set(key, new Set());
    units.get(key)!.add(mark.id);
  }
  return [...units.values()].map((members) => ({
    ids: members,
    box: selectionBounds(marks, members, context)!,
  }));
}

export function alignMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  align: ObjectAlign,
  context: BoundsContext,
): readonly Mark[] {
  const bounds = selectionBounds(marks, ids, context);
  const units = selectionUnits(marks, ids, context);
  if (!bounds || units.length < 2) return marks;
  let next = marks;
  for (const unit of units) {
    const box = unit.box;
    const dx =
      align === "left"
        ? bounds.x - box.x
        : align === "right"
          ? bounds.x + bounds.width - box.x - box.width
          : align === "hcenter"
            ? bounds.x + bounds.width / 2 - box.x - box.width / 2
            : 0;
    const dy =
      align === "top"
        ? bounds.y - box.y
        : align === "bottom"
          ? bounds.y + bounds.height - box.y - box.height
          : align === "vcenter"
            ? bounds.y + bounds.height / 2 - box.y - box.height / 2
            : 0;
    next = moveMarkIds(next, unit.ids, { x: dx, y: dy });
  }
  return next;
}

export function distributeMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  axis: ObjectDistributeAxis,
  context: BoundsContext,
): readonly Mark[] {
  const units = selectionUnits(marks, ids, context)
    .map((unit) => ({
      ...unit,
      center:
        axis === "h"
          ? unit.box.x + unit.box.width / 2
          : unit.box.y + unit.box.height / 2,
    }))
    .sort((a, b) => a.center - b.center);
  if (units.length < 3) return marks;
  const first = units[0]!.center,
    last = units[units.length - 1]!.center;
  let next = marks;
  units.forEach((unit, index) => {
    const delta =
      first + ((last - first) * index) / (units.length - 1) - unit.center;
    next = moveMarkIds(
      next,
      unit.ids,
      axis === "h" ? { x: delta, y: 0 } : { x: 0, y: delta },
    );
  });
  return next;
}

/** 组合：为选区（≥2）分配同一 groupId；不重排 z-order，不嵌套（成员先剥离旧组）。 */
export function groupMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  groupId: string,
): readonly Mark[] {
  const selected = marks.filter((mark) => ids.has(mark.id));
  if (selected.length < 2) return marks;
  const currentGroup = selected[0]!.groupId;
  if (
    currentGroup &&
    selected.every((mark) => mark.groupId === currentGroup) &&
    !marks.some((mark) => mark.groupId === currentGroup && !ids.has(mark.id))
  )
    return marks;
  return marks.map((mark) => (ids.has(mark.id) ? { ...mark, groupId } : mark));
}

/** 解组：剥离选区触及的全部组成员的 groupId（整组解体）。 */
export function ungroupMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
): readonly Mark[] {
  const groupIds = new Set(
    marks
      .filter((mark) => mark.groupId && ids.has(mark.id))
      .map((mark) => mark.groupId!),
  );
  if (groupIds.size === 0) return marks;
  return marks.map((mark) =>
    mark.groupId && groupIds.has(mark.groupId)
      ? { ...mark, groupId: undefined }
      : mark,
  );
}

/** 锁定/解锁：组成员统一（锁一个成员即锁整组）；解锁同理。 */
export function setMarkLocked(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  locked: boolean,
): readonly Mark[] {
  const expanded = expandGroupSelection(marks, ids);
  return marks.map((mark) =>
    expanded.has(mark.id) && !!mark.locked !== locked
      ? { ...mark, ...(locked ? { locked: true } : { locked: undefined }) }
      : mark,
  );
}

/** 删除：按 id 移除，未选对象索引/顺序不受影响。 */
export function deleteMarkIds(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
): readonly Mark[] {
  return marks.filter((mark) => !ids.has(mark.id));
}

/** 深拷贝单个标记（含嵌套样式对象），换成新 id；groupId 由映射决定。 */
function cloneMark(
  mark: Mark,
  newId: string,
  groupId: string | undefined,
  offset: Point,
): Mark {
  const style = mark.style
    ? {
        ...mark.style,
        ...(mark.style.fill ? { fill: { ...mark.style.fill } } : {}),
        ...(mark.style.textBackground
          ? { textBackground: { ...mark.style.textBackground } }
          : {}),
        ...(mark.style.textOutline
          ? {
              textOutline: {
                ...mark.style.textOutline,
              },
            }
          : {}),
      }
    : undefined;
  const moved = mapMarkPoints(mark, (at) => ({
    x: at.x + offset.x,
    y: at.y + offset.y,
  }));
  return {
    ...moved,
    id: newId,
    style,

    ...(groupId !== undefined ? { groupId } : { groupId: undefined }),
    // 副本解锁：锁定是对象自身状态，粘贴不应继承导致画布不可点。
    locked: undefined,
  };
}

/** 复制/粘贴/重复：深拷贝选区（按 z-order），全部换新 id；原组的成员
 * 重建为同一个新组。副本与原件样式/轨迹/变换完全独立。 */
export function cloneMarks(
  marks: readonly Mark[],
  ids: ReadonlySet<string>,
  offset: Point,
  newId: () => string,
  newGroupId: () => string,
): readonly Mark[] {
  const selected = marks.filter((mark) => ids.has(mark.id));
  const sourceGroups = new Map<string, string>();
  return selected.map((mark) => {
    const groupId = cloneGroupId(mark, sourceGroups, newGroupId);
    return cloneMark(mark, newId(), groupId, offset);
  });
}

function cloneGroupId(
  mark: Mark,
  sourceGroups: Map<string, string>,
  newGroupId: () => string,
): string | undefined {
  if (!mark.groupId) return undefined;
  let mapped = sourceGroups.get(mark.groupId);
  if (!mapped) {
    mapped = newGroupId();
    sourceGroups.set(mark.groupId, mapped);
  }
  return mapped;
}

/** 全选（未锁定）候选。 */
export function unlockedMarkIds(marks: readonly Mark[]): ReadonlySet<string> {
  return new Set(marks.filter((mark) => !mark.locked).map((mark) => mark.id));
}

/** 框选完整包含：未锁定且边界完全落在框内（含边界重合）。 */
export function marksContainedInRect(
  marks: readonly Mark[],
  rect: Rect,
  context: BoundsContext,
): ReadonlySet<string> {
  const ids = new Set<string>();
  for (const mark of marks) {
    if (mark.locked) continue;
    const box = markBoundsOf(mark, context);
    if (
      box.x >= rect.x &&
      box.y >= rect.y &&
      box.x + box.width <= rect.x + rect.width &&
      box.y + box.height <= rect.y + rect.height
    )
      ids.add(mark.id);
  }
  return ids;
}

/** 对象是否完全在可见画布外（边界与画布无交集）。 */
export function markOffscreen(
  mark: Mark,
  size: CanvasSize,
  context: BoundsContext,
): boolean {
  const box = markBoundsOf(mark, context);
  return (
    box.x + box.width < 0 ||
    box.y + box.height < 0 ||
    box.x > size.width ||
    box.y > size.height
  );
}

/** 离屏对象显式回中心：平移使边界中心与画布中心重合。 */
export function centerMarkInCanvas(
  mark: Mark,
  size: CanvasSize,
  context: BoundsContext,
): Mark {
  const box = markBoundsOf(mark, context);
  const dx = size.width / 2 - (box.x + box.width / 2);
  const dy = size.height / 2 - (box.y + box.height / 2);
  return mapMarkPoints(mark, (at) => ({ x: at.x + dx, y: at.y + dy }));
}
