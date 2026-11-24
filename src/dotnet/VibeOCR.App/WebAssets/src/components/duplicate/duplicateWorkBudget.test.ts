import { expect, it, vi } from "vitest";

import { findDuplicateRegions } from "./duplicateMatch";

vi.mock(import("./duplicateWorkBudget"), async (importOriginal) => {
  const budgets = await importOriginal();
  expect(budgets.DUPLICATE_WORK_BUDGET).toBe(96_000_000);
  return { ...budgets, DUPLICATE_WORK_BUDGET: 6432 };
});

it.each([
  [400, "未找到"],
  [401, "筛选工作量超过预算 6432"],
])("charges both RGB and alpha checks at image width %i", (width, message) => {
  const height = 100;
  const rgba = new Uint8ClampedArray(width * height * 4);
  for (let i = 3; i < rgba.length; i += 4) rgba[i] = 128;
  for (let y = 0; y < height; y += 25) {
    for (let x = 0; x < 100; x += 25) {
      const alpha = (y * width + x) * 4 + 3;
      rgba[alpha] = 255;
      rgba[alpha + 4] = 1;
    }
  }
  // 400px 时有 201 个不重叠起点，每个做 16 次 RGB 和 16 次 alpha 检查。
  // 分块和相同但首像素不同：恰好用完 6432 次时搜索完成，多一个起点就超限。
  expect(() =>
    findDuplicateRegions(rgba, width, height, {
      x: 0,
      y: 0,
      width: 100,
      height,
    }),
  ).toThrow(message);
});
