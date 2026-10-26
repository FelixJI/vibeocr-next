import type { CaptureScene, CapturePixelFailure } from "../bridge/captureScene";
import type { AppRoute } from "../bridge/client";

import type { EditorProjectInstall } from "../components/ImageCanvasEditor";
import type { ProjectOpenWire } from "../components/project/projectWire";

export interface ProjectOpenHandoffState {
  readonly pendingId: string;
  readonly sourceUrl: string;
  readonly phase: "confirming" | "awaitingState" | "uncertain" | "installing";
  readonly sessionId?: string;
  readonly install?: EditorProjectInstall;
}

export type ThemePreference = "system" | "light" | "dark";

export interface AppViewState {
  readonly connected: boolean;
  readonly captureScene?: CaptureScene | null;
  readonly revision: number;
  readonly route: AppRoute;
  readonly theme: ThemePreference;
  readonly capabilities: readonly string[];
  readonly features: Readonly<Record<string, unknown>>;
  readonly runtimeLabel: string;
  readonly commandProblem?: string;
  /** 仅当前 Web runtime 内存交接，不进入宿主 wire 或持久化。 */
  readonly projectOpenHandoff?: ProjectOpenHandoffState;
}

export type AppActionType =
  | "settings.setEditorPreferences"
  | "settings.pickImageOutputDirectory"
  | "imageEdit.savePresetImage"
  | "recognition.savePresetImage"
  | "imageConversion.addFiles"
  | "imageConversion.clear"
  | "imageConversion.removeItem"
  | "imageConversion.moveItem"
  | "imageConversion.setWindow"
  | "imageConversion.prepareImageExport"
  | "imageConversion.exportImage"
  | "imageEdit.selectImage"
  | "imageEdit.readClipboard"
  | "imageEdit.pasteSystemContent"
  | "imageEdit.ackImport"
  | "imageEdit.rejectImport"
  | "imageEdit.releaseAssets"
  | "imageEdit.notifyScreenshotRevision"
  | "imageEdit.closeScreenshotSession"
  | "imageEdit.copyScreenshotImage"
  | "imageEdit.saveScreenshotImage"
  | "imageEdit.pinScreenshotImage"
  | "imageEdit.recognizeScreenshotImage"
  | "imageEdit.prepareScreenshotTextLayer"
  | "imageEdit.cancelScreenshotTextLayer"
  | "imageEdit.copyScreenshotSelection"
  | "imageEdit.copyAnnotatedImage"
  | "imageEdit.saveAnnotatedImage"
  | "imageEdit.saveProject"
  | "imageEdit.openProject"
  | "imageEdit.ackProjectOpen"
  | "imageEdit.rejectProjectOpen"
  // #238 贴图内编辑（pinEdit）：仅真实消费者的动作子集。
  | "pinEdit.notifyScreenshotRevision"
  | "pinEdit.closeScreenshotSession"
  | "pinEdit.copyScreenshotImage"
  | "pinEdit.saveScreenshotImage"
  | "pinEdit.copyAnnotatedImage"
  | "pinEdit.saveAnnotatedImage"
  | "pinEdit.ackImport"
  | "pinEdit.rejectImport"
  | "pinEdit.releaseAssets"
  | "pinEdit.applySnapshot"
  | "pinEdit.rejectProjectRestore"
  // pinEdit 预设输出：与 imageEdit/recognition 同一预设命令，但会话强绑定。
  | "pinEdit.savePresetImage"
  // 以下 pinEdit.* 仅为编辑器静态全量命令映射的类型完整性存在；宿主未
  // 实现且 pin 模式 UI 不可达（发送会被宿主 fail-closed 拒绝）。
  | "pinEdit.pinScreenshotImage"
  | "pinEdit.recognizeScreenshotImage"
  | "pinEdit.prepareScreenshotTextLayer"
  | "pinEdit.cancelScreenshotTextLayer"
  | "pinEdit.copyScreenshotSelection"
  | "pinEdit.showManager"
  | "pinEdit.setHistoryEnabled"
  | "pinEdit.listHistory"
  | "pinEdit.reopenHistoryEntry"
  | "pinEdit.deleteHistoryEntry"
  | "pinEdit.clearHistory"
  | "recognition.selectImage"
  | "recognition.readClipboard"
  | "recognition.openImageForEdit"
  | "recognition.pasteImageForEdit"
  | "recognition.pasteSystemContent"
  | "recognition.ackImport"
  | "recognition.rejectImport"
  | "recognition.releaseAssets"
  | "recognition.captureScreen"
  | "recognition.captureScreenshotSession"
  | "recognition.captureScreenshotTextSession"
  | "recognition.captureScrollingScreenshot"
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
  | "recognition.copyStructured"
  | "recognition.copyLines"
  | "recognition.copyStructuredCells"
  | "recognition.updateLine"
  | "recognition.export"
  | "recognition.copyAnnotatedImage"
  | "recognition.saveAnnotatedImage"
  | "recognition.saveProject"
  | "batch.addFiles"
  | "batch.exportAll"
  | "batch.start"
  | "batch.cancel"
  | "batch.clear"
  | "batch.moveItem"
  | "batch.removeItem"
  | "batch.setItemPageRange"
  | "batch.setWindow"
  | "batch.setTaskEngine"
  | "batch.setTaskModelPreset"
  | "pdf.activateDocument"
  | "pdf.saveAs"
  | "pdf.exportDocuments"
  | "pdf.cancelExport"
  | "pdf.setPreviewPosition"
  | "pdf.open"
  | "pdf.rotate"
  | "pdf.orient"
  | "pdf.correctOrientation"
  | "pdf.insertBlank"
  | "pdf.insertFrom"
  | "pdf.movePage"
  | "pdf.setCurrentPage"
  | "pdf.updateBlockText"
  | "pdf.retryPageInspect"
  | "pdf.close"
  | "pdf.deletePages"
  | "pdf.ocrPages"
  | "pdf.addTextLayers"
  | "pdf.deleteTextLayers"
  | "pdf.cancel"
  | "pdf.setProcessingSettings"
  | "pdf.save"
  | "pdf.selectPages"
  | "pdf.selectAll"
  | "pdf.selectPage"
  | "pdf.setWindow"
  | "qrcode.generate"
  | "qrcode.decodeCurrent"
  | "qrcode.copyImage"
  | "qrcode.decode"
  | "qrcode.decodeClipboard"
  | "qrcode.cancel"
  | "qrcode.clear"
  | "qrcode.save"
  | "qrcode.openUrl"
  | "about.openProject"
  | "settings.refreshRuntime"
  | "settings.refreshEnvironmentList"
  | "settings.createEnvironment"
  | "settings.previewEnvironmentInstall"
  | "settings.confirmEnvironmentInstall"
  | "settings.setEnvironmentSources"
  | "settings.cancelEnvironmentInstall"
  | "settings.invalidateEnvironmentPlan"
  | "settings.switchEnvironment"
  | "settings.deleteEnvironment"
  | "settings.setEnvironmentCleanupPage"
  | "settings.previewEnvironmentCleanup"
  | "settings.runEnvironmentCleanup"
  | "settings.cancelEnvironmentCleanup"
  | "settings.repairEmptyEnvironment"
  | "settings.findCompatibleEnvironment"
  | "settings.prepareEnvironment"
  | "settings.setStartup"
  | "settings.beginHotkeyRecording"
  | "settings.endHotkeyRecording"
  | "settings.setActionHotkey"
  | "settings.resetActionHotkey"
  | "settings.setFloatingToolbarEnabled"
  | "settings.setFloatingToolbarLayout"
  | "settings.setFloatingToolbarPreferences"
  | "settings.showFloatingToolbar"
  | "settings.hideFloatingToolbar"
  | "settings.setSource"
  | "settings.setAccelerator"
  | "settings.setFeature"
  | "settings.setMineruConnection"
  | "settings.setMineruRecognition"
  | "settings.setDefaultRecognitionMode"
  | "settings.prepareMineruConnection"
  | "settings.prepareRemoteHost"
  | "settings.installRuntime"
  | "settings.confirmRuntimeInstall"
  | "settings.cancelRuntimeMaintenance"
  | "settings.retryRuntimeMaintenance"
  | "recognition.setTaskEngine"
  | "recognition.setTaskModelPreset"
  | "recognition.setOptions"
  | "pdf.setTaskEngine"
  | "pdf.setTaskModelPreset"
  | "update.check"
  | "update.download"
  | "update.cancel"
  | "update.cancelRuntimeMaintenance"
  | "diagnostics.export"
  | "diagnostics.copy";

export interface AppAction {
  readonly type: AppActionType;
  readonly [argument: string]: unknown;
}

export type AppCommandReceipt = "succeeded" | "rejected" | "unconfirmed";

export interface AppActions {
  readonly reportCapturePixels?: (
    scene: CaptureScene,
    failure?: CapturePixelFailure,
  ) => void;
  readonly recoverFeatureAssets?: () => Promise<boolean>;
  readonly recover?: () => void;
  readonly registerPdfPreviewFlush?: (
    documentId: string,
    flush: () => Promise<void>,
  ) => () => void;
  readonly commitProjectOpen?: (
    descriptor: ProjectOpenWire,
    install: EditorProjectInstall,
  ) => boolean;
  readonly consumeProjectOpen?: (
    token: string,
    sessionId: string,
    revision: number,
    install: () => void,
  ) => boolean;
  readonly run: (action: AppAction) => Promise<boolean>;
  readonly runWithReceipt?: (action: AppAction) => Promise<AppCommandReceipt>;
  readonly navigate: (route: AppRoute) => void;
  readonly setTheme: (theme: ThemePreference) => void;
}
