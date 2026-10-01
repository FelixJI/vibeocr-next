import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { describe, expect, it, vi } from "vitest";

import { App, type AppActions, type AppViewState } from "./App";

Object.assign(globalThis, { NodeFilter: { FILTER_SKIP: 3 } });

describe("AppShell", () => {
  it("starts scrolling capture without an OCR connection", async () => {
    window.location.hash = "#/recognition";
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
      capabilities: ["recognition.scrollCapture"],
      features: {},
      runtimeLabel: "未连接",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "长截图" }));
    expect(actions.run).toHaveBeenCalledExactlyOnceWith({
      type: "recognition.captureScrollingScreenshot",
    });
    unmount();
  });
  it("shows per-action hotkey state and drives apply, disable and reset", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 1,
      route: "settings",
      theme: "light",
      capabilities: ["settings.shell", "settings.hotkeys"],
      runtimeLabel: "运行时已就绪",
      features: {
        settings: {
          hotkeyActions: [
            {
              actionId: "screenshot_recognize",
              displayName: "快捷截图识别",
              configuredHotkey: "Ctrl+Alt+Q",
              registeredHotkey: null,
              error: "快捷键注册失败：该组合可能已被其他应用占用。",
              defaultHotkey: "Ctrl+Alt+Q",
            },
            {
              actionId: "clipboard_recognize",
              displayName: "剪贴板识别",
              configuredHotkey: null,
              registeredHotkey: null,
              error: null,
              defaultHotkey: null,
            },
          ],
        },
      },
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    // 配置已保存但未注册生效必须如实区分，不得冒称当前生效。
    expect(
      screen.getByText("已保存 Ctrl+Alt+Q，但当前未注册生效"),
    ).toBeVisible();
    expect(screen.getByText(/该组合可能已被其他应用占用/)).toBeVisible();
    expect(screen.getByText(/全局快捷键在系统任意位置可用/)).toBeVisible();

    await user.type(screen.getByLabelText("剪贴板识别新快捷键"), "Ctrl+Alt+C");
    await user.click(screen.getByRole("button", { name: "应用 剪贴板识别" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setActionHotkey",
      actionId: "clipboard_recognize",
      hotkey: "Ctrl+Alt+C",
    });

    // 未绑定的动作不提供“禁用”；已保存键位的动作可恢复默认。
    expect(
      screen.getByRole("button", { name: "禁用 剪贴板识别" }),
    ).toBeDisabled();
    await user.click(
      screen.getByRole("button", { name: "恢复默认 快捷截图识别" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.resetActionHotkey",
      actionId: "screenshot_recognize",
    });
    unmount();
  });
  it("drives the floating toolbar panel and describes its visibility state", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 2,
      route: "settings",
      theme: "light",
      capabilities: ["settings.floatingToolbar"],
      runtimeLabel: "运行时已就绪",
      features: {
        settings: {
          floatingToolbar: {
            enabled: true,
            edge: "top",
            autoHide: true,
            visibility: "userHidden",
          },
        },
      },
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    expect(
      screen.getByText(/当前状态：已主动隐藏（鼠标路过不恢复）/),
    ).toBeVisible();
    expect(
      screen.getByText(/可从本页、托盘菜单或“悬浮栏显示\/隐藏”快捷键找回/),
    ).toBeVisible();

    await user.click(screen.getByRole("button", { name: "显示" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.showFloatingToolbar",
    });
    await user.click(screen.getByRole("button", { name: "隐藏" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.hideFloatingToolbar",
    });
    await user.selectOptions(screen.getByLabelText("靠边位置"), "left");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFloatingToolbarLayout",
      edge: "left",
      autoHide: true,
    });
    await user.click(
      screen.getByRole("checkbox", { name: "鼠标离开后自动收起" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFloatingToolbarLayout",
      edge: "top",
      autoHide: false,
    });
    await user.click(screen.getByRole("checkbox", { name: "启用悬浮工具栏" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFloatingToolbarEnabled",
      enabled: false,
    });
    unmount();
  });
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
    expect(
      screen.getAllByText("此功能需要宿主能力：batch.export"),
    ).toHaveLength(3);

    unmount();
    await new Promise((resolve) => setTimeout(resolve, 0));
  });

  it("waits for connected host navigation before changing pages", async () => {
    window.location.hash = "#/recognition";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const initial: AppViewState = {
      connected: true,
      revision: 1,
      route: "recognition",
      theme: "light",
      capabilities: ["qrcode.generate"],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { rerender, unmount } = render(
      <App actions={actions} viewState={initial} />,
    );
    const push = vi.spyOn(window.history, "pushState");
    try {
      await user.click(screen.getByRole("link", { name: "二维码" }));
      expect(actions.navigate).toHaveBeenCalledWith("qrcode");
      expect(push).not.toHaveBeenCalled();
      expect(window.location.hash).toBe("#/recognition");
      rerender(
        <App
          actions={actions}
          viewState={{ ...initial, revision: 2, route: "qrcode" }}
        />,
      );
      expect(
        await screen.findByRole("heading", { name: "二维码工作台" }),
      ).toBeVisible();
    } finally {
      push.mockRestore();
      unmount();
    }
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
      capabilities: [
        "batch.add",
        "batch.run",
        "batch.export",
        "recognition.engine",
      ],
      features: {
        batch: {
          isRunning: false,
          itemCount: 2,
          completedCount: 1,
          failedCount: 0,
          engines: [
            {
              engine: "mineru_document",
              displayName: "MinerU 文档",
              selected: false,
              isTaskOverride: false,
              availability: "ready",
              requiresDownload: false,
            },
          ],
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
    expect(screen.getByText("批次由识别服务自动调度")).toBeVisible();
    expect(
      screen.queryByRole("combobox", { name: "批量并发数" }),
    ).not.toBeInTheDocument();
    await user.selectOptions(
      screen.getByLabelText("本次识别模式"),
      "mineru_document",
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "batch.setTaskEngine",
      engine: "mineru_document",
    });
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
      capabilities: ["recognition.file", "recognition.annotation"],
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
    expect(screen.getByRole("button", { name: "复制标注图" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "保存标注图" })).toBeEnabled();
    expect(
      screen.getByText(/马赛克.*会写入复制、保存副本及显式识别输入/),
    ).toBeVisible();
    expect(screen.getByLabelText("图片检查画布")).toHaveAttribute(
      "tabindex",
      "0",
    );
    await user.click(screen.getByRole("button", { name: "文字" }));
    expect(screen.getByRole("textbox", { name: "标注文字" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "旋转 90°" }));
    expect(undo).toBeEnabled();
    await user.click(undo);
    expect(undo).toBeDisabled();
    unmount();
  });

  it("shows failed recognition and rejected host commands without hiding them as idle", () => {
    window.location.hash = "#/recognition";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 11,
      route: "recognition",
      theme: "light",
      capabilities: [],
      features: { recognition: { statusCode: "recognition.failed" } },
      runtimeLabel: "原生宿主已连接",
      commandProblem: "workbench.error.desktopCommandFailed",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(screen.getByText("识别失败，请检查运行时状态后重试")).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "原生操作执行失败。请检查当前输入和运行时状态后重试。",
    );
    unmount();

    const cancelled = render(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          commandProblem: "workbench.error.annotationOperationCancelled",
        }}
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent(
      "未保存标注图片；原图和当前识别结果均未改变。",
    );
    cancelled.unmount();
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

  it("drives source and feature selection from settings state", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 20,
      route: "settings",
      theme: "light",
      capabilities: [
        "settings.shell",
        "settings.selection",
        "runtime.refresh",
        "qrcode.generate",
        "qrcode.decode",
      ],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "nvidia_cuda",
          canSwitchBackend: true,
          sources: [
            {
              kind: "package_index",
              id: "tuna-pypi",
              displayName: "TUNA PyPI 镜像",
              selected: true,
            },
            {
              kind: "package_index",
              id: "pypi",
              displayName: "PyPI 官方源",
              selected: false,
            },
            {
              kind: "model_registry",
              id: "huggingface",
              displayName: "Hugging Face",
              selected: false,
            },
          ],
          features: [
            {
              featureId: "document_parsing",
              displayName: "文档解析（PaddleOCR/MinerU）",
              accelerator: "nvidia_cuda",
              selected: false,
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(screen.queryByLabelText("默认模式")).not.toBeInTheDocument();
    expect(screen.getByText("二维码与条形码")).toBeVisible();
    expect(screen.getByText("随包可用")).toBeVisible();
    expect(screen.getByText(/从二维码工具直接使用/)).toBeVisible();
    expect(screen.getByText(/识别模式在对应任务中选择/)).toBeVisible();

    const packageSource = screen.getByLabelText("Python 包下载源");
    expect(packageSource).toHaveValue("tuna-pypi");
    await user.selectOptions(packageSource, "");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setSource",
      kind: "package_index",
    });
    await user.selectOptions(packageSource, "pypi");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setSource",
      kind: "package_index",
      sourceId: "pypi",
    });

    const modelSource = screen.getByLabelText("模型下载源");
    expect(modelSource).toHaveValue("");
    await user.selectOptions(modelSource, "huggingface");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setSource",
      kind: "model_registry",
      sourceId: "huggingface",
    });

    expect(screen.getByText("Hugging Face")).toBeInTheDocument();

    const accelerator = screen.getByLabelText("推理设备");
    expect(accelerator).toHaveValue("nvidia_cuda");
    await user.selectOptions(accelerator, "cpu");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setAccelerator",
      accelerator: "cpu",
    });

    await user.click(
      screen.getByRole("checkbox", {
        name: "文档解析（PaddleOCR/MinerU）",
      }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFeature",
      featureId: "document_parsing",
      enabled: true,
    });
    unmount();
  });

  it("auto-loads runtime settings once per settings page visit", () => {
    window.location.hash = "#/settings";
    const run = vi.fn();
    const actions: AppActions = {
      run,
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const settings = {
      isBusy: false,
      statusCode: "settings.ready",
      statusMessage: "正在读取设置",
      backend: "cpu",
      pendingBackend: "cpu",
      sources: [],
      features: [],
    };
    const base: AppViewState = {
      connected: true,
      revision: 40,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection"],
      features: { settings },
      runtimeLabel: "原生宿主已连接",
    };

    // 宿主未声明 runtime.refresh 能力时不派发；StrictMode 双挂载也不得提前触发。
    const { rerender, unmount } = render(
      <StrictMode>
        <App actions={actions} viewState={base} />
      </StrictMode>,
    );
    expect(run).not.toHaveBeenCalled();

    // 能力随宿主广播到达后自动加载一次，与手动“重新检查状态”同一命令。
    rerender(
      <StrictMode>
        <App
          actions={actions}
          viewState={{
            ...base,
            revision: 41,
            capabilities: ["settings.selection", "runtime.refresh"],
          }}
        />
      </StrictMode>,
    );
    expect(run).toHaveBeenCalledWith({ type: "settings.refreshRuntime" });

    // 后续宿主状态广播（刷新结果、能力数组重建）不得再次触发挂载刷新。
    rerender(
      <StrictMode>
        <App
          actions={actions}
          viewState={{
            ...base,
            revision: 42,
            capabilities: ["settings.selection", "runtime.refresh"],
            features: {
              settings: {
                ...settings,
                statusMessage: "默认 TTL 300s；已驻留管线 0 个",
                sources: [
                  {
                    kind: "package_index",
                    id: "pypi",
                    displayName: "PyPI 官方源",
                    selected: true,
                  },
                ],
              },
            },
          }}
        />
      </StrictMode>,
    );
    expect(
      run.mock.calls.filter(
        ([action]) => action?.type === "settings.refreshRuntime",
      ),
    ).toHaveLength(1);
    unmount();
  });

  it("configures a remote MinerU connection through the settings panel", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 50,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "cpu",
          sources: [],
          features: [],
          mineruConnection: {
            supported: true,
            mode: "local",
            apiUrl: "",
            hasApiKey: false,
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    const mode = screen.getByLabelText("MinerU 运行模式");
    expect(mode).toHaveValue("local");
    expect(screen.queryByLabelText("服务根地址")).not.toBeInTheDocument();
    await user.selectOptions(mode, "remote");
    const url = screen.getByLabelText("服务根地址");
    await user.type(url, "https://mineru.example.com");
    const key = screen.getByLabelText("API Key（可选）");
    expect(key).toHaveAttribute("type", "password");
    expect(key).toHaveAttribute("autocomplete", "off");
    await user.type(key, "secret-key");
    await user.click(screen.getByRole("button", { name: "保存 MinerU 配置" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setMineruConnection",
      mode: "remote",
      apiUrl: "https://mineru.example.com",
      apiKey: "secret-key",
    });
    // 保存不验证连通性，本地驻留/TTL 不控制远程，都不得被暗示成已验证可用。
    expect(screen.getByText(/不验证服务连通性/)).toBeVisible();
    expect(screen.getByText(/不控制远程服务/)).toBeVisible();
    expect(screen.getByText(/不要求本地 MinerU/)).toBeVisible();
    unmount();
  });

  it("marks remote MinerU explicitly unavailable on old backends", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 51,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "cpu",
          sources: [],
          features: [],
          mineruConnection: {
            supported: false,
            mode: "local",
            apiUrl: "",
            hasApiKey: false,
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(
      screen.getByRole("option", { name: "远程（自部署服务）" }),
    ).toBeDisabled();
    expect(
      screen.getByText(/当前 Backend 未声明 ocr\.mineru-remote-api\.v1/),
    ).toBeVisible();
    // 本地模式在旧 Backend 上仍可保存（它是缺省状态）。
    await user.click(screen.getByRole("button", { name: "保存 MinerU 配置" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setMineruConnection",
      mode: "local",
    });
    unmount();
  });

  it("waits for the runtime catalog before offering MinerU connection editing", () => {
    window.location.hash = "#/settings";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 52,
      route: "settings",
      theme: "light",
      capabilities: [],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "cpu",
          sources: [],
          features: [],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(
      screen.getByText(/MinerU 连接设置等待运行环境目录同步/),
    ).toBeVisible();
    expect(screen.getByLabelText("MinerU 运行模式")).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "保存 MinerU 配置" }),
    ).toBeDisabled();
    unmount();
  });

  it("does not describe unloaded download sources as a backend decision", () => {
    window.location.hash = "#/settings";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 43,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection"],
      features: {
        settings: {
          isBusy: false,
          statusCode: "settings.ready",
          statusMessage: "正在读取设置",
          backend: "cpu",
          pendingBackend: "cpu",
          sources: [],
          features: [],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(screen.getByText(/尚未加载/)).toBeVisible();
    expect(
      screen.queryByText(/Backend 未提供下载源目录/),
    ).not.toBeInTheDocument();
    unmount();
  });

  it("drives runtime maintenance install, cancel and retry from settings state", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 31,
      route: "settings",
      theme: "light",
      capabilities: [
        "settings.shell",
        "settings.selection",
        "runtime.maintenance",
      ],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "nvidia_cuda",
          engines: [],
          sources: [],
          features: [
            {
              featureId: "document_parsing",
              displayName: "文档解析（PaddleOCR/MinerU）",
              accelerator: "nvidia_cuda",
              selected: true,
            },
          ],
          canPreviewInstall: true,
          maintenance: {
            isRunning: false,
            statusCode: "failed",
            operationId: "ui-op-1",
            requestedComponentIds: ["document_parsing"],
            effectiveComponentIds: ["document_parsing", "runtime_host"],
            requestedSourceIds: ["tuna-pypi"],
            effectiveSourceIds: ["pypi"],
            canCancel: false,
            canRetry: true,
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(screen.getByText("当前运行环境不可用")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "预览安装范围" }));
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "settings.confirmRuntimeInstall" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.installRuntime",
    });

    await user.click(screen.getByRole("button", { name: "重新预览上次选择" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.retryRuntimeMaintenance",
    });

    // 原始组件与下载源 id 属于诊断细节，不在普通设置页回显。
    expect(screen.queryByText(/runtime_host/)).not.toBeInTheDocument();
    expect(screen.queryByText(/请求下载源/)).not.toBeInTheDocument();
    confirmSpy.mockRestore();
    unmount();
  });

  it("renders the install plan in Chinese and confirms only the displayed planId", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 32,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection", "runtime.maintenance"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "nvidia_cuda",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "nvidia_cuda",
          sources: [
            {
              kind: "package_index",
              id: "tuna-pypi",
              displayName: "TUNA PyPI 镜像",
              selected: true,
            },
          ],
          features: [
            {
              featureId: "document_parsing",
              displayName: "文档解析（PaddleOCR/MinerU）",
              accelerator: "nvidia_cuda",
              selected: true,
            },
          ],
          canPreviewInstall: true,
          maintenance: {
            isRunning: false,
            statusCode: "idle",
            operationId: null,
            requestedComponentIds: [],
            effectiveComponentIds: [],
            requestedSourceIds: [],
            effectiveSourceIds: [],
            canCancel: false,
            canRetry: false,
          },
          installPlan: {
            planId: "plan-42",
            expiresAt: "2099-01-01T00:00:00.000Z",
            accelerator: "NVIDIA CUDA",
            profileId: "profile-default",
            effectiveComponentIds: ["document_parsing", "runtime_host"],
            effectiveDownloadSourceIds: ["tuna-pypi"],
            components: [
              {
                componentId: "document_parsing",
                action: "install",
                dependencyState: "satisfied",
                reasonCodes: ["requested"],
              },
              {
                componentId: "runtime_host",
                action: "retain",
                dependencyState: "pending",
                reasonCodes: ["required_dependency"],
              },
              {
                componentId: "legacy_engine",
                action: "remove",
                dependencyState: "satisfied",
                reasonCodes: ["removed_by_selection", "future_reason_code"],
              },
            ],
            blockers: [],
            cost: {
              downloadBytes: 0,
              additionalDiskBytes: 3221225472,
              unknownReasonCodes: [
                "artifact_resolution_required",
                "future_cost_code",
              ],
            },
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(
      screen.getByText(
        "文档解析（PaddleOCR/MinerU）：安装；依赖已满足；用户选择",
      ),
    ).toBeVisible();
    expect(
      screen.getByText("runtime_host：保留；依赖待安装；必要依赖"),
    ).toBeVisible();
    expect(
      screen.getByText("legacy_engine：移除；依赖已满足；按选择移除"),
    ).toBeVisible();
    expect(screen.getByText("下载来源：TUNA PyPI 镜像")).toBeVisible();
    expect(
      screen.getByText("预计下载 0 B；新增磁盘占用 3 GiB。"),
    ).toBeVisible();
    expect(screen.getByText("下载量待解析")).toBeVisible();

    await user.click(screen.getByText("技术详情"));
    expect(screen.getByText("future_reason_code")).toBeVisible();
    expect(screen.getByText("future_cost_code")).toBeVisible();

    await user.click(screen.getByRole("button", { name: "预览安装范围" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.installRuntime",
    });
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "settings.confirmRuntimeInstall" }),
    );

    await user.click(screen.getByRole("button", { name: "确认此计划并安装" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmRuntimeInstall",
      planId: "plan-42",
    });
    unmount();
  });

  it("blocks confirming blocked, expired, busy or unsupported install plans", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const base: AppViewState = {
      connected: true,
      revision: 33,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection", "runtime.maintenance"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "nvidia_cuda",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "nvidia_cuda",
          sources: [
            {
              kind: "package_index",
              id: "tuna-pypi",
              displayName: "TUNA PyPI 镜像",
              selected: true,
            },
          ],
          features: [
            {
              featureId: "document_parsing",
              displayName: "文档解析（PaddleOCR/MinerU）",
              accelerator: "nvidia_cuda",
              selected: true,
            },
          ],
          canPreviewInstall: true,
          maintenance: {
            isRunning: false,
            statusCode: "idle",
            operationId: null,
            requestedComponentIds: [],
            effectiveComponentIds: [],
            requestedSourceIds: [],
            effectiveSourceIds: [],
            canCancel: false,
            canRetry: false,
          },
          installPlan: {
            planId: "plan-43",
            expiresAt: "2099-01-01T00:00:00.000Z",
            accelerator: "NVIDIA CUDA",
            profileId: "profile-default",
            effectiveComponentIds: ["document_parsing"],
            effectiveDownloadSourceIds: ["tuna-pypi"],
            components: [
              {
                componentId: "document_parsing",
                action: "install",
                dependencyState: "satisfied",
                reasonCodes: ["requested"],
              },
            ],
            blockers: [],
            cost: {
              downloadBytes: 1024,
              additionalDiskBytes: null,
              unknownReasonCodes: [],
            },
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { rerender, unmount } = render(
      <App actions={actions} viewState={base} />,
    );
    const confirm = screen.getByRole("button", { name: "确认此计划并安装" });
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmRuntimeInstall",
      planId: "plan-43",
    });

    const settingsBase = base.features.settings as Record<string, unknown> & {
      readonly installPlan: Record<string, unknown>;
    };
    const settings = (patch: Record<string, unknown>): AppViewState => ({
      ...base,
      revision: base.revision + 1,
      features: {
        settings: {
          ...settingsBase,
          ...patch,
          installPlan: {
            ...settingsBase.installPlan,
            ...(patch.installPlan as Record<string, unknown> | undefined),
          },
        },
      },
    });

    rerender(
      <App
        actions={actions}
        viewState={settings({
          installPlan: {
            blockers: [
              {
                code: "disk_space_insufficient",
                componentId: "document_parsing",
                nextAction: "请清理磁盘后重新预览",
              },
            ],
          },
        })}
      />,
    );
    expect(confirm).toBeDisabled();
    expect(
      screen.getByText(
        "无法安装 文档解析（PaddleOCR/MinerU）：请清理磁盘后重新预览",
      ),
    ).toBeVisible();
    await user.click(screen.getByText("技术详情"));
    expect(screen.getByText("disk_space_insufficient")).toBeVisible();

    vi.useFakeTimers();
    try {
      rerender(
        <App
          actions={actions}
          viewState={settings({
            installPlan: {
              expiresAt: new Date(Date.now() + 1_000).toISOString(),
            },
          })}
        />,
      );
      expect(confirm).toBeEnabled();
      await act(async () => {
        vi.advanceTimersByTime(1_001);
      });
      expect(confirm).toBeDisabled();
    } finally {
      vi.useRealTimers();
    }
    rerender(
      <App
        actions={actions}
        viewState={settings({
          installPlan: { expiresAt: "2020-01-01T00:00:00.000Z" },
        })}
      />,
    );
    expect(confirm).toBeDisabled();
    expect(
      screen.getByText("安装计划已过期，请重新预览后确认。"),
    ).toBeVisible();

    rerender(
      <App
        actions={actions}
        viewState={settings({
          maintenance: {
            isRunning: true,
            statusCode: "running",
            operationId: "op-9",
            requestedComponentIds: ["document_parsing"],
            effectiveComponentIds: ["document_parsing"],
            requestedSourceIds: [],
            effectiveSourceIds: [],
            canCancel: true,
            canRetry: true,
          },
        })}
      />,
    );
    expect(confirm).toBeDisabled();
    expect(screen.getByRole("button", { name: "预览安装范围" })).toBeDisabled();
    expect(screen.getByLabelText("推理设备")).toBeDisabled();
    expect(screen.getByLabelText("Python 包下载源")).toBeDisabled();
    expect(
      screen.getByRole("checkbox", {
        name: "文档解析（PaddleOCR/MinerU）",
      }),
    ).toBeDisabled();
    expect(
      screen.getByText(
        /维护进行中：推理设备、组件与下载来源的修改和确认已暂停/,
      ),
    ).toBeVisible();
    expect(screen.getByRole("button", { name: "取消安装" })).toBeEnabled();

    rerender(
      <App
        actions={actions}
        viewState={settings({ canPreviewInstall: false })}
      />,
    );
    expect(confirm).toBeDisabled();
    expect(screen.getByText(/当前运行环境不支持安装预览/)).toBeVisible();
    unmount();
  });

  it("keeps the current service state separate from a failed maintenance outcome", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 34,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection", "runtime.maintenance"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          pendingBackend: "cpu",
          sources: [],
          features: [],
          serviceStatus: "识别服务已就绪。",
          maintenanceStatus: "上次安装失败：下载中断。",
          maintenancePhase: "",
          progressActive: false,
          progressText: "",
          progressPercent: null,
          canPreviewInstall: true,
          maintenance: {
            isRunning: false,
            statusCode: "failed",
            operationId: "op-8",
            requestedComponentIds: ["document_parsing"],
            effectiveComponentIds: ["document_parsing"],
            requestedSourceIds: [],
            effectiveSourceIds: [],
            canCancel: false,
            canRetry: true,
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(screen.getByText("当前服务：识别服务已就绪。")).toBeVisible();
    expect(
      screen.getByText("本次维护：上次安装失败：下载中断。"),
    ).toBeVisible();
    expect(screen.getByText("维护操作失败")).toBeVisible();
    // 终态不得继续渲染活动进度动画；准确终态文案保留。
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    expect(
      screen.queryByText(/维护操作已完成|维护任务已完成|100%/),
    ).not.toBeInTheDocument();

    const retry = screen.getByRole("button", { name: "重新预览上次选择" });
    expect(retry).toBeEnabled();
    await user.click(retry);
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.retryRuntimeMaintenance",
    });
    unmount();
  });

  it("renders idle settings without a maintenance progress bar and an unread target accelerator", () => {
    window.location.hash = "#/settings";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const settingsFeature = {
      theme: "light",
      isBusy: false,
      statusCode: "settings.ready",
      // 宿主尚未读取真实运行时快照：backend 为 null，不得把默认目标
      // cpu 冒充为实际运行设备。
      backend: null,
      startupEnabled: false,
      pendingBackend: "cpu",
      sources: [],
      features: [],
      serviceStatus: "等待运行时检查",
      maintenanceStatus: "等待运行时检查",
      maintenancePhase: "尚未开始",
      progressActive: false,
      progressText: "",
      progressDetail: "",
      progressPercent: null,
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 35,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection", "runtime.refresh"],
      features: { settings: settingsFeature },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(screen.getByText("目标推理设备：尚未读取")).toBeVisible();
    expect(screen.getByText("当前服务：等待运行时检查")).toBeVisible();
    // 空闲（无活动维护操作）不得渲染滚动维护进度。
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    unmount();
  });

  it("shows current instance device evidence without claiming a completed execution", () => {
    window.location.hash = "#/diagnostics";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 36,
      route: "diagnostics",
      theme: "light",
      capabilities: [],
      features: {
        diagnostics: {
          deviceEvidence: ["[Paddle worker] [GPU] 验证失败，回退到 CPU"],
          milestones: [],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    expect(
      screen.getByText("[Paddle worker] [GPU] 验证失败，回退到 CPU"),
    ).toBeVisible();
    expect(
      screen.getByText("设备决策与回退日志；不表示识别作业已成功执行。"),
    ).toBeVisible();
    unmount();
  });

  it("gates the maintenance progress bar on the active operation with a real total", () => {
    window.location.hash = "#/settings";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const settingsFeature = {
      theme: "light",
      isBusy: false,
      statusCode: "settings.ready",
      backend: "cpu",
      startupEnabled: false,
      pendingBackend: "cpu",
      sources: [],
      features: [],
      serviceStatus: "运行时已就绪",
      maintenanceStatus: "正在准备 Backend 运行时",
      maintenancePhase: "安装重依赖",
      progressActive: true,
      progressText: "已完成 1024 bytes · 总量未知",
      progressDetail: "",
      progressPercent: null,
      maintenance: {
        isRunning: true,
        statusCode: "running",
        operationId: "op-10",
        requestedComponentIds: [],
        effectiveComponentIds: [],
        requestedSourceIds: [],
        effectiveSourceIds: [],
        canCancel: true,
        canRetry: false,
      },
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 36,
      route: "settings",
      theme: "light",
      capabilities: ["settings.selection", "runtime.maintenance"],
      features: { settings: settingsFeature },
      runtimeLabel: "原生宿主已连接",
    };

    const { rerender, unmount } = render(
      <App actions={actions} viewState={viewState} />,
    );

    // 真实操作进行中且无总量：不确定进度。
    const indeterminate = screen.getByRole("progressbar", {
      name: "运行环境维护进度",
    });
    expect(indeterminate).not.toHaveAttribute("aria-valuenow");

    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 37,
          features: {
            settings: {
              ...settingsFeature,
              progressText: "512 / 1024 bytes",
              progressPercent: 50,
            },
          },
        }}
      />,
    );
    // 有真实总量才显示百分比。
    expect(
      screen.getByRole("progressbar", { name: "运行环境维护进度" }),
    ).toHaveAttribute("aria-valuenow", "0.5");

    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 38,
          features: {
            settings: {
              ...settingsFeature,
              maintenanceStatus: "维护操作已完成",
              maintenancePhase: "提交运行时切换",
              progressActive: false,
              progressText: "",
              progressPercent: null,
              maintenance: {
                ...settingsFeature.maintenance,
                isRunning: false,
                statusCode: "succeeded",
                canCancel: false,
              },
            },
          },
        }}
      />,
    );
    // 下一次宿主快照退出活动动画，保留终态文案。
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    expect(
      screen.getByText("本次维护：维护操作已完成 · 提交运行时切换"),
    ).toBeVisible();
    unmount();
  });

  it("uses a task recognition mode or delegates to the Runtime default", async () => {
    window.location.hash = "#/recognition";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 21,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.engine", "recognition.capture"],
      features: {
        recognition: {
          isBusy: false,
          statusCode: "recognition.ready",
          taskEngine: null,
          engines: [
            {
              engine: "rapidocr",
              displayName: "RapidOCR",
              selected: false,
              isTaskOverride: false,
              availability: "ready",
              requiresDownload: false,
              lifecycleKind: "unmanaged",
              supportsPreload: false,
              supportsTtl: false,
              supportsPinning: false,
              supportsRelease: false,
            },
            {
              engine: "windows",
              displayName: "Windows OCR",
              selected: false,
              isTaskOverride: false,
              availability: "unavailable",
              requiresDownload: false,
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );

    const taskEngine = screen.getByLabelText("本次识别模式");
    expect(taskEngine).toHaveValue("");
    expect(screen.getByText("使用 Runtime 默认模式")).toBeVisible();
    await user.selectOptions(taskEngine, "windows");
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.setTaskEngine",
      engine: "windows",
    });
    await user.selectOptions(taskEngine, "");
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.setTaskEngine",
    });
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 22,
          features: {
            recognition: {
              isBusy: false,
              statusCode: "recognition.ready",
              taskEngine: "paddle_text",
              engines: [
                {
                  engine: "paddle_text",
                  displayName: "通用 OCR（PaddleOCR）",
                  selected: true,
                  isTaskOverride: true,
                  availability: "ready",
                  requiresDownload: false,
                  lifecycleKind: "model_residency",
                  supportsPreload: true,
                  supportsTtl: true,
                  supportsPinning: true,
                  supportsRelease: true,
                },
              ],
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(
        "该 Paddle 模式使用模型驻留；支持：预热、TTL、固定驻留、释放。",
      ),
    ).toBeVisible();
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 23,
          features: {
            recognition: {
              isBusy: false,
              statusCode: "recognition.ready",
              taskEngine: "mineru_document",
              engines: [
                {
                  engine: "mineru_document",
                  displayName: "深度文档解析（MinerU）",
                  selected: true,
                  isTaskOverride: true,
                  availability: "ready",
                  requiresDownload: false,
                  lifecycleKind: "process_keep_alive",
                  supportsPreload: false,
                  supportsTtl: true,
                  supportsPinning: false,
                  supportsRelease: true,
                },
              ],
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(
        "本地 MinerU 使用进程保活；仅支持：TTL、释放。远程模型生命周期由服务端管理。",
      ),
    ).toBeVisible();
    unmount();
  });
  it("shows a fresh empty environment without starting OCR and routes explicit installation", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 1,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "尚未选择环境",
      features: {
        settings: {
          environments: [
            {
              id: "env-1",
              name: "文档",
              revision: 1,
              kind: "venv",
              status: "empty",
              pythonState: "ready",
              dependencyState: "empty",
              engineState: "unverified",
              modelState: "not_checked",
              serviceState: "not_started",
              configuredRecognitionTypes: [],
              targetDevice: null,
              actualDevice: null,
              lastInstallFailure: {
                phase: "failed",
                environmentRevision: 1,
                recipe: "rapidocr-cpu",
                reasonCode: "network_error",
                nextAction: "check_source_and_retry",
                detail: "下载源连接失败。",
                requestedSourceIds: null,
                effectiveSourceIds: ["tuna-pypi"],
              },
            },
          ],
          activeEnvironmentId: null,
          environmentSources: [
            {
              id: "tuna-pypi",
              kind: "package_index",
              displayName: "TUNA PyPI 镜像",
              endpoint: "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
            },
            {
              id: "pypi",
              kind: "package_index",
              displayName: "PyPI 官方源",
              endpoint: "https://pypi.org/simple",
            },
          ],
          environmentPackageSourceIds: ["tuna-pypi", "pypi"],
          environmentCanCancelInstall: true,
          environmentPlan: {
            planId: "plan-1",
            environmentId: "env-1",
            environmentRevision: 1,
            recipe: "rapidocr-cpu",
            requestedRecipe: "rapidocr-cpu",
            sourceIds: ["tuna-pypi"],
            requestedSourceIds: null,
            dependencies: ["rapidocr==3.6.0"],
          },
          environmentStatus: "已预览锁定配方。",
        },
      },
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    expect(screen.getByText(/Python ready · 依赖 empty/)).toBeVisible();
    expect(screen.getByText(/上次依赖安装未完成/)).toHaveTextContent(
      "下载源连接失败。",
    );
    expect(screen.getByText(/上次依赖安装未完成/)).toHaveTextContent(
      "检查下载源与网络后重新预览安装",
    );
    // 失败终态携带该操作绑定的请求/生效来源（请求为继承时不伪装显式）。
    expect(screen.getByText(/上次依赖安装未完成/)).toHaveTextContent(
      "请求源：跟随配置；生效源：TUNA PyPI 镜像",
    );
    expect(screen.getByText(/锁定依赖（1 项）/)).toBeVisible();
    expect(
      screen.getByText(
        /计划：rapidocr-cpu · 目标文档（环境修订 1）· 请求源：跟随配置；生效源：TUNA PyPI 镜像/,
      ),
    ).toBeVisible();
    await user.type(screen.getByLabelText("新环境名称"), "资料");
    await user.click(screen.getByRole("button", { name: "创建空环境" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.createEnvironment",
      name: "资料",
    });
    await user.click(screen.getByRole("button", { name: "确认安装依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmEnvironmentInstall",
      planId: "plan-1",
    });
    await user.click(screen.getByRole("button", { name: "切换到此环境" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.switchEnvironment",
      environmentId: "env-1",
    });
    await user.click(screen.getByRole("button", { name: "取消安装" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.cancelEnvironmentInstall",
    });
    unmount();
  });
  it("keeps a compatible MinerU request confirmable when Runtime resolves a combined recipe", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 2,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "运行环境",
      features: {
        settings: {
          environments: [
            {
              id: "env-1",
              name: "RapidOCR",
              revision: 2,
              kind: "venv",
              status: "installed",
              pythonState: "ready",
              dependencyState: "installed",
              engineState: "unverified",
              modelState: "not_checked",
              serviceState: "not_started",
              configuredRecognitionTypes: ["text"],
            },
          ],
          environmentPackageSourceIds: ["tuna-pypi"],
          environmentPlan: {
            planId: "combined-plan",
            environmentId: "env-1",
            environmentRevision: 2,
            requestedRecipe: "mineru-cpu",
            recipe: "rapidocr+mineru-cpu",
            sourceIds: ["tuna-pypi"],
            requestedSourceIds: null,
            dependencies: ["rapidocr==3.9.2", "mineru==4.0.2"],
          },
        },
      },
    };
    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    await user.selectOptions(
      screen.getByLabelText("锁定依赖配方"),
      "mineru-cpu",
    );
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    await user.selectOptions(
      screen.getByLabelText("锁定依赖配方"),
      "rapidocr-cpu",
    );
    await user.selectOptions(
      screen.getByLabelText("锁定依赖配方"),
      "mineru-cpu",
    );
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    const settingsState = viewState.features.settings as Record<
      string,
      unknown
    >;
    const previousPlan = settingsState.environmentPlan as Record<
      string,
      unknown
    >;
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 3,
          features: {
            settings: {
              ...settingsState,
              environmentPlan: { ...previousPlan, planId: "new-combined-plan" },
            },
          },
        }}
      />,
    );
    expect(screen.getByText(/计划：rapidocr\+mineru-cpu/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "确认安装依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmEnvironmentInstall",
      planId: "new-combined-plan",
    });
    unmount();
  });

  it("shows managed source catalog names and saves defaults and overrides separately", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 4,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "运行环境",
      features: {
        settings: {
          environments: [
            {
              id: "env-a",
              name: "文档",
              revision: 2,
              kind: "venv",
              status: "installed",
              pythonState: "ready",
              dependencyState: "installed",
              engineState: "unverified",
              modelState: "not_checked",
              serviceState: "ready",
              configuredRecognitionTypes: ["text"],
              targetDevice: "cpu",
              actualDevice: null,
              sourceIds: ["tuna-pypi"],
              overrideSourceIds: [],
              unknownSourceIds: ["gone-model"],
              resolvedSources: [
                {
                  kind: "package_index",
                  id: "tuna-pypi",
                  displayName: "TUNA PyPI 镜像",
                  origin: "global_default",
                },
                {
                  kind: "model_registry",
                  id: null,
                  displayName: null,
                  origin: "product_default",
                },
              ],
            },
            {
              id: "env-b",
              name: "空白",
              revision: 1,
              kind: "venv",
              status: "empty",
              pythonState: "ready",
              dependencyState: "empty",
              engineState: "unavailable",
              modelState: "not_applicable",
              serviceState: "not_started",
              configuredRecognitionTypes: [],
              overrideSourceIds: ["modelscope"],
              resolvedSources: [
                {
                  kind: "package_index",
                  id: "tuna-pypi",
                  displayName: "TUNA PyPI 镜像",
                  origin: "global_default",
                },
                {
                  kind: "model_registry",
                  id: "modelscope",
                  displayName: "ModelScope",
                  origin: "environment_override",
                },
              ],
            },
          ],
          activeEnvironmentId: "env-a",
          environmentSources: [
            {
              id: "tuna-pypi",
              kind: "package_index",
              displayName: "TUNA PyPI 镜像",
              endpoint: "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
            },
            {
              id: "pypi",
              kind: "package_index",
              displayName: "PyPI 官方源",
              endpoint: "https://pypi.org/simple",
            },
            {
              id: "huggingface",
              kind: "model_registry",
              displayName: "Hugging Face",
              endpoint: "https://huggingface.co",
            },
            {
              id: "modelscope",
              kind: "model_registry",
              displayName: "ModelScope",
              endpoint: "https://www.modelscope.cn",
            },
          ],
          environmentDefaultSourceIds: ["tuna-pypi"],
          environmentUnknownDefaultSourceIds: [],
        },
      },
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    // 折叠详情展开后再断言内部内容。
    await user.click(screen.getByText("来源配置（默认、覆盖与已生效值）"));
    await user.click(screen.getByText("全局默认来源（所有环境继承）"));

    // 目录展示名，不再堆 raw id；默认/覆盖/已生效值分开标注。
    expect(
      screen.getByText("依赖包来源：TUNA PyPI 镜像（全局默认）"),
    ).toBeVisible();
    expect(
      screen.getByText("模型来源：官方默认（端点未知）（产品默认）"),
    ).toBeVisible();
    expect(screen.getByText(/已安装依赖来源：TUNA PyPI 镜像/)).toBeVisible();
    expect(
      screen.getByText(
        /未知来源：gone-model（当前版本目录不含，解析已回退继承）/,
      ),
    ).toBeVisible();
    // 运行中环境的模型偏好标注下次启动生效。
    expect(
      screen.getByText(/该环境正在使用；模型来源修改将在下次启动时生效/),
    ).toBeVisible();
    // 管理模式下旧 Backend 下载源入口不冒充全局。
    expect(screen.getByText(/已并入“环境与依赖”/)).toBeVisible();
    expect(screen.queryByLabelText("Python 包下载源")).not.toBeInTheDocument();

    // 切到 B：B 的覆盖与 A 互不影响；保存只针对目标环境。
    await user.selectOptions(screen.getByLabelText("目标环境"), "env-b");
    expect(
      screen.getByText("模型来源：ModelScope（本环境覆盖）"),
    ).toBeVisible();
    await user.selectOptions(screen.getByLabelText("本环境依赖包来源"), "pypi");
    await user.click(screen.getByRole("button", { name: "保存本环境来源" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setEnvironmentSources",
      environmentId: "env-b",
      packageSourceId: "pypi",
      modelSourceId: "modelscope",
    });

    // 全局默认保存不带环境 id，不会触碰任何单环境 override。
    await user.selectOptions(screen.getByLabelText("全局依赖包来源"), "pypi");
    await user.selectOptions(
      screen.getByLabelText("全局模型来源"),
      "huggingface",
    );
    await user.click(screen.getByRole("button", { name: "保存全局默认来源" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setEnvironmentSources",
      packageSourceId: "pypi",
      modelSourceId: "huggingface",
    });
    unmount();
  });

  it("routes inherited and explicit preview sources with plan transparency", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 5,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "运行环境",
      features: {
        settings: {
          environments: [
            {
              id: "env-1",
              name: "空白",
              revision: 1,
              kind: "venv",
              status: "empty",
              pythonState: "ready",
              dependencyState: "empty",
              engineState: "unavailable",
              modelState: "not_applicable",
              serviceState: "not_started",
              configuredRecognitionTypes: [],
            },
          ],
          activeEnvironmentId: null,
          environmentSources: [
            {
              id: "tuna-pypi",
              kind: "package_index",
              displayName: "TUNA PyPI 镜像",
              endpoint: "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
            },
            {
              id: "pypi",
              kind: "package_index",
              displayName: "PyPI 官方源",
              endpoint: "https://pypi.org/simple",
            },
          ],
          environmentDefaultSourceIds: [],
        },
      },
    };
    const { rerender, unmount } = render(
      <App actions={actions} viewState={viewState} />,
    );
    const sourceSelect = screen.getByLabelText("本次依赖下载源");
    expect(sourceSelect).toHaveValue("");
    expect(
      within(sourceSelect).getByRole("option", {
        name: /跟随环境配置（当前解析为 TUNA PyPI 镜像（产品默认））/,
      }),
    ).toBeInTheDocument();
    expect(
      within(sourceSelect).getByRole("option", { name: "PyPI 官方源" }),
    ).toBeInTheDocument();

    // 跟随配置预览：不带 sourceId。
    await user.click(screen.getByRole("button", { name: "预览依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.previewEnvironmentInstall",
      environmentId: "env-1",
      recipe: "rapidocr-cpu",
    });

    // 显式选择预览：携带 sourceId，并作废旧计划。
    await user.selectOptions(sourceSelect, "pypi");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    await user.click(screen.getByRole("button", { name: "预览依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.previewEnvironmentInstall",
      environmentId: "env-1",
      recipe: "rapidocr-cpu",
      sourceId: "pypi",
    });

    // 显式计划的请求/生效/继承标记与资产来源分类。
    const settings = viewState.features.settings as Record<string, unknown>;
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 6,
          features: {
            settings: {
              ...settings,
              environmentPlan: {
                planId: "plan-explicit",
                environmentId: "env-1",
                environmentRevision: 1,
                recipe: "rapidocr-cpu",
                requestedRecipe: "rapidocr-cpu",
                sourceIds: ["pypi"],
                requestedSourceIds: ["pypi"],
                dependencies: ["rapidocr==3.9.2"],
                sources: [
                  {
                    id: "pypi",
                    kind: "package_index",
                    displayName: "PyPI 官方源",
                    endpoint: "https://pypi.org/simple",
                    requested: true,
                    inheritedFrom: "product_default",
                    usage: "online_index",
                    actualEndpoint: null,
                  },
                ],
                dependencyOrigin: "online_index",
                pythonOrigin: "product_bundle",
                runtimeWheelOrigin: "product_bundle",
              },
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(
        /计划：rapidocr-cpu · 目标空白（环境修订 1）· 请求源：PyPI 官方源；生效源：PyPI 官方源/,
      ),
    ).toBeVisible();
    await user.click(screen.getByText("来源与资产明细"));
    expect(screen.getByText(/PyPI 官方源（依赖包来源，本次指定/)).toBeVisible();
    expect(screen.getByText(/缓存命中\s*不会计入新下载/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "确认安装依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmEnvironmentInstall",
      planId: "plan-explicit",
      sourceId: "pypi",
    });
    unmount();
  });
});
