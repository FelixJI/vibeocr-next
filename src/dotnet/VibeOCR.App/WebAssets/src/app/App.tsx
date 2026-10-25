import {
  FluentProvider,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Button,
} from "@fluentui/react-components";
import {
  HashRouter,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
  Outlet,
} from "react-router";
import {
  Component,
  lazy,
  Suspense,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
  type ComponentType,
} from "react";
import { AppShell } from "../layout/AppShell";
import { vibeDarkTheme, vibeLightTheme } from "../theme/theme";
import type { AppActions, AppViewState } from "./types";

export type { AppActions, AppViewState } from "./types";

const FormulaRenderPage = lazy(() =>
  import("../features/FormulaRenderPage").then((module) => ({
    default: module.FormulaRenderPage,
  })),
);

const featureLoaders = {
  RecognitionPage: () =>
    import("../features/RecognitionPage").then((module) => ({
      default: module.RecognitionPage,
    })),
  ImageEditPage: () =>
    import("../features/ImageEditPage").then((module) => ({
      default: module.ImageEditPage,
    })),
  ImageConversionPage: () =>
    import("../features/ImageConversionPage").then((module) => ({
      default: module.ImageConversionPage,
    })),
  BatchPage: () =>
    import("../features/BatchPage").then((module) => ({
      default: module.BatchPage,
    })),
  PdfPage: () =>
    import("../features/PdfPage").then((module) => ({
      default: module.PdfPage,
    })),
  QrCodePage: () =>
    import("../features/QrCodePage").then((module) => ({
      default: module.QrCodePage,
    })),
  SettingsPage: () =>
    import("../features/SettingsPage").then((module) => ({
      default: module.SettingsPage,
    })),
  DiagnosticsPage: () =>
    import("../features/DiagnosticsPage").then((module) => ({
      default: module.DiagnosticsPage,
    })),
};

class FeatureLoadBoundary extends Component<
  { children: ReactNode; recover?: () => Promise<boolean> },
  {
    failed: boolean;
    moduleFailed: boolean;
    recovering: boolean;
    recoveryFailed: boolean;
  }
> {
  state = {
    failed: false,
    moduleFailed: false,
    recovering: false,
    recoveryFailed: false,
  };
  static getDerivedStateFromError(error: unknown) {
    return {
      failed: true,
      moduleFailed: error instanceof FeatureModuleLoadError,
    };
  }
  private recover = async () => {
    if (this.state.recovering || !this.props.recover) return;
    this.setState({ recovering: true, recoveryFailed: false });
    const recovered = await this.props.recover();
    this.setState({ recovering: false, recoveryFailed: !recovered });
  };
  render() {
    return this.state.failed ? (
      <div role="alert">
        {this.state.moduleFailed
          ? "页面加载失败。"
          : "页面显示失败。请返回其他页面。"}
        {this.state.moduleFailed && this.props.recover && (
          <Button
            disabled={this.state.recovering}
            onClick={() => void this.recover()}
          >
            {this.state.recovering ? "正在确认草稿…" : "保留草稿并重新加载页面"}
          </Button>
        )}
        {this.state.recoveryFailed && (
          <span>草稿尚未确认，页面未重新加载。请检查连接后重试。</span>
        )}
      </div>
    ) : (
      this.props.children
    );
  }
}

class FeatureModuleLoadError extends Error {
  constructor(cause: unknown) {
    super("Feature module failed to load", { cause });
  }
}

function createFeaturePage(
  load: (typeof featureLoaders)[keyof typeof featureLoaders],
) {
  type FeatureModule = {
    default: ComponentType<{ actions: AppActions; viewState: AppViewState }>;
  };
  let loaded: FeatureModule | undefined;
  let pending: Promise<FeatureModule> | undefined;
  const preload = () =>
    (pending ??= load()
      .then((module) => {
        loaded = module;
        return module;
      })
      .catch((error: unknown) => {
        throw new FeatureModuleLoadError(error);
      }));
  const LazyPage = lazy(preload);
  return {
    preload,
    get Page() {
      return loaded?.default ?? LazyPage;
    },
  };
}
const featurePages = {
  RecognitionPage: createFeaturePage(featureLoaders.RecognitionPage),
  ImageEditPage: createFeaturePage(featureLoaders.ImageEditPage),
  ImageConversionPage: createFeaturePage(featureLoaders.ImageConversionPage),
  BatchPage: createFeaturePage(featureLoaders.BatchPage),
  PdfPage: createFeaturePage(featureLoaders.PdfPage),
  QrCodePage: createFeaturePage(featureLoaders.QrCodePage),
  SettingsPage: createFeaturePage(featureLoaders.SettingsPage),
  DiagnosticsPage: createFeaturePage(featureLoaders.DiagnosticsPage),
};

