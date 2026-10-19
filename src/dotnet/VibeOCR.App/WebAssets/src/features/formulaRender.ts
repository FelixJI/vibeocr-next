import { BRIDGE_VERSION } from "../bridge/client";

const MAX_LATEX_BYTES = 4 * 1024;

function base64UrlDecode(value: string): string {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "=");
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (ch) => ch.charCodeAt(0));
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

export function decodeFormulaSource(value: string): string | undefined {
  try {
    const latex = base64UrlDecode(value);
    return latex.length > 0 &&
      new TextEncoder().encode(latex).byteLength <= MAX_LATEX_BYTES
      ? latex
      : undefined;
  } catch {
    return undefined;
  }
}

interface FormulaReport {
  readonly type: "formula.rendered" | "formula.renderFailed";
  readonly id: string;
  readonly payload: Readonly<Record<string, unknown>>;
}

export function postFormulaReport(
  report: Omit<FormulaReport, "payload"> & {
    readonly payload: Readonly<Record<string, unknown>>;
  },
): void {
  const channel = (
    window as unknown as {
      readonly chrome?: {
        readonly webview?: {
          postMessage(message: unknown): void;
        };
      };
    }
  ).chrome?.webview;
  channel?.postMessage({
    version: BRIDGE_VERSION,
    kind: "event",
    id: crypto.randomUUID(),
    type: report.type,
    payload: { id: report.id, ...report.payload },
  });
}

export function formulaRenderRequest(hash: string):
  | {
      readonly id: string;
      readonly latex: string;
    }
  | undefined {
  const query = hash.split("?")[1];
  if (query === undefined) return undefined;
  const params = new URLSearchParams(query);
  const id = params.get("render");
  const source = params.get("src");
  if (
    id === null ||
    !/^[0-9a-f]{32}$/.test(id) ||
    source === null ||
    source.length > 5500
  )
    return undefined;
  const latex = decodeFormulaSource(source);
  return latex === undefined ? undefined : { id, latex };
}
