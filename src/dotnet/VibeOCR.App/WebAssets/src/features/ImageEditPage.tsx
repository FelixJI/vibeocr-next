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
  const [autoTextPreference, setAutoTextPreference] = useState(true);
  const [projectMessage, setProjectMessage] = useState<string>();
  const [projectInstall, setProjectInstall] = useState<EditorProjectInstall>();
  const projectOpenGenerationRef = useRef(0);
  const consumedProjectPendingRef = useRef<ReadonlySet<string>>(new Set());
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
  // #233 打开工程：收到 projectOpen 投影 → 全部资源 fetch/decode 与全部
  // 可失败校验（预算/逻辑 frame/资产补丁一致）→ ack 旧绑定 → 确认宿主已
  // 切换（回执 ok）→ 交付只含已 decode 强引用的安装包。任何失败/取消/
  // 代际变化拒收并保持旧画布；绝不重 fetch 已释放 URL。30 秒到期由宿主
  // 事务权威取消（迟到 ack 得冲突回执）。仅普通编辑页（scene 无）。
  // descriptor 以原始 state.projectOpen 键控 memo：同 pending 不因渲染
  // 重建而打断在途准备；卸载/换 pending 时 AbortSignal 使迟到结果失效。
  const projectOpen = useMemo(
    () => (scene ? undefined : parseProjectOpen(state.projectOpen)),
    [scene, state.projectOpen],
  );
  const projectOpenRef = useRef(projectOpen);
  const currentSessionIdRef = useRef<string | undefined>(session?.sessionId);
  useLayoutEffect(() => {
    projectOpenRef.current = projectOpen;
    currentSessionIdRef.current = session?.sessionId;
  });
  const pendingId = projectOpen?.pendingId;
  useEffect(() => {
    if (!pendingId) return undefined;
    const descriptor = projectOpenRef.current;
    if (!descriptor || !viewState.connected) return undefined;
    const controller = new AbortController();
    const generation = ++projectOpenGenerationRef.current;
    const reject = () =>
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
        validateProjectRestore(sourceImage, manifest.canvas, manifest.state);
        if (
          (currentSessionIdRef.current ?? "0".repeat(32)) !==
          descriptor.sessionId
        ) {
          throw new Error("会话已变化，本次打开未确认");
        }
        setProjectMessage("资源就绪，正在确认切换……");
        const acked = await actions.run({
          type: "imageEdit.ackProjectOpen",
          pendingId: descriptor.pendingId,
          sessionId: descriptor.sessionId,
          revision: descriptor.revision,
          generation: descriptor.generation,
        });
        if (generation !== projectOpenGenerationRef.current) return;
        if (!acked) {
          setProjectMessage(
            "工程打开未完成：会话已变化或确认失败；当前画布保持不变。",
          );
          return;
        }
        // 宿主已切换：新会话/输入即将到位，安装包只携带已 decode 强引用
        // （含 source 位图），安装不再触发任何网络读取。
        setProjectInstall({
          token: descriptor.pendingId,
          sourceUrl: descriptor.source.url,
          sourceImage,
          savedCanvas: manifest.canvas,
          state: manifest.state,
          assets,
          patches,
        });
        setProjectMessage("工程已确认，正在恢复编辑内容……");
      } catch (error) {
        if (generation !== projectOpenGenerationRef.current) return;
        if (controller.signal.aborted) return;
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
      projectOpenGenerationRef.current += 1;
    };
  }, [pendingId, viewState.connected, actions]);

  const pendingProjectInstall =
    projectInstall && input?.url === projectInstall.sourceUrl
      ? projectInstall
      : undefined;
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
        {projectMessage ??
          importErrorLabel(state.importError) ??
          (busy
            ? "正在读取图片…"
            : state.statusCode === "recognition.inputFailed"
              ? "无法读取图片；当前编辑已保留，请检查格式、尺寸和访问权限。"
              : input
                ? "原图保留；默认另存副本。"
                : "选择、粘贴或拖入图片。")}
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
          recognitionModes={imageRecognitionModes(state.recognitionModes)}
          source={input.url}
          captureScene={captureScene}
          sourceByteLength={input.byteLength}
          session={session}
          textLayer={screenshotTextLayer(state.textLayer)}
          systemImport={imageEditImport(state.import)}
          projectInstall={pendingProjectInstall}
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
