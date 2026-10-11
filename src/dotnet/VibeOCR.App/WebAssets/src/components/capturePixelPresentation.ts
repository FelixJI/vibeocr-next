import type { CapturePixelFailure } from "../bridge/captureScene";

/** One presentation attempt; cancellation owns every queued frame/decode continuation. */
export class CapturePixelPresentation {
  private drawn = false;
  private backgroundDecoded = false;
  private stopped = false;
  private frame?: number;

  constructor(
    private readonly report: (failure?: CapturePixelFailure) => void,
  ) {}

  sourceDrawn(): void {
    this.drawn = true;
    this.schedule();
  }

  async backgroundLoaded(image: HTMLImageElement): Promise<void> {
    try {
      if (typeof image.decode === "function") await image.decode();
      if (
        !image.complete ||
        image.naturalWidth <= 0 ||
        image.naturalHeight <= 0
      )
        throw new Error("Background has no decoded pixels.");
      if (this.stopped) return;
      this.backgroundDecoded = true;
      this.schedule();
    } catch {
      this.fail("backgroundDecode");
    }
  }

  fail(reason: CapturePixelFailure): void {
    if (this.stopped) return;
    this.stop();
    this.report(reason);
  }

  dispose(): void {
    this.stop();
  }

  private schedule(): void {
    if (
      this.stopped ||
      !this.drawn ||
      !this.backgroundDecoded ||
      this.frame !== undefined
    )
      return;
    // Runs after the frame that drew the source and after the decoded backdrop committed.
    this.frame = requestAnimationFrame(() => {
      this.frame = undefined;
      if (this.stopped) return;
      this.stop();
      this.report();
    });
  }

  private stop(): void {
    this.stopped = true;
    if (this.frame !== undefined) cancelAnimationFrame(this.frame);
    this.frame = undefined;
  }
}
