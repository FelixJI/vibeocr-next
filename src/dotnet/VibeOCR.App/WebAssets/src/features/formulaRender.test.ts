import { describe, expect, it } from "vitest";
import { decodeFormulaSource, formulaRenderRequest } from "./formulaRender";

function encode(source: string): string {
  return btoa(String.fromCharCode(...new TextEncoder().encode(source)))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}

describe("controlled formula source", () => {
  it("roundtrips Unicode LaTeX and binds the render identity", () => {
    const latex = "\\frac{中文}{x^2}";
    expect(decodeFormulaSource(encode(latex))).toBe(latex);
    expect(
      formulaRenderRequest(
        `#/imageEdit?formula=1&render=${"a".repeat(32)}&src=${encode(latex)}`,
      ),
    ).toEqual({ id: "a".repeat(32), latex });
  });
  it("rejects oversized UTF8, invalid encoding and unbound requests", () => {
    expect(decodeFormulaSource(encode("中".repeat(1366)))).toBeUndefined();
    expect(decodeFormulaSource("_w")).toBeUndefined();
    expect(
      formulaRenderRequest("#/imageEdit?formula=1&render=bad&src=eA"),
    ).toBeUndefined();
    expect(formulaRenderRequest("#/imageEdit?formula=1")).toBeUndefined();
  });
});
