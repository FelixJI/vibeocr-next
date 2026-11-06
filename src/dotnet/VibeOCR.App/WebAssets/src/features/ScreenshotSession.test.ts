import { describe, expect, it } from "vitest";

import { imageRecognitionModes } from "./ScreenshotSession";

const readyModes = [
  { modeId: "paddle_text", displayName: "文字识别", availability: "ready" },
  { modeId: "paddle_table", displayName: "表格识别", availability: "ready" },
];

describe("imageRecognitionModes", () => {
  it("reads numeric intent from the native camel-case projection", () => {
    const cold = JSON.parse(
      '[{"modeId":"","displayName":"文字识别","availability":"catalog_unavailable","intent":0},{"modeId":"","displayName":"表格识别","availability":"catalog_unavailable","intent":1}]',
    );
    expect(imageRecognitionModes(cold)).toEqual(cold);
    for (const intent of [2, -1, "Text", true])
      expect(imageRecognitionModes([{ ...cold[0], intent }, cold[1]])).toEqual(
        [],
      );
    expect(
      imageRecognitionModes([
        { ...cold[0], availability: "unavailable" },
        cold[1],
      ]),
    ).toEqual([]);
    expect(
      imageRecognitionModes([{ ...cold[0], modeId: "paddle_text" }, cold[1]]),
    ).toEqual([]);
  });

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
