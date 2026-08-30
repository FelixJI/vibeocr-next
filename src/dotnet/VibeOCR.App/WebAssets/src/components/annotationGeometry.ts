export type AnnotationTool =
  "rectangle" | "ellipse" | "arrow" | "text" | "mosaic" | "blur";

export interface Point {
  readonly x: number;
  readonly y: number;
}

export interface Mark {
  readonly tool: AnnotationTool;
  readonly start: Point;
  readonly end: Point;
  readonly text?: string;
}

export interface EditorState {
  readonly rotation: number;
  readonly marks: readonly Mark[];
  readonly crop?: { readonly start: Point; readonly end: Point };
}

export interface CanvasSize {
  readonly width: number;
  readonly height: number;
}

export function imageTransform(
  image: HTMLImageElement,
  rotation: number,
  size: CanvasSize,
) {
  const quarterTurn = rotation % 180 !== 0;
  const rotatedWidth = quarterTurn ? image.naturalHeight : image.naturalWidth;
  const rotatedHeight = quarterTurn ? image.naturalWidth : image.naturalHeight;
  return {
    centerX: size.width / 2,
    centerY: size.height / 2,
    scale: Math.min(size.width / rotatedWidth, size.height / rotatedHeight),
  };
}

export function projectPoint(
  point: Point,
  image: HTMLImageElement,
  fromRotation: number,
  fromSize: CanvasSize,
  toRotation: number,
  toSize: CanvasSize,
): Point {
  const from = imageTransform(image, fromRotation, fromSize);
  const fromAngle = (fromRotation * Math.PI) / 180;
  const screenX = (point.x - from.centerX) / from.scale;
  const screenY = (point.y - from.centerY) / from.scale;
  const imageX = screenX * Math.cos(fromAngle) + screenY * Math.sin(fromAngle);
  const imageY = -screenX * Math.sin(fromAngle) + screenY * Math.cos(fromAngle);

  const to = imageTransform(image, toRotation, toSize);
  const toAngle = (toRotation * Math.PI) / 180;
  return {
    x:
      to.centerX +
      to.scale * (imageX * Math.cos(toAngle) - imageY * Math.sin(toAngle)),
    y:
      to.centerY +
      to.scale * (imageX * Math.sin(toAngle) + imageY * Math.cos(toAngle)),
  };
}

export function rotateEditorState(
  state: EditorState,
  image: HTMLImageElement,
  size: CanvasSize,
  nextRotation: number,
): EditorState {
  const rotatePoint = (point: Point) =>
    projectPoint(point, image, state.rotation, size, nextRotation, size);
  return {
    rotation: nextRotation,
    marks: state.marks.map((mark) => ({
      ...mark,
      start: rotatePoint(mark.start),
      end: rotatePoint(mark.end),
    })),
    ...(state.crop
      ? {
          crop: {
            start: rotatePoint(state.crop.start),
            end: rotatePoint(state.crop.end),
          },
        }
      : {}),
  };
}

export function outputSize(
  image: HTMLImageElement,
  rotation: number,
): CanvasSize {
  const quarterTurn = rotation % 180 !== 0;
  return {
    width: quarterTurn ? image.naturalHeight : image.naturalWidth,
    height: quarterTurn ? image.naturalWidth : image.naturalHeight,
  };
}
