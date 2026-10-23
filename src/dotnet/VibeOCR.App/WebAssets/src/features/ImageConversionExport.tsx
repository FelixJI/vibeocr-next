import { useEffect, useRef, useState } from "react";
import { Button } from "@fluentui/react-components";
import type { AppActions } from "../app/types";
import { EditorPreferencesPanel } from "../components/EditorPreferencesPanel";
import { editorPreferences } from "../components/editorPreferences";
import { encodeImagePreset } from "../components/imagePresetEncoding";
import {
  discardAnnotatedImage,
  uploadAnnotatedImage,
} from "../components/annotationHandoff";
import { resource } from "./FeatureValues";

type ImageReady =
  { url: string; id: string } | { unavailable: true } | undefined;
export function ImageConversionExport({
  state,
  settings,
  count,
  actions,
  busy,
  onBusy,
}: {
  readonly state: Readonly<Record<string, unknown>>;
  readonly settings: Readonly<Record<string, unknown>>;
  readonly count: number;
  readonly actions: AppActions;
  readonly busy: boolean;
  readonly onBusy: (value: boolean) => void;
}) {
  const preferences = editorPreferences(settings.editorPreferences);
  const waiting = useRef<((image: ImageReady) => void) | undefined>(undefined);
  const cancelled = useRef(false);
  const mounted = useRef(true);
  // 会话令牌：新一轮开始时旧循环（含停止后仍在途的上传/命令）立即失效，
  // 避免共享 cancel 标志被新运行重置后旧请求复活。
  const runSession = useRef(0);
  const [executing, setExecuting] = useState(false);
  const [progress, setProgress] = useState("");
  // 请求生命周期：每个 prepareImageExport 回执都重发一份新转换快照
  // （即使 exportImageId/error 值与上轮相同）；以快照到达驱动消费，同项重试
  // decode_unavailable 即时分类；非 prepare 路径的 转换状态 不携带导出
  // 瞬态字段，不会误消费无关快照。
  useEffect(() => {
    if (!waiting.current) return;
    const resolve = waiting.current;
    const image = resource(state.exportImage);
    const imageId =
      typeof state.exportImageId === "string" ? state.exportImageId : undefined;
    if (state.exportImageError === "decode_unavailable" && imageId) {
      waiting.current = undefined;
      resolve({ unavailable: true });
    } else if (image && imageId) {
      waiting.current = undefined;
      resolve({ url: image.url, id: imageId });
    }
  }, [state]);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      cancelled.current = true;
      waiting.current?.(undefined);
      waiting.current = undefined;
    };
  }, []);
  async function start() {
    if (busy || count === 0) return;
    const session = ++runSession.current;
    cancelled.current = false;
    onBusy(true);
    setExecuting(true);
    const stopped = () =>
      cancelled.current || runSession.current !== session || !mounted.current;
    let succeeded = 0,
      failed = 0,
      unavailable = 0;
    let cleanupError = "";
    try {
      for (let index = 0; index < count && !stopped(); index++) {
        setProgress(
          `正在输出图片 ${index + 1}/${count}；已成功 ${succeeded}，失败/非图片 ${failed}`,
        );
        let timer: ReturnType<typeof setTimeout> | undefined;
        try {
          const ready = new Promise<ImageReady>((resolve) => {
            waiting.current = resolve;
            timer = setTimeout(() => {
              waiting.current = undefined;
              resolve(undefined);
            }, 15_000);
          });
          const prepared = await actions.run({
            type: "imageConversion.prepareImageExport",
            index,
          });
          if (!prepared) {
            waiting.current?.(undefined);
            waiting.current = undefined;
          }
          const next = await ready;
          if (!next) throw new Error("队列图片未准备完成");
          if ("unavailable" in next) {
            unavailable++;
            throw new Error("格式解码不可用或图片内容损坏");
          }
          if (stopped()) break;
          const blob = await encodeImagePreset(next.url, preferences.output);
          if (stopped()) break;
          const resourceUri = await uploadAnnotatedImage(blob);
          // 停止/卸载/会话更替后不再写入：上传返回后才检查，不发 imageConversion.exportImage。
          if (stopped()) {
            try {
              await discardAnnotatedImage(resourceUri);
            } catch (error) {
              cleanupError =
                error instanceof Error
                  ? error.message
                  : "取消上传后的图片租约释放失败";
              throw error;
            }
            break;
          }
          if (
            await actions.run({
              type: "imageConversion.exportImage",
              itemId: next.id,
              resourceUri,
              presetId: preferences.output.id,
              presetBinding: {
                preset: preferences.output,
                directory:
                  typeof settings.imageOutputDirectory === "string"
                    ? settings.imageOutputDirectory
                    : "",
              },
            })
          )
            succeeded++;
          else failed++;
        } catch {
          failed++;
        } finally {
          if (timer) clearTimeout(timer);
          waiting.current = undefined;
        }
      }
      if (mounted.current)
        setProgress(
          `${stopped() ? "已停止" : "输出完成"}：成功 ${succeeded}，失败/非图片 ${failed}。${unavailable ? `其中 ${unavailable} 项格式解码不可用或图片内容损坏。` : ""}${cleanupError}已成功文件保留；原图与已有文件未覆盖。`,
        );
    } finally {
      if (mounted.current) {
        onBusy(false);
        setExecuting(false);
      }
    }
  }
  return (
    <section aria-label="批量图片预设输出">
      <Button
        disabled={busy || count === 0 || !settings.imageOutputDirectory}
        onClick={() => void start()}
      >
        开始转换
      </Button>
      {executing && (
        <Button
          onClick={() => {
            cancelled.current = true;
          }}
        >
          停止图片导出
        </Button>
      )}
      <p>
        逐张转换格式、质量与尺寸；沿用输出目录/文件名模板，不套用编辑器排版。JPEG
        透明区域合成白底。输出为静态图片，动画/多帧仅取首帧，多页图片仅取第一页；原文件保持不变。缺少格式解码器时明确报错。
      </p>
      {progress && <p role="status">{progress}</p>}
      <fieldset className="editor-preferences-lock" disabled={executing}>
        <EditorPreferencesPanel
          outputOnly
          key={JSON.stringify(preferences)}
          value={preferences}
          actions={actions}
          directory={
            typeof settings.imageOutputDirectory === "string"
              ? settings.imageOutputDirectory
              : undefined
          }
          error={
            typeof settings.editorPreferencesError === "string"
              ? settings.editorPreferencesError
              : undefined
          }
        />
      </fieldset>
    </section>
  );
}
