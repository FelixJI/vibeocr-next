import { describe, expect, it } from "vitest";

import {
  BridgeClient,
  ChromeWebViewTransport,
  type BridgeEnvelope,
  type WebViewTransport,
} from "./client";

class FakeTransport implements WebViewTransport {
  readonly posted: BridgeEnvelope[] = [];
  private listener?: (data: unknown) => void;

  postMessage(message: BridgeEnvelope): void {
    this.posted.push(message);
  }

  subscribe(listener: (data: unknown) => void): () => void {
    this.listener = listener;
    return () => {
      if (this.listener === listener) this.listener = undefined;
    };
  }

  receive(message: BridgeEnvelope): void {
    this.listener?.(message);
  }
}

describe("HostBridge", () => {
  it("adapts the production chrome.webview message channel", () => {
    let listener: ((event: { readonly data: unknown }) => void) | undefined;
    const posted: unknown[] = [];
    const webview = {
      addEventListener: (
        type: "message",
        next: (event: { readonly data: unknown }) => void,
      ) => {
        expect(type).toBe("message");
        listener = next;
      },
      postMessage: (message: unknown) => posted.push(message),
      removeEventListener: (
        type: "message",
        current: (event: { readonly data: unknown }) => void,
      ) => {
        expect(type).toBe("message");
        if (listener === current) listener = undefined;
      },
    };
    const transport = new ChromeWebViewTransport(webview);
    const received: unknown[] = [];
    const unsubscribe = transport.subscribe((message) =>
      received.push(message),
    );
    const envelope: BridgeEnvelope = {
      version: 2,
      kind: "request",
      id: "request-1",
      type: "app.bootstrap",
      payload: {},
    };

    transport.postMessage(envelope);
    listener?.({ data: { response: true } });

    expect(posted).toEqual([envelope]);
    expect(received).toEqual([{ response: true }]);
    unsubscribe();
    listener?.({ data: { response: false } });
    expect(received).toHaveLength(1);
  });

  it("detects production WebView2 without mistaking a normal browser for a host", () => {
    const channel = {
      addEventListener: () => undefined,
      postMessage: () => undefined,
      removeEventListener: () => undefined,
    };

    expect(
      ChromeWebViewTransport.fromWindow({ chrome: { webview: channel } }),
    ).toBeInstanceOf(ChromeWebViewTransport);
    expect(ChromeWebViewTransport.fromWindow({ chrome: {} })).toBeUndefined();
    expect(ChromeWebViewTransport.fromWindow({})).toBeUndefined();
  });

  it("confirms native PDF flush only after its correlated pending draft settles", async () => {
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport, { idFactory: () => "boot" });
    const boot = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "boot",
      type: "app.bootstrap",
      payload: {
        sessionId: "s1",
        revision: 0,
        route: "pdf",
        theme: "light",
        capabilities: [],
        features: {},
      },
    });
    await boot;
    const doc = "11111111111111111111111111111111";
    let resolve!: (confirmed: boolean) => void;
    let calls = 0;
    bridge.subscribePdfPreviewFlush(async (id) => {
      expect(id).toBe(doc);
      calls++;
      return new Promise<boolean>((done) => {
        resolve = done;
      });
    });
    const flush: BridgeEnvelope = {
      version: 2,
      kind: "request",
      id: "native-flush",
      type: "pdf.flushPreview",
      payload: { sessionId: "s1", documentId: doc },
    };
    transport.receive({
      ...flush,
      payload: { ...flush.payload, sessionId: "old-session" },
    });
    expect(calls).toBe(0);
    transport.receive(flush);
    expect(transport.posted).toHaveLength(1);
    transport.receive({ ...flush, id: "parallel" });
    await Promise.resolve();
    expect(transport.posted.at(-1)).toMatchObject({
      kind: "response",
      id: "parallel",
      payload: { ok: false },
    });
    resolve(true);
    await Promise.resolve();
    await Promise.resolve();
    expect(transport.posted.at(-1)).toEqual({
      ...flush,
      kind: "response",
      payload: { ...flush.payload, ok: true },
    });
    bridge.subscribePdfPreviewFlush(async () => {
      throw new Error("unconfirmed");
    });
    transport.receive({ ...flush, id: "failed" });
    await Promise.resolve();
    await Promise.resolve();
    expect(transport.posted.at(-1)).toMatchObject({
      id: "failed",
      payload: { ok: false },
    });
    bridge.dispose();
  });

  it("bootstraps through one versioned correlated request", async () => {
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => "request-1",
    });

    const pending = bridge.bootstrap();

    expect(transport.posted).toEqual([
      {
        version: 2,
        kind: "request",
        id: "request-1",
        type: "app.bootstrap",
        payload: {},
      },
    ]);

    transport.receive({
      version: 2,
      kind: "response",
      id: "request-1",
      type: "app.bootstrap",
      payload: {
        sessionId: "session-1",
        revision: 3,
        route: "recognition",
        theme: "system",
        capabilities: ["recognition.capture"],
        features: {},
      },
    });

    await expect(pending).resolves.toMatchObject({
      sessionId: "session-1",
      revision: 3,
      route: "recognition",
      capabilities: ["recognition.capture"],
    });
  });

  it("rejects a bootstrap response whose payload is not the exact contract", async () => {
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => "request-1",
    });
    const pending = bridge.bootstrap();

    transport.receive({
      version: 2,
      kind: "response",
      id: "request-1",
      type: "app.bootstrap",
      payload: {
        sessionId: "session-1",
        revision: 0,
        route: "recognition",
        capabilities: [],
        features: {},
      },
    });

    await expect(pending).rejects.toThrow("bootstrap payload");
  });

  it("rejects a correlated response over the 64 KiB protocol boundary", async () => {
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => "request-1",
      maxBytes: 256,
    });
    const pending = bridge.bootstrap();

    transport.receive({
      version: 2,
      kind: "response",
      id: "request-1",
      type: "app.bootstrap",
      payload: {
        sessionId: "session-1",
        revision: 0,
        route: "recognition",
        theme: "system",
        capabilities: [],
        features: { recognition: { text: "x".repeat(512) } },
      },
    });

    await expect(pending).rejects.toThrow("size limit");
  });

  it("rejects a command receipt whose payload is not the exact contract", async () => {
    const ids = ["bootstrap-1", "command-1"];
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => ids.shift()!,
    });
    const bootstrap = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "bootstrap-1",
      type: "app.bootstrap",
      payload: {
        sessionId: "session-1",
        revision: 0,
        route: "recognition",
        theme: "system",
        capabilities: [],
        features: {},
      },
    });
    await bootstrap;

    const pending = bridge.execute({
      scope: "shell",
      action: "navigate",
      arguments: { route: "batch" },
    });
    transport.receive({
      version: 2,
      kind: "response",
      id: "command-1",
      type: "app.command",
      payload: { revision: 1 },
    });

    await expect(pending).rejects.toThrow("command payload");
  });

  it("executes in the active session and publishes only newer state", async () => {
    const ids = ["bootstrap-1", "command-1"];
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => ids.shift()!,
    });
    const bootstrap = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "bootstrap-1",
      type: "app.bootstrap",
      payload: {
        sessionId: "session-1",
        revision: 3,
        route: "recognition",
        theme: "system",
        capabilities: [],
        features: {},
      },
    });
    await bootstrap;

    const received: unknown[] = [];
    const unsubscribe = bridge.subscribe((event) => received.push(event));
    const pending = bridge.execute({
      scope: "recognition",
      action: "capture",
      arguments: {},
    });

    expect(transport.posted.at(-1)).toMatchObject({
      id: "command-1",
      type: "app.command",
      payload: {
        sessionId: "session-1",
        command: { scope: "recognition", action: "capture" },
      },
    });

    transport.receive({
      version: 2,
      kind: "event",
      id: "state-old",
      type: "app.state",
      payload: {
        sessionId: "session-1",
        revision: 3,
        scope: "recognition",
        change: "replace",
        state: { status: "stale" },
      },
    });
    transport.receive({
      version: 2,
      kind: "event",
      id: "state-malformed",
      type: "app.state",
      payload: {
        sessionId: "session-1",
        revision: 4,
        scope: "recognition",
        state: { status: "must-not-be-published" },
      },
    });
    transport.receive({
      version: 2,
      kind: "event",
      id: "state-new",
      type: "app.state",
      payload: {
        sessionId: "session-1",
        revision: 4,
        scope: "recognition",
        change: "replace",
        state: { status: "capturing" },
      },
    });
    transport.receive({
      version: 2,
      kind: "response",
      id: "command-1",
      type: "app.command",
      payload: { revision: 4, ok: true, problem: null },
    });

    await expect(pending).resolves.toEqual({
      revision: 4,
      ok: true,
      problem: null,
    });
    expect(received).toEqual([
      {
        sessionId: "session-1",
        revision: 4,
        scope: "recognition",
        change: "replace",
        state: { status: "capturing" },
      },
    ]);

    unsubscribe();
  });
});

