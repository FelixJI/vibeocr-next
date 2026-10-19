import katex from "katex";
import { useEffect, useRef } from "react";

import "katex/dist/katex.min.css";

import { formulaRenderRequest, postFormulaReport } from "./formulaRender";

/** #238 公式位图化（C5）：独立授权渲染页。LaTeX 经 hash fragment
 * （base64url，≤4KiB）传入；KaTeX 渲染后把公式元素的 CSS 矩形与
 * devicePixelRatio 上报宿主，由宿主 CapturePreview→解码校验→裁剪。
 * 渲染失败上报 formula.renderFailed；源文本始终由宿主持有，不插坏图。 */

export function FormulaRenderPage() {
  const hostRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const request = formulaRenderRequest(window.location.hash);
    const host = hostRef.current;
    if (request === undefined || host === null) {
      if (request !== undefined)
        postFormulaReport({
          type: "formula.renderFailed",
          id: request.id,
          payload: { reason: "decode" },
        });
      return;
    }
    let markup: string;
    try {
      markup = katex.renderToString(request.latex, {
        displayMode: true,
        throwOnError: true,
      });
    } catch (error) {
      postFormulaReport({
        type: "formula.renderFailed",
        id: request.id,
        payload: {
          reason: "latex",
          message:
            error instanceof Error ? error.message.slice(0, 200) : "latex",
        },
      });
      return;
    }
    host.innerHTML = markup;
    const finish = () => {
      const element = host.firstElementChild;
      if (element === null) {
        postFormulaReport({
          type: "formula.renderFailed",
          id: request.id,
          payload: { reason: "empty" },
        });
        return;
      }
      const rect = element.getBoundingClientRect();
      if (
        !Number.isFinite(rect.x) ||
        !Number.isFinite(rect.y) ||
        rect.width <= 0 ||
        rect.height <= 0
      ) {
        postFormulaReport({
          type: "formula.renderFailed",
          id: request.id,
          payload: { reason: "geometry" },
        });
        return;
      }
      postFormulaReport({
        type: "formula.rendered",
        id: request.id,
        payload: {
          rect: {
            x: rect.x,
            y: rect.y,
            width: rect.width,
            height: rect.height,
          },
          viewportWidth: window.innerWidth,
          viewportHeight: window.innerHeight,
          devicePixelRatio: window.devicePixelRatio || 1,
        },
      });
    };
    // 字体与两帧布局稳定后再量测，避免截到半布局。
    void document.fonts.ready.then(() => {
      requestAnimationFrame(() => requestAnimationFrame(finish));
    });
  }, []);

  return (
    <div
      id="formula-render-host"
      ref={hostRef}
      style={{
        position: "fixed",
        inset: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        background: "#ffffff",
        fontSize: "24px",
        overflow: "hidden",
      }}
      aria-label="公式渲染"
    />
  );
}
