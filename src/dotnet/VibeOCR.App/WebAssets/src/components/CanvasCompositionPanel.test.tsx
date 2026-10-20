import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CanvasCompositionPanel } from "./CanvasCompositionPanel";
import {
  DEFAULT_COMPOSITION,
  type CanvasComposition,
} from "./canvasComposition";

const assets = ["a", "b", "c", "d"].map((id) => ({ id, label: id }));
const pendingPreview = () => new Promise<Blob>(() => {});

afterEach(cleanup);

describe("背景图与拼接子集同步", () => {
  it.each([
    {
      label: "排版背景",
      selected: "image",
      initial: { background: "transparent", assetIds: ["c", "a", "b"] },
      expected: {
        background: "image",
        backgroundAssetId: "a",
        backgroundStitchIndex: 1,
        assetIds: ["c", "b"],
      },
    },
    {
      label: "排版背景图",
      selected: "c",
      initial: {
        background: "image",
        backgroundAssetId: "a",
        backgroundStitchIndex: 1,
        assetIds: ["c", "b"],
      },
      expected: {
        background: "image",
        backgroundAssetId: "c",
        backgroundStitchIndex: 0,
        assetIds: ["a", "b"],
      },
    },
    {
      label: "排版背景",
      selected: "solid",
      initial: {
        background: "image",
        backgroundAssetId: "a",
        backgroundStitchIndex: 1,
        assetIds: ["c", "b"],
      },
      expected: {
        background: "solid",
        backgroundAssetId: undefined,
        backgroundStitchIndex: undefined,
        assetIds: ["c", "a", "b"],
      },
    },
    {
      label: "排版背景图",
      selected: "c",
      initial: { background: "image", backgroundAssetId: "a", assetIds: ["b"] },
      expected: {
        background: "image",
        backgroundAssetId: "c",
        backgroundStitchIndex: undefined,
        assetIds: ["b"],
      },
    },
    {
      label: "排版背景",
      selected: "solid",
      initial: { background: "image", backgroundAssetId: "a", assetIds: ["b"] },
      expected: {
        background: "solid",
        backgroundAssetId: undefined,
        backgroundStitchIndex: undefined,
        assetIds: ["b"],
      },
    },
    {
      label: "多图拼接",
      selected: "horizontal",
      initial: {
        background: "image",
        backgroundAssetId: "a",
        backgroundStitchIndex: 1,
        assetIds: ["b"],
      },
      expected: {
        background: "image",
        backgroundAssetId: "a",
        backgroundStitchIndex: 0,
        assetIds: ["b", "c", "d"],
      },
    },
    {
      label: "排版背景",
      selected: "transparent",
      initial: {
        background: "image",
        backgroundAssetId: "b",
        backgroundStitchIndex: 0,
        assetIds: ["c"],
      },
      expected: {
        background: "transparent",
        backgroundAssetId: undefined,
        backgroundStitchIndex: undefined,
        assetIds: ["b", "c"],
      },
    },
  ] as const)(
    "$label → $selected 只更新背景相关项",
    ({ label, selected, initial, expected }) => {
      const onChange = vi.fn();
      render(
        <CanvasCompositionPanel
          value={
            {
              ...DEFAULT_COMPOSITION,
              stitch: "horizontal",
              ...initial,
            } as CanvasComposition
          }
          assets={assets}
          onChange={onChange}
          render={pendingPreview}
          revision={0}
        />,
      );
      fireEvent.change(screen.getByLabelText(label, { exact: true }), {
        target: { value: selected },
      });
      expect(onChange).toHaveBeenCalledExactlyOnceWith({
        ...DEFAULT_COMPOSITION,
        stitch: "horizontal",
        ...expected,
      });
    },
  );
});
