import { beforeEach, describe, expect, it, vi } from "vitest";

import type { EditorState, Mark } from "../annotationGeometry";
import { validateProjectRestore } from "../ImageCanvasEditor";
import {
  buildContributionState,
  contributionStateBytes,
  countReferencedAssets,
  patchEntryBudget,
  parseProjectManifest,
  parseProjectOpen,
  restoreEditorState,
  uploadProjectContribution,
  type WireEditorState,
} from "./projectWire";

/** 全字段样例标记：覆盖样式全集（含连续 intensity/brushSize）、资产引用、
 * 组/锁/旋转/翻转/缩放、轨迹点与文字宽度。 */
const fullMark: Mark = {
  id: "mark-7",
  tool: "text",
  start: { x: 12.5, y: 30 },
  end: { x: 220.25, y: 88.5 },
  text: "标题文字",
  textWidth: 180.5,
  style: {
    color: "#112233",
    strokeWidth: 3.5,
    fontSize: 22.5,
    intensity: 1.7,
    brushSize: 42,
    strokeOpacity: 65,
    fill: { color: "#abcdef", opacity: 40, enabled: true },
    lineStyle: "dashed",
    cornerRadius: 6.5,
    arrowStyle: "hollow",
    arrowDouble: true,
    fontFamily: "Segoe UI",
    fontBold: true,
    fontItalic: true,
    fontUnderline: true,
    textAlign: "center",
    textBackground: { color: "#ffffff", opacity: 80 },
    textOutline: { color: "#000000", width: 2.5 },
    numberFormat: "upper",
    numberShape: "circle",
    calloutStyle: "bubble",
  },
  ordinal: 3,
  groupId: "group-4",
  locked: true,
  rotation: 90,
  flipX: true,
  flipY: false,
  objectScale: 1.25,
};

const state: EditorState = {
  rotation: 90,
  marks: [
    fullMark,
    {
      id: "mark-8",
      tool: "pen",
      start: { x: 1, y: 2 },
      end: { x: 30, y: 40 },
      points: [
        { x: 1, y: 2 },
        { x: 15, y: 20 },
        { x: 30, y: 40 },
      ],
    },
  ],
  crop: { start: { x: 5, y: 6 }, end: { x: 700, y: 500 } },
  resize: { width: 3200, height: 1800 },
  patches: [
    { id: 1, x: 10, y: 20, width: 30, height: 40 },
    { id: 2, x: 100, y: 200, width: 60, height: 20 },
  ],
};

