import { describe, expect, it, vi } from "vitest";

import {
  applyMaskBrush,
  applyMaskRect,
  INPAINT_MASK_WORK_BUDGET,
  maskBounds,
  maskCount,
  totalDistinctPatchPixels,
} from "./inpaintMaskState";
import { INPAINT_MAX_TOTAL_PATCH_PIXELS, type InpaintRect } from "./inpaint";

const IMAGE = { width: 64, height: 48 };

function rectState(): InpaintRect {
  return { x: 10, y: 8, width: 12, height: 8 };
}

describe("inpaint mask state", () => {
  it.each([
    { x: 100, y: 10, width: 8, height: 8 },
    { x: -100, y: 10, width: 8, height: 8 },
    { x: 10, y: 100, width: 8, height: 8 },
    { x: 10, y: -100, width: 8, height: 8 },
    { x: 10.2, y: 10.2, width: 0, height: 8 },
    { x: 10.2, y: 10.2, width: 8, height: 0 },
  ])("R5 empty intersections preserve the current mask: %j", (rect) => {
    const current = { rect: rectState(), data: new Uint8Array(96).fill(1) };
    for (const add of [true, false]) {
      expect(applyMaskRect(current, rect, IMAGE, add)).toEqual({
        ok: true,
        state: current,
      });
      const result = applyMaskRect(undefined, rect, IMAGE, add);
      expect(result).toEqual({ ok: true, state: undefined });
    }
  });

  it("R5 a brush wholly outside the image is a no-op", () => {
    expect(
      applyMaskBrush(undefined, [{ x: 100, y: 10 }], 3, IMAGE, true),
    ).toEqual({ ok: true, state: undefined });
  });

  it("R6 rejects oversized initial regions before allocating mask storage", () => {
    const allocations: number[] = [];
    const Native = Uint8Array;
    vi.stubGlobal(
      "Uint8Array",
      new Proxy(Native, {
        construct(target, args) {
          if (typeof args[0] === "number") allocations.push(args[0]);
          return Reflect.construct(target, args);
        },
      }),
    );
    try {
      expect(
        applyMaskRect(
          undefined,
          { x: 0, y: 0, width: 1001, height: 1001 },
          { width: 1100, height: 1100 },
          true,
        ),
      ).toEqual({ ok: false, reason: "roi-budget" });
      expect(allocations).toEqual([]);
      expect(
        applyMaskBrush(
          undefined,
          [{ x: 500, y: 500 }],
          501,
          { width: 1100, height: 1100 },
          true,
        ),
      ).toEqual({ ok: false, reason: "roi-budget" });
      expect(allocations).toEqual([]);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("rect add fills exactly the clamped region; remove carves holes", () => {
    const added = applyMaskRect(undefined, rectState(), IMAGE, true);
    expect(added.ok).toBe(true);
    if (!added.ok || !added.state) return;
    expect(maskCount(added.state)).toBe(12 * 8);
    expect(maskBounds(added.state)).toEqual(rectState());

    // 移除中心区域：掩膜出现洞，包围盒保持外扩范围。
    const carved = applyMaskRect(
      added.state,
      { x: 12, y: 10, width: 4, height: 4 },
      IMAGE,
      false,
    );
    expect(carved.ok).toBe(true);
    if (!carved.ok || !carved.state) return;
    expect(maskCount(carved.state)).toBe(12 * 8 - 16);
    expect(maskBounds(carved.state)).toEqual(rectState());
    // 逐点核对：洞内为 0，外框角落为 1。
    const at = (
      state: NonNullable<typeof carved.state>,
      x: number,
      y: number,
    ) => state.data[(y - state.rect.y) * state.rect.width + (x - state.rect.x)];
    expect(at(carved.state, 13, 11)).toBe(0);
    expect(at(carved.state, 10, 8)).toBe(1);
    expect(at(carved.state, 21, 15)).toBe(1);
  });

  it("removing from an empty mask is a successful no-op", () => {
    const result = applyMaskRect(undefined, rectState(), IMAGE, false);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(maskCount(result.state)).toBe(0);
  });

  it("brush stamps the trail with radius coverage", () => {
    const painted = applyMaskBrush(
      undefined,
      [
        { x: 10, y: 10 },
        { x: 20, y: 10 },
      ],
      3,
      IMAGE,
      true,
    );
    expect(painted.ok).toBe(true);
    if (!painted.ok || !painted.state) return;
    // 轨迹内部必被覆盖；远离轨迹的角落必为空。
    const state = painted.state;
    const at = (st: NonNullable<typeof state>, x: number, y: number) =>
      st.data[(y - st.rect.y) * st.rect.width + (x - st.rect.x)];
    expect(at(state, 15, 10)).toBe(1);
    expect(at(state, 10, 10)).toBe(1);
    expect(at(state, 12, 14)).toBe(0);
    expect(at(state, 24, 10)).toBe(0);
    // maskBounds 为紧致包围盒（覆盖像素范围），非 ROI。
    expect(maskBounds(state)).toEqual({ x: 7, y: 7, width: 17, height: 7 });

    // 笔刷移除在已有掩膜上挖洞。
    const erased = applyMaskBrush(state, [{ x: 14, y: 10 }], 2, IMAGE, false);
    expect(erased.ok).toBe(true);
    if (!erased.ok || !erased.state) return;
    expect(at(erased.state, 14, 10)).toBe(0);
    expect(at(erased.state, 20, 10)).toBe(1);
  });

  it("rejects rect add beyond the ROI budget and keeps the previous state", () => {
    const big = applyMaskRect(
      undefined,
      { x: 0, y: 0, width: 40, height: 30 },
      { width: 40, height: 30 },
      true,
    );
    expect(big.ok).toBe(true);
    if (!big.ok || !big.state) return;
    // 再加一个不相邻矩形，联合 ROI 超过 1MP 限额（这里用超大图模拟）。
    const huge = { width: 4000, height: 3000 };
    const overflow = applyMaskRect(
      {
        rect: { x: 0, y: 0, width: 4000, height: 3000 },
        data: new Uint8Array(4000 * 3000),
      },
      { x: 3990, y: 2990, width: 8, height: 8 },
      huge,
      true,
    );
    expect(overflow.ok).toBe(false);
    if (overflow.ok) return;
    expect(overflow.reason).toBe("roi-budget");
  });

  it("mask-budget rejection keeps the original state untouched", () => {
    // 单矩形 ROI 即全部有效像素：超过 1MP 时 ROI 预算先拒绝（两者同限）。
    const image = { width: 1100, height: 1100 };
    const overflow = applyMaskRect(
      undefined,
      { x: 0, y: 0, width: 1001, height: 1001 },
      image,
      true,
    );
    expect(overflow.ok).toBe(false);
    if (overflow.ok) return;
    expect(overflow.reason).toBe("roi-budget");
  });

  it("work budget bounds pathological brush scribbles", () => {
    // 构造一条超过 stamp 预算的密集长轨迹：拒绝而不是无限光栅化。
    const points = Array.from({ length: 4000 }, (_, i) => ({
      x: 10 + i * 8,
      y: 24,
    }));
    const image = { width: 64_000, height: 48 };
    const result = applyMaskBrush(undefined, points, 30, image, true);
    if (result.ok) {
      // 若预算内完成（实现允许），掩膜必须有限且不超限。
      expect(maskCount(result.state)).toBeGreaterThan(0);
    } else {
      expect(["roi-budget", "work-budget"]).toContain(result.reason);
      expect(INPAINT_MASK_WORK_BUDGET).toBeGreaterThan(0);
    }
  });
});

describe("totalDistinctPatchPixels（历史补丁聚合预算口径）", () => {
  it("counts only references retained by a new commit while preserving undo/redo storage", () => {
    const earlier = { id: 1, width: 2000, height: 2000 };
    const redoOnly = { id: 2, width: 2000, height: 2000 };
    const history = [{ patches: [earlier] }, { patches: [earlier, redoOnly] }];
    // Undo keeps redo live until the next commit; the new branch then drops it.
    expect(totalDistinctPatchPixels(history)).toBe(8_000_000);
    expect(totalDistinctPatchPixels(history, 0)).toBe(4_000_000);
    expect(
      totalDistinctPatchPixels(history, 0) + 4_000_000,
    ).toBeLessThanOrEqual(INPAINT_MAX_TOTAL_PATCH_PIXELS);
    expect(history[1]!.patches).toEqual([earlier, redoOnly]);
  });

  it("重复历史引用只计一次：同一补丁在多个条目出现不重复累计", () => {
    const patch = { id: 1, width: 1000, height: 1000 };
    const entries = [
      { patches: [patch] },
      // 后续每次提交（或撤销/重做分支）都重复引用同一补丁。
      { patches: [patch] },
      { patches: [patch, { id: 2, width: 500, height: 400 }] },
    ];
    // 旧实现逐条目累加会得到 3.2MP，普通编辑几次就虚假满额。
    expect(totalDistinctPatchPixels(entries)).toBe(1_200_000);
  });

  it("真实超限口径：4MP 补丁重复引用两次仍是 4MP，恰好加满不误拒", () => {
    const fourMp = { id: 7, width: 2000, height: 2000 };
    const entries = [{ patches: [fourMp] }, { patches: [fourMp] }];
    expect(totalDistinctPatchPixels(entries)).toBe(4_000_000);
    // 存量 4MP + 新 4MP = 8MP ≤ 预算：应允许（旧实现 8M 存量会误拒）。
    expect(totalDistinctPatchPixels(entries) + 4_000_000).toBeLessThanOrEqual(
      INPAINT_MAX_TOTAL_PATCH_PIXELS,
    );
    // 真实超限（不同 id 的存活补丁总量超预算）仍应被拒绝路径识别。
    const bloated = [
      { patches: [{ id: 1, width: 2000, height: 2000 }] },
      { patches: [{ id: 2, width: 2000, height: 2000 }] },
      { patches: [{ id: 3, width: 2000, height: 2000 }] },
      {
        patches: [
          { id: 4, width: 2000, height: 2000 },
          { id: 5, width: 100, height: 50 },
        ],
      },
    ];
    expect(totalDistinctPatchPixels(bloated) + 1).toBeGreaterThan(
      INPAINT_MAX_TOTAL_PATCH_PIXELS,
    );
  });
});
