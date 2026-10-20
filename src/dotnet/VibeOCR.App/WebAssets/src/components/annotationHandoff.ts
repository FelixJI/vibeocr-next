/** /__annotation 上传允许的最终编码格式；扩展名与 MIME 由编码器真实产出。 */
const ANNOTATION_MEDIA_TYPES: ReadonlySet<string> = new Set([
  "image/png",
  "image/jpeg",
]);

export async function uploadAnnotatedImage(blob: Blob): Promise<string> {
  if (!ANNOTATION_MEDIA_TYPES.has(blob.type) || blob.size < 8) {
    throw new Error("annotated image must be a non-empty PNG or JPEG");
  }
  const response = await fetch("/__annotation", {
    method: "POST",
    headers: { "Content-Type": blob.type },
    body: blob,
  });
  if (!response.ok) throw new Error("annotated image upload failed");
  const payload: unknown = await response.json();
  if (
    !payload ||
    typeof payload !== "object" ||
    !("resourceUri" in payload) ||
    typeof payload.resourceUri !== "string" ||
    !/^https:\/\/app\.vibeocr\/__annotation\/[0-9a-f]{32}$/.test(
      payload.resourceUri,
    )
  ) {
    throw new Error("annotated image upload response is invalid");
  }
  return payload.resourceUri;
}

/** 只释放本宿主上传且尚未消费的租约；宿主检查列表归属。 */
export async function discardAnnotatedImage(
  resourceUri: string,
): Promise<void> {
  if (!/^https:\/\/app\.vibeocr\/__annotation\/[0-9a-f]{32}$/.test(resourceUri))
    throw new Error("annotated image discard URI is invalid");
  const response = await fetch(new URL(resourceUri).pathname, {
    method: "DELETE",
  });
  if (!response.ok)
    throw new Error("取消上传后的图片租约释放失败，请重试或关闭页面。");
}
