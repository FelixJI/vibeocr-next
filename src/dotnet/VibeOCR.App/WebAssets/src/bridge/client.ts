import {
  isCaptureScene,
  type CaptureScene,
  type CapturePixelFailure,
} from "./captureScene";

export const BRIDGE_VERSION = 2 as const;
export const MAX_BRIDGE_BYTES = 64 * 1024;

export type BridgeKind = "request" | "response" | "event";

export interface BridgeEnvelope {
  readonly version: typeof BRIDGE_VERSION;
  readonly kind: BridgeKind;
  readonly id: string;
  readonly type: string;
  readonly payload: Record<string, unknown>;
}

export type AppRoute =
  | "recognition"
  | "imageEdit"
  | "batch"
  | "imageConversion"
  | "qrcode"
  | "pdf"
  | "settings"
  | "about"
  | "diagnostics";

export interface AppSnapshot {
  readonly sessionId: string;
  readonly revision: number;
  readonly route: AppRoute;
  readonly theme: "system" | "light" | "dark";
  readonly capabilities: readonly string[];
  readonly features: Readonly<Record<string, unknown>>;
  readonly captureScene?: CaptureScene | null;
}

export interface HostCommand {
  readonly scope: string;
  readonly action: string;
  readonly arguments: Record<string, unknown>;
}

export interface CommandResult {
  readonly revision: number;
  readonly ok: boolean;
  readonly problem: Readonly<Record<string, unknown>> | null;
}

export interface HostStateEvent {
  readonly sessionId: string;
  readonly revision: number;
  readonly scope: string;
  readonly change: "replace" | "remove" | "reset" | "ready";
  readonly state?: unknown;
  // 仅客户端提交完整恢复事务后生成，不是 wire 字段。
  readonly snapshot?: Readonly<Record<string, unknown>>;
}

export interface WebViewTransport {
  postMessage(message: BridgeEnvelope): void;
  subscribe(listener: (data: unknown) => void): () => void;
}

export interface ChromeWebViewChannel {
  postMessage(message: unknown): void;
  addEventListener(
    type: "message",
    listener: (event: { readonly data: unknown }) => void,
  ): void;
  removeEventListener(
    type: "message",
    listener: (event: { readonly data: unknown }) => void,
  ): void;
}

export class ChromeWebViewTransport implements WebViewTransport {
  static fromWindow(value: unknown): ChromeWebViewTransport | undefined {
    if (!isRecord(value) || !isRecord(value.chrome)) return undefined;
    const channel = value.chrome.webview;
    if (
      !isRecord(channel) ||
      typeof channel.postMessage !== "function" ||
      typeof channel.addEventListener !== "function" ||
      typeof channel.removeEventListener !== "function"
    ) {
      return undefined;
    }
    return new ChromeWebViewTransport(
      channel as unknown as ChromeWebViewChannel,
    );
  }

  constructor(private readonly webview: ChromeWebViewChannel) {}

  postMessage(message: BridgeEnvelope): void {
    this.webview.postMessage(message);
  }

  subscribe(listener: (data: unknown) => void): () => void {
    const receive = (event: { readonly data: unknown }) => listener(event.data);
    this.webview.addEventListener("message", receive);
    return () => this.webview.removeEventListener("message", receive);
  }
}

export interface HostBridge {
  bootstrap(): Promise<AppSnapshot>;
  reportCapturePixels?(
    scene: CaptureScene,
    failure?: CapturePixelFailure,
  ): void;
  execute(command: HostCommand): Promise<CommandResult>;
  subscribe(listener: (event: HostStateEvent) => void): () => void;
  subscribeStateFailure?(listener: (error: Error) => void): () => void;
  subscribePdfPreviewFlush?(
    listener: (documentId: string) => Promise<boolean>,
  ): () => void;
}

export class BridgeProtocolError extends Error {}

interface PendingRequest {
  readonly type: string;
  readonly resolve: (payload: Record<string, unknown>) => void;
  readonly reject: (error: Error) => void;
}

interface BridgeClientOptions {
  readonly idFactory?: () => string;
  readonly maxBytes?: number;
}

export class BridgeClient implements HostBridge {
  private readonly pending = new Map<string, PendingRequest>();
  private readonly listeners = new Set<(event: HostStateEvent) => void>();
  private readonly idFactory: () => string;
  private readonly maxBytes: number;
  private readonly unsubscribe: () => void;
  private sessionId?: string;
  private revision = -1;
  private captureScene?: CaptureScene;
  private captureReported = false;
  private projectedScopes = new Set<string>();
  private recovery?: { revision: number; states: Map<string, unknown> };
  private stateFailed = false;
  private recoveryDeadline?: ReturnType<typeof setTimeout>;
  private readonly failureListeners = new Set<(error: Error) => void>();
  private bootstrapGeneration = 0;
  private startup?: {
    snapshot: Omit<AppSnapshot, "features">;
    resolve: (snapshot: AppSnapshot) => void;
    reject: (error: Error) => void;
  };
  private pdfPreviewFlush?: (documentId: string) => Promise<boolean>;
  private flushingPdfPreview = false;
  private disposed = false;

