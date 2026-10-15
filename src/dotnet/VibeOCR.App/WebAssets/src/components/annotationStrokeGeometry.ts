import {
  annotationStrokeWidth,
  type Mark,
  type Point,
  type Rect,
} from "./annotationGeometry";

export interface AnnotationPath {
  readonly points: readonly Point[];
  readonly cap: "butt" | "round";
  readonly join?: "miter" | "round";
  readonly closed?: boolean;
  readonly filled?: boolean;
}

/** 开放头保留两条独立子路径，避免在尖端引入原绘制没有的连接角。 */
export function openArrowHeadPaths(
  tip: Point,
  angle: number,
  size: number,
  cap: AnnotationPath["cap"] = "butt",
): readonly AnnotationPath[] {
  return [-0.45, 0.45].map((side) => ({
    cap,
    points: [
      tip,
      {
        x: tip.x - size * Math.cos(angle + side),
        y: tip.y - size * Math.sin(angle + side),
      },
    ],
  }));
}

/** 坐标已映射到绘制空间；仅固定端头长度和线宽消费导出比例。 */
export function annotationArrowGeometry(mark: Mark, scale = 1) {
  const angle = Math.atan2(
    mark.end.y - mark.start.y,
    mark.end.x - mark.start.x,
  );
  const double = mark.style?.arrowDouble === true;
  const style = mark.style?.arrowStyle ?? "open";
  const cap = mark.style?.lineStyle === "dotted" ? "round" : "butt";
  const size = Math.min(
    (10 * (mark.objectScale ?? 1) + (mark.style?.strokeWidth ?? 3) * 2) * scale,
    Math.hypot(mark.end.x - mark.start.x, mark.end.y - mark.start.y) /
      (double ? 2 : 1),
  );
  const head = (tip: Point, dir: number): readonly AnnotationPath[] => {
    if (style === "open")
      return openArrowHeadPaths(
        tip,
        dir,
        16 * scale * (mark.objectScale ?? 1),
        cap,
      );
    const base = {
        x: tip.x - size * Math.cos(dir),
        y: tip.y - size * Math.sin(dir),
      },
      half = size / 2.5;
    return [
      {
        cap,
        points: [
          tip,
          {
            x: base.x + half * Math.cos(dir - Math.PI / 2),
            y: base.y + half * Math.sin(dir - Math.PI / 2),
          },
          {
            x: base.x + half * Math.cos(dir + Math.PI / 2),
            y: base.y + half * Math.sin(dir + Math.PI / 2),
          },
        ],
        closed: true,
        filled: style === "solid",
      },
    ];
  };
  const shaft: AnnotationPath = {
    cap,
    points:
      style === "open"
        ? [mark.start, mark.end]
        : [
            double
              ? {
                  x: mark.start.x + size * Math.cos(angle),
                  y: mark.start.y + size * Math.sin(angle),
                }
              : mark.start,
            {
              x: mark.end.x - size * Math.cos(angle),
              y: mark.end.y - size * Math.sin(angle),
            },
          ],
  };
  return {
    shaft,
    heads: [
      head(mark.end, angle),
      ...(double ? [head(mark.start, angle + Math.PI)] : []),
    ],
  };
}

export function annotationPathPoints(mark: Mark): readonly Point[] {
  const points = [
    ...(mark.points?.length ? mark.points : [mark.start, mark.end]),
  ];
  const last = points[points.length - 1]!;
  if (
    mark.tool === "polyline" &&
    (last.x !== mark.end.x || last.y !== mark.end.y)
  )
    points.push(mark.end);
  if (points.length === 1) points.push({ x: last.x + 0.1, y: last.y });
  return points;
}

export function annotationPaths(mark: Mark): readonly AnnotationPath[] {
  if (mark.tool === "arrow") {
    const { shaft, heads } = annotationArrowGeometry(mark);
    return [shaft, ...heads.flat()];
  }
  return [
    {
      points: annotationPathPoints(mark),
      join: "round",
      cap:
        mark.tool === "pen" ||
        mark.tool === "highlighter" ||
        mark.style?.lineStyle !== "dashed"
          ? "round"
          : "butt",
    },
  ];
}

function distanceToSegment(at: Point, from: Point, to: Point): number {
  const dx = to.x - from.x,
    dy = to.y - from.y;
  const t =
    dx || dy
      ? Math.max(
          0,
          Math.min(
            1,
            ((at.x - from.x) * dx + (at.y - from.y) * dy) / (dx * dx + dy * dy),
          ),
        )
      : 0;
  return Math.hypot(at.x - from.x - t * dx, at.y - from.y - t * dy);
}

