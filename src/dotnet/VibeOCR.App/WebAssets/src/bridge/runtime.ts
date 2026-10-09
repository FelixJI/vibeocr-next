import type { AppActions, AppViewState, ThemePreference } from "../app/types";
import type {
  AppRoute,
  AppSnapshot,
  HostBridge,
  HostCommand,
  HostStateEvent,
} from "./client";

const APP_ROUTES = new Set<AppRoute>([
  "recognition",
  "imageEdit",
  "batch",
  "qrcode",
  "pdf",
  "settings",
  "about",
  "diagnostics",
]);

const THEMES = new Set<ThemePreference>(["system", "light", "dark"]);

// 桥接可用不代表识别服务就绪；启动与恢复状态复用宿主的权威诊断快照。
function runtimeLabel(features: Readonly<Record<string, unknown>>): string {
  const diagnostics = features.diagnostics;
  if (!isRecord(diagnostics)) return "应用启动中…";
  if (diagnostics.isReady === true) return "应用已就绪";
  switch (diagnostics.supervisorStatus) {
    case "正在连接":
      return "应用启动中…";
    case "连接失败":
      return "识别服务连接失败";
    case "协议不兼容":
      return "识别服务版本不兼容";
    default:
      return "识别服务未就绪";
  }
}

export class WorkbenchWebRuntime {
  private current?: AppViewState;
  private listener?: (state: AppViewState) => void;
  private sessionId?: string;
  private unsubscribe?: () => void;
  private unsubscribePdfPreviewFlush?: () => void;
  private pdfPreviewFlush?: { documentId: string; flush: () => Promise<void> };

  // 卸载期间每文档仅保留首个在途和最新 drain；成功/真实关闭即释放，不缓存预览内容。
  private readonly retiringPdfPreviewFlush = new Map<
    string,
    {
      first: () => Promise<void>;
      latest: () => Promise<void>;
      draining?: Promise<void>;
    }
  >();

  readonly actions: AppActions = {
    registerPdfPreviewFlush: (documentId, flush) => {
      const registered = { documentId, flush };
      this.pdfPreviewFlush = registered;
      return () => {
        if (this.pdfPreviewFlush !== registered) return;
        this.pdfPreviewFlush = undefined;
        const retiring = this.retiringPdfPreviewFlush.get(documentId);
        if (retiring) retiring.latest = flush;
        else
          this.retiringPdfPreviewFlush.set(documentId, {
            first: flush,
            latest: flush,
          });
        void this.drainRetiringPdfPreview(documentId).catch(() => undefined);
      };
    },
    run: ({ type, ...payload }) => this.runCommand(type, payload),
    navigate: (route) => void this.navigateAfterPdfPreview(route),
    setTheme: (theme) => void this.runCommand("settings.setTheme", { theme }),
  };

  constructor(
    private readonly bridge: HostBridge,
    private readonly onError: (error: Error) => void = () => undefined,
  ) {}

  async start(listener: (state: AppViewState) => void): Promise<void> {
    this.unsubscribe?.();
    this.unsubscribePdfPreviewFlush?.();
    const snapshot = await this.bridge.bootstrap();
    this.sessionId = snapshot.sessionId;
    this.current = projectSnapshot(snapshot);
    this.listener = listener;
    listener(this.current);
    this.unsubscribePdfPreviewFlush = this.bridge.subscribePdfPreviewFlush?.(
      (documentId) => this.flushPdfPreview(documentId),
    );
    this.unsubscribe = this.bridge.subscribe((event) => {
      if (
        event.sessionId !== this.sessionId ||
        !this.current ||
        event.revision <= this.current.revision
      ) {
        return;
      }
      this.current = projectEvent(this.current, event);
      const pdf = this.current.features.pdf;
      if (
        event.scope === "pdf" &&
        isRecord(pdf) &&
        Array.isArray(pdf.documents)
      ) {
        const known = new Set(
          pdf.documents.filter(isRecord).map((doc) => doc.documentId),
        );
        for (const id of this.retiringPdfPreviewFlush.keys())
          if (!known.has(id)) this.retiringPdfPreviewFlush.delete(id);
      }
      listener(this.current);
    });
  }

  stop(): void {
    this.unsubscribe?.();
    this.unsubscribe = undefined;
    this.unsubscribePdfPreviewFlush?.();
    this.unsubscribePdfPreviewFlush = undefined;
    this.pdfPreviewFlush = undefined;
    this.retiringPdfPreviewFlush.clear();
    this.listener = undefined;
  }