function FeatureRoute({
  page,
  actions,
  viewState,
}: {
  page: keyof typeof featureLoaders;
  actions: AppActions;
  viewState: AppViewState;
}) {
  useEffect(() => {
    const query = new URLSearchParams(window.location.hash.split("?")[1]);
    if (query.has("scene") || query.has("pin") || query.has("formula")) return;
    let stopped = false;
    let idle: number | undefined;
    let timer: number | undefined;
    const remaining = Object.values(featurePages).filter(
      (feature) => feature !== featurePages[page],
    );
    const schedule = () => {
      if (stopped || remaining.length === 0) return;
      const next = () => {
        if (stopped) return;
        // Preloading never mounts a page or runs its effects; errors surface only on navigation.
        void remaining
          .shift()!
          .preload()
          .catch(() => undefined)
          .then(schedule);
      };
      if (window.requestIdleCallback)
        idle = window.requestIdleCallback(next, { timeout: 1500 });
      else timer = window.setTimeout(next, 100);
    };
    void featurePages[page].preload().then(schedule, () => undefined);
    return () => {
      stopped = true;
      if (idle !== undefined) window.cancelIdleCallback(idle);
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [page]);
  const Page = useMemo(() => featurePages[page].Page, [page]);
  return (
    <FeatureLoadBoundary key={page} recover={actions.recoverFeatureAssets}>
      <Suspense
        fallback={
          <div
            className="page-loading"
            role="status"
            aria-label="页面加载中"
            aria-busy="true"
          >
            <div className="page-loading-header" aria-hidden="true">
              <span />
              <span />
            </div>
            <div className="page-loading-actions" aria-hidden="true">
              <span />
              <span />
              <span />
            </div>
            <div className="page-loading-content" aria-hidden="true">
              <span />
              <span />
              <span />
            </div>
          </div>
        }
      >
        <Page actions={actions} viewState={viewState} />
      </Suspense>
    </FeatureLoadBoundary>
  );
}

function RouteSync({ viewState }: { readonly viewState: AppViewState }) {
  const location = useLocation();
  const navigate = useNavigate();

  useEffect(() => {
    if (
      !viewState.connected ||
      window.location.hash.includes("?scene=1") ||
      window.location.hash.includes("?pin=1")
    )
      return;
    // 关于与诊断合并后，宿主仍可能持久化旧 about 路由：与重定向目标对齐，
    // 避免 RouteSync 把 #/about 拉回造成与 #/diagnostics 重定向循环。
    const expected =
      viewState.route === "about" ? "/diagnostics" : `/${viewState.route}`;
    if (location.pathname !== expected) navigate(expected, { replace: true });
  }, [location.pathname, navigate, viewState.connected, viewState.route]);

  return null;
}

function SceneOutlet() {
  return (
    <main className="page-region" style={{ height: "100%" }}>
      <Outlet />
    </main>
  );
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
  if (window.location.hash.includes("?formula=1"))
    return (
      <Suspense fallback={null}>
        <FormulaRenderPage />
      </Suspense>
    );

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
              window.location.hash.includes("?scene=1") ||
              window.location.hash.includes("?pin=1") ? (
                <SceneOutlet />
              ) : (
                <AppShell
                  actions={actions}
                  onThemeChange={actions.setTheme}
                  theme={viewState.theme}
                  viewState={viewState}
                />
              )
            }
          >
            <Route
              index
              element={<Navigate replace to={`/${viewState.route}`} />}
            />
            <Route
              path="recognition"
              element={
                <FeatureRoute
                  page="RecognitionPage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            <Route
              path="imageEdit"
              element={
                <FeatureRoute
                  page="ImageEditPage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            <Route
              path="imageConversion"
              element={
                <FeatureRoute
                  page="ImageConversionPage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            <Route
              path="batch"
              element={
                <FeatureRoute
                  page="BatchPage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            <Route
              path="qrcode"
              element={
                <FeatureRoute
                  page="QrCodePage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            <Route
              path="pdf"
              element={
                <FeatureRoute
                  page="PdfPage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            <Route
              path="settings"
              element={
                <FeatureRoute
                  page="SettingsPage"
                  actions={actions}
                  viewState={viewState}
                />
              }
            />
            {/* 关于与诊断已合并；保留 #/about 兼容重定向，直接跳转/旧收藏不落空页。 */}
            <Route
              path="about"
              element={<Navigate replace to="/diagnostics" />}
            />
            <Route
              path="diagnostics"
              element={
                <FeatureRoute
                  page="DiagnosticsPage"
                  actions={actions}
                  viewState={viewState}
                />
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
              "workbench.error.annotationOperationCancelled" ||
            viewState.commandProblem ===
              "workbench.error.screenshotSessionStale" ||
            viewState.commandProblem === "workbench.error.captureInProgress"
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
                : viewState.commandProblem ===
                    "workbench.error.screenshotSessionStale"
                  ? "截图会话已更新"
                  : viewState.commandProblem ===
                      "workbench.error.captureInProgress"
                    ? "已有截图选区进行中"
                    : "操作未完成"}
            </MessageBarTitle>
            {commandProblemLabel(viewState.commandProblem)}
            {viewState.commandProblem === "workbench.error.stateSyncFailed" && (
              <Button onClick={actions.recover}>重新同步</Button>
            )}
          </MessageBarBody>
        </MessageBar>
      )}
    </FluentProvider>
  );
}

function commandProblemLabel(messageKey: string): string {
  const messages: Readonly<Record<string, string>> = {
    "workbench.error.recognitionModeRetired":
      "识别模式已退役，请在设置或任务面板重新选择可用模式。",
    "workbench.error.recognitionModeUnknown":
      "无法识别所选模式，请重新选择当前目录中的可用模式。",
    "workbench.error.recognitionModeUnavailable":
      "当前识别模式不可用，请检查运行环境或重新选择可用模式。",
    "workbench.error.stateSyncFailed":
      "界面状态同步中断。重新同步后可继续操作，未保存的本地编辑会保留。",
    "workbench.error.clipboardBusy": "剪贴板被占用，请稍后重试复制。",
    "workbench.error.desktopCommandFailed":
      "原生操作执行失败。请检查当前输入和运行时状态后重试。",
    "workbench.error.unsupportedCommand":
      "当前版本不支持这项操作，请更新应用后重试。",
    "workbench.error.bridgeUnavailable":
      "原生宿主暂时没有响应，请重启应用后重试。",
    "workbench.error.annotationOperationCancelled":
      "未保存标注图片；原图和当前识别结果均未改变。",
    "workbench.error.screenshotSessionStale":
      "截图内容或会话已更新；请在新画面上重试复制、保存或识别。",
    "workbench.error.captureInProgress":
      "已有截图选区正在进行中；请先完成或取消当前选区，再重新触发。",
    "workbench.error.imageImportInProgress":
      "正在导入系统内容；请稍候或先完成当前导入。",
    "workbench.error.imageImportSingleBaseline":
      "空画布请先导入一张图片；多图批次未导入。",
    "workbench.error.imageImportOpenFirst":
      "请先打开或截取一张图片，再把系统内容粘贴为对象。",
    "workbench.error.imageImportEmpty":
      "系统剪贴板或拖入内容中没有可导入的图片或文本。",
    "workbench.error.imageImportOversize":
      "导入内容超出限制（单图 16 MiB/1600 万像素/8192 边，文本 4096 字符/128 行）；本批未导入，已有画布保持不变。",
    "workbench.error.imageImportInvalid":
      "导入内容格式无效或无法解码；本批未导入，已有画布保持不变。",
    "workbench.error.imageImportRefused":
      "本次导入被拒绝；已有画布保持不变，请调整内容后重试。",
    "workbench.error.imageImportUnavailable":
      "当前环境暂不支持读取系统内容导入。",
    "workbench.error.pinLimitReached": "最多同时打开四张贴图，请先关闭一张。",
  };
  return messages[messageKey] ?? "当前操作被原生宿主拒绝，请检查状态后重试。";
}
