import { describe, expect, it } from "vitest";

import type {
  AppSnapshot,
  CommandResult,
  HostBridge,
  HostCommand,
  HostStateEvent,
} from "./client";
import type { AppViewState } from "../app/types";
import { projectSnapshot, WorkbenchWebRuntime } from "./runtime";

class FakeHostBridge implements HostBridge {
  readonly commands: HostCommand[] = [];
  previewFlush?: (documentId: string) => Promise<boolean>;
  subscribePdfPreviewFlush(
    listener: (documentId: string) => Promise<boolean>,
  ): () => void {
    this.previewFlush = listener;
    return () => {
      this.previewFlush = undefined;
    };
  }
  nextResult: CommandResult = { revision: 8, ok: true, problem: null };
  private listener?: (event: HostStateEvent) => void;

  async bootstrap(): Promise<AppSnapshot> {
    return {
      sessionId: "session-1",
      revision: 7,
      route: "recognition",
      theme: "dark",
      capabilities: ["recognition.capture"],
      features: { recognition: { status: "ready" } },
    };
  }

  async execute(command: HostCommand): Promise<CommandResult> {
    this.commands.push(command);
    return this.nextResult;
  }

  subscribe(listener: (event: HostStateEvent) => void): () => void {
    this.listener = listener;
    return () => {
      if (this.listener === listener) this.listener = undefined;
    };
  }

  emit(event: HostStateEvent): void {
    this.listener?.(event);
  }
}

