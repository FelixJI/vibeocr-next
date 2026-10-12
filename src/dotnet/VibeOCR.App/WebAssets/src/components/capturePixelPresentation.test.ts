import { afterEach, describe, expect, it, vi } from "vitest";
import { CapturePixelPresentation } from "./capturePixelPresentation";

afterEach(() => vi.unstubAllGlobals());

function frames() {
  const queued = new Map<number, FrameRequestCallback>();
  let next = 0;
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    queued.set(++next, callback);
    return next;
  });
  vi.stubGlobal("cancelAnimationFrame", (id: number) => queued.delete(id));
  return {
    queued,
    flush: () => {
      const callbacks = [...queued.values()];
      queued.clear();
      callbacks.forEach((callback) => callback(0));
    },
  };
}
function background(decode: () => Promise<void> = () => Promise.resolve()) {
  return {
    complete: true,
    naturalWidth: 3840,
    naturalHeight: 2160,
    decode,
  } as HTMLImageElement;
}

describe("capture pixel presentation", () => {
  it("waits for both real source drawing and backdrop decode, then one later frame", async () => {
    const harness = frames();
    const report = vi.fn();
    let decoded!: () => void;
    const attempt = new CapturePixelPresentation(report);
    const loading = attempt.backgroundLoaded(
      background(
        () =>
          new Promise((resolve) => {
            decoded = resolve;
          }),
      ),
    );
    attempt.sourceDrawn();
    harness.flush();
    expect(report).not.toHaveBeenCalled();
    decoded();
    await loading;
    expect(report).not.toHaveBeenCalled();
    expect(harness.queued.size).toBe(1);
    harness.flush();
    attempt.sourceDrawn();
    await attempt.backgroundLoaded(background());
    harness.flush();
    expect(report).toHaveBeenCalledTimes(1);
    expect(report).toHaveBeenCalledWith();
  });

  it("cancels queued frames and late decode completions when its source/document retires", async () => {
    const harness = frames();
    const report = vi.fn();
    const queued = new CapturePixelPresentation(report);
    queued.sourceDrawn();
    await queued.backgroundLoaded(background());
    expect(harness.queued.size).toBe(1);
    queued.dispose();
    expect(harness.queued.size).toBe(0);
    harness.flush();
    let decode!: () => void;
    const late = new CapturePixelPresentation(report);
    const loading = late.backgroundLoaded(
      background(
        () =>
          new Promise((resolve) => {
            decode = resolve;
          }),
      ),
    );
    late.sourceDrawn();
    late.dispose();
    decode();
    await loading;
    harness.flush();
    expect(report).not.toHaveBeenCalled();
  });

  it("emits one bounded failure and never a ready after decode/draw failure", async () => {
    const harness = frames();
    const report = vi.fn();
    const attempt = new CapturePixelPresentation(report);
    attempt.sourceDrawn();
    await attempt.backgroundLoaded(
      background(() => Promise.reject(new Error("decoder failed"))),
    );
    attempt.fail("draw");
    harness.flush();
    expect(report).toHaveBeenCalledExactlyOnceWith("backgroundDecode");
  });
});
