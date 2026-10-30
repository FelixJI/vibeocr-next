import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { DiagnosticsPage } from "./DiagnosticsPage";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function setup(
  features: AppViewState["features"],
  capabilities: string[] = [
    "update.check",
    "update.install",
    "update.selectSource",
    "about.openProject",
    "diagnostics.refresh",
    "diagnostics.export",
    "diagnostics.copy",
  ],
) {
  const run = vi.fn().mockResolvedValue(true);
  const actions: AppActions = {
    run,
    navigate: vi.fn(),
    setTheme: vi.fn(),
  };
  const viewState: AppViewState = {
    connected: true,
    revision: 1,
    route: "diagnostics",
    theme: "light",
    capabilities,
    runtimeLabel: "host",
    features,
  };
  render(<DiagnosticsPage viewState={viewState} actions={actions} />);
  return { actions, viewState };
}

describe("DiagnosticsPage 关于信息", () => {
  it("产品名与项目链接来自宿主 about 快照", () => {
    setup({
      about: {
        version: "0.8.5",
        license: "MIT",
        projectUrl: "https://github.com/FelixJI/vibeocr-next",
      },
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        updateAvailable: false,
      },
      diagnostics: {},
    });

    expect(screen.getByText("VibeOCR Next")).toBeVisible();
    expect(screen.getByText("0.8.5")).toBeVisible();
    expect(screen.getByText("MIT")).toBeVisible();
    expect(
      screen.getByText("https://github.com/FelixJI/vibeocr-next"),
    ).toBeVisible();
  });

  it("about 快照缺失时仍以占位文案呈现，不冒充宿主数据", () => {
    setup({
      update: { isBusy: false, statusCode: "update.unchecked" },
      diagnostics: {},
    });

    expect(screen.getAllByText("等待宿主同步").length).toBeGreaterThan(0);
    expect(screen.getByText("由宿主提供")).toBeVisible();
  });
});

describe("DiagnosticsPage 更新状态", () => {
  it("未检查状态如实显示未检查，不误报已是最新", () => {
    setup({
      about: { version: "0.8.5" },
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        updateAvailable: false,
      },
      diagnostics: {},
    });

    expect(screen.getByText("尚未检查更新")).toBeVisible();
    expect(screen.queryByText(/最新版本/)).not.toBeInTheDocument();
  });

  it("检查失败显示失败文案与宿主错误详情，不再显示等待宿主连接", () => {
    setup({
      about: {},
      update: {
        isBusy: false,
        statusCode: "update.error",
        updateAvailable: false,
        statusMessage: "无法连接更新源。",
      },
      diagnostics: {},
    });

    expect(screen.getByText("更新检查或下载失败")).toBeVisible();
    expect(screen.getByText("无法连接更新源。")).toBeVisible();
    expect(
      screen.queryByText("更新状态等待宿主连接。"),
    ).not.toBeInTheDocument();
  });

  it("update 快照缺失时显示尚未同步而不是宿主连接失败", () => {
    setup({ about: {}, diagnostics: {} });

    expect(screen.getByText("更新状态尚未同步。")).toBeVisible();
  });
});

