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
    expect(
      screen.getAllByText("此功能需要宿主能力：batch.export"),
    ).toHaveLength(3);

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
      screen.getByText(/马赛克与模糊会写入复制、保存的图片副本/),
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

  it("drives catalog engine, source and feature selection from settings state", async () => {
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
      capabilities: ["settings.shell", "settings.selection", "runtime.refresh"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          selectedEngine: "rapidocr",
          engineChoiceRequired: false,
          pendingBackend: "nvidia_cuda",
          canSwitchBackend: true,
          engines: [
            {
              engine: "rapidocr",
              displayName: "RapidOCR",
              availability: "ready",
              reasonCode: null,
              requiresDownload: false,
              selected: true,
            },
            {
              engine: "paddleocr",
              displayName: "PaddleOCR",
              availability: "preparation_required",
              reasonCode: null,
              requiresDownload: true,
              selected: false,
            },
          ],
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

    const engineSelect = screen.getByLabelText("全局默认识别模式");
    expect(engineSelect).toHaveValue("rapidocr");
    await user.selectOptions(engineSelect, "paddleocr");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setEngine",
      engine: "paddleocr",
    });

    const packageSource = screen.getByLabelText("Python 包源");
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

    const modelSource = screen.getByLabelText("模型源");
    expect(modelSource).toHaveValue("");
    await user.selectOptions(modelSource, "huggingface");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setSource",
      kind: "model_registry",
      sourceId: "huggingface",
    });

    expect(screen.getByText("Hugging Face")).toBeInTheDocument();

    const accelerator = screen.getByLabelText("目标加速器");
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

    await user.click(screen.getByRole("button", { name: "安装所选组件" }));
    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining("文档解析（PaddleOCR/MinerU）"),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.installRuntime",
    });

    await user.click(
      screen.getByRole("button", { name: "重试（沿用上次选择）" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.retryRuntimeMaintenance",
    });

    // requested/effective 回显是安装真相。
    expect(
      screen.getByText(/实际安装：document_parsing、runtime_host/),
    ).toBeVisible();
    expect(screen.getByText(/下载源：pypi/)).toBeVisible();

    confirmSpy.mockRestore();
    unmount();
  });

  it("separates the recognition task engine override from the global default", async () => {
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
              selected: true,
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
    expect(
      screen.getByText("该模式不提供模型预热、TTL、固定驻留或释放控制。"),
    ).toBeVisible();
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
              taskEngine: null,
              engines: [
                {
                  engine: "paddle_text",
                  displayName: "通用 OCR（PaddleOCR）",
                  selected: true,
                  isTaskOverride: false,
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
              taskEngine: null,
              engines: [
                {
                  engine: "mineru_document",
                  displayName: "深度文档解析（MinerU）",
                  selected: true,
                  isTaskOverride: false,
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
      screen.getByText("MinerU 使用进程保活；仅支持：TTL、释放。"),
    ).toBeVisible();
    unmount();
  });
});