describe("project contribution serialization", () => {
  it("serializes every editable field and keeps patch descriptors out of state", () => {
    const body = buildContributionState({ width: 900, height: 600 }, state);
    expect(body.canvas).toEqual({ width: 900, height: 600 });
    expect(body.state.rotation).toBe(90);
    expect(body.state.marks).toHaveLength(2);
    const wire = body.state.marks[0]!;
    expect(wire.id).toBe("mark-7");
    expect(wire.tool).toBe("text");
    expect(wire.start).toEqual({ x: 12.5, y: 30 });
    expect(wire.text).toBe("标题文字");
    expect(wire.textWidth).toBe(180.5);
    expect(wire.ordinal).toBe(3);
    expect(wire.groupId).toBe("group-4");
    expect(wire.locked).toBe(true);
    expect(wire.rotation).toBe(90);
    expect(wire.flipX).toBe(true);
    expect(wire.flipY).toBe(false);
    expect(wire.objectScale).toBe(1.25);
    expect(wire.style).toEqual(fullMark.style);
    expect(body.state.marks[1]!.points).toHaveLength(3);
    expect(body.state.crop).toEqual(state.crop);
    expect(body.state.resize).toEqual(state.resize);
    // 宿主 /__project 契约：state.patches 必须为空数组（描述符唯一真源在
    // 上传 patches 数组；违反即 403）。
    expect(body.state.patches).toEqual([]);
    expect(body.patches).toEqual([]);
  });

  it("save → restore → save is value-exact for scene frames and quarter turns", () => {
    // 逻辑 frame 与默认 900×600 不同（scene 尺寸）且带 90° 旋转：保存→
    // 恢复→再保存必须逐值等价（无重投影/无样式缩放）。宿主组装后的
    // manifest state 携带从上传 patches 重建的补丁描述符。
    const sceneCanvas = { width: 1200, height: 700 };
    const body = buildContributionState(sceneCanvas, state);
    const manifestState: WireEditorState = {
      ...body.state,
      patches: state.patches ?? [],
    };
    const restored = restoreEditorState(manifestState, {
      naturalSize: { width: 1600, height: 900 },
      savedCanvas: sceneCanvas,
      displayCanvas: sceneCanvas,
      rotation: manifestState.rotation,
    });
    expect(restored.marks).toEqual(state.marks);
    expect(restored.crop).toEqual(state.crop);
    expect(restored.resize).toEqual(state.resize);
    expect(restored.patches).toEqual(state.patches);
    expect(restored.rotation).toBe(90);
    const second = buildContributionState(sceneCanvas, restored);
    expect(JSON.stringify(second.state)).toBe(JSON.stringify(body.state));
    // 关键尺寸类字段逐字等价：textWidth/笔刷宽/对象 scale 不被缩放。
    expect(second.state.marks[0]!.textWidth).toBe(180.5);
    expect(second.state.marks[0]!.style!.brushSize).toBe(42);
    expect(second.state.marks[0]!.objectScale).toBe(1.25);
  });

  it("restore adopts the saved frame without reprojection across contexts", () => {
    const body = buildContributionState({ width: 1200, height: 700 }, state);
    const manifestState: WireEditorState = {
      ...body.state,
      patches: state.patches ?? [],
    };
    // 默认 900×600 编辑页：恢复采纳 saved canvas 为逻辑 frame，值不变。
    const restored = restoreEditorState(manifestState, {
      naturalSize: { width: 1600, height: 900 },
      savedCanvas: { width: 1200, height: 700 },
      displayCanvas: { width: 900, height: 600 },
      rotation: 90,
    });
    expect(restored.marks).toEqual(state.marks);
    expect(restored.marks[0]!.style!.fontSize).toBe(
      state.marks[0]!.style!.fontSize,
    );
    for (const mark of restored.marks) {
      expect(Number.isFinite(mark.start.x)).toBe(true);
      expect(Number.isFinite(mark.end.y)).toBe(true);
    }
  });

  it("counts the serialized state bytes for the 256KiB budget", () => {
    const body = buildContributionState({ width: 900, height: 600 }, state);
    expect(contributionStateBytes(body)).toBeLessThan(256 * 1024);
    expect(contributionStateBytes(body)).toBeGreaterThan(100);
  });
});

describe("project entry budget", () => {
  it("derives the patch budget from uniquely referenced assets", () => {
    // 共享 asset 引用只计一次：多个 image 标记引用同一资产。
    const marks = [
      { assetId: "a".repeat(32) },
      { assetId: "a".repeat(32) },
      { assetId: "b".repeat(32) },
      {},
      { assetId: undefined },
    ];
    expect(countReferencedAssets(marks)).toBe(2);
    expect(patchEntryBudget(0)).toBe(126);
    expect(patchEntryBudget(16)).toBe(110);
    // 16 资产 + 110 补丁 = 128 恰好允许；17 资产时预算 109 块。
    expect(2 + 16 + 110).toBeLessThanOrEqual(128);
    expect(2 + 16 + 111).toBeGreaterThan(128);
    expect(patchEntryBudget(17)).toBe(109);
  });
});

describe("project open projection parsing", () => {
  it("accepts a complete projection and rejects malformed ones", () => {
    const valid = parseProjectOpen({
      pendingId: "a".repeat(32),
      sessionId: "0".repeat(32),
      revision: 0,
      generation: 4,
      manifest: {
        url: "https://app.vibeocr/__resource/m",
        mediaType: "application/json; charset=utf-8",
        byteLength: 1024,
      },
      source: {
        url: "https://app.vibeocr/__resource/s",
        mediaType: "image/png",
        byteLength: 4096,
      },
      assets: [
        {
          id: "b".repeat(32),
          resource: {
            url: "https://app.vibeocr/__resource/a",
            mediaType: "image/png",
            byteLength: 512,
          },
        },
      ],
      patches: [
        {
          id: 1,
          resource: {
            url: "https://app.vibeocr/__resource/p",
            mediaType: "image/png",
            byteLength: 64,
          },
        },
      ],
    });
    expect(valid?.pendingId).toBe("a".repeat(32));
    expect(valid?.assets[0]?.id).toBe("b".repeat(32));
    expect(parseProjectOpen({ pendingId: "x" })).toBeUndefined();
    expect(parseProjectOpen(undefined)).toBeUndefined();
  });
});