/** Canvas miter连接（limit=10），尖角不能只外扩半线宽。 */
function miterJoins(
  path: AnnotationPath,
  radius: number,
): readonly (readonly Point[])[] {
  if (path.join === "round") return [];
  const joins: Point[][] = [];
  const points = path.points;
  points.forEach((at, index) => {
    if (!path.closed && (index === 0 || index === points.length - 1)) return;
    const before = points[(index + points.length - 1) % points.length]!,
      after = points[(index + 1) % points.length]!;
    const a = { x: at.x - before.x, y: at.y - before.y },
      b = { x: after.x - at.x, y: after.y - at.y };
    const al = Math.hypot(a.x, a.y),
      bl = Math.hypot(b.x, b.y);
    if (!al || !bl) return;
    const n1 = { x: -a.y / al, y: a.x / al },
      n2 = { x: -b.y / bl, y: b.x / bl };
    const denominator = 1 + n1.x * n2.x + n1.y * n2.y;
    if (denominator < 1e-10) return;
    const miter = {
      x: ((n1.x + n2.x) * radius) / denominator,
      y: ((n1.y + n2.y) * radius) / denominator,
    };
    if (Math.hypot(miter.x, miter.y) > radius * 10) return;
    for (const side of [-1, 1])
      joins.push([
        { x: at.x + n1.x * radius * side, y: at.y + n1.y * radius * side },
        { x: at.x + miter.x * side, y: at.y + miter.y * side },
        { x: at.x + n2.x * radius * side, y: at.y + n2.y * radius * side },
      ]);
  });
  return joins;
}

export function strokePathsBounds(
  paths: readonly AnnotationPath[],
  width: number,
): Rect {
  const radius = width / 2;
  const points: Point[] = [];
  const round = (at: Point) =>
    points.push(
      { x: at.x - radius, y: at.y - radius },
      { x: at.x + radius, y: at.y + radius },
    );
  for (const path of paths) {
    // 平端帽只沿法线外扩；不得借用端点处完整圆盘的轴向范围。
    for (let i = path.closed ? 0 : 1; i < path.points.length; i++) {
      const from =
          path.points[(i + path.points.length - 1) % path.points.length]!,
        to = path.points[i]!;
      const dx = to.x - from.x,
        dy = to.y - from.y,
        length = Math.hypot(dx, dy);
      if (!length) continue;
      const nx = (-dy / length) * radius,
        ny = (dx / length) * radius;
      for (const at of [from, to])
        for (const side of [-1, 1])
          points.push({ x: at.x + nx * side, y: at.y + ny * side });
    }
    if (!path.closed && path.cap === "round") {
      round(path.points[0]!);
      round(path.points.at(-1)!);
    }
    if (path.join === "round") {
      path.points.forEach((at, index) => {
        if (path.closed || (index > 0 && index < path.points.length - 1))
          round(at);
      });
    } else
      for (const join of miterJoins(path, radius))
        for (const at of join) points.push(at);
    // 退化路径仍保留几何位置，未绘制的butt零长度不产生虚构圆帽。
    for (const at of path.points) points.push(at);
  }
  let left = Infinity,
    top = Infinity,
    right = -Infinity,
    bottom = -Infinity;
  for (const at of points) {
    left = Math.min(left, at.x);
    top = Math.min(top, at.y);
    right = Math.max(right, at.x);
    bottom = Math.max(bottom, at.y);
  }
  return { x: left, y: top, width: right - left, height: bottom - top };
}

function inPolygon(at: Point, points: readonly Point[]): boolean {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const a = points[i]!,
      b = points[j]!;
    if (
      a.y > at.y !== b.y > at.y &&
      at.x < ((b.x - a.x) * (at.y - a.y)) / (b.y - a.y) + a.x
    )
      inside = !inside;
  }
  return inside;
}

export function hitStrokePaths(
  paths: readonly AnnotationPath[],
  at: Point,
  width: number,
  tolerance = 10,
): boolean {
  const radius = Math.max(tolerance, width / 2);
  return paths.some((path) => {
    if (path.filled && inPolygon(at, path.points)) return true;
    if (miterJoins(path, width / 2).some((join) => inPolygon(at, join)))
      return true;
    return path.points.some((point, index) => {
      const previous =
        index === 0
          ? path.closed
            ? path.points.at(-1)!
            : point
          : path.points[index - 1]!;
      return distanceToSegment(at, previous, point) <= radius;
    });
  });
}

export function annotationPathBounds(mark: Mark): Rect {
  return strokePathsBounds(annotationPaths(mark), annotationStrokeWidth(mark));
}
