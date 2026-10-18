import { useEffect, useRef, useState } from "react";
import { Button } from "@fluentui/react-components";
import type { AppActions } from "../app/types";
import { EditorPreferencesPanel } from "../components/EditorPreferencesPanel";
import { editorPreferences } from "../components/editorPreferences";
import { encodeImagePreset } from "../components/imagePresetEncoding";
import { uploadAnnotatedImage } from "../components/annotationHandoff";
import { resource } from "./FeatureValues";

type ImageReady = { url: string; id: string } | undefined;
export function BatchImageExport({
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
  const [executing, setExecuting] = useState(false);
  const [progress, setProgress] = useState("");
  const image = resource(state.exportImage);
  const imageId =
    typeof state.exportImageId === "string" ? state.exportImageId : undefined;
  useEffect(() => {
    if (image && imageId && waiting.current) {
      const resolve = waiting.current;
      waiting.current = undefined;
      resolve({ url: image.url, id: imageId });
    }
  }, [image, imageId]);
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
    cancelled.current = false;
    onBusy(true);
    setExecuting(true);
    let succeeded = 0,
      failed = 0;
    try {
      for (let index = 0; index < count && !cancelled.current; index++) {
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
            type: "batch.prepareImageExport",
            index,
          });
          if (!prepared) {
            waiting.current?.(undefined);
            waiting.current = undefined;
          }
          const next = await ready;
          if (!next) throw new Error("队列图片未准备完成");
          if (cancelled.current) break;
          const blob = await encodeImagePreset(next.url, preferences.output);
          if (cancelled.current) break;
          const resourceUri = await uploadAnnotatedImage(blob);
          if (
            await actions.run({
              type: "batch.exportImage",
              itemId: next.id,
              resourceUri,
              presetId: preferences.output.id,
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
          `${cancelled.current ? "已停止" : "输出完成"}：成功 ${succeeded}，失败/非图片 ${failed}。已成功文件保留；原图与已有文件未覆盖。`,
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
        按常用预设批量导出图片
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
        透明区域合成白底；文档项保留并报告为非图片。
      </p>
      {progress && <p role="status">{progress}</p>}
      <fieldset className="editor-preferences-lock" disabled={executing}>
        <EditorPreferencesPanel
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
