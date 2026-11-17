import { describe, expect, it } from "vitest";

import {
  PRODUCT_RUNTIME_DAMAGED_LABEL,
  PRODUCT_RUNTIME_DAMAGED_REASON,
  availabilityLabel,
  isProductRuntimeDamaged,
  modeAvailabilityLabel,
} from "./FeatureValues";

describe("#321 product runtime damage projection", () => {
  it("keeps generic availability labels for other reasons", () => {
    expect(availabilityLabel("unavailable")).toBe("不可用");
    expect(modeAvailabilityLabel("unavailable", undefined)).toBe("不可用");
    expect(modeAvailabilityLabel("unavailable", null)).toBe("不可用");
    expect(modeAvailabilityLabel("unavailable", "engine_not_installed")).toBe(
      "不可用",
    );
    expect(isProductRuntimeDamaged("engine_not_installed")).toBe(false);
  });

  it("surfaces product damage without online preparation wording", () => {
    expect(isProductRuntimeDamaged(PRODUCT_RUNTIME_DAMAGED_REASON)).toBe(true);
    const label = modeAvailabilityLabel(
      "unavailable",
      PRODUCT_RUNTIME_DAMAGED_REASON,
    );
    expect(label).toBe(PRODUCT_RUNTIME_DAMAGED_LABEL);
    expect(label).toContain("随包运行组件损坏");
    expect(label).not.toContain("重新选择");
    expect(label).not.toContain("需准备依赖");
  });
});