  private drainRetiringPdfPreview(documentId: string): Promise<void> {
    const retiring = this.retiringPdfPreviewFlush.get(documentId);
    if (!retiring) return Promise.resolve();
    if (retiring.draining) return retiring.draining;
    const task = (async () => {
      await Promise.resolve();
      try {
        let flushed = retiring.first;
        await flushed();
        while (retiring.latest !== flushed) {
          flushed = retiring.latest;
          await flushed();
        }
        if (this.retiringPdfPreviewFlush.get(documentId) === retiring)
          this.retiringPdfPreviewFlush.delete(documentId);
      } finally {
        retiring.draining = undefined;
      }
    })();
    retiring.draining = task;
    return task;
  }

  private async flushPdfPreview(documentId: string): Promise<boolean> {
    const pdf = this.current?.features.pdf;
    if (
      isRecord(pdf) &&
      typeof pdf.documentId === "string" &&
      pdf.documentId !== documentId
    )
      return false;
    await this.drainRetiringPdfPreview(documentId);
    if (this.current?.route !== "pdf") return true;
    if (this.pdfPreviewFlush?.documentId !== documentId) return false;
    await this.pdfPreviewFlush.flush();
    return true;
  }

  private async navigateAfterPdfPreview(route: AppRoute): Promise<void> {
    const pdf = this.current?.features.pdf;
    try {
      if (
        isRecord(pdf) &&
        typeof pdf.documentId === "string" &&
        (this.current?.route === "pdf" || route === "pdf")
      ) {
        if (!(await this.flushPdfPreview(pdf.documentId))) return;
      }
      await this.runCommand("shell.navigate", { route });
    } catch {
      this.reportCommandProblem("workbench.error.commandFailed");
    }
  }

  private async runCommand(
    type: string,
    args: Record<string, unknown>,
  ): Promise<boolean> {
    const separator = type.indexOf(".");
    if (separator < 1 || separator === type.length - 1) return false;
    const command: HostCommand = {
      scope: type.slice(0, separator),
      action: type.slice(separator + 1),
      arguments: args,
    };
    try {
      if (
        type === "pdf.activateDocument" &&
        typeof args.documentId === "string"
      )
        await this.drainRetiringPdfPreview(args.documentId);
      const receipt = await this.bridge.execute(command);
      if (!receipt.ok) {
        const messageKey = receipt.problem?.messageKey;
        this.reportCommandProblem(
          typeof messageKey === "string"
            ? messageKey
            : "workbench.error.commandFailed",
        );
        return false;
      }
      this.clearCommandProblem();
      return true;
    } catch (error: unknown) {
      const normalized =
        error instanceof Error ? error : new Error(String(error));
      this.onError(normalized);
      this.reportCommandProblem("workbench.error.bridgeUnavailable");
      return false;
    }
  }

  private reportCommandProblem(messageKey: string): void {
    if (!this.current || !this.listener) return;
    this.current = { ...this.current, commandProblem: messageKey };
    this.listener(this.current);
  }

  private clearCommandProblem(): void {
    if (!this.current?.commandProblem || !this.listener) return;
    const current = { ...this.current };
    delete current.commandProblem;
    this.current = current;
    this.listener(this.current);
  }
}

export function projectSnapshot(snapshot: AppSnapshot): AppViewState {
  return {
    connected: true,
    revision: snapshot.revision,
    route: snapshot.route,
    theme: snapshot.theme,
    capabilities: snapshot.capabilities,
    features: snapshot.features,
    runtimeLabel: runtimeLabel(snapshot.features),
  };
}

function projectEvent(
  current: AppViewState,
  event: HostStateEvent,
): AppViewState {
  let features = current.features;
  if (event.change === "reset") {
    features = {};
  } else if (event.change === "remove") {
    features = Object.fromEntries(
      Object.entries(features).filter(([scope]) => scope !== event.scope),
    );
  } else if (event.change === "replace") {
    features = { ...features, [event.scope]: event.state };
  }

  const state = isRecord(event.state) ? event.state : undefined;
  const route =
    event.scope === "shell" && isAppRoute(state?.route)
      ? state.route
      : current.route;
  const theme =
    event.scope === "settings" && isTheme(state?.theme)
      ? state.theme
      : current.theme;

  return {
    ...current,
    revision: event.revision,
    route,
    theme,
    features,
    runtimeLabel: runtimeLabel(features),
  };
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isAppRoute(value: unknown): value is AppRoute {
  return typeof value === "string" && APP_ROUTES.has(value as AppRoute);
}

function isTheme(value: unknown): value is ThemePreference {
  return typeof value === "string" && THEMES.has(value as ThemePreference);
}
