import { describe, expect, it, vi } from "vitest";
import type { Mark, Point } from "./annotationGeometry";
import {
  mirrorPointAround,
  rotatePointQuarter,
  rotateEditorState,
  projectPoint,
} from "./annotationGeometry";
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
  markBoundsOf,
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
        { x: -2, y: -2, width: 104, height: 24 },
        undefined,
      ),
    ).toEqual(ids("c"));
    expect(
      marksContainedInRect(
        marks,
        { x: -1.5, y: -1.5, width: 23, height: 13 },
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
  it.each(["mosaic", "blur"] as const)(
    "%s 笔刷缩放同步轨迹、宽度与命中包围盒",
    (tool) => {
      const original: Mark = {
        id: "brush",
        tool,
        start: { x: 100, y: 100 },
        end: { x: 200, y: 100 },
        points: [
          { x: 100, y: 100 },
          { x: 200, y: 100 },
        ],
        style: { brushSize: 24 },
      };
      const before = markBoundsOf(original, undefined);
      const scaled = scaleMarkIds([original], ids("brush"), 2, {
        x: 0,
        y: 0,
      })[0]!;
      expect(scaled.style!.brushSize).toBe(48);
      expect(scaled.points).toEqual([
        { x: 200, y: 200 },
        { x: 400, y: 200 },
      ]);
      const after = markBoundsOf(scaled, undefined);
      expect(after).toEqual({
        x: before.x * 2,
        y: before.y * 2,
        width: before.width * 2,
        height: before.height * 2,
      });
      expect(original.style!.brushSize).toBe(24);
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

describe("OBJ-04 说明局部变换与整图旋转组合", () => {
  it.each([
    { rotation: 0 as const, flipX: true },
    { rotation: 90 as const, flipY: true },
  ])("世界引线目标经正逆映射投影，保留字形方向与字号：%j", (transform) => {
    const image = { naturalWidth: 900, naturalHeight: 600 } as HTMLImageElement;
    const size = { width: 900, height: 600 };
    const original: Mark = {
      id: "callout",
      tool: "callout",
      start: { x: 100, y: 100 },
      end: { x: 300, y: 210 },
      text: "F",
      textWidth: 0,
      objectScale: 1.25,
      style: { fontSize: 40 },
      ...transform,
    };
    const worldTarget = transformTextPoint(original, original.start);
    const turned = rotateEditorState(
      { rotation: 0, marks: [original] },
      image,
      size,
      90,
    ).marks[0]!;
    const actual = transformTextPoint(turned, turned.start);
    const expected = projectPoint(worldTarget, image, 0, size, 90, size);
    expect(actual.x).toBeCloseTo(expected.x, 8);
    expect(actual.y).toBeCloseTo(expected.y, 8);
    expect(turned.rotation).toBe(original.rotation);
    expect(turned.flipX).toBe(original.flipX);
    expect(turned.flipY).toBe(original.flipY);
    expect(turned.style).toBe(original.style);
    expect(turned.textWidth).toBe(0);
    expect(turned.objectScale).toBe(1.25);
  });
});

describe("PR249 锁定幂等", () => {
  it("重复锁定或解锁保持对象引用，整组变化一次后不产生空修改", () => {
    const marks = [box("a", 0), box("b", 40)];
    const unchanged = setMarkLocked(marks, ids("a"), false);
    expect(unchanged[0]).toBe(marks[0]);
    expect(unchanged[1]).toBe(marks[1]);
    const grouped = groupMarkIds(marks, ids("a", "b"), "g");
    const locked = setMarkLocked(grouped, ids("a"), true);
    const relocked = setMarkLocked(locked, ids("b"), true);
    expect(relocked[0]).toBe(locked[0]);
    expect(relocked[1]).toBe(locked[1]);
    const unlocked = setMarkLocked(locked, ids("a"), false);
    expect(setMarkLocked(unlocked, ids("a"), false)).toEqual(unlocked);
    expect(setMarkLocked(unlocked, ids("a"), false)[0]).toBe(unlocked[0]);
  });
});

describe("PR249 第二轮对象契约", () => {
  it("完整单组重复组合保持引用，组加普通对象仍可合并", () => {
    const marks = groupMarkIds(
      [box("a", 0), box("b", 40), box("c", 80)],
      ids("a", "b"),
      "g",
    );
    expect(groupMarkIds(marks, ids("a", "b"), "replacement")).toBe(marks);
    const merged = groupMarkIds(marks, ids("a", "b", "c"), "merged");
    expect(merged.map((mark) => mark.groupId)).toEqual([
      "merged",
      "merged",
      "merged",
    ]);
  });
  it.each([
    { rotation: 90 as const },
    { flipX: true },
    { flipY: true },
    { rotation: 270 as const, flipX: true },
  ])(
    "带对象变换的普通文字整图旋转投影原世界锚点，不改变局部字形：%j",
    (transform) => {
      const image = {
        naturalWidth: 300,
        naturalHeight: 200,
      } as HTMLImageElement;
      const size = { width: 900, height: 600 };
      let mark: Mark = {
        id: "text",
        tool: "text",
        start: { x: 180, y: 180 },
        end: { x: 420, y: 216 },
        text: "F2Ab",
        textWidth: 240,
        objectScale: 1.25,
        style: { fontSize: 48 },
        ...transform,
      };
      let previousRotation = 0;
      for (const rotation of [90, 180, 270, 0]) {
        const anchor = { x: Math.min(mark.start.x, mark.end.x), y: mark.end.y };
        const expected = projectPoint(
          anchor,
          image,
          previousRotation,
          size,
          rotation,
          size,
        );
        const next = rotateEditorState(
          { rotation: previousRotation, marks: [mark] },
          image,
          size,
          rotation,
        ).marks[0]!;
        expect(Math.min(next.start.x, next.end.x)).toBeCloseTo(expected.x, 8);
        expect(next.end.y).toBeCloseTo(expected.y, 8);
        expect(next.end.x - next.start.x).toBeCloseTo(
          mark.end.x - mark.start.x,
          8,
        );
        expect(next.end.y - next.start.y).toBeCloseTo(
          mark.end.y - mark.start.y,
          8,
        );
        expect(next.rotation).toBe(mark.rotation);
        expect(next.flipX).toBe(mark.flipX);
        expect(next.flipY).toBe(mark.flipY);
        expect(next.textWidth).toBe(240);
        expect(next.style).toBe(mark.style);
        expect(next.objectScale).toBe(1.25);
        mark = next;
        previousRotation = rotation;
      }
    },
  );
});

describe("PR249 第三轮可见描边边界", () => {
  it.each(["pen", "highlighter"] as const)(
    "%s粗描边四向旋转/缩放后部分可见，不被当成离屏或完整框入中心线",
    (tool) => {
      const original: Mark = {
        id: "ink",
        tool,
        start: { x: 100, y: -12 },
        end: { x: 200, y: -12 },
        points: [
          { x: 100, y: -12 },
          { x: 200, y: -12 },
        ],
        style: { strokeWidth: tool === "highlighter" ? 8 : 32 },
      };
      for (const factor of [1, 1.25]) {
        let mark = scaleMarkIds([original], ids("ink"), factor, {
          x: 150,
          y: 0,
        })[0]!;
        for (let turn = 0; turn < 4; turn++) {
          expect(
            markOffscreen(mark, { width: 300, height: 300 }, undefined),
          ).toBe(false);
          expect(
            marksContainedInRect(
              [mark],
              {
                x: Math.min(mark.start.x, mark.end.x),
                y: Math.min(mark.start.y, mark.end.y),
                width: Math.abs(mark.end.x - mark.start.x),
                height: Math.abs(mark.end.y - mark.start.y),
              },
              undefined,
            ),
          ).toEqual(ids());
          mark = rotateMarkIds([mark], ids("ink"), { x: 150, y: 150 })[0]!;
        }
      }
    },
  );
  it.each(["rectangle", "ellipse", "line", "polyline"] as const)(
    "%s边界包含实际描边外沿，效果/屏蔽不凭空扩大",
    (tool) => {
      const mark: Mark = {
        id: "shape",
        tool,
        start: { x: 100, y: 100 },
        end: { x: 200, y: 150 },
        style: { strokeWidth: 32 },
      };
      expect(markBoundsOf(mark, undefined)).toEqual({
        x: 84,
        y: 84,
        width: 132,
        height: 82,
      });
      expect(markBoundsOf({ ...mark, tool: "exclude" }, undefined)).toEqual({
        x: 100,
        y: 100,
        width: 100,
        height: 50,
      });
    },
  );
  it.each(["open", "hollow", "solid"] as const)(
    "%s箭头的四向端头和描边在对象比例下进入边界",
    (arrowStyle) => {
      let mark: Mark = {
        id: "arrow",
        tool: "arrow",
        start: { x: 100, y: 150 },
        end: { x: 300, y: 150 },
        style: { strokeWidth: 12, arrowStyle, arrowDouble: true },
        objectScale: 4,
      };
      for (let turn = 0; turn < 4; turn++) {
        const bounds = markBoundsOf(mark, undefined);
        expect(turn % 2 ? bounds.width : bounds.height).toBeGreaterThan(60);
        const centerline = {
          x: Math.min(mark.start.x, mark.end.x) - 6,
          y: Math.min(mark.start.y, mark.end.y) - 6,
          width: Math.abs(mark.end.x - mark.start.x) + 12,
          height: Math.abs(mark.end.y - mark.start.y) + 12,
        };
        expect(marksContainedInRect([mark], centerline, undefined)).toEqual(
          ids(),
        );
        mark = rotateMarkIds([mark], ids("arrow"), { x: 200, y: 150 })[0]!;
      }
    },
  );
});
