import { useState } from "react";
import { Button } from "@fluentui/react-components";
import { ArrowDown, ArrowUp, FilePlus2, Trash2 } from "lucide-react";
import { CapabilityGate } from "../components/CapabilityGate";
import { EmptyStage, Workspace } from "../components/Workspace";
import { ImageConversionExport } from "./ImageConversionExport";
import { Panel } from "./FeaturePanels";
import { feature, numberValue, type FeatureProps } from "./FeatureValues";

interface ConversionItem {
  id: string;
  name: string;
}
function conversionItems(value: unknown): ConversionItem[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (item): item is ConversionItem =>
      item !== null &&
      typeof item === "object" &&
      typeof item.id === "string" &&
      typeof item.name === "string",
  );
}

export function ImageConversionPage({ viewState, actions }: FeatureProps) {
  const state = feature(viewState, "imageConversion");
  const count = numberValue(state.itemCount);
  const windowStart = numberValue(state.windowStart);
  const items = conversionItems(state.items);
  const [exporting, setExporting] = useState(false);
  const available = viewState.capabilities.includes("imageConversion.convert");
  return (
    <Workspace
      eyebrow="IMAGE CONVERSION"
      title="图像转换"
      description="批量转换图片格式、质量和尺寸；使用独立队列，无需进行文字识别。"
      actions={
        <>
          <CapabilityGate
            capability="imageConversion.convert"
            capabilities={viewState.capabilities}
            action={{ type: "imageConversion.addFiles" }}
            actions={actions}
            disabled={exporting}
            icon={<FilePlus2 aria-hidden="true" size={16} />}
          >
            添加图片
          </CapabilityGate>
          <CapabilityGate
            capability="imageConversion.convert"
            capabilities={viewState.capabilities}
            action={{ type: "imageConversion.clear" }}
            actions={actions}
            disabled={exporting || count === 0}
            icon={<Trash2 aria-hidden="true" size={16} />}
          >
            清空队列
          </CapabilityGate>
        </>
      }
    >
      <div className="image-conversion-workspace">
        <Panel label="IMAGES" title="转换队列">
          <p>{count} 张图片</p>
          {count === 0 ? (
            <EmptyStage
              title="队列为空"
              detail="添加或拖入图片，选择输出目录与预设后开始转换。"
            />
          ) : (
            <>
              <ol className="batch-queue" aria-label="图像转换队列">
                {items.map((item, index) => (
                  <li className="batch-item" key={item.id}>
                    <span className="batch-position">
                      {windowStart + index + 1}
                    </span>
                    <span className="batch-item-copy">
                      <strong>{item.name}</strong>
                    </span>
                    <span className="batch-item-actions">
                      <Button
                        aria-label={`上移 ${item.name}`}
                        disabled={
                          exporting || !available || windowStart + index === 0
                        }
                        icon={<ArrowUp aria-hidden="true" size={16} />}
                        onClick={() =>
                          actions.run({
                            type: "imageConversion.moveItem",
                            itemId: item.id,
                            delta: -1,
                          })
                        }
                      />
                      <Button
                        aria-label={`下移 ${item.name}`}
                        disabled={
                          exporting ||
                          !available ||
                          windowStart + index + 1 >= count
                        }
                        icon={<ArrowDown aria-hidden="true" size={16} />}
                        onClick={() =>
                          actions.run({
                            type: "imageConversion.moveItem",
                            itemId: item.id,
                            delta: 1,
                          })
                        }
                      />
                      <Button
                        aria-label={`移除 ${item.name}`}
                        disabled={exporting || !available}
                        icon={<Trash2 aria-hidden="true" size={16} />}
                        onClick={() =>
                          actions.run({
                            type: "imageConversion.removeItem",
                            itemId: item.id,
                          })
                        }
                      />
                    </span>
                  </li>
                ))}
              </ol>
              <div className="button-row">
                <Button
                  disabled={exporting || !available || windowStart === 0}
                  onClick={() =>
                    actions.run({
                      type: "imageConversion.setWindow",
                      start: Math.max(0, windowStart - 50),
                    })
                  }
                >
                  上一组
                </Button>
                <Button
                  disabled={
                    exporting ||
                    !available ||
                    windowStart + items.length >= count
                  }
                  onClick={() =>
                    actions.run({
                      type: "imageConversion.setWindow",
                      start: windowStart + 50,
                    })
                  }
                >
                  下一组
                </Button>
              </div>
            </>
          )}
        </Panel>
        <Panel label="OUTPUT" title="转换与输出">
          <ImageConversionExport
            state={state}
            settings={feature(viewState, "settings")}
            count={count}
            actions={actions}
            busy={exporting || !available}
            onBusy={setExporting}
          />
        </Panel>
      </div>
    </Workspace>
  );
}
