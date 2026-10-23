import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import {
  afterEach,
  afterAll,
  beforeAll,
  describe,
  expect,
  it,
  vi,
} from "vitest";
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

describe("预览 blob URL 生命周期", () => {
  // CI 回归（PR #269）：换渲染/撤销重做会重触发预览 effect；旧实现在 cleanup
  // 里立即撤销当前 URL，而 img.src 仍指向它直到下一次渲染提交并加载完成，
  // 窗口期内 <img>.decode() 抛 EncodingError（e2e previewPixels 随机红）。
  // 不变量：仍被 DOM 引用（或尚未被新预览加载取代）的 URL 不得撤销。
  const revoked: string[] = [];
  const created: string[] = [];
  let seq = 0;
  beforeAll(() => {
    URL.createObjectURL = (() => {
      const url = `blob:preview-${++seq}`;
      created.push(url);
      return url;
    }) as typeof URL.createObjectURL;
    URL.revokeObjectURL = (url: string) => {
      revoked.push(url);
    };
  });
  afterEach(() => {
    // 每个用例自包含：上一个用例（尤其断言中止后）的撤销/创建记录不串扰。
    revoked.length = 0;
    created.length = 0;
  });
  afterAll(() => {
    delete (URL as { createObjectURL?: unknown }).createObjectURL;
    delete (URL as { revokeObjectURL?: unknown }).revokeObjectURL;
    revoked.length = 0;
  });

  const mount = (
    value: CanvasComposition,
    renderPreview: () => Promise<Blob>,
    revision: number,
    onChange = vi.fn(),
  ) => (
    <CanvasCompositionPanel
      value={value}
      assets={assets}
      onChange={onChange}
      render={renderPreview}
      revision={revision}
    />
  );

  it("重渲染挂起期间不撤销仍显示的预览 URL，新预览加载后才撤销旧 URL", async () => {
    const first = {
      ...DEFAULT_COMPOSITION,
      stitch: "horizontal",
    } as CanvasComposition;
    let resolveFirst!: (blob: Blob) => void;
    const { rerender } = render(
      mount(
        first,
        () => new Promise<Blob>((resolve) => (resolveFirst = resolve)),
        0,
      ),
    );
    resolveFirst(new Blob(["a"]));
    const img = await screen.findByAltText("排版输出预览");
    await waitFor(() => expect(img.getAttribute("src")).toBe("blob:preview-1"));
    // 依赖变化且新渲染挂起：此刻旧 URL 仍被 img 引用，不得撤销（旧实现红）。
    let resolveSecond!: (blob: Blob) => void;
    rerender(
      mount(
        { ...first, margin: 5 },
        () => new Promise<Blob>((resolve) => (resolveSecond = resolve)),
        1,
      ),
    );
    expect(revoked).toEqual([]);
    resolveSecond(new Blob(["b"]));
    await waitFor(() => expect(img.getAttribute("src")).toBe("blob:preview-2"));
    // 新 URL 已提交但尚未 load：React 提交与浏览器加载存在时序，旧 URL 仍不得撤销。
    expect(revoked).toEqual([]);
    fireEvent.load(img);
    expect(revoked).toEqual(["blob:preview-1"]);
    cleanup();
    // 卸载后当前 URL 必须撤销，不泄漏。
    expect(revoked).toEqual(["blob:preview-1", "blob:preview-2"]);
  });

  it("渲染失败时撤销当前预览 URL，不泄漏", async () => {
    const first = {
      ...DEFAULT_COMPOSITION,
      stitch: "horizontal",
    } as CanvasComposition;
    let resolveFirst!: (blob: Blob) => void;
    const { rerender } = render(
      mount(
        first,
        () => new Promise<Blob>((resolve) => (resolveFirst = resolve)),
        0,
      ),
    );
    resolveFirst(new Blob(["a"]));
    const img = await screen.findByAltText("排版输出预览");
    await waitFor(() => expect(img.getAttribute("src")).toBe(created.at(-1)));
    rerender(
      mount(
        { ...first, margin: 5 },
        () => Promise.reject(new Error("排版渲染失败")),
        1,
      ),
    );
    await waitFor(() => expect(screen.getByRole("alert")).toBeTruthy());
    expect(img).not.toBeInTheDocument();
    expect(revoked).toEqual([created.at(-1)]);
  });
});
