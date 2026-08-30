import {
  FluentProvider,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
} from "@fluentui/react-components";
import {
  HashRouter,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
} from "react-router";
import { useEffect, useState } from "react";

import {
  AboutPage,
  BatchPage,
  DiagnosticsPage,
  PdfPage,
  QrCodePage,
  RecognitionPage,
  SettingsPage,
} from "../features/Pages";
import { AppShell } from "../layout/AppShell";
import { vibeDarkTheme, vibeLightTheme } from "../theme/theme";
import type { AppActions, AppViewState } from "./types";

export type { AppActions, AppViewState } from "./types";

function RouteSync({ viewState }: { readonly viewState: AppViewState }) {
  const location = useLocation();
  const navigate = useNavigate();

  useEffect(() => {
    if (!viewState.connected) return;
    const expected = `/${viewState.route}`;
    if (location.pathname !== expected) navigate(expected, { replace: true });
  }, [location.pathname, navigate, viewState.connected, viewState.route]);

  return null;
}

export function App({
  actions,
  viewState,
}: {
  readonly actions: AppActions;
  readonly viewState: AppViewState;
}) {
  const [systemDark, setSystemDark] = useState(
    () => window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false,
  );

  useEffect(() => {
    const media = window.matchMedia?.("(prefers-color-scheme: dark)");
    if (!media) return undefined;
    const sync = () => setSystemDark(media.matches);
    media.addEventListener("change", sync);
    return () => media.removeEventListener("change", sync);
  }, []);

  const effectiveDark =
    viewState.theme === "dark" || (viewState.theme === "system" && systemDark);

  return (
    <FluentProvider
      className="fluent-root"
      theme={effectiveDark ? vibeDarkTheme : vibeLightTheme}
    >
      <HashRouter>
        <RouteSync viewState={viewState} />
        <Routes>
          <Route
            element={
              <AppShell
                actions={actions}
                onThemeChange={actions.setTheme}
                theme={viewState.theme}
                viewState={viewState}
              />
            }
          >
            <Route
              index
              element={<Navigate replace to={`/${viewState.route}`} />}
            />
            <Route
              path="recognition"
              element={
                <RecognitionPage actions={actions} viewState={viewState} />
              }
            />
            <Route
              path="batch"
              element={<BatchPage actions={actions} viewState={viewState} />}
            />
            <Route
              path="qrcode"
              element={<QrCodePage actions={actions} viewState={viewState} />}
            />
            <Route
              path="pdf"
              element={<PdfPage actions={actions} viewState={viewState} />}
            />
            <Route
              path="settings"
              element={<SettingsPage actions={actions} viewState={viewState} />}
            />
            <Route
              path="about"
              element={<AboutPage actions={actions} viewState={viewState} />}
            />
            <Route
              path="diagnostics"
              element={
                <DiagnosticsPage actions={actions} viewState={viewState} />
              }
            />
            <Route
              path="*"
              element={<Navigate replace to={`/${viewState.route}`} />}
            />
          </Route>
        </Routes>
      </HashRouter>
      {!viewState.connected && (
        <MessageBar className="demo-notice" intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>演示界面</MessageBarTitle>宿主 Bridge
            尚未连接；页面仅展示能力边界，不会执行 OCR、文件或更新操作。
          </MessageBarBody>
        </MessageBar>
      )}
      {viewState.connected && viewState.commandProblem && (
        <MessageBar
          className="demo-notice"
          intent={
            viewState.commandProblem ===
            "workbench.error.annotationOperationCancelled"
              ? "warning"
              : "error"
          }
          layout="multiline"
          role="alert"
        >
          <MessageBarBody>
            <MessageBarTitle>
              {viewState.commandProblem ===
              "workbench.error.annotationOperationCancelled"
                ? "操作已取消"
                : "操作未完成"}
            </MessageBarTitle>
            {commandProblemLabel(viewState.commandProblem)}
          </MessageBarBody>
        </MessageBar>
      )}
    </FluentProvider>
  );
}

function commandProblemLabel(messageKey: string): string {
  const messages: Readonly<Record<string, string>> = {
    "workbench.error.desktopCommandFailed":
      "原生操作执行失败。请检查当前输入和运行时状态后重试。",
    "workbench.error.unsupportedCommand":
      "当前版本不支持这项操作，请更新应用后重试。",
    "workbench.error.bridgeUnavailable":
      "原生宿主暂时没有响应，请重启应用后重试。",
    "workbench.error.annotationOperationCancelled":
      "未保存标注图片；原图和当前识别结果均未改变。",
  };
  return messages[messageKey] ?? "当前操作被原生宿主拒绝，请检查状态后重试。";
}
