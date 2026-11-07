import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { afterAll, afterEach, expect, it, vi } from "vitest";

import { App, type AppActions, type AppViewState } from "./App";

const { imageEdit, diagnostics, qrcode, batch } = vi.hoisted(() => {
  // React captures the clock and timeout functions when its DOM renderer loads.
  vi.useFakeTimers({ toFake: ["performance", "setTimeout", "clearTimeout"] });
  function pending<Module>() {
    let resolve!: (module: Module) => void;
    let reject!: (cause: Error) => void;
    const promise = new Promise<Module>((done, fail) => {
      resolve = done;
      reject = fail;
    });
    return { promise, resolve, reject };
  }
  return {
    imageEdit: pending<{ ImageEditPage: () => ReactNode }>(),
    diagnostics: pending<{ DiagnosticsPage: () => ReactNode }>(),
    qrcode: pending<{ QrCodePage: () => ReactNode }>(),
    batch: pending<{ BatchPage: () => ReactNode }>(),
  };
});

vi.mock("../features/ImageEditPage", () => imageEdit.promise);
vi.mock("../features/DiagnosticsPage", () => diagnostics.promise);
vi.mock("../features/QrCodePage", () => qrcode.promise);
vi.mock("../features/BatchPage", () => batch.promise);
vi.mock("../features/SettingsPage", () => ({
  SettingsPage: () => <h1>设置</h1>,
}));

afterEach(cleanup);
afterAll(() => {
  vi.useRealTimers();
});

const actions: AppActions = {
  run: vi.fn(),
  navigate: vi.fn(),
  setTheme: vi.fn(),
  recoverFeatureAssets: vi.fn().mockResolvedValue(true),
};

function renderScene(route: AppViewState["route"], onCaughtError = vi.fn()) {
  window.history.replaceState(null, "", `#/${route}?scene=1`);
  return render(
    <App
      actions={actions}
      viewState={{
        connected: true,
        revision: 1,
        route,
        theme: "light",
        capabilities: [],
        features: {},
        runtimeLabel: "原生宿主已连接",
      }}
    />,
    { onCaughtError },
  );
}

it("shows the cold capture editor when its module resolves without advancing timers", async () => {
  renderScene("imageEdit");
  const fallback = screen.getByLabelText("页面加载中");

  imageEdit.resolve({ ImageEditPage: () => <h1>截图现场编辑</h1> });
  // An async act flush would skip React's normal Suspense retry delay.
  await vi.dynamicImportSettled();

  expect(
    screen.getByRole("heading", { name: "截图现场编辑" }),
  ).toBeInTheDocument();
  expect(fallback).not.toBeInTheDocument();
});

it("shows the existing module recovery UI when a cold import rejects", async () => {
  const onCaughtError = vi.fn();
  renderScene("diagnostics", onCaughtError);
  expect(screen.getByLabelText("页面加载中")).toBeInTheDocument();
  const cause = new Error("chunk unavailable");

  await act(async () => {
    diagnostics.reject(cause);
    await vi.dynamicImportSettled();
  });

  expect(screen.getByRole("alert")).toHaveTextContent("页面加载失败。");
  expect(
    screen.getByRole("button", { name: "保留草稿并重新加载页面" }),
  ).toBeInTheDocument();
  expect(onCaughtError).toHaveBeenCalled();
});

it("does not render a pending page after navigating to another route", async () => {
  const QrCodePage = vi.fn(() => <h1>二维码与条码</h1>);
  renderScene("qrcode");
  const fallback = screen.getByLabelText("页面加载中");
  window.history.replaceState(null, "", "#/settings?scene=1");
  fireEvent.popState(window);
  expect(fallback).not.toBeInTheDocument();

  await act(async () => {
    qrcode.resolve({ QrCodePage });
    await vi.dynamicImportSettled();
  });

  expect(screen.getByRole("heading", { name: "设置" })).toBeInTheDocument();
  expect(QrCodePage).not.toHaveBeenCalled();
});

it("does not render a pending page after the scene unmounts", async () => {
  const BatchPage = vi.fn(() => <h1>批量处理</h1>);
  const { unmount } = renderScene("batch");
  expect(screen.getByLabelText("页面加载中")).toBeInTheDocument();

  unmount();
  await act(async () => {
    batch.resolve({ BatchPage });
    await vi.dynamicImportSettled();
  });

  expect(BatchPage).not.toHaveBeenCalled();
});
