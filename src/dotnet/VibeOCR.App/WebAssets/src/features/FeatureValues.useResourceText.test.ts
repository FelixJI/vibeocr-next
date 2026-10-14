import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useResourceText, type ResourceReference } from "./FeatureValues";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const reference = (url: string): ResourceReference => ({
  url,
  mediaType: "text/plain",
  byteLength: 4,
});

/**
 * 手动放行响应的 fetch stub：模拟宿主在结果 A→B 原子替换后，A 的
 * 响应（成功或失败）仍可能迟到——现实中的慢响应竞态，abort 也无法
 * 保证拦截。
 */
function deferredFetch() {
  const pending = new Map<
    string,
    Array<{
      resolve: (response: Response) => void;
      reject: (error: unknown) => void;
    }>
  >();
  vi.stubGlobal(
    "fetch",
    (input: RequestInfo | URL) =>
      new Promise<Response>((resolve, reject) => {
        const queue = pending.get(String(input)) ?? [];
        queue.push({ resolve, reject });
        pending.set(String(input), queue);
      }),
  );
  const settle = (url: string) => {
    const queue = pending.get(url) ?? [];
    pending.delete(url);
    return queue;
  };
  return {
    respond(url: string, body: string) {
      for (const { resolve } of settle(url))
        resolve(new Response(body, { status: 200 }));
    },
    fail(url: string) {
      for (const { reject } of settle(url)) reject(new Error("network down"));
    },
  };
}

describe("useResourceText binds text to the resource url", () => {
  it("never renders the previous resource text while the new one loads (A→B)", async () => {
    const deferred = deferredFetch();
    // 记录每次渲染的返回值：B 的首帧（effect 尚未运行、内部文本仍是 A）
    // 也必须返回空串；仅在 effect 里清空旧文本的方案会在这里露馅。
    const seen: string[] = [];
    const { rerender } = renderHook(
      (ref: ResourceReference | undefined) => {
        const text = useResourceText(ref);
        seen.push(text);
        return text;
      },
      { initialProps: reference("https://host/a.json") },
    );
    expect(seen).toEqual([""]);
    await act(async () => {
      deferred.respond("https://host/a.json", "结果A");
    });
    expect(seen).toEqual(["", "结果A"]);

    // A→B：B 加载完成前的每一帧渲染（含首帧与 effect 之后）都不得再
    // 出现 A 的文本。
    rerender(reference("https://host/b.json"));
    expect(seen.slice(2)).toEqual([""]);
    await act(async () => {
      deferred.respond("https://host/b.json", "结果B");
    });
    expect(seen[seen.length - 1]).toBe("结果B");
  });

  it("clears the text when the resource reference becomes undefined", async () => {
    const deferred = deferredFetch();
    const seen: string[] = [];
    const { rerender } = renderHook(
      (ref: ResourceReference | undefined) => {
        const text = useResourceText(ref);
        seen.push(text);
        return text;
      },
      { initialProps: reference("https://host/a.json") },
    );
    await act(async () => {
      deferred.respond("https://host/a.json", "结果A");
    });
    rerender(undefined);
    expect(seen[seen.length - 1]).toBe("");
  });

  it("ignores a late response of a replaced resource (reverse order)", async () => {
    const deferred = deferredFetch();
    const { result, rerender } = renderHook(
      (ref: ResourceReference | undefined) => useResourceText(ref),
      { initialProps: reference("https://host/a.json") },
    );
    rerender(reference("https://host/b.json"));
    await act(async () => {
      deferred.respond("https://host/b.json", "结果B");
    });
    expect(result.current).toBe("结果B");
    // A 的响应迟到（晚于 B 完成）：不得覆写 B 的文本。
    await act(async () => {
      deferred.respond("https://host/a.json", "迟到A");
    });
    expect(result.current).toBe("结果B");
  });

  it("ignores a late failure of a replaced resource", async () => {
    const deferred = deferredFetch();
    const { result, rerender } = renderHook(
      (ref: ResourceReference | undefined) => useResourceText(ref),
      { initialProps: reference("https://host/a.json") },
    );
    rerender(reference("https://host/b.json"));
    await act(async () => {
      deferred.respond("https://host/b.json", "结果B");
    });
    // A 的请求迟到失败：错误文案不得顶掉 B 的已加载文本。
    await act(async () => {
      deferred.fail("https://host/a.json");
    });
    expect(result.current).toBe("结果B");
  });
});
