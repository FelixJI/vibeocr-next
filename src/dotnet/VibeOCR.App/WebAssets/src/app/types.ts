import type { AppRoute } from "../bridge/client";

export type ThemePreference = "system" | "light" | "dark";

export interface AppViewState {
  readonly connected: boolean;
  readonly revision: number;
  readonly route: AppRoute;
  readonly theme: ThemePreference;
  readonly capabilities: readonly string[];
  readonly features: Readonly<Record<string, unknown>>;
  readonly runtimeLabel: string;
}

export type AppActionType =
  | "recognition.selectImage"
  | "recognition.readClipboard"
  | "recognition.captureScreen"
  | "recognition.cancel"
  | "recognition.copy"
  | "recognition.export"
  | "batch.addFiles"
  | "batch.exportMarkdown"
  | "batch.start"
  | "batch.cancel"
  | "batch.clear"
  | "batch.moveItem"
  | "batch.removeItem"
  | "batch.setConcurrency"
  | "batch.setWindow"
  | "pdf.open"
  | "pdf.rotate"
  | "pdf.close"
  | "pdf.deletePages"
  | "pdf.ocrPages"
  | "pdf.save"
  | "pdf.selectPages"
  | "pdf.setWindow"
  | "qrcode.generate"
  | "qrcode.decode"
  | "qrcode.decodeClipboard"
  | "qrcode.cancel"
  | "qrcode.clear"
  | "qrcode.save"
  | "qrcode.openUrl"
  | "about.openProject"
  | "settings.refreshRuntime"
  | "settings.setStartup"
  | "settings.setHotkey"
  | "update.check"
  | "update.download"
  | "update.cancel"
  | "diagnostics.export";

export interface AppAction {
  readonly type: AppActionType;
  readonly [argument: string]: unknown;
}

export interface AppActions {
  readonly run: (action: AppAction) => void;
  readonly navigate: (route: AppRoute) => void;
  readonly setTheme: (theme: ThemePreference) => void;
}
