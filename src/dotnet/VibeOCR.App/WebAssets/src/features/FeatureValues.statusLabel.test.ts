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

/**
 * 更新状态契约：宿主侧 UpdateViewModel 会发出的每个状态码都必须有中文
 * 文案，不得落回到“等待宿主连接”式的误导性 fallback（受限网络机器上
 * 检查失败的 update.error 曾被展示成宿主桥未连接）。
 */
describe("statusLabel update", () => {
  const updateCodes = [
    "update.unchecked",
    "update.checking",
    "update.available",
    "update.current",
    "update.error",
    "update.cancelled",
    "update.downloaded",
    "update.applying",
    "update.runtimeBusy",
    "update.waitingRuntime",
    "update.runtimeReleased",
  ] as const;

  it.each(updateCodes)("为 %s 提供非空中文文案", (code) => {
    const label = statusLabel(code, "更新状态等待宿主连接。");
    expect(label).not.toBe("更新状态等待宿主连接。");
    expect(label.trim().length).toBeGreaterThan(3);
  });

  it("未检查状态不得显示为最新版本", () => {
    expect(statusLabel("update.unchecked", "fallback")).not.toContain("最新");
    expect(statusLabel("update.unchecked", "fallback")).toContain("未检查");
  });

  it("未知更新状态码仍回落调用方文案", () => {
    expect(statusLabel("update.unknown", "更新状态尚未同步。")).toBe(
      "更新状态尚未同步。",
    );
  });
});
