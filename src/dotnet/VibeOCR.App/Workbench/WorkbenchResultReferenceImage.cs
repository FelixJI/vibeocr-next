using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Services;
using Windows.Graphics.Imaging;

namespace VibeOCR.App.Workbench;

/// <summary>
/// 结果参考图归一化：统一经共享编码器重编码为无方向元数据的 PNG（存储
/// 像素网格，与引擎 PIL 不做 exif_transpose 的 bbox 坐标系一致，WebView
/// 也不会再按 EXIF 旋转）。失败返回 null（不发布参考图），取消向上传播。
/// </summary>
internal static class WorkbenchResultReferenceImage
{
  internal static async Task<(byte[] Data, string MediaType)?> NormalizeAsync(
    byte[] data,
    CancellationToken cancellationToken)
  {
    try
    {
      byte[] png = await QrCodeSavePlatform.EncodeImageAsync(
        data, BitmapEncoder.PngEncoderId, cancellationToken, bypassPassthrough: true);
      cancellationToken.ThrowIfCancellationRequested();
      return (png, "image/png");
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
      AppLog.Warn($"结果参考图归一化失败，跳过参考图发布：{error.Message}");
      return null;
    }
  }
}