  constructor(
    private readonly transport: WebViewTransport,
    options: BridgeClientOptions = {},
  ) {
    this.idFactory = options.idFactory ?? (() => crypto.randomUUID());
    this.maxBytes = options.maxBytes ?? MAX_BRIDGE_BYTES;
    this.unsubscribe = transport.subscribe((data) => this.receive(data));
  }

  bootstrap(): Promise<AppSnapshot> {
    const generation = ++this.bootstrapGeneration;
    this.rejectStartup(new BridgeProtocolError("Bootstrap was superseded."));
    this.rejectSupersededBootstraps();
    this.sessionId = undefined;
    this.captureScene = undefined;
    this.captureReported = false;
    this.clearRecovery();
    this.stateFailed = false;
    return this.request("app.bootstrap", {}).then((payload) => {
      if (generation !== this.bootstrapGeneration)
        throw new BridgeProtocolError("Bootstrap was superseded.");
      if (payload.stateTransfer === "snapshot") {
        const control = parseBootstrapControl(payload);
        this.sessionId = control.sessionId;
        this.revision = control.revision;
        this.projectedScopes = new Set(control.stateScopes);
        const snapshot = {
          sessionId: control.sessionId,
          revision: control.revision,
          route: control.route,
          theme: control.theme,
          capabilities: control.capabilities,
          captureScene: control.captureScene,
        };
        return new Promise<AppSnapshot>((resolve, reject) => {
          this.startup = { snapshot, resolve, reject };
          this.recoveryDeadline = setTimeout(() => {
            this.failState("Workbench startup snapshot did not complete.");
          }, 10_000);
        });
      }
      const snapshot = parseAppSnapshot(payload);
      this.captureScene = snapshot.captureScene ?? undefined;
      this.sessionId = snapshot.sessionId;
      this.revision = snapshot.revision;
      this.projectedScopes = new Set([
        "shell",
        ...Object.keys(snapshot.features),
      ]);
      return snapshot;
    });
  }

  reportCapturePixels(
    scene: CaptureScene,
    failure?: CapturePixelFailure,
  ): void {
    if (
      this.disposed ||
      this.startup ||
      this.stateFailed ||
      !this.sessionId ||
      this.captureScene !== scene ||
      this.captureReported
    )
      return;
    const envelope: BridgeEnvelope = {
      version: BRIDGE_VERSION,
      kind: "event",
      id: this.idFactory(),
      type: failure ? "capture.pixelFailed" : "capture.pixelReady",
      payload: {
        sessionId: this.sessionId,
        captureScene: scene,
        ...(failure ? { reason: failure } : {}),
      },
    };
    if (
      new TextEncoder().encode(JSON.stringify(envelope)).byteLength >
      this.maxBytes
    )
      throw new BridgeProtocolError(
        "Capture pixel report exceeds the size limit.",
      );
    this.transport.postMessage(envelope);
    this.captureReported = true;
  }

  execute(command: HostCommand): Promise<CommandResult> {
    if (!this.sessionId || this.startup || this.stateFailed) {
      return Promise.reject(
        new BridgeProtocolError(
          "Bridge must bootstrap before executing commands.",
        ),
      );
    }
    return this.request("app.command", {
      sessionId: this.sessionId,
      command,
    }).then(parseCommandResult);
  }

