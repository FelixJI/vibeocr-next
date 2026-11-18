using System.Buffers.Binary;

namespace VibeOCR.App.Inference;

/// <summary>
/// T3（#328）阶段测量用的 PNG 头部事实：只从真实传输字节读取 IHDR 宽高，
/// 不猜测、不把缺测当 0。非 PNG 载荷显式返回未解析（false），调用方按
/// NOT_MEASURED 披露。
/// </summary>
internal static class PngImageFacts
{
  private static readonly byte[] Signature =
      [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  /// <summary>PNG 签名 + IHDR 固定头（length/type）后 8 字节大端宽高。</summary>
  internal static bool TryParseDimensions(ReadOnlySpan<byte> png, out int width, out int height)
  {
    width = 0;
    height = 0;
    if (png.Length < 24) return false;
    for (int index = 0; index < 8; index++)
    {
      if (png[index] != Signature[index]) return false;
    }
    if (png[12] != (byte)'I' || png[13] != (byte)'H' ||
        png[14] != (byte)'D' || png[15] != (byte)'R')
      return false;
    width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
    height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
    return width > 0 && height > 0;
  }

  /// <summary>渲染 wire 结果的观测事实：bytes 恒真实长度；PNG 才有像素，其余留缺口。</summary>
  internal static PdfSmokeRequestProbe.ResultFacts Of(byte[]? payload)
  {
    if (payload is null || payload.Length == 0)
      return new PdfSmokeRequestProbe.ResultFacts(payload?.Length ?? 0, null, "empty_payload");
    return TryParseDimensions(payload, out int width, out int height)
      ? new PdfSmokeRequestProbe.ResultFacts(payload.Length, (long)width * height, null)
      : new PdfSmokeRequestProbe.ResultFacts(payload.Length, null, "png_ihdr_unparsed");
  }
}
