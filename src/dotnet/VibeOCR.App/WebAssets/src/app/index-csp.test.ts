import html from "../../index.html?raw";

import { describe, expect, it } from "vitest";

// 读取真实入口，守住打包页面的 CSP 与同源资源边界。
describe("packaged workbench document", () => {
  it("permits only same-origin broker resources", () => {
    expect(html).toMatch(/default-src 'none'/);
    expect(html).toMatch(/script-src 'self'/);
    expect(html).toMatch(/connect-src 'self'/);
    expect(html).toMatch(/img-src 'self' blob:/);
    expect(html).not.toMatch(/img-src[^;]*data:/);
    expect(html).not.toMatch(/unsafe-inline|unsafe-eval/);
    expect(html).not.toMatch(/<script(?![^>]*\bsrc=)[^>]*>/i);
    expect(html).not.toMatch(/https:\/\/(?!app\.vibeocr)/i);
  });
});
