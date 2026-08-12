import { RendererProvider, createDOMRenderer } from "@griffel/react";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./app/App";
import type { AppActions, AppViewState } from "./app/types";
import { BridgeClient, ChromeWebViewTransport } from "./bridge/client";
import { WorkbenchWebRuntime } from "./bridge/runtime";

const CSP_NONCE = "vibeocr-style";

const demoState: AppViewState = {
  connected: false,
  revision: 0,
  route: "recognition",
  theme: "system",
  capabilities: [],
  features: {},
  runtimeLabel: "演示模式 · 宿主未连接",
};

const demoActions: AppActions = {
  run: () => undefined,
  navigate: () => undefined,
  setTheme: () => undefined,
};

declare global {
  interface Window {
    __VIBEOCR_VISUAL_STATE__?: AppViewState;
  }
}

const root = document.getElementById("root");
if (!root) throw new Error("VibeOCR root element is missing.");

const renderer = createDOMRenderer(document, {
  styleElementAttributes: { nonce: CSP_NONCE },
});

const reactRoot = createRoot(root);

function renderApp(viewState: AppViewState, actions: AppActions): void {
  reactRoot.render(
    <StrictMode>
      <RendererProvider renderer={renderer} targetDocument={document}>
        <App actions={actions} viewState={viewState} />
      </RendererProvider>
    </StrictMode>,
  );
}

const transport = ChromeWebViewTransport.fromWindow(window);
if (!transport) {
  const visualState = import.meta.env.DEV
    ? window.__VIBEOCR_VISUAL_STATE__
    : undefined;
  renderApp(visualState ?? demoState, demoActions);
} else {
  reactRoot.render(<div role="status">正在连接原生宿主…</div>);
  const bridge = new BridgeClient(transport);
  const runtime = new WorkbenchWebRuntime(bridge);
  void runtime
    .start((state) => renderApp(state, runtime.actions))
    .catch(() => {
      reactRoot.render(
        <div role="alert">无法连接原生宿主，请关闭窗口后重新启动应用。</div>,
      );
    });
  window.addEventListener(
    "pagehide",
    () => {
      runtime.stop();
      bridge.dispose();
    },
    { once: true },
  );
}
