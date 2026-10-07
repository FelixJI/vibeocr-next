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
