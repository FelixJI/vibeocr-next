import {
  InpaintError,
  inpaintMask,
  inpaintRectangle,
  isInpaintMaskRequest,
  type InpaintRequest,
  type InpaintResponse,
} from "./inpaint";

/**
 * 修补 worker 薄入口：主线程经 vite 的 same-origin module worker 接入：
 *
 *   new Worker(new URL("./inpaint.worker.ts", import.meta.url), {
 *     type: "module",
 *   });
 *
 * - rgba（及掩膜请求的 mask）以 transfer 传入，避免复制；调用方必须传
 *   副本，transfer 后其原 buffer 被 detach。
 * - 结果 buffer 同样 transfer 回传；请求携带 generation，响应原样回带，
 *   迟到响应由 UI 丢弃。
 * - 掩膜请求（含 mask: ArrayBuffer）走 inpaintMask：只改掩膜内 RGB，
 *   洞与 ROI 外字节原样保留；矩形请求保持旧 inpaintRectangle 语义。
 * - 取消由调用方 worker.terminate() 立即完成；worker 内不做任务调度，
 *   逐条处理消息。
 */

const scope = self as unknown as {
  postMessage(message: InpaintResponse, transfer: Transferable[]): void;
};

self.addEventListener("message", (event: MessageEvent<InpaintRequest>) => {
  const request = event.data;
  const generation =
    typeof request?.generation === "number" ? request.generation : -1;
  try {
    if (!(request.rgba instanceof ArrayBuffer)) {
      throw new InpaintError(
        "invalid-image",
        "请求必须 transfer 一个 ArrayBuffer 像素副本",
      );
    }
    const pixels = new Uint8ClampedArray(request.rgba);
    const out = isInpaintMaskRequest(request)
      ? inpaintMask(
          pixels,
          request.width,
          request.height,
          request.rect,
          new Uint8Array(request.mask),
        )
      : inpaintRectangle(pixels, request.width, request.height, request.rect);
    const response: InpaintResponse = {
      generation,
      ok: true,
      rgba: out.buffer,
      width: request.width,
      height: request.height,
    };
    scope.postMessage(response, [out.buffer]);
  } catch (error) {
    const code = error instanceof InpaintError ? error.code : "unexpected";
    const message = error instanceof Error ? error.message : String(error);
    const response: InpaintResponse = {
      generation,
      ok: false,
      error: { code, message },
    };
    scope.postMessage(response, []);
  }
});
