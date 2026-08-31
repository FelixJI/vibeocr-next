export async function uploadAnnotatedImage(blob: Blob): Promise<string> {
  if (blob.type !== "image/png" || blob.size < 8) {
    throw new Error("annotated image must be a non-empty PNG");
  }
  const response = await fetch("/__annotation", {
    method: "POST",
    headers: { "Content-Type": "image/png" },
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
