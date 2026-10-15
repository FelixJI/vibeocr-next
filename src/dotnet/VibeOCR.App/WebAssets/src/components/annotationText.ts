import type { AnnotationStyle, Mark, Point, Rect } from "./annotationGeometry";

export const FONT_TOOLS = ["text", "callout", "numbering"] as const;
export function isFontMark(mark: Mark): boolean {
  return FONT_TOOLS.some((tool) => tool === mark.tool);
}
export function isContentMark(mark: Mark): boolean {
  return mark.tool === "text" || mark.tool === "callout";
}

export function numberLabel(
  value: number,
  format: AnnotationStyle["numberFormat"],
): string {
  if (!format || format === "number") return String(value);
  let label = "";
  for (
    let n = Math.max(1, Math.trunc(value));
    n > 0;
    n = Math.floor((n - 1) / 26)
  )
    label = String.fromCharCode(65 + ((n - 1) % 26)) + label;
  return format === "lower" ? label.toLowerCase() : label;
}

/** 起点是下限，已有有效序号决定后续编号，删除中间对象不会复用旧号。 */
export function nextNumberOrdinal(
  marks: readonly Mark[],
  start: number,
): number {
  return marks.reduce(
    (next, mark) =>
      mark.tool === "numbering" &&
      Number.isSafeInteger(mark.ordinal) &&
      (mark.ordinal ?? 0) > 0
        ? Math.max(next, mark.ordinal! + 1)
        : next,
    start,
  );
}

