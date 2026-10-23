import { Button } from "@fluentui/react-components";

import { ClipboardPaste, ImagePlus } from "lucide-react";

import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";

import {
  decodeProjectImage,
  decodeProjectPatch,
  fetchProjectResource,
  parseProjectManifest,
  parseProjectOpen,
} from "../components/project/projectWire";
import {
  ImageCanvasEditor,
  validateProjectRestore,
  type EditorProjectInstall,
} from "../components/ImageCanvasEditor";

import { PinHistoryPanel } from "../components/PinHistoryPanel";
import { parsePinHistory } from "../components/pinHistoryState";

import { shouldRunDocumentCommand } from "../app/keyboard";

import { EmptyStage, Workspace } from "../components/Workspace";

import type { FeatureProps } from "./FeatureValues";

import { feature, booleanValue } from "./FeatureValues";

import { resource } from "./FeatureValues";

import {
  screenshotSession,
  screenshotTextLayer,
  imageEditImport,
  importErrorLabel,
  imageRecognitionModes,
} from "./ScreenshotSession";

import { StatusLine } from "./FeaturePanels";

export function ImageEditPage({ viewState, actions }: FeatureProps) {
  const [projectMessage, setProjectMessage] = useState<string>();
  const [pinInstall, setPinInstall] = useState<EditorProjectInstall>();
  const pinInstalledRef = useRef<string | undefined>(undefined);

  const projectOpenGenerationRef = useRef(0);
  const mountedRef = useRef(false);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      projectOpenGenerationRef.current += 1;
    };
  }, []);
  const consumedProjectPendingRef = useRef<ReadonlySet<string>>(new Set());
  const captureScene = viewState.captureScene ?? undefined;
  const scene =
    captureScene !== undefined || window.location.hash.includes("?scene=1");
  // #238 贴图内编辑窗：fragment 参数 ?pin=1，固定 pinEdit 投影。
  const pin = !scene && window.location.hash.includes("?pin=1");
  useEffect(() => {
    if (!captureScene) return;
    document.documentElement.dataset.captureScene = "true";
    return () => {
      delete document.documentElement.dataset.captureScene;
    };
  }, [captureScene]);
  // 宿主命令约定：scene（#/imageEdit?scene=1）仍由 recognition feature 承载
  // 并发 recognition.*；非 scene 独立编辑页读 imageEdit feature，编辑器
  // 会话命令全部改发 imageEdit.*（与 recognition 同形命令）；pin 窗读
  // pinEdit feature 并发 pinEdit.*（仅真实消费者子集）。
  const state = feature(
    viewState,
    scene ? "recognition" : pin ? "pinEdit" : "imageEdit",
  );
  const input = resource(state.input);
  const session = screenshotSession(state.screenshotSession);
  const handoff = scene || pin ? undefined : viewState.projectOpenHandoff;
  const busy = booleanValue(state.isBusy) || handoff !== undefined;
  // #233 打开工程：收到 projectOpen 投影 → 全部资源 fetch/decode 与全部
  // 可失败校验（预算/逻辑 frame/资产补丁一致）→ ack 旧绑定 → 确认宿主已
  // 切换（权威 source/session/revision）→ 一次消费已 decode 安装包。准备失败/取消/
  // 代际变化拒收并保持旧画布；绝不重 fetch 已释放 URL。30 秒到期由宿主
  // 事务权威取消（迟到 ack 得冲突回执）。仅普通编辑页（scene 无）。
  // descriptor 按 pending 键控准备；发 ack 前所有权同步交给 runtime，
  // 此后 route 卸载不撤销已提交内容，未移交的准备仍由页面取消。
  const projectOpen = useMemo(
    () =>
      scene
        ? undefined
        : parseProjectOpen(pin ? state.projectRestore : state.projectOpen),
    [scene, pin, state.projectRestore, state.projectOpen],
  );
  const projectOpenRef = useRef(projectOpen);
  const currentSessionRef = useRef(session);
  useLayoutEffect(() => {
    projectOpenRef.current = projectOpen;
    currentSessionRef.current = session;
  });
  const pendingId = projectOpen?.pendingId;
  const handedOffId = handoff?.pendingId;
  useEffect(() => {
    if (!pendingId) return undefined;
    const descriptor = projectOpenRef.current;
    if (!descriptor || !viewState.connected || handedOffId === pendingId)
      return undefined;
    const controller = new AbortController();
    let transferred = false;
    const generation = ++projectOpenGenerationRef.current;
    const reject = () =>
      !pin &&
      void actions.run({
        type: "imageEdit.rejectProjectOpen",
        pendingId: descriptor.pendingId,
        sessionId: descriptor.sessionId,
        revision: descriptor.revision,
      });
    void (async () => {
      try {
        if (consumedProjectPendingRef.current.has(pendingId)) return;
        consumedProjectPendingRef.current = new Set([
          ...consumedProjectPendingRef.current,
          pendingId,
        ]);
        setProjectMessage("正在读取工程清单与资源……");
        const manifestResponse = await fetchProjectResource(
          descriptor.manifest.url,
          controller.signal,
        );
        const manifest = parseProjectManifest(await manifestResponse.json());
        if (generation !== projectOpenGenerationRef.current) return;
        const sourceImage = await decodeProjectImage(
          descriptor.source.url,
          controller.signal,
        );
        if (!sourceImage.naturalWidth || !sourceImage.naturalHeight) {
          throw new Error("工程原图解码失败");
        }
        const assets = new Map<
          string,
          { image: HTMLImageElement; url: string }
        >();
        for (const entry of descriptor.assets) {
          assets.set(entry.id, {
            image: await decodeProjectImage(
              entry.resource.url,
              controller.signal,
            ),
            url: entry.resource.url,
          });
        }
        const patches = new Map<number, HTMLCanvasElement>();
        for (const entry of descriptor.patches) {
          const rect = manifest.state.patches.find(
            (patch) => patch.id === entry.id,
          );
          if (!rect) throw new Error("工程清单与补丁资源不一致");
          patches.set(
            entry.id,
            await decodeProjectPatch(
              entry.resource.url,
              rect,
              controller.signal,
            ),
          );
        }
        if (generation !== projectOpenGenerationRef.current) return;
        // ack 前完成全部可失败校验：ack 后安装无失败路径。
        validateProjectRestore(
          sourceImage,
          manifest.canvas,
          manifest.state,
          assets,
        );
        if (
          (currentSessionRef.current?.sessionId ?? "0".repeat(32)) !==
            descriptor.sessionId ||
          (currentSessionRef.current?.revision ?? 0) !== descriptor.revision
        ) {
          throw new Error("会话已变化，本次打开未确认");
        }
        const install: EditorProjectInstall = {
          token: descriptor.pendingId,
          sourceUrl: descriptor.source.url,
          sourceImage,
          savedCanvas: manifest.canvas,
          state: manifest.state,
          assets,
          patches,
        };
        if (pin) {
          setPinInstall(install);
          transferred = true;
          setProjectMessage(undefined);
          return;
        }
        if (!actions.commitProjectOpen?.(descriptor, install))
          throw new Error("会话已变化，本次打开未确认");
        transferred = true;
        setProjectMessage(undefined);
      } catch (error) {
        if (generation !== projectOpenGenerationRef.current) return;
        if (controller.signal.aborted) return;
        if (pin) {
          transferred = true;
          setProjectMessage("工程恢复失败；正在回退到已保存的贴图……");
          const accepted = await actions
            .run({
              type: "pinEdit.rejectProjectRestore",
              pendingId: descriptor.pendingId,
              sessionId: descriptor.sessionId,
              revision: descriptor.revision,
            })
            .catch(() => false);
          if (
            generation !== projectOpenGenerationRef.current ||
            !mountedRef.current
          )
            return;
          setProjectMessage(
            accepted
              ? "工程恢复失败，已回退到已保存的贴图，可继续编辑；原工程仍保留。"
              : "工程恢复失败，回退未确认；请取消编辑后重试，原工程仍保留。",
          );
          return;
        }
        reject();
        setProjectMessage(
          error instanceof Error
            ? `${error.message}；当前画布保持不变。`
            : "工程打开失败；当前画布保持不变。",
        );
      }
    })();
    return () => {
      controller.abort();
      if (!transferred) {
        projectOpenGenerationRef.current += 1;
        reject();
        if (mountedRef.current)
          setProjectMessage("工程打开已取消或超时；当前画布与历史保持不变。");
      }
    };
  }, [pendingId, handedOffId, viewState.connected, actions, pin]);

  const activePinInstall =
    pinInstall?.token === projectOpen?.pendingId &&
    projectOpen?.sessionId === session?.sessionId
      ? pinInstall
      : undefined;
  const pendingProjectInstall = pin
    ? activePinInstall
    : handoff?.sessionId === session?.sessionId &&
        input?.url === handoff?.sourceUrl &&
        session?.revision === 0
      ? handoff?.install
      : undefined;
  const installProject = useCallback(
    (token: string, install: () => void) => {
      if (pin) {
        if (pinInstalledRef.current === token) return false;
        pinInstalledRef.current = token;
        install();
        return true;
      }
      return (
        session !== undefined &&
        actions.consumeProjectOpen?.(
          token,
          session.sessionId,
          session.revision,
          install,
        ) === true
      );
    },
    [actions, session, pin],
  );
  const openProject = useCallback(() => {
    setProjectMessage(undefined);
    void actions.run({ type: "imageEdit.openProject" });
  }, [actions]);

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
    if (scene || pin) return undefined;
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
  }, [openImage, viewState.connected, busy, scene, pin]);
  return (
    <Workspace
      eyebrow={scene ? "CAPTURE" : "IMAGE"}
      title={scene ? "截图现场编辑" : pin ? "贴图编辑" : "图片编辑"}
      description="本地编辑与格式转换；自动准备可选文字，识别结果由你主动生成。"
      actions={
        <>
          {!scene && !pin && (
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
              {/* #233 打开工程：宿主 picker 授权选择 .vibp，空会话也可打开。 */}
              <Button
                disabled={!viewState.connected || busy}
                onClick={openProject}
              >
                打开工程
              </Button>
            </>
          )}
          {/* #232 显式文字动作：宿主读取系统剪贴板，图片/文本成为可编辑
              对象；空画布时图片走旧基准入口、文本提示先打开图片。不新增图标。 */}
          {!pin && (
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
          )}
        </>
      }
    >
      <StatusLine>
        {pin
          ? (projectMessage ??
            "贴图内编辑：编辑后点击“完成贴图”应用；放弃更改则保持原贴图。")
          : state.statusCode === "imageEdit.projectOpenTimeout"
            ? "工程打开超时；当前画布与历史保持不变。"
            : state.statusCode === "imageEdit.projectOpenCancelled"
              ? "工程打开已取消；当前画布与历史保持不变。"
              : handoff
                ? handoff.phase === "uncertain"
                  ? "工程确认尚未完成，请检查连接后恢复；当前内容保持不变。"
                  : handoff.phase === "confirming"
                    ? "资源就绪，正在确认切换……"
                    : "工程已确认，正在恢复编辑内容……"
                : (projectMessage ??
                  importErrorLabel(state.importError) ??
                  (busy
                    ? "正在读取图片…"
                    : state.statusCode === "recognition.inputFailed"
                      ? "无法读取图片；当前编辑已保留，请检查格式、尺寸和访问权限。"
                      : input
                        ? "原图保留；默认另存副本。"
                        : "选择、粘贴或拖入图片。"))}
      </StatusLine>
      {session?.sceneEditing && !scene ? (
        <EmptyStage
          title="正在截图现场编辑"
          detail="请在截图现场窗口继续编辑当前图片。"
        />
      ) : input ? (
        <ImageCanvasEditor
          editorSettings={feature(viewState, "settings")}
          key={session ? `session-${session.sessionId}` : input.url}
          actions={actions}
          canExport={
            viewState.capabilities.includes("recognition.annotation") &&
            (!pin || !projectOpen || activePinInstall !== undefined)
          }
          recognitionBusy={busy}
          canRecognize={
            !pin &&
            viewState.capabilities.includes("recognition.screenshotSession")
          }
          recognitionModes={
            pin ? undefined : imageRecognitionModes(state.recognitionModes)
          }
          source={input.url}
          captureScene={captureScene}
          sourceByteLength={input.byteLength}
          session={session}
          textLayer={screenshotTextLayer(state.textLayer)}
          systemImport={imageEditImport(state.import)}
          projectInstall={pendingProjectInstall}
          installProject={installProject}
          interactionLocked={
            handoff !== undefined ||
            (pin && projectOpen !== undefined && activePinInstall === undefined)
          }
          commandScope={scene ? "recognition" : pin ? "pinEdit" : "imageEdit"}
          pinSnapshotApply={pin}
          // 纯编辑不展示识别结果；显式识别提交成功后导航到识别承载面。
          // scene/pin 窗口固定路由，由宿主完成会话交接与主窗口导航。
          onRecognitionSubmitted={
            scene || pin ? undefined : () => actions.navigate("recognition")
          }
        />
      ) : (
        <EmptyStage title="等待图片" detail="选择、粘贴或直接拖入图片" />
      )}
      {/* #238 截图历史管理：主窗（非 scene/pin）入口；无编辑会话也可用，
          编辑会话关闭不清列表（宿主投影语义）。 */}
      {!scene && !pin && (
        <PinHistoryPanel
          history={parsePinHistory(feature(viewState, "pinEdit").history)}
          actions={actions}
          connected={viewState.connected}
        />
      )}
    </Workspace>
  );
}
