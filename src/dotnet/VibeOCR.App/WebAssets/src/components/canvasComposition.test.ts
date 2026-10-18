import { rotateEditorState } from "./annotationGeometry";
import { validateProjectRestore } from "./ImageCanvasEditor";
import { describe, expect, it } from "vitest";
import {
  compositionSize,
  DEFAULT_COMPOSITION,
  parseComposition,
  boundedCanvasSize,
} from "./canvasComposition";
import {
  buildContributionState,
  restoreEditorState,
} from "./project/projectWire";

describe("non-destructive canvas composition", () => {
  it("computes bounded horizontal/vertical stitching, margins and fixed aspect", () => {
    expect(
      compositionSize(
        [
          { width: 80, height: 40 },
          { width: 20, height: 30 },
        ],
        { ...DEFAULT_COMPOSITION, stitch: "horizontal", margin: 10 },
      ),
    ).toEqual({ width: 120, height: 60 });
    expect(
      compositionSize(
        [
          { width: 80, height: 40 },
          { width: 20, height: 30 },
        ],
        { ...DEFAULT_COMPOSITION, stitch: "vertical", margin: 10 },
      ),
    ).toEqual({ width: 100, height: 90 });
    expect(
      compositionSize([{ width: 80, height: 40 }], {
        ...DEFAULT_COMPOSITION,
        ratio: "1:1",
        margin: 10,
      }),
    ).toEqual({ width: 100, height: 100 });
    expect(() => boundedCanvasSize(16384, 16384)).toThrow(/64MP/);
    expect(() => boundedCanvasSize(Number.NaN, 10)).toThrow();
  });
  it("keeps composition editable through project roundtrip and preserves original objects", () => {
    const composition = {
      ...DEFAULT_COMPOSITION,
      margin: 20,
      background: "gradient" as const,
      cut: { axis: "horizontal" as const, start: 25, end: 50 },
    };
    const original = { rotation: 0, marks: [], composition };
    const size = { width: 900, height: 600 };
    const body = buildContributionState(size, original);
    expect(body.state.composition).toEqual(composition);
    const restored = restoreEditorState(body.state, {
      naturalSize: size,
      savedCanvas: size,
      displayCanvas: size,
      rotation: 0,
    });
    expect(restored.composition).toEqual(composition);
    expect(original.marks).toEqual([]);
    expect(parseComposition(undefined)).toBeUndefined();
  });
  it("rejects unknown formats, malformed resource ids, invalid cuts and unbounded values", () => {
    for (const patch of [
      { future: true },
      { background: "future" },
      { margin: Infinity },
      { assetIds: ["file:///private.png"] },
      { cut: { axis: "horizontal", start: 0, end: 100 } },
    ])
      expect(() =>
        parseComposition({ ...DEFAULT_COMPOSITION, ...patch }),
      ).toThrow();
  });
});

it("rejects an unrenderable project composition before session acknowledgement", () => {
  const image = { naturalWidth: 8000, naturalHeight: 8000 } as HTMLImageElement;
  expect(() =>
    validateProjectRestore(
      image,
      { width: 900, height: 600 },
      {
        rotation: 0,
        marks: [],
        patches: [],
        composition: { ...DEFAULT_COMPOSITION, margin: 1000 },
      },
    ),
  ).toThrow(/64MP/);
});

it("retains editable composition when rotating the source image", () => {
  const composition = {
    ...DEFAULT_COMPOSITION,
    margin: 12,
    stitch: "horizontal" as const,
  };
  const image = { naturalWidth: 80, naturalHeight: 40 } as HTMLImageElement;
  const rotated = rotateEditorState(
    { rotation: 0, marks: [], composition },
    image,
    { width: 900, height: 600 },
    90,
  );
  expect(rotated.rotation).toBe(90);
  expect(rotated.composition).toEqual(composition);
});
