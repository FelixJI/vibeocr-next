import { describe, expect, it } from "vitest";
import { statusLabel } from "./FeatureValues";

/**
 * #245 C3 契约：本地截图校验拒绝以稳定 captureRefused 状态码经既有
 * statusLabel 投影；每个原因码必须有可见的中文修复指引，且区别于通用
 * 识别失败（不误导 Runtime 依赖）。
 */
describe("statusLabel captureRefused", () => {
  const captureRefusedCodes = [
    "recognition.captureRefused.missingFixedRegion",
    "recognition.captureRefused.regionUnavailable",
    "recognition.captureRefused.windowUnavailable",
    "recognition.captureRefused.monitorUnavailable",
    "recognition.captureRefused.layoutChanged",
    "recognition.captureRefused.tooLarge",
  ] as const;

  it.each(captureRefusedCodes)("为 %s 提供非空修复指引", (code) => {
    const label = statusLabel(code, "fallback");
    expect(label).not.toBe("fallback");
    expect(label.trim().length).toBeGreaterThan(6);
  });

  it("指引不落入通用识别失败文案", () => {
    const generic = statusLabel("recognition.failed", "fallback");
    for (const code of captureRefusedCodes) {
      expect(statusLabel(code, "fallback")).not.toEqual(generic);
    }
  });

  it("未知状态码仍回落调用方文案", () => {
    expect(statusLabel("recognition.captureRefused.unknown", "fallback")).toBe(
      "fallback",
    );
  });
});