describe("capture bootstrap presentation", () => {
  const scene = {
    surfaceId: "surface",
    captureSessionId: "capture",
    inputUrl: "https://app.vibeocr/__resource/input",
    inputRevision: 0,
    documentGeneration: 2,
    bootstrapGeneration: 3,
    x: 100,
    y: 100,
    width: 800,
    height: 400,
    desktopWidth: 3840,
    desktopHeight: 2160,
    initialTool: "select",
  };
  function response(
    transport: FakeTransport,
    payload: Record<string, unknown>,
  ) {
    const request = transport.posted.at(-1)!;
    transport.receive({ ...request, kind: "response", payload });
  }
  function state(
    transport: FakeTransport,
    change: "reset" | "replace" | "ready",
    scope: string,
    value?: unknown,
  ) {
    transport.receive({
      version: 2,
      kind: "event",
      id: "state",
      type: "app.state",
      payload: {
        sessionId: "bridge",
        revision: 1,
        scope,
        change,
        ...(value !== undefined ? { state: value } : {}),
      },
    });
  }
  const metadata = {
    sessionId: "bridge",
    revision: 1,
    route: "imageEdit",
    theme: "dark",
    capabilities: [],
    captureScene: scene,
  };

  it("keeps large startup metadata private until the complete snapshot atomically commits", async () => {
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport);
    const starting = bridge.bootstrap();
    response(transport, {
      ...metadata,
      stateTransfer: "snapshot",
      stateScopes: ["shell", "recognition"],
    });
    await Promise.resolve();
    bridge.reportCapturePixels(scene);
    expect(transport.posted).toHaveLength(1);
    state(transport, "reset", "shell");
    state(transport, "replace", "shell", { route: "imageEdit" });
    state(transport, "replace", "recognition", {
      input: { url: scene.inputUrl },
      screenshotSession: { sessionId: "capture", revision: 0 },
    });
    state(transport, "ready", "shell");
    const snapshot = await starting;
    expect(snapshot.captureScene).toBe(scene);
    bridge.reportCapturePixels(snapshot.captureScene!);
    bridge.reportCapturePixels(snapshot.captureScene!);
    expect(transport.posted).toHaveLength(2);
    expect(transport.posted[1]).toMatchObject({
      kind: "event",
      type: "capture.pixelReady",
      payload: { sessionId: "bridge", captureScene: scene },
    });
    bridge.dispose();
  });

  it("rejects invalid geometry and suppresses reports from superseded or disposed bootstraps", async () => {
    const transport = new FakeTransport();
    const bridge = new BridgeClient(transport);
    const malformed = bridge.bootstrap();
    response(transport, {
      ...metadata,
      captureScene: { ...scene, width: 9999 },
      features: {},
    });
    await expect(malformed).rejects.toThrow("Invalid app.bootstrap");
    const first = bridge.bootstrap();
    response(transport, { ...metadata, features: {} });
    const old = await first;
    const newerScene = { ...scene, bootstrapGeneration: 4 };
    const second = bridge.bootstrap();
    response(transport, {
      ...metadata,
      captureScene: newerScene,
      features: {},
    });
    await second;
    bridge.reportCapturePixels(old.captureScene!);
    expect(
      transport.posted.filter((message) => message.type.startsWith("capture.")),
    ).toHaveLength(0);
    bridge.reportCapturePixels(newerScene, "sourceDecode");
    expect(transport.posted.at(-1)).toMatchObject({
      type: "capture.pixelFailed",
      payload: { reason: "sourceDecode" },
    });
    bridge.dispose();
    bridge.reportCapturePixels(newerScene);
    expect(
      transport.posted.filter((message) => message.type.startsWith("capture.")),
    ).toHaveLength(1);
  });
});
