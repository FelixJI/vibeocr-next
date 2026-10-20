import { useEffect, useEffectEvent, useState } from "react";
import { Input, Select } from "@fluentui/react-components";
import {
  DEFAULT_COMPOSITION,
  type CanvasComposition,
} from "./canvasComposition";

// 只恢复确实被背景切换移出的项；旧工程缺记录时不推断成员资格。
function restitchForBackground(
  settings: CanvasComposition,
  next: string | undefined,
): Partial<CanvasComposition> {
  const previous =
    settings.background === "image" ? settings.backgroundAssetId : undefined;
  if (previous === next) return {};
  const ids = [...settings.assetIds];
  if (
    previous &&
    settings.backgroundStitchIndex !== undefined &&
    !ids.includes(previous)
  )
    ids.splice(
      Math.min(settings.backgroundStitchIndex, ids.length),
      0,
      previous,
    );
  const index = next === undefined ? -1 : ids.indexOf(next);
  if (index >= 0) ids.splice(index, 1);
  return {
    assetIds: ids,
    backgroundStitchIndex: index >= 0 ? index : undefined,
  };
}

export function CanvasCompositionPanel({
  value,
  assets,
  onChange,
  render,
  revision,
}: {
  readonly value?: CanvasComposition;
  readonly assets: readonly { id: string; label: string }[];
  readonly onChange: (value: CanvasComposition | undefined) => void;
  readonly render: () => Promise<Blob>;
  readonly revision: number;
}) {
  const settings = value ?? DEFAULT_COMPOSITION;
  const [preview, setPreview] = useState<{
    url?: string;
    error?: string;
    width?: number;
    height?: number;
  }>({});
  const renderCurrent = useEffectEvent(render);
  useEffect(() => {
    let cancelled = false,
      url: string | undefined;
    void renderCurrent()
      .then((blob) => {
        if (cancelled) return;
        url = URL.createObjectURL(blob);
        setPreview({ url });
      })
      .catch((error: unknown) => {
        if (!cancelled)
          setPreview({
            error: error instanceof Error ? error.message : "排版预览失败",
          });
      });
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [value, revision]);
  const update = (patch: Partial<CanvasComposition>) =>
    onChange({ ...settings, ...patch });
  return (
    <section aria-label="画布排版属性" className="canvas-composition-panel">
      <p>
        非破坏排版：原图与导入图片保留，可撤销；以下预览与最终输出使用同一渲染。
      </p>
      <label>
        边距（px）
        <Input
          aria-label="排版边距"
          type="number"
          min={0}
          max={1000}
          value={String(settings.margin)}
          onChange={(_, data) => update({ margin: Number(data.value) })}
        />
      </label>
      <label>
        背景
        <Select
          aria-label="排版背景"
          value={settings.background}
          onChange={(_, data) => {
            const background = data.value as CanvasComposition["background"];
            const binding =
              background === "image"
                ? (settings.backgroundAssetId ?? assets[0]?.id)
                : undefined;
            update({
              background,
              // 仅 image 背景持有绑定；离开时解除，避免旧资产被拼接排除或删除时误重置背景。
              backgroundAssetId: binding,
              ...restitchForBackground(settings, binding),
            });
          }}
        >
          <option value="transparent">透明</option>
          <option value="solid">纯色</option>
          <option value="gradient">渐变</option>
          <option value="image" disabled={assets.length === 0}>
            图像（先粘贴/拖入授权图片）
          </option>
        </Select>
      </label>
      <label>
        背景起色
        <input
          aria-label="背景起色"
          type="color"
          value={settings.color}
          onChange={(event) => update({ color: event.target.value })}
        />
      </label>
      <label>
        背景终色
        <input
          aria-label="背景终色"
          type="color"
          value={settings.endColor}
          onChange={(event) => update({ endColor: event.target.value })}
        />
      </label>
      {settings.background === "image" && (
        <label>
          背景图
          <Select
            aria-label="排版背景图"
            value={settings.backgroundAssetId ?? ""}
            onChange={(_, data) =>
              update({
                backgroundAssetId: data.value,
                ...restitchForBackground(settings, data.value),
              })
            }
          >
            {assets.map((asset) => (
              <option key={asset.id} value={asset.id}>
                {asset.label}
              </option>
            ))}
          </Select>
        </label>
      )}
      <label>
        阴影（px）
        <Input
          aria-label="排版阴影"
          type="number"
          min={0}
          max={1000}
          value={String(settings.shadow)}
          onChange={(_, data) => update({ shadow: Number(data.value) })}
        />
      </label>
      <label>
        画布圆角（px）
        <Input
          aria-label="画布圆角"
          type="number"
          min={0}
          max={1000}
          value={String(settings.radius)}
          onChange={(_, data) => update({ radius: Number(data.value) })}
        />
      </label>
      <label>
        固定比例
        <Select
          aria-label="排版固定比例"
          value={settings.ratio}
          onChange={(_, data) =>
            update({ ratio: data.value as CanvasComposition["ratio"] })
          }
        >
          {["free", "1:1", "4:3", "16:9", "9:16"].map((ratio) => (
            <option key={ratio} value={ratio}>
              {ratio === "free" ? "自由" : ratio}
            </option>
          ))}
        </Select>
      </label>
      <label>
        多图拼接
        <Select
          aria-label="多图拼接"
          value={settings.stitch}
          onChange={(_, data) =>
            update({
              stitch: data.value as CanvasComposition["stitch"],
              backgroundStitchIndex:
                settings.background === "image" && settings.backgroundAssetId
                  ? Math.max(
                      0,
                      assets.findIndex(
                        (asset) => asset.id === settings.backgroundAssetId,
                      ),
                    )
                  : undefined,
              // 仅当背景仍为 image 时排除背景图；陈旧绑定不阻碍旧资产拼接。
              assetIds: assets
                .filter(
                  (asset) =>
                    !(
                      settings.background === "image" &&
                      asset.id === settings.backgroundAssetId
                    ),
                )
                .map((asset) => asset.id),
            })
          }
        >
          <option value="none">关闭</option>
          <option value="horizontal">横向</option>
          <option value="vertical">纵向</option>
        </Select>
      </label>
      <p>
        拼接顺序为原图、导入图片对象的排列顺序；可在对象列表重排后重新选择拼接。
      </p>
      <label>
        中段裁除
        <Select
          aria-label="中段裁除方向"
          value={settings.cut?.axis ?? "none"}
          onChange={(_, data) =>
            update({
              cut:
                data.value === "none"
                  ? undefined
                  : {
                      axis: data.value as "horizontal" | "vertical",
                      start: 40,
                      end: 60,
                    },
            })
          }
        >
          <option value="none">关闭</option>
          <option value="horizontal">横向条带（上下接合）</option>
          <option value="vertical">纵向条带（左右接合）</option>
        </Select>
      </label>
      {settings.cut && (
        <>
          <label>
            起点（%）
            <Input
              aria-label="裁除起点"
              type="number"
              min={0}
              max={99}
              value={String(settings.cut.start)}
              onChange={(_, data) =>
                update({ cut: { ...settings.cut!, start: Number(data.value) } })
              }
            />
          </label>
          <label>
            终点（%）
            <Input
              aria-label="裁除终点"
              type="number"
              min={1}
              max={100}
              value={String(settings.cut.end)}
              onChange={(_, data) =>
                update({ cut: { ...settings.cut!, end: Number(data.value) } })
              }
            />
          </label>
        </>
      )}
      {preview.width && preview.height && (
        <p>
          排版最终尺寸：{preview.width}×{preview.height}
        </p>
      )}
      {preview.error ? (
        <p role="alert">{preview.error}</p>
      ) : preview.url ? (
        <img
          onLoad={(event) => {
            const width = event.currentTarget.naturalWidth,
              height = event.currentTarget.naturalHeight;
            setPreview((current) => ({ ...current, width, height }));
          }}
          alt="排版输出预览"
          src={preview.url}
          style={{ maxWidth: "100%", maxHeight: 360, objectFit: "contain" }}
        />
      ) : (
        <p>正在渲染预览……</p>
      )}
    </section>
  );
}
