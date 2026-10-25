import { describe, expect, it } from "vitest";

import { imageRecognitionModes } from "./ScreenshotSession";

const readyModes = [
  { modeId: "rapid_text", displayName: "文字识别", availability: "ready" },
  { modeId: "paddle_table", displayName: "表格识别", availability: "ready" },
];

describe("imageRecognitionModes", () => {
  it("accepts the host's two retained recognition actions", () => {
    expect(imageRecognitionModes(readyModes)).toEqual(readyModes);
  });

  it.each([
    undefined,
    {},
    [],
    [readyModes[0]],
    [...readyModes, { ...readyModes[0], modeId: "paddle_formula" }],
    [readyModes[0], null],
    [readyModes[0], { ...readyModes[1], modeId: 42 }],
    [readyModes[0], { ...readyModes[1], displayName: null }],
    [readyModes[0], { ...readyModes[1], availability: undefined }],
  ])("rejects incomplete or malformed host projection %j", (value) => {
    expect(imageRecognitionModes(value)).toEqual([]);
  });
});
