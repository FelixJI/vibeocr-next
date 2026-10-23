import { describe, expect, it } from "vitest";
import { editorTextBox } from "./editorTextLayerGeometry";
import type { EditorState } from "./annotationGeometry";

const image = { naturalWidth: 1000, naturalHeight: 500 } as HTMLImageElement;
const display = { width: 1000, height: 1000 };
const viewport = { width: 500, height: 500 };

describe("编辑画布文字层逆投影", () => {
  it.each([8, 10, 12])("对象包围盒外 %ipx 的文字必须避让手柄", (gap) => {
    const frame = { width: 1000, height: 500 };
    const state: EditorState = {
      rotation: 0,
      marks: [
        {
          id: "rectangle",
          tool: "rectangle",
          start: { x: 300, y: 100 },
          end: { x: 500, y: 300 },
          style: { strokeWidth: 2 },
        },
      ],
    };
    // 2px 描边的右外沿是 x=501；行框覆盖右侧手柄的中心 y=200。
    expect(
      editorTextBox(
        [501 + gap, 380, 521 + gap, 420],
        image,
        state,
        frame,
        frame,
      ),
    ).toBeUndefined();
  });
  it("对象包围盒外 13px 的文字仍可选择", () => {
    const frame = { width: 1000, height: 500 };
    expect(
      editorTextBox(
        [514, 380, 534, 420],
        image,
        {
          rotation: 0,
          marks: [
            {
              id: "rectangle",
              tool: "rectangle",
              start: { x: 300, y: 100 },
              end: { x: 500, y: 300 },
              style: { strokeWidth: 2 },
            },
          ],
        },
        frame,
        frame,
      ),
    ).toBeDefined();
  });
  it.each([8, 10, 12])("裁剪手柄的 %ipx 命中边界内文字必须避让", (gap) => {
    const frame = { width: 800, height: 400 };
    const source = {
      naturalWidth: 800,
      naturalHeight: 400,
    } as HTMLImageElement;
    expect(
      editorTextBox(
        [450, (gap * 1000) / 400, 550, ((gap + 20) * 1000) / 400],
        source,
        { rotation: 0, marks: [] },
        frame,
        frame,
        undefined,
        undefined,
        true,
      ),
    ).toBeUndefined();
  });
  it("裁剪手柄命中范围外的文字仍可选择", () => {
    const frame = { width: 800, height: 400 };
    expect(
      editorTextBox(
        [450, 32.5, 550, 82.5],
        { naturalWidth: 800, naturalHeight: 400 } as HTMLImageElement,
        { rotation: 0, marks: [] },
        frame,
        frame,
        undefined,
        undefined,
        true,
      ),
    ).toBeDefined();
  });
  it("初始截图选区即裁剪基准，顶边中央文字不能拦截裁剪手柄", () => {
    const frame = { width: 800, height: 400 };
    const source = {
      naturalWidth: 800,
      naturalHeight: 400,
    } as HTMLImageElement;
    expect(
      editorTextBox(
        [450, 0, 550, 50],
        source,
        { rotation: 0, marks: [] },
        frame,
        frame,
        undefined,
        undefined,
        true,
      ),
    ).toBeUndefined();
    expect(
      editorTextBox(
        [450, 100, 550, 150],
        source,
        { rotation: 0, marks: [] },
        frame,
        frame,
        undefined,
        undefined,
        true,
      ),
    ).toBeDefined();
  });
  it("保留留白与缩放，标注覆盖行时对象手势优先", () => {
    const state: EditorState = { rotation: 0, marks: [] };
    expect(
      editorTextBox([100, 100, 300, 200], image, state, display, viewport),
    ).toEqual({ left: 50, top: 150, width: 100, height: 25 });
    const marked: EditorState = {
      ...state,
      marks: [
        {
          id: "box",
          tool: "rectangle",
          start: { x: 90, y: 290 },
          end: { x: 310, y: 360 },
        },
      ],
    };
    expect(
      editorTextBox([100, 100, 300, 200], image, marked, display, viewport),
    ).toBeUndefined();
    expect(
      editorTextBox([600, 700, 900, 800], image, marked, display, viewport),
    ).toBeDefined();
  });
  it("旋转后的当前输出仍映射到对应画布区域", () => {
    expect(
      editorTextBox(
        [100, 100, 300, 200],
        image,
        { rotation: 90, marks: [] },
        display,
        viewport,
      ),
    ).toEqual({ left: 150, top: 50, width: 50, height: 50 });
  });
});