describe("project manifest parsing", () => {
  const manifest = {
    schemaVersion: 1,
    canvas: { width: 900, height: 600 },
    source: {
      mediaType: "image/png",
      width: 1600,
      height: 900,
      length: 4096,
    },
    assets: [],
    state: {
      rotation: 0,
      marks: [],
      patches: [{ id: 1, x: 1, y: 2, width: 3, height: 4 }],
    },
  };

  it("parses a v1 manifest", () => {
    const parsed = parseProjectManifest(manifest);
    expect(parsed.canvas).toEqual({ width: 900, height: 600 });
    expect(parsed.state.patches).toEqual([
      { id: 1, x: 1, y: 2, width: 3, height: 4 },
    ]);
  });

  it("rejects unsupported schema versions and broken structure", () => {
    expect(() =>
      parseProjectManifest({ ...manifest, schemaVersion: 2 }),
    ).toThrow("不支持的工程格式版本");
    expect(() =>
      parseProjectManifest({
        ...manifest,
        state: {
          ...manifest.state,
          patches: [{ id: 1, x: 1.5, y: 2, width: 3, height: 4 }],
        },
      }),
    ).toThrow("工程修补数据不完整");
  });

  it("rejects canvas and resize over pixel budgets before any allocation", () => {
    // 巨大逻辑 canvas：任何资源 fetch/decode 前拒绝（元数据层）。
    expect(() =>
      parseProjectManifest({
        ...manifest,
        canvas: { width: 32768, height: 32768 },
      }),
    ).toThrow("工程数据结构不完整");
    expect(() =>
      parseProjectManifest({
        ...manifest,
        canvas: { width: 8000, height: 8001 },
      }),
    ).toThrow("工程数据结构不完整");
    // 边界 8000×8000 恰好 64MP：接受。
    expect(() =>
      parseProjectManifest({
        ...manifest,
        canvas: { width: 8000, height: 8000 },
      }),
    ).not.toThrow();
    // 手造巨大 resize：单边超 16384 或总量超 64MP 拒绝。
    expect(() =>
      parseProjectManifest({
        ...manifest,
        state: {
          ...manifest.state,
          resize: { width: 32768, height: 32768 },
        },
      }),
    ).toThrow("工程输出尺寸超出上限");
    expect(() =>
      parseProjectManifest({
        ...manifest,
        state: {
          ...manifest.state,
          resize: { width: 16385, height: 2 },
        },
      }),
    ).toThrow("工程输出尺寸超出上限");
    // 边界 8000×8000 允许。
    expect(() =>
      parseProjectManifest({
        ...manifest,
        state: {
          ...manifest.state,
          resize: { width: 8000, height: 8000 },
        },
      }),
    ).not.toThrow();
  });
});

describe("project contribution upload", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("returns the contribute id for a 201 response", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ contributeId: "c".repeat(32) }), {
        status: 201,
      }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const id = await uploadProjectContribution({
      canvas: { width: 900, height: 600 },
      state: { rotation: 0, marks: [], patches: [] },
      patches: [],
    });
    expect(id).toBe("c".repeat(32));
    expect(fetchMock).toHaveBeenCalledWith("/__project", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: expect.any(String),
    });
  });

  it("fails honestly for refused and malformed responses", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response("no", { status: 403 })),
    );
    await expect(
      uploadProjectContribution({
        canvas: { width: 900, height: 600 },
        state: { rotation: 0, marks: [], patches: [] },
        patches: [],
      }),
    ).rejects.toThrow("工程贡献上传被拒绝");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(
          new Response(JSON.stringify({ contributeId: "zz" }), { status: 201 }),
        ),
    );
    await expect(
      uploadProjectContribution({
        canvas: { width: 900, height: 600 },
        state: { rotation: 0, marks: [], patches: [] },
        patches: [],
      }),
    ).rejects.toThrow("工程贡献上传响应无效");
  });
});

