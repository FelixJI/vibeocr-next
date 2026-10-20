import type {
  AppActions,
  AppCommandReceipt,
  AppViewState,
  ProjectOpenHandoffState,
  ThemePreference,
} from "../app/types";
import type {
  AppRoute,
  AppSnapshot,
  HostBridge,
  HostCommand,
  HostStateEvent,
} from "./client";

import type { EditorProjectInstall } from "../components/ImageCanvasEditor";
import {
  parseProjectOpen,
  type ProjectOpenWire,
} from "../components/project/projectWire";

interface ProjectOpenHandoff {
  readonly descriptor: ProjectOpenWire;
  install?: EditorProjectInstall;
  phase: ProjectOpenHandoffState["phase"];
  committedSessionId?: string;
}

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
  private unsubscribeStateFailure?: () => void;
  private startGeneration = 0;
  private featureRecovery?: Promise<boolean>;
  private projectOpen?: ProjectOpenHandoff;
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
    reportCapturePixels: (scene, failure) => {
      if (this.current?.captureScene === scene && this.listener)
        this.bridge.reportCapturePixels?.(scene, failure);
    },
    recoverFeatureAssets: () => {
      if (this.featureRecovery) return this.featureRecovery;
      const recovery = this.recoverFeatureAssets();
      this.featureRecovery = recovery;
      void recovery.finally(() => {
        if (this.featureRecovery === recovery) this.featureRecovery = undefined;
      });
      return recovery;
    },
    recover: () => {
      if (this.listener)
        void this.start(this.listener).catch((error: unknown) => {
          this.onError(
            error instanceof Error ? error : new Error(String(error)),
          );
          this.reportCommandProblem("workbench.error.stateSyncFailed");
        });
    },
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
    commitProjectOpen: (descriptor, install) =>
      this.commitProjectOpen(descriptor, install),
    consumeProjectOpen: (token, sessionId, revision, install) =>
      this.consumeProjectOpen(token, sessionId, revision, install),
    run: ({ type, ...payload }) => this.runCommand(type, payload),
    runWithReceipt: ({ type, ...payload }) =>
      this.runCommandWithReceipt(type, payload),
    navigate: (route) => void this.navigateAfterPdfPreview(route),
    setTheme: (theme) => void this.runCommand("settings.setTheme", { theme }),
  };

  constructor(
    private readonly bridge: HostBridge,
    private readonly onError: (error: Error) => void = () => undefined,
    private readonly reloadSurface: () => void = () => window.location.reload(),
  ) {}

  private async recoverFeatureAssets(): Promise<boolean> {
    if (this.projectOpen) return false;
    const generation = this.startGeneration;
    let deadline: ReturnType<typeof setTimeout> | undefined;
    try {
      // A failed module has never mounted. Preserve any previous PDF component's
      // retiring draft, and any still registered preview, before replacing only
      // this browser document and its failed module registry.
      await Promise.race([
        (async () => {
          await this.pdfPreviewFlush?.flush();
          for (const documentId of this.retiringPdfPreviewFlush.keys())
            await this.drainRetiringPdfPreview(documentId);
        })(),
        new Promise<never>((_, reject) => {
          deadline = setTimeout(
            () => reject(new Error("Draft confirmation timed out")),
            10_000,
          );
        }),
      ]);
      if (generation !== this.startGeneration) return false;
      this.reloadSurface();
      return true;
    } catch (error: unknown) {
      this.onError(error instanceof Error ? error : new Error(String(error)));
      this.reportCommandProblem("workbench.error.commandFailed");
      return false;
    } finally {
      if (deadline !== undefined) clearTimeout(deadline);
    }
  }

  async start(listener: (state: AppViewState) => void): Promise<void> {
    const generation = ++this.startGeneration;
    this.unsubscribe?.();
    this.unsubscribePdfPreviewFlush?.();
    this.unsubscribeStateFailure?.();
    this.unsubscribeStateFailure = this.bridge.subscribeStateFailure?.(
      (error) => {
        if (generation !== this.startGeneration) return;
        this.onError(error);
        this.reportCommandProblem("workbench.error.stateSyncFailed");
      },
    );
    let snapshot: AppSnapshot;
    try {
      snapshot = await this.bridge.bootstrap();
    } catch (error) {
      // 旧恢复的失败不能覆盖新会话状态。
      if (generation !== this.startGeneration) return;
      throw error;
    }
    if (generation !== this.startGeneration) return;

    this.sessionId = snapshot.sessionId;
    this.current = projectSnapshot(snapshot);
    this.listener = listener;
    this.reconcileProjectOpen();
    this.projectOpenState();
    listener(this.current);
    this.unsubscribePdfPreviewFlush = this.bridge.subscribePdfPreviewFlush?.(
      (documentId) => this.flushPdfPreview(documentId),
    );
    this.unsubscribe = this.bridge.subscribe((event) => {
      if (
        generation !== this.startGeneration ||
        event.sessionId !== this.sessionId ||
        !this.current ||
        event.revision <= this.current.revision
      ) {
        return;
      }
      this.current = projectEvent(this.current, event);
      this.reconcileProjectOpen();
      this.projectOpenState();
      const pdf = this.current.features.pdf;
      if (
        (event.scope === "pdf" || event.snapshot !== undefined) &&
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
    this.startGeneration++;
    this.unsubscribe?.();
    this.unsubscribe = undefined;
    this.unsubscribePdfPreviewFlush?.();
    this.unsubscribePdfPreviewFlush = undefined;
    this.unsubscribeStateFailure?.();
    this.unsubscribeStateFailure = undefined;
    this.pdfPreviewFlush = undefined;
    this.retiringPdfPreviewFlush.clear();
    this.releaseProjectOpen();
    this.projectOpenState();
    this.listener = undefined;
  }

  /** prepare 在页面完成；发 ack 前同步移交，跨 route 保有唯一安装包。 */
  private commitProjectOpen(
    descriptor: ProjectOpenWire,
    install: EditorProjectInstall,
  ): boolean {
    const feature = this.current?.features.imageEdit;
    const session =
      isRecord(feature) && isRecord(feature.screenshotSession)
        ? feature.screenshotSession
        : undefined;
    const pending = isRecord(feature)
      ? parseProjectOpen(feature.projectOpen)
      : undefined;
    if (
      this.projectOpen ||
      this.current?.route !== "imageEdit" ||
      pending?.pendingId !== descriptor.pendingId ||
      pending.sessionId !== descriptor.sessionId ||
      pending.revision !== descriptor.revision ||
      pending.generation !== descriptor.generation ||
      pending.source.url !== descriptor.source.url ||
      (session?.sessionId ?? "0".repeat(32)) !== descriptor.sessionId ||
      (session?.revision ?? 0) !== descriptor.revision ||
      install.token !== descriptor.pendingId ||
      install.sourceUrl !== descriptor.source.url
    )
      return false;
    const handoff: ProjectOpenHandoff = {
      descriptor,
      install,
      phase: "confirming",
    };
    this.projectOpen = handoff;
    this.publishProjectOpen();
    void this.acknowledgeProjectOpen(handoff);
    return true;
  }

  private async acknowledgeProjectOpen(
    handoff: ProjectOpenHandoff,
  ): Promise<void> {
    const descriptor = handoff.descriptor;
    try {
      const receipt = await this.bridge.execute({
        scope: "imageEdit",
        action: "ackProjectOpen",
        arguments: {
          pendingId: descriptor.pendingId,
          sessionId: descriptor.sessionId,
          revision: descriptor.revision,
          generation: descriptor.generation,
        },
      });
      // 已安装或被宿主新输入替换：迟到回执没有编辑器写权限。
      if (this.projectOpen !== handoff) return;
      if (handoff.committedSessionId) {
        if (!receipt.ok) {
          this.onError(
            new Error("Project receipt conflicts with committed host state"),
          );
          this.reportCommandProblem("workbench.error.stateSyncFailed");
        }
        return;
      }
      if (!receipt.ok) {
        this.releaseProjectOpen();
        this.publishProjectOpen();
        this.reportCommandProblem(
          typeof receipt.problem?.messageKey === "string"
            ? receipt.problem.messageKey
            : "workbench.error.commandFailed",
        );
        return;
      }
      handoff.phase = "awaitingState";
      this.publishProjectOpen();
    } catch (error: unknown) {
      if (this.projectOpen !== handoff || handoff.committedSessionId) return;
      // 传输失败不能证明宿主未提交；保留位图，等待权威状态/显式恢复。
      handoff.phase = "uncertain";
      this.publishProjectOpen();
      this.onError(error instanceof Error ? error : new Error(String(error)));
      this.reportCommandProblem("workbench.error.bridgeUnavailable");
    }
  }

  private reconcileProjectOpen(): void {
    const handoff = this.projectOpen;
    if (!handoff) return;
    const feature = this.current?.features.imageEdit;
    const session =
      isRecord(feature) && isRecord(feature.screenshotSession)
        ? feature.screenshotSession
        : undefined;
    const input =
      isRecord(feature) && isRecord(feature.input) ? feature.input : undefined;
    const descriptor = handoff.descriptor;
    // C# commit 将该 pending 独有的 source 安装到新 Guid 会话，初始 revision=0。
    // 这是提交证据；不等待回执即可恢复，迟到回执不得再次 setHistory。
    if (
      input?.url === descriptor.source.url &&
      typeof session?.sessionId === "string" &&
      session.sessionId !== descriptor.sessionId &&
      session.revision === 0 &&
      (!handoff.committedSessionId ||
        handoff.committedSessionId === session.sessionId)
    ) {
      handoff.committedSessionId = session.sessionId;
      handoff.phase = "installing";
      return;
    }
    const pending = isRecord(feature)
      ? parseProjectOpen(feature.projectOpen)
      : undefined;
    if (
      !handoff.committedSessionId &&
      (session?.sessionId ?? "0".repeat(32)) === descriptor.sessionId &&
      (session?.revision ?? 0) === descriptor.revision &&
      pending?.pendingId === descriptor.pendingId
    )
      return;
    // 取消/超时/宿主外部换图/修订推进均以实际状态为准，绝不覆盖新工作。
    this.releaseProjectOpen();
  }

  private consumeProjectOpen(
    token: string,
    sessionId: string,
    revision: number,
    install: () => void,
  ): boolean {
    const handoff = this.projectOpen;
    if (
      this.current?.route !== "imageEdit" ||
      !handoff?.install ||
      handoff.install.token !== token ||
      handoff.committedSessionId !== sessionId ||
      revision !== 0
    )
      return false;
    // 路由校验、同步安装与所有权释放在同一调用栈，旧页面 effect 不可抢走包。
    install();
    this.releaseProjectOpen();
    this.publishProjectOpen();
    return true;
  }

  private releaseProjectOpen(): void {
    // 在途 ack 闭包可继续存在，但消费/终止后不再强引用大位图。
    if (this.projectOpen) this.projectOpen.install = undefined;
    this.projectOpen = undefined;
  }

  private projectOpenState(): void {
    if (!this.current) return;
    const next = { ...this.current };
    delete next.projectOpenHandoff;
    const handoff = this.projectOpen;
    if (handoff)
      next.projectOpenHandoff = {
        pendingId: handoff.descriptor.pendingId,
        sourceUrl: handoff.descriptor.source.url,
        phase: handoff.phase,
        ...(handoff.committedSessionId
          ? {
              sessionId: handoff.committedSessionId,
              install: handoff.install,
            }
          : {}),
      };
    this.current = next;
  }

  private publishProjectOpen(): void {
    this.projectOpenState();
    if (this.current) this.listener?.(this.current);
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
    return (await this.runCommandWithReceipt(type, args)) === "succeeded";
  }

  private async runCommandWithReceipt(
    type: string,
    args: Record<string, unknown>,
  ): Promise<AppCommandReceipt> {
    if (
      this.projectOpen &&
      type.startsWith("imageEdit.") &&
      type !== "imageEdit.releaseAssets"
    )
      return "unconfirmed";
    const separator = type.indexOf(".");
    if (separator < 1 || separator === type.length - 1) return "unconfirmed";
    const command: HostCommand = {
      scope: type.slice(0, separator),
      action: type.slice(separator + 1),
      arguments: args,
    };
    const generation = this.startGeneration;
    try {
      if (
        type === "pdf.activateDocument" &&
        typeof args.documentId === "string"
      )
        await this.drainRetiringPdfPreview(args.documentId);
      const receipt = await this.bridge.execute(command);
      if (generation !== this.startGeneration) return "unconfirmed";
      if (!receipt.ok) {
        const messageKey = receipt.problem?.messageKey;
        this.reportCommandProblem(
          typeof messageKey === "string"
            ? messageKey
            : "workbench.error.commandFailed",
        );
        return "rejected";
      }
      this.clearCommandProblem();
      return "succeeded";
    } catch (error: unknown) {
      if (generation !== this.startGeneration) return "unconfirmed";
      const normalized =
        error instanceof Error ? error : new Error(String(error));
      this.onError(normalized);
      this.reportCommandProblem("workbench.error.bridgeUnavailable");
      return "unconfirmed";
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
    captureScene: snapshot.captureScene,
    runtimeLabel: runtimeLabel(snapshot.features),
  };
}

function projectEvent(
  current: AppViewState,
  event: HostStateEvent,
): AppViewState {
  let features = current.features;
  if (event.snapshot) {
    features = event.snapshot;
  } else if (event.change === "reset") {
    features = {};
  } else if (event.change === "remove") {
    features = Object.fromEntries(
      Object.entries(features).filter(([scope]) => scope !== event.scope),
    );
  } else if (event.change === "replace") {
    features = { ...features, [event.scope]: event.state };
  }

  const state = isRecord(event.state) ? event.state : undefined;
  const shell =
    event.snapshot && isRecord(event.snapshot.shell)
      ? event.snapshot.shell
      : undefined;
  const settings =
    event.snapshot && isRecord(event.snapshot.settings)
      ? event.snapshot.settings
      : undefined;
  const route = isAppRoute(shell?.route)
    ? shell.route
    : event.scope === "shell" && isAppRoute(state?.route)
      ? state.route
      : current.route;
  const theme = isTheme(settings?.theme)
    ? settings.theme
    : event.scope === "settings" && isTheme(state?.theme)
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
