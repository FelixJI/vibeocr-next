import { describe, expect, it, vi } from "vitest";
import {
  BridgeClient,
  type BridgeEnvelope,
  type HostStateEvent,
  type WebViewTransport,
} from "./client";

class Transport implements WebViewTransport {
  posted: BridgeEnvelope[] = [];
  receive: (data: unknown) => void = () => undefined;
  postMessage(message: BridgeEnvelope) {
    this.posted.push(message);
  }
  subscribe(listener: (data: unknown) => void) {
    this.receive = listener;
    return () => {
      this.receive = () => undefined;
    };
  }
}

async function setup() {
  const transport = new Transport();
  let id = 0;
  const bridge = new BridgeClient(transport, { idFactory: () => String(++id) });
  const boot = bridge.bootstrap();
  transport.receive({
    version: 2,
    kind: "response",
    id: "1",
    type: "app.bootstrap",
    payload: {
      sessionId: "current",
      revision: 5,
      route: "pdf",
      theme: "light",
      capabilities: [],
      features: { pdf: { documents: [] }, settings: { theme: "light" } },
    },
  });
  await boot;
  const received: HostStateEvent[] = [];
  bridge.subscribe((event) => received.push(event));
  function send(
    change: HostStateEvent["change"],
    scope = "shell",
    state: unknown = null,
    revision = 8,
    sessionId = "current",
  ) {
    transport.receive({
      version: 2,
      kind: "event",
      id: "state",
      type: "app.state",
      payload: { sessionId, revision, scope, change, state },
    });
  }
  return { transport, bridge, received, send };
}

