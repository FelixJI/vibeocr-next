import { describe, expect, it, vi } from "vitest";
import type { Mark, Point } from "./annotationGeometry";
import { mirrorPointAround, rotatePointQuarter } from "./annotationGeometry";
import { textAnnotationBounds, transformTextPoint } from "./annotationText";
import {
  alignMarkIds,
  centerMarkInCanvas,
  cloneMarks,
  deleteMarkIds,
  distributeMarkIds,
  expandGroupSelection,
  groupMarkIds,
  markOffscreen,
  marksContainedInRect,
  mirrorMarkIds,
  moveMarkIds,
  reorderMarkIds,
  rotateMarkIds,
  scaleMarkIds,
  selectionBounds,
  setMarkLocked,
  ungroupMarkIds,
  unlockedMarkIds,
} from "./annotationObjects";

function box(id: string, x: number, y = 0): Mark {
  return {
    id,
    tool: "rectangle",
    start: { x, y },
    end: { x: x + 20, y: y + 10 },
    style: { fill: { color: "#ff0000", opacity: 0 } },
  };
}
const ids = (...values: string[]) => new Set(values);
const context = {
  save: vi.fn(),
  restore: vi.fn(),
  measureText: (text: string) => ({ width: text.length * 10 }),
} as unknown as CanvasRenderingContext2D;

