import { useEffect, useLayoutEffect, useRef, useState } from "react";

import type { CanvasSize } from "./annotationGeometry";
import "./ImageTextLayer.css";
import {
  isLineBindingActive,
  layoutTextBox,
  type ImageTextLine,
  type ImageTextLineBinding,
} from "./imageTextLayerGeometry";

export type {
  ImageTextLine,
  ImageTextLineBinding,
} from "./imageTextLayerGeometry";

/**
 * 显示视口：OCR 行框参考系冻结为与识别同一张最终 PNG。
 * `image` 是该最终 PNG 的解码图（0°，未再旋转/裁剪）；`size` 为显示画布
 * 像素尺寸，最终图按适应缩放居中显示。显示缩放只需重投影，不触发
 * 识别；旋转/裁剪后 root 必须重新导出并识别，旧层不得映射冒充。
 */
export interface ImageTextLayerViewport {
  readonly image: HTMLImageElement;
  readonly size: CanvasSize;
}

export interface ImageTextLayerProps {
  /** OCR 结果绑定的会话+修订；与 activeSession 不一致即整体失效。 */
  readonly binding?: ImageTextLineBinding;
  /** 编辑器本地立即推进的当前会话+修订（root 直传本地值，不等宿主回显）。 */
  readonly activeSession?: ImageTextLineBinding;
  /** 已按阅读顺序整理的 OCR 行（root 用 toImageTextLines 解析宿主数据）。 */
  readonly lines: readonly ImageTextLine[];
  readonly viewport: ImageTextLayerViewport;
}

/**
 * 图片上的原生 DOM 可选文字层：每条 OCR 行渲染为真实文本节点，行框定位
 * 与画布 0° 绘制共享 annotationGeometry 变换；行内子串、跨行与正反
 * Selection、Ctrl+C/右键菜单复制全部交给原生 Range/Selection，组件不
 * 拦截 copy，也不新建文本框。行级对齐限制：OCR 只给整行框，字符水平
 * 位置是浏览器排版近似（字号取行框高）；整行按浏览器实测字形宽度压入
 * 行框，不假装拥有字符级识别坐标。
 * 失效（绑定/修订不一致，或旋转/裁剪后 root 重新导出识别）时行文本
 * 立即从 DOM 移除，并清除仍指向本层的文档 Selection；选择内容在层外
 * （如输入控件）时不被触碰。
 */
export function ImageTextLayer({
  activeSession,
  binding,
  lines,
  viewport,
}: ImageTextLayerProps) {
  // 行文本卸载后 Selection 节点可能游离出文档；保留最后一次挂载的
  // 容器引用，才能对游离树判定 contains 并清掉可复制的旧选区。
  const lastLinesRoot = useRef<HTMLDivElement | null>(null);
  const active = isLineBindingActive(binding, activeSession);

  useEffect(() => {
    if (active) return;
    // 防御：主流引擎会在移除选中节点时折叠选区（复制为空），但若
    // 引擎保留指向游离旧 DOM 的 Range，则主动清除；层外选区不碰。
    const root = lastLinesRoot.current;
    const selection = root?.ownerDocument.getSelection();
    if (!root || !selection) return;
    for (let index = 0; index < selection.rangeCount; index += 1) {
      if (root.contains(selection.getRangeAt(index).commonAncestorContainer)) {
        selection.removeAllRanges();
        return;
      }
    }
  }, [active]);

  if (!active) return null;
  const { image, size } = viewport;
  return (
    <div
      className="image-text-layer"
      ref={(node) => {
        if (node) lastLinesRoot.current = node;
      }}
    >
      {lines.map((line, index) => {
        const box = layoutTextBox(line.bbox, image, size);
        return box ? (
          <FittedLine key={index} text={line.text} box={box} />
        ) : null;
      })}
    </div>
  );
}

function FittedLine({
  text,
  box,
}: {
  readonly text: string;
  readonly box: { left: number; top: number; width: number; height: number };
}) {
  const glyphs = useRef<HTMLSpanElement>(null);
  const [scale, setScale] = useState(1);
  useLayoutEffect(() => {
    const measured = glyphs.current?.scrollWidth ?? 0;
    setScale(measured > box.width ? box.width / measured : 1);
  }, [text, box.width, box.height]);
  return (
    <div
      className="image-text-line"
      style={{
        left: `${box.left}px`,
        top: `${box.top}px`,
        width: `${box.width}px`,
        height: `${box.height}px`,
        fontSize: `${box.height}px`,
      }}
    >
      <span
        className="image-text-glyphs"
        ref={glyphs}
        style={{ transform: `scaleX(${scale})` }}
      >
        {text}
      </span>
      {"\n"}
    </div>
  );
}
