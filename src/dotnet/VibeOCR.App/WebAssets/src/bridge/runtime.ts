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
  "batch",
  "qrcode",
  "pdf",
  "settings",
  "about",
  "diagnostics",
]);

const THEMES = new Set<ThemePreference>(["system", "light", "dark"]);

export class WorkbenchWebRuntime {
  private current?: AppViewState;
  private listener?: (state: AppViewState) => void;
  private sessionId?: string;
  private unsubscribe?: () => void;

  readonly actions: AppActions = {
    run: ({ type, ...payload }) => this.runCommand(type, payload),
    navigate: (route) => void this.runCommand("shell.navigate", { route }),
    setTheme: (theme) => void this.runCommand("settings.setTheme", { theme }),
  };

  constructor(
    private readonly bridge: HostBridge,
    private readonly onError: (error: Error) => void = () => undefined,
  ) {}

  async start(listener: (state: AppViewState) => void): Promise<void> {
    this.unsubscribe?.();
    const snapshot = await this.bridge.bootstrap();
    this.sessionId = snapshot.sessionId;
    this.current = projectSnapshot(snapshot);
    this.listener = listener;
    listener(this.current);
    this.unsubscribe = this.bridge.subscribe((event) => {
      if (
        event.sessionId !== this.sessionId ||
        !this.current ||
        event.revision <= this.current.revision
      ) {
        return;
      }
      this.current = projectEvent(this.current, event);
      listener(this.current);
    });
  }

  stop(): void {
    this.unsubscribe?.();
    this.unsubscribe = undefined;
    this.listener = undefined;
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
    runtimeLabel: runtimeLabel(snapshot.revision),
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
    runtimeLabel: runtimeLabel(event.revision),
  };
}

function runtimeLabel(revision: number): string {
  return `原生宿主已连接 · 状态版本 ${revision}`;
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
