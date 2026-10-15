import { expect, it } from "vitest";
import type { Mark } from "./annotationGeometry";
import {
  annotationPaths,
  annotationPathBounds,
  hitStrokePaths,
} from "./annotationStrokeGeometry";

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
