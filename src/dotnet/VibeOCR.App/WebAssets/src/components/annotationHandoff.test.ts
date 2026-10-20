import { afterEach, expect, it, vi } from "vitest";
import { discardAnnotatedImage } from "./annotationHandoff";
afterEach(() => vi.unstubAllGlobals());
it("discards only an opaque same-origin annotation URI and reports refusal", async () => {
  const uri = "https://app.vibeocr/__annotation/" + "a".repeat(32);
  const fetch = vi.fn().mockResolvedValue({ ok: true });
  vi.stubGlobal("fetch", fetch);
  await expect(discardAnnotatedImage(uri)).resolves.toBeUndefined();
  expect(fetch).toHaveBeenCalledExactlyOnceWith(new URL(uri).pathname, {
    method: "DELETE",
  });
  fetch.mockResolvedValue({ ok: false });
  await expect(discardAnnotatedImage(uri)).rejects.toThrow("释放失败");
  fetch.mockClear();
  await expect(
    discardAnnotatedImage("https://other.test/__annotation/" + "a".repeat(32)),
  ).rejects.toThrow("invalid");
  expect(fetch).not.toHaveBeenCalled();
});
