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
  marksContainedInRect,
} from "./annotationObjects";

it("GEO-02 共线短末段的round连接不超出butt末端，框选与离屏一致", () => {
  const mark: Mark = {
    id: "polyline",
    tool: "polyline",
    start: { x: 400, y: 300 },
    end: { x: 443.25, y: 300 },
    points: [
      { x: 400, y: 300 },
      { x: 440, y: 300 },
      { x: 443.25, y: 300 },
    ],
    style: { strokeWidth: 8, lineStyle: "dashed" },
  };
  expect(markBoundsOf(mark, undefined)).toEqual({
    x: 400,
    y: 296,
    width: 43.25,
    height: 8,
  });
  expect(
    marksContainedInRect(
      [mark],
      { x: 399, y: 295, width: 44.5, height: 10 },
      undefined,
    ),
  ).toEqual(new Set(["polyline"]));
  const ids = new Set(["polyline"]);
  const shifted = moveMarkIds(
    scaleMarkIds([mark], ids, 1.25 ** 6, { x: 422, y: 300 }),
    ids,
    { x: -505, y: 0 },
  )[0]!;
  const box = markBoundsOf(shifted, undefined);
  expect(box.x + box.width).toBeCloseTo(-1.93768310546875, 8);
  expect(markOffscreen(shifted, { width: 900, height: 600 }, undefined)).toBe(
    true,
  );
});

it.each([1, -1] as const)(
  "GEO-02 %d向斜转角只含外侧圆弧；短末段不虚增另一轴",
  (sign) => {
    const q = 4 / Math.SQRT2;
    const points = [
      { x: -20, y: -20 * sign },
      { x: 0, y: 0 },
      { x: 0, y: 0 },
      { x: -0.25, y: 0.25 * sign },
    ];
    const box = strokePathsBounds([{ points, cap: "butt", join: "round" }], 8);
    expect(box.x).toBeCloseTo(-20 - q, 8);
    expect(box.x + box.width).toBeCloseTo(4, 8);
    expect(box.y).toBeCloseTo(sign === 1 ? -20 - q : -0.25 - q, 8);
    expect(box.y + box.height).toBeCloseTo(sign === 1 ? 0.25 + q : 20 + q, 8);
  },
);

it("GEO-02 近直线不变圆盘，180度折返仍保留前方半圆与round端帽", () => {
  const near = strokePathsBounds(
    [
      {
        points: [
          { x: 0, y: 0 },
          { x: 40, y: 0 },
          { x: 43.25, y: 0.01 },
        ],
        cap: "butt",
        join: "round",
      },
    ],
    8,
  );
  const length = Math.hypot(3.25, 0.01);
  expect(near.x + near.width).toBeCloseTo(43.25 + (4 * 0.01) / length, 8);
  expect(near.y + near.height).toBeCloseTo(0.01 + (4 * 3.25) / length, 8);
  const points = [
    { x: 0, y: 0 },
    { x: 40, y: 0 },
    { x: 39.75, y: 0 },
  ];
  expect(
    strokePathsBounds([{ points, cap: "butt", join: "round" }], 8),
  ).toEqual({ x: 0, y: -4, width: 44, height: 8 });
  expect(
    strokePathsBounds([{ points, cap: "round", join: "round" }], 8),
  ).toEqual({ x: -4, y: -4, width: 48, height: 8 });
});

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
