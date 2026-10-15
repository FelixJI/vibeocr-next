import { expect, it } from "vitest";
import type { Mark } from "./annotationGeometry";
import {
  annotationPaths,
  annotationPathBounds,
  hitStrokePaths,
  strokePathsBounds,
  openArrowHeadPaths,
} from "./annotationStrokeGeometry";
import {
  markBoundsOf,
  markOffscreen,
  moveMarkIds,
  scaleMarkIds,
} from "./annotationObjects";

it.each([
  ["line", undefined, "round"],
  ["line", "dashed", "butt"],
  ["line", "dotted", "round"],
  ["polyline", "dashed", "butt"],
  ["pen", "dashed", "round"],
  ["highlighter", undefined, "round"],
  ["arrow", "dashed", "butt"],
  ["arrow", "dotted", "round"],
] as const)("GEO-01 %s/%s共享路径保留实际%s端帽", (tool, lineStyle, cap) => {
  const mark: Mark = {
    id: "cap",
    tool,
    start: { x: 100, y: 100 },
    end: { x: 200, y: 100 },
    style: { lineStyle },
  };
  expect(annotationPaths(mark).every((path) => path.cap === cap)).toBe(true);
});

it("GEO-01 butt开放箭头放大后完全离屏，不能借用圆帽外伸区域", () => {
  const original: Mark = {
    id: "arrow",
    tool: "arrow",
    start: { x: 400, y: 300 },
    end: { x: 440, y: 300 },
    style: { strokeWidth: 8 },
  };
  const ids = new Set(["arrow"]);
  const scaled = scaleMarkIds([original], ids, 1.25 ** 6, { x: 420, y: 300 });
  const mark = moveMarkIds(scaled, ids, { x: -510, y: 0 })[0]!;
  const bounds = markBoundsOf(mark, undefined);
  expect(bounds.x + bounds.width).toBeCloseTo(
    mark.end.x + (mark.style!.strokeWidth! / 2) * Math.sin(0.45),
    8,
  );
  expect(markOffscreen(mark, { width: 900, height: 600 }, undefined)).toBe(
    true,
  );
});

it("GEO-01 对角butt只沿法线外扩；round端帽与round连接保留实际圆弧", () => {
  const points = [
    { x: 100, y: 100 },
    { x: 120, y: 120 },
  ];
  const radius = 4 / Math.SQRT2;
  const diagonal = strokePathsBounds([{ points, cap: "butt" }], 8);
  const expected = {
    x: 100 - radius,
    y: 100 - radius,
    width: 20 + radius * 2,
    height: 20 + radius * 2,
  };
  for (const edge of ["x", "y", "width", "height"] as const)
    expect(diagonal[edge]).toBeCloseTo(expected[edge], 8);
  expect(strokePathsBounds([{ points, cap: "round" }], 8)).toEqual({
    x: 96,
    y: 96,
    width: 28,
    height: 28,
  });
  expect(
    strokePathsBounds(
      [
        {
          points: [
            { x: 100, y: 100 },
            { x: 120, y: 100 },
            { x: 120, y: 120 },
          ],
          cap: "butt",
          join: "round",
        },
      ],
      8,
    ),
  ).toEqual({ x: 100, y: 96, width: 24, height: 24 });
});

it("GEO-01 说明开放头默认butt，dotted开放头round不可互换", () => {
  const tip = { x: 440, y: 300 };
  const butt = strokePathsBounds(openArrowHeadPaths(tip, 0, 16), 8);
  expect(butt.x + butt.width).toBeCloseTo(440 + 4 * Math.sin(0.45), 8);
  const dotted: Mark = {
    id: "arrow",
    tool: "arrow",
    start: { x: 400, y: 300 },
    end: tip,
    style: { strokeWidth: 8, lineStyle: "dotted" },
  };
  const round = annotationPathBounds(dotted);
  expect(round.x + round.width).toBe(444);
});

it("三角miter尖端和实心内部进入几何；空心内部与远处空白不命中", () => {
  const hollow: Mark = {
    id: "arrow",
    tool: "arrow",
    start: { x: 100, y: 300 },
    end: { x: 700, y: 300 },
    objectScale: 10,
    style: { strokeWidth: 30, arrowStyle: "hollow" },
  };
  const bounds = annotationPathBounds(hollow);
  expect(bounds.x + bounds.width).toBeCloseTo(
    700 + (15 * Math.sqrt(1 + 0.4 ** 2)) / 0.4,
    8,
  );
  expect(hitStrokePaths(annotationPaths(hollow), { x: 735, y: 300 }, 30)).toBe(
    true,
  );
  expect(hitStrokePaths(annotationPaths(hollow), { x: 750, y: 300 }, 30)).toBe(
    false,
  );
  expect(hitStrokePaths(annotationPaths(hollow), { x: 600, y: 300 }, 30)).toBe(
    false,
  );
  const solid = {
    ...hollow,
    style: { ...hollow.style, arrowStyle: "solid" as const },
  };
  expect(hitStrokePaths(annotationPaths(solid), { x: 600, y: 300 }, 30)).toBe(
    true,
  );
});
