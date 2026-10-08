import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { describe, expect, it, vi } from "vitest";

import { App, type AppActions, type AppViewState } from "./App";

Object.assign(globalThis, { NodeFilter: window.NodeFilter });

describe("AppShell", () => {
  it("keeps the capture editor isolated from main navigation", async () => {
    window.location.hash = "#/imageEdit?scene=1";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 1,
      route: "recognition",
      theme: "light",
      capabilities: [],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { rerender, unmount } = render(
      <App actions={actions} viewState={viewState} />,
    );
    expect(
      await screen.findByRole("heading", { name: "截图现场编辑" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("complementary", { name: "主导航" }),
    ).not.toBeInTheDocument();
    rerender(
      <App
        actions={actions}
        viewState={{ ...viewState, revision: 2, route: "pdf" }}
      />,
    );
    expect(
      screen.getByRole("heading", { name: "截图现场编辑" }),
    ).toBeInTheDocument();
    expect(window.location.hash).toBe("#/imageEdit?scene=1");
    unmount();
  });

  it("opens files and clipboard images for editing without recognition commands", async () => {
    window.location.hash = "#/imageEdit";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 1,
      route: "imageEdit",
      theme: "light",
      capabilities: ["recognition.annotation"],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "选择图片" }));
    await user.click(screen.getByRole("button", { name: "粘贴图片" }));
    expect(actions.run).toHaveBeenNthCalledWith(1, {
      type: "imageEdit.selectImage",
    });
    expect(actions.run).toHaveBeenNthCalledWith(2, {
      type: "imageEdit.readClipboard",
    });
    unmount();
  });
  it("offers one screenshot action without an OCR connection", async () => {
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
      capabilities: [
        "recognition.capture",
        "recognition.screenshotSession",
        "recognition.scrollCapture",
      ],
      features: {},
      runtimeLabel: "未连接",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    // 单击直接开始普通截图：不再弹出菜单，长截图等动作在框选后的动作栏里。
    expect(screen.getAllByRole("button", { name: /^截图$/ })).toHaveLength(1);
    expect(
      screen.queryByRole("menu", { name: /截图/ }),
    ).not.toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("button", { name: "截图" }));
    expect(actions.run).toHaveBeenCalledExactlyOnceWith({
      type: "recognition.captureScreen",
    });
    unmount();
  });
  it("disables the unified screenshot button without the capture capability", () => {
    window.location.hash = "#/recognition";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 1,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.screenshotSession"],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    const button = screen.getAllByRole("button", { name: /^截图$/ })[0]!;
    expect(button).toBeDisabled();
    unmount();
  });
  it("shows per-action hotkey state and drives apply, disable and reset", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
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

    const input = screen.getByLabelText("剪贴板识别新快捷键");
    await user.click(input);
    const start = vi.mocked(actions.run).mock.calls.at(-1)![0];
    expect(start.type).toBe("settings.beginHotkeyRecording");
    fireEvent.keyDown(input, { key: "Control", ctrlKey: true });
    expect(input).toHaveValue("Ctrl");
    expect(
      screen.getByRole("button", { name: "应用 剪贴板识别" }),
    ).toBeDisabled();
    fireEvent.keyDown(input, { key: "c", ctrlKey: true, altKey: true });
    expect(input).toHaveValue("Ctrl+Alt+C");
    await user.click(screen.getByRole("button", { name: "应用 剪贴板识别" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setActionHotkey",
      actionId: "clipboard_recognize",
      hotkey: "Ctrl+Alt+C",
    });

    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.endHotkeyRecording",
      recordingId: start.recordingId,
    });
    await user.click(screen.getByRole("button", { name: "清空 剪贴板识别" }));
    expect(input).toHaveValue("");
    expect(
      screen.getByRole("button", { name: "应用 剪贴板识别" }),
    ).toBeDisabled();

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
            lingerMs: 600,
            theme: "system",
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
    const delayInput = screen.getByLabelText("收起时间（毫秒）");
    await user.selectOptions(screen.getByLabelText("收起后露出像素"), "8");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFloatingToolbarPreferences",
      peekPixels: 8,
    });
    expect(delayInput).toHaveValue(600);
    await user.clear(delayInput);
    await user.type(delayInput, "99");
    expect(screen.getByRole("button", { name: "保存时间" })).toBeDisabled();
    await user.clear(delayInput);
    await user.type(delayInput, "900");
    await user.click(screen.getByRole("button", { name: "保存时间" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFloatingToolbarPreferences",
      lingerMs: 900,
    });
    // 宿主快照尚未确认 900ms 时切换主题，不回传陈旧的 600ms。
    await user.selectOptions(screen.getByLabelText("工具栏主题"), "dark");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setFloatingToolbarPreferences",
      theme: "dark",
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

    await user.click(screen.getByRole("link", { name: "二维码与条码" }));
    expect(actions.navigate).toHaveBeenCalledWith("qrcode");
    expect(
      await screen.findByRole("heading", { name: "二维码与条码" }),
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
      await user.click(screen.getByRole("link", { name: "二维码与条码" }));
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
        await screen.findByRole("heading", { name: "二维码与条码" }),
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
    await user.click(screen.getByRole("button", { name: "生成图片" }));

    expect(actions.run).toHaveBeenCalledWith({
      type: "qrcode.generate",
      text: "中文识别结果",
      format: "qrcode",
      captionMode: "off",
      captionText: "",
    });
    unmount();
  });

  it("decodes the shared preview once automatically and allows manual retry and image copy", async () => {
    window.location.hash = "#/qrcode";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 1,
      route: "qrcode",
      theme: "light",
      capabilities: [
        "qrcode.generate",
        "qrcode.decode",
        "qrcode.copyImage",
        "qrcode.save",
      ],
      features: {
        qrcode: {
          isBusy: false,
          previewRevision: 1,
          needsPreviewDecode: true,
          generatedResource: {
            url: "https://app.vibeocr/__resource/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            mediaType: "image/png",
            byteLength: 10,
          },
        },
      },
      runtimeLabel: "就绪",
    };
    const { unmount } = render(<App actions={actions} viewState={viewState} />);
    await user.selectOptions(screen.getByLabelText("生成格式"), "ean13");
    await user.selectOptions(screen.getByLabelText("底部文字"), "custom");
    await user.type(screen.getByLabelText("独立说明"), "标签");
    await user.type(screen.getByLabelText("输入内容"), "590123412345");
    await user.click(screen.getByRole("button", { name: "生成图片" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "qrcode.generate",
      text: "590123412345",
      format: "ean13",
      captionMode: "custom",
      captionText: "标签",
    });
    await user.click(screen.getByRole("tab", { name: "识别" }));
    // 解码能力说明基于预装解码器的真实支持清单，不再误导“取决于已安装解码器”。
    expect(screen.getByText(/解码能力已随产品预装/)).toBeVisible();
    expect(screen.queryByText(/取决于已安装解码器/)).not.toBeInTheDocument();
    expect(actions.run).toHaveBeenCalledWith({
      type: "qrcode.decodeCurrent",
      force: false,
    });
    await user.click(screen.getByRole("tab", { name: "生成" }));
    await user.click(screen.getByRole("tab", { name: "识别" }));
    expect(
      vi
        .mocked(actions.run)
        .mock.calls.filter(
          ([action]) =>
            action.type === "qrcode.decodeCurrent" && action.force === false,
        ),
    ).toHaveLength(1);
    await user.click(
      screen.getByRole("button", { name: "识别当前预览 / 重新识别" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "qrcode.decodeCurrent",
      force: true,
    });
    await user.click(screen.getByRole("button", { name: "复制图片" }));
    expect(actions.run).toHaveBeenCalledWith({ type: "qrcode.copyImage" });
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
    // 页码范围说明取代了旧的“批次由识别服务自动调度”调度说明。
    expect(screen.getByText(/页码从 1 开始；留空沿用 MinerU/)).toBeVisible();
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
    // 单次识别面只保留去水印、旋转、屏蔽三类图片处理工具（用户项2）；
    // 绘制/遮盖/标注类工具不再进入识别面。
    expect(screen.getByRole("button", { name: "去水印" })).toBeVisible();
    expect(screen.getByRole("button", { name: "屏蔽" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "椭圆" })).toBeNull();
    expect(screen.queryByRole("button", { name: "马赛克" })).toBeNull();
    expect(screen.queryByRole("button", { name: "文字" })).toBeNull();
    expect(screen.getByRole("button", { name: "复制标注图" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "保存标注图" })).toBeEnabled();
    expect(
      screen.getByText(/拖拽框选“屏蔽”区，识别时忽略其中内容/),
    ).toBeVisible();
    expect(screen.getByLabelText("图片检查画布")).toHaveAttribute(
      "tabindex",
      "0",
    );
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
    window.location.hash = "#/diagnostics";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 14,
      route: "diagnostics",
      theme: "light",
      capabilities: ["about.openProject", "diagnostics.export"],
      features: {
        about: {
          version: "0.2.0",
          license: "Proprietary",
          projectUrl: "https://github.com/felji/VibeOCR",
        },
        diagnostics: {
          supervisorStatus: "已就绪",
          protocolStatus: "客户端 v2 / Supervisor v2",
          milestones: [],
          deviceEvidence: [],
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

  it("merges about and diagnostics into one entry and redirects legacy #/about", async () => {
    window.location.hash = "#/about";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    // 宿主仍持久化旧 about 路由：RouteSync 对齐到 /diagnostics，与重定向目标一致。
    const viewState: AppViewState = {
      connected: true,
      revision: 15,
      route: "about",
      theme: "light",
      capabilities: [
        "about.openProject",
        "diagnostics.export",
        "diagnostics.copy",
      ],
      features: {
        about: { version: "0.2.0", license: "Proprietary", projectUrl: "" },
        diagnostics: {
          supervisorStatus: "正在连接",
          protocolStatus: "客户端 v2 / Supervisor 未知",
          milestones: ["T0"],
          deviceEvidence: ["[Paddle worker] [GPU] 回退到 CPU"],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    // 旧入口落到合并后的唯一页面，不再出现第二个内容重复页面。
    expect(
      await screen.findByRole("heading", { name: "关于与诊断" }),
    ).toBeVisible();
    expect(window.location.hash).toBe("#/diagnostics");
    expect(screen.queryByText("关于 VibeOCR")).not.toBeInTheDocument();
    // 导航不再标注“离线工作台”，也不虚构在线/离线模式。
    expect(screen.queryByText("离线工作台")).not.toBeInTheDocument();
    expect(
      screen.queryByRole("link", { name: "关于" }),
    ).not.toBeInTheDocument();
    // 产品语义的健康行保留；协议版本只出现在折叠技术详情里。
    expect(screen.getByText("识别服务")).toBeVisible();
    expect(screen.getByText("客户端 v2 / Supervisor 未知")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "复制诊断详情" }));
    expect(actions.run).toHaveBeenCalledWith({ type: "diagnostics.copy" });
    // 服务未就绪时修复入口可见并指向设置页的既有能力。
    await user.click(
      screen.getByRole("button", { name: "打开设置的修复入口" }),
    );
    expect(actions.navigate).toHaveBeenCalledWith("settings");
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
    // 随包基础能力展示已移除（纯视觉）：二维码能力不受设置页影响。
    expect(screen.queryByText("二维码与条形码")).not.toBeInTheDocument();
    expect(screen.queryByText("随包可用")).not.toBeInTheDocument();
    expect(screen.getByText(/默认识别类型在此设置/)).toBeVisible();

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
      screen.getByText(/当前识别服务未声明 ocr\.mineru-remote-api\.v1/),
    ).toBeVisible();
    // 本地模式在旧识别服务上仍可保存（它是缺省状态）。
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
      screen.queryByText(/识别服务未提供下载源目录/),
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

    // 随包能力展示已移除（纯视觉）：二维码能力不受设置页影响。
    expect(screen.queryByText("当前运行环境不可用")).not.toBeInTheDocument();
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
    // 设备证据属于排错信息：默认折叠在技术详情里，内容仍在文档中可展开。
    expect(
      screen.getByText("[Paddle worker] [GPU] 验证失败，回退到 CPU"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("设备决策与回退日志；不表示识别作业已成功执行。"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("技术详情（内部协议与排错证据）"),
    ).toBeInTheDocument();
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
      maintenanceStatus: "正在准备识别服务运行时",
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
    expect(screen.getByText("跟随默认（未读取）")).toBeVisible();
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
  it("shows runtime package counts and bounded text logs across remount without installing", async () => {
    window.location.hash = "#/settings";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState = {
      connected: true,
      revision: 1,
      route: "settings",
      theme: "light",
      runtimeLabel: "已连接",
      capabilities: ["runtime.environments"],
      features: {
        settings: {
          environments: [],
          environmentSupportsInstallProgress: true,
          environmentInstallProgress: {
            attempt_id: "attempt",
            seq: 11,
            environment_id: "env",
            timestamp: "2026-10-08T00:00:00Z",
            phase: "install",
            state: "running",
            current: "真实安装批次",
            dependency_total_known: true,
            download_files_total: 1,
            download_files_completed: 1,
            dependencies: [
              {
                name: "a",
                version: "1",
                download_state: "cached",
                install_state: "installed",
              },
              {
                name: "b",
                version: "2",
                download_state: "downloaded",
                install_state: "pending",
              },
            ],
          },
          environmentInstallLog: [
            "[较早输出已截断]",
            "<script>never execute</script>",
          ],
        },
      },
    } satisfies AppViewState;
    const first = render(<App actions={actions} viewState={viewState} />);
    expect(
      screen.getByRole("region", { name: "依赖安装进度" }),
    ).toHaveTextContent(
      "共 2 项依赖｜已复用 1 项｜新增下载文件 1/1 个｜批次已安装 1/2 项",
    );
    await userEvent.click(
      screen.getByText("实时命令输出（只读，最多保留 200 行，超限截断）"),
    );
    expect(screen.getByText(/never execute/)).toHaveTextContent(
      "<script>never execute</script>",
    );
    first.unmount();
    const second = render(
      <App actions={actions} viewState={{ ...viewState, revision: 2 }} />,
    );
    expect(screen.getByText(/当前：真实安装批次/)).toBeVisible();
    expect(actions.run).not.toHaveBeenCalled();
    const terminal = {
      ...viewState,
      revision: 3,
      features: {
        settings: {
          ...viewState.features.settings,
          environmentInstallProgress: {
            ...viewState.features.settings.environmentInstallProgress,
            phase: "complete",
            state: "succeeded",
            seq: 12,
            dependencies: [
              {
                name: "a",
                version: "1",
                download_state: "cached",
                install_state: "installed",
              },
              {
                name: "b",
                version: "2",
                download_state: "downloaded",
                install_state: "installed",
              },
            ],
          },
        },
      },
    };
    second.rerender(<App actions={actions} viewState={terminal} />);
    const panel = screen.getByRole("region", { name: "依赖安装进度" });
    expect(panel).toHaveAttribute("data-install-attempt", "attempt");
    expect(panel).toHaveAttribute("data-install-seq", "12");
    expect(panel).toHaveAttribute("data-install-state", "succeeded");
    expect(panel).toHaveAttribute("data-install-phase", "complete");
    expect(panel).toHaveAttribute("data-install-installed", "2");
    expect(panel).toHaveAttribute("data-install-total", "2");
    expect(panel).toHaveTextContent("批次已安装 2/2 项");
    expect(actions.run).not.toHaveBeenCalled();
    second.unmount();
  });

  it("requires explicit cleanup selection and confirmation while protecting shared resources", async () => {
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
      runtimeLabel: "connected",
      capabilities: ["runtime.environments"],
      features: {
        settings: {
          environmentSupportsCleanup: true,
          environmentCanCancelCleanup: true,
          environments: [],
          environmentCleanupPlan: {
            plan_id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            warning: "仅逻辑字节，不保证物理释放",
            items: [
              {
                id: "residual:known",
                category: "residual",
                label: "已知安装残留",
                environment_id: null,
                logical_bytes: 1024,
                can_clean: true,
                reason: "明确生产记录且无引用",
                paths: ["environments/known/revisions/2"],
                path_count: 1,
              },
              {
                id: "protected:model",
                category: "models",
                label: "共享模型",
                environment_id: null,
                logical_bytes: null,
                can_clean: false,
                reason: "两个保留环境仍需使用",
                paths: ["state/model-cache"],
                path_count: 1,
              },
            ],
          },
          environmentCleanupResult: {
            items: [
              {
                id: "previous",
                state: "failed",
                detail: "文件占用，重新检查继续",
                removed_logical_bytes: 10,
              },
            ],
          },
        },
      },
    };
    const { unmount } = render(<App viewState={viewState} actions={actions} />);
    expect(screen.getByRole("checkbox", { name: /共享模型/ })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "检查可清理项" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.previewEnvironmentCleanup",
    });
    await user.click(screen.getByRole("checkbox", { name: /已知安装残留/ }));
    await user.click(screen.getByRole("button", { name: "预览所选清理影响" }));
    expect(actions.run).not.toHaveBeenCalledWith(
      expect.objectContaining({ type: "settings.runEnvironmentCleanup" }),
    );
    expect(screen.getByText(/缓存以后可能需要重新下载/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "确认清理所选项目" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.runEnvironmentCleanup",
      planId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      itemIds: ["residual:known"],
    });
    await user.click(screen.getByRole("button", { name: "取消清理" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.cancelEnvironmentCleanup",
    });
    expect(screen.getByText(/文件占用，重新检查继续/)).toBeVisible();
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
          environmentRecipes: [
            {
              id: "rapidocr-cpu",
              displayName: "RapidOCR · CPU",
              configuredRecognitionTypes: ["text"],
              accelerator: "cpu",
              targetDevice: "cpu",
            },
          ],
          environmentHardware: { nvidiaDriverStatus: "unknown" },
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
    // 高级区默认折叠：先展开再操作原编辑器全部路径。
    expect(screen.queryByText("高级：环境与依赖管理")).not.toBeInTheDocument();
    // 快照已同步但无活动环境：展示默认配置“尚未启动”，不误写“尚未读取”，
    // 也不伪造就绪。
    expect(
      screen.getByText("默认运行环境：RapidOCR · CPU（尚未启动）"),
    ).toBeVisible();
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
    await user.click(screen.getByText("完整依赖与来源明细（只读）"));
    expect(screen.getByText(/锁定依赖（1 项）/)).toBeVisible();
    expect(
      screen.getByText(
        /计划：rapidocr-cpu · 目标文档（失败后重新准备）· 下载来源：TUNA PyPI 镜像/,
      ),
    ).toBeVisible();
    expect(
      screen.queryByLabelText("新环境名称（留空自动命名）"),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "继续准备依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.previewEnvironmentInstall",
      environmentId: "env-1",
      recipe: "rapidocr-cpu",
    });
    await user.click(screen.getByRole("button", { name: "移除环境" }));
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.deleteEnvironment",
      environmentId: "env-1",
    });
    await user.click(screen.getByRole("button", { name: "取消移除" }));
    // 重新预览隐藏旧计划，收到新计划后才允许确认。
    unmount();
    const restored = render(<App actions={actions} viewState={viewState} />);
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
    restored.unmount();
  });
  it("recommends catalog recipes with honest hardware gating and read-only reuse lookup", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const recipes = [
      {
        id: "rapidocr-cpu",
        displayName: "RapidOCR · CPU",
        configuredRecognitionTypes: ["text"],
        accelerator: "cpu",
        targetDevice: "cpu",
      },
      {
        id: "rapidocr+mineru-cpu",
        displayName: "RapidOCR + MinerU · CPU",
        configuredRecognitionTypes: ["text", "document"],
        accelerator: "cpu",
        targetDevice: "cpu",
      },
      {
        id: "rapidocr+mineru-cuda",
        displayName: "RapidOCR + MinerU · NVIDIA CUDA",
        configuredRecognitionTypes: ["text", "document"],
        accelerator: "nvidia_cuda",
        targetDevice: "cuda",
      },
    ];
    const settingsBase = {
      environments: [
        {
          id: "env-1",
          name: "文档环境",
          revision: 3,
          kind: "venv",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "not_started",
          configuredRecognitionTypes: ["text"],
          targetDevice: "cpu",
        },
      ],
      activeEnvironmentId: null,
      environmentRecipes: recipes,
      // 真正不支持时明确禁用并给出原因（AC3）：驱动过旧。
      environmentHardware: {
        nvidiaDriverStatus: "unsupported",
        nvidiaDriverReason: "nvidia_driver_incompatible",
        nvidiaDriverVersion: "527.00",
      },
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 10,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "运行环境",
      features: { settings: settingsBase },
    };
    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );
    // 选择即查询：默认用途/设备只读触发一次兼容查询，不创建不安装。
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "rapidocr-cpu",
      }),
    );
    expect(screen.getByText(/GPU 状态（Runtime 探测）：不支持/)).toBeVisible();
    // 目录分组来自 Runtime 投影，无前端臆造组合。
    expect(
      screen.getByText(/目标配置：RapidOCR · CPU（文字识别，设备 cpu/),
    ).toBeVisible();
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 11,
          features: {
            settings: {
              ...settingsBase,
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: "env-1",
                selectedEnvironmentRevision: 3,
                selectionReason: "deterministic_id_order",
              },
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(/可直接复用环境「文档环境」（修订 3，按稳定顺序选中）/),
    ).toBeVisible();
    // AC8：切换语义如实说明（重新启动并验证；运行中任务时被拒绝）。
    expect(
      screen.getAllByText(
        /切换会重新启动并验证识别服务；有运行中任务时切换会被拒绝/,
      ).length,
    ).toBeGreaterThan(0);
    await user.click(
      screen.getByRole("button", { name: "切换到此环境并启动验证" }),
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.switchEnvironment",
      environmentId: "env-1",
    });
    // active 且已验证时不伪重装：只说明现状，不再提供切换/准备按钮。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 12,
          features: {
            settings: {
              ...settingsBase,
              activeEnvironmentId: "env-1",
              environments: [
                {
                  ...settingsBase.environments[0],
                  serviceState: "ready",
                },
              ],
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: "env-1",
                selectedEnvironmentRevision: 3,
                selectionReason: "active_environment",
              },
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(/已是当前环境并通过启动验证，无需重装/),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "切换到此环境并启动验证" }),
    ).not.toBeInTheDocument();
    // 切换用途：旧选择失效 + 新配方的只读查询。
    await user.selectOptions(
      screen.getByLabelText("识别组件"),
      "rapidocr+mineru",
    );
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "rapidocr+mineru-cpu",
      }),
    );
    // 真正不支持的 GPU 配方明确禁用并给出原因，不发起查询。
    await user.selectOptions(screen.getByLabelText("加速方式"), "nvidia_cuda");
    expect(
      screen.getByText(
        /不支持：NVIDIA 驱动 527\.00 低于 CUDA 12\.x 下限（需 ≥ 528\.33）；该配置当前不可选。/,
      ),
    ).toBeVisible();
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.findCompatibleEnvironment",
      recipe: "rapidocr+mineru-cuda",
    });
    unmount();
  });
  it("prepares an unavailable recipe via auto-named create and a real preview without silent install", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const settingsBase = {
      environments: [],
      activeEnvironmentId: null,
      environmentRecipes: [
        {
          id: "rapidocr-cpu",
          displayName: "RapidOCR · CPU",
          configuredRecognitionTypes: ["text"],
          accelerator: "cpu",
          targetDevice: "cpu",
        },
        {
          id: "mineru-cpu",
          displayName: "MinerU · CPU",
          configuredRecognitionTypes: ["document"],
          accelerator: "cpu",
          targetDevice: "cpu",
        },
      ],
      environmentHardware: { nvidiaDriverStatus: "unknown" },
      environmentSources: [
        {
          id: "tuna-pypi",
          kind: "package_index",
          displayName: "TUNA PyPI 镜像",
          endpoint: "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
        },
      ],
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 20,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "运行环境",
      features: { settings: settingsBase },
    };
    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "rapidocr-cpu",
      }),
    );
    // 查询无命中：只读结果如实呈现“没有可直接复用”，准备按钮可用。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 21,
          features: {
            settings: {
              ...settingsBase,
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: null,
                selectedEnvironmentRevision: null,
                selectionReason: null,
              },
            },
          },
        }}
      />,
    );
    expect(screen.getByText(/没有可直接复用的已安装环境/)).toBeVisible();
    expect(screen.getByText(/自动准备目标环境并预览依赖/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "准备此配置" }));
    // 自动命名沿目录展示名；创建成功后对同一环境真实预览（跟随配置）。
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.prepareEnvironment",
      recipe: "rapidocr-cpu",
    });
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 22,
          features: {
            settings: {
              ...settingsBase,
              environments: [
                {
                  id: "env-new",
                  name: "RapidOCR · CPU",
                  revision: 1,
                  kind: "venv",
                  status: "empty",
                  pythonState: "ready",
                  dependencyState: "empty",
                  engineState: "unverified",
                  modelState: "not_checked",
                  serviceState: "not_started",
                  configuredRecognitionTypes: [],
                },
              ],
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: null,
                selectedEnvironmentRevision: null,
                selectionReason: null,
              },
            },
          },
        }}
      />,
    );
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.createEnvironment",
      name: "RapidOCR · CPU",
    });
    // 计划回显在推荐区：新增依赖如实列出，由用户确认安装。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 23,
          features: {
            settings: {
              ...settingsBase,
              environments: [
                {
                  id: "env-new",
                  name: "RapidOCR · CPU",
                  revision: 1,
                  kind: "venv",
                  status: "empty",
                  pythonState: "ready",
                  dependencyState: "empty",
                  engineState: "unverified",
                  modelState: "not_checked",
                  serviceState: "not_started",
                  configuredRecognitionTypes: [],
                },
              ],
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: null,
                selectedEnvironmentRevision: null,
                selectionReason: null,
              },
              environmentPlan: {
                planId: "p-9",
                environmentId: "env-new",
                environmentRevision: 1,
                recipe: "rapidocr-cpu",
                requestedRecipe: "rapidocr-cpu",
                sourceIds: ["tuna-pypi"],
                requestedSourceIds: null,
                dependencies: ["rapidocr==3.9.2"],
              },
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(
        /计划：rapidocr-cpu · 目标RapidOCR · CPU（空环境准备）· 下载来源：TUNA PyPI 镜像/,
      ),
    ).toBeVisible();
    // AC8：安装只写入目标环境、不停止当前识别服务（活动保护以 Runtime 为准）。
    expect(
      screen.getByText(/依赖安装只写入此目标环境，不会停止当前识别服务/),
    ).toBeVisible();
    // 同一待准备未确认前不再提供重复 create：指向既有高级确认。
    expect(
      screen.queryByRole("button", { name: "准备此配置" }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText(/高级：环境与依赖/)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "确认安装依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmEnvironmentInstall",
      planId: "p-9",
    });
    // 选择失效后仍可新建：切到另一用途后重新可以准备（不重复旧待准备）。
    await user.selectOptions(screen.getByLabelText("识别组件"), "mineru");
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 24,
          features: {
            settings: {
              ...settingsBase,
              environments: [
                {
                  id: "env-new",
                  name: "RapidOCR · CPU",
                  revision: 1,
                  kind: "venv",
                  status: "empty",
                  pythonState: "ready",
                  dependencyState: "empty",
                  engineState: "unverified",
                  modelState: "not_checked",
                  serviceState: "not_started",
                  configuredRecognitionTypes: [],
                },
              ],
              environmentCompatibility: {
                recipe: "mineru-cpu",
                selectedEnvironmentId: null,
                selectedEnvironmentRevision: null,
                selectionReason: null,
              },
            },
          },
        }}
      />,
    );
    await waitFor(() =>
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.findCompatibleEnvironment",
        recipe: "mineru-cpu",
      }),
    );
    expect(
      screen.getByRole("button", { name: "准备此配置" }),
    ).toBeInTheDocument();
    unmount();
  });
  it("keeps compatibility lookup single-flight with explicit retry and no stale overwrite", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const settingsBase = {
      environments: [
        {
          id: "env-1",
          name: "文档环境",
          revision: 3,
          kind: "venv",
          status: "installed",
          pythonState: "ready",
          dependencyState: "installed",
          engineState: "ready",
          modelState: "ready",
          serviceState: "not_started",
          configuredRecognitionTypes: ["text"],
          targetDevice: "cpu",
        },
      ],
      activeEnvironmentId: null,
      environmentRecipes: [
        {
          id: "rapidocr-cpu",
          displayName: "RapidOCR · CPU",
          configuredRecognitionTypes: ["text"],
          accelerator: "cpu",
          targetDevice: "cpu",
        },
        {
          id: "mineru-cpu",
          displayName: "MinerU · CPU",
          configuredRecognitionTypes: ["document"],
          accelerator: "cpu",
          targetDevice: "cpu",
        },
      ],
      environmentHardware: { nvidiaDriverStatus: "unknown" },
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 30,
      route: "settings",
      theme: "light",
      capabilities: ["runtime.environments"],
      runtimeLabel: "运行环境",
      features: { settings: settingsBase },
    };
    const countQueries = (recipe: string) =>
      vi
        .mocked(actions.run)
        .mock.calls.filter(
          ([action]) =>
            action?.type === "settings.findCompatibleEnvironment" &&
            action?.recipe === recipe,
        ).length;
    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );
    await waitFor(() =>
      expect(countQueries("rapidocr-cpu")).toBeGreaterThanOrEqual(1),
    );
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          features: {
            settings: {
              ...settingsBase,
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: null,
              },
            },
          },
        }}
      />,
    );
    // 原生 Select 会派发同值 change；不应废弃已查询的默认配置。
    fireEvent.change(screen.getByLabelText("识别组件"), {
      target: { value: "rapidocr" },
    });
    fireEvent.change(screen.getByLabelText("加速方式"), {
      target: { value: "cpu" },
    });
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
    expect(countQueries("rapidocr-cpu")).toBe(1);
    expect(screen.getByRole("button", { name: "准备此配置" })).toBeVisible();
    // 查询失败（宿主未回传结果）+ busy/状态推送多次重渲染：同一配方不得
    // 自动重发（每次查询都会冻结一个 Installer 子进程）。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 31,
          features: {
            settings: { ...settingsBase, environmentBusy: true },
          },
        }}
      />,
    );
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 32,
          features: {
            settings: {
              ...settingsBase,
              environmentStatus: "查询失败：运行环境操作失败。",
            },
          },
        }}
      />,
    );
    rerender(
      <App actions={actions} viewState={{ ...viewState, revision: 33 }} />,
    );
    expect(countQueries("rapidocr-cpu")).toBe(1);
    // 失败后如实提示并允许显式重试。
    expect(screen.getByText(/兼容性查询未完成/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "重新查询兼容环境" }));
    expect(countQueries("rapidocr-cpu")).toBe(2);
    // 迟到的旧选择结果不覆盖新选择：切到文档解析后，旧 rapidocr-cpu 结果
    // 只按配方 id 绑定，不冒充当前选择的查询结果。
    await user.selectOptions(screen.getByLabelText("识别组件"), "mineru");
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 34,
          features: {
            settings: {
              ...settingsBase,
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: "env-1",
                selectedEnvironmentRevision: 3,
                selectionReason: "deterministic_id_order",
              },
            },
          },
        }}
      />,
    );
    expect(
      screen.queryByText(/可直接复用环境「文档环境」/),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "切换到此环境并启动验证" }),
    ).not.toBeInTheDocument();
    await waitFor(() =>
      expect(countQueries("mineru-cpu")).toBeGreaterThanOrEqual(1),
    );
    // 切回文字识别：旧结果（未选中任何环境）如实呈现“没有可直接复用”。
    await user.selectOptions(screen.getByLabelText("识别组件"), "rapidocr");
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 35,
          features: {
            settings: {
              ...settingsBase,
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: null,
                selectedEnvironmentRevision: null,
                selectionReason: null,
              },
            },
          },
        }}
      />,
    );
    expect(screen.getByText(/没有可直接复用的已安装环境/)).toBeVisible();
    const installedBase = countQueries("rapidocr-cpu");
    // 安装进行中：宿主已清空兼容结果且环境修订推进，但 busy 时不补发。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 36,
          features: {
            settings: {
              ...settingsBase,
              environmentBusy: true,
              environments: [{ ...settingsBase.environments[0], revision: 4 }],
            },
          },
        }}
      />,
    );
    expect(countQueries("rapidocr-cpu")).toBe(installedBase);
    expect(screen.getByText(/正在查询可复用环境/)).toBeVisible();
    // 安装成功结束：兼容结果仍被宿主清空且环境状态已变——自动补发一次新
    // 查询（成功安装后需要新的兼容查询），不永久卡在“查询未完成”；
    // 重复状态推送也不再多发。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 37,
          features: {
            settings: {
              ...settingsBase,
              environments: [{ ...settingsBase.environments[0], revision: 4 }],
            },
          },
        }}
      />,
    );
    await waitFor(() =>
      expect(countQueries("rapidocr-cpu")).toBe(installedBase + 1),
    );
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 38,
          features: {
            settings: {
              ...settingsBase,
              environments: [{ ...settingsBase.environments[0], revision: 4 }],
            },
          },
        }}
      />,
    );
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 39,
          features: {
            settings: {
              ...settingsBase,
              environments: [{ ...settingsBase.environments[0], revision: 4 }],
            },
          },
        }}
      />,
    );
    expect(countQueries("rapidocr-cpu")).toBe(installedBase + 1);
    // 新结果绑定后如实指向修订 4 的可复用环境，不再卡在“查询未完成”。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 40,
          features: {
            settings: {
              ...settingsBase,
              environments: [{ ...settingsBase.environments[0], revision: 4 }],
              environmentCompatibility: {
                recipe: "rapidocr-cpu",
                selectedEnvironmentId: "env-1",
                selectedEnvironmentRevision: 4,
                selectionReason: "deterministic_id_order",
              },
            },
          },
        }}
      />,
    );
    expect(
      screen.getByText(/可直接复用环境「文档环境」（修订 4，按稳定顺序选中）/),
    ).toBeVisible();
    expect(screen.queryByText(/兼容性查询未完成/)).not.toBeInTheDocument();
    unmount();
  });
  describe("environment preparation invalidation", () => {
    const base: AppViewState = {
      connected: true,
      revision: 1,
      route: "settings",
      theme: "light",
      runtimeLabel: "运行环境",
      capabilities: ["runtime.environments"],
      features: {
        settings: {
          environments: [],
          environmentRecipes: [
            {
              id: "rapidocr-cpu",
              displayName: "RapidOCR · CPU",
              configuredRecognitionTypes: ["text"],
              accelerator: "cpu",
              targetDevice: "cpu",
            },
          ],
          environmentPackageSourceIds: ["tuna-pypi"],
        },
      },
    };
    const settings = base.features.settings as Record<string, unknown>;
    const plan = {
      planId: "plan-Y",
      environmentId: "Y",
      environmentRevision: 1,
      recipe: "rapidocr-cpu",
      requestedRecipe: "rapidocr-cpu",
      sourceIds: ["tuna-pypi"],
      dependencies: ["rapidocr==3.9.2"],
    };
    const environment = {
      id: "X",
      name: "旧目标",
      revision: 1,
      kind: "venv",
      status: "empty",
      pythonState: "ready",
      dependencyState: "empty",
    };

    it("queries once after source save or plan cancel without environment changes", async () => {
      window.location.hash = "#/settings";
      const user = userEvent.setup();
      const actions: AppActions = {
        run: vi.fn().mockResolvedValue(true),
        navigate: vi.fn(),
        setTheme: vi.fn(),
      };
      const { rerender, unmount } = render(
        <App actions={actions} viewState={base} />,
      );
      const queries = () =>
        vi
          .mocked(actions.run)
          .mock.calls.filter(
            ([action]) => action.type === "settings.findCompatibleEnvironment",
          ).length;
      await waitFor(() => expect(queries()).toBe(1));
      const withResult = {
        ...settings,
        environmentCompatibility: {
          recipe: "rapidocr-cpu",
          selectedEnvironmentId: null,
        },
      };
      rerender(
        <App
          actions={actions}
          viewState={{ ...base, features: { settings: withResult } }}
        />,
      );
      await user.click(screen.getByRole("button", { name: "保存下载来源" }));
      rerender(
        <App
          actions={actions}
          viewState={{
            ...base,
            features: { settings: { ...settings, environmentBusy: true } },
          }}
        />,
      );
      rerender(<App actions={actions} viewState={base} />);
      await waitFor(() => expect(queries()).toBe(2));
      rerender(<App actions={actions} viewState={{ ...base, revision: 2 }} />);
      expect(queries()).toBe(2);
      // 普通失败不循环重查；只有另一个明确失效动作允许新的一次查询。
      rerender(
        <App
          actions={actions}
          viewState={{
            ...base,
            features: {
              settings: {
                ...withResult,
                environments: [{ ...environment, id: "Y" }],
                environmentPlan: plan,
              },
            },
          }}
        />,
      );
      const beforeCancel = queries();
      await user.click(screen.getByRole("button", { name: /^取消$/ }));
      expect(
        screen.queryByRole("button", { name: "确认安装依赖" }),
      ).not.toBeInTheDocument();
      const afterCancel = {
        ...settings,
        environments: [{ ...environment, id: "Y" }],
      };
      rerender(
        <App
          actions={actions}
          viewState={{ ...base, features: { settings: afterCancel } }}
        />,
      );
      await waitFor(() => expect(queries()).toBe(beforeCancel + 1));
      rerender(
        <App
          actions={actions}
          viewState={{
            ...base,
            revision: 3,
            features: { settings: afterCancel },
          }}
        />,
      );
      expect(queries()).toBe(beforeCancel + 1);
      unmount();
    });

    it("keeps the current query single-flight when an older generation settles", async () => {
      window.location.hash = "#/settings";
      const user = userEvent.setup();
      let finishOld = () => {};
      let finishCurrent = () => {};
      const old = new Promise<boolean>((resolve) => {
        finishOld = () => resolve(true);
      });
      const current = new Promise<boolean>((resolve) => {
        finishCurrent = () => resolve(true);
      });
      let queries = 0;
      const actions: AppActions = {
        run: vi.fn((action) =>
          action.type === "settings.findCompatibleEnvironment"
            ? ++queries === 1
              ? old
              : queries === 2
                ? current
                : Promise.resolve(false)
            : Promise.resolve(true),
        ),
        navigate: vi.fn(),
        setTheme: vi.fn(),
      };
      const { rerender, unmount } = render(
        <App actions={actions} viewState={base} />,
      );
      await waitFor(() => expect(queries).toBe(1));
      await user.click(screen.getByRole("button", { name: "保存下载来源" }));
      await waitFor(() => expect(queries).toBe(2));
      await act(async () => {
        finishOld();
        await old;
      });
      await user.click(
        screen.getByRole("button", { name: "重新查询兼容环境" }),
      );
      expect(queries).toBe(2);
      await act(async () => {
        finishCurrent();
        await current;
      });
      await user.click(
        screen.getByRole("button", { name: "重新查询兼容环境" }),
      );
      expect(queries).toBe(3);
      rerender(<App actions={actions} viewState={{ ...base, revision: 2 }} />);
      expect(queries).toBe(3);
      unmount();
    });

    it("clears failed list target before preparing the recommended environment", async () => {
      window.location.hash = "#/settings";
      const user = userEvent.setup();
      const actions: AppActions = {
        run: vi.fn(
          async (action) =>
            action.type !== "settings.previewEnvironmentInstall",
        ),
        navigate: vi.fn(),
        setTheme: vi.fn(),
      };
      const initial = {
        ...settings,
        environments: [environment],
        environmentCompatibility: {
          recipe: "rapidocr-cpu",
          selectedEnvironmentId: null,
        },
      };
      const { rerender, unmount } = render(
        <App
          actions={actions}
          viewState={{ ...base, features: { settings: initial } }}
        />,
      );
      await user.click(screen.getByRole("button", { name: "继续准备依赖" }));
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.previewEnvironmentInstall",
        environmentId: "X",
        recipe: "rapidocr-cpu",
      });
      rerender(
        <App
          actions={actions}
          viewState={{
            ...base,
            features: {
              settings: { ...initial, environmentStatus: "预览失败" },
            },
          }}
        />,
      );
      await user.click(screen.getByRole("button", { name: "准备此配置" }));
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.prepareEnvironment",
        recipe: "rapidocr-cpu",
      });
      rerender(
        <App
          actions={actions}
          viewState={{
            ...base,
            features: {
              settings: {
                ...initial,
                environments: [
                  environment,
                  { ...environment, id: "Y", name: "自动目标" },
                ],
                environmentPlan: plan,
              },
            },
          }}
        />,
      );
      expect(
        screen.getByRole("button", { name: "确认安装依赖" }),
      ).toBeVisible();
      await user.click(screen.getByRole("button", { name: "确认安装依赖" }));
      expect(actions.run).toHaveBeenCalledWith({
        type: "settings.confirmEnvironmentInstall",
        planId: "plan-Y",
      });
      unmount();
    });

    it("shows wheel dependency names in summary and preserves exact URLs only in details", async () => {
      window.location.hash = "#/settings";
      const user = userEvent.setup();
      const actions: AppActions = {
        run: vi.fn().mockResolvedValue(true),
        navigate: vi.fn(),
        setTheme: vi.fn(),
      };
      const torch =
        "torch @ https://download.pytorch.org/whl/cu126/torch-2.8.0%2Bcu126-cp312-cp312-win_amd64.whl";
      const torchvision =
        "torchvision @ https://download.pytorch.org/whl/cu126/torchvision-0.23.0%2Bcu126-cp312-cp312-win_amd64.whl";
      const { unmount } = render(
        <App
          actions={actions}
          viewState={{
            ...base,
            features: {
              settings: {
                ...settings,
                environments: [{ ...environment, id: "Y" }],
                environmentPlan: {
                  ...plan,
                  dependencies: ["rapidocr==3.9.2", torch, torchvision],
                },
              },
            },
          }}
        />,
      );
      expect(
        screen.getByText(/主要组件：rapidocr==3.9.2、torch、torchvision；/),
      ).toBeVisible();
      expect(
        screen.getByText(`${"rapidocr==3.9.2"}、${torch}、${torchvision}`),
      ).not.toBeVisible();
      await user.click(screen.getByText("完整依赖与来源明细（只读）"));
      expect(
        screen.getByText(`${"rapidocr==3.9.2"}、${torch}、${torchvision}`),
      ).toBeVisible();
      unmount();
    });
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
          environmentRecipes: [
            {
              id: "rapidocr-cpu",
              displayName: "RapidOCR · CPU",
              configuredRecognitionTypes: ["text"],
              accelerator: "cpu",
              targetDevice: "cpu",
            },
            {
              id: "mineru-cpu",
              displayName: "MinerU · CPU",
              configuredRecognitionTypes: ["document"],
              accelerator: "cpu",
              targetDevice: "cpu",
            },
          ],
          environmentHardware: { nvidiaDriverStatus: "unknown" },
          environmentPackageSourceIds: ["tuna-pypi"],
          environmentPlan: {
            planId: "combined-plan",
            environmentId: "env-1",
            environmentRevision: 2,
            requestedRecipe: "mineru-cpu",
            recipe: "rapidocr+mineru-cpu",
            sourceIds: ["tuna-pypi"],
            requestedSourceIds: null,
            dependencies: ["rapidocr==3.9.2", "mineru==4.0.10"],
          },
        },
      },
    };
    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );
    expect(screen.queryByText("高级：环境与依赖管理")).not.toBeInTheDocument();
    // 重进页面从宿主 requestedRecipe 恢复选择，Runtime 实际合并配方仍可确认。
    expect(screen.getByLabelText("识别组件")).toHaveValue("mineru");
    expect(screen.getByRole("button", { name: "确认安装依赖" })).toBeVisible();
    await user.selectOptions(screen.getByLabelText("识别组件"), "rapidocr");
    expect(
      screen.queryByRole("button", { name: "确认安装依赖" }),
    ).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText("识别组件"), "mineru");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.invalidateEnvironmentPlan",
    });
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
                  kind: "paddleocr_model_registry",
                  id: "paddleocr-huggingface",
                  displayName: "Hugging Face",
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
              overrideSourceIds: ["paddleocr-modelscope"],
              resolvedSources: [
                {
                  kind: "package_index",
                  id: "tuna-pypi",
                  displayName: "TUNA PyPI 镜像",
                  origin: "global_default",
                },
                {
                  kind: "paddleocr_model_registry",
                  id: "paddleocr-modelscope",
                  displayName: "ModelScope",
                  origin: "environment_override",
                },
              ],
            },
          ],
          activeEnvironmentId: "env-a",
          environmentRecipes: [
            {
              id: "rapidocr-cpu",
              displayName: "RapidOCR · CPU",
              configuredRecognitionTypes: ["text"],
              accelerator: "cpu",
              targetDevice: "cpu",
            },
          ],
          environmentHardware: { nvidiaDriverStatus: "unknown" },
          environmentSources: [
            {
              id: "tuna-pypi",
              isDefault: true,
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
              id: "paddleocr-huggingface",
              kind: "paddleocr_model_registry",
              displayName: "Hugging Face",
              endpoint: "https://huggingface.co",
            },
            {
              id: "paddleocr-modelscope",
              kind: "paddleocr_model_registry",
              displayName: "ModelScope",
              endpoint: "https://www.modelscope.cn",
            },
            {
              id: "paddleocr-bos",
              kind: "paddleocr_model_registry",
              displayName: "百度 BOS",
              endpoint: "https://paddle-model-ecology.bj.bcebos.com",
            },
            {
              id: "mineru-huggingface",
              kind: "mineru_model_registry",
              displayName: "Hugging Face",
              endpoint: "https://huggingface.co",
            },
            {
              id: "mineru-modelscope",
              kind: "mineru_model_registry",
              displayName: "ModelScope",
              endpoint: "https://www.modelscope.cn",
            },
          ],
          environmentDefaultSourceIds: [
            "tuna-pypi",
            "paddleocr-huggingface",
            "mineru-huggingface",
          ],
          environmentResolvedDefaultSources: [
            {
              kind: "package_index",
              id: "tuna-pypi",
              displayName: "TUNA PyPI 镜像",
              origin: "product_default",
            },
            {
              kind: "paddleocr_model_registry",
              id: "paddleocr-huggingface",
              displayName: "Hugging Face",
              origin: "product_default",
            },
            {
              kind: "mineru_model_registry",
              id: "mineru-huggingface",
              displayName: "Hugging Face",
              origin: "product_default",
            },
          ],
          environmentUnknownDefaultSourceIds: [],
        },
      },
    };
    const { unmount, rerender } = render(
      <App actions={actions} viewState={viewState} />,
    );
    // 来源收敛为一个简单设置：目录展示名直出，默认值跟随 Runtime 解析；
    // 不再提供“来源配置/全局默认来源”两处重复入口与 override 三层。
    const packages = screen.getByLabelText("依赖包来源");
    expect(packages).toHaveValue("tuna-pypi");
    expect(
      within(packages).getByRole("option", { name: "TUNA PyPI 镜像（默认）" }),
    ).toBeInTheDocument();
    const model = screen.getByLabelText("模型来源");
    expect(model).toHaveValue("huggingface");
    expect(within(model).getAllByRole("option")).toHaveLength(2);
    expect(
      within(model).getByRole("option", { name: "Hugging Face" }),
    ).toBeInTheDocument();
    expect(
      within(model).getByRole("option", { name: "ModelScope（魔搭）" }),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText("本环境依赖包来源")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("全局依赖包来源")).not.toBeInTheDocument();
    expect(
      screen.queryByText("来源配置（默认、覆盖与已生效值）"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText("全局默认来源（所有环境继承）"),
    ).not.toBeInTheDocument();
    // 管理模式下 DOWNLOADS 卡片整体不渲染（来源已收敛进运行环境面板）。
    expect(screen.queryByText("下载来源")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Python 包下载源")).not.toBeInTheDocument();

    // 保存只写全局下载来源：依赖与模型一次保存，不按环境覆盖。
    await user.selectOptions(packages, "pypi");
    await user.selectOptions(model, "modelscope");
    await user.click(screen.getByRole("button", { name: "保存下载来源" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setEnvironmentSources",
      packageSourceId: "pypi",
      paddleocrModelSourceId: "paddleocr-modelscope",
      mineruModelSourceId: "mineru-modelscope",
    });

    // 引擎间不一致的存量配置如实标注，选择后保存统一为同一提供方。
    rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 41,
          features: {
            settings: {
              ...(viewState.features.settings as Record<string, unknown>),
              environmentDefaultSourceIds: [
                "tuna-pypi",
                "paddleocr-bos",
                "mineru-modelscope",
              ],
            },
          },
        }}
      />,
    );
    expect(model).toHaveValue("");
    expect(
      screen.getByText(/当前 PaddleOCR 与 MinerU 的模型来源不一致/),
    ).toBeVisible();
    await user.selectOptions(model, "huggingface");
    await user.click(screen.getByRole("button", { name: "保存下载来源" }));
    expect(
      vi
        .mocked(actions.run)
        .mock.calls.filter(
          ([action]) => action.type === "settings.setEnvironmentSources",
        )
        .at(-1)?.[0],
    ).toEqual({
      type: "settings.setEnvironmentSources",
      packageSourceId: "tuna-pypi",
      paddleocrModelSourceId: "paddleocr-huggingface",
      mineruModelSourceId: "mineru-huggingface",
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
          environmentRecipes: [
            {
              id: "rapidocr-cpu",
              displayName: "RapidOCR · CPU",
              configuredRecognitionTypes: ["text"],
              accelerator: "cpu",
              targetDevice: "cpu",
            },
          ],
          environmentHardware: { nvidiaDriverStatus: "unknown" },
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
    expect(screen.queryByText("高级：环境与依赖管理")).not.toBeInTheDocument();
    // 统一选择后预览不再有“本次依赖下载源”按次覆盖入口。
    expect(screen.queryByLabelText("本次依赖下载源")).not.toBeInTheDocument();

    // 预览跟随已保存的下载来源：不携带 sourceId。
    await user.click(screen.getByRole("button", { name: "继续准备依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.previewEnvironmentInstall",
      environmentId: "env-1",
      recipe: "rapidocr-cpu",
    });

    // 计划只展示实际使用的下载来源与资产分类。
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
                planId: "plan-follow",
                environmentId: "env-1",
                environmentRevision: 1,
                recipe: "rapidocr-cpu",
                requestedRecipe: "rapidocr-cpu",
                sourceIds: ["tuna-pypi"],
                requestedSourceIds: null,
                dependencies: ["rapidocr==3.9.2"],
                sources: [
                  {
                    id: "tuna-pypi",
                    kind: "package_index",
                    displayName: "TUNA PyPI 镜像",
                    endpoint:
                      "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
                    requested: false,
                    inheritedFrom: "global_default",
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
        /计划：rapidocr-cpu · 目标空白（空环境准备）· 下载来源：TUNA PyPI 镜像/,
      ),
    ).toBeVisible();
    await user.click(screen.getByText("完整依赖与来源明细（只读）"));
    expect(screen.getByText(/TUNA PyPI 镜像（依赖包来源/)).toBeVisible();
    expect(screen.getByText(/已缓存模型直接复用/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "确认安装依赖" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.confirmEnvironmentInstall",
      planId: "plan-follow",
    });
    unmount();
  });
  it("shows the resolved runtime default and routes to settings from the inherit option", async () => {
    window.location.hash = "#/recognition";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 61,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.engine"],
      features: {
        recognition: {
          isBusy: false,
          statusCode: "recognition.ready",
          taskEngine: null,
          engines: [
            {
              engine: "windows_text",
              displayName: "Windows OCR（系统内置）",
              selected: true,
              isTaskOverride: false,
              isDefault: true,
              availability: "ready",
              requiresDownload: false,
            },
            {
              engine: "paddle_text",
              displayName: "通用 OCR（PaddleOCR）",
              selected: false,
              isTaskOverride: false,
              availability: "preparation_required",
              requiresDownload: true,
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    // 继承选项同时展示来源与实际有效模式。
    expect(
      screen.getByRole("option", {
        name: "跟随默认（Windows OCR（系统内置））",
      }),
    ).toBeInTheDocument();
    // 可点击入口直达设置页的默认识别模式区。
    await user.click(screen.getByRole("button", { name: "修改默认识别类型" }));
    expect(actions.navigate).toHaveBeenCalledWith("settings");
    unmount();
  });

  it("keeps showing the committed default on the inherit option while overridden", () => {
    window.location.hash = "#/recognition";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 66,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.engine"],
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
              isDefault: false,
              availability: "ready",
              requiresDownload: false,
            },
            {
              engine: "windows_text",
              displayName: "Windows OCR（系统内置）",
              selected: false,
              isTaskOverride: false,
              isDefault: true,
              availability: "ready",
              requiresDownload: false,
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    // 本次覆盖后默认仍已知：继承项继续展示已提交默认，不退回“未读取”。
    expect(
      screen.getByRole("option", {
        name: "跟随默认（Windows OCR（系统内置））",
      }),
    ).toBeInTheDocument();
    unmount();
  });

  it("labels a non-ready runtime default with its availability on the inherit option", () => {
    window.location.hash = "#/batch";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 62,
      route: "batch",
      theme: "light",
      capabilities: ["recognition.engine"],
      features: {
        batch: {
          isRunning: false,
          itemCount: 0,
          completedCount: 0,
          failedCount: 0,
          taskEngine: null,
          engines: [
            {
              engine: "mineru_document",
              displayName: "深度文档解析（MinerU）",
              selected: true,
              isTaskOverride: false,
              availability: "preparation_required",
              requiresDownload: true,
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(
      screen.getByRole("option", {
        name: "跟随默认（深度文档解析（MinerU），需准备依赖）",
      }),
    ).toBeInTheDocument();
    unmount();
  });

  it("saves the default recognition mode from the settings panel", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 63,
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
          pendingBackend: "cpu",
          sources: [],
          features: [],
          defaultRecognitionMode: {
            supported: true,
            modeId: "rapid_text",
            stored: true,
          },
          recognitionModes: [
            {
              id: "rapid_text",
              displayName: "快速 OCR（RapidOCR）",
              availability: "ready",
            },
            {
              id: "windows_text",
              displayName: "Windows OCR（系统内置）",
              availability: "ready",
            },
            {
              id: "paddle_text",
              displayName: "通用 OCR（PaddleOCR）",
              availability: "preparation_required",
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    const selector = screen.getByLabelText("默认识别类型");
    expect(selector).toHaveValue("rapid_text");
    // 未就绪模式不可选为默认，且可用性如实标注。
    expect(
      screen.getByRole("option", {
        name: /通用 OCR（PaddleOCR）（需准备依赖）/,
      }),
    ).toBeDisabled();
    await user.selectOptions(selector, "windows_text");
    await user.click(screen.getByRole("button", { name: "保存默认识别模式" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setDefaultRecognitionMode",
      mode: "windows_text",
    });
    expect(screen.getByText(/已在运行\/排队的任务参数不受影响/)).toBeVisible();
    unmount();
  });

  it("keeps the default recognition mode read-only on backends without the capability", () => {
    window.location.hash = "#/settings";
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 64,
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
          pendingBackend: "cpu",
          sources: [],
          features: [],
          defaultRecognitionMode: {
            supported: false,
            modeId: null,
            stored: false,
          },
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(
      screen.getByText(/当前识别服务未声明 ocr\.default-recognition-mode\.v1/),
    ).toBeVisible();
    expect(screen.getByText(/按快速 OCR（RapidOCR）执行/)).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "保存默认识别模式" }),
    ).not.toBeInTheDocument();
    unmount();
  });

  it("marks an unparsable stored default and routes recovery through saving a valid mode", async () => {
    window.location.hash = "#/settings";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn().mockResolvedValue(true),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const viewState: AppViewState = {
      connected: true,
      revision: 65,
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
          pendingBackend: "cpu",
          sources: [],
          features: [],
          defaultRecognitionMode: {
            supported: true,
            // Runtime 对显式 null/非字符串持久值原样回显。
            modeId: null,
            stored: true,
          },
          recognitionModes: [
            {
              id: "rapid_text",
              displayName: "快速 OCR（RapidOCR）",
              availability: "ready",
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = render(<App actions={actions} viewState={viewState} />);

    expect(
      screen.getByRole("option", { name: "请重新选择（当前值无法解析）" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/保存的默认值无法解析，未显式选择的任务会拒绝提交/),
    ).toBeVisible();
    const selector = screen.getByLabelText("默认识别类型");
    expect(selector).toHaveValue("");
    await user.selectOptions(selector, "rapid_text");
    // 修复项选择后 selector 真实回显新值，不被无效旧值强制回空白。
    expect(selector).toHaveValue("rapid_text");
    await user.click(screen.getByRole("button", { name: "保存默认识别模式" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setDefaultRecognitionMode",
      mode: "rapid_text",
    });
    unmount();
  });
});
