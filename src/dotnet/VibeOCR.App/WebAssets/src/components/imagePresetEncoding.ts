import { boundedCanvasSize } from "./canvasComposition";
import type { ImageOutputPreset } from "./editorPreferences";

export async function encodeImagePreset(
  url: string,
  preset: ImageOutputPreset,
): Promise<Blob> {
  const response = await fetch(url, { cache: "no-store", credentials: "omit" });
  if (!response.ok) throw new Error("授权图片读取失败");
  const bytes = await response.blob();
  if (bytes.size > 64 * 1024 * 1024 || !bytes.type.startsWith("image/"))
    throw new Error("图片编码超出边界或格式无效");
  const objectUrl = URL.createObjectURL(bytes);
  try {
    const image = await new Promise<HTMLImageElement>((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve(image);
      image.onerror = () => reject(new Error("图片无法解码"));
      image.src = objectUrl;
    });
    // 输入按宿主图片合同（单边≤32768px、总像素≤100MP）：16385–32768 边
    // 的大图允许经预设合法缩小输出；实际目标 canvas 才受输出预算约束。
    if (
      image.naturalWidth > 32768 ||
      image.naturalHeight > 32768 ||
      image.naturalWidth * image.naturalHeight > 100_000_000
    )
      throw new Error("输入图片超出宿主解码合同（单边≤32768px，总像素≤100MP）");
    const size = boundedCanvasSize(
      preset.width || image.naturalWidth,
      preset.height || image.naturalHeight,
    );
    const canvas = document.createElement("canvas");
    canvas.width = size.width;
    canvas.height = size.height;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("输出画布不可用");
    if (preset.format === "image/jpeg") {
      context.fillStyle = "#ffffff";
      context.fillRect(0, 0, canvas.width, canvas.height);
    }
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob(
        (blob) => {
          if (
            !blob ||
            blob.type !== preset.format ||
            blob.size > 64 * 1024 * 1024
          )
            reject(new Error("图片编码失败或超过 64MiB"));
          else resolve(blob);
        },
        preset.format,
        preset.quality,
      ),
    );
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}