describe("对象身份、组与批量几何", () => {
  it("删除和重排使用稳定身份，层级调整保持所选相对顺序", () => {
    const marks = [box("a", 0), box("b", 40), box("c", 80), box("d", 120)];
    const moved = reorderMarkIds(marks, ids("a", "c"), "up");
    expect(moved.map((m) => m.id)).toEqual(["b", "a", "d", "c"]);
    expect(
      reorderMarkIds(moved, ids("a", "c"), "back").map((m) => m.id),
    ).toEqual(["a", "c", "b", "d"]);
    expect(deleteMarkIds(moved, ids("a"))).toEqual([
      marks[1],
      marks[3],
      marks[2],
    ]);
    expect(marks[0]!.start.x).toBe(0);
  });
  it("副本重新分组，轨迹偏移并深拷贝嵌套样式", () => {
    const mark: Mark = {
      ...box("a", 0),
      tool: "pen",
      groupId: "old",
      points: [
        { x: 0, y: 0 },
        { x: 20, y: 10 },
      ],
      style: {
        fill: { opacity: 20 },
        textBackground: { color: "#ffffff" },
        textOutline: { color: "#000000", width: 2 },
      },
    };
    let next = 0;
    const clones = cloneMarks(
      [mark, { ...box("b", 40), groupId: "old" }],
      ids("a", "b"),
      { x: 16, y: 16 },
      () => "copy-" + ++next,
      () => "new-group",
    );
    expect(clones.map((m) => m.id)).toEqual(["copy-1", "copy-2"]);
    expect(clones.map((m) => m.groupId)).toEqual(["new-group", "new-group"]);
    expect(clones[0]!.points).toEqual([
      { x: 16, y: 16 },
      { x: 36, y: 26 },
    ]);
    for (const key of ["fill", "textBackground", "textOutline"] as const)
      expect(clones[0]!.style![key]).not.toBe(mark.style![key]);
    expect(clones[0]!.points![0]).not.toBe(mark.points![0]);
  });
  it("组整体选择与锁定；透明对象可框选，锁定对象跳过", () => {
    const marks = groupMarkIds(
      [box("a", 0), box("b", 40), box("c", 80)],
      ids("a", "b"),
      "g",
    );
    expect(expandGroupSelection(marks, ids("a"))).toEqual(ids("a", "b"));
    const locked = setMarkLocked(marks, ids("b"), true);
    expect(unlockedMarkIds(locked)).toEqual(ids("c"));
    expect(
      marksContainedInRect(
        locked,
        { x: 0, y: 0, width: 100, height: 20 },
        undefined,
      ),
    ).toEqual(ids("c"));
    expect(
      marksContainedInRect(
        marks,
        { x: 0, y: 0, width: 20, height: 10 },
        undefined,
      ),
    ).toEqual(ids("a"));
    expect(ungroupMarkIds(marks, ids("a")).every((m) => !m.groupId)).toBe(true);
  });
  it("组合是对齐与等距单元，首尾固定且组内位置不变", () => {
    const marks = [
      { ...box("a", 0), groupId: "g" },
      { ...box("b", 40), groupId: "g" },
      box("c", 80),
      box("d", 300),
    ];
    const selected = ids("a", "b", "c", "d");
    const aligned = alignMarkIds(marks, selected, "left", undefined);
    expect(aligned.map((m) => m.start.x)).toEqual([0, 40, 0, 0]);
    const spaced = distributeMarkIds(marks, selected, "h", undefined);
    expect(spaced.map((m) => m.start.x)).toEqual([0, 40, 160, 300]);
  });
  it("离屏对象保留且显式回中心，旧状态可直接恢复", () => {
    const original = box("a", 1000, -40);
    expect(
      markOffscreen(original, { width: 900, height: 600 }, undefined),
    ).toBe(true);
    const restored = centerMarkInCanvas(
      original,
      { width: 900, height: 600 },
      undefined,
    );
    expect(restored.start).toEqual({ x: 440, y: 295 });
    expect(original.start).toEqual({ x: 1000, y: -40 });
  });
  it("几何旋转镜像保留轴对齐排除矩形与层级", () => {
    const marks = [
      { ...box("a", 20, 30), tool: "exclude" as const },
      box("b", 80),
    ];
    const turned = rotateMarkIds(marks, ids("a"), { x: 0, y: 0 });
    expect(turned[0]!.start).toEqual({ x: -30, y: 20 });
    expect(turned[0]!.end).toEqual({ x: -40, y: 40 });
    expect(turned[1]).toBe(marks[1]);
    expect(
      mirrorMarkIds(turned, ids("a"), { x: 0, y: 0 }, "h")[0]!.start,
    ).toEqual({ x: 30, y: 20 });
  });
  it.each(["text", "callout", "numbering"] as const)(
    "%s 的旋转与镜像组合使用同一正逆映射",
    (tool) => {
      const original: Mark = {
        id: "a",
        tool,
        start: { x: 100, y: 120 },
        end: { x: 220, y: 160 },
        text: "AB",
        textWidth: 120,
        style: { fontSize: 20 },
      };
      const center = { x: 350.25, y: 240.75 };
      const probes: Point[] = [
        original.start,
        original.end,
        { x: 130, y: 145 },
      ];
      const turned = rotateMarkIds([original], ids("a"), center)[0]!;
      const mirrored = mirrorMarkIds([turned], ids("a"), center, "h")[0]!;
      const localDelta = {
        x: mirrored.end.x - original.end.x,
        y: mirrored.end.y - original.end.y,
      };
      // numbering 的锚点同样整体平移，因此 end/start 的位移一致。
      for (const at of probes) {
        const local = { x: at.x + localDelta.x, y: at.y + localDelta.y };
        const expected = mirrorPointAround(
          rotatePointQuarter(at, center, 1),
          center,
          "h",
        );
        expect(transformTextPoint(mirrored, local)).toEqual(expected);
        expect(transformTextPoint(mirrored, expected, true)).toEqual(local);
      }
      let roundTrip = original;
      for (let i = 0; i < 4; i++)
        roundTrip = rotateMarkIds([roundTrip], ids("a"), center)[0]!;
      expect(roundTrip.start).toEqual(original.start);
      expect(roundTrip.end).toEqual(original.end);
      expect(roundTrip.rotation).toBe(0);
    },
  );
  it("比例缩放字号、横宽、装饰只乘一次，零横宽保留且非法比例拒绝", () => {
    const original: Mark = {
      id: "a",
      tool: "text",
      start: { x: 20, y: 50 },
      end: { x: 20, y: 80 },
      text: "A",
      textWidth: 0,
      style: { fontSize: 20, textBackground: { color: "#ffffff" } },
    };
    const scaled = scaleMarkIds([original], ids("a"), 2, { x: 0, y: 0 })[0]!;
    expect(scaled.textWidth).toBe(0);
    expect(scaled.style!.fontSize).toBe(40);
    expect(scaled.objectScale).toBe(2);
    const before = textAnnotationBounds(context, original);
    const after = textAnnotationBounds(context, scaled);
    expect(after).toEqual({
      x: before.x * 2,
      y: before.y * 2,
      width: before.width * 2,
      height: before.height * 2,
    });
    const list = [original];
    expect(scaleMarkIds(list, ids("a"), Infinity, { x: 0, y: 0 })).toBe(list);
    expect(scaleMarkIds(list, ids("a"), 0, { x: 0, y: 0 })).toBe(list);
    expect(moveMarkIds(list, ids("a"), { x: 5, y: -10 })[0]!.textWidth).toBe(0);
    expect(selectionBounds([], ids(), undefined)).toBeUndefined();
  });
});