  subscribe(listener: (event: HostStateEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  subscribeStateFailure(listener: (error: Error) => void): () => void {
    this.failureListeners.add(listener);
    return () => this.failureListeners.delete(listener);
  }

  private clearRecovery(): void {
    clearTimeout(this.recoveryDeadline);
    this.recoveryDeadline = undefined;
    this.recovery = undefined;
  }

  private failState(message: string): BridgeProtocolError {
    this.clearRecovery();
    this.stateFailed = true;
    const error = new BridgeProtocolError(message);
    this.rejectStartup(error);
    for (const listener of this.failureListeners) listener(error);
    return error;
  }

  private rejectStartup(error: Error): void {
    const startup = this.startup;
    this.startup = undefined;
    startup?.reject(error);
  }

  // 宿主换代后不再回复旧 bootstrap，必须终结其 Promise，避免在途请求积累。
  private rejectSupersededBootstraps(): void {
    const error = new BridgeProtocolError("Bootstrap was superseded.");
    for (const [id, pending] of this.pending) {
      if (pending.type !== "app.bootstrap") continue;
      this.pending.delete(id);
      pending.reject(error);
    }
  }

  subscribePdfPreviewFlush(
    listener: (documentId: string) => Promise<boolean>,
  ): () => void {
    this.pdfPreviewFlush = listener;
    return () => {
      if (this.pdfPreviewFlush === listener) this.pdfPreviewFlush = undefined;
    };
  }

  dispose(): void {
    this.disposed = true;
    this.clearRecovery();
    this.pdfPreviewFlush = undefined;
    this.unsubscribe();
    const error = new BridgeProtocolError("Bridge client was disposed.");
    this.rejectStartup(error);
    for (const pending of this.pending.values()) pending.reject(error);
    this.pending.clear();
    this.listeners.clear();
    this.failureListeners.clear();
  }

  private request(
    type: string,
    payload: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const id = this.idFactory();
    if (this.pending.has(id)) {
      throw new BridgeProtocolError("Bridge request id must be unique.");
    }

    const envelope: BridgeEnvelope = {
      version: BRIDGE_VERSION,
      kind: "request",
      id,
      type,
      payload,
    };
    if (
      new TextEncoder().encode(JSON.stringify(envelope)).byteLength >
      this.maxBytes
    ) {
      throw new BridgeProtocolError("Bridge request exceeds the size limit.");
    }

    return new Promise((resolve, reject) => {
      this.pending.set(id, { type, resolve, reject });
      try {
        this.transport.postMessage(envelope);
      } catch (error) {
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private receive(value: unknown): void {
    let byteLength: number;
    try {
      const serialized = JSON.stringify(value);
      if (serialized === undefined) return;
      byteLength = new TextEncoder().encode(serialized).byteLength;
    } catch {
      if (this.recovery || this.startup)
        this.failState("Workbench snapshot frame is not serializable.");
      this.rejectCorrelated(value, "Bridge response is not serializable.");
      return;
    }
    if (byteLength > this.maxBytes) {
      if (this.recovery || this.startup)
        this.failState("Workbench recovery frame exceeds the size limit.");
      this.rejectCorrelated(value, "Bridge response exceeds the size limit.");
      return;
    }
    if (!isBridgeEnvelope(value)) {
      if (this.recovery || this.startup)
        this.failState("Invalid workbench snapshot envelope.");
      this.rejectCorrelated(value, "Bridge response envelope is invalid.");
      return;
    }

    if (value.kind === "request" && value.type === "pdf.flushPreview") {
      void this.receivePdfPreviewFlush(value);
      return;
    }
    if (value.kind === "event") {
      this.receiveEvent(value);
      return;
    }
    if (value.kind !== "response") return;

    const pending = this.pending.get(value.id);
    if (!pending) {
      // 已撤销的 bootstrap 可能迟到；无匹配请求的回复不再拥有状态写入权。
      if (value.type === "app.bootstrap") return;
      throw new BridgeProtocolError("Received an unsolicited bridge response.");
    }
    this.pending.delete(value.id);
    if (pending.type !== value.type) {
      pending.reject(
        new BridgeProtocolError("Bridge response type does not match."),
      );
      return;
    }
    pending.resolve(value.payload);
  }

  private async receivePdfPreviewFlush(
    envelope: BridgeEnvelope,
  ): Promise<void> {
    const { sessionId, documentId } = envelope.payload;
    if (
      !hasExactFields(envelope.payload, ["sessionId", "documentId"]) ||
      sessionId !== this.sessionId ||
      typeof documentId !== "string" ||
      !/^[0-9a-f]{32}$/i.test(documentId)
    )
      return;
    let ok = false;
    if (!this.flushingPdfPreview && this.pdfPreviewFlush) {
      this.flushingPdfPreview = true;
      try {
        ok = await this.pdfPreviewFlush(documentId);
      } catch {
        ok = false;
      } finally {
        this.flushingPdfPreview = false;
      }
    }
    // 已接受的 flush 按原请求 id/session/doc 回执一次；同文档 bootstrap 换 session 不丢，仅 dispose 抑制。
    if (!this.disposed)
      this.transport.postMessage({
        version: BRIDGE_VERSION,
        kind: "response",
        id: envelope.id,
        type: envelope.type,
        payload: { sessionId, documentId, ok },
      });
  }

  private rejectCorrelated(value: unknown, message: string): void {
    if (!isRecord(value) || typeof value.id !== "string") return;
    const pending = this.pending.get(value.id);
    if (!pending) return;
    this.pending.delete(value.id);
    pending.reject(new BridgeProtocolError(message));
  }

  private receiveEvent(envelope: BridgeEnvelope): void {
    if (this.stateFailed) return;
    if (envelope.type !== "app.state") return;
    const event = parseHostStateEvent(envelope.payload);
    if (!event) {
      if (this.recovery || this.startup)
        this.failState("Invalid workbench recovery frame.");
      return;
    }
    if (
      event.sessionId !== this.sessionId ||
      !Number.isSafeInteger(event.revision)
    ) {
      return;
    }
    if (event.change === "reset") {
      if (
        this.recovery ||
        event.scope !== "shell" ||
        (this.startup && event.revision !== this.startup.snapshot.revision) ||
        (event.revision <= this.revision &&
          !(this.startup && event.revision === this.startup.snapshot.revision))
      ) {
        throw this.failState("Invalid workbench recovery reset.");
      }
      this.recovery = { revision: event.revision, states: new Map() };
      this.recoveryDeadline ??= setTimeout(() => {
        this.failState("Workbench recovery snapshot did not complete.");
      }, 10_000);
      return;
    }
    if (this.recovery) {
      const recovery = this.recovery;
      if (
        event.revision !== recovery.revision ||
        (event.change !== "replace" && event.change !== "ready")
      ) {
        throw this.failState("Workbench recovery checkpoint changed.");
      }
      if (event.change === "replace") {
        if (
          !this.projectedScopes.has(event.scope) ||
          recovery.states.has(event.scope)
        ) {
          throw this.failState("Invalid workbench recovery scope.");
        }
        recovery.states.set(event.scope, event.state);
        return;
      }
      this.clearRecovery();
      if (
        event.scope !== "shell" ||
        recovery.states.size !== this.projectedScopes.size
      ) {
        throw this.failState("Workbench recovery snapshot is incomplete.");
      }
      this.revision = event.revision;
      if (this.startup) {
        const startup = this.startup;
        const features = Object.fromEntries(recovery.states);
        const shell = features.shell;
        const settings = features.settings;
        delete features.shell;
        let snapshot: AppSnapshot;
        try {
          snapshot = parseAppSnapshot({
            ...startup.snapshot,
            revision: event.revision,
            route: isRecord(shell) ? shell.route : startup.snapshot.route,
            theme:
              isRecord(settings) &&
              ["system", "light", "dark"].includes(String(settings.theme))
                ? settings.theme
                : startup.snapshot.theme,
            features,
          });
        } catch {
          throw this.failState("Workbench startup snapshot is invalid.");
        }
        this.startup = undefined;
        this.captureScene = snapshot.captureScene ?? undefined;
        startup.resolve(snapshot);
        return;
      }
      const committed = {
        ...event,
        snapshot: Object.fromEntries(recovery.states),
      };
      for (const listener of this.listeners) listener(committed);
      return;
    }
    if (this.startup)
      throw this.failState(
        "Workbench startup requires reset before state frames.",
      );
    if (
      event.change === "ready" ||
      !STATE_SCOPES.has(event.scope) ||
      event.revision <= this.revision
    )
      return;
    this.revision = event.revision;
    for (const listener of this.listeners) listener(event);
  }
}

const STATE_SCOPES = new Set<string>([
  "shell",
  "recognition",
  "imageEdit",
  "pinEdit",
  "batch",
  "imageConversion",
  "pdf",
  "qrcode",
  "settings",
  "update",
  "about",
  "diagnostics",
]);

interface BootstrapControl extends Omit<AppSnapshot, "features"> {
  readonly stateTransfer: "snapshot";
  readonly stateScopes: readonly string[];
}

function parseBootstrapControl(
  payload: Record<string, unknown>,
): BootstrapControl {
  if (
    !hasExactFields(payload, [
      "sessionId",
      "revision",
      "route",
      "theme",
      "capabilities",
      "stateTransfer",
      "stateScopes",
      ...("captureScene" in payload ? ["captureScene"] : []),
    ]) ||
    payload.stateTransfer !== "snapshot" ||
    !Array.isArray(payload.stateScopes) ||
    !payload.stateScopes.includes("shell") ||
    payload.stateScopes.length > STATE_SCOPES.size ||
    new Set(payload.stateScopes).size !== payload.stateScopes.length ||
    !payload.stateScopes.every(
      (scope) => typeof scope === "string" && STATE_SCOPES.has(scope),
    )
  )
    throw new BridgeProtocolError("Invalid app.bootstrap snapshot control.");
  parseAppSnapshot({
    sessionId: payload.sessionId,
    revision: payload.revision,
    route: payload.route,
    theme: payload.theme,
    capabilities: payload.capabilities,
    features: {},
    ...("captureScene" in payload
      ? { captureScene: payload.captureScene }
      : {}),
  });
  return payload as unknown as BootstrapControl;
}

function isBridgeEnvelope(value: unknown): value is BridgeEnvelope {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return false;
  const candidate = value as Partial<BridgeEnvelope>;
  return (
    hasExactFields(value as Record<string, unknown>, [
      "version",
      "kind",
      "id",
      "type",
      "payload",
    ]) &&
    candidate.version === BRIDGE_VERSION &&
    (candidate.kind === "request" ||
      candidate.kind === "response" ||
      candidate.kind === "event") &&
    typeof candidate.id === "string" &&
    candidate.id.length > 0 &&
    typeof candidate.type === "string" &&
    candidate.type.length > 0 &&
    candidate.payload !== null &&
    typeof candidate.payload === "object" &&
    !Array.isArray(candidate.payload)
  );
}

const APP_ROUTES = new Set<AppRoute>([
  "recognition",
  "imageEdit",
  "batch",
  "imageConversion",
  "qrcode",
  "pdf",
  "settings",
  "about",
  "diagnostics",
]);

function parseAppSnapshot(payload: Record<string, unknown>): AppSnapshot {
  if (
    !hasExactFields(payload, [
      "sessionId",
      "revision",
      "route",
      "theme",
      "capabilities",
      "features",
      ...("captureScene" in payload ? ["captureScene"] : []),
    ]) ||
    typeof payload.sessionId !== "string" ||
    payload.sessionId.length === 0 ||
    !Number.isSafeInteger(payload.revision) ||
    (payload.revision as number) < 0 ||
    typeof payload.route !== "string" ||
    !APP_ROUTES.has(payload.route as AppRoute) ||
    (payload.theme !== "system" &&
      payload.theme !== "light" &&
      payload.theme !== "dark") ||
    !Array.isArray(payload.capabilities) ||
    !payload.capabilities.every(
      (capability) => typeof capability === "string" && capability.length > 0,
    ) ||
    !isRecord(payload.features) ||
    (payload.captureScene != null && !isCaptureScene(payload.captureScene))
  ) {
    throw new BridgeProtocolError("Invalid app.bootstrap payload.");
  }

  return payload as unknown as AppSnapshot;
}

function parseHostStateEvent(
  payload: Record<string, unknown>,
): HostStateEvent | undefined {
  const expected =
    "state" in payload
      ? ["sessionId", "revision", "scope", "change", "state"]
      : ["sessionId", "revision", "scope", "change"];
  if (
    !hasExactFields(payload, expected) ||
    typeof payload.sessionId !== "string" ||
    payload.sessionId.length === 0 ||
    !Number.isSafeInteger(payload.revision) ||
    (payload.revision as number) < 0 ||
    typeof payload.scope !== "string" ||
    payload.scope.length === 0 ||
    (payload.change !== "replace" &&
      payload.change !== "remove" &&
      payload.change !== "reset" &&
      payload.change !== "ready") ||
    (payload.change === "replace" && !("state" in payload))
  ) {
    return undefined;
  }
  return payload as unknown as HostStateEvent;
}

function parseCommandResult(payload: Record<string, unknown>): CommandResult {
  if (
    !hasExactFields(payload, ["revision", "ok", "problem"]) ||
    !Number.isSafeInteger(payload.revision) ||
    (payload.revision as number) < 0 ||
    typeof payload.ok !== "boolean" ||
    (payload.ok && payload.problem !== null) ||
    (!payload.ok && !isWorkbenchProblem(payload.problem))
  ) {
    throw new BridgeProtocolError("Invalid app.command payload.");
  }
  return payload as unknown as CommandResult;
}

function isWorkbenchProblem(value: unknown): boolean {
  if (
    !isRecord(value) ||
    !hasExactFields(value, ["code", "category", "retryable", "messageKey"])
  ) {
    return false;
  }
  return (
    typeof value.code === "string" &&
    value.code.length > 0 &&
    (value.category === "InvalidCommand" ||
      value.category === "Unavailable" ||
      value.category === "Conflict" ||
      value.category === "Internal") &&
    typeof value.retryable === "boolean" &&
    typeof value.messageKey === "string" &&
    value.messageKey.length > 0
  );
}

function hasExactFields(
  value: Record<string, unknown>,
  expected: readonly string[],
): boolean {
  const actual = Object.keys(value);
  return (
    actual.length === expected.length &&
    expected.every((field) => field in value)
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
