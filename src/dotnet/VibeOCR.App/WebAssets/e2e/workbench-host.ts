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
  entryPath = "/",
  waitForFeature = true,
): Promise<void> {
  await page.addInitScript((initial) => {
    let revision = initial.revision;
    const listeners = new Set<(event: { data: unknown }) => void>();
    const commands: HostCommand[] = [];
    const captureReports: BridgeEnvelope[] = [];
    const pinReports: BridgeEnvelope[] = [];
    let rejectNext = false;
    let deferredAction: string | undefined;
    let clearPinEditorOnCommit = false;
    let deferredResponse: (() => void) | undefined;
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
        captureReports,
        pinReports,
        requestPinSnapshot: (sourceSession: string) => {
          const id = crypto.randomUUID();
          emit({
            version: 2,
            kind: "request",
            id,
            type: "pinEdit.requestSnapshot",
            payload: { sessionId: sourceSession },
          });
          return id;
        },
        state,
        deferCommand: (action: string, clearPinEditor = false) => {
          deferredAction = action;
          clearPinEditorOnCommit = clearPinEditor;
        },
        releaseCommand: () => {
          if (!deferredResponse) throw new Error("No deferred command");
          deferredResponse();
          deferredResponse = undefined;
        },
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
              if (
                request.type === "capture.pixelReady" ||
                request.type === "capture.pixelFailed"
              ) {
                captureReports.push(request);
                return;
              }
              if (
                request.type === "pinEdit.snapshotResult" ||
                request.type.startsWith("formula.")
              ) {
                pinReports.push(request);
                return;
              }
              const command = request.payload.command as unknown as HostCommand;
              commands.push(command);
              if (command.action === deferredAction) {
                deferredAction = undefined;
                deferredResponse = () =>
                  emit({
                    ...request,
                    kind: "response",
                    payload: { revision, ok: true, problem: null },
                  });
                if (clearPinEditorOnCommit) {
                  clearPinEditorOnCommit = false;
                  state("pinEdit", {
                    isBusy: false,
                    statusCode: "pinEdit.applied",
                    input: null,
                    screenshotSession: null,
                  });
                }
                return;
              }
              if (rejectNext) {
                rejectNext = false;
                emit({
                  ...request,
                  kind: "response",
                  payload: {
                    revision,
                    ok: false,
                    problem: {
                      code: "desktop_command_failed",
                      category: "Internal",
                      retryable: true,
                      messageKey: "workbench.error.commandFailed",
                    },
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
  await page.goto(entryPath);
  if (!waitForFeature) return;
  if (entryPath.includes("?scene=1"))
    await expect(
      page.getByRole("toolbar", { name: "图片编辑工具" }),
    ).toBeVisible();
  else await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
}

type TestHostWindow = Window & {
  __testHost: {
    commands: HostCommand[];
    pinReports: BridgeEnvelope[];
    requestPinSnapshot(sourceSession: string): string;
    rejectNext(): void;
    deferCommand(action: string, clearPinEditor?: boolean): void;
    releaseCommand(): void;
    state(scope: string, value: unknown): void;
  };
};

export async function requestPinSnapshot(
  page: Page,
  sourceSession: string,
): Promise<string> {
  return page.evaluate(
    (source) =>
      (window as TestHostWindow).__testHost.requestPinSnapshot(source),
    sourceSession,
  );
}

export async function readPinReports(page: Page): Promise<BridgeEnvelope[]> {
  return page.evaluate(() => (window as TestHostWindow).__testHost.pinReports);
}

export async function deferCommand(
  page: Page,
  action: string,
  clearPinEditor = false,
): Promise<void> {
  await page.evaluate(
    ([action, clearPinEditor]) =>
      (window as TestHostWindow).__testHost.deferCommand(
        action,
        clearPinEditor,
      ),
    [action, clearPinEditor] as const,
  );
}

export async function releaseCommand(page: Page): Promise<void> {
  await page.evaluate(() =>
    (window as TestHostWindow).__testHost.releaseCommand(),
  );
}

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

// 模拟原生边界已加载的权威三分类目录；无目录用例应显式省略。
export const readyImageRecognitionModes = [
  { modeId: "rapid_text", displayName: "文字识别", availability: "ready" },
  { modeId: "paddle_table", displayName: "表格识别", availability: "ready" },
  { modeId: "paddle_formula", displayName: "公式识别", availability: "ready" },
];

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
