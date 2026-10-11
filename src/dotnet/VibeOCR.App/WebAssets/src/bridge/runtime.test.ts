import { describe, expect, it, vi } from "vitest";

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
  failure?: (error: Error) => void;
  subscribeStateFailure(listener: (error: Error) => void): () => void {
    this.failure = listener;
    return () => {
      if (this.failure === listener) this.failure = undefined;
    };
  }
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
  // 模拟已修复的 BridgeClient 契约：新 bootstrap 按类型撤销并 reject 旧在途请求。
  deferBootstrap = false;
  private boot?: {
    resolve: (snapshot: AppSnapshot) => void;
    reject: (error: Error) => void;
  };
  private listener?: (event: HostStateEvent) => void;

  async bootstrap(): Promise<AppSnapshot> {
    if (!this.deferBootstrap)
      return {
        sessionId: "session-1",
        revision: 7,
        route: "recognition",
        theme: "dark",
        capabilities: ["recognition.capture"],
        features: { recognition: { status: "ready" } },
      };
    this.boot?.reject(new Error("Bootstrap was superseded."));
    return new Promise<AppSnapshot>((resolve, reject) => {
      this.boot = { resolve, reject };
    });
  }

  settleBootstrap(override: Partial<AppSnapshot> = {}): void {
    const boot = this.boot;
    this.boot = undefined;
    boot?.resolve({
      sessionId: "session-1",
      revision: 7,
      route: "recognition",
      theme: "dark",
      capabilities: ["recognition.capture"],
      features: { recognition: { status: "ready" } },
      ...override,
    });
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
  it("explicit feature recovery waits for active and retiring PDF drafts before reloading only its surface", async () => {
    const bridge = new FakeHostBridge();
    const order: string[] = [];
    const runtime = new WorkbenchWebRuntime(
      bridge,
      () => undefined,
      () => {
        order.push("reload");
      },
    );
    await runtime.start(() => undefined);
    let acknowledge!: () => void;
    const pending = new Promise<void>((resolve) => {
      acknowledge = resolve;
    });
    const unregister = runtime.actions.registerPdfPreviewFlush!(
      "first",
      async () => {
        order.push("first");
        await pending;
      },
    );
    unregister();
    runtime.actions.registerPdfPreviewFlush!("active", async () => {
      order.push("active");
    });
    const recovery = runtime.actions.recoverFeatureAssets!();
    expect(runtime.actions.recoverFeatureAssets!()).toBe(recovery);
    await Promise.resolve();
    await Promise.resolve();
    expect(order).toContain("active");
    expect(order).not.toContain("reload");
    const latest = runtime.actions.registerPdfPreviewFlush!(
      "first",
      async () => {
        order.push("latest");
      },
    );
    latest();
    acknowledge();
    expect(await recovery).toBe(true);
    expect(order.indexOf("latest")).toBeLessThan(order.indexOf("reload"));
    expect(order.at(-1)).toBe("reload");
    expect(bridge.commands).toEqual([]);
    runtime.stop();
  });

  it("failed or timed out draft confirmation keeps recovery available and never reloads", async () => {
    vi.useFakeTimers();
    try {
      const bridge = new FakeHostBridge();
      const errors: Error[] = [];
      const reload = vi.fn();
      const runtime = new WorkbenchWebRuntime(
        bridge,
        (error) => {
          errors.push(error);
        },
        reload,
      );
      await runtime.start(() => undefined);
      let fail = true;
      const unregister = runtime.actions.registerPdfPreviewFlush!(
        "draft",
        async () => {
          if (fail) throw new Error("not acknowledged");
        },
      );
      unregister();
      await Promise.resolve();
      expect(await runtime.actions.recoverFeatureAssets!()).toBe(false);
      expect(reload).not.toHaveBeenCalled();
      fail = false;
      expect(await runtime.actions.recoverFeatureAssets!()).toBe(true);
      expect(reload).toHaveBeenCalledOnce();
      reload.mockClear();
      runtime.actions.registerPdfPreviewFlush!(
        "active",
        () => new Promise<void>(() => undefined),
      );
      const pending = runtime.actions.recoverFeatureAssets!();
      await vi.advanceTimersByTimeAsync(10_000);
      expect(await pending).toBe(false);
      expect(reload).not.toHaveBeenCalled();
      expect(errors.map((error) => error.message)).toEqual([
        "not acknowledged",
        "Draft confirmation timed out",
      ]);
      runtime.stop();
    } finally {
      vi.useRealTimers();
    }
  });

  it("shows state failure and lets the user resynchronize without replaying commands", async () => {
    const bridge = new FakeHostBridge();
    const errors: Error[] = [];
    const runtime = new WorkbenchWebRuntime(bridge, (error) =>
      errors.push(error),
    );
    let latest: AppViewState | undefined;
    await runtime.start((state) => {
      latest = state;
    });
    bridge.failure?.(new Error("incomplete"));
    expect(latest?.commandProblem).toBe("workbench.error.stateSyncFailed");
    expect(errors).toHaveLength(1);
    runtime.actions.recover?.();
    await Promise.resolve();
    await Promise.resolve();
    expect(latest?.commandProblem).toBeUndefined();
    expect(bridge.commands).toHaveLength(0);
    runtime.stop();
  });
  it("keeps a superseded recovery bootstrap from polluting the newer session", async () => {
    const bridge = new FakeHostBridge();
    bridge.deferBootstrap = true;
    const errors: Error[] = [];
    const runtime = new WorkbenchWebRuntime(bridge, (error) =>
      errors.push(error),
    );
    const states: AppViewState[] = [];
    const starting = runtime.start((state) => states.push(state));
    bridge.settleBootstrap();
    await starting;
    runtime.actions.recover?.(); // 第一次恢复：bootstrap 在途。
    runtime.actions.recover?.(); // 第二次恢复 supersede 第一次。
    bridge.settleBootstrap({
      sessionId: "session-2",
      revision: 9,
      features: { recognition: { status: "ready" }, pdf: { fresh: true } },
    });
    await new Promise((flush) => setTimeout(flush, 0));
    expect(errors).toEqual([]);
    expect(states.filter((state) => state.commandProblem)).toHaveLength(0);
    expect(states.at(-1)?.features).toEqual({
      recognition: { status: "ready" },
      pdf: { fresh: true },
    });
    runtime.stop();
  });
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
      scope: "shell",
      change: "ready",
      snapshot: { pdf: { documents: [] }, shell: { route: "recognition" } },
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

it("runtime forwards only its committed capture bootstrap and stops on unmount", async () => {
  const bridge = new FakeHostBridge();
  const reportCapturePixels = vi.fn();
  const capturedBridge = Object.assign(bridge, { reportCapturePixels });
  capturedBridge.deferBootstrap = true;
  const scene = {
    surfaceId: "surface",
    captureSessionId: "capture",
    inputUrl: "/image.png",
    inputRevision: 0,
    documentGeneration: 2,
    bootstrapGeneration: 3,
    x: 10,
    y: 20,
    width: 100,
    height: 50,
    desktopWidth: 3840,
    desktopHeight: 2160,
    initialTool: "select",
  };
  const runtime = new WorkbenchWebRuntime(capturedBridge);
  const start = runtime.start(() => undefined);
  capturedBridge.settleBootstrap({ captureScene: scene });
  await start;
  runtime.actions.reportCapturePixels?.({ ...scene });
  expect(reportCapturePixels).not.toHaveBeenCalled();
  runtime.actions.reportCapturePixels?.(scene);
  expect(reportCapturePixels).toHaveBeenCalledWith(scene, undefined);
  runtime.stop();
  runtime.actions.reportCapturePixels?.(scene);
  expect(reportCapturePixels).toHaveBeenCalledTimes(1);
});
