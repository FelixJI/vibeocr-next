import { Button } from "@fluentui/react-components";

import { ClipboardPaste, ImagePlus } from "lucide-react";

import { useEffect, useState } from "react";

import { shouldRunDocumentCommand } from "../app/keyboard";

import { ImageCanvasEditor } from "../components/ImageCanvasEditor";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { resource } from "./FeatureValues";

import { screenshotSession, screenshotTextLayer } from "./ScreenshotSession";

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
      void actions.run({ type: "imageEdit.selectImage" });
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [actions, viewState.connected, busy, scene]);
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
                disabled={!viewState.connected || busy}
                onClick={() => actions.run({ type: "imageEdit.selectImage" })}
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
        </>
      }
    >
      <StatusLine>
        {busy
          ? "正在读取图片…"
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