function numberTextColor(background: string): string {
  if (!/^#[0-9a-f]{6}$/i.test(background)) return "#161616";
  const linear = (value: number) => {
    const channel = value / 255;
    return channel <= 0.04045
      ? channel / 12.92
      : ((channel + 0.055) / 1.055) ** 2.4;
  };
  const rgb = Number.parseInt(background.slice(1), 16);
  const luminance =
    0.2126 * linear((rgb >> 16) & 255) +
    0.7152 * linear((rgb >> 8) & 255) +
    0.0722 * linear(rgb & 255);
  const darkContrast = (luminance + 0.05) / (linear(22) + 0.05);
  const lightContrast = 1.05 / (luminance + 0.05);
  return lightContrast > darkContrast ? "#ffffff" : "#161616";
}

/** 中心朝目标的射线与框边相交；目标在框内/边上时不画引线，避免穿字。 */
export function calloutLeaderSegment(
  box: Rect,
  target: Point,
  scale = 1,
): { from: Point; to: Point; headSize: number } | undefined {
  const right = box.x + box.width,
    bottom = box.y + box.height;
  if (
    target.x >= box.x &&
    target.x <= right &&
    target.y >= box.y &&
    target.y <= bottom
  )
    return undefined;
  const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  const dx = target.x - center.x,
    dy = target.y - center.y;
  const fraction = Math.min(
    dx === 0 ? Infinity : box.width / 2 / Math.abs(dx),
    dy === 0 ? Infinity : box.height / 2 / Math.abs(dy),
  );
  const distance = Math.hypot(
    target.x - Math.max(box.x, Math.min(right, target.x)),
    target.y - Math.max(box.y, Math.min(bottom, target.y)),
  );
  return {
    from: { x: center.x + dx * fraction, y: center.y + dy * fraction },
    to: target,
    headSize: Math.min(16 * scale, distance / 2),
  };
}

export function annotationFont(
  style: AnnotationStyle | undefined,
  scale = 1,
): string {
  const family = style?.fontFamily || "system-ui";
  const weight =
    style?.fontBold === undefined ? 600 : style.fontBold ? 700 : 400;
  return (
    (style?.fontItalic ? "italic " : "") +
    weight +
    " " +
    (style?.fontSize ?? 24) * scale +
    "px " +
    (["system-ui", "sans-serif", "serif", "monospace"].includes(family)
      ? family
      : JSON.stringify(family)) +
    ", sans-serif"
  );
}

/** 比较两种后备字体的实际字宽；FontFaceSet.check 对不存在的本地字体也可能返回 true。 */
export function resolveAnnotationFont(
  context: CanvasRenderingContext2D | null,
  requested: string,
): { family: string; available: boolean } {
  const family = requested.trim();
  if (["system-ui", "sans-serif", "serif", "monospace"].includes(family))
    return { family, available: true };
  if (!context || !/^[\p{L}\p{N} _.-]{1,80}$/u.test(family))
    return { family: "system-ui", available: false };
  context.save();
  const sample = "mmmmmmmmllli汉字123";
  let available = false;
  for (const fallback of ["monospace", "serif"]) {
    context.font = "24px " + fallback;
    const base = context.measureText(sample).width;
    context.font = "24px " + JSON.stringify(family) + ", " + fallback;
    if (Math.abs(context.measureText(sample).width - base) > 0.01)
      available = true;
  }
  context.restore();
  return { family: available ? family : "system-ui", available };
}

function textLayout(
  context: CanvasRenderingContext2D,
  mark: Mark,
  scale: number,
) {
  const size = (mark.style?.fontSize ?? 24) * scale;
  context.font = annotationFont(mark.style, scale);
  const lines = (mark.text ?? "文本").split("\n");
  const measured = lines.map((line) => context.measureText(line).width);
  const x =
    mark.tool === "callout" ? mark.end.x : Math.min(mark.start.x, mark.end.x);
  const baseline = mark.end.y;
  const width = Math.max(
    size / 2,
    ...measured,
    mark.tool === "callout" ? 0 : Math.abs(mark.end.x - mark.start.x),
  );
  return {
    x,
    baseline,
    width,
    size,
    lines,
    measured,
    lineHeight: size * 1.25,
    top: baseline - size,
    height: size * 1.25 * lines.length,
  };
}

export function textAnnotationBounds(
  context: CanvasRenderingContext2D,
  mark: Mark,
  scale = 1,
): Rect {
  context.save();
  if (mark.tool === "numbering") {
    context.font = annotationFont({ ...mark.style, fontBold: true }, scale);
    const size = (mark.style?.fontSize ?? 24) * scale;
    const radius = Math.max(
      size * 0.68,
      8 * scale,
      context.measureText(
        numberLabel(mark.ordinal ?? 1, mark.style?.numberFormat),
      ).width /
        2 +
        4 * scale,
    );
    context.restore();
    return {
      x: mark.start.x - radius,
      y: mark.start.y - radius,
      width: radius * 2,
      height: radius * 2,
    };
  }
  const layout = textLayout(context, mark, scale);
  const padding =
    mark.tool === "callout" ||
    (mark.style?.textBackground?.enabled !== false &&
      mark.style?.textBackground)
      ? 6 * scale
      : 0;
  context.restore();
  return {
    x: layout.x - padding,
    y: layout.top - padding,
    width: layout.width + padding * 2,
    height: layout.height + padding * 2,
  };
}

export function drawTextAnnotation(
  context: CanvasRenderingContext2D,
  mark: Mark,
  scale = 1,
): void {
  context.save();
  context.setLineDash([]);
  const color = mark.style?.color ?? "#f38b35";
  if (mark.tool === "numbering") {
    const box = textAnnotationBounds(context, mark, scale);
    const radius = box.width / 2;
    const shape = mark.style?.numberShape ?? "circle";
    context.fillStyle = color;
    context.beginPath();
    if (shape === "circle")
      context.arc(mark.start.x, mark.start.y, radius, 0, Math.PI * 2);
    else if (shape === "square")
      context.rect(box.x, box.y, box.width, box.height);
    else if (shape === "diamond") {
      context.moveTo(mark.start.x, box.y);
      context.lineTo(box.x + box.width, mark.start.y);
      context.lineTo(mark.start.x, box.y + box.height);
      context.lineTo(box.x, mark.start.y);
      context.closePath();
    }
    if (shape !== "none") context.fill();
    context.font = annotationFont({ ...mark.style, fontBold: true }, scale);
    context.fillStyle = shape === "none" ? color : numberTextColor(color);
    context.textAlign = "center";
    context.textBaseline = "middle";
    context.fillText(
      numberLabel(mark.ordinal ?? 1, mark.style?.numberFormat),
      mark.start.x,
      mark.start.y,
    );
    context.restore();
    return;
  }
  const layout = textLayout(context, mark, scale);
  const box = textAnnotationBounds(context, mark, scale);
  const leader =
    mark.tool === "callout"
      ? calloutLeaderSegment(box, mark.start, scale)
      : undefined;
  if (leader) {
    const joint = leader.from;
    context.strokeStyle = color;
    context.lineWidth = (mark.style?.strokeWidth ?? 3) * scale;
    context.beginPath();
    context.moveTo(joint.x, joint.y);
    context.lineTo(mark.start.x, mark.start.y);
    if (mark.style?.calloutStyle === "arrow") {
      const angle = Math.atan2(mark.start.y - joint.y, mark.start.x - joint.x);
      const head = leader.headSize;
      for (const side of [-0.45, 0.45]) {
        context.moveTo(mark.start.x, mark.start.y);
        context.lineTo(
          mark.start.x - head * Math.cos(angle + side),
          mark.start.y - head * Math.sin(angle + side),
        );
      }
    }
    context.stroke();
  }
  const background = mark.style?.textBackground;
  if ((background && background.enabled !== false) || mark.tool === "callout") {
    context.save();
    context.globalAlpha =
      background?.enabled === false ? 0 : (background?.opacity ?? 100) / 100;
    context.fillStyle = background?.color ?? "#ffffff";
    context.fillRect(box.x, box.y, box.width, box.height);
    context.restore();
  }
  if (mark.tool === "callout" && mark.style?.calloutStyle !== "arrow") {
    context.strokeStyle = color;
    context.lineWidth = (mark.style?.strokeWidth ?? 3) * scale;
    context.strokeRect(box.x, box.y, box.width, box.height);
  }
  context.font = annotationFont(mark.style, scale);
  context.fillStyle = color;
  context.textAlign = "left";
  context.textBaseline = "alphabetic";
  layout.lines.forEach((line, index) => {
    const offset =
      mark.style?.textAlign === "center"
        ? (layout.width - layout.measured[index]!) / 2
        : mark.style?.textAlign === "right"
          ? layout.width - layout.measured[index]!
          : 0;
    const x = layout.x + offset,
      y = layout.baseline + index * layout.lineHeight;
    if (mark.style?.textOutline && mark.style.textOutline.width > 0) {
      context.strokeStyle = mark.style.textOutline.color;
      context.lineWidth = mark.style.textOutline.width * scale;
      context.strokeText(line, x, y);
    }
    context.fillText(line, x, y);
    if (mark.style?.fontUnderline) {
      context.strokeStyle = color;
      context.lineWidth = Math.max(1, layout.size / 16);
      context.beginPath();
      context.moveTo(x, y + layout.size * 0.12);
      context.lineTo(x + layout.measured[index]!, y + layout.size * 0.12);
      context.stroke();
    }
  });
  context.restore();
}
