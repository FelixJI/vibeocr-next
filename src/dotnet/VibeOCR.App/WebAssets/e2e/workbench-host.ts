import { expect, type Page } from "@playwright/test";
import type {
  AppSnapshot,
  BridgeEnvelope,
  HostCommand,
} from "../src/bridge/client";

// Only the native boundary is simulated. Tests exercise the production React,
// router, BridgeClient and WorkbenchWebRuntime through real browser clicks.
export async function mountHost(
  page: Page,
  snapshot: AppSnapshot,
): Promise<void> {
  await page.addInitScript((initial) => {
    let revision = initial.revision;
    const listeners = new Set<(event: { data: unknown }) => void>();
    const commands: HostCommand[] = [];
    let rejectNext = false;
    const emit = (data: unknown) =>
      listeners.forEach((listener) => listener({ data }));
    const state = (scope: string, value: unknown) => {
      emit({
        version: 2,
        kind: "event",
        id: `event-${++revision}`,
        type: "app.state",
        payload: {
          sessionId: initial.sessionId,
          revision,
          scope,
          change: "replace",
          state: value,
        },
      });
    };
    Object.assign(window, {
      __testHost: {
        commands,
        state,
        rejectNext: () => {
          rejectNext = true;
        },
      },
      chrome: {
        webview: {
          addEventListener: (
            _type: string,
            listener: (event: { data: unknown }) => void,
          ) => listeners.add(listener),
          removeEventListener: (
            _type: string,
            listener: (event: { data: unknown }) => void,
          ) => listeners.delete(listener),
          postMessage: (request: BridgeEnvelope) =>
            queueMicrotask(() => {
              if (request.type === "app.bootstrap") {
                emit({ ...request, kind: "response", payload: initial });
                return;
              }
              const command = request.payload.command as unknown as HostCommand;
              commands.push(command);
              if (rejectNext) {
                rejectNext = false;
                emit({
                  ...request,
                  kind: "response",
                  payload: {
                    revision,
                    ok: false,
                    problem: { messageKey: "workbench.error.commandFailed" },
                  },
                });
                return;
              }
              emit({
                ...request,
                kind: "response",
                payload: { revision, ok: true, problem: null },
              });
              if (command.scope === "shell" && command.action === "navigate")
                state("shell", command.arguments);
              if (command.scope === "settings" && command.action === "setTheme")
                state("settings", command.arguments);
            }),
        },
      },
    });
  }, snapshot);
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
}

type TestHostWindow = Window & {
  __testHost: {
    commands: HostCommand[];
    rejectNext(): void;
    state(scope: string, value: unknown): void;
  };
};

export async function rejectNextCommand(page: Page): Promise<void> {
  await page.evaluate(() => (window as TestHostWindow).__testHost.rejectNext());
}

export async function sendState(
  page: Page,
  scope: string,
  state: unknown,
): Promise<void> {
  await page.evaluate(
    ({ scope, state }) => {
      (window as TestHostWindow).__testHost.state(scope, state);
    },
    { scope, state },
  );
}

export async function expectCommand(
  page: Page,
  expected: HostCommand,
): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(() => (window as TestHostWindow).__testHost.commands),
    )
    .toContainEqual(expected);
}

export const snapshot: AppSnapshot = {
  sessionId: "layout-e2e",
  revision: 1,
  route: "recognition",
  theme: "light",
  capabilities: [
    "recognition.file",
    "recognition.capture",
    "recognition.clipboard",
    "recognition.results",
    "pdf.open",
    "pdf.edit",
    "pdf.rotate",
    "pdf.save",
  ],
  features: { recognition: { isBusy: false, statusCode: "recognition.ready" } },
};