describe("wire editor state shape", () => {
  it("keeps patches in natural unrotated integer space", () => {
    const wire: WireEditorState = {
      rotation: 270,
      marks: [],
      patches: [{ id: 9, x: 0, y: 0, width: 10, height: 5 }],
    };
    expect(wire.patches[0]!.width).toBe(10);
  });
});

describe("#235 聚光灯/放大镜线形状", () => {
  const baseManifest = {
    schemaVersion: 1,
    canvas: { width: 900, height: 600 },
    source: {
      mediaType: "image/png",
      width: 900,
      height: 600,
      length: 4096,
    },
    assets: [],
    state: {
      rotation: 0,
      marks: [
        {
          id: "mark-1",
          tool: "spotlight",
          start: { x: 4, y: 6 },
          end: { x: 40, y: 30 },
          regions: [
            { x: 4, y: 6, width: 36, height: 24 },
            { x: 100, y: 80, width: 20, height: 10 },
          ],
          style: { fill: { color: "#000000", opacity: 55 } },
        },
        {
          id: "mark-2",
          tool: "magnifier",
          start: { x: 8, y: 8 },
          end: { x: 88, y: 68 },
          zoom: 4,
        },
      ],
      patches: [],
    },
  } as const;

  it("parses spotlight regions and magnifier zoom, and round-trips marks", () => {
    const parsed = parseProjectManifest(baseManifest);
    const spotlight = parsed.state.marks[0]!;
    expect(spotlight.tool).toBe("spotlight");
    expect(spotlight.regions).toHaveLength(2);
    expect(spotlight.regions![1]).toEqual({
      x: 100,
      y: 80,
      width: 20,
      height: 10,
    });
    expect(parsed.state.marks[1]!.zoom).toBe(4);

    // 保存采集与恢复映射：区域坐标原样、倍率不变（比例不随缩放变化）。
    const contribution = buildContributionState(
      { width: 900, height: 600 },
      {
        rotation: 0,
        marks: [
          {
            id: "mark-1",
            tool: "spotlight",
            start: { x: 4, y: 6 },
            end: { x: 40, y: 30 },
            regions: [
              { x: 4, y: 6, width: 36, height: 24 },
              { x: 100, y: 80, width: 20, height: 10 },
            ],
            style: { fill: { color: "#000000", opacity: 55 } },
          },
          {
            id: "mark-2",
            tool: "magnifier",
            start: { x: 8, y: 8 },
            end: { x: 88, y: 68 },
            zoom: 4,
          },
        ],
      },
    );
    expect(contribution.state.marks[0]!.regions).toEqual([
      { x: 4, y: 6, width: 36, height: 24 },
      { x: 100, y: 80, width: 20, height: 10 },
    ]);
    expect(contribution.state.marks[1]!.zoom).toBe(4);
    const restored = restoreEditorState(contribution.state, {
      naturalSize: { width: 900, height: 600 },
      savedCanvas: { width: 900, height: 600 },
      displayCanvas: { width: 900, height: 600 },
      rotation: 0,
    });
    expect(restored.marks[0]!.regions).toEqual([
      { x: 4, y: 6, width: 36, height: 24 },
      { x: 100, y: 80, width: 20, height: 10 },
    ]);
    expect(restored.marks[1]!.zoom).toBe(4);
  });

  it("fails closed on misplaced or out-of-bound regions/zoom", () => {
    // 区域被非 spotlight 工具携带：拒绝。
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-1",
              tool: "rectangle",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
              regions: [{ x: 0, y: 0, width: 5, height: 5 }],
            },
          ],
        },
      }),
    ).toThrow("工程对象数据不完整");
    // 空区域集/超过 16 个区域：拒绝。
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-1",
              tool: "spotlight",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
              regions: [],
            },
          ],
        },
      }),
    ).toThrow("工程对象数据不完整");
    // 零尺寸区域（width/height = 0）：加载后会压暗全图且无孔洞，
    // 双侧（TS/C#）严格 > 0 拒绝；合法小正数可读（不把画布 8px 阈值
    // 强加到自然像素工程）。
    for (const degenerate of [
      { x: 4, y: 6, width: 0, height: 12 },
      { x: 4, y: 6, width: 12, height: 0 },
    ]) {
      expect(() =>
        parseProjectManifest({
          ...baseManifest,
          state: {
            ...baseManifest.state,
            marks: [
              {
                id: "mark-1",
                tool: "spotlight",
                start: { x: 0, y: 0 },
                end: { x: 9, y: 9 },
                regions: [degenerate],
              },
            ],
          },
        }),
      ).toThrow("工程对象数据不完整");
    }
    // 合法小正数区域可读。
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-1",
              tool: "spotlight",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
              regions: [{ x: 4, y: 6, width: 0.25, height: 0.5 }],
            },
          ],
        },
      }),
    ).not.toThrow();
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-1",
              tool: "spotlight",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
              regions: Array.from({ length: 17 }, (_, index) => ({
                x: index,
                y: 0,
                width: 2,
                height: 2,
              })),
            },
          ],
        },
      }),
    ).toThrow("工程对象数据不完整");
    // 倍率越界/被非 magnifier 工具携带：拒绝。
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-2",
              tool: "magnifier",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
              zoom: 12,
            },
          ],
        },
      }),
    ).toThrow("工程对象数据不完整");
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-2",
              tool: "ellipse",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
              zoom: 2,
            },
          ],
        },
      }),
    ).toThrow("工程对象数据不完整");
    // 旧工程（无新字段）仍可解析。
    expect(() =>
      parseProjectManifest({
        ...baseManifest,
        state: {
          ...baseManifest.state,
          marks: [
            {
              id: "mark-3",
              tool: "rectangle",
              start: { x: 0, y: 0 },
              end: { x: 9, y: 9 },
            },
          ],
        },
      }),
    ).not.toThrow();
  });
});