describe("DiagnosticsPage 更新源选择", () => {
  const directSources = [
    {
      id: "direct",
      kind: "direct",
      displayName: "官方直连（GitHub）",
      endpoint:
        "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      selected: true,
    },
  ];

  it("展示检测地址与下载来源；未配置自定义源时只有官方直连可用", () => {
    setup({
      about: {},
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        sources: directSources,
        detectionEndpoint:
          "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      },
      diagnostics: {},
    });

    expect(screen.getByText(/检测地址：https:\/\/github.com\//)).toBeVisible();
    expect(screen.getByText(/下载来源：官方直连（GitHub）/)).toBeVisible();
    expect(
      screen.getByRole("radio", { name: "官方直连", checked: true }),
    ).toBeVisible();
    expect(screen.getByRole("radio", { name: "自定义源" })).toBeEnabled();
  });

  it("正向代理选中时代理地址与检测地址同时可见", () => {
    setup({
      about: {},
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        sources: [
          ...directSources.map((source) => ({ ...source, selected: false })),
          {
            id: "custom",
            kind: "forward_proxy",
            displayName: "自定义正向代理",
            endpoint: "http://127.0.0.1:7890",
            selected: true,
          },
        ],
        detectionEndpoint:
          "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      },
      diagnostics: {},
    });

    // 代理模式下检测地址仍是官方地址，但下载经选定的代理地址进行，
    // 两者都必须可见，不能只显示 detectionEndpoint。
    expect(screen.getByText(/检测地址：https:\/\/github.com\//)).toBeVisible();
    expect(
      screen.getByText(
        /下载来源：自定义正向代理（经代理 http:\/\/127\.0\.0\.1:7890 下载）/,
      ),
    ).toBeVisible();
  });

  it("切换官方直连发出 update.setSource 命令", async () => {
    const { actions } = setup({
      about: {},
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        sources: [
          ...directSources.map((source) => ({ ...source, selected: false })),
          {
            id: "custom",
            kind: "url_prefix",
            displayName: "自定义镜像前缀",
            endpoint: "https://mirror.example.com/vibeocr",
            selected: true,
          },
        ],
        detectionEndpoint:
          "https://mirror.example.com/vibeocr/https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      },
      diagnostics: {},
    });
    const user = userEvent.setup();

    await user.click(screen.getByRole("radio", { name: "官方直连" }));

    expect(actions.run).toHaveBeenCalledExactlyOnceWith({
      type: "update.setSource",
      sourceId: "direct",
    });
  });

  it("自定义源需要填写 http/https 地址后才可保存", async () => {
    const { actions } = setup({
      about: {},
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        sources: directSources,
        detectionEndpoint:
          "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      },
      diagnostics: {},
    });
    const user = userEvent.setup();

    await user.click(screen.getByRole("radio", { name: "自定义源" }));
    await user.click(screen.getByRole("button", { name: "保存并使用" }));

    // 空地址不允许保存：不发起命令，避免必然被宿主拒绝的请求。
    expect(actions.run).not.toHaveBeenCalledWith({
      type: "update.setSource",
      sourceId: "custom",
      kind: "url_prefix",
      url: "",
    });

    await user.clear(screen.getByLabelText("镜像前缀"));
    await user.type(
      screen.getByLabelText("镜像前缀"),
      "https://mirror.example.com/vibeocr",
    );
    await user.click(screen.getByRole("button", { name: "保存并使用" }));

    expect(actions.run).toHaveBeenCalledWith({
      type: "update.setSource",
      sourceId: "custom",
      kind: "url_prefix",
      url: "https://mirror.example.com/vibeocr",
    });
  });

  it("支持正向代理类型选择", async () => {
    const { actions } = setup({
      about: {},
      update: {
        isBusy: false,
        statusCode: "update.unchecked",
        sources: directSources,
        detectionEndpoint:
          "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      },
      diagnostics: {},
    });
    const user = userEvent.setup();

    await user.click(screen.getByRole("radio", { name: "自定义源" }));
    await user.selectOptions(
      screen.getByLabelText("自定义源类型"),
      "forward_proxy",
    );
    await user.type(screen.getByLabelText("代理地址"), "http://127.0.0.1:7890");
    await user.click(screen.getByRole("button", { name: "保存并使用" }));

    expect(actions.run).toHaveBeenCalledWith({
      type: "update.setSource",
      sourceId: "custom",
      kind: "forward_proxy",
      url: "http://127.0.0.1:7890",
    });
  });

  it("更新忙时禁用切源，避免检查/下载与源不一致", () => {
    setup({
      about: {},
      update: {
        isBusy: true,
        statusCode: "update.checking",
        sources: directSources,
        detectionEndpoint:
          "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
      },
      diagnostics: {},
    });

    expect(screen.getByRole("radio", { name: "官方直连" })).toBeDisabled();
    expect(screen.getByRole("radio", { name: "自定义源" })).toBeDisabled();
  });

  it("宿主未提供 update.selectSource 能力时不允许切源", () => {
    setup(
      {
        about: {},
        update: {
          isBusy: false,
          statusCode: "update.unchecked",
          sources: directSources,
          detectionEndpoint:
            "https://github.com/FelixJI/vibeocr-next/releases/latest/download/",
        },
        diagnostics: {},
      },
      ["update.check", "update.install"],
    );

    expect(screen.getByRole("radio", { name: "官方直连" })).toBeDisabled();
    expect(screen.getByRole("radio", { name: "自定义源" })).toBeDisabled();
  });
});

it("显示宿主工作进程快照并通过既有桥刷新", async () => {
  const { actions } = setup({
    diagnostics: {
      workers: [
        {
          owner: "MinerU",
          generation: 4,
          state: "idle_stopped",
          idleSeconds: 600,
          activeLeases: 0,
        },
      ],
    },
  });
  expect(screen.getByText(/idle_stopped/)).toBeVisible();
  await userEvent.click(screen.getByRole("button", { name: "刷新工作进程" }));
  expect(actions.run).toHaveBeenCalledWith({ type: "diagnostics.refresh" });
});
