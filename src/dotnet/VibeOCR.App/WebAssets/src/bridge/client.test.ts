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
