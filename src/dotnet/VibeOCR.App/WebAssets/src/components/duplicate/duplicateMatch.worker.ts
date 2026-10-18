import {
  DuplicateError,
  findDuplicateRegions,
  type DuplicateRequest,
  type DuplicateResponse,
} from "./duplicateMatch";

/**
 * 重复匹配 worker 薄入口：与修补 worker 同构。
 *
 *   new Worker(new URL("./duplicateMatch.worker.ts", import.meta.url), {
 *     type: "module",
 *   });
 *
 * - rgba 以 transfer 传入（调用方副本）；worker 只读，无额外像素副本。
 * - 响应携带 generation；取消由调用方 terminate() 完成。
 */

const scope = self as unknown as {
  postMessage(message: DuplicateResponse): void;
};

self.addEventListener("message", (event: MessageEvent<DuplicateRequest>) => {
  const request = event.data;
  const generation =
    typeof request?.generation === "number" ? request.generation : -1;
  try {
    if (!(request.rgba instanceof ArrayBuffer)) {
      throw new DuplicateError(
        "invalid-image",
        "请求必须 transfer 一个 ArrayBuffer 像素副本",
      );
    }
    const candidates = findDuplicateRegions(
      new Uint8ClampedArray(request.rgba),
      request.width,
      request.height,
      request.rect,
    );
    const response: DuplicateResponse = {
      generation,
      ok: true,
      candidates,
    };
    scope.postMessage(response);
  } catch (error) {
    const code = error instanceof DuplicateError ? error.code : "unexpected";
    const message = error instanceof Error ? error.message : String(error);
    scope.postMessage({
      generation,
      ok: false,
      error: { code, message },
    });
  }
});
