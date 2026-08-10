import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { App, type AppActions, type AppViewState } from "./App";

Object.assign(globalThis, { NodeFilter: { FILTER_SKIP: 3 } });

describe("AppShell", () => {
  it("lets the user navigate to QR tools and clearly gates an unavailable batch export", async () => {
    window.location.hash = "#/recognition";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: false,
      revision: 0,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.file", "qrcode.generate"],
      features: {},
      runtimeLabel: "运行时已就绪",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    await user.click(screen.getByRole("link", { name: "二维码" }));
    expect(actions.navigate).toHaveBeenCalledWith("qrcode");
    expect(
      await screen.findByRole("heading", { name: "二维码工作台" }),
    ).toBeVisible();

    await user.click(screen.getByRole("link", { name: "批量识别" }));
    expect(
      await screen.findByRole("heading", { name: "批量识别" }),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "导出全部 Markdown" }),
    ).toBeDisabled();
    expect(screen.getByText("此功能需要宿主能力：batch.export")).toBeVisible();

    unmount();
    await new Promise((resolve) => setTimeout(resolve, 0));
  });

  it("follows an external host route revision", async () => {
    window.location.hash = "#/recognition";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const initial: AppViewState = {
      connected: true,
      revision: 1,
      route: "recognition",
      theme: "system",
      capabilities: [],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { rerender, unmount } = render(
      <App actions={actions} viewState={initial} />,
    );

    rerender(
      <App
        actions={actions}
        viewState={{ ...initial, revision: 2, route: "pdf" }}
      />,
    );

    expect(
      await screen.findByRole("heading", { name: "PDF 工作台" }),
    ).toBeVisible();
    expect(window.location.hash).toBe("#/pdf");
    unmount();
  });

  it("sends Chinese QR content as a typed action payload", async () => {
    window.location.hash = "#/qrcode";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: false,
      revision: 0,
      route: "qrcode",
      theme: "light",
      capabilities: ["qrcode.generate"],
      features: {},
      runtimeLabel: "演示模式",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    await user.type(screen.getByLabelText("输入内容"), "中文识别结果");
    await user.click(screen.getByRole("button", { name: "生成二维码" }));

    expect(actions.run).toHaveBeenCalledWith({
      type: "qrcode.generate",
      text: "中文识别结果",
    });
    unmount();
  });

  it("shows a path-free batch queue and lets the user reorder it", async () => {
    window.location.hash = "#/batch";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 5,
      route: "batch",
      theme: "light",
      capabilities: ["batch.add", "batch.run", "batch.export"],
      features: {
        batch: {
          isRunning: false,
          itemCount: 2,
          completedCount: 1,
          failedCount: 0,
          concurrency: 2,
          items: [
            {
              id: "11111111-1111-1111-1111-111111111111",
              name: "发票一.png",
              statusCode: "batch.item.completed",
              resultSummary: "合计 42 元",
            },
            {
              id: "22222222-2222-2222-2222-222222222222",
              name: "发票二.png",
              statusCode: "batch.item.pending",
              resultSummary: null,
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    expect(screen.getByText("发票一.png")).toBeVisible();
    expect(screen.getByText("合计 42 元")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "下移 发票一.png" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "batch.moveItem",
      itemId: "11111111-1111-1111-1111-111111111111",
      delta: 1,
    });
    unmount();
  });

  it("selects PDF thumbnails before applying page commands", async () => {
    window.location.hash = "#/pdf";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 8,
      route: "pdf",
      theme: "light",
      capabilities: ["pdf.open", "pdf.rotate", "pdf.edit", "pdf.save"],
      features: {
        pdf: {
          pageCount: 2,
          selectedPage: 0,
          selectedPages: [0],
          pages: [
            {
              index: 0,
              statusCode: "pdf.page.done",
              thumbnail: {
                url: "https://app.vibeocr/__resource/first",
                mediaType: "image/png",
                byteLength: 120,
              },
            },
            {
              index: 1,
              statusCode: "pdf.page.none",
              thumbnail: {
                url: "https://app.vibeocr/__resource/second",
                mediaType: "image/png",
                byteLength: 140,
              },
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    await user.click(screen.getByRole("checkbox", { name: "选择第 2 页" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "pdf.selectPages",
      pages: [0, 1],
    });
    expect(screen.getByRole("img", { name: "第 2 页缩略图" })).toHaveAttribute(
      "src",
      "https://app.vibeocr/__resource/second",
    );
    unmount();
  });

  it("opens only QR results marked as web URLs", async () => {
    window.location.hash = "#/qrcode";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 9,
      route: "qrcode",
      theme: "light",
      capabilities: ["qrcode.decode", "qrcode.openUrl"],
      features: {
        qrcode: {
          items: [
            { data: "https://example.test", format: "QR_CODE", isUrl: true },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    await user.click(screen.getByRole("tab", { name: "识别" }));
    await user.click(screen.getByRole("button", { name: "打开链接" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "qrcode.openUrl",
      url: "https://example.test",
    });
    unmount();
  });

  it("provides local canvas rotate and undo tools for the broker image", async () => {
    window.location.hash = "#/recognition";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 10,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.file"],
      features: {
        recognition: {
          input: {
            url: "https://app.vibeocr/__resource/input",
            mediaType: "image/png",
            byteLength: 500,
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    const undo = screen.getByRole("button", { name: "撤销" });
    expect(undo).toBeDisabled();
    expect(screen.getByRole("button", { name: "椭圆" })).toBeVisible();
    expect(screen.getByRole("button", { name: "马赛克" })).toBeVisible();
    expect(screen.getByRole("button", { name: "模糊" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "文字" }));
    expect(screen.getByRole("textbox", { name: "标注文字" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "旋转 90°" }));
    expect(undo).toBeEnabled();
    await user.click(undo);
    expect(undo).toBeDisabled();
    unmount();
  });

  it("shows QR busy state immediately and lets the user cancel", async () => {
    window.location.hash = "#/qrcode";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 11,
      route: "qrcode",
      theme: "light",
      capabilities: ["qrcode.generate"],
      features: {
        qrcode: { isBusy: true, statusCode: "qrcode.running", items: [] },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    const cancel = screen.getByRole("button", { name: "取消任务" });
    expect(cancel).toBeEnabled();
    await user.click(cancel);
    expect(actions.run).toHaveBeenCalledWith({ type: "qrcode.cancel" });
    unmount();
  });

  it("pages through bounded batch and PDF windows", async () => {
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const base: AppViewState = {
      connected: true,
      revision: 12,
      route: "batch",
      theme: "light",
      capabilities: ["batch.run"],
      features: {
        batch: {
          itemCount: 80,
          windowStart: 0,
          items: [
            {
              id: "11111111-1111-1111-1111-111111111111",
              name: "第一页.png",
              statusCode: "batch.item.pending",
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { rerender, unmount } = render(
      <App actions={actions} viewState={base} />,
    );
    await user.click(screen.getByRole("button", { name: "下一组" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "batch.setWindow",
      start: 40,
    });

    window.location.hash = "#/pdf";
    rerender(
      <App
        actions={actions}
        viewState={{
          ...base,
          revision: 13,
          route: "pdf",
          capabilities: ["pdf.open"],
          features: {
            pdf: {
              pageCount: 130,
              windowStart: 0,
              pages: [{ index: 0, statusCode: "pdf.page.none" }],
            },
          },
        }}
      />,
    );
    await user.click(await screen.findByRole("button", { name: "下一组" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "pdf.setWindow",
      start: 64,
    });
    unmount();
  });

  it("shows host About metadata and opens the fixed project page", async () => {
    window.location.hash = "#/about";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 14,
      route: "about",
      theme: "light",
      capabilities: ["about.openProject"],
      features: {
        about: {
          version: "0.2.0",
          license: "Proprietary",
          projectUrl: "https://github.com/felji/VibeOCR",
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    expect(screen.getByText("0.2.0")).toBeVisible();
    expect(screen.getByText("Proprietary")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "打开项目主页" }));
    expect(actions.run).toHaveBeenCalledWith({ type: "about.openProject" });
    unmount();
  });
});