describe("bounded snapshot recovery", () => {
  it.each(["checkpoint", "replace", "oversized"])(
    "startup immediately rejects %s before reset",
    async (kind) => {
      const transport = new Transport();
      const bridge = new BridgeClient(transport, { idFactory: () => "boot" });
      const boot = bridge.bootstrap();
      const rejected = expect(boot).rejects.toThrow();
      transport.receive({
        version: 2,
        kind: "response",
        id: "boot",
        type: "app.bootstrap",
        payload: {
          sessionId: "new",
          revision: 3,
          route: "pdf",
          theme: "light",
          capabilities: [],
          stateTransfer: "snapshot",
          stateScopes: ["shell", "pdf"],
        },
      });
      await Promise.resolve();
      const frame = {
        version: 2,
        kind: "event",
        id: "state",
        type: "app.state",
        payload: {
          sessionId: "new",
          revision: kind === "checkpoint" ? 4 : 3,
          scope: "shell",
          change: kind === "checkpoint" ? "reset" : "replace",
          state: kind === "oversized" ? "x".repeat(70_000) : {},
        },
      };
      if (kind === "oversized") transport.receive(frame);
      else expect(() => transport.receive(frame)).toThrow();
      await rejected;
      bridge.dispose();
    },
  );

  it("large startup resolves only after a complete same-checkpoint snapshot and then accepts concurrent updates", async () => {
    const transport = new Transport();
    let id = 0;
    const bridge = new BridgeClient(transport, {
      idFactory: () => String(++id),
    });
    const scopes = [
      "shell",
      "recognition",
      "imageEdit",
      "batch",
      "pdf",
      "qrcode",
      "settings",
      "update",
      "about",
      "diagnostics",
    ];
    let completed = false;
    const received: HostStateEvent[] = [];
    bridge.subscribe((event) => {
      received.push(event);
    });
    const boot = bridge.bootstrap().then((snapshot) => {
      completed = true;
      return snapshot;
    });
    transport.receive({
      version: 2,
      kind: "response",
      id: "1",
      type: "app.bootstrap",
      payload: {
        sessionId: "current",
        revision: 99,
        route: "pdf",
        theme: "system",
        capabilities: [],
        stateTransfer: "snapshot",
        stateScopes: scopes,
      },
    });
    await Promise.resolve();
    const send = (
      change: string,
      scope: string,
      state: unknown,
      revision = 99,
    ) => {
      const frame = {
        version: 2,
        kind: "event",
        id: "state",
        type: "app.state",
        payload: { sessionId: "current", revision, scope, change, state },
      };
      expect(
        new TextEncoder().encode(JSON.stringify(frame)).byteLength,
      ).toBeLessThanOrEqual(64 * 1024);
      transport.receive(frame);
    };
    send("reset", "shell", null);
    await expect(
      bridge.execute({
        scope: "settings",
        action: "setTheme",
        arguments: { theme: "dark" },
      }),
    ).rejects.toThrow("bootstrap");
    expect(transport.posted).toHaveLength(1);
    for (const scope of scopes)
      send(
        "replace",
        scope,
        scope === "shell"
          ? { route: "pdf" }
          : scope === "settings"
            ? { theme: "dark", terminal: "failed" }
            : { text: "x".repeat(9000) },
      );
    expect(completed).toBe(false);
    expect(received).toEqual([]);
    send("ready", "shell", null);
    const snapshot = await boot;
    expect(
      new TextEncoder().encode(JSON.stringify(snapshot)).byteLength,
    ).toBeGreaterThan(64 * 1024);
    expect(snapshot.theme).toBe("dark");
    expect(Object.keys(snapshot.features)).toHaveLength(9);
    send("replace", "settings", { theme: "light" }, 100);
    expect(received[0]?.revision).toBe(100);
    bridge.dispose();
  });

  it("startup rejects a duplicate scope and keeps ordinary revision checks strict", async () => {
    const transport = new Transport();
    const bridge = new BridgeClient(transport, { idFactory: () => "boot" });
    const boot = bridge.bootstrap();
    const rejected = expect(boot).rejects.toThrow("scope");
    transport.receive({
      version: 2,
      kind: "response",
      id: "boot",
      type: "app.bootstrap",
      payload: {
        sessionId: "new",
        revision: 3,
        route: "pdf",
        theme: "light",
        capabilities: [],
        stateTransfer: "snapshot",
        stateScopes: ["shell", "pdf"],
      },
    });
    await Promise.resolve();
    const send = (change: string, scope: string) =>
      transport.receive({
        version: 2,
        kind: "event",
        id: "state",
        type: "app.state",
        payload: { sessionId: "new", revision: 3, scope, change, state: {} },
      });
    send("reset", "shell");
    send("replace", "pdf");
    expect(() => send("replace", "pdf")).toThrow("scope");
    await rejected;
    bridge.dispose();
  });

  it("startup missing ready times out and superseding bootstrap ignores the old session", async () => {
    vi.useFakeTimers();
    const transport = new Transport();
    let id = 0;
    const bridge = new BridgeClient(transport, {
      idFactory: () => String(++id),
    });
    const control = (id: string, sessionId: string) =>
      transport.receive({
        version: 2,
        kind: "response",
        id,
        type: "app.bootstrap",
        payload: {
          sessionId,
          revision: 3,
          route: "pdf",
          theme: "light",
          capabilities: [],
          stateTransfer: "snapshot",
          stateScopes: ["shell", "pdf"],
        },
      });
    try {
      const first = bridge.bootstrap();
      const timeout = expect(first).rejects.toThrow("did not complete");
      control("1", "old");
      await Promise.resolve();
      await vi.advanceTimersByTimeAsync(10_000);
      await timeout;
      await expect(
        bridge.execute({
          scope: "settings",
          action: "setTheme",
          arguments: { theme: "dark" },
        }),
      ).rejects.toThrow("bootstrap");
      expect(transport.posted).toHaveLength(1);
      const second = bridge.bootstrap();
      const superseded = expect(second).rejects.toThrow("superseded");
      control("2", "old2");
      await Promise.resolve();
      const third = bridge.bootstrap();
      await superseded;
      await expect(
        bridge.execute({
          scope: "settings",
          action: "setTheme",
          arguments: { theme: "dark" },
        }),
      ).rejects.toThrow("bootstrap");
      transport.receive({
        version: 2,
        kind: "response",
        id: "3",
        type: "app.bootstrap",
        payload: {
          sessionId: "fresh",
          revision: 4,
          route: "pdf",
          theme: "light",
          capabilities: [],
          features: { pdf: {} },
        },
      });
      await third;
      const received: HostStateEvent[] = [];
      bridge.subscribe((event) => {
        received.push(event);
      });
      transport.receive({
        version: 2,
        kind: "event",
        id: "late",
        type: "app.state",
        payload: {
          sessionId: "old2",
          revision: 3,
          scope: "shell",
          change: "ready",
          state: null,
        },
      });
      expect(received).toEqual([]);
    } finally {
      bridge.dispose();
      vi.useRealTimers();
    }
  });

  it("terminates every superseded in-flight bootstrap and ignores their late replies", async () => {
    const ids = ["boot-1", "boot-2", "boot-3", "cmd-1"];
    const transport = new Transport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => ids.shift()!,
    });
    const first = bridge.bootstrap();
    const second = bridge.bootstrap();
    const third = bridge.bootstrap();
    // 宿主换代后不再回复旧请求；被 supersede 的旧 bootstrap 必须就地终结，
    // 否则重复恢复会无界积累在途请求与未终结 Promise。
    await expect(first).rejects.toThrow("superseded");
    await expect(second).rejects.toThrow("superseded");
    const late = (id: string, sessionId: string) =>
      transport.receive({
        version: 2,
        kind: "response",
        id,
        type: "app.bootstrap",
        payload: {
          sessionId,
          revision: 40,
          route: "pdf",
          theme: "dark",
          capabilities: [],
          features: { pdf: { stale: true } },
        },
      });
    late("boot-1", "stale-1");
    late("boot-2", "stale-2");
    transport.receive({
      version: 2,
      kind: "response",
      id: "boot-3",
      type: "app.bootstrap",
      payload: {
        sessionId: "current",
        revision: 5,
        route: "pdf",
        theme: "light",
        capabilities: [],
        features: { pdf: { documents: [] } },
      },
    });
    await expect(third).resolves.toMatchObject({
      sessionId: "current",
      revision: 5,
    });
    // 已撤销请求的迟到响应即使载荷合法也不得改写当前会话或抛出 unsolicited。
    late("boot-1", "stale-1");
    const command = bridge.execute({
      scope: "settings",
      action: "setTheme",
      arguments: { theme: "dark" },
    });
    expect(transport.posted.at(-1)?.payload).toMatchObject({
      sessionId: "current",
      command: { scope: "settings", action: "setTheme" },
    });
    transport.receive({
      version: 2,
      kind: "response",
      id: "cmd-1",
      type: "app.command",
      payload: { revision: 6, ok: true, problem: null },
    });
    await expect(command).resolves.toEqual({
      revision: 6,
      ok: true,
      problem: null,
    });
    const received: HostStateEvent[] = [];
    bridge.subscribe((event) => received.push(event));
    late("boot-2", "stale-2");
    transport.receive({
      version: 2,
      kind: "event",
      id: "state",
      type: "app.state",
      payload: {
        sessionId: "stale-1",
        revision: 40,
        scope: "pdf",
        change: "replace",
        state: { stale: true },
      },
    });
    transport.receive({
      version: 2,
      kind: "event",
      id: "state",
      type: "app.state",
      payload: {
        sessionId: "current",
        revision: 6,
        scope: "pdf",
        change: "replace",
        state: { fresh: true },
      },
    });
    expect(received).toEqual([
      {
        sessionId: "current",
        revision: 6,
        scope: "pdf",
        change: "replace",
        state: { fresh: true },
      },
    ]);
    bridge.dispose();
  });

  it("keeps ordinary command receipts across a bootstrap supersede", async () => {
    const ids = ["boot-1", "cmd-1", "boot-2"];
    const transport = new Transport();
    const bridge = new BridgeClient(transport, {
      idFactory: () => ids.shift()!,
    });
    const boot = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "boot-1",
      type: "app.bootstrap",
      payload: {
        sessionId: "current",
        revision: 2,
        route: "pdf",
        theme: "light",
        capabilities: [],
        features: { pdf: {} },
      },
    });
    await boot;
    const command = bridge.execute({
      scope: "pdf",
      action: "cancel",
      arguments: {},
    });
    const reboot = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "cmd-1",
      type: "app.command",
      payload: { revision: 3, ok: true, problem: null },
    });
    await expect(command).resolves.toEqual({
      revision: 3,
      ok: true,
      problem: null,
    });
    transport.receive({
      version: 2,
      kind: "response",
      id: "boot-2",
      type: "app.bootstrap",
      payload: {
        sessionId: "fresh",
        revision: 4,
        route: "pdf",
        theme: "light",
        capabilities: [],
        features: { pdf: { fresh: true } },
      },
    });
    await expect(reboot).resolves.toMatchObject({
      sessionId: "fresh",
      revision: 4,
    });
    bridge.dispose();
  });

  it("still rejects type mismatches for recorded ids and unsolicited replies", async () => {
    const transport = new Transport();
    const bridge = new BridgeClient(transport, { idFactory: () => "boot-1" });
    const boot = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "boot-1",
      type: "app.command",
      payload: { revision: 1, ok: true, problem: null },
    });
    await expect(boot).rejects.toThrow("type does not match");
    expect(() =>
      transport.receive({
        version: 2,
        kind: "response",
        id: "ghost",
        type: "app.command",
        payload: { revision: 1, ok: true, problem: null },
      }),
    ).toThrow("unsolicited");
    bridge.dispose();
  });

  it("reports a missing ready and recovers through a fresh bootstrap", async () => {
    vi.useFakeTimers();
    const { send, bridge, transport, received } = await setup();
    const failures: Error[] = [];
    bridge.subscribeStateFailure((error) => failures.push(error));
    try {
      send("reset");
      send("replace", "shell", { route: "pdf" });
      vi.advanceTimersByTime(10_000);
      expect(failures).toHaveLength(1);
      send("replace", "pdf", {}, 11);
      expect(received).toHaveLength(0);
      const boot = bridge.bootstrap();
      transport.receive({
        version: 2,
        kind: "response",
        id: "2",
        type: "app.bootstrap",
        payload: {
          sessionId: "new",
          revision: 12,
          route: "pdf",
          theme: "light",
          capabilities: [],
          features: { pdf: {} },
        },
      });
      await boot;
      send("replace", "pdf", { fresh: true }, 13, "new");
      expect(received[0]?.state).toEqual({ fresh: true });
    } finally {
      bridge.dispose();
      vi.useRealTimers();
    }
  });
  it("commits at ready atomically while receipts and PDF flush remain independent", async () => {
    const { send, bridge, transport, received } = await setup();
    send("reset");
    send("replace", "shell", { route: "pdf" });
    send("replace", "pdf", { documents: [{ documentId: "kept" }] });
    expect(received).toHaveLength(0);
    const command = bridge.execute({
      scope: "pdf",
      action: "cancel",
      arguments: {},
    });
    transport.receive({
      version: 2,
      kind: "response",
      id: "2",
      type: "app.command",
      payload: {
        revision: 9,
        ok: false,
        problem: {
          code: "conflict",
          category: "Conflict",
          retryable: false,
          messageKey: "failed",
        },
      },
    });
    expect((await command).ok).toBe(false);
    bridge.subscribePdfPreviewFlush(async () => true);
    transport.receive({
      version: 2,
      kind: "request",
      id: "flush",
      type: "pdf.flushPreview",
      payload: { sessionId: "current", documentId: "a".repeat(32) },
    });
    await Promise.resolve();
    await Promise.resolve();
    expect(transport.posted.at(-1)?.payload.ok).toBe(true);
    send("replace", "settings", { theme: "dark" });
    send("ready");
    expect(received).toHaveLength(1);
    expect(received[0]?.snapshot?.settings).toEqual({ theme: "dark" });
    send("replace", "pdf", {}, 8);
    expect(received).toHaveLength(1);
    send("replace", "pdf", {}, 9);
    expect(received).toHaveLength(2);
  });

  it.each(["missing", "duplicate", "unknown", "oversized", "checkpoint"])(
    "fails closed for %s recovery",
    async (kind) => {
      const { send, received } = await setup();
      send("reset");
      send("replace", "shell", { route: "pdf" });
      if (kind === "missing") expect(() => send("ready")).toThrow();
      if (kind === "duplicate") expect(() => send("reset")).toThrow();
      if (kind === "unknown")
        expect(() => send("replace", "unbounded", {})).toThrow();
      if (kind === "oversized") send("replace", "pdf", "中".repeat(30000));
      if (kind === "checkpoint")
        expect(() => send("replace", "pdf", {}, 9)).toThrow();
      send("replace", "pdf", {}, 10);
      send("ready", "shell", null, 10);
      expect(received).toHaveLength(0);
    },
  );

  it("ignores old-session callbacks and discards an incomplete transaction on bootstrap", async () => {
    const { send, bridge, transport, received } = await setup();
    send("reset");
    send("replace", "pdf", {}, 8, "old");
    expect(received).toHaveLength(0);
    const boot = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "2",
      type: "app.bootstrap",
      payload: {
        sessionId: "new",
        revision: 10,
        route: "pdf",
        theme: "light",
        capabilities: [],
        features: { pdf: {} },
      },
    });
    await boot;
    send("ready");
    send("replace", "pdf", { fresh: true }, 11, "new");
    expect(received).toHaveLength(1);
    expect(received[0]?.state).toEqual({ fresh: true });
  });

  it("replies an accepted PDF flush with its original session after a bootstrap supersede", async () => {
    const { bridge, transport } = await setup();
    const doc = "a".repeat(32);
    let confirm!: (ok: boolean) => void;
    bridge.subscribePdfPreviewFlush(
      () =>
        new Promise<boolean>((done) => {
          confirm = done;
        }),
    );
    transport.receive({
      version: 2,
      kind: "request",
      id: "flush-1",
      type: "pdf.flushPreview",
      payload: { sessionId: "current", documentId: doc },
    });
    // flush 已按 "current" 会话接受；同文档内的恢复式 bootstrap 换成新会话。
    const reboot = bridge.bootstrap();
    transport.receive({
      version: 2,
      kind: "response",
      id: "2",
      type: "app.bootstrap",
      payload: {
        sessionId: "fresh",
        revision: 6,
        route: "pdf",
        theme: "light",
        capabilities: [],
        features: { pdf: {} },
      },
    });
    await reboot;
    // 入站旧 session 的新 flush 请求仍被拒绝。
    transport.receive({
      version: 2,
      kind: "request",
      id: "flush-2",
      type: "pdf.flushPreview",
      payload: { sessionId: "current", documentId: doc },
    });
    const responses = () =>
      transport.posted.filter((message) => message.kind === "response");
    expect(responses()).toHaveLength(0);
    confirm(true);
    await Promise.resolve();
    await Promise.resolve();
    // 同一活着的 client 按原请求 session/doc/id 恰好回执一次。
    expect(responses()).toEqual([
      {
        version: 2,
        kind: "response",
        id: "flush-1",
        type: "pdf.flushPreview",
        payload: { sessionId: "current", documentId: doc, ok: true },
      },
    ]);
    bridge.dispose();
  });

  it("suppresses an accepted PDF flush reply after dispose", async () => {
    const { bridge, transport } = await setup();
    const doc = "b".repeat(32);
    let confirm!: (ok: boolean) => void;
    bridge.subscribePdfPreviewFlush(
      () =>
        new Promise<boolean>((done) => {
          confirm = done;
        }),
    );
    transport.receive({
      version: 2,
      kind: "request",
      id: "flush-1",
      type: "pdf.flushPreview",
      payload: { sessionId: "current", documentId: doc },
    });
    bridge.dispose();
    confirm(true);
    await Promise.resolve();
    await Promise.resolve();
    expect(
      transport.posted.filter((message) => message.kind === "response"),
    ).toHaveLength(0);
  });
});
