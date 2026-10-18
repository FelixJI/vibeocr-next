import { describe, expect, it } from "vitest";
import { compositionExclusions } from "./compositionExclusions";
import { DEFAULT_COMPOSITION } from "./canvasComposition";
import type { EditorState, Mark } from "./annotationGeometry";
const asset = "22222222222222222222222222222222";
const image = { naturalWidth: 80, naturalHeight: 40 } as HTMLImageElement;
const display = { width: 80, height: 40 };
const assets = new Map([
  [asset, { naturalWidth: 20, naturalHeight: 10 } as HTMLImageElement],
]);
function exclude(
  id: number,
  x: number,
  y: number,
  width: number,
  height: number,
): Mark {
  return {
    id: `mark-${id}`,
    tool: "exclude",
    start: { x, y },
    end: { x: x + width, y: y + height },
  };
}
function imported(x: number, y: number, width = 20, height = 10): Mark {
  return {
    id: "mark-100",
    tool: "image",
    assetId: asset,
    start: { x, y },
    end: { x: x + width, y: y + height },
  };
}
describe("composition exclusion projection", () => {
  it("shares magnifier leakage with plain and composed output, even when its source is cut away", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [
        exclude(1, 42, 15, 4, 10),
        {
          id: "mark-2",
          tool: "magnifier",
          start: { x: 30, y: 10 },
          end: { x: 70, y: 30 },
          zoom: 2,
        },
      ],
    };
    expect(compositionExclusions(image, state, display).pixels).toEqual([
      { x: 42, y: 15, width: 4, height: 10 },
      { x: 34, y: 10, width: 8, height: 20 },
    ]);
    const composed = compositionExclusions(
      image,
      {
        ...state,
        marks: [...state.marks, imported(80, 0)],
        composition: {
          ...DEFAULT_COMPOSITION,
          margin: 10,
          stitch: "horizontal",
          assetIds: [asset],
          cut: { axis: "vertical", start: 50, end: 75 },
        },
      },
      display,
      assets,
    );
    expect(composed.size).toEqual({ width: 100, height: 60 });
    expect(composed.pixels).toEqual([{ x: 44, y: 20, width: 6, height: 20 }]);
    expect(composed.fullyMasked).toBe(false);
    expect(composed.normalized[0]).toEqual({
      x: 440,
      y: 1000 / 3,
      width: 60,
      height: 1000 / 3,
    });
  });

  it("refuses over-budget leakage before upload even without composition", () => {
    const large = { naturalWidth: 800, naturalHeight: 400 } as HTMLImageElement;
    const marks: Mark[] = Array.from({ length: 64 }, (_, i) =>
      exclude(i + 1, i * 10, 0, 1, 400),
    );
    marks.push({
      id: "mark-100",
      tool: "magnifier",
      start: { x: 0, y: 0 },
      end: { x: 800, y: 400 },
      zoom: 2,
    });
    expect(() =>
      compositionExclusions(
        large,
        { rotation: 0, marks },
        { width: 800, height: 400 },
      ),
    ).toThrow(/64/);
  });

  it("compresses snapped source pixels through a middle cut and offsets margins", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [exclude(1, 10, 0, 60, 40)],
      composition: {
        ...DEFAULT_COMPOSITION,
        margin: 10,
        cut: { axis: "vertical", start: 25, end: 75 },
      },
    };
    const result = compositionExclusions(image, state, display);
    expect(result.size).toEqual({ width: 60, height: 60 });
    expect(result.pixels).toEqual([{ x: 20, y: 10, width: 20, height: 40 }]);
    expect(result.normalized[0]!.x).toBeCloseTo(1000 / 3);
    expect(result.fullyMasked).toBe(false);
  });
  it("does not reject an unmasked stitched image when the base is fully masked", () => {
    const state: EditorState = {
      rotation: 0,
      marks: [exclude(1, 0, 0, 80, 40), imported(90, 0)],
      composition: {
        ...DEFAULT_COMPOSITION,
        stitch: "horizontal",
        assetIds: [asset],
      },
    };
    const result = compositionExclusions(image, state, display, assets);
    expect(result.fullyMasked).toBe(false);
    expect(result.pixels).toEqual([{ x: 0, y: 0, width: 80, height: 40 }]);
  });
  it("propagates positive overlap to reused assets, but touching borders do not count", () => {
    const composition = {
      ...DEFAULT_COMPOSITION,
      stitch: "horizontal" as const,
      assetIds: [asset],
    };
    const overlapping = compositionExclusions(
      image,
      {
        rotation: 0,
        marks: [exclude(1, 20, 10, 20, 10), imported(30, 10)],
        composition,
      },
      display,
      assets,
    );
    expect(overlapping.pixels).toContainEqual({
      x: 80,
      y: 15,
      width: 20,
      height: 10,
    });
    const touching = compositionExclusions(
      image,
      {
        rotation: 0,
        marks: [exclude(1, 20, 10, 20, 10), imported(40, 10)],
        composition,
      },
      display,
      assets,
    );
    expect(touching.pixels).toHaveLength(1);
    const background = compositionExclusions(
      image,
      {
        rotation: 0,
        marks: [exclude(1, 20, 10, 20, 10), imported(30, 10)],
        composition: {
          ...composition,
          background: "image",
          backgroundAssetId: asset,
        },
      },
      display,
      assets,
    );
    expect(background.fullyMasked).toBe(true);
  });
  it("keeps normalized endpoints in range for odd dimensions and refuses over 64 mapped boxes", () => {
    const oddImage = { naturalWidth: 13, naturalHeight: 7 } as HTMLImageElement;
    const result = compositionExclusions(
      oddImage,
      {
        rotation: 0,
        marks: [exclude(1, 0, 0, 13, 7)],
        composition: { ...DEFAULT_COMPOSITION, margin: 1, ratio: "16:9" },
      },
      { width: 13, height: 7 },
    );
    for (const rect of result.normalized) {
      expect(rect.x + rect.width).toBeLessThanOrEqual(1000);
      expect(rect.y + rect.height).toBeLessThanOrEqual(1000);
      expect(rect.x).toBeGreaterThanOrEqual(0);
    }
    const largeImage = {
      naturalWidth: 128,
      naturalHeight: 2,
    } as HTMLImageElement;
    const marks = Array.from({ length: 64 }, (_, index) =>
      exclude(index + 1, index * 2, 0, 1, 2),
    );
    marks.push(imported(0, 0, 1, 1));
    expect(() =>
      compositionExclusions(
        largeImage,
        {
          rotation: 0,
          marks,
          composition: {
            ...DEFAULT_COMPOSITION,
            stitch: "horizontal",
            assetIds: [asset],
          },
        },
        { width: 128, height: 2 },
        assets,
      ),
    ).toThrow(/64/);
  });
});
