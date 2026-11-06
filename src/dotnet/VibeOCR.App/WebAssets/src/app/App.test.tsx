import {
  act,
  cleanup,
  fireEvent,
  render as renderImmediate,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

import { App, type AppActions, type AppViewState } from "./App";

Object.assign(globalThis, { NodeFilter: window.NodeFilter });

// 先完成真实页面模块加载，再验证路由渲染；冷启动编译不占用 DOM 断言的等待窗口。
beforeAll(async () => {
  await Promise.all([
    import("../features/RecognitionPage"),
    import("../features/ImageEditPage"),
    import("../features/BatchPage"),
    import("../features/PdfPage"),
    import("../features/QrCodePage"),
    import("../features/SettingsPage"),
    import("../features/DiagnosticsPage"),
  ]);
});

afterEach(cleanup);

async function renderLoaded(element: React.ReactNode) {
  const result = renderImmediate(element);
  await waitFor(() =>
    expect(screen.queryByLabelText("页面加载中")).not.toBeInTheDocument(),
  );
  return {
    ...result,
    rerender: async (next: React.ReactNode) => {
      if (vi.isFakeTimers()) {
        act(() => {
          result.rerender(next);
        });
        expect(screen.queryByLabelText("页面加载中")).not.toBeInTheDocument();
        return;
      }
      await act(async () => {
        result.rerender(next);
      });
      await waitFor(() =>
        expect(screen.queryByLabelText("页面加载中")).not.toBeInTheDocument(),
      );
    },
  };
}

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
    const { rerender } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    expect(
      await screen.findByRole("heading", { name: "截图现场编辑" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("complementary", { name: "主导航" }),
    ).not.toBeInTheDocument();
    await rerender(
      <App
        actions={actions}
        viewState={{ ...viewState, revision: 2, route: "pdf" }}
      />,
    );
    expect(
      screen.getByRole("heading", { name: "截图现场编辑" }),
    ).toBeInTheDocument();
    expect(window.location.hash).toBe("#/imageEdit?scene=1");
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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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

  it("restores choose-button focus after the open image operation finishes", async () => {
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
      capabilities: [],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { rerender, unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    // 受控 hasFocus：代表真实宿主在系统选择器取消后的窗口焦点状态
    // （titlebar 取证已证实 focused=true），不依赖 JSDOM 默认值。
    const hasFocus = vi.spyOn(document, "hasFocus").mockReturnValue(true);
    try {
      const user = userEvent.setup();
      const choose = await screen.findByRole("button", { name: "选择图片" });
      await user.click(choose);
      expect(actions.run).toHaveBeenCalledWith({
        type: "imageEdit.selectImage",
      });
      // 系统选择器打开会夺走文档焦点：在按钮仍启用时显式 blur 模拟（JSDOM
      // 对 disabled 元素的 blur 无效，且不因 disabled 真实失焦）。
      act(() => choose.blur());
      expect(document.activeElement).toBe(document.body);
      await rerender(
        <App
          actions={actions}
          viewState={{
            ...viewState,
            revision: 2,
            features: { imageEdit: { isBusy: true } },
          }}
        />,
      );
      expect(choose).toBeDisabled();
      expect(document.activeElement).toBe(document.body);
      // busy=false（成功或取消）：恢复到可见的“选择图片”按钮；旧实现停在
      // body，本断言失败。
      await rerender(<App actions={actions} viewState={viewState} />);
      await waitFor(() => expect(document.activeElement).toBe(choose));
      expect(actions.run).toHaveBeenCalledTimes(1);
    } finally {
      hasFocus.mockRestore();
      unmount();
    }
  });

  it("does not restore focus while the page is not focused", async () => {
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
      capabilities: [],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { rerender, unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    // 负例：后台/失焦窗口（hasFocus=false）不恢复焦点，验证不会在后台
    // 抢控件。
    const hasFocus = vi.spyOn(document, "hasFocus").mockReturnValue(false);
    try {
      const user = userEvent.setup();
      const choose = await screen.findByRole("button", { name: "选择图片" });
      await user.click(choose);
      act(() => choose.blur());
      await rerender(
        <App
          actions={actions}
          viewState={{
            ...viewState,
            revision: 2,
            features: { imageEdit: { isBusy: true } },
          }}
        />,
      );
      await rerender(<App actions={actions} viewState={viewState} />);
      await waitFor(() => expect(choose).toBeEnabled());
      expect(document.activeElement).toBe(document.body);
      expect(actions.run).toHaveBeenCalledTimes(1);
    } finally {
      hasFocus.mockRestore();
      unmount();
    }
  });

  it("leaves focus untouched for the clipboard entry across a busy cycle", async () => {
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
      capabilities: [],
      features: {},
      runtimeLabel: "原生宿主已连接",
    };
    const { rerender, unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    const user = userEvent.setup();
    const choose = await screen.findByRole("button", { name: "选择图片" });
    const paste = screen.getByRole("button", { name: "粘贴图片" });
    await user.click(paste);
    act(() => paste.blur());
    await rerender(
      <App
        actions={actions}
        viewState={{
          ...viewState,
          revision: 2,
          features: { imageEdit: { isBusy: true } },
        }}
      />,
    );
    await rerender(<App actions={actions} viewState={viewState} />);
    await waitFor(() => expect(choose).toBeEnabled());
    // 粘贴入口无系统选择器：不抢焦点，保持 body（约束不过度恢复）。
    expect(document.activeElement).toBe(document.body);
    expect(actions.run).toHaveBeenCalledWith({
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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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
  it("disables the unified screenshot button without the capture capability", async () => {
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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    // 配置已保存但未注册生效必须如实区分，不得冒称当前生效。
    expect(
      screen.getByText("已保存 Ctrl+Alt+Q，但当前未注册生效"),
    ).toBeVisible();
    expect(screen.getByText(/该组合可能已被其他应用占用/)).toBeVisible();
    expect(screen.getByText("快捷键需包含 Ctrl、Alt 或 Win。")).toBeVisible();

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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    await user.click(screen.getByRole("link", { name: "二维码与条码" }));
    expect(actions.navigate).toHaveBeenCalledWith("qrcode");
    expect(
      await screen.findByRole("heading", { name: "二维码与条码" }),
    ).toBeVisible();

    await user.click(screen.getByRole("link", { name: "批量识别" }));
    expect(
      await screen.findByRole("heading", { name: "批量识别" }),
    ).toBeVisible();
    // 多格式导出收纳进“导出全部”菜单：未完成项时触发器禁用，
    // 能力缺失原因不占布局（视觉隐藏描述 + Tooltip）。
    expect(screen.getByRole("button", { name: "导出全部" })).toBeDisabled();
    expect(screen.queryByRole("menuitem", { name: "Markdown" })).toBeNull();

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
    const { rerender, unmount } = await renderLoaded(
      <App actions={actions} viewState={initial} />,
    );
    const push = vi.spyOn(window.history, "pushState");
    try {
      await user.click(screen.getByRole("link", { name: "二维码与条码" }));
      expect(actions.navigate).toHaveBeenCalledWith("qrcode");
      expect(push).not.toHaveBeenCalled();
      expect(window.location.hash).toBe("#/recognition");
      await rerender(
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
    const { rerender, unmount } = await renderLoaded(
      <App actions={actions} viewState={initial} />,
    );

    await rerender(
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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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
    await user.click(screen.getByText("支持的格式"));
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    await user.click(screen.getByRole("checkbox", { name: "选择第 2 页" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "pdf.selectPage",
      page: 1,
      selected: true,
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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
    // 静态用法收进“使用帮助与图像信息”：展开后仍可读。
    fireEvent.click(screen.getByText("使用帮助与图像信息"));
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

  it("shows failed recognition and rejected host commands without hiding them as idle", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(screen.getByText("识别失败，请检查运行时状态后重试")).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "原生操作执行失败。请检查当前输入和运行时状态后重试。",
    );
    unmount();

    const cancelled = await renderLoaded(
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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

    const { rerender, unmount } = await renderLoaded(
      <App actions={actions} viewState={base} />,
    );
    await user.click(screen.getByRole("button", { name: "下一组" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "batch.setWindow",
      start: 40,
    });

    window.location.hash = "#/pdf";
    await rerender(
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

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
    // 服务未就绪时设置入口可见并指向设置页的既有能力。
    await user.click(screen.getByRole("button", { name: "打开识别服务设置" }));
    expect(actions.navigate).toHaveBeenCalledWith("settings");
    unmount();
  });

  it("offers model sources from settings without package installation", async () => {
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
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(screen.queryByLabelText("默认模式")).not.toBeInTheDocument();
    // 随包基础能力展示已移除（纯视觉）：二维码能力不受设置页影响。
    expect(screen.queryByText("二维码与条形码")).not.toBeInTheDocument();
    expect(screen.queryByText("随包可用")).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "识别默认模式" })).toBeVisible();
    expect(screen.queryByLabelText("Python 包下载源")).not.toBeInTheDocument();
    const modelSource = screen.getByLabelText("模型下载源");
    expect(modelSource).toHaveValue("");
    await user.selectOptions(modelSource, "huggingface");
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setSource",
      kind: "model_registry",
      sourceId: "huggingface",
    });

    expect(screen.getByText("Hugging Face")).toBeInTheDocument();
    unmount();
  });

  it("prepares runtime only after the explicit settings action", async () => {
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
    const { rerender, unmount } = await renderLoaded(
      <StrictMode>
        <App actions={actions} viewState={base} />
      </StrictMode>,
    );
    expect(run).not.toHaveBeenCalled();

    // 能力广播和通用设置挂载均保持纯本地；只有显式点击才准备。
    await rerender(
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
    expect(run).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "检查识别服务" }));
    expect(run).toHaveBeenCalledWith({ type: "settings.refreshRuntime" });

    // 后续宿主状态广播（刷新结果、能力数组重建）不得再次触发挂载刷新。
    await rerender(
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

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

  it("waits for the runtime catalog before offering MinerU connection editing", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(
      screen.getByText("启动识别服务后即可配置远程 MinerU 连接。"),
    ).toBeVisible();
    expect(screen.getByLabelText("MinerU 运行模式")).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "保存 MinerU 配置" }),
    ).toBeDisabled();
    unmount();
  });

  it("does not describe unloaded download sources as a backend decision", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(screen.getByText(/尚未加载/)).toBeVisible();
    expect(
      screen.queryByText(/识别服务未提供下载源目录/),
    ).not.toBeInTheDocument();
    unmount();
  });

  it("keeps the current service state separate from a retired maintenance outcome", async () => {
    window.location.hash = "#/settings";
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
      capabilities: ["settings.selection", "runtime.refresh"],
      features: {
        settings: {
          theme: "light",
          isBusy: false,
          statusCode: "settings.ready",
          backend: "cpu",
          startupEnabled: false,
          hotkey: "Ctrl+Alt+Q",
          sources: [],
          serviceStatus: "识别服务已就绪。",
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(screen.getByText("当前服务：识别服务已就绪。")).toBeVisible();
    // 预构建运行时树：维护投影已退役，不得再渲染维护行或进度条。
    expect(screen.queryByText(/本次维护/)).not.toBeInTheDocument();
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "重新预览上次选择" }),
    ).not.toBeInTheDocument();
    expect(actions.run).not.toHaveBeenCalled();
    unmount();
  });

  it("renders idle settings without a maintenance progress bar and an unread target accelerator", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(screen.getByLabelText("计算设备")).toHaveValue("");
    expect(screen.getByText("当前服务：等待运行时检查")).toBeVisible();
    // 空闲（无活动维护操作）不得渲染滚动维护进度。
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    unmount();
  });

  it("shows current instance device evidence without claiming a completed execution", async () => {
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
    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
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
              engine: "paddleocr",
              displayName: "PaddleOCR",
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

    const { unmount, rerender } = await renderLoaded(
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
    await rerender(
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
                  lifecycleKind: "process_keep_alive",
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
    // 生命周期技术提示收纳在“高级选项”折叠区：展开后仍完整可读。
    fireEvent.click(screen.getByText("高级选项"));
    expect(
      screen.getByText(
        "空闲后释放进程资源，可在设置中调整或设为持久；远程 MinerU 由服务端管理。",
      ),
    ).toBeVisible();
    await rerender(
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
        "空闲后释放进程资源，可在设置中调整或设为持久；远程 MinerU 由服务端管理。",
      ),
    ).toBeVisible();
    unmount();
  });
  it("groups paddle_table with table modes and persists its device option", async () => {
    window.location.hash = "#/recognition";
    const user = userEvent.setup();
    const actions: AppActions = {
      run: vi.fn(),
      navigate: vi.fn(),
      setTheme: vi.fn(),
    };
    const engines = [
      {
        engine: "paddle_text",
        displayName: "通用 OCR（PaddleOCR）",
        selected: false,
        isTaskOverride: false,
        availability: "ready",
        requiresDownload: false,
        supportedOptions: [],
      },
      {
        engine: "paddle_table",
        displayName: "表格结构识别（PaddleOCR）",
        selected: true,
        isTaskOverride: true,
        availability: "ready",
        requiresDownload: false,
        lifecycleKind: "process_keep_alive",
        supportsPreload: true,
        supportsTtl: true,
        supportsPinning: true,
        supportsRelease: true,
        supportedOptions: ["device"],
        options: { device: "cpu" },
      },
    ];
    const viewState: AppViewState = {
      connected: true,
      revision: 31,
      route: "recognition",
      theme: "light",
      capabilities: ["recognition.engine", "recognition.capture"],
      features: {
        recognition: {
          isBusy: false,
          statusCode: "recognition.ready",
          taskEngine: "paddle_table",
          engines,
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );
    const select = screen.getByLabelText("本次识别模式") as HTMLSelectElement;
    const tableGroup = [...select.querySelectorAll("optgroup")].find(
      (group) => group.label === "表格",
    );
    expect(tableGroup).toBeDefined();
    const grouped = [...tableGroup!.children].map(
      (option) => (option as HTMLOptionElement).value,
    );
    expect(grouped).toEqual(["paddle_table"]);
    fireEvent.click(screen.getByText("高级选项"));
    expect(
      screen.getByText(
        "空闲后释放进程资源，可在设置中调整或设为持久；远程 MinerU 由服务端管理。",
      ),
    ).toBeVisible();
    // 识别参数编辑器承载 device 选项；保存走 recognition.setOptions。
    const deviceSelect = await screen.findByLabelText("运行设备");
    expect(deviceSelect).toHaveValue("cpu");
    await user.selectOptions(deviceSelect, "directml");
    await user.click(screen.getByRole("button", { name: "保存参数" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "recognition.setOptions",
      modeId: "paddle_table",
      options: { device: "directml" },
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    // 继承选项同时展示来源与实际有效模式。
    expect(
      screen.getByRole("option", {
        name: "跟随默认（Windows OCR（系统内置））",
      }),
    ).toBeInTheDocument();
    // 可点击入口直达设置页的默认识别模式区（收纳在高级选项内）。
    await user.click(screen.getByText("高级选项"));
    await user.click(screen.getByRole("button", { name: "修改默认识别类型" }));
    expect(actions.navigate).toHaveBeenCalledWith("settings");
    unmount();
  });

  it("keeps showing the committed default on the inherit option while overridden", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    // 本次覆盖后默认仍已知：继承项继续展示已提交默认，不退回“未读取”。
    expect(
      screen.getByRole("option", {
        name: "跟随默认（Windows OCR（系统内置））",
      }),
    ).toBeInTheDocument();
    unmount();
  });

  it("labels a non-ready runtime default with its availability on the inherit option", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

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
            modeId: "paddle_text",
            stored: true,
          },
          recognitionModes: [
            {
              id: "paddle_text",
              displayName: "通用 OCR（PaddleOCR）",
              availability: "ready",
            },
            {
              id: "windows_text",
              displayName: "Windows OCR（系统内置）",
              availability: "ready",
            },
            {
              id: "paddle_structure",
              displayName: "文档结构识别（PP-StructureV3）",
              availability: "preparation_required",
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    const selector = screen.getByLabelText("默认识别类型");
    expect(selector).toHaveValue("paddle_text");
    // 未就绪模式不可选为默认，且可用性如实标注。
    expect(
      screen.getByRole("option", {
        name: /文档结构识别（PP-StructureV3）（需准备依赖）/,
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

  it("keeps the default recognition mode read-only on backends without the capability", async () => {
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

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(
      screen.getByText(/当前识别服务未声明 ocr\.default-recognition-mode\.v1/),
    ).toBeVisible();
    expect(screen.getByText(/按通用 OCR（PaddleOCR）执行/)).toBeVisible();
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
              id: "paddle_text",
              displayName: "通用 OCR（PaddleOCR）",
              availability: "ready",
            },
          ],
        },
      },
      runtimeLabel: "原生宿主已连接",
    };

    const { unmount } = await renderLoaded(
      <App actions={actions} viewState={viewState} />,
    );

    expect(
      screen.getByRole("option", { name: "请重新选择（当前值无法解析）" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/保存的默认值无法解析，未显式选择的任务会拒绝提交/),
    ).toBeVisible();
    const selector = screen.getByLabelText("默认识别类型");
    expect(selector).toHaveValue("");
    await user.selectOptions(selector, "paddle_text");
    // 修复项选择后 selector 真实回显新值，不被无效旧值强制回空白。
    expect(selector).toHaveValue("paddle_text");
    await user.click(screen.getByRole("button", { name: "保存默认识别模式" }));
    expect(actions.run).toHaveBeenCalledWith({
      type: "settings.setDefaultRecognitionMode",
      mode: "paddle_text",
    });
    unmount();
  });
  it.each([
    [
      "workbench.error.recognitionModeRetired",
      "识别模式已退役，请在设置或任务面板重新选择可用模式。",
    ],
    [
      "workbench.error.recognitionModeUnknown",
      "无法识别所选模式，请重新选择当前目录中的可用模式。",
    ],
    [
      "workbench.error.recognitionModeUnavailable",
      "当前识别模式不可用，请检查运行环境或重新选择可用模式。",
    ],
  ])(
    "renders the host recognition problem %s",
    async (commandProblem, message) => {
      window.location.hash = "#/recognition";
      const mounted = await renderLoaded(
        <App
          actions={{ run: vi.fn(), navigate: vi.fn(), setTheme: vi.fn() }}
          viewState={{
            connected: true,
            revision: 1,
            route: "recognition",
            theme: "light",
            capabilities: [],
            features: {},
            runtimeLabel: "原生宿主已连接",
            commandProblem,
          }}
        />,
      );
      expect(screen.getByRole("alert")).toHaveTextContent(message);
      mounted.unmount();
    },
  );

  it.each([true, false])(
    "renders the host retired default reason with catalog=%s",
    async (hasCatalog) => {
      window.location.hash = "#/settings";
      const user = userEvent.setup();
      const actions: AppActions = {
        run: vi.fn().mockResolvedValue(true),
        navigate: vi.fn(),
        setTheme: vi.fn(),
      };
      const mounted = await renderLoaded(
        <App
          actions={actions}
          viewState={{
            connected: true,
            revision: 1,
            route: "settings",
            theme: "light",
            capabilities: ["settings.selection"],
            runtimeLabel: "原生宿主已连接",
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
                  modeId: "paddle_formula",
                  stored: true,
                  reasonCode: "recognition_mode_retired",
                },
                recognitionModes: hasCatalog
                  ? [
                      {
                        id: "paddle_text",
                        displayName: "通用 OCR（PaddleOCR）",
                        availability: "ready",
                      },
                    ]
                  : [],
              },
            },
          }}
        />,
      );
      if (hasCatalog) {
        expect(
          screen.getByRole("option", { name: "请重新选择（当前模式已退役）" }),
        ).toBeVisible();
      }
      expect(
        screen.getByText(/当前默认识别模式已退役，请重新选择可用模式并保存/),
      ).toBeVisible();
      if (hasCatalog) {
        await user.selectOptions(
          screen.getByLabelText("默认识别类型"),
          "paddle_text",
        );
        await user.click(
          screen.getByRole("button", { name: "保存默认识别模式" }),
        );
        expect(actions.run).toHaveBeenCalledWith({
          type: "settings.setDefaultRecognitionMode",
          mode: "paddle_text",
        });
      } else {
        expect(
          screen.queryByRole("button", { name: "保存默认识别模式" }),
        ).not.toBeInTheDocument();
      }
      mounted.unmount();
    },
  );
});
