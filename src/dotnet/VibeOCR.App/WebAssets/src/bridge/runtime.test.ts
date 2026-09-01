import { describe, expect, it } from "vitest";

import type {
  AppSnapshot,
  CommandResult,
  HostBridge,
  HostCommand,
  HostStateEvent,
} from "./client";
import type { AppViewState } from "../app/types";
import { WorkbenchWebRuntime } from "./runtime";

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
        runtimeLabel: "原生宿主已连接",
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
        runtimeLabel: "原生宿主已连接",
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
