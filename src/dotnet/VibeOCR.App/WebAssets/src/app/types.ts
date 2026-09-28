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
  readonly commandProblem?: string;
}

export type AppActionType =
  | "recognition.selectImage"
  | "recognition.readClipboard"
  | "recognition.captureScreen"
  | "recognition.captureScreenshotSession"
  | "recognition.captureScreenshotTextSession"
  | "recognition.closeScreenshotSession"
  | "recognition.notifyScreenshotRevision"
  | "recognition.copyScreenshotImage"
  | "recognition.pinScreenshotImage"
  | "recognition.saveScreenshotImage"
  | "recognition.recognizeScreenshotImage"
  | "recognition.prepareScreenshotTextLayer"
  | "recognition.cancelScreenshotTextLayer"
  | "recognition.copyScreenshotSelection"
  | "recognition.cancel"
  | "recognition.copy"
  | "recognition.export"
  | "recognition.copyAnnotatedImage"
  | "recognition.saveAnnotatedImage"
  | "batch.addFiles"
  | "batch.exportAll"
  | "batch.start"
  | "batch.cancel"
  | "batch.clear"
  | "batch.moveItem"
  | "batch.removeItem"
  | "batch.setWindow"
  | "batch.setTaskEngine"
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
  | "settings.setActionHotkey"
  | "settings.resetActionHotkey"
  | "settings.setFloatingToolbarEnabled"
  | "settings.setFloatingToolbarLayout"
  | "settings.showFloatingToolbar"
  | "settings.hideFloatingToolbar"
  | "settings.setSource"
  | "settings.setAccelerator"
  | "settings.setFeature"
  | "settings.setMineruConnection"
  | "settings.prepareMineruConnection"
  | "settings.installRuntime"
  | "settings.confirmRuntimeInstall"
  | "settings.cancelRuntimeMaintenance"
  | "settings.retryRuntimeMaintenance"
  | "recognition.setTaskEngine"
  | "update.check"
  | "update.download"
  | "update.cancel"
  | "update.cancelRuntimeMaintenance"
  | "diagnostics.export";

export interface AppAction {
  readonly type: AppActionType;
  readonly [argument: string]: unknown;
}

export interface AppActions {
  readonly run: (action: AppAction) => Promise<boolean>;
  readonly navigate: (route: AppRoute) => void;
  readonly setTheme: (theme: ThemePreference) => void;
}