describe("WorkbenchWebRuntime", () => {
  it("native preview flush matches the mounted PDF and succeeds with no PDF work outside that route", async () => {
    const bridge = new FakeHostBridge();
    const runtime = new WorkbenchWebRuntime(bridge);
    await runtime.start(() => undefined);
    const a = "11111111111111111111111111111111",
      b = "22222222222222222222222222222222";
    expect(await bridge.previewFlush!(a)).toBe(true);
    bridge.emit({
      sessionId: "session-1",
      revision: 8,
      scope: "shell",
      change: "replace",
      state: { route: "pdf" },
    });
    bridge.emit({
      sessionId: "session-1",
      revision: 9,
      scope: "pdf",
      change: "replace",
      state: { documentId: a },
    });
    expect(await bridge.previewFlush!(a)).toBe(false);
    let done!: () => void;
    const deferred = new Promise<void>((resolve) => {
      done = resolve;
    });
    const unregister = runtime.actions.registerPdfPreviewFlush!(
      a,
      () => deferred,
    );
    expect(await bridge.previewFlush!(b)).toBe(false);
    let completed = false;
    const pending = bridge.previewFlush!(a).then((ok) => {
      completed = ok;
    });
    await Promise.resolve();
    expect(completed).toBe(false);
    done();
    await pending;
    expect(completed).toBe(true);
    unregister();
    runtime.actions.registerPdfPreviewFlush!(a, async () => undefined);
    expect(await bridge.previewFlush!(a)).toBe(true); // 空文档/无待提交的预览也明确完成。
    runtime.stop();
    expect(bridge.previewFlush).toBeUndefined();
  });

  it("waits for the exact retiring drain outside PDF and before returning to that document", async () => {
    const bridge = new FakeHostBridge();
    const runtime = new WorkbenchWebRuntime(bridge);
    await runtime.start(() => undefined);
    const a = "11111111111111111111111111111111";
    bridge.emit({
      sessionId: "session-1",
      revision: 8,
      scope: "pdf",
      change: "replace",
      state: { documentId: a, documents: [{ documentId: a }] },
    });
    let done!: () => void;
    const pending = new Promise<void>((resolve) => {
      done = resolve;
    });
    const unregister = runtime.actions.registerPdfPreviewFlush!(
      a,
      () => pending,
    );
    unregister(); // 实际组件已卸载，当前注册为空；保留其确切 drain。
    let complete = false;
    const native = bridge.previewFlush!(a).then((ok) => {
      complete = ok;
    });
    const returning = runtime.actions.run({
      type: "pdf.activateDocument",
      documentId: a,
    });
    await Promise.resolve();
    expect(complete).toBe(false);
    expect(bridge.commands).toHaveLength(0);
    expect(await bridge.previewFlush!("22222222222222222222222222222222")).toBe(
      false,
    );
    done();
    await native;
    expect(complete).toBe(true);
    expect(await returning).toBe(true);
    expect(bridge.commands[0]?.action).toBe("activateDocument");
    runtime.stop();
  });

  it("failed retiring drain blocks navigation and can retry until authoritative close releases it", async () => {
    const bridge = new FakeHostBridge();
    const runtime = new WorkbenchWebRuntime(bridge);
    await runtime.start(() => undefined);
    const a = "11111111111111111111111111111111";
    bridge.emit({
      sessionId: "session-1",
      revision: 8,
      scope: "pdf",
      change: "replace",
      state: { documentId: a, documents: [{ documentId: a }] },
    });
    let reject = true;
    let calls = 0;
    const unregister = runtime.actions.registerPdfPreviewFlush!(a, async () => {
      calls++;
      if (reject) throw new Error("unconfirmed");
    });
    unregister();
    await expect(bridge.previewFlush!(a)).rejects.toThrow("unconfirmed");
    runtime.actions.navigate("pdf");
    expect(
      await runtime.actions.run({
        type: "pdf.activateDocument",
        documentId: a,
      }),
    ).toBe(false);
    expect(bridge.commands).toHaveLength(0);
    reject = false;
    expect(await bridge.previewFlush!(a)).toBe(true);
    expect(calls).toBeGreaterThan(1);
    reject = true;
    const again = runtime.actions.registerPdfPreviewFlush!(a, async () => {
      calls++;
      throw new Error("unconfirmed");
    });
    again();
    await expect(bridge.previewFlush!(a)).rejects.toThrow("unconfirmed");
    bridge.emit({
      sessionId: "session-1",
      revision: 9,
      scope: "pdf",
      change: "replace",
      state: { documents: [] },
    });
    const before = calls;
    expect(await bridge.previewFlush!(a)).toBe(true);
    expect(calls).toBe(before);
    runtime.stop();
  });

  it.each([
    [{ supervisorStatus: "未就绪", isReady: false }, "识别服务未就绪"],
    [{ supervisorStatus: "正在连接", isReady: false }, "应用启动中…"],
    [{ supervisorStatus: "已就绪", isReady: true }, "应用已就绪"],
    [{ supervisorStatus: "已就绪", isReady: false }, "识别服务未就绪"],
    [{ supervisorStatus: "连接失败", isReady: false }, "识别服务连接失败"],
    [{ supervisorStatus: "协议不兼容", isReady: false }, "识别服务版本不兼容"],
  ])(
    "projects application readiness from diagnostics %j",
    async (diagnostics, label) => {
      const snapshot = await new FakeHostBridge().bootstrap();
      const state = projectSnapshot({ ...snapshot, features: { diagnostics } });
      expect(state.runtimeLabel).toBe(label);
      // The bridge remains available for local editing and recovery settings.
      expect(state.connected).toBe(true);
    },
  );

  it("updates readiness on startup, failure and recovery without navigation", async () => {
    const bridge = new FakeHostBridge();
    const runtime = new WorkbenchWebRuntime(bridge);
    const states: AppViewState[] = [];
    await runtime.start((state) => states.push(state));
    for (const [index, state] of [
      { supervisorStatus: "正在连接", isReady: false },
      { supervisorStatus: "已就绪", isReady: true },
      { supervisorStatus: "连接失败", isReady: false },
      { supervisorStatus: "已就绪", isReady: true },
    ].entries()) {
      bridge.emit({
        sessionId: "session-1",
        revision: 8 + index,
        scope: "diagnostics",
        change: "replace",
        state,
      });
    }
    expect(states.map((state) => state.runtimeLabel)).toEqual([
      "应用启动中…",
      "应用启动中…",
      "应用已就绪",
      "识别服务连接失败",
      "应用已就绪",
    ]);
    bridge.emit({
      sessionId: "session-1",
      revision: 12,
      scope: "diagnostics",
      change: "remove",
      state: null,
    });
    expect(states.at(-1)?.runtimeLabel).toBe("应用启动中…");
  });

  it("projects bootstrap and newer host state into a connected AppViewState", async () => {
    const bridge = new FakeHostBridge();
    const runtime = new WorkbenchWebRuntime(bridge);
    const states: unknown[] = [];

    await runtime.start((state) => states.push(state));
    bridge.emit({
      sessionId: "session-old",
      revision: 99,
      scope: "shell",
      change: "replace",
      state: { route: "pdf" },
    });
    bridge.emit({
      sessionId: "session-1",
      revision: 8,
      scope: "shell",
      change: "replace",
      state: { route: "batch" },
    });

    expect(states).toEqual([
      {
        connected: true,
        revision: 7,
        route: "recognition",
        theme: "dark",
        capabilities: ["recognition.capture"],
        features: { recognition: { status: "ready" } },
        runtimeLabel: "应用启动中…",
      },
      {
        connected: true,
        revision: 8,
        route: "batch",
        theme: "dark",
        capabilities: ["recognition.capture"],
        features: {
          recognition: { status: "ready" },
          shell: { route: "batch" },
        },
        runtimeLabel: "应用启动中…",
      },
    ]);
  });

  it("reports navigation and preserves extensible action payload fields", async () => {
    const bridge = new FakeHostBridge();
    const runtime = new WorkbenchWebRuntime(bridge);
    await runtime.start(() => undefined);

    runtime.actions.navigate("pdf");
    await runtime.actions.run({ type: "pdf.rotate", degrees: 90 });

    expect(bridge.commands).toEqual([
      {
        scope: "shell",
        action: "navigate",
        arguments: { route: "pdf" },
      },
      {
        scope: "pdf",
        action: "rotate",
        arguments: { degrees: 90 },
      },
    ]);
  });

  it("projects rejected host receipts into visible application state", async () => {
    const bridge = new FakeHostBridge();
    bridge.nextResult = {
      revision: 8,
      ok: false,
      problem: {
        code: "desktop_command_failed",
        category: "Internal",
        retryable: true,
        messageKey: "workbench.error.desktopCommandFailed",
      },
    };
    const runtime = new WorkbenchWebRuntime(bridge);
    const states: AppViewState[] = [];
    await runtime.start((state) => states.push(state));

    await runtime.actions.run({ type: "recognition.captureScreen" });

    expect(states.at(-1)?.commandProblem).toBe(
      "workbench.error.desktopCommandFailed",
    );

    bridge.nextResult = { revision: 9, ok: true, problem: null };
    await runtime.actions.run({ type: "recognition.captureScreen" });

    expect(states.at(-1)?.commandProblem).toBeUndefined();
  });
});
