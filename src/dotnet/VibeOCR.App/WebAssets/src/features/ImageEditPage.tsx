import { Button } from "@fluentui/react-components";

import { ClipboardPaste, ImagePlus } from "lucide-react";

import { useCallback, useEffect, useRef, useState } from "react";

import { shouldRunDocumentCommand } from "../app/keyboard";

import { ImageCanvasEditor } from "../components/ImageCanvasEditor";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { resource } from "./FeatureValues";

import {
  screenshotSession,
  screenshotTextLayer,
  imageEditImport,
} from "./ScreenshotSession";

import { StatusLine } from "./FeaturePanels";

export function ImageEditPage({ viewState, actions }: FeatureProps) {
  const [autoTextPreference, setAutoTextPreference] = useState(true);
  const captureScene = viewState.captureScene ?? undefined;
  const scene =
    captureScene !== undefined || window.location.hash.includes("?scene=1");
  useEffect(() => {
    if (!captureScene) return;
    document.documentElement.dataset.captureScene = "true";
    return () => {
      delete document.documentElement.dataset.captureScene;
    };
  }, [captureScene]);
  // 宿主命令约定：scene（#/imageEdit?scene=1）仍由 recognition feature 承载
  // 并发 recognition.*；非 scene 独立编辑页读 imageEdit feature，编辑器
  // 会话命令全部改发 imageEdit.*（与 recognition 同形命令）。
  const state = feature(viewState, scene ? "recognition" : "imageEdit");
  const input = resource(state.input);
  const session = screenshotSession(state.screenshotSession);
  const busy = booleanValue(state.isBusy);
  // 打开入口共享同一业务命令；busy=true 会禁用按钮并失焦，busy 周期
  // 回落时恢复到可见的“选择图片”按钮（scene 无此按钮不参与）。
  const chooseButtonRef = useRef<HTMLButtonElement>(null);
  const openFocusRestore = useRef({ armed: false, sawBusy: false });
  const openImage = useCallback(() => {
    openFocusRestore.current = { armed: true, sawBusy: false };
    void actions.run({ type: "imageEdit.selectImage" });
  }, [actions]);
  useEffect(() => {
    if (scene) return;
    const restore = openFocusRestore.current;
    if (!restore.armed) return;
    if (busy) {
      restore.sawBusy = true;
      return;
    }
    if (!restore.sawBusy) return;
    // busy 周期才是完成依据：run 回执可能先于系统选择器关闭到达；
    // armed 立即清除，仅页面可见且实际持有焦点时恢复，不重试。
    restore.armed = false;
    const choose = chooseButtonRef.current;
    if (!document.hidden && document.hasFocus() && choose && !choose.disabled)
      choose.focus();
  }, [busy, scene]);
  // Windows 打开命令：与“选择图片”按钮同一入口与启用条件；截图现场窗
  // （scene）由 ImageCanvasEditor 承载会话命令，不接收文档级打开。
  useEffect(() => {
    if (scene) return undefined;
    const onKeyDown = (event: KeyboardEvent) => {
      if (
        !shouldRunDocumentCommand(event) ||
        event.repeat ||
        event.altKey ||
        event.shiftKey ||
        !(event.ctrlKey || event.metaKey) ||
        event.key.toLowerCase() !== "o" ||
        !viewState.connected ||
        busy
      )
        return;
      event.preventDefault();
      openImage();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [openImage, viewState.connected, busy, scene]);
  return (
    <Workspace
      eyebrow={scene ? "CAPTURE" : "IMAGE"}
      title={scene ? "截图现场编辑" : "图片编辑"}
      description="本地编辑、缩放与真实格式转换；识别仅在明确点击时执行。"
      actions={
        <>
          {!scene && (
            <>
              <Button
                ref={chooseButtonRef}
                disabled={!viewState.connected || busy}
                onClick={openImage}
                icon={<ImagePlus aria-hidden="true" size={16} />}
              >
                选择图片
              </Button>
              <Button
                disabled={!viewState.connected || busy}
                onClick={() => actions.run({ type: "imageEdit.readClipboard" })}
                icon={<ClipboardPaste aria-hidden="true" size={16} />}
              >
                粘贴图片
              </Button>
            </>
          )}
          {/* #232 显式文字动作：宿主读取系统剪贴板，图片/文本成为可编辑
              对象；空画布时图片走旧基准入口、文本提示先打开图片。不新增图标。 */}
          <Button
            disabled={!viewState.connected || busy}
            onClick={() =>
              actions.run({
                type: scene
                  ? "recognition.pasteSystemContent"
                  : "imageEdit.pasteSystemContent",
              })
            }
          >
            粘贴系统内容
          </Button>
        </>
      }
    >
      <StatusLine>
        {busy
          ? "正在读取图片…"
          : state.importError
            ? state.importError === "cancelled"
              ? "导入已取消；已有画布与历史保持不变。"
              : "导入未完成；本批未导入，已有画布与历史保持不变。"
            : state.statusCode === "recognition.inputFailed"
              ? "无法读取图片；当前编辑已保留，请检查格式、尺寸和访问权限。"
              : input
                ? "原图保留；默认另存副本。"
                : "选择、粘贴或拖入图片。"}
      </StatusLine>
      {session?.sceneEditing && !scene ? (
        <EmptyStage
          title="正在截图现场编辑"
          detail="请在截图现场窗口继续编辑当前图片。"
        />
      ) : input ? (
        <ImageCanvasEditor
          key={session ? `session-${session.sessionId}` : input.url}
          actions={actions}
          canExport={viewState.capabilities.includes("recognition.annotation")}
          canRecognize={viewState.capabilities.includes(
            "recognition.screenshotSession",
          )}
          source={input.url}
          captureScene={captureScene}
          sourceByteLength={input.byteLength}
          session={session}
          textLayer={screenshotTextLayer(state.textLayer)}
          systemImport={imageEditImport(state.import)}
          autoText={
            session?.textSelectionRequested === true && autoTextPreference
          }
          showAutoTextPreference={session?.textSelectionRequested === true}
          onAutoTextChange={setAutoTextPreference}
          commandScope={scene ? "recognition" : "imageEdit"}
          // 纯编辑不展示识别结果；显式识别提交成功后导航到识别承载面。
          // scene 窗口固定路由，由宿主完成会话交接与主窗口导航。
          onRecognitionSubmitted={
            scene ? undefined : () => actions.navigate("recognition")
          }
        />
      ) : (
        <EmptyStage title="等待图片" detail="选择、粘贴或直接拖入图片" />
      )}
    </Workspace>
  );
}