describe("ack 前预算校验", () => {
  it("rejects giant canvas/resize metadata before install", () => {
    const image = new Image();
    Object.defineProperty(image, "naturalWidth", { value: 1600 });
    Object.defineProperty(image, "naturalHeight", { value: 900 });
    const state: WireEditorState = { rotation: 0, marks: [], patches: [] };
    expect(() =>
      validateProjectRestore(image, { width: 32768, height: 32768 }, state),
    ).toThrow("逻辑画布超出 64MP");
    expect(() =>
      validateProjectRestore(
        image,
        { width: 900, height: 600 },
        { ...state, resize: { width: 16385, height: 2 } },
      ),
    ).toThrow("工程输出尺寸超出上限");
    expect(() =>
      validateProjectRestore(
        image,
        { width: 900, height: 600 },
        { ...state, resize: { width: 8000, height: 8000 } },
      ),
    ).not.toThrow();
  });

  it("accepts a modest brush and rejects an over-budget restore", () => {
    const image = new Image();
    Object.defineProperty(image, "naturalWidth", { value: 20_000 });
    Object.defineProperty(image, "naturalHeight", { value: 15_000 });
    const small: WireEditorState = {
      rotation: 0,
      marks: [
        {
          id: "mark-1",
          tool: "mosaic",
          start: { x: 10, y: 10 },
          end: { x: 60, y: 60 },
          points: [
            { x: 10, y: 10 },
            { x: 60, y: 60 },
          ],
          style: { brushSize: 20 },
        },
      ],
      patches: [],
    };
    expect(() =>
      validateProjectRestore(image, { width: 900, height: 600 }, small),
    ).not.toThrow();
    // 巨幅自然图 + 全画布轨迹：8MP 暂存预算必须拒绝（ack 前失败）。
    const huge: WireEditorState = {
      rotation: 0,
      marks: [
        {
          id: "mark-1",
          tool: "mosaic",
          start: { x: 0, y: 0 },
          end: { x: 899, y: 599 },
          points: Array.from({ length: 4096 }, (_, index) => ({
            x: (index * 37) % 900,
            y: (index * 53) % 600,
          })),
        },
      ],
      patches: [],
    };
    expect(() =>
      validateProjectRestore(image, { width: 900, height: 600 }, huge),
    ).toThrow("超出当前预算");
  });
});
