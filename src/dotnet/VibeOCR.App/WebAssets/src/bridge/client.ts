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
  | "batch"
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
  execute(command: HostCommand): Promise<CommandResult>;
  subscribe(listener: (event: HostStateEvent) => void): () => void;
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

  constructor(
    private readonly transport: WebViewTransport,
    options: BridgeClientOptions = {},
  ) {
    this.idFactory = options.idFactory ?? (() => crypto.randomUUID());
    this.maxBytes = options.maxBytes ?? MAX_BRIDGE_BYTES;
    this.unsubscribe = transport.subscribe((data) => this.receive(data));
  }

  bootstrap(): Promise<AppSnapshot> {
    return this.request("app.bootstrap", {}).then((payload) => {
      const snapshot = parseAppSnapshot(payload);
      this.sessionId = snapshot.sessionId;
      this.revision = snapshot.revision;
      return snapshot;
    });
  }

  execute(command: HostCommand): Promise<CommandResult> {
    if (!this.sessionId) {
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

  dispose(): void {
    this.unsubscribe();
    const error = new BridgeProtocolError("Bridge client was disposed.");
    for (const pending of this.pending.values()) pending.reject(error);
    this.pending.clear();
    this.listeners.clear();
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
      this.rejectCorrelated(value, "Bridge response is not serializable.");
      return;
    }
    if (byteLength > this.maxBytes) {
      this.rejectCorrelated(value, "Bridge response exceeds the size limit.");
      return;
    }
    if (!isBridgeEnvelope(value)) {
      this.rejectCorrelated(value, "Bridge response envelope is invalid.");
      return;
    }

    if (value.kind === "event") {
      this.receiveEvent(value);
      return;
    }
    if (value.kind !== "response") return;

    const pending = this.pending.get(value.id);
    if (!pending) {
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

  private rejectCorrelated(value: unknown, message: string): void {
    if (!isRecord(value) || typeof value.id !== "string") return;
    const pending = this.pending.get(value.id);
    if (!pending) return;
    this.pending.delete(value.id);
    pending.reject(new BridgeProtocolError(message));
  }

  private receiveEvent(envelope: BridgeEnvelope): void {
    if (envelope.type !== "app.state") return;
    const event = parseHostStateEvent(envelope.payload);
    if (!event) return;
    if (
      event.sessionId !== this.sessionId ||
      !Number.isSafeInteger(event.revision) ||
      event.revision <= this.revision
    ) {
      return;
    }
    this.revision = event.revision;
    for (const listener of this.listeners) listener(event);
  }
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
  "batch",
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
    !isRecord(payload.features)
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
